module UniversityOS.Tests.PlannerRowTests

open System
open Xunit
open Avalonia.Media
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.Domain.Planning
open UniversityOS.Application
open UniversityOS.App
open UniversityOS.App.Features
open UniversityOS.Tests.AuditHarness

/// The Planner session row: status placement, priority derivation, and the
/// always-visible strategy previews.
///
/// **The view reads the real clock** — `Planner.view` takes one `DateTime.Now`
/// per frame — so every fixture here is built around the actual current date.
/// A fixture pinned to a frozen date is not "today" as far as the view is
/// concerned and every row silently disappears, which would make these tests
/// pass for the wrong reason.

let private noServices: AppServices = Unchecked.defaultof<AppServices>
let private today = DateTime.Now.Date

let private assignmentId (n: int) =
    AssignmentId(Guid(sprintf "00000000-0000-0000-0000-%012d" n))

let private sessionAt (n: int) (startHour: float) (endHour: float) =
    { Id = SessionId(Guid(sprintf "10000000-0000-0000-0000-%012d" n))
      AssignmentId = None
      MilestoneId = None
      Title = sprintf "Session %d" n
      ModuleCode = Some "COMP3811"
      StartsAt = today.AddHours startHour
      EndsAt = today.AddHours endHour
      Reason = "Nearest deadline"
      Completed = false
      CreatedAt = today }

let private linked (n: int) (startHour: float) (endHour: float) =
    { sessionAt n startHour endHour with AssignmentId = Some(assignmentId n) }

let private task (n: int) (priority: Priority) : PlannableTask =
    { AssignmentId = assignmentId n
      Title = sprintf "Assignment %d" n
      ModuleCode = Some "COMP3811"
      Deadline = None
      RemainingMinutes = 120
      Priority = priority
      Weighting = None 
      Milestones = [] }

let private planInput: PlannerInput =
    { Now = DateTime.Now
      Tasks = []
      Busy = []
      Prefs = PlannerPreferences.ofSettings UserSettings.defaults }

let private plannerWith sessions tasks =
    let model, _ = Planner.init noServices

    { model with
        Sessions = sessions
        Tasks = tasks
        Loading = false
        Width = 1400.0 }

let private renderOf sessions tasks =
    Planner.view AuditFixtures.tokens (plannerWith sessions tasks) ignore

let private says (fragment: string) (tree: IView) =
    everyString tree |> List.exists (fun s -> s.Contains(fragment, StringComparison.Ordinal))

// ---------------------------------------------------------------------------
// priority: from the linked assignment, or not at all
// ---------------------------------------------------------------------------

[<Theory>]
[<InlineData 0>]
[<InlineData 1>]
[<InlineData 2>]
[<InlineData 3>]
let ``a linked session takes its assignment's real priority`` (rank: int) =
    let priority = Priority.ofRank rank
    Assert.Equal(Some priority, PlannerLogic.priorityOf [ task 1 priority ] (linked 1 9.0 10.0))

/// **No default.** An unlinked session has no priority, and inventing one would
/// assert something about the user's work that nothing recorded.
[<Fact>]
let ``an unlinked session has no priority`` () =
    Assert.Equal(None, PlannerLogic.priorityOf [ task 1 Priority.High ] (sessionAt 1 9.0 10.0))

/// A link that no longer resolves — the assignment finished or deleted after the
/// plan was made — is the same as no link. It must not fall back to a guess.
[<Fact>]
let ``a session whose linked assignment is gone shows no priority`` () =
    let orphan = linked 99 9.0 10.0
    Assert.Equal(None, PlannerLogic.priorityOf [ task 1 Priority.High ] orphan)
    Assert.Equal(None, PlannerLogic.priorityOf [] orphan)

/// Priority and effort are independent readings of different things. A long block
/// on a low-priority assignment is High effort and Low priority at once — were
/// priority inferred from duration, these two would move together.
[<Fact>]
let ``priority is not inferred from duration`` () =
    let long = linked 1 9.0 12.0
    let short = linked 1 9.0 9.5
    let tasks = [ task 1 Priority.Low ]

    Assert.Equal(PlannerLogic.Deep, PlannerLogic.effortOf long)
    Assert.Equal(PlannerLogic.Light, PlannerLogic.effortOf short)

    Assert.Equal(Some Priority.Low, PlannerLogic.priorityOf tasks long)
    Assert.Equal(Some Priority.Low, PlannerLogic.priorityOf tasks short)

/// The label names the scale, as effort does — and keeps all four real cases
/// rather than collapsing Critical into High.
[<Fact>]
let ``priority labels name the scale and keep every real case`` () =
    Assert.Equal<string>("High priority", PlannerLogic.priorityLabel Priority.High)
    Assert.Equal<string>("Medium priority", PlannerLogic.priorityLabel Priority.Medium)
    Assert.Equal<string>("Low priority", PlannerLogic.priorityLabel Priority.Low)
    Assert.Equal<string>("Critical priority", PlannerLogic.priorityLabel Priority.Critical)

[<Fact>]
let ``a linked session renders its priority badge`` () =
    Assert.True(
        says "High priority" (renderOf [ linked 1 9.0 10.0 ] [ task 1 Priority.High ]),
        "the linked priority badge is missing from the row"
    )

[<Fact>]
let ``an unlinked session renders no priority badge at all`` () =
    let tree = renderOf [ sessionAt 1 9.0 10.0 ] [ task 1 Priority.High ]

    for banned in [ "High priority"; "Medium priority"; "Low priority"; "Critical priority" ] do
        Assert.False(says banned tree, sprintf "an unlinked session was given %s" banned)

// ---------------------------------------------------------------------------
// the title row
// ---------------------------------------------------------------------------

/// The status word belongs on the row exactly once. A second copy would be the
/// old inline badge left behind by the move to the right-hand column.
[<Fact>]
let ``the status word appears exactly once on a session row`` () =
    let tree = renderOf [ sessionAt 1 9.0 10.0 ] []

    let occurrences =
        everyString tree
        |> List.filter (fun s ->
            s.Contains("SCHEDULED", StringComparison.Ordinal)
            || s.Contains("UP NEXT", StringComparison.Ordinal)
            || s.Contains("IN PROGRESS", StringComparison.Ordinal)
            || s.Contains("MISSED", StringComparison.Ordinal)
            || s.Contains("DONE", StringComparison.Ordinal))
        |> List.length

    Assert.Equal(1, occurrences)

/// A long title must not cost the badge its place: the title sits in a star
/// column so it wraps, and the badge keeps its own auto column.
[<Fact>]
let ``a very long title keeps the status badge on the row`` () =
    let longTitle =
        "Final project report covering the design, implementation and evaluation of a physically based renderer including every appendix"

    let tree = renderOf [ { sessionAt 1 9.0 10.0 with Title = longTitle } ] []

    Assert.True(says longTitle tree, "the long title is missing")

    Assert.True(
        says "SCHEDULED" tree || says "UP NEXT" tree || says "MISSED" tree,
        "the status badge was lost when the title grew"
    )

    let titleBlock =
        ofType "TextBlock" tree |> List.tryFind (fun v -> stringProp "Text" v = Some longTitle)

    Assert.True(titleBlock.IsSome, "the title is not a TextBlock")

    match propOf "TextWrapping" titleBlock.Value with
    | Some(:? TextWrapping as w) -> Assert.NotEqual(TextWrapping.NoWrap, w)
    | _ -> failwith "the session title does not declare wrapping"

/// The completion control writes through to the repository, so it must survive
/// every layout change.
[<Fact>]
let ``the completion control is still on every session row`` () =
    let tree = renderOf [ sessionAt 1 9.0 10.0 ] []

    Assert.True(
        visibleInteractive tree |> List.exists (hasHandler "Click"),
        "the session row has no interactive control left"
    )

// ---------------------------------------------------------------------------
// alternative plans, without asking
// ---------------------------------------------------------------------------

/// The previews used to sit behind a button, so the comparison the section
/// exists to offer was invisible until requested. A load now asks for them.
[<Fact>]
let ``loading the Planner requests every strategy preview`` () =
    let _, cmd =
        Planner.update noServices (Planner.Loaded(planInput, [], [])) (plannerWith [] [])

    let messages = ResizeArray<Planner.Msg>()
    for effect in cmd do effect messages.Add

    Assert.Contains(Planner.PreviewAlternatives, messages)

[<Fact>]
let ``no preview-all control remains on the Planner`` () =
    let tree = renderOf [ sessionAt 1 9.0 10.0 ] []

    for banned in [ "Preview all strategies"; "Previewing" ] do
        Assert.False(says banned tree, sprintf "%s is still on the Planner" banned)

// ---------------------------------------------------------------------------
// the overlay's testable contract
// ---------------------------------------------------------------------------

[<Fact>]
let ``closing clears the open state`` () =
    let opened = { plannerWith [ sessionAt 1 9.0 10.0 ] [] with AllTodayOpen = true }
    let closed, _ = Planner.update noServices Planner.CloseAllToday opened

    Assert.False closed.AllTodayOpen

/// Opening twice must not stack two panels on top of each other.
[<Fact>]
let ``opening twice cannot stack two panels`` () =
    let once, _ = Planner.update noServices Planner.OpenAllToday (plannerWith [ sessionAt 1 9.0 10.0 ] [])
    let twice, _ = Planner.update noServices Planner.OpenAllToday once

    Assert.True twice.AllTodayOpen

    let closeActions =
        Planner.view AuditFixtures.tokens twice ignore
        |> everyString
        // Case-insensitive: the shared `FloatingWindow` derives the close
        // action's name from the window title, so the capitalisation follows the
        // title rather than being written out here.
        |> List.filter (fun s -> s.Contains("close today", StringComparison.OrdinalIgnoreCase))
        |> List.length

    Assert.Equal(1, closeActions)
