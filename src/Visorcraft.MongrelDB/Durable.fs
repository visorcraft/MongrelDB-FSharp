namespace Visorcraft.MongrelDB

open System.Collections.Generic
open System.Text.Json

/// <summary>Structural HLC from durable recovery (0.64+).</summary>
type CommitHlc =
    { PhysicalMicros: int64
      Logical: int
      NodeTiebreaker: int }

/// <summary>Nested durable recovery payload.</summary>
type DurableOutcome =
    { Committed: bool option
      LastCommitEpoch: int64 option
      LastCommitHlc: CommitHlc option
      Serialization: string
      SerializationState: string option
      TerminalState: string option }

/// <summary>GET /queries/{query_id} decoded status.</summary>
type QueryStatus =
    { QueryId: string
      Status: string
      State: string
      ServerState: string
      TerminalState: string option
      Committed: bool option
      LastCommitEpoch: int64 option
      LastCommitHlc: CommitHlc option
      Outcome: DurableOutcome
      Durable: DurableOutcome option
      Raw: IDictionary<string, obj> }

module Durable =
    let parseCommitHlc (raw: obj) : CommitHlc option =
        match raw with
        | :? IDictionary<string, obj> as m when m.ContainsKey("physical_micros") && not (isNull m.["physical_micros"]) ->
            let phys =
                match m.["physical_micros"] with
                | :? int64 as x -> x
                | :? int as x -> int64 x
                | :? double as x -> int64 x
                | x -> System.Convert.ToInt64(x)
            let logical =
                if m.ContainsKey("logical") && not (isNull m.["logical"]) then System.Convert.ToInt32(m.["logical"]) else 0
            let node =
                if m.ContainsKey("node_tiebreaker") && not (isNull m.["node_tiebreaker"]) then System.Convert.ToInt32(m.["node_tiebreaker"]) else 0
            Some { PhysicalMicros = phys; Logical = logical; NodeTiebreaker = node }
        | _ -> None

    let parseDurableOutcome (raw: obj) : DurableOutcome =
        let m =
            match raw with
            | :? IDictionary<string, obj> as d -> d
            | _ -> upcast Dictionary<string, obj>()
        let committed =
            if m.ContainsKey("committed") && not (isNull m.["committed"]) then
                Some(System.Convert.ToBoolean(m.["committed"]))
            else None
        let epoch =
            if m.ContainsKey("last_commit_epoch") && not (isNull m.["last_commit_epoch"]) then
                Some(System.Convert.ToInt64(m.["last_commit_epoch"]))
            else None
        let hlc =
            if m.ContainsKey("last_commit_hlc") then parseCommitHlc m.["last_commit_hlc"] else None
        let ser =
            if m.ContainsKey("serialization") && not (isNull m.["serialization"]) then string m.["serialization"] else ""
        let serState =
            if m.ContainsKey("serialization_state") && not (isNull m.["serialization_state"]) then Some(string m.["serialization_state"]) else None
        let term =
            if m.ContainsKey("terminal_state") && not (isNull m.["terminal_state"]) then Some(string m.["terminal_state"]) else None
        { Committed = committed
          LastCommitEpoch = epoch
          LastCommitHlc = hlc
          Serialization = ser
          SerializationState = serState
          TerminalState = term }

    let parseQueryStatus (raw: IDictionary<string, obj>) : QueryStatus =
        let getStr key =
            if raw.ContainsKey(key) && not (isNull raw.[key]) then string raw.[key] else ""
        let committed =
            if raw.ContainsKey("committed") && not (isNull raw.["committed"]) then
                Some(System.Convert.ToBoolean(raw.["committed"]))
            else None
        let epoch =
            if raw.ContainsKey("last_commit_epoch") && not (isNull raw.["last_commit_epoch"]) then
                Some(System.Convert.ToInt64(raw.["last_commit_epoch"]))
            else None
        let durable =
            if raw.ContainsKey("durable") && not (isNull raw.["durable"]) then
                Some(parseDurableOutcome raw.["durable"])
            else None
        let outcome =
            if raw.ContainsKey("outcome") then parseDurableOutcome raw.["outcome"]
            else parseDurableOutcome null
        let topHlc =
            if raw.ContainsKey("last_commit_hlc") then parseCommitHlc raw.["last_commit_hlc"] else None
        { QueryId = getStr "query_id"
          Status = getStr "status"
          State = getStr "state"
          ServerState = if raw.ContainsKey("server_state") then getStr "server_state" else getStr "state"
          TerminalState = if raw.ContainsKey("terminal_state") && not (isNull raw.["terminal_state"]) then Some(getStr "terminal_state") else None
          Committed = committed
          LastCommitEpoch = epoch
          LastCommitHlc = topHlc
          Outcome = outcome
          Durable = durable
          Raw = raw }

    let commitHlc (s: QueryStatus) : CommitHlc option =
        match s.Durable with
        | Some d when d.LastCommitHlc.IsSome -> d.LastCommitHlc
        | _ ->
            match s.Outcome.LastCommitHlc with
            | Some _ as h -> h
            | None -> s.LastCommitHlc

    let serializationState (s: QueryStatus) : string =
        match s.Durable with
        | Some d when d.SerializationState.IsSome && d.SerializationState.Value <> "" -> d.SerializationState.Value
        | Some d when d.Serialization <> "" -> d.Serialization
        | _ ->
            match s.Outcome.SerializationState with
            | Some x when x <> "" -> x
            | _ -> s.Outcome.Serialization

    let parseQueryStatusJson (json: string) : QueryStatus =
        use doc = JsonDocument.Parse(json)
        let d = Dictionary<string, obj>()
        for prop in doc.RootElement.EnumerateObject() do
            d.[prop.Name] <- Json.toObject(prop.Value)
        parseQueryStatus d
