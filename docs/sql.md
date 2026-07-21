# SQL

MongrelDB ships a DataFusion-backed SQL engine at `POST /sql`. From F#, run
SQL with `Client.Sql`:

```fsharp
let rows = db.Sql("SELECT 1")
```

This guide covers the SQL surface - DDL, DML, `CREATE TABLE AS SELECT`,
recursive CTEs, and window functions - and when to reach for SQL versus the
native query builder.

---

## How `Sql` behaves

`Client.Sql(sql)` sends `{"sql": "...", "format": "json"}` to `/sql`. It
requests JSON output and returns the decoded rows when the daemon replies with
a JSON result set, and an empty array otherwise.

In practice:

- **DDL and DML** (`CREATE TABLE`, `INSERT`, `UPDATE`, `DELETE`) reply with a
  non-JSON status body. `Sql` returns `[||]` - success is the signal.
- **`SELECT`** in most daemon builds streams Arrow IPC bytes rather than JSON,
  so when the daemon honors the JSON request it returns row objects; otherwise
  `Sql` returns `[||]`. Use the native `QueryBuilder` for typed row retrieval
  in application code, and use `Sql` for statements whose execution is the goal
  (DDL/DML/admin).

Errors are mapped to the same typed exceptions as everything else: an HTTP 400
or 5xx raises `QueryException`; 409 raises `ConflictException`; and so on. See
[errors.md](errors.md).

```fsharp
try
    db.Sql("INSERT INTO orders (id, customer, amount) VALUES (99, 'Zoe', 999.0)") |> ignore
with :? ConflictException as e ->
    if e.ErrorCode = "UNIQUE_VIOLATION" then eprintfn "duplicate row: %s" e.Message
```

## CREATE TABLE

Define a table in SQL instead of via `Client.CreateTable`. Column ids are
assigned by the server when not stated.

```fsharp
db.Sql("""
  CREATE TABLE products (
    id          INT64 PRIMARY KEY,
    name        VARCHAR,
    price       FLOAT64,
    category    VARCHAR,
    in_stock    BOOLEAN
  )
""") |> ignore
```

## INSERT

```fsharp
db.Sql("INSERT INTO products (id, name, price, category, in_stock) VALUES (1, 'Widget', 9.99, 'tools', true)") |> ignore
db.Sql("INSERT INTO products VALUES (2, 'Gadget', 19.99, 'tools', true)") |> ignore
```

For bulk inserts, the native batch transaction (`Client.BeginTransaction`) is
usually faster because it stages ops in one round trip without re-parsing SQL.

## UPDATE

```fsharp
db.Sql("UPDATE products SET price = 14.99 WHERE id = 1") |> ignore
db.Sql("UPDATE orders SET amount = 200.0 WHERE customer = 'Bob'") |> ignore
```

## DELETE

```fsharp
db.Sql("DELETE FROM products WHERE in_stock = false") |> ignore
db.Sql("DELETE FROM products WHERE id = 2") |> ignore
```

## SELECT

```fsharp
db.Sql("SELECT id, name FROM products WHERE category = 'tools' ORDER BY price") |> ignore
db.Sql("SELECT category, COUNT(*) AS n FROM products GROUP BY category") |> ignore
```

When the daemon returns JSON, `Sql` returns `IDictionary<string,obj>[]` rows
keyed by column name. When it streams Arrow IPC instead, the result is an empty
array; mirror the same lookup with the `QueryBuilder` to read rows back into
typed maps.

## CREATE TABLE AS SELECT

Materialize a query result into a new table. Great for snapshots, rollups,
and denormalized aggregates.

```fsharp
// Snapshot all high-value orders into a new table.
db.Sql("CREATE TABLE archive AS SELECT * FROM orders WHERE amount > 500") |> ignore

// Roll up sales by customer.
db.Sql("""
  CREATE TABLE sales_by_customer AS
  SELECT customer, SUM(amount) AS total
  FROM orders
  GROUP BY customer
""") |> ignore
```

The new table inherits column types from the query. Query it afterward with
the native builder or SQL.

## Recursive CTEs

`WITH RECURSIVE` is fully supported. Classic use cases: series generation,
hierarchy/graph traversal.

```fsharp
// Generate the numbers 1..10.
db.Sql("""
  WITH RECURSIVE r(n) AS (
    SELECT 1
    UNION ALL
    SELECT n + 1 FROM r WHERE n < 10
  )
  SELECT n FROM r
""") |> ignore
```

A common practical example is walking an adjacency list:

```fsharp
db.Sql("""
  WITH RECURSIVE descendants(id) AS (
    SELECT id FROM categories WHERE id = 1
    UNION ALL
    SELECT c.id FROM categories c
    JOIN descendants d ON c.parent_id = d.id
  )
  SELECT id FROM descendants
""") |> ignore
```

## Window functions

Window functions compute aggregates/rankings across a moving window without
collapsing rows. Useful for top-N-per-group, running totals, and row numbers.

```fsharp
// Row number within each customer, ordered by amount descending.
db.Sql("""
  SELECT id, customer, amount,
         ROW_NUMBER() OVER (PARTITION BY customer ORDER BY amount DESC) AS rn
  FROM orders
""") |> ignore

// Running total per customer.
db.Sql("""
  SELECT id, customer, amount,
         SUM(amount) OVER (PARTITION BY customer ORDER BY id) AS running_total
  FROM orders
""") |> ignore
```

`RANK()`, `DENSE_RANK()`, `LAG()`, `LEAD()`, `NTILE()`, and the usual
window-frame clauses are available through DataFusion.

## ANN index backends

The engine's `ann` index is swappable across three backends - `hnsw` (the default), `diskann`, and `ivf` - selected with the `algorithm` option. Quantization is independently configurable: `dense`, `binary_sign`, or `product` (product quantization, with `num_subvectors`, `bits_per_subvector`, `pq_training_samples`, `pq_seed`, and `pq_rerank_factor`). These are ordinary DDL strings run through `sql`, so no client changes are needed.

```fsharp
// DiskANN (on-disk graph, terabyte-scale)
db.Sql("CREATE INDEX orders_emb_diskann ON orders USING ann (embedding) WITH (algorithm = 'diskann', quantization = 'dense', diskann_l = 50, diskann_r = 64, beam_width = 8)") |> ignore

// IVF with product quantization (clustered, memory-frugal)
db.Sql("CREATE INDEX orders_emb_ivf ON orders USING ann (embedding) WITH (algorithm = 'ivf', quantization = 'product', nlist = 1024, nprobe = 16, num_subvectors = 16, bits_per_subvector = 8)") |> ignore

// HNSW with product quantization (recall-tuned)
db.Sql("CREATE INDEX orders_emb_hnsw_pq ON orders USING ann (embedding) WITH (algorithm = 'hnsw', quantization = 'product', m = 16, ef_construction = 200, ef_search = 50, num_subvectors = 32, pq_training_samples = 50000, pq_rerank_factor = 8)") |> ignore
```


## When to use SQL vs. the query builder

Both read from the same tables, but they are optimized for different jobs.

| Reach for | When |
|-----------|------|
| **`QueryBuilder`** | Point lookups, range scans, bitmap filters, full-text, and vector similarity that map to a native index. Sub-millisecond, no parser overhead, and rows decode into dictionaries directly. |
| **SQL** | DDL (`CREATE TABLE`, schemas, materialized views), multi-statement setup, joins, recursive CTEs, window functions, and arbitrary aggregates. Also the natural choice for admin scripts and one-off analysis. |

Rules of thumb:

- Need a typed array of matching rows? Use the query builder.
- Building/dropping tables, or running a `CREATE TABLE AS SELECT`? Use SQL.
- Joining multiple tables, computing rankings, or walking a graph? Use SQL.
- Filtering by one or more indexed columns? Use the query builder - it is
  faster and avoids Arrow-to-object decoding.

Mix freely: create tables with SQL, write rows with `Client.Put`, read them
back with `QueryBuilder`, and run analytics with SQL.

## Next steps

- [queries.md](queries.md) - every native index condition in detail
- [transactions.md](transactions.md) - bulk inserts via batch transactions
- [errors.md](errors.md) - handling SQL execution errors
