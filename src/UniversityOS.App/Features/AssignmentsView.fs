module UniversityOS.App.Features.AssignmentsView

open System
open Elmish
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open UniversityOS.Domain
// For `PlannerPreferences.ofSettings` — the scheduling horizon this screen
// measures "scheduled cover" over is the planner's own, not a second one.
open UniversityOS.Domain.Planning
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Controls

/// Assignments: what coursework there is, how it is going, what is at risk, and
/// what to do next — over the same full CRUD editor the screen has always had.
///
/// Every decision this screen makes lives in `AssignmentsLogic`; this file only
/// draws what that module resolves. See its header for the four elements of the
/// redesign that have no data behind them.

// ---------------------------------------------------------------------------
// the editor draft — unchanged behaviour, preserved in full
// ---------------------------------------------------------------------------

type Draft =
    { Original: Assignment option // None = creating new
      Title: string
      Description: string
      ModuleIndex: int // 0 = no module; else Modules.[i-1]
      DeadlineText: string
      WeightingText: string
      PriorityIndex: int
      StatusIndex: int
      EstimatedText: string
      Progress: int
      Checklist: ChecklistItem list
      NewChecklistText: string
      SubmissionUrl: string
      RepoUrl: string
      FormError: string option }

type Model =
    { /// One snapshot per assignment, gathered in a single load so no two panels
      /// can describe different moments.
      Facts: AssignmentsLogic.Facts list
      Modules: StudyModule list
      Filter: AssignmentsLogic.Filter
      /// The one expanded card. Only ever one — expanding another replaces it,
      /// and expanding the open one collapses it.
      Expanded: AssignmentId option
      /// True once the user has opened or closed a card themselves. Until then
      /// the screen may open its first visible assignment; afterwards it never
      /// overrides what they chose. See `AssignmentsLogic.expansionFor`.
      ExpansionChosen: bool
      Draft: Draft option
      /// A delete waiting for explicit confirmation.
      PendingDelete: bool
      /// Whether an AI provider is configured, so "Ask Assistant" is honest.
      AiAvailable: bool
      /// The application's shared week boundary, from `StudyTime.weekStart`.
      WeekStart: DateTime
      Saving: bool
      Loading: bool
      Error: string option
      /// Content width, used only for layout decisions.
      Width: float }

/// Raised to the shell, which owns navigation and cross-screen focus.
type External =
    | Goto of Page
    /// Navigate to the Timeline and focus this entity's history.
    | ShowInTimeline of EntityRef * string
    /// Pre-fill the assistant with a real question and navigate to it.
    | AskAssistantAbout of string

type Msg =
    | Load
    | Loaded of AssignmentsLogic.Facts list * StudyModule list * bool * DateTime
    | Failed of exn
    | SetFilter of AssignmentsLogic.Filter
    | ToggleExpanded of AssignmentId
    | Act of AssignmentsLogic.ActionId * AssignmentsLogic.Facts
    /// The panel's own "Open full Timeline" row. Plain navigation to a screen —
    /// unlike a card's "View Timeline", which focuses one assignment's history.
    | OpenTimeline
    | Resized of float
    // ---- editor ----
    | NewAssignment
    | CloseEditor
    | SetTitle of string
    | SetDescription of string
    | SetModuleIndex of int
    | SetDeadline of string
    | SetWeighting of string
    | SetPriorityIndex of int
    | SetStatusIndex of int
    | SetEstimated of string
    | SetSubmission of string
    | SetRepo of string
    | ProgressDelta of int
    | SetNewChecklistText of string
    | AddChecklistItem
    | ToggleChecklistItem of ChecklistItemId
    | RemoveChecklistItem of ChecklistItemId
    | Save
    | Saved
    | RequestDelete
    | CancelDelete
    | ConfirmDelete
    | Deleted

// ---------------------------------------------------------------------------
// loading
// ---------------------------------------------------------------------------

/// One command, one snapshot.
///
/// Six reads, batched, because every panel on this page is derived from the same
/// moment: the assignments and their modules, the planner's own sessions, the
/// scheduler's verdict on what it could not place, the link graph, and the
/// activity log. Issuing them separately would let the risk badge on a card
/// describe a different instant than the Risk Overview beside it.
///
/// `PreviewWith id` runs the **real** scheduler against the user's own saved
/// preferences and persists nothing — the same call the Planner's "preview
/// alternatives" uses. Its `Unplanned` list is the only genuine planner-conflict
/// signal in the application.
let private loadCmd (services: AppServices) =
    Elm.ofAsync
        (async {
            let now = services.Clock.Now
            let! assignments = services.Assignments.GetAll()
            let! modules = services.Modules.GetAll()
            let! settings = services.Settings.Load()

            // Sessions from now to the end of the planner's own horizon, so
            // "scheduled cover" is measured over the window the planner plans.
            let horizonDays = float (PlannerPreferences.ofSettings settings).HorizonDays
            let! sessions = services.Sessions.GetBetween(now, now.AddDays(horizonDays + 1.0))

            let! preview = services.Planner.PreviewWith id
            let! entries = services.Activity.GetRecent 300
            let! links = services.Links.GetAll()
            let! aiAvailable = services.Ai.IsAvailable()

            let moduleOf (a: Assignment) =
                a.ModuleId |> Option.bind (fun mid -> modules |> List.tryFind (fun m -> m.Id = mid))

            // Future, not-yet-completed sessions carrying this assignment's id.
            let sessionsFor (a: Assignment) =
                sessions
                |> List.filter (fun s -> s.AssignmentId = Some a.Id && not s.Completed)

            let unplacedReason (a: Assignment) =
                preview.Unplanned
                |> List.tryFind (fun u -> u.Task.AssignmentId = a.Id)
                |> Option.map (fun u -> u.Reason)

            let linkedFor (a: Assignment) =
                let self = { Kind = EntityKind.Assignment; Id = a.Id.Value }

                links
                |> List.choose (Link.otherEnd self)
                |> List.countBy (fun e -> e.Kind)
                |> List.sortBy (fun (k, _) -> string k)

            let activityFor (a: Assignment) =
                entries
                |> List.filter (fun e -> e.Entity = Some { Kind = EntityKind.Assignment; Id = a.Id.Value })
                |> List.length

            let facts =
                assignments
                |> List.map (fun a ->
                    let scheduled = sessionsFor a

                    { AssignmentsLogic.Assignment = a
                      AssignmentsLogic.Module = moduleOf a
                      AssignmentsLogic.ScheduledMinutes =
                        scheduled |> List.sumBy (fun s -> max 0 (int (s.EndsAt - s.StartsAt).TotalMinutes))
                      AssignmentsLogic.SessionCount = scheduled.Length
                      AssignmentsLogic.UnplacedReason = unplacedReason a
                      AssignmentsLogic.Linked = linkedFor a
                      AssignmentsLogic.ActivityCount = activityFor a })

            let weekStart =
                StudyTime.weekStart Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek now

            return facts, modules, aiAvailable, weekStart
        })
        Loaded
        Failed

let init (services: AppServices) : Model * Cmd<Msg> =
    { Facts = []
      Modules = []
      Filter = AssignmentsLogic.Filter.All
      Expanded = None
      ExpansionChosen = false
      Draft = None
      PendingDelete = false
      AiAvailable = false
      WeekStart = DateTime.Today
      Saving = false
      Loading = true
      Error = None
      Width = 1400.0 },
    loadCmd services

// ---------------------------------------------------------------------------
// editor plumbing — behaviour preserved exactly
// ---------------------------------------------------------------------------

let private draftOf (modules: StudyModule list) (a: Assignment) : Draft =
    { Original = Some a
      Title = a.Title
      Description = a.Description
      ModuleIndex =
        match a.ModuleId with
        | Some mid ->
            match modules |> List.tryFindIndex (fun m -> m.Id = mid) with
            | Some i -> i + 1
            | None -> 0
        | None -> 0
      DeadlineText = a.Deadline |> Option.map Format.dateTimeInput |> Option.defaultValue ""
      WeightingText = a.Weighting |> Option.map string |> Option.defaultValue ""
      PriorityIndex = Priority.rank a.Priority
      StatusIndex = AssignmentStatus.toInt a.Status
      EstimatedText = a.EstimatedHours |> Option.map string |> Option.defaultValue ""
      Progress = a.Progress
      Checklist = a.Checklist
      NewChecklistText = ""
      SubmissionUrl = a.SubmissionUrl |> Option.defaultValue ""
      RepoUrl = a.RepositoryUrl |> Option.defaultValue ""
      FormError = None }

let private emptyDraft: Draft =
    { Original = None
      Title = ""
      Description = ""
      ModuleIndex = 0
      DeadlineText = ""
      WeightingText = ""
      PriorityIndex = Priority.rank Priority.Medium
      StatusIndex = 0
      EstimatedText = ""
      Progress = 0
      Checklist = []
      NewChecklistText = ""
      SubmissionUrl = ""
      RepoUrl = ""
      FormError = None }

/// Turns the draft back into a domain record, or a validation error.
let private buildAssignment (now: DateTime) (modules: StudyModule list) (draft: Draft) : Result<Assignment, string> =
    if String.IsNullOrWhiteSpace draft.Title then
        Error "A title is required."
    else
        let deadline =
            if String.IsNullOrWhiteSpace draft.DeadlineText then Ok None
            else
                match Format.parseDateTimeInput draft.DeadlineText with
                | Some d -> Ok(Some d)
                | None -> Error "Deadline not understood — use e.g. 2026-08-15 17:00."

        match deadline with
        | Error e -> Error e
        | Ok deadline ->
            let baseRecord =
                match draft.Original with
                | Some original -> original
                | None -> Assignment.create now draft.Title

            Ok
                { baseRecord with
                    Title = draft.Title.Trim()
                    Description = draft.Description
                    ModuleId =
                        if draft.ModuleIndex > 0 && draft.ModuleIndex <= modules.Length then
                            Some modules.[draft.ModuleIndex - 1].Id
                        else
                            None
                    Deadline = deadline
                    Weighting = Format.parseFloatOpt draft.WeightingText
                    Priority = Priority.ofRank draft.PriorityIndex
                    Status = AssignmentStatus.ofInt draft.StatusIndex
                    EstimatedHours = Format.parseFloatOpt draft.EstimatedText
                    Progress = draft.Progress
                    Checklist = draft.Checklist
                    SubmissionUrl =
                        if String.IsNullOrWhiteSpace draft.SubmissionUrl then None else Some(draft.SubmissionUrl.Trim())
                    RepositoryUrl =
                        if String.IsNullOrWhiteSpace draft.RepoUrl then None else Some(draft.RepoUrl.Trim())
                    UpdatedAt = now }

// ---------------------------------------------------------------------------
// update
// ---------------------------------------------------------------------------

let update (services: AppServices) (msg: Msg) (model: Model) : Model * Cmd<Msg> * External option =
    let withDraft f =
        match model.Draft with
        | Some d -> { model with Draft = Some(f d) }
        | None -> model

    let plain m = m, Cmd.none, None

    match msg with
    | Load -> { model with Loading = true }, loadCmd services, None

    | Loaded(facts, modules, aiAvailable, weekStart) ->
        // An expansion whose assignment has gone is dropped rather than left
        // dangling, so no card can be open on something that no longer exists.
        let expanded =
            model.Expanded
            |> Option.filter (fun id -> facts |> List.exists (fun f -> f.Assignment.Id = id))

        plain
            { model with
                Facts = facts
                Modules = modules
                AiAvailable = aiAvailable
                WeekStart = weekStart
                Expanded = expanded
                Loading = false
                Error = None }

    | Failed ex ->
        // Every in-flight flag is cleared: a failure must not leave the editor
        // stuck saying "Saving…" or a delete permanently pending.
        plain
            { model with
                Loading = false
                Saving = false
                PendingDelete = false
                Error = Some ex.Message }

    | SetFilter filter -> plain { model with Filter = filter }

    // One expanded card at a time; re-selecting the open one closes it.
    | ToggleExpanded id ->
        plain
            { model with
                Expanded = (if model.Expanded = Some id then None else Some id)
                ExpansionChosen = true }

    | Resized width -> plain { model with Width = width }

    // The Timeline is a screen, and a screen can always be opened. It does not
    // depend on any assignment having history — an empty Timeline is a real and
    // reachable state, and refusing to navigate to it was the wrong call.
    | OpenTimeline -> model, Cmd.none, Some(Goto Page.Timeline)

    | Act(id, facts) ->
        let entity = { Kind = EntityKind.Assignment; Id = facts.Assignment.Id.Value }

        match id with
        | AssignmentsLogic.ActionId.Open ->
            { model with Draft = Some(draftOf model.Modules facts.Assignment); PendingDelete = false },
            Cmd.none,
            None
        | AssignmentsLogic.ActionId.OpenPlanner -> model, Cmd.none, Some(Goto Page.Planner)
        | AssignmentsLogic.ActionId.ViewTimeline ->
            model, Cmd.none, Some(ShowInTimeline(entity, facts.Assignment.Title))
        | AssignmentsLogic.ActionId.AskAssistant ->
            model, Cmd.none, Some(AskAssistantAbout(AssignmentsLogic.assistantPrompt facts))

    // ---- editor ----
    | NewAssignment -> plain { model with Draft = Some emptyDraft; PendingDelete = false }
    | CloseEditor -> plain { model with Draft = None; PendingDelete = false }

    | SetTitle v -> plain (withDraft (fun d -> { d with Title = v }))
    | SetDescription v -> plain (withDraft (fun d -> { d with Description = v }))
    | SetModuleIndex i -> plain (withDraft (fun d -> { d with ModuleIndex = i }))
    | SetDeadline v -> plain (withDraft (fun d -> { d with DeadlineText = v }))
    | SetWeighting v -> plain (withDraft (fun d -> { d with WeightingText = v }))
    | SetPriorityIndex i -> plain (withDraft (fun d -> { d with PriorityIndex = i }))
    | SetStatusIndex i -> plain (withDraft (fun d -> { d with StatusIndex = i }))
    | SetEstimated v -> plain (withDraft (fun d -> { d with EstimatedText = v }))
    | SetSubmission v -> plain (withDraft (fun d -> { d with SubmissionUrl = v }))
    | SetRepo v -> plain (withDraft (fun d -> { d with RepoUrl = v }))
    | ProgressDelta delta ->
        plain (withDraft (fun d -> { d with Progress = max 0 (min 100 (d.Progress + delta)) }))

    | SetNewChecklistText v -> plain (withDraft (fun d -> { d with NewChecklistText = v }))
    | AddChecklistItem ->
        plain (
            withDraft (fun d ->
                if String.IsNullOrWhiteSpace d.NewChecklistText then
                    d
                else
                    { d with
                        Checklist =
                            d.Checklist
                            @ [ { Id = ChecklistItemId.New()
                                  Text = d.NewChecklistText.Trim()
                                  Done = false
                                  Position = d.Checklist.Length } ]
                        NewChecklistText = "" })
        )
    | ToggleChecklistItem id ->
        plain (
            withDraft (fun d ->
                { d with
                    Checklist = d.Checklist |> List.map (fun c -> if c.Id = id then { c with Done = not c.Done } else c) })
        )
    | RemoveChecklistItem id ->
        plain (withDraft (fun d -> { d with Checklist = d.Checklist |> List.filter (fun c -> c.Id <> id) }))

    // The re-entry guard: a second Save while one is running is dropped rather
    // than queued, so a double click cannot write twice.
    | Save when model.Saving -> plain model
    | Save ->
        match model.Draft with
        | None -> plain model
        | Some draft ->
            match buildAssignment services.Clock.Now model.Modules draft with
            | Error message -> plain { model with Draft = Some { draft with FormError = Some message } }
            | Ok assignment ->
                let isNew = draft.Original.IsNone

                let deadlineChanged =
                    match draft.Original with
                    | Some original -> original.Deadline <> assignment.Deadline
                    | None -> false

                { model with
                    Saving = true
                    Draft = Some { draft with Original = Some assignment; FormError = None } },
                Elm.ofAsync
                    (async {
                        do! services.Assignments.Save assignment
                        let now = services.Clock.Now
                        let entity = Some { Kind = EntityKind.Assignment; Id = assignment.Id.Value }

                        let kind =
                            if isNew then ActivityKind.AssignmentCreated else ActivityKind.AssignmentUpdated

                        do! services.Activity.Log(
                                ActivityEntry.create now kind (sprintf "\"%s\"" assignment.Title) None entity
                            )

                        if deadlineChanged then
                            do! services.Activity.Log(
                                    ActivityEntry.create
                                        now
                                        ActivityKind.DeadlineChanged
                                        (sprintf
                                            "Deadline of \"%s\" is now %s"
                                            assignment.Title
                                            (assignment.Deadline
                                             |> Option.map (fun d -> d.ToString "ddd d MMM HH:mm")
                                             |> Option.defaultValue "removed"))
                                        None
                                        entity
                                )
                    })
                    (fun _ -> Saved)
                    Failed,
                None

    | Saved -> { model with Saving = false }, loadCmd services, None

    // Deleting is destructive and irreversible — no repository here has a soft
    // delete — so it takes an explicit confirmation step.
    | RequestDelete -> plain { model with PendingDelete = true }
    | CancelDelete -> plain { model with PendingDelete = false }
    | ConfirmDelete when model.Saving -> plain model
    | ConfirmDelete ->
        match model.Draft |> Option.bind (fun d -> d.Original) with
        | Some original ->
            { model with Saving = true; PendingDelete = false },
            Elm.ofAsync (async { return! services.Assignments.Delete original.Id }) (fun _ -> Deleted) Failed,
            None
        | None -> plain { model with Draft = None; PendingDelete = false }

    | Deleted -> { model with Saving = false; Draft = None }, loadCmd services, None

// ---------------------------------------------------------------------------
// view — shared treatments
// ---------------------------------------------------------------------------

/// No specular pass on any Assignments content card. Named once so no card can
/// opt out by accident and a test can state the claim for the whole screen.
let private contentHighlightScale = AssignmentsLogic.Geometry.contentHighlight

/// **The one card treatment on this screen.** Compact rows, expanded rows, metric
/// cards and the forecast panels all draw through this, so there is a single
/// surface material, a single highlight behaviour and a single border.
///
/// `align` is the only thing that varies beyond padding and content: a summary
/// metric card centres its block in the row's shared height, while a panel of
/// stacked sections starts at the top. Both are the same surface.
let private cardWith
    (tokens: Tokens)
    (padding: float)
    (tint: Color)
    (align: VerticalAlignment)
    (children: IView list)
    : IView =
    GlassPanel.create
        [ GlassPanel.baseRadius DashboardLogic.Card.radius
          GlassPanel.elevation 1
          GlassPanel.highlightScale contentHighlightScale
          GlassPanel.tint tint
          GlassPanel.child (
              Border.create
                  [ Border.padding (Tokens.space tokens padding)
                    Border.verticalAlignment VerticalAlignment.Stretch
                    Border.child (
                        StackPanel.create
                            [ StackPanel.spacing (Tokens.space tokens 10.0)
                              StackPanel.verticalAlignment align
                              // Content FILLS the card's width. Only the
                              // VERTICAL position of the block varies.
                              //
                              // ⚠️ Do not set `HorizontalAlignment.Left` here to
                              // get left-aligned text. A StackPanel that is not
                              // stretched sizes to its widest child, so every
                              // row inside it — the divider, the right-aligned
                              // forecast value, the Timeline row's arrow — is
                              // measured against that child rather than against
                              // the card. Text is left-aligned already, because
                              // `TextBlock.TextAlignment` defaults to Left; the
                              // container has nothing to do with it.
                              StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                              StackPanel.children children ]
                    ) ] ) ]
    :> IView

let private card (tokens: Tokens) (padding: float) (tint: Color) (children: IView list) =
    cardWith tokens padding tint VerticalAlignment.Top children

let private plainCard (tokens: Tokens) (padding: float) (children: IView list) =
    card tokens padding (Theme.surfaces ()).Card children

// ---- named text roles ------------------------------------------------------
//
// One place each role becomes a control, so the hierarchy cannot drift into a
// uniform weight. The sizes and weights come from `AssignmentsLogic.Typography`,
// where a test pins each role as genuinely distinct from the one it outranks.

let private weight (w: int) = enum<FontWeight> w

/// A panel's own name — COURSEWORK FORECAST, RISK OVERVIEW.
let private cardTitle (text: string) : IView =
    TextBlock.create
        [ TextBlock.text (text.ToUpperInvariant())
          TextBlock.fontSize AssignmentsLogic.Typography.cardTitleSize
          TextBlock.fontWeight (weight AssignmentsLogic.Typography.cardTitleWeight)
          TextBlock.foreground (Theme.label ()) ]
    :> IView

/// A section heading inside a panel. It introduces the answer; it is not the
/// answer, so it is quieter than both the panel's name and the value below it.
let private sectionLabel (text: string) : IView =
    TextBlock.create
        [ TextBlock.text (text.ToUpperInvariant())
          TextBlock.fontSize AssignmentsLogic.Typography.sectionLabelSize
          TextBlock.fontWeight (weight AssignmentsLogic.Typography.sectionLabelWeight)
          TextBlock.foreground (Theme.faint ()) ]
    :> IView

/// The answer.
let private primaryValue (text: string) : IView =
    TextBlock.create
        [ TextBlock.text text
          TextBlock.fontSize AssignmentsLogic.Typography.primaryValueSize
          TextBlock.fontWeight (weight AssignmentsLogic.Typography.primaryValueWeight)
          TextBlock.foreground (Theme.text ())
          TextBlock.textWrapping TextWrapping.Wrap ]
    :> IView

/// A sentence explaining the answer.
let private supporting (text: string) : IView =
    TextBlock.create
        [ TextBlock.text text
          TextBlock.fontSize AssignmentsLogic.Typography.supportingSize
          TextBlock.fontWeight (weight AssignmentsLogic.Typography.supportingWeight)
          TextBlock.foreground (Theme.muted ())
          TextBlock.textWrapping TextWrapping.Wrap ]
    :> IView

/// A glyph in a fixed column beside text that wraps in whatever width is left.
let private bulletRow (glyph: string) (glyphBrush: IBrush) (text: string) (textBrush: IBrush) : IView =
    Grid.create
        [ Grid.columnDefinitions AssignmentsLogic.Geometry.bulletColumns
          Grid.children
              [ TextBlock.create
                    [ Grid.column 0
                      TextBlock.text glyph
                      TextBlock.fontSize Theme.Type.rowMeta
                      TextBlock.foreground glyphBrush
                      TextBlock.verticalAlignment VerticalAlignment.Top
                      Ui.decorative () ]
                TextBlock.create
                    [ Grid.column 2
                      TextBlock.text text
                      TextBlock.fontSize Theme.Type.rowMeta
                      TextBlock.foreground textBrush
                      TextBlock.textWrapping TextWrapping.Wrap ] ] ]
    :> IView

/// A label / value row inside a forecast panel. A Grid, not a DockPanel, so a
/// long value wraps under itself rather than pushing the label out of the card.
///
/// The label is regular weight; whatever the caller puts on the right is
/// stronger. One uniform weight across the row would flatten the whole panel.
let private forecastRow (label: string) (value: IView) : IView =
    Grid.create
        [ Grid.columnDefinitions AssignmentsLogic.Geometry.forecastColumns
          Grid.margin (0.0, 3.0)
          Grid.children
              [ TextBlock.create
                    [ Grid.column 0
                      TextBlock.text label
                      TextBlock.fontSize Theme.Type.rowMeta
                      TextBlock.fontWeight (weight AssignmentsLogic.Typography.overviewLabelWeight)
                      TextBlock.foreground (Theme.muted ())
                      TextBlock.verticalAlignment VerticalAlignment.Top ]
                Border.create
                    [ Grid.column 2
                      Border.horizontalAlignment HorizontalAlignment.Right
                      Border.child value ] ] ]
    :> IView

/// The opaque surface a card is drawn on. Expanding changes it, and everything
/// measured for readability inside that card has to follow.
let private cardSurface (isExpanded: bool) =
    if isExpanded then (Theme.surfaces ()).Selected else (Theme.surfaces ()).Card

/// A chip's own fill, as the opaque colour it will actually render as — so its
/// ink can be walked against what the eye sees rather than against the card.
let private chipWash (surface: Color) (hex: string) =
    Theme.compositeOver surface (Theme.withAlphaHex "1F" hex)

/// A small labelled wash. Decorative and hit-test-transparent, and its ink is
/// measured against its own fill.
let private chip (tokens: Tokens) (surface: Color) (text: string) (hex: string) : IView =
    Border.create
        [ Border.background (Theme.brush (Theme.withAlphaHex "1F" hex))
          Border.cornerRadius (Tokens.radius tokens 999.0)
          Border.padding (9.0, 3.0)
          // Content-sized, for the same reason as `Ui.pill`: the default
          // Stretch made this wash span its whole cell instead of hugging its
          // label. Containers still control where the chip sits.
          Border.horizontalAlignment HorizontalAlignment.Left
          Border.verticalAlignment VerticalAlignment.Center
          Border.isHitTestVisible false
          Border.child (
              TextBlock.create
                  [ TextBlock.text text
                    TextBlock.fontSize Theme.Type.metricLabel
                    TextBlock.fontWeight FontWeight.SemiBold
                    TextBlock.foreground (Theme.brush (Theme.readableOnColor (chipWash surface hex) hex)) ]
          ) ]
    :> IView

// ---- semantic ink ----------------------------------------------------------

let private riskHex (risk: AssignmentsLogic.Risk) =
    match risk with
    | AssignmentsLogic.Risk.Settled -> Theme.successHex ()
    | AssignmentsLogic.Risk.Low -> Theme.Identity.neutral
    | AssignmentsLogic.Risk.Medium -> Theme.infoHex ()
    | AssignmentsLogic.Risk.High -> Theme.warningHex ()
    | AssignmentsLogic.Risk.Critical -> Theme.dangerHex ()

let private paceHex (pace: AssignmentsLogic.Pace) =
    match pace with
    | AssignmentsLogic.Pace.Submitted -> Theme.successHex ()
    | AssignmentsLogic.Pace.Overdue -> Theme.dangerHex ()
    | AssignmentsLogic.Pace.Stalled -> Theme.warningHex ()
    | AssignmentsLogic.Pace.Behind -> Theme.warningHex ()
    | AssignmentsLogic.Pace.NotStarted -> Theme.Identity.neutral
    | AssignmentsLogic.Pace.Unpaced -> Theme.Identity.neutral
    | AssignmentsLogic.Pace.OnPace -> Theme.successHex ()

let private trajectoryHex (t: AssignmentsLogic.Trajectory) =
    match t with
    | AssignmentsLogic.Trajectory.NothingOutstanding -> Theme.Identity.neutral
    | AssignmentsLogic.Trajectory.OnTrack -> Theme.successHex ()
    | AssignmentsLogic.Trajectory.Watch -> Theme.infoHex ()
    | AssignmentsLogic.Trajectory.AtRisk -> Theme.warningHex ()
    | AssignmentsLogic.Trajectory.Critical -> Theme.dangerHex ()

let private signalHex (s: AssignmentsLogic.Signal) =
    if AssignmentsLogic.Signal.points s >= 3 then Theme.dangerHex ()
    elif AssignmentsLogic.Signal.isWarning s then Theme.warningHex ()
    else Theme.Identity.neutral

// ---- glass actions ---------------------------------------------------------

/// A real Button whose visible surface is a `GlassPanel`, exactly as the
/// Planner's and the Inbox's action rows are built. Geometry is identical in
/// every state; only the tint and the ink move.
let private glassAction
    (tokens: Tokens)
    (label: string)
    (primary: bool)
    (enabled: bool)
    (accessibleName: string)
    (onClick: unit -> unit)
    : IView =
    // Geometry only. The glass, the veil, the accent floor and the ink rule all
    // live in `Ui.glassAction` — see the note there on why eleven copies of
    // this button existed and only one of them kept its accent.
    Ui.glassAction
        tokens
        { Ui.GlassActionMetrics.Padding = (15.0, 8.0)
          Ui.GlassActionMetrics.Margin = (0.0, 0.0, 10.0, 10.0) }
        label
        primary
        enabled
        accessibleName
        onClick

/// The visible explanations under a row of actions. `Muted`, not `Faint`: a
/// required explanation the user cannot comfortably read is not a visible one.
let private unavailableNotes (surface: Color) (entries: (string * string) list) : IView list =
    [ if not entries.IsEmpty then
          StackPanel.create
              [ StackPanel.spacing 2.0
                StackPanel.margin (2.0, 2.0, 0.0, 0.0)
                StackPanel.children
                    [ TextBlock.create
                          [ TextBlock.text "UNAVAILABLE RIGHT NOW"
                            TextBlock.fontSize Theme.fontXs
                            TextBlock.fontWeight FontWeight.SemiBold
                            TextBlock.foreground (
                                Theme.brush (Theme.readableOnColor surface (Ui.hexOf (Theme.surfaces ()).Label))
                            ) ]
                      for label, why in entries do
                          TextBlock.create
                              [ TextBlock.text (sprintf "%s — %s" label why)
                                TextBlock.fontSize Theme.Type.metricLabel
                                TextBlock.foreground (Theme.muted ())
                                TextBlock.textWrapping TextWrapping.Wrap ] ] ] ]

// ---------------------------------------------------------------------------
// sections
// ---------------------------------------------------------------------------

let private header (tokens: Tokens) : IView =
    StackPanel.create
        [ StackPanel.spacing 6.0
          StackPanel.horizontalAlignment HorizontalAlignment.Left
          StackPanel.maxWidth tokens.ReadingWidth
          StackPanel.children
              [ TextBlock.create
                    [ TextBlock.text "Assignments"
                      TextBlock.fontSize Theme.Type.pageTitle
                      TextBlock.fontWeight FontWeight.Bold
                      TextBlock.foreground (Theme.text ()) ]
                TextBlock.create
                    [ TextBlock.text
                          "What coursework do I have, how am I progressing, and what should I do next?"
                      TextBlock.fontSize Theme.Type.pageMeta
                      TextBlock.foreground (Theme.muted ())
                      TextBlock.textWrapping TextWrapping.Wrap ] ] ]
    :> IView

/// One summary card.
///
/// The value/label block is **vertically centred and left-aligned**: the row's
/// shared height comes from the tallest card, and a two-line label on one card
/// must not leave the other four hanging from the top. An optional qualifier
/// joins the same centred block, so it cannot pull the figure upward either.
///
/// A missing figure is a dash and nothing more — the card carries no empty-state
/// sentence.
let private metricCard (tokens: Tokens) (metric: AssignmentsLogic.Metric) : IView =
    cardWith
        tokens
        14.0
        (Theme.surfaces ()).Card
        VerticalAlignment.Center
        [ StackPanel.create
              [ StackPanel.spacing 2.0
                // Stretched, like every other card's content. The text inside is
                // left-aligned by TextBlock's own default.
                StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                StackPanel.children
                    [ TextBlock.create
                          [ TextBlock.text (metric.Value |> Option.defaultValue AssignmentsLogic.unavailableMark)
                            TextBlock.fontSize AssignmentsLogic.Typography.metricValueSize
                            TextBlock.fontWeight (weight AssignmentsLogic.Typography.metricValueWeight)
                            // A dash is the value, so it keeps the value's ink.
                            TextBlock.foreground (Theme.text ())
                            TextBlock.textTrimming TextTrimming.CharacterEllipsis ]

                      TextBlock.create
                          [ TextBlock.text metric.Label
                            TextBlock.fontSize AssignmentsLogic.Typography.metricLabelSize
                            TextBlock.fontWeight (weight AssignmentsLogic.Typography.metricLabelWeight)
                            TextBlock.foreground (Theme.muted ())
                            TextBlock.textWrapping TextWrapping.Wrap ]

                      // Only ever a qualifier on a real figure — never filler
                      // under an empty one.
                      match metric.Note with
                      | Some note ->
                          TextBlock.create
                              [ TextBlock.text note
                                TextBlock.fontSize AssignmentsLogic.Typography.qualifierSize
                                TextBlock.fontWeight (weight AssignmentsLogic.Typography.qualifierWeight)
                                TextBlock.foreground (Theme.faint ())
                                TextBlock.textWrapping TextWrapping.Wrap ]
                      | None -> () ] ] ]

/// One logical row of cards, as ONE Grid.
///
/// A Grid row sizes to its tallest child and every cell is stretched into it, so
/// the cards' top and bottom edges line up by construction. `minHeight` is on the
/// ROW, never on a card.
let private cardRow (spacing: float) (minHeight: float) (columns: int) (cards: IView list) : IView =
    StackPanel.create
        [ StackPanel.spacing spacing
          StackPanel.children
              (cards
               |> List.chunkBySize (max 1 columns)
               |> List.map (fun row ->
                   Grid.create
                       [ Grid.minHeight minHeight
                         Grid.columnDefinitions (
                             List.replicate row.Length "1*" |> String.concat (sprintf ",%f," spacing)
                         )
                         Grid.children
                             (row
                              |> List.mapi (fun i c ->
                                  Border.create
                                      [ Grid.column (i * 2)
                                        Border.verticalAlignment VerticalAlignment.Stretch
                                        Border.horizontalAlignment HorizontalAlignment.Stretch
                                        Border.child c ]
                                  :> IView)) ]
                   :> IView)) ]
    :> IView

/// The filter chips, plus the one control that keeps assignment CRUD reachable.
///
/// "New assignment" lives here rather than in the header, because the header
/// carries the page's question and nothing else.
let private filtersSection (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let page = (Theme.surfaces ()).Window
    let selectedFill = Theme.compositeOver page (Theme.accentSoftHex ())
    let selectedRing = Theme.brush (Theme.visibleOnColor page (Theme.accentHex ()))

    let chipFor (filter: AssignmentsLogic.Filter) : IView =
        let isSelected = model.Filter = filter
        let label = AssignmentsLogic.Filter.label filter

        Button.create
            [ Button.background (if isSelected then Theme.accentSoft () else Theme.transparent)
              Button.borderThickness AssignmentsLogic.Geometry.filterBorderThickness
              Button.borderBrush (if isSelected then selectedRing else Theme.border ())
              Button.foreground (
                  if isSelected then Theme.brush (Theme.readableOnColor selectedFill (Theme.accentHex ()))
                  else Theme.muted ()
              )
              Button.cornerRadius (Tokens.radius tokens AssignmentsLogic.Geometry.filterRadius)
              Button.minHeight AssignmentsLogic.Geometry.filterMinHeight
              Button.padding (13.0, 4.0)
              Button.fontSize Theme.Type.metricLabel
              Button.fontWeight FontWeight.SemiBold
              Button.margin (0.0, 0.0, 8.0, 8.0)
              Button.cursor Ui.hand
              Button.content label
              Ui.accessibleName (
                  if isSelected then sprintf "%s filter, selected" label else sprintf "Show %s" label
              )
              Button.onClick ((fun _ -> dispatch (SetFilter filter)), SubPatchOptions.Always) ]
        :> IView

    DockPanel.create
        [ DockPanel.children
              [ Border.create
                    [ DockPanel.dock Dock.Right
                      Border.margin (12.0, 0.0, 0.0, 8.0)
                      Border.verticalAlignment VerticalAlignment.Top
                      Border.child (
                          glassAction tokens "＋ New assignment" false (not model.Saving) "Create a new assignment" (fun () ->
                              dispatch NewAssignment)
                      ) ]
                WrapPanel.create
                    [ WrapPanel.children (AssignmentsLogic.Filter.all |> List.map chipFor) ] ] ]
    :> IView

// ---------------------------------------------------------------------------
// the assignment card
// ---------------------------------------------------------------------------

let private progressStrip (tokens: Tokens) (now: DateTime) (f: AssignmentsLogic.Facts) : IView =
    let percent = AssignmentsLogic.progress f
    let pace = AssignmentsLogic.paceOf now f
    let hex = paceHex pace

    StackPanel.create
        [ StackPanel.orientation Orientation.Horizontal
          StackPanel.spacing 12.0
          StackPanel.children
              [ // A FIXED track, so the label beside it cannot move the bar when
                // it changes from "9%" to "100%".
                Border.create
                    [ Border.width AssignmentsLogic.Geometry.progressTrackWidth
                      Border.height AssignmentsLogic.Geometry.progressTrackHeight
                      Border.cornerRadius (AssignmentsLogic.Geometry.progressTrackHeight / 2.0)
                      Border.background (Theme.surfaceAlt ())
                      Border.verticalAlignment VerticalAlignment.Center
                      Border.isHitTestVisible false
                      Ui.decorative ()
                      Border.child (
                          Grid.create
                              [ Grid.columnDefinitions (sprintf "%d*,%d*" (max percent 1) (max (100 - percent) 1))
                                Grid.children
                                    [ Border.create
                                          [ Grid.column 0
                                            Border.cornerRadius (AssignmentsLogic.Geometry.progressTrackHeight / 2.0)
                                            Border.background (
                                                if percent > 0 then Theme.brush hex else Theme.transparent
                                            ) ] ] ]
                      ) ]

                TextBlock.create
                    [ TextBlock.text (sprintf "%d%% · %s" percent (AssignmentsLogic.Pace.label pace))
                      TextBlock.fontSize Theme.Type.rowMeta
                      TextBlock.minWidth AssignmentsLogic.Geometry.progressLabelMinWidth
                      TextBlock.foreground (Theme.muted ())
                      TextBlock.verticalAlignment VerticalAlignment.Center ]

                match AssignmentsLogic.checklist f with
                | Some(complete, total) ->
                    TextBlock.create
                        [ TextBlock.text (sprintf "Checklist %d/%d" complete total)
                          TextBlock.fontSize Theme.Type.metricLabel
                          TextBlock.foreground (Theme.faint ())
                          TextBlock.verticalAlignment VerticalAlignment.Center ]
                | None -> () ] ]
    :> IView

/// The real progression, drawn from `AssignmentStatus` — not the redesign's
/// invented Research/Planning/Draft/Review/Submission stages.
let private stageRow (tokens: Tokens) (surface: Color) (f: AssignmentsLogic.Facts) : IView =
    let blocked = AssignmentsLogic.isBlocked f

    StackPanel.create
        [ StackPanel.spacing 6.0
          StackPanel.children
              [ Ui.sectionTitle "Status"

                WrapPanel.create
                    [ WrapPanel.children
                          (AssignmentsLogic.stageStates f
                           |> List.map (fun (stage, state) ->
                               let hex =
                                   match state with
                                   | AssignmentsLogic.StageState.Done -> Theme.successHex ()
                                   | AssignmentsLogic.StageState.Current ->
                                       if blocked then Theme.warningHex () else Theme.accentHex ()
                                   | AssignmentsLogic.StageState.Pending -> Theme.Identity.neutral

                               StackPanel.create
                                   [ StackPanel.orientation Orientation.Horizontal
                                     StackPanel.spacing 6.0
                                     StackPanel.margin (0.0, 0.0, 16.0, 4.0)
                                     StackPanel.children
                                         [ Border.create
                                               [ Border.width 9.0
                                                 Border.height 9.0
                                                 Border.cornerRadius 4.5
                                                 Border.verticalAlignment VerticalAlignment.Center
                                                 Border.background (
                                                     match state with
                                                     | AssignmentsLogic.StageState.Pending -> Theme.transparent
                                                     | _ -> Theme.brush hex
                                                 )
                                                 Border.borderThickness 1.5
                                                 Border.borderBrush (Theme.brush hex)
                                                 Ui.decorative () ]
                                           TextBlock.create
                                               [ TextBlock.text (AssignmentsLogic.Stage.label stage)
                                                 TextBlock.fontSize Theme.Type.rowMeta
                                                 TextBlock.verticalAlignment VerticalAlignment.Center
                                                 TextBlock.foreground (
                                                     match state with
                                                     | AssignmentsLogic.StageState.Pending -> Theme.faint ()
                                                     | _ -> Theme.text ()
                                                 ) ] ] ]
                               :> IView)) ]

                if blocked then
                    bulletRow
                        "⚠"
                        (Theme.brush (Theme.readableOnColor surface (Theme.warningHex ())))
                        "Marked as blocked — the current stage is held up."
                        (Theme.text ())

                match AssignmentsLogic.checklist f with
                | Some(complete, total) ->
                    bulletRow
                        "•"
                        (Theme.faint ())
                        (sprintf "%d of %d checklist steps done. Open the assignment to tick them off." complete total)
                        (Theme.muted ())
                | None ->
                    // The domain has no milestone model. Rather than invent
                    // stages, this says what would fill the space.
                    bulletRow
                        "•"
                        (Theme.faint ())
                        "No checklist yet — open the assignment to add the steps you plan to work through."
                        (Theme.muted ()) ] ]
    :> IView

let private assignmentCard
    (tokens: Tokens)
    (now: DateTime)
    (model: Model)
    (expandedId: AssignmentId option)
    (dispatch: Msg -> unit)
    (f: AssignmentsLogic.Facts) : IView =
    let a = f.Assignment
    // The RESOLVED expansion, which includes the default the screen opens for
    // itself — not the raw model field, which is empty on first arrival.
    let isExpanded = expandedId = Some a.Id
    let surface = cardSurface isExpanded
    let risk = AssignmentsLogic.riskOf now f
    let signals = AssignmentsLogic.signalsFor now f
    let inkOn (hex: string) = Theme.brush (Theme.readableOnColor surface hex)

    let actionStates =
        AssignmentsLogic.actions
            { HasSessions = f.SessionCount > 0
              HasActivity = f.ActivityCount > 0
              AiAvailable = model.AiAvailable
              Busy = model.Saving }

    let unavailable =
        actionStates |> List.choose (fun s -> s.DisabledBecause |> Option.map (fun why -> s.Label, why))

    // ---- header row ----
    let headerRow: IView =
        Grid.create
            [ Grid.columnDefinitions "Auto,8,Auto,10,*,10,Auto"
              Grid.children
                  [ // The expansion control: a real Button, so it is focusable
                    // and keyboard-activatable, in a FIXED column so the title
                    // starts at the same x whether the card is open or shut.
                    Button.create
                        [ Grid.column 0
                          Button.content (if isExpanded then "▾" else "▸")
                          Button.width AssignmentsLogic.Geometry.chevronWidth
                          Button.background Theme.transparent
                          Button.borderThickness 0.0
                          Button.padding 0.0
                          Button.fontSize Theme.Type.metricLabel
                          Button.foreground (Theme.muted ())
                          Button.verticalAlignment VerticalAlignment.Center
                          Button.cursor Ui.hand
                          Ui.accessibleName (
                              if isExpanded then sprintf "Collapse %s" a.Title else sprintf "Expand %s" a.Title
                          )
                          Button.onClick ((fun _ -> dispatch (ToggleExpanded a.Id)), SubPatchOptions.Always) ]

                    // The module's own identity colour, as stored on the module.
                    Border.create
                        [ Grid.column 2
                          Border.width AssignmentsLogic.Geometry.moduleDotSize
                          Border.height AssignmentsLogic.Geometry.moduleDotSize
                          Border.cornerRadius (AssignmentsLogic.Geometry.moduleDotSize / 2.0)
                          Border.verticalAlignment VerticalAlignment.Center
                          Border.background (
                              match f.Module with
                              | Some m -> Theme.brush m.Color
                              | None -> Theme.brush Theme.Identity.neutral
                          )
                          Ui.decorative () ]

                    TextBlock.create
                        [ Grid.column 4
                          TextBlock.text a.Title
                          TextBlock.fontSize Theme.Type.rowTitle
                          TextBlock.fontWeight FontWeight.SemiBold
                          TextBlock.verticalAlignment VerticalAlignment.Center
                          // Wraps rather than ellipsises: a long coursework title
                          // is the thing being identified.
                          TextBlock.textWrapping TextWrapping.Wrap
                          TextBlock.foreground (
                              if AssignmentsLogic.isOpen f then Theme.text () else Theme.muted ()
                          ) ]

                    match AssignmentsLogic.moduleCode f with
                    | Some code ->
                        Border.create
                            [ Grid.column 6
                              Border.verticalAlignment VerticalAlignment.Center
                              Border.child (chip tokens surface code (Theme.accentHex ())) ]
                    | None -> () ] ]
        :> IView

    // ---- metadata row ----
    let meta (text: string) (brush: IBrush) : IView =
        TextBlock.create
            [ TextBlock.text text
              TextBlock.fontSize Theme.Type.rowMeta
              TextBlock.foreground brush
              TextBlock.margin (0.0, 0.0, 16.0, 4.0)
              TextBlock.verticalAlignment VerticalAlignment.Center ]
        :> IView

    let metaRow: IView =
        WrapPanel.create
            [ WrapPanel.children
                  [ Border.create
                        [ Border.minWidth AssignmentsLogic.Geometry.riskBadgeMinWidth
                          Border.margin (0.0, 0.0, 16.0, 4.0)
                          Border.horizontalAlignment HorizontalAlignment.Left
                          Border.child (
                              chip
                                  tokens
                                  surface
                                  (sprintf "%s risk" (AssignmentsLogic.Risk.label risk))
                                  (riskHex risk)
                          ) ]

                    match a.Weighting with
                    | Some w -> meta (sprintf "Weight %g%%" w) (Theme.muted ())
                    | None -> meta "Weight not set" (Theme.faint ())

                    match a.Deadline with
                    | Some d ->
                        meta
                            (sprintf "Due %s · %s" (Format.shortDate d) (Format.deadline now d))
                            (if AssignmentsLogic.isOverdue now f then inkOn (Theme.dangerHex ())
                             else Theme.muted ())
                    | None -> meta "No deadline" (Theme.faint ())

                    if AssignmentsLogic.isOpen f then
                        meta
                            (sprintf "%s left" (Format.duration (AssignmentsLogic.remainingMinutes f)))
                            (Theme.muted ())

                    Border.create
                        [ Border.margin (0.0, 0.0, 16.0, 4.0)
                          Border.child (
                              chip tokens surface (AssignmentStatus.label a.Status) (Theme.Identity.neutral)
                          ) ] ] ]
        :> IView

    // ---- expanded detail ----
    let expandedDetail: IView list =
        [ Border.create
              [ Border.height 1.0
                Border.margin (0.0, 4.0)
                Border.background (Theme.divider ())
                Ui.decorative () ]

          if a.Description.Trim().Length > 0 then
              TextBlock.create
                  [ TextBlock.text a.Description
                    TextBlock.fontSize Theme.Type.rowMeta
                    TextBlock.foreground (Theme.muted ())
                    TextBlock.textWrapping TextWrapping.Wrap
                    TextBlock.lineHeight (Theme.Type.rowMeta * 1.55) ]
          else
              bulletRow "•" (Theme.faint ()) "No description recorded." (Theme.muted ())

          // Every risk signal, in words. Colour is never the only cue.
          if not signals.IsEmpty then
              StackPanel.create
                  [ StackPanel.spacing 6.0
                    StackPanel.children
                        [ Ui.sectionTitle "Signals"
                          WrapPanel.create
                              [ WrapPanel.children
                                    (signals
                                     |> List.map (fun s ->
                                         Border.create
                                             [ Border.margin (0.0, 0.0, 6.0, 6.0)
                                               Border.child (
                                                   chip
                                                       tokens
                                                       surface
                                                       ((if AssignmentsLogic.Signal.isWarning s then "⚠ " else "• ")
                                                        + AssignmentsLogic.Signal.text s)
                                                       (signalHex s)
                                               ) ]
                                         :> IView)) ] ] ]

          stageRow tokens surface f

          // Only relationships that genuinely exist. No zero rows.
          match AssignmentsLogic.linkedGroups f with
          | [] ->
              StackPanel.create
                  [ StackPanel.spacing 6.0
                    StackPanel.children
                        [ Ui.sectionTitle "Linked"
                          bulletRow
                              "•"
                              (Theme.faint ())
                              "Nothing is linked to this assignment yet."
                              (Theme.muted ()) ] ]
          | groups ->
              StackPanel.create
                  [ StackPanel.spacing 6.0
                    StackPanel.children
                        [ Ui.sectionTitle "Linked"
                          WrapPanel.create
                              [ WrapPanel.children
                                    (groups
                                     |> List.map (fun g ->
                                         Border.create
                                             [ Border.margin (0.0, 0.0, 6.0, 6.0)
                                               Border.child (
                                                   chip tokens surface g.Label (Theme.entityKindHex g.Kind)
                                               ) ]
                                         :> IView)) ] ] ]

          StackPanel.create
              [ StackPanel.spacing 4.0
                StackPanel.children
                    [ WrapPanel.create
                          [ WrapPanel.children
                                (actionStates
                                 |> List.map (fun s ->
                                     glassAction
                                         tokens
                                         s.Label
                                         s.Primary
                                         s.Enabled
                                         (sprintf "%s: %s" s.Label a.Title)
                                         (fun () -> dispatch (Act(s.Id, f))))) ]

                      yield! unavailableNotes surface unavailable ] ] ]

    card
        tokens
        18.0
        surface
        [ Border.create
              [ // **No expansion outline.** This used to paint an accent border
                // here when expanded, which drew a line around the whole of the
                // card's content — reported in the first runtime review as an
                // unintended outline. It was redundant as well as unwanted:
                // expansion already changes the card's surface from `Card` to
                // `Selected`, and that difference is separately proven visible in
                // both themes (LightThemeTests, "the Selected surface is
                // distinguishable from Card").
                //
                // The thickness stays, in both states, so removing the colour
                // cannot move anything — the reserved space was always there.
                // Keyboard focus is unaffected: it is drawn by Fluent's adorner
                // on the header button, never by this border.
                Border.borderThickness AssignmentsLogic.Geometry.cardBorderThickness
                Border.borderBrush Theme.transparent
                Border.cornerRadius (Tokens.radius tokens AssignmentsLogic.Geometry.cardRadius)
                Border.padding (0.0, 0.0, 0.0, 0.0)
                Border.child (
                    StackPanel.create
                        [ StackPanel.spacing (Tokens.space tokens 9.0)
                          StackPanel.children
                              [ headerRow
                                metaRow
                                progressStrip tokens now f

                                bulletRow
                                    "✦"
                                    (Theme.accent ())
                                    (AssignmentsLogic.insight now f)
                                    (Theme.textSecondary ())

                                if isExpanded then
                                    yield! expandedDetail ] ]
                ) ] ]

// ---------------------------------------------------------------------------
// the forecast column
// ---------------------------------------------------------------------------

let private forecastPanel (tokens: Tokens) (now: DateTime) (model: Model) : IView =
    let trajectory = AssignmentsLogic.trajectory now model.Facts
    let surface = (Theme.surfaces ()).Card
    let workload = AssignmentsLogic.workloadMinutes model.Facts

    plainCard
        tokens
        20.0
        [ // NOT "AI forecast": every figure below is computed here from recorded
          // data. Nothing on this panel came from a model.
          cardTitle "Coursework forecast"

          StackPanel.create
              [ StackPanel.spacing 4.0
                StackPanel.children
                    [ sectionLabel "Current trajectory"
                      StackPanel.create
                          [ StackPanel.orientation Orientation.Horizontal
                            StackPanel.spacing 8.0
                            StackPanel.children
                                [ chip
                                      tokens
                                      surface
                                      (AssignmentsLogic.Trajectory.label trajectory)
                                      (trajectoryHex trajectory) ] ]
                      // The one sentence that explains the whole panel. Because
                      // it is here, the rows below need no prose of their own.
                      supporting (AssignmentsLogic.Trajectory.detail trajectory) ] ]

          StackPanel.create
              [ StackPanel.spacing 2.0
                StackPanel.children
                    [ sectionLabel "Highest risk"
                      match AssignmentsLogic.highestRisk now model.Facts with
                      | Some f -> primaryValue f.Assignment.Title
                      | None -> primaryValue AssignmentsLogic.unavailableMark ] ]

          StackPanel.create
              [ StackPanel.spacing 2.0
                StackPanel.children
                    [ sectionLabel "Suggested next"
                      match AssignmentsLogic.suggestedNext now model.Facts with
                      | Some f ->
                          primaryValue f.Assignment.Title
                          // Only worth saying when there IS an ordering to
                          // explain.
                          supporting
                              "Ordered by risk, then the nearer deadline, then the heavier weight. Deterministic — not a model's opinion."
                      | None -> primaryValue AssignmentsLogic.unavailableMark ] ]

          Border.create
              [ Border.height 1.0
                Border.margin (0.0, 4.0)
                Border.background (Theme.divider ())
                Ui.decorative () ]

          forecastRow "Workload remaining" (primaryValue (Format.hours (float workload / 60.0))) ]

let private riskPanel (tokens: Tokens) (now: DateTime) (model: Model) : IView =
    let surface = (Theme.surfaces ()).Card

    plainCard
        tokens
        20.0
        [ cardTitle "Risk overview"

          for row in AssignmentsLogic.riskOverview now model.Facts do
              // The value on the right outranks its label: a chip when there is
              // a level, and a single dash — nothing more — when there is not.
              forecastRow
                  row.Label
                  (StackPanel.create
                      [ StackPanel.spacing 2.0
                        StackPanel.horizontalAlignment HorizontalAlignment.Right
                        StackPanel.children
                            [ match row.Value with
                              | Some value ->
                                  chip
                                      tokens
                                      surface
                                      value
                                      (match row.Level with
                                       | Some level -> riskHex level
                                       | None -> Theme.Identity.neutral)
                              | None ->
                                  TextBlock.create
                                      [ TextBlock.text AssignmentsLogic.unavailableMark
                                        TextBlock.fontSize Theme.Type.rowMeta
                                        TextBlock.fontWeight (
                                            weight AssignmentsLogic.Typography.overviewValueWeight
                                        )
                                        TextBlock.foreground (Theme.text ())
                                        TextBlock.horizontalAlignment HorizontalAlignment.Right ]

                              match row.Note with
                              | Some note ->
                                  TextBlock.create
                                      [ TextBlock.text note
                                        TextBlock.fontSize AssignmentsLogic.Typography.supportingSize
                                        TextBlock.fontWeight (
                                            weight AssignmentsLogic.Typography.supportingWeight
                                        )
                                        TextBlock.foreground (Theme.faint ())
                                        TextBlock.textAlignment TextAlignment.Right
                                        TextBlock.textWrapping TextWrapping.Wrap ]
                              | None -> () ] ])

          // Kept because the absence is a real design decision a reader will ask
          // about — but quieter than a row, and without a bullet competing with
          // the five above it.
          TextBlock.create
              [ TextBlock.text
                    "Grade risk is absent: no mark, target or prediction exists anywhere in the application."
                TextBlock.fontSize AssignmentsLogic.Typography.qualifierSize
                TextBlock.fontWeight (weight AssignmentsLogic.Typography.qualifierWeight)
                TextBlock.foreground (Theme.faint ())
                TextBlock.margin (0.0, 4.0, 0.0, 0.0)
                TextBlock.textWrapping TextWrapping.Wrap ] ]

/// The Timeline navigation row.
///
/// **A navigation row, not a button.** It has no resting fill and no outline: it
/// is a transparent full-width control inside the card, with its text at the left
/// inset and its arrow at the right, so the whole row is the target rather than a
/// pill floating in a card.
///
/// Hover and pressed come from Fluent's presenter fill, which `FluentMaterial`
/// maps to the material's own **Hover** and **Selected** roles — a warm wash, not
/// a cool grey block.
///
/// **It is always enabled.** The Timeline is a screen, and a screen can always be
/// opened; an empty Timeline is a real state with its own honest empty view.
/// Gating this on some assignment having history conflated it with a card's
/// "View Timeline", which focuses *one* assignment and genuinely does need
/// something to focus on.
let private timelineLink (tokens: Tokens) (dispatch: Msg -> unit) (model: Model) : IView =
    let hasHistory = model.Facts |> List.exists (fun f -> f.ActivityCount > 0)

    plainCard
        tokens
        AssignmentsLogic.Geometry.timelineCardPadding
        [ Button.create
              [ Button.background Theme.transparent
                Button.borderThickness 0.0
                Button.cornerRadius (Tokens.radius tokens 10.0)
                Button.horizontalAlignment HorizontalAlignment.Stretch
                Button.horizontalContentAlignment HorizontalAlignment.Stretch
                Button.padding (AssignmentsLogic.Geometry.timelineRowInset, 8.0)
                Button.minHeight AssignmentsLogic.Geometry.timelineRowMinHeight
                Button.cursor Ui.hand
                Ui.accessibleName "Open the full Timeline"
                Button.onClick ((fun _ -> dispatch OpenTimeline), SubPatchOptions.Always)
                Button.content (
                    // Star column then Auto: the text sits at the left inset and
                    // the arrow is pushed to the opposite edge of the row.
                    Grid.create
                        [ Grid.columnDefinitions AssignmentsLogic.Geometry.timelineColumns
                          Grid.children
                              [ TextBlock.create
                                    [ Grid.column 0
                                      TextBlock.text "Open full Timeline"
                                      TextBlock.fontSize Theme.Type.rowMeta
                                      TextBlock.fontWeight (
                                          weight AssignmentsLogic.Typography.overviewValueWeight
                                      )
                                      TextBlock.foreground (Theme.text ())
                                      TextBlock.verticalAlignment VerticalAlignment.Center
                                      TextBlock.horizontalAlignment HorizontalAlignment.Left ]
                                TextBlock.create
                                    [ Grid.column 1
                                      TextBlock.text "→"
                                      TextBlock.fontSize Theme.Type.rowMeta
                                      TextBlock.foreground (Theme.muted ())
                                      TextBlock.verticalAlignment VerticalAlignment.Center
                                      Ui.decorative () ] ] ]
                ) ]

          // Outside the row, never inside it: the control says what it does, and
          // this says why it cannot.
          if not hasHistory then
              TextBlock.create
                  [ TextBlock.text "Nothing has been recorded against any assignment yet."
                    TextBlock.fontSize AssignmentsLogic.Typography.qualifierSize
                    TextBlock.fontWeight (weight AssignmentsLogic.Typography.qualifierWeight)
                    TextBlock.foreground (Theme.faint ())
                    TextBlock.margin (AssignmentsLogic.Geometry.timelineRowInset, 0.0, 0.0, 0.0)
                    TextBlock.textWrapping TextWrapping.Wrap ] ]

// ---------------------------------------------------------------------------
// the editor
// ---------------------------------------------------------------------------

let private editor (tokens: Tokens) (model: Model) (draft: Draft) (dispatch: Msg -> unit) : IView =
    let moduleOptions = "(no module)" :: (model.Modules |> List.map StudyModule.displayName)

    plainCard
        tokens
        20.0
        [ DockPanel.create
              [ DockPanel.children
                    [ StackPanel.create
                          [ DockPanel.dock Dock.Right
                            StackPanel.orientation Orientation.Horizontal
                            StackPanel.spacing 8.0
                            StackPanel.children
                                [ if draft.Original.IsSome then
                                      Ui.dangerButton "Delete" (fun () -> dispatch RequestDelete)
                                  Ui.ghostButton "Close" (fun () -> dispatch CloseEditor)
                                  Ui.primaryButton (if model.Saving then "Saving…" else "Save") (fun () ->
                                      if not model.Saving then dispatch Save) ] ]
                      TextBlock.create
                          [ TextBlock.text (if draft.Original.IsSome then "Edit assignment" else "New assignment")
                            TextBlock.fontSize Theme.Type.cardTitle
                            TextBlock.fontWeight FontWeight.SemiBold
                            TextBlock.foreground (Theme.text ()) ] ] ]

          // Deleting is irreversible: no repository here keeps a prior state.
          if model.PendingDelete then
              Border.create
                  [ Border.background (Theme.brush (Theme.withAlphaHex "1F" (Theme.dangerHex ())))
                    Border.borderThickness 1.0
                    Border.borderBrush (Theme.brush (Theme.overlayEdgeHex "55" (Theme.dangerHex ())))
                    Border.cornerRadius (Tokens.radius tokens 12.0)
                    Border.padding (16.0, 12.0)
                    Border.child (
                        StackPanel.create
                            [ StackPanel.spacing 10.0
                              StackPanel.children
                                  [ TextBlock.create
                                        [ TextBlock.text
                                              "Delete this assignment permanently? Its planner sessions lose their link and nothing here keeps a prior state, so this cannot be undone."
                                          TextBlock.fontSize Theme.Type.rowMeta
                                          TextBlock.foreground (Theme.text ())
                                          TextBlock.textWrapping TextWrapping.Wrap ]
                                    StackPanel.create
                                        [ StackPanel.orientation Orientation.Horizontal
                                          StackPanel.spacing 10.0
                                          StackPanel.children
                                              [ Ui.dangerButton "Yes, delete it" (fun () -> dispatch ConfirmDelete)
                                                Ui.ghostButton "Cancel" (fun () -> dispatch CancelDelete) ] ] ] ]
                    ) ]

          match draft.FormError with
          | Some error ->
              TextBlock.create
                  [ TextBlock.text error
                    TextBlock.foreground (Theme.danger ())
                    TextBlock.textWrapping TextWrapping.Wrap
                    TextBlock.fontSize Theme.Type.rowMeta ]
          | None -> ()

          Ui.labeled "Title" (Ui.textBox draft.Title "e.g. Graphics coursework 2" (SetTitle >> dispatch))

          Grid.create
              [ Grid.columnDefinitions "1*,12,1*"
                Grid.children
                    [ StackPanel.create
                          [ Grid.column 0
                            StackPanel.children
                                [ Ui.labeled
                                      "Module"
                                      (Ui.combo moduleOptions draft.ModuleIndex (SetModuleIndex >> dispatch)) ] ]
                      StackPanel.create
                          [ Grid.column 2
                            StackPanel.children
                                [ Ui.labeled
                                      "Deadline"
                                      (Ui.textBox draft.DeadlineText "yyyy-MM-dd HH:mm" (SetDeadline >> dispatch)) ] ] ] ]

          Grid.create
              [ Grid.columnDefinitions "1*,12,1*,12,1*"
                Grid.children
                    [ StackPanel.create
                          [ Grid.column 0
                            StackPanel.children
                                [ Ui.labeled
                                      "Priority"
                                      (Ui.combo
                                          (Priority.all |> List.map Priority.label)
                                          draft.PriorityIndex
                                          (SetPriorityIndex >> dispatch)) ] ]
                      StackPanel.create
                          [ Grid.column 2
                            StackPanel.children
                                [ Ui.labeled
                                      "Status"
                                      (Ui.combo
                                          (AssignmentStatus.all |> List.map AssignmentStatus.label)
                                          draft.StatusIndex
                                          (SetStatusIndex >> dispatch)) ] ]
                      StackPanel.create
                          [ Grid.column 4
                            StackPanel.children
                                [ Ui.labeled
                                      "Weighting %"
                                      (Ui.textBox draft.WeightingText "e.g. 40" (SetWeighting >> dispatch)) ] ] ] ]

          Grid.create
              [ Grid.columnDefinitions "1*,12,2*"
                Grid.children
                    [ StackPanel.create
                          [ Grid.column 0
                            StackPanel.children
                                [ Ui.labeled
                                      "Estimated hours"
                                      (Ui.textBox draft.EstimatedText "e.g. 12" (SetEstimated >> dispatch)) ] ]
                      StackPanel.create
                          [ Grid.column 2
                            StackPanel.children
                                [ Ui.labeled
                                      "Progress"
                                      (StackPanel.create
                                          [ StackPanel.orientation Orientation.Horizontal
                                            StackPanel.spacing 8.0
                                            StackPanel.children
                                                [ Ui.smallButton "−10" (fun () -> dispatch (ProgressDelta -10))
                                                  Border.create
                                                      [ Border.width 220.0
                                                        Border.verticalAlignment VerticalAlignment.Center
                                                        Border.child (
                                                            Ui.progressBar draft.Progress (Theme.accentHex ())
                                                        ) ]
                                                  Ui.smallButton "+10" (fun () -> dispatch (ProgressDelta 10))
                                                  TextBlock.create
                                                      [ TextBlock.text (sprintf "%d%%" draft.Progress)
                                                        TextBlock.fontSize Theme.Type.rowMeta
                                                        TextBlock.fontWeight FontWeight.SemiBold
                                                        TextBlock.foreground (Theme.text ())
                                                        TextBlock.verticalAlignment VerticalAlignment.Center ] ] ]) ] ] ] ]

          Ui.labeled
              "Description"
              (Ui.multilineBox draft.Description "Notes, requirements, marking scheme…" 90.0 (SetDescription >> dispatch))

          Ui.sectionTitle "Checklist"

          StackPanel.create
              [ StackPanel.spacing 4.0
                StackPanel.children
                    (draft.Checklist
                     |> List.map (fun item ->
                         DockPanel.create
                             [ DockPanel.children
                                   [ StackPanel.create
                                         [ DockPanel.dock Dock.Right
                                           StackPanel.children
                                               [ Ui.smallButton "✕" (fun () ->
                                                     dispatch (RemoveChecklistItem item.Id)) ] ]
                                     StackPanel.create
                                         [ StackPanel.orientation Orientation.Horizontal
                                           StackPanel.spacing 8.0
                                           StackPanel.children
                                               [ Ui.checkGlyph item.Done (fun () ->
                                                     dispatch (ToggleChecklistItem item.Id))
                                                 TextBlock.create
                                                     [ TextBlock.text item.Text
                                                       TextBlock.fontSize Theme.Type.rowMeta
                                                       TextBlock.verticalAlignment VerticalAlignment.Center
                                                       TextBlock.textWrapping TextWrapping.Wrap
                                                       TextBlock.foreground (
                                                           if item.Done then Theme.faint () else Theme.text ()
                                                       )
                                                       TextBlock.textDecorations (
                                                           if item.Done then TextDecorations.Strikethrough else null
                                                       ) ] ] ] ] ]
                         :> IView)) ]

          DockPanel.create
              [ DockPanel.children
                    [ StackPanel.create
                          [ DockPanel.dock Dock.Right
                            StackPanel.children [ Ui.smallButton "Add" (fun () -> dispatch AddChecklistItem) ] ]
                      Ui.textBox draft.NewChecklistText "Add a checklist step…" (SetNewChecklistText >> dispatch) ] ]

          Grid.create
              [ Grid.columnDefinitions "1*,12,1*"
                Grid.children
                    [ StackPanel.create
                          [ Grid.column 0
                            StackPanel.children
                                [ Ui.labeled
                                      "Submission link"
                                      (Ui.textBox draft.SubmissionUrl "https://…" (SetSubmission >> dispatch)) ] ]
                      StackPanel.create
                          [ Grid.column 2
                            StackPanel.children
                                [ Ui.labeled
                                      "Git repository"
                                      (Ui.textBox draft.RepoUrl "https://gitlab.com/…" (SetRepo >> dispatch)) ] ] ] ] ]

// ---------------------------------------------------------------------------
// page
// ---------------------------------------------------------------------------

let view (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let spacing = Tokens.space tokens 22.0

    // ONE clock reading for the whole frame. Every risk badge, deadline and pace
    // on the page derives from this single instant.
    let now = DateTime.Now

    let visible = AssignmentsLogic.visible now model.WeekStart model.Filter model.Facts

    // The open card, including the default the screen opens for itself. Resolved
    // here rather than stored, so filtering cannot leave a card open on
    // something no longer on screen.
    let expandedId = AssignmentsLogic.expansionFor model.ExpansionChosen model.Expanded visible

    let listColumn: IView =
        if model.Loading && model.Facts.IsEmpty then
            StackPanel.create
                [ StackPanel.spacing spacing
                  StackPanel.children [ Ui.skeletonCard 4; Ui.skeletonCard 3; Ui.skeletonCard 3 ] ]
            :> IView
        elif visible.IsEmpty then
            // The list is empty, the PAGE is not: the metrics, the filters and
            // the forecast column all keep their places.
            plainCard
                tokens
                20.0
                [ Ui.sectionTitle "Coursework"
                  bulletRow
                      "•"
                      (Theme.faint ())
                      (AssignmentsLogic.Filter.emptyMessage model.Filter)
                      (Theme.muted ())

                  if model.Filter <> AssignmentsLogic.Filter.All && not model.Facts.IsEmpty then
                      Border.create
                          [ Border.horizontalAlignment HorizontalAlignment.Left
                            Border.child (
                                Ui.smallButton
                                    (sprintf "Show all %d assignments" model.Facts.Length)
                                    (fun () -> dispatch (SetFilter AssignmentsLogic.Filter.All))
                            ) ]

                  if model.Facts.IsEmpty then
                      bulletRow
                          "•"
                          (Theme.faint ())
                          "Create one with ＋ New assignment, or capture a deadline with Ctrl+N and apply the proposed action."
                          (Theme.faint ()) ]
        else
            StackPanel.create
                [ StackPanel.spacing (Tokens.space tokens 12.0)
                  StackPanel.children (visible |> List.map (assignmentCard tokens now model expandedId dispatch)) ]
            :> IView

    let sideColumn: IView =
        StackPanel.create
            [ StackPanel.spacing spacing
              StackPanel.children
                  [ forecastPanel tokens now model
                    riskPanel tokens now model
                    timelineLink tokens dispatch model ] ]
        :> IView

    let workspace: IView =
        if AssignmentsLogic.showsSidePanel model.Width then
            Grid.create
                [ Grid.minHeight AssignmentsLogic.Geometry.workspaceMinHeight
                  Grid.columnDefinitions (
                      sprintf
                          "%f*,%f,%f*"
                          AssignmentsLogic.Geometry.mainLeftWeight
                          spacing
                          AssignmentsLogic.Geometry.mainRightWeight
                  )
                  Grid.children
                      [ Border.create
                            [ Grid.column 0
                              Border.verticalAlignment VerticalAlignment.Top
                              Border.horizontalAlignment HorizontalAlignment.Stretch
                              Border.child listColumn ]
                        Border.create
                            [ Grid.column 2
                              Border.verticalAlignment VerticalAlignment.Top
                              Border.horizontalAlignment HorizontalAlignment.Stretch
                              Border.child sideColumn ] ] ]
            :> IView
        else
            // Medium and narrow: the list takes the full width and the forecast
            // moves beneath it, in the same reading order.
            StackPanel.create
                [ StackPanel.spacing spacing
                  StackPanel.children [ listColumn; sideColumn ] ]
            :> IView

    let renderSection (section: AssignmentsLogic.Section) : IView =
        match section with
        | AssignmentsLogic.Section.Header -> header tokens
        | AssignmentsLogic.Section.Metrics ->
            cardRow
                spacing
                AssignmentsLogic.Geometry.metricRowMinHeight
                (AssignmentsLogic.metricColumns model.Width)
                (AssignmentsLogic.metrics now model.Facts |> List.map (metricCard tokens))
        | AssignmentsLogic.Section.Filters -> filtersSection tokens model dispatch
        | AssignmentsLogic.Section.Workspace -> workspace

    let body: IView list =
        match model.Draft with
        | Some draft ->
            // The editor is a distinct workflow, not a panel squeezed beside the
            // list: it owns the page while it is open, and every field the screen
            // has ever had is still on it.
            [ header tokens; editor tokens model draft dispatch ]
        | None -> AssignmentsLogic.sectionOrder |> List.map renderSection

    Border.create
        [ Border.onSizeChanged (
              (fun args ->
                  // Only when a layout decision would actually change.
                  let decisions (width: float) =
                      AssignmentsLogic.showsSidePanel width, AssignmentsLogic.metricColumns width

                  if decisions model.Width <> decisions args.NewSize.Width then
                      dispatch (Resized args.NewSize.Width)),
              SubPatchOptions.Always
          )
          Border.child (
              Ui.scrollNoChrome (
                  StackPanel.create
                      [ StackPanel.spacing spacing
                        StackPanel.children
                            [ // A failure never blanks the page: the problem is
                              // stated, retry is one click away, and everything
                              // below keeps whatever it can still show.
                              match model.Error with
                              | Some error ->
                                  let wash =
                                      Theme.compositeOver
                                          (Theme.surfaces ()).Window
                                          (Theme.withAlphaHex "22" (Theme.dangerHex ()))

                                  Border.create
                                      [ Border.background (
                                            Theme.brush (Theme.withAlphaHex "22" (Theme.dangerHex ()))
                                        )
                                        Border.cornerRadius (Tokens.radius tokens 12.0)
                                        Border.padding (16.0, 12.0)
                                        Border.child (
                                            DockPanel.create
                                                [ DockPanel.children
                                                      [ Border.create
                                                            [ DockPanel.dock Dock.Right
                                                              Border.margin (12.0, 0.0, 0.0, 0.0)
                                                              Border.verticalAlignment VerticalAlignment.Center
                                                              Border.child (
                                                                  Ui.smallButton "Try again" (fun () ->
                                                                      dispatch Load)
                                                              ) ]
                                                        TextBlock.create
                                                            [ TextBlock.text error
                                                              TextBlock.fontSize Theme.Type.rowMeta
                                                              TextBlock.foreground (
                                                                  Theme.brush (
                                                                      Theme.readableOnColor
                                                                          wash
                                                                          (Theme.dangerHex ())
                                                                  )
                                                              )
                                                              TextBlock.verticalAlignment VerticalAlignment.Center
                                                              TextBlock.textWrapping TextWrapping.Wrap ] ] ]
                                        ) ]
                              | None -> ()

                              yield! body ] ] ) ) ]
    :> IView
