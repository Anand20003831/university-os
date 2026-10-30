module UniversityOS.Tests.PrimaryActionTintTests

open System
open Xunit
open Avalonia.Media
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Controls
open UniversityOS.Tests.AuditHarness

/// **Four buttons were reported as looking greyed out: Regenerate Planner,
/// Accept, Accept All and New Note. They have one cause.**
///
/// A `GlassPanel`'s tint alpha follows a shared curve that bottoms out at 0.40,
/// where the panel is 60% blurred backdrop. A saturated accent behind that much
/// backdrop desaturates toward the surface — so a primary action drawn on glass
/// stops looking like the accent and starts looking like everything else.
///
/// `Glass.primaryTintAlpha` is the derived floor that fixes it. It existed, it
/// was correct, and it was applied in **one** of the **eleven** copies of the
/// glass action button in this application. Ten screens' primary actions were
/// desaturated; the reviewer happened to name four of them.
///
/// So the fix is not eleven fixes. There is now one `Ui.glassAction`, and the
/// tests below are written as a **cross-screen invariant** rather than a list of
/// buttons, because a list would go stale the moment somebody adds a twelfth
/// screen — which is exactly how this happened.
///
/// **What these tests cannot tell you.** They assert the tint floor reaches the
/// panel. Whether the button now *looks* like the accent in the running window
/// is the reviewer's judgement.

let private accentOf (tokens: Tokens) = tokens.Accent.Normal

let private glassPanels (root: Avalonia.FuncUI.Types.IView) =
    root |> ofType "GlassPanel"

let private tintOf (view: Avalonia.FuncUI.Types.IView) =
    byAvaloniaProperty GlassPanel.TintProperty view |> Option.map (fun v -> v :?> Color)

let private minTintAlphaOf (view: Avalonia.FuncUI.Types.IView) =
    byAvaloniaProperty GlassPanel.MinTintAlphaProperty view
    |> Option.map (fun v -> v :?> float)

// ---------------------------------------------------------------------------
// The invariant, across every migrated screen
// ---------------------------------------------------------------------------

/// **The test that would have caught this.**
///
/// Any glass surface tinted with the accent is, by construction, something the
/// design intends to read *as the accent*. Every one of them must carry the
/// floor, on every screen, or it desaturates at low glass settings.
[<Theory>]
[<InlineData "Settings">]
[<InlineData "Connections">]
[<InlineData "Calendar">]
[<InlineData "Search">]
[<InlineData "Assistant">]
[<InlineData "Graph">]
[<InlineData "Documents">]
[<InlineData "Notes">]
[<InlineData "Lectures">]
[<InlineData "Modules">]
[<InlineData "Assignments">]
let ``every accent-tinted glass surface carries the primary tint floor`` (screen: string) =
    let tokens = AuditFixtures.tokens
    let accent = accentOf tokens
    let tree = AuditFixtures.render screen 1400.0

    let accentTinted =
        glassPanels tree |> List.filter (fun v -> tintOf v = Some accent)

    for panel in accentTinted do
        Assert.Equal(
            Some Glass.primaryTintAlpha,
            minTintAlphaOf panel
        )

/// The floor is for the accent and nothing else. A neutral surface that quietly
/// acquired it would stop responding to the glass slider at all, which is a
/// different bug in the opposite direction.
[<Theory>]
[<InlineData "Settings">]
[<InlineData "Connections">]
[<InlineData "Calendar">]
[<InlineData "Search">]
[<InlineData "Assistant">]
[<InlineData "Graph">]
[<InlineData "Documents">]
[<InlineData "Notes">]
[<InlineData "Lectures">]
[<InlineData "Modules">]
[<InlineData "Assignments">]
let ``no neutral glass surface is pinned to the primary floor`` (screen: string) =
    let tokens = AuditFixtures.tokens
    let accent = accentOf tokens
    let tree = AuditFixtures.render screen 1400.0

    for panel in glassPanels tree do
        if tintOf panel <> Some accent then
            Assert.NotEqual(Some Glass.primaryTintAlpha, minTintAlphaOf panel)

// ---------------------------------------------------------------------------
// The shared button itself
// ---------------------------------------------------------------------------

let private button (primary: bool) (enabled: bool) =
    Ui.glassAction
        AuditFixtures.tokens
        Ui.GlassActionMetrics.standard
        "Accept"
        primary
        enabled
        "Accept this item"
        ignore

[<Fact>]
let ``a primary action gets the floor and a secondary one does not`` () =
    let primaryPanel = glassPanels (button true true) |> List.exactlyOne
    Assert.Equal(Some(accentOf AuditFixtures.tokens), tintOf primaryPanel)
    Assert.Equal(Some Glass.primaryTintAlpha, minTintAlphaOf primaryPanel)

    let secondaryPanel = glassPanels (button false true) |> List.exactlyOne
    Assert.NotEqual(Some(accentOf AuditFixtures.tokens), tintOf secondaryPanel)
    Assert.Equal(Some 0.0, minTintAlphaOf secondaryPanel)

/// **A genuinely disabled button must still read as disabled.** The requirement
/// was to stop *enabled* buttons looking dead, not to make dead ones look
/// available.
[<Fact>]
let ``enabled and disabled remain distinguishable`` () =
    Assert.Equal(Some true, boolProp "IsEnabled" (button true true))
    Assert.Equal(Some false, boolProp "IsEnabled" (button true false))

/// The class is what lets `App.Initialize` neutralise Fluent's own cool grey
/// `ButtonBackgroundDisabled`, which paints straight through the glass. Without
/// it the button is grey again, for a completely different reason.
[<Fact>]
let ``the button carries the classes its styling depends on`` () =
    let root = button true true

    // FuncUI records `classes` as a plain string sequence, not Avalonia's
    // `Classes` collection.
    let classesOf (view: Avalonia.FuncUI.Types.IView) =
        match propOf "Classes" view with
        | Some(:? System.Collections.IEnumerable as items) -> items |> Seq.cast<obj> |> Seq.map string |> List.ofSeq
        | _ -> []

    Assert.Contains(Ui.glassActionClass (), classesOf root)

    // And the veil, which is the only channel hover has: Opacity does not reach
    // a GlassPanel's Skia draw, so without this Border there is no hover at all.
    let veils =
        root |> all |> List.filter (fun v -> classesOf v |> List.contains (Ui.glassVeilClass ()))

    Assert.Single veils |> ignore

/// Nothing may shift when a state changes, so a row of actions cannot jitter as
/// the pointer crosses it.
[<Fact>]
let ``primary and secondary have identical geometry`` () =
    let geometry (view: Avalonia.FuncUI.Types.IView) =
        floatProp "MinHeight" view,
        floatProp "CornerRadius" view,
        propOf "Padding" view,
        propOf "Margin" view

    Assert.Equal(geometry (button true true), geometry (button false true))
    Assert.Equal(geometry (button true true), geometry (button true false))

[<Fact>]
let ``the button is named for assistive technology`` () =
    Assert.Equal(Some "Accept this item", accessibleName (button true true))

/// The metrics record exists so padding and margin can differ per screen — they
/// genuinely do — while everything that was wrong stays shared.
[<Fact>]
let ``metrics carry padding and margin, and nothing that was ever wrong`` () =
    let custom =
        { Ui.GlassActionMetrics.Padding = (21.0, 3.0)
          Ui.GlassActionMetrics.Margin = (1.0, 2.0, 3.0, 4.0) }

    let root =
        Ui.glassAction AuditFixtures.tokens custom "X" true true "X" ignore

    Assert.Equal(Some(box (Avalonia.Thickness(1.0, 2.0, 3.0, 4.0))), propOf "Margin" root)

    // The floor is not something a caller can forget or override.
    Assert.Equal(Some Glass.primaryTintAlpha, minTintAlphaOf (glassPanels root |> List.exactlyOne))
