module UniversityOS.Tests.MemoryPressureTests

open Xunit
open SkiaSharp
open UniversityOS.App.Design
open UniversityOS.App.Rendering
open UniversityOS.App.Controls

/// Tiered release under memory pressure (§21.2).
///
/// The property that matters is not "memory went down" — it is that release is
/// ordered cheapest-first, is idempotent, happens at a frame boundary, and
/// leaves every subsystem able to rebuild itself. A release that freed memory
/// but left a control unable to render would be a worse bug than the pressure.

// ---- policy ----------------------------------------------------------------

[<Fact>]
let ``pressure level follows the load against the high-load threshold`` () =
    Assert.Equal(PressureLevel.None, MemoryPressure.levelForRatio 0.0)
    Assert.Equal(PressureLevel.None, MemoryPressure.levelForRatio 0.79)
    Assert.Equal(PressureLevel.Moderate, MemoryPressure.levelForRatio 0.80)
    Assert.Equal(PressureLevel.Moderate, MemoryPressure.levelForRatio 0.89)
    Assert.Equal(PressureLevel.High, MemoryPressure.levelForRatio 0.90)
    Assert.Equal(PressureLevel.High, MemoryPressure.levelForRatio 0.97)
    Assert.Equal(PressureLevel.Critical, MemoryPressure.levelForRatio 0.98)
    Assert.Equal(PressureLevel.Critical, MemoryPressure.levelForRatio 5.0)

[<Fact>]
let ``a missing or nonsensical threshold reports no pressure`` () =
    // GetGCMemoryInfo can legitimately report zeroes early in startup or in a
    // container without limits. Dividing by that must not manufacture a
    // Critical event that throws away every cache on the first frame.
    Assert.Equal(PressureLevel.None, MemoryPressure.levelFor 100L 0L)
    Assert.Equal(PressureLevel.None, MemoryPressure.levelFor 100L -1L)
    Assert.Equal(PressureLevel.None, MemoryPressure.levelFor -1L 100L)
    Assert.Equal(PressureLevel.None, MemoryPressure.levelForRatio nan)

[<Fact>]
let ``release is ordered cheapest-to-rebuild first and grows with severity`` () =
    Assert.Empty(MemoryPressure.tiersFor PressureLevel.None)

    // Moderate touches only what the next frame rebuilds for free.
    Assert.Equal<ReleaseTier list>([ ReleaseTier.TransientSurfaces ], MemoryPressure.tiersFor PressureLevel.Moderate)

    // Each level is a prefix-extension of the one below: nothing expensive is
    // released before everything cheaper has been.
    let moderate = MemoryPressure.tiersFor PressureLevel.Moderate
    let high = MemoryPressure.tiersFor PressureLevel.High
    let critical = MemoryPressure.tiersFor PressureLevel.Critical

    Assert.Equal<ReleaseTier list>(moderate, high |> List.truncate moderate.Length)
    Assert.Equal<ReleaseTier list>(high, critical |> List.truncate high.Length)

    // Compiled shaders are the last resort and appear only at Critical.
    Assert.DoesNotContain(ReleaseTier.CompiledShaders, high)
    Assert.Equal(ReleaseTier.CompiledShaders, List.last critical)

[<Fact>]
let ``quality is pinned lower only under sustained severe pressure`` () =
    Assert.False(MemoryPressure.pinsQuality PressureLevel.None)
    Assert.False(MemoryPressure.pinsQuality PressureLevel.Moderate)
    Assert.True(MemoryPressure.pinsQuality PressureLevel.High)
    Assert.True(MemoryPressure.pinsQuality PressureLevel.Critical)

// ---- registry and application ----------------------------------------------

/// Records which tiers ran, in order.
let private recording () =
    let seen = ResizeArray<ReleaseTier>()

    for tier in
        [ ReleaseTier.TransientSurfaces
          ReleaseTier.BackdropImage
          ReleaseTier.GpuResourceCache
          ReleaseTier.PainterCaches
          ReleaseTier.CompiledShaders ] do
        MemoryPressure.register tier (fun _ -> seen.Add tier)

    seen

[<Fact>]
let ``a requested release runs its tiers in order at the next frame boundary`` () =
    try
        MemoryPressure.resetForTesting ()
        let seen = recording ()

        // Requesting alone must do nothing: the request can arrive on any
        // thread, and freeing a native mid-draw is how renderers crash.
        MemoryPressure.requestRelease PressureLevel.High
        Assert.Empty(seen)

        MemoryPressure.applyPending null
        Assert.Equal<ReleaseTier list>(MemoryPressure.tiersFor PressureLevel.High, List.ofSeq seen)
        Assert.Equal(PressureLevel.High, MemoryPressure.lastReleaseLevel ())
        Assert.Equal(1, MemoryPressure.releasesPerformed ())
    finally
        MemoryPressure.clearRegistrations ()
        MemoryPressure.resetForTesting ()

[<Fact>]
let ``applying with nothing pending is free and idempotent`` () =
    try
        MemoryPressure.resetForTesting ()
        let seen = recording ()

        MemoryPressure.applyPending null
        MemoryPressure.applyPending null
        Assert.Empty(seen)
        Assert.Equal(0, MemoryPressure.releasesPerformed ())

        // And a request is consumed exactly once.
        MemoryPressure.requestRelease PressureLevel.Moderate
        MemoryPressure.applyPending null
        MemoryPressure.applyPending null
        Assert.Equal(1, seen.Count)
        Assert.Equal(1, MemoryPressure.releasesPerformed ())
    finally
        MemoryPressure.clearRegistrations ()
        MemoryPressure.resetForTesting ()

[<Fact>]
let ``two requests between frames collapse into the more severe one`` () =
    try
        MemoryPressure.resetForTesting ()
        let seen = recording ()

        MemoryPressure.requestRelease PressureLevel.Critical
        MemoryPressure.requestRelease PressureLevel.Moderate
        MemoryPressure.applyPending null

        // Not the most recent — the most severe. Downgrading here would leave
        // the critical condition unaddressed until the next poll.
        Assert.Equal<ReleaseTier list>(MemoryPressure.tiersFor PressureLevel.Critical, List.ofSeq seen)
    finally
        MemoryPressure.clearRegistrations ()
        MemoryPressure.resetForTesting ()

[<Fact>]
let ``a failing release does not abort the remaining tiers or the frame`` () =
    try
        MemoryPressure.resetForTesting ()
        let seen = ResizeArray<ReleaseTier>()

        MemoryPressure.register ReleaseTier.TransientSurfaces (fun _ -> failwith "boom")
        MemoryPressure.register ReleaseTier.BackdropImage (fun _ -> seen.Add ReleaseTier.BackdropImage)
        MemoryPressure.register ReleaseTier.GpuResourceCache (fun _ -> seen.Add ReleaseTier.GpuResourceCache)

        MemoryPressure.requestRelease PressureLevel.High
        MemoryPressure.applyPending null

        Assert.Equal<ReleaseTier list>(
            [ ReleaseTier.BackdropImage; ReleaseTier.GpuResourceCache ],
            List.ofSeq seen
        )
    finally
        MemoryPressure.clearRegistrations ()
        MemoryPressure.resetForTesting ()

// ---- rebuild ---------------------------------------------------------------

[<Fact>]
let ``released painters and surfaces rebuild on the next frame`` () =
    // The rule that makes tiered release safe: release must never leave a
    // control unable to render. Drive a real frame, release everything the
    // renderer owns, then drive another and require an identical result.
    let snapshot =
        let accent = SKColor(0xCBuy, 0x8Auy, 0x4Euy)
        let avaColor = Avalonia.Media.Color.FromRgb(accent.Red, accent.Green, accent.Blue)
        let palette = Palette.generate avaColor

        { Mode = FoldedGlass
          Time = 4.25f
          Accent = accent
          AccentLight = SkiaInterop.toSkColor palette.Light
          AccentDeep = SkiaInterop.toSkColor palette.Dark
          AccentAlt = SkiaInterop.toSkColor (Palette.shiftHue 18.0 avaColor)
          Alpha = 0.6f
          Intensity = 1.0f
          BlurPx = 6.0f
          ParallaxX = 0.0f
          ParallaxY = 0.0f
          OverscanX = float32 (Parallax.overscanPx true 6.0)
          OverscanY = float32 (Parallax.overscanPx true 6.0)
          GlassScale = 1.0f
          Width = 200.0f
          Height = 140.0f
          Quality = QualityLevel.Ultra
          Tier = RenderTier.B
          Generation = 1 }

    let renderFrame () =
        let info = SKImageInfo(200, 140, SKColorType.Rgba8888, SKAlphaType.Premul)
        use surface = SKSurface.Create info
        surface.Canvas.Clear SKColors.Black
        surface.Canvas.Save() |> ignore
        surface.Canvas.ClipRect(SKRect(0.0f, 0.0f, 200.0f, 140.0f))

        BackdropPipeline.render
            { Canvas = surface.Canvas
              Bounds = SKRect(0.0f, 0.0f, 200.0f, 140.0f)
              GrContext = null
              Snapshot = box snapshot }
            snapshot

        surface.Canvas.Restore()
        use image = surface.Snapshot()
        use bitmap = SKBitmap.FromImage image
        bitmap.Bytes

    try
        MemoryPressure.resetForTesting ()
        let before = renderFrame ()

        // Everything the renderer owns, released for real — not a fake.
        BackdropPipeline.dispose ()
        BackdropCache.requestClear ()
        BackdropCache.applyPendingClear ()
        BackgroundPainter.dispose ()
        GlassPainter.dispose ()

        let after = renderFrame ()

        Assert.Equal<byte[]>(before, after)
    finally
        BackdropPipeline.dispose ()
        BackdropCache.dispose ()
        MemoryPressure.resetForTesting ()

[<Fact>]
let ``released shaders recompile on next use instead of degrading the session`` () =
    // dispose() is shutdown and leaves the library empty; releaseUnderPressure()
    // must arm an on-demand recompile, or a single pressure event would silently
    // pin the whole session to Tier B until it was restarted.
    Capabilities.resetForTesting ()

    try
        ShaderLibrary.prewarmAll ()
        Assert.Equal(4, ShaderLibrary.compiledCount ())

        ShaderLibrary.releaseUnderPressure ()
        Assert.Equal(0, ShaderLibrary.compiledCount ())

        // Asking for one recompiles that one, and only that one.
        Assert.True((ShaderLibrary.tryGet Aurora).IsSome, "Aurora must recompile on demand")
        Assert.Equal(1, ShaderLibrary.compiledCount ())

        Assert.True((ShaderLibrary.tryGet LiquidGlass).IsSome)
        Assert.Equal(2, ShaderLibrary.compiledCount ())

        // The tier must not have been demoted by the release.
        Assert.Equal(RenderTier.A, Capabilities.detectedTier ())
    finally
        ShaderLibrary.dispose ()
        Capabilities.resetForTesting ()

[<Fact>]
let ``sampling real memory never throws and reports a valid level`` () =
    // It reads GC info on a timer thread; an exception there would be silent.
    let level = MemoryPressure.sample ()

    Assert.Contains(
        level,
        [ PressureLevel.None; PressureLevel.Moderate; PressureLevel.High; PressureLevel.Critical ]
    )

    Assert.False(System.String.IsNullOrWhiteSpace(MemoryPressure.label level))
