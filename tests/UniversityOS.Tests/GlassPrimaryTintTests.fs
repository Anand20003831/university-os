module UniversityOS.Tests.GlassPrimaryTintTests

open System
open Xunit
open Avalonia.Media
open UniversityOS.Domain
open UniversityOS.App.Design

/// **A primary action has to keep looking like the accent.**
///
/// Reported from the first runtime review: the Notes *New Note* button appeared
/// grey. Every token feeding it was correct — `primary = true` reached both
/// `GlassPanel.tint` (the accent) and the label (`Theme.onAccent`), the hover
/// veil rests at zero opacity, nothing overpaints the tint, and the non-glass
/// fallback keeps the accent's RGB.
///
/// The cause is the tint's **strength**, not its colour. `Glass.tintAlpha`
/// interpolates 0.98 → 0.64 → **0.40** across the glass slider, floored at
/// `minimumReadableTintAlpha`. That floor was chosen for *surfaces*, where the
/// constraint is that body text stays readable over a moving backdrop. At 0.40 a
/// panel is 60% blurred backdrop, so an accent desaturates toward the background
/// and a primary action stops being distinguishable from a secondary one.
///
/// These tests **derive** the primary floor rather than asserting a chosen
/// number: they search for the smallest alpha that keeps primary and secondary
/// apart across every accent in both themes, and check `Glass.primaryTintAlpha`
/// meets it. The same search shows 0.40 does not.
///
/// **What this cannot establish:** how it looks. The real backdrop is a live
/// blurred image, and this models it with the shell surface — a fair stand-in
/// for the compositing arithmetic, but not a rendering. Whether the button now
/// reads as accent-coloured still needs the user's eyes.

/// **Greyness is a chroma property, not a luminance one.**
///
/// The first version of this file measured WCAG contrast between the primary and
/// secondary surfaces, and found them "distinguishable" even at 0.40 — because a
/// washed-out accent and a neutral grey can sit at almost the same luminance
/// while looking completely different in saturation. Contrast ratio is blind to
/// exactly the thing the user reported.
///
/// Chroma — the spread between the strongest and weakest channel — is what
/// survives or does not when a colour is diluted toward a near-neutral backdrop.
/// A primary action must retain most of its accent's chroma to still read as
/// that accent.
[<Literal>]
let private chromaRetention = 0.60

let private chroma (c: Color) =
    let r, g, b = float c.R, float c.G, float c.B
    (max r (max g b) - min r (min g b)) / 255.0

let private modes = [ ThemeMode.Dark; ThemeMode.Light ]

let private accents = Palette.accents |> List.map (fun (key, _) -> key, Palette.ofKey key)

/// What a panel actually paints: its tint composited over the backdrop at the
/// given alpha. The backdrop is modelled by the shell surface.
let private composited (mode: ThemeMode) (backdrop: Color) (tint: Color) (alpha: float) =
    let a = byte (max 0.0 (min 255.0 (alpha * 255.0)))
    ignore mode
    Palette.composite backdrop (Color.FromArgb(a, tint.R, tint.G, tint.B))

/// True when a primary action painted at `alpha` still carries enough of its
/// accent's chroma to read as that accent rather than as a neutral panel.
let private separatedAt (alpha: float) =
    modes
    |> List.forall (fun mode ->
        accents
        |> List.forall (fun (_, accent) ->
            let s = Surfaces.resolve mode accent
            let painted = composited mode s.Shell accent.Normal alpha
            chroma painted >= chromaRetention * chroma accent.Normal))

/// The smallest alpha, to two decimal places, at which every accent in both
/// themes still separates.
let private derivedMinimum =
    [ 40..100 ]
    |> List.map (fun i -> float i / 100.0)
    |> List.tryFind separatedAt

// ---------------------------------------------------------------------------
// the derivation
// ---------------------------------------------------------------------------

[<Fact>]
let ``a primary tint floor is genuinely required — the shared floor does not separate`` () =
    Assert.False(
        separatedAt Glass.minimumReadableTintAlpha,
        sprintf
            "at the shared floor of %.2f a primary action is already distinguishable from a secondary one, so no primary contract would be needed"
            Glass.minimumReadableTintAlpha
    )

[<Fact>]
let ``the primary floor meets the derived minimum`` () =
    match derivedMinimum with
    | None -> failwith "no alpha in 0.40-1.00 separates primary from secondary — the model or the palette is wrong"
    | Some minimum ->
        Assert.True(
            Glass.primaryTintAlpha >= minimum,
            sprintf "Glass.primaryTintAlpha is %.2f but %.2f is needed to keep primary and secondary apart" Glass.primaryTintAlpha minimum
        )

/// The floor must not be higher than it needs to be either — an opaque primary
/// button would simply stop being glass. A quarter above the derived minimum is
/// the allowance.
[<Fact>]
let ``the primary floor is not wastefully high`` () =
    match derivedMinimum with
    | None -> failwith "no separating alpha found"
    | Some minimum ->
        Assert.True(
            Glass.primaryTintAlpha <= min 1.0 (minimum + 0.25),
            sprintf "Glass.primaryTintAlpha is %.2f, far above the %.2f actually required" Glass.primaryTintAlpha minimum
        )

// ---------------------------------------------------------------------------
// the contract across the whole slider
// ---------------------------------------------------------------------------

/// The point of a floor: it holds at **every** glass setting, including the one
/// where the shared curve is weakest. Slider 0, 50 and 100 are the three defined
/// anchor points in `Glass`.
[<Theory>]
[<InlineData 0.0>]
[<InlineData 1.0>]
[<InlineData 2.0>]
let ``a primary action stays accent-coloured at every glass setting`` (scale: float) =
    let effective = Glass.effectiveTintAlpha Glass.primaryTintAlpha scale

    Assert.True(
        effective >= Glass.primaryTintAlpha,
        sprintf "at glass scale %.1f the primary tint fell to %.2f" scale effective
    )

    Assert.True(separatedAt effective, sprintf "primary and secondary are not distinguishable at glass scale %.1f" scale)

/// Secondary actions are untouched — the floor is opt-in, and an ordinary panel
/// must keep exactly the tint it had before. If this fails, the change stopped
/// being narrow.
[<Theory>]
[<InlineData 0.0>]
[<InlineData 1.0>]
[<InlineData 2.0>]
let ``an ordinary panel keeps the shared tint exactly`` (scale: float) =
    Assert.Equal(Glass.tintAlpha scale, Glass.effectiveTintAlpha 0.0 scale)

// ---------------------------------------------------------------------------
// readability is not traded away
// ---------------------------------------------------------------------------

/// Strengthening the tint moves the surface toward the pure accent, so the label
/// has to stay readable on it. Checked at the floor, where the surface is
/// furthest from pure accent, and at full strength.
[<Theory>]
[<InlineData 0.0>]
[<InlineData 2.0>]
let ``the primary label stays readable on the strengthened surface`` (scale: float) =
    let alpha = Glass.effectiveTintAlpha Glass.primaryTintAlpha scale

    for mode in modes do
        for key, accent in accents do
            let s = Surfaces.resolve mode accent
            let surface = composited mode s.Shell accent.Normal alpha
            let ink = s.OnAccent

            let ratio = Palette.contrastRatio surface ink

            Assert.True(
                ratio >= Palette.aaText,
                sprintf "%A/%s: the primary label measures %.2f on its own surface at glass %.1f" mode key ratio scale
            )

// ---------------------------------------------------------------------------
// the contract is actually used
// ---------------------------------------------------------------------------

/// **Found by mutation.** Everything above proves the contract is correct; none
/// of it proved anything *uses* it. Removing the opt-in from the Notes primary
/// action left the whole suite green, which meant the reported button could
/// silently go back to being grey.
[<Fact>]
let ``the Notes primary action opts into the primary tint floor`` () =
    let tree = AuditFixtures.render "Notes" (AuditHarness.contentWidth 1440.0)

    let floors =
        AuditHarness.all tree
        |> List.filter (fun v -> AuditHarness.typeName v = "GlassPanel")
        |> List.choose (fun v ->
            match AuditHarness.propOf "MinTintAlpha" v with
            | Some(:? float as f) -> Some f
            | _ -> None)

    Assert.True(
        floors |> List.exists (fun f -> f >= Glass.primaryTintAlpha),
        sprintf "no Notes glass panel carries the primary tint floor; floors found = %A" floors
    )

/// And the complement: secondary actions must NOT be raised, or the distinction
/// the floor exists to create disappears again.
[<Fact>]
let ``Notes secondary actions keep the shared tint`` () =
    let tree = AuditFixtures.render "Notes" (AuditHarness.contentWidth 1440.0)

    let floors =
        AuditHarness.all tree
        |> List.filter (fun v -> AuditHarness.typeName v = "GlassPanel")
        |> List.choose (fun v ->
            match AuditHarness.propOf "MinTintAlpha" v with
            | Some(:? float as f) -> Some f
            | _ -> None)

    Assert.True(
        floors |> List.exists (fun f -> f = 0.0),
        sprintf "every Notes glass panel is raised to a primary floor; floors found = %A" floors
    )
