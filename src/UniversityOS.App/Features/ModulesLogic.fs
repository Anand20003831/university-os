module UniversityOS.App.Features.ModulesLogic

open System
open UniversityOS.Domain
open UniversityOS.App

/// Every decision the Modules screen makes, as pure functions over the real
/// domain types — testable without an Avalonia application or a database.
///
/// **This screen loses more of its redesign than any before it, and the reason is
/// always the same one: grades are not modelled.** There is no mark, no target,
/// no prediction and no grade history anywhere in `UniversityOS.Domain`. The
/// mockup's module card is built around exactly those:
///
/// | Redesign | Status |
/// |---|---|
/// | Health score dial (0–100) | **absent** — derived from grades |
/// | Current % / Target % / Predicted % | **absent** — no grade is stored |
/// | Confidence | **absent** — nothing produces one |
/// | Grade trend bars | **absent** — no grade history exists |
/// | Lectures dial | **absent** — `Lecture` has no attended/watched flag |
/// | Revision dial | **absent** — `RevisionItem` has no review schedule, so "due for review" has no source |
/// | Overall dial | **absent** — it averages the three above |
/// | Coursework dial | **kept** — mean `Assignment.Progress`, which is real |
/// | Highest / Lowest Grade filters | **absent** |
/// | "Average Grade" health metric | **absent** |
///
/// What remains is everything the application genuinely knows about a module:
/// its coursework progress, its outstanding workload, its next deadline, lecture,
/// study session and exam, and the real counts of the notes, documents, lectures
/// and flashcards filed under it.
///
/// **The risk model is not reinvented here.** "Needs attention" reuses
/// `AssignmentsLogic.riskOf` over the module's own assignments, so a module and
/// the assignments inside it can never disagree about whether something is at
/// risk. That is why this module compiles after `AssignmentsLogic`.

// ---------------------------------------------------------------------------
// geometry
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module Geometry =

    /// No specular pass on a content card.
    [<Literal>]
    let contentHighlight = 0.0

    /// Glass action geometry, pinned equal to the Planner's, the Inbox's and the
    /// Assignments screen's by test.
    [<Literal>]
    let actionMinHeight = 38.0

    [<Literal>]
    let actionRadius = 11.0

    /// Half the hairline's 1px stroke.
    [<Literal>]
    let borderInset = 0.5

    /// A filter chip. Height and border reserved in BOTH states.
    [<Literal>]
    let filterMinHeight = 30.0

    [<Literal>]
    let filterRadius = 999.0

    [<Literal>]
    let filterBorderThickness = 1.0

    /// A module card. The redesign uses `auto-fit, minmax(420px, 1fr)`; this is
    /// that minimum, honoured by choosing a column count from the width.
    [<Literal>]
    let cardMinWidth = 420.0

    [<Literal>]
    let cardRadius = 20.0

    /// Drawn in both states — accent when expanded — so expanding recolours a
    /// card and never resizes it.
    [<Literal>]
    let cardBorderThickness = 1.0

    /// The summary metric row.
    [<Literal>]
    let metricRowMinHeight = 72.0

    /// The module identity dot and the coursework dial.
    [<Literal>]
    let moduleDotSize = 9.0

    [<Literal>]
    let dialSize = 48.0

    /// The expand control, in a fixed column so the title cannot shift.
    [<Literal>]
    let chevronWidth = 18.0

    /// The redesign's always-visible per-card action buttons: a 30px square with
    /// a 9px radius, carrying a glyph.
    ///
    /// A glyph alone would be unreadable to a screen reader, so each one also
    /// carries an accessible name **and** a tooltip — the same treatment the
    /// collapsed sidebar already uses for its icon-only rail (Shell.fs §747).
    [<Literal>]
    let iconActionSize = 30.0

    [<Literal>]
    let iconActionRadius = 9.0

    /// Glyph column, gap, wrapping text column.
    [<Literal>]
    let bulletColumns = "Auto,9,*"

    /// A label / value row inside a card's detail grid.
    [<Literal>]
    let detailColumns = "Auto,12,*"

// ---------------------------------------------------------------------------
// documented thresholds
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module Threshold =

    /// How far ahead an exam counts as "soon". A fortnight is the point at which
    /// revision has to displace other work rather than fit around it.
    [<Literal>]
    let examSoonDays = 14

    /// A module needs attention when at least this many of its assignments are
    /// at High risk or worse — one is enough, because a single endangered piece
    /// of coursework is a real call on the user's week.
    [<Literal>]
    let attentionAssignments = 1

// ---------------------------------------------------------------------------
// facts
// ---------------------------------------------------------------------------

/// Everything the screen knows about one module, gathered in a single load so no
/// two cards can describe different moments.
///
/// Every field is a real query result: `IAssignmentRepository.GetByModule`,
/// `INoteRepository.GetByModule`, `IDocumentRepository.GetByModule`,
/// `ILectureRepository.GetByModule`, `IRevisionRepository.GetByModule`,
/// `IEventRepository.GetBetween` filtered to this module, `PlannedSession`s
/// carrying its code, and `ActivityEntry`s pointing at it.
type Facts =
    { Module: StudyModule
      /// The assignment facts this screen shares with the Assignments screen, so
      /// the two cannot disagree about risk, progress or remaining effort.
      Assignments: AssignmentsLogic.Facts list
      Notes: Note list
      Documents: Document list
      Lectures: Lecture list
      Flashcards: RevisionItem list
      /// Calendar events filed under this module, within the loaded window.
      Events: CalendarEvent list
      /// Future, not-yet-completed planner sessions carrying this module's code.
      Sessions: PlannedSession list
      /// Activity entries referencing this module.
      ActivityCount: int }

[<RequireQualifiedAccess>]
module Facts =

    /// A bare snapshot, for a module with nothing filed under it yet.
    let ofModule (m: StudyModule) =
        { Module = m
          Assignments = []
          Notes = []
          Documents = []
          Lectures = []
          Flashcards = []
          Events = []
          Sessions = []
          ActivityCount = 0 }

let isArchived (f: Facts) = f.Module.Archived

let openAssignments (f: Facts) = f.Assignments |> List.filter AssignmentsLogic.isOpen

/// **The one genuine progress figure on this screen.**
///
/// Mean `Assignment.Progress` across every assignment filed under the module,
/// finished ones included — a module is not 40% done because its submitted work
/// is excluded from the average. `None` when the module has no assignments at
/// all: an average over nothing is not zero.
let courseworkProgress (f: Facts) =
    if f.Assignments.IsEmpty then
        None
    else
        f.Assignments
        |> List.averageBy (AssignmentsLogic.progress >> float)
        |> Math.Round
        |> int
        |> Some

/// Outstanding effort, from the domain's own rule — the same one the Planner and
/// the Assignments screen use.
let remainingMinutes (f: Facts) =
    f.Assignments |> List.sumBy AssignmentsLogic.remainingMinutes

/// Study time the planner has actually booked for this module.
let scheduledMinutes (f: Facts) =
    f.Sessions |> List.sumBy (fun s -> max 0 (int (s.EndsAt - s.StartsAt).TotalMinutes))

let nextDeadline (now: DateTime) (f: Facts) =
    openAssignments f
    |> List.choose (fun a -> a.Assignment.Deadline |> Option.map (fun d -> d, a.Assignment.Title))
    |> List.filter (fun (d, _) -> d >= now)
    |> List.sortBy fst
    |> List.tryHead

let nextLecture (now: DateTime) (f: Facts) =
    f.Lectures
    |> List.choose (fun l -> l.Date |> Option.map (fun d -> d, l.Title))
    |> List.filter (fun (d, _) -> d >= now)
    |> List.sortBy fst
    |> List.tryHead

let nextSession (now: DateTime) (f: Facts) =
    f.Sessions
    |> List.filter (fun s -> not s.Completed && s.StartsAt >= now)
    |> List.sortBy (fun s -> s.StartsAt)
    |> List.tryHead

/// The next exam, from real calendar events — `EventKind.Exam` carrying this
/// module's id. Not inferred from a title.
let nextExam (now: DateTime) (f: Facts) =
    f.Events
    |> List.filter (fun e -> e.Kind = EventKind.Exam && e.StartsAt >= now)
    |> List.sortBy (fun e -> e.StartsAt)
    |> List.tryHead

let examIsSoon (now: DateTime) (f: Facts) =
    match nextExam now f with
    | Some e -> (e.StartsAt - now).TotalDays <= float Threshold.examSoonDays
    | None -> false

/// Assignments in this module the shared risk model rates High or worse.
let assignmentsAtRisk (now: DateTime) (f: Facts) =
    openAssignments f
    |> List.filter (fun a -> AssignmentsLogic.Risk.atLeast AssignmentsLogic.Risk.High (AssignmentsLogic.riskOf now a))

/// A module wants a decision when its own coursework does, or when its exam is
/// close. Both clauses defer to models that already exist.
let needsAttention (now: DateTime) (f: Facts) =
    not (isArchived f)
    && ((assignmentsAtRisk now f).Length >= Threshold.attentionAssignments || examIsSoon now f)

// ---------------------------------------------------------------------------
// filters
// ---------------------------------------------------------------------------

/// The redesign offers eight chips and they are **toggles**, not a single
/// selection — `toggleModuleFilter` flips one at a time and several can be on at
/// once. `Active` and `Archived` govern which SECTION is shown; the rest narrow
/// or order the active grid.
///
/// **Highest Grade and Lowest Grade are absent** — they sort by a number the
/// application does not store.
[<RequireQualifiedAccess>]
type Filter =
    | Active
    | Archived
    | NeedsAttention
    | ExamsSoon
    | MostWorkRemaining
    | HighestWorkload

[<RequireQualifiedAccess>]
module Filter =

    let all =
        [ Filter.Active
          Filter.Archived
          Filter.NeedsAttention
          Filter.ExamsSoon
          Filter.MostWorkRemaining
          Filter.HighestWorkload ]

    let label =
        function
        | Filter.Active -> "Active"
        | Filter.Archived -> "Archived"
        | Filter.NeedsAttention -> "Needs attention"
        | Filter.ExamsSoon -> "Exams soon"
        | Filter.MostWorkRemaining -> "Most work remaining"
        | Filter.HighestWorkload -> "Highest workload"

    /// `MostWorkRemaining` and `HighestWorkload` are **orderings**, not
    /// predicates: they say which module to look at first, not which to hide.
    let isOrdering =
        function
        | Filter.MostWorkRemaining
        | Filter.HighestWorkload -> true
        | _ -> false

    /// `Active` and `Archived` choose which section is on screen rather than
    /// filtering a grid.
    let isSectionToggle =
        function
        | Filter.Active
        | Filter.Archived -> true
        | _ -> false

/// The set of chips currently switched on.
type Selection = Set<Filter>

[<RequireQualifiedAccess>]
module Selection =

    let empty: Selection = Set.empty

    let toggle (filter: Filter) (selection: Selection) : Selection =
        if selection.Contains filter then selection.Remove filter else selection.Add filter

    /// Mirrors the redesign exactly: the active grid hides only when Archived is
    /// on **and** Active is off, and vice versa. With neither on, both show.
    let showsActive (s: Selection) =
        not (s.Contains Filter.Archived && not (s.Contains Filter.Active))

    let showsArchived (s: Selection) =
        not (s.Contains Filter.Active && not (s.Contains Filter.Archived))

/// Whether one narrowing chip keeps this module.
let matches (now: DateTime) (filter: Filter) (f: Facts) =
    match filter with
    | Filter.Active -> not (isArchived f)
    | Filter.Archived -> isArchived f
    | Filter.NeedsAttention -> needsAttention now f
    | Filter.ExamsSoon -> examIsSoon now f
    | Filter.MostWorkRemaining -> remainingMinutes f > 0
    | Filter.HighestWorkload -> scheduledMinutes f > 0

/// The active grid: every narrowing chip applied, then ordered.
///
/// Ordering is explicit rather than inherited from the repository, so a refresh
/// cannot move a card out from under the pointer. Modules wanting attention come
/// first, then by code, so the order is total.
let activeGrid (now: DateTime) (selection: Selection) (facts: Facts list) =
    let narrowing =
        selection
        |> Set.filter (fun f -> not (Filter.isSectionToggle f) && not (Filter.isOrdering f))

    let kept =
        facts
        |> List.filter (isArchived >> not)
        |> List.filter (fun f -> narrowing |> Set.forall (fun chip -> matches now chip f))

    if selection.Contains Filter.MostWorkRemaining then
        kept |> List.sortBy (fun f -> -(remainingMinutes f), f.Module.Code)
    elif selection.Contains Filter.HighestWorkload then
        kept |> List.sortBy (fun f -> -(scheduledMinutes f), f.Module.Code)
    else
        kept |> List.sortBy (fun f -> (if needsAttention now f then 0 else 1), f.Module.Code)

/// The archived grid — always the plain archived set, in code order.
let archivedGrid (facts: Facts list) =
    facts |> List.filter isArchived |> List.sortBy (fun f -> f.Module.Code)

/// Why the active grid is empty, given which chips are on.
let emptyActiveMessage (selection: Selection) =
    let narrowing =
        selection
        |> Set.filter (fun f -> not (Filter.isSectionToggle f) && not (Filter.isOrdering f))
        |> Set.toList

    match narrowing with
    | [] -> "No active modules yet. Create one — everything else hangs off a module."
    | [ Filter.NeedsAttention ] -> "No module has endangered coursework or an exam within a fortnight."
    | [ Filter.ExamsSoon ] -> sprintf "No exam is scheduled within %d days." Threshold.examSoonDays
    | _ -> "No module matches every chip you have switched on."

// ---------------------------------------------------------------------------
// academic health — the five summary cards
// ---------------------------------------------------------------------------

/// A summary figure. `Value` is an option because some figures are genuinely
/// unknown rather than zero. `Note` is a qualifier on a real figure, never
/// empty-state prose.
type Metric =
    { Label: string
      Value: string option
      Note: string option }

/// The redesign's "Average Grade" card is **absent**; "Average study hours" takes
/// its place, because `StudyTime` genuinely measures it from completed sessions.
///
/// `studiedMinutesThisWeek` is passed in rather than computed, because it must
/// come from `StudyTime.minutesStudiedThisWeek` — the one definition the
/// Dashboard and the Timeline already share.
let metrics (now: DateTime) (studiedMinutesThisWeek: int) (facts: Facts list) : Metric list =
    let active = facts |> List.filter (isArchived >> not)
    let withCoursework = active |> List.choose courseworkProgress

    [ { Label = "Active modules"
        Value = Some(string active.Length)
        Note =
          let archived = facts.Length - active.Length
          if archived > 0 then Some(sprintf "%d archived" archived) else None }

      { Label = "Coursework progress"
        // An average over modules that have no assignments is not zero.
        Value =
          if withCoursework.IsEmpty then None
          else Some(sprintf "%d%%" (int (Math.Round(withCoursework |> List.averageBy float))))
        Note =
          if withCoursework.IsEmpty || withCoursework.Length = active.Length then None
          else Some(sprintf "%d of %d with coursework" withCoursework.Length active.Length) }

      { Label = "Hours remaining"
        Value = Some(Format.hours (float (active |> List.sumBy remainingMinutes) / 60.0))
        Note = None }

      { Label = "Needing attention"
        Value = Some(string (active |> List.filter (needsAttention now) |> List.length))
        Note = None }

      { Label = "Studied this week"
        Value = Some(Format.hours (float studiedMinutesThisWeek / 60.0))
        Note = None } ]

// ---------------------------------------------------------------------------
// the expanded card's tabs
// ---------------------------------------------------------------------------

/// The redesign shows nine tabs. Each of these has a real `GetByModule` query or
/// a real foreign key behind it; none is a placeholder.
[<RequireQualifiedAccess>]
type Tab =
    | Assignments
    | Lectures
    | Notes
    | Documents
    | Flashcards
    | Calendar
    | Planner
    | Timeline

[<RequireQualifiedAccess>]
module Tab =

    let all =
        [ Tab.Assignments
          Tab.Lectures
          Tab.Notes
          Tab.Documents
          Tab.Flashcards
          Tab.Calendar
          Tab.Planner
          Tab.Timeline ]

    let label =
        function
        | Tab.Assignments -> "Assignments"
        | Tab.Lectures -> "Lectures"
        | Tab.Notes -> "Notes"
        | Tab.Documents -> "Documents"
        | Tab.Flashcards -> "Flashcards"
        | Tab.Calendar -> "Calendar"
        | Tab.Planner -> "Planner"
        | Tab.Timeline -> "Timeline"

    let emptyMessage =
        function
        | Tab.Assignments -> "No coursework is filed under this module."
        | Tab.Lectures -> "No lectures recorded yet."
        | Tab.Notes -> "No notes yet."
        | Tab.Documents -> "No documents filed here."
        | Tab.Flashcards -> "No flashcards yet — generate a deck from a note or a lecture."
        | Tab.Calendar -> "No calendar events for this module."
        | Tab.Planner -> "The planner has not scheduled this module."
        | Tab.Timeline -> "Nothing has been recorded against this module."

/// How many real items sit behind each tab, so a tab can show its count and an
/// empty one can be told apart from an unvisited one.
let tabCount (f: Facts) (tab: Tab) =
    match tab with
    | Tab.Assignments -> f.Assignments.Length
    | Tab.Lectures -> f.Lectures.Length
    | Tab.Notes -> f.Notes.Length
    | Tab.Documents -> f.Documents.Length
    | Tab.Flashcards -> f.Flashcards.Length
    | Tab.Calendar -> f.Events.Length
    | Tab.Planner -> f.Sessions.Length
    | Tab.Timeline -> f.ActivityCount

// ---------------------------------------------------------------------------
// the card's one-line summary
// ---------------------------------------------------------------------------

/// One deterministic sentence about the module. Never reassurance: every branch
/// below states a recorded figure.
let summary (now: DateTime) (f: Facts) =
    if isArchived f then
        "Archived — hidden from planning and AI context."
    else
        let atRisk = (assignmentsAtRisk now f).Length
        let remaining = remainingMinutes f

        if atRisk > 0 then
            sprintf
                "%d %s at high risk, %s of work left."
                atRisk
                (if atRisk = 1 then "assignment" else "assignments")
                (Format.duration remaining)
        elif examIsSoon now f then
            match nextExam now f with
            | Some e -> sprintf "Exam on %s — %s of coursework left." (Format.shortDate e.StartsAt) (Format.duration remaining)
            | None -> sprintf "%s of work left." (Format.duration remaining)
        elif remaining > 0 then
            match nextDeadline now f with
            | Some(d, title) -> sprintf "%s left; next is \"%s\", %s." (Format.duration remaining) title (Format.deadline now d)
            | None -> sprintf "%s of work left, none of it dated." (Format.duration remaining)
        elif f.Assignments.IsEmpty then
            "No coursework filed under this module yet."
        else
            "All coursework for this module is finished."

/// The redesign's card carries a block of one-to-two ✦ insight bullets *below*
/// the summary line, not just the summary itself. This is that block.
///
/// The second bullet is not commentary — it reports a **recorded gap** between
/// two figures the application already stores: outstanding effort versus the
/// study time the planner has actually booked for this module. When there is no
/// such gap there is no second bullet, rather than a sentence of reassurance.
let insights (now: DateTime) (f: Facts) : string list =
    [ summary now f

      if not (isArchived f) then
          let remaining = remainingMinutes f
          let scheduled = scheduledMinutes f

          if remaining > 0 && scheduled < remaining then
              sprintf "%s of that has no study session booked." (Format.duration (remaining - scheduled))
          else
              match nextLecture now f with
              | Some(d, title) ->
                  sprintf "Next lecture: \"%s\", %s." (Format.truncate 34 title) (Format.shortDate d)
              | None -> () ]

/// The redesign's "Suggested action" cell, in the expanded card's upcoming-work
/// grid.
///
/// **It suggests nothing the data does not already say.** Every branch names a
/// specific recorded object and the specific recorded gap that makes it the next
/// thing to touch — an at-risk assignment with no session booked, an exam inside
/// the documented window, a module with no coursework at all. When no branch
/// applies there is genuinely nothing to point at, and the cell renders as a
/// dash rather than inventing advice.
let suggestedAction (now: DateTime) (f: Facts) : string option =
    if isArchived f then
        None
    else
        let atRisk = assignmentsAtRisk now f
        let unscheduled = atRisk |> List.filter (fun a -> a.ScheduledMinutes <= 0)

        match unscheduled, atRisk with
        | a :: _, _ -> Some(sprintf "Book time for \"%s\"" (Format.truncate 26 a.Assignment.Title))
        | [], a :: _ -> Some(sprintf "Push on with \"%s\"" (Format.truncate 26 a.Assignment.Title))
        | [], [] ->
            if examIsSoon now f then
                nextExam now f
                |> Option.map (fun e -> sprintf "Revise — exam on %s" (Format.shortDate e.StartsAt))
            elif f.Assignments.IsEmpty then
                Some "Add this module's coursework"
            else
                None

/// The expanded card's upcoming-work grid, as data.
///
/// It lives here rather than in the view so its **order and its count** are
/// testable: the redesign has five cells and "Suggested action" is the fifth,
/// and a silent regression to four was one of the gaps this pass fixed.
///
/// A `None` value means the application genuinely does not know, and the view
/// draws a dash. It never means zero.
let upcomingCells (now: DateTime) (f: Facts) : (string * string option) list =
    let dated (label: string) (d: DateTime) = sprintf "%s · %s" (Format.truncate 22 label) (Format.shortDate d)

    [ "Next lecture", nextLecture now f |> Option.map (fun (d, t) -> dated t d)
      "Next assignment", nextDeadline now f |> Option.map (fun (d, t) -> dated t d)
      "Planner session", nextSession now f |> Option.map (fun s -> dated s.Title s.StartsAt)
      "Exam date", nextExam now f |> Option.map (fun e -> Format.shortDate e.StartsAt)
      "Suggested action", suggestedAction now f ]

/// The regions inside an expanded card, in the redesign's own order.
///
/// The mockup runs upcoming work → the action row → the tab strip → the tab
/// panel. The first implementation drew the actions **last**, after the panel;
/// this list is what stops that happening again unnoticed.
[<RequireQualifiedAccess>]
type ExpandedRegion =
    | Divider
    | UpcomingWork
    | Actions
    | Tabs
    | TabPanel

let expandedOrder =
    [ ExpandedRegion.Divider
      ExpandedRegion.UpcomingWork
      ExpandedRegion.Actions
      ExpandedRegion.Tabs
      ExpandedRegion.TabPanel ]

/// The four always-visible actions on a card, matching the redesign's row of
/// small glyph buttons. Every one is a real destination or a real editor.
let primaryActions: (string * string * Page option) list =
    [ "✎", "Edit this module", None
      "☑", "Open Assignments", Some Page.Assignments
      "◷", "Open the Planner", Some Page.Planner
      "↷", "Open the Timeline", Some Page.Timeline ]

/// The archived card's link row. The redesign lists five; the fifth is Grades,
/// which nothing stores, so four real destinations remain alongside Edit.
let archivedLinks: (string * Page) list =
    [ "Assignments", Page.Assignments
      "Notes", Page.Notes
      "Documents", Page.Documents
      "Timeline", Page.Timeline ]

// ---------------------------------------------------------------------------
// page composition
// ---------------------------------------------------------------------------

/// The page, as data — and it is the redesign's own shape: a health row, a chip
/// row, a captioned grid of large module cards, then a captioned grid of small
/// archived ones. The view maps over this list.
[<RequireQualifiedAccess>]
type Section =
    | Header
    | Metrics
    | Filters
    /// "ACTIVE MODULES" caption + the large card grid.
    | ActiveSection
    /// "ARCHIVED MODULES" caption + the compact grid.
    | ArchivedSection

let sectionOrder =
    [ Section.Header
      Section.Metrics
      Section.Filters
      Section.ActiveSection
      Section.ArchivedSection ]

// ---------------------------------------------------------------------------
// the four figures on a module card
// ---------------------------------------------------------------------------

/// The redesign's card carries **Current % · Target % · Predicted % ·
/// Confidence**. All four are grades or forecasts of grades, and none exists.
///
/// The row is kept — it is structurally important, four figures across the card —
/// but filled with the four things the application genuinely knows about a
/// module. `Value` is an option so a module with no coursework shows a dash
/// rather than a fabricated zero.
type CardFigure = { Label: string; Value: string option }

let cardFigures (now: DateTime) (f: Facts) : CardFigure list =
    let atRisk = (assignmentsAtRisk now f).Length

    [ { Label = "Coursework"
        Value = courseworkProgress f |> Option.map (sprintf "%d%%") }

      { Label = "Assignments"
        Value = Some(sprintf "%d/%d" (f.Assignments.Length - (openAssignments f).Length) f.Assignments.Length) }

      { Label = "Work left"
        Value = Some(Format.hours (float (remainingMinutes f) / 60.0)) }

      { Label = "At risk"
        Value = Some(string atRisk) } ]

/// The counts under the figures — the real contents of the module, and the
/// honest replacement for the redesign's Lectures / Revision / Overall dials.
let cardCounts (f: Facts) =
    [ "Lectures", f.Lectures.Length
      "Notes", f.Notes.Length
      "Documents", f.Documents.Length
      "Flashcards", f.Flashcards.Length ]

// ---------------------------------------------------------------------------
// responsive layout
// ---------------------------------------------------------------------------

/// Module cards per row. The redesign's grid is
/// `repeat(auto-fit, minmax(420px, 1fr))`; this is that rule expressed as a
/// column count, so the cards never fall below their readable minimum.
let cardColumns (contentWidth: float) =
    max 1 (int (contentWidth / Geometry.cardMinWidth))

/// Columns for the five summary cards, matching every other migrated screen.
let metricColumns (contentWidth: float) =
    if contentWidth < 700.0 then 2
    elif contentWidth < 1180.0 then 3
    else 5

/// The archived grid is four across in the redesign, and reflows down rather
/// than shrinking its cards below readability.
let archivedColumns (contentWidth: float) =
    if contentWidth < 700.0 then 1
    elif contentWidth < 1000.0 then 2
    elif contentWidth < 1400.0 then 3
    else 4
