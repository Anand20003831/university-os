module UniversityOS.Tests.PlannerReasoningTests

open System
open Xunit
open UniversityOS.Domain
open UniversityOS.Domain.Planning
open UniversityOS.App
open UniversityOS.App.Features
open UniversityOS.Tests.AuditHarness

/// **Two Planner defects, both about saying things properly.**
///
/// The reasoning under a study block read `OVERDUE · 5.5h remaining · Critical
/// priority · worth 35%` — four fragments naming quantities without saying what
/// they imply, and one of them repeating a badge already on the same row.
///
/// And the full-day view was a bespoke modal with its own scrim, its own
/// Escape, its own ✕ and a scrim that swallowed every press, so clicking
/// outside it did nothing at all.
///
/// **What these tests cannot tell you.** Whether the reasoning now reads well,
/// or whether the floating window looks right. That is the reviewer's
/// judgement in the running window.

let private now = DateTime(2026, 3, 4, 9, 30, 0)

let private prefs =
    { WorkdayStart = TimeSpan.FromHours 9.0
      WorkdayEnd = TimeSpan.FromHours 21.0
      SessionMinutes = 50
      BreakMinutes = 10
      MaxDailyStudyMinutes = 360
      DeadlineBufferHours = 12.0
      HorizonDays = 7 }

let private task (title: string) (daysOut: float option) (minutes: int) (priority: Priority) (weight: float option) =
    { AssignmentId = AssignmentId(Guid.NewGuid())
      Title = title
      ModuleCode = Some "COMP3811"
      Deadline = daysOut |> Option.map (fun d -> now.Date.AddDays(d).AddHours 17.0)
      RemainingMinutes = minutes
      Priority = priority
      Weighting = weight 
      Milestones = [] }

let private planWith (tasks: PlannableTask list) =
    Planning.plan
        { Now = now
          Tasks = tasks
          Busy = []
          Prefs = prefs }

let private reasonsOf (tasks: PlannableTask list) =
    (planWith tasks).Blocks |> List.map (fun b -> b.Reason)

// ---------------------------------------------------------------------------
// Reasoning
// ---------------------------------------------------------------------------

/// **Priority is a badge on the title row.** Repeating it in the reasoning said
/// the same thing twice in one row and pushed the parts that were *not*
/// duplicated out of view.
[<Fact>]
let ``reasoning never repeats the priority badge`` () =
    for priority in Priority.all do
        let reasons = reasonsOf [ task "Coursework" (Some 3.0) 200 priority (Some 35.0) ]
        Assert.NotEmpty reasons

        for reason in reasons do
            Assert.DoesNotContain("priority", reason, StringComparison.OrdinalIgnoreCase)
            Assert.DoesNotContain(Priority.label priority, reason, StringComparison.OrdinalIgnoreCase)

/// The exact fragments the review named, gone.
[<Fact>]
let ``the terse fragments the review named are gone`` () =
    let reasons =
        reasonsOf [ task "Overdue work" (Some -2.0) 330 Priority.Critical (Some 35.0) ]

    Assert.NotEmpty reasons

    for reason in reasons do
        Assert.DoesNotContain("OVERDUE", reason, StringComparison.Ordinal)
        Assert.DoesNotContain("h remaining", reason, StringComparison.Ordinal)
        Assert.DoesNotContain("worth 35%", reason, StringComparison.Ordinal)

/// Replaced with statements, not removed. A block with no reasoning at all
/// would be worse than a terse one.
[<Fact>]
let ``reasoning explains the deadline, the remaining work and the weighting`` () =
    let reason =
        reasonsOf [ task "Coursework" (Some 3.0) 330 Priority.High (Some 35.0) ] |> List.head

    // Four, not three: the deadline is 3 days and 7½ hours away and the day
    // count rounds **up**. That is the safe direction — never tell somebody
    // they have longer than they do — and it is unchanged by this rewrite.
    Assert.Contains("Deadline in 4 days", reason)
    Assert.Contains("5.5 hours of work still estimated", reason)
    Assert.Contains("Worth 35% of the module", reason)

[<Fact>]
let ``a passed deadline is stated rather than shouted`` () =
    let reason = reasonsOf [ task "Late" (Some -2.0) 200 Priority.Critical None ] |> List.head
    Assert.Contains("Already past its deadline", reason)

/// **An absent fact is omitted, not softened into a guess.** A task with no
/// deadline and no weighting must not acquire sentences about either.
[<Fact>]
let ``facts the task does not carry are left out`` () =
    let reason = reasonsOf [ task "Open-ended" None 200 Priority.Low None ] |> List.head

    Assert.Contains("No deadline set", reason)
    Assert.DoesNotContain("Worth", reason, StringComparison.Ordinal)
    Assert.DoesNotContain("Deadline in", reason, StringComparison.Ordinal)

/// The view splits the reason on " · " into chips, so the separator has to
/// survive the rewrite or every block shows one long run-on line.
[<Fact>]
let ``the reasoning still splits into chips for the row`` () =
    let session: PlannedSession =
        { Id = SessionId(Guid.NewGuid())
          AssignmentId = None
          MilestoneId = None
          Title = "Coursework"
          ModuleCode = Some "COMP3811"
          StartsAt = now
          EndsAt = now.AddMinutes 50.0
          Reason = reasonsOf [ task "Coursework" (Some 3.0) 330 Priority.High (Some 35.0) ] |> List.head
          Completed = false
          CreatedAt = now }

    let chips = PlannerLogic.reasonChips session

    Assert.Equal(3, chips.Length)

    for chip in chips do
        Assert.False(chip.StartsWith " ")
        Assert.False(chip.EndsWith " ")

/// Small remainders read in minutes rather than as "0.3 hours".
[<Fact>]
let ``a short remainder is stated in minutes`` () =
    let reason = reasonsOf [ task "Nearly done" (Some 1.0) 25 Priority.Low None ] |> List.head
    Assert.Contains("minutes of work still estimated", reason)

// ---------------------------------------------------------------------------
// The full-day view, now the shared window
// ---------------------------------------------------------------------------

/// **The view reads the real clock, not the frozen instant above.**
///
/// `Planner.view` calls `DateTime.Now` per frame, so a session dated to the
/// frozen instant is not "today" to it — it lands in the "Later in this plan"
/// section instead. The first version of these tests missed that and passed
/// while the window was empty, because the block titles were on the page
/// anyway. Mutation caught it: truncating the full-day list failed nothing.
let private viewToday = DateTime.Now.Date

let private sessionAt (index: int) (hour: float) : PlannedSession =
    { Id = SessionId(Guid(sprintf "00000000-0000-0000-0000-%012d" (500 + index)))
      AssignmentId = None
      MilestoneId = None
      Title = sprintf "Block %d" index
      ModuleCode = Some "COMP3811"
      StartsAt = viewToday.AddHours hour
      EndsAt = viewToday.AddHours(hour + 0.8)
      Reason = "Deadline in 3 days"
      Completed = false
      CreatedAt = viewToday }

let private plannerOpen (count: int) =
    let model, _ = Planner.init AuditFixtures.noServices

    { model with
        Sessions = [ for i in 1..count -> sessionAt i (8.0 + float i * 0.9) ]
        Loading = false
        Width = 1400.0
        AllTodayOpen = true }

let private render (model: Planner.Model) = Planner.view AuditFixtures.tokens model ignore

/// **The scrim must be dismissible.** The bespoke panel swallowed every press
/// with `Handled <- true`, so clicking outside did nothing — which is the
/// requirement it failed.
[<Fact>]
let ``the full-day view dismisses on an outside press`` () =
    let tree = render (plannerOpen 9)

    let scrims =
        tree
        |> all
        |> List.filter (fun v -> typeName v = "Border" && hasHandler "PointerPressed" v && accessibleName v = None)

    Assert.NotEmpty scrims

/// The window is named, so assistive technology and the heading agree.
[<Fact>]
let ``the full-day view is a named window`` () =
    let tree = render (plannerOpen 9)

    Assert.Contains(
        "Today's study sessions",
        everyString tree |> String.concat "\n"
    )

    Assert.Contains(
        all tree,
        fun v -> accessibleName v = Some "Today's study sessions"
    )

/// **Every session, not a capped preview.** The card is the summary; this is the
/// full list, and a full list that also truncated would leave the day
/// unreachable altogether.
[<Fact>]
let ``the full-day view shows every session today, past the card's cap`` () =
    let count = PlannerLogic.todayPreviewLimit + 5

    // Scoped to the window's own subtree. Asserting over the whole page would
    // pass on strings the *card* rendered — which is exactly how the first
    // version of this test passed while the window was truncated.
    let windowText =
        render (plannerOpen count)
        |> allWithAncestors
        |> List.filter (fun (_, ancestors) ->
            ancestors |> List.exists (fun a -> accessibleName a = Some "Today's study sessions"))
        |> List.choose (fun (v, _) -> visibleText v)
        |> String.concat "\n"

    for i in 1..count do
        Assert.Contains(sprintf "Block %d" i, windowText)

/// The card stays a summary: it is capped, and the cap is inside the range the
/// review asked for.
[<Fact>]
let ``the card's preview stays between five and ten items`` () =
    Assert.InRange(PlannerLogic.todayPreviewLimit, 5, 10)

    let sessions = [ for i in 1..20 -> sessionAt i (8.0 + float i * 0.5) ]

    Assert.Equal(
        PlannerLogic.todayPreviewLimit,
        (PlannerLogic.todayPreview viewToday sessions).Length
    )

    Assert.Equal(20 - PlannerLogic.todayPreviewLimit, PlannerLogic.todayHiddenCount viewToday sessions)

/// A dedicated full list gets an ordinary scrollbar — unlike the Dashboard's
/// summary, where the chrome is suppressed on purpose.
[<Fact>]
let ``the full-day list scrolls with its scrollbar visible`` () =
    let tree = render (plannerOpen 14)

    let inWindow =
        tree
        |> allWithAncestors
        |> List.filter (fun (v, ancestors) ->
            typeName v = "ScrollViewer"
            && ancestors |> List.exists (fun a -> accessibleName a = Some "Today's study sessions"))
        |> List.map fst

    Assert.NotEmpty inWindow

    for viewer in inWindow do
        Assert.NotEqual<obj option>(
            Some(box Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden),
            propOf "VerticalScrollBarVisibility" viewer
        )

// ---------------------------------------------------------------------------
// Real tasks, not anonymous assignment chunks
// ---------------------------------------------------------------------------

/// **The review's words: "do not pretend that a generic assignment chunk is an
/// actual task."**
///
/// Before this, a 45-hour project produced a column of identical blocks all
/// called "Final project report". A block now names the milestone it advances.

let private milestone (n: int) (title: string) (estimate: int option) (assignmentId: AssignmentId) =
    { Id = MilestoneId(Guid(sprintf "00000000-0000-0000-0000-%012d" (1400 + n)))
      AssignmentId = assignmentId
      Title = title
      State = MilestoneState.NotStarted
      Order = n
      EstimatedMinutes = estimate
      CreatedAt = now
      UpdatedAt = now }

let private taskWithSteps (titles: (string * int option) list) =
    let id = AssignmentId(Guid "00000000-0000-0000-0000-000000000201")

    { AssignmentId = id
      Title = "Final project report"
      ModuleCode = Some "COMP3931"
      Deadline = Some(now.Date.AddDays(6.0).AddHours 17.0)
      RemainingMinutes = 600
      Priority = Priority.Critical
      Weighting = Some 60.0
      Milestones = titles |> List.mapi (fun i (t, e) -> milestone i t e id) }

[<Fact>]
let ``blocks are named for the milestone they advance, not the assignment`` () =
    let plan =
        planWith [ taskWithSteps [ "Build the loaders", Some 120; "Run the seeds", Some 240; "Write it up", Some 240 ] ]

    Assert.NotEmpty plan.Blocks

    for block in plan.Blocks do
        Assert.NotEqual<string>("Final project report", block.Title)
        Assert.True(block.MilestoneId.IsSome, "a block advances no identified step")

    let names = plan.Blocks |> List.map (fun b -> b.Title) |> List.distinct
    Assert.True(names.Length >= 2, sprintf "the whole plan advances one step only: %A" names)

/// The steps are worked in the user's order, not in whatever order the
/// scheduler reaches them.
[<Fact>]
let ``milestones are advanced in the order the user arranged them`` () =
    let plan =
        planWith [ taskWithSteps [ "First", Some 60; "Second", Some 60; "Third", Some 60 ] ]

    let order = plan.Blocks |> List.map (fun b -> b.Title) |> List.distinct
    Assert.Equal<string list>([ "First"; "Second"; "Third" ], order |> List.truncate 3)

/// **An assignment nobody has broken down still schedules.** Empty is a real
/// case, not a missing one, and it must keep the behaviour it always had.
[<Fact>]
let ``an assignment with no milestones keeps its own title`` () =
    let plan = planWith [ task "Read the handbook" (Some 5.0) 120 Priority.Low None ]

    Assert.NotEmpty plan.Blocks

    for block in plan.Blocks do
        Assert.Equal<string>("Read the handbook", block.Title)
        Assert.True(block.MilestoneId.IsNone)

/// Estimated steps take their estimate; unestimated ones split what is left,
/// and never get zero — a zero share would skip the step entirely and it would
/// never appear on the plan at all.
[<Fact>]
let ``the allocation gives estimated steps their estimate and shares the rest`` () =
    let id = AssignmentId(Guid "00000000-0000-0000-0000-000000000201")

    let steps =
        [ milestone 0 "Known" (Some 100) id
          milestone 1 "Unknown A" None id
          milestone 2 "Unknown B" None id ]

    let allocation = Planning.milestoneAllocation 300 steps |> List.map (fun (_, t, m) -> t, m)

    Assert.Equal<(string * int) list>([ "Known", 100; "Unknown A", 100; "Unknown B", 100 ], allocation)

    // Nothing left over still leaves every step a place on the plan.
    let squeezed = Planning.milestoneAllocation 100 steps |> List.map (fun (_, _, m) -> m)
    Assert.All(squeezed, fun m -> Assert.True(m >= 1))

[<Fact>]
let ``an assignment with no milestones allocates nothing`` () =
    Assert.Empty(Planning.milestoneAllocation 300 [])
