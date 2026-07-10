namespace Visorcraft.MongrelDB

open System.Collections.Generic
open System.Text.Json

/// <summary>
/// A fluent query builder for the daemon's <c>/kit/query</c> endpoint, where
/// conditions push down to the engine's specialized indexes for
/// sub-millisecond lookups.
///
/// Condition parameters accept friendly aliases that are translated to the
/// server's exact on-wire keys before sending (see <c>Where</c>).
/// </summary>
type QueryBuilder =
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
                |> Seq.map (fun row ->
                    let d = Dictionary<string, obj>()
                    for prop in row.EnumerateObject() do
                        d.[prop.Name] <- Json.toObject(prop.Value)
                    upcast d)
                |> Seq.toArray
            | _ -> [||]

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
