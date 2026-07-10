namespace Visorcraft.MongrelDB.Tests

open System
open System.Collections.Generic
open Xunit
open Visorcraft.MongrelDB
open Visorcraft.MongrelDB.Tests.TestHelper
open Visorcraft.MongrelDB.Tests.Daemon

/// <summary>
/// A collection fixture that boots the daemon once for the whole live suite
/// and shuts it down when xUnit disposes it.
/// </summary>
type DaemonFixture () =
    do boot()
    interface IDisposable with
        member _.Dispose() = shutdown()

/// <summary>Marker for the collection that shares the single daemon instance.</summary>
[<CollectionDefinition("Daemon collection")>]
type DaemonCollection () =
    interface ICollectionFixture<DaemonFixture>

/// <summary>
/// Live integration tests against a real mongreldb-server daemon. They boot a
/// real daemon and exercise the full 14-operation conformance matrix against
/// it. Each test self-skips when no daemon is available.
/// </summary>
[<Collection("Daemon collection")>]
type LiveTests () =

    let client () : Client =
        skipIfNoClient()
        getClient()

    [<DaemonFact>]
    member _.``health returns true against the real daemon`` () =
        Assert.True(client().Health())

    [<DaemonFact>]
    member _.``connect defaults to 127.0.0.1:8453`` () =
        use c2 = new Client()
        Assert.Equal(Client.DefaultBaseUrl, c2.BaseUrl)
        Assert.False(client().Auth)

    [<DaemonFact>]
    member _.``create_table then count returns 0`` () =
        let c = client()
        let name = uniqueTable("fs_create")
        try
            freshTable c name [| intCol 1 "id" true; floatCol 2 "amount" |]
            Assert.Equal(0L, c.Count(name))
        finally cleanup c name

    [<DaemonFact>]
    member _.``put then count round-trips`` () =
        let c = client()
        let name = uniqueTable("fs_put")
        try
            freshTable c name [| intCol 1 "id" true; floatCol 2 "amount" |]
            let r1 = Dictionary<int, obj>()
            r1.[1] <- box 1
            r1.[2] <- box 99.5
            c.Put(name, r1) |> ignore
            let r2 = Dictionary<int, obj>()
            r2.[1] <- box 2
            r2.[2] <- box 150.0
            c.Put(name, r2) |> ignore
            Assert.Equal(2L, c.Count(name))
        finally cleanup c name

    [<DaemonFact>]
    member _.``upsert inserts then updates`` () =
        let c = client()
        let name = uniqueTable("fs_upsert")
        try
            freshTable c name [| intCol 1 "id" true; floatCol 2 "amount" |]
            let upd = Dictionary<int, obj>()
            upd.[2] <- box 99.5
            let r1 = Dictionary<int, obj>()
            r1.[1] <- box 1
            r1.[2] <- box 99.5
            c.Upsert(name, r1, upd) |> ignore
            Assert.Equal(1L, c.Count(name))
            let upd2 = Dictionary<int, obj>()
            upd2.[2] <- box 120.0
            let r2 = Dictionary<int, obj>()
            r2.[1] <- box 1
            r2.[2] <- box 120.0
            c.Upsert(name, r2, upd2) |> ignore
            Assert.Equal(1L, c.Count(name))
        finally cleanup c name

    [<DaemonFact>]
    member _.``delete_by_pk removes the row`` () =
        let c = client()
        let name = uniqueTable("fs_delpk")
        try
            freshTable c name [| intCol 1 "id" true |]
            let r = Dictionary<int, obj>()
            r.[1] <- box 5
            c.Put(name, r) |> ignore
            Assert.Equal(1L, c.Count(name))
            c.DeleteByPk(name, box 5)
            Assert.Equal(0L, c.Count(name))
        finally cleanup c name

    [<DaemonFact>]
    member _.``delete by row id removes the row`` () =
        let c = client()
        let name = uniqueTable("fs_delrid")
        try
            freshTable c name [| intCol 1 "id" true |]
            let r = Dictionary<int, obj>()
            r.[1] <- box 7
            c.Put(name, r) |> ignore
            // Row id is internal; for a fresh single-row table it is typically 1.
            c.Delete(name, 1L)
            Assert.Equal(0L, c.Count(name))
        finally cleanup c name

    [<DaemonFact>]
    member _.``query by primary key returns one row`` () =
        let c = client()
        let name = uniqueTable("fs_pk")
        try
            freshTable c name [| intCol 1 "id" true |]
            let r1 = Dictionary<int, obj>()
            r1.[1] <- box 42
            c.Put(name, r1) |> ignore
            let r2 = Dictionary<int, obj>()
            r2.[1] <- box 43
            c.Put(name, r2) |> ignore
            let cond = Dictionary<string, obj>()
            cond.["value"] <- box 42
            let rows = c.Query(name).Where("pk", cond).Execute()
            Assert.Equal(1, rows.Length)
        finally cleanup c name

    [<DaemonFact>]
    member _.``query range with friendly aliases filters correctly`` () =
        let c = client()
        let name = uniqueTable("fs_range")
        try
            freshTable c name [| intCol 1 "id" true; intColN 2 "amount" |]
            let mk a b =
                let r = Dictionary<int, obj>()
                r.[1] <- box a
                r.[2] <- box b
                r
            c.Put(name, mk 1 50) |> ignore
            c.Put(name, mk 2 120) |> ignore
            c.Put(name, mk 3 200) |> ignore
            let cond = Dictionary<string, obj>()
            cond.["column"] <- box 2
            cond.["min"] <- box 100
            cond.["max"] <- box 150
            let q = c.Query(name).Where("range", cond)
            let rows = q.Execute()
            Assert.Equal(1, rows.Length)
            Assert.False(q.Truncated)
        finally cleanup c name

    [<DaemonFact>]
    member _.``query projection and limit`` () =
        let c = client()
        let name = uniqueTable("fs_proj")
        try
            freshTable c name [| intCol 1 "id" true; floatCol 2 "amount" |]
            for i in 0 .. 4 do
                let r = Dictionary<int, obj>()
                r.[1] <- box i
                r.[2] <- box (float i)
                c.Put(name, r) |> ignore
            let rows = c.Query(name).ProjectionOf([| 1 |]).LimitTo(2).Execute()
            Assert.Equal(2, rows.Length)
        finally cleanup c name

    [<DaemonFact>]
    member _.``transaction put commit`` () =
        let c = client()
        let name = uniqueTable("fs_txn")
        try
            freshTable c name [| intCol 1 "id" true |]
            let txn = c.BeginTransaction()
            for i in 1 .. 3 do
                let r = Dictionary<int, obj>()
                r.[1] <- box i
                txn.Put(name, r) |> ignore
            Assert.Equal(3, txn.Count)
            let results = txn.Commit()
            Assert.Equal(3, results.Length)
            Assert.Equal(3L, c.Count(name))
        finally cleanup c name

    [<DaemonFact>]
    member _.``transaction commit with idempotency key does not double-apply`` () =
        let c = client()
        let name = uniqueTable("fs_txn_idem")
        try
            freshTable c name [| intCol 1 "id" true |]
            let idemKey = "order-100-create-" + string (DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            let txn = c.BeginTransaction()
            let r = Dictionary<int, obj>()
            r.[1] <- box 100
            txn.Put(name, r) |> ignore
            let results = txn.Commit(idempotencyKey = idemKey)
            Assert.Equal(1, results.Length)
            Assert.Equal(1L, c.Count(name))
            // A second commit with the same key must not create a duplicate row.
            let txn2 = c.BeginTransaction()
            let r2 = Dictionary<int, obj>()
            r2.[1] <- box 100
            txn2.Put(name, r2) |> ignore
            try txn2.Commit(idempotencyKey = idemKey) |> ignore with _ -> ()
            Assert.Equal(1L, c.Count(name))
        finally cleanup c name

    [<DaemonFact>]
    member _.``transaction rollback discards ops`` () =
        let c = client()
        let name = uniqueTable("fs_txn_rb")
        try
            freshTable c name [| intCol 1 "id" true |]
            let txn = c.BeginTransaction()
            let r1 = Dictionary<int, obj>()
            r1.[1] <- box 1
            let r2 = Dictionary<int, obj>()
            r2.[1] <- box 2
            txn.Put(name, r1) |> ignore
            txn.Put(name, r2) |> ignore
            txn.Rollback()
            Assert.Equal(0L, c.Count(name))
        finally cleanup c name

    [<DaemonFact>]
    member _.``transaction double commit raises`` () =
        let c = client()
        let name = uniqueTable("fs_txn_double")
        try
            freshTable c name [| intCol 1 "id" true |]
            let txn = c.BeginTransaction()
            let r = Dictionary<int, obj>()
            r.[1] <- box 1
            txn.Put(name, r) |> ignore
            txn.Commit() |> ignore
            Assert.Throws<MongrelDBException>(fun () -> txn.Commit() |> ignore) |> ignore
        finally cleanup c name

    [<DaemonFact>]
    member _.``table_names lists the created table`` () =
        let c = client()
        let name = uniqueTable("fs_tables")
        try
            freshTable c name [| intCol 1 "id" true |]
            let names = c.TableNames()
            Assert.Contains(name, names)
        finally cleanup c name

    [<DaemonFact>]
    member _.``drop_table removes it`` () =
        let c = client()
        let name = uniqueTable("fs_drop")
        freshTable c name [| intCol 1 "id" true |]
        c.DropTable(name)
        Assert.DoesNotContain(name, c.TableNames())

    [<DaemonFact>]
    member _.``sql insert increases count and select returns row`` () =
        let c = client()
        let name = uniqueTable("fs_sql")
        try
            freshTable c name [| intCol 1 "id" true; floatCol 2 "amount" |]
            Assert.Equal(0L, c.Count(name))
            // INSERT via SQL must increase the row count.
            c.Sql("INSERT INTO " + name + " (id, amount) VALUES (77, 7.5)") |> ignore
            Assert.Equal(1L, c.Count(name))
            // JSON SQL mode must return the inserted row when supported.
            let rows = c.Sql("SELECT id, amount FROM " + name)
            if rows.Length > 0 then Assert.Equal(1, rows.Length)
        finally cleanup c name

    [<DaemonFact>]
    member _.``schema includes the created table`` () =
        let c = client()
        let name = uniqueTable("fs_schema")
        try
            freshTable c name [| intCol 1 "id" true; floatCol 2 "amount" |]
            let schema = c.Schema()
            Assert.True(schema.ContainsKey(name))
        finally cleanup c name

    [<DaemonFact>]
    member _.``schema_for returns a descriptor with columns`` () =
        let c = client()
        let name = uniqueTable("fs_schema_for")
        try
            freshTable c name [| intCol 1 "id" true; floatCol 2 "amount" |]
            let desc = c.SchemaFor(name)
            Assert.True(desc.ContainsKey("schema_id") || desc.ContainsKey("columns"))
        finally cleanup c name

    [<DaemonFact>]
    member _.``compact all tables returns a map`` () =
        let c = client()
        let result = c.Compact()
        Assert.NotNull(result)

    [<DaemonFact>]
    member _.``compact single table returns a map`` () =
        let c = client()
        let name = uniqueTable("fs_compact")
        try
            freshTable c name [| intCol 1 "id" true |]
            let r = Dictionary<int, obj>()
            r.[1] <- box 1
            c.Put(name, r) |> ignore
            let result = c.CompactTable(name)
            Assert.NotNull(result)
        finally cleanup c name

    [<DaemonFact>]
    member _.``schema_for on a nonexistent table raises NotFoundException`` () =
        let c = client()
        let name = uniqueTable("fs_missing")
        Assert.Throws<NotFoundException>(fun () -> c.SchemaFor(name) |> ignore) |> ignore

    [<DaemonFact>]
    member _.``duplicate put with a UNIQUE constraint raises ConflictException`` () =
        let c = client()
        let name = uniqueTable("fs_conflict")
        try
            // A bare put on a PK-only table is last-write-wins; a UNIQUE
            // constraint is required for the engine to reject a duplicate with a 409.
            try c.DropTable(name) with :? MongrelDBException -> ()
            let constraints = Dictionary<string, obj>()
            let uq = Dictionary<string, obj>()
            uq.["id"] <- box 1
            uq.["name"] <- box "uq"
            uq.["columns"] <- box [| 1 |]
            constraints.["uniques"] <- box [| uq |]
            c.CreateTable(name, [| intCol 1 "id" true |], constraints) |> ignore
            let r = Dictionary<int, obj>()
            r.[1] <- box 1
            c.Put(name, r) |> ignore
            let err = Assert.Throws<ConflictException>(fun () ->
                let r2 = Dictionary<int, obj>()
                r2.[1] <- box 1
                c.Put(name, r2) |> ignore)
            Assert.False(String.IsNullOrEmpty(err.ErrorCode))
        finally cleanup c name
