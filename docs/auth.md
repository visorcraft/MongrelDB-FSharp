# Authentication & Authorization

A `mongreldb-server` daemon runs in one of three modes:

1. **Open** (default) - no auth required.
2. **Bearer token** (`--auth-token <TOKEN>`) - every request must carry an
   `Authorization: Bearer <TOKEN>` header.
3. **HTTP Basic** (`--auth-users`) - every request must carry an
   `Authorization: Basic <base64(user:pass)>` header.

The F# client supports all three through `Client` constructor arguments. This
guide shows each mode, how to inspect what was sent, and how to manage users
and roles via SQL when the server is in Basic mode.

---

## Bearer token mode

Start the daemon with a token:

```sh
mongreldb-server --auth-token s3cret-token
```

Connect with the `token` argument. The token is sent as
`Authorization: Bearer ...` on every request.

```fsharp
let db = new Client(url = "http://127.0.0.1:8453", token = "s3cret-token")

try
    let ok = db.Health()
    printfn "healthy: %b" ok
with :? AuthException ->
    eprintfn "bad or missing token"; exit 1
```

A missing or wrong token surfaces as `AuthException` (HTTP 401/403).

### Where the token comes from

Hard-coding secrets in source is bad practice. Read it from the environment:

```fsharp
let token = Environment.GetEnvironmentVariable("MONGRELDB_TOKEN")
if String.IsNullOrEmpty(token) then failwith "MONGRELDB_TOKEN not set"

let db = new Client(token = token)
```

## Basic auth mode

Start the daemon with a users file or inline users:

```sh
mongreldb-server --auth-users
```

Connect with `username` / `password`:

```fsharp
let db = new Client(
    url = "http://127.0.0.1:8453",
    username = "admin",
    password = "s3cret")
```

The client base64-encodes `username:password` and sets
`Authorization: Basic ...` on every request.

## Token takes precedence

If you supply both, `token` wins and Basic credentials are ignored. This lets
you layer an override without branching:

```fsharp
let db = new Client(
    url = url,
    username = "fallback",
    password = "user",
    token = "overrides-everything")
```

## Timeouts

The client takes a `timeout` argument (a `TimeSpan`), passed straight through
to the underlying `HttpClient`.

```fsharp
let db = new Client(
    url = url,
    token = token,
    timeout = TimeSpan.FromSeconds(60.0))
```

## User and role management via SQL

When the daemon is in Basic auth mode, users and roles live in the catalog and
are managed with SQL. Run these statements through `Client.Sql`.

### Create a user

```fsharp
db.Sql("CREATE USER alice WITH PASSWORD 'hunter2'") |> ignore
```

### Alter a user

Change a password:

```fsharp
db.Sql("ALTER USER alice WITH PASSWORD 'new-password'") |> ignore
```

Grant the admin role:

```fsharp
db.Sql("ALTER USER alice ADMIN") |> ignore
```

`ALTER USER ... ADMIN` is how you promote a user to full administrative
privileges (table creation/drop, compaction, user management). Use it
sparingly.

### Drop a user

```fsharp
db.Sql("DROP USER alice") |> ignore
```

### Roles and grants

```fsharp
db.Sql("CREATE ROLE analyst") |> ignore
db.Sql("GRANT SELECT ON orders TO analyst") |> ignore
db.Sql("GRANT analyst TO alice") |> ignore
db.Sql("REVOKE SELECT ON orders FROM analyst") |> ignore
db.Sql("DROP ROLE analyst") |> ignore
```

Exact grant syntax mirrors the server's SQL flavor; consult the server's SQL
reference for the full `GRANT`/`REVOKE` grammar available in your build.

## Common pitfalls

**Auth errors look like other errors without a specific catch.** A 401/403
raises `AuthException`; a 404 raises `NotFoundException`. Always discriminate
by type rather than string-matching `e.Message`.

**Forgetting to set auth in production.** A client built with `new Client()`
and no credentials sends no credentials. Against an auth-enabled daemon, every
call raises `AuthException`. Centralize client construction so the auth option
is never accidentally dropped.

**Sharing one client across async workflows is fine; sharing credentials
across users is not.** A `Client` is safe for concurrent use, but it carries
one identity. If you serve multiple authenticated users, build a client per
user (or per request) with that user's token.

**Token in version control.** Put secrets in the environment, a secret
manager, or a file outside the repo. Never commit a real token.

## Next steps

- [errors.md](errors.md) - `AuthException` and the rest of the error hierarchy
- [quickstart.md](quickstart.md) - the full end-to-end walkthrough
