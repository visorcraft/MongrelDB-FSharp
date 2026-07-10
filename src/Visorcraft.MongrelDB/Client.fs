namespace Visorcraft.MongrelDB

open System
open System.Collections.Generic
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json

/// <summary>Internal helpers shared across the client (JSON envelope decoding).</summary>
module internal Helpers =
    /// <summary>Decode the server's JSON error envelope ({error: {message, code, op_index}}) or a flat object.</summary>
    let decodeErrorEnvelope (body: string) : string * string * int option =
        if String.IsNullOrEmpty(body) then ("", "", None)
        else
            let trimmed = body.TrimStart()
            if not (trimmed.StartsWith("{")) then (body, "", None)
            else
                try
                    use doc = JsonDocument.Parse(body)
                    if doc.RootElement.ValueKind <> JsonValueKind.Object then (body, "", None)
                    else
                        match doc.RootElement.TryGetProperty("error") with
                        | true, err when err.ValueKind = JsonValueKind.Object ->
                            let msg = match err.TryGetProperty("message") with true, m -> m.GetString() | false, _ -> null
                            let code = match err.TryGetProperty("code") with true, c -> c.GetString() | false, _ -> null
                            let opIdx =
                                match err.TryGetProperty("op_index") with
                                | true, p when p.ValueKind = JsonValueKind.Number -> Some (p.GetInt32())
                                | _ -> None
                            (msg, code, opIdx)
                        | _ ->
                            let msg = match doc.RootElement.TryGetProperty("message") with true, m -> m.GetString() | false, _ -> null
                            let code = match doc.RootElement.TryGetProperty("code") with true, c -> c.GetString() | false, _ -> null
                            (msg, code, None)
                with :? JsonException -> (body, "", None)

    /// <summary>Decode the results array out of a <c>/kit/txn</c> response body.</summary>
    let decodeResults (body: string) : IDictionary<string, obj>[] =
        if String.IsNullOrEmpty(body) then [||]
        else
            try
                use doc = JsonDocument.Parse(body)
                if doc.RootElement.ValueKind <> JsonValueKind.Object then [||]
                else
                    match doc.RootElement.TryGetProperty("results") with
                    | true, p when p.ValueKind = JsonValueKind.Array ->
                        p.EnumerateArray()
                        |> Seq.map (fun row ->
                            let d = Dictionary<string, obj>()
                            for prop in row.EnumerateObject() do
                                d.[prop.Name] <- Json.toObject(prop.Value)
                            d :> IDictionary<string, obj>)
                        |> Seq.toArray
                    | _ -> [||]
            with :? JsonException as ex ->
                raise (QueryException("Failed to decode transaction response: " + ex.Message, ex))

/// <summary>
/// Pure F# HTTP client for a running <c>mongreldb-server</c> daemon.
///
/// Talks to the daemon's JSON API over <c>HttpClient</c> and decodes with
/// <c>System.Text.Json</c> -- both built into .NET 8, so there are no external
/// dependencies. The API mirrors the MongrelDB PHP, Go, Ruby, and Java clients:
/// typed CRUD over the Kit transaction endpoint, a fluent query builder that
/// pushes conditions down to the engine's native indexes, idempotent batch
/// transactions, full SQL access, schema introspection, and maintenance
/// operations.
///
/// Connect with a base URL and optional credentials:
///
/// <code>
/// let db = new Client(url = "http://127.0.0.1:8453")
/// db.Health() // true
/// </code>
///
/// A client is safe for concurrent use across async workflows once configured.
/// </summary>
type Client
    (   ?url: string,
        ?token: string,
        ?username: string,
        ?password: string,
        ?timeout: TimeSpan,
        ?httpClient: HttpClient) as this =

    let baseUrl =
        let raw = defaultArg url Client.DefaultBaseUrl
        let b = if String.IsNullOrEmpty(raw) then Client.DefaultBaseUrl else raw
        if b.EndsWith("/") then b.TrimEnd('/') else b

    let token = token |> Option.bind (fun t -> if String.IsNullOrEmpty(t) then None else Some t)
    let username = username |> Option.bind (fun u -> if String.IsNullOrEmpty(u) then None else Some u)
    let password = defaultArg password ""

    let ownsClient =
        match httpClient with
        | Some _ -> false
        | None -> true

    let http =
        match httpClient with
        | Some c -> c
        | None ->
            let h = new HttpClient()
            h.Timeout <- defaultArg timeout (TimeSpan.FromSeconds(60.0))
            h

    let encodeJson (value: obj) : string =
        try
            JsonSerializer.Serialize(value, Json.serOpts)
        with
        | :? JsonException as ex ->
            raise (QueryException(
                "Request payload cannot be JSON-encoded: " + ex.Message
                + ". (NaN, Infinity, and recursive structures have no JSON representation.)", ex))

    let urlPathEscape (segment: string) : string =
        let sb = StringBuilder(segment.Length)
        for b in segment.ToCharArray() do
            let i = int b
            if (i >= int 'A' && i <= int 'Z') || (i >= int 'a' && i <= int 'z')
               || (i >= int '0' && i <= int '9')
               || b = '-' || b = '.' || b = '_' || b = '~' then
                sb.Append(b) |> ignore
            else
                sb.Append('%').Append((uint16 b).ToString("X2").ToLowerInvariant()) |> ignore
        sb.ToString()

    let uriFor (path: string) : string =
        let p = if path.StartsWith("/") then path.Substring(1) else path
        baseUrl + "/" + p

    let applyAuth (req: HttpRequestMessage) : unit =
        match token with
        | Some t ->
            req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", t)
        | None ->
            match username with
            | Some u ->
                let creds = Convert.ToBase64String(Encoding.UTF8.GetBytes(u + ":" + password))
                req.Headers.Authorization <- AuthenticationHeaderValue("Basic", creds)
            | None -> ()

    let throwForStatus (status: int) (body: string) =
        let msg, code, opIdx = Helpers.decodeErrorEnvelope(body)
        let fallback =
            match status with
            | 401 -> "Authentication failed (401)"
            | 403 -> "Authentication failed (403)"
            | 404 -> "Resource not found"
            | 409 -> "Constraint violation"
            | _ -> String.Format("Server error ({0})", status)
        let finalMsg = if String.IsNullOrEmpty(msg) then fallback else msg
        match status with
        | 401 | 403 -> raise (AuthException(finalMsg) :> MongrelDBException)
        | 404 -> raise (NotFoundException(finalMsg) :> MongrelDBException)
        | 409 ->
            let opIdxNullable = match opIdx with Some i -> Nullable(i) | None -> Nullable()
            raise (ConflictException(finalMsg, code, opIdxNullable) :> MongrelDBException)
        | _ -> raise (QueryException(finalMsg) :> MongrelDBException)

    let request (method: HttpMethod) (path: string) (body: IDictionary<string, obj> option) : Response.MongrelDBResponse =
        let uri = uriFor(path)
        use req = new HttpRequestMessage(method, uri)
        req.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))
        applyAuth(req)
        match body with
        | Some b ->
            let json = encodeJson(b)
            req.Content <- new StringContent(json, Encoding.UTF8, "application/json")
        | None -> ()
        let response =
            try
                http.SendAsync(req).Result
            with
            | :? AggregateException as ae ->
                let inner = ae.InnerException
                raise (QueryException("request " + path + " failed: " + inner.Message, inner))
        let status = int response.StatusCode
        let respBody =
            use s = response.Content.ReadAsStreamAsync().Result
            use ms = new IO.MemoryStream()
            // Enforce the 256 MB cap during streaming so an oversized body
            // aborts before it can exhaust memory.
            let max = int64 Client.MaxResponseBytes
            let buffer = Array.zeroCreate<byte> 65536
            let mutable total = 0L
            let mutable n = s.Read(buffer, 0, buffer.Length)
            while n > 0 do
                total <- total + int64 n
                if total > max then
                    raise (QueryException(String.Format("Response body exceeds maximum size of {0} bytes", Client.MaxResponseBytes)))
                ms.Write(buffer, 0, n)
                n <- s.Read(buffer, 0, buffer.Length)
            let bytes = ms.ToArray()
            Encoding.UTF8.GetString(bytes)
        let resp = { Response.Status = status; Response.Body = respBody }
        if resp.Success then resp else throwForStatus status resp.Body

    /// <summary>Default daemon address used when none is supplied.</summary>
    static member DefaultBaseUrl = "http://127.0.0.1:8453"

    /// <summary>Maximum response body size (256 MB). Bodies larger than this are aborted with a <c>QueryException</c>.</summary>
    static member MaxResponseBytes = 268435456

    /// <summary>The daemon base URL the client was configured with (no trailing slash).</summary>
    member _.BaseUrl = baseUrl

    /// <summary>True when a bearer token or basic-auth username is configured.</summary>
    member _.Auth = Option.isSome token || Option.isSome username

    // ── Health & tables ────────────────────────────────────────────────────

    /// <summary>Check whether the daemon is reachable and healthy.</summary>
    member this.Health() : bool =
        try
            this.Get("/health") |> ignore
            true
        with :? MongrelDBException -> false

    /// <summary>List all table names in the database (empty array when none).</summary>
    member this.TableNames() : string[] =
        let resp = this.Get("/tables")
        match Response.json(resp) with
        | Some el when el.ValueKind = JsonValueKind.Array ->
            el.EnumerateArray() |> Seq.map (fun x -> x.GetString()) |> Seq.toArray
        | _ -> [||]

    /// <summary>Create a table with typed columns. Returns the assigned table id.</summary>
    member this.CreateTable(name: string, columns: IDictionary<string, obj>[]) : int64 =
        let body = dict ["name", box name; "columns", box columns]
        let resp = this.Post("/kit/create_table", body)
        match Response.json(resp) with
        | Some el ->
            match el.TryGetProperty("table_id") with
            | true, p -> p.GetInt64()
            | false, _ -> 0L
        | None -> 0L

    /// <summary>Create a table with a <c>constraints</c> block (uniques, foreign keys).</summary>
    member this.CreateTable(name: string, columns: IDictionary<string, obj>[],
                            constraints: IDictionary<string, obj>) : int64 =
        let body = dict ["name", box name; "columns", box columns; "constraints", box constraints]
        let resp = this.Post("/kit/create_table", body)
        match Response.json(resp) with
        | Some el ->
            match el.TryGetProperty("table_id") with
            | true, p -> p.GetInt64()
            | false, _ -> 0L
        | None -> 0L

    /// <summary>Drop a table by name.</summary>
    member this.DropTable(name: string) : unit =
        this.HttpDelete("/tables/" + urlPathEscape(name)) |> ignore

    /// <summary>Get the row count for a table.</summary>
    member this.Count(table: string) : int64 =
        let resp = this.Get("/tables/" + urlPathEscape(table) + "/count")
        match Response.json(resp) with
        | Some el ->
            match el.TryGetProperty("count") with
            | true, p -> p.GetInt64()
            | false, _ -> raise (QueryException("malformed count response from server"))
        | None -> raise (QueryException("malformed count response from server"))

    // ── CRUD (via the Kit typed transaction endpoint) ──────────────────────

    /// <summary>
    /// Insert a row. <c>cells</c> maps column id -> value; flattened to the
    /// server's <c>[col_id, value, ...]</c> array. <c>idempotencyKey</c> makes
    /// the commit safe to retry.
    /// </summary>
    member this.Put(table: string, cells: IDictionary<int, obj>, ?idempotencyKey: string) : IDictionary<string, obj> =
        let op = dict ["put", box (dict ["table", box table; "cells", box (Client.FlattenCells(cells))])]
        let results = this.CommitTxn([op], defaultArg idempotencyKey null)
        if results.Length > 0 then results.[0] else Dictionary<string, obj>() :> IDictionary<string, obj>

    /// <summary>
    /// Upsert a row (insert or update on a primary-key conflict).
    /// <c>updateCells</c> are written on a PK conflict; <c>null</c> means DO NOTHING.
    /// </summary>
    member this.Upsert(table: string, cells: IDictionary<int, obj>,
                       ?updateCells: IDictionary<int, obj>, ?idempotencyKey: string) : IDictionary<string, obj> =
        let baseOp = Dictionary<string, obj>()
        baseOp.["table"] <- box table
        baseOp.["cells"] <- box (Client.FlattenCells(cells))
        match updateCells with
        | Some uc -> baseOp.["update_cells"] <- box (Client.FlattenCells(uc))
        | None -> ()
        let op = dict ["upsert", box baseOp]
        let results = this.CommitTxn([op], defaultArg idempotencyKey null)
        if results.Length > 0 then results.[0] else Dictionary<string, obj>() :> IDictionary<string, obj>

    /// <summary>Delete a row by its internal row id.</summary>
    member this.Delete(table: string, rowId: int64) : unit =
        let op = dict ["delete", box (dict ["table", box table; "row_id", box rowId])]
        this.CommitTxn([op], null) |> ignore

    /// <summary>Delete a row by its primary-key value.</summary>
    member this.DeleteByPk(table: string, pk: obj) : unit =
        let op = dict ["delete_by_pk", box (dict ["table", box table; "pk", box pk])]
        this.CommitTxn([op], null) |> ignore

    // ── Query ──────────────────────────────────────────────────────────────

    /// <summary>Start a fluent <c>QueryBuilder</c> against <c>table</c>.</summary>
    member this.Query(table: string) : QueryBuilder =
        QueryBuilder.Create(this, table)

    // ── SQL ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Execute a SQL statement via the <c>/sql</c> endpoint, requesting JSON
    /// output. The server returns a JSON array of row objects keyed by column
    /// name. For statements that yield no rows (DDL/DML), an empty array is returned.
    /// </summary>
    member this.Sql(sql: string) : IDictionary<string, obj>[] =
        let body = dict ["sql", box sql; "format", box "json"]
        let resp = this.Post("/sql", body)
        if String.IsNullOrEmpty(resp.Body) then [||]
        else
            try
                use doc = JsonDocument.Parse(resp.Body)
                if doc.RootElement.ValueKind = JsonValueKind.Array then
                    doc.RootElement.EnumerateArray()
                    |> Seq.map (fun row ->
                        let d = Dictionary<string, obj>()
                        for prop in row.EnumerateObject() do
                            d.[prop.Name] <- Json.toObject(prop.Value)
                        d :> IDictionary<string, obj>)
                    |> Seq.toArray
                else [||]
            with :? JsonException -> [||]

    // ── Schema ─────────────────────────────────────────────────────────────

    /// <summary>Get the full schema catalog (table name -> descriptor).</summary>
    member this.Schema() : IDictionary<string, obj> =
        let resp = this.Get("/kit/schema")
        match Response.json(resp) with
        | Some el ->
            match el.TryGetProperty("tables") with
            | true, p when p.ValueKind = JsonValueKind.Object ->
                let d = Dictionary<string, obj>()
                for prop in p.EnumerateObject() do
                    d.[prop.Name] <- Json.toObject(prop.Value)
                upcast d
            | _ -> upcast (Dictionary<string, obj>())
        | None -> upcast (Dictionary<string, obj>())

    /// <summary>Get the descriptor for a single table.</summary>
    member this.SchemaFor(table: string) : IDictionary<string, obj> =
        let resp = this.Get("/kit/schema/" + urlPathEscape(table))
        match Response.json(resp) with
        | Some el ->
            match el.ValueKind with
            | JsonValueKind.Object ->
                let d = Dictionary<string, obj>()
                for prop in el.EnumerateObject() do
                    d.[prop.Name] <- Json.toObject(prop.Value)
                upcast d
            | _ -> upcast (Dictionary<string, obj>())
        | None -> upcast (Dictionary<string, obj>())

    // ── Maintenance ────────────────────────────────────────────────────────

    /// <summary>POST with no body and decode the JSON object response.</summary>
    member private this.PostAndDecode(path: string) : IDictionary<string, obj> =
        let resp = this.Post(path)
        match Response.json(resp) with
        | Some el when el.ValueKind = JsonValueKind.Object ->
            let d = Dictionary<string, obj>()
            for prop in el.EnumerateObject() do
                d.[prop.Name] <- Json.toObject(prop.Value)
            upcast d
        | _ -> upcast (Dictionary<string, obj>())

    /// <summary>Compact (merge sorted runs) across all tables.</summary>
    member this.Compact() : IDictionary<string, obj> =
        this.PostAndDecode("/compact")

    /// <summary>Compact a single table.</summary>
    member this.CompactTable(name: string) : IDictionary<string, obj> =
        this.PostAndDecode("/tables/" + urlPathEscape(name) + "/compact")

    // ── Transactions ───────────────────────────────────────────────────────

    /// <summary>
    /// Begin a batch transaction. Operations are staged locally and committed
    /// atomically in a single <c>/kit/txn</c> request.
    /// </summary>
    member this.BeginTransaction() : Transaction = Transaction.Create(this)

    /// <summary>
    /// Commit a batch of staged operations atomically. Exposed for the
    /// <c>Transaction</c> type; prefer <c>Transaction.Commit</c>.
    /// </summary>
    member this.CommitTxn(ops: IDictionary<string, obj> list, idempotencyKey: string) : IDictionary<string, obj>[] =
        if List.isEmpty ops then [||]
        else
            let payload = Dictionary<string, obj>()
            payload.["ops"] <- box (Seq.toArray ops)
            if not (isNull idempotencyKey) && idempotencyKey <> "" then
                payload.["idempotency_key"] <- box idempotencyKey
            let resp = this.Post("/kit/txn", payload)
            Helpers.decodeResults(resp.Body)

    // ── Low-level HTTP (for endpoints not yet wrapped) ─────────────────────

    /// <summary>Perform a GET request, mapping HTTP errors to typed exceptions.</summary>
    member this.Get(path: string) : Response.MongrelDBResponse =
        request HttpMethod.Get path None

    /// <summary>Perform a POST request with a JSON body (Content-Type: application/json).</summary>
    member this.Post(path: string, body: IDictionary<string, obj>) : Response.MongrelDBResponse =
        request HttpMethod.Post path (Some body)

    /// <summary>Perform a POST request with no body.</summary>
    member this.Post(path: string) : Response.MongrelDBResponse =
        request HttpMethod.Post path None

    /// <summary>Perform a DELETE request, mapping HTTP errors to typed exceptions.</summary>
    member this.HttpDelete(path: string) : Response.MongrelDBResponse =
        request HttpMethod.Delete path None

    /// <summary>
    /// Convert a column-id-to-value map to the server's flat
    /// <c>[col_id, value, col_id, value, ...]</c> array.
    /// </summary>
    static member FlattenCells(cells: IDictionary<int, obj>) : obj[] =
        let flat = ResizeArray<obj>()
        for kv in cells do
            flat.Add(box kv.Key)
            flat.Add(kv.Value)
        flat.ToArray()

    interface IDisposable with
        member _.Dispose() =
            if ownsClient then http.Dispose()

// ── QueryBuilder ───────────────────────────────────────────────────────────
// QueryBuilder and Transaction are declared here (as `and`-recursive partners
// of Client) because they hold a Client field while Client builds instances of
// them: the three types are mutually recursive and F# requires them in a single
// `type ... and ...` group (forward references across separate files are not
// permitted, even in a `namespace rec`).

/// <summary>
/// A fluent query builder for the daemon's <c>/kit/query</c> endpoint, where
/// conditions push down to the engine's specialized indexes for
/// sub-millisecond lookups.
///
/// Condition parameters accept friendly aliases that are translated to the
/// server's exact on-wire keys before sending (see <c>Where</c>).
/// </summary>
and QueryBuilder =
    private
        { Client: Client
          Table: string
          Conditions: IDictionary<string, obj> list
          Projection: int[] option
          Limit: int option
          mutable LastTruncated: bool }

    /// <summary>Initialize a new QueryBuilder. Normally created via <c>Client.Query</c>.</summary>
    static member internal Create(client: Client, table: string) =
        { Client = client
          Table = table
          Conditions = []
          Projection = None
          Limit = None
          LastTruncated = false }

    /// <summary>
    /// Add a native condition (AND-ed). Friendly aliases
    /// (<c>column</c> -> <c>column_id</c>, <c>min</c>/<c>max</c> ->
    /// <c>lo</c>/<c>hi</c>) are accepted; the server's canonical keys are
    /// also accepted as-is.
    /// </summary>
    member this.Where(condType: string, parameters: IDictionary<string, obj>) : QueryBuilder =
        let normalized = QueryBuilder.NormalizeCondition(condType, parameters)
        let entry : IDictionary<string, obj> = dict [condType, box normalized]
        { this with Conditions = this.Conditions @ [entry] }

    /// <summary>Set the column projection (column ids to return). <c>null</c> means all columns.</summary>
    member this.ProjectionOf(columnIds: int[]) : QueryBuilder =
        { this with Projection = Some columnIds }

    /// <summary>Cap the number of rows returned.</summary>
    member this.LimitTo(limit: int) : QueryBuilder =
        { this with Limit = Some limit }

    /// <summary>Build the request payload that will be sent to <c>/kit/query</c>.</summary>
    member this.Build() : IDictionary<string, obj> =
        let payload = Dictionary<string, obj>()
        payload.["table"] <- box this.Table
        match this.Conditions with
        | [] -> ()
        | _ ->
            let arr = ResizeArray<IDictionary<string, obj>>()
            for c in this.Conditions do arr.Add(c)
            payload.["conditions"] <- box (arr.ToArray())
        match this.Projection with
        | Some cols -> payload.["projection"] <- box cols
        | None -> ()
        match this.Limit with
        | Some lim -> payload.["limit"] <- box lim
        | None -> ()
        upcast payload

    /// <summary>
    /// Run the query and return the matching rows. Also records whether the
    /// result was truncated by the limit; check it with <c>Truncated</c>.
    /// </summary>
    member this.Execute() : IDictionary<string, obj>[] =
        let resp = this.Client.Post("/kit/query", this.Build())
        match Response.json resp with
        | None ->
            this.LastTruncated <- false
            [||]
        | Some el ->
            this.LastTruncated <-
                match el.TryGetProperty("truncated") with
                | true, p -> p.GetBoolean()
                | false, _ -> false
            match el.TryGetProperty("rows") with
            | true, p when p.ValueKind = JsonValueKind.Array ->
                p.EnumerateArray()
                |> Seq.map QueryBuilder.DecodeRow
                |> Seq.toArray
            | _ -> [||]

    /// <summary>
    /// Decode one <c>/kit/query</c> row. The daemon returns each row as
    /// <c>{"row_id": "...", "cells": [col_id, value, col_id, value, ...]}</c>
    /// with a flat <c>cells</c> array. This expands that flat array into a
    /// column-id-keyed dictionary (keys are the column id as a string) and
    /// preserves the <c>row_id</c>.
    /// </summary>
    static member private DecodeRow(row: JsonElement) : IDictionary<string, obj> =
        let d = Dictionary<string, obj>()
        // Preserve row_id so callers that need the engine-assigned id can read it.
        match row.TryGetProperty("row_id") with
        | true, rid -> d.["row_id"] <- Json.toObject(rid)
        | false, _ -> ()
        match row.TryGetProperty("cells") with
        | true, cells when cells.ValueKind = JsonValueKind.Array ->
            // Flat array: even indices are column ids, odd indices are values.
            let arr = cells.EnumerateArray() |> Seq.toArray
            let mutable i = 0
            while i + 1 < arr.Length do
                // Column id is a JSON number; use its string form as the key.
                let colKey =
                    match arr.[i].ValueKind with
                    | JsonValueKind.String -> arr.[i].GetString()
                    | _ -> arr.[i].GetRawText()
                d.[colKey] <- Json.toObject(arr.[i + 1])
                i <- i + 2
        | _ -> ()
        upcast d

    /// <summary>
    /// Whether the most recent <c>Execute</c> result was capped by the limit.
    /// Returns <c>false</c> until <c>Execute</c> has been called.
    /// </summary>
    member this.Truncated = this.LastTruncated

    /// <summary>
    /// Translate friendly parameter aliases to the server's canonical on-wire
    /// keys. Both spellings are accepted, so callers may use whichever is clearer.
    ///
    /// Generic aliases (all condition types):
    /// <list>
    /// <item><c>column</c> -> <c>column_id</c></item>
    /// <item><c>min</c>/<c>max</c> -> <c>lo</c>/<c>hi</c></item>
    /// <item><c>min_inclusive</c>/<c>max_inclusive</c> -> <c>lo_inclusive</c>/<c>hi_inclusive</c></item>
    /// </list>
    ///
    /// Type-specific aliases (FTS only):
    /// <list>
    /// <item><c>fm_contains</c>: <c>value</c> -> <c>pattern</c></item>
    /// <item><c>fm_contains_all</c>: <c>value</c> -> <c>patterns</c></item>
    /// </list>
    /// </summary>
    static member NormalizeCondition(condType: string, parameters: IDictionary<string, obj>) : IDictionary<string, obj> =
        let aliases =
            dict [
                "column", "column_id"
                "min", "lo"
                "max", "hi"
                "min_inclusive", "lo_inclusive"
                "max_inclusive", "hi_inclusive"
            ]
        let aliases =
            match condType with
            | "fm_contains" ->
                let d = Dictionary<string, string>(aliases)
                d.["value"] <- "pattern"
                d
            | "fm_contains_all" ->
                let d = Dictionary<string, string>(aliases)
                d.["value"] <- "patterns"
                d
            | _ -> Dictionary<string, string>(aliases)

        let normalized = Dictionary<string, obj>()
        for kv in parameters do
            let canon =
                match aliases.TryGetValue(kv.Key) with
                | true, v -> v
                | false, _ -> kv.Key
            normalized.[canon] <- kv.Value
        upcast normalized

// ── Transaction ────────────────────────────────────────────────────────────

/// <summary>
/// A staged batch transaction. Operations are staged locally and committed
/// atomically in a single <c>/kit/txn</c> request. The engine enforces
/// unique, foreign-key, check, and trigger constraints at commit time; on any
/// violation all operations roll back and <c>Commit</c> raises a
/// <c>ConflictException</c>.
///
/// A Transaction is single-use -- call <c>Commit</c> or <c>Rollback</c> once,
/// then create a new one with <c>Client.BeginTransaction</c>.
/// </summary>
and Transaction =
    private
        { Client: Client
          mutable Ops: IDictionary<string, obj> list
          mutable Committed: bool }

    /// <summary>Initialize a new Transaction. Normally created via <c>Client.BeginTransaction</c>.</summary>
    static member internal Create(client: Client) =
        { Client = client; Ops = []; Committed = false }

    /// <summary>Stage a put (insert) operation. <c>returning</c> asks the daemon to echo the row.</summary>
    member this.Put(table: string, cells: IDictionary<int, obj>, ?returning: bool) : Transaction =
        let ret = defaultArg returning false
        let op =
            dict [
                "put", box (dict [
                    "table", box table
                    "cells", box (Client.FlattenCells(cells))
                    "returning", box ret
                ])
            ] :> IDictionary<string, obj>
        this.Ops <- this.Ops @ [op]
        this

    /// <summary>Stage an upsert (insert-or-update) operation.</summary>
    member this.Upsert(table: string, cells: IDictionary<int, obj>,
                       ?updateCells: IDictionary<int, obj>, ?returning: bool) : Transaction =
        let ret = defaultArg returning false
        let baseOp =
            dict [
                "table", box table
                "cells", box (Client.FlattenCells(cells))
                "returning", box ret
            ]
        match updateCells with
        | Some uc -> (baseOp :?> Dictionary<string, obj>).["update_cells"] <- box (Client.FlattenCells(uc))
        | None -> ()
        let op = dict ["upsert", box baseOp] :> IDictionary<string, obj>
        this.Ops <- this.Ops @ [op]
        this

    /// <summary>Stage a delete by the internal row id.</summary>
    member this.Delete(table: string, rowId: int64) : Transaction =
        let op =
            dict [
                "delete", box (dict [
                    "table", box table
                    "row_id", box rowId
                ])
            ] :> IDictionary<string, obj>
        this.Ops <- this.Ops @ [op]
        this

    /// <summary>Stage a delete by primary-key value.</summary>
    member this.DeleteByPk(table: string, pk: obj) : Transaction =
        let op =
            dict [
                "delete_by_pk", box (dict [
                    "table", box table
                    "pk", box pk
                ])
            ] :> IDictionary<string, obj>
        this.Ops <- this.Ops @ [op]
        this

    /// <summary>The number of staged operations.</summary>
    member this.Count = this.Ops.Length

    /// <summary>
    /// Commit all staged operations atomically.
    ///
    /// <c>idempotencyKey</c> is an optional idempotency key for safe retries --
    /// the daemon returns the original response on duplicate commits, even
    /// after a crash. A constraint violation raises <c>ConflictException</c>
    /// (the engine has already rolled back the entire batch).
    /// </summary>
    member this.Commit(?idempotencyKey: string) : IDictionary<string, obj>[] =
        if this.Committed then
            raise (QueryException("transaction already committed"))
        this.Committed <- true
        if this.Ops.IsEmpty then [||]
        else this.Client.CommitTxn(this.Ops, defaultArg idempotencyKey null)

    /// <summary>Rollback (discard all staged operations). Raises if the transaction was already committed.</summary>
    member this.Rollback() : unit =
        if this.Committed then
            raise (QueryException("cannot rollback a committed transaction"))
        this.Ops <- []

