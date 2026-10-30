namespace UniversityOS.App.Rendering

open System
open SkiaSharp

/// Severity of the current memory situation.
[<RequireQualifiedAccess>]
type PressureLevel =
    | None
    | Moderate
    | High
    | Critical

/// What gets released, in least-valuable-first order. The numbers are the
/// release order, so sorting by them is the policy.
[<RequireQualifiedAccess>]
type ReleaseTier =
    /// Offscreen scene and blur surfaces. Rebuilt on the next frame.
    | TransientSurfaces
    /// The published blurred backdrop. Glass falls back to tint for one frame.
    | BackdropImage
    /// Skia's own GPU resource cache. Re-uploaded on demand.
    | GpuResourceCache
    /// Reused SKPaint/SKShader sets. Cheap to rebuild from tokens.
    | PainterCaches
    /// Compiled SkSL programs. Last resort: expensive, triggers a controlled
    /// recompile on next use.
    | CompiledShaders

/// Tiered release of recreatable rendering resources under memory pressure
/// (docs/PRESENTATION-ARCHITECTURE.md §21.2).
///
/// Two design choices worth stating.
///
/// **Tiered, not "drop everything".** Dropping every cache at once causes a
/// visible multi-frame hitch and immediately re-allocates most of what it freed.
/// Releasing cheapest-to-rebuild first usually relieves pressure before anything
/// expensive is touched.
///
/// **A registry, not direct calls.** The things to release live at every level
/// of the compile order — surfaces in the pipeline, paints in the painters,
/// programs in the shader library, and glass paints up in Controls. Calling them
/// directly would force this module to the bottom of the dependency graph, where
/// the pipeline could no longer ask it to run a pending release at a frame
/// boundary. Inverting it keeps the ordering honest and makes the policy
/// testable with fake registrations.
[<RequireQualifiedAccess>]
module MemoryPressure =

    // ---- pure policy -------------------------------------------------------

    /// Fraction of the OS high-load threshold at which each level begins.
    [<Literal>]
    let moderateAt = 0.80

    [<Literal>]
    let highAt = 0.90

    [<Literal>]
    let criticalAt = 0.98

    /// Level for a memory load expressed as a fraction of the high-load
    /// threshold the runtime reports. Total: a zero or nonsensical threshold
    /// reports no pressure rather than dividing by zero and panicking.
    let levelForRatio (ratio: float) =
        if Double.IsNaN ratio || ratio < moderateAt then PressureLevel.None
        elif ratio < highAt then PressureLevel.Moderate
        elif ratio < criticalAt then PressureLevel.High
        else PressureLevel.Critical

    let levelFor (loadBytes: int64) (thresholdBytes: int64) =
        if thresholdBytes <= 0L || loadBytes < 0L then
            PressureLevel.None
        else
            levelForRatio (float loadBytes / float thresholdBytes)

    /// Which tiers a level releases, in the order they are released.
    let tiersFor (level: PressureLevel) =
        match level with
        | PressureLevel.None -> []
        | PressureLevel.Moderate -> [ ReleaseTier.TransientSurfaces ]
        | PressureLevel.High ->
            [ ReleaseTier.TransientSurfaces
              ReleaseTier.BackdropImage
              ReleaseTier.GpuResourceCache ]
        | PressureLevel.Critical ->
            [ ReleaseTier.TransientSurfaces
              ReleaseTier.BackdropImage
              ReleaseTier.GpuResourceCache
              ReleaseTier.PainterCaches
              ReleaseTier.CompiledShaders ]

    /// Sustained High or worse also pins the quality controller lower, so what
    /// gets rebuilt is smaller than what was released.
    let pinsQuality (level: PressureLevel) =
        match level with
        | PressureLevel.High
        | PressureLevel.Critical -> true
        | _ -> false

    let label (level: PressureLevel) =
        match level with
        | PressureLevel.None -> "none"
        | PressureLevel.Moderate -> "moderate"
        | PressureLevel.High -> "high"
        | PressureLevel.Critical -> "critical"

    // ---- registry ----------------------------------------------------------

    let private gate = obj ()

    let private actions =
        System.Collections.Generic.Dictionary<ReleaseTier, GRContext -> unit>()

    /// Registers how a tier is released. Called once per tier during startup
    /// wiring; re-registering replaces, so it is safe to call again in tests.
    let register (tier: ReleaseTier) (release: GRContext -> unit) =
        lock gate (fun () -> actions[tier] <- release)

    let clearRegistrations () = lock gate (fun () -> actions.Clear())

    // ---- state -------------------------------------------------------------

    let mutable private pending = PressureLevel.None
    let mutable private lastObserved = PressureLevel.None
    let mutable private lastApplied = PressureLevel.None
    let mutable private releaseCount = 0
    let mutable private timer: Threading.Timer = null

    let currentLevel () = lastObserved
    let lastReleaseLevel () = lastApplied
    let releasesPerformed () = releaseCount

    /// Asks for a release at this level. Safe from any thread — it only records
    /// the request; the work happens on the render thread at a frame boundary,
    /// because render-thread-owned natives must never be freed mid-draw.
    let requestRelease (level: PressureLevel) =
        lock gate (fun () ->
            // Escalate only: two requests between frames collapse into the
            // more severe one rather than the more recent one.
            if tiersFor level |> List.length > (tiersFor pending |> List.length) then
                pending <- level)

    /// Runs any pending release. RENDER THREAD, at the top of a frame.
    /// Idempotent: with nothing pending it costs one comparison.
    let applyPending (grContext: GRContext) =
        let level = lock gate (fun () -> let p = pending in pending <- PressureLevel.None; p)

        if level <> PressureLevel.None then
            let snapshot = lock gate (fun () -> actions |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq)

            for tier in tiersFor level do
                match snapshot.TryFind tier with
                | Some release ->
                    try
                        release grContext
                    with ex ->
                        // A failed release must never take the frame with it.
                        Capabilities.reportFailure (sprintf "memory-release-%A" tier) ex
                | None -> ()

            if pinsQuality level then
                QualityController.pinLower ()

            lastApplied <- level
            releaseCount <- releaseCount + 1
            Diagnostics.countAlways "memory.release"

    // ---- monitoring --------------------------------------------------------

    /// Reads the runtime's view of memory load. Separated so the policy above
    /// can be tested without waiting for a real low-memory condition.
    let sample () =
        try
            let info = GC.GetGCMemoryInfo()
            levelFor info.MemoryLoadBytes info.HighMemoryLoadThresholdBytes
        with _ ->
            PressureLevel.None

    /// One monitoring tick. Returns the level observed.
    let poll () =
        let level = sample ()
        let previous = lastObserved
        lastObserved <- level

        if level <> PressureLevel.None then
            requestRelease level

            // Logged once per transition, not once per poll: a sustained
            // condition should not produce a scrolling wall of identical lines.
            if level <> previous then
                printfn "[render] memory pressure %s — releasing %d resource tier(s)" (label level) (tiersFor level).Length

        level

    /// Starts low-frequency monitoring. Deliberately slow: this is a safety
    /// net, and polling GC info at frame rate would itself be a cost.
    let start (intervalMs: int) =
        lock gate (fun () ->
            if isNull timer then
                timer <- new Threading.Timer((fun _ -> poll () |> ignore), null, intervalMs, intervalMs))

    let stop () =
        lock gate (fun () ->
            if not (isNull timer) then
                timer.Dispose()
                timer <- null)

    /// Test hook — clears observation state without touching registrations.
    let resetForTesting () =
        lock gate (fun () ->
            pending <- PressureLevel.None
            lastObserved <- PressureLevel.None
            lastApplied <- PressureLevel.None
            releaseCount <- 0)
