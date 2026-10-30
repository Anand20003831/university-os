namespace UniversityOS.App.Rendering

open System

/// The three rendering tiers. Every tier is fully functional: fidelity drops,
/// capability never does. See docs/PRESENTATION-ARCHITECTURE.md §6.
[<RequireQualifiedAccess>]
type RenderTier =
    /// GPU shader rendering (SKSL runtime effects).
    | A
    /// Skia draw-call rendering.
    | B
    /// Plain Avalonia rendering — no Skia lease available.
    | C

[<RequireQualifiedAccess>]
module RenderTier =
    let label =
        function
        | RenderTier.A -> "A (GPU shader)"
        | RenderTier.B -> "B (Skia draw calls)"
        | RenderTier.C -> "C (Avalonia)"

    /// Tier ordering for demotion: A is highest.
    let rank =
        function
        | RenderTier.A -> 2
        | RenderTier.B -> 1
        | RenderTier.C -> 0

/// Which rendering tier is active, and the one-way demotion path when an
/// advanced feature fails.
///
/// Demotion is one-way within a session on purpose: retrying a broken GPU path
/// sixty times a second is itself a performance bug, and a path that failed once
/// because the driver cannot support it will fail again.
[<RequireQualifiedAccess>]
module Capabilities =

    let private gate = obj ()

    /// What the environment turned out to support. Starts optimistic and is
    /// corrected by the first real render.
    let mutable private detected = RenderTier.A

    /// Developer override, so every tier can be exercised on any machine.
    let mutable private forced: RenderTier option = None

    let mutable private probed = false
    let mutable private lastFailure: string option = None
    let private reportedFailures = System.Collections.Generic.HashSet<string>()

    /// The tier in force right now.
    let effective () =
        match forced with
        | Some t -> t
        | None -> detected

    let detectedTier () = detected
    let forcedTier () = forced
    let hasProbed () = probed
    let lastFailureMessage () = lastFailure

    /// Developer override. None returns to the detected tier.
    let force (tier: RenderTier option) = lock gate (fun () -> forced <- tier)

    /// Called by the first successful render with what the platform provided.
    /// Only ever lowers the detected tier.
    let reportProbe (hasSkiaLease: bool) (hasGpuContext: bool) =
        lock gate (fun () ->
            probed <- true

            let supported =
                if not hasSkiaLease then RenderTier.C
                elif not hasGpuContext then RenderTier.B
                else RenderTier.A

            if RenderTier.rank supported < RenderTier.rank detected then
                detected <- supported)

    /// Permanently drop to the given tier (or lower). Logged once per reason.
    let demoteTo (tier: RenderTier) (reason: string) =
        lock gate (fun () ->
            if RenderTier.rank tier < RenderTier.rank detected then
                detected <- tier
                lastFailure <- Some reason

                if reportedFailures.Add reason then
                    eprintfn "[render] demoted to tier %s: %s" (RenderTier.label tier) reason)

    /// Records a failure without necessarily demoting. Logged once per key so a
    /// failing path cannot flood the log at frame rate.
    let reportFailure (key: string) (ex: exn) =
        lock gate (fun () ->
            lastFailure <- Some(sprintf "%s: %s" key ex.Message)

            if reportedFailures.Add key then
                eprintfn "[render] %s failed: %s" key ex.Message)

    /// True when a failure with this key has already been seen. Used to decide
    /// when repeated failures justify demotion.
    let hasFailed (key: string) = lock gate (fun () -> reportedFailures.Contains key)

    /// Test/diagnostic reset. Not used by the running application.
    let resetForTesting () =
        lock gate (fun () ->
            detected <- RenderTier.A
            forced <- None
            probed <- false
            lastFailure <- None
            reportedFailures.Clear())
