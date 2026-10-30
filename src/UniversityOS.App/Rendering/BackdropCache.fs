namespace UniversityOS.App.Rendering

open SkiaSharp

/// The shared blurred backdrop that every glass surface samples.
///
/// Avalonia has no backdrop API — `BlurEffect` blurs an element's own content,
/// not what is behind it. But the background is ours, so we know exactly what is
/// behind a glass panel: the background host renders it once per frame into an
/// offscreen surface, blurs it once, and publishes it here. N glass panels then
/// cost one blur, not N.
///
/// OWNERSHIP: the published SKImage is owned by this cache and is only ever
/// created or disposed on the render thread. `requestClear` (which may be called
/// from the UI thread under memory pressure) sets a flag that the render thread
/// honours at the start of the next frame, so a panel can never sample an image
/// that another thread just disposed.
[<RequireQualifiedAccess>]
module BackdropCache =

    let mutable private image: SKImage = null
    let mutable private logicalWidth = 0.0f
    let mutable private logicalHeight = 0.0f
    let mutable private generation = -1
    let mutable private clearRequested = false

    /// Publishes a new backdrop and takes ownership of it, disposing the
    /// previous one. RENDER THREAD ONLY.
    let publish (newImage: SKImage) (width: float32) (height: float32) (tokenGeneration: int) =
        if not (isNull image) then
            image.Dispose()

        image <- newImage
        logicalWidth <- width
        logicalHeight <- height
        generation <- tokenGeneration

    /// The current backdrop with the logical size it represents, or None when
    /// nothing has been published yet (first frame, or background disabled).
    /// RENDER THREAD ONLY.
    let tryGet () =
        if isNull image then
            Diagnostics.count Diagnostics.Names.glassSampleMiss
            None
        else
            Diagnostics.count Diagnostics.Names.glassSampleHit
            Some(image, logicalWidth, logicalHeight)

    let currentGeneration () = generation

    /// Asks for the backdrop to be dropped. Safe to call from any thread; the
    /// actual dispose happens on the render thread.
    let requestClear () = clearRequested <- true

    /// Honours a pending clear. Called by the background host at the top of its
    /// paint, on the render thread.
    let applyPendingClear () =
        if clearRequested then
            clearRequested <- false

            if not (isNull image) then
                image.Dispose()
                image <- null

            generation <- -1

    /// Final teardown at shutdown. Render thread, or after rendering has stopped.
    let dispose () =
        clearRequested <- false

        if not (isNull image) then
            image.Dispose()
            image <- null

        generation <- -1
