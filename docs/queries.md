# Queries

The fluent `QueryBuilder` pushes conditions down to MongrelDB's native indexes
for sub-millisecond lookups - bitmap, learned-range, FM-index full text, HNSW
vector similarity, and more. Each condition type maps to one specialized
index; conditions are AND-ed together.

```fsharp
let cond = Dictionary<string, obj>()
cond.["column"] <- box 3
cond.["min"] <- box 100.0
cond.["max"] <- box 500.0
let rows = db.Query("orders")
              .Where("range_f64", cond)
              .ProjectionOf([| 1; 2 |])
              .LimitTo(100)
              .Execute()
```

This guide covers every condition type, projection, limits and truncation,
combining conditions, and the friendly aliases the builder translates for you.

---

## The basics

Every query starts with `Client.Query(table)` and ends with `Execute`:

| Member | Purpose |
|--------|---------|
| `Where(type, params)` | Add a native condition. Multiple `Where` calls are AND-ed. |
| `ProjectionOf(columnIds)` | Return only these column ids (`None` means all columns). |
| `LimitTo(n)` | Cap the number of rows. |
| `Build()` | Produce the request payload (useful for debugging). |
| `Execute()` | Send and decode. Records the `Truncated` flag. |
| `Truncated` | Whether the last `Execute` hit the limit. |

The request body produced by `Build` matches the daemon's `/kit/query` shape:

```json
{
  "table": "orders",
  "conditions": [{"range_f64": {"column_id": 3, "lo": 100.0, "hi": 500.0, "lo_inclusive": true, "hi_inclusive": true}}],
  "projection": [1, 2],
  "limit": 100
}
```

## Condition types

`params` is an `IDictionary<string, obj>`. Column references use the numeric
**column id**, never the column name.

### `pk` - exact primary-key match

The fastest lookup. `value` is the primary-key value.

```fsharp
let p = Dictionary<string, obj>(); p.["value"] <- box 42
db.Query("orders").Where("pk", p).Execute()
```

### `range` - integer range (learned-range index)

Inclusive bounds. Omit `lo` or `hi` for an open range.

```fsharp
let r = Dictionary<string, obj>(); r.["column"] <- box 3; r.["min"] <- box 100; r.["max"] <- box 500
db.Query("orders").Where("range", r).Execute()

// Open-ended: amount >= 100
let r2 = Dictionary<string, obj>(); r2.["column"] <- box 3; r2.["min"] <- box 100
db.Query("orders").Where("range", r2).Execute()
```

### `range_f64` - float range with inclusive/exclusive control

Adds `lo_inclusive` / `hi_inclusive` flags (default inclusive).

```fsharp
let r = Dictionary<string, obj>()
r.["column"] <- box 3
r.["min"] <- box 100.0
r.["max"] <- box 500.0
r.["min_inclusive"] <- box true
r.["max_inclusive"] <- box false   // (100.0, 500.0]
db.Query("orders").Where("range_f64", r).Execute()
```

### `bitmap_eq` - equality on a bitmap-indexed column

Best for low-cardinality columns (status, category, booleans).

```fsharp
let b = Dictionary<string, obj>(); b.["column"] <- box 2; b.["value"] <- box "Alice"
db.Query("orders").Where("bitmap_eq", b).Execute()
```

### `bitmap_in` - IN predicate on a bitmap-indexed column

Match any of a set of values.

```fsharp
let b = Dictionary<string, obj>()
b.["column"] <- box 2
b.["values"] <- box [| "Alice"; "Bob"; "Carol" |]
db.Query("orders").Where("bitmap_in", b).Execute()
```

### `is_null` / `is_not_null` - null checks

```fsharp
let n = Dictionary<string, obj>(); n.["column"] <- box 3
db.Query("orders").Where("is_null", n).Execute()
db.Query("orders").Where("is_not_null", n).Execute()
```

### `fm_contains` - full-text substring search (FM-index)

Substring match within a column. Use `pattern` (the server key) or the
friendly `value` alias - both translate to `pattern` on the wire for FTS
conditions.

```fsharp
let f = Dictionary<string, obj>(); f.["column"] <- box 2; f.["pattern"] <- box "database performance"
db.Query("documents").Where("fm_contains", f).LimitTo(10).Execute()

// Friendly alias: "value" -> "pattern" for fm_contains only.
let f2 = Dictionary<string, obj>(); f2.["column"] <- box 2; f2.["value"] <- box "database"
db.Query("documents").Where("fm_contains", f2).Execute()
```

### `fm_contains_all` - multiple substrings, all must match

```fsharp
let f = Dictionary<string, obj>()
f.["column"] <- box 2
f.["patterns"] <- box [| "database"; "performance" |]
db.Query("documents").Where("fm_contains_all", f).Execute()
```

### `ann` - dense vector similarity (HNSW)

Approximate nearest-neighbors over a `float` vector column. `k` is the result
count.

```fsharp
let a = Dictionary<string, obj>()
a.["column"] <- box 2
a.["query"] <- box [| 0.1; 0.2; 0.3; 0.4 |]
a.["k"] <- box 10
db.Query("embeddings").Where("ann", a).Execute()
```

## Projection (column selection)

`ProjectionOf([|1;2;...|])` restricts the columns in each returned row. Skip the
call for all columns. Projecting to only the columns you need cuts bandwidth
and decode cost.

```fsharp
// Return only the id and customer columns.
let r = Dictionary<string, obj>(); r.["column"] <- box 3; r.["min"] <- box 100
db.Query("orders").Where("range", r).ProjectionOf([| 1; 2 |]).Execute()
```

Returned rows are `IDictionary<string, obj>` objects keyed by the column id as a
JSON-decoded string key. Access accordingly:

```fsharp
let rows = db.Query("orders").ProjectionOf([| 1; 2 |]).Execute()
for r in rows do
    let customer = r.["2"]
    printfn "%A" customer
```

## Limit and the truncated flag

`LimitTo(n)` caps the result. When the server has more matches than the limit
allows, it returns the first `n` and sets `truncated: true`. Read it with
`Truncated` **after** `Execute`.

```fsharp
let r = Dictionary<string, obj>(); r.["column"] <- box 3; r.["min"] <- box 0
let q = db.Query("orders").Where("range", r).LimitTo(100)
let rows = q.Execute()
if q.Truncated then
    eprintfn "result capped at %d; more rows available" rows.Length
```

`Truncated` returns `false` until `Execute` has run, so build a fresh query
for each independent lookup.

## Multiple AND conditions

Chain `Where` calls. Every condition must match; the server intersects the
index results.

```fsharp
// Customer is Alice AND amount is between 100 and 500.
let b = Dictionary<string, obj>(); b.["column"] <- box 2; b.["value"] <- box "Alice"
let r = Dictionary<string, obj>(); r.["column"] <- box 3; r.["min"] <- box 100; r.["max"] <- box 500
db.Query("orders")
  .Where("bitmap_eq", b)
  .Where("range", r)
  .ProjectionOf([| 1; 3 |])
  .LimitTo(50)
  .Execute()
```

Because each `Where` targets a different specialized index, the engine can
pick the most selective one to drive the lookup and intersect the rest.

## Friendly alias translation

The builder accepts readable parameter names and translates them to the
server's canonical on-wire keys. Both spellings work, so use whichever is
clearer in context.

| You write | Sent as | Applies to |
|-----------|---------|------------|
| `column` | `column_id` | all condition types |
| `min` | `lo` | `range`, `range_f64` |
| `max` | `hi` | `range`, `range_f64` |
| `min_inclusive` | `lo_inclusive` | `range_f64` |
| `max_inclusive` | `hi_inclusive` | `range_f64` |
| `value` | `pattern` | `fm_contains`, `fm_contains_all` only |

The `value` to `pattern` alias applies **only** to FTS conditions, because
`pk` and `bitmap_eq` use `value` as their canonical key. For those, write
`value` directly.

```fsharp
// pk: "value" stays "value" (canonical)
let p = Dictionary<string, obj>(); p.["value"] <- box 42
db.Query("orders").Where("pk", p)

// fm_contains: "value" is translated to "pattern"
let f = Dictionary<string, obj>(); f.["column"] <- box 2; f.["value"] <- box "search term"
db.Query("documents").Where("fm_contains", f)
// equivalent to:
let f2 = Dictionary<string, obj>(); f2.["column_id"] <- box 2; f2.["pattern"] <- box "search term"
db.Query("documents").Where("fm_contains", f2)
```

## Putting it together

A realistic combined lookup - bitmap equality + range + projection + limit +
truncation check:

```fsharp
let topSpenders (db: Client) (customer: string) =
    let b = Dictionary<string, obj>(); b.["column"] <- box 2; b.["value"] <- box customer
    let r = Dictionary<string, obj>(); r.["column"] <- box 3; r.["min"] <- box 100
    let q = db.Query("orders")
                .Where("bitmap_eq", b)
                .Where("range", r)
                .ProjectionOf([| 1; 3 |])
                .LimitTo(50)
    let rows = q.Execute()
    if q.Truncated then eprintfn "warning: topSpenders result capped at 50"
    rows
```

For arbitrary predicates, joins, and aggregations that the native indexes do
not cover, use SQL instead - see [sql.md](sql.md).
