namespace UniversityOS.App.Rendering

open System
open SkiaSharp
open UniversityOS.App.Design

/// Immutable per-frame background parameters. Built on the UI thread, consumed
/// on the render thread. Structural equality means an unchanged frame is skipped
/// entirely, which is what makes a stopped background free.
///
/// Deliberately excludes any frame counter: when the clock stops, the snapshot
/// stops changing and redraws short-circuit.
type BackgroundSnapshot =
    { Mode: BackgroundMode
      /// Accumulated, speed-scaled animation seconds.
      Time: float32
      Accent: SKColor
      /// Lighter tint of the accent — luminous cores and highlights.
      AccentLight: SKColor
      /// Deeper shade — valleys, depth and the dark end of gradients.
      AccentDeep: SKColor
      /// Analogous hue (accent rotated ~30°), so compositions have a second
      /// related colour instead of one hue at varying brightness.
      AccentAlt: SKColor
      /// Final composite opacity: caps, sliders and adaptive dimming applied.
      Alpha: float32
      /// Eased intensity (0–2), weighting internal contrast.
      Intensity: float32
      /// Blur applied to the whole background layer.
      BlurPx: float32
      ParallaxX: float32
      ParallaxY: float32
      /// Extra logical px rendered beyond each viewport edge, so the parallax
      /// offset and the blur's reach can never uncover a viewport pixel.
      /// See Design/Parallax.fs.
      OverscanX: float32
      OverscanY: float32
      /// Tokens.GlassScale, carried so the one shared backdrop blur responds to
      /// the Glass setting. Panels read the same scale for tint and border.
      GlassScale: float32
      Width: float32
      Height: float32
      Quality: QualityLevel
      Tier: RenderTier
      Generation: int }

/// Draws the background content itself. Given a canvas, it paints — it does not
/// know about offscreen surfaces, the backdrop cache or controls. That keeps it
/// directly testable: render into a raster surface with a fixed Time and the
/// output is byte-reproducible.
[<RequireQualifiedAccess>]
module BackgroundPainter =

    // Reused paints, owned by this module and released by `dispose`. Creating
    // these per frame would allocate on the render thread.
    let mutable private shaderPaint: SKPaint = null
    let mutable private fillPaint: SKPaint = null

    /// One uniforms block per shader, reused for the life of the process.
    ///
    /// Measured: building a fresh SKRuntimeEffectUniforms every frame costs
    /// ~842 bytes/frame; reusing one and only rebuilding the shader costs ~208.
    /// The remaining allocation is the SKShader itself, which `ToShader` must
    /// create because it captures the uniform values at construction — there is
    /// no mutate-in-place shader in SkiaSharp 2.88.9.
    ///
    /// Render-thread-owned: only tryPaintShader touches this dictionary.
    let private uniformCache =
        System.Collections.Generic.Dictionary<BackgroundMode, SKRuntimeEffectUniforms>()

    let private uniformsFor (mode: BackgroundMode) (effect: SKRuntimeEffect) =
        match uniformCache.TryGetValue mode with
        | true, existing -> existing
        | _ ->
            let created = SKRuntimeEffectUniforms effect
            uniformCache[mode] <- created
            Diagnostics.countAlways "uniforms.created"
            created

    let private ensurePaints () =
        if isNull shaderPaint then
            shaderPaint <- new SKPaint(IsAntialias = false)
            Diagnostics.countAlways Diagnostics.Names.painterCreated

        if isNull fillPaint then
            fillPaint <- new SKPaint(IsAntialias = true)
            Diagnostics.countAlways Diagnostics.Names.painterCreated

    /// Resets every mutable field of the shared fill paint.
    ///
    /// The paint is reused across frames to avoid allocating, which means one
    /// painter can leave state another inherits — Aurora sets a round stroke
    /// cap, Folded sets a stroke width. That made output depend on which mode
    /// rendered previously and broke determinism. Painters now start clean.
    let private resetFillPaint () =
        fillPaint.Shader <- null
        fillPaint.MaskFilter <- null
        fillPaint.Style <- SKPaintStyle.Fill
        fillPaint.StrokeWidth <- 0.0f
        fillPaint.StrokeCap <- SKStrokeCap.Butt
        fillPaint.Color <- SKColors.White

    /// Linear blend between two colours.
    let private mixColour (a: SKColor) (b: SKColor) (amount: float32) =
        let f (x: byte) (y: byte) = byte (float32 x + (float32 y - float32 x) * amount)
        SKColor(f a.Red b.Red, f a.Green b.Green, f a.Blue b.Blue)

    /// Deterministic hash matching the shaders' hash1, so Tier A and Tier B
    /// place their elements identically.
    let private hash1 (n: float32) =
        let v = sin (float n) * 43758.5453123
        float32 (v - floor v)

    // ---- Tier A: GPU shader ------------------------------------------------

    /// Every painter takes two rectangles.
    ///
    /// `content` is the viewport: it fixes the SCALE of the composition, and is
    /// the rectangle every mode was authored against. `extent` is the region
    /// actually painted — content grown by the overscan. Keeping them separate
    /// is what lets the background be rendered larger without the approved
    /// compositions changing size: the field simply continues past the edge.
    let private tryPaintShader (canvas: SKCanvas) (content: SKRect) (extent: SKRect) (s: BackgroundSnapshot) =
        match ShaderLibrary.tryGet s.Mode with
        | None -> false
        | Some effect ->
            try
                let started = Diagnostics.startTimer ()
                ensurePaints ()

                // SKRuntimeEffectUniform has no public constructor: values go in
                // through implicit conversion operators, which F# does not apply
                // automatically. These must stay as direct calls — wrapping them
                // in a helper function makes F# try to form a first-class
                // function over the Span overloads, which CIL forbids.
                // uRes stays the VIEWPORT size even though the draw covers the
                // overscanned extent: the shader normalises fragCoord by uRes,
                // so the composition keeps its scale and simply continues past
                // uv 0..1 into the overscan.
                let res: float32[] = [| content.Width; content.Height |]
                let accent: float32[] = SkiaInterop.rgbFloats s.Accent
                let accentLight: float32[] = SkiaInterop.rgbFloats s.AccentLight
                let accentDeep: float32[] = SkiaInterop.rgbFloats s.AccentDeep
                let accentAlt: float32[] = SkiaInterop.rgbFloats s.AccentAlt

                // Every assignment is guarded by Contains. SkSL strips uniforms a
                // program does not reference, and writing a stripped name walks
                // past the uniform buffer — which crashes the process natively
                // (0xC000001D inside sk_canvas_draw_rect), not with an exception
                // we could catch. Not every shader uses every accent slot.
                let uniforms = uniformsFor s.Mode effect

                if uniforms.Contains "uRes" then
                    uniforms["uRes"] <- SKRuntimeEffectUniform.op_Implicit res

                if uniforms.Contains "uTime" then
                    uniforms["uTime"] <- SKRuntimeEffectUniform.op_Implicit s.Time

                if uniforms.Contains "uAccent" then
                    uniforms["uAccent"] <- SKRuntimeEffectUniform.op_Implicit accent

                if uniforms.Contains "uAccentLight" then
                    uniforms["uAccentLight"] <- SKRuntimeEffectUniform.op_Implicit accentLight

                if uniforms.Contains "uAccentDeep" then
                    uniforms["uAccentDeep"] <- SKRuntimeEffectUniform.op_Implicit accentDeep

                if uniforms.Contains "uAccentAlt" then
                    uniforms["uAccentAlt"] <- SKRuntimeEffectUniform.op_Implicit accentAlt

                if uniforms.Contains "uIntensity" then
                    uniforms["uIntensity"] <- SKRuntimeEffectUniform.op_Implicit s.Intensity

                if uniforms.Contains "uAlpha" then
                    uniforms["uAlpha"] <- SKRuntimeEffectUniform.op_Implicit s.Alpha

                use shader = effect.ToShader(true, uniforms)

                if isNull (box shader) then
                    false
                else
                    shaderPaint.Shader <- shader
                    canvas.DrawRect(extent, shaderPaint)
                    shaderPaint.Shader <- null
                    Diagnostics.stopTimer Diagnostics.shader started
                    true
            with ex ->
                // One failure is enough: stop retrying this mode at frame rate.
                ShaderLibrary.markFailed s.Mode
                Capabilities.reportFailure (sprintf "shader-%A" s.Mode) ex
                false

    // ---- Tier B: Skia draw calls -------------------------------------------

    // ---- Aurora contour field (shared by Tier A intent and Tier B) ----------

    /// The same scalar field the Tier A shader evaluates, so both tiers show the
    /// same composition. Domain-warped sinusoids: organic curvature, contours
    /// that bunch on steep slopes and spread on shallow ones.
    let private auroraField (px: float32) (py: float32) (t: float32) =
        let f = float
        let qx0 = f px + 0.42 * sin (f py * 0.85 + f t * 0.060)
        let qy0 = f py + 0.42 * cos (f px * 0.70 - f t * 0.050)
        let qx = qx0 + 0.20 * sin (qy0 * 1.35 - f t * 0.043)
        let qy = qy0 + 0.20 * cos (qx0 * 1.10 + f t * 0.037)

        sin (qx * 0.62 + qy * 0.22 + f t * 0.031)
        + 0.70 * sin (qy * 0.78 - qx * 0.16 - f t * 0.026)
        + 0.34 * sin ((qx + qy) * 0.50 + f t * 0.019)

    /// Tier B renders the contour field on the CPU at reduced resolution and
    /// caches the bitmap, regenerating only when the field has moved
    /// meaningfully. Evaluating it per screen pixel every frame would be far too
    /// expensive; the motion is slow by design, so a coarse field refreshed a
    /// few times a second is visually equivalent.
    let mutable private auroraBitmap: SKBitmap = null
    let mutable private auroraKey = (0, 0, 0.0f, 0u, 0.0f, 0.0f)

    let private auroraLowResHeight = 220

    /// `wBase` is the bitmap width that covers exactly the viewport; `obX`/`obY`
    /// are the overscan in WHOLE BITMAP PIXELS on each side.
    ///
    /// Snapping the overscan to whole bitmap pixels — rather than scaling the
    /// bitmap to whatever the overscan happens to be — is what keeps the field's
    /// sampling grid identical with and without overscan. Any other choice
    /// resamples the cached field slightly and the composition shimmers as the
    /// blur setting changes the overscan.
    let private ensureAuroraBitmap (s: BackgroundSnapshot) (wBase: int) (obX: int) (obY: int) =
        let hBase = auroraLowResHeight
        let aspect = float (s.Width / max 1.0f s.Height)
        let w = wBase + 2 * obX
        let h = hBase + 2 * obY

        // Quantise time so the bitmap is rebuilt a few times a second, not
        // every frame.
        let timeKey = floor (s.Time * 4.0f) / 4.0f
        let colourKey = uint32 s.Accent

        let key = (w, h, timeKey, colourKey, float32 obX, float32 obY)

        if isNull auroraBitmap || auroraKey <> key then
            if isNull auroraBitmap || auroraBitmap.Width <> w || auroraBitmap.Height <> h then
                if not (isNull auroraBitmap) then auroraBitmap.Dispose()
                auroraBitmap <- new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul)

            auroraKey <- key

            let density = 13.0
            // 1.5 bitmap pixels in field units. Constant, because the density is.
            let pe = float32 (1.5 * 3.4 / float hBase)

            // Sweep parameters — loop-invariant, so they are computed once per
            // bitmap rather than per pixel. Same maths as the Tier A shader.
            let t = float s.Time
            let dirX = 0.52 + 0.20 * sin (t * 0.013)
            let dirY = 0.72 + 0.16 * cos (t * 0.011)
            let dirLen = max 1e-5 (sqrt (dirX * dirX + dirY * dirY))
            let nx = dirX / dirLen
            let ny = dirY / dirLen
            let centre = 0.72 + 0.24 * sin (t * 0.021)

            let dim = mixColour (SKColor(uint32 s.AccentDeep)) s.Accent 0.34f
            let baseCol = s.AccentDeep

            for y in 0 .. h - 1 do
                for x in 0 .. w - 1 do
                    // Normalised VIEWPORT coordinates: exactly 0..1 across the
                    // viewport, negative or >1 across the overscan band. Pixel
                    // (obX, obY) is u=0, v=0 whatever the overscan.
                    let u = float (x - obX) / float wBase
                    let v = float (y - obY) / float hBase
                    let px = float32 (u * aspect * 3.4)
                    let py = float32 (v * 3.4)

                    let f0 = auroraField px py s.Time
                    let fx = auroraField (px + pe) py s.Time
                    let fy = auroraField px (py + pe) s.Time

                    let sv = f0 * density
                    let gx = (fx * density - sv) / 1.5
                    let gy = (fy * density - sv) / 1.5
                    let g = max (sqrt (gx * gx + gy * gy)) 1e-5

                    let idx = floor (sv + 0.5)
                    let dpx = abs (sv - idx) / g

                    let m5 = idx - 4.0 * floor (idx / 4.0)
                    let m12 = idx - 7.0 * floor (idx / 7.0)
                    let isSecondary = m5 < 0.5
                    let isRidge = m12 < 0.5

                    let width =
                        0.85 + (if isSecondary then 0.22 else 0.0) + (if isRidge then 0.45 else 0.0)

                    let edge0 = width - 0.45
                    let edge1 = width + 0.55

                    let tsm = max 0.0 (min 1.0 ((dpx - edge0) / (edge1 - edge0)))
                    let smooth = tsm * tsm * (3.0 - 2.0 * tsm)
                    let line = (1.0 - smooth) * (0.70 + 0.30 * sin (idx * 2.399 + 1.7))

                    let lineColour =
                        if isRidge then s.AccentLight
                        elif isSecondary then s.AccentAlt
                        else dim

                    // The travelling dense/calm boundary, floored so linework is
                    // never invisible anywhere. See the Tier A shader for why
                    // the drift moves the boundary rather than the mask level.
                    let dn =
                        (float u * nx + float v * ny) / (nx + ny)
                        + 0.11 * sin (float v * 3.1 + t * 0.037)
                        + 0.08 * sin (float u * 2.4 - t * 0.029)

                    let mt = max 0.0 (min 1.0 ((dn - (centre - 0.42)) / 0.76))
                    let sweep = 1.0 - mt * mt * (3.0 - 2.0 * mt)
                    let mask = 0.38 + 0.62 * sweep
                    let la = float32 (max 0.0 (min 1.0 (line * mask)))
                    let baseA = float32 (0.22 * (0.35 + 0.65 * mask))

                    let blend (b: byte) (l: byte) =
                        byte (float32 b * 0.30f * (1.0f - la) + float32 l * la)

                    let alpha = baseA * (1.0f - la) + la

                    auroraBitmap.SetPixel(
                        x,
                        y,
                        SKColor(
                            blend baseCol.Red lineColour.Red,
                            blend baseCol.Green lineColour.Green,
                            blend baseCol.Blue lineColour.Blue,
                            byte (max 0.0f (min 255.0f (alpha * 255.0f)))
                        )
                    )

        auroraBitmap

    /// Normalised position of the extent's edges within the content rect. 0..1
    /// with no overscan; a little outside it when there is.
    let private spanOf (content: SKRect) (extent: SKRect) =
        let cw = max 1e-4f content.Width
        let ch = max 1e-4f content.Height

        float ((extent.Left - content.Left) / cw),
        float ((extent.Right - content.Left) / cw),
        float ((extent.Top - content.Top) / ch),
        float ((extent.Bottom - content.Top) / ch)

    /// Aurora, Tier B: the cached low-resolution contour field, scaled up.
    let private paintAurora (canvas: SKCanvas) (content: SKRect) (extent: SKRect) (s: BackgroundSnapshot) =
        ensurePaints ()
        resetFillPaint ()

        let cw = max 1e-4f content.Width
        let ch = max 1e-4f content.Height
        let hBase = auroraLowResHeight
        let wBase = max 1 (int (round (float hBase * float (cw / ch))))

        // Overscan rounded UP to whole bitmap pixels, and symmetric, so the
        // destination scale stays exactly cw/wBase per bitmap pixel. Rounding up
        // means the painted area is never smaller than the extent asked for.
        let needX = max 0.0f (max (content.Left - extent.Left) (extent.Right - content.Right))
        let needY = max 0.0f (max (content.Top - extent.Top) (extent.Bottom - content.Bottom))
        let obX = int (ceil (float (needX / cw) * float wBase))
        let obY = int (ceil (float (needY / ch) * float hBase))

        let bmp = ensureAuroraBitmap s wBase obX obY
        let padX = float32 obX / float32 wBase * cw
        let padY = float32 obY / float32 hBase * ch

        let dest =
            SKRect(content.Left - padX, content.Top - padY, content.Right + padX, content.Bottom + padY)

        fillPaint.Color <- SkiaInterop.withAlphaF s.Alpha SKColors.White
        canvas.DrawBitmap(bmp, dest, fillPaint)

    let private paintWaves (canvas: SKCanvas) (content: SKRect) (extent: SKRect) (s: BackgroundSnapshot) =
        ensurePaints ()
        resetFillPaint ()
        // Scale comes from the viewport; the span comes from the extent, so the
        // bands keep their authored size and simply run further off-screen.
        let w = content.Width
        let h = content.Height
        let u0, u1, _, _ = spanOf content extent
        let t = s.Time

        // baseY, amp1, per1, ph1, amp2, per2, ph2, slide, weight, colour
        let layers =
            [| 0.18f, 0.050f, 4.0f, 0.2f, 0.020f, 8.0f, 1.1f, 0.010f, 0.30f, SKColor(120uy, 150uy, 168uy)
               0.32f, 0.038f, 6.0f, 1.4f, 0.022f, 10.0f, 0.3f, 0.014f, 0.16f, SKColor(242uy, 236uy, 226uy)
               0.45f, 0.062f, 4.0f, 2.1f, 0.017f, 12.0f, 0.9f, 0.017f, 0.24f, s.Accent
               0.58f, 0.042f, 6.0f, 0.6f, 0.025f, 8.0f, 2.4f, 0.011f, 0.20f, SKColor(111uy, 160uy, 184uy)
               0.71f, 0.055f, 4.0f, 1.2f, 0.015f, 10.0f, 0.5f, 0.015f, 0.12f, SKColor(242uy, 236uy, 226uy)
               0.84f, 0.038f, 6.0f, 2.8f, 0.027f, 8.0f, 0.1f, 0.010f, 0.30f, SKColor(90uy, 124uy, 146uy) |]

        let steps = if QualityLevel.detail s.Quality < 1.0 then 48 else 96

        // Same number of segments per viewport width, so the overscan adds
        // resolution rather than stretching it.
        let spanSteps = max 2 (int (float steps * (u1 - u0)))

        for (baseY, a1, p1, ph1, a2, p2, ph2, slide, weight, colour) in layers do
            use path = new SKPath()
            path.MoveTo(extent.Left, extent.Bottom)

            for i in 0..spanSteps do
                let fx = float32 (u0 + (u1 - u0) * (float i / float spanSteps))
                let x = fx + t * slide

                let y =
                    baseY
                    + a1 * float32 (sin (float (x * p1 * 6.28318f + ph1)))
                    + a2 * float32 (sin (float (x * p2 * 6.28318f + ph2)))
                    + 0.012f * float32 (sin (float (t * 0.25f + ph1)))

                path.LineTo(content.Left + fx * w, content.Top + y * h)

            path.LineTo(extent.Right, extent.Bottom)
            path.Close()

            fillPaint.Shader <- null
            fillPaint.Color <- SkiaInterop.withAlphaF (s.Alpha * weight) colour
            canvas.DrawPath(path, fillPaint)

    /// Liquid Glass needs no extent handling of its own: the blobs are placed in
    /// viewport space with radii of 13–28% of the viewport, so they already
    /// spill far past every edge. Giving it a larger surface is all it takes for
    /// the overscan band to be covered by the same blobs that cover the edge.
    let private paintLiquid (canvas: SKCanvas) (content: SKRect) (s: BackgroundSnapshot) =
        ensurePaints ()
        resetFillPaint ()
        let rect = content
        let w = rect.Width
        let h = rect.Height
        let t = s.Time
        let count = if QualityLevel.detail s.Quality < 1.0 then 8 else 14

        let blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 26.0f)
        fillPaint.MaskFilter <- blur

        let frost = SKColor(242uy, 236uy, 226uy)

        let mix (a: SKColor) (b: SKColor) (amount: float32) =
            let f x y = byte (float32 x + (float32 y - float32 x) * amount)
            SKColor(f a.Red b.Red, f a.Green b.Green, f a.Blue b.Blue)

        for i in 0 .. count - 1 do
            let fi = float32 i + 1.0f

            // Jittered 5x3 grid, matching the shader: guarantees the accent is
            // visible across the whole viewport rather than clustered wherever
            // the hash placed the few accent blobs.
            let gx = float32 ((i + 1) % 5)
            let gy = float32 ((i + 1) / 5)
            let cellX = (gx + 0.5f) / 5.0f
            let cellY = (gy + 0.5f) / 3.0f
            let jx = (hash1 (fi * 3.1f) - 0.5f) * 0.26f
            let jy = (hash1 (fi * 7.7f) - 0.5f) * 0.30f

            let sr = 0.13f + 0.15f * hash1 (fi * 11.3f)
            let sp = 0.04f + 0.06f * hash1 (fi * 5.9f)

            let cx = cellX + jx + float32 (sin (float (t * sp + fi))) * 0.05f
            let cy = cellY + jy + float32 (cos (float (t * sp * 0.9f + fi * 1.7f))) * 0.045f
            let centre = SKPoint(rect.Left + cx * w, rect.Top + cy * h)
            let radius = sr * (max w h)

            // Four accent-derived classes cycled by index. Even the neutral
            // class carries a tint, so no blob renders grey.
            let baseColour =
                match (i + 1) % 4 with
                | 0 -> s.Accent
                | 1 -> mix frost s.Accent 0.55f
                | 2 -> s.AccentAlt
                | _ -> mix frost s.Accent 0.14f

            use shader =
                SKShader.CreateRadialGradient(
                    centre,
                    radius,
                    [| SkiaInterop.withAlphaF (s.Alpha * 0.9f) baseColour; SKColors.Transparent |],
                    [| 0.0f; 1.0f |],
                    SKShaderTileMode.Clamp
                )

            fillPaint.Shader <- shader
            canvas.DrawCircle(centre, radius, fillPaint)
            fillPaint.Shader <- null

        fillPaint.MaskFilter <- null
        blur.Dispose()

    /// Folded Glass, Tier B: three broad half-plane sheets whose edges are the
    /// same fold ridges the shader uses, each filled with a linear gradient and
    /// finished with a soft highlight along the crease.
    ///
    /// The previous version drew a per-cell triangle lattice with independent
    /// hash colours, which read as low-poly wallpaper. Here the planes are large
    /// and connected, and their shading relates across each ridge.
    let private paintFolded (canvas: SKCanvas) (content: SKRect) (extent: SKRect) (s: BackgroundSnapshot) =
        ensurePaints ()
        resetFillPaint ()
        // Fold geometry is authored in viewport space; only the filled area and
        // the clip grow with the overscan.
        let rect = content
        let w = rect.Width
        let h = rect.Height
        let t = s.Time
        let sheet = SKColor(242uy, 236uy, 226uy)

        fillPaint.Shader <- null
        fillPaint.MaskFilter <- null
        fillPaint.Style <- SKPaintStyle.Fill

        // Fold ridges in normalised space: (direction, offset). Matches the
        // shader's d1/d2/d3 so both tiers compose the same way.
        let folds =
            [| (0.94f, 0.34f), 0.62f + 0.035f * float32 (sin (float (t * 0.045f))), s.Accent, 0.62f
               (-0.42f, 0.91f), 0.74f + 0.030f * float32 (sin (float (t * 0.037f + 1.9f))), s.AccentAlt, 0.48f
               (0.62f, -0.78f), -0.34f + 0.028f * float32 (sin (float (t * 0.029f + 3.4f))), s.AccentDeep, 0.40f |]

        // Base wash so the sheet reads as continuous material behind the folds.
        use baseShader =
            SKShader.CreateLinearGradient(
                SKPoint(rect.Left, rect.Top),
                SKPoint(rect.Right, rect.Bottom),
                [| SkiaInterop.withAlphaF (s.Alpha * 0.30f) sheet
                   SkiaInterop.withAlphaF (s.Alpha * 0.14f) s.AccentDeep |],
                [| 0.0f; 1.0f |],
                SKShaderTileMode.Clamp
            )

        fillPaint.Shader <- baseShader
        canvas.DrawRect(extent, fillPaint)
        fillPaint.Shader <- null

        for ((dx, dy), offset, colour, weight) in folds do
            // The half-plane dot(uv,d) > offset, clipped to the viewport, is a
            // large connected polygon — one face of the fold.
            let far = 4.0f
            let px = dx * offset
            let py = dy * offset
            let tx = -dy
            let ty = dx

            use path = new SKPath()
            path.MoveTo(rect.Left + (px + tx * far) * w, rect.Top + (py + ty * far) * h)
            path.LineTo(rect.Left + (px - tx * far) * w, rect.Top + (py - ty * far) * h)
            path.LineTo(rect.Left + (px - tx * far + dx * far) * w, rect.Top + (py - ty * far + dy * far) * h)
            path.LineTo(rect.Left + (px + tx * far + dx * far) * w, rect.Top + (py + ty * far + dy * far) * h)
            path.Close()

            // Gradient across the plane gives depth rather than a flat fill.
            use planeShader =
                SKShader.CreateLinearGradient(
                    SKPoint(rect.Left + px * w, rect.Top + py * h),
                    SKPoint(rect.Left + (px + dx) * w, rect.Top + (py + dy) * h),
                    [| SkiaInterop.withAlphaF (s.Alpha * weight * 0.55f) colour
                       SkiaInterop.withAlphaF (s.Alpha * weight * 0.10f) colour |],
                    [| 0.0f; 1.0f |],
                    SKShaderTileMode.Clamp
                )

            canvas.Save() |> ignore
            canvas.ClipRect extent
            fillPaint.Shader <- planeShader
            canvas.DrawPath(path, fillPaint)
            fillPaint.Shader <- null
            canvas.Restore()

            // Fine highlight along the ridge itself.
            let ridgeBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 0.012f * h)
            fillPaint.MaskFilter <- ridgeBlur
            fillPaint.Style <- SKPaintStyle.Stroke
            fillPaint.StrokeWidth <- 0.006f * h
            fillPaint.Color <- SkiaInterop.withAlphaF (s.Alpha * weight * 0.85f) s.AccentLight

            canvas.DrawLine(
                SKPoint(rect.Left + (px + tx * far) * w, rect.Top + (py + ty * far) * h),
                SKPoint(rect.Left + (px - tx * far) * w, rect.Top + (py - ty * far) * h),
                fillPaint
            )

            fillPaint.MaskFilter <- null
            ridgeBlur.Dispose()
            fillPaint.Style <- SKPaintStyle.Fill
            fillPaint.StrokeWidth <- 0.0f

    // ---- public surface ----------------------------------------------------

    /// Paints the background content into the given canvas at the given rect.
    ///
    /// Tier A is attempted first when the active tier allows it; any failure
    /// falls through to Tier B rather than leaving the frame blank. Tier C is
    /// handled by the control, which never reaches this function.
    /// `hasGpuContext` is a hard safety gate, not an optimisation.
    ///
    /// Drawing ANY SkSL runtime effect onto a CPU raster surface kills the
    /// process on SkiaSharp 2.88.9 with 0xC000001D (illegal instruction) — even
    /// `half4 main(){ return half4(1,0,0,1); }`. That is a native fault, so the
    /// try/catch at the render boundary cannot contain it. Tier A must therefore
    /// never be attempted without a GPU context, and this is enforced here at
    /// the point of use rather than relying solely on tier probing.
    /// `content` is the viewport rectangle and fixes the composition's scale;
    /// `extent` is content grown by the overscan and is the area actually
    /// filled. With no overscan the two are the same rectangle and every mode
    /// renders exactly as it did before overscan existed.
    let paintRegion
        (canvas: SKCanvas)
        (content: SKRect)
        (extent: SKRect)
        (hasGpuContext: bool)
        (snapshot: BackgroundSnapshot)
        =
        if snapshot.Mode <> NoBackground && snapshot.Alpha > 0.001f then
            let usedShader =
                if snapshot.Tier = RenderTier.A && hasGpuContext then
                    tryPaintShader canvas content extent snapshot
                else
                    false

            if not usedShader then
                let started = Diagnostics.startTimer ()

                match snapshot.Mode with
                | Aurora -> paintAurora canvas content extent snapshot
                | LayeredWaves -> paintWaves canvas content extent snapshot
                | LiquidGlass -> paintLiquid canvas content snapshot
                | FoldedGlass -> paintFolded canvas content extent snapshot
                | NoBackground -> ()

                Diagnostics.stopTimer Diagnostics.render started

    /// Paints with no overscan. Kept because most callers — and every
    /// deterministic test — want exactly the viewport.
    let paintContent (canvas: SKCanvas) (rect: SKRect) (hasGpuContext: bool) (snapshot: BackgroundSnapshot) =
        paintRegion canvas rect rect hasGpuContext snapshot

    /// Releases the reused paints. Called at shutdown.
    /// SKRuntimeEffectUniforms is managed-only (no Dispose), so clearing the
    /// dictionary is sufficient.
    let dispose () =
        uniformCache.Clear()

        if not (isNull shaderPaint) then
            shaderPaint.Dispose()
            shaderPaint <- null

        if not (isNull fillPaint) then
            fillPaint.Dispose()
            fillPaint <- null
