module UniversityOS.Tests.BadgeSizingTests

open System
open Xunit
open Avalonia.Layout
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Features
open UniversityOS.Tests.AuditHarness
open UniversityOS.Tests.AuditFixtures

/// **A badge must be the size of its label.**
///
/// Reported from the first runtime review: the Planner status badge and the
/// Assignments *Missing estimates* badge had backgrounds that did not hug their
/// text.
///
/// The cause is a default, not a mistake anyone made explicitly. In Avalonia
/// `Layoutable.HorizontalAlignment` defaults to **Stretch**, so a `Border` fills
/// whatever cell it is placed in. A pill inside a horizontal `StackPanel` gets
/// its desired width and looks right; the same pill in a `Grid` cell or a
/// vertical `StackPanel` silently spans the whole column. Nothing in the badge
/// helper says "stretch" — it simply never said not to.
///
/// The contract asserted here: a badge sizes to content plus its own padding, in
/// every container, so it cannot depend on where a caller happens to put it.
///
/// **What this cannot establish:** the rendered pixel width. Text measurement
/// needs a live font, so these tests check the *declared* sizing contract — no
/// fixed width, no stretch, real padding — which is what actually decides
/// whether the background hugs.

let private wide = contentWidth 1440.0

/// Badge-shaped elements: fully rounded borders, which is what every pill and
/// status chip in this codebase is.
let private badges (root: IView) =
    ofType "Border" root
    |> List.filter (fun b ->
        match propOf "CornerRadius" b with
        | Some(:? Avalonia.CornerRadius as r) -> r.TopLeft >= 100.0
        | _ -> false)

let private horizontalAlignmentOf (b: IView) =
    match propOf "HorizontalAlignment" b with
    | Some(:? HorizontalAlignment as a) -> Some a
    | _ -> None

/// A badge that would stretch: it states no horizontal alignment, or states
/// Stretch outright.
let private stretching (root: IView) =
    badges root
    |> List.filter (fun b ->
        match horizontalAlignmentOf b with
        | None -> true
        | Some HorizontalAlignment.Stretch -> true
        | Some _ -> false)

// ---------------------------------------------------------------------------
// the contract, on the shared helper
// ---------------------------------------------------------------------------

[<Fact>]
let ``the shared pill sizes to its content`` () =
    let badge = Ui.pill "Done" "#4FC98F"

    Assert.NotEqual<HorizontalAlignment option>(Some HorizontalAlignment.Stretch, horizontalAlignmentOf badge)
    Assert.True((horizontalAlignmentOf badge).IsSome, "the pill does not state a horizontal alignment, so it inherits Stretch")

    Assert.True((floatProp "Width" badge).IsNone, "the pill declares a fixed width")
    Assert.True((floatProp "MinWidth" badge).IsNone, "the pill declares a minimum width")

/// Padding is what makes it a badge rather than bare text, so it has to be real
/// in both directions.
[<Fact>]
let ``the shared pill reserves deliberate padding on both axes`` () =
    match propOf "Padding" (Ui.pill "Done" "#4FC98F") with
    | Some(:? Avalonia.Thickness as t) ->
        Assert.True(t.Left > 0.0 && t.Right > 0.0, "no horizontal padding")
        Assert.True(t.Top > 0.0 && t.Bottom > 0.0, "no vertical padding")
    | _ -> failwith "the pill declares no padding"

/// Every label the review names, short and long, through the same helper. A
/// badge whose geometry depends on its text length is the defect restated.
[<Theory>]
[<InlineData "Done">]
[<InlineData "Scheduled">]
[<InlineData "In progress">]
[<InlineData "Missed">]
[<InlineData "Missing estimates">]
[<InlineData "High priority">]
[<InlineData "Medium priority">]
[<InlineData "Low priority">]
[<InlineData "Low effort">]
[<InlineData "Medium effort">]
[<InlineData "High effort">]
let ``every reviewed label produces a content-sized badge`` (label: string) =
    let badge = Ui.pill label "#4FC98F"

    Assert.NotEqual<HorizontalAlignment option>(Some HorizontalAlignment.Stretch, horizontalAlignmentOf badge)
    Assert.True((floatProp "Width" badge).IsNone, sprintf "%A got a fixed width" label)

    // The label must actually be in it — colour alone never states a state.
    Assert.True(
        everyString badge |> List.exists (fun s -> s = label),
        sprintf "the badge does not carry the label %A" label
    )

// ---------------------------------------------------------------------------
// the two screens the review named
// ---------------------------------------------------------------------------

/// Planner is not one of the eleven migrated screens, so it is not in the shared
/// fixture list — build it directly.
let private plannerTree () =
    let model, _ = Planner.init noServices
    Planner.view tokens { model with Width = wide } ignore

[<Fact>]
let ``no badge on the Planner stretches across its cell`` () =
    let offenders = stretching (plannerTree ()) |> List.map (fun b -> everyString b)

    Assert.True(
        List.isEmpty offenders,
        sprintf "%d Planner badge(s) would stretch across their cell: %A" offenders.Length offenders
    )

[<Fact>]
let ``no badge on Assignments stretches across its cell`` () =
    let offenders = stretching (render "Assignments" wide) |> List.map (fun b -> everyString b)

    Assert.True(
        List.isEmpty offenders,
        sprintf "%d Assignments badge(s) would stretch across their cell: %A" offenders.Length offenders
    )

/// The populated case, which is the one the user saw — an expanded assignment
/// with a risk panel, where *Missing estimates* actually renders.
[<Fact>]
let ``no badge stretches on a populated, expanded Assignment`` () =
    let assignment =
        { Assignment.create now "Ray tracer coursework 2" with
            ModuleId = Some studyModules.Head.Id
            Deadline = Some(now.AddDays 2.0)
            Priority = Priority.High
            Status = AssignmentStatus.InProgress
            Progress = 40 }

    let tree =
        AssignmentsView.view
            tokens
            { assignmentsModel () with
                Facts = [ AssignmentsLogic.Facts.ofAssignment assignment ]
                Modules = studyModules
                Expanded = Some assignment.Id
                Width = wide }
            ignore

    let offenders = stretching tree |> List.map (fun b -> everyString b)

    Assert.True(
        List.isEmpty offenders,
        sprintf "%d badge(s) stretch on an expanded Assignment: %A" offenders.Length offenders
    )
