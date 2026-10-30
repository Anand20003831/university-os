namespace UniversityOS.Infrastructure.Repositories

open System.Text.Json
open System.Text.Json.Nodes
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.Infrastructure

/// Versioned JSON codec for AppearanceSettings.
///
/// Deliberately hand-written rather than reflection-based: appearance is the one
/// record we must be able to load from *any* past version of the app, and that
/// requires field-level defaulting and total enum parsing. Enums persist as
/// plain lowercase strings rather than F# union encodings so the payload stays
/// readable and independent of the serializer's union representation.
///
/// Migration rules — docs/PRESENTATION-ARCHITECTURE.md §14.2:
///   1. Bump currentVersion and add a readVN + migration step.
///   2. Never reuse or repurpose a field name.
///   3. Missing field -> documented default.
///   4. Unknown enum value -> nearest sensible default.
///   5. Version newer than current -> defaults, and don't overwrite the stored row.
///   6. Any failure -> defaults, never an exception.
[<RequireQualifiedAccess>]
module AppearanceJson =

    // ---- total enum codecs -------------------------------------------------

    let private speedToString =
        function
        | Speed.Slow -> "slow"
        | Speed.Normal -> "normal"
        | Speed.Fast -> "fast"

    let private speedOfString =
        function
        | "slow" -> Speed.Slow
        | "fast" -> Speed.Fast
        | _ -> Speed.Normal

    let private densityToString =
        function
        | Density.Compact -> "compact"
        | Density.Comfortable -> "comfortable"
        | Density.Spacious -> "spacious"

    let private densityOfString =
        function
        | "compact" -> Density.Compact
        | "spacious" -> Density.Spacious
        | _ -> Density.Comfortable

    let private readingWidthToString =
        function
        | ReadingWidth.Narrow -> "narrow"
        | ReadingWidth.Medium -> "medium"
        | ReadingWidth.Wide -> "wide"

    let private readingWidthOfString =
        function
        | "narrow" -> ReadingWidth.Narrow
        | "wide" -> ReadingWidth.Wide
        | _ -> ReadingWidth.Medium

    let private backgroundStyleToString =
        function
        | BackgroundStyle.Adaptive -> "adaptive"
        | BackgroundStyle.Aurora -> "aurora"
        | BackgroundStyle.LayeredWaves -> "layeredWaves"
        | BackgroundStyle.LiquidGlass -> "liquidGlass"
        | BackgroundStyle.FoldedGlass -> "foldedGlass"
        | BackgroundStyle.None -> "none"

    let private backgroundStyleOfString =
        function
        | "aurora" -> BackgroundStyle.Aurora
        | "layeredWaves" -> BackgroundStyle.LayeredWaves
        | "liquidGlass" -> BackgroundStyle.LiquidGlass
        | "foldedGlass" -> BackgroundStyle.FoldedGlass
        | "none" -> BackgroundStyle.None
        // Unknown (e.g. a style added by a newer build) falls back to Adaptive.
        | _ -> BackgroundStyle.Adaptive

    let private themeToString =
        function
        | ThemePreference.Dark -> "dark"
        | ThemePreference.Light -> "light"
        | ThemePreference.System -> "system"

    let private themeOfString =
        function
        | "light" -> ThemePreference.Light
        | "system" -> ThemePreference.System
        | _ -> ThemePreference.Dark

    // ---- total field readers -----------------------------------------------

    let private readInt (root: JsonElement) (name: string) (fallback: int) =
        match root.TryGetProperty name with
        | true, el when el.ValueKind = JsonValueKind.Number ->
            match el.TryGetInt32() with
            | true, v -> v
            | _ -> fallback
        | _ -> fallback

    let private readBool (root: JsonElement) (name: string) (fallback: bool) =
        match root.TryGetProperty name with
        | true, el when el.ValueKind = JsonValueKind.True || el.ValueKind = JsonValueKind.False -> el.GetBoolean()
        | _ -> fallback

    let private readString (root: JsonElement) (name: string) (fallback: string) =
        match root.TryGetProperty name with
        | true, el when el.ValueKind = JsonValueKind.String ->
            match el.GetString() with
            | null -> fallback
            | s -> s
        | _ -> fallback

    // ---- versioned readers -------------------------------------------------

    /// Shape of schema v1. An unversioned (v0) record is read the same way —
    /// field-level defaults cover everything it omitted.
    let private readV1 (root: JsonElement) =
        let d = AppearanceSettings.defaults

        { Version = AppearanceSettings.currentVersion
          AccentKey = readString root "AccentKey" d.AccentKey
          Theme = readString root "Theme" (themeToString d.Theme) |> themeOfString
          GlassIntensity = readInt root "GlassIntensity" d.GlassIntensity
          CornerRadius = readInt root "CornerRadius" d.CornerRadius
          AnimationSpeed = readString root "AnimationSpeed" (speedToString d.AnimationSpeed) |> speedOfString
          Density = readString root "Density" (densityToString d.Density) |> densityOfString
          ReadingWidth = readString root "ReadingWidth" (readingWidthToString d.ReadingWidth) |> readingWidthOfString
          BackgroundStyle =
            readString root "BackgroundStyle" (backgroundStyleToString d.BackgroundStyle) |> backgroundStyleOfString
          BackgroundSpeed = readString root "BackgroundSpeed" (speedToString d.BackgroundSpeed) |> speedOfString
          BackgroundOpacity = readInt root "BackgroundOpacity" d.BackgroundOpacity
          BackgroundBlur = readInt root "BackgroundBlur" d.BackgroundBlur
          BackgroundIntensity = readInt root "BackgroundIntensity" d.BackgroundIntensity
          BackgroundParallax = readBool root "BackgroundParallax" d.BackgroundParallax
          ReduceMotion = readBool root "ReduceMotion" d.ReduceMotion }

    // ---- public surface ----------------------------------------------------

    let encode (s: AppearanceSettings) =
        let o = JsonObject()
        o["Version"] <- JsonValue.Create AppearanceSettings.currentVersion
        o["AccentKey"] <- JsonValue.Create s.AccentKey
        o["Theme"] <- JsonValue.Create(themeToString s.Theme)
        o["GlassIntensity"] <- JsonValue.Create s.GlassIntensity
        o["CornerRadius"] <- JsonValue.Create s.CornerRadius
        o["AnimationSpeed"] <- JsonValue.Create(speedToString s.AnimationSpeed)
        o["Density"] <- JsonValue.Create(densityToString s.Density)
        o["ReadingWidth"] <- JsonValue.Create(readingWidthToString s.ReadingWidth)
        o["BackgroundStyle"] <- JsonValue.Create(backgroundStyleToString s.BackgroundStyle)
        o["BackgroundSpeed"] <- JsonValue.Create(speedToString s.BackgroundSpeed)
        o["BackgroundOpacity"] <- JsonValue.Create s.BackgroundOpacity
        o["BackgroundBlur"] <- JsonValue.Create s.BackgroundBlur
        o["BackgroundIntensity"] <- JsonValue.Create s.BackgroundIntensity
        o["BackgroundParallax"] <- JsonValue.Create s.BackgroundParallax
        o["ReduceMotion"] <- JsonValue.Create s.ReduceMotion
        o.ToJsonString()

    /// Total. Any malformed, truncated, foreign or future payload yields
    /// defaults rather than throwing — appearance must never block startup.
    let decode (raw: string) =
        try
            use doc = JsonDocument.Parse raw
            let root = doc.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                AppearanceSettings.defaults
            else
                let version = readInt root "Version" 0

                if version > AppearanceSettings.currentVersion then
                    // Written by a newer build — the user downgraded. Load
                    // defaults and leave the stored row alone (Save only runs
                    // when the user actually changes something), so upgrading
                    // again restores their real preferences.
                    AppearanceSettings.defaults
                else
                    readV1 root |> AppearanceSettings.sanitize
        with _ ->
            AppearanceSettings.defaults

/// Appearance preferences in their own settings row — same pattern as
/// SettingsStore and UiStateStore (see Abstractions.fs).
type AppearanceStore(db: Database) =

    let appearanceKey = "appearance"

    interface IAppearanceStore with

        member _.Load() =
            async {
                let! row =
                    db.QueryOne(
                        "SELECT value FROM settings WHERE key = $key",
                        [ "$key", Sql.ofStr appearanceKey ],
                        fun r -> r.GetString 0
                    )

                return row |> Option.map AppearanceJson.decode |> Option.defaultValue AppearanceSettings.defaults
            }

        member _.Save(settings) =
            db.Execute(
                "INSERT INTO settings(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value=$value",
                [ "$key", Sql.ofStr appearanceKey
                  "$value", Sql.ofStr (AppearanceJson.encode settings) ]
            )
