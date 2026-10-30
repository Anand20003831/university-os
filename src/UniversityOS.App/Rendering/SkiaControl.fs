namespace UniversityOS.App.Rendering

open Avalonia
open Avalonia.Controls
open Avalonia.Media
open Avalonia.Rendering.SceneGraph
open Avalonia.Skia
open SkiaSharp

/// Everything a Skia painter is given. Passed as one record so adding a
/// capability later does not ripple through every painter signature.
///
/// GrContext is null under software rendering — painters that need an offscreen
/// GPU surface must check it and degrade rather than assume.
type SkiaPaintArgs =
    { Canvas: SKCanvas
      Bounds: SKRect
      GrContext: GRContext
      Snapshot: obj }

/// The custom draw operation every custom-rendered control pushes.
///
/// It carries an IMMUTABLE snapshot and nothing else. `Render` runs on the
/// render thread, where reading live UI state would tear; the snapshot removes
/// that failure class rather than guarding it with locks.
///
/// It also never lets an exception escape. An exception propagating into
/// Avalonia's render loop can tear down the renderer for the whole window, so
/// every failure is caught, recorded once, and the frame is simply skipped.
type SkiaDrawOperation
    (
        bounds: Rect,
        snapshot: obj,
        name: string,
        paint: SkiaPaintArgs -> unit,
        fallback: ImmediateDrawingContext -> Rect -> obj -> unit
    ) =

    member _.Snapshot = snapshot
    member _.OperationBounds = bounds

    interface ICustomDrawOperation with

        member _.Bounds = bounds

        /// Decorative surfaces never take input. Interactive custom controls
        /// handle input with real Avalonia handlers on the control itself.
        member _.HitTest(_) = false

        /// Structural comparison of the snapshot. This is what makes a stopped
        /// background free: an identical frame is skipped rather than redrawn.
        member _.Equals(other: ICustomDrawOperation) =
            match other with
            | :? SkiaDrawOperation as o -> o.OperationBounds = bounds && System.Object.Equals(o.Snapshot, snapshot)
            | _ -> false

        member _.Render(context: ImmediateDrawingContext) =
            Diagnostics.countDraw name

            try
                // Tier C means "render without Skia". It must take the fallback
                // path even when a lease IS available, otherwise forcing Tier C
                // would only change the reported label while still rendering
                // through Skia — which is not a test of anything.
                if Capabilities.effective () = RenderTier.C then
                    Diagnostics.count Diagnostics.Names.fallbackPaint
                    fallback context bounds snapshot
                else

                // TryGetFeature is non-generic in Avalonia 11.3.2: it takes a
                // Type and returns obj, so the type test doubles as the null check.
                match context.TryGetFeature(typeof<ISkiaSharpApiLeaseFeature>) with
                | :? ISkiaSharpApiLeaseFeature as feature ->
                    use lease = feature.Lease()
                    let canvas = lease.SkCanvas

                    if not (Capabilities.hasProbed ()) then
                        Capabilities.reportProbe true (not (isNull (box lease.GrContext)))

                    if isNull (box canvas) then
                        fallback context bounds snapshot
                    else
                        paint
                            { Canvas = canvas
                              Bounds =
                                SKRect(
                                    float32 bounds.X,
                                    float32 bounds.Y,
                                    float32 (bounds.X + bounds.Width),
                                    float32 (bounds.Y + bounds.Height)
                                )
                              GrContext = lease.GrContext
                              Snapshot = snapshot }
                | _ ->
                    // No Skia lease at all — software renderer or an unsupported
                    // platform. Tier C.
                    if not (Capabilities.hasProbed ()) then
                        Capabilities.reportProbe false false

                    Diagnostics.count Diagnostics.Names.fallbackPaint
                    fallback context bounds snapshot
            with ex ->
                // Swallowed deliberately, logged once per operation name.
                Capabilities.reportFailure name ex

        member _.Dispose() =
            // Nothing owned here: native resources live in caches with explicit
            // lifetimes (BackdropCache, ShaderLibrary).
            ()

/// Base class for custom-rendered controls.
///
/// Subclasses implement three things: how to build the immutable snapshot (UI
/// thread), how to paint it with Skia (render thread), and how to paint it with
/// plain Avalonia primitives when no Skia lease is available (Tier C).
///
/// IMPORTANT for subclasses: `PaintSkia` and `PaintFallback` run on the RENDER
/// thread. They must read only their arguments — never instance fields, never
/// the Elmish model, never a service — and must not allocate.
[<AbstractClass>]
type SkiaControl() =
    inherit Control()

    /// Built on the UI thread, consumed on the render thread. Must be an
    /// immutable value with structural equality (an F# record).
    abstract BuildSnapshot: Rect -> obj

    /// Render thread. Arguments only.
    abstract PaintSkia: SkiaPaintArgs -> unit

    /// Render thread, Tier C. Plain Avalonia primitives, no Skia.
    abstract PaintFallback: ImmediateDrawingContext * Rect * obj -> unit
    default _.PaintFallback(_, _, _) = ()

    /// Stable name used for one-per-session failure logging.
    abstract OperationName: string

    override this.Render(context: DrawingContext) =
        let width = this.Bounds.Width
        let height = this.Bounds.Height

        if width > 0.0 && height > 0.0 then
            let local = Rect(0.0, 0.0, width, height)
            let snapshot = this.BuildSnapshot local

            context.Custom(
                new SkiaDrawOperation(
                    local,
                    snapshot,
                    this.OperationName,
                    (fun args -> this.PaintSkia args),
                    (fun ctx rect snap -> this.PaintFallback(ctx, rect, snap))
                )
            )
