module UniversityOS.Tests.TimelineTests

open System
open Xunit
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Features

/// The migrated Timeline.
///
/// Grouping, filtering, counting and the layout decisions all live in
/// TimelineLogic, so they are testable without an Avalonia application.

let private now = DateTime(2026, 7, 26, 14, 0, 0)

let private entry (kind: ActivityKind) (daysAgo: float) (title: string) : ActivityEntry =
    { Id = Guid.NewGuid()
      Kind = kind
      Title = title
      Detail = None
      Entity = None
      OccurredAt = now.AddDays(-daysAgo) }

let private withEntity (ref: EntityRef) (e: ActivityEntry) = { e with Entity = Some ref }

// ---- categories -------------------------------------------------------------

[<Fact>]
let ``every activity kind belongs to exactly one category`` () =
    // The property that matters: no entry can be invisible under every filter,
    // and none can be double-counted in the breakdown.
    let specific =
        TimelineLogic.Category.all |> List.filter (fun c -> c <> TimelineLogic.All)

    for kind in ActivityKind.all do
        let owners = specific |> List.filter (fun c -> TimelineLogic.Category.contains c kind)

        Assert.Equal(1, owners.Length)

[<Fact>]
let ``the All category admits everything`` () =
    for kind in ActivityKind.all do
        Assert.True(TimelineLogic.Category.contains TimelineLogic.All kind)

[<Fact>]
let ``every category has a label and covers at least one kind`` () =
    for category in TimelineLogic.Category.all do
        Assert.False(String.IsNullOrWhiteSpace(TimelineLogic.Category.label category))
        Assert.NotEmpty(TimelineLogic.Category.kinds category)

[<Fact>]
let ``filtering by category keeps only that category's entries`` () =
    let entries =
        [ entry ActivityKind.SessionCompleted 0.0 "Study block"
          entry ActivityKind.NoteCreated 0.0 "A note"
          entry ActivityKind.NoteUpdated 0.0 "Edited note"
          entry ActivityKind.ConnectorSynced 0.0 "Moodle" ]

    let notes = TimelineLogic.visibleEntries TimelineLogic.Notes "" entries
    Assert.Equal(2, notes.Length)
    Assert.All(notes, fun e -> Assert.True(TimelineLogic.Category.contains TimelineLogic.Notes e.Kind))

    Assert.Equal(4, (TimelineLogic.visibleEntries TimelineLogic.All "" entries).Length)

// ---- search -----------------------------------------------------------------

[<Fact>]
let ``an empty query matches everything`` () =
    let e = entry ActivityKind.NoteCreated 0.0 "Anything"
    Assert.True(TimelineLogic.matchesQuery "" e)
    Assert.True(TimelineLogic.matchesQuery "   " e)

[<Fact>]
let ``search covers title, detail and the kind's own label`` () =
    let e =
        { entry ActivityKind.DocumentImported 0.0 "lab-report.docx" with
            Detail = Some "Classified as a lab template" }

    Assert.True(TimelineLogic.matchesQuery "lab-report" e)
    Assert.True(TimelineLogic.matchesQuery "template" e)
    // Searching for the kind finds it even though the word is in neither field.
    Assert.True(TimelineLogic.matchesQuery "Document imported" e)
    Assert.False(TimelineLogic.matchesQuery "flashcards" e)

[<Fact>]
let ``search is case-insensitive and ignores surrounding space`` () =
    let e = entry ActivityKind.LectureAdded 0.0 "Distributed Systems"
    Assert.True(TimelineLogic.matchesQuery "DISTRIBUTED" e)
    Assert.True(TimelineLogic.matchesQuery "  systems  " e)

[<Fact>]
let ``category and query compose`` () =
    let entries =
        [ entry ActivityKind.NoteCreated 0.0 "Algebra revision"
          entry ActivityKind.NoteCreated 0.0 "CAP theorem"
          entry ActivityKind.SessionCompleted 0.0 "Algebra revision" ]

    let result = TimelineLogic.visibleEntries TimelineLogic.Notes "algebra" entries
    Assert.Single result |> ignore
    Assert.Equal("Algebra revision", result.Head.Title)

// ---- periods ----------------------------------------------------------------

[<Fact>]
let ``entries fall into the redesign's period buckets`` () =
    Assert.Equal(TimelineLogic.Today, TimelineLogic.periodOf now (now.AddHours -3.0))
    Assert.Equal(TimelineLogic.Yesterday, TimelineLogic.periodOf now (now.AddDays -1.0))
    Assert.Equal(TimelineLogic.EarlierThisWeek, TimelineLogic.periodOf now (now.AddDays -4.0))
    Assert.Equal(TimelineLogic.LastWeek, TimelineLogic.periodOf now (now.AddDays -9.0))
    Assert.Equal(TimelineLogic.LastMonth, TimelineLogic.periodOf now (now.AddDays -30.0))
    Assert.Equal(TimelineLogic.Earlier, TimelineLogic.periodOf now (now.AddDays -200.0))

[<Fact>]
let ``periods are calendar-relative, not elapsed hours`` () =
    // 23:50 yesterday is Yesterday even though it is only a couple of hours
    // before a 01:00 "now" — an elapsed-time bucket would call that Today.
    let justAfterMidnight = DateTime(2026, 7, 26, 1, 0, 0)
    let lateLastNight = DateTime(2026, 7, 25, 23, 50, 0)

    Assert.Equal(TimelineLogic.Yesterday, TimelineLogic.periodOf justAfterMidnight lateLastNight)

[<Fact>]
let ``a future entry does not fall outside every bucket`` () =
    // Clock skew or an imported event dated ahead must still render somewhere.
    Assert.Equal(TimelineLogic.Today, TimelineLogic.periodOf now (now.AddDays 3.0))

[<Fact>]
let ``groups come back newest period first, newest entry first`` () =
    let entries =
        [ entry ActivityKind.NoteCreated 9.0 "old"
          entry ActivityKind.NoteCreated 0.0 "newest"
          entry ActivityKind.NoteCreated 1.0 "yesterday"
          entry ActivityKind.NoteCreated 0.2 "earlier today" ]

    let groups = TimelineLogic.grouped now entries
    let periods = groups |> List.map fst

    Assert.Equal<TimelineLogic.Period list>(
        [ TimelineLogic.Today; TimelineLogic.Yesterday; TimelineLogic.LastWeek ],
        periods
    )

    // Within Today, newest first.
    let today = groups |> List.head |> snd
    Assert.Equal("newest", today.Head.Title)

[<Fact>]
let ``empty periods are dropped rather than rendered as bare headings`` () =
    let entries = [ entry ActivityKind.NoteCreated 0.0 "only today" ]
    let groups = TimelineLogic.grouped now entries

    Assert.Single groups |> ignore
    Assert.Equal(TimelineLogic.Today, fst groups.Head)

[<Fact>]
let ``grouping never drops or duplicates an entry`` () =
    let entries = [ 0.0; 1.0; 3.0; 9.0; 30.0; 400.0 ] |> List.map (fun d -> entry ActivityKind.AiAction d "x")

    let flattened = TimelineLogic.grouped now entries |> List.collect snd
    Assert.Equal(entries.Length, flattened.Length)

// ---- entity history ---------------------------------------------------------

[<Fact>]
let ``entity history follows one thing and ignores the rest`` () =
    let target = { Kind = EntityKind.Assignment; Id = Guid.NewGuid() }
    let other = { Kind = EntityKind.Assignment; Id = Guid.NewGuid() }

    let entries =
        [ entry ActivityKind.AssignmentCreated 5.0 "created" |> withEntity target
          entry ActivityKind.AssignmentUpdated 1.0 "updated" |> withEntity target
          entry ActivityKind.AssignmentCreated 2.0 "someone else" |> withEntity other
          entry ActivityKind.NoteCreated 0.0 "unlinked" ]

    let history = TimelineLogic.entityHistory target entries

    Assert.Equal(2, history.Length)
    // Newest first.
    Assert.Equal("updated", history.Head.Title)

[<Fact>]
let ``two entities of different kinds with the same id do not collide`` () =
    let id = Guid.NewGuid()
    let asAssignment = { Kind = EntityKind.Assignment; Id = id }
    let asNote = { Kind = EntityKind.Note; Id = id }

    let entries =
        [ entry ActivityKind.AssignmentCreated 1.0 "assignment" |> withEntity asAssignment
          entry ActivityKind.NoteCreated 1.0 "note" |> withEntity asNote ]

    Assert.Single(TimelineLogic.entityHistory asAssignment entries) |> ignore
    Assert.Equal("assignment", (TimelineLogic.entityHistory asAssignment entries).Head.Title)

// ---- derived counts ---------------------------------------------------------

[<Fact>]
let ``the stat tiles are six real counts`` () =
    let entries =
        [ entry ActivityKind.SessionCompleted 0.0 "s1"
          entry ActivityKind.SessionCompleted 2.0 "s2"
          entry ActivityKind.SessionCompleted 20.0 "old session"
          entry ActivityKind.LectureAdded 1.0 "lecture"
          entry ActivityKind.PlanGenerated 3.0 "plan" ]

    let tiles = TimelineLogic.stats now 95 entries

    Assert.Equal(6, tiles.Length)
    // Sessions this week counts the two inside seven days, not the old one.
    Assert.Equal("2", fst tiles.[0])
    Assert.Equal("1", fst tiles.[1])
    Assert.Equal("5", fst tiles.[4])

[<Fact>]
let ``stats on an empty log are zeroes, not blanks`` () =
    let tiles = TimelineLogic.stats now 0 []

    Assert.Equal(6, tiles.Length)
    Assert.All(tiles, fun (value, label) ->
        Assert.False(String.IsNullOrWhiteSpace value)
        Assert.False(String.IsNullOrWhiteSpace label))

[<Fact>]
let ``patterns say nothing when there is nothing to say`` () =
    Assert.Empty(TimelineLogic.patterns now [])

[<Fact>]
let ``patterns are derived from the entries, never invented`` () =
    let entries =
        [ for i in 1..4 -> entry ActivityKind.SessionCompleted (float i * 0.1) "session" ]

    let observations = TimelineLogic.patterns now entries

    Assert.NotEmpty observations
    Assert.All(observations, fun o -> Assert.False(String.IsNullOrWhiteSpace o))
    Assert.True(observations.Length <= 4)

    // The commonest kind is named because it genuinely is the commonest.
    Assert.Contains(observations, fun o -> o.Contains "Study session completed")

// ---- layout -----------------------------------------------------------------

[<Fact>]
let ``the side column appears only when there is room for it`` () =
    Assert.False(TimelineLogic.showsSideColumn 800.0)
    Assert.False(TimelineLogic.showsSideColumn 1099.0)
    Assert.True(TimelineLogic.showsSideColumn 1100.0)
    Assert.True(TimelineLogic.showsSideColumn 2400.0)

[<Fact>]
let ``stat tiles reflow rather than shrinking`` () =
    Assert.Equal(2, TimelineLogic.statsPerRow 600.0)
    Assert.Equal(3, TimelineLogic.statsPerRow 900.0)
    Assert.Equal(6, TimelineLogic.statsPerRow 1600.0)

    // Every arrangement divides the six tiles evenly — no ragged last row.
    for width in [ 600.0; 900.0; 1600.0 ] do
        Assert.Equal(0, 6 % TimelineLogic.statsPerRow width)

[<Fact>]
let ``layout decisions are monotonic in width`` () =
    // A resize must not flip a decision back and forth.
    let widths = [ 320.0 .. 40.0 .. 2600.0 ]
    let counts = widths |> List.map TimelineLogic.statsPerRow

    counts
    |> List.pairwise
    |> List.iter (fun (a, b) -> Assert.True(b >= a, sprintf "tiles per row went backwards: %d then %d" a b))

// ---- view modes -------------------------------------------------------------

[<Fact>]
let ``all three redesign view modes are offered and labelled`` () =
    Assert.Equal(3, TimelineLogic.ViewMode.all.Length)

    Assert.Equal<string list>(
        [ "Feed"; "Academic Journey"; "Entity History" ],
        TimelineLogic.ViewMode.all |> List.map TimelineLogic.ViewMode.label
    )

// ---- update behaviour -------------------------------------------------------

let private noServices: AppServices = Unchecked.defaultof<AppServices>

let private messagesFrom (cmd: Elmish.Cmd<TimelineView.Msg>) =
    let seen = ResizeArray<TimelineView.Msg>()
    for effect in cmd do effect seen.Add
    List.ofSeq seen

let private loaded entries : TimelineView.Model =
    { Entries = entries
      Category = TimelineLogic.All
      Mode = TimelineLogic.Feed
      Query = ""
      FocusEntity = None
      Selected = None
      Study = StudyTime.emptySummary
      Loading = false
      Error = None
      Width = 1400.0 }

[<Fact>]
let ``a successful load clears loading and any previous error`` () =
    let before = { loaded [] with Loading = true; Error = Some "earlier" }
    let entries = [ entry ActivityKind.AiAction 0.0 "x" ]
    let after, cmd, external = TimelineView.update noServices (TimelineView.Loaded(entries, StudyTime.emptySummary)) before

    Assert.Equal(1, after.Entries.Length)
    Assert.False after.Loading
    Assert.Equal(None, after.Error)
    Assert.Equal(None, external)
    Assert.Empty(messagesFrom cmd)

[<Fact>]
let ``a failure keeps whatever was already loaded`` () =
    let entries = [ entry ActivityKind.AiAction 0.0 "x" ]
    let after, _, _ = TimelineView.update noServices (TimelineView.Failed(exn "offline")) (loaded entries)

    Assert.Equal(1, after.Entries.Length)
    Assert.Equal(Some "offline", after.Error)
    Assert.False after.Loading

[<Fact>]
let ``changing filter, search or view mode never re-queries`` () =
    // One read feeds all three presentations. A filter that refetched would
    // hammer the activity log on every keystroke.
    let model = loaded [ entry ActivityKind.NoteCreated 0.0 "x" ]

    for msg in
        [ TimelineView.SetCategory TimelineLogic.Notes
          TimelineView.SetQuery "abc"
          TimelineView.SetMode TimelineLogic.Journey
          TimelineView.Resized 900.0 ] do
        let _, cmd, external = TimelineView.update noServices msg model
        Assert.Empty(messagesFrom cmd)
        Assert.Equal(None, external)

[<Fact>]
let ``focusing an entity switches to its history view`` () =
    let ref = { Kind = EntityKind.Assignment; Id = Guid.NewGuid() }
    let after, _, _ = TimelineView.update noServices (TimelineView.FocusOn(ref, "Coursework")) (loaded [])

    Assert.Equal(TimelineLogic.Entity, after.Mode)
    Assert.True after.FocusEntity.IsSome

    // And clearing returns to the feed.
    let back, _, _ = TimelineView.update noServices TimelineView.ClearFocus after
    Assert.Equal(TimelineLogic.Feed, back.Mode)
    Assert.Equal(None, back.FocusEntity)

[<Fact>]
let ``opening a linked entry asks the shell to navigate`` () =
    let ref = { Kind = EntityKind.Note; Id = Guid.NewGuid() }
    let linked = entry ActivityKind.NoteCreated 0.0 "note" |> withEntity ref

    let _, _, external = TimelineView.update noServices (TimelineView.OpenEntry linked) (loaded [ linked ])
    Assert.Equal(Some(TimelineView.Goto Page.Notes), external)

[<Fact>]
let ``opening an unlinked entry does nothing rather than navigating wrongly`` () =
    let unlinked = entry ActivityKind.ConnectorSynced 0.0 "sync"
    let _, cmd, external = TimelineView.update noServices (TimelineView.OpenEntry unlinked) (loaded [ unlinked ])

    Assert.Equal(None, external)
    Assert.Empty(messagesFrom cmd)

[<Fact>]
let ``every entity kind an entry can carry has a destination`` () =
    // A timeline entry that navigates nowhere is a dead "Open" button.
    for kind in
        [ EntityKind.Module
          EntityKind.Assignment
          EntityKind.Event
          EntityKind.Note
          EntityKind.Document
          EntityKind.Lecture
          EntityKind.InboxItem
          EntityKind.Session
          EntityKind.Conversation
          EntityKind.RevisionItem ] do
        let page = Page.forEntity kind
        Assert.Contains(Page.all, fun (p, _, _) -> p = page)
