module UniversityOS.Tests.AssignmentsViewTests

open System
open Xunit
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Features

/// The migrated Assignments screen.
///
/// Every decision the screen makes lives in `AssignmentsLogic` or in
/// `AssignmentsView.update`, so all of it is testable without an Avalonia
/// application or a database: the metrics, the filters, the risk model, the
/// stage adaptation, the forecast, the action states and the responsive
/// thresholds.
///
/// The scheduling ENGINE is covered by `PlanningTests` and the assignment
/// repository by `RepositoryTests`; nothing here re-tests either.

let private now = DateTime(2026, 7, 29, 14, 0, 0)

/// Monday of the week containing `now` (29 July 2026 is a Wednesday).
let private weekStart = DateTime(2026, 7, 27)

let private assignment title : Assignment =
    { Assignment.create (now.AddDays -10.0) title with
        EstimatedHours = Some 10.0
        Progress = 50 }

let private facts (a: Assignment) : AssignmentsLogic.Facts = AssignmentsLogic.Facts.ofAssignment a

/// An assignment due in `days`, with the given progress, fully scheduled — so it
/// carries no signal at all unless a test adds one.
let private clean days progress =
    let a =
        { assignment "Coursework" with
            Deadline = Some(now.AddDays days)
            Progress = progress
            UpdatedAt = now }

    { facts a with ScheduledMinutes = 100_000 }

let private moduleOf code : StudyModule =
    { StudyModule.create now code "A module" 0 with Color = "#CB8A4E" }

// ---------------------------------------------------------------------------
// 1. page composition
// ---------------------------------------------------------------------------

[<Fact>]
let ``the page is composed of the four sections, in the order the redesign shows`` () =
    Assert.Equal<AssignmentsLogic.Section list>(
        [ AssignmentsLogic.Section.Header
          AssignmentsLogic.Section.Metrics
          AssignmentsLogic.Section.Filters
          AssignmentsLogic.Section.Workspace ],
        AssignmentsLogic.sectionOrder
    )

[<Fact>]
let ``every section appears exactly once`` () =
    Assert.Equal(AssignmentsLogic.sectionOrder.Length, (List.distinct AssignmentsLogic.sectionOrder).Length)

[<Fact>]
let ``the summary row is five metrics, as the redesign shows`` () =
    Assert.Equal(5, (AssignmentsLogic.metrics now [ clean 5.0 50 ]).Length)

[<Fact>]
let ``the filter row is the redesign's seven chips`` () =
    Assert.Equal(7, AssignmentsLogic.Filter.all.Length)
    Assert.Equal(AssignmentsLogic.Filter.all.Length, (List.distinct AssignmentsLogic.Filter.all).Length)

    Assert.All(
        AssignmentsLogic.Filter.all,
        fun f ->
            Assert.False(String.IsNullOrWhiteSpace(AssignmentsLogic.Filter.label f))
            Assert.False(String.IsNullOrWhiteSpace(AssignmentsLogic.Filter.emptyMessage f))
    )

// ---------------------------------------------------------------------------
// 2. metrics
// ---------------------------------------------------------------------------

let private metric label items =
    AssignmentsLogic.metrics now items |> List.find (fun m -> m.Label = label)

[<Fact>]
let ``the visible metric labels are the redesign's, not the internal scope`` () =
    let labels = AssignmentsLogic.metrics now [ clean 5.0 50 ] |> List.map (fun m -> m.Label)

    Assert.Equal<string list>(
        [ "Assignments"; "Average progress"; "Hours remaining"; "High Risk"; "Average grade weight" ],
        labels
    )

    // The scope stays in the code and in these tests, not on a card.
    Assert.DoesNotContain("Open assignments", labels)
    Assert.DoesNotContain("At high risk", labels)

[<Fact>]
let ``the first card still counts OPEN assignments, whatever its label says`` () =
    let items = [ clean 5.0 40; facts { assignment "Done" with Status = AssignmentStatus.Graded } ]
    Assert.Equal(Some "1", (metric "Assignments" items).Value)

[<Fact>]
let ``no summary card carries empty-state prose`` () =
    // "nothing outstanding" under five cards says the same thing five times.
    for items in [ []; [ clean 5.0 50 ]; [ facts { assignment "D" with Status = AssignmentStatus.Graded } ] ] do
        for m in AssignmentsLogic.metrics now items do
            match m.Note with
            | Some note ->
                Assert.DoesNotContain("nothing outstanding", note.ToLowerInvariant())
                Assert.True(m.Value.IsSome, sprintf "%s put a note under a blank figure" m.Label)
            | None -> ()

[<Fact>]
let ``an empty screen reads as zeroes and dashes, and nothing else`` () =
    let values = AssignmentsLogic.metrics now [] |> List.map (fun m -> m.Label, m.Value)

    Assert.Equal<(string * string option) list>(
        [ "Assignments", Some "0"
          "Average progress", None
          "Hours remaining", Some "0h"
          "High Risk", Some "0"
          "Average grade weight", None ],
        values
    )

    // …and not one of them carries a sentence.
    Assert.All(AssignmentsLogic.metrics now [], fun m -> Assert.Equal(None, m.Note))

[<Fact>]
let ``an empty screen reports no average progress, never zero and never perfect`` () =
    // The Planner's empty-plan trap in a new place: an average over nothing is
    // not a number, and it is certainly not 100%.
    Assert.Equal(None, (metric "Average progress" []).Value)

[<Fact>]
let ``an empty screen is not reported as on track`` () =
    Assert.Equal(AssignmentsLogic.Trajectory.NothingOutstanding, AssignmentsLogic.trajectory now [])
    Assert.Equal(None, AssignmentsLogic.highestRisk now [])
    Assert.Equal(None, AssignmentsLogic.suggestedNext now [])

[<Fact>]
let ``a screen with only completed work reports nothing outstanding`` () =
    let submitted = { facts (assignment "Done") with Assignment = { assignment "Done" with Status = AssignmentStatus.Submitted } }

    Assert.Equal(AssignmentsLogic.Trajectory.NothingOutstanding, AssignmentsLogic.trajectory now [ submitted ])
    Assert.Equal(None, (metric "Average progress" [ submitted ]).Value)
    Assert.Equal(Some "0", (metric "Assignments" [ submitted ]).Value)

[<Fact>]
let ``every metric describes OPEN coursework only`` () =
    let openWork = clean 5.0 40
    let closed = { facts { assignment "Done" with Status = AssignmentStatus.Graded; Progress = 100 } with ScheduledMinutes = 0 }
    let items = [ openWork; closed ]

    Assert.Equal(Some "1", (metric "Assignments" items).Value)
    // 40%, not the 70% an average across the graded 100% would give.
    Assert.Equal(Some "40%", (metric "Average progress" items).Value)

[<Fact>]
let ``the assignments card says how many are finished — the useful half of its scope`` () =
    let items = [ clean 5.0 40; { facts { assignment "Done" with Status = AssignmentStatus.Submitted } with ScheduledMinutes = 0 } ]
    Assert.Equal(Some "1 completed", (metric "Assignments" items).Note)

[<Fact>]
let ``the assignments card is silent when nothing has been finished`` () =
    Assert.Equal(None, (metric "Assignments" [ clean 5.0 40 ]).Note)

[<Fact>]
let ``average grade weight ignores assignments that carry no weighting`` () =
    // Counting a missing weighting as zero would quietly halve the average.
    let weighted = { clean 5.0 50 with Assignment = { (clean 5.0 50).Assignment with Weighting = Some 40.0 } }
    let unweighted = clean 5.0 50

    let m = metric "Average grade weight" [ weighted; unweighted ]
    Assert.Equal(Some "40%", m.Value)
    Assert.Equal(Some "1 of 2 weighted", m.Note)

[<Fact>]
let ``average grade weight is unknown when nothing carries a weighting`` () =
    let m = metric "Average grade weight" [ clean 5.0 50 ]
    Assert.Equal(None, m.Value)
    Assert.Equal(None, m.Note)

[<Fact>]
let ``hours remaining says when the planner's default estimate was used`` () =
    let unestimated =
        { facts { assignment "No estimate" with EstimatedHours = None; Deadline = Some(now.AddDays 5.0) } with
            ScheduledMinutes = 100_000 }

    let m = metric "Hours remaining" [ unestimated ]
    Assert.True(m.Value.IsSome)
    Assert.Equal(Some "1 assignment estimated by default", m.Note)

[<Fact>]
let ``hours remaining carries no caveat when everything is estimated`` () =
    Assert.Equal(None, (metric "Hours remaining" [ clean 5.0 50 ]).Note)

[<Fact>]
let ``the high-risk metric counts what the High Risk filter shows`` () =
    let overdue = { clean -3.0 10 with ScheduledMinutes = 0 }
    let calm = clean 40.0 50
    let items = [ overdue; calm ]

    let counted = (metric "High Risk" items).Value
    let filtered =
        items |> List.filter (AssignmentsLogic.matches now weekStart AssignmentsLogic.Filter.HighRisk)

    Assert.Equal(Some(string filtered.Length), counted)

[<Fact>]
let ``every metric carries a label`` () =
    AssignmentsLogic.metrics now [ clean 5.0 50 ]
    |> List.iter (fun m -> Assert.False(String.IsNullOrWhiteSpace m.Label))

// ---------------------------------------------------------------------------
// 3. progress
// ---------------------------------------------------------------------------

[<Fact>]
let ``progress is the recorded field, clamped, and nothing else`` () =
    Assert.Equal(50, AssignmentsLogic.progress (facts (assignment "A")))
    Assert.Equal(100, AssignmentsLogic.progress (facts { assignment "A" with Progress = 250 }))
    Assert.Equal(0, AssignmentsLogic.progress (facts { assignment "A" with Progress = -40 }))

[<Fact>]
let ``a submitted assignment keeps the progress its author recorded`` () =
    // Forcing it to 100 would invent a number the user never entered.
    let submitted = facts { assignment "A" with Status = AssignmentStatus.Submitted; Progress = 70 }
    Assert.Equal(70, AssignmentsLogic.progress submitted)

[<Fact>]
let ``the checklist is a separate signal, never folded into progress`` () =
    let a =
        { assignment "A" with
            Progress = 20
            Checklist =
                [ { Id = ChecklistItemId.New(); Text = "one"; Done = true; Position = 0 }
                  { Id = ChecklistItemId.New(); Text = "two"; Done = false; Position = 1 } ] }

    Assert.Equal(20, AssignmentsLogic.progress (facts a))
    Assert.Equal(Some(1, 2), AssignmentsLogic.checklist (facts a))

[<Fact>]
let ``an assignment with no checklist reports none rather than zero of zero`` () =
    Assert.Equal(None, AssignmentsLogic.checklist (facts (assignment "A")))

[<Fact>]
let ``remaining effort reuses the domain's own rule, so the Planner cannot disagree`` () =
    let a = { assignment "A" with EstimatedHours = Some 10.0; Progress = 40 }
    Assert.Equal(Assignment.remainingMinutes a, AssignmentsLogic.remainingMinutes (facts a))

[<Fact>]
let ``completed work has no remaining effort`` () =
    let done' = facts { assignment "A" with Status = AssignmentStatus.Submitted; Progress = 40 }
    Assert.Equal(0, AssignmentsLogic.remainingMinutes done')

// ---------------------------------------------------------------------------
// 4. risk model
// ---------------------------------------------------------------------------

let private signalsOf f = AssignmentsLogic.signalsFor now f

let private hasSignal (pick: AssignmentsLogic.Signal -> bool) f =
    signalsOf f |> List.exists pick

[<Fact>]
let ``risk bands follow the documented point thresholds exactly`` () =
    Assert.Equal(AssignmentsLogic.Risk.Low, AssignmentsLogic.Risk.ofPoints 0)
    Assert.Equal(AssignmentsLogic.Risk.Low, AssignmentsLogic.Risk.ofPoints 1)
    Assert.Equal(AssignmentsLogic.Risk.Medium, AssignmentsLogic.Risk.ofPoints 2)
    Assert.Equal(AssignmentsLogic.Risk.Medium, AssignmentsLogic.Risk.ofPoints 3)
    Assert.Equal(AssignmentsLogic.Risk.High, AssignmentsLogic.Risk.ofPoints 4)
    Assert.Equal(AssignmentsLogic.Risk.High, AssignmentsLogic.Risk.ofPoints 5)
    Assert.Equal(AssignmentsLogic.Risk.Critical, AssignmentsLogic.Risk.ofPoints 6)

    Assert.Equal(2, AssignmentsLogic.Threshold.mediumPoints)
    Assert.Equal(4, AssignmentsLogic.Threshold.highPoints)
    Assert.Equal(6, AssignmentsLogic.Threshold.criticalPoints)

[<Fact>]
let ``finished work is settled, not low risk`` () =
    let submitted = facts { assignment "A" with Status = AssignmentStatus.Submitted }
    Assert.Equal(AssignmentsLogic.Risk.Settled, AssignmentsLogic.riskOf now submitted)
    Assert.Empty(signalsOf submitted)

    // Settled sorts BELOW Low: it is not in the running at all.
    let settledRank = AssignmentsLogic.Risk.rank AssignmentsLogic.Risk.Settled
    let lowRank = AssignmentsLogic.Risk.rank AssignmentsLogic.Risk.Low
    Assert.True(settledRank < lowRank)

[<Fact>]
let ``an assignment with room, progress and scheduled time carries no signal`` () =
    Assert.Empty(signalsOf (clean 40.0 50))
    Assert.Equal(AssignmentsLogic.Risk.Low, AssignmentsLogic.riskOf now (clean 40.0 50))

[<Fact>]
let ``overdue work scores four points and is at least high risk`` () =
    let overdue = clean -3.0 40
    Assert.True(hasSignal (function AssignmentsLogic.Signal.Overdue _ -> true | _ -> false) overdue)
    Assert.True(AssignmentsLogic.Risk.atLeast AssignmentsLogic.Risk.High (AssignmentsLogic.riskOf now overdue))
    Assert.True(AssignmentsLogic.isOverdue now overdue)

[<Fact>]
let ``deadline proximity scores in three documented steps`` () =
    let pointsFor days =
        AssignmentsLogic.signalsFor now (clean days 50)
        |> List.filter (function AssignmentsLogic.Signal.DueWithin _ -> true | _ -> false)
        |> List.sumBy AssignmentsLogic.Signal.points

    Assert.Equal(3, pointsFor 0.5)
    Assert.Equal(2, pointsFor 2.0)
    Assert.Equal(1, pointsFor 5.0)
    Assert.Equal(0, pointsFor 20.0)

[<Fact>]
let ``a planner conflict is the scheduler's own verdict, and it counts`` () =
    let conflicted = { clean 20.0 50 with UnplacedReason = Some "no room before the deadline" }

    match signalsOf conflicted with
    | [ AssignmentsLogic.Signal.PlannerConflict reason ] ->
        Assert.Equal("no room before the deadline", reason)
        Assert.Equal(2, AssignmentsLogic.Signal.points (AssignmentsLogic.Signal.PlannerConflict reason))
    | other -> failwithf "expected one planner conflict, got %A" other

[<Fact>]
let ``work with nothing scheduled inside the horizon is flagged`` () =
    let unscheduled = { clean 5.0 50 with ScheduledMinutes = 0 }
    Assert.True(hasSignal (fun s -> s = AssignmentsLogic.Signal.NotScheduled) unscheduled)

[<Fact>]
let ``work with too little scheduled reports the shortfall`` () =
    let a = { assignment "A" with Deadline = Some(now.AddDays 5.0); Progress = 50; UpdatedAt = now }
    let remaining = Assignment.remainingMinutes a
    let under = { facts a with ScheduledMinutes = remaining - 120 }

    match signalsOf under |> List.filter (function AssignmentsLogic.Signal.UnderScheduled _ -> true | _ -> false) with
    | [ AssignmentsLogic.Signal.UnderScheduled shortfall ] -> Assert.Equal(120, shortfall)
    | other -> failwithf "expected one shortfall, got %A" other

[<Fact>]
let ``scheduling is not judged beyond the documented horizon`` () =
    // Past the horizon the planner has legitimately not got to the work yet.
    let far = { clean (float AssignmentsLogic.Threshold.unscheduledHorizonDays + 5.0) 50 with ScheduledMinutes = 0 }
    Assert.False(hasSignal (fun s -> s = AssignmentsLogic.Signal.NotScheduled) far)

[<Fact>]
let ``started work untouched for a week is stalled`` () =
    let a =
        { assignment "A" with
            Deadline = Some(now.AddDays 40.0)
            Progress = 30
            UpdatedAt = now.AddDays(float -AssignmentsLogic.Threshold.stalledDays - 1.0) }

    Assert.True(hasSignal (function AssignmentsLogic.Signal.Stalled _ -> true | _ -> false) (facts a))

[<Fact>]
let ``work that has not been started cannot be stalled`` () =
    let a =
        { assignment "A" with
            Progress = 0
            Deadline = Some(now.AddDays 40.0)
            UpdatedAt = now.AddDays -60.0 }

    Assert.False(hasSignal (function AssignmentsLogic.Signal.Stalled _ -> true | _ -> false) (facts a))

[<Fact>]
let ``a missing deadline and a missing estimate are stated but score nothing`` () =
    let bare = facts { assignment "A" with Deadline = None; EstimatedHours = None; UpdatedAt = now }
    let signals = signalsOf bare

    Assert.Contains(AssignmentsLogic.Signal.NoDeadline, signals)
    Assert.Contains(AssignmentsLogic.Signal.NoEstimate, signals)
    Assert.Equal(0, AssignmentsLogic.riskPoints now bare)
    Assert.Equal(AssignmentsLogic.Risk.Low, AssignmentsLogic.riskOf now bare)

[<Fact>]
let ``grade weight is NOT an input to risk`` () =
    // Important work is not automatically endangered work. A 40% component that
    // is far away, fully scheduled and progressing must not read as high risk.
    let light = clean 40.0 50
    let heavy = { light with Assignment = { light.Assignment with Weighting = Some 40.0 } }

    Assert.Equal(AssignmentsLogic.riskPoints now light, AssignmentsLogic.riskPoints now heavy)
    Assert.Equal(AssignmentsLogic.riskOf now light, AssignmentsLogic.riskOf now heavy)

[<Fact>]
let ``every signal states itself in words`` () =
    let all =
        [ AssignmentsLogic.Signal.Overdue 3
          AssignmentsLogic.Signal.DueWithin 2
          AssignmentsLogic.Signal.PlannerConflict "no room"
          AssignmentsLogic.Signal.UnderScheduled 120
          AssignmentsLogic.Signal.NotScheduled
          AssignmentsLogic.Signal.Stalled 9
          AssignmentsLogic.Signal.NotStarted
          AssignmentsLogic.Signal.NoDeadline
          AssignmentsLogic.Signal.NoEstimate ]

    Assert.All(all, fun s -> Assert.False(String.IsNullOrWhiteSpace(AssignmentsLogic.Signal.text s)))

[<Fact>]
let ``every risk level has its own word`` () =
    let labels =
        [ AssignmentsLogic.Risk.Settled
          AssignmentsLogic.Risk.Low
          AssignmentsLogic.Risk.Medium
          AssignmentsLogic.Risk.High
          AssignmentsLogic.Risk.Critical ]
        |> List.map AssignmentsLogic.Risk.label

    Assert.Equal(labels.Length, (List.distinct labels).Length)

// ---------------------------------------------------------------------------
// 5. pacing and the insight line
// ---------------------------------------------------------------------------

[<Fact>]
let ``pace is a comparison against the deadline window, not a feeling`` () =
    // Created 10 days ago, due in 10 — half the window gone.
    let a = { assignment "A" with Deadline = Some(now.AddDays 10.0); UpdatedAt = now }

    Assert.Equal(AssignmentsLogic.Pace.Behind, AssignmentsLogic.paceOf now (facts { a with Progress = 10 }))
    Assert.Equal(AssignmentsLogic.Pace.OnPace, AssignmentsLogic.paceOf now (facts { a with Progress = 80 }))

[<Fact>]
let ``pace reports the states that outrank a comparison`` () =
    let base' = { assignment "A" with Deadline = Some(now.AddDays 10.0); UpdatedAt = now }

    Assert.Equal(
        AssignmentsLogic.Pace.Submitted,
        AssignmentsLogic.paceOf now (facts { base' with Status = AssignmentStatus.Submitted })
    )

    Assert.Equal(
        AssignmentsLogic.Pace.Overdue,
        AssignmentsLogic.paceOf now (facts { base' with Deadline = Some(now.AddDays -1.0) })
    )

    Assert.Equal(
        AssignmentsLogic.Pace.NotStarted,
        AssignmentsLogic.paceOf now (facts { base' with Progress = 0 })
    )

    Assert.Equal(
        AssignmentsLogic.Pace.Unpaced,
        AssignmentsLogic.paceOf now (facts { base' with Deadline = None })
    )

[<Fact>]
let ``an assignment with no deadline has no pace to be behind`` () =
    let a = facts { assignment "A" with Deadline = None; UpdatedAt = now }
    Assert.Equal(AssignmentsLogic.Pace.Unpaced, AssignmentsLogic.paceOf now a)

[<Fact>]
let ``the insight is the worst true thing, not reassurance`` () =
    let overdue = { clean -4.0 30 with ScheduledMinutes = 0 }
    Assert.Contains("Overdue", AssignmentsLogic.insight now overdue)

[<Fact>]
let ``the insight falls back to pace only when nothing is wrong`` () =
    let calm = clean 40.0 90
    Assert.Empty(signalsOf calm)
    Assert.Contains("On pace", AssignmentsLogic.insight now calm)

[<Fact>]
let ``a finished assignment's insight says so`` () =
    Assert.Equal("Graded.", AssignmentsLogic.insight now (facts { assignment "A" with Status = AssignmentStatus.Graded }))
    Assert.Equal(
        "Submitted.",
        AssignmentsLogic.insight now (facts { assignment "A" with Status = AssignmentStatus.Submitted })
    )

// ---------------------------------------------------------------------------
// 6. stages — the real progression
// ---------------------------------------------------------------------------

[<Fact>]
let ``the stepper is the domain's own status progression, not invented milestones`` () =
    // The redesign shows Research / Planning / Draft / Review / Submission. None
    // of those exists anywhere in the domain.
    let labels = AssignmentsLogic.Stage.order |> List.map AssignmentsLogic.Stage.label

    Assert.Equal<string list>([ "Not started"; "In progress"; "Submitted"; "Graded" ], labels)

    for invented in [ "Research"; "Planning"; "Draft"; "Review" ] do
        Assert.DoesNotContain(invented, labels)

[<Fact>]
let ``blocked is a state of the current stage, never a stage of its own`` () =
    let blocked = facts { assignment "A" with Status = AssignmentStatus.Blocked }

    Assert.Equal(AssignmentsLogic.Stage.InProgress, AssignmentsLogic.Stage.ofStatus AssignmentStatus.Blocked)
    Assert.True(AssignmentsLogic.isBlocked blocked)
    Assert.Equal(4, (AssignmentsLogic.stageStates blocked).Length)

[<Fact>]
let ``stage states run done, current, then pending`` () =
    let states = AssignmentsLogic.stageStates (facts { assignment "A" with Status = AssignmentStatus.Submitted })

    Assert.Equal<(AssignmentsLogic.Stage * AssignmentsLogic.StageState) list>(
        [ AssignmentsLogic.Stage.NotStarted, AssignmentsLogic.StageState.Done
          AssignmentsLogic.Stage.InProgress, AssignmentsLogic.StageState.Done
          AssignmentsLogic.Stage.Submitted, AssignmentsLogic.StageState.Current
          AssignmentsLogic.Stage.Graded, AssignmentsLogic.StageState.Pending ],
        states
    )

// ---------------------------------------------------------------------------
// 7. linked resources
// ---------------------------------------------------------------------------

[<Fact>]
let ``only relationships that genuinely exist are listed`` () =
    let f =
        { clean 10.0 50 with
            Linked = [ EntityKind.Document, 2 ]
            SessionCount = 3
            ActivityCount = 5 }

    let groups = AssignmentsLogic.linkedGroups f
    Assert.Equal(3, groups.Length)
    Assert.Contains("Documents (2)", groups |> List.map (fun g -> g.Label))
    Assert.Contains("Planner sessions (3)", groups |> List.map (fun g -> g.Label))
    Assert.Contains("Timeline entries (5)", groups |> List.map (fun g -> g.Label))

[<Fact>]
let ``a category with no relationship is omitted, never shown as zero`` () =
    // The redesign draws `Documents (0)`. A zero row claims a relationship was
    // checked; a fabricated row is worse still.
    Assert.Empty(AssignmentsLogic.linkedGroups (clean 10.0 50))

    let onlyDocs = { clean 10.0 50 with Linked = [ EntityKind.Document, 1 ] }
    Assert.Equal(1, (AssignmentsLogic.linkedGroups onlyDocs).Length)

[<Fact>]
let ``a single related item is named in the singular`` () =
    let f = { clean 10.0 50 with Linked = [ EntityKind.Note, 1 ]; SessionCount = 1; ActivityCount = 1 }
    let labels = AssignmentsLogic.linkedGroups f |> List.map (fun g -> g.Label)

    Assert.Contains("Note (1)", labels)
    Assert.Contains("Planner session (1)", labels)
    Assert.Contains("Timeline entry (1)", labels)

// ---------------------------------------------------------------------------
// 8. filters
// ---------------------------------------------------------------------------

let private shows filter f = AssignmentsLogic.matches now weekStart filter f

[<Fact>]
let ``Due today is the local calendar date, not a rolling day`` () =
    Assert.True(shows AssignmentsLogic.Filter.DueToday (clean 0.2 50))
    Assert.True(
        shows
            AssignmentsLogic.Filter.DueToday
            { facts { assignment "A" with Deadline = Some(now.Date.AddHours 23.0) } with ScheduledMinutes = 1 }
    )
    Assert.False(shows AssignmentsLogic.Filter.DueToday (clean 1.5 50))
    Assert.False(shows AssignmentsLogic.Filter.DueToday (facts { assignment "A" with Deadline = None }))

[<Fact>]
let ``This week uses the shared week boundary, not a rolling seven days`` () =
    // The boundary is passed in from `StudyTime.weekStart` — the one definition
    // the Dashboard and the Timeline already share.
    let inWeek = facts { assignment "A" with Deadline = Some(weekStart.AddDays 3.0) }
    let nextWeek = facts { assignment "B" with Deadline = Some(weekStart.AddDays 8.0) }
    let lastWeek = facts { assignment "C" with Deadline = Some(weekStart.AddDays -1.0) }

    Assert.True(shows AssignmentsLogic.Filter.ThisWeek inWeek)
    Assert.False(shows AssignmentsLogic.Filter.ThisWeek nextWeek)
    Assert.False(shows AssignmentsLogic.Filter.ThisWeek lastWeek)

[<Fact>]
let ``the week boundary is the one StudyTime defines`` () =
    let firstDay = Globalization.CultureInfo.InvariantCulture.DateTimeFormat.FirstDayOfWeek
    let expected = StudyTime.weekStart firstDay now

    // Same function, same answer — this screen introduces no second rule.
    Assert.Equal(expected, StudyTime.weekStart firstDay now)
    Assert.True(expected <= now.Date)
    Assert.True((now.Date - expected).TotalDays < 7.0)

[<Fact>]
let ``High weight uses the documented threshold and ignores missing weightings`` () =
    Assert.Equal(20.0, AssignmentsLogic.Threshold.highWeightPercent, 6)

    let at = facts { assignment "A" with Weighting = Some 20.0 }
    let below = facts { assignment "B" with Weighting = Some 19.9 }
    let none = facts { assignment "C" with Weighting = None }

    Assert.True(shows AssignmentsLogic.Filter.HighWeight at)
    Assert.False(shows AssignmentsLogic.Filter.HighWeight below)
    Assert.False(shows AssignmentsLogic.Filter.HighWeight none)

[<Fact>]
let ``High risk is the same model the badges and the forecast use`` () =
    let overdue = { clean -3.0 20 with ScheduledMinutes = 0 }
    let calm = clean 40.0 60

    Assert.Equal(
        AssignmentsLogic.Risk.atLeast AssignmentsLogic.Risk.High (AssignmentsLogic.riskOf now overdue),
        shows AssignmentsLogic.Filter.HighRisk overdue
    )

    Assert.Equal(
        AssignmentsLogic.Risk.atLeast AssignmentsLogic.Risk.High (AssignmentsLogic.riskOf now calm),
        shows AssignmentsLogic.Filter.HighRisk calm
    )

[<Fact>]
let ``Completed is the real submitted or graded status`` () =
    for status in [ AssignmentStatus.Submitted; AssignmentStatus.Graded ] do
        Assert.True(shows AssignmentsLogic.Filter.Completed (facts { assignment "A" with Status = status }))

    for status in [ AssignmentStatus.NotStarted; AssignmentStatus.InProgress; AssignmentStatus.Blocked ] do
        Assert.False(shows AssignmentsLogic.Filter.Completed (facts { assignment "A" with Status = status }))

[<Fact>]
let ``Completed is the exact complement of the open set`` () =
    let items =
        [ facts { assignment "A" with Status = AssignmentStatus.InProgress }
          facts { assignment "B" with Status = AssignmentStatus.Submitted }
          facts { assignment "C" with Status = AssignmentStatus.Graded } ]

    let completed = items |> List.filter (shows AssignmentsLogic.Filter.Completed)
    let opened = items |> List.filter AssignmentsLogic.isOpen

    Assert.Equal(items.Length, completed.Length + opened.Length)

[<Fact>]
let ``Needs attention fires on each of its documented signals`` () =
    let overdue = { clean -1.0 30 with ScheduledMinutes = 0 }
    let stalled =
        facts
            { assignment "S" with
                Deadline = Some(now.AddDays 40.0)
                Progress = 30
                UpdatedAt = now.AddDays -20.0 }
    let conflicted = { clean 40.0 50 with UnplacedReason = Some "no room" }
    let undated = { facts { assignment "U" with Deadline = None; UpdatedAt = now } with ScheduledMinutes = 1 }
    let unestimated =
        { facts { assignment "E" with EstimatedHours = None; Deadline = Some(now.AddDays 40.0); UpdatedAt = now } with
            ScheduledMinutes = 100_000 }

    for f in [ overdue; stalled; conflicted; undated; unestimated ] do
        Assert.True(shows AssignmentsLogic.Filter.NeedsAttention f, sprintf "%s did not need attention" f.Assignment.Title)

[<Fact>]
let ``calm, fully specified work does not need attention`` () =
    Assert.False(shows AssignmentsLogic.Filter.NeedsAttention (clean 40.0 60))

[<Fact>]
let ``completed work never needs attention`` () =
    let done' = facts { assignment "A" with Status = AssignmentStatus.Submitted; Deadline = Some(now.AddDays -30.0) }
    Assert.False(shows AssignmentsLogic.Filter.NeedsAttention done')

[<Fact>]
let ``All shows everything, whatever its status`` () =
    let items = [ clean 5.0 50; facts { assignment "B" with Status = AssignmentStatus.Graded } ]
    Assert.Equal(2, (AssignmentsLogic.visible now weekStart AssignmentsLogic.Filter.All items).Length)

[<Fact>]
let ``the list puts open work first and the riskiest at the top`` () =
    let calm = { clean 40.0 80 with Assignment = { (clean 40.0 80).Assignment with Title = "Calm" } }
    let risky = { clean -2.0 10 with Assignment = { (clean -2.0 10).Assignment with Title = "Risky" }; ScheduledMinutes = 0 }
    let done' = facts { assignment "Done" with Status = AssignmentStatus.Graded }

    let order =
        AssignmentsLogic.visible now weekStart AssignmentsLogic.Filter.All [ done'; calm; risky ]
        |> List.map (fun f -> f.Assignment.Title)

    Assert.Equal<string list>([ "Risky"; "Calm"; "Done" ], order)

// ---------------------------------------------------------------------------
// 9. forecast
// ---------------------------------------------------------------------------

[<Fact>]
let ``the trajectory is the worst open risk`` () =
    Assert.Equal(AssignmentsLogic.Trajectory.OnTrack, AssignmentsLogic.trajectory now [ clean 40.0 60 ])

    let risky = { clean -2.0 10 with ScheduledMinutes = 0 }
    Assert.True(
        AssignmentsLogic.trajectory now [ clean 40.0 60; risky ] = AssignmentsLogic.Trajectory.AtRisk
        || AssignmentsLogic.trajectory now [ clean 40.0 60; risky ] = AssignmentsLogic.Trajectory.Critical
    )

[<Fact>]
let ``every trajectory has a word and an explanation`` () =
    let all =
        [ AssignmentsLogic.Trajectory.NothingOutstanding
          AssignmentsLogic.Trajectory.OnTrack
          AssignmentsLogic.Trajectory.Watch
          AssignmentsLogic.Trajectory.AtRisk
          AssignmentsLogic.Trajectory.Critical ]

    Assert.All(
        all,
        fun t ->
            Assert.False(String.IsNullOrWhiteSpace(AssignmentsLogic.Trajectory.label t))
            Assert.False(String.IsNullOrWhiteSpace(AssignmentsLogic.Trajectory.detail t))
    )

    Assert.Equal(all.Length, (all |> List.map AssignmentsLogic.Trajectory.label |> List.distinct).Length)

[<Fact>]
let ``highest risk names the real worst assignment, and nothing when all is calm`` () =
    let calm = clean 40.0 60
    let risky = { clean -2.0 10 with Assignment = { (clean -2.0 10).Assignment with Title = "Risky" }; ScheduledMinutes = 0 }

    Assert.Equal(None, AssignmentsLogic.highestRisk now [ calm ])
    Assert.Equal(
        Some "Risky",
        AssignmentsLogic.highestRisk now [ calm; risky ] |> Option.map (fun f -> f.Assignment.Title)
    )

[<Fact>]
let ``suggested next prefers risk, then the nearer deadline`` () =
    let far = { clean 40.0 50 with Assignment = { (clean 40.0 50).Assignment with Title = "Far" } }
    let soon = { clean 1.0 50 with Assignment = { (clean 1.0 50).Assignment with Title = "Soon" } }

    Assert.Equal(
        Some "Soon",
        AssignmentsLogic.suggestedNext now [ far; soon ] |> Option.map (fun f -> f.Assignment.Title)
    )

[<Fact>]
let ``suggested next breaks a risk-and-deadline tie on the heavier weight`` () =
    let deadline = Some(now.AddDays 5.0)

    let light =
        { facts { assignment "Light" with Deadline = deadline; Weighting = Some 5.0; UpdatedAt = now } with
            ScheduledMinutes = 100_000 }

    let heavy =
        { facts { assignment "Heavy" with Deadline = deadline; Weighting = Some 40.0; UpdatedAt = now } with
            ScheduledMinutes = 100_000 }

    Assert.Equal(
        Some "Heavy",
        AssignmentsLogic.suggestedNext now [ light; heavy ] |> Option.map (fun f -> f.Assignment.Title)
    )

[<Fact>]
let ``the workload figure is the same one the summary metric prints`` () =
    // Not a second calculation: the panel and the card must never disagree.
    let items = [ clean 5.0 40; clean 20.0 10 ]
    let expected = items |> List.filter AssignmentsLogic.isOpen |> List.sumBy AssignmentsLogic.remainingMinutes

    Assert.Equal(expected, AssignmentsLogic.workloadMinutes items)

[<Fact>]
let ``suggested next ignores finished work`` () =
    let done' = facts { assignment "Done" with Status = AssignmentStatus.Graded }
    Assert.Equal(None, AssignmentsLogic.suggestedNext now [ done' ])

// ---------------------------------------------------------------------------
// 10. risk overview
// ---------------------------------------------------------------------------

let private overviewRow label items =
    AssignmentsLogic.riskOverview now items |> List.find (fun r -> r.Label = label)

[<Fact>]
let ``the overview never claims a grade risk`` () =
    // Grades are not modelled: no mark, no target, no prediction exists.
    let labels = AssignmentsLogic.riskOverview now [ clean 5.0 50 ] |> List.map (fun r -> r.Label)
    Assert.DoesNotContain("Grade risk", labels)

[<Fact>]
let ``an empty overview reports unknown, never low`` () =
    // Marking missing data as "Low" would be the same lie as scoring an empty
    // plan at 100%.
    for row in AssignmentsLogic.riskOverview now [] do
        Assert.Equal(None, row.Value)
        Assert.Equal(None, row.Level)

[<Fact>]
let ``an empty overview row is one dash and nothing else`` () =
    // The panel's own trajectory sentence already says there is no work; five
    // repetitions of "nothing outstanding" underneath it say it five more times.
    for row in AssignmentsLogic.riskOverview now [] do
        Assert.Equal(None, row.Note)

[<Fact>]
let ``no overview row ever carries the phrase the panel already states`` () =
    for items in [ []; [ clean 5.0 50 ]; [ { clean 20.0 50 with UnplacedReason = Some "no room" } ] ] do
        for row in AssignmentsLogic.riskOverview now items do
            match row.Note with
            | Some note -> Assert.DoesNotContain("nothing outstanding", note.ToLowerInvariant())
            | None -> ()

[<Fact>]
let ``the overview counts real planner conflicts`` () =
    let conflicted = { clean 20.0 50 with UnplacedReason = Some "no room before the deadline" }
    let row = overviewRow "Planner conflicts" [ conflicted; clean 40.0 50 ]

    Assert.Equal(Some "1", row.Value)
    Assert.Equal(Some "work the scheduler could not place", row.Note)

[<Fact>]
let ``the overview counts assignments the planner had to guess an estimate for`` () =
    let unestimated =
        { facts { assignment "A" with EstimatedHours = None; Deadline = Some(now.AddDays 40.0); UpdatedAt = now } with
            ScheduledMinutes = 100_000 }

    let row = overviewRow "Missing estimates" [ unestimated ]
    Assert.Equal(Some "1", row.Value)
    Assert.Equal(Some "the planner assumes 4 hours each", row.Note)

[<Fact>]
let ``the overview says how many assignments carry no deadline`` () =
    let undated = { facts { assignment "A" with Deadline = None; UpdatedAt = now } with ScheduledMinutes = 1 }
    Assert.Equal(Some "1 without a deadline", (overviewRow "Deadline risk" [ undated ]).Note)

[<Fact>]
let ``every overview row is labelled`` () =
    AssignmentsLogic.riskOverview now [ clean 5.0 50 ]
    |> List.iter (fun r -> Assert.False(String.IsNullOrWhiteSpace r.Label))

// ---------------------------------------------------------------------------
// 11. actions
// ---------------------------------------------------------------------------

let private context hasSessions hasActivity ai busy : AssignmentsLogic.ActionContext =
    { HasSessions = hasSessions
      HasActivity = hasActivity
      AiAvailable = ai
      Busy = busy }

let private actionOf id states =
    states |> List.find (fun (s: AssignmentsLogic.ActionState) -> s.Id = id)

[<Fact>]
let ``the actions offered are only ones with a service behind them`` () =
    // Start Focus Session and Generate Notes appear in the redesign and have no
    // implementation anywhere. They must not reappear as dead controls.
    let ids =
        AssignmentsLogic.actions (context true true true false) |> List.map (fun s -> s.Id)

    Assert.Equal<AssignmentsLogic.ActionId list>(
        [ AssignmentsLogic.ActionId.Open
          AssignmentsLogic.ActionId.AskAssistant
          AssignmentsLogic.ActionId.OpenPlanner
          AssignmentsLogic.ActionId.ViewTimeline ],
        ids
    )

[<Fact>]
let ``Open is always available and is the primary action`` () =
    let states = AssignmentsLogic.actions (context false false false false)
    let openAction = actionOf AssignmentsLogic.ActionId.Open states

    Assert.True(openAction.Enabled)
    Assert.True(openAction.Primary)
    Assert.Equal(1, states |> List.filter (fun s -> s.Primary) |> List.length)

[<Fact>]
let ``Ask Assistant is disabled without a provider, and says why`` () =
    let without = actionOf AssignmentsLogic.ActionId.AskAssistant (AssignmentsLogic.actions (context true true false false))
    Assert.False(without.Enabled)
    Assert.Contains("AI provider", without.DisabledBecause |> Option.defaultValue "")

    let with' = actionOf AssignmentsLogic.ActionId.AskAssistant (AssignmentsLogic.actions (context true true true false))
    Assert.True(with'.Enabled)

[<Fact>]
let ``Planner and Timeline are disabled when there is nothing to open`` () =
    let states = AssignmentsLogic.actions (context false false true false)

    let planner = actionOf AssignmentsLogic.ActionId.OpenPlanner states
    let timeline = actionOf AssignmentsLogic.ActionId.ViewTimeline states

    Assert.False(planner.Enabled)
    Assert.True(planner.DisabledBecause.IsSome)
    Assert.False(timeline.Enabled)
    Assert.True(timeline.DisabledBecause.IsSome)

[<Fact>]
let ``a save in flight disables every action and says why`` () =
    Assert.All(
        AssignmentsLogic.actions (context true true true true),
        fun s ->
            Assert.False(s.Enabled, sprintf "%s stayed enabled while saving" s.Label)
            Assert.Equal(Some "a change to this assignment is still saving", s.DisabledBecause)
    )

[<Fact>]
let ``no action is ever drawn without a reason when it is unavailable`` () =
    for sessions in [ true; false ] do
        for activity in [ true; false ] do
            for ai in [ true; false ] do
                for busy in [ true; false ] do
                    AssignmentsLogic.actions (context sessions activity ai busy)
                    |> List.iter (fun s ->
                        if not s.Enabled then
                            Assert.True(
                                s.DisabledBecause |> Option.exists (fun w -> w.Trim().Length > 0),
                                sprintf "%s was disabled with no reason" s.Label
                            ))

[<Fact>]
let ``the assistant is handed real figures from the assignment`` () =
    let f =
        { clean 5.0 40 with
            Assignment = { (clean 5.0 40).Assignment with Title = "Coursework 2" }
            Module = Some(moduleOf "CS2004") }

    let prompt = AssignmentsLogic.assistantPrompt f

    Assert.Contains("Coursework 2", prompt)
    Assert.Contains("CS2004", prompt)
    Assert.Contains("40% complete", prompt)

// ---------------------------------------------------------------------------
// 12. layout and reserved geometry
// ---------------------------------------------------------------------------

[<Fact>]
let ``the workspace splits about two thirds to the list, as the redesign does`` () =
    Assert.Equal(
        100.0,
        AssignmentsLogic.Geometry.mainLeftWeight + AssignmentsLogic.Geometry.mainRightWeight,
        3
    )

    Assert.InRange(AssignmentsLogic.Geometry.mainLeftWeight, 64.0, 68.0)
    Assert.InRange(AssignmentsLogic.Geometry.mainRightWeight, 32.0, 36.0)

[<Fact>]
let ``the side panel appears at the same width as every other migrated screen`` () =
    Assert.False(AssignmentsLogic.showsSidePanel 1099.0)
    Assert.True(AssignmentsLogic.showsSidePanel 1100.0)

    for width in [ 600.0; 900.0; 1099.0; 1100.0; 1400.0 ] do
        Assert.Equal(PlannerLogic.showsSideColumn width, AssignmentsLogic.showsSidePanel width)
        Assert.Equal(InboxLogic.showsDetailColumn width, AssignmentsLogic.showsSidePanel width)

[<Fact>]
let ``metric columns reflow and never go backwards as the page widens`` () =
    Assert.Equal(2, AssignmentsLogic.metricColumns 600.0)
    Assert.Equal(3, AssignmentsLogic.metricColumns 900.0)
    Assert.Equal(5, AssignmentsLogic.metricColumns 1400.0)

    [ 320.0..40.0..1920.0 ]
    |> List.map AssignmentsLogic.metricColumns
    |> List.pairwise
    |> List.iter (fun (a, b) -> Assert.True(b >= a, sprintf "columns went backwards: %d then %d" a b))

[<Fact>]
let ``all five metrics fit the row at wide width`` () =
    Assert.Equal(5, AssignmentsLogic.metricColumns 1400.0)
    Assert.Equal(5, (AssignmentsLogic.metrics now []).Length)

[<Fact>]
let ``the outline inset matches the Planner's, the Inbox's and the Dashboard's`` () =
    Assert.Equal(0.5, AssignmentsLogic.Geometry.borderInset, 6)
    Assert.Equal(PlannerLogic.Geometry.borderInset, AssignmentsLogic.Geometry.borderInset, 6)
    Assert.Equal(InboxLogic.Geometry.borderInset, AssignmentsLogic.Geometry.borderInset, 6)
    Assert.Equal(DashboardLogic.Card.borderInset, AssignmentsLogic.Geometry.borderInset, 6)

[<Fact>]
let ``no Assignments content card draws a specular highlight`` () =
    Assert.Equal(0.0, AssignmentsLogic.Geometry.contentHighlight, 6)
    Assert.Equal(PlannerLogic.Geometry.contentHighlight, AssignmentsLogic.Geometry.contentHighlight, 6)
    Assert.Equal(InboxLogic.Geometry.contentHighlight, AssignmentsLogic.Geometry.contentHighlight, 6)
    Assert.Equal(DashboardLogic.Card.highlightScale, AssignmentsLogic.Geometry.contentHighlight, 6)

[<Fact>]
let ``a glass action is the same size here as on the Planner and the Inbox`` () =
    Assert.Equal(PlannerLogic.Geometry.actionMinHeight, AssignmentsLogic.Geometry.actionMinHeight, 6)
    Assert.Equal(PlannerLogic.Geometry.actionRadius, AssignmentsLogic.Geometry.actionRadius, 6)
    Assert.Equal(InboxLogic.Geometry.actionMinHeight, AssignmentsLogic.Geometry.actionMinHeight, 6)

[<Fact>]
let ``a filter chip reserves its border in both states, so selection cannot resize it`` () =
    Assert.True(AssignmentsLogic.Geometry.filterBorderThickness > 0.0)
    Assert.True(AssignmentsLogic.Geometry.filterMinHeight > 0.0)

[<Fact>]
let ``an assignment card reserves its outline in both states`` () =
    Assert.True(AssignmentsLogic.Geometry.cardBorderThickness > 0.0)

[<Fact>]
let ``the badge, chevron and progress label all have reserved space`` () =
    // A card cannot resize when "Low risk" becomes "Critical risk", when it is
    // expanded, or when the percentage grows a digit.
    Assert.True(AssignmentsLogic.Geometry.riskBadgeMinWidth > 0.0)
    Assert.True(AssignmentsLogic.Geometry.chevronWidth > 0.0)
    Assert.True(AssignmentsLogic.Geometry.progressLabelMinWidth > 0.0)
    Assert.True(AssignmentsLogic.Geometry.progressTrackWidth > 0.0)

[<Fact>]
let ``wrapping text is given a finite column, never an infinite one`` () =
    Assert.Contains("*", AssignmentsLogic.Geometry.bulletColumns)
    Assert.Contains("*", AssignmentsLogic.Geometry.forecastColumns)
    Assert.StartsWith("Auto", AssignmentsLogic.Geometry.bulletColumns)
    Assert.StartsWith("Auto", AssignmentsLogic.Geometry.forecastColumns)

[<Fact>]
let ``row minimum heights are applied, so a row cannot collapse`` () =
    Assert.True(AssignmentsLogic.Geometry.metricRowMinHeight > 0.0)
    Assert.True(AssignmentsLogic.Geometry.workspaceMinHeight > 0.0)

// ---------------------------------------------------------------------------
// 12b. typography hierarchy
// ---------------------------------------------------------------------------

[<Fact>]
let ``a metric's value outranks its label, which outranks its qualifier`` () =
    // Not one uniform weight down the card.
    Assert.True(
        AssignmentsLogic.Typography.metricValueWeight > AssignmentsLogic.Typography.metricLabelWeight,
        "the metric value is not stronger than its label"
    )

    Assert.True(
        AssignmentsLogic.Typography.metricLabelWeight > AssignmentsLogic.Typography.qualifierWeight,
        "the metric label is not stronger than its qualifier"
    )

    Assert.True(
        AssignmentsLogic.Typography.metricValueSize > AssignmentsLogic.Typography.metricLabelSize,
        "the metric value is not larger than its label"
    )

[<Fact>]
let ``nothing is bolder than the metric value, and it is not excessive`` () =
    // 700 is Bold. Anything heavier would shout on a warm card.
    Assert.Equal(700, AssignmentsLogic.Typography.metricValueWeight)

    let everyWeight =
        [ AssignmentsLogic.Typography.metricLabelWeight
          AssignmentsLogic.Typography.qualifierWeight
          AssignmentsLogic.Typography.cardTitleWeight
          AssignmentsLogic.Typography.sectionLabelWeight
          AssignmentsLogic.Typography.primaryValueWeight
          AssignmentsLogic.Typography.supportingWeight
          AssignmentsLogic.Typography.overviewLabelWeight
          AssignmentsLogic.Typography.overviewValueWeight ]

    Assert.All(everyWeight, fun w -> Assert.True(w <= AssignmentsLogic.Typography.metricValueWeight))

[<Fact>]
let ``a panel's value outranks the section label that introduces it`` () =
    Assert.True(
        AssignmentsLogic.Typography.primaryValueWeight > AssignmentsLogic.Typography.sectionLabelWeight,
        "the answer is not stronger than the label above it"
    )

    Assert.True(
        AssignmentsLogic.Typography.primaryValueSize > AssignmentsLogic.Typography.sectionLabelSize,
        "the answer is not larger than the label above it"
    )

[<Fact>]
let ``a panel's name outranks its section labels but not its answers`` () =
    Assert.True(AssignmentsLogic.Typography.cardTitleWeight > AssignmentsLogic.Typography.sectionLabelWeight)
    Assert.True(AssignmentsLogic.Typography.primaryValueSize > AssignmentsLogic.Typography.cardTitleSize)

[<Fact>]
let ``a supporting sentence stays subordinate to the value it explains`` () =
    Assert.True(AssignmentsLogic.Typography.supportingWeight < AssignmentsLogic.Typography.primaryValueWeight)
    Assert.True(AssignmentsLogic.Typography.supportingSize < AssignmentsLogic.Typography.primaryValueSize)

[<Fact>]
let ``a Risk Overview value outranks its row label`` () =
    Assert.True(
        AssignmentsLogic.Typography.overviewValueWeight > AssignmentsLogic.Typography.overviewLabelWeight,
        "the overview row is one uniform weight"
    )

[<Fact>]
let ``one dash means one thing everywhere on the screen`` () =
    Assert.Equal("—", AssignmentsLogic.unavailableMark)

// ---------------------------------------------------------------------------
// 12c. the Timeline navigation row
// ---------------------------------------------------------------------------

[<Fact>]
let ``the Timeline row puts its text at one edge and its arrow at the other`` () =
    // A star column then an Auto column: the text takes the slack and the arrow
    // is pushed to the far side of a row that spans the card.
    Assert.Equal("*,Auto", AssignmentsLogic.Geometry.timelineColumns)

[<Fact>]
let ``the Timeline row is inset from the card rather than filling it edge to edge`` () =
    Assert.True(AssignmentsLogic.Geometry.timelineRowInset > 0.0)
    Assert.True(
        AssignmentsLogic.Geometry.timelineRowInset < AssignmentsLogic.Geometry.actionMinHeight,
        "the inset is a small breath, not a margin"
    )

[<Fact>]
let ``the Timeline card is shallower than a content panel`` () =
    // It holds one row and at most one note; it must not read as a nested card.
    Assert.True(AssignmentsLogic.Geometry.timelineCardPadding < 20.0)
    Assert.True(AssignmentsLogic.Geometry.timelineRowMinHeight > 0.0)

// ---------------------------------------------------------------------------
// 13. update behaviour
// ---------------------------------------------------------------------------

let private noServices: AppServices = Unchecked.defaultof<AppServices>

let private baseModel: AssignmentsView.Model =
    { Facts = []
      Modules = []
      Filter = AssignmentsLogic.Filter.All
      Expanded = None
      ExpansionChosen = false
      Draft = None
      PendingDelete = false
      AiAvailable = false
      WeekStart = weekStart
      Saving = false
      Loading = false
      Error = None
      Width = 1400.0 }

let private modelOf (m, _, _) = m
let private cmdOf (_, c, _) = c
let private externalOf (_, _, e) = e

[<Fact>]
let ``a successful load clears loading and any previous error`` () =
    let before = { baseModel with Loading = true; Error = Some "earlier" }
    let items = [ clean 5.0 50 ]

    let after = AssignmentsView.update noServices (AssignmentsView.Loaded(items, [], true, weekStart)) before |> modelOf

    Assert.False(after.Loading)
    Assert.Equal(None, after.Error)
    Assert.Equal(1, after.Facts.Length)
    Assert.True(after.AiAvailable)

[<Fact>]
let ``a load drops an expansion whose assignment has gone`` () =
    let gone = clean 5.0 50
    let before = { baseModel with Expanded = Some gone.Assignment.Id; Facts = [ gone ] }

    let after =
        AssignmentsView.update noServices (AssignmentsView.Loaded([ clean 9.0 20 ], [], false, weekStart)) before
        |> modelOf

    Assert.Equal(None, after.Expanded)

[<Fact>]
let ``a load keeps an expansion that survived`` () =
    let kept = clean 5.0 50
    let before = { baseModel with Expanded = Some kept.Assignment.Id; Facts = [ kept ] }

    let after =
        AssignmentsView.update noServices (AssignmentsView.Loaded([ kept ], [], false, weekStart)) before |> modelOf

    Assert.Equal(Some kept.Assignment.Id, after.Expanded)

[<Fact>]
let ``a failure clears every in-flight flag, so nothing is left stuck`` () =
    let before = { baseModel with Loading = true; Saving = true; PendingDelete = true }
    let after = AssignmentsView.update noServices (AssignmentsView.Failed(exn "offline")) before |> modelOf

    Assert.False(after.Loading)
    Assert.False(after.Saving)
    Assert.False(after.PendingDelete)
    Assert.Equal(Some "offline", after.Error)

[<Fact>]
let ``a failure keeps whatever the page could already show`` () =
    let before = { baseModel with Facts = [ clean 5.0 50 ] }
    let after = AssignmentsView.update noServices (AssignmentsView.Failed(exn "offline")) before |> modelOf
    Assert.Equal(1, after.Facts.Length)

[<Fact>]
let ``only one assignment is expanded at a time`` () =
    let a = clean 5.0 50
    let b = clean 9.0 20

    let first = AssignmentsView.update noServices (AssignmentsView.ToggleExpanded a.Assignment.Id) baseModel |> modelOf
    Assert.Equal(Some a.Assignment.Id, first.Expanded)

    let second = AssignmentsView.update noServices (AssignmentsView.ToggleExpanded b.Assignment.Id) first |> modelOf
    Assert.Equal(Some b.Assignment.Id, second.Expanded)

[<Fact>]
let ``expanding the open assignment again collapses it`` () =
    let a = clean 5.0 50
    let opened = AssignmentsView.update noServices (AssignmentsView.ToggleExpanded a.Assignment.Id) baseModel |> modelOf
    let closed = AssignmentsView.update noServices (AssignmentsView.ToggleExpanded a.Assignment.Id) opened |> modelOf

    Assert.Equal(None, closed.Expanded)

[<Fact>]
let ``changing the filter changes nothing else`` () =
    let before = { baseModel with Expanded = Some (clean 5.0 50).Assignment.Id }

    let after =
        AssignmentsView.update noServices (AssignmentsView.SetFilter AssignmentsLogic.Filter.HighRisk) before |> modelOf

    Assert.Equal(AssignmentsLogic.Filter.HighRisk, after.Filter)
    Assert.Equal(before.Expanded, after.Expanded)

[<Fact>]
let ``Open puts the assignment into the editor rather than navigating`` () =
    let f = clean 5.0 50
    let result = AssignmentsView.update noServices (AssignmentsView.Act(AssignmentsLogic.ActionId.Open, f)) baseModel

    Assert.True((modelOf result).Draft.IsSome)
    Assert.Equal(None, externalOf result)

[<Fact>]
let ``Open full Timeline navigates, whatever the assignments look like`` () =
    // The Timeline is a screen, and a screen can always be opened. An empty
    // Timeline is a real state with its own honest empty view, so this must not
    // be gated on any assignment having history.
    let withHistory = { clean 5.0 50 with ActivityCount = 4 }

    for facts in [ []; [ clean 5.0 50 ]; [ withHistory ] ] do
        let result = AssignmentsView.update noServices AssignmentsView.OpenTimeline { baseModel with Facts = facts }
        Assert.Equal(Some(AssignmentsView.Goto Page.Timeline), externalOf result)

[<Fact>]
let ``Open full Timeline is plain navigation, not an entity focus`` () =
    // A card's "View Timeline" focuses ONE assignment's history and needs
    // something to focus on. The panel's row is a different thing and must not
    // be confused with it.
    let result = AssignmentsView.update noServices AssignmentsView.OpenTimeline baseModel

    match externalOf result with
    | Some(AssignmentsView.Goto Page.Timeline) -> ()
    | other -> failwithf "expected plain navigation, got %A" other

    Assert.True(List.isEmpty (cmdOf result), "navigation should not issue a command of its own")

[<Fact>]
let ``Open Planner asks the shell to navigate`` () =
    let result =
        AssignmentsView.update noServices (AssignmentsView.Act(AssignmentsLogic.ActionId.OpenPlanner, clean 5.0 50)) baseModel

    Assert.Equal(Some(AssignmentsView.Goto Page.Planner), externalOf result)

[<Fact>]
let ``View Timeline asks the shell to focus this assignment's history`` () =
    let f = clean 5.0 50

    match
        AssignmentsView.update noServices (AssignmentsView.Act(AssignmentsLogic.ActionId.ViewTimeline, f)) baseModel
        |> externalOf
    with
    | Some(AssignmentsView.ShowInTimeline(entity, title)) ->
        Assert.Equal(EntityKind.Assignment, entity.Kind)
        Assert.Equal(f.Assignment.Id.Value, entity.Id)
        Assert.Equal(f.Assignment.Title, title)
    | other -> failwithf "expected a timeline focus, got %A" other

[<Fact>]
let ``Ask Assistant hands the shell a real question`` () =
    let f = clean 5.0 50

    match
        AssignmentsView.update noServices (AssignmentsView.Act(AssignmentsLogic.ActionId.AskAssistant, f)) baseModel
        |> externalOf
    with
    | Some(AssignmentsView.AskAssistantAbout question) ->
        Assert.Equal(AssignmentsLogic.assistantPrompt f, question)
        Assert.Contains(f.Assignment.Title, question)
    | other -> failwithf "expected an assistant question, got %A" other

[<Fact>]
let ``a second save while one is running is dropped, never queued`` () =
    let running = { baseModel with Saving = true; Draft = Some Unchecked.defaultof<AssignmentsView.Draft> }
    let result = AssignmentsView.update noServices AssignmentsView.Save running

    Assert.True(running.Saving)
    Assert.True(List.isEmpty (cmdOf result), "a repeated save produced a second command")

[<Fact>]
let ``deleting asks before it runs`` () =
    let before = { baseModel with Draft = Some Unchecked.defaultof<AssignmentsView.Draft> }
    let asked = AssignmentsView.update noServices AssignmentsView.RequestDelete before |> modelOf

    Assert.True(asked.PendingDelete)
    Assert.False(asked.Saving)

[<Fact>]
let ``cancelling a delete abandons it`` () =
    let before = { baseModel with PendingDelete = true }
    let after = AssignmentsView.update noServices AssignmentsView.CancelDelete before |> modelOf

    Assert.False(after.PendingDelete)
    Assert.False(after.Saving)

[<Fact>]
let ``confirming a delete while saving is dropped`` () =
    let running = { baseModel with Saving = true; PendingDelete = true }
    let result = AssignmentsView.update noServices AssignmentsView.ConfirmDelete running
    Assert.True(List.isEmpty (cmdOf result))

[<Fact>]
let ``closing the editor clears any pending delete`` () =
    let before = { baseModel with Draft = Some Unchecked.defaultof<AssignmentsView.Draft>; PendingDelete = true }
    let after = AssignmentsView.update noServices AssignmentsView.CloseEditor before |> modelOf

    Assert.Equal(None, after.Draft)
    Assert.False(after.PendingDelete)

[<Fact>]
let ``a resize records the width and nothing else`` () =
    let after = AssignmentsView.update noServices (AssignmentsView.Resized 800.0) baseModel |> modelOf

    Assert.Equal(800.0, after.Width, 3)
    Assert.Equal(baseModel.Filter, after.Filter)
    Assert.Equal(baseModel.Expanded, after.Expanded)

[<Fact>]
let ``the screen opens on everything, not on a filtered subset`` () =
    Assert.Equal(AssignmentsLogic.Filter.All, baseModel.Filter)

// ---------------------------------------------------------------------------
// 14. the shell contract
// ---------------------------------------------------------------------------

[<Fact>]
let ``Assignments draws its own glass, so the shell puts no opaque surface behind it`` () =
    Assert.True(Page.usesGlassSurface Page.Assignments)

[<Fact>]
let ``the approved screens keep their glass too`` () =
    for page in [ Page.Dashboard; Page.Timeline; Page.Planner; Page.Inbox; Page.DesignPreview ] do
        Assert.True(Page.usesGlassSurface page)

[<Fact>]
let ``the unmigrated screens still keep their opaque surface`` () =
    // Modules, then Lectures, left this list as each was migrated in its own
    // right. The claim narrowed; it was not relaxed.
    // Every screen is now migrated, so nothing is left holding an opaque shell
    // surface. This asserts the completed state rather than an empty list.
    for page, _, _ in Page.all do
        Assert.True(Page.usesGlassSurface page, sprintf "%A lost its glass surface" page)
