namespace Visorcraft.MongrelDB

open System

/// <summary>
/// Base class for every error raised by the MongrelDB client. Catch this to
/// catch any MongrelDB failure (network, auth, not-found, conflict, query).
/// </summary>
[<AbstractClass>]
type MongrelDBException =
    inherit Exception
    new(message: string) = { inherit Exception(message) }
    new(message: string, inner: Exception) = { inherit Exception(message, inner) }

/// <summary>Raised for HTTP 401 or 403 responses -- bad or missing credentials.</summary>
type AuthException =
    inherit MongrelDBException
    new(message: string) = { inherit MongrelDBException(message) }

/// <summary>Raised for HTTP 404 responses -- a missing table, schema, or resource.</summary>
type NotFoundException =
    inherit MongrelDBException
    new(message: string) = { inherit MongrelDBException(message) }

/// <summary>
/// Raised for HTTP 409 responses -- a unique, foreign-key, check, or trigger
/// constraint violation. Carries the server's structured error code (e.g.
/// <c>UNIQUE_VIOLATION</c>) and, when the daemon reports one, the index of the
/// offending operation within the transaction.
/// </summary>
type ConflictException
    /// <summary>The human-readable error message from the daemon.</summary>
    (message: string,
     /// <summary>The server's structured error code, when present (e.g. <c>UNIQUE_VIOLATION</c>, <c>FK_VIOLATION</c>). Empty string when the server did not supply one.</summary>
     errorCode: string,
     /// <summary>The index of the offending operation within a transaction commit, when the daemon reports one. <c>Nullable&lt;int&gt;</c> otherwise.</summary>
     opIndex: Nullable<int>) =
    inherit MongrelDBException(message)

    /// <summary>The server's structured error code, when present (e.g. <c>UNIQUE_VIOLATION</c>, <c>FK_VIOLATION</c>). Empty string when the server did not supply one.</summary>
    member _.ErrorCode = errorCode
    /// <summary>The index of the offending operation within a transaction commit, when the daemon reports one. <c>Nullable&lt;int&gt;</c> otherwise.</summary>
    member _.OpIndex = opIndex


/// <summary>
/// Raised for HTTP 400 and 5xx responses, and for any request-level failure
/// not covered by the more specific exceptions (including network/encoding
/// problems).
/// </summary>
type QueryException =
    inherit MongrelDBException
    new(message: string) = { inherit MongrelDBException(message) }
    new(message: string, inner: Exception) = { inherit MongrelDBException(message, inner) }
