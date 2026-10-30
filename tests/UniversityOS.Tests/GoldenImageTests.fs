module UniversityOS.Tests.GoldenImageTests

open System
open System.IO
open Xunit
open SkiaSharp
open UniversityOS.App.Design
open UniversityOS.App.Rendering
open UniversityOS.App.Controls

/// Committed golden-image baselines for the approved Stage 1 rendering
/// (docs/PRESENTATION-ARCHITECTURE.md §25.2).
///
/// These were deliberately NOT created until the look was signed off: baselines
/// committed before visual approval are invalidated by the first requested
/// change, and a suite that fails for expected reasons gets ignored and then
/// deleted.
///
/// TIER A IS DELIBERATELY ABSENT. Tier A is a GPU shader path, and on this
/// SkiaSharp build drawing any SkSL runtime effect onto a CPU raster surface
/// kills the process natively — so there is no honest way to capture it here.
/// Rendering the Tier B fallback and filing it under "Tier A" would produce a
/// baseline that passes forever while proving nothing about the shaders, which
/// is worse than having no baseline at all. Tier A is covered instead by the
/// compile-and-prewarm test in RenderingTests and by manual review of the
/// published build. When a GPU-capable test host exists, Tier A baselines are
/// captured there and committed alongside these.

// ---- baseline storage ------------------------------------------------------

/// Walks up from the test assembly to the project directory, so baselines are
/// read from and written to the SOURCE tree rather than a copy under bin/ that
/// would be silently regenerated on every build.
let private projectDirectory =
    let rec search (dir: DirectoryInfo) =
        if isNull (box dir) then
            failwith "could not locate UniversityOS.Tests project directory"
        elif File.Exists(Path.Combine(dir.FullName, "UniversityOS.Tests.fsproj")) then
            dir.FullName
        else
            search dir.Parent

    search (DirectoryInfo AppContext.BaseDirectory)

let private baselineDirectory = Path.Combine(projectDirectory, "baselines")

/// Failure artefacts go to the build output, never into the source tree.
let private artefactDirectory =
    Path.Combine(AppContext.BaseDirectory, "golden-failures")

/// Baselines are regenerated only when explicitly asked for, so a genuine
/// regression can never quietly rewrite the thing it was supposed to fail
/// against.
let private updateRequested =
    match Environment.GetEnvironmentVariable "UNIVERSITYOS_UPDATE_BASELINES" with
    | null
    | "" -> false
    | value -> value <> "0"

// ---- comparison ------------------------------------------------------------

type Tolerance =
    { /// Per-channel difference (0–255) below which a pixel counts as equal.
      MaxChannelDelta: int
      /// Fraction of pixels allowed to exceed MaxChannelDelta.
      MaxDifferingPixelRatio: float
      /// Border pixels ignored entirely, absorbing antialiasing seams.
      IgnoreEdgePixels: int }

[<RequireQualifiedAccess>]
module Tolerance =
    /// Flat or near-flat output: tints, borders, solid fills.
    let tight =
        { MaxChannelDelta = 3
          MaxDifferingPixelRatio = 0.002
          IgnoreEdgePixels = 1 }

    /// Gradient- and blur-heavy output, where Skia builds and GPU vendors
    /// legitimately differ by a level or two per channel.
    let soft =
        { MaxChannelDelta = 8
          MaxDifferingPixelRatio = 0.02
          IgnoreEdgePixels = 2 }

/// Returns (withinTolerance, differingRatio, diffImage).
let private compare (tolerance: Tolerance) (width: int) (height: int) (baseline: byte[]) (actual: byte[]) =
    if baseline.Length <> actual.Length then
        false, 1.0, Array.zeroCreate 0
    else
        let diff = Array.zeroCreate<byte> baseline.Length
        let mutable differing = 0
        let mutable considered = 0
        let edge = tolerance.IgnoreEdgePixels

        for y in 0 .. height - 1 do
            for x in 0 .. width - 1 do
                let o = (y * width + x) * 4

                let onEdge =
                    x < edge || y < edge || x >= width - edge || y >= height - edge

                let delta =
                    [ 0..3 ] |> List.map (fun c -> abs (int baseline[o + c] - int actual[o + c])) |> List.max

                if not onEdge then
                    considered <- considered + 1

                    if delta > tolerance.MaxChannelDelta then
                        differing <- differing + 1

                // Heat map: red where it differs, dimmed original elsewhere.
                if delta > tolerance.MaxChannelDelta && not onEdge then
                    diff[o] <- 255uy
                    diff[o + 1] <- 0uy
                    diff[o + 2] <- 0uy
                    diff[o + 3] <- 255uy
                else
                    diff[o] <- baseline[o] / 3uy
                    diff[o + 1] <- baseline[o + 1] / 3uy
                    diff[o + 2] <- baseline[o + 2] / 3uy
                    diff[o + 3] <- 255uy

        let ratio =
            if considered = 0 then 0.0 else float differing / float considered

        ratio <= tolerance.MaxDifferingPixelRatio, ratio, diff

let private writePng (path: string) (width: int) (height: int) (pixels: byte[]) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    use bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul)
    System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length)
    use image = SKImage.FromBitmap bitmap
    use data = image.Encode(SKEncodedImageFormat.Png, 100)
    use stream = File.Create path
    data.SaveTo stream

let private readPng (path: string) (width: int) (height: int) =
    use bitmap = SKBitmap.Decode path

    if isNull bitmap || bitmap.Width <> width || bitmap.Height <> height then
        None
    else
        use converted =
            bitmap.Copy(SKColorType.Rgba8888) |> Option.ofObj |> Option.defaultValue bitmap

        Some converted.Bytes

/// The one assertion every golden test funnels through.
let private assertMatchesBaseline (name: string) (tolerance: Tolerance) (width: int) (height: int) (actual: byte[]) =
    let path = Path.Combine(baselineDirectory, name + ".png")

    if updateRequested then
        writePng path width height actual
    elif not (File.Exists path) then
        Assert.Fail(
            sprintf
                "No baseline for '%s'. Baselines are never created implicitly — re-run with UNIVERSITYOS_UPDATE_BASELINES=1 to record one, and review the PNG before committing it."
                name
        )
    else
        match readPng path width height with
        | None ->
            Assert.Fail(sprintf "Baseline '%s' is unreadable or the wrong size (expected %dx%d)." name width height)
        | Some baseline ->
            let ok, ratio, diff = compare tolerance width height baseline actual

            if not ok then
                writePng (Path.Combine(artefactDirectory, name + ".expected.png")) width height baseline
                writePng (Path.Combine(artefactDirectory, name + ".actual.png")) width height actual
                writePng (Path.Combine(artefactDirectory, name + ".diff.png")) width height diff

                Assert.Fail(
                    sprintf
                        "'%s' drifted from its baseline: %.4f of pixels exceeded a channel delta of %d (limit %.4f). expected/actual/diff written to %s"
                        name
                        ratio
                        tolerance.MaxChannelDelta
                        tolerance.MaxDifferingPixelRatio
                        artefactDirectory
                )

// ---- subjects --------------------------------------------------------------

let private amber = SKColor(0xCBuy, 0x8Auy, 0x4Euy)

let private baselineWidth = 240
let private baselineHeight = 160

let private snapshotFor mode quality =
    let avaColor = Avalonia.Media.Color.FromRgb(amber.Red, amber.Green, amber.Blue)
    let palette = Palette.generate avaColor

    { Mode = mode
      // A fixed time is what makes this reproducible at all.
      Time = 11.5f
      Accent = amber
      AccentLight = SkiaInterop.toSkColor palette.Light
      AccentDeep = SkiaInterop.toSkColor palette.Dark
      AccentAlt = SkiaInterop.toSkColor (Palette.shiftHue 18.0 avaColor)
      Alpha = 0.55f
      Intensity = 1.0f
      // A real blur and a real displacement, so the baselines cover the
      // overscan geometry rather than only the still composition.
      BlurPx = 9.0f
      ParallaxX = 6.0f
      ParallaxY = -4.0f
      OverscanX = float32 (Parallax.overscanPx true 9.0)
      OverscanY = float32 (Parallax.overscanPx true 9.0)
      GlassScale = 1.0f
      Width = float32 baselineWidth
      Height = float32 baselineHeight
      Quality = quality
      Tier = RenderTier.B
      Generation = 1 }

/// Rendered through BackdropPipeline, not the painter alone.
///
/// The painter ignores the quality level for two of the four modes — quality
/// enters as the offscreen divisor, which is a pipeline concern. Capturing at
/// the painter would have produced three byte-identical files per mode and a
/// "each quality level" claim that covered nothing. Going through the pipeline
/// also puts the overscan and blur geometry under the baselines.
let private renderBackground (snapshot: BackgroundSnapshot) =
    let info = SKImageInfo(baselineWidth, baselineHeight, SKColorType.Rgba8888, SKAlphaType.Premul)
    use surface = SKSurface.Create info
    surface.Canvas.Clear(SKColor(0x17uy, 0x18uy, 0x1Cuy))
    surface.Canvas.Save() |> ignore
    surface.Canvas.ClipRect(SKRect(0.0f, 0.0f, snapshot.Width, snapshot.Height))

    BackdropPipeline.render
        { Canvas = surface.Canvas
          Bounds = SKRect(0.0f, 0.0f, snapshot.Width, snapshot.Height)
          GrContext = null
          Snapshot = box snapshot }
        snapshot

    surface.Canvas.Restore()
    use image = surface.Snapshot()
    use bitmap = SKBitmap.FromImage image
    bitmap.Bytes

// ---- the baselines ---------------------------------------------------------

[<Theory>]
[<InlineData("aurora")>]
[<InlineData("waves")>]
[<InlineData("liquid")>]
[<InlineData("folded")>]
let ``background modes match their baseline at each quality level`` (modeName: string) =
    let mode =
        match modeName with
        | "aurora" -> Aurora
        | "waves" -> LayeredWaves
        | "liquid" -> LiquidGlass
        | _ -> FoldedGlass

    try
        for quality, label in
            [ QualityLevel.Ultra, "ultra"
              QualityLevel.Medium, "medium"
              QualityLevel.Low, "low" ] do
            let actual = renderBackground (snapshotFor mode quality)

            assertMatchesBaseline
                (sprintf "background-%s-tierB-%s" modeName label)
                Tolerance.soft
                baselineWidth
                baselineHeight
                actual
    finally
        BackdropPipeline.dispose ()
        BackdropCache.dispose ()

/// GlassPanel is rendered through its production painter with a synthetic
/// backdrop published to the shared cache, so the baseline exercises the real
/// sampling path rather than a tint-only shortcut.
let private renderGlassCore (glassScale: float) (highlightScale: float) (borderInset: float32) =
    let width, height = 200, 120

    // A deterministic stand-in for the blurred background, with enough
    // structure that a panel which stopped sampling it would look obviously
    // different rather than merely darker.
    use backdropSurface =
        SKSurface.Create(SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))

    backdropSurface.Canvas.Clear(SKColor(0x24uy, 0x1Cuy, 0x12uy))

    use stripe = new SKPaint(Color = SKColor(0xCBuy, 0x8Auy, 0x4Euy, 0xB0uy), IsAntialias = false)

    for i in 0..7 do
        backdropSurface.Canvas.DrawRect(SKRect(float32 (i * 26), 0.0f, float32 (i * 26 + 13), float32 height), stripe)

    BackdropCache.publish (backdropSurface.Snapshot()) (float32 width) (float32 height) 1

    let toByte (v: float) = byte (max 0.0 (min 255.0 (v * 255.0)))

    let snapshot: GlassSnapshot =
        { Width = float32 width
          Height = float32 height
          OffsetX = 0.0f
          OffsetY = 0.0f
          Radius = 14.0f
          Tint = SKColor(0x22uy, 0x1Duy, 0x16uy, toByte (Glass.tintAlpha glassScale))
          Border = SKColor(255uy, 255uy, 255uy, toByte (Glass.borderAlpha glassScale))
          BackdropAlpha = float32 (Glass.backdropAlpha glassScale)
          HighlightAlpha = float32 (Glass.highlightAlpha glassScale * highlightScale)
          ShadowAlpha = float32 (0.18 * Glass.shadowAlphaScale glassScale)
          ShadowSigma = float32 (Glass.shadowSigma glassScale)
          BorderInset = borderInset
          Generation = 1 }

    use surface =
        SKSurface.Create(SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))

    surface.Canvas.Clear(SKColor(0x17uy, 0x18uy, 0x1Cuy))

    GlassPainter.paint
        { Canvas = surface.Canvas
          Bounds = SKRect(0.0f, 0.0f, float32 width, float32 height)
          GrContext = null
          Snapshot = box snapshot }

    use image = surface.Snapshot()
    use bitmap = SKBitmap.FromImage image
    bitmap.Bytes, width, height

/// The committed baselines are the shell's own glass, which keeps the full
/// specular highlight. Unchanged by the card highlight-removal pass.
/// The already-approved outline: stroked on the bounds, no inset. The committed
/// baselines and every approved surface render this way.
let private renderGlassWith (glassScale: float) (highlightScale: float) =
    renderGlassCore glassScale highlightScale 0.0f

let private renderGlass (glassScale: float) = renderGlassWith glassScale 1.0

[<Theory>]
[<InlineData(0, "0")>]
[<InlineData(50, "50")>]
[<InlineData(100, "100")>]
let ``glass panel matches its baseline at each defined point`` (slider: int, label: string) =
    try
        let actual, width, height = renderGlass (float slider / 50.0)
        assertMatchesBaseline (sprintf "glass-panel-%s" label) Tolerance.tight width height actual
    finally
        BackdropCache.dispose ()

// ---- the card's no-sheen treatment, at the pixel level ---------------------
//
// The Dashboard's white top section came from the specular pass: a linear
// gradient from white down over `Glass.highlightReach` — 38% of the panel — so
// on a card it was never an edge highlight, it was a pale region across the top
// third. These render through the production painter rather than asserting a
// constant, because "the pass is skipped" is a claim about drawing.

/// Renders a panel at the Dashboard's card treatment.
let private renderCard () =
    renderGlassWith 1.0 UniversityOS.App.Features.DashboardLogic.Card.highlightScale

/// The same panel with the specular pass provably absent.
let private renderWithoutHighlight () = renderGlassWith 1.0 0.0

/// Renders a panel at the PLANNER's content-card treatment.
///
/// Every Planner card — including Planner History and Future Prediction, the two
/// that showed a pale top section — goes through one `card` helper carrying this
/// one value, so this is the treatment all nine of them draw with.
let private renderPlannerCard () =
    renderGlassWith 1.0 UniversityOS.App.Features.PlannerLogic.Geometry.contentHighlight

/// Renders a panel at the INBOX's content-card treatment.
///
/// Every Inbox surface — the five metric cards, the insight strip, each review
/// card and the detail panel — goes through one `card` helper carrying this one
/// value, so this is the treatment the whole screen draws with.
let private renderInboxCard () =
    renderGlassWith 1.0 UniversityOS.App.Features.InboxLogic.Geometry.contentHighlight

/// A review card and a glass action carry the inset outline; the approved
/// content cards do not. Rendered through the production painter, so this is a
/// statement about drawing rather than about a constant.
let private renderInboxAction () =
    renderGlassCore
        1.0
        UniversityOS.App.Features.InboxLogic.Geometry.contentHighlight
        (float32 UniversityOS.App.Features.InboxLogic.Geometry.borderInset)

/// Renders a panel at the ASSIGNMENTS screen's content-card treatment.
///
/// Every surface on that screen — the five metric cards, each assignment row
/// compact or expanded, the forecast panel, the risk overview and the editor —
/// goes through one `card` helper carrying this one value, so this is the
/// treatment the whole screen draws with.
let private renderAssignmentsCard () =
    renderGlassWith 1.0 UniversityOS.App.Features.AssignmentsLogic.Geometry.contentHighlight

let private renderAssignmentsAction () =
    renderGlassCore
        1.0
        UniversityOS.App.Features.AssignmentsLogic.Geometry.contentHighlight
        (float32 UniversityOS.App.Features.AssignmentsLogic.Geometry.borderInset)

// ---- the action button's outline, at the pixel level ------------------------

[<Fact>]
let ``insetting the outline puts more of it inside the panel`` () =
    // The defect behind the ragged button corners. A stroke is centred on its
    // path, so an outline laid exactly on the panel bounds puts half its width
    // outside them, where the draw operation's bounds clip it away. Insetting by
    // half the stroke width keeps the whole line — measurably more border ink
    // inside the same panel.
    try
        let onBounds, width, height = renderGlassCore 1.0 0.0 0.0f
        let inset, _, _ = renderGlassCore 1.0 0.0 0.5f

        // How far the outermost ring of pixels departs from the panel's interior
        // fill. The outline is the only thing drawn there, so more departure is
        // more surviving outline.
        let edgeInk (bytes: byte[]) =
            let luminance x y =
                let i = (y * width + x) * 4
                0.2126 * float bytes.[i] + 0.7152 * float bytes.[i + 1] + 0.0722 * float bytes.[i + 2]

            // Interior reference, well away from every edge and corner.
            let reference = luminance (width / 2) (height / 2)

            let mutable total = 0.0

            for x in 0 .. width - 1 do
                total <- total + abs (luminance x 0 - reference)
                total <- total + abs (luminance x (height - 1) - reference)

            for y in 0 .. height - 1 do
                total <- total + abs (luminance 0 y - reference)
                total <- total + abs (luminance (width - 1) y - reference)

            total

        Assert.True(
            edgeInk inset > edgeInk onBounds,
            sprintf
                "inset edge ink %.1f was not greater than on-bounds %.1f — the outline is still being clipped"
                (edgeInk inset)
                (edgeInk onBounds)
        )
    finally
        BackdropCache.dispose ()

[<Fact>]
let ``the inset outline changes the corners, not just the straight edges`` () =
    // If the two renders differed only along the flat edges the corners would
    // still be clipped. The corner blocks must differ too.
    try
        let onBounds, width, height = renderGlassCore 1.0 0.0 0.0f
        let inset, _, _ = renderGlassCore 1.0 0.0 0.5f

        let cornerDelta () =
            let mutable diff = 0
            let span = 10

            for y in 0 .. span - 1 do
                for x in 0 .. span - 1 do
                    for cx, cy in
                        [ x, y; width - 1 - x, y; x, height - 1 - y; width - 1 - x, height - 1 - y ] do
                        let i = (cy * width + cx) * 4
                        if onBounds.[i] <> inset.[i] then diff <- diff + 1

            diff

        Assert.True(cornerDelta () > 0, "the corners rendered identically — the inset did not reach them")
    finally
        BackdropCache.dispose ()

[<Fact>]
let ``the approved surfaces still render with the original outline`` () =
    // The fix is scoped. Everything already approved — the cards, the shell's
    // glass, the committed baselines — must be byte-identical to before.
    try
        let a, _, _ = renderGlassWith 1.0 0.0
        let b, _, _ = renderGlassCore 1.0 0.0 0.0f
        Assert.Equal<byte[]>(a, b)
    finally
        BackdropCache.dispose ()

[<Fact>]
let ``a Planner content card is pixel-identical to one drawn with no specular pass`` () =
    try
        let card, _, _ = renderPlannerCard ()
        let flat, _, _ = renderWithoutHighlight ()
        Assert.Equal<byte[]>(flat, card)
    finally
        BackdropCache.dispose ()

[<Fact>]
let ``a Planner content card is the same surface as an approved Dashboard card`` () =
    // No separate implementation for any Planner card: the same painter, the
    // same treatment, the same bytes as the surface that is already approved.
    try
        let planner, _, _ = renderPlannerCard ()
        let dashboard, _, _ = renderCard ()
        Assert.Equal<byte[]>(dashboard, planner)
    finally
        BackdropCache.dispose ()

[<Fact>]
let ``the Planner card surface has no top luminance band`` () =
    // Reads the rendered card directly. This is the pixel-level statement of the
    // defect reported on Planner History and Future Prediction: the top strip
    // and the bottom strip must be the same brightness.
    try
        let bytes, width, _ = renderPlannerCard ()

        let stripMean (fromRow: int) (toRow: int) =
            let mutable total = 0.0
            let mutable count = 0

            for y in fromRow .. toRow - 1 do
                for x in 20 .. width - 21 do
                    let i = (y * width + x) * 4
                    total <- total + 0.2126 * float bytes.[i] + 0.7152 * float bytes.[i + 1] + 0.0722 * float bytes.[i + 2]
                    count <- count + 1

            total / float count

        // Rows 6-20 sit inside where the 38% gradient would reach; 80-110 sit
        // well outside it.
        let top = stripMean 6 20
        let bottom = stripMean 80 110

        Assert.True(
            abs (top - bottom) < 1.0,
            sprintf "top strip %.2f vs bottom strip %.2f — that difference is the white section" top bottom
        )
    finally
        BackdropCache.dispose ()

// ---- the Inbox, at the pixel level -----------------------------------------

[<Fact>]
let ``an Inbox content card is pixel-identical to one drawn with no specular pass`` () =
    // The strongest available statement of "there is no white sheen anywhere on
    // this screen": not a threshold, not a tolerance — the same bytes.
    try
        let card, _, _ = renderInboxCard ()
        let flat, _, _ = renderWithoutHighlight ()
        Assert.Equal<byte[]>(flat, card)
    finally
        BackdropCache.dispose ()

[<Fact>]
let ``an Inbox card is the same surface as the approved Planner and Dashboard cards`` () =
    // No new surface for a new screen: the same painter, the same treatment, the
    // same bytes as the two that are already approved.
    try
        let inbox, _, _ = renderInboxCard ()
        let planner, _, _ = renderPlannerCard ()
        let dashboard, _, _ = renderCard ()

        Assert.Equal<byte[]>(planner, inbox)
        Assert.Equal<byte[]>(dashboard, inbox)
    finally
        BackdropCache.dispose ()

[<Fact>]
let ``the Inbox card surface has no top luminance band`` () =
    try
        let bytes, width, _ = renderInboxCard ()

        let stripMean (fromRow: int) (toRow: int) =
            let mutable total = 0.0
            let mutable count = 0

            for y in fromRow .. toRow - 1 do
                for x in 20 .. width - 21 do
                    let i = (y * width + x) * 4
                    total <- total + 0.2126 * float bytes.[i] + 0.7152 * float bytes.[i + 1] + 0.0722 * float bytes.[i + 2]
                    count <- count + 1

            total / float count

        // Rows 6-20 sit inside where the 38% gradient would reach; 80-110 sit
        // well outside it.
        let top = stripMean 6 20
        let bottom = stripMean 80 110

        Assert.True(
            abs (top - bottom) < 1.0,
            sprintf "top strip %.2f vs bottom strip %.2f — that difference is the white section" top bottom
        )
    finally
        BackdropCache.dispose ()

[<Fact>]
let ``an Inbox glass action keeps its whole outline, exactly as the Planner's does`` () =
    // The inset is the fix for the chewed button corners. Both screens take it,
    // so both must render the same outline.
    try
        let inbox, _, _ = renderInboxAction ()

        let planner, _, _ =
            renderGlassCore
                1.0
                UniversityOS.App.Features.PlannerLogic.Geometry.contentHighlight
                (float32 UniversityOS.App.Features.PlannerLogic.Geometry.borderInset)

        Assert.Equal<byte[]>(planner, inbox)
    finally
        BackdropCache.dispose ()

[<Fact>]
let ``the Inbox action outline really is inset, not merely declared to be`` () =
    // The same measurement as the Planner's: more surviving border ink inside
    // the panel once half the stroke is no longer clipped away.
    try
        let onBounds, width, height = renderGlassCore 1.0 0.0 0.0f
        let inset, _, _ = renderInboxAction ()

        let edgeInk (bytes: byte[]) =
            let luminance x y =
                let i = (y * width + x) * 4
                0.2126 * float bytes.[i] + 0.7152 * float bytes.[i + 1] + 0.0722 * float bytes.[i + 2]

            let reference = luminance (width / 2) (height / 2)
            let mutable total = 0.0

            for x in 0 .. width - 1 do
                total <- total + abs (luminance x 0 - reference) + abs (luminance x (height - 1) - reference)

            for y in 1 .. height - 2 do
                total <- total + abs (luminance 0 y - reference) + abs (luminance (width - 1) y - reference)

            total

        Assert.True(
            edgeInk inset > edgeInk onBounds,
            sprintf "inset kept %.1f of outline, on-bounds kept %.1f" (edgeInk inset) (edgeInk onBounds)
        )
    finally
        BackdropCache.dispose ()

// ---- Assignments, at the pixel level ---------------------------------------

[<Fact>]
let ``an Assignments content card is pixel-identical to one drawn with no specular pass`` () =
    // The strongest available statement of "there is no white sheen anywhere on
    // this screen": not a threshold, not a tolerance — the same bytes.
    try
        let card, _, _ = renderAssignmentsCard ()
        let flat, _, _ = renderWithoutHighlight ()
        Assert.Equal<byte[]>(flat, card)
    finally
        BackdropCache.dispose ()

[<Fact>]
let ``an Assignments card is the same surface as every approved screen's card`` () =
    // No new surface for a new screen: the same painter, the same treatment, the
    // same bytes as the three that are already approved.
    try
        let assignments, _, _ = renderAssignmentsCard ()
        let inbox, _, _ = renderInboxCard ()
        let planner, _, _ = renderPlannerCard ()
        let dashboard, _, _ = renderCard ()

        Assert.Equal<byte[]>(inbox, assignments)
        Assert.Equal<byte[]>(planner, assignments)
        Assert.Equal<byte[]>(dashboard, assignments)
    finally
        BackdropCache.dispose ()

[<Fact>]
let ``the Assignments card surface has no top luminance band`` () =
    try
        let bytes, width, _ = renderAssignmentsCard ()

        let stripMean (fromRow: int) (toRow: int) =
            let mutable total = 0.0
            let mutable count = 0

            for y in fromRow .. toRow - 1 do
                for x in 20 .. width - 21 do
                    let i = (y * width + x) * 4
                    total <- total + 0.2126 * float bytes.[i] + 0.7152 * float bytes.[i + 1] + 0.0722 * float bytes.[i + 2]
                    count <- count + 1

            total / float count

        // Rows 6-20 sit inside where the 38% gradient would reach; 80-110 sit
        // well outside it.
        let top = stripMean 6 20
        let bottom = stripMean 80 110

        Assert.True(
            abs (top - bottom) < 1.0,
            sprintf "top strip %.2f vs bottom strip %.2f — that difference is the white section" top bottom
        )
    finally
        BackdropCache.dispose ()

[<Fact>]
let ``an Assignments glass action keeps its whole outline, as the other screens' do`` () =
    // The inset is the fix for the chewed button corners. Every migrated screen
    // takes it, so every one must render the same outline.
    try
        let assignments, _, _ = renderAssignmentsAction ()
        let inbox, _, _ = renderInboxAction ()

        let planner, _, _ =
            renderGlassCore
                1.0
                UniversityOS.App.Features.PlannerLogic.Geometry.contentHighlight
                (float32 UniversityOS.App.Features.PlannerLogic.Geometry.borderInset)

        Assert.Equal<byte[]>(inbox, assignments)
        Assert.Equal<byte[]>(planner, assignments)
    finally
        BackdropCache.dispose ()

[<Fact>]
let ``a Dashboard card is pixel-identical to one drawn with no specular pass`` () =
    // The strongest available statement of "there is no sheen": not a threshold,
    // not a tolerance — the same bytes.
    try
        let card, _, _ = renderCard ()
        let flat, _, _ = renderWithoutHighlight ()
        Assert.Equal<byte[]>(flat, card)
    finally
        BackdropCache.dispose ()

[<Fact>]
let ``the card surface is uniform from top to bottom`` () =
    // Reads the rendered card directly: the top band and the bottom band must
    // be the same brightness. With the old 0.12 highlight the top was lighter,
    // which is exactly what was seen as a white section.
    try
        let bytes, width, height = renderCard ()

        // Mean luminance of one horizontal strip, away from the rounded corners
        // and the hairline border.
        let stripMean (fromRow: int) (toRow: int) =
            let mutable total = 0.0
            let mutable count = 0

            for y in fromRow .. toRow - 1 do
                for x in 20 .. width - 21 do
                    let i = (y * width + x) * 4
                    total <- total + 0.2126 * float bytes.[i] + 0.7152 * float bytes.[i + 1] + 0.0722 * float bytes.[i + 2]
                    count <- count + 1

            total / float count

        // The old gradient reached 38% down, so rows 6-20 sat inside it and
        // rows 80-110 sat well outside.
        let top = stripMean 6 20
        let bottom = stripMean 80 110

        Assert.True(
            abs (top - bottom) < 1.0,
            sprintf "top strip %.2f vs bottom strip %.2f — that difference is the white section" top bottom
        )
    finally
        BackdropCache.dispose ()

[<Fact>]
let ``a panel that keeps its highlight still shows one`` () =
    // The other direction. If the skip were unconditional, the floating sidebar
    // would have lost its approved lit-pane character and this suite would say
    // nothing about it.
    try
        let lit, width, _ = renderGlassWith 1.0 1.0
        let flat, _, _ = renderWithoutHighlight ()

        Assert.NotEqual<byte[]>(flat, lit)

        // …and specifically at the top, where the specular gradient lives.
        let topPixel (bytes: byte[]) =
            let i = (8 * width + 100) * 4
            0.2126 * float bytes.[i] + 0.7152 * float bytes.[i + 1] + 0.0722 * float bytes.[i + 2]

        Assert.True(topPixel lit > topPixel flat, "the shell's glass must still catch the light")
    finally
        BackdropCache.dispose ()

[<Fact>]
let ``the golden comparator rejects a real difference`` () =
    // A baseline suite that cannot fail is decoration. Prove the comparator
    // still has teeth, in both directions, without touching the committed files.
    let size = 32 * 32 * 4
    let baseline = Array.create size 120uy
    let nudged = baseline |> Array.map (fun b -> b + 2uy)
    let different = Array.create size 10uy

    let ok, _, _ = compare Tolerance.tight 32 32 baseline nudged
    Assert.True(ok, "a two-level channel shift must be tolerated")

    let stillOk, ratio, _ = compare Tolerance.tight 32 32 baseline different
    Assert.False(stillOk, "a wholly different image must be rejected")
    Assert.True(ratio > 0.9)

[<Fact>]
let ``every committed baseline is covered by a test`` () =
    // Guards the opposite failure: a baseline left behind after its test was
    // renamed or deleted looks like coverage and is not.
    let expected =
        [ for mode in [ "aurora"; "waves"; "liquid"; "folded" ] do
              for quality in [ "ultra"; "medium"; "low" ] do
                  sprintf "background-%s-tierB-%s" mode quality
          for point in [ "0"; "50"; "100" ] do
              sprintf "glass-panel-%s" point ]
        |> Set.ofList

    if Directory.Exists baselineDirectory then
        let committed =
            Directory.GetFiles(baselineDirectory, "*.png")
            |> Array.map Path.GetFileNameWithoutExtension
            |> Set.ofArray

        let orphaned = Set.difference committed expected

        Assert.True(
            orphaned.IsEmpty,
            sprintf "baseline files with no test: %s" (String.Join(", ", orphaned))
        )
