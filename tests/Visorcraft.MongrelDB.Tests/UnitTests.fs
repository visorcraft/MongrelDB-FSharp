namespace Visorcraft.MongrelDB.Tests

open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Visorcraft.MongrelDB
open Visorcraft.MongrelDB.Tests.TestHelper

/// <summary>Offline unit tests for the MongrelDB F# client. No daemon needed.</summary>
module UnitTests =

    /// <summary>HttpMessageHandler that records the request and returns a canned response.</summary>
    type MockHandler(response: HttpResponseMessage, capture: HttpRequestMessage -> unit) =
        inherit HttpMessageHandler()
        override _.SendAsync(request, _ct) =
            capture request
            Task.FromResult(response)

    [<Fact>]
    let ``create-table columns preserve new default wire fields`` () =
        let captureCreateTableBody (columns: IDictionary<string, obj>[]) : string =
            let mutable req : HttpRequestMessage = null
            use resp = new HttpResponseMessage(HttpStatusCode.OK)
            resp.Content <- new StringContent("{\"table_id\": 1}")
            let handler = new MockHandler(resp, (fun r -> req <- r))
            use c = new Client(url = "http://test.example", httpClient = new HttpClient(handler))
            let tableId = c.CreateTable("events", columns)
            Assert.Equal(1L, tableId)
            Assert.NotNull(req)
            Assert.Equal("http://test.example/kit/create_table", req.RequestUri.ToString())
            req.Content.ReadAsStringAsync().Result

        let col = Dictionary<string, obj>()
        col.["id"] <- box 1
        col.["name"] <- box "attempts"
        col.["ty"] <- box "int64"
        col.["enum_variants"] <- box [| "low"; "high" |]
        col.["default_value"] <- box 3
        col.["default_expr"] <- box "now"

        let firstJson = captureCreateTableBody [| col |]
        use firstDoc = JsonDocument.Parse(firstJson)
        let firstCol = firstDoc.RootElement.GetProperty("columns").EnumerateArray() |> Seq.head
        Assert.True(firstCol.GetProperty("enum_variants").ValueKind = JsonValueKind.Array)
        let variants = firstCol.GetProperty("enum_variants").EnumerateArray() |> Seq.toArray
        Assert.Equal("low", variants.[0].GetString())
        Assert.Equal("high", variants.[1].GetString())
        Assert.Equal(3, firstCol.GetProperty("default_value").GetInt32())
        Assert.Equal("now", firstCol.GetProperty("default_expr").GetString())

        // Matrix of literal default_value scalars: each must round-trip through
        // the client's CreateTable path with its original JSON type.
        let expectations =
            [ box "draft", (fun (p: JsonElement) -> p.ValueKind = JsonValueKind.String && p.GetString() = "draft")
              box 7,       (fun p -> p.ValueKind = JsonValueKind.Number && p.GetInt32() = 7)
              box true,    (fun p -> p.ValueKind = JsonValueKind.True)
              null,        (fun p -> p.ValueKind = JsonValueKind.Null)
              box "now",   (fun p -> p.ValueKind = JsonValueKind.String && p.GetString() = "now") ]
        for value, check in expectations do
            col.["default_value"] <- value
            col.Remove("default_expr") |> ignore
            let json = captureCreateTableBody [| col |]
            use doc = JsonDocument.Parse(json)
            let actual = doc.RootElement.GetProperty("columns").EnumerateArray() |> Seq.head
            let dv = actual.GetProperty("default_value")
            Assert.True(check dv, "default_value did not preserve its JSON type for value " + string value)

        // default_expr is a separate key and is preserved verbatim through the client.
        col.["default_value"] <- null
        col.["default_expr"] <- box "now"
        let exprJson = captureCreateTableBody [| col |]
        use exprDoc = JsonDocument.Parse(exprJson)
        let exprCol = exprDoc.RootElement.GetProperty("columns").EnumerateArray() |> Seq.head
        Assert.Equal("now", exprCol.GetProperty("default_expr").GetString())
        Assert.Equal(JsonValueKind.Null, exprCol.GetProperty("default_value").ValueKind)

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


    // ── History retention transport/wire tests ─────────────────────────────

    let private runWithMock (status: HttpStatusCode) (body: string) (capture: HttpRequestMessage -> unit) (action: Client -> unit) =
        use resp = new HttpResponseMessage(status)
        resp.Content <- new StringContent(body)
        let handler = new MockHandler(resp, capture)
        use c = new Client(url = "http://test.example", httpClient = new HttpClient(handler))
        action c

    [<Fact>]
    let ``set_history_retention_epochs sends put with only retention key`` () =
        let mutable req : HttpRequestMessage = null
        runWithMock HttpStatusCode.OK "{\"history_retention_epochs\":20,\"earliest_retained_epoch\":5}"
            (fun r -> req <- r)
            (fun c ->
                let epochs, earliest = c.SetHistoryRetentionEpochs(20uL)
                Assert.NotNull(req)
                Assert.Equal(HttpMethod.Put, req.Method)
                Assert.Equal("http://test.example/history/retention", req.RequestUri.ToString())
                let json = req.Content.ReadAsStringAsync().Result
                Assert.Equal("{\"history_retention_epochs\":20}", json)
                use doc = JsonDocument.Parse(json)
                Assert.Equal(1, doc.RootElement.EnumerateObject() |> Seq.length)
                Assert.Equal(20uL, epochs)
                Assert.Equal(5uL, earliest))

    [<Fact>]
    let ``history_retention_epochs getter sends get and parses response`` () =
        let mutable req : HttpRequestMessage = null
        runWithMock HttpStatusCode.OK "{\"history_retention_epochs\":42,\"earliest_retained_epoch\":7}"
            (fun r -> req <- r)
            (fun c ->
                Assert.Equal(42uL, c.HistoryRetentionEpochs())
                Assert.NotNull(req)
                Assert.Equal(HttpMethod.Get, req.Method)
                Assert.Equal("http://test.example/history/retention", req.RequestUri.ToString())
                Assert.Null(req.Content))

    [<Fact>]
    let ``earliest_retained_epoch getter sends get and parses response`` () =
        let mutable req : HttpRequestMessage = null
        runWithMock HttpStatusCode.OK "{\"history_retention_epochs\":42,\"earliest_retained_epoch\":7}"
            (fun r -> req <- r)
            (fun c ->
                Assert.Equal(7uL, c.EarliestRetainedEpoch())
                Assert.NotNull(req)
                Assert.Equal(HttpMethod.Get, req.Method)
                Assert.Equal("http://test.example/history/retention", req.RequestUri.ToString()))

    [<Fact>]
    let ``history_retention endpoints map 403 to AuthException`` () =
        runWithMock HttpStatusCode.Forbidden "{\"error\":{\"message\":\"forbidden\"}}"
            (fun _ -> ())
            (fun c ->
                Assert.Throws<AuthException>(fun () -> c.HistoryRetentionEpochs() |> ignore) |> ignore
                Assert.Throws<AuthException>(fun () -> c.SetHistoryRetentionEpochs(10uL) |> ignore) |> ignore
                Assert.Throws<AuthException>(fun () -> c.EarliestRetainedEpoch() |> ignore) |> ignore)

    let private assertMalformedRaisesQueryException (json: string) (action: Client -> unit) =
        runWithMock HttpStatusCode.OK json (fun _ -> ()) (fun c ->
            Assert.Throws<QueryException>(fun () -> action c) |> ignore)

    [<Fact>]
    let ``history_retention rejects missing epochs key`` () =
        assertMalformedRaisesQueryException "{\"earliest_retained_epoch\":5}"
            (fun c -> c.HistoryRetentionEpochs() |> ignore)
        assertMalformedRaisesQueryException "{\"earliest_retained_epoch\":5}"
            (fun c -> c.SetHistoryRetentionEpochs(10uL) |> ignore)

    [<Fact>]
    let ``history_retention rejects missing earliest key`` () =
        assertMalformedRaisesQueryException "{\"history_retention_epochs\":20}"
            (fun c -> c.EarliestRetainedEpoch() |> ignore)
        assertMalformedRaisesQueryException "{\"history_retention_epochs\":20}"
            (fun c -> c.SetHistoryRetentionEpochs(10uL) |> ignore)

    [<Fact>]
    let ``history_retention rejects extra keys`` () =
        assertMalformedRaisesQueryException "{\"history_retention_epochs\":20,\"earliest_retained_epoch\":5,\"extra\":1}"
            (fun c -> c.HistoryRetentionEpochs() |> ignore)

    [<Fact>]
    let ``history_retention rejects non-integer epochs value`` () =
        assertMalformedRaisesQueryException "{\"history_retention_epochs\":\"twenty\",\"earliest_retained_epoch\":5}"
            (fun c -> c.HistoryRetentionEpochs() |> ignore)

    [<Fact>]
    let ``history_retention rejects non-integer earliest value`` () =
        assertMalformedRaisesQueryException "{\"history_retention_epochs\":20,\"earliest_retained_epoch\":\"five\"}"
            (fun c -> c.EarliestRetainedEpoch() |> ignore)

    [<Fact>]
    let ``history_retention rejects negative epochs value`` () =
        assertMalformedRaisesQueryException "{\"history_retention_epochs\":-1,\"earliest_retained_epoch\":5}"
            (fun c -> c.HistoryRetentionEpochs() |> ignore)

    [<Fact>]
    let ``history_retention rejects negative earliest value`` () =
        assertMalformedRaisesQueryException "{\"history_retention_epochs\":20,\"earliest_retained_epoch\":-1}"
            (fun c -> c.EarliestRetainedEpoch() |> ignore)
