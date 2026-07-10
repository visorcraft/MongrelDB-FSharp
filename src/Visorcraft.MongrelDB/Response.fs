namespace Visorcraft.MongrelDB

open System
open System.Text.Json

/// <summary>HTTP response decoding helpers shared across the client.
/// Public because the low-level <c>Client.Get/Post/HttpDelete</c> members
/// expose <c>MongrelDBResponse</c> in their signatures.</summary>
module Response =
    /// <summary>
    /// Wraps one HTTP response from the daemon. Exposes the raw status code
    /// and body and a <c>json</c> helper for decoding a JSON body.
    /// </summary>
    type MongrelDBResponse =
        { /// <summary>The HTTP status code.</summary>
          Status: int
          /// <summary>The raw response body (may be empty).</summary>
          Body: string }

        /// <summary>True when the HTTP status is in the 2xx success range.</summary>
        member this.Success =
            this.Status >= 200 && this.Status < 300

    /// <summary>
    /// Parse the response body as JSON and return the decoded value
    /// (<c>JsonElement</c>). Returns <c>None</c> for an empty body. Raises
    /// <c>QueryException</c> if the body is not valid JSON.
    /// </summary>
    let json (resp: MongrelDBResponse) : JsonElement option =
        if isNull resp.Body || String.IsNullOrEmpty(resp.Body) then
            None
        else
            try
                use doc = JsonDocument.Parse(resp.Body)
                Some (doc.RootElement.Clone())
            with
            | :? JsonException as ex ->
                raise (QueryException("Failed to decode JSON response: " + ex.Message, ex :> Exception))
