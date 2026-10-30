namespace UniversityOS.App.Design

open System
open UniversityOS.Domain

/// Which visual family a screen belongs to. Adaptive background selects by
/// section so moving between related screens keeps a stable sense of place.
type BackgroundSection =
    | Overview
    | Work
    | Knowledge
    | SystemArea

/// The background actually drawn, with Adaptive already resolved to a concrete
/// mode. Rendering only ever sees this type, never the user's preference.
type BackgroundMode =
    | Aurora
    | LayeredWaves
    | LiquidGlass
    | FoldedGlass
    | NoBackground

/// Pure background maths — the eased slider response, per-mode caps and the
/// adaptive mapping. Kept free of rendering so the numbers that decide how the
/// background looks are unit-testable on their own.
[<RequireQualifiedAccess>]
module BackgroundParams =

    let styleForSection =
        function
        | Overview -> Aurora
        | Work -> LayeredWaves
        | Knowledge -> LiquidGlass
        | SystemArea -> FoldedGlass

    let resolveMode (style: BackgroundStyle) (section: BackgroundSection) =
        match style with
        | BackgroundStyle.Adaptive -> styleForSection section
        | BackgroundStyle.Aurora -> Aurora
        | BackgroundStyle.LayeredWaves -> LayeredWaves
        | BackgroundStyle.LiquidGlass -> LiquidGlass
        | BackgroundStyle.FoldedGlass -> FoldedGlass
        | BackgroundStyle.None -> NoBackground

    /// Eased slider response: 0 -> 0, 50 -> 1, 100 -> 2. Gives fine control at
    /// the low end where the background is subtle and the difference matters.
    let scale (v: int) =
        let c = float (max 0 (min 100 v)) / 100.0
        1.0 - cos (Math.PI * c)

    /// Ceiling on how present each mode may become. These differ per mode
    /// because the modes have very different perceived weight at equal alpha.
    let opacityCap =
        function
        | Aurora -> 0.20
        | LayeredWaves -> 0.13
        | LiquidGlass -> 0.18
        | FoldedGlass -> 0.12
        | NoBackground -> 0.0

    let blurCap =
        function
        | Aurora -> 45.0
        | LayeredWaves -> 28.0
        | LiquidGlass -> 35.0
        | FoldedGlass -> 26.0
        | NoBackground -> 0.0

    /// RATE multipliers for the background clock: animation seconds accumulated
    /// per real second. A bigger number means the composition moves FASTER, so
    /// Slow is the smallest.
    ///
    /// These were previously written as 1.6 / 1.0 / 0.55 — duration factors,
    /// copied from Tokens.animScale, where a bigger number means a longer
    /// transition. Applied to a rate they inverted the labels: "Slow" advanced
    /// the clock 1.6x per second. Named constants now, in one sense only, and
    /// the duration factors live under names that say what they are.
    [<Literal>]
    let slowRate = 0.6

    [<Literal>]
    let normalRate = 1.0

    [<Literal>]
    let fastRate = 1.6

    let speedMultiplier =
        function
        | Speed.Slow -> slowRate
        | Speed.Normal -> normalRate
        | Speed.Fast -> fastRate

    /// Advances the background clock by one frame.
    ///
    /// Accumulating speed-scaled deltas — rather than multiplying a raw elapsed
    /// time by the current speed — is what makes changing speed continuous: the
    /// new rate applies to the next delta, and the time already accumulated is
    /// never rescaled, so the composition cannot jump.
    let advanceTime (current: float) (deltaSeconds: float) (speed: Speed) =
        current + max 0.0 deltaSeconds * speedMultiplier speed

    /// Adaptive mode dims the background on the knowledge graph so nodes and
    /// edges stay legible against it.
    let adaptiveMultiplier (style: BackgroundStyle) (isGraphScreen: bool) =
        if style = BackgroundStyle.Adaptive && isGraphScreen then 0.4 else 1.0

    /// Final composite opacity for the background layer.
    let opacityFraction (mode: BackgroundMode) (adaptiveMult: float) (opacity: int) (intensity: int) =
        let opF = scale opacity
        let inF = scale intensity
        opacityCap mode * adaptiveMult * opF * (0.62 + 0.38 * inF)

    /// Blur radius in px applied to the whole background layer.
    let blurPx (mode: BackgroundMode) (blur: int) =
        (float (max 0 (min 100 blur)) / 100.0) * blurCap mode
