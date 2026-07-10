# Error handling

Every non-2xx response from the daemon is mapped to a typed F# exception. This
is the complete reference: the exception hierarchy, the HTTP-status mapping,
the daemon's error envelope, and recovery patterns for each category.

---

## The error model

All client errors descend from `MongrelDBException`. The client raises a
specific subclass for each failure category:

| Class | Meaning | Typical cause |
|-------|---------|---------------|
| `MongrelDBException` | Base class for all client errors | (catch this to handle any failure) |
| `AuthException` | HTTP 401 or 403 | Missing/bad credentials against an auth-enabled daemon |
| `NotFoundException` | HTTP 404 | Missing table, schema, or resource |
| `ConflictException` | HTTP 409 | Unique, foreign-key, check, or trigger violation at commit |
| `QueryException` | HTTP 400 or 5xx, plus network | Malformed request, server failure, transport error |

`ConflictException` carries extra detail via properties:

| Property | Meaning |
|----------|---------|
| `ErrorCode` | The server's structured error code (e.g. `"UNIQUE_VIOLATION"`); `""` when absent |
| `OpIndex` | The offending op index within a batch, when reported; `Nullable<int>` otherwise |

## The daemon's error envelope

```json
{
  "status": "aborted",
  "error": {
    "code": "UNIQUE_VIOLATION",
    "message": "duplicate key in column 1",
    "op_index": 0
  }
}
```

Structured codes you will commonly see in `ErrorCode`:

| `ErrorCode` | Meaning |
|-------------|---------|
| `UNIQUE_VIOLATION` | A unique/PK constraint rejected the commit |
| `FK_VIOLATION` | A foreign-key reference was missing |
| `CHECK_VIOLATION` | A check constraint or trigger rejected the commit |
| `NOT_FOUND` | A named resource (table, schema) does not exist |

## HTTP status to exception mapping

| HTTP status | Exception | Notes |
|-------------|-----------|-------|
| 401, 403 | `AuthException` | Bad/missing credentials |
| 404 | `NotFoundException` | Resource not found |
| 409 | `ConflictException` | Constraint violation at commit |
| 400 | `QueryException` | Malformed request / bad query |
| 5xx | `QueryException` | Daemon-side failure |
| other non-2xx | `QueryException` | Catch-all |
| 2xx | (no error) | Success |

Network and encoding problems (`HttpRequestException`, `TaskCanceledException` for
timeouts, `JsonException` for NaN/Infinity, etc.) are also mapped to
`QueryException`.

## Discriminating errors

### By category - catch the subclass

```fsharp
try
    db.SchemaFor("missing_table") |> ignore
with
| :? NotFoundException -> printfn "table does not exist"
| :? ConflictException -> printfn "unexpected conflict on a read"
| :? AuthException -> printfn "bad credentials"
| :? QueryException -> printfn "server error or malformed request"
| :? MongrelDBException as e -> printfn "other error: %s" e.Message
```

### By details - read `ConflictException` fields

```fsharp
try
    txn.Commit() |> ignore
with :? ConflictException as e ->
    printfn "status=409 code=%s op=%A msg=%s" e.ErrorCode e.OpIndex e.Message
```

## Recovery patterns

### Auth failure - do not retry blindly

A retry will not fix bad credentials. Surface the error to the caller or
operator.

```fsharp
try
    db.SchemaFor(table) |> ignore
with :? AuthException as e ->
    failwithf "credentials rejected; refresh token: %s" e.Message
```

### Not found - fall back, do not crash

For lookups by primary key, a 404 may be a normal "absent" result.

```fsharp
try
    db.SchemaFor(tableName)
with :? NotFoundException ->
    dict [] :> IDictionary<string, obj>  // table missing - treat as empty
```

Note: a `pk` query against an existing table returns zero rows, not a 404;
`NotFoundException` here means the table itself is missing.

### Constraint conflict - report the offending op

```fsharp
try
    txn.Commit() |> ignore
with :? ConflictException as e ->
    if e.OpIndex.HasValue then
        eprintfn "op %A violated %s: %s" e.OpIndex e.ErrorCode e.Message
    else
        eprintfn "conflict %s: %s" e.ErrorCode e.Message
    reraise()
```

The engine already rolled back the whole batch - there is nothing to undo.

### Transient failure - retry with an idempotency key

`QueryException` covers transport and 5xx failures. With an idempotency key,
retrying a transaction is safe (see [transactions.md](transactions.md)).

```fsharp
let run (db: Client) (buildTxn: Client -> Transaction) key =
    // buildTxn is a function that returns a fresh Transaction with the same ops.
    try
        buildTxn(db).Commit(idempotencyKey = key)
    with
    | :? AuthException | :? ConflictException ->
        reraise()   // not transient
    | :? MongrelDBException ->
        reraise()   // QueryException / network - caller may retry with the same key
```

### Transaction-state error

Calling `Commit` or `Rollback` twice on the same `Transaction` raises a
`QueryException`. That is a programming bug - fix the control flow rather than
catching it.

## Quick reference

```fsharp
// Category checks (most specific first):
try ... with
| :? AuthException -> ...       // 401/403
| :? NotFoundException -> ...   // 404
| :? ConflictException -> ...   // 409
| :? QueryException -> ...      // 400/5xx/network
| :? MongrelDBException -> ...  // base

// Detail extraction on a conflict:
try ... with :? ConflictException as e ->
    e.ErrorCode   // string, e.g. "UNIQUE_VIOLATION"
    e.OpIndex     // Nullable<int>
    e.Message     // string
```

## Next steps

- [transactions.md](transactions.md) - constraint handling and retries in context
- [auth.md](auth.md) - credential management
