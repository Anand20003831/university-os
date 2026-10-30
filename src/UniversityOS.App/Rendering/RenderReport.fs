namespace UniversityOS.App.Rendering

open System

/// The single source of truth for what the diagnostics say.
///
/// Both consumers read from here — the in-app Design Preview panel and the
/// console logger — so there is one definition of every metric rather than two
/// that drift apart (Rendering Principle 7).
///
/// Counters are reported as NAMED CUMULATIVE TOTALS with explicit per-pass
/// ratios. An earlier version reported a single "draw calls per frame" number
/// that was both racy and unattributed; it read the same on a page with four
/// glass panels as on a page with none, and was mistakenly read as proof of a
/// particular pipeline. Aggregates without attribution are not evidence.
[<RequireQualifiedAccess>]
module RenderReport =

    /// Environment variable that turns on developer diagnostics for a run.
    [<Literal>]
    let EnvVar = "UNIVERSITYOS_RENDER_LOG"

    let private ratio (numerator: int64) (denominator: int64) =
        if denominator = 0L then "n/a" else sprintf "%.2f" (float numerator / float denominator)

    let lines () =
        let c = Diagnostics.counters
        let backgroundDraws = c.Value Diagnostics.Names.backgroundDraw
        let glassDraws = c.Value Diagnostics.Names.glassDraw
        let generated = c.Value Diagnostics.Names.backdropGenerated
        let blurs = c.Value Diagnostics.Names.backdropBlur
        let skipped = c.Value Diagnostics.Names.backdropSkipped
        let glassHit = c.Value Diagnostics.Names.glassSampleHit
        let glassMiss = c.Value Diagnostics.Names.glassSampleMiss
        let gc = Diagnostics.gcSnapshot ()

        [ "-- capability --"
          sprintf "Surface         %s" (Diagnostics.currentSurface ())
          sprintf "Tier            %s" (RenderTier.label (Capabilities.effective ()))
          sprintf "Detected        %s" (RenderTier.label (Capabilities.detectedTier ()))
          sprintf "Forced          %s" (match Capabilities.forcedTier () with
                                        | Some t -> RenderTier.label t
                                        | None -> "no (automatic)")
          sprintf "Probed          %b" (Capabilities.hasProbed ())
          sprintf "Shaders         %d compiled" (ShaderLibrary.compiledCount ())
          sprintf "Quality         %s (%s)" (QualityLevel.label (QualityController.current ())) (QualityController.currentState ()).LastChange

          "-- timing --"
          sprintf "Clock           %s, %d ticks" (if FrameClock.isRunning () then "running" else "stopped") (c.Value Diagnostics.Names.clockTick)
          sprintf "FPS             %.1f" (Diagnostics.fps ())
          sprintf "Frame           %.2f ms avg, %.2f ms worst (%d samples)" Diagnostics.frame.Ewma Diagnostics.frame.Worst Diagnostics.frame.Samples
          sprintf "Background      %.2f ms avg, %.2f ms worst" Diagnostics.render.Ewma Diagnostics.render.Worst
          sprintf "Shader          %.3f ms avg" Diagnostics.shader.Ewma
          sprintf "Blur            %.3f ms avg" Diagnostics.blur.Ewma
          sprintf "Layout          %.2f ms avg" Diagnostics.layout.Ewma

          // Draw operations are attributed per control. background-host draws
          // exactly once per render pass, so it doubles as the pass count.
          "-- draw operations (cumulative, by control) --"
          sprintf "BackgroundHost  %d  (= render passes)" backgroundDraws
          sprintf "GlassPanel      %d  (%s per pass)" glassDraws (ratio glassDraws backgroundDraws)
          sprintf "Tier-C paints   %d" (c.Value Diagnostics.Names.fallbackPaint)

          "-- backdrop pipeline (cumulative) --"
          sprintf "Generated       %d  (%s per pass)" generated (ratio generated backgroundDraws)
          sprintf "Blur passes     %d  (%s per generation)" blurs (ratio blurs generated)
          sprintf "Skipped         %d  (mode off / surface unavailable)" skipped

          "-- glass sampling of the shared backdrop --"
          sprintf "Samples hit     %d  (%s per GlassPanel draw)" glassHit (ratio glassHit glassDraws)
          sprintf "Samples missed  %d  (no backdrop available)" glassMiss

          "-- other caches --"
          sprintf "Shader cache    %d hit / %d miss" (c.Value Diagnostics.Names.shaderHit) (c.Value Diagnostics.Names.shaderMiss)
          sprintf "Painters made   %d  (process lifetime)" (c.Value Diagnostics.Names.painterCreated)
          sprintf "Shaders built   %d  (process lifetime)" (c.Value "shader.compiled")

          "-- memory --"
          sprintf "GPU cache       %.1f MB / %.1f MB limit (%d resources)" (float (Diagnostics.gpuMemoryBytes ()) / 1048576.0) (float (Diagnostics.gpuMemoryLimitBytes ()) / 1048576.0) (Diagnostics.gpuResourceCount ())
          sprintf "Managed alloc   %.1f MB total" (float gc.AllocatedBytes / 1048576.0)
          sprintf "GC gen0/1/2     %d / %d / %d" gc.Gen0 gc.Gen1 gc.Gen2

          sprintf
              "Memory pressure %s now, %d release(s), last at %s"
              (MemoryPressure.label (MemoryPressure.currentLevel ()))
              (MemoryPressure.releasesPerformed ())
              (MemoryPressure.label (MemoryPressure.lastReleaseLevel ()))

          sprintf "Last failure    %s" (Capabilities.lastFailureMessage () |> Option.defaultValue "none") ]

    /// True when the run was started with diagnostics requested.
    let requestedByEnvironment () =
        match Environment.GetEnvironmentVariable EnvVar with
        | null
        | "" -> false
        | value -> value <> "0"

    /// Starts a console logger when the environment asks for one. Returns a
    /// disposable that stops it. Does nothing (and costs nothing) otherwise.
    let startConsoleLogger (intervalMs: int) : IDisposable =
        if not (requestedByEnvironment ()) then
            { new IDisposable with
                member _.Dispose() = () }
        else
            Diagnostics.setEnabled true

            let timer =
                new Threading.Timer(
                    (fun _ ->
                        printfn "---- render report ----"

                        for line in lines () do
                            printfn "%s" line),
                    null,
                    intervalMs,
                    intervalMs
                )

            timer :> IDisposable
