module UniversityOS.Tests.SurfaceTests

open System
open Xunit
open Avalonia.Media
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Design

// ---------------------------------------------------------------------------
// The material palette.
//
// The claim under test is the whole point of the correction: **a surface is
// material, not state**. The application must look like the redesign — deep
// warm charcoal, brown-charcoal cards — under every accent, and the accent must
// show up only in the details.
//
// The tests this replaced asserted the opposite (`every accent produces a
// distinct card surface`, `cool accents stay cool`, `accentBlend is between
// 0.15 and 0.5`). That requirement was wrong: it is what made amber orange and
// blue blue-grey, so those tests are gone rather than relaxed.
// ---------------------------------------------------------------------------

let private accents = Palette.accents |> List.map fst

let private modes = [ ThemeMode.Dark; ThemeMode.Light ]

/// Every surface role in a resolved palette, named, so a failure says which one.
let private surfaceRoles (s: Surfaces) =
    [ "Window", s.Window
      "Shell", s.Shell
      "SidebarPinned", s.SidebarPinned
      "SidebarFloating", s.SidebarFloating
      "Card", s.Card
      "CardAlt", s.CardAlt
      "Action", s.Action
      "Hover", s.Hover
      "Selected", s.Selected
      "Overlay", s.Overlay ]

let private inkRoles (s: Surfaces) =
    [ "Text", s.Text
      "TextSecondary", s.TextSecondary
      "Muted", s.Muted
      "Label", s.Label
      "Faint", s.Faint ]

/// How far red leads blue. Positive is warm, negative is cool.
let private warmth (c: Color) = int c.R - int c.B

/// The largest per-channel distance between two colours.
let private channelDistance (a: Color) (b: Color) =
    List.max [ abs (int a.R - int b.R); abs (int a.G - int b.G); abs (int a.B - int b.B) ]

/// Chroma: the spread between the strongest and weakest channel, in 0–255.
///
/// Deliberately NOT HSL saturation. Saturation is a ratio, so at the lightness
/// these surfaces live at it exaggerates wildly — `#100D09`, which is visually
/// indistinguishable from black, measures 0.28, the same as a colour anyone
/// would call brown. Chroma is the absolute quantity of colour present, which
/// is what "the card must not look orange" actually means. For reference, the
/// 0.34 accent blend this correction removed produced a card at chroma 29
/// against a base of 7.
let private chroma (c: Color) =
    List.max [ int c.R; int c.G; int c.B ] - List.min [ int c.R; int c.G; int c.B ]

// ---- 1. the base family is warm-neutral and stable -------------------------

[<Fact>]
let ``every dark surface is a warm neutral, never blue-grey`` () =
    // The original defect. `#1C1E23` — the old shell chrome — has warmth -7:
    // blue leads red. Every material surface must lead the other way.
    Assert.True(warmth (Color.Parse "#1C1E23") < 0, "the colour this replaced really was blue-leaning")

    for name, c in surfaceRoles Surfaces.dark do
        Assert.True(warmth c > 0, sprintf "%s must be warm, got R=%d G=%d B=%d" name c.R c.G c.B)

[<Fact>]
let ``every light surface is warm parchment, not cold white or blue-grey`` () =
    for name, c in surfaceRoles Surfaces.light do
        Assert.True(warmth c > 0, sprintf "light %s must be warm, got R=%d G=%d B=%d" name c.R c.G c.B)

[<Fact>]
let ``the base surfaces carry almost no colour`` () =
    // Warmth is a hue statement; this is the intensity statement.
    for mode in modes do
        for name, c in surfaceRoles (Surfaces.baseFor mode) do
            Assert.True(chroma c <= 26, sprintf "%A %s has chroma %d" mode name (chroma c))

[<Fact>]
let ``harmonisation adds no visible colour to any surface`` () =
    // The decisive test. The removed 0.34 blend took the card from chroma 7 to
    // 29 — a jump of 22 — which is what "the entire interface is too orange"
    // measures as. Nothing may add more than a fraction of that.
    for mode in modes do
        let baseline = surfaceRoles (Surfaces.baseFor mode)

        for key in accents do
            let resolved = surfaceRoles (Surfaces.resolve mode (Palette.ofKey key))

            for (name, b), (_, r) in List.zip baseline resolved do
                let added = chroma r - chroma b

                Assert.True(
                    added <= 8,
                    sprintf "%A %s gained %d chroma under %s" mode name added key
                )

[<Fact>]
let ``changing the accent does not recolour any surface`` () =
    // The headline requirement. Across all six accents, every surface role must
    // stay within a handful of 0-255 steps of the unharmonised base — a number
    // small enough that the card is visibly the same material.
    for mode in modes do
        let baseline = Surfaces.baseFor mode

        for key in accents do
            let resolved = Surfaces.resolve mode (Palette.ofKey key)

            for (name, b), (_, r) in List.zip (surfaceRoles baseline) (surfaceRoles resolved) do
                Assert.True(
                    channelDistance b r <= 12,
                    sprintf "%A %s moved %d steps under %s" mode name (channelDistance b r) key
                )

[<Fact>]
let ``amber and blue produce the same card material`` () =
    // Stated as the two accents the user named: amber must not wash the card
    // orange, blue must not drag it back to blue-grey. They must be the same
    // material as each other and as the base.
    for mode in modes do
        let card key = (Surfaces.resolve mode (Palette.ofKey key)).Card

        Assert.True(
            channelDistance (card "amber") (card "blue") <= 12,
            sprintf "%A amber and blue cards differ by %d" mode (channelDistance (card "amber") (card "blue"))
        )

        Assert.True(warmth (card "blue") > 0, "the blue accent must not produce a cool card")

        Assert.True(
            chroma (card "amber") <= 26,
            sprintf "the amber card has chroma %d — that is an orange wash" (chroma (card "amber"))
        )

[<Fact>]
let ``the sidebar is as stable as the cards`` () =
    for mode in modes do
        for key in accents do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, c in [ "SidebarPinned", s.SidebarPinned; "SidebarFloating", s.SidebarFloating ] do
                Assert.True(warmth c > 0, sprintf "%A %s under %s must stay warm" mode name key)
                Assert.True(chroma c <= 26, sprintf "%A %s under %s has chroma %d" mode name key (chroma c))

[<Fact>]
let ``harmonisation is present but nearly invisible`` () =
    // Not zero — the surfaces would sit inertly against strongly coloured
    // linework — and not enough to see. Both halves are asserted, because a
    // future "simplification" to either extreme is the failure mode.
    Assert.InRange(Surfaces.accentHarmony, 0.02, 0.10)

    let amber = Palette.ofKey "amber"
    let plain = Surfaces.baseFor ThemeMode.Dark
    let tinted = Surfaces.resolve ThemeMode.Dark amber

    Assert.NotEqual(plain.Card, tinted.Card)
    Assert.True(channelDistance plain.Card tinted.Card <= 12)

[<Fact>]
let ``ink and overlays are never harmonised`` () =
    // Text tinted toward the accent is a legibility problem; a border or scrim
    // is an alpha overlay whose colour comes from what is behind it.
    for mode in modes do
        let plain = Surfaces.baseFor mode

        for key in accents do
            let r = Surfaces.resolve mode (Palette.ofKey key)

            for (name, a), (_, b) in List.zip (inkRoles plain) (inkRoles r) do
                Assert.True((a = b), sprintf "%A %s must not follow the accent" mode name)

            Assert.Equal(plain.Border, r.Border)
            Assert.Equal(plain.Divider, r.Divider)
            Assert.Equal(plain.Scrim, r.Scrim)

[<Fact>]
let ``blending preserves alpha, so overlays stay overlays`` () =
    let translucent = Color.FromArgb(0x14uy, 0xFFuy, 0xFFuy, 0xFFuy)
    let opaque = Color.FromRgb(0uy, 0uy, 0uy)

    Assert.Equal(0x14uy, (Palette.mix translucent opaque 0.5).A)

    // And it is still a real interpolation, clamped at both ends.
    let black = Color.FromRgb(0uy, 0uy, 0uy)
    let white = Color.FromRgb(255uy, 255uy, 255uy)

    Assert.Equal(black, Palette.mix black white 0.0)
    Assert.Equal(white, Palette.mix black white 1.0)
    Assert.Equal(127uy, (Palette.mix black white 0.5).R)
    Assert.Equal(black, Palette.mix black white -5.0)
    Assert.Equal(white, Palette.mix black white 5.0)

// ---- 2. the surface hierarchy is subtle but visible ------------------------

/// Perceived lightness, 0–1. Used for ordering surfaces, not for contrast.
let private lightness (c: Color) =
    (0.2126 * float c.R + 0.7152 * float c.G + 0.0722 * float c.B) / 255.0

[<Fact>]
let ``the dark hierarchy steps upward from window to hover`` () =
    let s = Surfaces.dark

    let ladder =
        [ "Window", s.Window; "Shell", s.Shell; "SidebarPinned", s.SidebarPinned; "Card", s.Card; "Hover", s.Hover; "Selected", s.Selected ]

    for (lowerName, lower), (upperName, upper) in List.pairwise ladder do
        Assert.True(
            lightness upper > lightness lower,
            sprintf "%s must sit above %s" upperName lowerName
        )

[<Fact>]
let ``the light hierarchy steps the other way, as a light theme must`` () =
    let s = Surfaces.light

    let ladder =
        [ "Window", s.Window; "Shell", s.Shell; "SidebarPinned", s.SidebarPinned; "Card", s.Card ]

    for (lowerName, lower), (upperName, upper) in List.pairwise ladder do
        Assert.True(
            lightness upper > lightness lower,
            sprintf "light %s must sit above %s" upperName lowerName
        )

    // …and the interactive states go DOWN from the card, because on a light
    // theme a lift is a darkening.
    Assert.True(lightness s.Hover < lightness s.Card)
    Assert.True(lightness s.Selected < lightness s.Hover)

[<Fact>]
let ``every surface role is distinct, so the hierarchy is real`` () =
    // "A flat identical brown across every element" is one of the named failure
    // modes. Neighbouring roles must differ.
    for mode in modes do
        let roles = surfaceRoles (Surfaces.baseFor mode)
        let values = roles |> List.map snd |> List.distinct

        Assert.True(
            values.Length >= 8,
            sprintf "%A collapsed %d roles into %d colours" mode roles.Length values.Length
        )

[<Fact>]
let ``the steps are subtle, never a hard jump`` () =
    for mode in modes do
        let s = Surfaces.baseFor mode

        for name, c in [ "Shell", s.Shell; "SidebarPinned", s.SidebarPinned; "CardAlt", s.CardAlt; "Hover", s.Hover ] do
            Assert.True(
                abs (lightness c - lightness s.Card) < 0.10,
                sprintf "%A %s is too far from the card to read as the same material" mode name
            )

[<Fact>]
let ``borders and dividers are restrained, never a bright rim`` () =
    for mode in modes do
        let s = Surfaces.baseFor mode

        // Alpha overlays, and quiet ones: "bright white borders or highlights"
        // is a named failure mode.
        Assert.True(s.Border.A < 64uy, sprintf "%A border is too strong (%d/255)" mode s.Border.A)
        Assert.True(s.Divider.A < s.Border.A, sprintf "%A divider must be quieter than the border" mode)

[<Fact>]
let ``the modal scrim dims without going pure black`` () =
    for mode in modes do
        let scrim = (Surfaces.baseFor mode).Scrim
        Assert.InRange(int scrim.A, 80, 200)
        Assert.True(warmth scrim > 0, sprintf "%A scrim must be warm, not neutral black" mode)

// ---- 3. contrast ------------------------------------------------------------

/// WCAG relative luminance.
let private relativeLuminance (c: Color) =
    let channel (v: byte) =
        let s = float v / 255.0
        if s <= 0.04045 then s / 12.92 else ((s + 0.055) / 1.055) ** 2.4

    0.2126 * channel c.R + 0.7152 * channel c.G + 0.0722 * channel c.B

let private contrast (fg: Color) (bg: Color) =
    let a, b = relativeLuminance fg, relativeLuminance bg
    let hi, lo = max a b, min a b
    (hi + 0.05) / (lo + 0.05)

[<Fact>]
let ``every ink role clears WCAG AA on the surfaces it is used on`` () =
    // Tested against the primary content card and the sidebar, which is where
    // body copy and navigation labels actually sit. The redesign's own bottom
    // two ink steps measure 4.1:1 and 2.9:1 here, which is why `Surfaces` lifts
    // them rather than transcribing them.
    for mode in modes do
        let s = Surfaces.baseFor mode

        for surfaceName, bg in [ "Card", s.Card; "SidebarPinned", s.SidebarPinned ] do
            for inkName, fg in inkRoles s do
                Assert.True(
                    contrast fg bg >= 4.5,
                    sprintf "%A %s on %s is %.2f:1" mode inkName surfaceName (contrast fg bg)
                )

[<Fact>]
let ``ink on an accent fill is readable under every accent`` () =
    // The redesign always writes `color:#1B1710` on an accent-filled control.
    // White would vibrate on a mid-tone accent; this checks the alternative
    // actually works for all six.
    for mode in modes do
        let onAccent = (Surfaces.baseFor mode).OnAccent

        for key in accents do
            let fill = (Palette.ofKey key).Normal

            Assert.True(
                contrast onAccent fill >= 4.5,
                sprintf "%A ink on %s is %.2f:1" mode key (contrast onAccent fill)
            )

[<Fact>]
let ``contrast survives the harmonisation`` () =
    // Harmonisation moves the surface, so the contrast claim has to hold on the
    // resolved palette, not only on the base.
    for mode in modes do
        for key in accents do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for inkName, fg in inkRoles s do
                Assert.True(
                    contrast fg s.Card >= 4.5,
                    sprintf "%A %s on the %s card is %.2f:1" mode inkName key (contrast fg s.Card)
                )

// ---- 3b. the Inbox's semantic inks -----------------------------------------
//
// The Inbox writes words in seven semantic roles — a status, an effect, a
// conflict, a module code, a source. `Theme.readableHex` walks each toward the
// theme's ink until it clears AA, and returns it untouched when it already
// passes. These assert the walk really covers every role this screen can paint,
// in both themes and under every accent.

/// The colour a semantic role resolves to, before the readability walk.
/// Mirrors `InboxView.inkHex`, which is the one place the mapping is made.
let private inkRoleColour (s: Surfaces) (accent: Palette) (ink: UniversityOS.App.Features.InboxLogic.Ink) =
    match ink with
    | UniversityOS.App.Features.InboxLogic.Ink.Positive -> s.Success
    | UniversityOS.App.Features.InboxLogic.Ink.Caution -> s.Warning
    | UniversityOS.App.Features.InboxLogic.Ink.Informational -> s.Info
    | UniversityOS.App.Features.InboxLogic.Ink.Critical -> s.Danger
    | UniversityOS.App.Features.InboxLogic.Ink.Neutral
    | UniversityOS.App.Features.InboxLogic.Ink.Identity -> Color.Parse Theme.Identity.neutral
    | UniversityOS.App.Features.InboxLogic.Ink.Accent -> accent.Normal

/// Every fixed identity hue the Inbox uses for a source badge.
let private inboxSourceColours =
    [ "amber", Theme.Identity.amber
      "orchid", Theme.Identity.orchid
      "sky", Theme.Identity.sky
      "teal", Theme.Identity.teal
      "neutral", Theme.Identity.neutral ]

[<Fact>]
let ``every semantic ink the Inbox writes clears AA on the card, in both themes`` () =
    for mode in modes do
        for key in accents do
            let accent = Palette.ofKey key
            let s = Surfaces.resolve mode accent

            for ink in UniversityOS.App.Features.InboxLogic.inksUsed do
                let raw = inkRoleColour s accent ink
                let readable = Palette.readableOn s.Card s.Text raw

                Assert.True(
                    contrast readable s.Card >= 4.5,
                    sprintf "%A %A ink under %s is %.2f:1 on the card" mode ink key (contrast readable s.Card)
                )

[<Fact>]
let ``every Inbox source badge colour clears AA once made readable`` () =
    for mode in modes do
        for key in accents do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, hex in inboxSourceColours do
                let readable = Palette.readableOn s.Card s.Text (Color.Parse hex)

                Assert.True(
                    contrast readable s.Card >= 4.5,
                    sprintf "%A source %s is %.2f:1 on the card" mode name (contrast readable s.Card)
                )

[<Fact>]
let ``a selected Inbox card is visibly distinct from an unselected one, in both themes`` () =
    // Selection changes the card's tint from Card to Selected. If the two were
    // near-identical the accent outline would be doing all the work.
    for mode in modes do
        for key in accents do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            Assert.True(
                channelDistance s.Card s.Selected >= 4,
                sprintf "%A selected card under %s is only %d from the card" mode key (channelDistance s.Card s.Selected)
            )

[<Fact>]
let ``the Inbox's semantic inks are still material, not accent-driven`` () =
    // Status, effect and conflict colours must not follow the swatch: an applied
    // item is green whatever accent is selected.
    let materialInks =
        [ UniversityOS.App.Features.InboxLogic.Ink.Positive
          UniversityOS.App.Features.InboxLogic.Ink.Caution
          UniversityOS.App.Features.InboxLogic.Ink.Informational
          UniversityOS.App.Features.InboxLogic.Ink.Critical ]

    for mode in modes do
        let reference = Surfaces.resolve mode (Palette.ofKey "amber")

        for key in accents do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for ink in materialInks do
                Assert.Equal(
                    inkRoleColour reference (Palette.ofKey "amber") ink,
                    inkRoleColour s (Palette.ofKey key) ink
                )

// ---- 3c. readability measured against the REAL background -------------------
//
// The Light Inbox pass found one defect class behind almost every failure:
// readability was walked against the CARD while the text was drawn on something
// else — a chip's own 12% wash, a banner, an accent veil, a selected card. Each
// of those is darker than the card on a light theme, so the walk stopped a step
// early. These pin the corrected behaviour, in both themes and under every
// accent, so it cannot regress to measuring against the wrong surface.

/// The alphas the Inbox actually paints with, read from the view's own
/// constants rather than restated — so a change to the fill moves the
/// measurement with it instead of leaving the two describing different surfaces.
let private chipAlpha =
    System.Int32.Parse(
        UniversityOS.App.Features.InboxLogic.Geometry.chipFillAlpha,
        System.Globalization.NumberStyles.HexNumber
    )

let private bannerAlpha =
    System.Int32.Parse(
        UniversityOS.App.Features.InboxLogic.Geometry.bannerFillAlpha,
        System.Globalization.NumberStyles.HexNumber
    )

let private washed (alpha: int) (colour: Color) (surface: Color) =
    Palette.composite surface (Color.FromArgb(byte alpha, colour.R, colour.G, colour.B))

/// Every colour the Inbox writes a word in, as raw role colours.
let private inboxWordColours (s: Surfaces) (accent: Palette) =
    [ "Success", s.Success
      "Warning", s.Warning
      "Info", s.Info
      "Danger", s.Danger
      "Neutral", Color.Parse Theme.Identity.neutral
      "Accent", accent.Normal
      "Identity amber", Color.Parse Theme.Identity.amber
      "Identity orchid", Color.Parse Theme.Identity.orchid
      "Identity sky", Color.Parse Theme.Identity.sky
      "Identity teal", Color.Parse Theme.Identity.teal ]

[<Fact>]
let ``composite is a real alpha blend, exact at both ends`` () =
    let red = Color.FromRgb(255uy, 0uy, 0uy)
    let white = Color.FromRgb(255uy, 255uy, 255uy)

    // Fully transparent overlay leaves the backdrop untouched.
    Assert.Equal(white, Palette.composite white (Palette.withAlpha 0.0 red))
    // Fully opaque overlay replaces it.
    Assert.Equal(red, Palette.composite white (Palette.withAlpha 1.0 red))

    // Half-way is half-way, and the result is opaque.
    let half = Palette.composite white (Palette.withAlpha 0.5 red)
    Assert.Equal(255uy, half.A)
    Assert.InRange(int half.G, 126, 129)
    Assert.InRange(int half.B, 126, 129)

[<Fact>]
let ``a chip's ink clears AA on its OWN wash, not merely on the card`` () =
    // The exact defect: measured against the card these landed at 4.15-4.44:1 in
    // Light, and at 3.4-3.6:1 on a selected card.
    for mode in modes do
        for key in accents do
            let accent = Palette.ofKey key
            let s = Surfaces.resolve mode accent

            for surfaceName, surface in [ "card", s.Card; "selected card", s.Selected ] do
                for roleName, raw in inboxWordColours s accent do
                    let wash = washed chipAlpha raw surface
                    let ink = Palette.readableOn wash s.Text raw

                    Assert.True(
                        contrast ink wash >= 4.5,
                        sprintf
                            "%A %s chip under %s on the %s is %.2f:1"
                            mode roleName key surfaceName (contrast ink wash)
                    )

[<Fact>]
let ``a status word clears AA on the card it is written on, selected or not`` () =
    // The status word has no wash of its own — it is written straight onto the
    // card, which selection darkens.
    for mode in modes do
        for key in accents do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for surfaceName, surface in [ "card", s.Card; "selected card", s.Selected ] do
                for roleName, raw in
                    [ "New", s.Warning
                      "Analysed", s.Info
                      "Applied", s.Success
                      "Dismissed", Color.Parse Theme.Identity.neutral ] do
                    let ink = Palette.readableOn surface s.Text raw

                    Assert.True(
                        contrast ink surface >= 4.5,
                        sprintf "%A status %s on the %s is %.2f:1" mode roleName surfaceName (contrast ink surface)
                    )

[<Fact>]
let ``the quietest ink steps stay readable on a selected card`` () =
    // Faint measured 4.32:1 on a dark selected card and 3.88:1 on a light one —
    // both under the floor, which is why the timestamp line walks against the
    // card's own surface now.
    for mode in modes do
        for key in accents do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for roleName, raw in [ "Faint", s.Faint; "Label", s.Label; "Muted", s.Muted ] do
                let ink = Palette.readableOn s.Selected s.Text raw

                Assert.True(
                    contrast ink s.Selected >= 4.5,
                    sprintf "%A %s on a selected card under %s is %.2f:1" mode roleName key (contrast ink s.Selected)
                )

[<Fact>]
let ``a conflict glyph clears AA on its banner, in both themes`` () =
    for mode in modes do
        for key in accents do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for surface in [ s.Card; s.Selected ] do
                for roleName, raw in [ "warning", s.Warning; "danger", s.Danger ] do
                    let banner = washed bannerAlpha raw surface
                    let glyph = Palette.readableOn banner s.Text raw

                    Assert.True(
                        contrast glyph banner >= 4.5,
                        sprintf "%A %s conflict glyph is %.2f:1 on its banner" mode roleName (contrast glyph banner)
                    )

                    // …and the words beside it, which are the real signal.
                    Assert.True(contrast s.Text banner >= 4.5)

[<Fact>]
let ``an accent-washed control's label clears AA on the wash, not on what is behind it`` () =
    // The selected filter chip and the per-proposal Apply button.
    for mode in modes do
        for key in accents do
            let accent = Palette.ofKey key
            let s = Surfaces.resolve mode accent

            for surfaceName, surface in [ "page", s.Window; "card", s.Card ] do
                let wash = Palette.composite surface accent.Soft
                let label = Palette.readableOn wash s.Text accent.Normal

                Assert.True(
                    contrast label wash >= 4.5,
                    sprintf "%A accent label under %s on the %s is %.2f:1" mode key surfaceName (contrast label wash)
                )

[<Fact>]
let ``a page banner's ink clears AA on the banner, in both themes`` () =
    for mode in modes do
        for key in accents do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            let errorWash = washed 0x22 s.Danger s.Window
            let warnWash = washed chipAlpha s.Warning s.Window
            let noticeWash = washed chipAlpha s.Success s.Window

            Assert.True(
                contrast (Palette.readableOn errorWash s.Text s.Danger) errorWash >= 4.5,
                sprintf "%A error banner ink is %.2f:1" mode (contrast s.Danger errorWash)
            )

            // The retry control sits on the same wash.
            Assert.True(contrast s.Muted errorWash >= 4.5, sprintf "%A 'Try again' is %.2f:1" mode (contrast s.Muted errorWash))

            Assert.True(
                contrast (Palette.readableOn warnWash s.Text s.Danger) warnWash >= 4.5,
                sprintf "%A confirm button ink is %.2f:1" mode (contrast s.Danger warnWash)
            )

            Assert.True(contrast s.Text warnWash >= 4.5)
            Assert.True(contrast s.Text noticeWash >= 4.5)

[<Fact>]
let ``measuring against the real background never makes a colour less readable`` () =
    // The correction is monotone: it can only move a colour further from its
    // background, never closer. A regression that reversed the arguments would
    // fail here rather than merely looking slightly worse.
    for mode in modes do
        for key in accents do
            let accent = Palette.ofKey key
            let s = Surfaces.resolve mode accent

            for _, raw in inboxWordColours s accent do
                let wash = washed chipAlpha raw s.Card
                let againstCard = Palette.readableOn s.Card s.Text raw
                let againstWash = Palette.readableOn wash s.Text raw

                Assert.True(
                    contrast againstWash wash >= contrast againstCard wash - 0.001,
                    sprintf "%A correcting the surface made a chip WORSE under %s" mode key
                )

[<Fact>]
let ``the correction only ever moves an ink that was failing`` () =
    // The precise guarantee, and the one that protects the approved themes: a
    // colour that already cleared AA against its own wash is returned BYTE FOR
    // BYTE, and a colour that did not now clears it. Nothing that passed is
    // restyled.
    //
    // Measured over Dark's 240 combinations: 176 are byte-identical and 64 move
    // — every one of the 64 having been below the floor beforehand, most of them
    // on a SELECTED card, where the darker surface took Danger to 3.58:1 and the
    // accent to 3.94:1.
    for mode in modes do
        for key in accents do
            let accent = Palette.ofKey key
            let s = Surfaces.resolve mode accent

            for surfaceName, surface in [ "card", s.Card; "selected card", s.Selected ] do
                for alphaName, alpha in [ "chip", chipAlpha; "banner", bannerAlpha ] do
                    for roleName, raw in inboxWordColours s accent do
                        let wash = washed alpha raw surface
                        let before = Palette.readableOn s.Card s.Text raw
                        let after = Palette.readableOn wash s.Text raw

                        if contrast before wash >= 4.5 then
                            // Parenthesised: inside a method call F# reads
                            // `before = after` as a named argument, not equality.
                            Assert.True(
                                (before = after),
                                sprintf
                                    "%A %s %s on the %s under %s already passed at %.2f:1 and was still changed"
                                    mode roleName alphaName surfaceName key (contrast before wash)
                            )
                        else
                            Assert.True(
                                contrast after wash >= 4.5,
                                sprintf
                                    "%A %s %s on the %s under %s is still %.2f:1"
                                    mode roleName alphaName surfaceName key (contrast after wash)
                            )

// ---- 3d. non-text signals ---------------------------------------------------

[<Fact>]
let ``visibleOn returns a colour untouched when it already clears the non-text bar`` () =
    let white = Color.FromRgb(255uy, 255uy, 255uy)
    let black = Color.FromRgb(0uy, 0uy, 0uy)

    Assert.Equal(black, Palette.visibleOn white black black)
    Assert.Equal(Palette.aaNonText, 3.0)

[<Fact>]
let ``the selection ring is a visible outline on the page, in both themes`` () =
    // An accent measures 6.5-8.7:1 against the dark page and only 1.75-2.33:1
    // against the light one, so on Light the selected filter chip was ringed in
    // something barely distinguishable from the background.
    for mode in modes do
        for key in accents do
            let accent = Palette.ofKey key
            let s = Surfaces.resolve mode accent
            let ring = Palette.visibleOn s.Window s.Text accent.Normal

            Assert.True(
                contrast ring s.Window >= 3.0,
                sprintf "%A selection ring under %s is %.2f:1" mode key (contrast ring s.Window)
            )

[<Fact>]
let ``the Dark selection ring is the accent itself, unwalked`` () =
    // The fix must be Light-only: on Dark every accent already clears 3:1 by a
    // wide margin, so the walk is a no-op and the approved screens are unchanged.
    for key in accents do
        let accent = Palette.ofKey key
        let s = Surfaces.resolve ThemeMode.Dark accent

        Assert.Equal(accent.Normal, Palette.visibleOn s.Window s.Text accent.Normal)

[<Fact>]
let ``a translucent coloured edge reads with the same weight in both themes`` () =
    // Dark is 1.0 by definition; Light is scaled because a dark ink at low alpha
    // over parchment separates less than a light one over charcoal.
    Assert.Equal(1.0, Surfaces.overlayEdgeScale ThemeMode.Dark, 6)
    Assert.True(Surfaces.overlayEdgeScale ThemeMode.Light > 1.0)

    let scaled (mode: ThemeMode) (alpha: int) (colour: Color) (surface: Color) =
        let a = int (System.Math.Round(float alpha * Surfaces.overlayEdgeScale mode)) |> min 255
        washed a colour surface

    for key in accents do
        let d = Surfaces.resolve ThemeMode.Dark (Palette.ofKey key)
        let l = Surfaces.resolve ThemeMode.Light (Palette.ofKey key)

        for name, alpha, pick, surf in
            [ "conflict warning", 0x4D, (fun (s: Surfaces) -> s.Warning), (fun (s: Surfaces) -> s.Card)
              "conflict danger", 0x4D, (fun s -> s.Danger), (fun s -> s.Card)
              "confirmation prompt", 0x55, (fun s -> s.Warning), (fun s -> s.Window) ] do
            let darkWeight = contrast (scaled ThemeMode.Dark alpha (pick d) (surf d)) (surf d)
            let lightWeight = contrast (scaled ThemeMode.Light alpha (pick l) (surf l)) (surf l)

            Assert.True(
                lightWeight >= darkWeight - 0.05,
                sprintf "%s edge under %s: light %.2f:1 vs dark %.2f:1" name key lightWeight darkWeight
            )

// ---- 3e. Light surfaces, states and loading ---------------------------------

[<Fact>]
let ``the Light card is warm parchment, never pure white or grey`` () =
    for key in accents do
        let s = Surfaces.resolve ThemeMode.Light (Palette.ofKey key)

        for name, surface in [ "Card", s.Card; "Overlay", s.Overlay; "Action", s.Action ] do
            Assert.True(int surface.R < 255 || int surface.G < 255 || int surface.B < 255,
                        sprintf "%s under %s is pure white" name key)

            Assert.True(warmth surface > 0, sprintf "%s under %s is not warm" name key)
            // Warm, but not a beige wash.
            Assert.True(chroma surface <= 18, sprintf "%s under %s is over-saturated (chroma %d)" name key (chroma surface))

[<Fact>]
let ``a card is distinct from the page it sits on, in both themes`` () =
    for mode in modes do
        for key in accents do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            Assert.True(
                channelDistance s.Card s.Window >= 8,
                sprintf "%A card under %s is only %d from the page" mode key (channelDistance s.Card s.Window)
            )

[<Fact>]
let ``a loading skeleton is visible on the card it stands in for`` () =
    // `Ui.skeletonBlock` fills with SurfaceAlt, which is the material's Hover.
    // Too close to the card and the loading state looks like an empty one.
    //
    // The approved Dark theme defines what "visible" means here — it measures 7
    // to 8 channel steps — so the floor is Dark's own value rather than a number
    // picked to pass, and Light is required never to be the fainter of the two.
    for key in accents do
        let dark = Surfaces.resolve ThemeMode.Dark (Palette.ofKey key)
        let light = Surfaces.resolve ThemeMode.Light (Palette.ofKey key)

        let darkStep = channelDistance dark.Card dark.Hover
        let lightStep = channelDistance light.Card light.Hover

        Assert.True(darkStep >= 6, sprintf "dark skeleton under %s is only %d from the card" key darkStep)

        Assert.True(
            lightStep >= darkStep,
            sprintf "light skeleton under %s is fainter than dark's (%d vs %d)" key lightStep darkStep
        )

[<Fact>]
let ``the glass hover veil visibly moves the surface in both themes`` () =
    // The veil is the theme's own ink at 18% — it lifts a dark surface and
    // deepens a light one. Either way it has to be a change the eye can see.
    for mode in modes do
        for key in accents do
            let s = Surfaces.resolve mode (Palette.ofKey key)
            let veiled = Palette.composite s.Action (Palette.withAlpha (46.0 / 255.0) s.Text)

            Assert.True(
                channelDistance s.Action veiled >= 20,
                sprintf "%A hover veil under %s moves the surface only %d" mode key (channelDistance s.Action veiled)
            )

[<Fact>]
let ``a disabled action's explanation is ordinary readable body text`` () =
    // The control itself dims, which WCAG exempts. The REASON does not: it is a
    // required explanation and is drawn in Muted, undimmed, on the surface the
    // row sits on.
    for mode in modes do
        for key in accents do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for surfaceName, surface in [ "page", s.Window; "card", s.Card; "selected card", s.Selected ] do
                Assert.True(
                    contrast s.Muted surface >= 4.5,
                    sprintf "%A disabled reason on the %s is %.2f:1" mode surfaceName (contrast s.Muted surface)
                )

// ---- 4. dark and light are one design system --------------------------------

/// Hue in degrees, 0–360.
let private hue (c: Color) =
    let r, g, b = float c.R / 255.0, float c.G / 255.0, float c.B / 255.0
    let mx, mn = max r (max g b), min r (min g b)

    if mx = mn then
        0.0
    else
        let d = mx - mn

        let h =
            if mx = r then (g - b) / d + (if g < b then 6.0 else 0.0)
            elif mx = g then (b - r) / d + 2.0
            else (r - g) / d + 4.0

        h * 60.0

[<Fact>]
let ``dark and light share one hue family`` () =
    // Same design system, different lightness. Every surface in both themes sits
    // in the warm 20-50 degree band — brown/amber — so neither theme reads as a
    // different product.
    for mode in modes do
        for name, c in surfaceRoles (Surfaces.baseFor mode) do
            Assert.InRange(hue c, 15.0, 55.0)

[<Fact>]
let ``dark and light define exactly the same roles`` () =
    // A light theme that quietly dropped a role would fall back to a dark
    // colour at runtime. Record types make this structural, but the ordering
    // and count are asserted so the two lists cannot drift.
    let d = surfaceRoles Surfaces.dark |> List.map fst
    let l = surfaceRoles Surfaces.light |> List.map fst

    Assert.Equal<string list>(d, l)

    // …and they are genuinely different palettes, not one duplicated.
    for (_, a), (_, b) in List.zip (surfaceRoles Surfaces.dark) (surfaceRoles Surfaces.light) do
        Assert.NotEqual(a, b)

[<Fact>]
let ``light is light and dark is dark`` () =
    for _, c in surfaceRoles Surfaces.dark do
        Assert.True(lightness c < 0.25, sprintf "dark surface %s is not dark" (Theme.hexOfColor c))

    for _, c in surfaceRoles Surfaces.light do
        Assert.True(lightness c > 0.85, sprintf "light surface %s is not light" (Theme.hexOfColor c))

// ---- 5. accent details still change ----------------------------------------

[<Fact>]
let ``accent details do change with the accent`` () =
    // The other half of the separation: if surfaces stop moving, the details
    // must still move, or the accent picker does nothing at all.
    let normals = accents |> List.map (fun k -> (Palette.ofKey k).Normal)
    Assert.Equal(normals.Length, (List.distinct normals).Length)

    // The roles §4 names: nav indicator, primary action, progress, focus ring.
    for role in [ (fun (p: Palette) -> p.Normal); (fun p -> p.Light); (fun p -> p.Dark); (fun p -> p.Ring) ] do
        let values = accents |> List.map (Palette.ofKey >> role)
        Assert.Equal(values.Length, (List.distinct values).Length)

[<Fact>]
let ``an accent detail moves far more than a surface does`` () =
    // The separation stated as one number: the same accent change that moves a
    // card by a few steps moves the detail it drives by an order of magnitude
    // more. This is the test that would fail if someone reintroduced a surface
    // blend.
    let amber, blue = Palette.ofKey "amber", Palette.ofKey "blue"

    let surfaceMove =
        channelDistance (Surfaces.resolve ThemeMode.Dark amber).Card (Surfaces.resolve ThemeMode.Dark blue).Card

    let detailMove = channelDistance amber.Normal blue.Normal

    Assert.True(
        detailMove > surfaceMove * 5,
        sprintf "detail moved %d, surface moved %d — not enough separation" detailMove surfaceMove
    )

[<Fact>]
let ``the active navigation indicator is a low-opacity accent surface`` () =
    // §4's rule for selection: an accent-derived indicator, not an accent fill.
    // `A1F` is what the sidebar paints behind the selected item.
    for key in accents do
        let p = Palette.ofKey key

        Assert.InRange(int p.A1F.A, 16, 64)
        Assert.Equal(p.Normal.R, p.A1F.R)
        Assert.Equal(p.Normal.G, p.A1F.G)
        Assert.Equal(p.Normal.B, p.A1F.B)

    // …and it really does differ per accent, so selection reads as the user's
    // colour rather than a fixed one.
    let fills = accents |> List.map (fun k -> (Palette.ofKey k).A1F)
    Assert.Equal(fills.Length, (List.distinct fills).Length)

[<Fact>]
let ``the sidebar tint stays material through the whole pin transition`` () =
    // The shell interpolates the panel's tint between two material roles as the
    // sidebar docks and undocks. Every intermediate frame must still be a warm
    // neutral — there is no point at which it passes through the accent.
    let s = Surfaces.resolve ThemeMode.Dark (Palette.ofKey "amber")

    for step in 0..10 do
        let phase = float step / 10.0
        let tint = Palette.mix s.SidebarPinned s.SidebarFloating phase

        Assert.True(warmth tint > 0, sprintf "phase %.1f is not warm" phase)
        Assert.True(chroma tint <= 26, sprintf "phase %.1f has chroma %d" phase (chroma tint))

[<Fact>]
let ``the theme palette follows the selected accent, not a brand colour`` () =
    // `Theme.accentHex` used to return a fixed purple regardless of the swatch,
    // which is how the progress dials ended up purple. It is now a projection of
    // the live accent.
    for key in accents do
        Theme.setAccent (Palette.ofKey key)
        Assert.Equal(Theme.hexOfColor (Palette.ofKey key).Normal, Theme.accentHex ())

    Theme.setAccent (Palette.ofKey "amber")

[<Fact>]
let ``the theme palette is a projection of the material, not its own colours`` () =
    for mode in modes do
        Theme.setMode mode
        Theme.setAccent (Palette.ofKey "amber")

        let s = Theme.surfaces ()
        let p = Theme.paletteOf s (Palette.ofKey "amber")

        Assert.Equal(Theme.hexOfColor s.Card, p.Surface)
        Assert.Equal(Theme.hexOfColor s.SidebarPinned, p.Sidebar)
        Assert.Equal(Theme.hexOfColor s.Shell, p.Content)
        Assert.Equal(Theme.hexOfColor s.Border, p.Border)

        // The two deliberate non-literal mappings, pinned so they are a decision
        // rather than an accident.
        Assert.Equal(Theme.hexOfColor s.Hover, p.SurfaceAlt)
        Assert.Equal(Theme.hexOfColor s.Selected, p.Hover)

    Theme.setMode ThemeMode.Dark

[<Fact>]
let ``an alpha surface round-trips through the hex projection`` () =
    // Border and scrim carry alpha, and Avalonia parses eight hex digits as
    // #AARRGGBB. Appending the alpha instead of prepending it is the exact bug
    // that turned a faint purple into a bright green.
    for c in [ Surfaces.dark.Border; Surfaces.dark.Divider; Surfaces.dark.Scrim; Surfaces.light.Border ] do
        Assert.Equal(c, Color.Parse(Theme.hexOfColor c))

    // Opaque colours stay six digits, so every existing `withAlphaHex` caller
    // keeps working.
    Assert.Equal(7, (Theme.hexOfColor Surfaces.dark.Card).Length)

// ---- 6. identity colours ----------------------------------------------------

[<Fact>]
let ``entity identity colours are distinct and no longer cool primaries`` () =
    let kinds =
        [ EntityKind.Module; EntityKind.Assignment; EntityKind.Event; EntityKind.Note
          EntityKind.Document; EntityKind.Lecture; EntityKind.InboxItem; EntityKind.Session
          EntityKind.Conversation; EntityKind.RevisionItem ]

    let hexes = kinds |> List.map Theme.entityKindHex
    Assert.Equal(hexes.Length, (List.distinct hexes).Length)

    // The old set led with `#4FA8E8` and `#7C6FF0` — bright cool primaries that
    // were the last visible trace of the blue-grey character.
    Assert.DoesNotContain("#4FA8E8", hexes)
    Assert.DoesNotContain("#7C6FF0", hexes)

// ---- the Light theme pass ---------------------------------------------------
//
// Light was derived rather than transcribed and had never been reviewed. These
// pin the four defects that review found, each with the number that proves it.

[<Fact>]
let ``the status inks are readable in BOTH themes, not only dark`` () =
    // The gap: the ink-role test above covers Text/TextSecondary/Muted/Label/
    // Faint but never the semantic four, and those carry body text — a failed
    // constraint row, a risk level, a warning. On the light card the values
    // these replaced measured Success 4.49:1, Warning 4.08:1, Info 4.44:1.
    for mode in modes do
        let s = Surfaces.baseFor mode

        for name, c in [ "Success", s.Success; "Warning", s.Warning; "Danger", s.Danger; "Info", s.Info ] do
            Assert.True(
                contrast c s.Card >= 4.5,
                sprintf "%A %s on the card is %.2f:1" mode name (contrast c s.Card)
            )

[<Fact>]
let ``the status inks keep their hue while gaining contrast`` () =
    // Darkened, not re-hued: a warning must still look like a warning.
    let hue (c: Color) =
        let r, g, b = float c.R, float c.G, float c.B
        let mx, mn = List.max [ r; g; b ], List.min [ r; g; b ]
        if mx = mn then 0.0
        elif mx = r then (60.0 * ((g - b) / (mx - mn)) + 360.0) % 360.0
        elif mx = g then 60.0 * ((b - r) / (mx - mn)) + 120.0
        else 60.0 * ((r - g) / (mx - mn)) + 240.0

    // Warning stays gold, Info stays blue, Success stays green.
    Assert.InRange(hue Surfaces.light.Warning, 30.0, 55.0)
    Assert.InRange(hue Surfaces.light.Info, 180.0, 220.0)
    Assert.InRange(hue Surfaces.light.Success, 100.0, 150.0)

    // And each is warmer or cooler than the card in the same direction as dark.
    Assert.True(warmth Surfaces.light.Warning > 0, "the light warning must stay warm")

[<Fact>]
let ``the glass hairline is visible in both themes`` () =
    // It was hard-coded white. On the light card white at the default alpha
    // composites to 1.01:1 — no edge at all, so a light card had no boundary.
    let over (surface: Color) (line: Color) (alpha: float) =
        Palette.mix surface line alpha

    for mode in modes do
        let s = Surfaces.baseFor mode
        let line = Glass.hairline mode
        let alpha = Glass.borderAlpha 1.0 * Glass.hairlineAlphaScale mode
        let edge = over s.Card line alpha

        Assert.True(
            contrast edge s.Card >= 1.2,
            sprintf "%A hairline is %.2f:1 against the card — that is not an edge" mode (contrast edge s.Card)
        )

        // Restrained, not a rule: a card outline must not read as a divider.
        Assert.True(
            contrast edge s.Card <= 3.0,
            sprintf "%A hairline is %.2f:1 — too heavy for a hairline" mode (contrast edge s.Card)
        )

    // Dark keeps white; only light changes.
    Assert.Equal(Colors.White, Glass.hairline ThemeMode.Dark)
    Assert.NotEqual(Colors.White, Glass.hairline ThemeMode.Light)

[<Fact>]
let ``a detail colour is made readable on light and left alone on dark`` () =
    // `Palette.readableOn` is what stops the module-code pill, the status pill
    // and the SELECTED badge writing 1.7:1 text on the light card. The dark
    // no-op half matters just as much: it is what keeps the approved dark
    // screens pixel-identical.
    let identity =
        [ Theme.Identity.violet; Theme.Identity.amber; Theme.Identity.sky
          Theme.Identity.green; Theme.Identity.orchid; Theme.Identity.teal
          Theme.Identity.terracotta; Theme.Identity.gold; Theme.Identity.rose
          Theme.Identity.neutral ]
        |> List.map Color.Parse

    let accentNormals = accents |> List.map (fun k -> (Palette.ofKey k).Normal)

    for mode in modes do
        let s = Surfaces.baseFor mode

        for c in identity @ accentNormals do
            let ink = Palette.readableOn s.Card s.Text c

            Assert.True(
                contrast ink s.Card >= 4.5,
                sprintf "%A %A resolved to %.2f:1" mode c (contrast ink s.Card)
            )

            if mode = ThemeMode.Dark then
                // Untouched on dark — the approved screens must not move.
                Assert.Equal(c, ink)

[<Fact>]
let ``every identity and accent colour genuinely needed the light correction`` () =
    // States the defect, so the fix above is not guarding a hypothetical: on the
    // light card every one of these fails as text before correction, and every
    // one already passes on the dark card.
    let raw =
        [ Theme.Identity.violet; Theme.Identity.sky; Theme.Identity.teal; Theme.Identity.gold ]
        |> List.map Color.Parse

    for c in raw do
        Assert.True(contrast c Surfaces.light.Card < 4.5, sprintf "%A already passed on light" c)
        Assert.True(contrast c Surfaces.dark.Card >= 4.5, sprintf "%A failed on dark" c)

[<Fact>]
let ``light detail colours stay distinguishable from one another after correction`` () =
    // Darkening toward one ink could collapse ten identity colours into one
    // muddy brown. They must remain telling-apart-able, which is their job.
    let corrected =
        [ Theme.Identity.violet; Theme.Identity.amber; Theme.Identity.sky
          Theme.Identity.green; Theme.Identity.teal; Theme.Identity.rose ]
        |> List.map (Color.Parse >> Palette.readableOn Surfaces.light.Card Surfaces.light.Text)

    for i in 0 .. corrected.Length - 1 do
        for j in i + 1 .. corrected.Length - 1 do
            Assert.True(
                channelDistance corrected.[i] corrected.[j] >= 20,
                sprintf "%A and %A collapsed together on light" corrected.[i] corrected.[j]
            )

[<Fact>]
let ``identity colours read on the card they sit on`` () =
    let kinds =
        [ EventKind.Lecture; EventKind.Deadline; EventKind.Meeting; EventKind.Personal
          EventKind.StudySession; EventKind.Exam; EventKind.Other ]

    for kind in kinds do
        let c = Color.Parse(Theme.eventKindHex kind)
        Assert.True(contrast c Surfaces.dark.Card >= 4.5, sprintf "%A is %.2f:1 on the card" kind (contrast c Surfaces.dark.Card))

// ---- 7. Fluent's own state colours -----------------------------------------

[<Fact>]
let ``every Fluent state resource is mapped to a material role`` () =
    // Fluent paints button hover, text-box fill and combo backgrounds from its
    // own neutral greys, which a view cannot set. Each override must resolve to
    // a real material colour — a key with no role would silently paint the card
    // colour into a place that should be a field.
    let s = Surfaces.dark
    let roles = surfaceRoles s |> List.map snd

    Assert.NotEmpty FluentMaterial.keys

    for (key: string) in FluentMaterial.keys do
        let resolved = FluentMaterial.resolve s key

        if key.Contains "Border" then
            Assert.Equal(s.Border, resolved)
        else
            Assert.True(List.contains resolved roles, sprintf "%s resolved outside the material palette" key)

[<Fact>]
let ``Fluent overrides follow the theme`` () =
    for key in FluentMaterial.keys do
        Assert.NotEqual(FluentMaterial.resolve Surfaces.dark key, FluentMaterial.resolve Surfaces.light key)
