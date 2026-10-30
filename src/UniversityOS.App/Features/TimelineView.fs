module UniversityOS.App.Features.TimelineView

open System
open Elmish
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Controls

/// The academic timeline — everything that has happened, in one chronological
/// feed, with three presentations of the same real activity log.

type Model =
    { Entries: ActivityEntry list
      Category: TimelineLogic.Category
      Mode: TimelineLogic.ViewMode
      /// Free-text filter over the loaded entries.
      Query: string
      /// The entity whose history the Entity view shows, once one is chosen.
      FocusEntity: (EntityRef * string) option
      /// The selected Feed entry, by activity id.
      ///
      /// Transient by construction: it lives here and nowhere else, so there is
      /// no store it could be persisted to or restored from. It is a reading
      /// aid — somewhere to keep your place while scanning — and drives nothing
      /// destructive.
      Selected: Guid option
      /// This week's study, derived once from one session read and shared by the
      /// "Studied this week" tile and the Recent Focus card.
      Study: StudySummary
      Loading: bool
      Error: string option
      /// Content width, used only for layout decisions.
      Width: float }

type External = Goto of Page

type Msg =
    | Load
    | Loaded of ActivityEntry list * StudySummary
    | Failed of exn
    | SetCategory of TimelineLogic.Category
    | SetMode of TimelineLogic.ViewMode
    | SetQuery of string
    | FocusOn of EntityRef * string
    | ClearFocus
    | OpenEntry of ActivityEntry
    | Resized of float
    /// Clicking an entry. Toggles: the selected one clears, another replaces.
    | SelectEntry of ActivityEntry
    /// Clicking the Feed's background, or pressing Escape. An explicit message
    /// rather than a bubbled pointer event, so "clear" is something the update
    /// can be asked for and asserted on.
    | ClearSelection

/// One read of the log plus one read of this week's sessions.
///
/// Both on load, together, so switching view, filter or search never re-queries
/// — and so the "Studied this week" tile and Recent Focus describe the same
/// moment. The sessions are summarised here and the raw list is dropped: the
/// view needs the five figures, not the records.
let private loadCmd (services: AppServices) =
    Elm.ofAsync
        (async {
            let! entries = services.Activity.GetRecent 300
            let now = services.Clock.Now

            // The user's own calendar week: Monday under en-GB, Sunday under
            // en-US. Read from the culture rather than hard-coded.
            let firstDay = Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek
            let weekStart = StudyTime.weekStart firstDay now
            let! sessions = services.Sessions.GetBetween(weekStart, weekStart.AddDays 7.0)
            return entries, StudyTime.summarise firstDay now sessions
        })
        Loaded
        Failed

let init (services: AppServices) : Model * Cmd<Msg> =
    { Entries = []
      Category = TimelineLogic.All
      Mode = TimelineLogic.Feed
      Query = ""
      FocusEntity = None
      Selected = None
      Study = StudyTime.emptySummary
      Loading = true
      Error = None
      Width = 1400.0 },
    loadCmd services

/// The one rule that keeps a selection valid.
///
/// Applied wherever the visible set or the mode can change, rather than
/// remembering to clear at each of the six call sites that can invalidate it: a
/// selection survives only if the mode allows selecting AND the entry is still
/// on screen. Switching to Journey, filtering it away, searching past it and
/// reloading without it all fall out of that one condition.
let private reconcileSelection (model: Model) =
    let visible = TimelineLogic.visibleEntries model.Category model.Query model.Entries
    { model with Selected = TimelineLogic.retainedSelection model.Mode visible model.Selected }

let update (services: AppServices) (msg: Msg) (model: Model) : Model * Cmd<Msg> * External option =
    match msg with
    | Load -> { model with Loading = true }, loadCmd services, None

    | Loaded(entries, study) ->
        // A reload can remove the selected entry — reconcile rather than
        // trusting that it survived.
        { model with
            Entries = entries
            Study = study
            Loading = false
            Error = None }
        |> reconcileSelection,
        Cmd.none,
        None

    | Failed ex -> { model with Loading = false; Error = Some ex.Message }, Cmd.none, None

    | SetCategory category -> { model with Category = category } |> reconcileSelection, Cmd.none, None
    | SetMode mode -> { model with Mode = mode } |> reconcileSelection, Cmd.none, None
    | SetQuery query -> { model with Query = query } |> reconcileSelection, Cmd.none, None

    | FocusOn(entity, title) ->
        // Choosing an entry's subject switches to its history — the redesign's
        // Entity History view, on real linked data.
        { model with FocusEntity = Some(entity, title); Mode = TimelineLogic.Entity }
        |> reconcileSelection,
        Cmd.none,
        None

    | ClearFocus ->
        { model with FocusEntity = None; Mode = TimelineLogic.Feed } |> reconcileSelection, Cmd.none, None

    | OpenEntry entry ->
        match entry.Entity with
        | Some entity -> model, Cmd.none, Some(Goto(Page.forEntity entity.Kind))
        | None -> model, Cmd.none, None

    | Resized width -> { model with Width = width }, Cmd.none, None

    // Toggling: clicking the selected entry again clears it, clicking another
    // moves the selection. Outside the Feed there is no selection to change.
    | SelectEntry _ when not (TimelineLogic.allowsSelection model.Mode) -> model, Cmd.none, None
    | SelectEntry entry ->
        { model with Selected = TimelineLogic.toggledSelection model.Selected entry.Id }, Cmd.none, None

    | ClearSelection -> { model with Selected = None }, Cmd.none, None

// ---------------------------------------------------------------------------
// view
// ---------------------------------------------------------------------------

/// Identity colour per activity kind. These are semantic categories with fixed
/// meanings, so they are not accent-derived — they come from the one shared
/// identity family in `Theme.Identity`, which every entity and event kind also
/// draws from, so the same thing is the same colour wherever it appears.
let kindHex (kind: ActivityKind) =
    match kind with
    | ActivityKind.AssignmentCreated
    | ActivityKind.AssignmentUpdated -> Theme.Identity.amber
    | ActivityKind.DeadlineChanged -> Theme.Identity.terracotta
    | ActivityKind.NoteCreated
    | ActivityKind.NoteUpdated -> Theme.Identity.green
    | ActivityKind.DocumentImported -> Theme.Identity.orchid
    | ActivityKind.LectureAdded -> Theme.Identity.teal
    | ActivityKind.FlashcardsGenerated -> Theme.Identity.rose
    | ActivityKind.SessionCompleted
    | ActivityKind.PlanGenerated
    | ActivityKind.AiAction -> Theme.Identity.violet
    | ActivityKind.CaptureApplied
    | ActivityKind.EventAdded
    | ActivityKind.ModuleUpdated -> Theme.Identity.sky
    | ActivityKind.ConnectorSynced -> Theme.Identity.neutral

let private kindGlyph (kind: ActivityKind) =
    match kind with
    | ActivityKind.AssignmentCreated
    | ActivityKind.AssignmentUpdated -> "☑"
    | ActivityKind.DeadlineChanged -> "⏱"
    | ActivityKind.NoteCreated
    | ActivityKind.NoteUpdated -> "▤"
    | ActivityKind.DocumentImported -> "▧"
    | ActivityKind.LectureAdded -> "▶"
    | ActivityKind.FlashcardsGenerated -> "◈"
    | ActivityKind.SessionCompleted -> "◔"
    | ActivityKind.PlanGenerated -> "⟳"
    | ActivityKind.CaptureApplied -> "＋"
    | ActivityKind.ConnectorSynced -> "⇄"
    | ActivityKind.EventAdded -> "▦"
    | ActivityKind.AiAction -> "✦"
    | ActivityKind.ModuleUpdated -> "◫"

/// The shared warm card treatment: the same geometry tokens and the same
/// material role the Dashboard uses, not a Timeline colour of its own.
let private card (tokens: Tokens) (padding: float) (children: IView list) : IView =
    GlassPanel.create
        [ GlassPanel.baseRadius DashboardLogic.Card.radius
          GlassPanel.elevation 1
          GlassPanel.highlightScale DashboardLogic.Card.highlightScale
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

// ---- header ---------------------------------------------------------------

let private modeButton (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) (mode: TimelineLogic.ViewMode) : IView =
    let selected = model.Mode = mode

    Button.create
        [ Button.content (TimelineLogic.ViewMode.label mode)
          Button.fontSize Theme.Type.rowMeta
          Button.fontWeight (if selected then FontWeight.SemiBold else FontWeight.Normal)
          Button.background (
              if selected then SolidColorBrush tokens.Accent.Normal :> IBrush else Theme.transparent
          )
          Button.foreground (if selected then Theme.onAccent () else Theme.muted ())
          Button.borderThickness 1.0
          Button.borderBrush (if selected then Theme.transparent else Theme.border ())
          Button.cornerRadius (Tokens.radius tokens 9.0)
          Button.padding (14.0, 7.0)
          Button.cursor Ui.hand
          Button.onClick ((fun _ -> dispatch (SetMode mode)), SubPatchOptions.Always) ]
    :> IView

let private header (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    DockPanel.create
        [ DockPanel.children
              [ StackPanel.create
                    [ DockPanel.dock Dock.Right
                      StackPanel.orientation Orientation.Horizontal
                      StackPanel.spacing 8.0
                      StackPanel.verticalAlignment VerticalAlignment.Center
                      StackPanel.margin (24.0, 0.0, 0.0, 0.0)
                      StackPanel.children
                          (TimelineLogic.ViewMode.all |> List.map (modeButton tokens model dispatch)) ]

                StackPanel.create
                    [ StackPanel.spacing 6.0
                      StackPanel.horizontalAlignment HorizontalAlignment.Left
                      StackPanel.verticalAlignment VerticalAlignment.Center
                      StackPanel.maxWidth tokens.ReadingWidth
                      StackPanel.children
                          [ TextBlock.create
                                [ TextBlock.text "Timeline"
                                  TextBlock.fontSize Theme.Type.pageTitle
                                  TextBlock.fontWeight FontWeight.Bold
                                  TextBlock.foreground (Theme.text ()) ]
                            TextBlock.create
                                [ TextBlock.text "What has happened throughout your academic journey"
                                  TextBlock.fontSize Theme.Type.pageMeta
                                  TextBlock.foreground (Theme.muted ())
                                  TextBlock.textWrapping TextWrapping.Wrap ] ] ] ] ]
    :> IView

/// The redesign's search bar. A real substring filter over the loaded entries —
/// the prompt is phrased plainly rather than promising natural language the
/// activity log cannot answer.
let private searchBar (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    Border.create
        [ Border.background (Theme.brush (Theme.withAlphaHex "0C" (Ui.hexOf tokens.Accent.Normal)))
          Border.borderBrush (Theme.brush (Theme.withAlphaHex "1C" (Ui.hexOf tokens.Accent.Normal)))
          Border.borderThickness 1.0
          Border.cornerRadius (Tokens.radius tokens 12.0)
          Border.padding (14.0, 4.0)
          Border.child (
              DockPanel.create
                  [ DockPanel.children
                        [ TextBlock.create
                              [ DockPanel.dock Dock.Left
                                TextBlock.text "⌕"
                                TextBlock.fontSize 16.0
                                TextBlock.margin (0.0, 0.0, 10.0, 0.0)
                                TextBlock.foreground (Theme.muted ())
                                TextBlock.verticalAlignment VerticalAlignment.Center ]
                          TextBox.create
                              [ TextBox.text model.Query
                                TextBox.watermark "Search the timeline — a title, a module, a kind of event"
                                TextBox.fontSize Theme.Type.cardBody
                                TextBox.background Theme.transparent
                                TextBox.borderThickness 0.0
                                TextBox.padding (0.0, 8.0)
                                Ui.accessibleName "Search the timeline"
                                TextBox.onTextChanged ((SetQuery >> dispatch), SubPatchOptions.Always) ] ] ]
          ) ]
    :> IView

let private statsRow (tokens: Tokens) (model: Model) : IView =
    let tiles = TimelineLogic.stats DateTime.Now model.Study.MinutesThisWeek model.Entries
    let perRow = TimelineLogic.statsPerRow model.Width
    let gap = 12.0
    let columnDefs = List.replicate perRow "1*" |> String.concat (sprintf ",%f," gap)

    StackPanel.create
        [ StackPanel.children
              (tiles
               |> List.chunkBySize perRow
               |> List.map (fun chunk ->
                   Grid.create
                       [ Grid.columnDefinitions columnDefs
                         Grid.margin (0.0, 0.0, 0.0, gap)
                         Grid.children
                             (chunk
                              |> List.mapi (fun i (value, label) ->
                                  Border.create
                                      [ Grid.column (i * 2)
                                        Border.verticalAlignment VerticalAlignment.Stretch
                                        Border.child (card tokens 16.0 [ Ui.statTile value label ]) ]
                                  :> IView)) ]
                   :> IView)) ]
    :> IView

let private filterChips (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    WrapPanel.create
        [ WrapPanel.children
              (TimelineLogic.Category.all
               |> List.map (fun category ->
                   let selected = model.Category = category

                   Border.create
                       [ Border.margin (0.0, 0.0, 8.0, 8.0)
                         Border.child (
                             Button.create
                                 [ Button.content (TimelineLogic.Category.label category)
                                   Button.fontSize Theme.Type.rowMeta
                                   Button.cornerRadius 999.0
                                   Button.padding (14.0, 6.0)
                                   Button.cursor Ui.hand
                                   Button.background (
                                       if selected then
                                           Theme.brush (Theme.withAlphaHex "1F" (Ui.hexOf tokens.Accent.Normal))
                                       else
                                           Theme.transparent
                                   )
                                   Button.foreground (
                                       if selected then
                                           SolidColorBrush tokens.Accent.Light :> IBrush
                                       else
                                           Theme.muted ()
                                   )
                                   Button.borderThickness 1.0
                                   Button.borderBrush (
                                       if selected then
                                           Theme.brush (Theme.withAlphaHex "59" (Ui.hexOf tokens.Accent.Normal))
                                       else
                                           Theme.border ()
                                   )
                                   Button.onClick ((fun _ -> dispatch (SetCategory category)), SubPatchOptions.Always) ]
                         ) ]
                   :> IView)) ]
    :> IView

// ---- feed -----------------------------------------------------------------

let private entryCard (tokens: Tokens) (selected: bool) (dispatch: Msg -> unit) (entry: ActivityEntry) : IView =
    let hex = kindHex entry.Kind

    // Selection wrapper.
    //
    // Lightweight on purpose: selection is a place-keeper while you scan, not a
    // pending action. A hairline in the user's accent at reduced alpha, plus a
    // word in the accessible name — a colour must never be the only signal, but
    // with nothing destructive attached it does not need a badge either.
    let selectionRing (content: IView) : IView =
        Border.create
            [ Border.cornerRadius (Tokens.radius tokens (DashboardLogic.Card.radius + 2.0))
              Border.borderThickness 1.0
              Border.padding 2.0
              Border.borderBrush (
                  if selected then SolidColorBrush tokens.Accent.B55 :> IBrush else Theme.transparent
              )
              // Selecting must not move the card, so the ring's space is always
              // reserved — transparent when unselected rather than absent.
              Border.background Theme.transparent
              Border.cursor Ui.hand
              Ui.accessibleName (
                  sprintf "%s. %s" entry.Title (if selected then "Selected" else "Not selected")
              )
              // The card is a click target **except where a real control owns the
              // click**, which is decided from the event's SOURCE.
              //
              // Two earlier attempts got this wrong by reasoning about ordering
              // instead. Marking the press handled inside the child button was
              // supposed to stop it reaching this handler; a user reported that
              // pressing Open still selected the row, so it does not — whether
              // because this handler sees the press first, or because the child's
              // flag is not consulted, the ordering argument was never sound.
              //
              // Asking *what was pressed* avoids the question entirely: a press
              // that started inside a Button is that Button's, no matter which
              // phase this handler runs in or what any flag says.
              //
              // `Ui.isInteractiveSource` walks the visual — then logical — tree
              // from the source looking for a control that owns its click. The
              // Feed's background handler below already uses it for exactly this
              // reason; this handler simply never did.
              //
              // Handled is still marked for a genuine row press, so the Feed's
              // background handler cannot clear the selection the same click just
              // made. For a press that belongs to a button, nothing is dispatched
              // and nothing is marked — the event is left entirely alone so the
              // Button's own Click can complete.
              Border.onPointerPressed (
                  (fun args ->
                      if not (Ui.isInteractiveSource args.Source) then
                          dispatch (SelectEntry entry)
                          args.Handled <- true),
                  SubPatchOptions.Always
              )
              Border.child content ]
        :> IView

    selectionRing (
        card
            tokens
            16.0
            [ DockPanel.create
                  [ DockPanel.children
                        [ // Icon badge — also the keyboard route to selection.
                          // A real Button, so Tab reaches it and Enter/Space
                          // activate it; the glyph alone would be nameless.
                      Button.create
                          [ DockPanel.dock Dock.Left
                            Button.width 38.0
                            Button.height 38.0
                            Button.padding 0.0
                            Button.borderThickness 0.0
                            Button.cornerRadius (Tokens.radius tokens 10.0)
                            Button.margin (0.0, 0.0, 14.0, 0.0)
                            Button.verticalAlignment VerticalAlignment.Top
                            Button.cursor Ui.hand
                            Button.background (Theme.brush (Theme.withAlphaHex "1F" hex))
                            Ui.accessibleName (
                                sprintf
                                    "%s. %s. %s"
                                    entry.Title
                                    (ActivityKind.label entry.Kind)
                                    (if selected then "Selected" else "Select this entry")
                            )
                            Button.onClick ((fun _ -> dispatch (SelectEntry entry)), SubPatchOptions.Always)
                            Button.content (
                                TextBlock.create
                                    [ TextBlock.text (kindGlyph entry.Kind)
                                      TextBlock.fontSize 15.0
                                      TextBlock.foreground (Theme.brush hex)
                                      TextBlock.horizontalAlignment HorizontalAlignment.Center
                                      TextBlock.verticalAlignment VerticalAlignment.Center ]
                            ) ]

                      StackPanel.create
                          [ StackPanel.spacing 5.0
                            StackPanel.children
                                [ DockPanel.create
                                      [ DockPanel.children
                                            [ TextBlock.create
                                                  [ DockPanel.dock Dock.Right
                                                    TextBlock.text (Format.time entry.OccurredAt)
                                                    TextBlock.fontSize Theme.Type.rowMeta
                                                    TextBlock.foreground (Theme.faint ())
                                                    TextBlock.margin (12.0, 0.0, 0.0, 0.0)
                                                    TextBlock.verticalAlignment VerticalAlignment.Center ]
                                              StackPanel.create
                                                  [ StackPanel.orientation Orientation.Horizontal
                                                    StackPanel.spacing 8.0
                                                    StackPanel.children
                                                        [ TextBlock.create
                                                              [ TextBlock.text entry.Title
                                                                TextBlock.fontSize Theme.Type.rowTitle
                                                                TextBlock.fontWeight FontWeight.SemiBold
                                                                TextBlock.foreground (Theme.text ())
                                                                TextBlock.textWrapping TextWrapping.Wrap ]
                                                          Ui.pill (ActivityKind.label entry.Kind) hex ] ] ] ]

                                  match entry.Detail with
                                  | Some detail ->
                                      TextBlock.create
                                          [ TextBlock.text (Format.truncate 180 detail)
                                            TextBlock.fontSize Theme.Type.cardBody
                                            TextBlock.foreground (Theme.muted ())
                                            TextBlock.textWrapping TextWrapping.Wrap ]
                                  | None -> ()

                                  match entry.Entity with
                                  | Some entity ->
                                      StackPanel.create
                                          [ StackPanel.orientation Orientation.Horizontal
                                            StackPanel.spacing 8.0
                                            StackPanel.margin (0.0, 3.0, 0.0, 0.0)
                                            StackPanel.children
                                                // `rowActionButton`, not `smallButton`: these sit
                                                // inside a row that selects on PointerPressed, and
                                                // the press arrives before the click. See the
                                                // comment on the row handler below.
                                                [ Ui.rowActionButton "Open →" (fun () -> dispatch (OpenEntry entry))
                                                  Ui.rowActionButton "History" (fun () ->
                                                      dispatch (FocusOn(entity, entry.Title))) ] ]
                                  | None -> () ] ] ] ] ]
    )

let private feedView (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) (entries: ActivityEntry list) : IView =
    let selected = TimelineLogic.retainedSelection model.Mode entries model.Selected
    let groups = TimelineLogic.grouped DateTime.Now entries

    // Background clearing is NOT handled here. It lives on the page root, so
    // the whole Timeline surface clears — see `view` at the bottom of this file.
    // A handler on this container would only cover the feed column, which is
    // exactly the defect being fixed.
    Border.create
        [ Border.child (
              StackPanel.create
                  [ StackPanel.spacing (Tokens.space tokens 22.0)
                    StackPanel.children
                        (groups
               |> List.map (fun (period, items) ->
                   StackPanel.create
                       [ StackPanel.spacing (Tokens.space tokens 10.0)
                         StackPanel.children
                             [ yield
                                   // Uppercase in the VIEW, not in the label
                                   // itself: `Period.label` is the semantic
                                   // name, asserted in tests and read by
                                   // assistive technology as ordinary words.
                                   TextBlock.create
                                       [ TextBlock.text ((TimelineLogic.Period.label period).ToUpperInvariant())
                                         TextBlock.fontSize Theme.Type.cardLabel
                                         TextBlock.fontWeight FontWeight.SemiBold
                                         TextBlock.foreground (Theme.label ()) ]
                               yield!
                                   items
                                   |> List.map (fun e -> entryCard tokens (selected = Some e.Id) dispatch e) ] ]
                         :> IView)) ]
          ) ]
    :> IView

/// A vertical stepper over the same entries — the redesign's Academic Journey.
///
/// **No vertical offset is computed anywhere in here.** That is the whole point
/// of this structure, and the reason the previous two attempts looked wrong.
///
/// Each entry is one Grid:
///
/// ```
///   columns:  markerWidth │ contentGap │ *
///   rows:     Auto  ← title row: the node and the title line
///             Auto  ← metadata row, plus the gap to the next entry
/// ```
///
/// The title row's marker cell holds its own three-row Grid — `*, Auto, *` —
/// with the node in the middle row. Avalonia measures the title row from the
/// title `TextBlock`, and the node is `VerticalAlignment.Center` in that same
/// row, so **the node's centre is the title's centre by construction**: no
/// assumed line height, no `nodeTopOffset`, nothing that can be off by two
/// pixels when the real font metrics differ from a guess.
///
/// The two star rows above and below the node take whatever vertical space is
/// left in the title row, so the segments run edge-to-edge and meet the node
/// exactly at its top and bottom centre. The metadata row's marker cell holds
/// one more segment spanning the metadata and the inter-entry gap, so the line
/// is continuous from one node to the next with no boundary to fall through.
///
/// A wrapped title grows the title row and takes the node down with it, which
/// is right — the node tracks its title. Wrapped *metadata* grows only the
/// second row, which the node is not in, so it cannot drag the node away.
let private stepperView (tokens: Tokens) (entries: ActivityEntry list) : IView =
    let now = DateTime.Now
    let count = entries.Length
    let size = TimelineLogic.JourneyLayout.nodeSize
    let gap = Tokens.space tokens TimelineLogic.JourneyLayout.rowGap

    /// One segment of the line. Centred in the marker column and stretched to
    /// its cell, so it never needs a margin or a height.
    let connector (visible: bool) : IView =
        Border.create
            [ Border.width TimelineLogic.JourneyLayout.connectorWidth
              Border.horizontalAlignment HorizontalAlignment.Center
              Border.verticalAlignment VerticalAlignment.Stretch
              Border.isVisible visible
              Border.background (Theme.brush (Theme.withAlphaHex "26" (Ui.hexOf tokens.Accent.Normal)))
              Ui.decorative () ]
        :> IView

    StackPanel.create
        [ StackPanel.spacing 0.0
          StackPanel.children
              (entries
               |> List.mapi (fun index entry ->
                   let isLatest = index = 0
                   let above = TimelineLogic.JourneyLayout.hasConnectorAbove index
                   let below = TimelineLogic.JourneyLayout.hasConnectorBelow index count

                   Grid.create
                       [ Grid.columnDefinitions (
                             sprintf
                                 "%f,%f,*"
                                 TimelineLogic.JourneyLayout.markerWidth
                                 TimelineLogic.JourneyLayout.contentGap
                         )
                         Grid.rowDefinitions "Auto,Auto"
                         Grid.children
                             [ // --- title row, marker column ---------------
                               // Its own Grid so the node can sit in the middle
                               // of three rows with the line filling above and
                               // below. Star rows, so they take the leftovers
                               // rather than a chosen height.
                               Grid.create
                                   [ Grid.column 0
                                     Grid.row 0
                                     Grid.rowDefinitions "*,Auto,*"
                                     Grid.children
                                         [ Border.create [ Grid.row 0; Border.child (connector above) ]

                                           Border.create
                                               [ Grid.row 1
                                                 Border.width size
                                                 Border.height size
                                                 // Exactly half the size: a
                                                 // true circle, not a rounded
                                                 // square.
                                                 Border.cornerRadius TimelineLogic.JourneyLayout.nodeRadius
                                                 Border.horizontalAlignment HorizontalAlignment.Center
                                                 // The alignment that replaces
                                                 // the offset: centred in the
                                                 // same row as the title.
                                                 Border.verticalAlignment VerticalAlignment.Center
                                                 Border.background (
                                                     if isLatest then
                                                         SolidColorBrush tokens.Accent.Normal :> IBrush
                                                     else
                                                         Theme.brush (
                                                             Theme.withAlphaHex "4D" (Ui.hexOf tokens.Accent.Normal)
                                                         )
                                                 )
                                                 Ui.decorative () ]

                                           Border.create [ Grid.row 2; Border.child (connector below) ] ] ]

                               // --- title row, content column ---------------
                               TextBlock.create
                                   [ Grid.column 2
                                     Grid.row 0
                                     TextBlock.text entry.Title
                                     TextBlock.fontSize Theme.Type.rowTitle
                                     TextBlock.verticalAlignment VerticalAlignment.Center
                                     TextBlock.foreground (
                                         if isLatest then
                                             SolidColorBrush tokens.Accent.Light :> IBrush
                                         else
                                             Theme.text ()
                                     )
                                     TextBlock.textWrapping TextWrapping.Wrap ]

                               // --- metadata row ----------------------------
                               // One segment spanning the metadata and the gap
                               // to the next entry, so there is no seam at the
                               // row boundary.
                               Border.create
                                   [ Grid.column 0
                                     Grid.row 1
                                     Border.child (connector below) ]

                               TextBlock.create
                                   [ Grid.column 2
                                     Grid.row 1
                                     TextBlock.text (
                                         sprintf
                                             "%s · %s"
                                             (ActivityKind.label entry.Kind)
                                             (Format.relativeTime now entry.OccurredAt)
                                     )
                                     TextBlock.fontSize Theme.Type.rowMeta
                                     TextBlock.foreground (Theme.faint ())
                                     TextBlock.textWrapping TextWrapping.Wrap
                                     // The inter-entry gap lives here, inside
                                     // the row the connector spans, so the line
                                     // bridges it.
                                     TextBlock.margin (0.0, 2.0, 0.0, (if index = count - 1 then 0.0 else gap)) ] ] ]
                   :> IView)) ]
    :> IView

// ---- side column ----------------------------------------------------------

let private patternsCard (tokens: Tokens) (model: Model) : IView =
    let items = TimelineLogic.patterns DateTime.Now model.Entries

    card
        tokens
        20.0
        [ Ui.sectionTitle "Patterns"
          if items.IsEmpty then
              Ui.subtle "Not enough activity yet to see a pattern."
          else
              StackPanel.create
                  [ StackPanel.spacing 10.0
                    StackPanel.children
                        (items
                         |> List.map (fun text ->
                             StackPanel.create
                                 [ StackPanel.orientation Orientation.Horizontal
                                   StackPanel.spacing 8.0
                                   StackPanel.children
                                       [ TextBlock.create
                                             [ TextBlock.text "✦"
                                               TextBlock.fontSize Theme.Type.rowMeta
                                               TextBlock.foreground (SolidColorBrush tokens.Accent.Normal) ]
                                         TextBlock.create
                                             [ TextBlock.text text
                                               TextBlock.fontSize Theme.Type.cardBody
                                               TextBlock.foreground (Theme.text ())
                                               TextBlock.textWrapping TextWrapping.Wrap ] ] ]
                             :> IView)) ] ]

let private breakdownCard (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    // How the activity breaks down, each row a shortcut to that filter.
    let counts =
        TimelineLogic.Category.all
        |> List.filter (fun c -> c <> TimelineLogic.All)
        |> List.map (fun c ->
            c, model.Entries |> List.filter (fun e -> TimelineLogic.Category.contains c e.Kind) |> List.length)
        |> List.filter (fun (_, n) -> n > 0)
        |> List.sortByDescending snd

    card
        tokens
        20.0
        [ Ui.sectionTitle "Breakdown"
          if counts.IsEmpty then
              Ui.subtle "Nothing recorded yet."
          else
              StackPanel.create
                  [ StackPanel.spacing 4.0
                    StackPanel.children
                        (counts
                         |> List.truncate 8
                         |> List.map (fun (category, count) ->
                             Button.create
                                 [ Button.horizontalAlignment HorizontalAlignment.Stretch
                                   Button.horizontalContentAlignment HorizontalAlignment.Stretch
                                   Button.background Theme.transparent
                                   Button.borderThickness 0.0
                                   Button.padding (0.0, 5.0)
                                   Button.cursor Ui.hand
                                   Ui.accessibleName (
                                       sprintf "Filter to %s, %d entries" (TimelineLogic.Category.label category) count
                                   )
                                   Button.onClick (
                                       (fun _ -> dispatch (SetCategory category)),
                                       SubPatchOptions.Always
                                   )
                                   Button.content (
                                       DockPanel.create
                                           [ DockPanel.children
                                                 [ TextBlock.create
                                                       [ DockPanel.dock Dock.Right
                                                         TextBlock.text (string count)
                                                         TextBlock.fontSize Theme.Type.rowMeta
                                                         TextBlock.fontWeight FontWeight.SemiBold
                                                         TextBlock.foreground (SolidColorBrush tokens.Accent.Light) ]
                                                   TextBlock.create
                                                       [ TextBlock.text (TimelineLogic.Category.label category)
                                                         TextBlock.fontSize Theme.Type.rowMeta
                                                         TextBlock.foreground (Theme.muted ()) ] ] ]
                                   ) ]
                             :> IView)) ] ]

/// Recent Focus: what your study has actually looked like lately.
///
/// Every figure comes from the ONE session read the page already does for the
/// "Studied this week" tile — `model.Study` is the summary of that same list, so
/// this card cannot disagree with the tile and costs no extra query. Nothing
/// here is inferred from activity-log counts.
let private recentFocusCard (tokens: Tokens) (model: Model) : IView =
    let s = model.Study

    let row (label: string) (value: string) : IView =
        DockPanel.create
            [ DockPanel.children
                  [ TextBlock.create
                        [ DockPanel.dock Dock.Right
                          TextBlock.text value
                          TextBlock.fontSize Theme.Type.rowMeta
                          TextBlock.fontWeight FontWeight.SemiBold
                          TextBlock.foreground (Theme.text ())
                          TextBlock.margin (12.0, 0.0, 0.0, 0.0) ]
                    TextBlock.create
                        [ TextBlock.text label
                          TextBlock.fontSize Theme.Type.rowMeta
                          TextBlock.foreground (Theme.muted ())
                          TextBlock.textTrimming TextTrimming.CharacterEllipsis ] ] ]
        :> IView

    card
        tokens
        20.0
        [ Ui.sectionTitle "Recent Focus"

          if s.CompletedThisWeek = 0 then
              // A compact, intentional empty state. Five zeroed rows would read
              // as a broken card rather than as "nothing yet".
              TextBlock.create
                  [ TextBlock.text "No completed study sessions this week."
                    TextBlock.fontSize Theme.Type.rowMeta
                    TextBlock.foreground (Theme.muted ())
                    TextBlock.textWrapping TextWrapping.Wrap ]

              TextBlock.create
                  [ TextBlock.text "Tick a session off in the Planner and it will show up here."
                    TextBlock.fontSize Theme.Type.rowMeta
                    TextBlock.foreground (Theme.faint ())
                    TextBlock.textWrapping TextWrapping.Wrap ]
          else
              StackPanel.create
                  [ StackPanel.spacing 6.0
                    StackPanel.children
                        [ row "Studied today" (StudyTime.format s.MinutesToday)
                          row "Studied this week" (StudyTime.format s.MinutesThisWeek)
                          row "Completed sessions" (string s.CompletedThisWeek)
                          row "Average session" (StudyTime.format s.AverageMinutes) ] ]

              match s.Last with
              | Some(title, minutes, endedAt) ->
                  Ui.divider ()

                  StackPanel.create
                      [ StackPanel.spacing 2.0
                        StackPanel.children
                            [ TextBlock.create
                                  [ TextBlock.text "LAST SESSION"
                                    TextBlock.fontSize Theme.Type.cardLabel
                                    TextBlock.fontWeight FontWeight.SemiBold
                                    TextBlock.foreground (Theme.label ()) ]
                              TextBlock.create
                                  [ TextBlock.text title
                                    TextBlock.fontSize Theme.Type.rowMeta
                                    TextBlock.foreground (Theme.text ())
                                    TextBlock.textWrapping TextWrapping.Wrap ]
                              TextBlock.create
                                  [ TextBlock.text (
                                        sprintf
                                            "%s · %s"
                                            (StudyTime.format minutes)
                                            (Format.relativeTime DateTime.Now endedAt)
                                    )
                                    TextBlock.fontSize Theme.Type.rowMeta
                                    TextBlock.foreground (Theme.faint ()) ] ] ]
              | None -> () ]

// ---- page -----------------------------------------------------------------

let view (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let visible = TimelineLogic.visibleEntries model.Category model.Query model.Entries

    let body: IView =
        if model.Loading && model.Entries.IsEmpty then
            StackPanel.create
                [ StackPanel.spacing 14.0
                  StackPanel.children [ Ui.skeletonCard 3; Ui.skeletonCard 3; Ui.skeletonCard 3 ] ]
            :> IView

        elif model.Error.IsSome && model.Entries.IsEmpty then
            StackPanel.create
                [ StackPanel.spacing 12.0
                  StackPanel.children
                      [ Ui.emptyState "!" "Could not load the timeline" (model.Error |> Option.defaultValue "")
                        StackPanel.create
                            [ StackPanel.orientation Orientation.Horizontal
                              StackPanel.horizontalAlignment HorizontalAlignment.Center
                              StackPanel.children [ Ui.primaryButton "Try again" (fun () -> dispatch Load) ] ] ] ]
            :> IView

        elif model.Entries.IsEmpty then
            Ui.emptyState
                "⧗"
                "Nothing here yet"
                "As you capture, plan, study and sync, your academic activity builds up here automatically."

        elif visible.IsEmpty then
            // Data exists, the filter hides it. A different message, and a way
            // back — an empty feed with no explanation reads as a broken screen.
            StackPanel.create
                [ StackPanel.spacing 12.0
                  StackPanel.children
                      [ Ui.emptyState
                            "⌕"
                            "No matching activity"
                            "Nothing recorded matches this filter. Try another category, or clear the search."
                        StackPanel.create
                            [ StackPanel.orientation Orientation.Horizontal
                              StackPanel.horizontalAlignment HorizontalAlignment.Center
                              StackPanel.spacing 8.0
                              StackPanel.children
                                  [ Ui.ghostButton "Show all" (fun () ->
                                        dispatch (SetCategory TimelineLogic.All)
                                        dispatch (SetQuery "")) ] ] ] ]
            :> IView

        else
            match model.Mode with
            | TimelineLogic.Feed -> feedView tokens model dispatch visible

            | TimelineLogic.Journey -> card tokens 22.0 [ Ui.sectionTitle "Academic journey"; stepperView tokens visible ]

            | TimelineLogic.Entity ->
                match model.FocusEntity with
                | Some(entity, title) ->
                    let history = TimelineLogic.entityHistory entity model.Entries

                    card
                        tokens
                        22.0
                        [ DockPanel.create
                              [ DockPanel.children
                                    [ Border.create
                                          [ DockPanel.dock Dock.Right
                                            Border.child (Ui.smallButton "Back to feed" (fun () -> dispatch ClearFocus)) ]
                                      Ui.sectionTitle (sprintf "Entity history · %s" (Format.truncate 40 title)) ] ]

                          if history.IsEmpty then
                              Ui.subtle "No other activity recorded for this item yet."
                          else
                              stepperView tokens history ]
                | None ->
                    Ui.emptyState
                        "◈"
                        "Pick something to follow"
                        "Choose History on any timeline entry to see everything that has happened to it."

    let sideColumn: IView =
        StackPanel.create
            [ StackPanel.spacing (Tokens.space tokens 22.0)
              // Order comes from `TimelineLogic.SideCard.order`, so it is one
              // definition rather than a list here that could drift from what
              // the tests assert.
              StackPanel.children
                  (TimelineLogic.SideCard.order
                   |> List.map (fun card ->
                       match card with
                       | TimelineLogic.Patterns -> patternsCard tokens model
                       | TimelineLogic.Breakdown -> breakdownCard tokens model dispatch
                       | TimelineLogic.RecentFocus -> recentFocusCard tokens model)) ]
        :> IView

    let spacing = Tokens.space tokens 22.0

    let main: IView =
        if TimelineLogic.showsSideColumn model.Width then
            Grid.create
                // The redesign's `minmax(0,2.2fr) minmax(0,1fr)`, exactly.
                [ Grid.columnDefinitions (sprintf "2.2*,%f,1*" spacing)
                  Grid.children
                      [ Border.create
                            [ Grid.column 0
                              Border.verticalAlignment VerticalAlignment.Stretch
                              Border.child body ]
                        Border.create
                            [ Grid.column 2
                              Border.verticalAlignment VerticalAlignment.Stretch
                              Border.child sideColumn ] ] ]
            :> IView
        else
            StackPanel.create [ StackPanel.spacing spacing; StackPanel.children [ body; sideColumn ] ] :> IView

    Border.create
        [ // Hit-testable across the WHOLE page. An unpainted Border is
          // transparent to hit-testing in Avalonia, so without this the empty
          // regions — around the header, beside the chips, below the last card,
          // the right column's gaps — would pass the click straight through and
          // nothing would clear. This is the fix: the clear area is the page,
          // not the feed column.
          Border.background Brushes.Transparent

          // Click anywhere that is not a control and not a card → clear.
          //
          // Two independent exclusions, belt and braces:
          //
          //   1. Entry cards and Avalonia's own controls mark PointerPressed
          //      handled, and FuncUI subscribes without `handledEventsToo`, so
          //      a handled press never reaches this handler at all.
          //   2. `Ui.isInteractiveSource` walks up the visual tree from the
          //      event's source anyway, because the source of a press is a leaf
          //      — the TextBlock inside a Button, the Border inside a TextBox
          //      template — and a control that forgot to mark its event handled
          //      must still not be treated as background.
          //
          // Guarded on mode and selection so an ordinary click on an ordinary
          // page does not push a message through MVU for nothing. Pointer
          // MOVEMENT, wheel scrolling, hover and focus are not subscribed here,
          // so none of them can clear a selection.
          Border.onPointerPressed (
              (fun args ->
                  if
                      TimelineLogic.allowsSelection model.Mode
                      && model.Selected.IsSome
                      && not (Ui.isInteractiveSource args.Source)
                  then
                      dispatch ClearSelection),
              SubPatchOptions.Always
          )

          // Escape clears the Feed selection. On the page root so it fires
          // wherever focus happens to be — the search box, a chip, a card —
          // and left unhandled so the shell's own Escape handling still runs
          // for overlays. Same message as the background click: one path.
          Border.onKeyDown (
              (fun args -> if args.Key = Avalonia.Input.Key.Escape then dispatch ClearSelection),
              SubPatchOptions.Always
          )
          Border.onSizeChanged (
              (fun args ->
                  // Only when a layout decision would actually change.
                  let before = TimelineLogic.showsSideColumn model.Width, TimelineLogic.statsPerRow model.Width

                  let after =
                      TimelineLogic.showsSideColumn args.NewSize.Width, TimelineLogic.statsPerRow args.NewSize.Width

                  if before <> after then
                      dispatch (Resized args.NewSize.Width)),
              SubPatchOptions.Always
          )
          Border.child (
              Ui.scrollNoChrome (
                  StackPanel.create
                      [ StackPanel.spacing spacing
                        StackPanel.children
                            [ header tokens model dispatch
                              searchBar tokens model dispatch
                              statsRow tokens model
                              filterChips tokens model dispatch
                              main ] ]
              )
          ) ]
    :> IView
