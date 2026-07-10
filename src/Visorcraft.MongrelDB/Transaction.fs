namespace Visorcraft.MongrelDB

open System.Collections.Generic

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
type Transaction =
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
    member this.Commit([?idempotencyKey: string]) : IDictionary<string, obj>[] =
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
