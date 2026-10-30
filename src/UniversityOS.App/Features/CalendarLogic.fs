module UniversityOS.App.Features.CalendarLogic

open System
open UniversityOS.Domain
open UniversityOS.App

/// Every decision the Calendar screen makes, as pure functions.
///
/// **Two verified facts govern this screen.**
///
/// 1. The redesign's Week and Month areas are a **Gantt of module-project bars**
///    — progress percentages, milestone diamonds, projected states, AI tails.
///    None of that exists in this domain. Week and Month are therefore real
///    event calendars, and the Gantt apparatus is omitted rather than faked.
/// 2. `CalendarEvent.AllDay` is **stored and was never read** by the old view,
///    and nothing laid overlapping events into lanes. Both are implemented here.

// ---------------------------------------------------------------------------
// modes
// ---------------------------------------------------------------------------

/// The four modes that genuinely exist. The redesign offers five *zoom levels*
/// including semester and year; both would need a term model this application
/// does not have, so neither is offered.
[<RequireQualifiedAccess>]
type ViewMode =
    | Day
    | Week
    | Month
    | Agenda

[<RequireQualifiedAccess>]
module ViewMode =
    let all = [ ViewMode.Day; ViewMode.Week; ViewMode.Month; ViewMode.Agenda ]

    let label =
        function
        | ViewMode.Day -> "Day"
        | ViewMode.Week -> "Week"
        | ViewMode.Month -> "Month"
        | ViewMode.Agenda -> "Agenda"

    let ofIndex (i: int) =
        if i >= 0 && i < all.Length then all.[i] else ViewMode.Week

// ---------------------------------------------------------------------------
// entries — two sources, kept apart
// ---------------------------------------------------------------------------

/// **A calendar entry is one of two genuinely different things.** They are not
/// merged into a single domain type: a stored event is something the user (or an
/// import) put in the calendar; a planner session is something the planner
/// placed, carrying its own reason and completion state.
type Entry =
    | Ev of CalendarEvent
    | Sess of PlannedSession

[<RequireQualifiedAccess>]
type Source =
    | Events
    | Sessions

[<RequireQualifiedAccess>]
module Source =
    let all = [ Source.Events; Source.Sessions ]

    let label =
        function
        | Source.Events -> "Calendar events"
        | Source.Sessions -> "Planner sessions"

    /// A glyph as well as a colour, so the source is never carried by hue alone.
    let glyph =
        function
        | Source.Events -> "▦"
        | Source.Sessions -> "◷"

[<RequireQualifiedAccess>]
module Entry =

    let source =
        function
        | Ev _ -> Source.Events
        | Sess _ -> Source.Sessions

    let start =
        function
        | Ev e -> e.StartsAt
        | Sess s -> s.StartsAt

    let finish =
        function
        | Ev e -> e.EndsAt
        | Sess s -> s.EndsAt

    let title =
        function
        | Ev e -> e.Title
        | Sess s -> s.Title

    /// **Only a stored event can be all-day.** A planner session always has real
    /// times, because the planner placed it at one.
    let isAllDay =
        function
        | Ev e -> e.AllDay
        | Sess _ -> false

    let moduleId =
        function
        | Ev e -> e.ModuleId
        | Sess _ -> None

    /// A session names its module by code rather than by id.
    let moduleCode =
        function
        | Ev _ -> None
        | Sess s -> s.ModuleCode

    let hex =
        function
        | Ev e -> Theme.eventKindHex e.Kind
        | Sess _ -> Theme.eventKindHex EventKind.StudySession

    /// The kind label, from the record itself. **Never inferred from the title**
    /// — a session called "Revise for the exam" is a session, not an exam.
    let kindLabel =
        function
        | Ev e -> EventKind.label e.Kind
        | Sess _ -> "Planner session"

    let key =
        function
        | Ev e -> "event:" + e.Id.Value.ToString "N"
        | Sess s -> "session:" + s.Id.Value.ToString "N"

    /// Identity, so a selection can be reconciled after a filter or a reload.
    let sameAs (a: Entry) (b: Entry) = key a = key b

// ---------------------------------------------------------------------------
// date rules
// ---------------------------------------------------------------------------

/// **The one week definition.** `StudyTime.weekStart` takes the culture's first
/// day, so every screen moves together. Nothing here recomputes a Monday of its
/// own — that divergence is exactly what this indirection prevents.
let weekStart (firstDay: DayOfWeek) (date: DateTime) = StudyTime.weekStart firstDay date

let monthStart (date: DateTime) = DateTime(date.Year, date.Month, 1)

/// The half-open range a mode shows: `[start, end)`.
let visibleRange (mode: ViewMode) (firstDay: DayOfWeek) (anchor: DateTime) =
    match mode with
    | ViewMode.Day ->
        let d = anchor.Date
        d, d.AddDays 1.0
    | ViewMode.Week ->
        let s = weekStart firstDay anchor
        s, s.AddDays 7.0
    | ViewMode.Month ->
        let s = monthStart anchor
        s, s.AddMonths 1
    | ViewMode.Agenda ->
        let d = anchor.Date
        d, d.AddDays 28.0

/// Navigation steps by whatever the mode shows, so Next always advances by one
/// "screenful" rather than by a fixed number of days.
let step (mode: ViewMode) (direction: int) (anchor: DateTime) =
    match mode with
    | ViewMode.Day -> anchor.Date.AddDays(float direction)
    | ViewMode.Week
    | ViewMode.Agenda -> anchor.Date.AddDays(7.0 * float direction)
    | ViewMode.Month -> (monthStart anchor).AddMonths direction

/// **Today is the clock's today**, never a remembered value — that is the whole
/// point of the control.
let todayAnchor (now: DateTime) = now.Date

let rangeTitle (mode: ViewMode) (firstDay: DayOfWeek) (anchor: DateTime) =
    match mode with
    | ViewMode.Day -> anchor.ToString "dddd d MMMM yyyy"
    | ViewMode.Month -> anchor.ToString "MMMM yyyy"
    | ViewMode.Week ->
        let s = weekStart firstDay anchor
        let e = s.AddDays 6.0

        if s.Month = e.Month then sprintf "%d–%d %s" s.Day e.Day (s.ToString "MMMM yyyy")
        elif s.Year = e.Year then sprintf "%s – %s" (s.ToString "d MMM") (e.ToString "d MMM yyyy")
        else sprintf "%s – %s" (s.ToString "d MMM yyyy") (e.ToString "d MMM yyyy")
    | ViewMode.Agenda ->
        let s, e = visibleRange ViewMode.Agenda firstDay anchor
        sprintf "%s – %s" (s.ToString "d MMM") (e.AddDays(-1.0).ToString "d MMM yyyy")

/// Whether the visible range contains today — which is what decides whether a
/// current-time marker is drawn at all.
let containsToday (now: DateTime) (rangeStart: DateTime, rangeEnd: DateTime) =
    now.Date >= rangeStart.Date && now.Date < rangeEnd.Date

// ---------------------------------------------------------------------------
// month cells
// ---------------------------------------------------------------------------

/// One cell of the month grid.
type MonthCell =
    { Date: DateTime
      /// False for the leading and trailing days borrowed from adjacent months.
      InMonth: bool }

/// **Always six rows.** A month that would fit in five still gets six, so
/// stepping between months never changes the grid's height and never makes the
/// rest of the page jump.
[<Literal>]
let monthRows = 6

let monthCells (firstDay: DayOfWeek) (anchor: DateTime) : MonthCell list =
    let first = monthStart anchor
    let gridStart = weekStart firstDay first
    let month = first.Month

    [ for i in 0 .. (monthRows * 7) - 1 do
          let date = gridStart.AddDays(float i)
          { Date = date; InMonth = date.Month = month && date.Year = first.Year } ]

/// Weekday headings in the culture's own order.
let weekdayHeadings (firstDay: DayOfWeek) =
    [ for i in 0..6 ->
        let day = enum<DayOfWeek> ((int firstDay + i) % 7)
        Globalization.CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames.[int day] ]

/// How many chips fit in a month cell before the overflow count takes over.
[<Literal>]
let monthChipsPerCell = 3

/// `+n more`, or nothing. The number is exact — it is the count that is hidden,
/// not an approximation.
let monthOverflow (entries: Entry list) =
    let hidden = entries.Length - monthChipsPerCell
    if hidden > 0 then Some hidden else None

// ---------------------------------------------------------------------------
// filtering
// ---------------------------------------------------------------------------

/// Empty means All, for both axes.
type Filter =
    { Sources: Set<Source>
      Kinds: Set<EventKind>
      Modules: Set<Guid> }

[<RequireQualifiedAccess>]
module Filter =
    let none = { Sources = Set.empty; Kinds = Set.empty; Modules = Set.empty }

    let isAll (f: Filter) =
        Set.isEmpty f.Sources && Set.isEmpty f.Kinds && Set.isEmpty f.Modules

    let toggleSource (s: Source) (f: Filter) =
        { f with Sources = (if Set.contains s f.Sources then Set.remove s f.Sources else Set.add s f.Sources) }

    let toggleKind (k: EventKind) (f: Filter) =
        { f with Kinds = (if Set.contains k f.Kinds then Set.remove k f.Kinds else Set.add k f.Kinds) }

    let toggleModule (id: Guid) (f: Filter) =
        { f with Modules = (if Set.contains id f.Modules then Set.remove id f.Modules else Set.add id f.Modules) }

/// A session has no `EventKind`, so a kind filter can only ever narrow to
/// events. That is stated by the filter itself rather than hidden.
let matchesFilter (modules: StudyModule list) (filter: Filter) (entry: Entry) =
    let sourceOk = Set.isEmpty filter.Sources || Set.contains (Entry.source entry) filter.Sources

    let kindOk =
        Set.isEmpty filter.Kinds
        || (match entry with
            | Ev e -> Set.contains e.Kind filter.Kinds
            | Sess _ -> false)

    let moduleOk =
        Set.isEmpty filter.Modules
        || (match entry with
            | Ev e -> e.ModuleId |> Option.map (fun m -> Set.contains m.Value filter.Modules) |> Option.defaultValue false
            | Sess s ->
                // A session names its module by code; resolve it through the
                // module list rather than guessing.
                match s.ModuleCode with
                | Some code ->
                    modules
                    |> List.exists (fun m ->
                        String.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase)
                        && Set.contains m.Id.Value filter.Modules)
                | None -> false)

    sourceOk && kindOk && moduleOk

let applyFilter (modules: StudyModule list) (filter: Filter) (entries: Entry list) =
    entries |> List.filter (matchesFilter modules filter)

/// Which kinds are worth offering a chip for: the ones genuinely present.
let kindsPresent (entries: Entry list) =
    EventKind.all
    |> List.filter (fun k ->
        entries
        |> List.exists (function
            | Ev e -> e.Kind = k
            | Sess _ -> false))

let sourcesPresent (entries: Entry list) =
    Source.all |> List.filter (fun s -> entries |> List.exists (fun e -> Entry.source e = s))

let countFor (predicate: Entry -> bool) (entries: Entry list) =
    entries |> List.filter predicate |> List.length

// ---------------------------------------------------------------------------
// day slicing
// ---------------------------------------------------------------------------

/// Entries touching a given local day, split into the all-day band and the
/// timed grid.
///
/// **The overlap test is half-open**: an entry that ends exactly at midnight
/// belongs to the day it ran in, not to the next one.
let onDay (day: DateTime) (entries: Entry list) =
    let dayStart = day.Date
    let dayEnd = dayStart.AddDays 1.0

    entries
    |> List.filter (fun e -> Entry.start e < dayEnd && Entry.finish e > dayStart)

let allDayOf (entries: Entry list) = entries |> List.filter Entry.isAllDay
let timedOf (entries: Entry list) = entries |> List.filter (Entry.isAllDay >> not)

// ---------------------------------------------------------------------------
// overlap lanes
// ---------------------------------------------------------------------------

/// One entry, placed.
type Placed =
    { Entry: Entry
      /// Which column within its overlap cluster.
      Lane: int
      /// How many columns that cluster needs.
      Lanes: int
      Top: float
      Height: float }

[<RequireQualifiedAccess>]
module Geometry =

    /// One hour of the timed grid.
    [<Literal>]
    let hourHeight = 52.0

    /// **The one documented minimum.** A zero-duration or malformed record still
    /// has to be clickable, so it is drawn at this height — without the stored
    /// values being changed.
    [<Literal>]
    let minEventHeight = 22.0

    [<Literal>]
    let allDayRowHeight = 30.0

    [<Literal>]
    let timeGutter = 56.0

    [<Literal>]
    let cardRadius = 14.0

    [<Literal>]
    let chipRadius = 6.0

    [<Literal>]
    let actionMinHeight = 38.0

    [<Literal>]
    let actionRadius = 11.0

    [<Literal>]
    let contentHighlight = 0.0

    [<Literal>]
    let borderInset = 0.5

    /// Event washes and their edges.
    [<Literal>]
    let eventFillAlpha = "26"

    [<Literal>]
    let eventEdgeAlpha = "4D"

    /// A selected entry.
    [<Literal>]
    let selectedFillAlpha = "3D"

    [<Literal>]
    let selectedEdgeAlpha = "8C"

    [<Literal>]
    let chipFillAlpha = "1F"

    /// Today's column or cell.
    [<Literal>]
    let todayFillAlpha = "14"

    [<Literal>]
    let gridLineAlpha = "12"

    [<Literal>]
    let veilAlpha = "2E"

    [<Literal>]
    let detailColumns = "Auto,10,*"

let dayHeight = Geometry.hourHeight * 24.0

/// Minutes from local midnight, clamped into the day so an entry that started
/// yesterday still draws from the top of today rather than off-screen.
let private minutesInto (day: DateTime) (at: DateTime) =
    let delta = (at - day.Date).TotalMinutes
    Math.Max(0.0, Math.Min(24.0 * 60.0, delta))

let topOf (day: DateTime) (at: DateTime) = minutesInto day at / 60.0 * Geometry.hourHeight

let heightOf (day: DateTime) (startsAt: DateTime) (endsAt: DateTime) =
    let top = topOf day startsAt
    let bottom = topOf day endsAt
    Math.Max(Geometry.minEventHeight, bottom - top)

/// **Two entries overlap only if they genuinely share time.** Touching exactly
/// at a boundary — one ending at 10:00, the next starting at 10:00 — is not an
/// overlap, which matches `CalendarEvent.overlaps` in the domain.
let overlapping (a: Entry) (b: Entry) =
    Entry.start a < Entry.finish b && Entry.start b < Entry.finish a

/// Lays a day's timed entries into lanes.
///
/// Entries are clustered by transitive overlap; within a cluster each entry
/// takes the lowest lane whose previous occupant has already finished, and every
/// entry in the cluster is given the cluster's full lane count so their widths
/// agree. **Nothing is ever hidden behind anything else.**
let layoutDay (day: DateTime) (entries: Entry list) : Placed list =
    let ordered =
        entries
        |> List.sortBy (fun e -> Entry.start e, -(Entry.finish e).Ticks, Entry.key e)

    // Cluster by transitive overlap.
    let clusters = ResizeArray<ResizeArray<Entry>>()
    let mutable clusterEnd = DateTime.MinValue

    for entry in ordered do
        if clusters.Count = 0 || Entry.start entry >= clusterEnd then
            clusters.Add(ResizeArray [ entry ])
            clusterEnd <- Entry.finish entry
        else
            clusters.[clusters.Count - 1].Add entry
            if Entry.finish entry > clusterEnd then clusterEnd <- Entry.finish entry

    [ for cluster in clusters do
          // Lane assignment: the lowest lane free at this entry's start.
          let laneEnds = ResizeArray<DateTime>()
          let assigned = ResizeArray<Entry * int>()

          for entry in cluster do
              let mutable lane = -1
              let mutable i = 0

              while lane < 0 && i < laneEnds.Count do
                  if laneEnds.[i] <= Entry.start entry then lane <- i
                  i <- i + 1

              if lane < 0 then
                  laneEnds.Add(Entry.finish entry)
                  lane <- laneEnds.Count - 1
              else
                  laneEnds.[lane] <- Entry.finish entry

              assigned.Add(entry, lane)

          let lanes = max 1 laneEnds.Count

          for entry, lane in assigned do
              { Entry = entry
                Lane = lane
                Lanes = lanes
                Top = topOf day (Entry.start entry)
                Height = heightOf day (Entry.start entry) (Entry.finish entry) } ]

// ---------------------------------------------------------------------------
// agenda
// ---------------------------------------------------------------------------

let agendaOrder = [ "Today"; "Tomorrow"; "This week"; "Later" ]

/// Deterministic local-date grouping. **Nothing is called urgent** — there is no
/// urgency rule for a calendar entry.
let agendaGroup (now: DateTime) (firstDay: DayOfWeek) (at: DateTime) =
    let today = now.Date
    let day = at.Date
    let weekEnd = (weekStart firstDay now).AddDays 7.0

    if day = today then "Today"
    elif day = today.AddDays 1.0 then "Tomorrow"
    elif day > today && day < weekEnd.Date then "This week"
    else "Later"

let agendaGroups (now: DateTime) (firstDay: DayOfWeek) (entries: Entry list) =
    let ordered = entries |> List.sortBy (fun e -> Entry.start e, Entry.key e)

    agendaOrder
    |> List.map (fun label -> label, ordered |> List.filter (fun e -> agendaGroup now firstDay (Entry.start e) = label))
    |> List.filter (fun (_, es) -> not es.IsEmpty)

// ---------------------------------------------------------------------------
// selection
// ---------------------------------------------------------------------------

/// A selection that is no longer in the visible set is **cleared**, not moved —
/// silently selecting a neighbour would offer Edit and Delete on the wrong item.
let reconcileSelection (selected: Entry option) (visible: Entry list) =
    match selected with
    | None -> None
    | Some entry -> visible |> List.tryFind (Entry.sameAs entry)

// ---------------------------------------------------------------------------
// the editor
// ---------------------------------------------------------------------------

/// Only fields a `CalendarEvent` row can persist. No recurrence, no reminders,
/// no participants — none of those columns exist.
type Draft =
    { Original: CalendarEvent option
      Title: string
      KindIndex: int
      AllDay: bool
      StartText: string
      EndText: string
      Location: string
      ModuleIndex: int
      Notes: string }

/// Why a draft cannot be saved. `None` means it can.
///
/// **All-day changes what must be valid**: an all-day event needs a date, not a
/// time, so the end field is neither required nor shown.
let draftError (draft: Draft) =
    if String.IsNullOrWhiteSpace draft.Title then
        Some "A title is required."
    elif draft.AllDay then
        match Format.parseDateTimeInput draft.StartText with
        | None -> Some "Date not understood — use yyyy-MM-dd."
        | Some _ -> None
    else
        match Format.parseDateTimeInput draft.StartText with
        | None -> Some "Start time not understood — use yyyy-MM-dd HH:mm."
        | Some start ->
            match Format.parseDateTimeInput draft.EndText with
            | Some finish when finish > start -> None
            | Some _ -> Some "The end must be after the start."
            | None when String.IsNullOrWhiteSpace draft.EndText -> None
            | None -> Some "End time not understood — use yyyy-MM-dd HH:mm."

let canSave (saving: bool) (draft: Draft) = not saving && (draftError draft).IsNone

/// The times a draft resolves to. An all-day event spans local midnight to local
/// midnight, which is what "all day" means for a stored local `DateTime`.
let resolveTimes (draft: Draft) =
    match Format.parseDateTimeInput draft.StartText with
    | None -> None
    | Some start ->
        if draft.AllDay then
            Some(start.Date, start.Date.AddDays 1.0)
        else
            match Format.parseDateTimeInput draft.EndText with
            | Some finish when finish > start -> Some(start, finish)
            | _ when String.IsNullOrWhiteSpace draft.EndText -> Some(start, start.AddHours 1.0)
            | _ -> None

// ---------------------------------------------------------------------------
// honesty notices
// ---------------------------------------------------------------------------

/// `Source` and `ExternalId` exist so a re-sync upserts instead of duplicating.
/// That is **import**, not a live connection, and the screen says so.
[<Literal>]
let importNotice = "Imported once from this source. It is not kept in sync, and changes here are not sent back."

/// Rescheduling is explicit, because nothing persists a drag.
[<Literal>]
let rescheduleNotice = "Use Edit to move an event. Dragging is not supported."

[<Literal>]
let sessionNotice = "Planner sessions are created by the Planner. Edit them there."

// ---------------------------------------------------------------------------
// composition and responsive layout
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type Section =
    | Header
    | Navigation
    | Modes
    | Filters
    | Workspace
    | Side

let sectionOrder =
    [ Section.Header; Section.Navigation; Section.Modes; Section.Filters; Section.Workspace; Section.Side ]

[<Literal>]
let mediumWidth = 1280.0

[<Literal>]
let narrowWidth = 900.0

[<RequireQualifiedAccess>]
type SidePlacement =
    | Rail
    | Below

type Placement =
    { Side: SidePlacement
      /// Below the narrow breakpoint a seven-column grid stops being readable.
      Narrow: bool }

let placementFor (width: float) =
    if width < narrowWidth then
        { Side = SidePlacement.Below; Narrow = true }
    elif width < mediumWidth then
        { Side = SidePlacement.Below; Narrow = false }
    else
        { Side = SidePlacement.Rail; Narrow = false }

/// **The stored mode is never changed by a resize.** A narrow window cannot show
/// a readable month or week grid, so the *presentation* falls back to Agenda —
/// and says so — while `Mode` keeps whatever the user chose. Widening restores
/// it without the user touching anything.
let effectiveMode (placement: Placement) (mode: ViewMode) =
    if placement.Narrow then
        match mode with
        | ViewMode.Month
        | ViewMode.Week -> ViewMode.Agenda
        | other -> other
    else
        mode

let narrowNotice (mode: ViewMode) =
    sprintf "%s needs a wider window, so this is the agenda. Widen the window to get it back." (ViewMode.label mode)

[<Literal>]
let sideWidth = 320.0
