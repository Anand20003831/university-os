module UniversityOS.Tests.AppearanceBehaviourTests

open Xunit
open UniversityOS.Domain
open UniversityOS.Infrastructure.Repositories
open UniversityOS.App.Design

/// The four Stage 1 defects that were not about pixels: theme switching, the
/// Glass slider, reduced motion as accessibility, and the parallax geometry.
///
/// Each group starts with the defect it exists to catch, because a test whose
/// motivation is lost gets deleted the first time it is inconvenient.

// ---- theme -----------------------------------------------------------------

[<Fact>]
let ``every theme preference resolves to a concrete mode`` () =
    Assert.Equal(ThemeMode.Dark, Tokens.themeModeFor false { AppearanceSettings.defaults with Theme = ThemePreference.Dark })
    Assert.Equal(ThemeMode.Light, Tokens.themeModeFor true { AppearanceSettings.defaults with Theme = ThemePreference.Light })

    // System is the only one that reads the OS, and it must read it in both
    // directions rather than defaulting one way.
    let system = { AppearanceSettings.defaults with Theme = ThemePreference.System }
    Assert.Equal(ThemeMode.Dark, Tokens.themeModeFor true system)
    Assert.Equal(ThemeMode.Light, Tokens.themeModeFor false system)

[<Fact>]
let ``switching theme regenerates the tokens and bumps the generation`` () =
    // The generation is the invalidation key for every derived cache. If it did
    // not move, custom-drawn controls would keep painting the old theme.
    let dark = Tokens.ofAppearance true 7 { AppearanceSettings.defaults with Theme = ThemePreference.Dark }
    let light = Tokens.ofAppearance true 8 { AppearanceSettings.defaults with Theme = ThemePreference.Light }

    Assert.Equal(ThemeMode.Dark, dark.Theme)
    Assert.Equal(ThemeMode.Light, light.Theme)
    Assert.NotEqual(dark.Generation, light.Generation)

[<Fact>]
let ``the theme survives a persistence round trip`` () =
    for preference in ThemePreference.all do
        let saved = { AppearanceSettings.defaults with Theme = preference }
        let loaded = AppearanceJson.decode (AppearanceJson.encode saved)

        Assert.Equal(preference, loaded.Theme)

        // And resolves the same way after reloading as it did before.
        Assert.Equal(Tokens.themeModeFor true saved, Tokens.themeModeFor true loaded)

[<Fact>]
let ``the legacy UserSettings theme cannot override the appearance theme`` () =
    // UserSettings.Theme is retained only so previously persisted JSON keeps
    // deserializing. It used to be applied at startup and again on every
    // Settings save, which is why choosing Light in the appearance panel did
    // nothing that survived. themeModeFor takes the appearance record and
    // nothing else — this test fails the moment anyone reintroduces the legacy
    // value as an input.
    let legacy = { UserSettings.defaults with Theme = ThemeMode.Dark }
    let appearance = { AppearanceSettings.defaults with Theme = ThemePreference.Light }

    Assert.Equal(ThemeMode.Light, Tokens.themeModeFor false appearance)
    Assert.Equal(ThemeMode.Light, (Tokens.ofAppearance false 1 appearance).Theme)

    // The field is still readable — it just has no say.
    Assert.Equal(ThemeMode.Dark, legacy.Theme)

// ---- glass -----------------------------------------------------------------

[<Fact>]
let ``the three defined glass points behave as specified`` () =
    // 0 = opaque escape hatch, 50 = balanced default, 100 = strongest approved.
    let atSlider value =
        (Tokens.ofAppearance true 0 { AppearanceSettings.defaults with GlassIntensity = value }).GlassScale

    Assert.Equal(0.0, atSlider 0, 6)
    Assert.Equal(1.0, atSlider 50, 6)
    Assert.Equal(2.0, atSlider 100, 6)

    // Opaque enough to read text over a moving background.
    Assert.True(Glass.tintAlpha (atSlider 0) >= 0.95, "Glass 0 must be effectively opaque")
    Assert.False(Glass.usesBackdrop (atSlider 0), "Glass 0 must not sample the backdrop at all")

    // Strongest, but never so transparent that the panel stops being a surface.
    Assert.True(Glass.tintAlpha (atSlider 100) >= Glass.minimumReadableTintAlpha)
    Assert.True(Glass.usesBackdrop (atSlider 100))

[<Fact>]
let ``glass drives every part of the material, not just the tint`` () =
    // The slider used to change one number. Each of these is a separate visual
    // channel, and all six must respond or the setting feels broken.
    let low = 0.0
    let mid = 1.0
    let high = 2.0

    Assert.True(Glass.tintAlpha low > Glass.tintAlpha mid)
    Assert.True(Glass.tintAlpha mid > Glass.tintAlpha high)

    Assert.True(Glass.backdropAlpha low < Glass.backdropAlpha mid)
    Assert.True(Glass.backdropAlpha mid < Glass.backdropAlpha high)

    Assert.True(Glass.borderAlpha low < Glass.borderAlpha high)
    Assert.True(Glass.highlightAlpha low < Glass.highlightAlpha high)
    Assert.True(Glass.shadowAlphaScale low < Glass.shadowAlphaScale high)
    Assert.True(Glass.shadowSigma low < Glass.shadowSigma high)
    Assert.True(Glass.backdropSigma low < Glass.backdropSigma high)

[<Fact>]
let ``glass responds at every quarter of the slider`` () =
    // Not just at the three anchors: a curve that only moved at the ends would
    // pass the test above and still feel dead under the mouse.
    let scales = [ 0; 25; 50; 75; 100 ] |> List.map (fun v -> float v / 50.0)
    let tints = scales |> List.map Glass.tintAlpha

    tints
    |> List.pairwise
    |> List.iter (fun (a, b) -> Assert.True(b < a, sprintf "tint must keep falling: %f then %f" a b))

    let sigmas = scales |> List.map Glass.backdropSigma

    sigmas
    |> List.pairwise
    |> List.iter (fun (a, b) -> Assert.True(b > a, sprintf "blur must keep rising: %f then %f" a b))

[<Fact>]
let ``glass values stay in range for absurd input`` () =
    // Tokens.GlassScale is a division, and a hand-edited settings row can make
    // it anything. Every consumer feeds a Skia colour channel or a blur sigma.
    for scale in [ -1000.0; -1.0; 0.0; 1.0; 2.0; 50.0; 1e9 ] do
        let alpha = Glass.tintAlpha scale
        Assert.InRange(alpha, Glass.minimumReadableTintAlpha, 1.0)
        Assert.InRange(Glass.backdropAlpha scale, 0.0, 1.0)
        Assert.InRange(Glass.borderAlpha scale, 0.0, 1.0)
        Assert.InRange(Glass.highlightAlpha scale, 0.0, 1.0)
        Assert.True(Glass.backdropSigma scale > 0.0)
        Assert.True(Glass.shadowSigma scale > 0.0)

[<Fact>]
let ``the glass setting survives a persistence round trip`` () =
    for value in [ 0; 25; 50; 75; 100 ] do
        let saved = { AppearanceSettings.defaults with GlassIntensity = value }
        Assert.Equal(value, (AppearanceJson.decode (AppearanceJson.encode saved)).GlassIntensity)

// ---- reduce motion ---------------------------------------------------------

[<Fact>]
let ``the OS reduced-motion preference is honoured whatever the app says`` () =
    // Deliberately asymmetric: the app setting can only ADD reduced motion.
    // There is no way for the application to switch it off for a user whose
    // operating system asked for it.
    Assert.True(Motion.reduceMotionActive false true)
    Assert.True(Motion.reduceMotionActive true true)
    Assert.True(Motion.reduceMotionActive true false)
    Assert.False(Motion.reduceMotionActive false false)

[<Fact>]
let ``reduce motion stops the background and the parallax`` () =
    let animates user os =
        Motion.backgroundAnimates user os false true

    let parallax user os =
        Motion.parallaxActive true user os false true

    Assert.True(animates false false)
    Assert.True(parallax false false)

    // App setting.
    Assert.False(animates true false)
    Assert.False(parallax true false)

    // OS setting.
    Assert.False(animates false true)
    Assert.False(parallax false true)

[<Fact>]
let ``reduce motion preserves the selected speed rather than overwriting it`` () =
    // Reduce Motion is not a fourth speed. Turning it on must not touch the
    // Slow/Normal/Fast choice, and turning it off must return to exactly it.
    let chosen = { AppearanceSettings.defaults with BackgroundSpeed = Speed.Fast }
    let reduced = { chosen with ReduceMotion = true }

    Assert.Equal(Speed.Fast, reduced.BackgroundSpeed)
    Assert.False(Motion.backgroundAnimates reduced.ReduceMotion false false true)

    let restored = { reduced with ReduceMotion = false }
    Assert.Equal(Speed.Fast, restored.BackgroundSpeed)
    Assert.True(Motion.backgroundAnimates restored.ReduceMotion false false true)

    Assert.Equal(
        BackgroundParams.speedMultiplier Speed.Fast,
        BackgroundParams.speedMultiplier restored.BackgroundSpeed,
        6
    )

    // And it survives a restart with reduced motion still on.
    let loaded = AppearanceJson.decode (AppearanceJson.encode reduced)
    Assert.Equal(Speed.Fast, loaded.BackgroundSpeed)
    Assert.True(loaded.ReduceMotion)

[<Fact>]
let ``an inactive window or a disabled background also stops motion`` () =
    Assert.False(Motion.backgroundAnimates false false false false)
    Assert.False(Motion.backgroundAnimates false false true true)
    Assert.False(Motion.parallaxActive true false false true true)

[<Fact>]
let ``parallax stays off when the user has not asked for it`` () =
    Assert.False(Motion.parallaxActive false false false false true)
    Assert.True(Motion.parallaxActive true false false false true)
