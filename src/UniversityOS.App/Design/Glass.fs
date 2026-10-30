namespace UniversityOS.App.Design

open Avalonia.Media
open UniversityOS.Domain

/// How the Glass setting turns into the material.
///
/// One slider drives six things at once — tint opacity, how much of the shared
/// backdrop shows through, border weight, the inner highlight, the shared blur
/// radius and shadow softness. Keeping all six here, as pure functions of the
/// scale, is what makes "Glass 50 is the balanced default" a checkable statement
/// rather than a comment: every anchor below is asserted in DesignSystemTests.
///
/// The input is Tokens.GlassScale — GlassIntensity / 50 — so the three defined
/// points are scale 0.0 (slider 0), 1.0 (slider 50) and 2.0 (slider 100).
[<RequireQualifiedAccess>]
module Glass =

    /// Below this the panel stops being a readable surface: body text over a
    /// blurred, moving background loses contrast. Enforced by `tintAlpha`.
    [<Literal>]
    let minimumReadableTintAlpha = 0.40

    let scaleRange = (0.0, 2.0)

    /// Clamps a raw token scale into the supported range.
    let clampScale (scale: float) = max 0.0 (min 2.0 scale)

    /// Piecewise-linear through the three defined points: slider 0, 50 and 100.
    /// Deliberately not a smooth curve — the anchors are the specification, and
    /// interpolating straight between them means the value at 25 and 75 is
    /// obvious rather than emergent.
    let private through (atZero: float) (atFifty: float) (atHundred: float) (scale: float) =
        let g = clampScale scale

        if g <= 1.0 then
            atZero + (atFifty - atZero) * g
        else
            atFifty + (atHundred - atFifty) * (g - 1.0)

    /// Opacity of the tint drawn over the backdrop.
    ///
    /// At 0 it is effectively opaque: that is the reduce-transparency escape
    /// hatch (docs §24) for users who cannot read text over moving imagery.
    let tintAlpha (scale: float) =
        max minimumReadableTintAlpha (through 0.98 0.64 minimumReadableTintAlpha scale)

    /// **The floor for a primary action's tint.**
    ///
    /// `minimumReadableTintAlpha` was chosen for *surfaces*, where the constraint
    /// is that body text stays readable over a moving backdrop. A primary action
    /// has a second constraint the surface floor does not serve: it has to still
    /// look like the accent. At 0.40 the panel is 60% blurred backdrop, so an
    /// accent tint desaturates toward the background and a primary button becomes
    /// indistinguishable from a secondary one — reported in the first runtime
    /// review as the New Note button "appearing grey" while every token feeding
    /// it was correct.
    ///
    /// The value is **derived, not chosen**, and it sits inside a genuinely narrow
    /// window that `GlassPrimaryTintTests` computes:
    ///
    /// * **Lower bound — chroma.** Greyness is a chroma property, not a luminance
    ///   one, so WCAG contrast cannot see it: a washed-out accent and a neutral
    ///   grey can share a luminance while looking nothing alike. The test instead
    ///   requires the painted surface to keep at least 60% of the accent's chroma,
    ///   which needs roughly 0.70.
    /// * **Upper bound — label readability.** A bright accent such as amber takes
    ///   dark ink, so diluting it toward a dark backdrop moves the surface *toward*
    ///   its own label. At 0.70 Dark/amber measured 4.42 against the 4.5 AA floor.
    ///   Strengthening the tint fixes that rather than worsening it.
    ///
    /// The two constraints pull in opposite directions, and 0.85 is inside both
    /// for every accent in both themes. 0.95 is outside — wastefully strong for
    /// what the chroma rule needs, and it would stop reading as glass at all.
    ///
    /// Applies only to panels that opt in via `GlassPanel.MinTintAlpha`; ordinary
    /// surfaces keep `tintAlpha` untouched.
    [<Literal>]
    let primaryTintAlpha = 0.85

    /// The tint a panel actually paints: its own floor, or the shared curve,
    /// whichever is stronger. A floor of zero leaves the shared behaviour exactly
    /// as it was.
    let effectiveTintAlpha (minAlpha: float) (scale: float) =
        max (max 0.0 (min 1.0 minAlpha)) (tintAlpha scale)

    /// How strongly the shared blurred backdrop is composited into the panel.
    /// Zero at slider 0, so the escape hatch costs nothing to draw and cannot
    /// leak movement through the tint.
    let backdropAlpha (scale: float) = through 0.0 0.85 1.0 scale

    /// True when the panel should sample the shared backdrop at all.
    let usesBackdrop (scale: float) = backdropAlpha scale > 0.01

    /// Hairline border. It gets stronger as the fill gets weaker: a nearly
    /// transparent panel needs its edge to define the shape.
    let borderAlpha (scale: float) = through 0.10 0.15 0.24 scale

    /// The hairline's COLOUR, which is not the same in both themes.
    ///
    /// The border was hard-coded white. On a dark card that is a light edge and
    /// reads correctly; on the Light theme's `#FDFAF4` card, white at 15%
    /// composites to `#FDFBF6` — a contrast ratio of **1.01:1** against the card
    /// it is supposed to be outlining. Every glass card, every glass action
    /// button and the floating sidebar therefore had no visible edge at all in
    /// Light mode, which is why a light card read as an unbounded pale area
    /// rather than as a card.
    ///
    /// Light takes the material `Border` role's own hue — the warm dark brown
    /// the design system already uses for a hairline on a light surface — so
    /// this is the palette's answer, not a new colour.
    let hairline (mode: ThemeMode) =
        match mode with
        | ThemeMode.Light -> Color.FromRgb(0x59uy, 0x4Buy, 0x36uy)
        | ThemeMode.Dark -> Colors.White

    /// Per-theme weight for that hairline.
    ///
    /// A dark line on a light surface has to be laid on more thickly than a
    /// light line on a dark one to read as the same restrained edge: at the
    /// default glass setting the Light hairline lands at 1.63:1 against the
    /// card, which is the weight a light-theme card edge conventionally has —
    /// clearly present, still quiet. Applying the Dark alpha unchanged would
    /// leave it at 1.26:1, which is a smudge rather than an edge.
    let hairlineAlphaScale (mode: ThemeMode) =
        match mode with
        | ThemeMode.Light -> 2.0
        | ThemeMode.Dark -> 1.0

    /// Inner highlight along the top edge — the specular line that reads as a
    /// lit pane. Absent from the opaque escape hatch, where there is no pane.
    let highlightAlpha (scale: float) = through 0.0 0.07 0.14 scale

    /// How far down the panel the specular gradient reaches, as a fraction of
    /// its height. Named because it is the reason a small alpha still reads as a
    /// *region* rather than an edge: at 0.38 the white covers the top third of
    /// every card, so scaling the alpha down makes the band fainter but no
    /// smaller. Removing the pass is the only way to remove the band.
    [<Literal>]
    let highlightReach = 0.38

    /// The smallest highlight alpha worth drawing.
    ///
    /// Below this the specular pass costs a shader allocation and a full-panel
    /// draw to deposit at most one or two 8-bit levels of white — invisible on
    /// its own, and cumulative across a page of ten cards. The old guard was
    /// 0.004, which still admitted alphas that quantise to 1/255, so "nearly
    /// zero" was drawn rather than skipped.
    [<Literal>]
    let minimumVisibleHighlightAlpha = 0.012

    /// Whether the specular pass should be drawn at all.
    let drawsHighlight (alpha: float) = alpha >= minimumVisibleHighlightAlpha

    /// Multiplier on the elevation shadow's opacity.
    let shadowAlphaScale (scale: float) = through 0.55 1.0 1.30 scale

    /// Shadow blur sigma in px: heavier glass floats higher and softer.
    let shadowSigma (scale: float) = through 8.0 12.0 18.0 scale

    /// Sigma of the one shared backdrop blur, in logical px. This is the "blur
    /// contribution" of the glass setting — it is applied once per frame in
    /// BackdropPipeline and sampled by every panel, so raising it is O(1) in the
    /// number of panels.
    let backdropSigma (scale: float) = through 10.0 18.0 26.0 scale
