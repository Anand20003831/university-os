module UniversityOS.Tests.GlyphCentringTests

open System
open Xunit
open Avalonia.Layout
open Avalonia.FuncUI.Types
open UniversityOS.App
open UniversityOS.App.Features
open UniversityOS.Tests.AuditHarness
open UniversityOS.Tests.AuditFixtures

/// **Glyph buttons with a fixed square box must centre their content.**
///
/// Reported from the first runtime review: Calendar's `‹` / `›` navigation
/// glyphs and both of Notes' sidebar disclosure glyphs were off-centre.
///
/// The mechanism is the same in both places and is not obvious. A `Button` with
/// an explicit `Width`/`Height` and `Padding 0.0` does **not** centre a string
/// content on its own — `ContentControl`'s content alignment defaults to
/// `Stretch`, so the `TextBlock` Avalonia wraps the string in fills the whole
/// square and draws its text at the top-left corner of that stretched box. The
/// glyph then sits high and left inside an otherwise correct button.
///
/// The fix is to state both content alignments explicitly. That is what these
/// tests pin: any glyph button that fixes its own size must also say where the
/// glyph goes.
///
/// **What this cannot establish:** where the glyph actually lands once the font
/// is measured. Side bearings and baseline metrics are a rendering concern, and a
/// glyph can still look optically off-centre while being geometrically centred.
/// That part needs the user's eyes.

let private wide = contentWidth 1440.0

/// Buttons that declare a fixed square and therefore must declare alignment too.
let private fixedSquareButtons (root: IView) =
    ofType "Button" root
    |> List.filter (fun b ->
        match floatProp "Width" b, floatProp "Height" b with
        | Some w, Some h -> w > 0.0 && h > 0.0 && abs (w - h) < 0.01
        | _ -> false)

let private centres (b: IView) =
    let h =
        match propOf "HorizontalContentAlignment" b with
        | Some(:? HorizontalAlignment as a) -> a = HorizontalAlignment.Center
        | _ -> false

    let v =
        match propOf "VerticalContentAlignment" b with
        | Some(:? VerticalAlignment as a) -> a = VerticalAlignment.Center
        | _ -> false

    h && v

[<Theory>]
[<InlineData "Calendar">]
[<InlineData "Notes">]
let ``every fixed-square glyph button states both content alignments`` (screen: string) =
    let offenders =
        fixedSquareButtons (render screen wide)
        |> List.filter (fun b -> not (centres b))
        |> List.map (fun b -> stringProp "Content" b |> Option.defaultValue "<no content>")

    Assert.True(
        List.isEmpty offenders,
        sprintf
            "%s has %d fixed-square button(s) that do not centre their glyph: %A — content alignment defaults to Stretch, so the glyph draws at the top-left"
            screen offenders.Length offenders
    )

/// The Calendar navigation buttons specifically, named so a failure points at
/// the control the user actually reported rather than at a count.
[<Fact>]
let ``the Calendar previous and next buttons centre their glyphs`` () =
    let tree = render "Calendar" wide

    let navButtons =
        ofType "Button" tree
        |> List.filter (fun b ->
            match stringProp "Content" b with
            | Some c -> c = "‹" || c = "›"
            | None -> false)

    Assert.True(navButtons.Length >= 2, sprintf "expected both nav glyphs, found %d" navButtons.Length)

    for b in navButtons do
        Assert.True(centres b, sprintf "the %s button does not centre its glyph" (stringProp "Content" b |> Option.defaultValue "?"))

/// Both Notes disclosure controls go through one helper, so fixing it fixes the
/// expanded rail toggle and the collapsed rail alike. This asserts the glyphs are
/// present and centred rather than asserting the helper's identity, so the test
/// survives a refactor that keeps the behaviour.
[<Fact>]
let ``the Notes sidebar disclosure glyphs are centred`` () =
    let tree = render "Notes" wide

    let disclosures =
        ofType "Button" tree
        |> List.filter (fun b ->
            match stringProp "Content" b with
            | Some c -> c = "‹" || c = "›"
            | None -> false)

    Assert.True(not (List.isEmpty disclosures), "no disclosure glyphs found on Notes")

    for b in disclosures do
        Assert.True(centres b, "a Notes disclosure glyph does not centre")

/// Glyph buttons must keep their spoken names — centring is a layout fix and
/// must not disturb what a screen reader announces.
[<Theory>]
[<InlineData "Calendar">]
[<InlineData "Notes">]
let ``centring a glyph button does not cost it its accessible name`` (screen: string) =
    let unnamed =
        fixedSquareButtons (render screen wide)
        |> List.filter (fun b -> (accessibleName b).IsNone)
        |> List.map (fun b -> stringProp "Content" b |> Option.defaultValue "<no content>")

    Assert.True(List.isEmpty unnamed, sprintf "%s: fixed-square buttons with no accessible name: %A" screen unnamed)
