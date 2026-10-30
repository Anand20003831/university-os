module UniversityOS.Tests.DashboardCardBalanceTests

open System
open Xunit
open Avalonia.Controls
open Avalonia.Controls.Primitives
open UniversityOS.Domain
open UniversityOS.Domain.Planning
open UniversityOS.Application
open UniversityOS.App
open UniversityOS.App.Features
open UniversityOS.Tests.AuditHarness

/// **Two reported defects, on one row of the Dashboard.**
///
/// The Morning Briefing and Today's Timeline sit in a pair that stretches both
/// cards to the taller of the two. Whichever is longer therefore decides the
/// height of the other, and the shorter one ends as a headline over blank glass.
///
/// The reported symptom was the briefing being mostly empty. The cause is on
/// both sides, and so is the fix: the timeline no longer grows without limit,
/// and the briefing has real content to fill the height it is given.
///
/// **What these tests cannot tell you.** They assert the tree — that every item
/// is present, that the viewport is derived rather than guessed, that the
/// scrollbar is hidden rather than disabled. Whether the two cards *look*
/// balanced is a judgement only the reviewer can make in the running window.

let private now = DateTime(2026, 3, 4, 9, 30, 0)

let private emptyData: DashboardData =
    { Now = now
      TodayEvents = []
      TodaySessions = []
      Recommendation = None
      UpcomingDeadlines = []
      InboxCount = 0
      RecentNotes = []
      OpenAssignmentCount = 0
      AverageProgress = None
      FreeMinutesToday = 0
      PlannedMinutesToday = 0
      StudiedMinutesToday = 0
      DailyTargetMinutes = 240
      CompletedSessionsToday = 0
      StudiedMinutesThisWeek = 0
      WeeklyTargetMinutes = 1680
      ModuleProgress = []
      RecentActivity = []
      NoteCount = 0 }

let private session (index: int) : PlannedSession =
    { Id = SessionId(Guid(sprintf "00000000-0000-0000-0000-%012d" (500 + index)))
      AssignmentId = None
      MilestoneId = None
      Title = sprintf "Study block %d" index
      ModuleCode = Some "COMP3811"
      StartsAt = now.Date.AddHours(8.0 + float index)
      EndsAt = now.Date.AddHours(8.0 + float index).AddMinutes 50.0
      Reason = "planned"
      Completed = false
      CreatedAt = now }

let private dataWithSessions (count: int) =
    { emptyData with TodaySessions = [ for i in 1..count -> session i ] }

let private recommended: PlannableTask =
    { AssignmentId = AssignmentId(Guid "00000000-0000-0000-0000-000000000201")
      Title = "Final project report"
      ModuleCode = Some "COMP3931"
      Deadline = Some(now.Date.AddDays(4.0).AddHours 17.0)
      RemainingMinutes = 1080
      Priority = Priority.Critical
      Weighting = Some 60.0 
      Milestones = [] }

let private render (data: DashboardData) =
    let model: Dashboard.Model =
        { Data = Some data
          UserName = "Reviewer"
          Loading = false
          Error = None
          Refreshing = false
          Columns = 3 }

    Dashboard.view AuditFixtures.tokens model ignore

// ---------------------------------------------------------------------------
// Today's Timeline — five visible, none lost
// ---------------------------------------------------------------------------

/// **The viewport is derived, not guessed.** A hardcoded pixel height would
/// silently show four-and-a-bit rows the moment a row grew, and nothing would
/// fail.
[<Fact>]
let ``the timeline viewport is exactly five rows and the gaps between them`` () =
    let expected =
        float DashboardLogic.Timeline.initialRows * DashboardLogic.Timeline.rowHeight
        + float (DashboardLogic.Timeline.initialRows - 1) * DashboardLogic.Timeline.rowSpacing

    Assert.Equal(expected, DashboardLogic.Timeline.viewportHeight)
    Assert.Equal(5, DashboardLogic.Timeline.initialRows)

/// The row applies the height the arithmetic above assumes. Without this the
/// two can drift apart and the card quietly stops showing five.
[<Fact>]
let ``every timeline row is the height the viewport is computed from`` () =
    let tree = render (dataWithSessions 8)

    let rows =
        tree
        |> ofType "DockPanel"
        |> List.filter (fun v -> floatProp "MinHeight" v = Some DashboardLogic.Timeline.rowHeight)

    Assert.Equal(8, rows.Length)

/// **Nothing is truncated.** The viewport clips; the model does not. A cut list
/// would put the last item of a busy day permanently out of reach.
[<Fact>]
let ``every timeline item is in the tree, however many there are`` () =
    for count in [ 1; 5; 6; 14 ] do
        let tree = render (dataWithSessions count)
        let text = everyString tree

        for i in 1..count do
            Assert.Contains(sprintf "Study block %d" i, text)

/// `Hidden` and `Disabled` look identical in a screenshot and are opposites in
/// behaviour: `Disabled` stops the wheel working. The requirement is a hidden
/// bar that still scrolls, so the distinction is the assertion.
[<Fact>]
let ``the timeline scrolls with no visible scrollbar`` () =
    let tree = render (dataWithSessions 9)

    let capped =
        tree
        |> allWithAncestors
        |> List.filter (fun (v, _) -> floatProp "MaxHeight" v = Some DashboardLogic.Timeline.viewportHeight)

    Assert.NotEmpty capped

    let scrollers =
        capped
        |> List.collect (fun (v, _) -> ofType "ScrollViewer" v)
        |> List.filter (fun v ->
            propOf "VerticalScrollBarVisibility" v = Some(box ScrollBarVisibility.Hidden))

    Assert.NotEmpty scrollers

    // Not Disabled anywhere in that subtree — that is the mutation this test
    // exists to catch.
    for v in capped |> List.collect (fun (v, _) -> ofType "ScrollViewer" v) do
        Assert.NotEqual<obj option>(Some(box ScrollBarVisibility.Disabled), propOf "VerticalScrollBarVisibility" v)

/// A list that scrolls with no scrollbar and no hint looks like a list that
/// ends, so the count of what is below is shown — and only when there is one.
[<Fact>]
let ``the overflow hint appears only when the list actually overflows`` () =
    Assert.False(DashboardLogic.Timeline.scrolls 5)
    Assert.True(DashboardLogic.Timeline.scrolls 6)
    Assert.Equal(0, DashboardLogic.Timeline.hiddenCount 5)
    Assert.Equal(4, DashboardLogic.Timeline.hiddenCount 9)

    let short = everyString (render (dataWithSessions 5)) |> String.concat "\n"
    let long = everyString (render (dataWithSessions 9)) |> String.concat "\n"

    Assert.DoesNotContain("more today", short)
    Assert.Contains("4 more today", long)

/// Five or fewer must not be forced to the viewport height — `maxHeight` caps,
/// it does not stretch, and a quiet day should give a short card.
[<Fact>]
let ``a short day is not padded out to the full viewport`` () =
    let tree = render (dataWithSessions 3)

    let heights =
        tree |> all |> List.choose (floatProp "Height") |> List.filter (fun h -> h = DashboardLogic.Timeline.viewportHeight)

    Assert.Empty heights

// ---------------------------------------------------------------------------
// Morning Briefing — real content, not filler
// ---------------------------------------------------------------------------

/// The card said what to start and gave one reason line. These are the
/// recommended task's own fields, so the reviewer can check the recommendation
/// rather than take it on trust.
[<Fact>]
let ``the briefing shows the recommended task's real facts`` () =
    let data =
        { emptyData with Recommendation = Some(recommended, "Highest weighted coursework, due soonest") }

    let facts = DashboardLogic.recommendationFacts data
    let labels = facts |> List.map fst

    Assert.Contains("Due", labels)
    Assert.Contains("Remaining", labels)
    Assert.Contains("Worth", labels)
    Assert.Contains("Priority", labels)

    let text = everyString (render data) |> String.concat "\n"
    Assert.Contains("18h", text) // 1080 minutes remaining
    Assert.Contains("60% of the module", text)
    Assert.Contains("Critical", text)

/// **An absent field is omitted, never rendered as a zero.** An assignment with
/// no deadline and one due at midnight are different facts, and "0h" for an
/// unknown estimate is a claim the data does not support.
[<Fact>]
let ``absent fields are left out rather than filled with a plausible zero`` () =
    let bare =
        { recommended with
            Deadline = None
            Weighting = None
            RemainingMinutes = 0 }

    let facts =
        DashboardLogic.recommendationFacts { emptyData with Recommendation = Some(bare, "reason") }

    let labels = facts |> List.map fst

    Assert.DoesNotContain("Due", labels)
    Assert.DoesNotContain("Worth", labels)
    Assert.DoesNotContain("Remaining", labels)

    // Priority always exists on the task, so it always shows.
    Assert.Contains("Priority", labels)

[<Fact>]
let ``no recommendation means no fact row at all`` () =
    Assert.Empty(DashboardLogic.recommendationFacts emptyData)

    // And the card still says something rather than rendering an empty block.
    let text = everyString (render emptyData) |> String.concat "\n"
    Assert.Contains("Nothing urgent", text)

/// The tiles are data so the set, the order and the wording are assertable
/// rather than buried in the view.
[<Fact>]
let ``the briefing tiles are three real figures, in order`` () =
    let data =
        { emptyData with
            TodayEvents = []
            TodaySessions = [ session 1; session 2 ]
            FreeMinutesToday = 210 }

    let tiles = DashboardLogic.briefingTiles data

    Assert.Equal<string list>([ "Today's schedule"; "Next deadline"; "Free study time" ], tiles |> List.map snd)
    Assert.Equal<string>("2", fst tiles.[0])
    Assert.Equal<string>("None", fst tiles.[1])
    Assert.Equal<string>(Format.duration 210, fst tiles.[2])

    let text = everyString (render data) |> String.concat "\n"

    for (_, label) in tiles do
        Assert.Contains(label, text)
