namespace Visorcraft.MongrelDB.Tests

open System
open Xunit
open Xunit.Sdk

/// <summary>
/// A fact that runs only when a live <c>mongreldb-server</c> is reachable:
/// either the <c>MONGRELDB_URL</c> env var points at a running daemon, or the
/// server binary can be resolved (env <c>MONGRELDB_SERVER</c>,
/// <c>./bin/mongreldb-server</c>, or on <c>PATH</c>). When neither holds the
/// test is reported as <i>skipped</i> rather than failed, so the offline CI
/// job stays green while the live job (which downloads the binary) runs the
/// full suite for real.
/// </summary>
/// <remarks>
/// The availability check is intentionally static (no daemon boot) so it is
/// safe to evaluate at xUnit test-discovery time, where the <c>Skip</c>
/// property is read.
/// </remarks>
type DaemonFactAttribute () =
    inherit FactAttribute ()

    static member DaemonReachable () : bool =
        // An explicit URL means a daemon is already running somewhere.
        let url = Environment.GetEnvironmentVariable("MONGRELDB_URL")
        if not (String.IsNullOrEmpty(url)) then true
        else
            // Otherwise a bootable binary must be resolvable.
            not (isNull (Daemon.resolveServerBinary()))

    override this.Skip
        with get () =
            if DaemonFactAttribute.DaemonReachable() then null
            else "no mongreldb-server available: skipping live test"
