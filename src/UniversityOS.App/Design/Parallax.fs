namespace UniversityOS.App.Design

open System

/// Pointer parallax, and the overscan that makes it safe.
///
/// The background used to be rendered at exactly the viewport size and then
/// drawn at an offset. Shifting a same-size image by n px necessarily uncovers
/// an n-px strip on the opposite edge — and the background's own blur fades the
/// image out over roughly 3σ before that, so the boundary became visible well
/// before the strip did.
///
/// The fix is structural: render a region LARGER than the viewport, then crop
/// back to it. This module owns both halves of that contract — how far the
/// pointer may push the layer, and how much extra must therefore be rendered.
/// Everything here is pure, so the guarantee "displacement can never exceed
/// overscan" is a unit test rather than a hope.
[<RequireQualifiedAccess>]
module Parallax =

    /// Maximum displacement of the background layer, in logical px, at full
    /// pointer deflection. Large enough to be clearly felt — the defect is
    /// fixed by rendering more, not by moving less.
    [<Literal>]
    let maxDisplacementPx = 18.0

    /// A Gaussian blur's visible reach is about three sigma; beyond that the
    /// contribution is under half a percent of peak.
    [<Literal>]
    let blurExtentFactor = 3.0

    /// Absorbs rounding when the overscan is converted into reduced-resolution
    /// pixels, plus filtering taps at the edge.
    [<Literal>]
    let safetyPx = 3.0

    /// Exponential smoothing time constant, in seconds: the pointer target is
    /// approached with roughly 63% of the remaining distance covered per tau.
    [<Literal>]
    let timeConstantSeconds = 0.14

    /// Normalises a pointer position within an extent to −1 … +1, clamped.
    /// Clamping matters because a pointer can legitimately report a position
    /// outside the window (capture, multi-monitor drag, a stale event during
    /// resize) and unclamped input is exactly what walks past the overscan.
    let normalise (position: float) (extent: float) =
        if extent <= 0.0 || Double.IsNaN position then
            0.0
        else
            let n = (position / extent - 0.5) * 2.0
            max -1.0 (min 1.0 n)

    /// Target displacement in px for a normalised deflection. Negative sign:
    /// the layer moves against the pointer, which is what reads as depth.
    let displacement (normalised: float) =
        let n = max -1.0 (min 1.0 (if Double.IsNaN normalised then 0.0 else normalised))
        -n * maxDisplacementPx

    /// Frame-rate independent smoothing factor for one frame of the given
    /// length. At 60Hz and 144Hz the layer takes the same wall-clock time to
    /// settle, which is what stops it feeling different on different displays.
    let smoothingFactor (deltaSeconds: float) =
        if deltaSeconds <= 0.0 || Double.IsNaN deltaSeconds then
            0.0
        else
            let f = 1.0 - exp (-deltaSeconds / timeConstantSeconds)
            max 0.0 (min 1.0 f)

    /// One easing step toward the target.
    let ease (current: float) (target: float) (factor: float) =
        current + (target - current) * max 0.0 (min 1.0 factor)

    /// Overscan required on each edge, in logical px.
    ///
    /// Three contributions: how far parallax can push the layer, how far the
    /// background's own blur reaches past the image edge, and a small margin for
    /// the reduced-resolution rounding.
    ///
    /// Deliberately NOT capped as a fraction of the viewport. An earlier version
    /// was, to bound memory — but the blur is already capped per mode at 45px,
    /// so this total can never exceed about 156px however the sliders are set,
    /// and a fractional cap only ever binds on windows small enough for the
    /// absolute cost to be irrelevant. All it achieved was breaking the coverage
    /// guarantee exactly where the surface was cheapest.
    let overscanPx (parallaxEnabled: bool) (blurPx: float) =
        let parallaxPart = if parallaxEnabled then maxDisplacementPx else 0.0
        let blurPart = blurExtentFactor * max 0.0 blurPx
        parallaxPart + blurPart + safetyPx

    /// Rounds an overscan up to a whole pixel of the reduced-resolution
    /// offscreen surface, then back to logical px. This is where the quality
    /// level enters: the surface is 1/divisor scale, so rounding UP is what
    /// stops the overscan vanishing at Low quality.
    let quantise (divisor: int) (overscan: float) =
        let d = float (max 1 divisor)
        ceil (overscan / d) * d

    /// The displacement this much overscan can cover.
    ///
    /// With the overscan above this is always the full throw; it is kept as an
    /// explicit guard so the invariant is enforced at the point of use rather
    /// than inferred from two constants agreeing.
    let allowedDisplacement (overscan: float) (blurPx: float) =
        let headroom = overscan - blurExtentFactor * max 0.0 blurPx
        max 0.0 (min maxDisplacementPx headroom)

    /// True when a displacement is safely inside the overscan. If this is ever
    /// false, a viewport pixel is uncovered — which is the whole defect.
    let isCovered (overscan: float) (displacementPx: float) (blurPx: float) =
        abs displacementPx + blurExtentFactor * max 0.0 blurPx <= overscan + 1e-9
