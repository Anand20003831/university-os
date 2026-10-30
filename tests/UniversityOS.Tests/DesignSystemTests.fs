module UniversityOS.Tests.DesignSystemTests

open Xunit
open UniversityOS.Domain
open UniversityOS.App.Design

/// The pure design-token core: accent palette generation, token derivation and
/// motion durations. No Avalonia service is started here — Color is a value
/// struct, so this stays a fast unit test.

// ---- palette ---------------------------------------------------------------

[<Fact>]
let ``every accent preset parses and generates a full palette`` () =
    for (key, hex) in Palette.accents do
        match Palette.tryParseHex hex with
        | None -> failwithf "preset %s has an unparseable hex %s" key hex
        | Some colour ->
            let palette = Palette.generate colour
            Assert.Equal(colour, palette.Normal)
            // Derived tints must actually differ from the base.
            Assert.NotEqual(palette.Normal, palette.Light)
            Assert.NotEqual(palette.Normal, palette.Dark)

[<Fact>]
let ``palette derivation follows the specified lightness relationships`` () =
    // Amber #CB8A4E is the default accent.
    let palette = Palette.ofKey "amber"

    let luminance (c: Avalonia.Media.Color) =
        (0.2126 * float c.R + 0.7152 * float c.G + 0.0722 * float c.B) / 255.0

    // light and vlight move towards white; dark moves towards black.
    Assert.True(luminance palette.Light > luminance palette.Normal, "light must be lighter than normal")
    Assert.True(luminance palette.VLight > luminance palette.Light, "vlight must be lighter than light")
    Assert.True(luminance palette.Dark < luminance palette.Normal, "dark must be darker than normal")

[<Fact>]
let ``alpha ladder carries the accent hue at the specified opacities`` () =
    let palette = Palette.ofKey "blue"

    // The ladder keeps RGB and varies only alpha.
    for entry in [ palette.B10; palette.B25; palette.B45; palette.B55 ] do
        Assert.Equal(palette.Normal.R, entry.R)
        Assert.Equal(palette.Normal.G, entry.G)
        Assert.Equal(palette.Normal.B, entry.B)

    Assert.Equal(byte (System.Math.Round(0.10 * 255.0)), palette.B10.A)
    Assert.Equal(byte (System.Math.Round(0.55 * 255.0)), palette.B55.A)
    Assert.Equal(byte (System.Math.Round(0.078 * 255.0)), palette.A14.A)

[<Fact>]
let ``an unknown accent key falls back to the default rather than throwing`` () =
    Assert.Equal(Palette.ofKey "amber", Palette.ofKey "not-a-real-accent")

[<Fact>]
let ``hex parsing is total`` () =
    Assert.True((Palette.tryParseHex "#CB8A4E").IsSome)
    Assert.True((Palette.tryParseHex "CB8A4E").IsSome)
    Assert.True((Palette.tryParseHex "").IsNone)
    Assert.True((Palette.tryParseHex "#GGGGGG").IsNone)
    Assert.True((Palette.tryParseHex null).IsNone)

// ---- tokens ----------------------------------------------------------------

[<Fact>]
let ``token scales are 1.0 at the documented defaults`` () =
    let tokens = Tokens.ofAppearance true 0 AppearanceSettings.defaults

    // Glass 50 and radius 14 are the "unscaled" points of the design system.
    Assert.Equal(1.0, tokens.GlassScale, 3)
    Assert.Equal(1.0, tokens.RadiusScale, 3)
    Assert.Equal(1.0, tokens.AnimScale, 3)
    Assert.Equal(1.0, tokens.Density, 3)
    Assert.Equal(820.0, tokens.ReadingWidth, 3)

[<Fact>]
let ``token scales track their settings at the boundaries`` () =
    let at settings = Tokens.ofAppearance true 0 settings

    let glassOff = at { AppearanceSettings.defaults with GlassIntensity = 0 }
    Assert.Equal(0.0, glassOff.GlassScale, 3)

    let glassMax = at { AppearanceSettings.defaults with GlassIntensity = 100 }
    Assert.Equal(2.0, glassMax.GlassScale, 3)

    let squared = at { AppearanceSettings.defaults with CornerRadius = 0 }
    Assert.Equal(0.0, squared.RadiusScale, 3)

    Assert.Equal(1.55, (at { AppearanceSettings.defaults with AnimationSpeed = Speed.Slow }).AnimScale, 3)
    Assert.Equal(0.6, (at { AppearanceSettings.defaults with AnimationSpeed = Speed.Fast }).AnimScale, 3)
    Assert.Equal(0.74, (at { AppearanceSettings.defaults with Density = Density.Compact }).Density, 3)
    Assert.Equal(1.32, (at { AppearanceSettings.defaults with Density = Density.Spacious }).Density, 3)
    Assert.Equal(640.0, (at { AppearanceSettings.defaults with ReadingWidth = ReadingWidth.Narrow }).ReadingWidth, 3)
    Assert.Equal(1040.0, (at { AppearanceSettings.defaults with ReadingWidth = ReadingWidth.Wide }).ReadingWidth, 3)

[<Fact>]
let ``system theme resolves against the OS preference`` () =
    let systemTheme = { AppearanceSettings.defaults with Theme = ThemePreference.System }
    Assert.Equal(ThemeMode.Dark, (Tokens.ofAppearance true 0 systemTheme).Theme)
    Assert.Equal(ThemeMode.Light, (Tokens.ofAppearance false 0 systemTheme).Theme)

    // An explicit choice ignores the OS.
    let explicitLight = { AppearanceSettings.defaults with Theme = ThemePreference.Light }
    Assert.Equal(ThemeMode.Light, (Tokens.ofAppearance true 0 explicitLight).Theme)

[<Fact>]
let ``radius and spacing helpers apply their scales`` () =
    let spacious =
        Tokens.ofAppearance true 0 { AppearanceSettings.defaults with CornerRadius = 28; Density = Density.Spacious }

    Assert.Equal(28.0, Tokens.radius spacious 14.0, 3)
    Assert.Equal(21.12, Tokens.space spacious 16.0, 3)

// ---- typography ------------------------------------------------------------

[<Fact>]
let ``the application typeface is Inter, resolved through the design system`` () =
    // Inter ships with Avalonia under SIL OFL 1.1 and is redistributable inside
    // this application. General Sans (the mock-up's face) is deliberately NOT
    // used: no file or licence for it exists in this repository.
    Assert.StartsWith("Inter", Typography.uiFamilyName)
    Assert.DoesNotContain("General Sans", Typography.uiFamilyName)
    Assert.DoesNotContain("General Sans", Typography.monoFamilyName)

    // Fallbacks must exist so a missing font never renders as blank boxes.
    Assert.Contains(",", Typography.uiFamilyName)
    Assert.Contains("monospace", Typography.monoFamilyName)

    // The FontFamily values resolve without an Avalonia application running.
    Assert.NotNull(Typography.ui)
    Assert.NotNull(Typography.mono)

// ---- motion ----------------------------------------------------------------

[<Fact>]
let ``durations scale with the animation-speed preference`` () =
    let at speed =
        Tokens.ofAppearance true 0 { AppearanceSettings.defaults with AnimationSpeed = speed }

    Assert.Equal(240.0, (Motion.durBase(at Speed.Normal)).TotalMilliseconds, 1)
    Assert.Equal(140.0, (Motion.durFast(at Speed.Normal)).TotalMilliseconds, 1)
    Assert.Equal(372.0, (Motion.durBase(at Speed.Slow)).TotalMilliseconds, 1)
    Assert.Equal(144.0, (Motion.durBase(at Speed.Fast)).TotalMilliseconds, 1)

[<Fact>]
let ``the motion gate honours both the app and the OS preference`` () =
    let normal = Tokens.ofAppearance true 0 AppearanceSettings.defaults
    let reduced = Tokens.ofAppearance true 0 { AppearanceSettings.defaults with ReduceMotion = true }

    Assert.True(Motion.enabled normal false)
    // Either source alone disables motion.
    Assert.False(Motion.enabled reduced false)
    Assert.False(Motion.enabled normal true)
    Assert.False(Motion.enabled reduced true)

[<Fact>]
let ``gated durations collapse to zero when motion is disabled`` () =
    let tokens = Tokens.ofAppearance true 0 AppearanceSettings.defaults
    Assert.Equal(System.TimeSpan.Zero, Motion.gated tokens true (Motion.durBase tokens))
    Assert.Equal(Motion.durBase tokens, Motion.gated tokens false (Motion.durBase tokens))
