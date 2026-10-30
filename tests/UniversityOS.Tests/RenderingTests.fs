module UniversityOS.Tests.RenderingTests

open Xunit
open SkiaSharp
open UniversityOS.App.Design
open UniversityOS.App.Rendering

/// Rendering tests for the background engine.
///
/// Comparison is TOLERANCE-BASED, never exact. GPU vendors, driver versions and
/// Skia builds differ by a bit or two per channel on antialiasing and gradient
/// dithering; exact matching produces tests that fail on a different machine for
/// no real reason, and teams respond to that by deleting them. A tolerance still
/// catches genuine regressions — a wrong colour, a missing layer, a mode that
/// stopped animating — while ignoring insignificant noise.

type ImageTolerance =
    { /// Per-channel difference (0–255) below which a pixel counts as equal.
      MaxChannelDelta: int
      /// Fraction of pixels allowed to exceed MaxChannelDelta.
      MaxDifferingPixelRatio: float }

[<RequireQualifiedAccess>]
module ImageTolerance =
    /// For flat or near-flat output.
    let strict =
        { MaxChannelDelta = 2
          MaxDifferingPixelRatio = 0.001 }

    /// For blurred, gradient-heavy output where dithering differs by build.
    let loose =
        { MaxChannelDelta = 6
          MaxDifferingPixelRatio = 0.02 }

/// Returns (withinTolerance, differingRatio).
let private compareImages (tolerance: ImageTolerance) (a: byte[]) (b: byte[]) =
    if a.Length <> b.Length || a.Length = 0 then
        false, 1.0
    else
        let pixelCount = a.Length / 4
        let mutable differing = 0

        for i in 0 .. pixelCount - 1 do
            let o = i * 4

            let delta =
                [ abs (int a[o] - int b[o])
                  abs (int a[o + 1] - int b[o + 1])
                  abs (int a[o + 2] - int b[o + 2])
                  abs (int a[o + 3] - int b[o + 3]) ]
                |> List.max

            if delta > tolerance.MaxChannelDelta then
                differing <- differing + 1

        let ratio = float differing / float pixelCount
        ratio <= tolerance.MaxDifferingPixelRatio, ratio

let private snapshotFor mode time tier (accent: SKColor) width height =
    // Derive the related hues exactly as BackgroundHost does, so tests exercise
    // the same colour relationships the application renders.
    let avaColor = Avalonia.Media.Color.FromRgb(accent.Red, accent.Green, accent.Blue)
    let palette = Palette.generate avaColor

    { Mode = mode
      Time = time
      Accent = accent
      AccentLight = SkiaInterop.toSkColor palette.Light
      AccentDeep = SkiaInterop.toSkColor palette.Dark
      AccentAlt = SkiaInterop.toSkColor (Palette.shiftHue 18.0 avaColor)
      Alpha = 0.6f
      Intensity = 1.0f
      BlurPx = 0.0f
      ParallaxX = 0.0f
      ParallaxY = 0.0f
      OverscanX = 0.0f
      OverscanY = 0.0f
      GlassScale = 1.0f
      Width = float32 width
      Height = float32 height
      Quality = QualityLevel.Medium
      Tier = tier
      Generation = 1 }

/// Renders background content into a raster surface. No GPU and no Avalonia:
/// deterministic on any machine, which is what makes these tests portable.
let private render (snapshot: BackgroundSnapshot) =
    let width = int snapshot.Width
    let height = int snapshot.Height
    let info = SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul)
    use surface = SKSurface.Create info
    let canvas = surface.Canvas
    canvas.Clear SKColors.Black
    // hasGpuContext=false: a raster surface must never run SkSL (see
    // BackgroundPainter.paintContent — it is a native crash, not an exception).
    BackgroundPainter.paintContent canvas (SKRect(0.0f, 0.0f, snapshot.Width, snapshot.Height)) false snapshot
    use image = surface.Snapshot()
    use bitmap = SKBitmap.FromImage image
    bitmap.Bytes

let private allModes = [ Aurora; LayeredWaves; LiquidGlass; FoldedGlass ]
let private amber = SKColor(0xCBuy, 0x8Auy, 0x4Euy)
let private teal = SKColor(0x6Fuy, 0xBFuy, 0xAEuy)

// ---- the comparator itself -------------------------------------------------

[<Fact>]
let ``the comparator accepts insignificant noise and rejects real differences`` () =
    // A test harness that cannot fail is worthless, so prove both directions.
    let baseline = Array.create (64 * 64 * 4) 128uy

    let nudged = baseline |> Array.map (fun b -> b + 1uy)
    let withinTolerance, _ = compareImages ImageTolerance.strict baseline nudged
    Assert.True(withinTolerance, "a one-level channel shift must be tolerated")

    let different = Array.create (64 * 64 * 4) 20uy
    let stillWithin, ratio = compareImages ImageTolerance.strict baseline different
    Assert.False(stillWithin, "a wholly different image must be rejected")
    Assert.Equal(1.0, ratio, 3)

    // Size mismatch is always a failure, never an exception.
    let shorter = Array.create 16 128uy
    Assert.False(fst (compareImages ImageTolerance.loose baseline shorter))

// ---- determinism -----------------------------------------------------------

[<Fact>]
let ``rendering the same snapshot twice is byte-identical`` () =
    // Determinism is what makes golden-image testing possible at all: the
    // snapshot carries the time, so nothing else can vary between runs.
    for mode in allModes do
        let snapshot = snapshotFor mode 3.5f RenderTier.B amber 96 64
        let first = render snapshot
        let second = render snapshot
        Assert.Equal<byte[]>(first, second)

[<Fact>]
let ``a fixed time produces the same frame regardless of when it is rendered`` () =
    let snapshot = snapshotFor LiquidGlass 12.25f RenderTier.B amber 96 64
    let first = render snapshot
    System.Threading.Thread.Sleep 30
    let second = render snapshot
    let withinTolerance, ratio = compareImages ImageTolerance.strict first second
    Assert.True(withinTolerance, sprintf "wall-clock time must not affect output (ratio %f)" ratio)

// ---- sensitivity: the engine actually responds to its inputs ---------------

[<Fact>]
let ``advancing time changes every animated mode`` () =
    // The delta is deliberately large. These backgrounds are slow by design —
    // aurora ribbons drift on a roughly sixty-second period — so a couple of
    // seconds legitimately looks almost identical. Forty seconds is around one
    // full cycle of the slowest mode.
    for mode in allModes do
        let early = render (snapshotFor mode 0.0f RenderTier.B amber 160 120)
        let later = render (snapshotFor mode 40.0f RenderTier.B amber 160 120)
        // Sensitivity checks use the STRICT tolerance: here we are proving that
        // something changed, so the comparator should be as sensitive as
        // possible. The loose tolerance is for absorbing cross-machine noise.
        let unchanged, ratio = compareImages ImageTolerance.strict early later

        Assert.False(unchanged, sprintf "%A must animate over time (only %f of pixels differed)" mode ratio)

[<Fact>]
let ``changing the accent changes the rendered background`` () =
    // Regression guard: Liquid Glass once selected its accent blobs with a hash
    // threshold that matched none of the fourteen, dropping the accent from the
    // mode entirely without any visible error.
    for mode in allModes do
        let withAmber = render (snapshotFor mode 2.0f RenderTier.B amber 160 120)
        let withTeal = render (snapshotFor mode 2.0f RenderTier.B teal 160 120)
        // Strict, for the same reason as the animation test above: the accent
        // contribution is real but subtle under heavy blur at low alpha.
        let unchanged, ratio = compareImages ImageTolerance.strict withAmber withTeal

        Assert.False(unchanged, sprintf "%A must follow the accent (only %f of pixels differed)" mode ratio)

[<Fact>]
let ``changing the accent changes MANY regions of the image, not one corner`` () =
    // The defect this guards: Liquid Glass placed its accent blobs by hash, and
    // they all landed near the top-left, so switching accent recoloured one
    // corner while the rest of the composition stayed grey. A whole-image
    // difference check passes in that state — only a per-region check catches it.
    let cols, rows = 4, 3
    let w, h = 240, 180

    for mode in [ Aurora; LiquidGlass; FoldedGlass ] do
        let a = render (snapshotFor mode 6.0f RenderTier.B amber w h)
        let b = render (snapshotFor mode 6.0f RenderTier.B teal w h)

        // Mean absolute channel difference within each grid cell.
        let regionChanged cx cy =
            let mutable total = 0.0
            let mutable count = 0

            for y in (cy * h / rows) .. ((cy + 1) * h / rows - 1) do
                for x in (cx * w / cols) .. ((cx + 1) * w / cols - 1) do
                    let o = (y * w + x) * 4

                    total <-
                        total
                        + float (abs (int a[o] - int b[o]))
                        + float (abs (int a[o + 1] - int b[o + 1]))
                        + float (abs (int a[o + 2] - int b[o + 2]))

                    count <- count + 3

            if count = 0 then 0.0 else total / float count

        let changedRegions =
            [ for cy in 0 .. rows - 1 do
                  for cx in 0 .. cols - 1 do
                      if regionChanged cx cy > 1.0 then yield (cx, cy) ]

        let required = (cols * rows) / 2

        Assert.True(
            changedRegions.Length >= required,
            sprintf
                "%A: accent must reach most of the viewport — only %d of %d regions changed"
                mode
                changedRegions.Length
                (cols * rows)
        )

        // And specifically not just the top-left quadrant.
        let rightHalf = changedRegions |> List.filter (fun (cx, _) -> cx >= cols / 2)
        let bottomHalf = changedRegions |> List.filter (fun (_, cy) -> cy >= rows / 2)
        Assert.True(not rightHalf.IsEmpty, sprintf "%A: accent must reach the right half" mode)
        Assert.True(not bottomHalf.IsEmpty, sprintf "%A: accent must reach the lower half" mode)

// ---- Aurora's contour field ------------------------------------------------

/// Which pixels read as linework. The threshold is ABSOLUTE, not a percentile:
/// a frame whose contours have faded to nothing still has relative contrast
/// between its own darkest and lightest pixels, so a percentile threshold would
/// happily pass on an effectively blank frame. Against a black clear at a known
/// composite alpha, absolute luminance means "visible to the eye" — and this
/// render path is deterministic CPU raster, so it is stable across machines.
let private lineMap (bytes: byte[]) =
    Array.init (bytes.Length / 4) (fun i ->
        let o = i * 4
        0.299 * float bytes[o] + 0.587 * float bytes[o + 1] + 0.114 * float bytes[o + 2] > 20.0)

let private lineCoverage (map: bool[]) =
    float (map |> Array.filter id |> Array.length) / float map.Length

/// Total number of separate line crossings along four evenly spaced scanlines,
/// counted in one direction. Contour lines are thin and separated, so a healthy
/// field produces many crossings; a single solid blob produces one per line.
let private lineCrossings (map: bool[]) (w: int) (h: int) (horizontal: bool) =
    let outer = if horizontal then h else w
    let inner = if horizontal then w else h

    [ outer / 5; 2 * outer / 5; 3 * outer / 5; 4 * outer / 5 ]
    |> List.sumBy (fun a ->
        let mutable crossings = 0
        let mutable previous = false

        for b in 0 .. inner - 1 do
            let lit = map[(if horizontal then a * w + b else b * w + a)]
            if lit && not previous then crossings <- crossings + 1
            previous <- lit

        crossings)

[<Fact>]
let ``Aurora's linework never fades away as time advances`` () =
    // The defect this guards, exactly: the spatial mask used to be a drifting
    // ADDITIVE term, so its slowest component could push every level of the
    // mask below the smoothstep at once. Around eight minutes in, the mean mask
    // fell to 0.118 and only 6% of the viewport stayed above half — the user
    // saw the contours dim and then disappear. The mask now moves the BOUNDARY
    // between dense and calm rather than the level, and carries a floor, so
    // coverage is bounded below by construction.
    //
    // Fifteen minutes of animation covers three cycles of the sweep (299s) and
    // one and a half of the slowest direction drift (571s).
    let w, h = 240, 180

    for step in 0..36 do
        let time = float32 step * 25.0f
        let coverage = lineCoverage (lineMap (render (snapshotFor Aurora time RenderTier.B amber w h)))

        Assert.True(
            coverage >= 0.02,
            sprintf "Aurora must stay visible at t=%.0fs — only %.4f of pixels were lit" time coverage
        )

[<Fact>]
let ``Aurora draws many separate contour lines in both directions`` () =
    // "Many separate lines" rules out a single blob; requiring crossings along
    // BOTH axes rules out a few horizontal bands, which is what the first
    // attempt at this mode produced. Measured margin is wide: the observed
    // minimum over fifteen minutes is 11 horizontal and 8 vertical.
    let w, h = 240, 180

    for time in [ 0.0f; 90.0f; 200.0f; 420.0f; 825.0f ] do
        let map = lineMap (render (snapshotFor Aurora time RenderTier.B amber w h))
        let across = lineCrossings map w h true
        let down = lineCrossings map w h false

        Assert.True(across >= 6, sprintf "t=%.0fs: expected many contour crossings across, got %d" time across)
        Assert.True(down >= 4, sprintf "t=%.0fs: expected many contour crossings down, got %d" time down)

        // Thin lines with dark gaps between them, never a filled mass.
        let coverage = lineCoverage map
        Assert.True(coverage < 0.60, sprintf "t=%.0fs: %.3f of the frame is lit — that is a blob, not linework" time coverage)

[<Fact>]
let ``Aurora's contours spread across the viewport, not one corner`` () =
    let cols, rows = 4, 3
    let w, h = 240, 180

    for time in [ 0.0f; 200.0f; 500.0f; 825.0f ] do
        let map = lineMap (render (snapshotFor Aurora time RenderTier.B amber w h))

        let populated =
            [ for cy in 0 .. rows - 1 do
                  for cx in 0 .. cols - 1 do
                      let mutable lit = 0
                      let mutable total = 0

                      for y in (cy * h / rows) .. ((cy + 1) * h / rows - 1) do
                          for x in (cx * w / cols) .. ((cx + 1) * w / cols - 1) do
                              total <- total + 1
                              if map[y * w + x] then lit <- lit + 1

                      if float lit / float total > 0.01 then yield (cx, cy) ]

        Assert.True(
            populated.Length >= 6,
            sprintf "t=%.0fs: linework reached only %d of %d regions" time populated.Length (cols * rows)
        )

[<Fact>]
let ``Reduce Motion holds Aurora on a stable frame`` () =
    // With motion disabled the clock stops, so the snapshot time never advances
    // and every repaint — resize, focus change, an overlapping window — must
    // reproduce the same frame rather than jumping.
    let held = snapshotFor Aurora 7.5f RenderTier.B amber 160 120
    Assert.Equal<byte[]>(render held, render held)

    // And the check has teeth: an advancing clock does change the frame.
    let moved = render (snapshotFor Aurora 40.0f RenderTier.B amber 160 120)
    Assert.False(fst (compareImages ImageTolerance.strict (render held) moved), "a running clock must change the frame")

[<Fact>]
let ``opacity of zero and NoBackground both render nothing`` () =
    let blank = Array.create (96 * 64 * 4) 0uy |> Array.mapi (fun i _ -> if i % 4 = 3 then 255uy else 0uy)

    // NoBackground draws nothing at all.
    let none = render (snapshotFor NoBackground 2.0f RenderTier.B amber 96 64)
    Assert.True(fst (compareImages ImageTolerance.strict blank none), "NoBackground must leave the canvas untouched")

    // A silenced layer is equally invisible.
    let silenced =
        render { snapshotFor Aurora 2.0f RenderTier.B amber 96 64 with Alpha = 0.0f }

    Assert.True(fst (compareImages ImageTolerance.strict blank silenced), "zero alpha must leave the canvas untouched")

// ---- tier fallback ---------------------------------------------------------

[<Fact>]
let ``every mode renders visible output at Tier B`` () =
    // Tier B is the guaranteed path when shaders are unavailable. No mode may
    // silently render nothing there.
    let blank = Array.create (96 * 64 * 4) 0uy |> Array.mapi (fun i _ -> if i % 4 = 3 then 255uy else 0uy)

    for mode in allModes do
        let output = render (snapshotFor mode 2.0f RenderTier.B amber 96 64)
        let isBlank, _ = compareImages ImageTolerance.loose blank output
        Assert.False(isBlank, sprintf "%A must draw something at Tier B" mode)

[<Fact>]
let ``a Tier A snapshot still renders when no shader is available`` () =
    // In this test process the shader library was never pre-warmed and there is
    // no GPU context, so the Tier A path must fall through to Tier B rather
    // than leaving the frame blank.
    let blank = Array.create (96 * 64 * 4) 0uy |> Array.mapi (fun i _ -> if i % 4 = 3 then 255uy else 0uy)

    for mode in allModes do
        let output = render (snapshotFor mode 2.0f RenderTier.A amber 96 64)
        let isBlank, _ = compareImages ImageTolerance.loose blank output
        Assert.False(isBlank, sprintf "%A must fall back rather than render nothing" mode)

[<Fact>]
let ``rendering at a reduced quality level still produces output`` () =
    let blank = Array.create (96 * 64 * 4) 0uy |> Array.mapi (fun i _ -> if i % 4 = 3 then 255uy else 0uy)

    for quality in [ QualityLevel.Ultra; QualityLevel.Low; QualityLevel.Static ] do
        for mode in allModes do
            let snapshot =
                { snapshotFor mode 2.0f RenderTier.B amber 96 64 with Quality = quality }

            let output = render snapshot
            Assert.False(fst (compareImages ImageTolerance.loose blank output), sprintf "%A at %A must draw" mode quality)

[<Fact>]
let ``degenerate sizes do not throw`` () =
    // A control can be laid out at 1px during a resize; rendering must survive.
    for width, height in [ 1, 1; 1, 64; 64, 1 ] do
        let snapshot = snapshotFor Aurora 1.0f RenderTier.B amber width height
        let output = render snapshot
        Assert.Equal(width * height * 4, output.Length)

// ---- parallax coverage, through the real pipeline ---------------------------

/// Runs a frame through BackdropPipeline exactly as the control does: render
/// the overscanned scene offscreen, draw it back displaced by the parallax, and
/// clip to the viewport. No GPU — the pipeline falls back to raster surfaces,
/// which is also why this stays on Tier B.
let private renderPipeline (snapshot: BackgroundSnapshot) =
    let width = int snapshot.Width
    let height = int snapshot.Height
    let info = SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul)
    use surface = SKSurface.Create info
    let canvas = surface.Canvas
    canvas.Clear SKColors.Transparent

    // BackgroundHost sets ClipToBounds, so the overscan is cropped away here.
    canvas.Save() |> ignore
    canvas.ClipRect(SKRect(0.0f, 0.0f, snapshot.Width, snapshot.Height))

    BackdropPipeline.render
        { Canvas = canvas
          Bounds = SKRect(0.0f, 0.0f, snapshot.Width, snapshot.Height)
          GrContext = null
          Snapshot = box snapshot }
        snapshot

    canvas.Restore()
    use image = surface.Snapshot()
    use bitmap = SKBitmap.FromImage image
    bitmap.Bytes

/// The snapshot BackgroundHost would build for this displacement: overscan
/// computed from the same functions the control uses.
///
/// Quality is pinned to Ultra (divisor 1) for the comparison tests. At lower
/// quality the scene is rendered small and upscaled, so changing the overscan
/// moves the sampling grid and every pixel shifts by a fraction — real, but
/// resampling noise, not coverage. Divisor 1 with a whole-pixel overscan keeps
/// the grid aligned, which isolates the property under test.
let private withParallax (dx: float) (dy: float) (blurPx: float) (snapshot: BackgroundSnapshot) =
    let overscan = Parallax.overscanPx true blurPx
    let limit = Parallax.allowedDisplacement overscan blurPx

    { snapshot with
        Quality = QualityLevel.Ultra
        BlurPx = float32 blurPx
        ParallaxX = float32 (max -limit (min limit dx))
        ParallaxY = float32 (max -limit (min limit dy))
        OverscanX = float32 overscan
        OverscanY = float32 overscan }

let private maxThrow = Parallax.maxDisplacementPx

let private extremes =
    [ "left", -maxThrow, 0.0
      "right", maxThrow, 0.0
      "up", 0.0, -maxThrow
      "down", 0.0, maxThrow
      "up-left", -maxThrow, -maxThrow
      "up-right", maxThrow, -maxThrow
      "down-left", -maxThrow, maxThrow
      "down-right", maxThrow, maxThrow ]

[<Fact>]
let ``parallax never exposes an edge, in any direction, at any size`` () =
    // THE defect. The background used to be rendered at exactly the viewport
    // size and then offset, so pushing it n px left uncovered an n-px strip on
    // the right. With overscan, the result must be INDEPENDENT of how much
    // overscan there is — rendering three times as much more must not change a
    // single visible pixel. If the overscan were ever insufficient, the extra
    // would fill in what was missing and the two frames would differ.
    try
        for width, height in [ 480, 320; 320, 480; 1200, 300 ] do
            for mode in allModes do
                for (name, dx, dy) in extremes do
                    let baseSnapshot = snapshotFor mode 12.0f RenderTier.B amber width height
                    let normal = withParallax dx dy 13.5 baseSnapshot

                    let generous =
                        { normal with
                            OverscanX = normal.OverscanX * 3.0f
                            OverscanY = normal.OverscanY * 3.0f }

                    let a = renderPipeline normal
                    let b = renderPipeline generous
                    let withinTolerance, ratio = compareImages ImageTolerance.loose a b

                    Assert.True(
                        withinTolerance,
                        sprintf
                            "%A at %dx%d pushed %s: %f of the viewport changed when the overscan grew, so the rendered region does not cover it"
                            mode
                            width
                            height
                            name
                            ratio
                    )
    finally
        BackdropPipeline.dispose ()
        BackdropCache.dispose ()

[<Fact>]
let ``a full-bleed background covers every viewport pixel at maximum parallax`` () =
    // Folded Glass lays down a base wash across the whole layer, so "no blank
    // edges" is directly checkable: every pixel must carry some alpha.
    try
        for (name, dx, dy) in extremes do
            let snapshot =
                snapshotFor FoldedGlass 9.0f RenderTier.B amber 400 260
                |> withParallax dx dy 13.5

            let bytes = renderPipeline snapshot
            let transparent = [ 0 .. bytes.Length / 4 - 1 ] |> List.filter (fun i -> bytes[i * 4 + 3] = 0uy)

            Assert.True(
                transparent.IsEmpty,
                sprintf "pushed %s: %d viewport pixels were left uncovered" name transparent.Length
            )
    finally
        BackdropPipeline.dispose ()
        BackdropCache.dispose ()

[<Fact>]
let ``a heavier blur does not eat the edges`` () =
    // Blur is the reason overscan cannot just equal the displacement: a blurred
    // image fades out before its edge. At maximum blur the overscan grows to
    // match, and the frame must still be independent of extra overscan.
    try
        for blur in [ 0.0; 20.0; 45.0 ] do
            let snapshot =
                snapshotFor Aurora 5.0f RenderTier.B teal 420 300
                |> withParallax maxThrow maxThrow blur

            let generous =
                { snapshot with
                    OverscanX = snapshot.OverscanX * 3.0f
                    OverscanY = snapshot.OverscanY * 3.0f }

            let withinTolerance, ratio =
                compareImages ImageTolerance.loose (renderPipeline snapshot) (renderPipeline generous)

            Assert.True(withinTolerance, sprintf "blur %f: %f of the viewport changed with more overscan" blur ratio)
    finally
        BackdropPipeline.dispose ()
        BackdropCache.dispose ()

[<Fact>]
let ``every quality level renders a covered frame`` () =
    // Overscan is expressed in logical px and converted through the quality
    // divisor. Rounding it down at a high divisor would put the uncovered strip
    // straight back, one reduced pixel wide.
    try
        for quality in [ QualityLevel.Ultra; QualityLevel.High; QualityLevel.Medium; QualityLevel.Low ] do
            let snapshot =
                { snapshotFor FoldedGlass 3.0f RenderTier.B amber 360 240 with Quality = quality }
                |> withParallax maxThrow maxThrow 13.5

            let bytes = renderPipeline snapshot
            let uncovered = [ 0 .. bytes.Length / 4 - 1 ] |> List.filter (fun i -> bytes[i * 4 + 3] = 0uy)

            Assert.True(uncovered.IsEmpty, sprintf "%A left %d pixels uncovered" quality uncovered.Length)
    finally
        BackdropPipeline.dispose ()
        BackdropCache.dispose ()

[<Fact>]
let ``resizing while displaced stays covered at every size`` () =
    try
        for width, height in [ 1200, 400; 900, 400; 640, 400; 420, 400; 240, 400; 140, 400 ] do
            let snapshot =
                snapshotFor FoldedGlass 7.0f RenderTier.B amber width height
                |> withParallax maxThrow maxThrow 13.5

            let bytes = renderPipeline snapshot
            let uncovered = [ 0 .. bytes.Length / 4 - 1 ] |> List.filter (fun i -> bytes[i * 4 + 3] = 0uy)

            Assert.True(uncovered.IsEmpty, sprintf "%dx%d left %d pixels uncovered" width height uncovered.Length)
    finally
        BackdropPipeline.dispose ()
        BackdropCache.dispose ()

[<Fact>]
let ``overscan alone does not change the composition`` () =
    // The approved look must survive the fix: with no displacement, adding
    // overscan may only extend the field past the edge, never rescale or shift
    // what is inside the viewport.
    try
        for mode in allModes do
            let plain =
                { snapshotFor mode 15.0f RenderTier.B amber 400 260 with Quality = QualityLevel.Ultra }

            let overscanned =
                { plain with
                    OverscanX = float32 (Parallax.overscanPx true 0.0)
                    OverscanY = float32 (Parallax.overscanPx true 0.0) }

            let withinTolerance, ratio =
                compareImages ImageTolerance.loose (renderPipeline plain) (renderPipeline overscanned)

            Assert.True(
                withinTolerance,
                sprintf "%A: overscan shifted or rescaled the composition (%f of pixels differed)" mode ratio
            )
    finally
        BackdropPipeline.dispose ()
        BackdropCache.dispose ()

// ---- capability demotion ---------------------------------------------------

[<Fact>]
let ``tier demotion is one-way and a developer override wins`` () =
    Capabilities.resetForTesting ()

    try
        Assert.Equal(RenderTier.A, Capabilities.effective ())

        // Probing with no GPU context drops to B.
        Capabilities.reportProbe true false
        Assert.Equal(RenderTier.B, Capabilities.effective ())

        // A later "everything is fine" probe must not promote back up:
        // retrying a broken path every frame is itself a performance bug.
        Capabilities.reportProbe true true
        Assert.Equal(RenderTier.B, Capabilities.effective ())

        // Demotion continues downwards.
        Capabilities.demoteTo RenderTier.C "test"
        Assert.Equal(RenderTier.C, Capabilities.effective ())

        // The developer override takes precedence in both directions.
        Capabilities.force (Some RenderTier.A)
        Assert.Equal(RenderTier.A, Capabilities.effective ())
        Assert.Equal(RenderTier.C, Capabilities.detectedTier ())

        Capabilities.force None
        Assert.Equal(RenderTier.C, Capabilities.effective ())
    finally
        Capabilities.resetForTesting ()

[<Fact>]
let ``every background shader compiles, and pre-warming is idempotent`` () =
    // Tier A availability rests entirely on this. SkSL compilation is CPU-side,
    // so it is verifiable here without a GPU — if a shader has a syntax error
    // this test catches it rather than the application silently running at
    // Tier B forever.
    Capabilities.resetForTesting ()

    try
        ShaderLibrary.prewarmAll ()
        Assert.True(ShaderLibrary.hasPrewarmed ())

        Assert.Equal(4, ShaderLibrary.compiledCount ())

        for mode in allModes do
            Assert.True((ShaderLibrary.tryGet mode).IsSome, sprintf "%A must have a compiled shader" mode)

        // Nothing is compiled a second time.
        ShaderLibrary.prewarmAll ()
        Assert.Equal(4, ShaderLibrary.compiledCount ())

        // Everything compiled, so the tier must not have been demoted.
        Assert.Equal(RenderTier.A, Capabilities.detectedTier ())
    finally
        ShaderLibrary.dispose ()
        Capabilities.resetForTesting ()

[<Fact>]
let ``repeated failures are only reported once per key`` () =
    Capabilities.resetForTesting ()

    try
        Assert.False(Capabilities.hasFailed "example")
        Capabilities.reportFailure "example" (exn "first")
        Assert.True(Capabilities.hasFailed "example")
        // Reporting again must not throw or reset anything.
        Capabilities.reportFailure "example" (exn "second")
        Assert.True(Capabilities.hasFailed "example")
    finally
        Capabilities.resetForTesting ()
