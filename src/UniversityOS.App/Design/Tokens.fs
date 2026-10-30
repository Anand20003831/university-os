namespace UniversityOS.App.Design

open UniversityOS.Domain

/// The resolved design tokens every surface reads from. One record, rebuilt
/// whenever appearance changes, carrying a Generation that doubles as the
/// invalidation key for every derived cache (paints, shaders, brushes).
type Tokens =
    { Accent: Palette
      Theme: ThemeMode
      /// Blur/translucency multiplier, 0.0–2.0. 1.0 at the balanced default
      /// glass setting of 50. Consumed through the Glass module, never raw.
      GlassScale: float
      /// Corner-radius multiplier. 1.0 at the default radius of 14px.
      RadiusScale: float
      /// Duration multiplier: slow 1.55, normal 1.0, fast 0.6.
      AnimScale: float
      /// Spacing multiplier: compact 0.74, comfortable 1.0, spacious 1.32.
      Density: float
      /// Maximum measure for long-form reading columns, in px.
      ReadingWidth: float
      /// User-level reduce motion (the OS setting is applied on top).
      ReduceMotion: bool
      /// Bumped on every appearance change. The universal cache key.
      Generation: int }

[<RequireQualifiedAccess>]
module Tokens =

    /// DURATION multipliers for UI transitions — a bigger number means a LONGER
    /// animation, so Slow is the largest. This is the reciprocal sense of the
    /// background's RATE multipliers in BackgroundParams; the two are named and
    /// documented apart because conflating them is exactly how the background
    /// speed ended up inverted.
    [<Literal>]
    let slowDurationFactor = 1.55

    [<Literal>]
    let normalDurationFactor = 1.0

    [<Literal>]
    let fastDurationFactor = 0.6

    let animScale =
        function
        | Speed.Slow -> slowDurationFactor
        | Speed.Normal -> normalDurationFactor
        | Speed.Fast -> fastDurationFactor

    let densityScale =
        function
        | Density.Compact -> 0.74
        | Density.Comfortable -> 1.0
        | Density.Spacious -> 1.32

    let readingWidthPx =
        function
        | ReadingWidth.Narrow -> 640.0
        | ReadingWidth.Medium -> 820.0
        | ReadingWidth.Wide -> 1040.0

    /// The single authoritative derivation of the applied theme.
    ///
    /// Every path that applies a theme — the shell, the startup pre-render pass,
    /// token regeneration — goes through this one function. UserSettings.Theme
    /// is legacy persisted state and is deliberately not an input: it survives
    /// only so old settings JSON still deserializes.
    let themeModeFor (osPrefersDark: bool) (a: AppearanceSettings) =
        ThemePreference.resolve osPrefersDark a.Theme

    /// Projects persisted appearance into renderable tokens. osPrefersDark only
    /// matters when the user chose "System".
    let ofAppearance (osPrefersDark: bool) (generation: int) (a: AppearanceSettings) =
        { Accent = Palette.ofKey a.AccentKey
          Theme = themeModeFor osPrefersDark a
          GlassScale = float a.GlassIntensity / 50.0
          RadiusScale = float a.CornerRadius / 14.0
          AnimScale = animScale a.AnimationSpeed
          Density = densityScale a.Density
          ReadingWidth = readingWidthPx a.ReadingWidth
          ReduceMotion = a.ReduceMotion
          Generation = generation }

    let defaults = ofAppearance true 0 AppearanceSettings.defaults

    /// A design radius scaled by the user's corner-radius preference.
    let radius (t: Tokens) (basePx: float) = basePx * t.RadiusScale

    /// A design spacing value scaled by the user's density preference.
    let space (t: Tokens) (basePx: float) = basePx * t.Density
