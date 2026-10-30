module UniversityOS.Tests.PlannerPresentationTests

open System
open Xunit
open UniversityOS.Domain
open UniversityOS.App.Features

/// Planner presentation corrections from the first runtime review.
///
/// Three separate findings, all in `PlannerLogic` so they are decidable without
/// rendering anything.

let private now = DateTime(2026, 3, 4, 12, 0, 0)

let private session (id: int) (startHour: float) (endHour: float) (completed: bool) =
    let at (h: float) = now.Date.AddHours h

    { Id = SessionId(Guid(sprintf "00000000-0000-0000-0000-%012d" id))
      AssignmentId = None
      MilestoneId = None
      Title = sprintf "Session %d" id
      ModuleCode = Some "COMP3811"
      StartsAt = at startHour
      EndsAt = at endHour
      Reason = "Nearest deadline"
      Completed = completed
      CreatedAt = at 0.0 }

// ---------------------------------------------------------------------------
// 1. Missed is not Done
// ---------------------------------------------------------------------------

/// **The correctness finding.** A session that ended without being ticked off
/// used to report as `Done`, which credited the user with work they had not
/// done and made the two states indistinguishable on screen. Only `Completed`
/// means done.
[<Fact>]
let ``a session that ended un-ticked is Missed, not Done`` () =
    let ended = session 1 9.0 10.0 false
    Assert.Equal(PlannerLogic.Missed, PlannerLogic.statusOf now ended)

[<Fact>]
let ``a session that ended and was ticked off is Done`` () =
    let ended = session 2 9.0 10.0 true
    Assert.Equal(PlannerLogic.Done, PlannerLogic.statusOf now ended)

/// Completion is what decides Done — a completed session stays Done even before
/// its end time, so the clock never overrides the user's own record.
[<Fact>]
let ``completion beats the clock in both directions`` () =
    Assert.Equal(PlannerLogic.Done, PlannerLogic.statusOf now (session 3 14.0 15.0 true))
    Assert.Equal(PlannerLogic.Scheduled, PlannerLogic.statusOf now (session 4 20.0 21.0 false))

[<Fact>]
let ``a running session is In progress`` () =
    Assert.Equal(PlannerLogic.InProgress, PlannerLogic.statusOf now (session 5 11.5 12.5 false))

/// Every state says a word, so status is never colour alone.
[<Fact>]
let ``every session status has a distinct label`` () =
    let labels = PlannerLogic.SessionStatus.all |> List.map PlannerLogic.SessionStatus.label

    Assert.All(labels, fun l -> Assert.False(String.IsNullOrWhiteSpace l))
    Assert.Equal(labels.Length, (List.distinct labels).Length)
    Assert.Contains("MISSED", labels)

// ---------------------------------------------------------------------------
// 2. effort says what it is measuring
// ---------------------------------------------------------------------------

/// A bare "Light" beside a bare "High" told the reader nothing about which scale
/// either belonged to.
[<Fact>]
let ``effort labels name the quantity they describe`` () =
    Assert.Equal<string>("Low effort", PlannerLogic.Effort.label PlannerLogic.Light)
    Assert.Equal<string>("Medium effort", PlannerLogic.Effort.label PlannerLogic.Moderate)
    Assert.Equal<string>("High effort", PlannerLogic.Effort.label PlannerLogic.Deep)

/// The wording is presentation over a real derivation — block length — so the
/// mapping from duration must still hold.
[<Fact>]
let ``effort is still derived from the length of the block`` () =
    Assert.Equal(PlannerLogic.Light, PlannerLogic.effortOf (session 6 9.0 9.5 false))
    Assert.Equal(PlannerLogic.Moderate, PlannerLogic.effortOf (session 7 9.0 10.0 false))
    Assert.Equal(PlannerLogic.Deep, PlannerLogic.effortOf (session 8 9.0 11.0 false))

// ---------------------------------------------------------------------------
// 3. today's preview is bounded, today-only and chronological
// ---------------------------------------------------------------------------

let private manyToday count =
    [ for i in 1..count -> session i (7.0 + float i) (7.5 + float i) false ]

[<Fact>]
let ``the preview shows at most seven sessions`` () =
    let preview = PlannerLogic.todayPreview now (manyToday 12)

    Assert.Equal(PlannerLogic.todayPreviewLimit, preview.Length)
    Assert.Equal(7, PlannerLogic.todayPreviewLimit)

[<Fact>]
let ``a short day is shown whole, with nothing hidden`` () =
    let sessions = manyToday 4

    Assert.Equal(4, (PlannerLogic.todayPreview now sessions).Length)
    Assert.Equal(0, PlannerLogic.todayHiddenCount now sessions)

[<Fact>]
let ``the hidden count is what the preview leaves out`` () =
    Assert.Equal(5, PlannerLogic.todayHiddenCount now (manyToday 12))

/// The cap keeps the EARLIEST sessions, in time order — a preview that showed an
/// arbitrary seven, or the last seven, would misrepresent the day.
[<Fact>]
let ``the preview keeps the earliest sessions in chronological order`` () =
    let shuffled = manyToday 10 |> List.rev
    let preview = PlannerLogic.todayPreview now shuffled

    let starts = preview |> List.map (fun s -> s.StartsAt)
    Assert.Equal<DateTime list>(List.sort starts, starts)
    Assert.Equal(now.Date.AddHours 8.0, starts.Head)

/// **Today only.** Yesterday's and tomorrow's sessions must never appear in a
/// card that says "today".
[<Fact>]
let ``the preview excludes other days entirely`` () =
    let yesterday = { session 100 9.0 10.0 false with StartsAt = now.Date.AddDays(-1.0).AddHours 9.0 }
    let tomorrow = { session 101 9.0 10.0 false with StartsAt = now.Date.AddDays(1.0).AddHours 9.0 }

    let preview = PlannerLogic.todayPreview now (yesterday :: tomorrow :: manyToday 3)

    Assert.Equal(3, preview.Length)
    Assert.All(preview, fun s -> Assert.Equal(now.Date, s.StartsAt.Date))

// ---------------------------------------------------------------------------
// 4. the view: preview, trigger and full-day panel
// ---------------------------------------------------------------------------

open Avalonia.FuncUI.Types
open UniversityOS.Application
open UniversityOS.App
open UniversityOS.Tests.AuditHarness

let private noServices: AppServices = Unchecked.defaultof<AppServices>

/// **The view reads the real clock.** `Planner.view` takes one `DateTime.Now`
/// reading per frame, so a fixture pinned to a frozen date is not "today" as far
/// as the view is concerned and every row silently disappears. View fixtures are
/// therefore built around the actual current date; the logic tests above keep
/// their frozen instant, where it is the input rather than an ambient fact.
let private viewSession (id: int) (startHour: float) =
    let today = DateTime.Now.Date

    { session id startHour (startHour + 0.5) false with
        StartsAt = today.AddHours startHour
        EndsAt = today.AddHours(startHour + 0.5) }

let private manyViewToday count =
    [ for i in 1..count -> viewSession i (7.0 + float i) ]

/// A populated Planner. **Never an empty fixture** — a test that scans a screen
/// with no session rows on it cannot fail for the reason it claims to.
let private plannerWith (sessions: PlannedSession list) =
    let model, _ = Planner.init noServices
    { model with Sessions = sessions; Loading = false; Width = 1400.0 }

let private renderWith sessions =
    Planner.view AuditFixtures.tokens (plannerWith sessions) ignore

let private saysAnywhere (fragment: string) (tree: IView) =
    everyString tree |> List.exists (fun s -> s.Contains(fragment, StringComparison.OrdinalIgnoreCase))

/// The trigger's own words, so the assertions cannot drift from the control.
let private viewAllFragment = "View all today's sessions"

[<Fact>]
let ``seven sessions show no view-all trigger`` () =
    Assert.False(saysAnywhere viewAllFragment (renderWith (manyViewToday 7)))

[<Fact>]
let ``eight sessions show the view-all trigger with the real hidden count`` () =
    let tree = renderWith (manyViewToday 8)

    Assert.True(saysAnywhere viewAllFragment tree, "no view-all trigger with eight sessions")
    Assert.True(saysAnywhere "(1 more)" tree, "the trigger does not state the real hidden count")

[<Fact>]
let ``an empty day shows neither a trigger nor a hidden count`` () =
    let tree = renderWith []

    Assert.False(saysAnywhere viewAllFragment tree)
    Assert.False(saysAnywhere "more)" tree)

/// The panel is absent from the tree when closed, not merely hidden in it.
///
/// Checked against the CLOSE action rather than the heading: the trigger's own
/// accessible name is "View all of today's study sessions", which contains the
/// heading text and would match while the panel is shut.
[<Fact>]
let ``the full-day panel is absent until it is opened`` () =
    Assert.False(saysAnywhere "Close today" (renderWith (manyViewToday 9)))

[<Fact>]
let ``opening the panel shows every session for today, not only the hidden ones`` () =
    let sessions = manyViewToday 9
    let opened = { plannerWith sessions with AllTodayOpen = true }
    let tree = Planner.view AuditFixtures.tokens opened ignore

    Assert.True(saysAnywhere "Today's study sessions" tree, "the panel heading is missing")

    // Every one of the nine, including the two the preview left out.
    for s in sessions do
        Assert.True(saysAnywhere s.Title tree, sprintf "the panel omits %s" s.Title)

/// **A whole-tree scan was the wrong instrument here.** The page legitimately
/// lists later days below the card, so an other-day title is expected SOMEWHERE
/// in the tree. What must not happen is the panel adding another copy of it.
/// Counting occurrences open-versus-closed isolates the panel exactly.
[<Fact>]
let ``opening the panel does not pull in a session from another day`` () =
    let other =
        { viewSession 200 9.0 with
            Title = "OtherDaySession"
            StartsAt = DateTime.Now.Date.AddDays(-1.0).AddHours 9.0
            EndsAt = DateTime.Now.Date.AddDays(-1.0).AddHours 10.0 }

    let sessions = other :: manyViewToday 9
    let occurrences (m: Planner.Model) =
        Planner.view AuditFixtures.tokens m ignore
        |> everyString
        |> List.filter (fun s -> s.Contains("OtherDaySession", StringComparison.Ordinal))
        |> List.length

    let closed = occurrences (plannerWith sessions)
    let opened = occurrences { plannerWith sessions with AllTodayOpen = true }

    Assert.Equal(closed, opened)

[<Fact>]
let ``the panel offers an explicit close with a real name`` () =
    let opened = { plannerWith (manyViewToday 9) with AllTodayOpen = true }
    let tree = Planner.view AuditFixtures.tokens opened ignore

    Assert.True(
        // Case-insensitive: `FloatingWindow` derives this from the window title.
        everyString tree
        |> List.exists (fun s -> s.Contains("close today's", StringComparison.OrdinalIgnoreCase)),
        "the panel has no named close action"
    )

/// Escape must be handled somewhere in the panel, and the scrim must swallow
/// pointer input so the page behind cannot be activated through it.
[<Fact>]
let ``the panel handles Escape and blocks the page behind it`` () =
    let opened = { plannerWith (manyViewToday 9) with AllTodayOpen = true }
    let tree = Planner.view AuditFixtures.tokens opened ignore

    Assert.True(all tree |> List.exists (hasHandler "KeyDown"), "nothing in the panel handles a key press")

    Assert.True(
        all tree |> List.exists (fun v -> boolProp "Focusable" v = Some true),
        "the panel cannot take focus, so Escape could never reach it"
    )

// ---------------------------------------------------------------------------
// 5. the page survives the overlay
// ---------------------------------------------------------------------------

/// **The failure mode this structure exists to prevent.** A `Border` takes one
/// child; a second `Border.child` replaces the first rather than layering, and
/// it compiles cleanly. Asserting the page is still present with the panel open
/// is what would catch that.
[<Fact>]
let ``the page is still rendered underneath an open panel`` () =
    let sessions = manyViewToday 9
    let closed = Planner.view AuditFixtures.tokens (plannerWith sessions) ignore
    let opened = Planner.view AuditFixtures.tokens { plannerWith sessions with AllTodayOpen = true } ignore

    // The card heading belongs to the page, never to the modal.
    Assert.True(saysAnywhere "Today's study plan" closed, "the page heading is missing when closed")
    Assert.True(saysAnywhere "Today's study plan" opened, "the page was replaced by the overlay rather than layered under it")

    Assert.True(
        (all opened).Length > (all closed).Length,
        "opening the panel did not add to the tree — it replaced it"
    )

// ---------------------------------------------------------------------------
// 6. reasoning, without asking
// ---------------------------------------------------------------------------

[<Fact>]
let ``reasoning is visible without any interaction`` () =
    let tree = renderWith (manyViewToday 3)
    Assert.True(saysAnywhere "Nearest deadline" tree, "the scheduler's reasoning is not shown by default")

[<Fact>]
let ``no reasoning toggle remains anywhere on the screen`` () =
    let tree = renderWith (manyViewToday 3)

    for banned in [ "Explain Plan"; "Hide Reasoning"; "Show reasoning" ] do
        Assert.False(saysAnywhere banned tree, sprintf "%A is still on the Planner" banned)
