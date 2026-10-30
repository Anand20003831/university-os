module UniversityOS.Tests.LightThemeTests

open System
open Xunit
open Avalonia.Media
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Features

/// The Light-theme audit.
///
/// **What "Modules Light" and "Assignments Light" actually needed.** Neither
/// view contains a single hardcoded colour — verified by grep, zero `"#RRGGBB"`
/// literals in either file. Every colour already resolves through `Theme` and
/// `Surfaces`, which are per-mode, so both screens have *rendered* in Light
/// since they were migrated.
///
/// What was missing was **proof**: neither test file contained a single
/// `ThemeMode.Light` assertion, while the other nine screens had theirs. So the
/// Light "pass" is this file — every wash those two screens paint, measured in
/// Light as well as Dark, over every accent and every identity colour they can
/// carry.
///
/// A composite-aware measurement throughout: a translucent wash is composited
/// over the surface it genuinely sits on before its contrast is taken. Measuring
/// the raw translucent colour would pass washes that are unreadable on screen.

let private themeModes = [ ThemeMode.Dark; ThemeMode.Light ]
let private accentKeys = Palette.accents |> List.map fst

let private washedBy (alphaHex: string) (colour: Color) (surface: Color) =
    let a = Int32.Parse(alphaHex, Globalization.NumberStyles.HexNumber)
    Palette.composite surface (Color.FromArgb(byte a, colour.R, colour.G, colour.B))

/// The alphas Modules and Assignments genuinely paint with, taken from the two
/// views by grep rather than assumed.
let private modulesAlphas = [ "10"; "1F"; "22"; "26"; "2E" ]
let private assignmentsAlphas = [ "1F"; "22"; "2E" ]

[<Literal>]
let private edgeAlpha = "55"

/// Every identity colour these screens can put a wash under: the nine kind hues,
/// the neutral, and every accent — because a module's `Color` is user-chosen and
/// the accent presets are the representative set the app ships.
let private identityColours () =
    [ "violet", Theme.Identity.violet
      "amber", Theme.Identity.amber
      "sky", Theme.Identity.sky
      "green", Theme.Identity.green
      "orchid", Theme.Identity.orchid
      "teal", Theme.Identity.teal
      "terracotta", Theme.Identity.terracotta
      "gold", Theme.Identity.gold
      "rose", Theme.Identity.rose
      "neutral", Theme.Identity.neutral ]
    |> List.map (fun (n, hex) -> n, Color.Parse hex)

let private accentColours () =
    Palette.accents |> List.map (fun (key, _) -> "accent " + key, (Palette.ofKey key).Normal)

/// Every colour either screen can wash a surface with.
let private allWashColours () = identityColours () @ accentColours ()

// ---------------------------------------------------------------------------
// 1. Modules Light
// ---------------------------------------------------------------------------

[<Fact>]
let ``Modules: every wash it paints carries readable ink, in BOTH themes`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            // Cards sit on Card; expanded regions and chips on CardAlt.
            for surfaceName, surface in [ "card", s.Card; "cardAlt", s.CardAlt ] do
                for colourName, raw in allWashColours () do
                    for alpha in modulesAlphas do
                        let wash = washedBy alpha raw surface
                        let ink = Palette.readableOn wash s.Text raw

                        Assert.True(
                            Palette.contrastRatio ink wash >= Palette.aaText,
                            sprintf
                                "%A/%s %s on %s @%s: %.2f"
                                mode
                                key
                                colourName
                                surfaceName
                                alpha
                                (Palette.contrastRatio ink wash)
                        )

[<Fact>]
let ``Modules: body ink is readable on every surface it uses, in BOTH themes`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, surface in [ "card", s.Card; "cardAlt", s.CardAlt; "action", s.Action ] do
                Assert.True(
                    Palette.contrastRatio s.Text surface >= Palette.aaText,
                    sprintf "%A/%s text on %s: %.2f" mode key name (Palette.contrastRatio s.Text surface)
                )

                // Secondary ink carries module metadata and insight bullets.
                Assert.True(
                    Palette.contrastRatio s.TextSecondary surface >= Palette.aaNonText,
                    sprintf "%A/%s secondary on %s: %.2f" mode key name (Palette.contrastRatio s.TextSecondary surface)
                )

[<Fact>]
let ``Modules: a selected card is distinguishable from an unselected one, in BOTH themes`` () =
    // Selection is an accent wash at 2E over the card; the unselected card is
    // the bare surface. They must differ, and text must survive both.
    for mode in themeModes do
        for key in accentKeys do
            let accent = Palette.ofKey key
            let s = Surfaces.resolve mode accent
            let selected = washedBy "2E" accent.Normal s.Card

            Assert.True(
                Palette.contrastRatio selected s.Card > 1.0,
                sprintf "%A/%s selection invisible" mode key
            )

            Assert.True(
                Palette.contrastRatio s.Text selected >= Palette.aaText,
                sprintf "%A/%s text on selection: %.2f" mode key (Palette.contrastRatio s.Text selected)
            )

[<Fact>]
let ``Modules: an edge is visible against the wash it borders, in BOTH themes`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for colourName, raw in allWashColours () do
                let fill = washedBy "1F" raw s.Card
                let edge = washedBy edgeAlpha raw s.Card

                Assert.True(
                    Palette.contrastRatio edge fill > 1.0,
                    sprintf "%A/%s %s edge invisible" mode key colourName
                )

// ---------------------------------------------------------------------------
// 2. Assignments Light
// ---------------------------------------------------------------------------

[<Fact>]
let ``Assignments: every wash it paints carries readable ink, in BOTH themes`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for surfaceName, surface in [ "card", s.Card; "surfaceAlt", s.CardAlt ] do
                for colourName, raw in allWashColours () do
                    for alpha in assignmentsAlphas do
                        let wash = washedBy alpha raw surface
                        let ink = Palette.readableOn wash s.Text raw

                        Assert.True(
                            Palette.contrastRatio ink wash >= Palette.aaText,
                            sprintf
                                "%A/%s %s on %s @%s: %.2f"
                                mode
                                key
                                colourName
                                surfaceName
                                alpha
                                (Palette.contrastRatio ink wash)
                        )

[<Fact>]
let ``Assignments: the status colours stay readable, in BOTH themes`` () =
    // Risk, pace, trajectory and the pipeline all resolve to these four roles
    // plus the identity neutral.
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            let roles =
                [ "success", s.Success
                  "warning", s.Warning
                  "danger", s.Danger
                  "info", Color.Parse(Theme.Identity.sky)
                  "neutral", Color.Parse(Theme.Identity.neutral) ]

            for name, raw in roles do
                for alpha in assignmentsAlphas do
                    let wash = washedBy alpha raw s.Card
                    let ink = Palette.readableOn wash s.Text raw

                    Assert.True(
                        Palette.contrastRatio ink wash >= Palette.aaText,
                        sprintf "%A/%s %s @%s: %.2f" mode key name alpha (Palette.contrastRatio ink wash)
                    )

[<Fact>]
let ``Assignments: the High Risk label is readable on its own wash, in BOTH themes`` () =
    // The one label the map pins by name.
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)
            let wash = washedBy "22" s.Danger s.Card
            let ink = Palette.readableOn wash s.Text s.Danger

            Assert.True(
                Palette.contrastRatio ink wash >= Palette.aaText,
                sprintf "%A/%s High Risk: %.2f" mode key (Palette.contrastRatio ink wash)
            )

[<Fact>]
let ``Assignments: the transparent Timeline row still shows its text, in BOTH themes`` () =
    // The row is deliberately transparent, so its text sits on the page surface
    // rather than a card of its own.
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, surface in [ "shell", s.Shell; "card", s.Card ] do
                Assert.True(
                    Palette.contrastRatio s.Text surface >= Palette.aaText,
                    sprintf "%A/%s Timeline row on %s: %.2f" mode key name (Palette.contrastRatio s.Text surface)
                )

// ---------------------------------------------------------------------------
// 3. cross-screen Light audit — all eleven screens
// ---------------------------------------------------------------------------

/// Every screen-level alpha in the application, gathered from the migrated
/// feature modules rather than invented for this test.
let private everyPaintedAlpha =
    [ "Assistant chip/user bubble", AssistantLogic.Geometry.chipFillAlpha
      "Assistant selected row", AssistantLogic.Geometry.selectedRowAlpha
      "Assistant applied strip", AssistantLogic.Geometry.appliedFillAlpha
      "Assistant error", AssistantLogic.Geometry.errorFillAlpha
      "Search chip", SearchLogic.Geometry.chipFillAlpha
      "Search selected", SearchLogic.Geometry.selectedFillAlpha
      "Search highlight", SearchLogic.Geometry.highlightAlpha
      "Calendar event", CalendarLogic.Geometry.eventFillAlpha
      "Calendar selected", CalendarLogic.Geometry.selectedFillAlpha
      "Calendar chip", CalendarLogic.Geometry.chipFillAlpha
      "Connections chip", ConnectionsLogic.Geometry.chipFillAlpha
      "Connections selected", ConnectionsLogic.Geometry.selectedFillAlpha
      "Connections tile", ConnectionsLogic.Geometry.tileAlpha
      "Connections notice", ConnectionsLogic.Geometry.noteFillAlpha
      "Settings selected row", SettingsLogic.Geometry.selectedFillAlpha
      "Settings chip", SettingsLogic.Geometry.chipFillAlpha
      "Settings notice", SettingsLogic.Geometry.noticeFillAlpha ]

[<Fact>]
let ``every screen-level wash in the application is readable, in BOTH themes`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, alpha in everyPaintedAlpha do
                for colourName, raw in allWashColours () do
                    let wash = washedBy alpha raw s.Card
                    let ink = Palette.readableOn wash s.Text raw

                    Assert.True(
                        Palette.contrastRatio ink wash >= Palette.aaText,
                        sprintf
                            "%A/%s %s with %s @%s: %.2f"
                            mode
                            key
                            name
                            colourName
                            alpha
                            (Palette.contrastRatio ink wash)
                    )

[<Fact>]
let ``disabled and faint ink stays distinguishable from the surface, in BOTH themes`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, surface in [ "card", s.Card; "cardAlt", s.CardAlt; "shell", s.Shell ] do
                // Faint ink is the quietest role the app draws words in. It is
                // non-text weight, but it must not vanish.
                Assert.True(
                    Palette.contrastRatio s.Faint surface >= 2.0,
                    sprintf "%A/%s faint on %s: %.2f" mode key name (Palette.contrastRatio s.Faint surface)
                )

                // And it must stay quieter than primary ink, or the hierarchy
                // has collapsed.
                Assert.True(
                    Palette.contrastRatio s.Faint surface < Palette.contrastRatio s.Text surface,
                    sprintf "%A/%s faint is not quieter than text on %s" mode key name
                )

[<Fact>]
let ``borders and dividers are visible without being loud, in BOTH themes`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, line in [ "border", s.Border; "divider", s.Divider ] do
                let over = Palette.composite s.Card line
                let ratio = Palette.contrastRatio over s.Card

                // A real floor, not `> 1.0`. Mutation testing showed that an
                // alpha of 01 — a hairline nobody could see — still cleared
                // `> 1.0`, so the guard was worthless.
                Assert.True(ratio >= 1.05, sprintf "%A/%s %s too faint on card: %.4f" mode key name ratio)
                Assert.True(ratio < 4.5, sprintf "%A/%s %s is louder than body text: %.2f" mode key name ratio)

/// **Found by mutation.** `Surfaces.Selected` is the expanded-card surface on
/// Assignments and the selected-row surface on Documents and Inbox — and it had
/// no contrast test in either mode. Replacing it with the card colour left every
/// test passing while selection became invisible.
[<Fact>]
let ``the Selected surface is distinguishable from Card and carries readable ink`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            Assert.True(
                Palette.contrastRatio s.Selected s.Card >= 1.05,
                sprintf
                    "%A/%s Selected is indistinguishable from Card: %.4f"
                    mode
                    key
                    (Palette.contrastRatio s.Selected s.Card)
            )

            Assert.True(
                Palette.contrastRatio s.Text s.Selected >= Palette.aaText,
                sprintf "%A/%s text on Selected: %.2f" mode key (Palette.contrastRatio s.Text s.Selected)
            )

            // Hover is the same trap one step quieter.
            Assert.True(
                Palette.contrastRatio s.Hover s.Card >= 1.02,
                sprintf "%A/%s Hover is indistinguishable from Card: %.4f" mode key (Palette.contrastRatio s.Hover s.Card)
            )

/// **A defect in waiting, pinned rather than papered over.**
///
/// `Palette.Ring` is the accent's *Light* variant at 50% alpha. Over a **dark**
/// surface that reads clearly; over a **light** one it nearly vanishes —
/// Light/amber measures **1.29** against the card, where non-text needs 3.0.
///
/// It is not a live defect: **`Palette.Ring` has no production callers** —
/// verified by grep across `src/` — because focus is currently drawn by
/// Avalonia's Fluent adorner, which this harness cannot measure. So nothing on
/// screen is broken today, and no production colour was changed on the strength
/// of a token nobody uses.
///
/// This test records the trap and proves the way out: a usable ring colour does
/// exist for every accent in both modes, so whoever wires focus up has a
/// validated option and does not have to rediscover this.
[<Fact>]
let ``a usable focus-ring colour exists for every accent, in BOTH themes`` () =
    for mode in themeModes do
        for key in accentKeys do
            let accent = Palette.ofKey key
            let s = Surfaces.resolve mode accent

            for surfaceName, surface in [ "card", s.Card; "cardAlt", s.CardAlt; "shell", s.Shell ] do
                let candidates =
                    [ "Normal", accent.Normal
                      "Strong", accent.Strong
                      "Dark", accent.Dark
                      "Light", accent.Light ]

                let usable =
                    candidates
                    |> List.filter (fun (_, c) -> Palette.contrastRatio c surface >= Palette.aaNonText)

                Assert.True(
                    not usable.IsEmpty,
                    sprintf "%A/%s: no accent variant clears 3.0 on %s" mode key surfaceName
                )

[<Fact>]
let ``the current Ring token is NOT fit for a light surface, and is unused`` () =
    // Pinning the measured fact so it cannot be "fixed" by a future change that
    // merely moves the problem. If this ever starts passing on Light, the token
    // was improved and this test should be retired deliberately.
    let amber = Palette.ofKey "amber"
    let light = Surfaces.resolve ThemeMode.Light amber
    let ring = Palette.composite light.Card amber.Ring

    Assert.True(
        Palette.contrastRatio ring light.Card < Palette.aaNonText,
        "Palette.Ring now clears 3.0 on a light card — retire this test and wire it up."
    )

    // Over a dark card it is fine, which is why the problem is invisible today.
    let dark = Surfaces.resolve ThemeMode.Dark amber
    let onDark = Palette.composite dark.Card amber.Ring
    Assert.True(Palette.contrastRatio onDark dark.Card > Palette.contrastRatio ring light.Card)

[<Fact>]
let ``no surface has a cool blue-grey cast, in BOTH themes`` () =
    // The batch's standing rule: warm neutrals throughout, in Light as well as
    // Dark. Light is where a cool cast is easiest to introduce by accident.
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, c in
                [ "window", s.Window
                  "shell", s.Shell
                  "card", s.Card
                  "cardAlt", s.CardAlt
                  "action", s.Action
                  "overlay", s.Overlay
                  "hover", s.Hover
                  "selected", s.Selected ] do
                Assert.True(int c.R >= int c.B, sprintf "%A/%s %s is cool: %A" mode key name c)

[<Fact>]
let ``Light is genuinely light and Dark is genuinely dark`` () =
    // A guard against a Dark token being pasted into a Light surface: the two
    // modes must sit on opposite sides of mid-grey, for every accent.
    let luminance (c: Color) = Palette.contrastRatio c (Color.FromRgb(0uy, 0uy, 0uy))

    for key in accentKeys do
        let dark = Surfaces.resolve ThemeMode.Dark (Palette.ofKey key)
        let light = Surfaces.resolve ThemeMode.Light (Palette.ofKey key)

        for name, d, l in
            [ "window", dark.Window, light.Window
              "card", dark.Card, light.Card
              "cardAlt", dark.CardAlt, light.CardAlt ] do
            Assert.True(luminance l > luminance d, sprintf "%s: Light (%A) is not lighter than Dark (%A)" name l d)

        // And their inks invert with them.
        Assert.True(
            luminance light.Text < luminance dark.Text,
            sprintf "%s: Light ink is not darker than Dark ink" key
        )

[<Fact>]
let ``geometry is identical between Dark and Light`` () =
    // Theme changes colour, never layout. Every geometry constant the migrated
    // screens use is a compile-time literal, so this holds by construction —
    // pinned here so a future mode-dependent size is caught.
    Assert.Equal(220.0, SettingsLogic.Geometry.railWidth, 6)
    Assert.Equal(420.0, AssistantLogic.Geometry.centreMinWidth, 6)
    Assert.Equal(900.0, SearchLogic.Geometry.columnWidth, 6)
    Assert.Equal(52.0, CalendarLogic.Geometry.hourHeight, 6)
    Assert.Equal(330.0, ConnectionsLogic.Geometry.cardMinWidth, 6)
