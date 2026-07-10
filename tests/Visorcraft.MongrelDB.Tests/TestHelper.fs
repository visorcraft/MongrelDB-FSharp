namespace Visorcraft.MongrelDB.Tests

open System
open System.Collections.Generic

/// <summary>Helpers shared by the offline unit tests and the live conformance suite.</summary>
module TestHelper =

    /// <summary>Build a typed int64 column descriptor.</summary>
    let intCol (id: int) (name: string) (primaryKey: bool) : IDictionary<string, obj> =
        let d = Dictionary<string, obj>()
        d.["id"] <- box id
        d.["name"] <- box name
        d.["ty"] <- box "int64"
        d.["primary_key"] <- box primaryKey
        d.["nullable"] <- box false
        upcast d

    /// <summary>Build a typed int64 column descriptor that is not a primary key.</summary>
    let intColN (id: int) (name: string) : IDictionary<string, obj> =
        intCol id name false

    /// <summary>Build a typed float64 column descriptor.</summary>
    let floatCol (id: int) (name: string) : IDictionary<string, obj> =
        let d = Dictionary<string, obj>()
        d.["id"] <- box id
        d.["name"] <- box name
        d.["ty"] <- box "float64"
        d.["primary_key"] <- box false
        d.["nullable"] <- box false
        upcast d

    /// <summary>A unique table name per call to isolate each test's data.</summary>
    let uniqueTable (prefix: string) : string =
        let stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        let rnd = Random()
        let rand = rnd.Next(0x10000, 0xFFFFF).ToString("x")
        prefix + "_" + string stamp + "_" + rand

    /// <summary>Drop +name+ if present then create it with the given columns.</summary>
    let freshTable (client: Client) (name: string) (columns: IDictionary<string, obj>[]) : unit =
        try client.DropTable(name) with :? MongrelDBException -> ()
        client.CreateTable(name, columns) |> ignore

    /// <summary>Drop +name+ if present (tolerates a missing table).</summary>
    let cleanup (client: Client) (name: string) : unit =
        try client.DropTable(name) with :? MongrelDBException -> ()
