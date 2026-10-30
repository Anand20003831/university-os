module UniversityOS.Tests.CalendarViewTests

open System
open Xunit
open Avalonia.Media
open Avalonia.Controls.Primitives
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Features

/// The rebuilt Calendar screen.
///
/// **The redesign's Week and Month areas are a Gantt of module projects** —
/// progress percentages, milestone diamonds, projected states, AI tails. None of
/// that exists in this domain. Much of what these tests protect is that none of
/// it was invented, and that the two things that *were* missing — all-day
/// handling and overlap lanes — now genuinely work.

// ---------------------------------------------------------------------------
// harness
// ---------------------------------------------------------------------------

let private accessorName (accessor: Accessor) =
    match accessor with
    | Accessor.InstanceProperty p -> p.Name
    | Accessor.AvaloniaProperty p -> p.Name

let private kids (view: IView) : IView list =
    view.Attrs
    |> List.collect (fun attr ->
        match attr.Content with
        | ValueSome content ->
            match content.Content with
            | ViewContent.Single(Some child) -> [ child ]
            | ViewContent.Single None -> []
            | ViewContent.Multiple children -> children
        | ValueNone -> [])

let rec private all (view: IView) : IView list = view :: (kids view |> List.collect all)

let private propOf (name: string) (view: IView) : obj option =
    view.Attrs
    |> List.tryPick (fun attr ->
        match attr.Property with
        | ValueSome p when accessorName p.Accessor = name -> Some p.Value
        | _ -> None)

let private stringProp name view =
    match propOf name view with
    | Some(:? string as s) -> Some s
    | _ -> None

let private floatProp name view =
    match propOf name view with
    | Some(:? float as f) -> Some f
    | _ -> None

let private ofType (name: string) (root: IView) =
    all root |> List.filter (fun v -> v.ViewType.Name = name)

let private words (root: IView) =
    all root
    |> List.collect (fun v -> [ stringProp "Text" v; stringProp "Content" v ] |> List.choose id)

let private says (needle: string) (root: IView) =
    words root |> List.exists (fun s -> s.Contains(needle, StringComparison.Ordinal))

let private countSaying (needle: string) (root: IView) =
    words root |> List.filter (fun s -> s.Contains(needle, StringComparison.Ordinal)) |> List.length

let private accessibleNames (root: IView) = all root |> List.choose (stringProp "Name")

// ---------------------------------------------------------------------------
// fixtures
// ---------------------------------------------------------------------------

let private tokens = Tokens.ofAppearance true 0 AppearanceSettings.defaults
let private noServices: AppServices = Unchecked.defaultof<AppServices>

let private modelOf (m, _, _) = m
let private cmdOf (_, c, _) = c
let private externalOf (_, _, e) = e

/// A Wednesday, so week-start arithmetic has something to do.
let private now = DateTime(2026, 8, 5, 14, 30, 0)

let private firstDay = Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek

let private eventAt (title: string) (start: DateTime) (finish: DateTime) : CalendarEvent =
    { CalendarEvent.create now title EventKind.Meeting start finish with Id = EventId.New() }

let private allDayOn (title: string) (day: DateTime) : CalendarEvent =
    { eventAt title day.Date (day.Date.AddDays 1.0) with AllDay = true }

let private sessionAt (title: string) (start: DateTime) (finish: DateTime) : PlannedSession =
    { Id = SessionId.New()
      AssignmentId = None
      MilestoneId = None
      Title = title
      ModuleCode = None
      StartsAt = start
      EndsAt = finish
      Reason = "The planner placed this here."
      Completed = false
      CreatedAt = now }

let private moduleOf code name : StudyModule =
    { StudyModule.create (now.AddDays -60.0) code name 0 with Color = "#CB8A4E" }

/// Built directly rather than through `init`, which genuinely needs a clock to
/// choose its first anchor.
let private ready () : CalendarView.Model =
    { Anchor = now.Date
      Mode = CalendarLogic.ViewMode.Week
      Events = []
      Sessions = []
      Modules = []
      Filter = CalendarLogic.Filter.none
      Selected = None
      Draft = None
      PendingDelete = false
      Saving = false
      Loading = false
      Now = now
      Width = 1600.0
      Error = None }

let private withEvents (events: CalendarEvent list) = { ready () with Events = events }

let private render (model: CalendarView.Model) = CalendarView.view tokens model ignore

let private ev = CalendarLogic.Ev
let private sess = CalendarLogic.Sess

// ---------------------------------------------------------------------------
// 1. no Gantt was invented
// ---------------------------------------------------------------------------

[<Fact>]
let ``nothing claims progress, milestones or projection`` () =
    let model =
        { withEvents [ eventAt "Supervision" (now.Date.AddHours 10.0) (now.Date.AddHours 11.0) ] with
            Sessions = [ sessionAt "Revise compilers" (now.Date.AddHours 13.0) (now.Date.AddHours 15.0) ] }

    for mode in CalendarLogic.ViewMode.all do
        let root = render { model with Mode = mode }
        let shown = String.concat " " (words root) |> fun s -> s.ToLowerInvariant()

        for absent in
            [ "milestone"; "projected"; "% complete"; "completion"; "progress"; "confidence"; "in progress" ] do
            Assert.False(shown.Contains absent, sprintf "%A said %s" mode absent)

        // A percentage of anything would be an invention.
        for s in words root do
            Assert.False(s.Contains "%", sprintf "%A: %s" mode s)

[<Fact>]
let ``only the four real modes are offered`` () =
    Assert.Equal(4, CalendarLogic.ViewMode.all.Length)

    Assert.Equal<CalendarLogic.ViewMode list>(
        [ CalendarLogic.ViewMode.Day
          CalendarLogic.ViewMode.Week
          CalendarLogic.ViewMode.Month
          CalendarLogic.ViewMode.Agenda ],
        CalendarLogic.ViewMode.all
    )

    // The redesign offers semester and year zooms; both would need a term model
    // that does not exist.
    let root = render (ready ())
    Assert.False(says "Semester" root)
    Assert.False(says "Year" root)

[<Fact>]
let ``no AI tail appears anywhere on the calendar`` () =
    let root = render (withEvents [ eventAt "Lecture" (now.Date.AddHours 9.0) (now.Date.AddHours 10.0) ])
    let shown = String.concat " " (words root)

    Assert.DoesNotContain("✦", shown)
    Assert.DoesNotContain("AI", shown)

// ---------------------------------------------------------------------------
// 2. date and week rules
// ---------------------------------------------------------------------------

[<Fact>]
let ``the week comes from the one shared definition`` () =
    // Not a local Monday calculation — the culture's own first day.
    Assert.Equal(StudyTime.weekStart firstDay now, CalendarLogic.weekStart firstDay now)

    let start = CalendarLogic.weekStart firstDay now
    Assert.Equal(firstDay, start.DayOfWeek)
    Assert.True(start <= now.Date)
    Assert.True(start > now.Date.AddDays -7.0)

[<Fact>]
let ``every mode's range is half-open and starts at local midnight`` () =
    for mode in CalendarLogic.ViewMode.all do
        let s, e = CalendarLogic.visibleRange mode firstDay now
        Assert.Equal(s.Date, s)
        Assert.Equal(TimeSpan.Zero, s.TimeOfDay)
        Assert.True(e > s, string mode)

[<Fact>]
let ``the day range is exactly one local day`` () =
    let s, e = CalendarLogic.visibleRange CalendarLogic.ViewMode.Day firstDay now
    Assert.Equal(now.Date, s)
    Assert.Equal(1.0, (e - s).TotalDays, 6)

[<Fact>]
let ``the week range is seven days from the shared week start`` () =
    let s, e = CalendarLogic.visibleRange CalendarLogic.ViewMode.Week firstDay now
    Assert.Equal(StudyTime.weekStart firstDay now, s)
    Assert.Equal(7.0, (e - s).TotalDays, 6)

[<Fact>]
let ``the month range is the calendar month`` () =
    let s, e = CalendarLogic.visibleRange CalendarLogic.ViewMode.Month firstDay now
    Assert.Equal(DateTime(2026, 8, 1), s)
    Assert.Equal(DateTime(2026, 9, 1), e)

[<Fact>]
let ``stepping moves by whatever the mode shows`` () =
    Assert.Equal(now.Date.AddDays 1.0, CalendarLogic.step CalendarLogic.ViewMode.Day 1 now)
    Assert.Equal(now.Date.AddDays -1.0, CalendarLogic.step CalendarLogic.ViewMode.Day -1 now)
    Assert.Equal(now.Date.AddDays 7.0, CalendarLogic.step CalendarLogic.ViewMode.Week 1 now)
    Assert.Equal(now.Date.AddDays 7.0, CalendarLogic.step CalendarLogic.ViewMode.Agenda 1 now)
    Assert.Equal(DateTime(2026, 9, 1), CalendarLogic.step CalendarLogic.ViewMode.Month 1 now)

[<Fact>]
let ``stepping crosses month and year boundaries correctly`` () =
    let december = DateTime(2026, 12, 14)
    Assert.Equal(DateTime(2027, 1, 1), CalendarLogic.step CalendarLogic.ViewMode.Month 1 december)

    let january = DateTime(2027, 1, 14)
    Assert.Equal(DateTime(2026, 12, 1), CalendarLogic.step CalendarLogic.ViewMode.Month -1 january)

    // A short month cannot overflow: the 31st of January steps to February.
    let jan31 = DateTime(2027, 1, 31)
    Assert.Equal(DateTime(2027, 2, 1), CalendarLogic.step CalendarLogic.ViewMode.Month 1 jan31)

[<Fact>]
let ``Today is the clock's today, not a remembered anchor`` () =
    Assert.Equal(now.Date, CalendarLogic.todayAnchor now)
    Assert.Equal(TimeSpan.Zero, (CalendarLogic.todayAnchor now).TimeOfDay)

[<Fact>]
let ``a range containing today is recognised, and one that does not is not`` () =
    Assert.True(CalendarLogic.containsToday now (CalendarLogic.visibleRange CalendarLogic.ViewMode.Week firstDay now))

    let farOff = now.AddDays 90.0
    Assert.False(CalendarLogic.containsToday now (CalendarLogic.visibleRange CalendarLogic.ViewMode.Day firstDay farOff))

[<Fact>]
let ``the range title names what is actually visible`` () =
    Assert.Contains("August 2026", CalendarLogic.rangeTitle CalendarLogic.ViewMode.Month firstDay now)
    Assert.Contains("2026", CalendarLogic.rangeTitle CalendarLogic.ViewMode.Day firstDay now)

    let root = render { ready () with Mode = CalendarLogic.ViewMode.Month }
    Assert.True(says "August 2026" root)

// ---------------------------------------------------------------------------
// 3. month cells
// ---------------------------------------------------------------------------

[<Fact>]
let ``a month is always six rows, so the page never jumps`` () =
    for month in 1..12 do
        let cells = CalendarLogic.monthCells firstDay (DateTime(2026, month, 15))
        Assert.Equal(42, cells.Length)

    // Including a February that fits in four weeks exactly.
    let feb2027 = CalendarLogic.monthCells firstDay (DateTime(2027, 2, 15))
    Assert.Equal(42, feb2027.Length)

[<Fact>]
let ``the grid starts on the culture's first day and dates run consecutively`` () =
    let cells = CalendarLogic.monthCells firstDay now

    Assert.Equal(firstDay, (List.head cells).Date.DayOfWeek)

    cells
    |> List.pairwise
    |> List.iter (fun (a, b) -> Assert.Equal(a.Date.AddDays 1.0, b.Date))

[<Fact>]
let ``days borrowed from adjacent months are marked, not hidden`` () =
    let cells = CalendarLogic.monthCells firstDay now

    Assert.Contains(cells, fun c -> not c.InMonth)
    Assert.Contains(cells, fun c -> c.InMonth)

    for c in cells |> List.filter (fun c -> c.InMonth) do
        Assert.Equal(8, c.Date.Month)

[<Fact>]
let ``the overflow count is exact`` () =
    let entries n = List.init n (fun i -> ev (eventAt (string i) (now.Date.AddHours(float i)) (now.Date.AddHours(float i + 1.0))))

    Assert.Equal(None, CalendarLogic.monthOverflow (entries 0))
    Assert.Equal(None, CalendarLogic.monthOverflow (entries CalendarLogic.monthChipsPerCell))
    Assert.Equal(Some 2, CalendarLogic.monthOverflow (entries (CalendarLogic.monthChipsPerCell + 2)))

[<Fact>]
let ``overflow opens the real day`` () =
    let day = now.Date

    let events =
        List.init (CalendarLogic.monthChipsPerCell + 2) (fun i ->
            eventAt (sprintf "Event %d" i) (day.AddHours(float i)) (day.AddHours(float i + 1.0)))

    let root = render { withEvents events with Mode = CalendarLogic.ViewMode.Month }
    Assert.True(says "+2 more" root)

    let after = CalendarView.update noServices (CalendarView.SelectDay day) (ready ()) |> modelOf
    Assert.Equal(CalendarLogic.ViewMode.Day, after.Mode)
    Assert.Equal(day, after.Anchor)

[<Fact>]
let ``the weekday headings are seven, in the culture's order`` () =
    let headings = CalendarLogic.weekdayHeadings firstDay
    Assert.Equal(7, headings.Length)
    Assert.Equal(7, (List.distinct headings).Length)

    let root = render { ready () with Mode = CalendarLogic.ViewMode.Month }
    Assert.True(says ((List.head headings).ToUpperInvariant()) root)

// ---------------------------------------------------------------------------
// 4. all-day handling — real, and previously ignored
// ---------------------------------------------------------------------------

[<Fact>]
let ``an all-day event is recognised and a session never is`` () =
    Assert.True(CalendarLogic.Entry.isAllDay (ev (allDayOn "Reading week" now)))
    Assert.False(CalendarLogic.Entry.isAllDay (ev (eventAt "Supervision" now (now.AddHours 1.0))))

    // The planner always places a session at a real time.
    Assert.False(CalendarLogic.Entry.isAllDay (sess (sessionAt "Revise" now (now.AddHours 1.0))))

[<Fact>]
let ``all-day and timed entries are separated`` () =
    let entries =
        [ ev (allDayOn "Reading week" now)
          ev (eventAt "Supervision" (now.Date.AddHours 10.0) (now.Date.AddHours 11.0)) ]

    Assert.Single(CalendarLogic.allDayOf entries) |> ignore
    Assert.Single(CalendarLogic.timedOf entries) |> ignore

[<Fact>]
let ``an all-day event is drawn in the all-day band, never as a timed block`` () =
    let model =
        { withEvents [ allDayOn "Reading week" now ] with Mode = CalendarLogic.ViewMode.Day }

    let root = render model

    Assert.True(says "All day" root)
    Assert.True(says "Reading week" root)

    // It gets no time, because it has none that means anything.
    Assert.False(says "00:00–00:00" root)

    // And it is not laid into the timed grid.
    let placed = CalendarLogic.layoutDay now.Date (CalendarLogic.timedOf [ ev (allDayOn "Reading week" now) ])
    Assert.Empty placed

[<Fact>]
let ``several all-day events all appear, alongside timed ones`` () =
    let model =
        { withEvents
            [ allDayOn "Reading week" now
              allDayOn "Bank holiday" now
              eventAt "Supervision" (now.Date.AddHours 10.0) (now.Date.AddHours 11.0) ] with
            Mode = CalendarLogic.ViewMode.Day }

    let root = render model

    Assert.True(says "Reading week" root)
    Assert.True(says "Bank holiday" root)
    Assert.True(says "Supervision" root)

[<Fact>]
let ``with no all-day entry the band is not drawn at all`` () =
    let model =
        { withEvents [ eventAt "Supervision" (now.Date.AddHours 10.0) (now.Date.AddHours 11.0) ] with
            Mode = CalendarLogic.ViewMode.Day }

    Assert.False(says "All day" (render model))

[<Fact>]
let ``an all-day draft resolves to local midnight to midnight`` () =
    let draft =
        { CalendarLogic.Original = None
          CalendarLogic.Title = "Reading week"
          CalendarLogic.KindIndex = EventKind.toInt EventKind.Personal
          CalendarLogic.AllDay = true
          CalendarLogic.StartText = "2026-08-05"
          CalendarLogic.EndText = ""
          CalendarLogic.Location = ""
          CalendarLogic.ModuleIndex = 0
          CalendarLogic.Notes = "" }

    match CalendarLogic.resolveTimes draft with
    | Some(s, e) ->
        Assert.Equal(DateTime(2026, 8, 5), s)
        Assert.Equal(DateTime(2026, 8, 6), e)
    | None -> failwith "an all-day draft must resolve"

[<Fact>]
let ``an all-day draft is not asked for an end time`` () =
    let draft =
        { CalendarLogic.Original = None
          CalendarLogic.Title = "Reading week"
          CalendarLogic.KindIndex = 0
          CalendarLogic.AllDay = true
          CalendarLogic.StartText = "2026-08-05"
          CalendarLogic.EndText = ""
          CalendarLogic.Location = ""
          CalendarLogic.ModuleIndex = 0
          CalendarLogic.Notes = "" }

    Assert.Equal(None, CalendarLogic.draftError draft)

    // The field labels are watermarks, not body text.
    let marks (root: IView) = all root |> List.choose (stringProp "Watermark")

    let allDayMarks = marks (render { ready () with Draft = Some draft })
    Assert.DoesNotContain(allDayMarks, fun (m: string) -> m.StartsWith("Ends", StringComparison.Ordinal))
    Assert.Contains(allDayMarks, fun (m: string) -> m.StartsWith("Date", StringComparison.Ordinal))

    // A timed draft does ask for one.
    let timed = { draft with AllDay = false; StartText = "2026-08-05 09:00"; EndText = "2026-08-05 10:00" }
    let timedMarks = marks (render { ready () with Draft = Some timed })
    Assert.Contains(timedMarks, fun (m: string) -> m.StartsWith("Ends", StringComparison.Ordinal))

// ---------------------------------------------------------------------------
// 5. overlap lanes
// ---------------------------------------------------------------------------

let private at (h: float) (mins: float) = now.Date.AddHours(h).AddMinutes mins

let private laid (entries: CalendarLogic.Entry list) = CalendarLogic.layoutDay now.Date entries

[<Fact>]
let ``entries that do not overlap all take one lane`` () =
    let placed =
        laid
            [ ev (eventAt "A" (at 9.0 0.0) (at 10.0 0.0))
              ev (eventAt "B" (at 11.0 0.0) (at 12.0 0.0)) ]

    Assert.Equal(2, placed.Length)
    Assert.All(placed, fun p -> Assert.Equal(1, p.Lanes))
    Assert.All(placed, fun p -> Assert.Equal(0, p.Lane))

[<Fact>]
let ``two overlapping entries take one lane each`` () =
    let placed =
        laid
            [ ev (eventAt "A" (at 9.0 0.0) (at 11.0 0.0))
              ev (eventAt "B" (at 10.0 0.0) (at 12.0 0.0)) ]

    Assert.All(placed, fun p -> Assert.Equal(2, p.Lanes))
    Assert.Equal<int list>([ 0; 1 ], placed |> List.map (fun p -> p.Lane) |> List.sort)

[<Fact>]
let ``touching exactly at a boundary is not an overlap`` () =
    let a = ev (eventAt "A" (at 9.0 0.0) (at 10.0 0.0))
    let b = ev (eventAt "B" (at 10.0 0.0) (at 11.0 0.0))

    Assert.False(CalendarLogic.overlapping a b)

    let placed = laid [ a; b ]
    Assert.All(placed, fun p -> Assert.Equal(1, p.Lanes))

[<Fact>]
let ``a chained overlap keeps every entry visible`` () =
    // A–B overlap, B–C overlap, A–C do not. All three are in one cluster.
    let placed =
        laid
            [ ev (eventAt "A" (at 9.0 0.0) (at 10.0 30.0))
              ev (eventAt "B" (at 10.0 0.0) (at 11.0 30.0))
              ev (eventAt "C" (at 11.0 0.0) (at 12.0 0.0)) ]

    Assert.Equal(3, placed.Length)
    Assert.All(placed, fun p -> Assert.True(p.Lanes >= 2))

    // A and C do not overlap, so C may reuse A's lane — but B never shares.
    let laneOf title = placed |> List.find (fun p -> CalendarLogic.Entry.title p.Entry = title) |> fun p -> p.Lane
    Assert.NotEqual(laneOf "A", laneOf "B")
    Assert.NotEqual(laneOf "B", laneOf "C")

[<Fact>]
let ``a nested entry gets its own lane`` () =
    let placed =
        laid
            [ ev (eventAt "Outer" (at 9.0 0.0) (at 13.0 0.0))
              ev (eventAt "Inner" (at 10.0 0.0) (at 11.0 0.0)) ]

    Assert.All(placed, fun p -> Assert.Equal(2, p.Lanes))
    Assert.Equal(2, (placed |> List.map (fun p -> p.Lane) |> List.distinct).Length)

[<Fact>]
let ``identical intervals each get their own lane`` () =
    let placed =
        laid
            [ ev (eventAt "A" (at 9.0 0.0) (at 10.0 0.0))
              ev (eventAt "B" (at 9.0 0.0) (at 10.0 0.0))
              ev (eventAt "C" (at 9.0 0.0) (at 10.0 0.0)) ]

    Assert.Equal(3, placed.Length)
    Assert.All(placed, fun p -> Assert.Equal(3, p.Lanes))
    Assert.Equal(3, (placed |> List.map (fun p -> p.Lane) |> List.distinct).Length)

[<Fact>]
let ``the same start with different ends still separates`` () =
    let placed =
        laid
            [ ev (eventAt "Short" (at 9.0 0.0) (at 9.0 30.0))
              ev (eventAt "Long" (at 9.0 0.0) (at 12.0 0.0)) ]

    Assert.All(placed, fun p -> Assert.Equal(2, p.Lanes))
    Assert.Equal(2, (placed |> List.map (fun p -> p.Lane) |> List.distinct).Length)

[<Fact>]
let ``a zero-duration entry still has a clickable height`` () =
    let placed = laid [ ev (eventAt "Instant" (at 9.0 0.0) (at 9.0 0.0)) ]

    Assert.Single placed |> ignore
    Assert.Equal(CalendarLogic.Geometry.minEventHeight, (List.head placed).Height, 6)

    // The stored values are untouched — only the drawn height has a floor.
    Assert.Equal(at 9.0 0.0, CalendarLogic.Entry.start (List.head placed).Entry)
    Assert.Equal(at 9.0 0.0, CalendarLogic.Entry.finish (List.head placed).Entry)

[<Fact>]
let ``many simultaneous entries all stay visible`` () =
    let placed = laid [ for i in 1..8 -> ev (eventAt (string i) (at 9.0 0.0) (at 10.0 0.0)) ]

    Assert.Equal(8, placed.Length)
    Assert.All(placed, fun p -> Assert.Equal(8, p.Lanes))
    Assert.Equal(8, (placed |> List.map (fun p -> p.Lane) |> List.distinct).Length)

[<Fact>]
let ``lane assignment is deterministic`` () =
    let entries =
        [ ev (eventAt "A" (at 9.0 0.0) (at 11.0 0.0))
          ev (eventAt "B" (at 10.0 0.0) (at 12.0 0.0))
          ev (eventAt "C" (at 10.0 30.0) (at 11.0 30.0)) ]

    let once = laid entries |> List.map (fun p -> CalendarLogic.Entry.title p.Entry, p.Lane, p.Lanes)
    let twice = laid (List.rev entries) |> List.map (fun p -> CalendarLogic.Entry.title p.Entry, p.Lane, p.Lanes)

    Assert.Equal<(string * int * int) list>(List.sort once, List.sort twice)

[<Fact>]
let ``geometry follows the stored times`` () =
    let top = CalendarLogic.topOf now.Date (at 9.0 0.0)
    Assert.Equal(9.0 * CalendarLogic.Geometry.hourHeight, top, 6)

    let height = CalendarLogic.heightOf now.Date (at 9.0 0.0) (at 11.0 0.0)
    Assert.Equal(2.0 * CalendarLogic.Geometry.hourHeight, height, 6)

    // A half-hour is half an hour's height, not a rounded block.
    let half = CalendarLogic.heightOf now.Date (at 9.0 0.0) (at 9.0 30.0)
    Assert.Equal(CalendarLogic.Geometry.hourHeight / 2.0, half, 6)

[<Fact>]
let ``an entry that began yesterday still draws from the top of today`` () =
    let overnight = ev (eventAt "Overnight" (now.Date.AddHours -3.0) (now.Date.AddHours 2.0))
    let placed = laid [ overnight ]

    Assert.Equal(0.0, (List.head placed).Top, 6)
    Assert.True((List.head placed).Height > 0.0)

[<Fact>]
let ``separate days never interfere`` () =
    let today = ev (eventAt "Today" (at 9.0 0.0) (at 10.0 0.0))
    let tomorrow = ev (eventAt "Tomorrow" (now.Date.AddDays(1.0).AddHours 9.0) (now.Date.AddDays(1.0).AddHours 10.0))

    Assert.Single(CalendarLogic.onDay now.Date [ today; tomorrow ]) |> ignore
    Assert.Single(CalendarLogic.onDay (now.Date.AddDays 1.0) [ today; tomorrow ]) |> ignore

[<Fact>]
let ``an entry ending exactly at midnight belongs to the day it ran in`` () =
    let entry = ev (eventAt "Late" (at 22.0 0.0) (now.Date.AddDays 1.0))

    Assert.Single(CalendarLogic.onDay now.Date [ entry ]) |> ignore
    Assert.Empty(CalendarLogic.onDay (now.Date.AddDays 1.0) [ entry ])

// ---------------------------------------------------------------------------
// 6. sources kept apart
// ---------------------------------------------------------------------------

[<Fact>]
let ``an event and a session are different sources with different glyphs`` () =
    let e = ev (eventAt "Supervision" now (now.AddHours 1.0))
    let s = sess (sessionAt "Revise" now (now.AddHours 1.0))

    Assert.Equal(CalendarLogic.Source.Events, CalendarLogic.Entry.source e)
    Assert.Equal(CalendarLogic.Source.Sessions, CalendarLogic.Entry.source s)
    Assert.NotEqual<string>(CalendarLogic.Source.glyph CalendarLogic.Source.Events, CalendarLogic.Source.glyph CalendarLogic.Source.Sessions)
    Assert.NotEqual<string>(CalendarLogic.Entry.kindLabel e, CalendarLogic.Entry.kindLabel s)

[<Fact>]
let ``a session's kind is never inferred from its title`` () =
    // A session called "exam" is still a planner session.
    let s = sess (sessionAt "Revise for the exam" now (now.AddHours 1.0))
    Assert.Equal("Planner session", CalendarLogic.Entry.kindLabel s)

[<Fact>]
let ``an event's kind comes from the record`` () =
    for kind in EventKind.all do
        let e = ev { eventAt "Anything" now (now.AddHours 1.0) with Kind = kind }
        Assert.Equal(EventKind.label kind, CalendarLogic.Entry.kindLabel e)

[<Fact>]
let ``the two sources are labelled on screen, not merged`` () =
    let model =
        { withEvents [ eventAt "Supervision" (now.Date.AddHours 10.0) (now.Date.AddHours 11.0) ] with
            Sessions = [ sessionAt "Revise compilers" (now.Date.AddHours 13.0) (now.Date.AddHours 15.0) ]
            Mode = CalendarLogic.ViewMode.Agenda }

    let root = render model

    Assert.True(says "Supervision" root)
    Assert.True(says "Revise compilers" root)
    Assert.True(says "Planner session" root)

[<Fact>]
let ``entries have distinct identities across sources`` () =
    let e = ev (eventAt "Same title" now (now.AddHours 1.0))
    let s = sess (sessionAt "Same title" now (now.AddHours 1.0))

    Assert.NotEqual<string>(CalendarLogic.Entry.key e, CalendarLogic.Entry.key s)
    Assert.False(CalendarLogic.Entry.sameAs e s)

// ---------------------------------------------------------------------------
// 7. filters
// ---------------------------------------------------------------------------

[<Fact>]
let ``a source filter narrows every mode the same way`` () =
    let model =
        { withEvents [ eventAt "Supervision" (now.Date.AddHours 10.0) (now.Date.AddHours 11.0) ] with
            Sessions = [ sessionAt "Revise compilers" (now.Date.AddHours 13.0) (now.Date.AddHours 15.0) ]
            Filter = { CalendarLogic.Filter.none with Sources = Set.ofList [ CalendarLogic.Source.Events ] } }

    for mode in CalendarLogic.ViewMode.all do
        let root = render { model with Mode = mode }
        Assert.True(says "Supervision" root, string mode)
        Assert.False(says "Revise compilers" root, string mode)

[<Fact>]
let ``a kind filter can only ever narrow to events`` () =
    let entries =
        [ ev { eventAt "Exam" now (now.AddHours 2.0) with Kind = EventKind.Exam }
          ev { eventAt "Meeting" now (now.AddHours 1.0) with Kind = EventKind.Meeting }
          sess (sessionAt "Revise" now (now.AddHours 1.0)) ]

    let filter = { CalendarLogic.Filter.none with Kinds = Set.ofList [ EventKind.Exam ] }
    let kept = CalendarLogic.applyFilter [] filter entries

    Assert.Single kept |> ignore
    Assert.Equal("Exam", CalendarLogic.Entry.title (List.head kept))

[<Fact>]
let ``a module filter resolves a session's module by its code`` () =
    let m = moduleOf "CS4001" "Compilers"

    let entries =
        [ ev { eventAt "Supervision" now (now.AddHours 1.0) with ModuleId = Some m.Id }
          sess { sessionAt "Revise" now (now.AddHours 1.0) with ModuleCode = Some "CS4001" }
          sess { sessionAt "Other" now (now.AddHours 1.0) with ModuleCode = Some "CS9999" } ]

    let filter = { CalendarLogic.Filter.none with Modules = Set.ofList [ m.Id.Value ] }
    let kept = CalendarLogic.applyFilter [ m ] filter entries

    Assert.Equal(2, kept.Length)
    Assert.DoesNotContain(kept, fun e -> CalendarLogic.Entry.title e = "Other")

[<Fact>]
let ``only sources and kinds that actually appear get a chip`` () =
    let onlyEvents = [ ev { eventAt "Meeting" now (now.AddHours 1.0) with Kind = EventKind.Meeting } ]

    Assert.Equal<CalendarLogic.Source list>([ CalendarLogic.Source.Events ], CalendarLogic.sourcesPresent onlyEvents)
    Assert.Equal<EventKind list>([ EventKind.Meeting ], CalendarLogic.kindsPresent onlyEvents)

    let root = render (withEvents [ { eventAt "Meeting" now (now.AddHours 1.0) with Kind = EventKind.Meeting } ])
    Assert.True(says "Meeting" root)
    Assert.False(says "Planner sessions" root)

[<Fact>]
let ``filter chips keep identical geometry in both states`` () =
    let model = withEvents [ eventAt "Supervision" now (now.AddHours 1.0) ]
    let off = render model
    let on = render { model with Filter = { CalendarLogic.Filter.none with Kinds = Set.ofList [ EventKind.Meeting ] } }

    let chipBox (root: IView) =
        ofType "Button" root
        |> List.filter (fun b ->
            stringProp "Name" b
            |> Option.map (fun n -> n.StartsWith("Meeting filter", StringComparison.Ordinal))
            |> Option.defaultValue false)
        |> List.map (fun b -> propOf "Padding" b, propOf "MinHeight" b, propOf "BorderThickness" b)

    Assert.NotEmpty(chipBox off)
    Assert.Equal<(obj option * obj option * obj option) list>(chipBox off, chipBox on)

[<Fact>]
let ``a selection hidden by a filter is cleared, not moved`` () =
    let supervision = eventAt "Supervision" (now.Date.AddHours 10.0) (now.Date.AddHours 11.0)
    let session = sessionAt "Revise" (now.Date.AddHours 13.0) (now.Date.AddHours 15.0)

    let model = { withEvents [ supervision ] with Sessions = [ session ] }
    let selected = CalendarView.update noServices (CalendarView.Select(sess session)) model |> modelOf
    Assert.True(selected.Selected.IsSome)

    // The chips are inclusive: choosing Calendar events narrows to them, which
    // leaves the selected session with nowhere to point.
    let filtered =
        CalendarView.update noServices (CalendarView.ToggleSource CalendarLogic.Source.Events) selected |> modelOf

    Assert.True(filtered.Selected.IsNone)

[<Fact>]
let ``a selection that survives a filter is kept`` () =
    let supervision = eventAt "Supervision" (now.Date.AddHours 10.0) (now.Date.AddHours 11.0)
    let model = { withEvents [ supervision ] with Sessions = [ sessionAt "Revise" now (now.AddHours 1.0) ] }

    let selected = CalendarView.update noServices (CalendarView.Select(ev supervision)) model |> modelOf

    let filtered =
        CalendarView.update noServices (CalendarView.ToggleSource CalendarLogic.Source.Events) selected |> modelOf

    Assert.True(filtered.Selected.IsSome)

[<Fact>]
let ``a filter that hides everything says so and how to undo it`` () =
    let model =
        { withEvents [ eventAt "Supervision" now (now.AddHours 1.0) ] with
            Filter = { CalendarLogic.Filter.none with Sources = Set.ofList [ CalendarLogic.Source.Sessions ] } }

    let root = render model
    Assert.True(says "Every item is hidden by the filters above" root)
    Assert.True(says "Choose All" root)

[<Fact>]
let ``clearing the filters restores everything`` () =
    let model =
        { withEvents [ eventAt "Supervision" now (now.AddHours 1.0) ] with
            Filter = { CalendarLogic.Filter.none with Kinds = Set.ofList [ EventKind.Exam ] } }

    let cleared = CalendarView.update noServices CalendarView.ClearFilters model |> modelOf
    Assert.True(CalendarLogic.Filter.isAll cleared.Filter)

// ---------------------------------------------------------------------------
// 8. agenda
// ---------------------------------------------------------------------------

[<Fact>]
let ``agenda grouping is deterministic and local-date`` () =
    Assert.Equal("Today", CalendarLogic.agendaGroup now firstDay (now.Date.AddHours 23.0))
    Assert.Equal("Tomorrow", CalendarLogic.agendaGroup now firstDay (now.Date.AddDays 1.0))
    Assert.Equal("Later", CalendarLogic.agendaGroup now firstDay (now.Date.AddDays 60.0))

    // Yesterday is not "today" — it is in the past, so it falls to Later.
    Assert.Equal("Later", CalendarLogic.agendaGroup now firstDay (now.Date.AddDays -1.0))

[<Fact>]
let ``empty agenda groups are omitted`` () =
    let entries = [ ev (eventAt "Today only" (now.Date.AddHours 10.0) (now.Date.AddHours 11.0)) ]
    let groups = CalendarLogic.agendaGroups now firstDay entries

    Assert.Single groups |> ignore
    Assert.Equal("Today", fst (List.head groups))

[<Fact>]
let ``agenda groups appear in their fixed order`` () =
    Assert.Equal<string list>([ "Today"; "Tomorrow"; "This week"; "Later" ], CalendarLogic.agendaOrder)

    let entries =
        [ ev (eventAt "Far" (now.Date.AddDays 60.0) (now.Date.AddDays(60.0).AddHours 1.0))
          ev (eventAt "Now" (now.Date.AddHours 10.0) (now.Date.AddHours 11.0))
          ev (eventAt "Soon" (now.Date.AddDays 1.0) (now.Date.AddDays(1.0).AddHours 1.0)) ]

    let labels = CalendarLogic.agendaGroups now firstDay entries |> List.map fst
    Assert.Equal<string list>([ "Today"; "Tomorrow"; "Later" ], labels)

[<Fact>]
let ``nothing in the agenda is called urgent`` () =
    let model =
        { withEvents [ { eventAt "Coursework due" (now.Date.AddHours 16.0) (now.Date.AddHours 17.0) with Kind = EventKind.Deadline } ] with
            Mode = CalendarLogic.ViewMode.Agenda }

    let shown = String.concat " " (words (render model)) |> fun s -> s.ToLowerInvariant()

    for absent in [ "urgent"; "overdue"; "critical"; "at risk" ] do
        Assert.DoesNotContain(absent, shown)

[<Fact>]
let ``an empty agenda says so without collapsing the screen`` () =
    let root = render { ready () with Mode = CalendarLogic.ViewMode.Agenda }

    Assert.True(says "Nothing scheduled in the next four weeks." root)
    // The controls are still there.
    Assert.True(says "Today" root)
    Assert.True(says "Calendar" root)

// ---------------------------------------------------------------------------
// 9. composition and the modes as rendered
// ---------------------------------------------------------------------------

[<Fact>]
let ``the page sections are the mapped ones, in order`` () =
    Assert.Equal<CalendarLogic.Section list>(
        [ CalendarLogic.Section.Header
          CalendarLogic.Section.Navigation
          CalendarLogic.Section.Modes
          CalendarLogic.Section.Filters
          CalendarLogic.Section.Workspace
          CalendarLogic.Section.Side ],
        CalendarLogic.sectionOrder
    )

[<Fact>]
let ``the controls carry navigation, modes and a create action`` () =
    let root = render (ready ())

    Assert.Contains("Previous", accessibleNames root)
    Assert.Contains("Next", accessibleNames root)
    Assert.True(says "Today" root)
    Assert.Contains("Create an event", accessibleNames root)

    for mode in CalendarLogic.ViewMode.all do
        Assert.True(says (CalendarLogic.ViewMode.label mode) root, CalendarLogic.ViewMode.label mode)

[<Fact>]
let ``the week grid has seven day columns from the shared week start`` () =
    let model = { ready () with Mode = CalendarLogic.ViewMode.Week }
    let root = render model
    let start = CalendarLogic.weekStart firstDay now

    for i in 0..6 do
        let day = start.AddDays(float i)
        Assert.True(says (string day.Day) root, string day)

[<Fact>]
let ``the day view says when a day is empty`` () =
    let root = render { ready () with Mode = CalendarLogic.ViewMode.Day }
    Assert.True(says "No scheduled items for this day." root)

[<Fact>]
let ``the current-time marker appears only when the range holds today`` () =
    let entries = [ eventAt "Supervision" (now.Date.AddHours 10.0) (now.Date.AddHours 11.0) ]

    let markers (model: CalendarView.Model) =
        render model
        |> all
        |> List.filter (fun v -> floatProp "Height" v = Some 1.5)
        |> List.length

    // Today: one marker.
    Assert.Equal(1, markers { withEvents entries with Mode = CalendarLogic.ViewMode.Day })

    // A different day: none.
    Assert.Equal(
        0,
        markers { withEvents entries with Mode = CalendarLogic.ViewMode.Day; Anchor = now.Date.AddDays 5.0 }
    )

[<Fact>]
let ``the month view marks today and shows adjacent-month days`` () =
    let root = render { ready () with Mode = CalendarLogic.ViewMode.Month }
    let cells = CalendarLogic.monthCells firstDay now

    // Every one of the 42 dates has a button to open it.
    let openNames =
        accessibleNames root |> List.filter (fun n -> n.StartsWith("Open ", StringComparison.Ordinal))

    Assert.Equal(cells.Length, openNames.Length)

// ---------------------------------------------------------------------------
// 10. CRUD
// ---------------------------------------------------------------------------

let private draftOf title start finish : CalendarLogic.Draft =
    { CalendarLogic.Original = None
      CalendarLogic.Title = title
      CalendarLogic.KindIndex = EventKind.toInt EventKind.Meeting
      CalendarLogic.AllDay = false
      CalendarLogic.StartText = start
      CalendarLogic.EndText = finish
      CalendarLogic.Location = ""
      CalendarLogic.ModuleIndex = 0
      CalendarLogic.Notes = "" }

[<Fact>]
let ``a new event opens an empty draft`` () =
    let after = CalendarView.update noServices CalendarView.NewEvent (ready ()) |> modelOf

    Assert.True(after.Draft.IsSome)
    Assert.Equal("", after.Draft.Value.Title)
    Assert.False(after.Draft.Value.AllDay)

[<Fact>]
let ``a title is required`` () =
    Assert.Equal(Some "A title is required.", CalendarLogic.draftError (draftOf "" "2026-08-05 09:00" "2026-08-05 10:00"))
    Assert.Equal(None, CalendarLogic.draftError (draftOf "Supervision" "2026-08-05 09:00" "2026-08-05 10:00"))

[<Fact>]
let ``the end must be after the start`` () =
    let bad = draftOf "Supervision" "2026-08-05 10:00" "2026-08-05 09:00"
    Assert.Equal(Some "The end must be after the start.", CalendarLogic.draftError bad)
    Assert.Equal(None, CalendarLogic.resolveTimes bad)

    // Equal times are not a range either.
    let equal = draftOf "Supervision" "2026-08-05 10:00" "2026-08-05 10:00"
    Assert.True((CalendarLogic.draftError equal).IsSome)

[<Fact>]
let ``an omitted end becomes one hour`` () =
    match CalendarLogic.resolveTimes (draftOf "Supervision" "2026-08-05 09:00" "") with
    | Some(s, e) -> Assert.Equal(1.0, (e - s).TotalHours, 6)
    | None -> failwith "an omitted end must still resolve"

[<Fact>]
let ``an unparseable time is refused with a reason`` () =
    let bad = draftOf "Supervision" "not a date" "2026-08-05 10:00"
    Assert.True((CalendarLogic.draftError bad).IsSome)
    Assert.Contains("yyyy-MM-dd", (CalendarLogic.draftError bad).Value)

[<Fact>]
let ``a second save while saving does nothing`` () =
    let draft = draftOf "Supervision" "2026-08-05 09:00" "2026-08-05 10:00"

    Assert.True(CalendarLogic.canSave false draft)
    Assert.False(CalendarLogic.canSave true draft)

    let saving = { ready () with Draft = Some draft; Saving = true }
    let after = CalendarView.update noServices CalendarView.SaveEvent saving

    Assert.True(List.isEmpty (cmdOf after))
    Assert.True((modelOf after).Draft.IsSome)

[<Fact>]
let ``an invalid draft cannot be saved`` () =
    let invalid = { ready () with Draft = Some(draftOf "" "2026-08-05 09:00" "2026-08-05 10:00") }
    let after = CalendarView.update noServices CalendarView.SaveEvent invalid

    Assert.True(List.isEmpty (cmdOf after))

[<Fact>]
let ``cancelling a draft discards it and changes nothing else`` () =
    let model = { withEvents [ eventAt "Supervision" now (now.AddHours 1.0) ] with Draft = Some(draftOf "x" "2026-08-05 09:00" "") }
    let after = CalendarView.update noServices CalendarView.CloseDraft model |> modelOf

    Assert.True(after.Draft.IsNone)
    Assert.Equal(model.Events.Length, after.Events.Length)

[<Fact>]
let ``only a stored event can be edited here`` () =
    let supervision = eventAt "Supervision" now (now.AddHours 1.0)
    let session = sessionAt "Revise" now (now.AddHours 1.0)
    let model = { withEvents [ supervision ] with Sessions = [ session ] }

    let eventSelected = { model with Selected = Some(ev supervision) }
    Assert.True((CalendarView.update noServices CalendarView.EditSelected eventSelected |> modelOf).Draft.IsSome)

    // A planner session belongs to the Planner.
    let sessionSelected = { model with Selected = Some(sess session) }
    Assert.True((CalendarView.update noServices CalendarView.EditSelected sessionSelected |> modelOf).Draft.IsNone)

[<Fact>]
let ``a selected session offers the Planner, not Edit or Delete`` () =
    let session = sessionAt "Revise" (now.Date.AddHours 13.0) (now.Date.AddHours 15.0)
    let model = { ready () with Sessions = [ session ]; Selected = Some(sess session) }
    let root = render model

    Assert.True(says "Open Planner" root)
    Assert.True(says CalendarLogic.sessionNotice root)
    Assert.False(says "Delete" root)

[<Fact>]
let ``deleting asks first, and cancelling changes nothing`` () =
    let supervision = eventAt "Supervision" now (now.AddHours 1.0)
    let model = { withEvents [ supervision ] with Selected = Some(ev supervision) }

    Assert.False(says "Delete this event permanently?" (render model))

    let asked = CalendarView.update noServices CalendarView.RequestDelete model |> modelOf
    Assert.True(asked.PendingDelete)
    Assert.True(says "Delete this event permanently?" (render asked))

    let cancelled = CalendarView.update noServices CalendarView.CancelDelete asked |> modelOf
    Assert.False(cancelled.PendingDelete)
    Assert.Equal(model.Events.Length, cancelled.Events.Length)

[<Fact>]
let ``deleting a session is refused`` () =
    let session = sessionAt "Revise" now (now.AddHours 1.0)
    let model = { ready () with Sessions = [ session ]; Selected = Some(sess session); PendingDelete = true }

    let after = CalendarView.update noServices CalendarView.ConfirmDelete model
    Assert.True(List.isEmpty (cmdOf after))
    Assert.False((modelOf after).PendingDelete)

[<Fact>]
let ``a failure is reported and loses nothing`` () =
    let model = withEvents [ eventAt "Supervision" now (now.AddHours 1.0) ]
    let failed = CalendarView.update noServices (CalendarView.Failed(exn "the database is locked")) model |> modelOf

    Assert.Equal(Some "the database is locked", failed.Error)
    Assert.Equal(model.Events.Length, failed.Events.Length)
    Assert.False(failed.Saving)
    Assert.False(failed.Loading)

    let root = render failed
    Assert.True(says "The calendar could not be loaded: the database is locked" root)
    Assert.True(says "Try again" root)
    // The controls survive — the page does not collapse into one warning card.
    Assert.True(says "Calendar" root)
    Assert.Contains("Previous", accessibleNames root)

[<Fact>]
let ``the editor offers nothing the row cannot persist`` () =
    let root = render { ready () with Draft = Some(draftOf "Supervision" "2026-08-05 09:00" "2026-08-05 10:00") }
    let shown = String.concat " " (words root) |> fun s -> s.ToLowerInvariant()

    for absent in [ "repeat"; "recurrence"; "reminder"; "notify"; "participant"; "attendee"; "guest"; "sync" ] do
        Assert.DoesNotContain(absent, shown)

// ---------------------------------------------------------------------------
// 11. honesty about import, dragging and sessions
// ---------------------------------------------------------------------------

[<Fact>]
let ``an imported event says it was imported, never that it syncs`` () =
    let imported = { eventAt "Lecture" now (now.AddHours 1.0) with Source = Some "google-calendar" }
    let model = { withEvents [ imported ] with Selected = Some(ev imported) }
    let root = render model

    Assert.True(says "google-calendar" root)
    Assert.True(says CalendarLogic.importNotice root)
    Assert.Contains("not kept in sync", CalendarLogic.importNotice)

    let shown = String.concat " " (words root) |> fun s -> s.ToLowerInvariant()

    for overclaim in [ "live sync"; "two-way"; "connected calendar"; "continuously" ] do
        Assert.DoesNotContain(overclaim, shown)

[<Fact>]
let ``an event created here shows no import notice`` () =
    let own = eventAt "Supervision" now (now.AddHours 1.0)
    let root = render { withEvents [ own ] with Selected = Some(ev own) }

    Assert.False(says CalendarLogic.importNotice root)

[<Fact>]
let ``rescheduling is explicit, and nothing implies dragging`` () =
    let supervision = eventAt "Supervision" now (now.AddHours 1.0)
    let root = render { withEvents [ supervision ] with Selected = Some(ev supervision) }

    Assert.True(says CalendarLogic.rescheduleNotice root)
    Assert.Contains("Dragging is not supported", CalendarLogic.rescheduleNotice)

    let shown = String.concat " " (words root) |> fun s -> s.ToLowerInvariant()

    for absent in [ "drag to"; "drop here"; "resize" ] do
        Assert.DoesNotContain(absent, shown)

// ---------------------------------------------------------------------------
// 12. responsive
// ---------------------------------------------------------------------------

[<Fact>]
let ``the side panel is re-placed by width, never removed`` () =
    Assert.Equal(CalendarLogic.SidePlacement.Rail, (CalendarLogic.placementFor 1600.0).Side)
    Assert.Equal(CalendarLogic.SidePlacement.Below, (CalendarLogic.placementFor 1000.0).Side)
    Assert.Equal(CalendarLogic.SidePlacement.Below, (CalendarLogic.placementFor 700.0).Side)

    for width in [ 1600.0; 1000.0; 700.0 ] do
        Assert.True(says "NEXT UP" (render { ready () with Width = width }), string width)

[<Fact>]
let ``narrow adapts the presentation without changing the stored mode`` () =
    let narrow = { CalendarLogic.Side = CalendarLogic.SidePlacement.Below; CalendarLogic.Narrow = true }
    let wide = { CalendarLogic.Side = CalendarLogic.SidePlacement.Rail; CalendarLogic.Narrow = false }

    // Month and Week fall back to the agenda…
    Assert.Equal(CalendarLogic.ViewMode.Agenda, CalendarLogic.effectiveMode narrow CalendarLogic.ViewMode.Month)
    Assert.Equal(CalendarLogic.ViewMode.Agenda, CalendarLogic.effectiveMode narrow CalendarLogic.ViewMode.Week)

    // …Day is already usable, so it is left alone.
    Assert.Equal(CalendarLogic.ViewMode.Day, CalendarLogic.effectiveMode narrow CalendarLogic.ViewMode.Day)

    // Widening restores it with no user action.
    Assert.Equal(CalendarLogic.ViewMode.Month, CalendarLogic.effectiveMode wide CalendarLogic.ViewMode.Month)

[<Fact>]
let ``the stored mode really survives a narrow render, and the screen says why`` () =
    let model = { ready () with Mode = CalendarLogic.ViewMode.Month; Width = 700.0 }
    let root = render model

    Assert.Equal(CalendarLogic.ViewMode.Month, model.Mode)
    Assert.True(says "needs a wider window" root)
    Assert.True(says "Widen the window" root)

    // A resize never rewrites the stored mode.
    let resized = CalendarView.update noServices (CalendarView.Resized 700.0) { ready () with Mode = CalendarLogic.ViewMode.Month } |> modelOf
    Assert.Equal(CalendarLogic.ViewMode.Month, resized.Mode)

[<Fact>]
let ``a wide window says nothing about narrowing`` () =
    Assert.False(says "needs a wider window" (render { ready () with Mode = CalendarLogic.ViewMode.Month }))

[<Fact>]
let ``nothing in the screen scrolls sideways, at any width`` () =
    for width in [ 1600.0; 1000.0; 700.0 ] do
        for mode in CalendarLogic.ViewMode.all do
            let model =
                { withEvents [ eventAt "Supervision" (now.Date.AddHours 10.0) (now.Date.AddHours 11.0) ] with
                    Width = width
                    Mode = mode }

            for viewer in ofType "ScrollViewer" (render model) do
                Assert.Equal(Some(box ScrollBarVisibility.Disabled), propOf "HorizontalScrollBarVisibility" viewer)

// ---------------------------------------------------------------------------
// 13. Dark and Light, every accent
// ---------------------------------------------------------------------------

let private themeModes = [ ThemeMode.Dark; ThemeMode.Light ]
let private accentKeys = Palette.accents |> List.map fst

let private washedBy (alphaHex: string) (colour: Color) (surface: Color) =
    let a = Int32.Parse(alphaHex, Globalization.NumberStyles.HexNumber)
    Palette.composite surface (Color.FromArgb(byte a, colour.R, colour.G, colour.B))

/// Every colour an event can be painted with, plus representative module colours.
let private eventColours () =
    (EventKind.all |> List.map (fun k -> EventKind.label k, Color.Parse(Theme.eventKindHex k)))
    @ (Palette.accents |> List.map (fun (key, _) -> "module " + key, (Palette.ofKey key).Normal))

[<Fact>]
let ``an event's own ink is readable on its own wash, unselected and selected`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, raw in eventColours () do
                for alpha in
                    [ CalendarLogic.Geometry.eventFillAlpha; CalendarLogic.Geometry.selectedFillAlpha ] do
                    let wash = washedBy alpha raw s.Card
                    let ink = Palette.readableOn wash s.Text raw

                    Assert.True(
                        Palette.contrastRatio ink wash >= Palette.aaText,
                        sprintf "%A/%s %s @%s: %.2f" mode key name alpha (Palette.contrastRatio ink wash)
                    )

[<Fact>]
let ``an event's title stays readable on its wash — the colour is an accent, not the surface`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, raw in eventColours () do
                let wash = washedBy CalendarLogic.Geometry.eventFillAlpha raw s.Card

                Assert.True(
                    Palette.contrastRatio s.Text wash >= Palette.aaText,
                    sprintf "%A/%s %s title: %.2f" mode key name (Palette.contrastRatio s.Text wash)
                )

[<Fact>]
let ``a selected entry is visibly different from an unselected one`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for _, raw in eventColours () do
                let plain = washedBy CalendarLogic.Geometry.eventFillAlpha raw s.Card
                let selected = washedBy CalendarLogic.Geometry.selectedFillAlpha raw s.Card

                Assert.True(Palette.contrastRatio selected plain > 1.0, sprintf "%A/%s selection invisible" mode key)

                let edge = washedBy CalendarLogic.Geometry.selectedEdgeAlpha raw s.Card
                Assert.True(Palette.contrastRatio edge selected > 1.0, sprintf "%A/%s selection edge" mode key)

[<Fact>]
let ``the grid lines are visible without being loud`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)
            let line = washedBy CalendarLogic.Geometry.gridLineAlpha s.Text s.Card
            let ratio = Palette.contrastRatio line s.Card

            Assert.True(ratio > 1.0, sprintf "%A/%s grid line invisible" mode key)
            Assert.True(ratio < 2.2, sprintf "%A/%s grid line is loud: %.2f" mode key ratio)

[<Fact>]
let ``today's tint is felt without washing the day out`` () =
    for mode in themeModes do
        for key in accentKeys do
            let accent = Palette.ofKey key
            let s = Surfaces.resolve mode accent
            let tinted = washedBy CalendarLogic.Geometry.todayFillAlpha accent.Normal s.Card

            Assert.True(Palette.contrastRatio tinted s.Card > 1.0, sprintf "%A/%s today invisible" mode key)

            // Body text on today's column must still read.
            Assert.True(
                Palette.contrastRatio s.Text tinted >= Palette.aaText,
                sprintf "%A/%s text on today: %.2f" mode key (Palette.contrastRatio s.Text tinted)
            )

[<Fact>]
let ``a filter chip's ink is readable for every kind and module colour`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, raw in eventColours () do
                let wash = washedBy CalendarLogic.Geometry.chipFillAlpha raw s.Card
                let ink = Palette.readableOn wash s.Text raw

                Assert.True(
                    Palette.contrastRatio ink wash >= Palette.aaText,
                    sprintf "%A/%s %s chip: %.2f" mode key name (Palette.contrastRatio ink wash)
                )

[<Fact>]
let ``the surfaces stay warm in both themes`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, c in [ "card", s.Card; "cardAlt", s.CardAlt; "overlay", s.Overlay ] do
                Assert.True(int c.R >= int c.B, sprintf "%A/%s %s is cool: %A" mode key name c)

// ---------------------------------------------------------------------------
// 14. mutation guards
// ---------------------------------------------------------------------------

[<Fact>]
let ``MUTATION a local Monday calculation replacing the shared week start is caught`` () =
    // The shared definition follows the culture. A hard-coded Monday does not.
    for day in 0..6 do
        let date = DateTime(2026, 8, 3).AddDays(float day)
        Assert.Equal(StudyTime.weekStart firstDay date, CalendarLogic.weekStart firstDay date)

    // And a Sunday-first culture must give a different answer from Monday-first,
    // which is what makes copying a Monday calculation a real bug.
    let sunday = CalendarLogic.weekStart DayOfWeek.Sunday (DateTime(2026, 8, 5))
    let monday = CalendarLogic.weekStart DayOfWeek.Monday (DateTime(2026, 8, 5))
    Assert.NotEqual(sunday, monday)

[<Fact>]
let ``MUTATION merging the two sources is caught`` () =
    let e = ev (eventAt "Same" now (now.AddHours 1.0))
    let s = sess (sessionAt "Same" now (now.AddHours 1.0))

    Assert.NotEqual(CalendarLogic.Entry.source e, CalendarLogic.Entry.source s)
    Assert.NotEqual<string>(CalendarLogic.Entry.kindLabel e, CalendarLogic.Entry.kindLabel s)
    Assert.NotEqual<string>(CalendarLogic.Entry.key e, CalendarLogic.Entry.key s)

    // Filtering one source must not touch the other.
    let filter = { CalendarLogic.Filter.none with Sources = Set.ofList [ CalendarLogic.Source.Events ] }
    Assert.Equal<string list>([ "Same" ], CalendarLogic.applyFilter [] filter [ e; s ] |> List.map CalendarLogic.Entry.title)
    Assert.Equal(CalendarLogic.Source.Events, CalendarLogic.Entry.source (List.head (CalendarLogic.applyFilter [] filter [ e; s ])))

[<Fact>]
let ``MUTATION rendering all-day events as timed blocks is caught`` () =
    let entries = [ ev (allDayOn "Reading week" now); ev (eventAt "Timed" (at 9.0 0.0) (at 10.0 0.0)) ]

    // The all-day one never reaches the timed grid.
    let placed = CalendarLogic.layoutDay now.Date (CalendarLogic.timedOf entries)
    Assert.Single placed |> ignore
    Assert.Equal("Timed", CalendarLogic.Entry.title (List.head placed).Entry)

    Assert.Single(CalendarLogic.allDayOf entries) |> ignore

[<Fact>]
let ``MUTATION disabling lane assignment is caught`` () =
    let placed =
        laid
            [ ev (eventAt "A" (at 9.0 0.0) (at 11.0 0.0))
              ev (eventAt "B" (at 9.0 30.0) (at 11.0 30.0))
              ev (eventAt "C" (at 10.0 0.0) (at 12.0 0.0)) ]

    // Three mutually overlapping entries need three lanes, and three distinct
    // columns. Anything less means one is hidden behind another.
    Assert.All(placed, fun p -> Assert.Equal(3, p.Lanes))
    Assert.Equal(3, (placed |> List.map (fun p -> p.Lane) |> List.distinct).Length)

[<Fact>]
let ``MUTATION a selection surviving its own removal is caught`` () =
    let a = ev (eventAt "A" now (now.AddHours 1.0))
    let b = ev (eventAt "B" now (now.AddHours 1.0))

    Assert.Equal(Some a, CalendarLogic.reconcileSelection (Some a) [ a; b ])
    Assert.Equal(None, CalendarLogic.reconcileSelection (Some a) [ b ])
    Assert.Equal(None, CalendarLogic.reconcileSelection (Some a) [])

[<Fact>]
let ``MUTATION compressing the week instead of adapting is caught`` () =
    let narrow = CalendarLogic.placementFor 700.0
    Assert.True narrow.Narrow

    // A narrow window must not try to draw seven columns.
    Assert.NotEqual(CalendarLogic.ViewMode.Week, CalendarLogic.effectiveMode narrow CalendarLogic.ViewMode.Week)
    Assert.NotEqual(CalendarLogic.ViewMode.Month, CalendarLogic.effectiveMode narrow CalendarLogic.ViewMode.Month)

    // And the rendered tree really changes.
    let weekAtWidth (w: float) =
        render { ready () with Mode = CalendarLogic.ViewMode.Week; Width = w }

    Assert.False(says "needs a wider window" (weekAtWidth 1600.0))
    Assert.True(says "needs a wider window" (weekAtWidth 700.0))

[<Fact>]
let ``MUTATION removing the zero-duration floor is caught`` () =
    Assert.True(CalendarLogic.Geometry.minEventHeight > 0.0)

    let instant = CalendarLogic.heightOf now.Date (at 9.0 0.0) (at 9.0 0.0)
    Assert.Equal(CalendarLogic.Geometry.minEventHeight, instant, 6)

    // A one-minute entry is also floored rather than drawn as a hairline.
    let oneMinute = CalendarLogic.heightOf now.Date (at 9.0 0.0) (at 9.0 1.0)
    Assert.Equal(CalendarLogic.Geometry.minEventHeight, oneMinute, 6)

    // But a real duration is not inflated.
    Assert.True(CalendarLogic.heightOf now.Date (at 9.0 0.0) (at 11.0 0.0) > CalendarLogic.Geometry.minEventHeight)

[<Fact>]
let ``MUTATION Today navigating to the wrong focal date is caught`` () =
    Assert.Equal(now.Date, CalendarLogic.todayAnchor now)

    // Not the current anchor, and not a time-bearing value.
    let wandered = { ready () with Anchor = now.Date.AddMonths 3 }
    Assert.NotEqual(wandered.Anchor, CalendarLogic.todayAnchor now)
    Assert.Equal(TimeSpan.Zero, (CalendarLogic.todayAnchor now).TimeOfDay)

// ---------------------------------------------------------------------------
// 15. the shell contract
// ---------------------------------------------------------------------------

[<Fact>]
let ``Calendar draws its own glass, so the shell must not put a surface behind it`` () =
    Assert.True(Page.usesGlassSurface Page.Calendar)

[<Fact>]
let ``every migrated screen still draws its own glass`` () =
    for page in
        [ Page.Timeline
          Page.Dashboard
          Page.Planner
          Page.Inbox
          Page.Assignments
          Page.Modules
          Page.Lectures
          Page.Notes
          Page.Documents
          Page.Graph
          Page.Chat
          Page.Search
          Page.Calendar
          Page.Connections ] do
        Assert.True(Page.usesGlassSurface page, sprintf "%A lost its glass surface" page)

    for page, _, _ in Page.all do
        Assert.True(Page.usesGlassSurface page, sprintf "%A lost its glass surface" page)

[<Fact>]
let ``opening the Planner from a session navigates there`` () =
    let result = CalendarView.update noServices (CalendarView.GotoPage Page.Planner) (ready ())
    Assert.Equal(Some(CalendarView.Goto Page.Planner), externalOf result)

// ---------------------------------------------------------------------------
// 16. performance fixtures
// ---------------------------------------------------------------------------
//
// Pure decision cost (ranges, month cells, overlap layout, agenda grouping) and
// the cost of BUILDING each view tree. No FPS is claimed — no Avalonia
// application runs here, and no repository is reachable, which is itself the
// point: rendering must never touch one.

let private measure (runs: int) (work: unit -> int) =
    work () |> ignore
    GC.Collect()
    GC.WaitForPendingFinalizers()

    let before = GC.GetAllocatedBytesForCurrentThread()
    let times = ResizeArray<float>()

    for _ in 1..runs do
        let sw = Diagnostics.Stopwatch.StartNew()
        work () |> ignore
        sw.Stop()
        times.Add sw.Elapsed.TotalMilliseconds

    let allocated = GC.GetAllocatedBytesForCurrentThread() - before
    let sorted = times |> Seq.sort |> Seq.toArray
    sorted.[sorted.Length / 2], float allocated / float runs / 1024.0

/// `count` events spread across the visible month.
let private spread (count: int) =
    List.init count (fun i ->
        let day = now.Date.AddDays(float (i % 28))
        let hour = 8.0 + float (i % 10)
        eventAt (sprintf "Event number %d about coursework" i) (day.AddHours hour) (day.AddHours(hour + 1.0)))

/// `count` events all on the same day, all overlapping.
let private piled (count: int) =
    List.init count (fun i ->
        eventAt (sprintf "Overlapping %d" i) (now.Date.AddHours 9.0) (now.Date.AddHours(10.0 + float i * 0.25)))

[<Fact>]
let ``Calendar fixtures stay inside their budget`` () =
    let report = System.Text.StringBuilder()
    report.AppendLine "Calendar fixtures" |> ignore
    report.AppendLine "fixture | median ms | KB per run" |> ignore

    let record name runs work =
        let ms, kb = measure runs work
        report.AppendLine(sprintf "%-44s | %8.3f | %9.1f" name ms kb) |> ignore
        Assert.True(ms < 60.0, sprintf "%s took %.2f ms" name ms)

    // --- decision cost -----------------------------------------------------
    record "decide: visible range, all four modes" 500 (fun () ->
        CalendarLogic.ViewMode.all
        |> List.sumBy (fun m -> (CalendarLogic.visibleRange m firstDay now |> snd).Day))

    record "decide: month cells" 200 (fun () -> (CalendarLogic.monthCells firstDay now).Length)

    record "decide: overlap layout, 50 on one day" 100 (fun () ->
        (CalendarLogic.layoutDay now.Date (piled 50 |> List.map ev)).Length)

    record "decide: overlap layout, heavy chain" 100 (fun () ->
        let chained =
            List.init 60 (fun i ->
                ev (eventAt (string i) (now.Date.AddMinutes(float i * 20.0)) (now.Date.AddMinutes(float i * 20.0 + 90.0))))

        (CalendarLogic.layoutDay now.Date chained).Length)

    record "decide: agenda grouping, 500 entries" 50 (fun () ->
        (CalendarLogic.agendaGroups now firstDay (spread 500 |> List.map ev)).Length)

    record "decide: filter 500 entries" 100 (fun () ->
        let filter = { CalendarLogic.Filter.none with Kinds = Set.ofList [ EventKind.Meeting ] }
        (CalendarLogic.applyFilter [] filter (spread 500 |> List.map ev)).Length)

    // --- view construction -------------------------------------------------
    let build (model: CalendarView.Model) () = (all (render model)).Length

    record "view: empty month" 20 (build { ready () with Mode = CalendarLogic.ViewMode.Month })
    record "view: empty week" 20 (build (ready ()))

    for count in [ 20; 100; 500 ] do
        let model = withEvents (spread count)
        record (sprintf "view: month, %d events" count) 10 (build { model with Mode = CalendarLogic.ViewMode.Month })
        record (sprintf "view: week, %d events" count) 10 (build { model with Mode = CalendarLogic.ViewMode.Week })
        record (sprintf "view: agenda, %d events" count) 10 (build { model with Mode = CalendarLogic.ViewMode.Agenda })

    record "view: day, 50 events on one day" 10 (build { withEvents (piled 50) with Mode = CalendarLogic.ViewMode.Day })

    record "view: day, heavy overlap" 10 (build { withEvents (piled 30) with Mode = CalendarLogic.ViewMode.Day })

    record
        "view: 40 all-day events"
        10
        (build
            { withEvents (List.init 40 (fun i -> allDayOn (sprintf "All day %d" i) (now.Date.AddDays(float (i % 7))))) with
                Mode = CalendarLogic.ViewMode.Week })

    record
        "view: long titles"
        10
        (build
            { withEvents (
                  List.init 40 (fun i ->
                      eventAt (String.replicate 14 "averylongeventtitle") (now.Date.AddHours(float (i % 12))) (now.Date.AddHours(float (i % 12) + 1.0)))
              ) with
                Mode = CalendarLogic.ViewMode.Week })

    record
        "view: many module colours"
        10
        (build
            { withEvents (spread 60) with
                Modules = Palette.accents |> List.map (fun (key, _) -> moduleOf key key)
                Mode = CalendarLogic.ViewMode.Month })

    record "view: rapid range navigation" 10 (fun () ->
        let model = withEvents (spread 100)

        [ -2 .. 2 ]
        |> List.sumBy (fun step ->
            let anchor = CalendarLogic.step CalendarLogic.ViewMode.Month step now
            (all (render { model with Anchor = anchor; Mode = CalendarLogic.ViewMode.Month })).Length))

    record "view: rapid filter changes" 10 (fun () ->
        let model = withEvents (spread 100)

        [ EventKind.Meeting; EventKind.Exam; EventKind.Lecture ]
        |> List.sumBy (fun k ->
            (all (render { model with Filter = { CalendarLogic.Filter.none with Kinds = Set.ofList [ k ] } })).Length))

    IO.File.WriteAllText(IO.Path.Combine(IO.Path.GetTempPath(), "calendar-fixtures.txt"), report.ToString())
