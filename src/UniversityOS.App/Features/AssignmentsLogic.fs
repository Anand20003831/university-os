module UniversityOS.App.Features.AssignmentsLogic

open System
open UniversityOS.Domain
// For `Format` only — the application's one date/number voice. Without this,
// `Format.duration` resolves to `Printf.Format` and fails to compile.
open UniversityOS.App

/// Every decision the Assignments screen makes, as pure functions over the real
/// domain types — so the whole screen is testable without an Avalonia
/// application, a window or a database. The view maps over what is defined here;
/// it decides nothing of its own.
///
/// **The honesty rules that shape this screen.**
///
/// - **Grades are not modelled.** There is no grade, no target and no predicted
///   mark anywhere in the domain, so the redesign's "Goal 75%+", its confidence
///   percentage and its "Grade risk" row have no source. They are omitted and
///   replaced with figures the model can produce.
/// - **There is no milestone model.** `Research / Planning / Draft / Review /
///   Submission` exist only in the mockup. The two real progressions are
///   `AssignmentStatus` and the user's own `Checklist`, and this screen shows
///   those instead of inventing stages — see `Stage`.
/// - **Progress is manually entered.** `Assignment.Progress` is a 0–100 field the
///   editor sets; nothing derives it. It is shown as recorded, clamped, and is
///   never silently replaced by 100 because something was submitted.
/// - **Nothing outstanding is not the same as everything on track.** With no open
///   coursework the average-progress metric is `None`, not 0% or 100%, and the
///   forecast reports "nothing outstanding" rather than "On Track".

// ---------------------------------------------------------------------------
// geometry
// ---------------------------------------------------------------------------

/// Every dimension the screen depends on, named once.
///
/// Each is a **reservation**: space held whether or not the thing occupying it is
/// present, so a card cannot change size when a badge appears, a risk resolves or
/// a row expands. The Planner and the Inbox earned these; the same defects apply.
[<RequireQualifiedAccess>]
module Geometry =

    /// No specular pass on a content card. Zero, not "small": the gradient's
    /// REACH is 38% of the panel and does not shrink with its alpha.
    [<Literal>]
    let contentHighlight = 0.0

    /// The main workspace split — list against the forecast column.
    [<Literal>]
    let mainLeftWeight = 66.0

    [<Literal>]
    let mainRightWeight = 34.0

    /// Applied to the workspace ROW, never to either column.
    [<Literal>]
    let workspaceMinHeight = 320.0

    /// The summary metric row.
    [<Literal>]
    let metricRowMinHeight = 72.0

    /// Glass action geometry, identical in every state. Pinned equal to the
    /// Planner's and the Inbox's by test.
    [<Literal>]
    let actionMinHeight = 38.0

    [<Literal>]
    let actionRadius = 11.0

    /// Half the hairline's 1px stroke — a stroke laid on the panel bounds is
    /// half-clipped and its corners clip asymmetrically.
    [<Literal>]
    let borderInset = 0.5

    /// A filter chip. Height and border are reserved in BOTH states.
    [<Literal>]
    let filterMinHeight = 30.0

    [<Literal>]
    let filterRadius = 999.0

    [<Literal>]
    let filterBorderThickness = 1.0

    /// An assignment card's outline, drawn in both states — accent when
    /// expanded, ordinary border when not.
    [<Literal>]
    let cardBorderThickness = 1.0

    [<Literal>]
    let cardRadius = 16.0

    /// The expand/collapse control. A fixed column, so the title starts at the
    /// same x whether the card is open or shut.
    [<Literal>]
    let chevronWidth = 18.0

    /// The module identity dot.
    [<Literal>]
    let moduleDotSize = 8.0

    /// The risk badge. Reserved, so a card cannot resize when "Low" becomes
    /// "Critical".
    [<Literal>]
    let riskBadgeMinWidth = 74.0

    /// The progress bar. A fixed track, so the percentage label beside it cannot
    /// move the bar when it changes from "9%" to "100%".
    [<Literal>]
    let progressTrackWidth = 200.0

    [<Literal>]
    let progressTrackHeight = 6.0

    /// The percentage + pace label beside the bar.
    [<Literal>]
    let progressLabelMinWidth = 150.0

    /// Glyph column, gap, wrapping text column. A horizontal StackPanel measures
    /// its children with infinite width, so TextWrapping never engages inside
    /// one; a Grid gives the text a real finite width.
    [<Literal>]
    let bulletColumns = "Auto,9,*"

    /// A forecast row: label, gap, value pushed right.
    [<Literal>]
    let forecastColumns = "Auto,12,*"

    /// The Timeline navigation row: text on the left, arrow pushed to the far
    /// right. A star column and an Auto column, so the two sit at opposite edges
    /// of a row that spans almost the whole card.
    [<Literal>]
    let timelineColumns = "*,Auto"

    /// The Timeline row's own height and its inset from the card's edges. The
    /// row is a transparent full-width control, not a pill: its hover wash runs
    /// nearly edge to edge inside the card.
    [<Literal>]
    let timelineRowMinHeight = 40.0

    [<Literal>]
    let timelineRowInset = 8.0

    /// The Timeline card is deliberately shallow — one row and, at most, one
    /// muted note.
    [<Literal>]
    let timelineCardPadding = 10.0

/// What an unknown figure renders as, everywhere on the screen.
///
/// One mark, one meaning: **this could not be evaluated**. It is deliberately
/// not accompanied by a sentence on every card — repeating "nothing outstanding"
/// under five metrics and five overview rows says the same thing ten times.
[<Literal>]
let unavailableMark = "—"

// ---------------------------------------------------------------------------
// typography roles
// ---------------------------------------------------------------------------

/// The text roles this screen composes with, as sizes and Avalonia font weights.
///
/// Named here rather than picked per control so the hierarchy is a checkable
/// claim: a test asserts each role is genuinely distinct from the one it must
/// outrank. Weights are the numeric values `Avalonia.Media.FontWeight` carries
/// (Normal 400, Medium 500, SemiBold 600, Bold 700), which keeps this module
/// free of Avalonia.
[<RequireQualifiedAccess>]
module Typography =

    // ---- summary metric cards ----

    /// The figure. The strongest thing on the card by both size and weight.
    [<Literal>]
    let metricValueSize = 22.0

    [<Literal>]
    let metricValueWeight = 700

    /// What the figure is. Clearly subordinate, but not faint.
    [<Literal>]
    let metricLabelSize = 12.0

    [<Literal>]
    let metricLabelWeight = 500

    /// An optional qualifier — quieter again, and never present merely to fill
    /// an empty state.
    [<Literal>]
    let qualifierSize = 12.0

    [<Literal>]
    let qualifierWeight = 400

    // ---- the forecast and risk panels ----

    /// The panel's own name: COURSEWORK FORECAST, RISK OVERVIEW.
    [<Literal>]
    let cardTitleSize = 11.0

    [<Literal>]
    let cardTitleWeight = 600

    /// A section heading inside a panel: CURRENT TRAJECTORY, HIGHEST RISK.
    /// Quieter than the panel's own name and much quieter than the value under
    /// it — it introduces the answer, it is not the answer.
    [<Literal>]
    let sectionLabelSize = 11.0

    [<Literal>]
    let sectionLabelWeight = 400

    /// The answer itself.
    [<Literal>]
    let primaryValueSize = 14.5

    [<Literal>]
    let primaryValueWeight = 600

    /// A sentence explaining the answer. Regular weight, visibly subordinate.
    [<Literal>]
    let supportingSize = 12.0

    [<Literal>]
    let supportingWeight = 400

    /// A Risk Overview row. The value on the right outranks its label.
    [<Literal>]
    let overviewLabelWeight = 400

    [<Literal>]
    let overviewValueWeight = 600

// ---------------------------------------------------------------------------
// documented thresholds
// ---------------------------------------------------------------------------

/// Every number the screen judges by, in one place, because each of them is a
/// claim that a test can pin and a reader can disagree with.
[<RequireQualifiedAccess>]
module Threshold =

    /// "High weight" — a fifth of the module's grade or more. Chosen because a
    /// 20% component is the point at which one piece of coursework can move a
    /// module's outcome by a full classification band.
    [<Literal>]
    let highWeightPercent = 20.0

    /// Work counts as stalled when it has been started, is not finished, and has
    /// not been touched for this many days. `Assignment.UpdatedAt` is written on
    /// every save, so this is a real "nothing has happened" signal.
    [<Literal>]
    let stalledDays = 7

    /// How far ahead unscheduled work is worth flagging. Beyond this the planner
    /// legitimately has not got to it yet.
    [<Literal>]
    let unscheduledHorizonDays = 14

    /// Risk bands, by summed signal points.
    [<Literal>]
    let criticalPoints = 6

    [<Literal>]
    let highPoints = 4

    [<Literal>]
    let mediumPoints = 2

// ---------------------------------------------------------------------------
// facts — one snapshot per assignment, derived once
// ---------------------------------------------------------------------------

/// Everything the screen knows about one assignment, gathered in a single load
/// so no two panels can describe different moments.
///
/// Every field is a real query result:
///
/// - `ScheduledMinutes` / `SessionCount` — `PlannedSession.AssignmentId`, future
///   and not yet completed.
/// - `UnplacedReason` — `Planning.plan`'s own `UnplannedTask.Reason`, obtained
///   from `PlannerService.PreviewWith id`, which runs the real scheduler and
///   persists nothing. This is the only genuine "planner conflict" signal in the
///   application.
/// - `Linked` — `ILinkRepository.GetFor`, grouped by the kind at the other end.
///   Only kinds that actually appear are present; there are no zero rows.
/// - `ActivityCount` — `ActivityEntry.Entity` pointing at this assignment.
type Facts =
    { Assignment: Assignment
      Module: StudyModule option
      ScheduledMinutes: int
      SessionCount: int
      UnplacedReason: string option
      Linked: (EntityKind * int) list
      ActivityCount: int }

[<RequireQualifiedAccess>]
module Facts =

    /// A bare snapshot, for an assignment with nothing attached to it yet.
    let ofAssignment (a: Assignment) =
        { Assignment = a
          Module = None
          ScheduledMinutes = 0
          SessionCount = 0
          UnplacedReason = None
          Linked = []
          ActivityCount = 0 }

let isOpen (f: Facts) = Assignment.isOpen f.Assignment

let moduleCode (f: Facts) = f.Module |> Option.map (fun m -> m.Code)

/// The domain's own remaining-effort rule, reused rather than reimplemented, so
/// this screen and the Planner can never disagree about how much work is left.
///
/// ⚠️ `Assignment.remainingMinutes` falls back to **4 hours** when no estimate
/// exists, so the planner still reserves time for unestimated work. That default
/// is a real domain rule, not an invention here — but it is a default, so
/// `estimateIsDefaulted` reports it and the metric prints the caveat.
let remainingMinutes (f: Facts) =
    if isOpen f then Assignment.remainingMinutes f.Assignment else 0

let estimateIsDefaulted (f: Facts) = f.Assignment.EstimatedHours.IsNone

/// How many pieces of open work have no stored estimate.
///
/// Named so the Risk Overview's figure can be checked on its own. Only *open*
/// work counts: a finished assignment that was never estimated is not something
/// the planner still has to guess at, so counting it would overstate the number.
let missingEstimateCount (facts: Facts list) =
    facts |> List.filter isOpen |> List.filter estimateIsDefaulted |> List.length

/// Progress exactly as recorded, clamped into range.
///
/// **Not** derived, not blended with the checklist, and **not** forced to 100
/// because the status says Submitted — a submitted assignment shows the progress
/// its author actually left on it.
let progress (f: Facts) = max 0 (min 100 f.Assignment.Progress)

/// The user's own checklist, as a second and separately labelled progress
/// signal. Never folded into `progress`.
let checklist (f: Facts) = Assignment.checklistProgress f.Assignment

let daysUntilDeadline (now: DateTime) (f: Facts) =
    f.Assignment.Deadline |> Option.map (fun d -> (d - now).TotalDays)

let isOverdue (now: DateTime) (f: Facts) =
    isOpen f
    && (match f.Assignment.Deadline with
        | Some d -> d < now
        | None -> false)

/// Started, unfinished, and untouched for `Threshold.stalledDays`.
let stalledDays (now: DateTime) (f: Facts) =
    let p = progress f

    if isOpen f && p > 0 && p < 100 then
        let idle = (now - f.Assignment.UpdatedAt).TotalDays
        if idle >= float Threshold.stalledDays then Some(int idle) else None
    else
        None

// ---------------------------------------------------------------------------
// risk — one deterministic model, used everywhere
// ---------------------------------------------------------------------------

/// One reason a piece of work is at risk. Carried as data so the card can print
/// the reasons rather than only the level, and so a test can assert which signal
/// fired rather than only the total.
[<RequireQualifiedAccess>]
type Signal =
    | Overdue of days: int
    | DueWithin of days: int
    /// The scheduler ran and could not place this work. Carries its own reason.
    | PlannerConflict of reason: string
    /// Less study time is scheduled than the work needs.
    | UnderScheduled of shortfallMinutes: int
    /// Nothing at all is scheduled and the deadline is inside the horizon.
    | NotScheduled
    | Stalled of days: int
    | NotStarted
    /// Informational: contributes no points, but is worth saying.
    | NoDeadline
    | NoEstimate

[<RequireQualifiedAccess>]
module Signal =

    /// Points contributed to the risk score. **Grade weight is deliberately not
    /// here**: important work is not automatically endangered work, and treating
    /// it as such would make every 40% component permanently "high risk" no
    /// matter how far ahead its author was.
    let points =
        function
        | Signal.Overdue _ -> 4
        | Signal.DueWithin d when d < 1 -> 3
        | Signal.DueWithin d when d < 3 -> 2
        | Signal.DueWithin _ -> 1
        | Signal.PlannerConflict _ -> 2
        | Signal.UnderScheduled _ -> 2
        | Signal.NotScheduled -> 1
        | Signal.Stalled _ -> 1
        | Signal.NotStarted -> 1
        | Signal.NoDeadline -> 0
        | Signal.NoEstimate -> 0

    /// Words, always — a signal is readable with colour removed.
    let text =
        function
        | Signal.Overdue d when d <= 1 -> "Overdue"
        | Signal.Overdue d -> sprintf "Overdue by %d days" d
        | Signal.DueWithin d when d < 1 -> "Due within a day"
        | Signal.DueWithin 1 -> "Due tomorrow"
        | Signal.DueWithin d -> sprintf "Due in %d days" d
        | Signal.PlannerConflict reason -> sprintf "The planner could not fit this — %s" reason
        | Signal.UnderScheduled shortfall ->
            sprintf "%s less study time is scheduled than this needs" (Format.duration shortfall)
        | Signal.NotScheduled -> "No study time is scheduled for this"
        | Signal.Stalled d -> sprintf "No progress recorded for %d days" d
        | Signal.NotStarted -> "Not started"
        | Signal.NoDeadline -> "No deadline set"
        | Signal.NoEstimate -> "No effort estimate — the planner assumes 4 hours"

    /// Whether the signal is a warning or merely a note about missing data.
    let isWarning s = points s > 0

[<RequireQualifiedAccess>]
type Risk =
    /// Submitted or graded. Outstanding work is what carries risk; finished work
    /// is not "low risk", it is not in the running at all.
    | Settled
    | Low
    | Medium
    | High
    | Critical

[<RequireQualifiedAccess>]
module Risk =

    let label =
        function
        | Risk.Settled -> "Settled"
        | Risk.Low -> "Low"
        | Risk.Medium -> "Medium"
        | Risk.High -> "High"
        | Risk.Critical -> "Critical"

    /// Order for comparison. `Settled` sits below `Low` because it is not a risk
    /// at all.
    let rank =
        function
        | Risk.Settled -> -1
        | Risk.Low -> 0
        | Risk.Medium -> 1
        | Risk.High -> 2
        | Risk.Critical -> 3

    let atLeast (bar: Risk) (r: Risk) = rank r >= rank bar

    let ofPoints (points: int) =
        if points >= Threshold.criticalPoints then Risk.Critical
        elif points >= Threshold.highPoints then Risk.High
        elif points >= Threshold.mediumPoints then Risk.Medium
        else Risk.Low

/// Every signal firing on one assignment, in a stable order.
let signalsFor (now: DateTime) (f: Facts) : Signal list =
    if not (isOpen f) then
        []
    else
        let remaining = remainingMinutes f
        let days = daysUntilDeadline now f

        [ match days with
          | Some d when d < 0.0 -> Signal.Overdue(int (abs d))
          | Some d when d < 7.0 -> Signal.DueWithin(int d)
          | Some _ -> ()
          | None -> Signal.NoDeadline

          match f.UnplacedReason with
          | Some reason -> Signal.PlannerConflict reason
          | None -> ()

          // Scheduling is only judged against a deadline inside the horizon:
          // beyond it the planner has legitimately not got to the work yet.
          match days with
          | Some d when d >= 0.0 && d <= float Threshold.unscheduledHorizonDays && remaining > 0 ->
              if f.ScheduledMinutes = 0 then Signal.NotScheduled
              elif f.ScheduledMinutes < remaining then
                  Signal.UnderScheduled(remaining - f.ScheduledMinutes)
          | _ -> ()

          match stalledDays now f with
          | Some d -> Signal.Stalled d
          | None -> ()

          if progress f = 0 then
              match days with
              | Some d when d < 7.0 -> Signal.NotStarted
              | _ -> ()

          if estimateIsDefaulted f then Signal.NoEstimate ]

let riskPoints (now: DateTime) (f: Facts) =
    signalsFor now f |> List.sumBy Signal.points

let riskOf (now: DateTime) (f: Facts) =
    if not (isOpen f) then Risk.Settled else Risk.ofPoints (riskPoints now f)

// ---------------------------------------------------------------------------
// pacing
// ---------------------------------------------------------------------------

/// How the work is going, in one word. Deterministic, and never reassurance:
/// every case below is a statement about recorded data.
[<RequireQualifiedAccess>]
type Pace =
    | Submitted
    | Overdue
    | NotStarted
    | Stalled
    | Behind
    | OnPace
    /// Open work with no deadline — there is nothing to be on pace *for*.
    | Unpaced

[<RequireQualifiedAccess>]
module Pace =
    let label =
        function
        | Pace.Submitted -> "submitted"
        | Pace.Overdue -> "overdue"
        | Pace.NotStarted -> "not started"
        | Pace.Stalled -> "stalled"
        | Pace.Behind -> "behind"
        | Pace.OnPace -> "on pace"
        | Pace.Unpaced -> "no deadline"

/// **"Behind" is a comparison, not a feeling.** Elapsed share of the time between
/// creation and deadline, against recorded progress: if two thirds of the window
/// has gone and a third of the work is done, the work is behind. Without a
/// deadline there is no window and the question is not asked.
let paceOf (now: DateTime) (f: Facts) =
    if not (isOpen f) then Pace.Submitted
    elif isOverdue now f then Pace.Overdue
    else
        match stalledDays now f with
        | Some _ -> Pace.Stalled
        | None ->
            if progress f = 0 then
                Pace.NotStarted
            else
                match f.Assignment.Deadline with
                | None -> Pace.Unpaced
                | Some deadline ->
                    let window = (deadline - f.Assignment.CreatedAt).TotalMinutes

                    if window <= 0.0 then
                        Pace.OnPace
                    else
                        let elapsedShare = (now - f.Assignment.CreatedAt).TotalMinutes / window
                        let doneShare = float (progress f) / 100.0
                        if doneShare + 0.1 < elapsedShare then Pace.Behind else Pace.OnPace

/// The one line under the progress bar.
///
/// **The rule, so it is never reassurance:** if any warning signal is firing, the
/// insight is the text of the highest-scoring one — the single most important
/// true thing about this work. Only when nothing is wrong does it fall back to a
/// statement of pace, and even that quotes real figures.
let insight (now: DateTime) (f: Facts) =
    if not (isOpen f) then
        match f.Assignment.Status with
        | AssignmentStatus.Graded -> "Graded."
        | _ -> "Submitted."
    else
        let warnings = signalsFor now f |> List.filter Signal.isWarning

        if not warnings.IsEmpty then
            warnings |> List.maxBy Signal.points |> Signal.text
        else
            let left = Format.duration (remainingMinutes f)

            match paceOf now f with
            | Pace.Unpaced -> sprintf "No deadline set — about %s of work left." left
            | Pace.Behind -> sprintf "Behind the pace its deadline sets — %d%% done, about %s left." (progress f) left
            | _ -> sprintf "On pace — %d%% done, about %s left." (progress f) left

// ---------------------------------------------------------------------------
// stages — the real progression, not invented milestones
// ---------------------------------------------------------------------------

/// The redesign's milestone stepper is `Research · Planning · Draft · Review ·
/// Submission`. **None of those exists.** `AssignmentStatus` is the domain's own
/// progression and is what this shows instead.
///
/// `Blocked` is deliberately not a stage: it is not a point on the path, it is a
/// state the current point can be in, and it is surfaced as a warning on the
/// active stage rather than as a sixth dot that would imply going backwards.
[<RequireQualifiedAccess>]
type Stage =
    | NotStarted
    | InProgress
    | Submitted
    | Graded

[<RequireQualifiedAccess>]
module Stage =

    let order = [ Stage.NotStarted; Stage.InProgress; Stage.Submitted; Stage.Graded ]

    let label =
        function
        | Stage.NotStarted -> "Not started"
        | Stage.InProgress -> "In progress"
        | Stage.Submitted -> "Submitted"
        | Stage.Graded -> "Graded"

    let rank =
        function
        | Stage.NotStarted -> 0
        | Stage.InProgress -> 1
        | Stage.Submitted -> 2
        | Stage.Graded -> 3

    /// Which stage the assignment is currently at. `Blocked` maps to
    /// `InProgress` — work that has been started and is held up.
    let ofStatus =
        function
        | AssignmentStatus.NotStarted -> Stage.NotStarted
        | AssignmentStatus.InProgress
        | AssignmentStatus.Blocked -> Stage.InProgress
        | AssignmentStatus.Submitted -> Stage.Submitted
        | AssignmentStatus.Graded -> Stage.Graded

[<RequireQualifiedAccess>]
type StageState =
    | Done
    | Current
    | Pending

let stageStates (f: Facts) =
    let current = Stage.ofStatus f.Assignment.Status

    Stage.order
    |> List.map (fun s ->
        let state =
            if Stage.rank s < Stage.rank current then StageState.Done
            elif s = current then StageState.Current
            else StageState.Pending

        s, state)

let isBlocked (f: Facts) = f.Assignment.Status = AssignmentStatus.Blocked

// ---------------------------------------------------------------------------
// linked resources — real relationships only
// ---------------------------------------------------------------------------

/// A category of related content, with a real count behind it.
///
/// The redesign shows six fixed categories including `Documents (0)` and
/// `Flashcards (12)`. A zero row is a claim that a relationship was checked and
/// found empty; a fabricated row is worse still. Only categories with a genuine
/// count appear here.
type LinkedGroup =
    { Kind: EntityKind
      Count: int
      Label: string }

let private kindLabel (kind: EntityKind) (count: int) =
    let name =
        match kind with
        | EntityKind.Module -> "Module"
        | EntityKind.Assignment -> "Assignment"
        | EntityKind.Event -> "Calendar event"
        | EntityKind.Note -> "Note"
        | EntityKind.Document -> "Document"
        | EntityKind.Lecture -> "Lecture"
        | EntityKind.InboxItem -> "Inbox item"
        | EntityKind.Session -> "Planner session"
        | EntityKind.Conversation -> "Conversation"
        | EntityKind.RevisionItem -> "Flashcard"

    if count = 1 then sprintf "%s (1)" name else sprintf "%ss (%d)" name count

/// Everything genuinely attached to this assignment.
///
/// Two of these are foreign keys rather than links and are passed in separately:
/// planner sessions (`PlannedSession.AssignmentId`) and timeline entries
/// (`ActivityEntry.Entity`). The rest come from `ILinkRepository.GetFor`.
let linkedGroups (f: Facts) : LinkedGroup list =
    [ for kind, count in f.Linked do
          if count > 0 then
              { Kind = kind; Count = count; Label = kindLabel kind count }

      if f.SessionCount > 0 then
          { Kind = EntityKind.Session
            Count = f.SessionCount
            Label = kindLabel EntityKind.Session f.SessionCount }

      if f.ActivityCount > 0 then
          { Kind = EntityKind.InboxItem
            Count = f.ActivityCount
            Label =
              if f.ActivityCount = 1 then "Timeline entry (1)"
              else sprintf "Timeline entries (%d)" f.ActivityCount } ]

// ---------------------------------------------------------------------------
// filters
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type Filter =
    | All
    | DueToday
    | ThisWeek
    | HighWeight
    | HighRisk
    | Completed
    | NeedsAttention

[<RequireQualifiedAccess>]
module Filter =

    let all =
        [ Filter.All
          Filter.DueToday
          Filter.ThisWeek
          Filter.HighWeight
          Filter.HighRisk
          Filter.Completed
          Filter.NeedsAttention ]

    let label =
        function
        | Filter.All -> "All"
        | Filter.DueToday -> "Due today"
        | Filter.ThisWeek -> "This week"
        | Filter.HighWeight -> "High weight"
        | Filter.HighRisk -> "High risk"
        | Filter.Completed -> "Completed"
        | Filter.NeedsAttention -> "Needs attention"

    /// Shown when a filter selects nothing, so an empty list explains itself.
    let emptyMessage =
        function
        | Filter.All -> "No coursework yet."
        | Filter.DueToday -> "Nothing is due today."
        | Filter.ThisWeek -> "Nothing is due this week."
        | Filter.HighWeight ->
            sprintf "No open assignment is worth %g%% or more of its module." Threshold.highWeightPercent
        | Filter.HighRisk -> "No open assignment is at high risk."
        | Filter.Completed -> "Nothing has been submitted or graded yet."
        | Filter.NeedsAttention -> "Nothing needs attention — no overdue, stalled, unscheduled or unestimated work."

/// Work that wants a decision from the user. Every clause is a real signal:
/// overdue, high risk, stalled, a planner conflict, or a missing field the rest
/// of the application depends on.
let needsAttention (now: DateTime) (f: Facts) =
    isOpen f
    && (isOverdue now f
        || Risk.atLeast Risk.High (riskOf now f)
        || (stalledDays now f).IsSome
        || f.UnplacedReason.IsSome
        || f.Assignment.Deadline.IsNone
        || estimateIsDefaulted f)

/// `weekStart` is passed in rather than computed, because it must come from
/// `StudyTime.weekStart` with the culture's own first day — the one definition
/// the Dashboard and the Timeline already share. A second week rule on this
/// screen would be a screen that disagrees with the other two.
let matches (now: DateTime) (weekStart: DateTime) (filter: Filter) (f: Facts) =
    match filter with
    | Filter.All -> true
    | Filter.DueToday ->
        match f.Assignment.Deadline with
        | Some d -> d.Date = now.Date
        | None -> false
    | Filter.ThisWeek ->
        match f.Assignment.Deadline with
        | Some d -> d >= weekStart && d < weekStart.AddDays 7.0
        | None -> false
    | Filter.HighWeight ->
        match f.Assignment.Weighting with
        | Some w -> w >= Threshold.highWeightPercent
        | None -> false
    | Filter.HighRisk -> Risk.atLeast Risk.High (riskOf now f)
    | Filter.Completed -> not (isOpen f)
    | Filter.NeedsAttention -> needsAttention now f

/// The visible list: filtered, then ordered by how much it wants doing.
///
/// Ordering is explicit rather than inherited from the repository, so the list
/// cannot silently reorder under a refresh and move a card out from under the
/// pointer. Open work first, then by risk, then by the nearer deadline, then by
/// the heavier weight, then by title so the order is total.
/// **Which card is open, including the one nobody asked for.**
///
/// The screen opens its first visible assignment so the page does not arrive as
/// a list of closed rows. That default must never fight the user, which is what
/// `chosen` is for: without it the model cannot tell "nothing has been opened
/// yet" from "the user closed the one that was open", and collapsing the first
/// card would make it spring straight back open.
///
/// * a still-visible choice is kept, always — an explicit selection wins;
/// * a choice that has been filtered away or deleted falls back to the first
///   visible card, deterministically, rather than leaving nothing open;
/// * a deliberate collapse stays collapsed;
/// * an empty list opens nothing.
let expansionFor (chosen: bool) (current: AssignmentId option) (visibleFacts: Facts list) =
    let stillVisible id =
        visibleFacts |> List.exists (fun f -> f.Assignment.Id = id)

    match current with
    | Some id when stillVisible id -> Some id
    | Some _ ->
        // The chosen card is gone. Falling back keeps the screen in the same
        // shape it had a moment ago rather than silently closing everything.
        visibleFacts |> List.tryHead |> Option.map (fun f -> f.Assignment.Id)
    | None when chosen -> None
    | None -> visibleFacts |> List.tryHead |> Option.map (fun f -> f.Assignment.Id)

let visible (now: DateTime) (weekStart: DateTime) (filter: Filter) (facts: Facts list) =
    facts
    |> List.filter (matches now weekStart filter)
    |> List.sortBy (fun f ->
        (if isOpen f then 0 else 1),
        -(Risk.rank (riskOf now f)),
        (f.Assignment.Deadline |> Option.map (fun d -> d.Ticks) |> Option.defaultValue Int64.MaxValue),
        -(f.Assignment.Weighting |> Option.defaultValue 0.0),
        f.Assignment.Title)

// ---------------------------------------------------------------------------
// summary metrics
// ---------------------------------------------------------------------------

/// A summary figure.
///
/// `Value` is an option because some figures are genuinely unknown rather than
/// zero: an average over nothing is not 0%, and an average weight over
/// assignments that carry no weighting is not 0% either. `None` renders as
/// `unavailableMark` and **nothing else** — the dash is the whole statement.
///
/// `Note` is a qualifier on a figure that is real but conditional, never
/// empty-state prose. An empty screen therefore reads `0 · — · 0h · 0 · —` with
/// no sentences under it at all.
type Metric =
    { Label: string
      Value: string option
      Note: string option }

/// **All five metrics describe OPEN coursework**, because the page's question is
/// "what do I have left". Submitted and graded work is excluded from every one of
/// them and is reachable through the Completed filter.
///
/// The visible label is simply "Assignments": the scope is documented here and
/// pinned by test rather than spelled out on a card that has room for two words.
/// When completed work exists, the card says how much — which is the useful half
/// of the same fact.
let metrics (now: DateTime) (facts: Facts list) : Metric list =
    let openWork = facts |> List.filter isOpen
    let weighted = openWork |> List.choose (fun f -> f.Assignment.Weighting)
    let defaulted = openWork |> List.filter estimateIsDefaulted |> List.length

    let plural n singular =
        if n = 1 then sprintf "%d %s" n singular else sprintf "%d %ss" n singular

    [ { Label = "Assignments"
        Value = Some(string openWork.Length)
        Note =
          let closed = facts.Length - openWork.Length
          if closed > 0 then Some(sprintf "%d completed" closed) else None }

      { Label = "Average progress"
        // An average over nothing is not zero, and it is certainly not 100%.
        Value =
          if openWork.IsEmpty then None
          else Some(sprintf "%d%%" (int (Math.Round(openWork |> List.averageBy (progress >> float)))))
        Note = None }

      { Label = "Hours remaining"
        Value = Some(Format.hours (float (openWork |> List.sumBy remainingMinutes) / 60.0))
        Note =
          // A real qualifier on a real figure: the planner guessed some of it.
          if defaulted > 0 then Some(sprintf "%s estimated by default" (plural defaulted "assignment"))
          else None }

      { Label = "High Risk"
        Value = Some(string (openWork |> List.filter (fun f -> Risk.atLeast Risk.High (riskOf now f)) |> List.length))
        Note = None }

      { Label = "Average grade weight"
        // Missing weightings are NOT counted as zero — that would quietly halve
        // the average every time a weighting was left blank.
        Value =
          if weighted.IsEmpty then None
          else Some(sprintf "%g%%" (Math.Round(List.average weighted, 1)))
        Note =
          if weighted.IsEmpty || weighted.Length = openWork.Length then None
          else Some(sprintf "%d of %d weighted" weighted.Length openWork.Length) } ]

// ---------------------------------------------------------------------------
// forecast — deterministic, and labelled as such
// ---------------------------------------------------------------------------

/// The overall direction of travel.
[<RequireQualifiedAccess>]
type Trajectory =
    /// No open coursework at all. Not "on track" — there is nothing to track.
    | NothingOutstanding
    | OnTrack
    | Watch
    | AtRisk
    | Critical

[<RequireQualifiedAccess>]
module Trajectory =

    let label =
        function
        | Trajectory.NothingOutstanding -> "Nothing outstanding"
        | Trajectory.OnTrack -> "On track"
        | Trajectory.Watch -> "Watch"
        | Trajectory.AtRisk -> "At risk"
        | Trajectory.Critical -> "Critical"

    let detail =
        function
        | Trajectory.NothingOutstanding -> "Every assignment has been submitted or graded."
        | Trajectory.OnTrack -> "No open assignment is showing a risk signal."
        | Trajectory.Watch -> "Some work is close to its deadline or short of scheduled time."
        | Trajectory.AtRisk -> "At least one assignment is unlikely to be finished as scheduled."
        | Trajectory.Critical -> "At least one assignment is overdue or cannot be fitted before its deadline."

/// The worst open risk, which is the honest summary: a plan is only as sound as
/// the piece of work most likely to break it.
let trajectory (now: DateTime) (facts: Facts list) =
    let openWork = facts |> List.filter isOpen

    if openWork.IsEmpty then
        Trajectory.NothingOutstanding
    else
        match openWork |> List.map (riskOf now) |> List.maxBy Risk.rank with
        | Risk.Critical -> Trajectory.Critical
        | Risk.High -> Trajectory.AtRisk
        | Risk.Medium -> Trajectory.Watch
        | _ -> Trajectory.OnTrack

/// The open assignment carrying the most risk, if any is above Low.
///
/// Ties break on the nearer deadline, then the heavier weight, then the title —
/// a total order, so the answer cannot depend on repository ordering.
let highestRisk (now: DateTime) (facts: Facts list) =
    facts
    |> List.filter (fun f -> isOpen f && Risk.atLeast Risk.Medium (riskOf now f))
    |> List.sortBy (fun f ->
        -(riskPoints now f),
        (f.Assignment.Deadline |> Option.map (fun d -> d.Ticks) |> Option.defaultValue Int64.MaxValue),
        -(f.Assignment.Weighting |> Option.defaultValue 0.0),
        f.Assignment.Title)
    |> List.tryHead

/// What to do next.
///
/// **Deterministic, not an AI suggestion.** Ordered by risk points, then the
/// nearer deadline, then the heavier weight, then the least progress (finish what
/// is nearly done last, because it needs the least rescue), then the title.
/// Overdue work sorts to the front through its risk points, not a special case.
let suggestedNext (now: DateTime) (facts: Facts list) =
    facts
    |> List.filter isOpen
    |> List.sortBy (fun f ->
        -(riskPoints now f),
        (f.Assignment.Deadline |> Option.map (fun d -> d.Ticks) |> Option.defaultValue Int64.MaxValue),
        -(f.Assignment.Weighting |> Option.defaultValue 0.0),
        progress f,
        f.Assignment.Title)
    |> List.tryHead

/// The workload figure, from the SAME function the summary metric uses, so the
/// two cannot drift apart.
let workloadMinutes (facts: Facts list) =
    facts |> List.filter isOpen |> List.sumBy remainingMinutes

// ---------------------------------------------------------------------------
// risk overview
// ---------------------------------------------------------------------------

/// One aggregate row. `Value` is an option so a row whose inputs are missing
/// reports that rather than quietly reading as "Low".
type OverviewRow =
    { Label: string
      Value: string option
      /// Absent when `Value` is `None`.
      Level: Risk option
      /// Why the value is missing, or what qualifies it.
      Note: string option }

/// **"Grade risk" is absent.** Grades are not modelled anywhere in the domain —
/// no mark, no target, no prediction — so the row has no source. `Scheduled
/// cover` takes its place, which is a real comparison between the work left and
/// the study time booked for it.
let riskOverview (now: DateTime) (facts: Facts list) : OverviewRow list =
    let openWork = facts |> List.filter isOpen

    let worst (pick: Facts -> Signal list) =
        let levels =
            openWork
            |> List.map (fun f -> pick f |> List.sumBy Signal.points |> Risk.ofPoints)

        if levels.IsEmpty then None else Some(levels |> List.maxBy Risk.rank)

    let deadlineSignals f =
        signalsFor now f
        |> List.filter (function
            | Signal.Overdue _
            | Signal.DueWithin _ -> true
            | _ -> false)

    let progressSignals f =
        signalsFor now f
        |> List.filter (function
            | Signal.Stalled _
            | Signal.NotStarted -> true
            | _ -> false)

    let scheduleSignals f =
        signalsFor now f
        |> List.filter (function
            | Signal.NotScheduled
            | Signal.UnderScheduled _
            | Signal.PlannerConflict _ -> true
            | _ -> false)

    let undated = openWork |> List.filter (fun f -> f.Assignment.Deadline.IsNone) |> List.length
    let unestimated = openWork |> List.filter estimateIsDefaulted |> List.length
    // Same rule as `missingEstimateCount`, which is what the tests read.
    let conflicts = openWork |> List.filter (fun f -> f.UnplacedReason.IsSome) |> List.length

    let levelRow label level note =
        { Label = label
          Value = level |> Option.map Risk.label
          Level = level
          Note = note }

    // With nothing open, every row is a single dash and NOTHING else. Repeating
    // "nothing outstanding" five times says once what the panel already says.
    let empty = openWork.IsEmpty

    [ levelRow
          "Deadline risk"
          (if empty then None else worst deadlineSignals)
          (if not empty && undated > 0 then Some(sprintf "%d without a deadline" undated) else None)

      levelRow "Progress risk" (if empty then None else worst progressSignals) None

      levelRow "Scheduled cover" (if empty then None else worst scheduleSignals) None

      { Label = "Missing estimates"
        Value = if empty then None else Some(string unestimated)
        Level = None
        Note = if not empty && unestimated > 0 then Some "the planner assumes 4 hours each" else None }

      { Label = "Planner conflicts"
        Value = if empty then None else Some(string conflicts)
        Level = None
        Note = if not empty && conflicts > 0 then Some "work the scheduler could not place" else None } ]

// ---------------------------------------------------------------------------
// actions
// ---------------------------------------------------------------------------

/// The actions an assignment card really offers.
///
/// The redesign shows Open, Start Focus Session, Ask Assistant, Generate Notes,
/// Open Planner and View Timeline. Two are omitted rather than drawn dead:
///
/// - **Start Focus Session** — there is no focus-session workflow anywhere in the
///   application. The Dashboard's own "Focus Session" quick action simply
///   navigates to the Planner, which is what **Open Planner** already does here,
///   so a second button would be the same button twice.
/// - **Generate Notes** — nothing generates a note from an assignment.
///   `AiService` can summarise arbitrary text and make flashcards from a source,
///   but neither is wired to assignments, and building that is a new feature
///   rather than a migration.
[<RequireQualifiedAccess>]
type ActionId =
    | Open
    | AskAssistant
    | OpenPlanner
    | ViewTimeline

type ActionState =
    { Id: ActionId
      Label: string
      Primary: bool
      Enabled: bool
      /// Printed as visible text beneath the row when the action is disabled.
      DisabledBecause: string option }

type ActionContext =
    { HasSessions: bool
      HasActivity: bool
      /// Whether an AI provider is configured. The assistant button pre-fills a
      /// real question; without a provider there is nothing to answer it.
      AiAvailable: bool
      /// True while a save or delete is in flight.
      Busy: bool }

let actions (ctx: ActionContext) : ActionState list =
    let state id label primary enabled why =
        { Id = id
          Label = label
          Primary = primary
          Enabled = enabled && not ctx.Busy
          DisabledBecause =
            if ctx.Busy then Some "a change to this assignment is still saving"
            elif enabled then None
            else Some why }

    [ state ActionId.Open "Open" true true ""

      state
          ActionId.AskAssistant
          "Ask Assistant"
          false
          ctx.AiAvailable
          "no AI provider is configured — connect Claude in Connections"

      state
          ActionId.OpenPlanner
          "Open Planner"
          false
          ctx.HasSessions
          "the planner has not scheduled any session for this assignment"

      state
          ActionId.ViewTimeline
          "View Timeline"
          false
          ctx.HasActivity
          "nothing has been recorded against this assignment yet" ]

/// The question the assistant is asked. Real assignment context, phrased as the
/// user would ask it — the button pre-fills this and navigates; it does not claim
/// the assistant has already answered.
let assistantPrompt (f: Facts) =
    let deadline =
        match f.Assignment.Deadline with
        | Some d -> sprintf ", due %s" (d.ToString "d MMMM yyyy 'at' HH:mm")
        | None -> ""

    let moduleText =
        match moduleCode f with
        | Some code -> sprintf " for %s" code
        | None -> ""

    sprintf
        "How should I approach \"%s\"%s%s? It is %d%% complete and I have about %s of work left."
        f.Assignment.Title
        moduleText
        deadline
        (progress f)
        (Format.duration (remainingMinutes f))

// ---------------------------------------------------------------------------
// page composition
// ---------------------------------------------------------------------------

/// The page, as data. The view maps over this list, so a section cannot be
/// reordered or dropped without editing it — which the layout tests assert.
[<RequireQualifiedAccess>]
type Section =
    | Header
    | Metrics
    | Filters
    | Workspace

let sectionOrder = [ Section.Header; Section.Metrics; Section.Filters; Section.Workspace ]

// ---------------------------------------------------------------------------
// responsive layout
// ---------------------------------------------------------------------------

/// Whether the forecast column sits beside the list or beneath it. The same
/// threshold as the Planner, the Timeline and the Inbox, so the four screens
/// break at one width rather than four.
let showsSidePanel (contentWidth: float) = contentWidth >= 1100.0

/// Columns for the five summary cards. They reflow rather than compress.
let metricColumns (contentWidth: float) =
    if contentWidth < 700.0 then 2
    elif contentWidth < 1180.0 then 3
    else 5
