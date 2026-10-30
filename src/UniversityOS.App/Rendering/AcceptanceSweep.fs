namespace UniversityOS.App.Rendering

open System
open UniversityOS.Domain
open UniversityOS.App.Design

/// An automated Stage-1 acceptance sweep.
///
/// It exists because the tier and appearance controls must be proven to drive
/// the REAL production path, not merely to change a label. The sweep applies
/// each configuration through exactly the same entry points the Design Preview
/// buttons use — `Capabilities.force` and an `AppearanceSettings` record pushed
/// through the shell's normal apply-and-persist message — then measures a clean
/// window after a warm-up and prints one row per configuration.
///
/// Enabled only by UNIVERSITYOS_ACCEPTANCE=1. Off, it costs a single
/// environment-variable read at startup.
[<RequireQualifiedAccess>]
module AcceptanceSweep =

    [<Literal>]
    let EnvVar = "UNIVERSITYOS_ACCEPTANCE"

    let requested () =
        match Environment.GetEnvironmentVariable EnvVar with
        | null
        | "" -> false
        | value -> value <> "0"

    /// One measured configuration.
    type Step =
        { Label: string
          Tier: RenderTier option
          /// Pinned quality, so a run is repeatable rather than depending on
          /// where the adaptive controller happens to have settled.
          Quality: QualityLevel option
          Appearance: AppearanceSettings }

    let private baseline = AppearanceSettings.defaults

    /// The matrix. Every background mode at every tier, plus the appearance
    /// dimensions that change what is rendered.
    let steps: Step list =
        let modes =
            [ "aurora", BackgroundStyle.Aurora
              "waves", BackgroundStyle.LayeredWaves
              "liquid", BackgroundStyle.LiquidGlass
              "folded", BackgroundStyle.FoldedGlass
              "none", BackgroundStyle.None
              "adaptive", BackgroundStyle.Adaptive ]

        let tiers = [ "A", RenderTier.A; "B", RenderTier.B; "C", RenderTier.C ]

        [ // Every mode at every tier.
          for (tierName, tier) in tiers do
              for (modeName, style) in modes do
                  { Label = sprintf "tier%s/%s" tierName modeName
                    Tier = Some tier
                    Quality = None
                    Appearance = { baseline with BackgroundStyle = style } }

          // Appearance dimensions, exercised at Tier A on the liquid mode.
          for (key, _) in Palette.accents do
              { Label = sprintf "accent/%s" key
                Tier = Some RenderTier.A
                Quality = None
                Appearance = { baseline with AccentKey = key; BackgroundStyle = BackgroundStyle.LiquidGlass } }

          { Label = "glass/min"
            Tier = Some RenderTier.A
            Quality = None
            Appearance = { baseline with GlassIntensity = 0 } }
          { Label = "glass/max"
            Tier = Some RenderTier.A
            Quality = None
            Appearance = { baseline with GlassIntensity = 100 } }
          { Label = "radius/min"
            Tier = Some RenderTier.A
            Quality = None
            Appearance = { baseline with CornerRadius = 0 } }
          { Label = "radius/max"
            Tier = Some RenderTier.A
            Quality = None
            Appearance = { baseline with CornerRadius = 28 } }
          { Label = "anim/slow"
            Tier = Some RenderTier.A
            Quality = None
            Appearance = { baseline with AnimationSpeed = Speed.Slow } }
          { Label = "anim/fast"
            Tier = Some RenderTier.A
            Quality = None
            Appearance = { baseline with AnimationSpeed = Speed.Fast } }
          { Label = "opacity/min"
            Tier = Some RenderTier.A
            Quality = None
            Appearance = { baseline with BackgroundOpacity = 0 } }
          { Label = "opacity/max"
            Tier = Some RenderTier.A
            Quality = None
            Appearance = { baseline with BackgroundOpacity = 100 } }
          { Label = "blur/max"
            Tier = Some RenderTier.A
            Quality = None
            Appearance = { baseline with BackgroundBlur = 100 } }
          { Label = "intensity/min"
            Tier = Some RenderTier.A
            Quality = None
            Appearance = { baseline with BackgroundIntensity = 0 } }
          { Label = "intensity/max"
            Tier = Some RenderTier.A
            Quality = None
            Appearance = { baseline with BackgroundIntensity = 100 } }
          { Label = "parallax/on"
            Tier = Some RenderTier.A
            Quality = None
            Appearance = { baseline with BackgroundParallax = true } }
          { Label = "reduce-motion/on"
            Tier = Some RenderTier.A
            Quality = None
            Appearance = { baseline with ReduceMotion = true } }
          { Label = "theme/light"
            Tier = Some RenderTier.A
            Quality = None
            Appearance = { baseline with Theme = ThemePreference.Light } } ]

    /// A long-window soak for allocation and collection pressure, covering each
    /// tier plus the static (reduce-motion) path.
    let gcSteps: Step list =
        [ { Label = "gc/tierA-ultra"
            Tier = Some RenderTier.A
            Quality = None
            Appearance = { baseline with BackgroundStyle = BackgroundStyle.LiquidGlass } }
          { Label = "gc/tierB"
            Tier = Some RenderTier.B
            Quality = None
            Appearance = { baseline with BackgroundStyle = BackgroundStyle.LiquidGlass } }
          { Label = "gc/tierC"
            Tier = Some RenderTier.C
            Quality = None
            Appearance = { baseline with BackgroundStyle = BackgroundStyle.LiquidGlass } }
          { Label = "gc/static"
            Tier = Some RenderTier.A
            Quality = None
            Appearance =
              { baseline with
                  BackgroundStyle = BackgroundStyle.LiquidGlass
                  ReduceMotion = true } } ]

    /// The reference performance set: the configurations Stage 1 is judged on,
    /// with quality pinned so successive runs are directly comparable.
    /// Ordering matters: the first step is measured while the window is still
    /// taking focus, and an inactive window correctly stops the master clock.
    /// Tier A therefore sits after a step that has already settled, and the
    /// deliberately-static reduce-motion case goes last.
    let cleanSteps: Step list =
        [ { Label = "tierB/aurora"
            Tier = Some RenderTier.B
            Quality = Some QualityLevel.Ultra
            Appearance = { baseline with BackgroundStyle = BackgroundStyle.Aurora } }
          { Label = "tierB/waves"
            Tier = Some RenderTier.B
            Quality = None // adaptive: proves the frame-aware budget reacts
            Appearance = { baseline with BackgroundStyle = BackgroundStyle.LayeredWaves } }
          { Label = "tierB/liquid"
            Tier = Some RenderTier.B
            Quality = Some QualityLevel.Ultra
            Appearance = { baseline with BackgroundStyle = BackgroundStyle.LiquidGlass } }
          { Label = "tierC/liquid"
            Tier = Some RenderTier.C
            Quality = Some QualityLevel.Ultra
            Appearance = { baseline with BackgroundStyle = BackgroundStyle.LiquidGlass } }
          { Label = "tierA-ultra/liquid"
            Tier = Some RenderTier.A
            Quality = Some QualityLevel.Ultra
            Appearance = { baseline with BackgroundStyle = BackgroundStyle.LiquidGlass } }
          { Label = "static/reduce-motion"
            Tier = Some RenderTier.A
            Quality = None
            Appearance =
              { baseline with
                  BackgroundStyle = BackgroundStyle.LiquidGlass
                  ReduceMotion = true } } ]

    /// "gc" = long allocation soak, "clean" = the reference performance set,
    /// anything else = the full matrix.
    let private mode () =
        match Environment.GetEnvironmentVariable EnvVar with
        | null -> ""
        | value -> value.Trim().ToLowerInvariant()

    let activeSteps () =
        match mode () with
        | "gc" -> gcSteps
        | "clean" -> cleanSteps
        | _ -> steps

    /// Warm-up before each measurement window, in milliseconds.
    let warmUpMs () =
        match mode () with
        | "gc" -> 3000
        | "clean" -> 8000
        | _ -> 1500

    /// Measurement window per configuration, in milliseconds.
    let measureMs () =
        match mode () with
        | "gc" -> 20000
        | "clean" -> 10000
        | _ -> 2500

    /// Settle time before the first step, so the very first row is not measured
    /// while the window is still being created and taking focus.
    /// Settle before the first step. Generous on a clean run: the GPU clocks up
    /// and Avalonia's renderer settles over the first several seconds, and
    /// measuring through that produces rows that understate steady-state
    /// throughput.
    let private settleMs () =
        match mode () with
        | "clean" -> 20000
        | "gc" -> 8000
        | _ -> 1000

    let private header () =
        printfn ""
        printfn "==== STAGE 1 ACCEPTANCE SWEEP ===="
        printfn "warm-up %dms, measure %dms per step, %d steps" (warmUpMs ()) (measureMs ()) (activeSteps ()).Length

        printfn
            "%-18s %-14s %7s %8s %8s %8s %8s %7s %7s %7s %7s %6s"
            "step"
            "tier/quality"
            "fps"
            "frame_ms"
            "worst_ms"
            "bg_ms"
            "blur_ms"
            "passes"
            "glass"
            "gen"
            "blurs"
            "gc0/1/2"

    /// Prints one measured row. Call after the measurement window has elapsed.
    let private row (step: Step) (gcBefore: Diagnostics.GcSnapshot) =
        let c = Diagnostics.counters
        let gc = Diagnostics.gcDelta gcBefore (Diagnostics.gcSnapshot ())
        let passes = c.Value Diagnostics.Names.backgroundDraw

        let bytesPerPass =
            if passes > 0L then float gc.Bytes / float passes else 0.0

        printfn
            "%-18s %-14s %7.1f %8.2f %8.2f %8.3f %8.3f %7d %7d %7d %7d %8s  alloc=%.0fKB/s %.0fB/pass"
            step.Label
            (sprintf "%s/%s"
                (match Capabilities.effective () with
                 | RenderTier.A -> "A"
                 | RenderTier.B -> "B"
                 | RenderTier.C -> "C")
                (QualityLevel.label (QualityController.current ())))
            (Diagnostics.fps ())
            Diagnostics.frame.Ewma
            Diagnostics.frame.Worst
            Diagnostics.render.Ewma
            Diagnostics.blur.Ewma
            passes
            (c.Value Diagnostics.Names.glassDraw)
            (c.Value Diagnostics.Names.backdropGenerated)
            (c.Value Diagnostics.Names.backdropBlur)
            (sprintf "%d/%d/%d" gc.Gen0 gc.Gen1 gc.Gen2)
            (gc.BytesPerSecond / 1024.0)
            bytesPerPass

    /// Runs the sweep. `apply` must push the appearance through the application's
    /// normal path so the real controls observe it; `finished` is called at the
    /// end. Both run on the UI thread.
    let run (apply: AppearanceSettings -> unit) (finished: unit -> unit) =
        Diagnostics.setEnabled true
        let plan = activeSteps ()
        header ()

        let mutable index = 0
        let mutable gcBefore = Diagnostics.gcSnapshot ()
        let mutable measuring = false

        let timer =
            new Avalonia.Threading.DispatcherTimer(Interval = TimeSpan.FromMilliseconds(float (warmUpMs ())))

        timer.Tick.Add(fun _ ->
            if not measuring then
                // Warm-up elapsed for this step: clear counters and measure.
                if index < plan.Length then
                    Diagnostics.resetMeasurements ()
                    gcBefore <- Diagnostics.gcSnapshot ()
                    measuring <- true
                    timer.Interval <- TimeSpan.FromMilliseconds(float (measureMs ()))
                else
                    timer.Stop()
                    printfn "==== SWEEP COMPLETE ===="
                    finished ()
            else
                // Measurement window elapsed: record and advance.
                row plan[index] gcBefore
                index <- index + 1
                measuring <- false
                timer.Interval <- TimeSpan.FromMilliseconds(float (warmUpMs ()))

                if index < plan.Length then
                    let next = plan[index]
                    Capabilities.force next.Tier
                    QualityController.force next.Quality
                    apply next.Appearance)

        // Prime the first step, then start.
        if not plan.IsEmpty then
            Capabilities.force plan[0].Tier
            QualityController.force plan[0].Quality
            apply plan[0].Appearance

        // First interval is the settle time; OnTick switches to warm-up/measure.
        timer.Interval <- TimeSpan.FromMilliseconds(float (settleMs ()))
        timer.Start()
