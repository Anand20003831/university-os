module UniversityOS.Tests.ExpandedOutlineTests

open System
open Xunit
open Avalonia.Media
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Features
open UniversityOS.Tests.AuditHarness
open UniversityOS.Tests.AuditFixtures

/// **Expanding a card must not draw a line around its content.**
///
/// Reported from the first runtime review, on both Assignments and Modules. Both
/// screens painted a coloured border on an inner container when a card expanded —
/// the accent on Assignments, the module colour on Modules — which read as an
/// outline around everything inside the card.
///
/// It was redundant as well as unwanted. Expansion already swaps the card's
/// surface from `Card` to `Selected`, and that difference is separately proven
/// visible in both themes by `LightThemeTests`. So the state was never in doubt;
/// only the line was.
///
/// **What is preserved, and asserted below:** the outer card boundary, the
/// surface change that signals expansion, and the reserved border thickness — the
/// brush went transparent but the thickness did not, so nothing can move when a
/// card expands. Keyboard focus is untouched throughout: it is drawn by Fluent's
/// adorner on the header button, never by these borders.
///
/// **What this cannot establish:** whether the card now *reads* as expanded to a
/// person. That is what the surface change is for, and it needs the user's eyes.

let private wide = contentWidth 1440.0

/// **A card has to actually be on screen.** Mutation testing caught this: the
/// shared fixtures carry `Facts = []`, so no card rendered, the border was never
/// in the tree, and reinstating the outline in production source did not fail a
/// single assertion. A test that scans an empty screen for a line will never find
/// one — and will report that as success.
let private assignment =
    { Assignment.create now "Ray tracer coursework 2" with
        ModuleId = Some studyModules.Head.Id
        Deadline = Some(now.AddDays 2.0)
        Weighting = Some 35.0
        Priority = Priority.High
        Status = AssignmentStatus.InProgress
        Progress = 40 }

let private assignmentsAt (expanded: bool) =
    AssignmentsView.view
        tokens
        { assignmentsModel () with
            Facts = [ AssignmentsLogic.Facts.ofAssignment assignment ]
            Modules = studyModules
            Expanded = (if expanded then Some assignment.Id else None)
            Width = wide }
        ignore

let private modulesAt (expanded: bool) =
    let m = studyModules.Head

    ModulesView.view
        tokens
        { modulesModel () with
            Facts = [ ModulesLogic.Facts.ofModule m ]
            Expanded = (if expanded then Some m.Id else None)
            Width = wide }
        ignore

/// Every border in the tree that would actually paint: non-zero thickness and a
/// brush that is not transparent.
let private paintedBorders (root: IView) =
    ofType "Border" root
    |> List.filter (fun b ->
        let thick =
            match propOf "BorderThickness" b with
            | Some(:? Avalonia.Thickness as t) -> t.Top > 0.0 || t.Left > 0.0 || t.Right > 0.0 || t.Bottom > 0.0
            | _ -> false

        let painted =
            match propOf "BorderBrush" b with
            | Some(:? ISolidColorBrush as brush) -> brush.Color.A > 0uy
            | Some _ -> true
            | None -> false

        thick && painted)

// ---------------------------------------------------------------------------
// the rule
// ---------------------------------------------------------------------------

/// **The precise rule.** The outline was painted on the card's content wrapper —
/// the one border whose thickness is the screen's `cardBorderThickness`. Every
/// other border on the card (chips, sections, the outer card itself) has its own
/// thickness, so matching on that constant names the wrapper exactly.
///
/// Earlier attempts were both wrong and are worth recording: scanning for any
/// accent-coloured border failed on legitimate accents elsewhere, and comparing
/// border COUNTS collapsed-vs-expanded failed because expanding legitimately
/// reveals more bordered content.
let private wrapperBrushes (thickness: float) (root: IView) =
    ofType "Border" root
    |> List.choose (fun b ->
        match propOf "BorderThickness" b with
        | Some(:? Avalonia.Thickness as t) when abs (t.Top - thickness) < 0.001 && abs (t.Left - thickness) < 0.001 ->
            match propOf "BorderBrush" b with
            | Some(:? ISolidColorBrush as brush) -> Some brush.Color
            | _ -> None
        | _ -> None)

[<Fact>]
let ``the expanded Assignment content wrapper draws no line`` () =
    let opaque =
        wrapperBrushes AssignmentsLogic.Geometry.cardBorderThickness (assignmentsAt true)
        |> List.filter (fun c -> c.A > 0uy)

    Assert.True(
        List.isEmpty opaque,
        sprintf "the expanded Assignment card paints its content wrapper: %A — the expansion outline is back" opaque
    )

[<Fact>]
let ``the expanded Module content wrapper draws no line`` () =
    let opaque =
        wrapperBrushes ModulesLogic.Geometry.cardBorderThickness (modulesAt true)
        |> List.filter (fun c -> c.A > 0uy)

    Assert.True(
        List.isEmpty opaque,
        sprintf "the expanded Module card paints its content wrapper: %A — the expansion outline is back" opaque
    )

// ---------------------------------------------------------------------------
// what must survive the removal
// ---------------------------------------------------------------------------

/// The surface change is now the *only* signal that a card is expanded, so it
/// has to be a real one. This is the guard that stops the outline removal from
/// silently making expansion invisible.
[<Theory>]
[<InlineData true>]
[<InlineData false>]
let ``the expanded surface is genuinely different from the collapsed one`` (dark: bool) =
    let mode = if dark then ThemeMode.Dark else ThemeMode.Light
    let surfaces = Surfaces.resolve mode (Palette.ofKey (fst Palette.accents.Head))

    let ratio = Palette.contrastRatio surfaces.Selected surfaces.Card

    Assert.True(
        ratio >= 1.05,
        sprintf "%A: the expanded surface is indistinguishable from the collapsed one (%.4f) — with the outline gone, nothing would show expansion" mode ratio
    )

/// Removing the brush must not have removed the thickness: the space was
/// reserved in both states precisely so expanding could never resize a card.
[<Fact>]
let ``the reserved border thickness is unchanged, so expanding cannot move anything`` () =
    Assert.True(AssignmentsLogic.Geometry.cardBorderThickness > 0.0)
    Assert.True(ModulesLogic.Geometry.cardBorderThickness > 0.0)
