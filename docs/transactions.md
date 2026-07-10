# Transactions

MongrelDB commits every write through a single atomic transaction endpoint
(`POST /kit/txn`). This guide covers the two ways to use it - a one-shot
single op, and a staged batch - plus idempotency keys for safe retries, typed
constraint-violation handling, and rollback.

The engine enforces `UNIQUE`, foreign-key, check, and trigger constraints at
**commit time**. A violation aborts the entire batch: no op in the batch
becomes visible.

---

## Single puts vs. batch transactions

### Single op: `Client.Put`

`Client.Put` is a convenience wrapper that sends a one-op transaction. Use it
when a write is independent and you do not need atomicity across multiple
rows.

```fsharp
let cells = Dictionary<int, obj>()
cells.[1] <- box 1
cells.[2] <- box "Alice"
cells.[3] <- box 99.5
let res = db.Put("orders", cells)
printfn "%A" res
```

`Client.Upsert`, `Client.Delete`, and `Client.DeleteByPk` are the same shape:
single-op transactions.

### Batch: `Client.BeginTransaction` + `Transaction`

When several writes must succeed or fail together, stage them on a
`Transaction` and commit once. All ops go to the server in a single HTTP
request and commit atomically.

```fsharp
let txn = db.BeginTransaction()
let mk id customer amount =
    let c = Dictionary<int, obj>()
    c.[1] <- box id; c.[2] <- box customer; c.[3] <- box amount
    upcast c

txn.Put("orders", mk 10 "Dave" 50.0) |> ignore
txn.Put("orders", mk 11 "Eve"  75.0) |> ignore
txn.DeleteByPk("orders", box 2) |> ignore

let results = txn.Commit()
printfn "committed %d ops" results.Length
```

The `returning` argument on `Transaction.Put` asks the daemon to echo the
written row back in the result - useful for reading server-assigned values.

```fsharp
let txn = db.BeginTransaction()
let c = Dictionary<int, obj>(); c.[1] <- box 42; c.[2] <- box "Hal"; c.[3] <- box 12.0
txn.Put("orders", c, returning = true) |> ignore
let res = txn.Commit()
printfn "server echoed: %A" res.[0]
```

`Transaction.Upsert(table, cells, updateCells)` applies `updateCells` on a
primary-key conflict. An omitted `updateCells` means "do nothing on conflict".

## Idempotency keys for safe retries

Networks drop requests and daemons crash after committing but before replying.
An idempotency key makes a commit safe to retry: the daemon remembers the key
and replays the **original** result on a duplicate commit, even across
restarts.

Pass the key with the `idempotencyKey` argument on `Commit` (or on
`Client.Put` / `Client.Upsert`):

```fsharp
// A web handler that must not double-charge, even if the client retries or the
// connection drops after the daemon committed.
let charge (db: Client) (orderId: int) =
    let txn = db.BeginTransaction()
    let c = Dictionary<int, obj>(); c.[1] <- box orderId; c.[2] <- box 199.0
    txn.Put("charges", c) |> ignore
    // Use a stable, business-meaningful key derived from the request. On a
    // retry with the same key the daemon returns the first commit's result
    // instead of inserting a second row.
    txn.Commit(idempotencyKey = "charge:" + string orderId)
```

Rules for keys:

- Any non-empty string works. Prefer content-derived, globally-unique values
  (e.g. `"charge:" + string orderId`).
- `null` (the default) disables idempotency - a retry will commit again.
- The key scopes the **entire batch**, not individual ops. Reuse the exact
  same ops and key together when retrying.

A safe retry loop:

```fsharp
let commitWithRetry (db: Client) (buildTxn: Client -> Transaction) key maxAttempts =
    let rec loop attempt =
        if attempt >= maxAttempts then failwith "commit failed after retries"
        // Build a fresh Transaction inside the loop so retries always start clean.
        let txn = buildTxn(db)
        try
            txn.Commit(idempotencyKey = key)
        with
        | :? ConflictException | :? AuthException ->
            reraise()   // not transient - surface to the caller
        | :? QueryException when attempt = maxAttempts - 1 ->
            reraise()
        | :? QueryException ->
            System.Threading.Thread.Sleep(1 <<< attempt)
            loop (attempt + 1)
    loop 0
```

Build the transaction inside the retry loop so a failed `Commit` (which flips
the `Transaction` to "committed") is replaced by a fresh one carrying the same
ops and the same key.

## Handling constraint violations

Constraint violations arrive as HTTP 409, mapped to `ConflictException`. It
carries the structured `ErrorCode` and the offending op index:

```fsharp
let txn = db.BeginTransaction()
let c = Dictionary<int, obj>(); c.[1] <- box 1
txn.Put("orders", c) |> ignore   // duplicate PK
try
    txn.Commit() |> ignore
with :? ConflictException as e ->
    match e.ErrorCode with
    | "UNIQUE_VIOLATION" -> eprintfn "duplicate at op %A: %s" e.OpIndex e.Message
    | "FK_VIOLATION"     -> eprintfn "missing parent at op %A: %s" e.OpIndex e.Message
    | "CHECK_VIOLATION"  -> eprintfn "check failed at op %A: %s" e.OpIndex e.Message
    | _                  -> eprintfn "other conflict: %s" e.Message
```

The error envelope from the daemon looks like:

```json
{"status": "aborted", "error": {"code": "UNIQUE_VIOLATION", "message": "...", "op_index": 0}}
```

`op_index` points at the offending op within the batch so you can report which
row caused the failure.

## Rollback after failure

There are two notions of "rollback":

1. **Server-side.** When `Commit` raises `ConflictException`, the engine has
   already discarded the entire batch. Nothing was written; there is no server
   rollback to perform.
2. **Client-side.** `Transaction.Rollback` clears the locally staged ops. Call
   it to release the `Transaction` when you decide not to commit (for example,
   after a validation error in your own code, before ever sending).

```fsharp
let txn = db.BeginTransaction()
let c = Dictionary<int, obj>(); c.[1] <- box 1; c.[2] <- box "Iris"; c.[3] <- box 5.0
txn.Put("orders", c) |> ignore

if not businessRuleOk then
    // Throw the staged ops away locally. Nothing has been sent to the daemon.
    txn.Rollback()
else
    try
        txn.Commit() |> ignore
    with :? ConflictException ->
        // On conflict the server already rolled back; nothing more to do.
        ()
```

`Rollback` and `Commit` both raise a `QueryException` if the transaction was
already committed. Treat that as a programming error to fix upstream, not a
runtime condition to silence.

## Summary

| Goal | Use |
|------|-----|
| One independent write | `Client.Put` / `Upsert` / `Delete` / `DeleteByPk` |
| Several writes that must commit together | `Client.BeginTransaction` + `Transaction.Commit` |
| Retry safely after a network blip | `Commit(idempotencyKey = ...)` with a stable key |
| Distinguish constraint classes | catch `ConflictException`, read `.ErrorCode` and `.OpIndex` |
| Abort before sending | `Transaction.Rollback` |

See [errors.md](errors.md) for the full error hierarchy and [queries.md](queries.md)
for read patterns.
