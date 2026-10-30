namespace UniversityOS.App.Features

open System
open UniversityOS.Domain
open UniversityOS.App

/// Everything the Timeline decides, separated from how it is drawn.
///
/// The redesign's Timeline is grouping, filtering and counting over one list of
/// activity entries. Doing that in the view would make it untestable and would
/// recompute it on every repaint — including repaints caused by a background
/// frame tick. Pure functions of the loaded entries instead.
[<RequireQualifiedAccess>]
module TimelineLogic =

    // ---- categories ---------------------------------------------------------

    /// The redesign's filter chips.
    ///
    /// These are BROADER than `ActivityKind`: the mockup filters by the kind of
    /// thing that happened ("Assignments") where the log records the specific
    /// event ("Assignment created", "Deadline changed"). One category therefore
    /// covers several kinds.
    ///
    /// The mockup also offers Emails and Grades. Neither has an activity kind
    /// behind it — email arrives as a generic capture and grades are not
    /// modelled at all — so they are omitted rather than shown as chips that
    /// filter to nothing. A dead filter is worse than an absent one.
    type Category =
        | All
        | Ai
        | Planner
        | Assignments
        | Modules
        | Lectures
        | Notes
        | Documents
        | Calendar
        | StudySessions
        | Integrations
        | Flashcards

    [<RequireQualifiedAccess>]
    module Category =

        let all =
            [ All
              Ai
              Planner
              Assignments
              Modules
              Lectures
              Notes
              Documents
              Calendar
              StudySessions
              Integrations
              Flashcards ]

        let label =
            function
            | All -> "All"
            | Ai -> "AI"
            | Planner -> "Planner"
            | Assignments -> "Assignments"
            | Modules -> "Modules"
            | Lectures -> "Lectures"
            | Notes -> "Notes"
            | Documents -> "Documents"
            | Calendar -> "Calendar"
            | StudySessions -> "Study Sessions"
            | Integrations -> "Integrations"
            | Flashcards -> "Flashcards"

        /// Which activity kinds a category covers. Every kind belongs to exactly
        /// one category, so no entry can be invisible under every filter — a
        /// property the tests pin down.
        let kinds =
            function
            | All -> ActivityKind.all
            | Ai -> [ ActivityKind.AiAction; ActivityKind.CaptureApplied ]
            | Planner -> [ ActivityKind.PlanGenerated ]
            | Assignments ->
                [ ActivityKind.AssignmentCreated
                  ActivityKind.AssignmentUpdated
                  ActivityKind.DeadlineChanged ]
            | Modules -> [ ActivityKind.ModuleUpdated ]
            | Lectures -> [ ActivityKind.LectureAdded ]
            | Notes -> [ ActivityKind.NoteCreated; ActivityKind.NoteUpdated ]
            | Documents -> [ ActivityKind.DocumentImported ]
            | Calendar -> [ ActivityKind.EventAdded ]
            | StudySessions -> [ ActivityKind.SessionCompleted ]
            | Integrations -> [ ActivityKind.ConnectorSynced ]
            | Flashcards -> [ ActivityKind.FlashcardsGenerated ]

        let contains (category: Category) (kind: ActivityKind) =
            match category with
            | All -> true
            | c -> kinds c |> List.contains kind

    // ---- view modes ---------------------------------------------------------

    /// The redesign's three presentations of the same data.
    type ViewMode =
        /// Grouped cards, newest first.
        | Feed
        /// A single vertical stepper across the whole period.
        | Journey
        /// One entity's history, when an entry has been picked.
        | Entity

    [<RequireQualifiedAccess>]
    module ViewMode =
        let all = [ Feed; Journey; Entity ]

        let label =
            function
            | Feed -> "Feed"
            | Journey -> "Academic Journey"
            | Entity -> "Entity History"

    // ---- relative grouping --------------------------------------------------

    /// The redesign's period headings, in display order.
    type Period =
        | Today
        | Yesterday
        | EarlierThisWeek
        | LastWeek
        | LastMonth
        | Earlier

    [<RequireQualifiedAccess>]
    module Period =
        /// Newest first, matching the feed's order.
        let order = [ Today; Yesterday; EarlierThisWeek; LastWeek; LastMonth; Earlier ]

        let label =
            function
            | Today -> "Today"
            | Yesterday -> "Yesterday"
            | EarlierThisWeek -> "Earlier This Week"
            | LastWeek -> "Last Week"
            | LastMonth -> "Last Month"
            | Earlier -> "Earlier"

        let rank (p: Period) =
            order |> List.findIndex (fun x -> x = p)

    /// Which period an entry falls in, relative to now.
    ///
    /// Deliberately calendar-relative rather than "n days ago": something at
    /// 23:50 yesterday reads as Yesterday, not as "today, 1 hour ago", which is
    /// what a pure elapsed-time bucket would produce at midnight.
    let periodOf (now: DateTime) (occurredAt: DateTime) =
        let days = (now.Date - occurredAt.Date).Days

        if days <= 0 then Today
        elif days = 1 then Yesterday
        elif days < 7 then EarlierThisWeek
        elif days < 14 then LastWeek
        elif days < 60 then LastMonth
        else Earlier

    // ---- filtering ----------------------------------------------------------

    /// Free-text match over an entry's visible text. The redesign shows a
    /// natural-language prompt; this is a plain substring filter over the title
    /// and detail, which is what the loaded data can honestly support — there is
    /// no semantic index over the activity log.
    let matchesQuery (query: string) (entry: ActivityEntry) =
        if String.IsNullOrWhiteSpace query then
            true
        else
            let q = query.Trim()

            let hit (s: string) =
                not (isNull s) && s.Contains(q, StringComparison.OrdinalIgnoreCase)

            hit entry.Title
            || (entry.Detail |> Option.map hit |> Option.defaultValue false)
            || hit (ActivityKind.label entry.Kind)

    let visibleEntries (category: Category) (query: string) (entries: ActivityEntry list) =
        entries
        |> List.filter (fun e -> Category.contains category e.Kind)
        |> List.filter (matchesQuery query)

    /// Entries grouped into the redesign's periods, newest period first and
    /// newest entry first within each. Empty periods are dropped rather than
    /// rendered as bare headings.
    let grouped (now: DateTime) (entries: ActivityEntry list) : (Period * ActivityEntry list) list =
        entries
        |> List.groupBy (fun e -> periodOf now e.OccurredAt)
        |> List.map (fun (period, items) -> period, items |> List.sortByDescending (fun e -> e.OccurredAt))
        |> List.sortBy (fst >> Period.rank)

    /// Every entry for the same entity as the given one, newest first. This is
    /// the Entity History view: real data, filtered to one thing's story.
    let entityHistory (entity: EntityRef) (entries: ActivityEntry list) =
        entries
        |> List.filter (fun e ->
            match e.Entity with
            | Some ref -> ref.Kind = entity.Kind && ref.Id = entity.Id
            | None -> false)
        |> List.sortByDescending (fun e -> e.OccurredAt)

    // ---- derived counts -----------------------------------------------------

    let private countOf (kinds: ActivityKind list) (entries: ActivityEntry list) =
        entries |> List.filter (fun e -> List.contains e.Kind kinds) |> List.length

    /// The redesign's six stat tiles. Every one is a real count over the loaded
    /// entries — nothing here is authored.
    /// The six summary tiles.
    ///
    /// `studiedMinutes` is passed in rather than derived from `entries`: study
    /// time is a property of the SESSIONS, not of the activity log. Counting
    /// `SessionCompleted` entries would over-report, because ticking a session,
    /// unticking it and ticking it again writes three entries for one session,
    /// and would under-report any session completed before that entry was
    /// logged. `Domain.StudyTime` owns the real sum.
    let stats (now: DateTime) (studiedMinutes: int) (entries: ActivityEntry list) : (string * string) list =
        let thisWeek =
            entries |> List.filter (fun e -> (now.Date - e.OccurredAt.Date).Days < 7)

        [ string (countOf [ ActivityKind.SessionCompleted ] thisWeek), "Study sessions this week"
          string (countOf [ ActivityKind.LectureAdded ] entries), "Lectures processed"
          string (countOf [ ActivityKind.FlashcardsGenerated ] entries), "Flashcard batches"
          string (countOf [ ActivityKind.PlanGenerated ] entries), "Planner regenerations"
          string entries.Length, "Timeline events"
          StudyTime.format studiedMinutes, "Studied this week" ]

    /// Observations over the loaded entries.
    ///
    /// Rule-based, and named "Patterns" in the view rather than "AI insights":
    /// nothing generates these, and calling them AI would misrepresent how the
    /// application works.
    let patterns (now: DateTime) (entries: ActivityEntry list) : string list =
        if entries.IsEmpty then
            []
        else
            let busiest =
                entries
                |> List.countBy (fun e -> e.OccurredAt.Hour)
                |> List.sortByDescending snd
                |> List.tryHead

            let commonest =
                entries |> List.countBy (fun e -> e.Kind) |> List.sortByDescending snd |> List.tryHead

            [ match busiest with
              | Some(hour, count) when count > 1 ->
                  sprintf "Most of your activity lands around %02d:00 — %d entries." hour count
              | _ -> ()

              match commonest with
              | Some(kind, count) when count > 1 ->
                  sprintf "%s is your most frequent event, %d times." (ActivityKind.label kind) count
              | _ -> ()

              let week = entries |> List.filter (fun e -> (now.Date - e.OccurredAt.Date).Days < 7)

              if not week.IsEmpty then
                  sprintf "%d of %d events happened in the last seven days." week.Length entries.Length

              match entries |> List.tryHead with
              | Some latest -> sprintf "Last recorded: %s." (Format.relativeTime now latest.OccurredAt)
              | None -> () ]
            |> List.truncate 4

    // ---- layout -------------------------------------------------------------

    /// Whether the side column sits beside the feed or beneath it.
    let showsSideColumn (contentWidth: float) = contentWidth >= 1100.0

    /// Stat tiles per row, so they reflow rather than shrinking.
    let statsPerRow (contentWidth: float) =
        if contentWidth < 700.0 then 2
        elif contentWidth < 1100.0 then 3
        else 6

    // ---- Academic Journey geometry ------------------------------------------

    /// The stepper's layout rules.
    ///
    /// **Two attempts, and why the second is different in kind.**
    ///
    /// The first stepper stacked node-then-line in a `StackPanel` with a
    /// `minHeight` of 34 — a guess at how tall a row would be — and gave the
    /// content an 18px bottom margin the marker column knew nothing about. Gaps
    /// everywhere, a line trailing below the last node, and the node sitting
    /// above its title.
    ///
    /// The second replaced the guess with a *computed* offset:
    /// `(titleLineHeight − nodeSize) / 2`, where `titleLineHeight` was
    /// `fontSize × 1.35`. That is still a guess, just a better-dressed one — 1.35
    /// is an assumption about Avalonia's text metrics, and when the real line
    /// box differs the node lands visibly high. Tests that asserted the formula
    /// agreed with itself could never catch that.
    ///
    /// **This version has no offset at all.** The node and the title live in the
    /// SAME Grid row, both `VerticalAlignment.Center`. Avalonia measures the row
    /// from the title and centres both in it, so the node's centre is the
    /// title's centre by construction — whatever the font metrics turn out to
    /// be, at any density, in any theme. There is no number here that can be
    /// wrong, which is why the constants below are only sizes and widths.
    [<RequireQualifiedAccess>]
    module JourneyLayout =

        /// Diameter of the node. Width and height are both this and the corner
        /// radius is exactly half, so it is a true circle rather than a rounded
        /// square — the previous 11px box at radius 6 was not.
        [<Literal>]
        let nodeSize = 11.0

        /// Corner radius that makes `nodeSize` a circle. Derived so the two can
        /// never drift apart.
        let nodeRadius = nodeSize / 2.0

        [<Literal>]
        let connectorWidth = 2.0

        /// The fixed marker column. The node and both connector segments are
        /// centred in it, so they share one horizontal centre by construction —
        /// there is no margin arithmetic and nothing to offset.
        [<Literal>]
        let markerWidth = 22.0

        /// Gap between the marker column and the content column.
        [<Literal>]
        let contentGap = 12.0

        /// Vertical gap between entries, before density scaling. Carried by the
        /// content column's bottom margin and bridged by the connector, which
        /// occupies the marker column for the row's full height.
        [<Literal>]
        let rowGap = 18.0

        /// A connector enters a node from above unless it is the first.
        let hasConnectorAbove (index: int) = index > 0

        /// A connector leaves a node downward unless it is the last. The same
        /// answer governs both the segment inside the title row (below the node)
        /// and the segment spanning the metadata row and the inter-item gap, so
        /// the two cannot disagree and leave a break.
        let hasConnectorBelow (index: int) (count: int) = index < count - 1

        /// Where a connector segment sits in a row, as a pure description of the
        /// composition. Used by the tests to state the join condition without a
        /// running layout pass.
        type Segment =
            /// Inside the title row, from the row's top edge to the node.
            | AboveNode
            /// Inside the title row, from the node to the row's bottom edge.
            | BelowNode
            /// Spanning the metadata row and the inter-item gap.
            | ThroughMetadata

        /// The segments an entry actually draws.
        let segmentsFor (index: int) (count: int) : Segment list =
            [ if hasConnectorAbove index then AboveNode
              if hasConnectorBelow index count then
                  BelowNode
                  ThroughMetadata ]

    // ---- feed selection -----------------------------------------------------

    /// Whether a view mode supports selecting an entry.
    ///
    /// Feed only. The Journey and Entity views are readings of the same data
    /// with their own purpose — a stepper is a shape, not a list of targets.
    let allowsSelection (mode: ViewMode) = mode = Feed

    /// The right-hand column's cards, top to bottom.
    ///
    /// Carried as data so the order is one definition the view maps over rather
    /// than a hand-written list that can silently drift — and so "these three,
    /// in this order" is a statement a test can make. The slot the redesign
    /// gives to "Undo & restore" holds Recent Focus: the app has no undo
    /// infrastructure, and a card of real study figures is worth more than four
    /// buttons that cannot work.
    type SideCard =
        | Patterns
        | Breakdown
        | RecentFocus

    [<RequireQualifiedAccess>]
    module SideCard =
        let order = [ Patterns; Breakdown; RecentFocus ]

        let title =
            function
            | Patterns -> "Patterns"
            | Breakdown -> "Breakdown"
            | RecentFocus -> "Recent Focus"

    /// What clicking an entry does: selection toggles.
    ///
    /// Clicking an unselected entry selects it; clicking the selected one again
    /// clears it. Stated here rather than in the update so the rule is one
    /// expression that can be read and asserted whole.
    let toggledSelection (current: Guid option) (clicked: Guid) =
        if current = Some clicked then None else Some clicked

    /// The selection that survives a change of mode, filter, search or data.
    ///
    /// One rule, applied everywhere, rather than remembering to clear the
    /// selection at each of the six call sites that can invalidate it: a
    /// selection is kept only if the entry is still on screen in a mode that
    /// allows selecting. Everything else — switching to Journey, filtering the
    /// entry away, searching past it, reloading after it was removed — falls out
    /// of that one condition.
    let retainedSelection (mode: ViewMode) (visible: ActivityEntry list) (selected: Guid option) =
        match selected with
        | Some id when allowsSelection mode && visible |> List.exists (fun e -> e.Id = id) -> Some id
        | _ -> None

    /// The selected entry itself, if it is still visible.
    let selectedEntry (mode: ViewMode) (visible: ActivityEntry list) (selected: Guid option) =
        match retainedSelection mode visible selected with
        | Some id -> visible |> List.tryFind (fun e -> e.Id = id)
        | None -> None
