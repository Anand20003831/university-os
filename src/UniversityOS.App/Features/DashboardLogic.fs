namespace UniversityOS.App.Features

open System
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.App

/// Everything the Dashboard *decides*, separated from how it is drawn.
///
/// The redesign's dashboard is mostly derived values — a status per timeline
/// row, a ring percentage, a priority list, a set of insights. Deriving them in
/// the view would make them untestable and would recompute them on every
/// repaint, including the ones caused by a background frame tick. They are pure
/// functions of the loaded data instead, computed once per load.
[<RequireQualifiedAccess>]
module DashboardLogic =

    /// Where a scheduled item sits relative to now. Carried as data rather than
    /// a colour so the view can render both a label and a colour — status must
    /// never be communicated by colour alone.
    type ItemStatus =
        | Done
        | Now
        | Next
        | Later

    [<RequireQualifiedAccess>]
    module ItemStatus =
        let label =
            function
            | Done -> "DONE"
            | Now -> "NOW"
            | Next -> "NEXT"
            | Later -> "SOON"

    /// One row of the merged today timeline: fixed events and planned study
    /// sessions interleaved in time order.
    type TimelineItem =
        { StartsAt: DateTime
          EndsAt: DateTime
          Title: string
          /// Session id when this row is a study session and can be ticked off.
          Session: PlannedSession option
          Kind: EventKind option
          Detail: string
          Status: ItemStatus }

    let private minutesBetween (a: DateTime) (b: DateTime) = int (b - a).TotalMinutes

    /// Status for an item, given the current time. `completed` is only
    /// meaningful for sessions; events are judged purely on the clock.
    let statusOf (now: DateTime) (startsAt: DateTime) (endsAt: DateTime) (completed: bool) =
        if completed then Done
        elif endsAt <= now then Done
        elif startsAt <= now then Now
        elif startsAt <= now.AddHours 2.0 then Next
        else Later

    /// The merged, time-ordered timeline the redesign shows.
    let timeline (data: DashboardData) : TimelineItem list =
        let events =
            data.TodayEvents
            |> List.map (fun e ->
                { StartsAt = e.StartsAt
                  EndsAt = e.EndsAt
                  Title = e.Title
                  Session = None
                  Kind = Some e.Kind
                  Detail = sprintf "%d min" (minutesBetween e.StartsAt e.EndsAt)
                  Status = statusOf data.Now e.StartsAt e.EndsAt false })

        let sessions =
            data.TodaySessions
            |> List.map (fun s ->
                { StartsAt = s.StartsAt
                  EndsAt = s.EndsAt
                  Title = s.Title
                  Session = Some s
                  Kind = None
                  Detail =
                    let mins = PlannedSession.durationMinutes s
                    if String.IsNullOrWhiteSpace s.Reason then sprintf "%d min" mins
                    else sprintf "%d min · %s" mins s.Reason
                  Status = statusOf data.Now s.StartsAt s.EndsAt s.Completed })

        events @ sessions |> List.sortBy (fun i -> i.StartsAt)

    /// Percentage for the header's "today's plan" ring: how much of the day's
    /// intended study is already done. Total intent is what has been completed
    /// plus what is still planned, so the ring fills as sessions are ticked off.
    ///
    /// **`None` when there is no intent to measure against**, rather than 0.
    ///
    /// This used to return a hardcoded `0` when the denominator was zero, which
    /// made "no plan exists" indistinguishable from "a plan exists and none of it
    /// is done" — the screen claimed 0% of a plan that was never made. A user
    /// reported it on first run: the dial read 0% before they had generated
    /// anything. `PlannerLogic.scoreOf` had already settled the rule for its own
    /// dial — *"Never 100 for an empty plan — never any number at all"* — and
    /// this now matches it.
    ///
    /// A genuine 0% is unchanged: 0 done against 120 planned still returns
    /// `Some 0`, because there the ratio is defined and the answer really is zero.
    let planPercent (data: DashboardData) =
        let total = data.StudiedMinutesToday + data.PlannedMinutesToday

        if total <= 0 then
            None
        else
            Some(int (round (100.0 * float data.StudiedMinutesToday / float total)))

    /// Percentage of the daily study target reached.
    let dailyTargetPercent (data: DashboardData) =
        if data.DailyTargetMinutes <= 0 then 0
        else min 100 (int (round (100.0 * float data.StudiedMinutesToday / float data.DailyTargetMinutes)))

    let weeklyTargetPercent (data: DashboardData) =
        if data.WeeklyTargetMinutes <= 0 then 0
        else min 100 (int (round (100.0 * float data.StudiedMinutesThisWeek / float data.WeeklyTargetMinutes)))

    /// How soon the nearest deadline is, as a rendered string, or None.
    let nextDeadline (data: DashboardData) =
        data.UpcomingDeadlines
        |> List.tryHead
        |> Option.bind (fun (a, _) -> a.Deadline |> Option.map (fun d -> a, d))

    /// Urgency band for a deadline. Returned as a case, not a colour, so the
    /// view can pair it with a word.
    type Urgency =
        | Critical
        | Soon
        | Comfortable

    let urgencyOf (now: DateTime) (deadline: DateTime) =
        if deadline < now.AddDays 2.0 then Critical
        elif deadline < now.AddDays 5.0 then Soon
        else Comfortable

    [<RequireQualifiedAccess>]
    module Urgency =
        let label =
            function
            | Critical -> "Critical"
            | Soon -> "Soon"
            | Comfortable -> "On track"

    /// A priority line: something the user should know, worst first.
    ///
    /// Derived from real loaded data — deadlines, progress, inbox depth — not
    /// authored copy. Each carries its urgency so the view can show a word as
    /// well as a colour.
    type PriorityLine = { Urgency: Urgency; Text: string }

    let priorities (data: DashboardData) : PriorityLine list =
        let now = data.Now

        let deadlineLines =
            data.UpcomingDeadlines
            |> List.truncate 3
            |> List.choose (fun (a, code) ->
                a.Deadline
                |> Option.map (fun d ->
                    let prefix =
                        match code with
                        | Some c -> c + " "
                        | None -> ""

                    { Urgency = urgencyOf now d
                      Text =
                        sprintf
                            "%s%s — %s, %d%% done"
                            prefix
                            a.Title
                            (Format.deadline now d)
                            a.Progress }))

        let inboxLine =
            if data.InboxCount > 0 then
                [ { Urgency = Soon
                    Text =
                      sprintf
                          "%d inbox item%s waiting to be triaged"
                          data.InboxCount
                          (if data.InboxCount = 1 then "" else "s") } ]
            else
                []

        let idleLine =
            if data.TodaySessions.IsEmpty && data.FreeMinutesToday > 60 then
                [ { Urgency = Soon
                    Text = sprintf "%s free today with nothing planned" (Format.duration data.FreeMinutesToday) } ]
            else
                []

        deadlineLines @ inboxLine @ idleLine |> List.truncate 5

    /// Observations derived from the loaded data.
    ///
    /// Deliberately NOT called AI insights: these are rules over real numbers,
    /// and the redesign's AI copy has no data source behind it. Presenting
    /// rule-based text as model output would be a lie about how the app works.
    let insights (data: DashboardData) : string list =
        [ if data.FreeMinutesToday > 0 && data.PlannedMinutesToday > 0 then
              sprintf
                  "%s planned against %s free — the day fits."
                  (Format.duration data.PlannedMinutesToday)
                  (Format.duration data.FreeMinutesToday)

          if data.PlannedMinutesToday > data.FreeMinutesToday && data.FreeMinutesToday > 0 then
              "More study is planned today than there is free time — consider regenerating the plan."

          match data.AverageProgress with
          | Some avg when avg < 25.0 && data.OpenAssignmentCount > 0 ->
              sprintf "Open assignments average %d%% — an early push would help." (int avg)
          | Some avg when avg >= 75.0 -> sprintf "Open assignments average %d%% — you are ahead." (int avg)
          | _ -> ()

          if data.StudiedMinutesThisWeek > 0 then
              sprintf
                  "%s studied this week, %d%% of target."
                  (Format.duration data.StudiedMinutesThisWeek)
                  (weeklyTargetPercent data)

          if data.UpcomingDeadlines.IsEmpty then
              "No deadlines in the next two weeks — a good window to get ahead." ]
        |> List.truncate 4

    /// **The recommended task's own facts.**
    ///
    /// The morning briefing exists to say what to start and why, and it carried
    /// a title, a module code and one reason line — nothing you could check the
    /// recommendation against. These are the task's real fields.
    ///
    /// A genuinely absent field is **omitted**, not rendered as a zero or a
    /// dash: an assignment with no deadline and one that is due at midnight are
    /// different facts, and a tile reading "0h" for an unknown estimate is a
    /// claim the data does not support.
    let recommendationFacts (data: DashboardData) : (string * string) list =
        match data.Recommendation with
        | None -> []
        | Some(task, _) ->
            [ match task.Deadline with
              | Some d -> "Due", Format.deadline data.Now d
              | None -> ()

              if task.RemainingMinutes > 0 then
                  "Remaining", Format.duration task.RemainingMinutes

              match task.Weighting with
              | Some w -> "Worth", sprintf "%g%% of the module" w
              | None -> ()

              "Priority", Priority.label task.Priority ]

    /// The briefing's stat tiles, value first.
    ///
    /// Carried as data rather than built inline so the set, the order and the
    /// wording are assertable — the same reason `snapshot` and `quickActions`
    /// are. Every value is a real figure from the loaded data.
    let briefingTiles (data: DashboardData) : (string * string) list =
        [ string (data.TodayEvents.Length + data.TodaySessions.Length), "Today's schedule"

          (match nextDeadline data with
           | Some(_, d) -> Format.deadline data.Now d
           | None -> "None"),
          "Next deadline"

          Format.duration data.FreeMinutesToday, "Free study time" ]

    /// **Today's timeline geometry.**
    ///
    /// The card shows the first five rows and scrolls for the rest. Two things
    /// this is deliberately NOT:
    ///
    /// **Not a truncated model.** Every item stays in the tree; the *viewport*
    /// stops at five. Cutting the list would put the last item of a busy day
    /// permanently out of reach, which is worse than a long card.
    ///
    /// **Not a guessed pixel height.** `viewportHeight` is computed from the row
    /// height the view actually applies, so the two cannot drift apart — a
    /// hardcoded 270 would silently show four-and-a-bit rows the moment a row
    /// grew.
    [<RequireQualifiedAccess>]
    module Timeline =

        /// Applied by `timelineRow` itself, so the arithmetic below describes
        /// the rows that are really drawn.
        [<Literal>]
        let rowHeight = 46.0

        [<Literal>]
        let rowSpacing = 10.0

        [<Literal>]
        let initialRows = 5

        /// Five rows and the four gaps between them.
        let viewportHeight =
            float initialRows * rowHeight + float (initialRows - 1) * rowSpacing

        /// Whether the viewport clips the list.
        let scrolls (count: int) = count > initialRows

        /// How many rows start out of view. Shown, because a list that scrolls
        /// with no scrollbar and no hint looks like a list that ends.
        let hiddenCount (count: int) = max 0 (count - initialRows)

    /// The productivity snapshot tiles. Every one is a real count.
    let snapshot (data: DashboardData) : (string * string) list =
        [ Format.duration data.StudiedMinutesThisWeek, "Studied this week"
          string data.CompletedSessionsToday, "Sessions done today"
          string data.OpenAssignmentCount, "Open assignments"
          string data.InboxCount, "Inbox items"
          string data.NoteCount, "Notes" ]

    // ---- responsive layout --------------------------------------------------

    /// The card's GEOMETRY tokens.
    ///
    /// Colour is deliberately absent: a card's surface is material, and material
    /// lives in `Design.Surfaces` where the sidebar, the shell and every other
    /// screen read the same roles. This module previously owned a `tint` that
    /// blended the shell chrome 34% toward the accent's dark shade — which is
    /// what made the whole Dashboard orange under amber. Cards now read
    /// `Theme.surfaces().Card` like everything else.
    [<RequireQualifiedAccess>]
    module Card =

        /// Fraction of GlassPanel's specular highlight a card keeps: **none**.
        ///
        /// The specular gradient runs from the top edge down over
        /// `Glass.highlightReach` — 38% of the panel — so on a card it is not an
        /// edge highlight at all, it is a pale region across the top third.
        /// Scaling it to 0.12 made that region fainter without making it any
        /// smaller, and across a page of ten stacked cards the repeated bands
        /// still read as white sections rather than as lit glass.
        ///
        /// Zero, not "very small", for a concrete reason: at 0.12 the effective
        /// alpha at the default glass setting is 0.0084, which quantises to
        /// 1–2 of 255 — enough to see as a band against a card at luminance 11,
        /// and enough to cost a shader allocation and a full-panel draw per card
        /// per frame. `Glass.drawsHighlight` now skips the pass entirely.
        ///
        /// The shell's single floating sidebar is unaffected and keeps the full
        /// 1.0: one tall panel reads as a lit pane, which is the approved
        /// floating-glass character.
        [<Literal>]
        let highlightScale = 0.0

        [<Literal>]
        let radius = 16.0

        [<Literal>]
        let actionRadius = 14.0

        [<Literal>]
        let actionMinHeight = 96.0

        /// Half the glass hairline's 1px stroke.
        ///
        /// A stroke is centred on its path, so an outline laid exactly on a
        /// panel's bounds puts half its width outside them, where the draw
        /// operation clips it away. Along the straight edges that loss is even
        /// and only looks thin; at the corners the clipping is asymmetric as the
        /// curve turns, which is what made the quick actions look chewed.
        [<Literal>]
        let borderInset = 0.5

    /// The six quick actions, in the redesign's order.
    ///
    /// Carried as data rather than built inline in the view, so the set, the
    /// order and the labels are assertable and the view's only job is to map an
    /// id onto a dispatch.
    type QuickActionId =
        | Capture
        | AskAssistant
        | FocusSession
        | UploadDocs
        | GeneratePlanner
        | TodaysNotes

    type QuickAction = { Id: QuickActionId; Icon: string; Label: string }

    let quickActions: QuickAction list =
        [ { Id = Capture; Icon = "＋"; Label = "Capture" }
          { Id = AskAssistant; Icon = "✦"; Label = "Ask Assistant" }
          { Id = FocusSession; Icon = "◔"; Label = "Focus Session" }
          { Id = UploadDocs; Icon = "⇪"; Label = "Upload Docs" }
          { Id = GeneratePlanner; Icon = "⟳"; Label = "Generate Planner" }
          { Id = TodaysNotes; Icon = "▤"; Label = "Today's Notes" } ]

    /// Quick actions per row at a given column count: one row of six when wide,
    /// three over two rows at medium, two when narrow. They reflow rather than
    /// shrinking into chips.
    let actionsPerRow (columns: int) =
        if columns >= 3 then 6
        elif columns = 2 then 3
        else 2

    /// Splits cards into grid rows. One card per cell, so a Grid row sizes to
    /// its tallest child and stretches the rest — which is what makes cards
    /// sharing a row equal height.
    let cardRows (columns: int) (cards: 'a list) : 'a list list =
        cards |> List.chunkBySize (max 1 columns)

    /// How many columns the card grid uses at a given content width.
    ///
    /// Breakpoints rather than proportional shrinking: the redesign's cards
    /// have a natural minimum at which their content stops making sense, and
    /// three narrow columns read far worse than one wide one.
    let columnsFor (contentWidth: float) =
        if contentWidth < 720.0 then 1
        elif contentWidth < 1180.0 then 2
        else 3

    /// The header's supporting line: real, and specific to today.
    let contextLine (data: DashboardData) =
        match nextDeadline data with
        | Some(assignment, deadline) ->
            sprintf
                "%s is %s. %s free to work with today."
                (Format.truncate 40 assignment.Title)
                (Format.deadline data.Now deadline)
                (Format.duration data.FreeMinutesToday)
        | None when data.FreeMinutesToday > 0 ->
            sprintf "Nothing due in the next two weeks. %s free today." (Format.duration data.FreeMinutesToday)
        | None -> "Nothing due in the next two weeks."
