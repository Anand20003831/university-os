namespace UniversityOS.App.Controls

open Avalonia
open Avalonia.Controls
open Avalonia.Media
open Avalonia.Media.Immutable
open SkiaSharp
open UniversityOS.App.Design
open UniversityOS.App.Rendering

/// Immutable per-frame glass parameters. Offsets are window-space so the panel
/// can sample its own region of the shared backdrop.
type GlassSnapshot =
    { Width: float32
      Height: float32
      OffsetX: float32
      OffsetY: float32
      Radius: float32
      Tint: SKColor
      Border: SKColor
      /// How strongly the shared blurred backdrop is composited in. Zero at
      /// Glass 0, which is the opaque reduce-transparency escape hatch.
      BackdropAlpha: float32
      /// Specular line along the top edge; grows with the glass setting.
      HighlightAlpha: float32
      ShadowAlpha: float32
      ShadowSigma: float32
      /// How far to pull the stroked outline inside the panel bounds, in px.
      ///
      /// A stroke is centred on its path, so an outline laid exactly on the
      /// bounds puts half its width outside them — where the draw operation's
      /// own bounds clip it away. That leaves a half-weight hairline, and
      /// because the clipping is geometrically asymmetric where the corner
      /// curve turns, the corners come out visibly ragged.
      ///
      /// Zero keeps the original behaviour, which is what every already-approved
      /// surface renders with. Half the stroke width puts the whole line inside
      /// the panel and the corners come out clean.
      BorderInset: float32
      Generation: int }

/// Render-thread-owned paints for glass. Created once, reused every frame,
/// released at shutdown — drawing must not allocate.
///
/// Public rather than internal so the golden-image suite can drive the real
/// painter directly, instead of a reimplementation that could drift from it.
[<RequireQualifiedAccess>]
module GlassPainter =

    let mutable private fill: SKPaint = null
    let mutable private stroke: SKPaint = null
    let mutable private shadow: SKPaint = null

    let private ensure () =
        if isNull fill then
            fill <- new SKPaint(IsAntialias = true)

        if isNull stroke then
            stroke <- new SKPaint(IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.0f)

        if isNull shadow then
            shadow <- new SKPaint(IsAntialias = true)

    let paint (args: SkiaPaintArgs) =
        match args.Snapshot with
        | :? GlassSnapshot as s ->
            ensure ()
            let rect = args.Bounds
            let rounded = new SKRoundRect(rect, s.Radius, s.Radius)

            // 1. Drop shadow, so the panel reads as lifted off the background.
            //    Both its opacity and its softness follow the glass setting.
            if s.ShadowAlpha > 0.004f then
                use blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, max 1.0f s.ShadowSigma)
                shadow.MaskFilter <- blur
                shadow.Color <- SKColor(0uy, 0uy, 0uy, byte (min 255.0f (s.ShadowAlpha * 255.0f)))
                let shadowRect = SKRect(rect.Left, rect.Top + 6.0f, rect.Right, rect.Bottom + 6.0f)
                use shadowRounded = new SKRoundRect(shadowRect, s.Radius, s.Radius)
                args.Canvas.DrawRoundRect(shadowRounded, shadow)
                shadow.MaskFilter <- null

            args.Canvas.Save() |> ignore
            args.Canvas.ClipRoundRect(rounded, SKClipOperation.Intersect, true)

            // 2. The shared blurred backdrop, sampled at this panel's position.
            //    Skipped entirely at Glass 0: an opaque panel must not leak
            //    movement through its tint, and not drawing it is also cheaper.
            if s.BackdropAlpha > 0.01f then
                match BackdropCache.tryGet () with
                | Some(image, logicalW, logicalH) when logicalW > 0.0f && logicalH > 0.0f ->
                    let sx = float32 image.Width / logicalW
                    let sy = float32 image.Height / logicalH

                    let source =
                        SKRect(
                            s.OffsetX * sx,
                            s.OffsetY * sy,
                            (s.OffsetX + s.Width) * sx,
                            (s.OffsetY + s.Height) * sy
                        )

                    fill.Color <- SKColor(255uy, 255uy, 255uy, byte (min 255.0f (s.BackdropAlpha * 255.0f)))
                    args.Canvas.DrawImage(image, source, rect, fill)
                | _ ->
                    // No backdrop yet (first frame, or background off). The tint
                    // alone still renders a coherent surface.
                    ()

            // 3. Tint on top of the blur — this is what makes it read as glass
            //    rather than as a blurry photograph.
            fill.Shader <- null
            fill.Color <- s.Tint
            args.Canvas.DrawRect(rect, fill)

            // 4. Specular highlight down from the top edge. Absent at Glass 0,
            //    where there is no pane to catch the light, and absent whenever
            //    the effective alpha is below the visible threshold — a panel
            //    that asked for no highlight gets no draw call, not a
            //    transparent one.
            if Glass.drawsHighlight (float s.HighlightAlpha) then
                let stop = rect.Top + (rect.Bottom - rect.Top) * float32 Glass.highlightReach

                use highlight =
                    SKShader.CreateLinearGradient(
                        SKPoint(rect.Left, rect.Top),
                        SKPoint(rect.Left, stop),
                        [| SKColor(255uy, 255uy, 255uy, byte (min 255.0f (s.HighlightAlpha * 255.0f)))
                           SKColor(255uy, 255uy, 255uy, 0uy) |],
                        [| 0.0f; 1.0f |],
                        SKShaderTileMode.Clamp
                    )

                fill.Color <- SKColors.White
                fill.Shader <- highlight
                args.Canvas.DrawRect(rect, fill)
                fill.Shader <- null

            args.Canvas.Restore()

            // 5. Hairline border, drawn after restoring so the clip does not
            //    cut it — but the draw operation's own bounds still can. See
            //    `BorderInset`: pulling the path in by half the stroke width is
            //    what keeps the whole line inside the panel and the corners
            //    clean instead of half-clipped.
            stroke.Color <- s.Border

            if s.BorderInset > 0.0f then
                let i = s.BorderInset
                let inner = SKRect(rect.Left + i, rect.Top + i, rect.Right - i, rect.Bottom - i)
                // Concentric: the inset outline's radius shrinks with it, or the
                // corner curve stops matching the panel it is outlining.
                let r = max 0.0f (s.Radius - i)
                use insetRounded = new SKRoundRect(inner, r, r)
                args.Canvas.DrawRoundRect(insetRounded, stroke)
            else
                args.Canvas.DrawRoundRect(rounded, stroke)

            rounded.Dispose()
        | _ -> ()

    let dispose () =
        if not (isNull fill) then
            fill.Dispose()
            fill <- null

        if not (isNull stroke) then
            stroke.Dispose()
            stroke <- null

        if not (isNull shadow) then
            shadow.Dispose()
            shadow <- null

/// A glass surface that hosts real Avalonia controls.
///
/// Derives from Decorator on purpose: the glass is drawn behind, and the child
/// renders normally on top as ordinary controls. That keeps text rendering,
/// hit-testing, focus, IME and screen-reader support working — the
/// accessibility firewall from docs §15. A glass panel that painted its own
/// contents would lose all of it.
type GlassPanel() as this =
    inherit Decorator()

    static let baseRadiusProperty =
        AvaloniaProperty.Register<GlassPanel, float>("BaseRadius", 14.0)

    /// Defaults to the material's own card colour rather than a literal, so
    /// there is one place the warm-neutral surface is defined. The alpha here is
    /// ignored — `Glass.tintAlpha` computes it from the user's glass setting.
    static let tintProperty =
        AvaloniaProperty.Register<GlassPanel, Color>("Tint", Surfaces.dark.Card)

    /// A floor under the shared tint curve, for panels that must stay their own
    /// colour at every glass setting — see `Glass.primaryTintAlpha`. Zero (the
    /// default) leaves the shared behaviour untouched.
    static let minTintAlphaProperty =
        AvaloniaProperty.Register<GlassPanel, float>("MinTintAlpha", 0.0)

    static let elevationProperty =
        AvaloniaProperty.Register<GlassPanel, int>("Elevation", 1)

    /// How much of the glass treatment is applied, 0–1.
    ///
    /// 1 is the approved floating glass. 0 is a flat opaque surface with square
    /// corners, no backdrop sampling, no highlight and no shadow — the docked
    /// sidebar. Everything between is a straight blend.
    ///
    /// This exists so the shell can have ONE sidebar visual tree. Switching
    /// between a plain Border and a GlassPanel at the moment of pinning would
    /// remount the panel mid-gesture and make the transition uninterpolatable —
    /// exactly the class of problem that caused the reveal flicker.
    static let presentationProperty =
        AvaloniaProperty.Register<GlassPanel, float>("Presentation", 1.0)

    /// How far the hairline outline is pulled inside the panel bounds, in px.
    /// Default 0, which is the original behaviour every approved surface
    /// already renders with. Half the stroke width (0.5) gives a full-weight
    /// hairline with clean corners — see `GlassSnapshot.BorderInset`.
    static let borderInsetProperty =
        AvaloniaProperty.Register<GlassPanel, float>("BorderInset", 0.0)

    /// Multiplier on the specular top highlight only, 0–1. Default 1.
    ///
    /// A separate scalar from `Presentation` because they answer different
    /// questions. `Presentation` blends the whole material between docked and
    /// floating; this dials down ONE channel. Content-dense surfaces — a
    /// dashboard full of cards — read as foggy when every card carries a broad
    /// white wash across its top third, while the shell's single floating
    /// sidebar wants the full specular. One number, no forked renderer.
    static let highlightScaleProperty =
        AvaloniaProperty.Register<GlassPanel, float>("HighlightScale", 1.0)

    let mutable tokenSubscription: System.IDisposable = null

    /// Repaint when the panel MOVES, not only when it changes.
    ///
    /// The draw operation carries a frozen snapshot that includes the panel's
    /// window offset, and that offset is what selects the region of the shared
    /// blurred backdrop this panel samples. Scrolling moves the panel but
    /// changes none of its properties and none of the design tokens, so nothing
    /// below invalidated it — Avalonia reused the recorded operation and the
    /// panel went on sampling the backdrop region it occupied BEFORE the scroll.
    ///
    /// The visible result was a mismatched slice of the background printing
    /// through the glass: worst on whichever cards had travelled furthest from
    /// their initial layout position, which is why the two bottom cards of a
    /// scrolled page showed a pale band across the top and the same cards looked
    /// correct before anyone scrolled.
    ///
    /// This is the same class of bug as the Glass-slider one documented below:
    /// state the panel reads at Render time, changing without anything telling
    /// the panel to render again.
    let mutable viewportSubscription: System.IDisposable = null

    do this.ClipToBounds <- false

    /// Repaint when the design tokens change.
    ///
    /// This is what the Glass slider was missing. The panel reads GlassScale
    /// from TokenStore at Render time, but Avalonia only calls Render when
    /// something invalidates the visual — and moving the Glass slider changes
    /// no property OF THE PANEL, so nothing ever did. Corner radius appeared to
    /// work only because it happens to flow through BaseRadius.
    ///
    /// Subscribing here rather than adding a Generation property means every
    /// GlassPanel anywhere in the app is correct without its call site
    /// remembering to thread a token generation through.
    override this.OnAttachedToVisualTree(e) =
        base.OnAttachedToVisualTree e

        if isNull tokenSubscription then
            tokenSubscription <- TokenStore.changed.Subscribe(fun _ -> this.InvalidateVisual())

        // Fires whenever this control's position relative to its scrolling
        // viewport changes — which is exactly when the frozen window offset in
        // the draw operation stops matching reality. See `viewportSubscription`.
        if isNull viewportSubscription then
            viewportSubscription <- this.EffectiveViewportChanged.Subscribe(fun _ -> this.InvalidateVisual())

        this.InvalidateVisual()

    override this.OnDetachedFromVisualTree(e) =
        if not (isNull tokenSubscription) then
            tokenSubscription.Dispose()
            tokenSubscription <- null

        if not (isNull viewportSubscription) then
            viewportSubscription.Dispose()
            viewportSubscription <- null

        base.OnDetachedFromVisualTree e

    /// See the note in BackgroundHost: Visual.AffectsRender is protected and
    /// unreachable from F#'s static initialiser class.
    override this.OnPropertyChanged(change: AvaloniaPropertyChangedEventArgs) =
        base.OnPropertyChanged change

        if
            change.Property = (baseRadiusProperty :> AvaloniaProperty)
            || change.Property = (tintProperty :> AvaloniaProperty)
            || change.Property = (minTintAlphaProperty :> AvaloniaProperty)
            || change.Property = (elevationProperty :> AvaloniaProperty)
            || change.Property = (presentationProperty :> AvaloniaProperty)
            || change.Property = (highlightScaleProperty :> AvaloniaProperty)
            || change.Property = (borderInsetProperty :> AvaloniaProperty)
        then
            this.InvalidateVisual()

    static member BorderInsetProperty = borderInsetProperty

    member this.BorderInset
        with get () = this.GetValue borderInsetProperty
        and set (v: float) = this.SetValue(borderInsetProperty, v) |> ignore

    static member BaseRadiusProperty = baseRadiusProperty
    static member TintProperty = tintProperty
    static member MinTintAlphaProperty = minTintAlphaProperty
    static member ElevationProperty = elevationProperty
    static member PresentationProperty = presentationProperty
    static member HighlightScaleProperty = highlightScaleProperty

    member this.Presentation
        with get () = this.GetValue presentationProperty
        and set (v: float) = this.SetValue(presentationProperty, v) |> ignore

    member this.HighlightScale
        with get () = this.GetValue highlightScaleProperty
        and set (v: float) = this.SetValue(highlightScaleProperty, v) |> ignore

    /// Design radius in px, before the user's corner-radius preference.
    member this.BaseRadius
        with get () = this.GetValue baseRadiusProperty
        and set (v: float) = this.SetValue(baseRadiusProperty, v) |> ignore

    member this.Tint
        with get () = this.GetValue tintProperty
        and set (v: Color) = this.SetValue(tintProperty, v) |> ignore

    member this.MinTintAlpha
        with get () = this.GetValue minTintAlphaProperty
        and set (v: float) = this.SetValue(minTintAlphaProperty, v) |> ignore

    member this.Elevation
        with get () = this.GetValue elevationProperty
        and set (v: int) = this.SetValue(elevationProperty, v) |> ignore

    override this.Render(context: DrawingContext) =
        let w = this.Bounds.Width
        let h = this.Bounds.Height

        if w > 0.0 && h > 0.0 then
            let tokens = TokenStore.current ()

            // Window-space position, so the panel samples the right region of
            // the shared backdrop. Computed on the UI thread, then frozen.
            let offset =
                match TopLevel.GetTopLevel this with
                | null -> Point(0.0, 0.0)
                | topLevel ->
                    let translated = this.TranslatePoint(Point(0.0, 0.0), topLevel)
                    if translated.HasValue then translated.Value else Point(0.0, 0.0)

            // Glass intensity drives the whole material at once — see
            // Design/Glass.fs, where the three defined points (0, 50, 100) are
            // specified and asserted.
            let glass = Glass.clampScale tokens.GlassScale
            let tint = this.Tint
            let toByte (v: float) = byte (max 0.0 (min 255.0 (v * 255.0)))

            // Presentation blends the whole material between a docked opaque
            // surface (0) and the approved floating glass (1). Every channel is
            // driven from the one value, so a partially-transitioned panel is
            // always internally consistent.
            let p = max 0.0 (min 1.0 this.Presentation)
            let lerp (docked: float) (floating: float) = docked + (floating - docked) * p

            let snapshot =
                { Width = float32 w
                  Height = float32 h
                  OffsetX = float32 offset.X
                  OffsetY = float32 offset.Y
                  // Square when docked, the approved radius when floating.
                  Radius = float32 (Tokens.radius tokens this.BaseRadius * p)
                  Tint = SKColor(tint.R, tint.G, tint.B, toByte (lerp 1.0 (Glass.effectiveTintAlpha this.MinTintAlpha glass)))
                  // The hairline follows the THEME, not a hard-coded white — see
                  // `Glass.hairline`. White on a light card measured 1.01:1,
                  // i.e. no edge at all.
                  Border =
                    (let line = Glass.hairline tokens.Theme
                     let weight = Glass.hairlineAlphaScale tokens.Theme

                     SKColor(
                         line.R,
                         line.G,
                         line.B,
                         toByte (lerp (0.09 * weight) (Glass.borderAlpha glass * weight))
                     ))
                  // Docked samples no backdrop: it is an opaque surface, and
                  // sampling behind something opaque is wasted work.
                  BackdropAlpha = float32 (Glass.backdropAlpha glass * p)
                  HighlightAlpha =
                    float32 (Glass.highlightAlpha glass * p * max 0.0 (min 1.0 this.HighlightScale))
                  // No floating shadow when docked.
                  ShadowAlpha = float32 (0.18 * float this.Elevation * Glass.shadowAlphaScale glass * p)
                  ShadowSigma = float32 (Glass.shadowSigma glass)
                  BorderInset = float32 this.BorderInset
                  Generation = tokens.Generation }

            context.Custom(
                new SkiaDrawOperation(
                    Rect(0.0, 0.0, w, h),
                    box snapshot,
                    "glass-panel",
                    GlassPainter.paint,
                    (fun ctx rect snap ->
                        // Tier C: no Skia, so no backdrop sampling and no blur.
                        // A tinted rounded rect keeps every panel readable and
                        // correctly shaped. The glass setting still moves the
                        // alpha — so the slider visibly does something here too
                        // — but with a floor, because with nothing showing
                        // through, a transparent panel would just be washed out.
                        match snap with
                        | :? GlassSnapshot as s ->
                            let brush =
                                ImmutableSolidColorBrush(
                                    Color.FromArgb(max 209uy s.Tint.Alpha, s.Tint.Red, s.Tint.Green, s.Tint.Blue)
                                )

                            ctx.DrawRectangle(brush, null, rect, float s.Radius, float s.Radius, BoxShadows())
                        | _ -> ())
                )
            )
