# Quickstart

Zero to a running MongrelDB F# program in fifteen minutes. This guide assumes
a fresh machine and walks through installing the prerequisites, starting the
daemon, and writing, running, and understanding a complete program.

---

## 1. Prerequisites

You need two things installed: the .NET 8 SDK and a `mongreldb-server` daemon.

### Install the .NET 8 SDK

MongrelDB F# is built on `System.Net.Http` and `System.Text.Json`, both of which
ship in the .NET 8 base class library, so there are no third-party dependencies.

Verify it:

```sh
dotnet --version
# 8.0.x or newer
```

If you do not have it, install from <https://dotnet.microsoft.com/download> or
your package manager (e.g. `pacman -S dotnet-sdk`, `brew install --cask dotnet-sdk`).

### Install mongreldb-server

Fetch a prebuilt server binary from the
[MongrelDB releases](https://github.com/visorcraft/MongrelDB/releases):

```sh
mkdir -p bin
curl -fsSL -o bin/mongreldb-server \
  https://github.com/visorcraft/MongrelDB/releases/download/v0.49.0/mongreldb-server-linux-x64
chmod +x bin/mongreldb-server
```

Verify it runs:

```sh
./bin/mongreldb-server --version
```

## 2. Start the daemon

By default `mongreldb-server` listens on `http://127.0.0.1:8453` and stores
data in the current working directory.

```sh
mkdir -p /tmp/mdb-data && cd /tmp/mdb-data
/path/to/mongreldb-server
```

In another terminal, sanity-check it:

```sh
curl http://127.0.0.1:8453/health
# ok
```

Leave the daemon running for the rest of this guide.

## 3. Create a project and pull in the client

Create a console project and add a project reference to the library (or a
`PackageReference` to the published NuGet package `Visorcraft.MongrelDB`):

```sh
dotnet new console -lang F# -o Demo
cd Demo
dotnet add package Visorcraft.MongrelDB
```

## 4. Write your first program

Replace `Program.fs` with:

```fsharp
open System.Collections.Generic
open Visorcraft.MongrelDB

// 1. Connect to the daemon. Empty/omitted url falls back to http://127.0.0.1:8453.
let db = new Client(url = "http://127.0.0.1:8453")

// 2. Health check before doing anything else.
if not (db.Health()) then
    eprintfn "daemon not reachable"
    exit 1

// 3. Create a table. Each column has a stable numeric id, a name, a type, and
//    flags. The first column is the primary key.
let col id name ty =
    let d = Dictionary<string, obj>()
    d.["id"] <- box id
    d.["name"] <- box name
    d.["ty"] <- box ty
    d.["primary_key"] <- box (id = 1)
    d.["nullable"] <- box false
    upcast d

let tid = db.CreateTable("orders", [| col 1 "id" "int64"; col 2 "customer" "varchar"; col 3 "amount" "float64" |])
printfn "created table id: %d" tid

// 4. Insert rows. Cells maps column id -> value.
let cells pairs =
    let d = Dictionary<int, obj>()
    for (k, v) in pairs do d.[k] <- v
    upcast d

db.Put("orders", cells [1, box 1; 2, box "Alice"; 3, box 99.5]) |> ignore
db.Put("orders", cells [1, box 2; 2, box "Bob";   3, box 150.0]) |> ignore

// 5. Query with a native index condition. The range index serves this in
//    sub-millisecond. Projection selects only column ids 1 and 2.
let cond = Dictionary<string, obj>(); cond.["column"] <- box 3; cond.["min"] <- box 100.0
let rows = db.Query("orders")
              .Where("range", cond)
              .ProjectionOf([| 1; 2 |])
              .LimitTo(100)
              .Execute()
for row in rows do printfn "row: %A" row

// 6. Count the rows.
printfn "total rows: %d" (db.Count("orders"))

(db :> IDisposable).Dispose()
```

Run it:

```sh
dotnet run
```

You should see:

```
created table id: 1
row: ...
total rows: 2
```

## 5. What each part does

| Code | What it does |
|------|--------------|
| `new Client(url = ...)` | Builds an `HttpClient` targeting one daemon. Safe to share across async workflows. |
| `db.Health()` | GET `/health`; returns `true` when the daemon answers. Always check before real work. |
| `db.CreateTable(name, cols)` / `db.CreateTable(name, cols, constraints)` | POST `/kit/create_table`. Column `id`s are the on-wire identifiers; optional `enum_variants`/`default_value` keys and the native `constraints` object are forwarded unchanged. |
| `db.Put(table, cells)` | Single-op transaction: POST `/kit/txn` with one `put` op. `cells` is flattened to `[col_id, val, ...]`. |
| `db.Query(table).Where(...)` | Builds a `/kit/query` body. `Where` pushes a condition down to a native index. |
| `.ProjectionOf([|1;2|])` | Server returns only those column ids, saving bandwidth. |
| `.LimitTo(100)` | Caps the result; check `q.Truncated` afterward to detect overflow. |
| `.Execute()` | Sends the query and decodes the `rows` array. |
| `db.Count(table)` | GET `/tables/{name}/count`. |

## 6. Typed columns: enums and defaults

The column dictionaries passed to `CreateTable` are forwarded to the daemon
verbatim, so any column-level constraint the engine supports is just another
key. Three useful ones:

- `enum_variants` (`string[]`) - restricts an `enum` column to a fixed set of
  string values. The engine rejects writes that fall outside the set.
- `default_value` (`string`, `int`, `bool`, explicit `null`, and the literal
  string `"now"`) - the value written into the column when a row omits it. The
  engine-side default is applied before any client-side default. `"now"` passed
  as `default_value` is treated as a literal string, not as the current
  timestamp.
- `default_expr` (`"now"` or `"uuid"`) - a dynamic expression evaluated by the
  engine on each insert. This is **not** an alias for `default_value`; use it
  when you need a per-row computed default such as the current timestamp or a
  fresh UUID.

```fsharp
let draftCol =
    let d = Dictionary<string, obj>()
    d.["id"] <- box 1
    d.["name"] <- box "status"
    d.["ty"] <- box "varchar"
    d.["default_value"] <- box "draft"
    d.["nullable"] <- box false
    upcast d

let createdCol =
    let d = Dictionary<string, obj>()
    d.["id"] <- box 2
    d.["name"] <- box "created_at"
    d.["ty"] <- box "varchar"
    d.["default_expr"] <- box "now"
    d.["nullable"] <- box false
    upcast d

let countCol =
    let d = Dictionary<string, obj>()
    d.["id"] <- box 3
    d.["name"] <- box "count"
    d.["ty"] <- box "int64"
    d.["default_value"] <- box 0
    d.["nullable"] <- box false
    upcast d

db.CreateTable("tasks", [| draftCol; createdCol; countCol |]) |> ignore
```

## 7. History retention

MongrelDB keeps a configurable number of MVCC epochs. You can widen or narrow
the window, inspect the current floor, and run SQL `AS OF EPOCH` queries to
read past states.

```fsharp
// Keep the last 1024 epochs of history.
db.SetHistoryRetentionEpochs(1024uL) |> ignore

printfn "retained epochs: %d" (db.HistoryRetentionEpochs())
printfn "earliest epoch : %d" (db.EarliestRetainedEpoch())

// Read a past state. The epoch must be >= EarliestRetainedEpoch().
let past = db.Sql("SELECT label FROM tasks AS OF EPOCH 5")
for row in past do printfn "past row: %A" row
```

## 8. Common pitfalls

**Using the column name instead of the column id.** Every on-wire API uses the
numeric `id` from `CreateTable`, never the `name`. The query builder's `column`
alias maps to the server's `column_id` - pass the integer id, not the string
name.

**Treating a single `Put` as non-transactional.** `Put` is a one-op
transaction. A unique constraint violation surfaces as a `ConflictException`
(HTTP 409), not as a silent no-op.

**Calling `Commit` twice on the same `Transaction`.** The second call raises a
`QueryException`. Create a fresh `db.BeginTransaction()` for each logical unit
of work.

**Reusing a `QueryBuilder` and expecting a fresh `Truncated`.** `Truncated`
reflects the most recent `Execute`. Build a new query, or re-run `Execute`
before reading it.

**Pointing at a daemon that requires auth.** If the daemon was started with
`--auth-token` or `--auth-users`, every call raises `AuthException` unless you
pass `token` or `username`/`password`. See [auth.md](auth.md).

## Next steps

- [transactions.md](transactions.md) - atomic batches, idempotency, retries
- [queries.md](queries.md) - every native index condition
- [sql.md](sql.md) - recursive CTEs, window functions, `CREATE TABLE AS SELECT`
- [auth.md](auth.md) - bearer tokens, basic auth, user/role management
- [errors.md](errors.md) - the full error hierarchy and recovery patterns
