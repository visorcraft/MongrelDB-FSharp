namespace Visorcraft.MongrelDB.Tests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Net.Http
open System.Threading
open Visorcraft.MongrelDB

/// <summary>
/// Shared daemon lifecycle for the live test suite. Boots a real
/// mongreldb-server (or reuses one at MONGRELDB_URL) and exposes the connected
/// <c>Client</c> via <c>Daemon.getClient()</c>.
///
/// The binary is resolved in this order:
///   1. the MONGRELDB_SERVER env var (path to the server binary).
///   2. a prebuilt binary at ./bin/mongreldb-server (downloaded by CI).
///   3. mongreldb-server on PATH.
/// </summary>
module Daemon =

    type private State =
        { mutable Current: Client option
          mutable Process: Process option
          mutable DataDir: string option
          mutable LogPath: string option }

    let private state = { Current = None; Process = None; DataDir = None; LogPath = None }
    let private bootLock = obj ()

    /// <summary>True when the daemon was booted (or reused) successfully.</summary>
    let isAvailable () : bool = Option.isSome state.Current

    /// <summary>The connected client, throwing if unavailable.</summary>
    let getClient () : Client =
        match state.Current with
        | Some c -> c
        | None -> failwith "no mongreldb-server available"

    /// <summary>Resolve the server binary path, mirroring the Ruby/Crystal/Erlang harness.</summary>
    let resolveServerBinary () : string =
        let envBin = Environment.GetEnvironmentVariable("MONGRELDB_SERVER")
        if not (String.IsNullOrEmpty(envBin)) && File.Exists(envBin) then envBin
        else
            let local = Path.Combine(Directory.GetCurrentDirectory(), "bin", "mongreldb-server")
            if File.Exists(local) then local
            else
                let onPath =
                    try
                        use p = Process.Start(ProcessStartInfo("mongreldb-server", "--version",
                                                                UseShellExecute = false,
                                                                RedirectStandardOutput = true,
                                                                RedirectStandardError = true))
                        p.WaitForExit(5000) |> ignore
                        true
                    with _ -> false
                if onPath then "mongreldb-server" else null

    let private freePort () : int =
        use l = new TcpListener(IPAddress.Loopback, 0)
        l.Start()
        let port = (l.LocalEndpoint :?> IPEndPoint).Port
        l.Stop()
        port

    let private reachable (url: string) : bool =
        try
            use c = new HttpClient()
            c.Timeout <- TimeSpan.FromSeconds(2.0)
            use resp = c.GetAsync(url + "/health").Result
            resp.IsSuccessStatusCode
        with _ -> false

    let private waitForHealth (url: string) (maxSeconds: int) : bool =
        let deadline = DateTime.UtcNow.AddSeconds(float maxSeconds)
        let mutable ok = false
        while not ok && DateTime.UtcNow < deadline do
            ok <- reachable url
            if not ok then Thread.Sleep(500)
        ok

    let private dumpLog () : unit =
        match state.LogPath with
        | Some path when File.Exists(path) ->
            eprintfn "--- mongreldb-server log (%s) ---" path
            eprintfn "%s" (File.ReadAllText(path))
        | _ -> ()

    /// <summary>
    /// Boot the daemon once for the whole suite. Sets the current client on
    /// success, leaves it None (so tests self-skip) when no binary is available.
    /// Safe to call multiple times.
    /// </summary>
    let boot () : unit =
        lock bootLock (fun () ->
            if Option.isSome state.Current then () else

            let existingUrl = Environment.GetEnvironmentVariable("MONGRELDB_URL")
            if not (String.IsNullOrEmpty(existingUrl)) then
                if reachable(existingUrl) then
                    let token = Environment.GetEnvironmentVariable("MONGRELDB_TOKEN")
                    let c = if String.IsNullOrEmpty(token) then new Client(url = existingUrl)
                            else new Client(url = existingUrl, token = token)
                    state.Current <- Some c
                else
                    eprintfn "mongreldb: MONGRELDB_URL=%s is not reachable" existingUrl
                    failwithf "MONGRELDB_URL=%s is not reachable" existingUrl
            else
                let bin = resolveServerBinary()
                if isNull bin then
                    eprintfn "--- no mongreldb-server binary: live tests will skip"
                    ()
                else
                    let port = freePort()
                    let dataDir = Path.Combine(Path.GetTempPath(),
                                               "mongreldb-fsharp-test-" + Guid.NewGuid().ToString("N"))
                    Directory.CreateDirectory(dataDir) |> ignore
                    state.DataDir <- Some dataDir
                    let url = "http://127.0.0.1:" + string port
                    let logPath = Path.Combine(Path.GetTempPath(),
                                               "mongreldb-fsharp-server-" + Guid.NewGuid().ToString("N") + ".log")
                    state.LogPath <- Some logPath

                    let log = File.AppendText(logPath)
                    log.AutoFlush <- true
                    let psi = ProcessStartInfo(bin,
                                               (dataDir :: ["--port"; string port]) |> List.toArray,
                                               UseShellExecute = false,
                                               RedirectStandardOutput = true,
                                               RedirectStandardError = true)
                    let p = Process.Start(psi)
                    p.OutputDataReceived.Add(fun e -> if not (isNull e.Data) then log.WriteLine(e.Data))
                    p.ErrorDataReceived.Add(fun e -> if not (isNull e.Data) then log.WriteLine(e.Data))
                    p.BeginOutputReadLine()
                    p.BeginErrorReadLine()
                    state.Process <- Some p

                    if not (waitForHealth url 40) then
                        dumpLog()
                        eprintfn "mongreldb: server did not become healthy"
                        failwith "mongreldb-server did not become healthy"

                    state.Current <- Some (new Client(url = url)))

    /// <summary>Tear the daemon down (called at suite exit).</summary>
    let shutdown () : unit =
        lock bootLock (fun () ->
            match state.Process with
            | Some p ->
                try p.Kill() with _ -> ()
                try p.WaitForExit(5000) |> ignore with _ -> ()
                state.Process <- None
            | None -> ()
            match state.DataDir with
            | Some d ->
                try Directory.Delete(d, true) with _ -> ()
                state.DataDir <- None
            | None -> ()
            match state.Current with
            | Some c ->
                (c :> IDisposable).Dispose()
                state.Current <- None
            | None -> ())

    /// <summary>Throw to abort the current test when no daemon is available.</summary>
    let skipIfNoClient () : unit =
        if Option.isNone state.Current then
            failwith "no mongreldb-server available: skipping live test"
