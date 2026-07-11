namespace Visorcraft.MongrelDB.Tests

open System
open System.Collections.Generic
open System.Text.Json
open Xunit
open Visorcraft.MongrelDB
open Visorcraft.MongrelDB.Tests.TestHelper

/// <summary>Offline unit tests for the MongrelDB F# client. No daemon needed.</summary>
module UnitTests =

    [<Fact>]
    let ``create-table columns preserve new default wire fields`` () =
        let col = Dictionary<string, obj>()
        col.["id"] <- box 1
        col.["name"] <- box "attempts"
        col.["ty"] <- box "int64"
        col.["enum_variants"] <- box [| "low"; "high" |]
        col.["default_value"] <- box 3
        col.["default_expr"] <- box "uuid"
        let wire = JsonSerializer.Serialize([| col |])
        Assert.Contains("\"enum_variants\":[\"low\",\"high\"]", wire)
        Assert.Contains("\"default_value\":3", wire)
        Assert.Contains("\"default_expr\":\"uuid\"", wire)
        for value, expected in [box "draft", "\"draft\""; box true, "true"; null, "null"] do
            col.["default_value"] <- value
            Assert.Contains("\"default_value\":" + expected, JsonSerializer.Serialize(col))

    // ── QueryBuilder.NormalizeCondition ────────────────────────────────────

    [<Fact>]
    let ``NormalizeCondition translates the generic aliases`` () =
        let p = Dictionary<string, obj>()
        p.["column"] <- box 3
        p.["min"] <- box 100
        p.["max"] <- box 150
        p.["min_inclusive"] <- box true
        p.["max_inclusive"] <- box false
        let n = QueryBuilder.NormalizeCondition("range", p)
        Assert.Equal(box 3, n.["column_id"])
        Assert.Equal(box 100, n.["lo"])
        Assert.Equal(box 150, n.["hi"])
        Assert.Equal(box true, n.["lo_inclusive"])
        Assert.Equal(box false, n.["hi_inclusive"])

    [<Fact>]
    let ``NormalizeCondition passes canonical keys through unchanged`` () =
        let p = Dictionary<string, obj>()
        p.["column_id"] <- box 3
        p.["lo"] <- box 100
        p.["hi"] <- box 150
        let n = QueryBuilder.NormalizeCondition("range", p)
        Assert.Equal(box 3, n.["column_id"])
        Assert.Equal(box 100, n.["lo"])
        Assert.Equal(box 150, n.["hi"])

    [<Fact>]
    let ``NormalizeCondition maps value to pattern for fm_contains`` () =
        let p = Dictionary<string, obj>()
        p.["column"] <- box 2
        p.["value"] <- box "database performance"
        let n = QueryBuilder.NormalizeCondition("fm_contains", p)
        Assert.Equal(box 2, n.["column_id"])
        Assert.Equal(box "database performance", n.["pattern"])

    [<Fact>]
    let ``NormalizeCondition maps value to patterns for fm_contains_all`` () =
        let p = Dictionary<string, obj>()
        p.["column"] <- box 2
        p.["value"] <- box ([| "database" |] :> obj)
        let n = QueryBuilder.NormalizeCondition("fm_contains_all", p)
        Assert.Equal(box 2, n.["column_id"])
        Assert.True(n.ContainsKey("patterns"))

    [<Fact>]
    let ``NormalizeCondition does NOT alias value for pk`` () =
        let p = Dictionary<string, obj>()
        p.["value"] <- box 42
        let n = QueryBuilder.NormalizeCondition("pk", p)
        Assert.Equal(box 42, n.["value"])

    // ── QueryBuilder.Build ─────────────────────────────────────────────────

    [<Fact>]
    let ``Build includes conditions, projection, and limit when set`` () =
        use c = new Client(url = "http://127.0.0.1:1")
        let cond = Dictionary<string, obj>()
        cond.["column"] <- box 3
        cond.["min"] <- box 100
        let q = c.Query("orders").Where("range", cond).ProjectionOf([| 1; 2 |]).LimitTo(10)
        let payload = q.Build()
        Assert.Equal(box "orders", payload.["table"])
        let conds = payload.["conditions"] :?> obj[]
        Assert.Equal(1, conds.Length)
        let rng = conds.[0] :?> IDictionary<string, obj>
        let inner = rng.["range"] :?> IDictionary<string, obj>
        Assert.Equal(box 3, inner.["column_id"])
        Assert.Equal(box 100, inner.["lo"])
        Assert.Equal(box [| 1; 2 |], payload.["projection"])
        Assert.Equal(box 10, payload.["limit"])

    [<Fact>]
    let ``Build omits unset fields`` () =
        use c = new Client(url = "http://127.0.0.1:1")
        let payload = c.Query("orders").Build()
        Assert.Equal(box "orders", payload.["table"])
        Assert.False(payload.ContainsKey("conditions"))
        Assert.False(payload.ContainsKey("projection"))
        Assert.False(payload.ContainsKey("limit"))

    [<Fact>]
    let ``Truncated defaults to false before execute`` () =
        use c = new Client(url = "http://127.0.0.1:1")
        Assert.False(c.Query("orders").Truncated)

    // ── Client.FlattenCells ────────────────────────────────────────────────

    [<Fact>]
    let ``FlattenCells flattens a column-id-to-value map into pairs`` () =
        let cells = Dictionary<int, obj>()
        cells.[1] <- box "Alice"
        cells.[3] <- box 99.5
        let flat = Client.FlattenCells(cells)
        // Pair order is not significant; collect into a map col_id -> value.
        let map = Dictionary<int, obj>()
        let arr = flat
        let i = ref 0
        while !i < arr.Length do
            let colId = unbox<int> arr.[!i]
            map.[colId] <- arr.[!i + 1]
            incr i
            incr i
        Assert.Equal(box "Alice", map.[1])
        Assert.Equal(box 99.5, map.[3])

    [<Fact>]
    let ``FlattenCells returns an empty array for empty cells`` () =
        let cells = Dictionary<int, obj>() :> IDictionary<int, obj>
        Assert.Empty(Client.FlattenCells(cells))

    // ── Client construction ────────────────────────────────────────────────

    [<Fact>]
    let ``defaults to the standard daemon URL`` () =
        use c = new Client()
        Assert.Equal(Client.DefaultBaseUrl, c.BaseUrl)

    [<Fact>]
    let ``strips a trailing slash`` () =
        use c = new Client(url = "http://127.0.0.1:8453/")
        Assert.Equal("http://127.0.0.1:8453", c.BaseUrl)

    [<Fact>]
    let ``falls back to the default when the URL is empty`` () =
        use c = new Client(url = "")
        Assert.Equal(Client.DefaultBaseUrl, c.BaseUrl)

    [<Fact>]
    let ``detects configured auth`` () =
        use a = new Client(token = "t")
        Assert.True(a.Auth)
        use b = new Client(username = "u", password = "p")
        Assert.True(b.Auth)
        use d = new Client()
        Assert.False(d.Auth)

    // ── CRLF injection resistance (RFC 3986 percent-encoding) ──────────────

    [<Fact>]
    let ``percent-encoding rejects CR/LF in table names`` () =
        // Mirror the client's algorithm to assert the CRLF-resistance contract.
        let escape (seg: string) =
            let sb = Text.StringBuilder()
            for ch in seg.ToCharArray() do
                let i = int ch
                if (i >= int 'A' && i <= int 'Z') || (i >= int 'a' && i <= int 'z')
                   || (i >= int '0' && i <= int '9')
                   || ch = '-' || ch = '.' || ch = '_' || ch = '~' then
                    sb.Append(ch) |> ignore
                else
                    sb.Append('%').Append((uint16 ch).ToString("x2")) |> ignore
            sb.ToString()
        Assert.Equal("a%0d%0ab", escape("a\r\nb"))

    [<Fact>]
    let ``percent-encoding encodes spaces and slashes`` () =
        let escape (seg: string) =
            let sb = Text.StringBuilder()
            for ch in seg.ToCharArray() do
                let i = int ch
                if (i >= int 'A' && i <= int 'Z') || (i >= int 'a' && i <= int 'z')
                   || (i >= int '0' && i <= int '9')
                   || ch = '-' || ch = '.' || ch = '_' || ch = '~' then
                    sb.Append(ch) |> ignore
                else
                    sb.Append('%').Append((uint16 ch).ToString("x2")) |> ignore
            sb.ToString()
        Assert.Equal("a%20b%2fc", escape("a b/c"))

    // ── ConflictException ──────────────────────────────────────────────────

    [<Fact>]
    let ``ConflictException carries ErrorCode and OpIndex`` () =
        let err = ConflictException("dup", "UNIQUE_VIOLATION", Nullable(2))
        Assert.Equal("UNIQUE_VIOLATION", err.ErrorCode)
        Assert.Equal(2, err.OpIndex.Value)
        Assert.Equal("dup", err.Message)

    [<Fact>]
    let ``ConflictException has sensible defaults for code and op_index`` () =
        let err = ConflictException("x", "", Nullable())
        Assert.Equal("", err.ErrorCode)
        Assert.False(err.OpIndex.HasValue)

    // ── Exception hierarchy ───────────────────────────────────────────────

    [<Fact>]
    let ``exceptions inherit from MongrelDBException`` () =
        Assert.True(typeof<MongrelDBException>.IsAssignableFrom(typeof<AuthException>))
        Assert.True(typeof<MongrelDBException>.IsAssignableFrom(typeof<NotFoundException>))
        Assert.True(typeof<MongrelDBException>.IsAssignableFrom(typeof<ConflictException>))
        Assert.True(typeof<MongrelDBException>.IsAssignableFrom(typeof<QueryException>))

    // ── Json.toObject (recursive) ─────────────────────────────────────────

    [<Fact>]
    let ``Json.toObject decodes nested objects and arrays`` () =
        use doc = JsonDocument.Parse("{\"id\": 1, \"name\": \"x\", \"tags\": [1, 2], \"ok\": true}")
        let o = Json.toObject(doc.RootElement) :?> IDictionary<string, obj>
        Assert.Equal(box 1L, o.["id"])
        Assert.Equal(box "x", o.["name"])
        Assert.Equal(box true, o.["ok"])
        let tags = o.["tags"] :?> obj[]
        Assert.Equal(2, tags.Length)
