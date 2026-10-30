namespace UniversityOS.Domain

/// Appearance preferences: the persisted half of the design-token system.
///
/// Deliberately a separate record from UserSettings. UserSettings' serialized
/// shape must stay stable or previously persisted JSON fails to deserialize,
/// and appearance adds fourteen fields — so it gets
/// its own settings row, the same way UiState does.
///
/// This record is VERSIONED. Read the migration rules in
/// docs/PRESENTATION-ARCHITECTURE.md §14.2 before changing it: never reuse a
/// field name, always add a migration step, and always keep loading total.

/// How fast time-based motion runs. Shared by UI animation and the background.
[<RequireQualifiedAccess>]
type Speed =
    | Slow
    | Normal
    | Fast

/// Spacing scale applied to page padding and control density.
[<RequireQualifiedAccess>]
type Density =
    | Compact
    | Comfortable
    | Spacious

/// Maximum measure for long-form reading columns.
[<RequireQualifiedAccess>]
type ReadingWidth =
    | Narrow
    | Medium
    | Wide

/// Which animated background is drawn. Adaptive picks per section.
[<RequireQualifiedAccess>]
type BackgroundStyle =
    | Adaptive
    | Aurora
    | LayeredWaves
    | LiquidGlass
    | FoldedGlass
    | None

/// Theme choice including "follow the OS", which UserSettings.ThemeMode cannot
/// express. Resolved to a concrete ThemeMode before the app applies it.
[<RequireQualifiedAccess>]
type ThemePreference =
    | Dark
    | Light
    | System

type AppearanceSettings =
    { /// Schema version of the persisted record. See AppearanceSettings.currentVersion.
      Version: int
      /// Named accent preset; unknown values resolve to the default accent.
      AccentKey: string
      /// The ONE authoritative theme choice. UserSettings.Theme is retained
      /// only so previously persisted JSON keeps deserializing; nothing reads
      /// it (see Design/Tokens.fs, themeModeFor).
      Theme: ThemePreference
      /// 0–100. Drives --glass-scale as value/50, so 50 is "unscaled": 0 is the
      /// opaque reduce-transparency escape hatch, 50 the balanced default and
      /// 100 the strongest approved glass.
      GlassIntensity: int
      /// Corner radius in px. Drives --radius-scale as value/14.
      CornerRadius: int
      AnimationSpeed: Speed
      Density: Density
      ReadingWidth: ReadingWidth
      BackgroundStyle: BackgroundStyle
      BackgroundSpeed: Speed
      /// 0–100, eased before use.
      BackgroundOpacity: int
      /// 0–100, scaled by the per-mode blur cap.
      BackgroundBlur: int
      /// 0–100, eased before use.
      BackgroundIntensity: int
      BackgroundParallax: bool
      /// User-level reduce motion. The OS setting is honoured independently.
      ReduceMotion: bool }

[<RequireQualifiedAccess>]
module Speed =
    let all = [ Speed.Slow; Speed.Normal; Speed.Fast ]

    let label =
        function
        | Speed.Slow -> "Slow"
        | Speed.Normal -> "Normal"
        | Speed.Fast -> "Fast"

[<RequireQualifiedAccess>]
module Density =
    let all = [ Density.Compact; Density.Comfortable; Density.Spacious ]

    let label =
        function
        | Density.Compact -> "Compact"
        | Density.Comfortable -> "Comfortable"
        | Density.Spacious -> "Spacious"

[<RequireQualifiedAccess>]
module ReadingWidth =
    let all = [ ReadingWidth.Narrow; ReadingWidth.Medium; ReadingWidth.Wide ]

    let label =
        function
        | ReadingWidth.Narrow -> "Narrow"
        | ReadingWidth.Medium -> "Medium"
        | ReadingWidth.Wide -> "Wide"

[<RequireQualifiedAccess>]
module BackgroundStyle =
    let all =
        [ BackgroundStyle.Adaptive
          BackgroundStyle.Aurora
          BackgroundStyle.LayeredWaves
          BackgroundStyle.LiquidGlass
          BackgroundStyle.FoldedGlass
          BackgroundStyle.None ]

    let label =
        function
        | BackgroundStyle.Adaptive -> "Adaptive"
        | BackgroundStyle.Aurora -> "Aurora"
        | BackgroundStyle.LayeredWaves -> "Layered Waves"
        | BackgroundStyle.LiquidGlass -> "Liquid Glass"
        | BackgroundStyle.FoldedGlass -> "Folded Glass"
        | BackgroundStyle.None -> "None"

[<RequireQualifiedAccess>]
module ThemePreference =
    let all = [ ThemePreference.Dark; ThemePreference.Light; ThemePreference.System ]

    let label =
        function
        | ThemePreference.Dark -> "Dark"
        | ThemePreference.Light -> "Light"
        | ThemePreference.System -> "System"

    /// Collapses to the concrete mode the rest of the app applies. "System"
    /// needs the OS preference, which only the presentation layer knows.
    let resolve (osPrefersDark: bool) (preference: ThemePreference) =
        match preference with
        | ThemePreference.Dark -> ThemeMode.Dark
        | ThemePreference.Light -> ThemeMode.Light
        | ThemePreference.System -> if osPrefersDark then ThemeMode.Dark else ThemeMode.Light

[<RequireQualifiedAccess>]
module AppearanceSettings =

    /// Bump this and add a migration step whenever the shape changes.
    let currentVersion = 1

    /// The default accent when none is set or the stored key is unknown.
    let defaultAccentKey = "amber"

    let defaults =
        { Version = currentVersion
          AccentKey = defaultAccentKey
          Theme = ThemePreference.Dark
          GlassIntensity = 50
          CornerRadius = 14
          AnimationSpeed = Speed.Normal
          Density = Density.Comfortable
          ReadingWidth = ReadingWidth.Medium
          BackgroundStyle = BackgroundStyle.Adaptive
          BackgroundSpeed = Speed.Normal
          BackgroundOpacity = 100
          BackgroundBlur = 30
          BackgroundIntensity = 100
          BackgroundParallax = false
          ReduceMotion = false }

    let private clamp lo hi (v: int) = max lo (min hi v)

    /// Forces every numeric field into its supported range. Applied after load
    /// so a hand-edited or migrated record can never produce invalid rendering
    /// parameters (a negative blur radius throws inside Skia).
    let sanitize (s: AppearanceSettings) =
        { s with
            Version = currentVersion
            AccentKey = if System.String.IsNullOrWhiteSpace s.AccentKey then defaultAccentKey else s.AccentKey
            GlassIntensity = clamp 0 100 s.GlassIntensity
            // 0 = square corners; 28 keeps radius scaling sane on small controls.
            CornerRadius = clamp 0 28 s.CornerRadius
            BackgroundOpacity = clamp 0 100 s.BackgroundOpacity
            BackgroundBlur = clamp 0 100 s.BackgroundBlur
            BackgroundIntensity = clamp 0 100 s.BackgroundIntensity }
