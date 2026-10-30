module UniversityOS.Tests.AppearanceMigrationTests

open Xunit
open UniversityOS.Domain
open UniversityOS.Infrastructure.Repositories

/// Loading appearance must be TOTAL. A user's persisted preferences may have
/// been written by any past — or future — build of the application, and none of
/// those cases may block startup.

[<Fact>]
let ``a round trip preserves every field`` () =
    let custom =
        { AppearanceSettings.defaults with
            AccentKey = "purple"
            Theme = ThemePreference.Light
            GlassIntensity = 25
            CornerRadius = 22
            AnimationSpeed = Speed.Fast
            Density = Density.Spacious
            ReadingWidth = ReadingWidth.Wide
            BackgroundStyle = BackgroundStyle.FoldedGlass
            BackgroundSpeed = Speed.Slow
            BackgroundOpacity = 40
            BackgroundBlur = 70
            BackgroundIntensity = 15
            BackgroundParallax = true
            ReduceMotion = true }

    Assert.Equal(custom, AppearanceJson.decode (AppearanceJson.encode custom))

[<Fact>]
let ``the current schema version is stamped on write`` () =
    let json = AppearanceJson.encode AppearanceSettings.defaults
    Assert.Contains(sprintf "\"Version\":%d" AppearanceSettings.currentVersion, json)

[<Fact>]
let ``a record missing fields loads with documented defaults`` () =
    // Rule 3: an older build wrote fewer fields; everything absent defaults.
    let partial = """{"Version":1,"AccentKey":"teal","GlassIntensity":10}"""
    let loaded = AppearanceJson.decode partial

    Assert.Equal("teal", loaded.AccentKey)
    Assert.Equal(10, loaded.GlassIntensity)
    Assert.Equal(AppearanceSettings.defaults.CornerRadius, loaded.CornerRadius)
    Assert.Equal(AppearanceSettings.defaults.BackgroundStyle, loaded.BackgroundStyle)
    Assert.Equal(AppearanceSettings.defaults.Density, loaded.Density)

[<Fact>]
let ``an unversioned record is read as v1 rather than rejected`` () =
    let unversioned = """{"AccentKey":"pink","CornerRadius":20}"""
    let loaded = AppearanceJson.decode unversioned

    Assert.Equal("pink", loaded.AccentKey)
    Assert.Equal(20, loaded.CornerRadius)
    Assert.Equal(AppearanceSettings.currentVersion, loaded.Version)

[<Fact>]
let ``unknown enum values fall back to the nearest sensible default`` () =
    // Rule 4: a style added by a newer build must still load on this one.
    let futureStyle =
        """{"Version":1,"BackgroundStyle":"holographic","Density":"enormous","Theme":"sepia","AnimationSpeed":"warp"}"""

    let loaded = AppearanceJson.decode futureStyle

    Assert.Equal(BackgroundStyle.Adaptive, loaded.BackgroundStyle)
    Assert.Equal(Density.Comfortable, loaded.Density)
    Assert.Equal(ThemePreference.Dark, loaded.Theme)
    Assert.Equal(Speed.Normal, loaded.AnimationSpeed)

[<Fact>]
let ``a record from a newer schema loads defaults instead of guessing`` () =
    // Rule 5: the user downgraded the app. Load defaults; the caller does not
    // overwrite the stored row, so upgrading again restores their settings.
    let future = """{"Version":9999,"AccentKey":"purple","GlassIntensity":3}"""
    Assert.Equal(AppearanceSettings.defaults, AppearanceJson.decode future)

[<Fact>]
let ``corrupt, truncated, empty and foreign payloads all load defaults`` () =
    // Rule 6: any failure yields defaults, never an exception.
    for payload in
        [ ""
          "   "
          "{"
          "not json at all"
          "[1,2,3]"
          "null"
          "12345"
          """{"Version":"not-a-number"}"""
          """{"Version":1,"GlassIntensity":"loads"}""" ] do
        let loaded = AppearanceJson.decode payload
        Assert.Equal(AppearanceSettings.defaults.AccentKey, loaded.AccentKey)
        Assert.Equal(AppearanceSettings.currentVersion, loaded.Version)

[<Fact>]
let ``out-of-range numbers are clamped into their supported ranges`` () =
    // A hand-edited file must not be able to produce a negative blur radius,
    // which Skia rejects outright.
    let wild =
        """{"Version":1,"GlassIntensity":-50,"CornerRadius":9999,"BackgroundOpacity":300,"BackgroundBlur":-1,"BackgroundIntensity":10000}"""

    let loaded = AppearanceJson.decode wild

    Assert.Equal(0, loaded.GlassIntensity)
    Assert.Equal(28, loaded.CornerRadius)
    Assert.Equal(100, loaded.BackgroundOpacity)
    Assert.Equal(0, loaded.BackgroundBlur)
    Assert.Equal(100, loaded.BackgroundIntensity)

[<Fact>]
let ``a blank accent key falls back to the default accent`` () =
    let blank = """{"Version":1,"AccentKey":"   "}"""
    Assert.Equal(AppearanceSettings.defaultAccentKey, (AppearanceJson.decode blank).AccentKey)
