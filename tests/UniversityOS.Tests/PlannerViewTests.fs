module UniversityOS.Tests.PlannerViewTests

open System
open Xunit
open UniversityOS.Domain
open UniversityOS.Domain.Planning
open UniversityOS.Application
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Features

/// The migrated Planner.
///
/// Every decision the screen makes lives in `PlannerLogic` or in `update`, so
/// all of it is testable without an Avalonia application or a database: the
/// workload split, the constraint checks, the optimisation figures, the derived
/// observations and the responsive thresholds.
///
/// The scheduling ENGINE itself is already covered by `PlanningTests`; nothing
/// here re-tests `Planning.plan`.

let private now = DateTime(2026, 7, 26, 14, 0, 0)

let private prefs = PlannerPreferences.ofSettings UserSettings.defaults

/// A session on a given day, at a given hour, for a given number of minutes.
let private sessionOn (day: int) (hour: int) (minutes: float) (title: string) (moduleCode: string option) =
    let starts = DateTime(2026, 7, day, hour, 0, 0)

    { Id = SessionId.New()
      AssignmentId = None
      MilestoneId = None
      Title = title
      ModuleCode = moduleCode
      StartsAt = starts
      EndsAt = starts.AddMinutes minutes
      Reason = "due in 3 days · 4.5h remaining · High priority"
      Completed = false
      CreatedAt = starts }

let private unplanned (title: string) : UnplannedTask =
    { Task =
        { AssignmentId = AssignmentId.New()
          Title = title
          ModuleCode = None
          Deadline = None
          RemainingMinutes = 300
          Priority = Priority.High
          Weighting = None 
          Milestones = [] }
      Reason = "no room before the deadline" }

// ---- session status ---------------------------------------------------------

[<Fact>]
let ``a session's status follows the clock and its completion`` () =
    let future = sessionOn 27 10 50.0 "Tomorrow" None
    let past = sessionOn 25 10 50.0 "Yesterday" None
    let running = { sessionOn 26 13 90.0 "Running now" None with EndsAt = now.AddMinutes 30.0 }
    let soon = sessionOn 26 15 50.0 "Later today" None

    Assert.Equal(PlannerLogic.Scheduled, PlannerLogic.statusOf now future)

    // **Missed, not Done.** This line used to assert `Done` for a session that
    // had merely ended, which credited the user with work they never ticked off
    // and made the two outcomes indistinguishable. The runtime review asked for
    // a real Missed state; it was derivable from `Completed` and the clock all
    // along. Completion is asserted separately in the test below, which is the
    // half of the old rule that was always right.
    Assert.Equal(PlannerLogic.Missed, PlannerLogic.statusOf now past)
    Assert.Equal(PlannerLogic.InProgress, PlannerLogic.statusOf now running)
    Assert.Equal(PlannerLogic.UpNext, PlannerLogic.statusOf now soon)

[<Fact>]
let ``a ticked session is done whatever the clock says`` () =
    let future = { sessionOn 27 10 50.0 "Tomorrow" None with Completed = true }
    Assert.Equal(PlannerLogic.Done, PlannerLogic.statusOf now future)

[<Fact>]
let ``every status has a distinct word, so colour is never the only signal`` () =
    let labels =
        [ PlannerLogic.Done; PlannerLogic.InProgress; PlannerLogic.UpNext; PlannerLogic.Scheduled ]
        |> List.map PlannerLogic.SessionStatus.label

    Assert.Equal(labels.Length, (List.distinct labels).Length)
    Assert.All(labels, fun l -> Assert.False(String.IsNullOrWhiteSpace l))

[<Fact>]
let ``elapsed progress is real time, not an invented estimate`` () =
    // Future: nothing elapsed. Completed: all of it. Halfway through: half.
    Assert.Equal(0, PlannerLogic.elapsedPercent now (sessionOn 27 10 50.0 "Tomorrow" None))
    Assert.Equal(100, PlannerLogic.elapsedPercent now { sessionOn 27 10 50.0 "Done" None with Completed = true })

    let halfway =
        { sessionOn 26 13 0.0 "Halfway" None with
            StartsAt = now.AddMinutes -30.0
            EndsAt = now.AddMinutes 30.0 }

    Assert.Equal(50, PlannerLogic.elapsedPercent now halfway)

[<Fact>]
let ``elapsed progress never leaves 0 to 100, even for an odd session`` () =
    let zeroLength = { sessionOn 26 13 0.0 "Instant" None with EndsAt = DateTime(2026, 7, 26, 13, 0, 0) }
    Assert.InRange(PlannerLogic.elapsedPercent now zeroLength, 0, 100)

    let longPast = sessionOn 1 9 50.0 "Ages ago" None
    Assert.InRange(PlannerLogic.elapsedPercent now longPast, 0, 100)

[<Fact>]
let ``effort bands follow the block length`` () =
    Assert.Equal(PlannerLogic.Light, PlannerLogic.effortOf (sessionOn 26 9 30.0 "Short" None))
    Assert.Equal(PlannerLogic.Moderate, PlannerLogic.effortOf (sessionOn 26 9 50.0 "Normal" None))
    Assert.Equal(PlannerLogic.Deep, PlannerLogic.effortOf (sessionOn 26 9 120.0 "Long" None))

// ---- reasoning --------------------------------------------------------------

[<Fact>]
let ``reason chips are the planner's own output, split on its own separator`` () =
    // `Planning.reasonFor` joins its parts with " · ". This reverses exactly
    // that rather than parsing free text.
    let s = sessionOn 26 9 50.0 "Coursework" None
    let chips = PlannerLogic.reasonChips s

    Assert.Equal<string list>([ "due in 3 days"; "4.5h remaining"; "High priority" ], chips)

[<Fact>]
let ``a session with no reason produces no chips`` () =
    let s = { sessionOn 26 9 50.0 "Coursework" None with Reason = "" }
    Assert.Empty(PlannerLogic.reasonChips s)

    let blank = { sessionOn 26 9 50.0 "Coursework" None with Reason = "   " }
    Assert.Empty(PlannerLogic.reasonChips blank)

// ---- days -------------------------------------------------------------------

[<Fact>]
let ``the plan groups by day, earliest first, sessions in time order`` () =
    let sessions =
        [ sessionOn 28 9 50.0 "Later day" None
          sessionOn 26 15 50.0 "Today afternoon" None
          sessionOn 26 9 50.0 "Today morning" None ]

    let days = PlannerLogic.days sessions

    Assert.Equal(2, days.Length)
    Assert.Equal(DateTime(2026, 7, 26), days.[0].Date)
    Assert.Equal(DateTime(2026, 7, 28), days.[1].Date)
    Assert.Equal<string list>(
        [ "Today morning"; "Today afternoon" ],
        days.[0].Sessions |> List.map (fun s -> s.Title)
    )

[<Fact>]
let ``each day carries its own planned total`` () =
    let sessions = [ sessionOn 26 9 50.0 "A" None; sessionOn 26 11 70.0 "B" None ]
    Assert.Equal(120, (PlannerLogic.days sessions).[0].Minutes)

[<Fact>]
let ``grouping never drops or duplicates a session`` () =
    let sessions =
        [ sessionOn 26 9 50.0 "A" None
          sessionOn 27 9 50.0 "B" None
          sessionOn 27 11 50.0 "C" None
          sessionOn 30 9 50.0 "D" None ]

    let regrouped = PlannerLogic.days sessions |> List.collect (fun d -> d.Sessions)

    Assert.Equal(sessions.Length, regrouped.Length)
    Assert.Equal<Guid list>(
        sessions |> List.map (fun s -> s.Id.Value) |> List.sort,
        regrouped |> List.map (fun s -> s.Id.Value) |> List.sort
    )

[<Fact>]
let ``today is only today's sessions`` () =
    let sessions = [ sessionOn 26 9 50.0 "Today" None; sessionOn 27 9 50.0 "Tomorrow" None ]
    let todays = PlannerLogic.today now sessions

    Assert.Single todays |> ignore
    Assert.Equal("Today", todays.Head.Title)

// ---- workload distribution --------------------------------------------------

[<Fact>]
let ``workload splits by module and totals the plan`` () =
    let sessions =
        [ sessionOn 26 9 60.0 "A" (Some "CS2004")
          sessionOn 26 11 60.0 "B" (Some "CS2004")
          sessionOn 27 9 60.0 "C" (Some "MA1512") ]

    let slices = PlannerLogic.workload sessions

    Assert.Equal(2, slices.Length)
    // Largest first.
    Assert.Equal("CS2004", slices.[0].Label)
    Assert.Equal(120, slices.[0].Minutes)
    Assert.Equal(67, slices.[0].Percent)
    Assert.Equal(60, slices.[1].Minutes)

    // The bar must account for the whole plan or it lies.
    Assert.Equal(180, slices |> List.sumBy (fun s -> s.Minutes))

[<Fact>]
let ``sessions with no module are grouped, not dropped`` () =
    let sessions = [ sessionOn 26 9 60.0 "A" (Some "CS2004"); sessionOn 26 11 60.0 "B" None ]
    let slices = PlannerLogic.workload sessions

    Assert.Equal(2, slices.Length)
    Assert.Contains("Other", slices |> List.map (fun s -> s.Label))
    Assert.Equal(120, slices |> List.sumBy (fun s -> s.Minutes))

[<Fact>]
let ``an empty plan has no workload slices`` () =
    Assert.Empty(PlannerLogic.workload [])

// ---- constraint validation --------------------------------------------------

[<Fact>]
let ``a well-formed plan passes every check`` () =
    let sessions =
        [ sessionOn 26 9 50.0 "A" (Some "CS2004")
          sessionOn 26 11 50.0 "B" (Some "CS2004")
          sessionOn 27 10 50.0 "C" (Some "MA1512") ]

    let checks = PlannerLogic.checks prefs [] sessions

    Assert.All(checks, fun c -> Assert.True(c.Passed, c.Text))
    Assert.Equal(100, PlannerLogic.score checks)

[<Fact>]
let ``overlapping sessions are caught`` () =
    let first = sessionOn 26 9 60.0 "A" None
    let overlapping = { sessionOn 26 9 60.0 "B" None with StartsAt = first.StartsAt.AddMinutes 30.0 }

    let checks = PlannerLogic.checks prefs [] [ first; overlapping ]
    let overlap = checks |> List.find (fun c -> c.Text.Contains "overlap")

    Assert.False overlap.Passed
    Assert.True(PlannerLogic.score checks < 100)

[<Fact>]
let ``a day over the daily maximum is caught, and the text names the figures`` () =
    // Default maximum is 6 hours. Seven one-hour blocks in a day exceeds it.
    let sessions = [ for hour in 9..15 -> sessionOn 26 hour 60.0 (sprintf "Block %d" hour) None ]
    let checks = PlannerLogic.checks prefs [] sessions
    let daily = checks.Head

    Assert.False daily.Passed
    Assert.Contains("maximum", daily.Text)

[<Fact>]
let ``sessions outside the preferred study hours are caught`` () =
    // Default window is 09:00–21:00.
    let early = sessionOn 26 6 50.0 "Dawn" None
    let checks = PlannerLogic.checks prefs [] [ early ]
    let hours = checks |> List.find (fun c -> c.Text.Contains "study hours")

    Assert.False hours.Passed

[<Fact>]
let ``blocks shorter than the scheduler's minimum are caught`` () =
    let tiny = sessionOn 26 9 10.0 "Too short" None
    let checks = PlannerLogic.checks prefs [] [ tiny ]
    let minimum = checks |> List.find (fun c -> c.Text.Contains "minutes")

    Assert.False minimum.Passed

[<Fact>]
let ``unscheduled work is reported as a failed check`` () =
    let sessions = [ sessionOn 26 9 50.0 "A" None ]

    let passing = PlannerLogic.checks prefs [] sessions |> List.last
    let failing = PlannerLogic.checks prefs [ unplanned "Essay" ] sessions |> List.last

    Assert.True passing.Passed
    Assert.False failing.Passed
    Assert.NotEqual<string>(passing.Text, failing.Text)

[<Fact>]
let ``a passing and a failing check read as different facts`` () =
    // Not the same label with a different tick: the sentence itself changes.
    let good = sessionOn 26 9 50.0 "A" None
    let bad = sessionOn 26 6 50.0 "Dawn" None

    let passingText = (PlannerLogic.checks prefs [] [ good ]).[2].Text
    let failingText = (PlannerLogic.checks prefs [] [ bad ]).[2].Text

    Assert.NotEqual<string>(passingText, failingText)

[<Fact>]
let ``the score is the share of checks that pass`` () =
    Assert.Equal(0, PlannerLogic.score [])

    let allPass = List.replicate 4 { PlannerLogic.Passed = true; PlannerLogic.Text = "ok" }
    Assert.Equal(100, PlannerLogic.score allPass)

    let half =
        [ { PlannerLogic.Passed = true; PlannerLogic.Text = "ok" }
          { PlannerLogic.Passed = false; PlannerLogic.Text = "no" } ]

    Assert.Equal(50, PlannerLogic.score half)

[<Fact>]
let ``checks are measured against the user's own preferences, not a constant`` () =
    // The same plan passes under a generous daily cap and fails under a strict
    // one — which is the whole point of reading the real settings.
    let sessions = [ for hour in 9..13 -> sessionOn 26 hour 60.0 (sprintf "Block %d" hour) None ]

    let strict =
        PlannerPreferences.ofSettings { UserSettings.defaults with MaxDailyStudyHours = 2.0 }

    Assert.True((PlannerLogic.checks prefs [] sessions).Head.Passed)
    Assert.False((PlannerLogic.checks strict [] sessions).Head.Passed)

// ---- optimisation figures ---------------------------------------------------

[<Fact>]
let ``the optimisation rows are real counts`` () =
    let sessions =
        [ sessionOn 26 9 60.0 "A" None
          sessionOn 26 11 60.0 "B" None
          sessionOn 27 9 60.0 "C" None ]

    let checks = PlannerLogic.checks prefs [] sessions
    let rows = PlannerLogic.optimisation prefs checks sessions |> dict

    Assert.Equal("3", rows.["Sessions scheduled"])
    Assert.Equal("2", rows.["Days with study"])
    Assert.Equal("5 of 5", rows.["Constraints satisfied"])
    // 180 planned against 2 days x 360 minutes of allowance = 25%.
    Assert.Equal("25%", rows.["Daily allowance used"])

[<Fact>]
let ``an empty plan does not divide by zero`` () =
    let checks = PlannerLogic.checks prefs [] []
    let rows = PlannerLogic.optimisation prefs checks [] |> dict

    Assert.Equal("0", rows.["Sessions scheduled"])
    Assert.Equal("—", rows.["Daily allowance used"])

[<Fact>]
let ``allowance used never exceeds 100 per cent`` () =
    let sessions = [ for hour in 9..20 -> sessionOn 26 hour 60.0 (sprintf "Block %d" hour) None ]
    let checks = PlannerLogic.checks prefs [] sessions
    let rows = PlannerLogic.optimisation prefs checks sessions |> dict

    Assert.Equal("100%", rows.["Daily allowance used"])

// ---- observations -----------------------------------------------------------

[<Fact>]
let ``an empty plan says nothing rather than inventing something`` () =
    Assert.Empty(PlannerLogic.observations now prefs [] [])

[<Fact>]
let ``observations describe the real plan`` () =
    let sessions =
        [ sessionOn 26 9 60.0 "A" (Some "CS2004")
          sessionOn 26 11 60.0 "B" (Some "CS2004")
          sessionOn 28 9 60.0 "C" (Some "MA1512") ]

    let items = PlannerLogic.observations now prefs [] sessions

    Assert.NotEmpty items
    Assert.All(items, fun t -> Assert.False(String.IsNullOrWhiteSpace t))
    // Today's total is stated first.
    Assert.Contains("planned today", items.Head)

[<Fact>]
let ``unscheduled work is mentioned when there is some`` () =
    let sessions = [ sessionOn 26 9 60.0 "A" None ]
    let withNone = PlannerLogic.observations now prefs [] sessions
    let withSome = PlannerLogic.observations now prefs [ unplanned "Essay" ] sessions

    Assert.DoesNotContain(withNone, fun (t: string) -> t.Contains "cannot give")
    Assert.Contains(withSome, fun (t: string) -> t.Contains "cannot give")

[<Fact>]
let ``observations stay short enough to read`` () =
    let sessions = [ for day in 26..30 -> sessionOn day 9 60.0 (sprintf "Day %d" day) (Some "CS2004") ]
    Assert.True((PlannerLogic.observations now prefs [ unplanned "Essay" ] sessions).Length <= 5)

// ---- planner history --------------------------------------------------------

let private entry (kind: ActivityKind) (hoursAgo: float) (title: string) : ActivityEntry =
    { Id = Guid.NewGuid()
      Kind = kind
      Title = title
      Detail = None
      Entity = None
      OccurredAt = now.AddHours(-hoursAgo) }

[<Fact>]
let ``planner history keeps only entries that describe the plan`` () =
    let entries =
        [ entry ActivityKind.PlanGenerated 1.0 "Study plan regenerated"
          entry ActivityKind.SessionCompleted 2.0 "Finished revision"
          entry ActivityKind.DeadlineChanged 3.0 "Deadline moved"
          entry ActivityKind.NoteCreated 4.0 "Wrote a note"
          entry ActivityKind.ConnectorSynced 5.0 "Synced" ]

    let history = PlannerLogic.history entries

    Assert.Equal(3, history.Length)
    Assert.DoesNotContain(ActivityKind.NoteCreated, history |> List.map (fun e -> e.Kind))
    Assert.DoesNotContain(ActivityKind.ConnectorSynced, history |> List.map (fun e -> e.Kind))

[<Fact>]
let ``planner history is newest first and bounded`` () =
    let entries = [ for i in 1..20 -> entry ActivityKind.PlanGenerated (float i) (sprintf "Plan %d" i) ]
    let history = PlannerLogic.history entries

    Assert.True(history.Length <= 6)
    Assert.Equal("Plan 1", history.Head.Title)

    history
    |> List.pairwise
    |> List.iter (fun (a, b) -> Assert.True(a.OccurredAt >= b.OccurredAt))

// ---- responsive layout ------------------------------------------------------

[<Fact>]
let ``the side column collapses at the same width as the Timeline's`` () =
    Assert.True(PlannerLogic.showsSideColumn 1400.0)
    Assert.False(PlannerLogic.showsSideColumn 900.0)
    Assert.Equal(TimelineLogic.showsSideColumn 1400.0, PlannerLogic.showsSideColumn 1400.0)
    Assert.Equal(TimelineLogic.showsSideColumn 900.0, PlannerLogic.showsSideColumn 900.0)

[<Fact>]
let ``summary columns reflow and never go backwards as the page widens`` () =
    Assert.Equal(1, PlannerLogic.summaryColumns 600.0)
    Assert.Equal(2, PlannerLogic.summaryColumns 900.0)
    Assert.Equal(3, PlannerLogic.summaryColumns 1400.0)

    [ 400.0..40.0..1800.0 ]
    |> List.map PlannerLogic.summaryColumns
    |> List.pairwise
    |> List.iter (fun (a, b) -> Assert.True(b >= a, sprintf "columns went backwards: %d then %d" a b))

// ---- update behaviour -------------------------------------------------------

let private noServices: AppServices = Unchecked.defaultof<AppServices>

let private messagesFrom (cmd: Elmish.Cmd<Planner.Msg>) =
    let seen = ResizeArray<Planner.Msg>()
    for effect in cmd do effect seen.Add
    List.ofSeq seen

let private loaded sessions : Planner.Model =
    { Sessions = sessions
      AllTodayOpen = false
      Unplanned = []
      Summary = None
      Prefs = prefs
      Tasks = []
      Busy = []
      History = []
      Generating = false
      Loading = false
      Error = None
      Strategy = PlannerLogic.Balanced
      Alternatives = []
      PreviewLoading = false
      Focused = None
      Width = 1400.0 }

/// The planner's own input record — what `PlannerService.BuildInput` returns and
/// what a load now carries, so the preferences, the open assignments and the
/// busy blocks on screen are provably the ones the plan was built from.
let private inputWith (prefs: PlannerPreferences) (tasks: PlannableTask list) : PlannerInput =
    { Now = now; Tasks = tasks; Busy = []; Prefs = prefs }

let private task (title: string) (remainingMinutes: int) (deadline: DateTime option) : PlannableTask =
    { AssignmentId = AssignmentId.New()
      Title = title
      ModuleCode = Some "CS2004"
      Deadline = deadline
      RemainingMinutes = remainingMinutes
      Priority = Priority.High
      Weighting = None 
      Milestones = [] }

let private modelOf (m, _) = m

[<Fact>]
let ``a successful load clears loading and any previous error`` () =
    let before = { loaded [] with Loading = true; Error = Some "earlier" }
    let sessions = [ sessionOn 26 9 50.0 "A" None ]

    let after, cmd =
        Planner.update noServices (Planner.Loaded(inputWith prefs [], sessions, [])) before

    Assert.Equal(1, after.Sessions.Length)
    Assert.False after.Loading
    Assert.Equal(None, after.Error)

    // A load now asks for the strategy previews as well. They used to sit behind
    // a "Preview all strategies" button, so the comparison the Alternative Plans
    // section exists to offer was invisible until requested; running them on load
    // is what makes that section honest. Exactly one follow-up, and it is that.
    Assert.Equal<Planner.Msg list>([ Planner.PreviewAlternatives ], messagesFrom cmd)

[<Fact>]
let ``a load carries the preferences the checks will be measured against`` () =
    let strict =
        PlannerPreferences.ofSettings { UserSettings.defaults with MaxDailyStudyHours = 2.0 }

    let after =
        Planner.update noServices (Planner.Loaded(inputWith strict [], [], [])) (loaded [])
        |> modelOf

    Assert.Equal(strict.MaxDailyStudyMinutes, after.Prefs.MaxDailyStudyMinutes)

[<Fact>]
let ``a load carries the outstanding work and the busy blocks, not just the sessions`` () =
    // Coverage, the outlook and the action row all read these. Taking them from
    // the planner's own input record is what stops them being measured against a
    // different moment than the plan they describe.
    let tasks = [ task "Essay" 300 (Some(now.AddDays 3.0)) ]

    let input =
        { inputWith prefs tasks with
            Busy = [ { Start = now; End = now.AddHours 1.0; Label = "Lecture" } ] }

    let after =
        Planner.update noServices (Planner.Loaded(input, [], [])) (loaded []) |> modelOf

    Assert.Equal(300, PlannerLogic.outstandingMinutes after.Tasks)
    Assert.Single after.Busy |> ignore

[<Fact>]
let ``a failure surfaces the message and stops any spinner`` () =
    let before = { loaded [] with Generating = true; Loading = true }
    let after = Planner.update noServices (Planner.Failed(exn "offline")) before |> modelOf

    Assert.Equal(Some "offline", after.Error)
    Assert.False after.Loading
    // The button must not stay stuck on "Planning…".
    Assert.False after.Generating

[<Fact>]
let ``a failure keeps the plan already on screen`` () =
    let sessions = [ sessionOn 26 9 50.0 "A" None ]
    let after = Planner.update noServices (Planner.Failed(exn "offline")) (loaded sessions) |> modelOf

    Assert.Equal(1, after.Sessions.Length)
    Assert.Equal(Some "offline", after.Error)

[<Fact>]
let ``retrying after a failure asks for the data again`` () =
    let failed = { loaded [] with Error = Some "offline" }
    let after, cmd = Planner.update noServices Planner.Load failed

    Assert.True after.Loading
    Assert.NotEmpty cmd

[<Fact>]
let ``generating sets the flag and clears the previous error`` () =
    let before = { loaded [] with Error = Some "earlier" }
    let after, cmd = Planner.update noServices Planner.Generate before

    Assert.True after.Generating
    Assert.Equal(None, after.Error)
    Assert.NotEmpty cmd

[<Fact>]
let ``a second Generate while one is running is dropped, not queued`` () =
    // The double-click guard: one generation at a time, never two.
    let running = { loaded [] with Generating = true }
    let after, cmd = Planner.update noServices Planner.Generate running

    Assert.True after.Generating
    Assert.Empty cmd

[<Fact>]
let ``a finished generation keeps the summary and reloads the real sessions`` () =
    let result =
        { Blocks = []
          Unplanned = [ unplanned "Essay" ]
          Summary = "Planned 6.0h across 3 day(s)." }

    let after, cmd = Planner.update noServices (Planner.Generated result) { loaded [] with Generating = true }

    Assert.False after.Generating
    Assert.Equal(Some "Planned 6.0h across 3 day(s).", after.Summary)
    Assert.Equal(1, after.Unplanned.Length)
    // Reloaded rather than patched from the result: `Regenerate` writes through
    // the repository, so only a re-read shows what was persisted.
    Assert.NotEmpty cmd

[<Fact>]
let ``completing a session asks the service and then reloads`` () =
    let sessions = [ sessionOn 26 9 50.0 "A" None ]
    let target = sessions.Head

    let _, cmd = Planner.update noServices (Planner.ToggleSession(target.Id, true)) (loaded sessions)
    Assert.NotEmpty cmd

    // The reload is what makes the plan, its totals and its checks agree again.
    let after, reload = Planner.update noServices Planner.SessionToggled (loaded sessions)
    Assert.NotEmpty reload
    Assert.Equal(1, after.Sessions.Length)

// The "Explain Plan" toggle is gone. The scheduler's reasoning is shown
// unconditionally — the domain describes `PlannedSession.Reason` as "always
// shown to the user", and hiding it behind a control that defaulted to off
// contradicted that. With no view state left to toggle there is nothing here to
// test; `PlannerPresentationTests` covers the reasoning itself.

[<Fact>]
let ``changing view state never re-queries`` () =
    // Toggling reasoning and resizing are presentation decisions over data
    // already loaded. Neither may issue a command.
    let before = loaded [ sessionOn 26 9 50.0 "A" None ]

    for msg in [ Planner.Resized 900.0 ] do
        let _, cmd = Planner.update noServices msg before
        Assert.Empty cmd

[<Fact>]
let ``resizing records the width the layout decisions read`` () =
    let after = Planner.update noServices (Planner.Resized 800.0) (loaded []) |> modelOf

    Assert.Equal(800.0, after.Width, 6)
    Assert.False(PlannerLogic.showsSideColumn after.Width)

[<Fact>]
let ``a fresh Planner starts empty, loading, and not generating`` () =
    let model = loaded []

    Assert.Empty model.Sessions
    Assert.False model.Generating
    Assert.Equal(None, model.Summary)

// =============================================================================
// The fidelity correction.
//
// The screen the redesign describes is a full page of nine cards. The version
// this replaces collapsed to a floating empty state with three cards hidden
// entirely whenever no plan existed, and scored that empty plan 100%. The tests
// below pin the structure, the honest empty states, and the fact that every
// action and every alternative does real work.
// =============================================================================

// ---- page structure ---------------------------------------------------------

[<Fact>]
let ``the wide layout presents every redesign card exactly once, in reading order`` () =
    Assert.Equal<PlannerLogic.Card list>(
        [ PlannerLogic.TodayPlan
          PlannerLogic.ScheduleOptimisation
          PlannerLogic.PlanReasoning
          PlannerLogic.WorkloadDistribution
          PlannerLogic.ConstraintValidation
          PlannerLogic.PlannerInsights
          PlannerLogic.AlternativePlans
          PlannerLogic.PlannerHistory
          PlannerLogic.FuturePrediction ],
        PlannerLogic.cardOrder
    )

    Assert.Equal(PlannerLogic.cardOrder.Length, (List.distinct PlannerLogic.cardOrder).Length)

[<Fact>]
let ``the rows partition the card order, so no card can be quietly dropped`` () =
    let fromRows =
        PlannerLogic.mainRow
        @ PlannerLogic.sideColumn
        @ PlannerLogic.analysisRow
        @ PlannerLogic.fullWidthRow
        @ PlannerLogic.bottomRow

    Assert.Equal<PlannerLogic.Card list>(PlannerLogic.cardOrder, fromRows)
    Assert.Equal(3, PlannerLogic.analysisRow.Length)
    Assert.Equal(2, PlannerLogic.bottomRow.Length)
    Assert.Equal<PlannerLogic.Card list>([ PlannerLogic.AlternativePlans ], PlannerLogic.fullWidthRow)

[<Fact>]
let ``an empty plan still fills every card, rather than collapsing the page`` () =
    // This is the defect the correction exists for: with no sessions the old
    // screen rendered one floating empty state and hid Workload, Constraints
    // and History outright. Every card must still have real content to show.
    let model = loaded []

    Assert.NotEmpty(PlannerLogic.constraintNames model.Prefs)
    Assert.NotEmpty(PlannerLogic.outlook now model.Tasks [] [])
    Assert.NotEmpty PlannerLogic.Strategy.supported

    Assert.NotEmpty(
        PlannerLogic.actions
            { Generating = false
              Strategy = PlannerLogic.Balanced
              HasSessions = false
              HasOutstandingWork = false
              HasFocusTarget = false }
    )

    // And the two that are legitimately empty say so by being empty, not by
    // being absent: the view renders their empty state inside the card.
    Assert.Empty(PlannerLogic.workload [])
    Assert.Empty(PlannerLogic.reasoningBullets model.Prefs [] [])

// ---- an empty plan is not a perfect plan ------------------------------------

[<Fact>]
let ``an empty plan is never scored, and never scores 100 per cent`` () =
    Assert.Equal(PlannerLogic.NotEvaluated, PlannerLogic.evaluate prefs [] [])
    Assert.Equal(None, PlannerLogic.scoreOf (PlannerLogic.evaluate prefs [] []))

    // The trap this guards, stated outright: every constraint is vacuously true
    // when there are no sessions, so scoring the raw checks awards a blank
    // planner full marks.
    Assert.Equal(100, PlannerLogic.score (PlannerLogic.checks prefs [] []))

[<Fact>]
let ``a real plan is evaluated and carries its real score`` () =
    let sessions = [ sessionOn 26 9 50.0 "A" (Some "CS2004") ]
    let evaluation = PlannerLogic.evaluate prefs [] sessions

    match evaluation with
    | PlannerLogic.NotEvaluated -> failwith "a plan with sessions must be evaluated"
    | PlannerLogic.Evaluated checks -> Assert.Equal(5, checks.Length)

    Assert.Equal(Some 100, PlannerLogic.scoreOf evaluation)

    // A plan that breaks a rule scores below full marks — the score still means
    // something once it exists.
    let broken = [ sessionOn 26 6 50.0 "Dawn" None ]
    let brokenScore = PlannerLogic.scoreOf (PlannerLogic.evaluate prefs [] broken)

    Assert.True(brokenScore.IsSome && brokenScore.Value < 100)

[<Fact>]
let ``an unevaluated plan still names every constraint it will be judged by`` () =
    // The Constraint Validation card lists the rules as pending rather than
    // showing them ticked. The two lists must stay the same length or the card
    // would promise a check the plan is never measured against.
    let sessions = [ sessionOn 26 9 50.0 "A" None ]

    Assert.Equal(
        (PlannerLogic.checks prefs [] sessions).Length,
        (PlannerLogic.constraintNames prefs).Length
    )

    Assert.All(PlannerLogic.constraintNames prefs, fun n -> Assert.False(String.IsNullOrWhiteSpace n))

[<Fact>]
let ``constraint names quote the user's own settings`` () =
    let strict =
        PlannerPreferences.ofSettings
            { UserSettings.defaults with
                MaxDailyStudyHours = 2.0
                WorkdayStartHour = 8 }

    let names = PlannerLogic.constraintNames strict |> String.concat " | "

    Assert.Contains("2h", names)
    Assert.Contains("08:00", names)

// ---- the action row ---------------------------------------------------------

let private context hasSessions hasWork hasFocus strategy : PlannerLogic.ActionContext =
    { Generating = false
      Strategy = strategy
      HasSessions = hasSessions
      HasOutstandingWork = hasWork
      HasFocusTarget = hasFocus }

[<Fact>]
let ``the action row offers all six of the redesign's actions, in order`` () =
    let ids =
        PlannerLogic.actions (context true true true PlannerLogic.LowStress)
        |> List.map (fun a -> a.Id)

    Assert.Equal<PlannerLogic.ActionId list>(
        [ PlannerLogic.RegeneratePlan
          PlannerLogic.OptimiseAgain
          PlannerLogic.BalanceWorkload
          PlannerLogic.FocusCurrentSession
          PlannerLogic.ViewAlternatives ],
        ids
    )

[<Fact>]
let ``with a plan and work to do, every action is available`` () =
    let states = PlannerLogic.actions (context true true true PlannerLogic.LowStress)

    Assert.All(states, fun a -> Assert.True(a.Enabled, a.Label))
    Assert.All(states, fun a -> Assert.Equal(None, a.DisabledBecause))

[<Fact>]
let ``only the primary action carries the accent`` () =
    let primaries =
        PlannerLogic.actions (context true true true PlannerLogic.Balanced)
        |> List.filter (fun a -> a.Primary)

    Assert.Single primaries |> ignore
    Assert.Equal(PlannerLogic.RegeneratePlan, primaries.Head.Id)

[<Fact>]
let ``every unavailable action explains itself in words`` () =
    // Never a silent no-op: an action that cannot run says why, and the view
    // prints that reason as visible text.
    let states = PlannerLogic.actions (context false false false PlannerLogic.Balanced)
    let disabled = states |> List.filter (fun a -> not a.Enabled)

    Assert.NotEmpty disabled

    Assert.All(
        disabled,
        fun a ->
            Assert.True(a.DisabledBecause.IsSome, a.Label)
            Assert.False(String.IsNullOrWhiteSpace a.DisabledBecause.Value)
    )

[<Fact>]
let ``a generation in flight disables the whole row`` () =
    let states =
        PlannerLogic.actions
            { context true true true PlannerLogic.LowStress with Generating = true }

    Assert.All(states, fun a -> Assert.False(a.Enabled, a.Label))
    Assert.Contains("Planning", states.Head.Label)

[<Fact>]
let ``Optimise Again is unavailable under Balanced, because the planner is deterministic`` () =
    // Re-running the user's saved settings returns the identical plan. Offering
    // it as a second Regenerate would be a control that does nothing new.
    let balanced =
        PlannerLogic.actions (context true true true PlannerLogic.Balanced)
        |> List.find (fun a -> a.Id = PlannerLogic.OptimiseAgain)

    Assert.False balanced.Enabled
    Assert.Contains("deterministic", balanced.DisabledBecause.Value)

    let alternative =
        PlannerLogic.actions (context true true true PlannerLogic.HighProductivity)
        |> List.find (fun a -> a.Id = PlannerLogic.OptimiseAgain)

    Assert.True alternative.Enabled

// "Explain Plan" no longer exists as an action — reasoning is always shown, so
// there is no command to enable or disable and nothing to explain about it.

[<Fact>]
let ``the actions that need outstanding coursework say so when there is none`` () =
    let states = PlannerLogic.actions (context true false true PlannerLogic.HighProductivity)

    for id in [ PlannerLogic.BalanceWorkload; PlannerLogic.ViewAlternatives; PlannerLogic.OptimiseAgain ] do
        let action = states |> List.find (fun a -> a.Id = id)
        Assert.False(action.Enabled, action.Label)
        Assert.Contains("coursework", action.DisabledBecause.Value)

[<Fact>]
let ``Focus Current Session targets the running session, else the next one today`` () =
    let running =
        { sessionOn 26 13 0.0 "Running" None with
            StartsAt = now.AddMinutes -10.0
            EndsAt = now.AddMinutes 40.0 }

    let later = sessionOn 26 17 50.0 "Later today" None
    let tomorrow = sessionOn 27 9 50.0 "Tomorrow" None

    Assert.Equal(Some running.Id, PlannerLogic.focusTarget now [ later; running; tomorrow ])
    Assert.Equal(Some later.Id, PlannerLogic.focusTarget now [ later; tomorrow ])

    // Nothing today: the action disables itself rather than doing nothing.
    Assert.Equal(None, PlannerLogic.focusTarget now [ tomorrow ])

    // A finished session is not something to focus on.
    Assert.Equal(None, PlannerLogic.focusTarget now [ { later with Completed = true } ])

[<Fact>]
let ``focusing marks a session without touching the plan or asking the service`` () =
    let sessions = [ sessionOn 26 9 50.0 "A" None ]
    let after, cmd = Planner.update noServices Planner.FocusCurrent (loaded sessions)

    Assert.Empty cmd
    Assert.Equal(1, after.Sessions.Length)

// ---- alternative plans ------------------------------------------------------

[<Fact>]
let ``every offered strategy is a real, distinct transform of the user's settings`` () =
    // Not decorative pills: each one changes preferences the scheduler reads,
    // so previewing it produces a genuinely different plan.
    let transformed =
        PlannerLogic.Strategy.supported
        |> List.map (fun s -> PlannerLogic.Strategy.applyTo prefs s)

    Assert.Equal(transformed.Length, (List.distinct transformed).Length)

    // Balanced is the user's own settings, untouched.
    Assert.Equal(prefs, PlannerLogic.Strategy.applyTo prefs PlannerLogic.Balanced)

    // The others each move something the planner actually uses.
    Assert.All(
        PlannerLogic.Strategy.supported |> List.filter (fun s -> s <> PlannerLogic.Balanced),
        fun s -> Assert.NotEqual(prefs, PlannerLogic.Strategy.applyTo prefs s)
    )

[<Fact>]
let ``each strategy moves the setting its description claims`` () =
    let deadline = PlannerLogic.Strategy.applyTo prefs PlannerLogic.DeadlineFocused
    let productive = PlannerLogic.Strategy.applyTo prefs PlannerLogic.HighProductivity
    let low = PlannerLogic.Strategy.applyTo prefs PlannerLogic.LowStress

    Assert.True(deadline.DeadlineBufferHours > prefs.DeadlineBufferHours)
    Assert.True(productive.MaxDailyStudyMinutes > prefs.MaxDailyStudyMinutes)
    Assert.True(productive.SessionMinutes > prefs.SessionMinutes)
    Assert.True(low.MaxDailyStudyMinutes < prefs.MaxDailyStudyMinutes)
    Assert.True(low.SessionMinutes < prefs.SessionMinutes)
    Assert.True(low.BreakMinutes > prefs.BreakMinutes)

[<Fact>]
let ``no strategy can produce a block below the scheduler's own minimum`` () =
    let tiny =
        PlannerPreferences.ofSettings
            { UserSettings.defaults with SessionMinutes = 25; MaxDailyStudyHours = 0.5 }

    for strategy in PlannerLogic.Strategy.supported do
        let result = PlannerLogic.Strategy.applyTo tiny strategy
        Assert.True(result.SessionMinutes >= Planning.MinBlockMinutes, PlannerLogic.Strategy.label strategy)
        Assert.True(result.MaxDailyStudyMinutes >= Planning.MinBlockMinutes, PlannerLogic.Strategy.label strategy)

[<Fact>]
let ``every strategy is named and described`` () =
    Assert.All(
        PlannerLogic.Strategy.supported,
        fun s ->
            Assert.False(String.IsNullOrWhiteSpace(PlannerLogic.Strategy.label s))
            Assert.False(String.IsNullOrWhiteSpace(PlannerLogic.Strategy.description s))
    )

    let labels = PlannerLogic.Strategy.supported |> List.map PlannerLogic.Strategy.label
    Assert.Equal(labels.Length, (List.distinct labels).Length)

[<Fact>]
let ``Revision Focused is absent, not offered as a strategy that does nothing`` () =
    // The redesign's fifth strategy. `BuildPlannables` reads open assignments
    // only and no preference weights revision, so it would schedule exactly the
    // same plan as Balanced while claiming otherwise.
    let labels = PlannerLogic.Strategy.supported |> List.map PlannerLogic.Strategy.label

    Assert.DoesNotContain("Revision Focused", labels)
    Assert.Equal(4, PlannerLogic.Strategy.supported.Length)

[<Fact>]
let ``selecting a strategy is a choice, and never regenerates on its own`` () =
    let before = loaded [ sessionOn 26 9 50.0 "A" None ]
    let after, cmd = Planner.update noServices (Planner.SelectStrategy PlannerLogic.LowStress) before

    Assert.Equal(PlannerLogic.LowStress, after.Strategy)
    Assert.Empty cmd
    Assert.Equal(1, after.Sessions.Length)

[<Fact>]
let ``applying a strategy is guarded exactly like Regenerate`` () =
    let running = { loaded [] with Generating = true; Strategy = PlannerLogic.LowStress }
    let blocked, blockedCmd = Planner.update noServices Planner.ApplyStrategy running

    Assert.True blocked.Generating
    Assert.Empty blockedCmd

    let idle = { loaded [] with Strategy = PlannerLogic.LowStress }
    let started, startedCmd = Planner.update noServices Planner.ApplyStrategy idle

    Assert.True started.Generating
    Assert.NotEmpty startedCmd

[<Fact>]
let ``balancing the workload is guarded, and derived from the real outstanding work`` () =
    let running = { loaded [] with Generating = true }
    let _, blockedCmd = Planner.update noServices Planner.BalanceWorkload running
    Assert.Empty blockedCmd

    let idle = { loaded [] with Tasks = [ task "Essay" 600 None ] }
    let started, startedCmd = Planner.update noServices Planner.BalanceWorkload idle

    Assert.True started.Generating
    Assert.NotEmpty startedCmd

[<Fact>]
let ``the balanced cap spreads outstanding work but never exceeds the user's own maximum`` () =
    // A lot of work: the cap stays where the user put it.
    let heavy = [ task "Dissertation" (prefs.MaxDailyStudyMinutes * prefs.HorizonDays * 3) None ]
    Assert.Equal(prefs.MaxDailyStudyMinutes, PlannerLogic.balancedCap prefs heavy)

    // A little work: the cap drops so the same work spreads across the horizon
    // instead of landing in the first day or two.
    let light = [ task "Reading" 140 None ]
    let cap = PlannerLogic.balancedCap prefs light

    Assert.True(cap < prefs.MaxDailyStudyMinutes)
    Assert.True(cap >= Planning.MinBlockMinutes)

    // Nothing outstanding: nothing to rebalance, so the setting is left alone.
    Assert.Equal(prefs.MaxDailyStudyMinutes, PlannerLogic.balancedCap prefs [])

[<Fact>]
let ``previewing alternatives is not queued twice`` () =
    let running = { loaded [] with PreviewLoading = true }
    let after, cmd = Planner.update noServices Planner.PreviewAlternatives running

    Assert.True after.PreviewLoading
    Assert.Empty cmd

    let idle, startedCmd = Planner.update noServices Planner.PreviewAlternatives (loaded [])
    Assert.True idle.PreviewLoading
    Assert.NotEmpty startedCmd

[<Fact>]
let ``a preview summarises what the scheduler actually produced`` () =
    let block (hour: int) (minutes: float) : PlannedBlock =
        let start = DateTime(2026, 7, 26, hour, 0, 0)

        { AssignmentId = AssignmentId.New()
          MilestoneId = None
          Title = "Essay"
          ModuleCode = Some "CS2004"
          Start = start
          End = start.AddMinutes minutes
          Reason = "due in 3 days" }

    let result =
        { Blocks = [ block 9 60.0; block 11 60.0; { block 9 90.0 with Start = DateTime(2026, 7, 27, 9, 0, 0); End = DateTime(2026, 7, 27, 10, 30, 0) } ]
          Unplanned = [ unplanned "Report" ]
          Summary = "" }

    let summary = PlannerLogic.summarise PlannerLogic.LowStress result

    Assert.Equal(PlannerLogic.LowStress, summary.Strategy)
    Assert.Equal(3, summary.Sessions)
    Assert.Equal(210, summary.Minutes)
    Assert.Equal(2, summary.Days)
    Assert.Equal(1, summary.Unplanned)

/// Annotated so the field names resolve: the record's `Strategy` field and the
/// `Strategy` module share a name under `PlannerLogic`.
let private alternative strategy : PlannerLogic.Alternative =
    { Strategy = strategy
      Sessions = 4
      Minutes = 200
      Days = 2
      Unplanned = 0 }

[<Fact>]
let ``previews arriving clear the loading flag and are kept`` () =
    let previews = [ alternative PlannerLogic.Balanced ]

    let after, cmd =
        Planner.update noServices (Planner.AlternativesReady previews) { loaded [] with PreviewLoading = true }

    Assert.False after.PreviewLoading
    Assert.Single after.Alternatives |> ignore
    Assert.Empty cmd

[<Fact>]
let ``a finished generation drops previews measured against the old plan`` () =
    let stale =
        { loaded [] with
            Generating = true
            Alternatives = [ alternative PlannerLogic.Balanced ] }

    let result = { Blocks = []; Unplanned = []; Summary = "done" }
    let after = Planner.update noServices (Planner.Generated result) stale |> modelOf

    Assert.Empty after.Alternatives
    Assert.False after.Generating

[<Fact>]
let ``a failure clears the preview spinner as well as the generation spinner`` () =
    let busy = { loaded [] with Generating = true; PreviewLoading = true }
    let after = Planner.update noServices (Planner.Failed(exn "offline")) busy |> modelOf

    Assert.False after.Generating
    Assert.False after.PreviewLoading
    Assert.Equal(Some "offline", after.Error)

// ---- schedule optimisation figures ------------------------------------------

[<Fact>]
let ``coverage measures scheduled minutes against outstanding minutes`` () =
    // Not a "completion confidence": a measurement of the plan, not a claim
    // about the future.
    let tasks = [ task "Essay" 200 None ]
    let sessions = [ sessionOn 26 9 100.0 "Essay" (Some "CS2004") ]

    Assert.Equal(Some 50, PlannerLogic.coverage tasks sessions)
    Assert.Equal(Some 0, PlannerLogic.coverage tasks [])

[<Fact>]
let ``coverage is absent rather than perfect when nothing is outstanding`` () =
    // A ratio with a zero denominator is not 100%.
    Assert.Equal(None, PlannerLogic.coverage [] [])
    Assert.Equal(None, PlannerLogic.coverage [] [ sessionOn 26 9 50.0 "A" None ])

[<Fact>]
let ``coverage never runs past 100 per cent`` () =
    let tasks = [ task "Essay" 60 None ]
    let sessions = [ for hour in 9..14 -> sessionOn 26 hour 60.0 "Essay" (Some "CS2004") ]

    Assert.Equal(Some 100, PlannerLogic.coverage tasks sessions)

[<Fact>]
let ``deadline risk is derived from the scheduler's own output, never predicted`` () =
    let clean = [ sessionOn 26 9 50.0 "A" None ]

    Assert.Equal(PlannerLogic.NoPlan, PlannerLogic.deadlineRisk [] [])
    Assert.Equal(PlannerLogic.Low, PlannerLogic.deadlineRisk [] clean)
    Assert.Equal(PlannerLogic.High, PlannerLogic.deadlineRisk [ unplanned "Essay" ] clean)

    // The scheduler marks a block it had to squeeze; that is the medium signal.
    let tight =
        [ { sessionOn 26 9 50.0 "A" None with
              Reason = "due in 3h · 4.5h remaining · tight against deadline" } ]

    Assert.Equal(PlannerLogic.Medium, PlannerLogic.deadlineRisk [] tight)

[<Fact>]
let ``an unassessed risk is not inked as a status`` () =
    // Found by looking at the running app: the neutral identity marker is a dot
    // colour, not an ink role, and reading "Not evaluated" in it measured about
    // 2.4:1 on a light card. The view now asks this before choosing ink.
    Assert.False(PlannerLogic.Risk.isAssessed PlannerLogic.NoPlan)

    for risk in [ PlannerLogic.Low; PlannerLogic.Medium; PlannerLogic.High ] do
        Assert.True(PlannerLogic.Risk.isAssessed risk, PlannerLogic.Risk.label risk)

[<Fact>]
let ``every risk level has a distinct word, so colour is never the only signal`` () =
    let labels =
        [ PlannerLogic.NoPlan; PlannerLogic.Low; PlannerLogic.Medium; PlannerLogic.High ]
        |> List.map PlannerLogic.Risk.label

    Assert.Equal(labels.Length, (List.distinct labels).Length)
    Assert.All(labels, fun l -> Assert.False(String.IsNullOrWhiteSpace l))

// ---- plan reasoning ---------------------------------------------------------

[<Fact>]
let ``plan reasoning is empty until there is a plan to explain`` () =
    Assert.Empty(PlannerLogic.reasoningBullets prefs [] [])

[<Fact>]
let ``plan reasoning quotes the scheduler's own words and states its rules`` () =
    let sessions =
        [ sessionOn 26 9 50.0 "Compiler coursework" (Some "CS2004")
          sessionOn 26 11 50.0 "Compiler coursework" (Some "CS2004") ]

    let busy = [ { Start = now; End = now.AddHours 1.0; Label = "Lecture" } ]
    let bullets = PlannerLogic.reasoningBullets prefs busy sessions
    let joined = String.concat " | " bullets

    // The block's own reason, split by `reasonChips`, not authored copy.
    Assert.Contains("Compiler coursework", joined)
    Assert.Contains("due in 3 days", joined)
    Assert.Contains("2 blocks", joined)

    // Rules `Planning.plan` demonstrably applied.
    Assert.Contains("fixed commitment", joined)
    Assert.Contains("study hours", joined)
    Assert.Contains("daily maximum", joined)
    Assert.Contains("before each deadline", joined)

[<Fact>]
let ``plan reasoning stays short enough to read`` () =
    let sessions =
        [ for day in 26..31 -> sessionOn day 9 60.0 (sprintf "Task %d" day) (Some "CS2004") ]

    Assert.True((PlannerLogic.reasoningBullets prefs [] sessions).Length <= 8)

// ---- upcoming outlook -------------------------------------------------------

[<Fact>]
let ``the outlook reports deterministic facts, and says so when there are none`` () =
    let rows = PlannerLogic.outlook now [] [] []
    let byLabel = rows |> List.map (fun r -> r.Label, r.Value) |> dict

    Assert.Equal("None set", byLabel.["Next deadline"])
    Assert.Equal("None", byLabel.["Study time still scheduled"])
    Assert.Equal("None", byLabel.["Outstanding coursework"])
    Assert.Equal("Not evaluated", byLabel.["Deadline risk"])

[<Fact>]
let ``the outlook names the nearest deadline still ahead`` () =
    let tasks =
        [ task "Far" 60 (Some(now.AddDays 20.0))
          task "Near" 60 (Some(now.AddDays 2.0))
          task "Passed" 60 (Some(now.AddDays -5.0)) ]

    let rows = PlannerLogic.outlook now tasks [] []
    let next = rows |> List.find (fun r -> r.Label = "Next deadline")

    Assert.Contains("Near", next.Value)

[<Fact>]
let ``the outlook counts only study time still ahead`` () =
    let past = sessionOn 20 9 60.0 "Done" None
    let ahead = { sessionOn 26 9 60.0 "Ahead" None with StartsAt = now.AddHours 1.0; EndsAt = now.AddHours 2.0 }

    let rows = PlannerLogic.outlook now [] [] [ past; ahead ]
    let scheduled = rows |> List.find (fun r -> r.Label = "Study time still scheduled")

    Assert.Equal("1h", scheduled.Value)

// ---- module identity --------------------------------------------------------

[<Fact>]
let ``a module keeps one colour index across the plan and the workload bar`` () =
    // The old version indexed colours by row position, so the same module was a
    // different colour on every line and the legend disagreed with the dots.
    let sessions =
        [ sessionOn 26 9 60.0 "A" (Some "CS2004")
          sessionOn 26 11 60.0 "B" (Some "MA1512")
          sessionOn 27 9 60.0 "C" (Some "CS2004") ]

    let order = PlannerLogic.moduleOrder sessions

    // The order is the workload order, so bar segment i and module i agree.
    Assert.Equal<string list>(PlannerLogic.workload sessions |> List.map (fun s -> s.Label), order)

    let indexOf code = PlannerLogic.moduleIndexOf order (Some code)
    Assert.Equal(indexOf "CS2004", indexOf "CS2004")
    Assert.NotEqual(indexOf "CS2004", indexOf "MA1512")

    // An unknown or absent module falls back rather than throwing.
    Assert.InRange(PlannerLogic.moduleIndexOf order None, 0, order.Length)

// ---- responsive reflow ------------------------------------------------------

[<Fact>]
let ``the analysis row reflows three to two to one and never goes backwards`` () =
    Assert.Equal(3, PlannerLogic.summaryColumns 1400.0)
    Assert.Equal(2, PlannerLogic.summaryColumns 900.0)
    Assert.Equal(1, PlannerLogic.summaryColumns 600.0)

[<Fact>]
let ``the history and outlook row drops to one column on a narrow page`` () =
    Assert.Equal(2, PlannerLogic.bottomColumns 1400.0)
    Assert.Equal(2, PlannerLogic.bottomColumns 1000.0)
    Assert.Equal(1, PlannerLogic.bottomColumns 700.0)

    [ 400.0..40.0..1800.0 ]
    |> List.map PlannerLogic.bottomColumns
    |> List.pairwise
    |> List.iter (fun (a, b) -> Assert.True(b >= a, "bottom columns went backwards"))

[<Fact>]
let ``strategy pills reflow rather than compressing`` () =
    Assert.Equal(4, PlannerLogic.strategyColumns 1400.0)
    Assert.Equal(2, PlannerLogic.strategyColumns 900.0)
    Assert.Equal(1, PlannerLogic.strategyColumns 600.0)

    [ 400.0..40.0..1800.0 ]
    |> List.map PlannerLogic.strategyColumns
    |> List.pairwise
    |> List.iter (fun (a, b) -> Assert.True(b >= a, "strategy columns went backwards"))

[<Fact>]
let ``every layout decision has settled by the time the page is wide`` () =
    // The resize handler only dispatches when one of these four changes, so
    // they are the complete set of width-dependent decisions on the page.
    let decisions width =
        PlannerLogic.showsSideColumn width,
        PlannerLogic.summaryColumns width,
        PlannerLogic.bottomColumns width,
        PlannerLogic.strategyColumns width

    Assert.Equal(decisions 1400.0, decisions 1800.0)
    Assert.NotEqual(decisions 700.0, decisions 1400.0)

// ---- surfaces and accent ----------------------------------------------------

let private accentKeys = [ "amber"; "blue"; "purple"; "teal"; "pink"; "gold" ]

let private channelDistance (a: Avalonia.Media.Color) (b: Avalonia.Media.Color) =
    [ abs (int a.R - int b.R); abs (int a.G - int b.G); abs (int a.B - int b.B) ] |> List.max

[<Fact>]
let ``the Planner's card material does not move when the accent changes`` () =
    // The Planner draws on the shared material `Card` role. Changing accent must
    // move the details, not the surfaces.
    for mode in [ ThemeMode.Dark; ThemeMode.Light ] do
        let cards = accentKeys |> List.map (fun k -> (Surfaces.resolve mode (Palette.ofKey k)).Card)

        for card in cards do
            Assert.True(
                channelDistance cards.Head card <= 12,
                sprintf "%A card shifted by %d across accents" mode (channelDistance cards.Head card)
            )

[<Fact>]
let ``the accent itself does change, so details really are accent-driven`` () =
    let normals = accentKeys |> List.map (fun k -> (Palette.ofKey k).Normal)
    Assert.Equal(normals.Length, (List.distinct normals).Length)

[<Fact>]
let ``Planner cards draw no specular highlight, exactly like the Dashboard's`` () =
    // The broad white top sheen is a named regression. The Planner uses the
    // Dashboard's card geometry, so this is the value it passes to GlassPanel.
    Assert.Equal(0.0, DashboardLogic.Card.highlightScale)

// =============================================================================
// The Dark-theme fidelity pass.
//
// Six defects, each with a dimension or a token behind it. The values these
// assert are the ones the view reads, so a regression has to change a constant
// a test names rather than a number buried in a layout expression.
// =============================================================================

// ---- the glass action row ---------------------------------------------------

[<Fact>]
let ``the action row is a glass treatment, not Fluent's own button fill`` () =
    // Root cause of the flat grey blocks: Fluent paints a Button's background on
    // PART_ContentPresenter inside the control template, and its :disabled,
    // :pointerover and :pressed styles set that presenter's background directly
    // — which beats the Button's own Background property. Five of the six
    // actions are disabled with no plan, so five grey blocks appeared through
    // the glass. `App.Initialize` neutralises the presenter for this class only.
    let className = Ui.glassActionClass ()

    Assert.False(String.IsNullOrWhiteSpace className)
    // Stable: the style in Program.fs and the buttons in Planner.fs must agree,
    // and they agree only by both reading this.
    Assert.Equal(className, Ui.glassActionClass ())

[<Fact>]
let ``a glass action button keeps its hover and pressed feedback`` () =
    // The regression this guards: the scoped style once neutralised the Fluent
    // presenter fill in EVERY state, including :pointerover and :pressed — so
    // these buttons alone had no hover, while every other button in the app did.
    // Only resting and :disabled may be neutralised.
    let neutralised = Ui.glassActionNeutralisedStates ()

    // Only :disabled. The earlier version of this test asserted that "" belonged
    // here too, which was wrong and is what kept the bug alive: a selector with
    // no pseudo-class matches in EVERY state, so an unconditional entry silently
    // overrides Fluent's :pointerover fill however carefully the explicit hover
    // entries are omitted.
    Assert.Equal<string list>([ ":disabled" ], neutralised)

    Assert.DoesNotContain("", neutralised)
    Assert.DoesNotContain(":pointerover", neutralised)
    Assert.DoesNotContain(":pressed", neutralised)

[<Fact>]
let ``the hover and pressed fills behind the glass are warm material, not Fluent grey`` () =
    // What the hover actually paints. Both keys must resolve to real material
    // roles, and both must be warm — a cool grey lift behind warm glass is the
    // defect the whole material override exists to prevent.
    for mode in [ ThemeMode.Dark; ThemeMode.Light ] do
        let s = Surfaces.baseFor mode

        let hover = FluentMaterial.resolve s "ButtonBackgroundPointerOver"
        let pressed = FluentMaterial.resolve s "ButtonBackgroundPressed"

        Assert.Equal(s.Hover, hover)
        Assert.Equal(s.Selected, pressed)

        for name, c in [ "hover", hover; "pressed", pressed ] do
            Assert.True(int c.R > int c.B, sprintf "%A %s fill is cold (R %d, B %d)" mode name c.R c.B)

        // And each is a visible step from the resting action surface, or the
        // hover would be an animation with nothing to show.
        Assert.True(channelDistance hover s.Action >= 4, sprintf "%A hover is not a visible lift" mode)

[<Fact>]
let ``the corner correction is the same one on both screens`` () =
    // Half of the 1px hairline stroke. The Planner's action row and the
    // Dashboard's quick actions take the identical correction — one defect, one
    // number, not two drifting local fixes.
    Assert.Equal(0.5, PlannerLogic.Geometry.borderInset)
    Assert.Equal(DashboardLogic.Card.borderInset, PlannerLogic.Geometry.borderInset)

[<Fact>]
let ``an action button reserves the same box in every state`` () =
    // Primary, secondary, hovered, pressed, focused and disabled all render the
    // same geometry. Only tint and ink move, so no state change can reflow the
    // row or shift the buttons beside it.
    Assert.True(PlannerLogic.Geometry.actionMinHeight > 0.0)
    Assert.True(PlannerLogic.Geometry.actionRadius > 0.0)

    // One height for the whole row, not one per state.
    let heights =
        PlannerLogic.actions (context true true true PlannerLogic.LowStress)
        |> List.map (fun _ -> PlannerLogic.Geometry.actionMinHeight)

    Assert.Equal(1, (List.distinct heights).Length)

[<Fact>]
let ``a disabled action keeps its label and its box, and only dims`` () =
    // Disabled must read as unavailable without changing shape: same label text,
    // same reserved height, and the reason stated separately in visible text.
    let enabled =
        PlannerLogic.actions (context true true true PlannerLogic.LowStress)
        |> List.find (fun a -> a.Id = PlannerLogic.BalanceWorkload)

    let disabled =
        PlannerLogic.actions (context true false true PlannerLogic.LowStress)
        |> List.find (fun a -> a.Id = PlannerLogic.BalanceWorkload)

    Assert.Equal<string>(enabled.Label, disabled.Label)
    Assert.True(enabled.Enabled)
    Assert.False(disabled.Enabled)
    Assert.True(disabled.DisabledBecause.IsSome)

[<Fact>]
let ``secondary actions sit on the warm material action role, never a grey`` () =
    // The secondary tint is the material Action role. It must stay warm and
    // near-neutral under every accent — a blue-grey or a flat grey here is the
    // regression this pass removed.
    for mode in [ ThemeMode.Dark; ThemeMode.Light ] do
        for key in accentKeys do
            let action = (Surfaces.resolve mode (Palette.ofKey key)).Action

            Assert.True(
                int action.R > int action.B,
                sprintf "%A Action under %s is not warm (R %d, B %d)" mode key action.R action.B
            )

    // And the primary tint really is the accent, so it differs per preset.
    let primaries = accentKeys |> List.map (fun k -> (Palette.ofKey k).Normal)
    Assert.Equal(primaries.Length, (List.distinct primaries).Length)

// ---- Today's Study Plan stability -------------------------------------------

[<Fact>]
let ``a session row reserves space for everything that can appear later`` () =
    // The row jumped because parts of it were rendered only once they had
    // something to show: the progress bar appeared the moment a session started,
    // and the status pill changed width as the word changed. Each now has a
    // reserved box.
    Assert.True(PlannerLogic.Geometry.progressStripHeight > 0.0)
    Assert.True(PlannerLogic.Geometry.statusMinWidth > 0.0)
    Assert.True(PlannerLogic.Geometry.timeMinWidth > 0.0)
    Assert.True(PlannerLogic.Geometry.durationMinWidth > 0.0)
    Assert.True(PlannerLogic.Geometry.sessionRowMinHeight > 0.0)

[<Fact>]
let ``the reserved status column fits the longest status word`` () =
    // "IN PROGRESS" is the widest. If the reservation were narrower than the
    // label it would stop reserving anything and the pill would push the module
    // code along as the clock moved.
    let longest =
        [ PlannerLogic.Done; PlannerLogic.InProgress; PlannerLogic.UpNext; PlannerLogic.Scheduled ]
        |> List.map (PlannerLogic.SessionStatus.label >> String.length)
        |> List.max

    // ~7px per uppercase character at rowMeta, plus the pill's own padding.
    Assert.True(
        PlannerLogic.Geometry.statusMinWidth >= float longest * 7.0,
        sprintf "%f is too narrow for a %d-character status" PlannerLogic.Geometry.statusMinWidth longest
    )

[<Fact>]
let ``every row on a frame reads the same clock`` () =
    // Each row used to call DateTime.Now for itself, so two rows could disagree
    // about the same instant and a re-render could flip them back. The view
    // takes one reading and threads it, which is what makes this assertable at
    // all: the same instant gives the same status, every time.
    let running =
        { sessionOn 26 13 0.0 "Running" None with
            StartsAt = now.AddMinutes -10.0
            EndsAt = now.AddMinutes 40.0 }

    let sessions = [ running; sessionOn 26 17 50.0 "Later" None ]

    let first = sessions |> List.map (PlannerLogic.statusOf now)
    let second = sessions |> List.map (PlannerLogic.statusOf now)

    Assert.Equal<PlannerLogic.SessionStatus list>(first, second)

[<Fact>]
let ``elapsed share is stable for a fixed instant, so the bar cannot flicker`` () =
    let halfway =
        { sessionOn 26 13 0.0 "Halfway" None with
            StartsAt = now.AddMinutes -30.0
            EndsAt = now.AddMinutes 30.0 }

    Assert.Equal(PlannerLogic.elapsedPercent now halfway, PlannerLogic.elapsedPercent now halfway)

[<Fact>]
let ``a refresh that changes nothing produces an identical row model`` () =
    // Reloading the same sessions must not reshape anything: same order, same
    // identity, same derived values. This is what makes the repeated background
    // reload invisible instead of a reflow.
    let sessions = [ sessionOn 26 9 50.0 "A" (Some "CS2004"); sessionOn 26 11 50.0 "B" (Some "MA1512") ]
    let before = loaded sessions

    let after =
        Planner.update noServices (Planner.Loaded(inputWith prefs [], sessions, [])) before
        |> modelOf

    Assert.Equal<Guid list>(
        before.Sessions |> List.map (fun s -> s.Id.Value),
        after.Sessions |> List.map (fun s -> s.Id.Value)
    )

    Assert.Equal<string list>(PlannerLogic.moduleOrder before.Sessions, PlannerLogic.moduleOrder after.Sessions)

[<Fact>]
let ``focusing a row does not change what the row contains`` () =
    // Focus is a border colour on an already-reserved border, never an extra
    // one, so focusing cannot change a row's height.
    let sessions = [ sessionOn 26 9 50.0 "A" None ]
    let before = loaded sessions
    let after = Planner.update noServices Planner.FocusCurrent before |> modelOf

    Assert.Equal<Guid list>(
        before.Sessions |> List.map (fun s -> s.Id.Value),
        after.Sessions |> List.map (fun s -> s.Id.Value)
    )

// ---- constraint wrapping ----------------------------------------------------

[<Fact>]
let ``a glyph-and-text row gives the text a real width to wrap in`` () =
    // The overflow fix. A horizontal StackPanel measures with infinite width, so
    // TextWrapping never engages and long text runs outside the card. The star
    // column is what gives the text a finite width.
    let columns = PlannerLogic.Geometry.bulletColumns

    Assert.EndsWith("*", columns)
    Assert.StartsWith("Auto", columns)
    // Exactly one flexible column, and it is the text.
    Assert.Equal(1, columns.Split(',') |> Array.filter (fun c -> c.Trim().EndsWith "*") |> Array.length)

[<Fact>]
let ``the longest real constraint message is long enough to need wrapping`` () =
    // Not a hypothetical: these are the actual messages, and the worst of them
    // is a busiest-day warning naming two durations. If the longest were short
    // the wrapping fix would be untested by real data.
    let strict =
        PlannerPreferences.ofSettings { UserSettings.defaults with MaxDailyStudyHours = 1.0 }

    let overloaded = [ for hour in 9..15 -> sessionOn 26 hour 60.0 (sprintf "Block %d" hour) None ]

    let longest =
        PlannerLogic.checks strict [ unplanned "Essay" ] overloaded
        |> List.map (fun c -> c.Text.Length)
        |> List.max

    Assert.True(longest > 40, sprintf "longest constraint message is only %d characters" longest)

[<Fact>]
let ``constraint text is never truncated or abbreviated to fit`` () =
    // Wrapping, not trimming: the message must arrive whole.
    let checks = PlannerLogic.checks prefs [] [ sessionOn 26 9 50.0 "A" None ]

    Assert.All(
        checks,
        fun c ->
            Assert.False(c.Text.EndsWith "…")
            Assert.False(c.Text.EndsWith "...")
    )

// ---- card sheen -------------------------------------------------------------

[<Fact>]
let ``every Planner content card draws no specular highlight`` () =
    // Zero, not small: the gradient's reach is 38% of the panel whatever its
    // alpha, so any non-zero value is a pale band across the top third rather
    // than a faint edge. `drawsHighlight` must skip the pass entirely.
    Assert.Equal(0.0, PlannerLogic.Geometry.contentHighlight)
    Assert.False(Glass.drawsHighlight PlannerLogic.Geometry.contentHighlight)

    // The Planner uses the Dashboard's approved value, not one of its own.
    Assert.Equal(DashboardLogic.Card.highlightScale, PlannerLogic.Geometry.contentHighlight)

[<Fact>]
let ``the no-sheen value holds at every glass setting`` () =
    // The highlight alpha is the glass scale times the card's scale. At zero the
    // product is zero for every slider position, so no setting can bring the
    // band back.
    for slider in [ 0.0; 0.5; 1.0; 1.5; 2.0 ] do
        let alpha = Glass.highlightAlpha slider * PlannerLogic.Geometry.contentHighlight

        Assert.False(
            Glass.drawsHighlight alpha,
            sprintf "glass %f produced a drawn highlight on a content card" slider
        )

[<Fact>]
let ``every Planner card goes through one content-card treatment`` () =
    // There is a single `card` helper in the view and a single `renderCard`
    // match, so no card can acquire a surface, highlight, border or tint of its
    // own — including the two bottom ones. What the test can pin from here is
    // that the set is complete and that the treatment is the approved one.
    Assert.Equal(9, PlannerLogic.cardOrder.Length)
    Assert.Equal(DashboardLogic.Card.highlightScale, PlannerLogic.Geometry.contentHighlight)
    Assert.Contains(PlannerLogic.PlannerHistory, PlannerLogic.cardOrder)
    Assert.Contains(PlannerLogic.FuturePrediction, PlannerLogic.cardOrder)

[<Fact>]
let ``the floating sidebar keeps its highlight`` () =
    // The approved floating-glass character. One tall panel reads as a lit pane;
    // that is deliberate and is not what the card fix removes.
    Assert.Equal(1.0, PlannerLogic.Geometry.sidebarHighlight)
    Assert.True(Glass.drawsHighlight(Glass.highlightAlpha 1.0 * PlannerLogic.Geometry.sidebarHighlight))

// ---- alternative plans geometry ---------------------------------------------

[<Fact>]
let ``every strategy control reserves the same height in both states`` () =
    // The selected control used to be taller because the SELECTED badge and the
    // preview metrics line were rendered only when present. Both now have a
    // reserved slot, and the border is reserved in both states too.
    Assert.True(PlannerLogic.Geometry.strategyMinHeight > 0.0)
    Assert.True(PlannerLogic.Geometry.strategyBadgeHeight > 0.0)
    Assert.True(PlannerLogic.Geometry.strategyMetricsHeight > 0.0)
    Assert.True(PlannerLogic.Geometry.strategyBorderThickness > 0.0)

    // The reserved slots must fit inside the control they sit in.
    let reserved =
        PlannerLogic.Geometry.strategyBadgeHeight + PlannerLogic.Geometry.strategyMetricsHeight

    Assert.True(reserved < PlannerLogic.Geometry.strategyMinHeight)

[<Fact>]
let ``selecting a different strategy cannot move the row`` () =
    // Every strategy resolves to the same reserved geometry, so which one is
    // selected changes colour only.
    let heights =
        PlannerLogic.Strategy.supported
        |> List.map (fun _ ->
            PlannerLogic.Geometry.strategyMinHeight,
            PlannerLogic.Geometry.strategyBadgeHeight,
            PlannerLogic.Geometry.strategyMetricsHeight,
            PlannerLogic.Geometry.strategyBorderThickness)

    Assert.Equal(1, (List.distinct heights).Length)

[<Fact>]
let ``previewing a strategy does not resize its control`` () =
    // The metrics line has a reserved slot whether or not a preview exists, so
    // running a preview fills the slot rather than growing the control.
    let before = loaded []
    let after = { before with Alternatives = [ alternative PlannerLogic.Balanced ] }

    Assert.NotEqual<int>(before.Alternatives.Length, after.Alternatives.Length)
    Assert.Equal(PlannerLogic.Geometry.strategyMetricsHeight, PlannerLogic.Geometry.strategyMetricsHeight)

// ---- the wide row layout ----------------------------------------------------

[<Fact>]
let ``the main row splits roughly two thirds to one third`` () =
    // The redesign's proportion. Star weights, so the ratio holds at any width.
    let left = PlannerLogic.Geometry.mainLeftWeight
    let right = PlannerLogic.Geometry.mainRightWeight
    let share = left / (left + right) * 100.0

    Assert.InRange(share, 64.0, 66.0)
    Assert.InRange(100.0 - share, 34.0, 36.0)
    Assert.True(left > right, "Today's Study Plan must remain the dominant card")

[<Fact>]
let ``each shared row is one row of equal cells`` () =
    // Row 2 is three cards in three columns, row 4 is two in two. Equal counts
    // are what make `1*` columns equal widths, and a single Grid row is what
    // makes the cards' top and bottom edges align.
    Assert.Equal(3, PlannerLogic.analysisRow.Length)
    Assert.Equal(3, PlannerLogic.summaryColumns 1400.0)

    Assert.Equal(2, PlannerLogic.bottomRow.Length)
    Assert.Equal(2, PlannerLogic.bottomColumns 1400.0)

    // Row 3 is one card across the whole content width.
    Assert.Equal(1, PlannerLogic.fullWidthRow.Length)

[<Fact>]
let ``a shared row wraps into whole lines, never a ragged remainder`` () =
    // At every width the row reflows to, the column count divides the row into
    // lines that are themselves single Grids — the medium-width "two columns
    // plus one full-width card" is deliberate, not a leftover.
    for width in [ 600.0; 900.0; 1400.0 ] do
        let columns = PlannerLogic.summaryColumns width
        Assert.InRange(columns, 1, PlannerLogic.analysisRow.Length)

        for columns in [ PlannerLogic.bottomColumns width ] do
            Assert.InRange(columns, 1, PlannerLogic.bottomRow.Length)

[<Fact>]
let ``every row carries a shared floor, so sparse cards keep the proportions`` () =
    // The floor is on the ROW, never on a card: a per-card floor would let one
    // card in a row be taller than another for no reason.
    Assert.True(PlannerLogic.Geometry.mainRowMinHeight > 0.0)
    Assert.True(PlannerLogic.Geometry.analysisRowMinHeight > 0.0)
    Assert.True(PlannerLogic.Geometry.bottomRowMinHeight > 0.0)

    // The main row is the tallest — Today's Study Plan stays dominant even with
    // nothing scheduled.
    Assert.True(PlannerLogic.Geometry.mainRowMinHeight > PlannerLogic.Geometry.analysisRowMinHeight)
    Assert.True(PlannerLogic.Geometry.mainRowMinHeight > PlannerLogic.Geometry.bottomRowMinHeight)

[<Fact>]
let ``the empty page keeps every structural row`` () =
    // Sparse data must not collapse the layout: the same four rows, the same
    // nine cards, the same floors.
    let model = loaded []

    Assert.Empty model.Sessions
    Assert.Equal(9, PlannerLogic.cardOrder.Length)
    Assert.Equal(3, PlannerLogic.summaryColumns model.Width)
    Assert.Equal(2, PlannerLogic.bottomColumns model.Width)
    Assert.True(PlannerLogic.Geometry.mainRowMinHeight > 0.0)

[<Fact>]
let ``the card grid is not constrained to prose reading width`` () =
    // Reading width is a rule for prose. The header uses it; the card grid must
    // not, or a dashboard-shaped page would sit in a column on a wide screen.
    // The widest reading width is far narrower than the layout the grid targets.
    let widest = ReadingWidth.all |> List.map Tokens.readingWidthPx |> List.max

    Assert.True(
        widest < 1100.0,
        sprintf "reading width %f would swallow the side-column threshold" widest
    )

    // And the side column appears at a width the reading rule would never allow.
    Assert.True(PlannerLogic.showsSideColumn 1400.0)

// ---- dark theme -------------------------------------------------------------

[<Fact>]
let ``the Dark Planner sits on warm-neutral material, under every accent`` () =
    // The approval target for this pass. Cards, the action role and the sidebar
    // must all stay warm and near-neutral whichever accent is selected.
    for key in accentKeys do
        let s = Surfaces.resolve ThemeMode.Dark (Palette.ofKey key)

        for name, colour in
            [ "Card", s.Card; "Action", s.Action; "CardAlt", s.CardAlt; "Selected", s.Selected ] do
            Assert.True(int colour.R >= int colour.B, sprintf "Dark %s under %s is not warm" name key)

            Assert.True(
                channelDistance colour (Surfaces.baseFor ThemeMode.Dark).Card <= 40,
                sprintf "Dark %s under %s has drifted from the material family" name key
            )

[<Fact>]
let ``Dark Planner surfaces do not move with the accent`` () =
    let cards = accentKeys |> List.map (fun k -> (Surfaces.resolve ThemeMode.Dark (Palette.ofKey k)).Card)
    let actions = accentKeys |> List.map (fun k -> (Surfaces.resolve ThemeMode.Dark (Palette.ofKey k)).Action)

    for c in cards do
        Assert.True(channelDistance cards.Head c <= 12, "the Dark card moved with the accent")

    for a in actions do
        Assert.True(channelDistance actions.Head a <= 12, "the Dark action surface moved with the accent")

[<Fact>]
let ``Dark Planner ink keeps a readable hierarchy`` () =
    // Four distinct steps, each lighter than the one below it, so the action
    // labels, the metric values and the disabled explanations remain separable.
    let s = Surfaces.baseFor ThemeMode.Dark
    let lum (c: Avalonia.Media.Color) = 0.2126 * float c.R + 0.7152 * float c.G + 0.0722 * float c.B

    let steps = [ "Text", s.Text; "TextSecondary", s.TextSecondary; "Muted", s.Muted; "Faint", s.Faint ]

    steps
    |> List.pairwise
    |> List.iter (fun ((aName, a), (bName, b)) ->
        Assert.True(lum a > lum b, sprintf "%s must be lighter than %s in Dark" aName bName))

    // And the dimmest of them still clears the card by a readable margin.
    Assert.True(lum s.Faint - lum s.Card > 40.0, "Faint is too close to the card in Dark")

// ---- the Light theme ---------------------------------------------------------
//
// The Dark Planner is approved and frozen. Everything here states that Light is
// the SAME screen — same structure, same geometry, same treatment — differing
// only in the palette it resolves against.

[<Fact>]
let ``the Light Planner is the same layout as the approved Dark one`` () =
    // Nothing in the Planner's geometry or composition takes a theme, so the
    // approved arrangement cannot drift between modes. This states it, so a
    // future theme-conditional layout fails here rather than in review.
    Assert.Equal(9, PlannerLogic.cardOrder.Length)
    Assert.Equal(3, PlannerLogic.analysisRow.Length)
    Assert.Equal(2, PlannerLogic.bottomRow.Length)
    Assert.Equal(1, PlannerLogic.fullWidthRow.Length)

    // The same breakpoints and the same weights in both themes.
    Assert.Equal(65.0, PlannerLogic.Geometry.mainLeftWeight)
    Assert.Equal(35.0, PlannerLogic.Geometry.mainRightWeight)
    Assert.True(PlannerLogic.showsSideColumn 1400.0)
    Assert.Equal(3, PlannerLogic.summaryColumns 1400.0)
    Assert.Equal(2, PlannerLogic.bottomColumns 1400.0)

[<Fact>]
let ``the Light Planner keeps the no-sheen card treatment`` () =
    // The white top section is a light-theme hazard too — a white specular on a
    // near-white card would be a glare rather than a band. Zero in both themes.
    Assert.Equal(0.0, PlannerLogic.Geometry.contentHighlight)
    Assert.False(Glass.drawsHighlight PlannerLogic.Geometry.contentHighlight)

[<Fact>]
let ``Light Planner controls reserve exactly the same box as Dark`` () =
    // Glass action buttons and strategy pills are geometry, not palette. A
    // theme change must not move a single edge.
    Assert.True(PlannerLogic.Geometry.actionMinHeight > 0.0)
    Assert.True(PlannerLogic.Geometry.strategyMinHeight > 0.0)
    Assert.True(PlannerLogic.Geometry.strategyBorderThickness > 0.0)
    Assert.True(PlannerLogic.Geometry.statusMinWidth > 0.0)
    Assert.True(PlannerLogic.Geometry.progressStripHeight > 0.0)

[<Fact>]
let ``the Light Planner sits on warm-neutral material, never cold or oversaturated`` () =
    // The two failure modes named for this pass: a cold blue-grey cast, and a
    // yellow or beige over-saturation. Both are measured, on every accent.
    for key in accentKeys do
        let s = Surfaces.resolve ThemeMode.Light (Palette.ofKey key)

        for name, colour in
            [ "Card", s.Card; "Action", s.Action; "CardAlt", s.CardAlt
              "Window", s.Window; "Selected", s.Selected ] do
            Assert.True(
                int colour.R > int colour.B,
                sprintf "Light %s under %s is cold (R %d, B %d)" name key colour.R colour.B
            )

            let chroma =
                List.max [ int colour.R; int colour.G; int colour.B ]
                - List.min [ int colour.R; int colour.G; int colour.B ]

            Assert.True(chroma <= 26, sprintf "Light %s under %s has chroma %d — a beige wash" name key chroma)

[<Fact>]
let ``Light Planner cards are distinct from the page without being pure white`` () =
    // A card must read as a card: separated from the page beneath it, and not a
    // sheet of paper-white glare.
    for key in accentKeys do
        let s = Surfaces.resolve ThemeMode.Light (Palette.ofKey key)

        Assert.True(
            channelDistance s.Card s.Window >= 8,
            sprintf "Light card and window under %s differ by only %d" key (channelDistance s.Card s.Window)
        )

        Assert.True(int s.Card.R < 255 && int s.Card.G < 255 && int s.Card.B < 255, "the light card must not be pure white")

[<Fact>]
let ``the accent moves Light details without moving Light surfaces`` () =
    // Same claim as Dark, restated for Light: details follow the swatch, the
    // material does not.
    let cards = accentKeys |> List.map (fun k -> (Surfaces.resolve ThemeMode.Light (Palette.ofKey k)).Card)

    for c in cards do
        Assert.True(channelDistance cards.Head c <= 12, "the Light card moved with the accent")

    let normals = accentKeys |> List.map (fun k -> (Palette.ofKey k).Normal)
    Assert.Equal(normals.Length, (List.distinct normals).Length)

[<Fact>]
let ``Light status wording carries the meaning, not the colour alone`` () =
    // Semantic colour is the weaker channel on a light surface, so the words
    // have to stand on their own. Both risk and session status are already
    // distinct words; this pins that they stay so.
    let risks =
        [ PlannerLogic.NoPlan; PlannerLogic.Low; PlannerLogic.Medium; PlannerLogic.High ]
        |> List.map PlannerLogic.Risk.label

    let statuses =
        [ PlannerLogic.Done; PlannerLogic.InProgress; PlannerLogic.UpNext; PlannerLogic.Scheduled ]
        |> List.map PlannerLogic.SessionStatus.label

    Assert.Equal(risks.Length, (List.distinct risks).Length)
    Assert.Equal(statuses.Length, (List.distinct statuses).Length)
    Assert.All(risks @ statuses, fun l -> Assert.False(String.IsNullOrWhiteSpace l))

[<Fact>]
let ``Light empty-state text does not fade into its card`` () =
    // Empty states lean on Muted and Faint. Both must stay clearly legible on
    // the light card, or an empty Planner reads as a broken one.
    let lum (c: Avalonia.Media.Color) =
        let ch (v: byte) =
            let s = float v / 255.0
            if s <= 0.04045 then s / 12.92 else ((s + 0.055) / 1.055) ** 2.4

        0.2126 * ch c.R + 0.7152 * ch c.G + 0.0722 * ch c.B

    let ratio (a: Avalonia.Media.Color) (b: Avalonia.Media.Color) =
        (max (lum a) (lum b) + 0.05) / (min (lum a) (lum b) + 0.05)

    let s = Surfaces.baseFor ThemeMode.Light

    for name, ink in [ "Muted", s.Muted; "Faint", s.Faint; "Label", s.Label ] do
        Assert.True(
            ratio ink s.Card >= 4.5,
            sprintf "Light %s on the card is %.2f:1 — empty-state text would fade" name (ratio ink s.Card)
        )

[<Fact>]
let ``the Planner hides its scrollbar without disabling scrolling`` () =
    // `Hidden` and `Disabled` look identical in a screenshot and are opposites
    // in behaviour. The Planner scrolls through `Ui.scrollNoChrome`, which uses
    // this shared value.
    Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden, Ui.pageScrollBarVisibility)
    Assert.NotEqual(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Ui.pageScrollBarVisibility)
