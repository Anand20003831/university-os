namespace UniversityOS.App.Rendering

open SkiaSharp
open UniversityOS.App.Design

/// Renders the background once per frame and derives the shared blurred
/// backdrop from that same pass.
///
/// The pipeline is: draw the background into a reduced-resolution offscreen
/// surface, upscale it to the screen with the background's own blur, then blur
/// it further into a second small surface and publish that for glass. Because
/// the glass blur is derived from a frame we already drew, glass costs one blur
/// per frame in total rather than one per panel.
///
/// OWNERSHIP: every native object here is created and disposed on the render
/// thread by this module. Surfaces are cached across frames and recreated only
/// when the required size changes, so a steady state allocates nothing.
[<RequireQualifiedAccess>]
module BackdropPipeline =

    let mutable private sceneSurface: SKSurface = null
    let mutable private sceneW = 0
    let mutable private sceneH = 0

    let mutable private blurSurface: SKSurface = null
    let mutable private blurW = 0
    let mutable private blurH = 0

    let mutable private screenPaint: SKPaint = null
    let mutable private blurPaint: SKPaint = null

    let private disposeScene () =
        if not (isNull sceneSurface) then
            sceneSurface.Dispose()
            sceneSurface <- null

        sceneW <- 0
        sceneH <- 0

    let private disposeBlur () =
        if not (isNull blurSurface) then
            blurSurface.Dispose()
            blurSurface <- null

        blurW <- 0
        blurH <- 0

    /// Creates a GPU-resident surface when a context is available, otherwise a
    /// raster one. Returns null on failure — callers must handle that rather
    /// than assume success (see docs §22, render-target creation failure).
    let private createSurface (grContext: GRContext) (w: int) (h: int) =
        try
            let info = SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul)

            if isNull (box grContext) then
                SKSurface.Create info
            else
                SKSurface.Create(grContext, false, info)
        with ex ->
            Capabilities.reportFailure "render-target-create" ex
            null

    let private ensureScene grContext w h =
        if isNull sceneSurface || sceneW <> w || sceneH <> h then
            disposeScene ()
            sceneSurface <- createSurface grContext w h

            if not (isNull sceneSurface) then
                sceneW <- w
                sceneH <- h

        not (isNull sceneSurface)

    let private ensureBlur grContext w h =
        if isNull blurSurface || blurW <> w || blurH <> h then
            disposeBlur ()
            blurSurface <- createSurface grContext w h

            if not (isNull blurSurface) then
                blurW <- w
                blurH <- h

        not (isNull blurSurface)

    let private ensurePaints () =
        if isNull screenPaint then
            screenPaint <- new SKPaint(IsAntialias = true)

        if isNull blurPaint then
            blurPaint <- new SKPaint(IsAntialias = true)

    /// Draws the background for this frame and publishes the glass backdrop.
    /// RENDER THREAD ONLY.
    let render (args: SkiaPaintArgs) (s: BackgroundSnapshot) =
        // Frame boundary: this is the one safe point to free render-thread-owned
        // natives, so any release the monitor asked for happens here rather than
        // underneath a draw already in progress.
        MemoryPressure.applyPending args.GrContext
        BackdropCache.applyPendingClear ()

        if s.Mode = NoBackground || s.Alpha <= 0.001f || s.Width < 1.0f || s.Height < 1.0f then
            // Nothing behind the glass this frame; drop any stale backdrop so
            // panels fall back to a plain tint rather than showing an old image.
            Diagnostics.count Diagnostics.Names.backdropSkipped
            BackdropCache.requestClear ()
            BackdropCache.applyPendingClear ()
        else
            // Measured unconditionally: adaptive quality depends on this even
            // when the diagnostics overlay is off. Two timestamp reads a frame.
            let hasGpu = not (isNull (box args.GrContext))
            let renderStart = System.Diagnostics.Stopwatch.GetTimestamp()
            let started = Diagnostics.startTimer ()
            ensurePaints ()

            let divisor = QualityLevel.backdropDivisor s.Quality

            // Overscan, rounded up to a whole pixel of the reduced-resolution
            // offscreen surface so it can never round away to nothing at low
            // quality. This is the entire fix for the parallax edge: the scene
            // is rendered wider and taller than the viewport, and the viewport
            // is a window onto it.
            let ox = float32 (Parallax.quantise divisor (float s.OverscanX))
            let oy = float32 (Parallax.quantise divisor (float s.OverscanY))

            let extentW = s.Width + 2.0f * ox
            let extentH = s.Height + 2.0f * oy

            let ow = max 1 (int (ceil (extentW / float32 divisor)))
            let oh = max 1 (int (ceil (extentH / float32 divisor)))

            if not (ensureScene args.GrContext ow oh) then
                // Offscreen unavailable: draw straight to the screen so the
                // background still appears, and skip the glass backdrop.
                Capabilities.reportFailure "backdrop-scene-surface" (exn "surface creation returned null")
                BackgroundPainter.paintContent args.Canvas args.Bounds hasGpu s
                BackdropCache.requestClear ()
                BackdropCache.applyPendingClear ()
            else
                let sceneCanvas = sceneSurface.Canvas
                sceneCanvas.Clear SKColors.Transparent
                sceneCanvas.Save() |> ignore

                // Logical space with the VIEWPORT origin at (0,0): the content
                // rect is exactly what every mode was authored against, and the
                // extent runs from -overscan to size+overscan around it.
                sceneCanvas.Scale(float32 ow / extentW, float32 oh / extentH)
                sceneCanvas.Translate(ox, oy)

                let content = SKRect(0.0f, 0.0f, s.Width, s.Height)
                let extent = SKRect(-ox, -oy, s.Width + ox, s.Height + oy)

                BackgroundPainter.paintRegion sceneCanvas content extent hasGpu s
                sceneCanvas.Restore()

                Diagnostics.count Diagnostics.Names.backdropGenerated
                use sceneImage = sceneSurface.Snapshot()

                // --- to screen, with the background's own blur and parallax ---
                //
                // The image covers the overscanned extent, so it is placed at
                // -overscan and then displaced. The control clips to its bounds,
                // which is the "crop back to the viewport" half of the contract.
                // Coverage holds as long as |parallax| + blur reach <= overscan,
                // which Parallax.allowedDisplacement guarantees at the source.
                let dest =
                    SKRect(
                        args.Bounds.Left - ox + s.ParallaxX,
                        args.Bounds.Top - oy + s.ParallaxY,
                        args.Bounds.Left - ox + s.ParallaxX + extentW,
                        args.Bounds.Top - oy + s.ParallaxY + extentH
                    )

                let screenSigma = s.BlurPx / float32 divisor

                if screenSigma > 0.2f then
                    let filter = SKImageFilter.CreateBlur(screenSigma, screenSigma)
                    screenPaint.ImageFilter <- filter
                    args.Canvas.DrawImage(sceneImage, dest, screenPaint)
                    screenPaint.ImageFilter <- null
                    filter.Dispose()
                else
                    args.Canvas.DrawImage(sceneImage, dest, screenPaint)

                // --- glass backdrop: one further blur, shared by every panel ---
                let blurStarted = Diagnostics.startTimer ()

                // The backdrop glass samples is VIEWPORT-shaped and window-space
                // aligned, so the overscanned scene is placed into it with the
                // same offset and parallax the screen got. Get this wrong and
                // every glass panel samples a shifted region of the background.
                let bw = max 1 (int (ceil (s.Width / float32 divisor)))
                let bh = max 1 (int (ceil (s.Height / float32 divisor)))

                if ensureBlur args.GrContext bw bh then
                    let bc = blurSurface.Canvas
                    bc.Clear SKColors.Transparent

                    let bsx = float32 bw / max 1.0f s.Width
                    let bsy = float32 bh / max 1.0f s.Height

                    // Glass blur radius now follows the Glass setting, in
                    // logical px, scaled into the reduced space so the perceived
                    // radius is resolution-independent.
                    let glassSigmaPx = float32 (Glass.backdropSigma (float s.GlassScale))
                    let glassSigmaX = max 2.0f (glassSigmaPx * bsx)
                    let glassSigmaY = max 2.0f (glassSigmaPx * bsy)
                    let filter = SKImageFilter.CreateBlur(glassSigmaX, glassSigmaY)
                    blurPaint.ImageFilter <- filter

                    bc.DrawImage(
                        sceneImage,
                        SKRect(
                            (-ox + s.ParallaxX) * bsx,
                            (-oy + s.ParallaxY) * bsy,
                            (-ox + s.ParallaxX + extentW) * bsx,
                            (-oy + s.ParallaxY + extentH) * bsy
                        ),
                        blurPaint
                    )

                    blurPaint.ImageFilter <- null
                    filter.Dispose()

                    // Ownership transfers to the cache, which disposes the previous.
                    Diagnostics.count Diagnostics.Names.backdropBlur
                    BackdropCache.publish (blurSurface.Snapshot()) s.Width s.Height s.Generation
                else
                    BackdropCache.requestClear ()
                    BackdropCache.applyPendingClear ()

                Diagnostics.stopTimer Diagnostics.blur blurStarted
                Diagnostics.stopTimer Diagnostics.render started

                // Feed the real measured cost back into adaptive quality.
                let elapsedMs =
                    float (System.Diagnostics.Stopwatch.GetTimestamp() - renderStart) * 1000.0
                    / float System.Diagnostics.Stopwatch.Frequency

                // Budget tracks the display's real frame interval, not a fixed
                // 60Hz assumption.
                QualityController.observe elapsedMs Diagnostics.frame.Ewma

                // GPU memory is only read when the overlay is on: it is a
                // driver round-trip, not something to do on every frame.
                if Diagnostics.isEnabled () && not (isNull (box args.GrContext)) then
                    let mutable resources = 0
                    let mutable bytes = 0L
                    args.GrContext.GetResourceCacheUsage(&resources, &bytes)
                    Diagnostics.reportGpuMemory bytes resources (args.GrContext.GetResourceCacheLimit())

    /// Releases every surface and paint. Called at shutdown and under memory
    /// pressure; everything here rebuilds automatically on the next frame.
    let dispose () =
        disposeScene ()
        disposeBlur ()

        if not (isNull screenPaint) then
            screenPaint.Dispose()
            screenPaint <- null

        if not (isNull blurPaint) then
            blurPaint.Dispose()
            blurPaint <- null
