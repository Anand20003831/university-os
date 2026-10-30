namespace UniversityOS.App.Controls

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Media
open Avalonia.Media.Immutable
open UniversityOS.Domain
open UniversityOS.App.Design
open UniversityOS.App.Rendering

/// The animated background: one control, one GPU pass, five modes.
///
/// It sits at the bottom of the shell, takes no input and is excluded from the
/// automation tree — it is decoration, and assistive technology should never see
/// it. All timing comes from the shared FrameClock so the background cannot
/// drift away from the rest of the interface.
type BackgroundHost() as this =
    inherit SkiaControl()

    static let appearanceProperty =
        AvaloniaProperty.Register<BackgroundHost, AppearanceSettings>("Appearance", AppearanceSettings.defaults)

    static let sectionProperty =
        AvaloniaProperty.Register<BackgroundHost, BackgroundSection>("Section", Overview)

    static let isGraphScreenProperty =
        AvaloniaProperty.Register<BackgroundHost, bool>("IsGraphScreen", false)

    static let accentProperty =
        AvaloniaProperty.Register<BackgroundHost, Color>("Accent", Color.FromRgb(0xCBuy, 0x8Auy, 0x4Euy))

    static let generationProperty =
        AvaloniaProperty.Register<BackgroundHost, int>("Generation", 0)

    /// Developer inspection: hold the current frame. Unlike Reduce Motion this
    /// is not a user setting and is not persisted — it only stops the clock
    /// advancing so a single composition can be examined.
    static let frozenProperty =
        AvaloniaProperty.Register<BackgroundHost, bool>("IsFrozen", false)

    /// Accumulated, speed-scaled animation seconds. Accumulating (rather than
    /// multiplying raw clock time) means changing speed does not make the
    /// animation jump.
    let mutable animTime = 0.0

    let mutable subscriptions: IDisposable list = []
    let mutable osReduceMotion = false
    let mutable windowActive = true

    /// Where parallax is now, and where the pointer is asking it to be. The
    /// gap between them is closed a little each frame, which is what stops the
    /// layer snapping and what makes returning the pointer to the centre glide
    /// back rather than jump.
    let mutable parallaxX = 0.0
    let mutable parallaxY = 0.0
    let mutable parallaxTargetX = 0.0
    let mutable parallaxTargetY = 0.0

    do
        this.IsHitTestVisible <- false
        this.ClipToBounds <- true

    static member AppearanceProperty = appearanceProperty
    static member SectionProperty = sectionProperty
    static member IsGraphScreenProperty = isGraphScreenProperty
    static member AccentProperty = accentProperty
    static member GenerationProperty = generationProperty
    static member IsFrozenProperty = frozenProperty

    member this.Appearance
        with get () = this.GetValue appearanceProperty
        and set (v: AppearanceSettings) = this.SetValue(appearanceProperty, v) |> ignore

    member this.Section
        with get () = this.GetValue sectionProperty
        and set (v: BackgroundSection) = this.SetValue(sectionProperty, v) |> ignore

    member this.IsGraphScreen
        with get () = this.GetValue isGraphScreenProperty
        and set (v: bool) = this.SetValue(isGraphScreenProperty, v) |> ignore

    member this.Accent
        with get () = this.GetValue accentProperty
        and set (v: Color) = this.SetValue(accentProperty, v) |> ignore

    member this.Generation
        with get () = this.GetValue generationProperty
        and set (v: int) = this.SetValue(generationProperty, v) |> ignore

    member this.IsFrozen
        with get () = this.GetValue frozenProperty
        and set (v: bool) = this.SetValue(frozenProperty, v) |> ignore

    /// True when the background should animate at all. Reduce Motion — from the
    /// app setting or the operating system — freezes it; so does an inactive
    /// window or the None style. The decision itself lives in Design/Motion.fs
    /// so it is unit-testable without a window.
    member private this.MotionWanted =
        let a = this.Appearance

        Motion.backgroundAnimates a.ReduceMotion osReduceMotion (a.BackgroundStyle = BackgroundStyle.None) windowActive

    /// Parallax is opt-in AND requires motion, so enabling Reduce Motion stops
    /// it without touching the user's parallax preference.
    member private this.ParallaxWanted =
        let a = this.Appearance

        Motion.parallaxActive
            a.BackgroundParallax
            a.ReduceMotion
            osReduceMotion
            (a.BackgroundStyle = BackgroundStyle.None)
            windowActive

    member private this.UpdateClock() = FrameClock.setRunning this.MotionWanted

    member private this.OnTick(tick: FrameTick) =
        if not this.IsFrozen then
            animTime <- BackgroundParams.advanceTime animTime tick.Delta this.Appearance.BackgroundSpeed

            // Ease toward the pointer target. Frame-rate independent, so the
            // layer settles in the same wall-clock time at 60Hz and 144Hz.
            if this.ParallaxWanted then
                let factor = Parallax.smoothingFactor tick.Delta
                parallaxX <- Parallax.ease parallaxX parallaxTargetX factor
                parallaxY <- Parallax.ease parallaxY parallaxTargetY factor

            this.InvalidateVisual()

    member private this.OnPointerMovedForParallax(e: PointerEventArgs) =
        if this.ParallaxWanted then
            match TopLevel.GetTopLevel this with
            | null -> ()
            | tl ->
                let p = e.GetPosition tl
                // Normalised and CLAMPED: a pointer can report a position
                // outside the window during capture or a resize, and unclamped
                // input is exactly what walks past the overscan.
                parallaxTargetX <- Parallax.displacement (Parallax.normalise p.X tl.Bounds.Width)
                parallaxTargetY <- Parallax.displacement (Parallax.normalise p.Y tl.Bounds.Height)
        else
            this.RecentreParallax()

    /// Returns the layer to centre. Snapped rather than eased because the only
    /// ways to get here are the pointer leaving, the window deactivating or
    /// Reduce Motion turning on — and in all three the clock has stopped, so
    /// there would be no frames left to ease with.
    member private this.RecentreParallax() =
        if parallaxX <> 0.0 || parallaxY <> 0.0 || parallaxTargetX <> 0.0 || parallaxTargetY <> 0.0 then
            parallaxX <- 0.0
            parallaxY <- 0.0
            parallaxTargetX <- 0.0
            parallaxTargetY <- 0.0
            this.InvalidateVisual()

    /// Repaint when any of our properties changes. Visual.AffectsRender is
    /// protected, and F#'s `static do` compiles into a separate startup class
    /// that cannot reach it, so the equivalent is done here instead.
    override this.OnPropertyChanged(change: AvaloniaPropertyChangedEventArgs) =
        base.OnPropertyChanged change

        if
            change.Property = (appearanceProperty :> AvaloniaProperty)
            || change.Property = (sectionProperty :> AvaloniaProperty)
            || change.Property = (isGraphScreenProperty :> AvaloniaProperty)
            || change.Property = (accentProperty :> AvaloniaProperty)
            || change.Property = (generationProperty :> AvaloniaProperty)
            || change.Property = (frozenProperty :> AvaloniaProperty)
        then
            this.UpdateClock()
            this.InvalidateVisual()

    override this.OnAttachedToVisualTree(e) =
        base.OnAttachedToVisualTree e
        osReduceMotion <- SystemPreferences.prefersReducedMotion ()

        match TopLevel.GetTopLevel this with
        | null -> ()
        | tl ->
            FrameClock.attach tl

            // Pointer parallax is opt-in and cheap; it only stores four floats.
            subscriptions <-
                (tl.PointerMoved |> Observable.subscribe this.OnPointerMovedForParallax)
                // The pointer leaving the window has to recentre explicitly:
                // otherwise the layer stays stuck at whatever displacement the
                // last move reported.
                :: (tl.PointerExited |> Observable.subscribe (fun _ -> this.RecentreParallax()))
                :: subscriptions

            match tl with
            | :? Window as w ->
                subscriptions <-
                    (w.Activated
                     |> Observable.subscribe (fun _ ->
                         windowActive <- true
                         this.UpdateClock()))
                    :: (w.Deactivated
                        |> Observable.subscribe (fun _ ->
                            windowActive <- false
                            this.RecentreParallax()
                            this.UpdateClock()))
                    :: subscriptions
            | _ -> ()

        subscriptions <- FrameClock.subscribe this.OnTick :: subscriptions
        this.UpdateClock()

    override this.OnDetachedFromVisualTree(e) =
        // Symmetric teardown. An undisposed subscription keeps this control
        // alive and the frame loop spinning — the classic Avalonia leak.
        for s in subscriptions do
            s.Dispose()

        subscriptions <- []
        FrameClock.setRunning false
        base.OnDetachedFromVisualTree e

    override _.OperationName = "background-host"

    override this.BuildSnapshot(rect: Rect) =
        let a = this.Appearance
        let mode = BackgroundParams.resolveMode a.BackgroundStyle this.Section
        let adaptiveMult = BackgroundParams.adaptiveMultiplier a.BackgroundStyle this.IsGraphScreen

        let quality =
            if this.MotionWanted then
                QualityController.current ()
            else
                QualityLevel.Static

        // Related hues from the same accent, so a background composition has
        // somewhere to move without leaving the accent family.
        let accent = this.Accent
        let blurPx = BackgroundParams.blurPx mode a.BackgroundBlur
        let parallaxOn = this.ParallaxWanted

        // Overscan is recomputed every frame from the CURRENT size and blur, so
        // resizing while the layer is displaced simply produces a correct new
        // overscan on the next frame rather than a stale one.
        let overscanX = Parallax.overscanPx parallaxOn blurPx
        let overscanY = Parallax.overscanPx parallaxOn blurPx

        // Never ask for more displacement than the overscan can cover. On any
        // normal window this leaves the full throw intact; it only bites when
        // the cap binds on a very small viewport.
        let limitX = Parallax.allowedDisplacement overscanX blurPx
        let limitY = Parallax.allowedDisplacement overscanY blurPx

        let appliedX = if parallaxOn then max -limitX (min limitX parallaxX) else 0.0
        let appliedY = if parallaxOn then max -limitY (min limitY parallaxY) else 0.0

        box
            { Mode = mode
              Time = float32 animTime
              Accent = SkiaInterop.toSkColor accent
              AccentLight = SkiaInterop.toSkColor (Palette.generate accent).Light
              AccentDeep = SkiaInterop.toSkColor (Palette.generate accent).Dark
              AccentAlt = SkiaInterop.toSkColor (Palette.shiftHue 18.0 accent)
              Alpha = float32 (BackgroundParams.opacityFraction mode adaptiveMult a.BackgroundOpacity a.BackgroundIntensity)
              Intensity = float32 (BackgroundParams.scale a.BackgroundIntensity)
              BlurPx = float32 blurPx
              ParallaxX = float32 appliedX
              ParallaxY = float32 appliedY
              OverscanX = float32 overscanX
              OverscanY = float32 overscanY
              GlassScale = float32 (TokenStore.current().GlassScale)
              Width = float32 rect.Width
              Height = float32 rect.Height
              Quality = quality
              Tier = Capabilities.effective ()
              Generation = this.Generation }

    override _.PaintSkia(args) =
        match args.Snapshot with
        | :? BackgroundSnapshot as snapshot -> BackdropPipeline.render args snapshot
        | _ -> ()

    /// Tier C. No Skia at all, so no shaders, no offscreen surfaces and no
    /// blur: a layered wash in the accent that still gives the interface depth.
    /// Fidelity drops; nothing stops working.
    ///
    /// Parallax is deliberately not applied here. There is no offscreen surface
    /// to overscan, so displacing the bands could only uncover an edge — and the
    /// rule is that Tier C may drop parallax but must never expose one. The
    /// bands are drawn across the full rect for the same reason.
    override _.PaintFallback(context, rect, snapshot) =
        match snapshot with
        | :? BackgroundSnapshot as s when s.Mode <> NoBackground && s.Alpha > 0.001f ->
            let bands = 6
            let accent = Color.FromRgb(s.Accent.Red, s.Accent.Green, s.Accent.Blue)

            for i in 0 .. bands - 1 do
                let f = float i / float bands
                let alpha = s.Alpha * float32 (1.0 - f) * 0.9f
                let colour = Color.FromArgb(byte (max 0.0f (min 255.0f (alpha * 255.0f))), accent.R, accent.G, accent.B)
                let brush = ImmutableSolidColorBrush colour

                let band =
                    Rect(rect.X, rect.Y + rect.Height * f, rect.Width, rect.Height / float bands + 1.0)

                context.FillRectangle(brush, band)
        | _ -> ()
