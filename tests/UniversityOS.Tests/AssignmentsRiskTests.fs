module UniversityOS.Tests.AssignmentsRiskTests

open System
open Avalonia.Layout
open Avalonia.FuncUI.Types
open Xunit
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Features
open UniversityOS.Tests.AuditHarness

/// The Risk Overview card, rendered with real assignments.
///
/// The shared badge contract was corrected earlier (a badge inherits
/// `HorizontalAlignment.Stretch` unless it says otherwise, so it spanned its
/// whole Grid cell). That fix was made in `Ui.pill` and the local `chip`; these
/// tests check it actually reaches **this caller**, which is what the runtime
/// review reported, rather than assuming a shared fix arrives everywhere.

let private now = DateTime(2026, 3, 4, 12, 0, 0)
let private weekStart = StudyTime.weekStart DayOfWeek.Monday now
let private wide = contentWidth 1440.0

let private assignmentId (n: int) =
    AssignmentId(Guid(sprintf "00000000-0000-0000-0000-%012d" n))

/// An assignment with, or without, a stored estimate. Everything else is held
/// constant so the only thing varying across these tests is what is being
/// measured.
let private assignmentWith (n: int) (estimate: float option) =
    { Assignment.create now (sprintf "Coursework %d" n) with
        Id = assignmentId n
        Deadline = Some(now.AddDays 4.0)
        Weighting = Some 30.0
        Priority = Priority.High
        Status = AssignmentStatus.InProgress
        EstimatedHours = estimate
        Progress = 40 }

let private factsFor (estimates: float option list) =
    estimates
    |> List.mapi (fun i estimate -> AssignmentsLogic.Facts.ofAssignment (assignmentWith (i + 1) estimate))

let private renderWith (estimates: float option list) =
    AssignmentsView.view
        AuditFixtures.tokens
        { AuditFixtures.assignmentsModel () with
            Facts = factsFor estimates
            Modules = AuditFixtures.studyModules
            WeekStart = weekStart
            Width = wide }
        ignore

/// Badge-shaped elements: fully rounded borders, which is what every pill and
/// chip in this codebase is.
let private badges (root: IView) =
    ofType "Border" root
    |> List.filter (fun b ->
        match propOf "CornerRadius" b with
        | Some(:? Avalonia.CornerRadius as r) -> r.TopLeft >= 100.0
        | _ -> false)

let private stretching (root: IView) =
    badges root
    |> List.filter (fun b ->
        match propOf "HorizontalAlignment" b with
        | Some(:? HorizontalAlignment as a) -> a = HorizontalAlignment.Stretch
        | _ -> true)

let private says (fragment: string) (root: IView) =
    everyString root |> List.exists (fun s -> s.Contains(fragment, StringComparison.Ordinal))

// ---------------------------------------------------------------------------
// the badge geometry, at this caller
// ---------------------------------------------------------------------------

/// Zero, one and several — the three cases the review named, each rendered from
/// real assignments rather than an empty tree.
[<Theory>]
[<InlineData 0>]
[<InlineData 1>]
[<InlineData 3>]
let ``no badge stretches in the Risk Overview, whatever the missing count`` (missing: int) =
    let estimates =
        [ for i in 1..4 -> if i <= missing then None else Some 6.0 ]

    let offenders = stretching (renderWith estimates) |> List.map everyString

    Assert.True(
        List.isEmpty offenders,
        sprintf "%d badge(s) stretch with %d missing estimate(s): %A" offenders.Length missing offenders
    )

/// The row must exist at all, or the tests above would be scanning a screen that
/// never renders the card.
[<Fact>]
let ``the Risk Overview states the missing-estimates row`` () =
    Assert.True(
        says "Missing estimates" (renderWith [ None; Some 6.0 ]),
        "the Missing estimates row is not on the screen, so the badge tests would be vacuous"
    )

// ---------------------------------------------------------------------------
// the value itself
// ---------------------------------------------------------------------------

/// The count is the real count. A row that always said something reassuring, or
/// that hid a zero, would be worse than no row.
[<Theory>]
[<InlineData(0, "0")>]
[<InlineData(1, "1")>]
[<InlineData(3, "3")>]
let ``the missing-estimates value is the real count`` (missing: int, expected: string) =
    let estimates =
        [ for i in 1..4 -> if i <= missing then None else Some 6.0 ]

    Assert.Equal(missing, AssignmentsLogic.missingEstimateCount (factsFor estimates))
    Assert.True(says expected (renderWith estimates), sprintf "the value %s is not on the screen" expected)

/// Zero is stated, not omitted. "No missing estimates" is information; a blank
/// space is not.
[<Fact>]
let ``zero missing estimates is stated rather than hidden`` () =
    let tree = renderWith [ Some 6.0; Some 4.0 ]

    Assert.True(says "Missing estimates" tree, "the row disappears when the count is zero")
    Assert.Equal(0, AssignmentsLogic.missingEstimateCount (factsFor [ Some 6.0; Some 4.0 ]))

// ---------------------------------------------------------------------------
// the surrounding row
// ---------------------------------------------------------------------------

/// The value column is right-aligned and hugs its content — the badge sits at
/// the right of the row without the row itself stretching it.
[<Fact>]
let ``the forecast value column hugs its content on the right`` () =
    let tree = renderWith [ None; Some 6.0 ]

    let rightAligned =
        ofType "Border" tree
        |> List.filter (fun b ->
            match propOf "HorizontalAlignment" b with
            | Some(:? HorizontalAlignment as a) -> a = HorizontalAlignment.Right
            | _ -> false)

    Assert.True(
        not (List.isEmpty rightAligned),
        "no right-aligned value container was found in the Risk Overview"
    )

/// **Found by mutation.** Every fixture above is open work, so the `isOpen`
/// filter never mattered and removing it changed nothing. A finished assignment
/// that was never estimated is not work the planner still has to guess at, so
/// counting it would overstate the figure — this is the case that proves the
/// filter is doing something.
[<Fact>]
let ``a finished assignment without an estimate is not counted as missing`` () =
    let openUnestimated =
        AssignmentsLogic.Facts.ofAssignment (assignmentWith 1 None)

    let finishedUnestimated =
        AssignmentsLogic.Facts.ofAssignment
            { assignmentWith 2 None with Status = AssignmentStatus.Submitted }

    Assert.Equal(1, AssignmentsLogic.missingEstimateCount [ openUnestimated; finishedUnestimated ])

    // And on its own, a finished one contributes nothing at all.
    Assert.Equal(0, AssignmentsLogic.missingEstimateCount [ finishedUnestimated ])
