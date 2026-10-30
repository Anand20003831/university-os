module UniversityOS.Tests.TimelineActionsTests

open System
open Xunit
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.App
open UniversityOS.App.Features

/// The Timeline's study figures, the Academic Journey's geometry, and Feed
/// selection.
///
/// All three are pure decisions or synchronous state transitions, so none of
/// this needs an Avalonia application or a database.

let private now = DateTime(2026, 7, 26, 14, 0, 0)

/// 26 July 2026 is a Sunday — chosen deliberately, because a Monday-based week
/// and a Sunday-based week disagree about it, so a test that hard-codes either
/// has to say which it means.
let private sunday = now

let private entry (kind: ActivityKind) (daysAgo: float) (title: string) : ActivityEntry =
    { Id = Guid.NewGuid()
      Kind = kind
      Title = title
      Detail = None
      Entity = None
      OccurredAt = now.AddDays(-daysAgo) }

let private withEntity (ref: EntityRef) (e: ActivityEntry) = { e with Entity = Some ref }

let private session (startsAt: DateTime) (minutes: float) (completed: bool) : PlannedSession =
    { Id = SessionId.New()
      AssignmentId = None
      MilestoneId = None
      Title = "Study"
      ModuleCode = None
      StartsAt = startsAt
      EndsAt = startsAt.AddMinutes minutes
      Reason = ""
      Completed = completed
      CreatedAt = startsAt }

// ---------------------------------------------------------------------------
// 1. Studied this week
//
// The figure comes from the SESSIONS, never from counting activity entries.
// These pin the arithmetic, including the two cases a naive sum gets wrong: the
// repository's range query returns sessions that merely OVERLAP the week, and a
// session can straddle midnight or the week boundary.
// ---------------------------------------------------------------------------

[<Fact>]
let ``an empty week reads as 0 min`` () =
    Assert.Equal(0, StudyTime.minutesStudiedThisWeek DayOfWeek.Monday sunday [])
    Assert.Equal("0 min", StudyTime.format 0)

[<Fact>]
let ``duration formats in the natural voice, singular and plural`` () =
    Assert.Equal("0 min", StudyTime.format 0)
    Assert.Equal("45 min", StudyTime.format 45)
    Assert.Equal("59 min", StudyTime.format 59)
    Assert.Equal("1 hr", StudyTime.format 60)
    Assert.Equal("1 hr 20 min", StudyTime.format 80)
    Assert.Equal("2 hrs", StudyTime.format 120)
    Assert.Equal("6 hrs 5 min", StudyTime.format 365)
    // Never negative, whatever arrives.
    Assert.Equal("0 min", StudyTime.format -30)

[<Fact>]
let ``only completed sessions count as studied time`` () =
    let sessions =
        [ session (sunday.AddHours -3.0) 60.0 true
          session (sunday.AddHours -2.0) 45.0 false ]

    Assert.Equal(60, StudyTime.minutesStudiedThisWeek DayOfWeek.Monday sunday sessions)

[<Fact>]
let ``multiple completed sessions add up`` () =
    let sessions =
        [ session (sunday.AddHours -5.0) 50.0 true
          session (sunday.AddHours -3.0) 25.0 true
          session (sunday.AddHours -1.0) 45.0 true ]

    Assert.Equal(120, StudyTime.minutesStudiedThisWeek DayOfWeek.Monday sunday sessions)
    Assert.Equal("2 hrs", StudyTime.format 120)

[<Fact>]
let ``the same session handed back twice is counted once`` () =
    let one = session (sunday.AddHours -4.0) 90.0 true
    Assert.Equal(90, StudyTime.minutesStudiedThisWeek DayOfWeek.Monday sunday [ one; one; one ])

[<Fact>]
let ``a session spanning midnight is counted once, split across the boundary`` () =
    // 23:30 Sunday to 00:30 Monday. The repository returns it for BOTH weeks —
    // its filter is an overlap test — so summing whole durations would count
    // these 60 minutes twice. Clipping gives 30 to each.
    let crossing = session (DateTime(2026, 7, 26, 23, 30, 0)) 60.0 true

    let endingWeek = StudyTime.minutesStudiedThisWeek DayOfWeek.Monday sunday [ crossing ]

    let startingWeek =
        StudyTime.minutesStudiedThisWeek DayOfWeek.Monday (DateTime(2026, 7, 27, 9, 0, 0)) [ crossing ]

    Assert.Equal(30, endingWeek)
    Assert.Equal(30, startingWeek)
    Assert.Equal(60, endingWeek + startingWeek)

[<Fact>]
let ``a session inside a single day is not clipped`` () =
    // The midnight rule must not quietly shorten ordinary sessions.
    let ordinary = session (DateTime(2026, 7, 22, 10, 0, 0)) 95.0 true
    Assert.Equal(95, StudyTime.minutesStudiedThisWeek DayOfWeek.Monday sunday [ ordinary ])

[<Fact>]
let ``last week's study does not leak into this week`` () =
    let lastWeek = session (DateTime(2026, 7, 18, 10, 0, 0)) 120.0 true
    let thisWeek = session (DateTime(2026, 7, 22, 10, 0, 0)) 30.0 true

    Assert.Equal(30, StudyTime.minutesStudiedThisWeek DayOfWeek.Monday sunday [ lastWeek; thisWeek ])

[<Fact>]
let ``the week starts on the culture's first day, not a hard-coded one`` () =
    // Under a Monday-based week, Sunday the 26th is the LAST day of the week
    // beginning the 20th; under a Sunday-based week it is the FIRST day of the
    // week beginning the 26th. Anything hard-coding one is wrong for the other.
    Assert.Equal(DateTime(2026, 7, 20), StudyTime.weekStart DayOfWeek.Monday sunday)
    Assert.Equal(DateTime(2026, 7, 26), StudyTime.weekStart DayOfWeek.Sunday sunday)

    let mondayMorning = session (DateTime(2026, 7, 20, 9, 0, 0)) 60.0 true
    Assert.Equal(60, StudyTime.minutesStudiedThisWeek DayOfWeek.Monday sunday [ mondayMorning ])
    Assert.Equal(0, StudyTime.minutesStudiedThisWeek DayOfWeek.Sunday sunday [ mondayMorning ])

[<Fact>]
let ``the week starts at local midnight and is stable within the week`` () =
    for day in 20..26 do
        let atSomeTime = DateTime(2026, 7, day, 17, 43, 12)
        let start = StudyTime.weekStart DayOfWeek.Monday atSomeTime

        Assert.Equal(DateTime(2026, 7, 20), start)
        Assert.Equal(TimeSpan.Zero, start.TimeOfDay)

[<Fact>]
let ``the studied tile reads from sessions, not from activity entries`` () =
    // Six SessionCompleted entries and 95 real minutes: the tile must say
    // "1 hr 35 min", not "6". This is the substitution the correction is about.
    let entries = List.init 6 (fun i -> entry ActivityKind.SessionCompleted (float i * 0.1) "session")
    let tiles = TimelineLogic.stats now 95 entries

    Assert.Equal(6, tiles.Length)
    Assert.Equal(("1 hr 35 min", "Studied this week"), tiles.[5])
    Assert.DoesNotContain("Events this week", tiles |> List.map snd)

// ---------------------------------------------------------------------------
// 2. Recent Focus
//
// Every figure comes from the SAME session list the "Studied this week" tile
// uses — one repository read, summarised once.
// ---------------------------------------------------------------------------

let private summarise sessions =
    StudyTime.summarise DayOfWeek.Monday sunday sessions

[<Fact>]
let ``an empty week gives an empty summary, not a row of zeroes`` () =
    let s = summarise []

    Assert.Equal(0, s.MinutesToday)
    Assert.Equal(0, s.MinutesThisWeek)
    Assert.Equal(0, s.CompletedThisWeek)
    Assert.Equal(0, s.AverageMinutes)
    Assert.Equal(None, s.Last)

    // The card branches on this one field to show its empty state rather than
    // rendering five zeroed rows.
    Assert.Equal(StudyTime.emptySummary, s)

[<Fact>]
let ``studied today counts only today's minutes`` () =
    // Sunday 26th is "today". Wednesday's session is in the week but not today.
    let sessions =
        [ session (DateTime(2026, 7, 26, 9, 0, 0)) 80.0 true
          session (DateTime(2026, 7, 22, 9, 0, 0)) 45.0 true ]

    let s = summarise sessions

    Assert.Equal(80, s.MinutesToday)
    Assert.Equal(125, s.MinutesThisWeek)
    Assert.Equal("1 hr 20 min", StudyTime.format s.MinutesToday)

[<Fact>]
let ``completed sessions are counted, and the average follows`` () =
    let sessions =
        [ session (DateTime(2026, 7, 21, 9, 0, 0)) 60.0 true
          session (DateTime(2026, 7, 22, 9, 0, 0)) 90.0 true
          session (DateTime(2026, 7, 23, 9, 0, 0)) 30.0 true
          // Not completed — counts for neither.
          session (DateTime(2026, 7, 24, 9, 0, 0)) 120.0 false ]

    let s = summarise sessions

    Assert.Equal(3, s.CompletedThisWeek)
    Assert.Equal(180, s.MinutesThisWeek)
    Assert.Equal(60, s.AverageMinutes)

[<Fact>]
let ``the average rounds rather than truncating`` () =
    let sessions =
        [ session (DateTime(2026, 7, 21, 9, 0, 0)) 50.0 true
          session (DateTime(2026, 7, 22, 9, 0, 0)) 51.0 true ]

    // 101 / 2 = 50.5 → 50 or 51, never 50 by truncation alone.
    Assert.Equal(51, (summarise sessions).AverageMinutes)

[<Fact>]
let ``the average is zero, not a division by zero, when nothing is completed`` () =
    let sessions = [ session (DateTime(2026, 7, 22, 9, 0, 0)) 60.0 false ]
    Assert.Equal(0, (summarise sessions).AverageMinutes)

[<Fact>]
let ``the last session is the most recently finished one`` () =
    let sessions =
        [ session (DateTime(2026, 7, 21, 9, 0, 0)) 60.0 true
          session (DateTime(2026, 7, 26, 10, 0, 0)) 45.0 true
          session (DateTime(2026, 7, 23, 9, 0, 0)) 30.0 true ]

    match (summarise sessions).Last with
    | Some(_, minutes, endedAt) ->
        Assert.Equal(45, minutes)
        Assert.Equal(DateTime(2026, 7, 26, 10, 45, 0), endedAt)
    | None -> failwith "expected a last session"

[<Fact>]
let ``a session still running is not the last session`` () =
    // "now" is 14:00. A session running 13:30-15:00 has not been done yet.
    let running = session (DateTime(2026, 7, 26, 13, 30, 0)) 90.0 true
    let finished = session (DateTime(2026, 7, 26, 9, 0, 0)) 45.0 true

    match (summarise [ running; finished ]).Last with
    | Some(_, minutes, _) -> Assert.Equal(45, minutes)
    | None -> failwith "expected the finished session"

[<Fact>]
let ``a future session is not the last session either`` () =
    let future = session (DateTime(2026, 7, 26, 18, 0, 0)) 60.0 true
    Assert.Equal(None, (summarise [ future ]).Last)

[<Fact>]
let ``duplicate sessions do not double-count anywhere in the summary`` () =
    let one = session (DateTime(2026, 7, 22, 9, 0, 0)) 60.0 true
    let s = summarise [ one; one; one ]

    Assert.Equal(1, s.CompletedThisWeek)
    Assert.Equal(60, s.MinutesThisWeek)
    Assert.Equal(60, s.AverageMinutes)

[<Fact>]
let ``last week's sessions are outside the summary`` () =
    let lastWeek = session (DateTime(2026, 7, 18, 10, 0, 0)) 120.0 true
    let thisWeek = session (DateTime(2026, 7, 22, 10, 0, 0)) 30.0 true
    let s = summarise [ lastWeek; thisWeek ]

    Assert.Equal(1, s.CompletedThisWeek)
    Assert.Equal(30, s.MinutesThisWeek)

[<Fact>]
let ``a midnight-spanning session is one session, with its minutes split`` () =
    // The two questions have different answers, and the card asks both: the
    // MINUTES belong half to each week, but it is one session, not half a
    // session in each.
    let crossing = session (DateTime(2026, 7, 26, 23, 30, 0)) 60.0 true
    let s = summarise [ crossing ]

    Assert.Equal(1, s.CompletedThisWeek)
    Assert.Equal(30, s.MinutesThisWeek)
    Assert.Equal(30, s.MinutesToday)

[<Fact>]
let ``the tile and Recent Focus come from one summary, so they cannot disagree`` () =
    // The tile is handed `Study.MinutesThisWeek` from the same record the card
    // reads. One session read, one summary, two consumers.
    let sessions =
        [ session (DateTime(2026, 7, 22, 9, 0, 0)) 95.0 true
          session (DateTime(2026, 7, 23, 9, 0, 0)) 25.0 true ]

    let s = summarise sessions
    let tiles = TimelineLogic.stats now s.MinutesThisWeek []

    Assert.Equal(("2 hrs", "Studied this week"), tiles.[5])
    Assert.Equal(120, s.MinutesThisWeek)

// ---------------------------------------------------------------------------
// 3. Academic Journey geometry
//
// These describe LAYOUT RULES, not a formula. The previous suite asserted that
// `nodeTopOffset` equalled the expression it was defined as — true by
// construction, and it passed while the node sat visibly high, because the
// assumed line height was wrong. There is no offset now: the node and the title
// share a Grid row and both centre in it, so the alignment is Avalonia's to get
// right rather than a number of mine.
// ---------------------------------------------------------------------------

[<Fact>]
let ``the node is a true circle`` () =
    // Width equals height, and the radius is exactly half — an 11px box at
    // radius 6 is a rounded square, which is what the first version drew.
    Assert.Equal(TimelineLogic.JourneyLayout.nodeSize / 2.0, TimelineLogic.JourneyLayout.nodeRadius, 6)
    Assert.True(TimelineLogic.JourneyLayout.nodeSize > 0.0)

[<Fact>]
let ``no vertical offset constant survives`` () =
    // The regression guard for the whole correction: if someone reintroduces a
    // computed vertical offset, the node stops tracking the title's real
    // metrics and the defect comes back. The layout module must expose only
    // sizes and widths.
    let members =
        typeof<TimelineLogic.JourneyLayout.Segment>.DeclaringType.GetMethods()
        |> Array.map (fun m -> m.Name)

    Assert.DoesNotContain("get_nodeTopOffset", members)
    Assert.DoesNotContain("get_titleLineHeight", members)
    Assert.DoesNotContain("get_nodeCentreY", members)
    Assert.DoesNotContain("get_titleCentreY", members)

[<Fact>]
let ``the marker column holds the node and the line on one centre`` () =
    // Both are centred in one fixed-width column, so they share a centre line by
    // construction — there is no margin arithmetic to get wrong.
    Assert.True(TimelineLogic.JourneyLayout.markerWidth > TimelineLogic.JourneyLayout.nodeSize)
    Assert.True(TimelineLogic.JourneyLayout.connectorWidth < TimelineLogic.JourneyLayout.nodeSize)
    Assert.True(TimelineLogic.JourneyLayout.contentGap > 0.0)
    Assert.True(TimelineLogic.JourneyLayout.rowGap > 0.0)

[<Fact>]
let ``the first entry draws nothing above its node`` () =
    Assert.False(TimelineLogic.JourneyLayout.hasConnectorAbove 0)
    Assert.DoesNotContain(TimelineLogic.JourneyLayout.AboveNode, TimelineLogic.JourneyLayout.segmentsFor 0 4)

[<Fact>]
let ``the last entry draws nothing below its node`` () =
    let segments = TimelineLogic.JourneyLayout.segmentsFor 3 4

    Assert.False(TimelineLogic.JourneyLayout.hasConnectorBelow 3 4)
    Assert.DoesNotContain(TimelineLogic.JourneyLayout.BelowNode, segments)
    Assert.DoesNotContain(TimelineLogic.JourneyLayout.ThroughMetadata, segments)

[<Fact>]
let ``a middle entry is joined on both sides`` () =
    let segments = TimelineLogic.JourneyLayout.segmentsFor 1 4

    Assert.Contains(TimelineLogic.JourneyLayout.AboveNode, segments)
    Assert.Contains(TimelineLogic.JourneyLayout.BelowNode, segments)
    Assert.Contains(TimelineLogic.JourneyLayout.ThroughMetadata, segments)

[<Fact>]
let ``a lone entry draws no line at all`` () =
    Assert.Empty(TimelineLogic.JourneyLayout.segmentsFor 0 1)

[<Fact>]
let ``the segment below a node always continues through its metadata`` () =
    // The row boundary is where the old version broke: the title row ended, the
    // metadata row began, and nothing bridged the seam. The two segments are
    // driven by ONE condition, so they cannot disagree.
    for count in 1..6 do
        for index in 0 .. count - 1 do
            let segments = TimelineLogic.JourneyLayout.segmentsFor index count

            Assert.Equal(
                List.contains TimelineLogic.JourneyLayout.BelowNode segments,
                List.contains TimelineLogic.JourneyLayout.ThroughMetadata segments
            )

[<Fact>]
let ``every boundary between two entries is bridged from both sides`` () =
    let count = 6

    for boundary in 0 .. count - 2 do
        let upper = TimelineLogic.JourneyLayout.segmentsFor boundary count
        let lower = TimelineLogic.JourneyLayout.segmentsFor (boundary + 1) count

        Assert.True(
            List.contains TimelineLogic.JourneyLayout.ThroughMetadata upper
            && List.contains TimelineLogic.JourneyLayout.AboveNode lower,
            sprintf "boundary %d is not bridged from both sides" boundary
        )

// ---------------------------------------------------------------------------
// 4. Feed selection
// ---------------------------------------------------------------------------

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

let private modelOf (m, _, _) = m

let private feed =
    [ entry ActivityKind.SessionCompleted 0.0 "Finished revision"
      entry ActivityKind.NoteCreated 1.0 "Wrote notes"
      entry ActivityKind.PlanGenerated 2.0 "Planned the week" ]

let private select e model =
    TimelineView.update noServices (TimelineView.SelectEntry e) model |> modelOf

[<Fact>]
let ``clicking an unselected entry selects it`` () =
    let after = loaded feed |> select feed.[1]
    Assert.Equal(Some feed.[1].Id, after.Selected)

[<Fact>]
let ``clicking the selected entry again clears it`` () =
    // The behaviour change: selection now toggles. Nothing destructive hangs off
    // it any more, so an accidental second click costs nothing.
    let once = loaded feed |> select feed.[0]
    let twice = once |> select feed.[0]

    Assert.Equal(Some feed.[0].Id, once.Selected)
    Assert.Equal(None, twice.Selected)

[<Fact>]
let ``toggling is a pure rule, stated once`` () =
    let a, b = Guid.NewGuid(), Guid.NewGuid()

    Assert.Equal(Some a, TimelineLogic.toggledSelection None a)
    Assert.Equal(None, TimelineLogic.toggledSelection (Some a) a)
    Assert.Equal(Some b, TimelineLogic.toggledSelection (Some a) b)

[<Fact>]
let ``clicking another entry replaces the selection`` () =
    let after = loaded feed |> select feed.[0] |> select feed.[2]

    Assert.Equal(Some feed.[2].Id, after.Selected)
    Assert.NotEqual(Some feed.[0].Id, after.Selected)

[<Fact>]
let ``clicking empty Feed space clears the selection`` () =
    // The Feed's background dispatches this explicitly — it is not a pointer
    // event that happens to bubble.
    let selected = loaded feed |> select feed.[1]
    let cleared = TimelineView.update noServices TimelineView.ClearSelection selected |> modelOf

    Assert.Equal(Some feed.[1].Id, selected.Selected)
    Assert.Equal(None, cleared.Selected)

[<Fact>]
let ``Escape clears the selection through the same message`` () =
    // Keyboard and background click are the same operation, so there is one
    // code path to reason about.
    let selected = loaded feed |> select feed.[0]
    let cleared, cmd, external = TimelineView.update noServices TimelineView.ClearSelection selected

    Assert.Equal(None, cleared.Selected)
    Assert.Empty(messagesFrom cmd)
    Assert.Equal(None, external)

[<Fact>]
let ``clearing an already-empty selection is harmless`` () =
    let after = TimelineView.update noServices TimelineView.ClearSelection (loaded feed) |> modelOf
    Assert.Equal(None, after.Selected)

[<Fact>]
let ``selection is only possible in the Feed`` () =
    Assert.True(TimelineLogic.allowsSelection TimelineLogic.Feed)
    Assert.False(TimelineLogic.allowsSelection TimelineLogic.Journey)
    Assert.False(TimelineLogic.allowsSelection TimelineLogic.Entity)

    for mode in [ TimelineLogic.Journey; TimelineLogic.Entity ] do
        let after = { loaded feed with Mode = mode } |> select feed.[0]
        Assert.Equal(None, after.Selected)

[<Fact>]
let ``switching away from the Feed clears the selection`` () =
    let moved =
        loaded feed
        |> select feed.[0]
        |> fun m -> TimelineView.update noServices (TimelineView.SetMode TimelineLogic.Journey) m |> modelOf

    Assert.Equal(None, moved.Selected)

[<Fact>]
let ``filtering the selected entry away clears the selection`` () =
    // The selected entry is a note; filter to Planner and it is gone.
    let filtered =
        loaded feed
        |> select feed.[1]
        |> fun m -> TimelineView.update noServices (TimelineView.SetCategory TimelineLogic.Planner) m |> modelOf

    Assert.Equal(None, filtered.Selected)

[<Fact>]
let ``filtering that keeps the selected entry keeps the selection`` () =
    let filtered =
        loaded feed
        |> select feed.[1]
        |> fun m -> TimelineView.update noServices (TimelineView.SetCategory TimelineLogic.Notes) m |> modelOf

    Assert.Equal(Some feed.[1].Id, filtered.Selected)

[<Fact>]
let ``searching past the selected entry clears the selection`` () =
    let searched =
        loaded feed
        |> select feed.[1]
        |> fun m -> TimelineView.update noServices (TimelineView.SetQuery "zzzz") m |> modelOf

    Assert.Equal(None, searched.Selected)

[<Fact>]
let ``reloading without the selected entry clears the selection`` () =
    let survivors = [ feed.[1]; feed.[2] ]

    let reloaded =
        loaded feed
        |> select feed.[0]
        |> fun m ->
            TimelineView.update noServices (TimelineView.Loaded(survivors, StudyTime.emptySummary)) m |> modelOf

    Assert.Equal(None, reloaded.Selected)

[<Fact>]
let ``reloading with the selected entry still present keeps the selection`` () =
    let reloaded =
        loaded feed
        |> select feed.[0]
        |> fun m -> TimelineView.update noServices (TimelineView.Loaded(feed, StudyTime.emptySummary)) m |> modelOf

    Assert.Equal(Some feed.[0].Id, reloaded.Selected)

[<Fact>]
let ``Open does not change the selection`` () =
    // Open and History act on the entry; they must not also move or clear the
    // selection out from under the user.
    let linked =
        entry ActivityKind.NoteCreated 0.0 "Linked note"
        |> withEntity { Kind = EntityKind.Note; Id = Guid.NewGuid() }

    let selected = loaded [ linked ] |> select linked
    let after, _, external = TimelineView.update noServices (TimelineView.OpenEntry linked) selected

    Assert.Equal(Some linked.Id, after.Selected)
    Assert.Equal(Some(TimelineView.Goto Page.Notes), external)

[<Fact>]
let ``History does not clear the selection, it moves the view`` () =
    // History switches to Entity mode, and selection is Feed-only, so the
    // selection goes — but through the one reconcile rule, not by accident.
    let ref = { Kind = EntityKind.Note; Id = Guid.NewGuid() }
    let linked = entry ActivityKind.NoteCreated 0.0 "Linked note" |> withEntity ref

    let after =
        loaded [ linked ]
        |> select linked
        |> fun m -> TimelineView.update noServices (TimelineView.FocusOn(ref, linked.Title)) m |> modelOf

    Assert.Equal(TimelineLogic.Entity, after.Mode)
    Assert.Equal(None, after.Selected)

[<Fact>]
let ``selection is transient — there is nowhere to persist it`` () =
    Assert.Equal(None, (loaded feed).Selected)

    let uiStateFields = typeof<UiState>.GetProperties() |> Array.map (fun p -> p.Name)

    Assert.DoesNotContain("SelectedTimelineEntry", uiStateFields)
    Assert.DoesNotContain("TimelineSelection", uiStateFields)

// ---------------------------------------------------------------------------
// 5. Undo & Restore is gone
// ---------------------------------------------------------------------------

[<Fact>]
let ``no reversal state remains on the Timeline model`` () =
    let fields = typeof<TimelineView.Model>.GetProperties() |> Array.map (fun p -> p.Name)

    Assert.DoesNotContain("Reversal", fields)
    Assert.DoesNotContain("StudiedMinutes", fields)
    Assert.Contains("Selected", fields)
    Assert.Contains("Study", fields)

[<Fact>]
let ``no reversal messages remain`` () =
    let cases =
        Reflection.FSharpType.GetUnionCases typeof<TimelineView.Msg>
        |> Array.map (fun c -> c.Name)

    for gone in
        [ "RequestReversal"; "CancelReversal"; "ConfirmReversal"; "ReversalSucceeded"; "ReversalErrored"; "CapabilityResolved" ] do
        Assert.DoesNotContain(gone, cases)

    Assert.Contains("SelectEntry", cases)
    Assert.Contains("ClearSelection", cases)

[<Fact>]
let ``the Timeline no longer asks the shell to show anything`` () =
    // `External` carried a `Notify` case solely so the reversal could raise a
    // toast. Navigation is all that is left.
    let cases =
        Reflection.FSharpType.GetUnionCases typeof<TimelineView.External>
        |> Array.map (fun c -> c.Name)

    Assert.Equal<string array>([| "Goto" |], cases)

[<Fact>]
let ``the reversal activity kind is gone and the log still parses`` () =
    let names = ActivityKind.all |> List.map ActivityKind.toString

    Assert.DoesNotContain("action-reversed", names)
    Assert.Equal(None, ActivityKind.parse "action-reversed")

    // Every surviving kind still round-trips, so removing one did not disturb
    // the persisted vocabulary.
    for kind in ActivityKind.all do
        Assert.Equal(Some kind, ActivityKind.parse (ActivityKind.toString kind))

[<Fact>]
let ``every activity kind still belongs to exactly one category`` () =
    // Removing a kind must not leave a category pointing at nothing, and must
    // not orphan a kind from every filter.
    for kind in ActivityKind.all do
        let owners =
            TimelineLogic.Category.all
            |> List.filter (fun c -> c <> TimelineLogic.All)
            |> List.filter (fun c -> TimelineLogic.Category.contains c kind)

        Assert.Equal(1, owners.Length)

[<Fact>]
let ``the session repository exposes no lookup added for the reversal`` () =
    let methods =
        typeof<ISessionRepository>.GetMethods() |> Array.map (fun m -> m.Name)

    Assert.DoesNotContain("GetById", methods)

    // The three that existed before are untouched, so session completion and
    // planning still work.
    Assert.Contains("GetBetween", methods)
    Assert.Contains("SetCompleted", methods)
    Assert.Contains("ReplaceFrom", methods)

[<Fact>]
let ``the right column is Patterns, Breakdown, Recent Focus`` () =
    // The order the view maps over, so this is the real thing and not a
    // restatement of it.
    Assert.Equal<string list>(
        [ "Patterns"; "Breakdown"; "Recent Focus" ],
        TimelineLogic.SideCard.order |> List.map TimelineLogic.SideCard.title
    )

[<Fact>]
let ``no side card is the removed Undo and Restore`` () =
    let titles = TimelineLogic.SideCard.order |> List.map TimelineLogic.SideCard.title

    Assert.DoesNotContain("Undo & Restore", titles)
    Assert.Equal(3, titles.Length)
    Assert.Equal(titles.Length, (List.distinct titles).Length)

// ---------------------------------------------------------------------------
// 6. Where a click clears the selection
//
// The page-root handler asks one question of every press: did this start inside
// a control that owns its own click? These build real Avalonia control trees and
// ask `Ui.isInteractiveSource` the same question the handler does, so the
// exclusion is tested against the actual routing rule rather than a description
// of it.
//
// Full pointer ROUTING — press, tunnel, bubble, handled — needs a running
// windowing platform, which this suite does not have. What is covered here is
// the decision the handler makes once an event reaches it; that the event
// reaches it at all is the structural change reported alongside.
// ---------------------------------------------------------------------------

open Avalonia.Controls

/// A press that landed on blank layout, not on a control.
[<Fact>]
let ``blank layout is not an interactive source`` () =
    // Everything the page is made of between its controls: panels, borders,
    // text. A press on any of these must reach the page handler and clear.
    let panel = StackPanel()
    let border = Border()
    let text = TextBlock(Text = "TODAY")
    let grid = Grid()

    for background in [ panel :> Control; border; text; grid ] do
        Assert.False(
            Ui.isInteractiveSource background,
            sprintf "%s must count as background" (background.GetType().Name)
        )

[<Fact>]
let ``a press inside a Button is an interactive source, however deep`` () =
    // The source of a real press is a leaf — the TextBlock inside the Button —
    // never the Button itself. Checking the source's own type would treat every
    // button press as a background click and clear the selection first.
    let label = TextBlock(Text = "Open →")
    let inner = StackPanel()
    inner.Children.Add label
    let button = Button(Content = inner)

    Assert.True(Ui.isInteractiveSource label)
    Assert.True(Ui.isInteractiveSource inner)
    Assert.True(Ui.isInteractiveSource button)

[<Fact>]
let ``the search box and its contents are interactive`` () =
    let box = TextBox()
    Assert.True(Ui.isInteractiveSource box)

[<Fact>]
let ``every control type the page uses owns its own click`` () =
    // Named individually so adding a control to the page without adding it here
    // is a visible omission rather than a silent bug.
    let controls: Control list =
        [ Button()
          TextBox()
          CheckBox()
          RadioButton()
          ComboBox()
          Slider()
          Avalonia.Controls.Primitives.ScrollBar() ]

    for control in controls do
        Assert.True(
            Ui.isInteractiveSource control,
            sprintf "%s must own its click" (control.GetType().Name)
        )

[<Fact>]
let ``a filter chip's label counts as its button`` () =
    // Chips are Buttons with text content; the press lands on the text.
    let chipText = TextBlock(Text = "Notes")
    let chip = Button(Content = chipText)

    Assert.True(Ui.isInteractiveSource chipText)
    Assert.NotNull chip

[<Fact>]
let ``a Breakdown row's inner panel counts as its button`` () =
    // Breakdown rows are Buttons whose content is a DockPanel of two TextBlocks.
    let count = TextBlock(Text = "12")
    let dock = DockPanel()
    dock.Children.Add count
    let row = Button(Content = dock)

    Assert.True(Ui.isInteractiveSource count)
    Assert.True(Ui.isInteractiveSource dock)
    Assert.NotNull row

[<Fact>]
let ``a card's own layout is background, even inside the feed`` () =
    // An entry card is a Border, not a Button — it is excluded from the page
    // handler by marking its press handled, not by this predicate. So the
    // predicate must NOT claim a card is interactive, or a click on a card's
    // padding would be treated as a control press.
    let cardBody = StackPanel()
    let card = Border(Child = cardBody)

    Assert.False(Ui.isInteractiveSource card)
    Assert.False(Ui.isInteractiveSource cardBody)

[<Fact>]
let ``a non-visual source is never interactive`` () =
    // Robustness: the handler must not throw on an event whose source is not a
    // control at all.
    Assert.False(Ui.isInteractiveSource null)
    Assert.False(Ui.isInteractiveSource (box "not a control"))

// The page handler's own guard, as a predicate over the model.

let private clearsOnBackgroundClick (model: TimelineView.Model) =
    TimelineLogic.allowsSelection model.Mode && model.Selected.IsSome

[<Fact>]
let ``a background click clears only when there is a Feed selection to clear`` () =
    let selected = loaded feed |> select feed.[0]

    Assert.True(clearsOnBackgroundClick selected)

    // Nothing selected: no message, no MVU churn on an ordinary page click.
    Assert.False(clearsOnBackgroundClick (loaded feed))

    // Not the Feed: the other two modes have no selection to clear.
    for mode in [ TimelineLogic.Journey; TimelineLogic.Entity ] do
        Assert.False(clearsOnBackgroundClick { selected with Mode = mode })

[<Fact>]
let ``pointer movement and scrolling have no clearing path`` () =
    // Stated as a structural fact: `ClearSelection` is reachable from exactly
    // two gestures — a background press and Escape. There is no hover, move,
    // wheel or focus handler that dispatches it, so none of them can clear.
    let selected = loaded feed |> select feed.[0]

    // Resizing is the only other pointer-adjacent message the page handles, and
    // it leaves the selection alone.
    let resized =
        TimelineView.update noServices (TimelineView.Resized 900.0) selected |> modelOf

    Assert.Equal(Some feed.[0].Id, resized.Selected)
