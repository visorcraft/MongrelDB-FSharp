<p align="center">
  <img src="assets/mongrel.png" alt="MongrelDB logo" width="250" />
</p>

<h1 align="center">MongrelDB F# Client</h1>

<p align="center">
  <b>Pure F# client for MongrelDB - embedded+server database with SQL, vector search, full-text search, AI-native retrieval, and configurable MVCC history retention.</b>
  <br />
  No external dependencies at runtime - built on .NET <code>HttpClient</code> and <code>System.Text.Json</code>. The API mirrors the MongrelDB PHP, Go, Ruby, and Java clients.
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/Visorcraft.MongrelDB"><img src="https://img.shields.io/nuget/v/Visorcraft.MongrelDB.svg" alt="NuGet" /></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-8.0-512BD4.svg" alt=".NET" /></a>
  <a href="https://github.com/visorcraft/MongrelDB-FSharp/actions/workflows/ci.yml"><img src="https://github.com/visorcraft/MongrelDB-FSharp/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
  <a href="#license"><img src="https://img.shields.io/badge/license-MIT%20OR%20Apache--2.0-blue.svg" alt="License" /></a>
</p>

## Package

| Surface | Package | Install |
|---|---|---|
| F# client | `Visorcraft.MongrelDB` | `dotnet add package Visorcraft.MongrelDB` |

## Requirements

- **.NET 8 SDK** or newer
- A running [`mongreldb-server`](https://github.com/visorcraft/MongrelDB) daemon

## What It Provides

- **Typed CRUD** over the Kit transaction endpoint: `Put`, `Upsert` (insert-or-update on PK conflict), `Delete` by row id or primary key, all with optional idempotency keys for safe retries.
- **Fluent query builder** that pushes conditions down to the engine's specialized indexes for sub-millisecond lookups: bitmap equality/IN, learned-range, null checks, FM-index full-text search, HNSW vector similarity (`ann`), and sparse vector match. Friendly aliases (`column` -> `column_id`, `min`/`max` -> `lo`/`hi`) are translated to the server's on-wire keys.
- **Idempotent batch transactions** - operations staged locally and committed atomically, with the engine enforcing unique, foreign-key, and check constraints at commit time. Idempotency keys return the original response on duplicate commits, even after a crash.
- **Full SQL access** through the DataFusion-backed `/sql` endpoint: recursive CTEs, window functions, `CREATE TABLE AS SELECT`, materialized views, and multi-statement execution.
- **Schema management**: typed table creation with enum/default fields and native constraints, full schema catalog, and per-table descriptors.

Column dictionaries preserve `enum_variants`, scalar `default_value` (strings,
numbers, booleans, explicit `null`, and the literal string `"now"`), dynamic
`default_expr` (`"now"` or `"uuid"`), and table `constraints.checks`.
`default_expr` is not an alias for `default_value`; it is evaluated by the
engine on each insert.
- **History retention and time-travel queries**: configure the number of MVCC
epochs kept with `SetHistoryRetentionEpochs`, inspect the floor with
`EarliestRetainedEpoch`, and read past states via SQL `AS OF EPOCH`.
- **User/role/credentials management** via SQL: Argon2id-hashed catalog users, roles, and `GRANT`/`REVOKE` table-level permissions, all executed through `Sql`.
- **Maintenance**: compaction (all tables or per-table).
- **Auth**: Bearer token (`--auth-token` mode) and HTTP Basic (`--auth-users` mode), with the bearer token taking precedence.
- **Typed exception hierarchy**: `MongrelDBException` (base), `AuthException` (401/403), `NotFoundException` (404), `ConflictException` (409, with error code + op index), and `QueryException` (everything else, including network failures).
- **Robust JSON handling**: NaN and Infinity raise a clear `QueryException` instead of corrupting data; the `/sql` endpoint's Arrow IPC bodies are tolerated gracefully.

## Install

```sh
dotnet add package Visorcraft.MongrelDB
```

Or reference the project directly:

```xml
<ProjectReference Include="path/to/mongreldb_fsharp/src/Visorcraft.MongrelDB/Visorcraft.MongrelDB.fsproj" />
```

## Examples

Task-focused, commented guides live in [`docs/`](docs):

- [Quickstart](docs/quickstart.md) - install, start the daemon, write and run a complete program.
- [Transactions](docs/transactions.md) - batch commits, idempotency keys, constraint handling.
- [Queries](docs/queries.md) - every native condition type and the index it pushes down to.
- [SQL](docs/sql.md) - recursive CTEs, window functions, advanced SQL.
- [Authentication](docs/auth.md) - Bearer token, HTTP Basic, and open modes.
- [Errors](docs/errors.md) - the exception hierarchy and recovery patterns.

## Quick Example

```fsharp
open System.Collections.Generic
open Visorcraft.MongrelDB

// Connect to a running mongreldb-server daemon.
let db = new Client(url = "http://127.0.0.1:8453")

let col id name ty =
    let d = Dictionary<string, obj>()
    d.["id"] <- box id; d.["name"] <- box name; d.["ty"] <- box ty
    d.["primary_key"] <- box (id = 1); d.["nullable"] <- box false
    upcast d

// Create a table. Column ids are stable on-wire identifiers.
db.CreateTable("orders", [| col 1 "id" "int64"; col 2 "customer" "varchar"; col 3 "amount" "float64" |]) |> ignore

let cells pairs =
    let d = Dictionary<int, obj>()
    for (k, v) in pairs do d.[k] <- v
    upcast d

// Insert rows (cells map column id -> value).
db.Put("orders", cells [1, box 1; 2, box "Alice"; 3, box 99.50]) |> ignore
db.Put("orders", cells [1, box 2; 2, box "Bob";   3, box 150.00]) |> ignore

// Upsert (insert or update on PK conflict).
db.Upsert("orders",
          cells [1, box 1; 2, box "Alice"; 3, box 120.00],
          cells [3, box 120.00]) |> ignore

// Query with a native index condition (learned-range index).
let cond = Dictionary<string, obj>(); cond.["column"] <- box 3; cond.["min"] <- box 100.0
let rows = db.Query("orders")
              .Where("range_f64", cond)
              .ProjectionOf([| 1; 2 |])
              .LimitTo(100)
              .Execute()

printfn "%d" (db.Count("orders"))   // 2

// Run SQL.
db.Sql("UPDATE orders SET amount = 200.0 WHERE customer = 'Bob'") |> ignore
```

## Authentication

```fsharp
// Bearer token (--auth-token mode)
let a = new Client(url = "http://127.0.0.1:8453", token = "my-secret-token")

// HTTP Basic (--auth-users mode)
let b = new Client(url = "http://127.0.0.1:8453", username = "admin", password = "s3cret")

// Arguments are optional; the daemon address defaults to 127.0.0.1:8453.
let c = new Client()
```

## Batch transactions

Operations are staged locally and committed atomically. The engine enforces
unique, foreign-key, and check constraints at commit time.

```fsharp
let txn = db.BeginTransaction()
txn.Put("orders", cells [1, box 10; 2, box "Dave"; 3, box 50.00]) |> ignore
txn.Put("orders", cells [1, box 11; 2, box "Eve";  3, box 75.00]) |> ignore
txn.DeleteByPk("orders", box 2) |> ignore

try
    let results = txn.Commit()                 // atomic - all or nothing
    printfn "Staged %d operations" txn.Count
with :? ConflictException as e ->
    printfn "Constraint violated: %s - %s" e.ErrorCode e.Message

// Idempotent commit - safe to retry; the daemon returns the original response.
let txn2 = db.BeginTransaction()
txn2.Put("orders", cells [1, box 20; 2, box "Frank"; 3, box 100.00]) |> ignore
txn2.Commit(idempotencyKey = "order-20-create") |> ignore
```

## Error handling

Every non-2xx response is mapped to a typed exception. Catch the specific type
for the category, or `MongrelDBException` for any client failure.

```fsharp
try
    let c = Dictionary<int, obj>(); c.[1] <- box 1
    db.Put("orders", c) |> ignore   // duplicate PK (with a UNIQUE constraint)
with
| :? ConflictException as e ->
    printfn "Constraint: %s" e.ErrorCode        // UNIQUE_VIOLATION
    printfn "Op index: %A" e.OpIndex             // offending op in the transaction
| :? AuthException as e ->
    printfn "Not authorized: %s" e.Message
| :? NotFoundException as e ->
    printfn "Not found: %s" e.Message
| :? QueryException as e ->
    printfn "Query/server error: %s" e.Message
| :? MongrelDBException as e ->
    printfn "Error: %s" e.Message
```

## API reference

### `Client`

| Member | Description |
|--------|-------------|
| `new(?url, ?token, ?username, ?password, ?timeout, ?httpClient)` | Construct a client (`url` defaults to `http://127.0.0.1:8453`) |
| `Health()` -> `bool` | Check daemon health |
| `TableNames()` -> `string[]` | List table names |
| `CreateTable(name, columns)` -> `int64` | Create a table; returns the table id |
| `CreateTable(name, columns, constraints)` -> `int64` | Create a table with a constraints block |
| `DropTable(name)` -> `unit` | Drop a table |
| `Count(table)` -> `int64` | Row count |
| `Put(table, cells, ?idempotencyKey)` -> `IDictionary` | Insert a row |
| `Upsert(table, cells, ?updateCells, ?idempotencyKey)` -> `IDictionary` | Upsert a row |
| `Delete(table, rowId)` -> `unit` | Delete by row id |
| `DeleteByPk(table, pk)` -> `unit` | Delete by primary key |
| `Query(table)` -> `QueryBuilder` | Start a native query |
| `Sql(sql)` -> `IDictionary[]` | Execute SQL (requests JSON output) |
| `Schema()` -> `IDictionary` | Full schema catalog |
| `SchemaFor(table)` -> `IDictionary` | Single-table descriptor |
| `Compact()` -> `IDictionary` | Compact all tables |
| `CompactTable(name)` -> `IDictionary` | Compact one table |
| `BeginTransaction()` -> `Transaction` | Start a batch |
| `HistoryRetention()` -> `uint64 * uint64` | Current retention window (`history_retention_epochs`, `earliest_retained_epoch`) |
| `HistoryRetentionEpochs()` -> `uint64` | Current `history_retention_epochs` value |
| `EarliestRetainedEpoch()` -> `uint64` | Lowest queryable epoch for `AS OF EPOCH` |
| `SetHistoryRetentionEpochs(epochs)` -> `uint64 * uint64` | Set the retention window and return the new state |
| `Get(path)`, `Post(path, body)`, `HttpDelete(path)` -> `Response` | Low-level HTTP |

### `QueryBuilder`

| Member | Description |
|--------|-------------|
| `Where(condType, parameters)` -> `QueryBuilder` | Add a native condition (AND-ed) |
| `ProjectionOf(columnIds)` -> `QueryBuilder` | Set column projection |
| `LimitTo(limit)` -> `QueryBuilder` | Set row limit |
| `Build()` -> `IDictionary` | Build the request payload |
| `Execute()` -> `IDictionary[]` | Run the query |
| `Truncated` -> `bool` | Whether the last `Execute` result hit the limit |

### `Transaction`

| Member | Description |
|--------|-------------|
| `Put(table, cells, ?returning)` -> `Transaction` | Stage an insert |
| `Upsert(table, cells, ?updateCells, ?returning)` -> `Transaction` | Stage an upsert |
| `Delete(table, rowId)` -> `Transaction` | Stage a delete by row id |
| `DeleteByPk(table, pk)` -> `Transaction` | Stage a delete by primary key |
| `Count` -> `int` | Number of staged operations |
| `Commit(?idempotencyKey)` -> `IDictionary[]` | Commit atomically |
| `Rollback()` -> `unit` | Discard all operations |

### Exceptions

| Class | HTTP status | Notes |
|-------|-------------|-------|
| `MongrelDBException` | - | Base class for all client errors |
| `AuthException` | 401, 403 | Bad or missing credentials |
| `NotFoundException` | 404 | Missing table, schema, or resource |
| `ConflictException` | 409 | Constraint violation; carries `ErrorCode` and `OpIndex` |
| `QueryException` | 400, 5xx, network | Everything else |

## Building and testing

The test suite uses xUnit. It is split into two layers:

- **Offline unit tests** - exception hierarchy, query-builder alias translation,
  cells flattening, and JSON decoding. No daemon needed.
- **Live integration tests** - boots a real `mongreldb-server` daemon and
  exercises the full client surface. Skips automatically when no binary is
  available.

```sh
dotnet build
dotnet test                  # runs the whole suite (live tests skip without a daemon)
```

Fetch a prebuilt server binary from the [MongrelDB releases](https://github.com/visorcraft/MongrelDB/releases)
and place it at `./bin/mongreldb-server`, set `MONGRELDB_SERVER`, or install it
on `PATH`:

```sh
mkdir -p bin
curl -fsSL -o bin/mongreldb-server \
  https://github.com/visorcraft/MongrelDB/releases/download/v0.50.0/mongreldb-server-linux-x64
chmod +x bin/mongreldb-server
```

The live harness resolves the binary in this order: the `MONGRELDB_SERVER` env
var, `./bin/mongreldb-server`, `mongreldb-server` on `PATH`. Or point it at an
already-running daemon with `MONGRELDB_URL`.

## Contributing

Contributions are welcome. Please:

1. Open an issue first for non-trivial changes.
2. Add focused tests near your change - the suite must stay green.
3. Run `dotnet test` before submitting.
4. Keep the client dependency-free (BCL only at runtime).

## License

Dual-licensed under the **MIT License** or the **Apache License, Version 2.0**,
at your option. See [MIT](LICENSE-MIT) OR [Apache-2.0](LICENSE-APACHE) for the full text.

`SPDX-License-Identifier: MIT OR Apache-2.0`
