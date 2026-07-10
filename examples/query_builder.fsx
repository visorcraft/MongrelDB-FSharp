// Run: dotnet fsi examples/query_builder.fsx
// Requires: mongreldb-server running on http://127.0.0.1:8453
//
// Example: query builder conditions with the MongrelDB F# client.
//
// Creates a table, inserts five rows with varying scores, then uses the native
// query builder to fetch rows by a range condition and by an exact primary-key
// match. Cleans up by dropping the table.

#I "src/Visorcraft.MongrelDB/bin/Debug/net8.0"
#r "Visorcraft.MongrelDB.dll"

open System
open System.Collections.Generic
open Visorcraft.MongrelDB

let url = "http://127.0.0.1:8453"
// Unique suffix per run so repeated/concurrent runs don't collide.
let suffix = string (DateTimeOffset.UtcNow.ToUnixTimeSeconds()) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8)
let table = "example_query_" + suffix

let db = new Client(url = url)

if not (db.Health()) then
    eprintfn "daemon not reachable at %s" url
    exit 1
printfn "Connected to MongrelDB"

let col id name ty =
    let d = Dictionary<string, obj>()
    d.["id"] <- box id
    d.["name"] <- box name
    d.["ty"] <- box ty
    d.["primary_key"] <- box (id = 1)
    d.["nullable"] <- box false
    upcast d

let cells pairs =
    let d = Dictionary<int, obj>()
    for (k, v) in pairs do d.[k] <- v
    upcast d

let cond pairs =
    let d = Dictionary<string, obj>()
    for (k, v) in pairs do d.[k] <- v
    upcast d

try
    db.CreateTable(table, [|
        col 1 "id"    "int64"
        col 2 "name"  "varchar"
        col 3 "score" "float64"
    |]) |> ignore
    printfn "Created table %s" table

    // Five rows with varying scores.
    db.Put(table, cells [1, box 1; 2, box "Alice"; 3, box 40.0]) |> ignore
    db.Put(table, cells [1, box 2; 2, box "Bob";   3, box 65.0]) |> ignore
    db.Put(table, cells [1, box 3; 2, box "Carol"; 3, box 82.0]) |> ignore
    db.Put(table, cells [1, box 4; 2, box "Dave";  3, box 91.0]) |> ignore
    db.Put(table, cells [1, box 5; 2, box "Eve";   3, box 12.5]) |> ignore
    printfn "Inserted 5 rows"

    // Range condition: scores in [60.0, 90.0]. "column" maps to column_id, so
    // pass the numeric column id (3), not the name. The "score" column is
    // float64, so use the range_f64 condition (plain "range" expects an i64
    // bound and rejects floats); range_f64 also requires the inclusivity flags
    // (min_inclusive/max_inclusive -> lo_inclusive/hi_inclusive).
    let rng =
        db.Query(table)
          .Where("range_f64",
                 cond ["column", box 3; "min", box 60.0; "max", box 90.0;
                       "min_inclusive", box true; "max_inclusive", box true])
          .Execute()
    printfn "Range query (score in [60,90]) returned %d rows:" rng.Length
    for row in rng do printfn "  %A" row

    // Primary-key condition: fetch the single row with id == 4.
    let pk = db.Query(table).Where("pk", cond ["value", box 4]).Execute()
    printfn "PK query (id == 4) returned %d rows:" pk.Length
    for row in pk do printfn "  %A" row
finally
    try db.DropTable(table) with _ -> ()
    printfn "Dropped table %s" table

(db :> IDisposable).Dispose()
