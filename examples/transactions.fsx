// Run: dotnet fsi examples/transactions.fsx
// Requires: mongreldb-server running on http://127.0.0.1:8453
//
// Example: atomic batch transactions with the MongrelDB F# client.
//
// Creates a table, stages three inserts in a single transaction, commits them
// atomically, verifies the count, then demonstrates idempotent retries by
// re-committing with the same idempotency key (the daemon returns the original
// result and applies no duplicate rows). Cleans up by dropping the table.

#I "../src/Visorcraft.MongrelDB/bin/Debug/net8.0"
#r "Visorcraft.MongrelDB.dll"

open System
open System.Collections.Generic
open Visorcraft.MongrelDB

let url = "http://127.0.0.1:8453"
// Unique suffix per run so repeated/concurrent runs don't collide.
let suffix = string (DateTimeOffset.UtcNow.ToUnixTimeSeconds()) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8)
let table = "example_txn_" + suffix
// Idempotency key must be unique per run so retry logic isn't confused with a
// previous run's committed batch.
let idempotencyKey = "example-txn-" + suffix

let db = new Client(url = url)

if not (db.Health()) then
    eprintfn "daemon not reachable at %s" url
    exit 1
printfn "Connected to MongrelDB"

let col id name ty : IDictionary<string, obj> =
    let d = Dictionary<string, obj>()
    d.["id"] <- box id
    d.["name"] <- box name
    d.["ty"] <- box ty
    d.["primary_key"] <- box (id = 1)
    d.["nullable"] <- box false
    d :> IDictionary<string, obj>

let cells pairs : IDictionary<int, obj> =
    let d = Dictionary<int, obj>()
    for (k, v) in pairs do d.[k] <- v
    d :> IDictionary<int, obj>

try
    db.CreateTable(table, [|
        col 1 "id"    "int64"
        col 2 "name"  "varchar"
        col 3 "score" "float64"
    |]) |> ignore
    printfn "Created table %s" table

    // Stage three puts and commit them atomically. Either every op lands or
    // none do; a constraint violation rolls back the whole batch.
    let txn = db.BeginTransaction()
    txn.Put(table, cells [1, box 1; 2, box "Alice"; 3, box 95.5]) |> ignore
    txn.Put(table, cells [1, box 2; 2, box "Bob";   3, box 82.0]) |> ignore
    txn.Put(table, cells [1, box 3; 2, box "Carol"; 3, box 78.3]) |> ignore
    printfn "Staged %d operations" txn.Count

    let results = txn.Commit()
    printfn "Committed atomically: %d operations applied" results.Length

    printfn "Verified row count after commit: %d" (db.Count(table))

    // Idempotent retry: stage the same batch again with an idempotency key,
    // then commit a second time with the SAME key. The daemon replays the
    // original result and applies no extra rows.
    let retry1 = db.BeginTransaction()
    retry1.Put(table, cells [1, box 4; 2, box "Dave"; 3, box 60.0]) |> ignore
    retry1.Commit(idempotencyKey = idempotencyKey) |> ignore
    printfn "After first idempotent commit: %d rows" (db.Count(table))

    let retry2 = db.BeginTransaction()
    retry2.Put(table, cells [1, box 4; 2, box "Dave"; 3, box 60.0]) |> ignore
    retry2.Commit(idempotencyKey = idempotencyKey) |> ignore
    printfn "After duplicate idempotent commit (same key): %d rows (no double-apply)" (db.Count(table))
finally
    try db.DropTable(table) with _ -> ()
    printfn "Dropped table %s" table

(db :> IDisposable).Dispose()
