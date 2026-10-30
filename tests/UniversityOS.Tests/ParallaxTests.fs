module UniversityOS.Tests.ParallaxTests

open Xunit
open UniversityOS.App.Design

/// The parallax geometry.
///
/// The defect: the background was rendered at exactly the viewport size and
/// then drawn at an offset, so a displacement of n px uncovered an n-px strip on
/// the opposite edge — and the layer's own blur faded the image out over roughly
/// 3σ before that, so the boundary showed up well before the strip did.
///
/// The contract these tests hold is one line: displacement + blur reach must
/// never exceed the overscan. Everything else is in service of that.

let private blurSettings = [ 0.0; 5.0; 13.5; 28.0; 45.0 ]
let private viewports = [ 640.0; 1280.0; 1920.0; 3440.0 ]

// ---- pointer input ---------------------------------------------------------

[<Fact>]
let ``pointer position normalises to minus one through plus one`` () =
    Assert.Equal(-1.0, Parallax.normalise 0.0 1000.0, 6)
    Assert.Equal(0.0, Parallax.normalise 500.0 1000.0, 6)
    Assert.Equal(1.0, Parallax.normalise 1000.0 1000.0, 6)

[<Fact>]
let ``pointer input outside the window is clamped, not extrapolated`` () =
    // A pointer legitimately reports positions outside the window during
    // capture, a drag across monitors, or a stale event mid-resize. Unclamped,
    // that is exactly what walks the layer past the overscan.
    Assert.Equal(-1.0, Parallax.normalise -5000.0 1000.0, 6)
    Assert.Equal(1.0, Parallax.normalise 5000.0 1000.0, 6)

    // Degenerate extents must not divide by zero or produce NaN.
    Assert.Equal(0.0, Parallax.normalise 100.0 0.0, 6)
    Assert.Equal(0.0, Parallax.normalise nan 1000.0, 6)

[<Fact>]
let ``displacement never exceeds the declared maximum in any direction`` () =
    for n in [ -10.0; -1.0; -0.5; 0.0; 0.5; 1.0; 10.0; nan ] do
        let d = Parallax.displacement n
        Assert.InRange(d, -Parallax.maxDisplacementPx, Parallax.maxDisplacementPx)

    // Full deflection reaches the full throw — the fix is rendering more, not
    // moving less.
    Assert.Equal(-Parallax.maxDisplacementPx, Parallax.displacement 1.0, 6)
    Assert.Equal(Parallax.maxDisplacementPx, Parallax.displacement -1.0, 6)

// ---- overscan --------------------------------------------------------------

[<Fact>]
let ``overscan covers maximum displacement in all four directions`` () =
    for blur in blurSettings do
        for viewport in viewports do
            let overscan = Parallax.overscanPx true blur
            let limit = Parallax.allowedDisplacement overscan blur

            for direction in [ 1.0; -1.0 ] do
                let displacement = direction * limit

                Assert.True(
                    Parallax.isCovered overscan displacement blur,
                    sprintf
                        "viewport %f, blur %f: displacement %f is not covered by overscan %f"
                        viewport
                        blur
                        displacement
                        overscan
                )

[<Fact>]
let ``overscan covers the diagonal maximum`` () =
    // Both axes at full deflection at once — the corner case, literally.
    for blur in blurSettings do
        let ox = Parallax.overscanPx true blur
        let oy = Parallax.overscanPx true blur

        Assert.True(Parallax.isCovered ox (Parallax.allowedDisplacement ox blur) blur)
        Assert.True(Parallax.isCovered oy (Parallax.allowedDisplacement oy blur) blur)

        // The two axes are independent, so a diagonal is covered exactly when
        // each axis is. Asserted rather than assumed.
        Assert.True(Parallax.isCovered ox -(Parallax.allowedDisplacement ox blur) blur)
        Assert.True(Parallax.isCovered oy -(Parallax.allowedDisplacement oy blur) blur)

[<Fact>]
let ``overscan grows with the blur radius`` () =
    // Blur is the reason overscan cannot simply equal the maximum displacement:
    // a blurred image fades out before its edge.
    let small = Parallax.overscanPx true 0.0
    let large = Parallax.overscanPx true 45.0

    Assert.True(large > small)
    Assert.True(large >= Parallax.maxDisplacementPx + 3.0 * 45.0)

[<Fact>]
let ``overscan is still required when parallax is off`` () =
    // With no parallax there is no displacement, but the blur still fades the
    // edge — so the layer is still rendered slightly large.
    let overscan = Parallax.overscanPx false 20.0
    Assert.True(overscan > 0.0)
    Assert.True(Parallax.isCovered overscan 0.0 20.0)

[<Fact>]
let ``resizing while displaced keeps coverage at every size`` () =
    // Overscan is recomputed from the current size every frame, so a window
    // being dragged smaller while the layer is pushed to its limit must stay
    // covered at every intermediate width.
    let blur = 13.5
    let mutable displacement = Parallax.maxDisplacementPx

    for width in [ 3440.0; 1920.0; 1280.0; 900.0; 640.0; 320.0; 120.0 ] do
        let overscan = Parallax.overscanPx true blur
        let limit = Parallax.allowedDisplacement overscan blur
        displacement <- max -limit (min limit displacement)

        Assert.True(
            Parallax.isCovered overscan displacement blur,
            sprintf "width %f: displacement %f escaped overscan %f" width displacement overscan
        )

[<Fact>]
let ``overscan is bounded, so the offscreen surface cannot grow without limit`` () =
    // No viewport-fraction cap — that would break coverage on small windows,
    // which is exactly what an earlier attempt did. The bound comes from the
    // blur cap instead: 45px is the largest any mode allows.
    let worst = Parallax.overscanPx true 45.0

    Assert.True(worst < 200.0, sprintf "overscan of %f px is more than the pipeline should ever allocate" worst)
    Assert.True(Parallax.isCovered worst (Parallax.allowedDisplacement worst 45.0) 45.0)

    // And the full throw survives even at the heaviest blur.
    Assert.Equal(Parallax.maxDisplacementPx, Parallax.allowedDisplacement worst 45.0, 6)

[<Fact>]
let ``quantised overscan is never smaller than requested`` () =
    // Rounding down at high quality divisors would silently reintroduce the
    // uncovered strip, one reduced pixel wide.
    for divisor in [ 1; 2; 3; 4; 8 ] do
        for overscan in [ 0.4; 1.0; 17.5; 55.0; 148.5 ] do
            let quantised = Parallax.quantise divisor overscan
            Assert.True(quantised >= overscan, sprintf "divisor %d: %f < %f" divisor quantised overscan)
            Assert.True(quantised - overscan < float divisor + 1e-9)

// ---- easing ----------------------------------------------------------------

[<Fact>]
let ``easing converges toward the target and settles`` () =
    let target = -Parallax.maxDisplacementPx
    let mutable current = 0.0
    let delta = 1.0 / 60.0

    for _ in 1..120 do
        current <- Parallax.ease current target (Parallax.smoothingFactor delta)

    Assert.True(abs (current - target) < 0.01, sprintf "did not settle: %f vs %f" current target)

[<Fact>]
let ``easing never overshoots the target`` () =
    // Overshoot on a background layer reads as a wobble, and would also push
    // past the overscan the target was clamped to.
    let target = Parallax.maxDisplacementPx
    let mutable current = 0.0

    for _ in 1..300 do
        current <- Parallax.ease current target (Parallax.smoothingFactor (1.0 / 60.0))
        Assert.InRange(current, 0.0, target)

[<Fact>]
let ``returning the pointer to the centre recentres the layer`` () =
    let mutable current = Parallax.maxDisplacementPx

    // Pointer returns to the middle: target becomes zero.
    let target = Parallax.displacement (Parallax.normalise 500.0 1000.0)
    Assert.Equal(0.0, target, 6)

    let mutable previous = current

    for _ in 1..120 do
        current <- Parallax.ease current target (Parallax.smoothingFactor (1.0 / 60.0))
        Assert.True(current <= previous + 1e-9, "recentring must be monotonic, never bounce")
        previous <- current

    Assert.True(abs current < 0.01, sprintf "did not recentre: %f" current)

[<Fact>]
let ``smoothing is frame-rate independent`` () =
    // The same wall-clock time must produce the same settle, or the layer feels
    // different on a 60Hz and a 144Hz display.
    let settle (frames: int) (delta: float) =
        let mutable current = 0.0
        let factor = Parallax.smoothingFactor delta

        for _ in 1..frames do
            current <- Parallax.ease current 10.0 factor

        current

    let atSixty = settle 30 (1.0 / 60.0) // 0.5s
    let atOneFortyFour = settle 72 (1.0 / 144.0) // 0.5s

    Assert.Equal(atSixty, atOneFortyFour, 3)

[<Fact>]
let ``a zero or negative frame delta does not move the layer`` () =
    Assert.Equal(0.0, Parallax.smoothingFactor 0.0, 9)
    Assert.Equal(0.0, Parallax.smoothingFactor -1.0, 9)
    Assert.Equal(5.0, Parallax.ease 5.0 100.0 (Parallax.smoothingFactor 0.0), 9)
