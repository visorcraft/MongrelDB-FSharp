namespace Visorcraft.MongrelDB

open System.Collections.Generic
open System.Text.Json

/// <summary>Internal JSON helpers shared across the client.</summary>
module internal Json =
    /// <summary>Serialization options: no property-name camel-casing (the server uses snake_case keys verbatim).</summary>
    let serOpts =
        let o = JsonSerializerOptions()
        o.PropertyNamingPolicy <- null
        o

    /// <summary>Convert a <c>JsonElement</c> to a plain object (recursive).</summary>
    let toObject (el: JsonElement) : obj =
        match el.ValueKind with
        | JsonValueKind.String -> box (el.GetString())
        | JsonValueKind.Number ->
            let mutable i = 0L
            if el.TryGetInt64(&i) then box i else box (el.GetDouble())
        | JsonValueKind.True -> box true
        | JsonValueKind.False -> box false
        | JsonValueKind.Null -> null
        | JsonValueKind.Array ->
            el.EnumerateArray() |> Seq.map toObject |> Seq.toArray |> box
        | JsonValueKind.Object ->
            let d = Dictionary<string, obj>()
            for prop in el.EnumerateObject() do
                d.[prop.Name] <- toObject(prop.Value)
            upcast d
        | _ -> box (el.GetRawText())
