module UniversityOS.App.Features.Planner

open System
open Elmish
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.Domain.Planning
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Controls

/// The intelligent planner: generates a realistic schedule and — crucially —
/// shows the reasoning for every block it places.
///
/// The page renders **the same nine cards whether or not a plan exists**. An
/// empty planner is a planner with nothing scheduled, not a planner with nothing
/// to say: each card carries its own honest empty state and the structure never
/// collapses. See `PlannerLogic.cardOrder`.

type Model =
    { Sessions: PlannedSession list
      Unplanned: UnplannedTask list
      Summary: string option
      /// The user's real scheduling preferences. Every constraint check and
      /// every "of your maximum" figure is measured against these, so the
      /// numbers are the ones the planner actually worked to.
      Prefs: PlannerPreferences
      /// Open assignments exactly as the planner sees them — the source for
      /// outstanding workload, coverage and the next deadline.
      Tasks: PlannableTask list
      /// Lectures, meetings and events the scheduler treated as busy time.
      Busy: BusyBlock list
      /// Real activity entries describing what has happened to the plan.
      History: ActivityEntry list
      /// True only while a regeneration is in flight. Also the re-entry guard:
      /// the button is disabled and the message is ignored while it is set, so a
      /// double click cannot queue a second generation.
      Generating: bool
      Loading: bool
      Error: string option
      /// Whether the full-day panel is open. Transient view state: never
      /// persisted, and reset by any reload.
      AllTodayOpen: bool
      /// The alternative strategy currently selected. Selecting one does not
      /// regenerate: it is a choice, and applying it is a separate, explicit act.
      Strategy: PlannerLogic.Strategy
      /// Real previews, produced by running the actual scheduler against each
      /// strategy's preferences. Empty until the user asks for them.
      Alternatives: PlannerLogic.Alternative list
      PreviewLoading: bool
      /// The session "Focus Current Session" highlighted, if any.
      Focused: SessionId option
      /// Content width, used only for layout decisions.
      Width: float }

type Msg =
    | Load
    | Loaded of PlannerInput * PlannedSession list * ActivityEntry list
    | Failed of exn
    | Generate
    | Generated of PlannerResult
    | ToggleSession of SessionId * bool
    | OpenAllToday
    | CloseAllToday
    | SessionToggled
    | SelectStrategy of PlannerLogic.Strategy
    | ApplyStrategy
    | BalanceWorkload
    | PreviewAlternatives
    | AlternativesReady of PlannerLogic.Alternative list
    | FocusCurrent
    | Resized of float

/// One command, three reads, no duplicates.
///
/// `BuildInput` is the same call the scheduler itself makes, so the preferences,
/// the open assignments and the busy blocks shown on screen are exactly the ones
/// the plan was built from — they cannot be from a different moment than the
/// sessions they describe. Batching them into a single async means one round of
/// work per load rather than several commands racing.
let private loadCmd (services: AppServices) =
    Elm.ofAsync
        (async {
            let now = services.Clock.Now
            let! input = services.Planner.BuildInput()
            let! sessions = services.Planner.GetSessions(now.Date, now.Date.AddDays 30.0)
            let! entries = services.Activity.GetRecent 60
            return input, sessions, PlannerLogic.history entries
        })
        Loaded
        Failed

let init (services: AppServices) : Model * Cmd<Msg> =
    { Sessions = []
      Unplanned = []
      Summary = None
      Prefs = PlannerPreferences.ofSettings UserSettings.defaults
      Tasks = []
      Busy = []
      History = []
      Generating = false
      Loading = true
      Error = None
      AllTodayOpen = false
      Strategy = PlannerLogic.Balanced
      Alternatives = []
      PreviewLoading = false
      Focused = None
      Width = 1400.0 },
    loadCmd services

let update (services: AppServices) (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | Load -> { model with Loading = true }, loadCmd services

    | Loaded(input, sessions, history) ->
        { model with
            Sessions = sessions
            Prefs = input.Prefs
            Tasks = input.Tasks
            Busy = input.Busy
            History = history
            Loading = false
            Error = None },
        // **Strategy previews are no longer something to ask for.** They used to
        // sit behind a "Preview all strategies" button, so the comparison the
        // section exists to offer was invisible until requested. Each preview is
        // a real scheduler run, so this is genuine work — but it is work the
        // screen is *for*, and running it on load is what makes the section
        // honest rather than an empty promise.
        Cmd.ofMsg PreviewAlternatives

    | Failed ex ->
        // `Generating` is cleared too: a failure during regeneration must not
        // leave the button stuck saying "Planning…". `PreviewLoading` likewise.
        { model with
            Loading = false
            Generating = false
            PreviewLoading = false
            Error = Some ex.Message },
        Cmd.none

    // The guard. A second Generate while one is running is dropped rather than
    // queued, so a double click cannot produce two plans. Every action that
    // writes a plan goes through the same flag.
    | Generate when model.Generating -> model, Cmd.none
    | Generate ->
        { model with Generating = true; Error = None },
        // Wrapped rather than `Elm.ofAsync (services.Planner.Regenerate()) …`:
        // an argument is evaluated where it is written, so the unwrapped form
        // touches the service during `update` instead of when the command runs.
        Elm.ofAsync (async { return! services.Planner.Regenerate() }) Generated Failed

    | ApplyStrategy when model.Generating -> model, Cmd.none
    | ApplyStrategy ->
        let strategy = model.Strategy

        { model with Generating = true; Error = None },
        Elm.ofAsync
            (async {
                return!
                    services.Planner.RegenerateWith(
                        PlannerLogic.Strategy.label strategy,
                        fun prefs -> PlannerLogic.Strategy.applyTo prefs strategy
                    )
            })
            Generated
            Failed

    | BalanceWorkload when model.Generating -> model, Cmd.none
    | BalanceWorkload ->
        // The cap is derived from the real outstanding work, not a preset —
        // see `PlannerLogic.balancedCap`.
        let cap = PlannerLogic.balancedCap model.Prefs model.Tasks

        { model with Generating = true; Error = None },
        Elm.ofAsync
            (async {
                return!
                    services.Planner.RegenerateWith(
                        "Balanced workload",
                        fun prefs -> { prefs with MaxDailyStudyMinutes = cap }
                    )
            })
            Generated
            Failed

    | Generated result ->
        // Reload rather than patching from the result: `Regenerate` writes the
        // sessions through the repository, so re-reading is the only way to show
        // what was actually persisted. Previews are dropped because they were
        // measured against a plan that no longer exists.
        { model with
            Generating = false
            Summary = Some result.Summary
            Unplanned = result.Unplanned
            Alternatives = [] },
        loadCmd services

    | PreviewAlternatives when model.PreviewLoading -> model, Cmd.none
    | PreviewAlternatives ->
        { model with PreviewLoading = true; Error = None },
        Elm.ofAsync
            (async {
                // Sequential, not parallel: each preview reads the repositories
                // through `BuildInput`, and one SQLite connection serves them.
                let! summaries =
                    PlannerLogic.Strategy.supported
                    |> List.map (fun strategy ->
                        async {
                            let! result =
                                services.Planner.PreviewWith(fun prefs ->
                                    PlannerLogic.Strategy.applyTo prefs strategy)

                            return PlannerLogic.summarise strategy result
                        })
                    |> Async.Sequential

                return List.ofArray summaries
            })
            AlternativesReady
            Failed

    | AlternativesReady alternatives ->
        { model with Alternatives = alternatives; PreviewLoading = false }, Cmd.none

    | OpenAllToday -> { model with AllTodayOpen = true }, Cmd.none
    | CloseAllToday -> { model with AllTodayOpen = false }, Cmd.none

    | ToggleSession(id, completed) ->
        let title =
            model.Sessions
            |> List.tryFind (fun s -> s.Id = id)
            |> Option.map (fun s -> s.Title)
            |> Option.defaultValue "Study session"

        model,
        Elm.ofAsync
            (async {
                do! services.Sessions.SetCompleted(id, completed)

                if completed then
                    do! services.Activity.Log(
                            // The session id travels with the entry, so a
                            // timeline entry says WHICH session was completed
                            // and its Open/History actions resolve.
                            ActivityEntry.create
                                services.Clock.Now
                                ActivityKind.SessionCompleted
                                title
                                None
                                (Some { Kind = EntityKind.Session; Id = id.Value })
                        )
            })
            (fun _ -> SessionToggled)
            Failed

    | SessionToggled -> model, loadCmd services

    // Choosing a strategy is a choice, not an instruction: nothing is
    // regenerated until the user applies it.
    | SelectStrategy strategy -> { model with Strategy = strategy }, Cmd.none

    | FocusCurrent ->
        { model with Focused = PlannerLogic.focusTarget DateTime.Now model.Sessions }, Cmd.none

    | Resized width -> { model with Width = width }, Cmd.none

// ---------------------------------------------------------------------------
// view
// ---------------------------------------------------------------------------

/// A status's colour. Always paired with `SessionStatus.label` at the call site:
/// status is never communicated by colour alone.
let private statusHex (tokens: Tokens) (status: PlannerLogic.SessionStatus) =
    match status with
    | PlannerLogic.Done -> Theme.successHex ()
    | PlannerLogic.InProgress -> Ui.hexOf tokens.Accent.Normal
    // A missed session is a real outcome, not a warning about an upcoming one:
    // it takes the danger colour, and its own word beside it.
    | PlannerLogic.Missed -> Theme.dangerHex ()
    | PlannerLogic.UpNext -> Theme.warningHex ()
    | PlannerLogic.Scheduled -> Theme.Identity.neutral

/// A module's identity colour, from the one shared identity family. Indexed by
/// the module's position in `PlannerLogic.moduleOrder`, so the workload bar and
/// the session dots always agree on what colour a module is.
let private moduleHex (index: int) =
    let palette =
        [ Theme.Identity.amber
          Theme.Identity.sky
          Theme.Identity.violet
          Theme.Identity.green
          Theme.Identity.rose
          Theme.Identity.teal
          Theme.Identity.orchid ]

    palette.[((index % palette.Length) + palette.Length) % palette.Length]

/// Ink for a risk value.
///
/// An unassessed risk takes the ordinary muted ink, not an identity marker
/// colour: markers are for dots and pills, and using one as text put the value
/// at 2.4:1 on a light card — below the AA floor the rest of this screen holds.
let private riskBrush (risk: PlannerLogic.Risk) =
    if not (PlannerLogic.Risk.isAssessed risk) then
        Theme.muted ()
    else
        Theme.brush (
            match risk with
            | PlannerLogic.High -> Theme.dangerHex ()
            | PlannerLogic.Medium -> Theme.warningHex ()
            | _ -> Theme.successHex ()
        )

/// Every Planner **content card** draws no specular highlight.
///
/// Named once, here, so no card can opt out by accident and so a test can state
/// the claim for the whole screen rather than for one card at a time. The
/// shell's floating sidebar is a different surface and keeps the full highlight;
/// that is approved and is not touched by this value.
let private contentHighlightScale = PlannerLogic.Geometry.contentHighlight

/// The shared warm card treatment — the same material role and the same geometry
/// tokens the Dashboard and Timeline use, not a Planner colour of its own.
let private card (tokens: Tokens) (padding: float) (children: IView list) : IView =
    GlassPanel.create
        [ GlassPanel.baseRadius DashboardLogic.Card.radius
          GlassPanel.elevation 1
          GlassPanel.highlightScale contentHighlightScale
          GlassPanel.tint (Theme.surfaces ()).Card
          GlassPanel.child (
              Border.create
                  [ Border.padding (Tokens.space tokens padding)
                    Border.verticalAlignment VerticalAlignment.Stretch
                    Border.child (
                        StackPanel.create
                            [ StackPanel.spacing (Tokens.space tokens 12.0)
                              StackPanel.verticalAlignment VerticalAlignment.Top
                              StackPanel.children children ]
                    ) ]
          ) ]
    :> IView

/// A card heading with a value on the right — the redesign's card header.
let private cardHeader (title: string) (right: string option) : IView =
    DockPanel.create
        [ DockPanel.children
              [ match right with
                | Some text ->
                    TextBlock.create
                        [ DockPanel.dock Dock.Right
                          TextBlock.text text
                          TextBlock.fontSize Theme.Type.rowMeta
                          TextBlock.foreground (Theme.faint ())
                          TextBlock.verticalAlignment VerticalAlignment.Center
                          TextBlock.margin (12.0, 0.0, 0.0, 0.0) ]
                | None -> ()
                Ui.sectionTitle title ] ]
    :> IView

/// A label/value row, used by every metric list on the page.
let private metricRow (label: string) (value: string) (valueBrush: IBrush) : IView =
    DockPanel.create
        [ DockPanel.margin (0.0, 5.0)
          DockPanel.children
              [ TextBlock.create
                    [ DockPanel.dock Dock.Right
                      TextBlock.text value
                      TextBlock.fontSize Theme.Type.rowMeta
                      TextBlock.fontWeight FontWeight.SemiBold
                      TextBlock.foreground valueBrush
                      TextBlock.margin (12.0, 0.0, 0.0, 0.0) ]
                TextBlock.create
                    [ TextBlock.text label
                      TextBlock.fontSize Theme.Type.rowMeta
                      TextBlock.foreground (Theme.muted ())
                      TextBlock.textTrimming TextTrimming.CharacterEllipsis ] ] ]
    :> IView

/// A glyph in a fixed column beside text that wraps in whatever width is left.
///
/// **This is the fix for the constraint text running outside its card.** A
/// horizontal `StackPanel` measures its children with infinite available width,
/// so a `TextBlock` inside one is told it has unlimited room and `TextWrapping`
/// never engages — the text simply runs on and is clipped by the card. A Grid
/// with an `Auto` glyph column and a `*` text column gives the text a real
/// finite width, so it wraps under itself instead. The glyph is top-aligned so
/// it stays on the first line of however many the text takes.
let private bulletRow (glyph: string) (glyphBrush: IBrush) (text: string) (textBrush: IBrush) : IView =
    Grid.create
        [ Grid.columnDefinitions PlannerLogic.Geometry.bulletColumns
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

/// A compact in-card empty state. Deliberately small: it has to sit inside a
/// card alongside other cards without pushing the page out of shape.
let private cardEmpty (icon: string) (text: string) : IView =
    Border.create
        [ Border.margin (0.0, 4.0, 0.0, 4.0)
          Border.child (bulletRow icon (Theme.faint ()) text (Theme.muted ())) ]
    :> IView

// ---- header ---------------------------------------------------------------

let private header (tokens: Tokens) (model: Model) : IView =
    StackPanel.create
        [ StackPanel.spacing 6.0
          StackPanel.horizontalAlignment HorizontalAlignment.Left
          StackPanel.maxWidth tokens.ReadingWidth
          StackPanel.children
              [ TextBlock.create
                    [ TextBlock.text "Planner"
                      TextBlock.fontSize Theme.Type.pageTitle
                      TextBlock.fontWeight FontWeight.Bold
                      TextBlock.foreground (Theme.text ()) ]
                TextBlock.create
                    [ TextBlock.text "What should I be doing, and why?"
                      TextBlock.fontSize Theme.Type.pageMeta
                      TextBlock.foreground (Theme.muted ())
                      TextBlock.textWrapping TextWrapping.Wrap ]
                match model.Summary with
                | Some summary ->
                    TextBlock.create
                        [ TextBlock.text summary
                          TextBlock.fontSize Theme.Type.pageLead
                          TextBlock.foreground (SolidColorBrush tokens.Accent.Light)
                          TextBlock.textWrapping TextWrapping.Wrap ]
                | None -> () ] ]
    :> IView

/// The redesign's full six-action row.
///
/// Every action is wired to real work — see `PlannerLogic.actions` for what each
/// one does and why it is disabled when it is. An action that cannot run is
/// disabled through `IsEnabled` (so it keeps its accessible name) and its reason
/// is printed underneath in visible text.
let private actionRow (tokens: Tokens) (now: DateTime) (model: Model) (dispatch: Msg -> unit) : IView =
    let states =
        PlannerLogic.actions
            { Generating = model.Generating
              Strategy = model.Strategy
              HasSessions = not model.Sessions.IsEmpty
              HasOutstandingWork = PlannerLogic.outstandingMinutes model.Tasks > 0
              HasFocusTarget = (PlannerLogic.focusTarget now model.Sessions).IsSome }

    let messageFor =
        function
        | PlannerLogic.RegeneratePlan -> Generate
        | PlannerLogic.OptimiseAgain -> ApplyStrategy
        | PlannerLogic.BalanceWorkload -> BalanceWorkload
        | PlannerLogic.FocusCurrentSession -> FocusCurrent
        | PlannerLogic.ViewAlternatives -> PreviewAlternatives

    /// A real Button whose visible surface is a GlassPanel, exactly as the
    /// The Planner's action button — geometry only.
    ///
    /// The glass, the veil, the accent floor and the ink rule live in
    /// `Ui.glassAction`. There were three copies of that button — here, in the
    /// Inbox and in Notes — and only Notes' copy carried
    /// `Glass.primaryTintAlpha`, which is why Regenerate Planner read as grey
    /// while every token feeding it was correct.
    let button (state: PlannerLogic.ActionState) : IView =
        Ui.glassAction
            tokens
            { Ui.GlassActionMetrics.Padding = (16.0, 9.0)
              Ui.GlassActionMetrics.Margin = (0.0, 0.0, 10.0, 10.0) }
            state.Label
            state.Primary
            state.Enabled
            state.Label
            (fun () -> dispatch (messageFor state.Id))

    let unavailable =
        states
        |> List.choose (fun s -> s.DisabledBecause |> Option.map (fun why -> s.Label, why))

    StackPanel.create
        [ StackPanel.spacing 4.0
          StackPanel.children
              [ // Wraps rather than compressing: at narrow widths the row
                // becomes two or three rows of full-size buttons.
                WrapPanel.create [ WrapPanel.children (states |> List.map button) ]

                if not unavailable.IsEmpty then
                    StackPanel.create
                        [ StackPanel.spacing 2.0
                          StackPanel.margin (2.0, 2.0, 0.0, 0.0)
                          StackPanel.children
                              [ Ui.sectionTitle "Unavailable right now"
                                for label, why in unavailable do
                                    // `Muted`, not `Faint`: this is a required
                                    // explanation, not incidental detail. An
                                    // instruction the user cannot comfortably
                                    // read is not a visible explanation.
                                    TextBlock.create
                                        [ TextBlock.text (sprintf "%s — %s" label why)
                                          TextBlock.fontSize Theme.Type.metricLabel
                                          TextBlock.foreground (Theme.muted ())
                                          TextBlock.textWrapping TextWrapping.Wrap ] ] ] ] ]
    :> IView

// ---- today's study plan ----------------------------------------------------

/// One session.
///
/// `now` is passed in rather than read here. Every row used to call
/// `DateTime.Now` for itself, so rows rendered either side of a second boundary
/// disagreed about the same instant — one row could show UP NEXT while the row
/// under it had already moved to IN PROGRESS, and a re-render could flip them
/// back. One clock reading per frame, taken in `view`, makes a frame internally
/// consistent.
let private sessionRow
    (tokens: Tokens)
    (now: DateTime)
    (model: Model)
    (dispatch: Msg -> unit)
    (order: string list)
    (s: PlannedSession)
    : IView =
    let status = PlannerLogic.statusOf now s
    let hex = statusHex tokens status
    let elapsed = PlannerLogic.elapsedPercent now s
    let index = PlannerLogic.moduleIndexOf order s.ModuleCode
    let isFocused = model.Focused = Some s.Id

    Border.create
        [ Border.padding (10.0, 8.0)
          Border.minHeight PlannerLogic.Geometry.sessionRowMinHeight
          Border.cornerRadius (Tokens.radius tokens 10.0)
          // "Focus Current Session" marks the row it acted on. A 1px accent
          // hairline, the same restrained treatment the Timeline's selection
          // uses — never a filled highlight.
          //
          // The thickness is ALWAYS 1: an unfocused row reserves the same border
          // with a transparent brush. Adding the border only when focused would
          // make the row two pixels taller the moment it was focused and push
          // every row under it down.
          Border.borderThickness 1.0
          Border.borderBrush (
              if isFocused then
                  Theme.brush (Theme.withAlphaHex "8C" (Ui.hexOf tokens.Accent.Normal))
              else
                  Theme.transparent
          )
          Border.child (
              StackPanel.create
                  [ StackPanel.spacing 8.0
                    StackPanel.children
                        [ // **The title row, as explicit columns.**
                          //
                          // Checkbox left, title flexible, status badge pinned to
                          // the far right. A horizontal StackPanel cannot do this:
                          // it measures children with infinite width, so a long
                          // title would never wrap and would push the badge out of
                          // the row instead. A `*` column constrains the title, so
                          // it wraps and the badge keeps its place.
                          Grid.create
                              [ Grid.columnDefinitions "Auto,10,Auto,10,*,10,Auto,10,Auto"
                                Grid.children
                                    [ // Completion. A real control — it writes
                                      // through to the repository — so it is
                                      // focusable, keyboard activatable and named.
                                      Border.create
                                          [ Grid.column 0
                                            Border.verticalAlignment VerticalAlignment.Center
                                            Border.child (
                                                Ui.checkGlyph s.Completed (fun () ->
                                                    dispatch (ToggleSession(s.Id, not s.Completed)))
                                            ) ]

                                      Border.create
                                          [ Grid.column 2
                                            Border.verticalAlignment VerticalAlignment.Center
                                            Border.child (Ui.colorDot (moduleHex index)) ]

                                      TextBlock.create
                                          [ Grid.column 4
                                            TextBlock.text s.Title
                                            TextBlock.fontSize Theme.Type.rowTitle
                                            TextBlock.fontWeight FontWeight.SemiBold
                                            TextBlock.foreground (Theme.text ())
                                            TextBlock.verticalAlignment VerticalAlignment.Center
                                            TextBlock.textWrapping TextWrapping.Wrap
                                            TextBlock.textDecorations (
                                                if s.Completed then TextDecorations.Strikethrough
                                                else null
                                            ) ]

                                      // Module code, and the linked assignment's
                                      // priority where one genuinely resolves.
                                      // Both are neutral-toned, so status keeps
                                      // the only coloured badge on the row.
                                      StackPanel.create
                                          [ Grid.column 6
                                            StackPanel.orientation Orientation.Horizontal
                                            StackPanel.spacing 6.0
                                            StackPanel.verticalAlignment VerticalAlignment.Center
                                            StackPanel.children
                                                [ match s.ModuleCode with
                                                  | Some code -> Ui.pill code (moduleHex index)
                                                  | None -> ()

                                                  // Absent, not defaulted, when the
                                                  // session has no resolvable
                                                  // assignment behind it.
                                                  match PlannerLogic.priorityOf model.Tasks s with
                                                  | Some priority ->
                                                      Ui.pill
                                                          (PlannerLogic.priorityLabel priority)
                                                          Theme.Identity.neutral
                                                  | None -> () ] ]

                                      // Status, at the far right. The reserved
                                      // minimum keeps the title column from
                                      // resizing when SCHEDULED becomes IN
                                      // PROGRESS; the badge itself still hugs its
                                      // own text, because `Ui.pill` is
                                      // content-sized and right-aligned here.
                                      Border.create
                                          [ Grid.column 8
                                            Border.minWidth PlannerLogic.Geometry.statusMinWidth
                                            Border.horizontalAlignment HorizontalAlignment.Right
                                            Border.verticalAlignment VerticalAlignment.Center
                                            Border.child (
                                                Ui.pill (PlannerLogic.SessionStatus.label status) hex
                                            ) ] ] ]

                          // Time, duration and effort, in reserved columns. As a
                          // horizontal StackPanel these shifted every time a
                          // duration went from "50m" to "1h 30m".
                          Grid.create
                              [ Grid.margin (18.0, 0.0, 0.0, 0.0)
                                Grid.columnDefinitions "Auto,16,Auto,16,*"
                                Grid.children
                                    [ TextBlock.create
                                          [ Grid.column 0
                                            TextBlock.text (Format.timeRange s.StartsAt s.EndsAt)
                                            TextBlock.minWidth PlannerLogic.Geometry.timeMinWidth
                                            TextBlock.fontSize Theme.Type.rowMeta
                                            TextBlock.foreground (Theme.muted ()) ]
                                      TextBlock.create
                                          [ Grid.column 2
                                            TextBlock.text (Format.duration (PlannedSession.durationMinutes s))
                                            TextBlock.minWidth PlannerLogic.Geometry.durationMinWidth
                                            TextBlock.fontSize Theme.Type.rowMeta
                                            TextBlock.foreground (Theme.faint ()) ]
                                      TextBlock.create
                                          [ Grid.column 4
                                            TextBlock.text (PlannerLogic.Effort.label (PlannerLogic.effortOf s))
                                            TextBlock.fontSize Theme.Type.rowMeta
                                            TextBlock.foreground (Theme.faint ())
                                            TextBlock.textTrimming TextTrimming.CharacterEllipsis ] ] ]

                          // Elapsed time, not an invented progress estimate.
                          //
                          // The strip is ALWAYS this tall. Previously the bar
                          // was omitted entirely at 0% and appeared the instant
                          // a session started, growing the row mid-session and
                          // pushing every row below it down — the single largest
                          // source of the jumping.
                          Border.create
                              [ Border.margin (18.0, 0.0, 0.0, 0.0)
                                Border.height PlannerLogic.Geometry.progressStripHeight
                                Border.maxWidth 260.0
                                Border.horizontalAlignment HorizontalAlignment.Left
                                Border.verticalAlignment VerticalAlignment.Center
                                Ui.accessibleName (
                                    if s.Completed then "Completed"
                                    elif elapsed > 0 then sprintf "%d per cent elapsed" elapsed
                                    else "Not started"
                                )
                                Border.child (
                                    if elapsed > 0 then
                                        Ui.progressBar elapsed hex
                                    else
                                        // Holds the space without drawing a bar.
                                        Border.create [ Border.height 6.0; Ui.decorative () ] :> IView
                                ) ]

                          // **The planner's own reasoning, always shown.**
                          //
                          // This used to sit behind an "Explain Plan" toggle that
                          // defaulted to off, so the scheduler's justification for
                          // placing a block was hidden until asked for — while the
                          // domain describes `PlannedSession.Reason` as "always
                          // shown to the user". These chips are that field, split
                          // on the separator `Planning.reasonFor` joins with, so
                          // they are real scheduler output and never authored copy.
                          // The only condition left is whether there is anything
                          // to say — never whether the reader asked.
                          if not (PlannerLogic.reasonChips s).IsEmpty then
                              WrapPanel.create
                                  [ WrapPanel.margin (18.0, 2.0, 0.0, 0.0)
                                    WrapPanel.children
                                        (PlannerLogic.reasonChips s
                                         |> List.map (fun chip ->
                                             Border.create
                                                 [ Border.margin (0.0, 0.0, 6.0, 6.0)
                                                   Border.background (Theme.cardAlt ())
                                                   Border.borderBrush (Theme.border ())
                                                   Border.borderThickness 1.0
                                                   Border.cornerRadius 999.0
                                                   Border.padding (10.0, 3.0)
                                                   Border.child (
                                                       TextBlock.create
                                                           [ TextBlock.text chip
                                                             TextBlock.fontSize Theme.Type.metricLabel
                                                             TextBlock.foreground (Theme.label ()) ]
                                                   ) ]
                                             :> IView)) ] ] ]
          ) ]
    :> IView

/// Rows with a hairline between them, never after the last one.
let private withDividers (rows: IView list) : IView list =
    rows
    |> List.mapi (fun i row -> if i = 0 then [ row ] else [ Ui.divider (); row ])
    |> List.concat

let private planCard (tokens: Tokens) (now: DateTime) (model: Model) (dispatch: Msg -> unit) : IView =
    let order = PlannerLogic.moduleOrder model.Sessions

    // The card shows a bounded preview, not the whole day. An unbounded list
    // pushed every card beneath it off the page on a busy day. `todayPreview`
    // keeps the EARLIEST sessions in time order, so what is dropped is always
    // the far end of the day rather than an arbitrary subset.
    let todays = PlannerLogic.todayPreview now model.Sessions
    let hiddenToday = PlannerLogic.todayHiddenCount now model.Sessions

    let later =
        PlannerLogic.days model.Sessions |> List.filter (fun d -> d.Date <> now.Date)

    card
        tokens
        22.0
        [ cardHeader "Today's study plan" (Some(Format.fullDate now))

          if model.Sessions.IsEmpty then
              // The empty state lives INSIDE the card. The card keeps its size
              // and its place in the grid, and every other card on the page
              // stays visible with its own empty state — the page structure
              // never collapses just because nothing is scheduled.
              StackPanel.create
                  [ StackPanel.spacing 10.0
                    StackPanel.minHeight 260.0
                    StackPanel.verticalAlignment VerticalAlignment.Center
                    StackPanel.children
                        [ Ui.emptyState
                              "◷"
                              (if model.Generating then "Building your plan…" else "No study sessions planned")
                              (if model.Generating then
                                   "Working through your assignments, deadlines and free time."
                               else
                                   "Add assignments with deadlines and estimated effort, then generate a plan. Every block will show why it was placed where it was.")
                          if not model.Generating then
                              StackPanel.create
                                  [ StackPanel.orientation Orientation.Horizontal
                                    StackPanel.horizontalAlignment HorizontalAlignment.Center
                                    StackPanel.children
                                        [ Ui.primaryButton "Generate a plan" (fun () -> dispatch Generate) ] ] ] ]
          else
              StackPanel.create
                  [ StackPanel.spacing 2.0
                    StackPanel.children
                        [ if todays.IsEmpty then
                              cardEmpty
                                  "○"
                                  "Nothing scheduled for today — the plan starts on a later day, below."
                          else
                              yield!
                                  withDividers (todays |> List.map (sessionRow tokens now model dispatch order))

                              // Only when the preview genuinely leaves something
                              // out. The count is the real number hidden, so the
                              // control never overstates what it will reveal.
                              // Only when the preview genuinely leaves something
                              // out. The count is the real hidden count, so the
                              // control can never overstate what it will reveal.
                              if hiddenToday > 0 then
                                  Border.create
                                      [ Border.margin (0.0, 10.0, 0.0, 0.0)
                                        Border.horizontalAlignment HorizontalAlignment.Left
                                        Border.child (
                                            Ui.namedSmallButton
                                                (sprintf "View all today's sessions (%d more)" hiddenToday)
                                                "View all of today's study sessions"
                                                (fun () -> dispatch OpenAllToday)
                                        ) ] ] ]

              // The rest of the plan. The redesign's card is today's sessions;
              // the application plans a whole horizon, and hiding the remaining
              // days to match a screenshot would delete real information.
              if not later.IsEmpty then
                  Ui.divider ()

                  StackPanel.create
                      [ StackPanel.spacing (Tokens.space tokens 14.0)
                        StackPanel.children
                            [ Ui.sectionTitle "Later in this plan"
                              for day in later do
                                  StackPanel.create
                                      [ StackPanel.spacing 6.0
                                        StackPanel.children
                                            [ StackPanel.create
                                                  [ StackPanel.orientation Orientation.Horizontal
                                                    StackPanel.spacing 8.0
                                                    StackPanel.children
                                                        [ TextBlock.create
                                                              [ TextBlock.text (
                                                                    (Format.dayLabel now day.Date)
                                                                        .ToUpperInvariant()
                                                                )
                                                                TextBlock.fontSize Theme.Type.cardLabel
                                                                TextBlock.fontWeight FontWeight.SemiBold
                                                                TextBlock.foreground (Theme.label ()) ]
                                                          TextBlock.create
                                                              [ TextBlock.text (
                                                                    sprintf
                                                                        "%s · %s planned"
                                                                        (day.Date.ToString "d MMMM")
                                                                        (Format.duration day.Minutes)
                                                                )
                                                                TextBlock.fontSize Theme.Type.cardLabel
                                                                TextBlock.foreground (Theme.faint ()) ] ] ]

                                              StackPanel.create
                                                  [ StackPanel.spacing 2.0
                                                    StackPanel.children
                                                        (withDividers (
                                                            day.Sessions
                                                            |> List.map (sessionRow tokens now model dispatch order)
                                                        )) ] ] ] ] ] ]

// ---- schedule optimisation -------------------------------------------------

let private optimisationCard (tokens: Tokens) (model: Model) : IView =
    let evaluation = PlannerLogic.evaluate model.Prefs model.Unplanned model.Sessions
    let checks = PlannerLogic.evaluatedChecks evaluation
    let score = PlannerLogic.scoreOf evaluation
    let risk = PlannerLogic.deadlineRisk model.Unplanned model.Sessions
    let coverage = PlannerLogic.coverage model.Tasks model.Sessions

    let rows = PlannerLogic.optimisation model.Prefs checks model.Sessions |> dict

    let notEvaluated = "Not evaluated"

    card
        tokens
        20.0
        [ cardHeader "Schedule optimisation" None

          StackPanel.create
              [ StackPanel.orientation Orientation.Horizontal
                StackPanel.spacing 14.0
                StackPanel.children
                    [ // An unevaluated plan shows a dial at zero with an em dash
                      // in it — never a percentage, and never a full ring.
                      Ui.ringGauge
                          64.0
                          (score |> Option.defaultValue 0)
                          tokens.Accent
                          (match score with
                           | Some value -> sprintf "%d%%" value
                           | None -> "—")
                          ""

                      StackPanel.create
                          [ StackPanel.spacing 2.0
                            StackPanel.verticalAlignment VerticalAlignment.Center
                            StackPanel.children
                                [ TextBlock.create
                                      [ TextBlock.text (
                                            match score with
                                            | Some _ -> "Overall planner score"
                                            | None -> "No plan generated"
                                        )
                                        TextBlock.fontSize Theme.Type.rowMeta
                                        TextBlock.fontWeight FontWeight.SemiBold
                                        TextBlock.foreground (Theme.text ())
                                        TextBlock.textWrapping TextWrapping.Wrap ]
                                  TextBlock.create
                                      [ TextBlock.text (
                                            match score with
                                            | Some _ -> "Share of your own planner constraints this plan honours"
                                            | None ->
                                                "An empty plan is not a satisfied plan — there is nothing to score yet"
                                        )
                                        TextBlock.fontSize Theme.Type.metricLabel
                                        TextBlock.foreground (Theme.faint ())
                                        TextBlock.textWrapping TextWrapping.Wrap ] ] ] ] ]

          Ui.divider ()

          StackPanel.create
              [ StackPanel.spacing 0.0
                StackPanel.children
                    [ metricRow
                          "Constraints satisfied"
                          (if checks.IsEmpty then notEvaluated else rows.["Constraints satisfied"])
                          (Theme.text ())

                      metricRow
                          "Available time used"
                          (if model.Sessions.IsEmpty then "0%" else rows.["Daily allowance used"])
                          (Theme.text ())

                      metricRow
                          "Sessions scheduled"
                          (string model.Sessions.Length)
                          (Theme.text ())

                      metricRow "Deadline risk" (PlannerLogic.Risk.label risk) (riskBrush risk)

                      // The redesign's "completion confidence". Nothing predicts
                      // completion, so the row carries what can actually be
                      // measured — and is named for what it measures.
                      metricRow
                          "Coursework covered"
                          (match coverage with
                           | Some percent -> sprintf "%d%%" percent
                           | None when model.Tasks.IsEmpty -> notEvaluated
                           | None -> "No outstanding work")
                          (Theme.text ()) ] ] ]

// ---- plan reasoning --------------------------------------------------------

let private reasoningCard (tokens: Tokens) (model: Model) : IView =
    let bullets = PlannerLogic.reasoningBullets model.Prefs model.Busy model.Sessions

    card
        tokens
        20.0
        [ // NOT "AI reasoning". This text is produced by `Planning.plan`'s own
          // rules; no AI provider is involved. Naming it after a model would
          // misdescribe the application to its user.
          cardHeader "Plan reasoning" None

          if bullets.IsEmpty then
              cardEmpty "✦" "Generate a plan to see why sessions were scheduled."
          else
              StackPanel.create
                  [ StackPanel.spacing 9.0
                    StackPanel.children
                        (bullets
                         |> List.map (fun text ->
                             bulletRow
                                 "✦"
                                 (SolidColorBrush tokens.Accent.Normal :> IBrush)
                                 text
                                 (Theme.textSecondary ()))) ]

          TextBlock.create
              [ TextBlock.text "Rules-based, produced by the scheduler itself — not model output."
                TextBlock.fontSize Theme.Type.metricLabel
                TextBlock.foreground (Theme.faint ())
                TextBlock.textWrapping TextWrapping.Wrap ] ]

// ---- workload distribution -------------------------------------------------

let private workloadCard (tokens: Tokens) (model: Model) : IView =
    let slices = PlannerLogic.workload model.Sessions
    let total = model.Sessions |> List.sumBy PlannedSession.durationMinutes
    let outstanding = PlannerLogic.outstandingMinutes model.Tasks

    card
        tokens
        20.0
        [ cardHeader "Workload distribution" None

          if slices.IsEmpty then
              cardEmpty
                  "○"
                  (if outstanding > 0 then
                       sprintf
                           "Nothing scheduled yet. %s of coursework is outstanding and will be split across your modules once you generate a plan."
                           (Format.duration outstanding)
                   else
                       "Nothing scheduled, and no outstanding coursework to split.")
          else
              // One bar, split by real minutes. Star columns rather than
              // measured widths, so the proportions are exact at any size.
              Border.create
                  [ Border.height 12.0
                    Border.cornerRadius 6.0
                    Border.clipToBounds true
                    Ui.decorative ()
                    Border.child (
                        Grid.create
                            [ Grid.columnDefinitions (
                                  slices |> List.map (fun s -> sprintf "%d*" (max 1 s.Minutes)) |> String.concat ","
                              )
                              Grid.children
                                  (slices
                                   |> List.mapi (fun i s ->
                                       Border.create [ Grid.column i; Border.background (Theme.brush (moduleHex i)) ]
                                       :> IView)) ]
                    ) ]

              StackPanel.create
                  [ StackPanel.spacing 6.0
                    StackPanel.children
                        (slices
                         |> List.mapi (fun i s ->
                             DockPanel.create
                                 [ DockPanel.children
                                       [ TextBlock.create
                                             [ DockPanel.dock Dock.Right
                                               TextBlock.text (
                                                   sprintf "%s · %d%%" (Format.duration s.Minutes) s.Percent
                                               )
                                               TextBlock.fontSize Theme.Type.rowMeta
                                               TextBlock.foreground (Theme.text ())
                                               TextBlock.margin (12.0, 0.0, 0.0, 0.0) ]
                                         StackPanel.create
                                             [ StackPanel.orientation Orientation.Horizontal
                                               StackPanel.spacing 7.0
                                               StackPanel.children
                                                   [ Ui.colorDot (moduleHex i)
                                                     TextBlock.create
                                                         [ TextBlock.text s.Label
                                                           TextBlock.fontSize Theme.Type.rowMeta
                                                           TextBlock.foreground (Theme.muted ())
                                                           TextBlock.verticalAlignment VerticalAlignment.Center ] ] ] ] ]
                             :> IView)) ]

          Ui.divider ()

          metricRow "Total planned" (Format.duration total) (Theme.text ())

          metricRow
              "Outstanding coursework"
              (if outstanding > 0 then Format.duration outstanding else "None")
              (Theme.text ()) ]

// ---- constraint validation -------------------------------------------------

let private constraintsCard (tokens: Tokens) (model: Model) : IView =
    let evaluation = PlannerLogic.evaluate model.Prefs model.Unplanned model.Sessions

    card
        tokens
        20.0
        [ cardHeader "Constraint validation" None

          match evaluation with
          | PlannerLogic.NotEvaluated ->
              // The rules are listed, but as PENDING. Showing them ticked
              // because no plan violates a rule it never touched would be the
              // most misleading thing on the page.
              cardEmpty "○" "Not evaluated — these are the constraints the next plan will be measured against."

              StackPanel.create
                  [ StackPanel.spacing 8.0
                    StackPanel.children
                        (PlannerLogic.constraintNames model.Prefs
                         |> List.map (fun name ->
                             bulletRow "○" (Theme.faint ()) (sprintf "%s — pending" name) (Theme.faint ()))) ]

          | PlannerLogic.Evaluated checks ->
              StackPanel.create
                  [ StackPanel.spacing 8.0
                    StackPanel.children
                        (checks
                         |> List.map (fun check ->
                             let hex = if check.Passed then Theme.successHex () else Theme.warningHex ()

                             // The longest of these — a busiest-day warning
                             // naming two durations — is what ran outside the
                             // card. It now wraps under itself.
                             bulletRow
                                 (if check.Passed then "✓" else "⚠")
                                 (Theme.brush hex)
                                 check.Text
                                 (if check.Passed then Theme.textSecondary () else Theme.brush hex))) ] ]

// ---- planner insights ------------------------------------------------------

let private insightsCard (tokens: Tokens) (now: DateTime) (model: Model) : IView =
    let items =
        PlannerLogic.observations now model.Prefs model.Unplanned model.Sessions

    card
        tokens
        20.0
        [ cardHeader "Planner insights" None

          if items.IsEmpty then
              cardEmpty "○" "Generate a plan to see how your week is shaped."
          else
              StackPanel.create
                  [ StackPanel.spacing 9.0
                    StackPanel.children
                        (items
                         |> List.map (fun text ->
                             bulletRow
                                 "•"
                                 (SolidColorBrush tokens.Accent.Normal :> IBrush)
                                 text
                                 (Theme.textSecondary ()))) ]

          if not model.Unplanned.IsEmpty then
              Ui.divider ()
              Ui.sectionTitle "Could not be fully scheduled"

              StackPanel.create
                  [ StackPanel.spacing 8.0
                    StackPanel.children
                        (model.Unplanned
                         |> List.map (fun u ->
                             StackPanel.create
                                 [ StackPanel.spacing 2.0
                                   StackPanel.children
                                       [ TextBlock.create
                                             [ TextBlock.text u.Task.Title
                                               TextBlock.fontSize Theme.Type.rowMeta
                                               TextBlock.fontWeight FontWeight.SemiBold
                                               TextBlock.foreground (Theme.warning ())
                                               TextBlock.textWrapping TextWrapping.Wrap ]
                                         TextBlock.create
                                             [ TextBlock.text u.Reason
                                               TextBlock.fontSize Theme.Type.metricLabel
                                               TextBlock.foreground (Theme.muted ())
                                               TextBlock.textWrapping TextWrapping.Wrap ] ] ]
                             :> IView)) ] ]

// ---- alternative plans -----------------------------------------------------

let private alternativesCard (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let selected = model.Strategy
    let columns = PlannerLogic.strategyColumns model.Width

    let previewOf strategy =
        model.Alternatives |> List.tryFind (fun a -> a.Strategy = strategy)

    /// One strategy control.
    ///
    /// Every dimension is identical in both states — the same minimum height,
    /// the same padding, the same corner radius, and a border of the same
    /// thickness whether or not it is selected. Selection changes colour only.
    ///
    /// The two things that vary in content each get a **reserved slot** of fixed
    /// height: the SELECTED badge, and the preview metrics line. Rendering them
    /// only when present is what made the selected control taller than its
    /// neighbours and made the whole row jump when the selection moved.
    let pill (strategy: PlannerLogic.Strategy) : IView =
        let isSelected = strategy = selected

        Button.create
            [ Button.horizontalAlignment HorizontalAlignment.Stretch
              Button.verticalAlignment VerticalAlignment.Stretch
              Button.horizontalContentAlignment HorizontalAlignment.Left
              Button.verticalContentAlignment VerticalAlignment.Top
              Button.padding (14.0, 12.0)
              Button.minHeight PlannerLogic.Geometry.strategyMinHeight
              Button.cornerRadius (Tokens.radius tokens 12.0)
              Button.background (if isSelected then Theme.accentSoft () else Theme.action ())
              // Reserved in both states — transparent when unselected rather
              // than absent, so the border never adds height on selection.
              Button.borderThickness PlannerLogic.Geometry.strategyBorderThickness
              Button.borderBrush (
                  if isSelected then SolidColorBrush tokens.Accent.Normal :> IBrush else Theme.border ()
              )
              Button.cursor Ui.hand
              Ui.accessibleName (
                  sprintf
                      "%s strategy. %s"
                      (PlannerLogic.Strategy.label strategy)
                      (if isSelected then "Selected." else "Not selected.")
              )
              Button.onClick ((fun _ -> dispatch (SelectStrategy strategy)), SubPatchOptions.Always)
              Button.content (
                  StackPanel.create
                      [ StackPanel.spacing 4.0
                        StackPanel.children
                            [ // Title, with the badge slot reserved beside it.
                              // `Auto` for the title, then the badge, then a star
                              // spacer that absorbs the slack. A star title
                              // column took all the remaining width and shoved
                              // the badge to the far edge of the control, a long
                              // way from the name it qualifies.
                              Grid.create
                                  [ Grid.columnDefinitions "Auto,7,Auto,*"
                                    Grid.height PlannerLogic.Geometry.strategyBadgeHeight
                                    Grid.children
                                        [ TextBlock.create
                                              [ Grid.column 0
                                                TextBlock.text (PlannerLogic.Strategy.label strategy)
                                                TextBlock.fontSize Theme.Type.rowMeta
                                                TextBlock.fontWeight FontWeight.SemiBold
                                                TextBlock.foreground (Theme.text ())
                                                TextBlock.verticalAlignment VerticalAlignment.Center
                                                TextBlock.textTrimming TextTrimming.CharacterEllipsis ]
                                          // Selection is stated in words as well
                                          // as in colour — and the slot exists
                                          // whether or not the word is in it.
                                          Border.create
                                              [ Grid.column 2
                                                Border.verticalAlignment VerticalAlignment.Center
                                                Border.child (
                                                    if isSelected then
                                                        Ui.pill "SELECTED" (Ui.hexOf tokens.Accent.Normal)
                                                    else
                                                        Border.create [ Ui.decorative () ] :> IView
                                                ) ] ] ]

                              // Reserved whether or not this strategy has been
                              // previewed, so previewing does not resize the row.
                              Border.create
                                  [ Border.height PlannerLogic.Geometry.strategyMetricsHeight
                                    Border.child (
                                        match previewOf strategy with
                                        | Some preview ->
                                            TextBlock.create
                                                [ TextBlock.text (
                                                      sprintf
                                                          "%d sessions · %s · %d day(s)%s"
                                                          preview.Sessions
                                                          (Format.duration preview.Minutes)
                                                          preview.Days
                                                          (if preview.Unplanned > 0 then
                                                               sprintf " · %d unplaced" preview.Unplanned
                                                           else
                                                               "")
                                                  )
                                                  TextBlock.fontSize Theme.Type.metricLabel
                                                  TextBlock.fontWeight FontWeight.SemiBold
                                                  TextBlock.foreground (SolidColorBrush tokens.Accent.Light)
                                                  TextBlock.verticalAlignment VerticalAlignment.Center
                                                  TextBlock.textTrimming TextTrimming.CharacterEllipsis ]
                                            :> IView
                                        | None -> Border.create [ Ui.decorative () ] :> IView
                                    ) ]

                              TextBlock.create
                                  [ TextBlock.text (PlannerLogic.Strategy.description strategy)
                                    TextBlock.fontSize Theme.Type.metricLabel
                                    TextBlock.foreground (Theme.faint ())
                                    TextBlock.textWrapping TextWrapping.Wrap ] ] ]
              ) ]
        :> IView

    let hasWork = PlannerLogic.outstandingMinutes model.Tasks > 0

    card
        tokens
        20.0
        [ cardHeader
              "Alternative plans"
              (Some(
                  if model.PreviewLoading then "Running the scheduler…"
                  elif model.Alternatives.IsEmpty then "Not previewed"
                  else "Previewed against your real coursework"
              ))

          Grid.create
              [ Grid.columnDefinitions (List.replicate columns "1*" |> String.concat ",10,")
                Grid.rowDefinitions (
                    List.replicate
                        ((PlannerLogic.Strategy.supported.Length + columns - 1) / columns)
                        "Auto"
                    |> String.concat ",10,"
                )
                Grid.children
                    (PlannerLogic.Strategy.supported
                     |> List.mapi (fun i strategy ->
                         Border.create
                             [ Grid.column ((i % columns) * 2)
                               Grid.row ((i / columns) * 2)
                               Border.child (pill strategy) ]
                         :> IView)) ]

          StackPanel.create
              [ StackPanel.orientation Orientation.Horizontal
                StackPanel.spacing 10.0
                StackPanel.children
                    [ // The "Preview all strategies" button is gone: previews now
                      // run on load, so there is nothing left to request. Only
                      // the real Apply action remains.
                      Button.create
                          [ Button.content (sprintf "Apply %s" (PlannerLogic.Strategy.label selected))
                            Button.isEnabled (hasWork && not model.Generating)
                            Button.fontSize Theme.Type.rowMeta
                            Button.fontWeight FontWeight.SemiBold
                            Button.cornerRadius (Tokens.radius tokens 11.0)
                            Button.padding (14.0, 9.0)
                            Button.background (
                                if hasWork && not model.Generating then
                                    SolidColorBrush tokens.Accent.Normal :> IBrush
                                else
                                    Theme.transparent
                            )
                            Button.foreground (
                                if hasWork && not model.Generating then Theme.onAccent () else Theme.faint ()
                            )
                            Button.borderThickness 1.0
                            Button.borderBrush Theme.transparent
                            Button.cursor Ui.hand
                            Ui.accessibleName (
                                sprintf
                                    "Regenerate and save the plan using the %s strategy"
                                    (PlannerLogic.Strategy.label selected)
                            )
                            Button.onClick ((fun _ -> dispatch ApplyStrategy), SubPatchOptions.Always) ] ] ]

          TextBlock.create
              [ TextBlock.text (
                    if hasWork then
                        "Previewing runs the real scheduler once per strategy and reports what it actually produced. Nothing is saved until you apply one. The redesign's fifth strategy, Revision Focused, is absent: the planner schedules assignments only, so it would produce an identical plan."
                    else
                        "Alternatives need outstanding coursework to schedule. The redesign's fifth strategy, Revision Focused, is absent: the planner schedules assignments only, so it would produce an identical plan."
                )
                TextBlock.fontSize Theme.Type.metricLabel
                TextBlock.foreground (Theme.faint ())
                TextBlock.textWrapping TextWrapping.Wrap ] ]

// ---- planner history -------------------------------------------------------

let private historyCard (tokens: Tokens) (now: DateTime) (model: Model) : IView =
    card
        tokens
        20.0
        [ cardHeader "Planner history" None

          if model.History.IsEmpty then
              cardEmpty "○" "Nothing has changed the plan yet. Generating one records it here."
          else
              StackPanel.create
                  [ StackPanel.spacing 10.0
                    StackPanel.children
                        (model.History
                         |> List.map (fun entry ->
                             DockPanel.create
                                 [ DockPanel.children
                                       [ TextBlock.create
                                             [ DockPanel.dock Dock.Right
                                               TextBlock.text (Format.relativeTime now entry.OccurredAt)
                                               TextBlock.fontSize Theme.Type.metricLabel
                                               TextBlock.foreground (Theme.faint ())
                                               TextBlock.margin (12.0, 0.0, 0.0, 0.0) ]
                                         StackPanel.create
                                             [ StackPanel.orientation Orientation.Horizontal
                                               StackPanel.spacing 9.0
                                               StackPanel.children
                                                   [ Ui.colorDot (TimelineView.kindHex entry.Kind)
                                                     StackPanel.create
                                                         [ StackPanel.spacing 1.0
                                                           StackPanel.children
                                                               [ TextBlock.create
                                                                     [ TextBlock.text entry.Title
                                                                       TextBlock.fontSize Theme.Type.rowMeta
                                                                       TextBlock.foreground (Theme.text ())
                                                                       TextBlock.textWrapping TextWrapping.Wrap ]
                                                                 TextBlock.create
                                                                     [ TextBlock.text (
                                                                           match entry.Detail with
                                                                           | Some detail ->
                                                                               sprintf
                                                                                   "%s · %s"
                                                                                   (ActivityKind.label entry.Kind)
                                                                                   (Format.truncate 70 detail)
                                                                           | None -> ActivityKind.label entry.Kind
                                                                       )
                                                                       TextBlock.fontSize Theme.Type.metricLabel
                                                                       TextBlock.foreground (Theme.faint ())
                                                                       TextBlock.textWrapping TextWrapping.Wrap ] ] ] ] ] ] ]
                             :> IView)) ] ]

// ---- upcoming outlook ------------------------------------------------------

let private outlookCard (tokens: Tokens) (now: DateTime) (model: Model) : IView =
    let rows =
        PlannerLogic.outlook now model.Tasks model.Unplanned model.Sessions

    let risk = PlannerLogic.deadlineRisk model.Unplanned model.Sessions

    card
        tokens
        20.0
        [ // The redesign's own title. Every row below is a deterministic
          // projection — a deadline or a plan already on disk, carried forward —
          // not a model output and not an estimate. The footnote says so, and a
          // row with no data states that rather than guessing.
          cardHeader "Future prediction" None

          StackPanel.create
              [ StackPanel.spacing 0.0
                StackPanel.children
                    (rows
                     |> List.map (fun row ->
                         metricRow
                             row.Label
                             row.Value
                             (if row.Label = "Deadline risk" then riskBrush risk else Theme.text ()))) ]

          TextBlock.create
              [ TextBlock.text
                    "A deterministic projection: your real deadlines and the plan already generated, carried forward. Nothing here is estimated or model-generated."
                TextBlock.fontSize Theme.Type.metricLabel
                TextBlock.foreground (Theme.faint ())
                TextBlock.textWrapping TextWrapping.Wrap ] ]

// ---- page ------------------------------------------------------------------

/// One logical row of cards, as ONE Grid.
///
/// **This is what makes cards in a row the same height.** A Grid row sizes to
/// its tallest child and every cell is stretched into it, so the cards' top and
/// bottom edges line up by construction. The previous arrangement grouped cards
/// into independent stacks whose heights merely happened to differ, which is
/// where the gaps came from.
///
/// `minHeight` is applied to the ROW, never to individual cards: a floor per
/// card would let one card in a row be taller than another for no reason, which
/// is the same defect in a different place.
///
/// Cards wrap into further Grids only when `columns` is fewer than the number of
/// cards — that is the medium-width reflow, and each wrapped line is still one
/// Grid of its own.
let private cardRow (spacing: float) (minHeight: float) (columns: int) (cards: IView list) : IView =
    StackPanel.create
        [ StackPanel.spacing spacing
          StackPanel.children
              (cards
               |> List.chunkBySize columns
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

/// The one place a `Card` becomes a rendered card.
///
/// Every row below is built by mapping this over `PlannerLogic`'s row lists, so
/// the page's composition really is the data those lists describe — reordering
/// or dropping a card means editing the list, which the layout tests assert.
let private renderCard
    (tokens: Tokens)
    (now: DateTime)
    (model: Model)
    (dispatch: Msg -> unit)
    (card: PlannerLogic.Card)
    : IView =
    match card with
    | PlannerLogic.TodayPlan -> planCard tokens now model dispatch
    | PlannerLogic.ScheduleOptimisation -> optimisationCard tokens model
    | PlannerLogic.PlanReasoning -> reasoningCard tokens model
    | PlannerLogic.WorkloadDistribution -> workloadCard tokens model
    | PlannerLogic.ConstraintValidation -> constraintsCard tokens model
    | PlannerLogic.PlannerInsights -> insightsCard tokens now model
    | PlannerLogic.AlternativePlans -> alternativesCard tokens model dispatch
    | PlannerLogic.PlannerHistory -> historyCard tokens now model
    | PlannerLogic.FuturePrediction -> outlookCard tokens now model

let view (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let spacing = Tokens.space tokens 22.0

    // ONE clock reading for the whole frame. Every card and every row derives
    // its status, elapsed share and relative timestamp from this single instant,
    // so nothing on the page can disagree with anything else about "now".
    let now = DateTime.Now
    let render = renderCard tokens now model dispatch

    // The main planning row.
    //
    // A Grid of TWO rows inside the right-hand cell, not a StackPanel: the
    // second row is star-sized, so Plan Reasoning grows to fill whatever height
    // the row has. A stack left the right column ending wherever its content
    // happened to end, which is exactly the blank void under it.
    let sideColumn: IView =
        Grid.create
            [ Grid.rowDefinitions (sprintf "Auto,%f,*" spacing)
              Grid.children
                  [ Border.create
                        [ Grid.row 0
                          Border.horizontalAlignment HorizontalAlignment.Stretch
                          Border.child (render PlannerLogic.ScheduleOptimisation) ]
                    Border.create
                        [ Grid.row 2
                          Border.verticalAlignment VerticalAlignment.Stretch
                          Border.horizontalAlignment HorizontalAlignment.Stretch
                          Border.child (render PlannerLogic.PlanReasoning) ] ] ]
        :> IView

    let mainRow: IView =
        if PlannerLogic.showsSideColumn model.Width then
            Grid.create
                // The redesign's split: roughly two thirds to the plan, one
                // third to the summary column. Star weights, so the ratio holds
                // at every width this row is shown at.
                [ Grid.minHeight PlannerLogic.Geometry.mainRowMinHeight
                  Grid.columnDefinitions (
                      sprintf
                          "%f*,%f,%f*"
                          PlannerLogic.Geometry.mainLeftWeight
                          spacing
                          PlannerLogic.Geometry.mainRightWeight
                  )
                  Grid.children
                      [ Border.create
                            [ Grid.column 0
                              Border.verticalAlignment VerticalAlignment.Stretch
                              Border.horizontalAlignment HorizontalAlignment.Stretch
                              Border.child (render PlannerLogic.TodayPlan) ]
                        Border.create
                            [ Grid.column 2
                              Border.verticalAlignment VerticalAlignment.Stretch
                              Border.horizontalAlignment HorizontalAlignment.Stretch
                              Border.child sideColumn ] ] ]
            :> IView
        else
            // Medium and narrow: the plan takes the full width and the summary
            // pair moves beneath it, in the same reading order.
            StackPanel.create
                [ StackPanel.spacing spacing
                  StackPanel.children
                      [ render PlannerLogic.TodayPlan
                        render PlannerLogic.ScheduleOptimisation
                        render PlannerLogic.PlanReasoning ] ]
            :> IView

    let body: IView =
        if model.Loading && model.Sessions.IsEmpty then
            // The loading state keeps the page's SHAPE, not just its colour.
            //
            // Two floating skeleton cards used to stand in for the whole page,
            // so arriving data replaced a short stack with a nine-card layout
            // and every heading on screen jumped. The skeletons now sit in the
            // same rows, at the same widths, as the cards that replace them.
            StackPanel.create
                [ StackPanel.spacing spacing
                  StackPanel.children
                      [ (if PlannerLogic.showsSideColumn model.Width then
                             Grid.create
                                 [ Grid.minHeight PlannerLogic.Geometry.mainRowMinHeight
                                   Grid.columnDefinitions (
                                       sprintf
                                           "%f*,%f,%f*"
                                           PlannerLogic.Geometry.mainLeftWeight
                                           spacing
                                           PlannerLogic.Geometry.mainRightWeight
                                   )
                                   Grid.children
                                       [ Border.create
                                             [ Grid.column 0
                                               Border.verticalAlignment VerticalAlignment.Stretch
                                               Border.child (Ui.skeletonCard 5) ]
                                         Border.create
                                             [ Grid.column 2
                                               Border.verticalAlignment VerticalAlignment.Stretch
                                               Border.child (
                                                   Grid.create
                                                       [ Grid.rowDefinitions (sprintf "Auto,%f,*" spacing)
                                                         Grid.children
                                                             [ Border.create
                                                                   [ Grid.row 0
                                                                     Border.child (Ui.skeletonCard 4) ]
                                                               Border.create
                                                                   [ Grid.row 2
                                                                     Border.verticalAlignment
                                                                         VerticalAlignment.Stretch
                                                                     Border.child (Ui.skeletonCard 3) ] ] ]
                                               ) ] ] ]
                             :> IView
                         else
                             StackPanel.create
                                 [ StackPanel.spacing spacing
                                   StackPanel.children [ Ui.skeletonCard 5; Ui.skeletonCard 4 ] ]
                             :> IView)

                        cardRow
                            spacing
                            PlannerLogic.Geometry.analysisRowMinHeight
                            (PlannerLogic.summaryColumns model.Width)
                            [ Ui.skeletonCard 3; Ui.skeletonCard 3; Ui.skeletonCard 3 ]

                        Ui.skeletonCard 2

                        cardRow
                            spacing
                            PlannerLogic.Geometry.bottomRowMinHeight
                            (PlannerLogic.bottomColumns model.Width)
                            [ Ui.skeletonCard 3; Ui.skeletonCard 3 ] ] ]
            :> IView
        else
            StackPanel.create
                [ StackPanel.spacing spacing
                  StackPanel.children
                      [ // Row 1 — plan | (optimisation over reasoning)
                        mainRow

                        // Row 2 — three equal cards, one shared height
                        cardRow
                            spacing
                            PlannerLogic.Geometry.analysisRowMinHeight
                            (PlannerLogic.summaryColumns model.Width)
                            (PlannerLogic.analysisRow |> List.map render)

                        // Row 3 — full content width
                        yield! (PlannerLogic.fullWidthRow |> List.map render)

                        // Row 4 — two equal cards, one shared height
                        cardRow
                            spacing
                            PlannerLogic.Geometry.bottomRowMinHeight
                            (PlannerLogic.bottomColumns model.Width)
                            (PlannerLogic.bottomRow |> List.map render) ] ]
            :> IView

    /// The page itself, bound rather than inlined so the root can hold exactly
    /// ONE child that layers it under the optional overlay. A `Border` takes a
    /// single child: a second `Border.child` does not stack, it replaces — which
    /// would blank the whole screen while still compiling.
    let pageContent: IView =
              Ui.scrollNoChrome (
                  StackPanel.create
                      [ StackPanel.spacing spacing
                        StackPanel.children
                            [ header tokens model

                              actionRow tokens now model dispatch

                              // A failure never blanks the page: the problem is
                              // stated, retry is one click away, and every card
                              // below keeps whatever it can still show.
                              match model.Error with
                              | Some error ->
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
                                                                  Ui.smallButton "Try again" (fun () -> dispatch Load)
                                                              ) ]
                                                        TextBlock.create
                                                            [ TextBlock.text error
                                                              TextBlock.fontSize Theme.Type.rowMeta
                                                              TextBlock.foreground (Theme.danger ())
                                                              TextBlock.verticalAlignment VerticalAlignment.Center
                                                              TextBlock.textWrapping TextWrapping.Wrap ] ] ]
                                        ) ]
                              | None -> ()

                              body ] ]
              )

    /// Every session today, in a bounded modal.
    ///
    /// It reuses `sessionRow` — the same renderer the inline preview uses — so
    /// the two can never drift into describing a session differently. Only the
    /// container differs.
    /// **Today in full, in the shared floating window.**
    ///
    /// This was a bespoke modal: its own scrim, its own Escape handler, its own
    /// ✕, its own geometry — and a scrim that swallowed every press with
    /// `Handled <- true`, so clicking outside it did nothing. The review asks
    /// for outside-click dismissal, and the way to get it everywhere at once is
    /// to stop writing one of these per screen.
    ///
    /// `FloatingWindow` brings the glass, the sibling scrim that makes
    /// outside-click work, Escape, the focus trap and the accessible name. The
    /// scroll here is deliberately the ORDINARY one, chrome and all: this is a
    /// dedicated full list rather than a summary card, so the bar belongs.
    let allTodayPanel: IView =
        let everySessionToday = PlannerLogic.today now model.Sessions
        let panelOrder = PlannerLogic.moduleOrder model.Sessions

        FloatingWindow.create
            tokens
            "Today's study sessions"
            (FloatingWindow.Geometry.widthFor 560.0 model.Width)
            (FloatingWindow.Geometry.heightFor 620.0 model.Width)
            // Nothing in this window is an editor, so there is never unsaved
            // work — every route out closes immediately, which is what
            // `outcomeOf` says for a clean window.
            (fun source ->
                match FloatingWindow.outcomeOf false source with
                | FloatingWindow.Close -> dispatch CloseAllToday
                | FloatingWindow.ConfirmDiscard -> ())
            (StackPanel.create
                [ StackPanel.spacing 2.0
                  StackPanel.children (
                      withDividers (everySessionToday |> List.map (sessionRow tokens now model dispatch panelOrder))
                  ) ])

    // ONE root child: a Grid holding the page, with the modal layered over it
    // when open. Both occupy the same cell, and the modal is declared second so
    // it paints on top.
    let layered: IView =
        Grid.create
            [ Grid.children
                  [ Border.create [ Border.child pageContent ]
                    if model.AllTodayOpen then
                        allTodayPanel ] ]
        :> IView

    Border.create
        [ Border.onSizeChanged (
              (fun args ->
                  // Only when a layout decision would actually change.
                  let decisions (width: float) =
                      PlannerLogic.showsSideColumn width,
                      PlannerLogic.summaryColumns width,
                      PlannerLogic.bottomColumns width,
                      PlannerLogic.strategyColumns width

                  if decisions model.Width <> decisions args.NewSize.Width then
                      dispatch (Resized args.NewSize.Width)),
              SubPatchOptions.Always
          )
          Border.child layered ]
    :> IView
