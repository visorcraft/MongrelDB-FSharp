// Run: dotnet fsi examples/basic_crud.fsx
// Requires: mongreldb-server running on http://127.0.0.1:8453
//
// Example: basic CRUD operations with the MongrelDB F# client.
//
// Builds the library once (`dotnet build`), references the produced assembly,
// then creates a table, inserts three rows, counts them, queries all rows,
// upserts (updates) one row by primary key, deletes one row, and drops the
// table. Progress is printed at every step. The table name is unique per run so
// concurrent / repeated runs don't collide, and the table is always dropped in
// a try/finally.

#I "../src/Visorcraft.MongrelDB/bin/Debug/net8.0"
#r "Visorcraft.MongrelDB.dll"

open System
open System.Collections.Generic
open Visorcraft.MongrelDB

let url = "http://127.0.0.1:8453"
// Unique suffix per run so concurrent / repeated runs don't collide on the same
// table name, and the table can always be dropped in the finally.
let suffix = string (DateTimeOffset.UtcNow.ToUnixTimeSeconds()) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8)
let table = "example_crud_" + suffix

let db = new Client(url = url)

// Health check; bail out if the daemon is unreachable.
if not (db.Health()) then
    eprintfn "daemon not reachable at %s" url
    exit 1
printfn "Connected to MongrelDB"

let col id name ty pk : IDictionary<string, obj> =
    let d = Dictionary<string, obj>()
    d.["id"] <- box id
    d.["name"] <- box name
    d.["ty"] <- box ty
    d.["primary_key"] <- box pk
    d.["nullable"] <- box false
    d :> IDictionary<string, obj>

/// `colEx` builds a column descriptor that includes optional enum_variants and
/// default_value keys, which the daemon validates (enum columns require a
/// non-empty enum_variants array).
let colEx id name ty pk enumVariants (defaultValue: obj option) : IDictionary<string, obj> =
    let d = Dictionary<string, obj>()
    d.["id"] <- box id
    d.["name"] <- box name
    d.["ty"] <- box ty
    d.["primary_key"] <- box pk
    d.["nullable"] <- box false
    match enumVariants with
    | Some v -> d.["enum_variants"] <- box v
    | None -> ()
    match defaultValue with
    | Some dv -> d.["default_value"] <- box dv
    | None -> ()
    d :> IDictionary<string, obj>

let cells pairs : IDictionary<int, obj> =
    let d = Dictionary<int, obj>()
    for (k, v) in pairs do d.[k] <- v
    d :> IDictionary<int, obj>

try
    // Create the table. Schema: id (int64 PK), role (enum with default), name
    // (varchar), score (float64 with default). Column-level keys (enum_variants,
    // default_value) are forwarded to the daemon verbatim.
    let tid = db.CreateTable(table, [|
        col 1 "id"    "int64"   true
        colEx 2 "role"  "enum"    false (Some [| "admin"; "guest" |]) (Some "guest")
        col 3 "name"  "varchar" false
        colEx 4 "score" "float64" false None (Some 0.0)
    |])
    printfn "Created table %s (id %d)" table tid

    // Insert three rows. Cells map column id -> value.
    db.Put(table, cells [1, box 1; 2, box "admin"; 3, box "Alice"; 4, box 95.5]) |> ignore
    db.Put(table, cells [1, box 2; 2, box "guest"; 3, box "Bob";   4, box 82.0]) |> ignore
    db.Put(table, cells [1, box 3; 2, box "guest"; 3, box "Carol"; 4, box 78.3]) |> ignore
    printfn "Inserted 3 rows"

    printfn "Total rows: %d" (db.Count(table))

    // Query all rows (no conditions).
    let all = db.Query(table).Execute()
    printfn "Query returned %d rows:" all.Length
    for row in all do
        printfn "  %A" row

    // Upsert (update) Alice's row. updateCells supplies the values written on a
    // primary-key conflict. Score is bumped from 95.5 to 100.0; role and name
    // are echoed back unchanged.
    db.Upsert(table,
              cells [1, box 1; 2, box "admin"; 3, box "Alice"; 4, box 100.0],
              cells [2, box "admin"; 3, box "Alice"; 4, box 100.0]) |> ignore
    printfn "Upserted Alice's score to 100.0"
    printfn "Total rows after upsert: %d" (db.Count(table))

    // Delete Carol (primary key 3).
    db.DeleteByPk(table, box 3)
    printfn "Deleted Carol; remaining rows: %d" (db.Count(table))
finally
    // Always drop the table, even if an earlier step raised.
    try db.DropTable(table) with _ -> ()
    printfn "Dropped table %s" table

(db :> IDisposable).Dispose()
