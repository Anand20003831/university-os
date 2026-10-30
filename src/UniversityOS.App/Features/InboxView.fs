module UniversityOS.App.Features.InboxView

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

/// The unified inbox: everything captured or imported lands here, is analysed
/// into proposed actions, and is applied or dismissed. The triage surface of the
/// capture-first philosophy.
///
/// Every decision this screen makes lives in `InboxLogic`; this file only draws
/// what that module resolves. See its header for the three elements of the
/// redesign that have no data behind them and are therefore absent.

type Model =
    { Items: InboxItem list
      /// What the user already has, so a conflict is a real repository check
      /// rather than a reading of the proposal's own words. Loaded in the SAME
      /// pass as the items, so a warning can never describe a different moment
      /// than the card it sits on.
      Existing: InboxLogic.Existing
      Filter: InboxLogic.Filter
      Selected: InboxItemId option
      /// The item whose request is in flight. Also the per-item re-entry guard:
      /// its actions are disabled and a repeated message is dropped rather than
      /// queued, so one control cannot submit the same operation twice.
      BusyItem: InboxItemId option
      /// A bulk action is running. The same guard, page-wide.
      BulkBusy: bool
      /// A destructive bulk action waiting for explicit confirmation.
      PendingBulk: InboxLogic.BulkActionId option
      Loading: bool
      Error: string option
      /// Result lines from the last apply, shown in a banner and then cleared.
      Notice: string list
      /// Content width, used only for layout decisions.
      Width: float }

type Msg =
    | Load
    | Loaded of InboxItem list * InboxLogic.Existing
    | Failed of exn
    | SelectFilter of InboxLogic.Filter
    | Select of InboxItemId
    | ClearSelection
    | Analyse of InboxItem
    | Accept of InboxItem
    | ApplyOne of InboxItem * ProposedAction
    | Dismiss of InboxItem
    | OpenFile of InboxItem
    /// An item-level request finished: refresh everything it could have changed.
    | ItemDone of string list
    /// A message with no side effect beyond the banner (opening a file).
    | Noticed of string list
    | Bulk of InboxLogic.BulkActionId
    | ConfirmBulk
    | CancelBulk
    | BulkDone of string list
    | ClearNotice
    | Resized of float

// ---------------------------------------------------------------------------
// commands
// ---------------------------------------------------------------------------

/// Type-annotated so the record's fields resolve to `InboxLogic`'s shape rather
/// than to `Assignment`'s identically named ones.
let private existingAssignment (a: Assignment) : InboxLogic.ExistingAssignment =
    { Id = a.Id.Value
      Title = a.Title
      Deadline = a.Deadline }

/// One command, three reads.
///
/// `GetAll` rather than `GetActive`, because the Completed filter needs applied
/// and dismissed items too — the filter decides what is shown, not the query, so
/// switching a chip never waits on the database.
let private loadCmd (services: AppServices) =
    Elm.ofAsync
        (async {
            let! items = services.Inbox.GetAll()
            let! assignments = services.Assignments.GetAll()
            let! modules = services.Modules.GetAll()

            let existing: InboxLogic.Existing =
                { Assignments = assignments |> List.map existingAssignment
                  ModuleCodes = modules |> List.map (fun m -> m.Code) }

            return items, existing
        })
        Loaded
        Failed

/// Runs a bulk action over exactly the items the current filter shows.
///
/// Sequential, never parallel: one SQLite connection serves every repository, and
/// `ApplyAll` writes through several of them per item.
let private bulkCmd (services: AppServices) (id: InboxLogic.BulkActionId) (visible: InboxItem list) =
    Elm.ofAsync
        (async {
            let results = ResizeArray<string>()

            match id with
            | InboxLogic.BulkActionId.AnalyseAll ->
                for item in visible |> List.filter (fun i -> InboxLogic.isActive i && i.Analysis.IsNone) do
                    let! _ = services.Capture.Analyze item
                    results.Add(sprintf "Analysed \"%s\"" item.Title)

            | InboxLogic.BulkActionId.AcceptAll ->
                let acceptable =
                    visible
                    |> List.filter (fun i ->
                        InboxLogic.isActive i
                        && i.Analysis.IsSome
                        && not (List.isEmpty (InboxLogic.realActionsOf i)))

                for item in acceptable do
                    let! _, lines = services.Capture.ApplyAll item
                    results.AddRange lines

            | InboxLogic.BulkActionId.DismissAll ->
                for item in visible |> List.filter InboxLogic.isActive do
                    let! _ = services.Capture.Dismiss item
                    results.Add(sprintf "Dismissed \"%s\"" item.Title)

            return List.ofSeq results
        })
        BulkDone
        Failed

/// Opens the imported file with the OS handler — the same call the Documents and
/// Lectures screens make. Reports rather than throws when the file has gone.
let private openFileCmd (services: AppServices) (item: InboxItem) =
    Elm.ofAsync
        (async {
            match item.DocumentId with
            | None -> return []
            | Some id ->
                let! document = services.Documents.GetById id

                match document with
                | None -> return [ "✕ The captured document no longer exists." ]
                | Some d when not (IO.File.Exists d.StoredPath) ->
                    return [ sprintf "✕ \"%s\" is no longer in the document store." d.FileName ]
                | Some d ->
                    Diagnostics.Process.Start(
                        Diagnostics.ProcessStartInfo(FileName = d.StoredPath, UseShellExecute = true)
                    )
                    |> ignore

                    return []
        })
        Noticed
        Failed

// ---------------------------------------------------------------------------
// init / update
// ---------------------------------------------------------------------------

let init (services: AppServices) : Model * Cmd<Msg> =
    { Items = []
      Existing = InboxLogic.Existing.empty
      Filter = InboxLogic.Filter.NeedsReview
      Selected = None
      BusyItem = None
      BulkBusy = false
      PendingBulk = None
      Loading = true
      Error = None
      Notice = []
      Width = 1400.0 },
    loadCmd services

/// The items the current filter shows, for the bulk row and its actions.
let private visibleOf (model: Model) =
    let conflictsOf = InboxLogic.conflictsFor model.Existing
    InboxLogic.visibleItems conflictsOf model.Filter model.Items

let update (services: AppServices) (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    /// Every item-level request goes through here, so the guard, the error reset
    /// and the busy flag cannot be forgotten on one path and applied on another.
    let request (item: InboxItem) (work: Cmd<Msg>) =
        if model.BusyItem.IsSome || model.BulkBusy then
            model, Cmd.none
        else
            { model with BusyItem = Some item.Id; Error = None }, work

    match msg with
    | Load -> { model with Loading = true }, loadCmd services

    | Loaded(items, existing) ->
        // A selection that no longer exists is dropped rather than left dangling,
        // so the detail panel cannot describe an item that has gone.
        let selected =
            model.Selected
            |> Option.filter (fun id -> items |> List.exists (fun i -> i.Id = id))

        { model with
            Items = items
            Existing = existing
            Selected = selected
            Loading = false
            Error = None },
        Cmd.none

    | Failed ex ->
        // Every in-flight flag is cleared: a failure must not leave a button
        // stuck saying "Analysing…" or a bulk row disabled forever.
        { model with
            Loading = false
            BusyItem = None
            BulkBusy = false
            PendingBulk = None
            Error = Some ex.Message },
        Cmd.none

    | SelectFilter filter ->
        // A pending confirmation is abandoned: it was counted against a different
        // set of items and must never be applied to this one.
        { model with Filter = filter; PendingBulk = None }, Cmd.none

    | Select id -> { model with Selected = Some id }, Cmd.none
    | ClearSelection -> { model with Selected = None }, Cmd.none

    // Each service call is wrapped in `async { return! … }` rather than passed
    // directly. An argument is evaluated where it is written, so the unwrapped
    // form touches the service during `update` instead of when the command runs.
    | Analyse item ->
        request item (Elm.ofAsync (async { return! services.Capture.Analyze item }) (fun _ -> ItemDone []) Failed)

    | Accept item ->
        request
            item
            (Elm.ofAsync (async { return! services.Capture.ApplyAll item }) (fun (_, lines) -> ItemDone lines) Failed)

    | ApplyOne(item, action) ->
        request
            item
            (Elm.ofAsync
                (async { return! services.Capture.ApplyAction(item, action) })
                (fun result ->
                    ItemDone
                        [ match result with
                          | Ok message -> "✓ " + message
                          | Error message -> "✕ " + message ])
                Failed)

    | Dismiss item ->
        request item (Elm.ofAsync (async { return! services.Capture.Dismiss item }) (fun _ -> ItemDone []) Failed)

    | OpenFile item -> model, openFileCmd services item

    | ItemDone lines ->
        { model with BusyItem = None; Notice = lines },
        // Reload rather than patching: the services write through repositories,
        // so re-reading is the only way to show what was actually persisted —
        // and the metrics, the insight strip and the conflict checks all depend
        // on assignments and modules this may just have changed.
        Cmd.batch
            [ loadCmd services
              if not lines.IsEmpty then Elm.delayed 7000 ClearNotice ]

    | Noticed lines ->
        { model with Notice = lines },
        (if lines.IsEmpty then Cmd.none else Elm.delayed 7000 ClearNotice)

    | Bulk _ when model.BulkBusy || model.BusyItem.IsSome -> model, Cmd.none
    | Bulk id ->
        let state = InboxLogic.bulkActions (InboxLogic.bulkContext false (visibleOf model)) |> List.tryFind (fun s -> s.Id = id)

        match state with
        | Some s when not s.Enabled -> model, Cmd.none
        | Some s when s.NeedsConfirmation -> { model with PendingBulk = Some id }, Cmd.none
        | Some _ -> { model with BulkBusy = true; Error = None }, bulkCmd services id (visibleOf model)
        | None -> model, Cmd.none

    | ConfirmBulk ->
        match model.PendingBulk with
        | None -> model, Cmd.none
        | Some id ->
            { model with BulkBusy = true; PendingBulk = None; Error = None }, bulkCmd services id (visibleOf model)

    | CancelBulk -> { model with PendingBulk = None }, Cmd.none

    | BulkDone lines ->
        { model with BulkBusy = false; Notice = lines },
        Cmd.batch
            [ loadCmd services
              if not lines.IsEmpty then Elm.delayed 9000 ClearNotice ]

    | ClearNotice -> { model with Notice = [] }, Cmd.none
    | Resized width -> { model with Width = width }, Cmd.none

// ---------------------------------------------------------------------------
// view — shared treatments
// ---------------------------------------------------------------------------

/// No specular pass on any Inbox content card. Named once so no card can opt out
/// by accident and a test can state the claim for the whole screen.
let private contentHighlightScale = InboxLogic.Geometry.contentHighlight

/// The shared warm card treatment — the same material role and geometry tokens
/// the Dashboard, Timeline and Planner use, not an Inbox surface of its own.
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
                            [ StackPanel.spacing (Tokens.space tokens 10.0)
                              StackPanel.verticalAlignment VerticalAlignment.Top
                              StackPanel.children children ]
                    ) ] ) ]
    :> IView

/// A glyph in a fixed column beside text that wraps in whatever width is left.
///
/// A horizontal `StackPanel` measures its children with infinite available
/// width, so a `TextBlock` inside one never wraps and simply runs out of its
/// card. A Grid gives the text a real finite width.
let private bulletRow (glyph: string) (glyphBrush: IBrush) (text: string) (textBrush: IBrush) : IView =
    Grid.create
        [ Grid.columnDefinitions InboxLogic.Geometry.bulletColumns
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

/// An uppercase section label with a value pushed to the right.
let private panelHeading (title: string) (right: string option) : IView =
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

/// A label / value row inside the detail panel. A Grid, not a DockPanel, so a
/// long value wraps under itself rather than pushing the label out of the card.
let private metadataRow (label: string) (value: string) : IView =
    Grid.create
        [ Grid.columnDefinitions InboxLogic.Geometry.metadataColumns
          Grid.margin (0.0, 3.0)
          Grid.children
              [ TextBlock.create
                    [ Grid.column 0
                      TextBlock.text label
                      TextBlock.fontSize Theme.Type.rowMeta
                      TextBlock.foreground (Theme.muted ())
                      TextBlock.verticalAlignment VerticalAlignment.Top ]
                TextBlock.create
                    [ Grid.column 2
                      TextBlock.text value
                      TextBlock.fontSize Theme.Type.rowMeta
                      TextBlock.fontWeight FontWeight.SemiBold
                      TextBlock.foreground (Theme.text ())
                      TextBlock.textAlignment TextAlignment.Right
                      TextBlock.textWrapping TextWrapping.Wrap ] ] ]
    :> IView

/// The opaque surface a review card is drawn on. Selecting a card changes it,
/// and everything measured for readability inside that card has to follow —
/// otherwise a chip that clears AA on an unselected card falls to 3.6:1 the
/// moment the card is selected.
let private cardSurface (isSelected: bool) =
    if isSelected then (Theme.surfaces ()).Selected else (Theme.surfaces ()).Card

/// The chip's own fill, as the opaque colour it will actually render as. Takes
/// its alpha from the SAME constant the fill is painted with, so the measured
/// background cannot drift away from the drawn one.
let private chipWash (surface: Color) (hex: string) =
    Theme.compositeOver surface (Theme.withAlphaHex InboxLogic.Geometry.chipFillAlpha hex)

/// A small labelled wash. Decorative and hit-test-transparent: it carries no
/// action, so it neither takes the pointer from the card it sits on nor appears
/// in the accessibility tree as an unnamed element.
///
/// **Its ink is measured against its own wash, not against the card.** A chip
/// never sits on the card — it sits on this fill, the same hue at 12% over the
/// card, which on a light theme is always the darker of the two. Measuring
/// against the card stopped the readability walk a step early and left every
/// chip on the Light screen at 4.15–4.44:1; on a selected card, darker again,
/// at 3.4–3.6:1. `surface` is therefore threaded in from the card that owns it.
let private chip (tokens: Tokens) (surface: Color) (glyph: string) (label: string) (hex: string) : IView =
    let wash = chipWash surface hex

    Border.create
        [ Border.background (Theme.brush (Theme.withAlphaHex InboxLogic.Geometry.chipFillAlpha hex))
          Border.cornerRadius (Tokens.radius tokens 999.0)
          Border.padding (9.0, 3.0)
          Border.isHitTestVisible false
          Border.child (
              TextBlock.create
                  [ TextBlock.text (glyph + " " + label)
                    TextBlock.fontSize Theme.Type.metricLabel
                    TextBlock.fontWeight FontWeight.SemiBold
                    TextBlock.foreground (Theme.brush (Theme.readableOnColor wash hex)) ]
          ) ]
    :> IView

/// The ONE place a semantic role becomes a colour.
///
/// `InboxLogic` decides what a value means; this decides what that meaning looks
/// like. Every role resolves to a shared design token, never to a literal, so the
/// contrast tests can walk `InboxLogic.inksUsed` and cover the whole screen.
let private inkHex (ink: InboxLogic.Ink) =
    match ink with
    | InboxLogic.Ink.Positive -> Theme.successHex ()
    | InboxLogic.Ink.Caution -> Theme.warningHex ()
    | InboxLogic.Ink.Informational -> Theme.infoHex ()
    | InboxLogic.Ink.Critical -> Theme.dangerHex ()
    | InboxLogic.Ink.Neutral -> Theme.Identity.neutral
    | InboxLogic.Ink.Accent -> Theme.accentHex ()
    | InboxLogic.Ink.Identity -> Theme.Identity.neutral

/// A source's fixed identity hue — the same wherever that source appears, and
/// never the user's accent, so a source does not change colour with a swatch.
let private sourceHex (kind: InboxLogic.SourceKind) =
    match kind with
    | InboxLogic.SourceKind.Capture -> Theme.Identity.amber
    | InboxLogic.SourceKind.File -> Theme.Identity.orchid
    | InboxLogic.SourceKind.Email -> Theme.Identity.sky
    | InboxLogic.SourceKind.CalendarFeed -> Theme.Identity.teal
    | InboxLogic.SourceKind.Connector -> Theme.Identity.neutral

let private effectHex (kind: InboxLogic.EffectKind) = inkHex (InboxLogic.effectInk kind)

let private statusHex (status: InboxStatus) = inkHex (InboxLogic.statusInk status)

let private conflictHex (conflict: InboxLogic.Conflict) = inkHex (InboxLogic.conflictInk conflict)

// ---- glass actions ---------------------------------------------------------

/// Inbox's action button — geometry only.
///
/// The glass, the veil, the accent floor and the ink rule live in
/// `Ui.glassAction`. There were three copies of that button — here, in the
/// Planner and in Notes — and only Notes' copy carried
/// `Glass.primaryTintAlpha`, which is why Accept and Accept All read as grey
/// while every token feeding them was correct.
let private glassAction
    (tokens: Tokens)
    (label: string)
    (primary: bool)
    (enabled: bool)
    (accessibleName: string)
    (onClick: unit -> unit)
    : IView =
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
///
/// `surface` is the surface these sit on, because the heading's `Label` ink
/// lands at exactly 4.50:1 on a SELECTED card under the blue and teal accents —
/// on the wrong side of the floor once rounding is taken out.
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
                                Theme.brush (
                                    Theme.readableOnColor surface (Ui.hexOf (Theme.surfaces ()).Label)
                                )
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
                    [ TextBlock.text "Inbox"
                      TextBlock.fontSize Theme.Type.pageTitle
                      TextBlock.fontWeight FontWeight.Bold
                      TextBlock.foreground (Theme.text ()) ]
                TextBlock.create
                    [ TextBlock.text
                          "What needs my attention — review what the AI proposes before it enters University OS."
                      TextBlock.fontSize Theme.Type.pageMeta
                      TextBlock.foreground (Theme.muted ())
                      TextBlock.textWrapping TextWrapping.Wrap ] ] ]
    :> IView

/// One metric card. The value and the label sit in a fixed order with a fixed
/// type scale, so five cards in a row are the same shape whatever they hold.
let private metricCard (tokens: Tokens) (metric: InboxLogic.Metric) : IView =
    card
        tokens
        14.0
        [ TextBlock.create
              [ TextBlock.text (metric.Value |> Option.defaultValue "—")
                TextBlock.fontSize Theme.Type.metric
                TextBlock.fontWeight FontWeight.SemiBold
                TextBlock.foreground (if metric.Value.IsSome then Theme.text () else Theme.muted ())
                TextBlock.textTrimming TextTrimming.CharacterEllipsis ]

          TextBlock.create
              [ TextBlock.text metric.Label
                TextBlock.fontSize Theme.Type.metricLabel
                TextBlock.foreground (Theme.muted ())
                TextBlock.textWrapping TextWrapping.Wrap ]

          // An unknown figure says why, rather than showing a dash and leaving
          // the user to guess whether it means zero.
          match metric.Unavailable with
          | Some why ->
              TextBlock.create
                  [ TextBlock.text why
                    TextBlock.fontSize Theme.Type.metricLabel
                    TextBlock.foreground (Theme.faint ())
                    TextBlock.textWrapping TextWrapping.Wrap ]
          | None -> () ]

/// One logical row of cards, as ONE Grid.
///
/// A Grid row sizes to its tallest child and every cell is stretched into it, so
/// the cards' top and bottom edges line up by construction. `minHeight` is
/// applied to the ROW, never to a card: a floor per card would let one card in a
/// row stand taller than its neighbour for no reason.
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

let private metricsSection (tokens: Tokens) (spacing: float) (model: Model) : IView =
    cardRow
        spacing
        InboxLogic.Geometry.metricRowMinHeight
        (InboxLogic.metricColumns model.Width)
        (InboxLogic.metrics model.Items |> List.map (metricCard tokens))

let private insightsSection (tokens: Tokens) (observations: string list) : IView =
    card
        tokens
        16.0
        [ if observations.IsEmpty then
              bulletRow "•" (Theme.faint ()) "Nothing to report — the inbox has nothing waiting on you." (Theme.muted ())
          else
              WrapPanel.create
                  [ WrapPanel.children
                        (observations
                         |> List.map (fun text ->
                             Border.create
                                 [ Border.margin (0.0, 0.0, 22.0, 4.0)
                                   // A WrapPanel measures its children with
                                   // infinite width in the wrap direction, so a
                                   // star column inside one resolves to the
                                   // text's natural width and never wraps. The
                                   // ceiling gives it a finite width again, so a
                                   // long observation wraps inside the strip
                                   // instead of pushing the page sideways.
                                   Border.maxWidth 420.0
                                   Border.child (
                                       bulletRow "✦" (Theme.accent ()) text (Theme.textSecondary ())
                                   ) ]
                             :> IView)) ] ]

/// The filter chips.
///
/// Selected and unselected chips are the same size: the border is drawn in both
/// states and only its brush changes, and the minimum height is fixed. Switching
/// a filter therefore recolours the row and never moves it.
let private filtersSection (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    // Filter chips sit on the PAGE, not inside a card, so everything about them
    // is measured against the page's own base rather than against a card.
    let page = (Theme.surfaces ()).Window
    let selectedFill = Theme.compositeOver page (Theme.accentSoftHex ())

    // The selection ring. An accent measures 6.5–8.7:1 against the dark page and
    // only 1.75–2.33:1 against the light one, so on Light the selected chip was
    // ringed in something barely distinguishable from the background. Walking to
    // the 3:1 non-text floor restores the signal and is a no-op on Dark.
    let selectedRing = Theme.brush (Theme.visibleOnColor page (Theme.accentHex ()))

    let chipFor (filter: InboxLogic.Filter) : IView =
        let isSelected = model.Filter = filter
        let label = InboxLogic.Filter.label filter

        Button.create
            [ Button.background (if isSelected then Theme.accentSoft () else Theme.transparent)
              Button.borderThickness InboxLogic.Geometry.filterBorderThickness
              Button.borderBrush (if isSelected then selectedRing else Theme.border ())
              Button.foreground (
                  if isSelected then Theme.brush (Theme.readableOnColor selectedFill (Theme.accentHex ()))
                  else Theme.muted ()
              )
              Button.cornerRadius (Tokens.radius tokens InboxLogic.Geometry.filterRadius)
              Button.minHeight InboxLogic.Geometry.filterMinHeight
              Button.padding (13.0, 4.0)
              Button.fontSize Theme.Type.metricLabel
              Button.fontWeight FontWeight.SemiBold
              Button.margin (0.0, 0.0, 8.0, 8.0)
              Button.cursor Ui.hand
              Button.content label
              Ui.accessibleName (
                  if isSelected then sprintf "%s filter, selected" label else sprintf "Show %s" label
              )
              Button.onClick ((fun _ -> dispatch (SelectFilter filter)), SubPatchOptions.Always) ]
        :> IView

    WrapPanel.create [ WrapPanel.children (InboxLogic.Filter.all |> List.map chipFor) ] :> IView

let private bulkSection (tokens: Tokens) (model: Model) (visible: InboxItem list) (dispatch: Msg -> unit) : IView =
    let states = InboxLogic.bulkActions (InboxLogic.bulkContext (model.BulkBusy || model.BusyItem.IsSome) visible)

    let unavailable =
        states |> List.choose (fun s -> s.DisabledBecause |> Option.map (fun why -> s.Label, why))

    StackPanel.create
        [ StackPanel.spacing 4.0
          StackPanel.children
              [ WrapPanel.create
                    [ WrapPanel.children
                          (states
                           |> List.map (fun s ->
                               glassAction
                                   tokens
                                   (if model.BulkBusy && s.Primary then "Working…" else s.Label)
                                   s.Primary
                                   s.Enabled
                                   s.Label
                                   (fun () -> dispatch (Bulk s.Id)))) ]

                // The confirmation step for a destructive bulk action. It states
                // the exact count and that the change cannot be undone, because
                // no repository here keeps a prior state.
                match model.PendingBulk with
                | Some id ->
                    let warnWash =
                        Theme.compositeOver
                            (Theme.surfaces ()).Window
                            (Theme.withAlphaHex "1F" (Theme.warningHex ()))

                    Border.create
                        [ Border.background (Theme.brush (Theme.withAlphaHex "1F" (Theme.warningHex ())))
                          Border.borderThickness 1.0
                          Border.borderBrush (Theme.brush (Theme.overlayEdgeHex "55" (Theme.warningHex ())))
                          Border.cornerRadius (Tokens.radius tokens 12.0)
                          Border.padding (16.0, 12.0)
                          Border.child (
                              StackPanel.create
                                  [ StackPanel.spacing 10.0
                                    StackPanel.children
                                        [ TextBlock.create
                                              [ TextBlock.text (
                                                    InboxLogic.confirmationPrompt
                                                        id
                                                        (visible |> List.filter InboxLogic.isActive |> List.length)
                                                )
                                                TextBlock.fontSize Theme.Type.rowMeta
                                                TextBlock.foreground (Theme.text ())
                                                TextBlock.textWrapping TextWrapping.Wrap ]

                                          StackPanel.create
                                              [ StackPanel.orientation Orientation.Horizontal
                                                StackPanel.spacing 10.0
                                                StackPanel.children
                                                    [ // Not `Ui.dangerButton`: its
                                                      // ink is the raw Danger
                                                      // role, which measures
                                                      // 3.73:1 against this
                                                      // warning wash on Light.
                                                      // Walked against the wash
                                                      // it actually sits on.
                                                      Button.create
                                                          [ Button.content "Yes, dismiss them"
                                                            Button.background Theme.transparent
                                                            Button.foreground (
                                                                Theme.brush (
                                                                    Theme.readableOnColor
                                                                        warnWash
                                                                        (Theme.dangerHex ())
                                                                )
                                                            )
                                                            Button.borderBrush (
                                                                Theme.brush (
                                                                    Theme.overlayEdgeHex
                                                                        "66"
                                                                        (Theme.dangerHex ())
                                                                )
                                                            )
                                                            Button.borderThickness 1.0
                                                            Button.cornerRadius (Tokens.radius tokens 6.0)
                                                            Button.padding (14.0, 7.0)
                                                            Button.fontSize Theme.fontSm
                                                            Button.fontWeight FontWeight.SemiBold
                                                            Button.cursor Ui.hand
                                                            Ui.accessibleName
                                                                "Confirm: dismiss every item currently shown"
                                                            Button.onClick (
                                                                (fun _ -> dispatch ConfirmBulk),
                                                                SubPatchOptions.Always
                                                            ) ]
                                                      Ui.ghostButton "Cancel" (fun () -> dispatch CancelBulk) ] ] ] ]
                          ) ]
                | None -> ()

                // The bulk row sits on the page, not inside a card.
                yield! unavailableNotes (Theme.surfaces ()).Window unavailable ] ]
    :> IView

// ---- the review card -------------------------------------------------------

/// One proposal row: what it would do, and a control to do just that one thing.
let private proposalRow
    (tokens: Tokens)
    (surface: Color)
    (model: Model)
    (item: InboxItem)
    (dispatch: Msg -> unit)
    (action: ProposedAction)
    : IView =
    let busy = model.BusyItem = Some item.Id || model.BulkBusy
    let processed = not (InboxLogic.isActive item)

    // The Apply button's fill is the accent at 14%, so its label's readability is
    // measured against that composite rather than against the card behind it.
    let applyWash = Theme.compositeOver surface (Theme.accentSoftHex ())

    Grid.create
        [ Grid.columnDefinitions "*,10,Auto"
          Grid.margin (0.0, 2.0)
          Grid.children
              [ TextBlock.create
                    [ Grid.column 0
                      TextBlock.text (InboxLogic.actionLabel action)
                      TextBlock.fontSize Theme.Type.rowMeta
                      TextBlock.foreground (Theme.text ())
                      TextBlock.textWrapping TextWrapping.Wrap
                      TextBlock.verticalAlignment VerticalAlignment.Center ]

                if not processed then
                    Border.create
                        [ Grid.column 2
                          Border.verticalAlignment VerticalAlignment.Center
                          Border.child (
                              Button.create
                                  [ Button.content "Apply"
                                    Button.isEnabled (not busy)
                                    Button.background (Theme.accentSoft ())
                                    Button.foreground (
                                        Theme.brush (Theme.readableOnColor applyWash (Theme.accentHex ()))
                                    )
                                    Button.borderThickness 0.0
                                    Button.cornerRadius (Tokens.radius tokens 8.0)
                                    Button.padding (10.0, 3.0)
                                    Button.fontSize Theme.Type.metricLabel
                                    Button.fontWeight FontWeight.SemiBold
                                    Button.cursor Ui.hand
                                    Ui.accessibleName (sprintf "Apply: %s" (InboxLogic.actionLabel action))
                                    Button.onClick (
                                        (fun _ -> dispatch (ApplyOne(item, action))),
                                        SubPatchOptions.Always
                                    ) ]
                          ) ] ] ]
    :> IView

/// One review card.
///
/// The whole card is a real `Button`, so it is focusable, keyboard-activatable
/// and named — selection is never a click handler on a decorative container. The
/// action buttons inside it still work because Avalonia's `Button` marks a
/// pointer press and release handled, and the outer Button's own handler runs
/// only when the event is unhandled: clicking Accept accepts, and clicking
/// anywhere else selects.
///
/// **Hover here is the Fluent presenter fill, NOT the glass veil**, and that is
/// deliberate. The veil works by being the topmost hit-testable child with only
/// inert content above it; a card contains real controls,
/// so a veil beneath them would be starved of the pointer events it needs. These
/// cards are warm-neutral rather than accent-filled, which is exactly the case
/// that reads correctly on the presenter fill — the same
/// hover the approved Dashboard quick actions use. `FluentMaterial` maps
/// `ButtonBackgroundPointerOver` to the material's own Hover role, so the lift
/// is warm and shows through the glass's transparency.
///
/// The veil is still used on this screen's ACTION buttons, whose structure is
/// the Planner's exactly and whose primary is accent-filled.
let private reviewCard
    (tokens: Tokens)
    (now: DateTime)
    (model: Model)
    (conflicts: InboxLogic.Conflict list)
    (dispatch: Msg -> unit)
    (item: InboxItem)
    : IView =
    let isSelected = model.Selected = Some item.Id
    let kind = InboxLogic.sourceKindOf item
    let actions = InboxLogic.itemActions (InboxLogic.itemContext model.BusyItem item)
    let busy = model.BusyItem = Some item.Id

    // Everything inside this card measures its readability against the card's
    // OWN surface, which selection changes.
    let surface = cardSurface isSelected
    let chipOn = chip tokens surface
    let inkOn (hex: string) = Theme.brush (Theme.readableOnColor surface hex)

    let messageFor =
        function
        | InboxLogic.ItemActionId.Accept -> Accept item
        | InboxLogic.ItemActionId.Analyse -> Analyse item
        | InboxLogic.ItemActionId.Dismiss -> Dismiss item
        | InboxLogic.ItemActionId.OpenFile -> OpenFile item

    let unavailable =
        actions |> List.choose (fun s -> s.DisabledBecause |> Option.map (fun why -> s.Label, why))

    let headerRow: IView =
        Grid.create
            [ Grid.columnDefinitions "Auto,11,*,10,Auto"
              Grid.children
                  [ // Source badge. Decorative — the source's own words sit two
                    // lines below it.
                    Border.create
                        [ Grid.column 0
                          Border.width InboxLogic.Geometry.sourceBadgeSize
                          Border.height InboxLogic.Geometry.sourceBadgeSize
                          Border.cornerRadius (Tokens.radius tokens 9.0)
                          Border.background (Theme.brush (Theme.withAlphaHex "1F" (sourceHex kind)))
                          Border.verticalAlignment VerticalAlignment.Top
                          Border.isHitTestVisible false
                          Ui.decorative ()
                          Border.child (
                              TextBlock.create
                                  [ TextBlock.text (InboxLogic.SourceKind.glyph kind)
                                    TextBlock.fontSize Theme.Type.rowMeta
                                    TextBlock.foreground (
                                        Theme.brush (
                                            Theme.readableOnColor (chipWash surface (sourceHex kind)) (sourceHex kind)
                                        )
                                    )
                                    TextBlock.horizontalAlignment HorizontalAlignment.Center
                                    TextBlock.verticalAlignment VerticalAlignment.Center ]
                          ) ]

                    StackPanel.create
                        [ Grid.column 2
                          StackPanel.spacing 3.0
                          StackPanel.children
                              [ TextBlock.create
                                    [ TextBlock.text item.Title
                                      TextBlock.fontSize Theme.Type.rowTitle
                                      TextBlock.fontWeight FontWeight.SemiBold
                                      TextBlock.foreground (Theme.text ())
                                      TextBlock.textWrapping TextWrapping.Wrap ]

                                TextBlock.create
                                    [ TextBlock.text (
                                          sprintf
                                              "%s · %s"
                                              (InboxLogic.sourceLabel item)
                                              (Format.relativeTime now item.ReceivedAt)
                                      )
                                      TextBlock.fontSize Theme.Type.metricLabel
                                      // Measured against THIS card's surface. On
                                      // a selected card the plain Faint ink
                                      // falls to 3.88:1 on Light and 4.32:1 on
                                      // Dark — both under the floor.
                                      TextBlock.foreground (
                                          inkOn (Ui.hexOf (Theme.surfaces ()).Faint)
                                      )
                                      TextBlock.textWrapping TextWrapping.Wrap ] ] ]

                    // Module code and status. The status column has a reserved
                    // width, so a card cannot resize when "New" becomes
                    // "Analysed".
                    StackPanel.create
                        [ Grid.column 4
                          StackPanel.orientation Orientation.Horizontal
                          StackPanel.spacing 7.0
                          StackPanel.verticalAlignment VerticalAlignment.Top
                          StackPanel.children
                              [ match item.Analysis |> Option.bind (fun a -> a.SuggestedModuleCode) with
                                | Some code -> chipOn "" code (Theme.accentHex ())
                                | None -> ()

                                Border.create
                                    [ Border.minWidth InboxLogic.Geometry.statusMinWidth
                                      Border.isHitTestVisible false
                                      Border.child (
                                          TextBlock.create
                                              [ TextBlock.text (InboxLogic.Status.label item.Status)
                                                TextBlock.fontSize Theme.Type.metricLabel
                                                TextBlock.fontWeight FontWeight.SemiBold
                                                // The status word sits directly
                                                // on the card, with no wash of
                                                // its own — so this is measured
                                                // against the card's surface.
                                                TextBlock.foreground (inkOn (statusHex item.Status))
                                                TextBlock.textAlignment TextAlignment.Right ]
                                      ) ] ] ] ] ]
        :> IView

    let summaryText =
        match item.Analysis with
        | Some analysis when analysis.Summary.Trim().Length > 0 -> Some analysis.Summary
        | _ when item.Body.Trim().Length > 0 -> Some(Format.truncate 240 item.Body)
        | _ -> None

    let content: IView =
        StackPanel.create
            [ StackPanel.spacing (Tokens.space tokens 11.0)
              StackPanel.children
                  [ headerRow

                    match summaryText with
                    | Some text ->
                        TextBlock.create
                            [ TextBlock.text text
                              TextBlock.fontSize Theme.Type.rowMeta
                              TextBlock.foreground (Theme.muted ())
                              TextBlock.textWrapping TextWrapping.Wrap
                              TextBlock.lineHeight (Theme.Type.rowMeta * 1.55) ]
                    | None -> ()

                    // Real conflicts, from real repository checks. Every one
                    // carries a glyph AND its words.
                    for conflict in conflicts do
                        let hex = conflictHex conflict
                        let fill = Theme.withAlphaHex InboxLogic.Geometry.bannerFillAlpha hex
                        let banner = Theme.compositeOver surface fill

                        Border.create
                            [ Border.background (Theme.brush fill)
                              Border.borderThickness 1.0
                              // The edge is weighted per theme, so a warm dark
                              // ink at low alpha on parchment reads with the
                              // same presence as a warm light one on charcoal.
                              Border.borderBrush (
                                  Theme.brush (Theme.overlayEdgeHex InboxLogic.Geometry.bannerEdgeAlpha hex)
                              )
                              Border.cornerRadius (Tokens.radius tokens 10.0)
                              Border.padding (11.0, 8.0)
                              Border.isHitTestVisible false
                              Border.child (
                                  bulletRow
                                      "⚠"
                                      (Theme.brush (Theme.readableOnColor banner hex))
                                      (InboxLogic.Conflict.text conflict)
                                      (Theme.text ())
                              ) ]

                    // What accepting would really do — read off
                    // `CaptureService.ApplyAction`, branch by branch.
                    match item.Analysis with
                    | Some analysis ->
                        let effects =
                            InboxLogic.realActions analysis
                            |> List.collect (InboxLogic.effectsFor (InboxLogic.hasDocument item))
                            |> List.distinct

                        if not effects.IsEmpty then
                            WrapPanel.create
                                [ WrapPanel.children
                                      (effects
                                       |> List.map (fun e ->
                                           Border.create
                                               [ Border.margin (0.0, 0.0, 6.0, 6.0)
                                                 Border.isHitTestVisible false
                                                 Border.child (chipOn "✓" e.Label (effectHex e.Kind)) ]
                                           :> IView)) ]

                        let real = InboxLogic.realActions analysis

                        if real.IsEmpty then
                            bulletRow
                                "•"
                                (Theme.faint ())
                                "Analysed — nothing needs doing with this item."
                                (Theme.muted ())
                        else
                            StackPanel.create
                                [ StackPanel.spacing 2.0
                                  StackPanel.children
                                      (real |> List.map (proposalRow tokens surface model item dispatch)) ]
                    | None ->
                        bulletRow
                            "•"
                            (Theme.faint ())
                            "Not analysed yet — analyse it to see what would happen."
                            (Theme.muted ()) ] ]
        :> IView

    let actionRow: IView =
        StackPanel.create
            [ StackPanel.spacing 4.0
              StackPanel.children
                  [ WrapPanel.create
                        [ WrapPanel.children
                              (actions
                               |> List.map (fun s ->
                                   glassAction
                                       tokens
                                       (if busy && s.Primary then "Working…" else s.Label)
                                       s.Primary
                                       s.Enabled
                                       (sprintf "%s: %s" s.Label item.Title)
                                       (fun () -> dispatch (messageFor s.Id)))) ]

                    yield! unavailableNotes surface unavailable ] ]
        :> IView

    Button.create
        [ Button.classes [ Ui.glassActionClass () ]
          Button.background Theme.transparent
          Button.borderThickness 0.0
          Button.padding 0.0
          Button.horizontalAlignment HorizontalAlignment.Stretch
          Button.horizontalContentAlignment HorizontalAlignment.Stretch
          Button.cornerRadius (Tokens.radius tokens InboxLogic.Geometry.cardRadius)
          Button.clipToBounds false
          Button.cursor Ui.hand
          Ui.accessibleName (
              if isSelected then sprintf "%s, selected" item.Title
              else sprintf "%s. Select to see its details" item.Title
          )
          Button.onClick ((fun _ -> dispatch (Select item.Id)), SubPatchOptions.Always)
          Button.content (
              GlassPanel.create
                  [ GlassPanel.baseRadius InboxLogic.Geometry.cardRadius
                    GlassPanel.elevation 1
                    GlassPanel.borderInset InboxLogic.Geometry.borderInset
                    GlassPanel.highlightScale contentHighlightScale
                    GlassPanel.tint (
                        if isSelected then (Theme.surfaces ()).Selected else (Theme.surfaces ()).Card
                    )
                    // The selection outline. Drawn in BOTH states at the same
                    // thickness — only the brush moves — so selecting a card
                    // recolours it and never resizes it.
                    GlassPanel.child (
                        Border.create
                            [ Border.borderThickness InboxLogic.Geometry.cardBorderThickness
                              Border.borderBrush (if isSelected then Theme.accent () else Theme.transparent)
                              Border.cornerRadius (Tokens.radius tokens InboxLogic.Geometry.cardRadius)
                              Border.padding (Tokens.space tokens 17.0)
                              Border.child (
                                  StackPanel.create
                                      [ StackPanel.spacing (Tokens.space tokens 12.0)
                                        StackPanel.children [ content; actionRow ] ]
                              ) ] ) ] ) ]
    :> IView

// ---- the detail panel ------------------------------------------------------

let private detailPanel
    (tokens: Tokens)
    (now: DateTime)
    (model: Model)
    (conflictsOf: InboxItem -> InboxLogic.Conflict list)
    (dispatch: Msg -> unit)
    : IView =
    let selected =
        model.Selected |> Option.bind (fun id -> model.Items |> List.tryFind (fun i -> i.Id = id))

    match selected with
    | None ->
        card
            tokens
            20.0
            [ panelHeading "Item details" None
              bulletRow
                  "•"
                  (Theme.faint ())
                  "Select an item on the left to see its source, what was extracted from it, and exactly what accepting it would change."
                  (Theme.muted ()) ]

    | Some item ->
        let analyser = InboxLogic.analyserOf item
        let conflicts = conflictsOf item
        let real = InboxLogic.realActionsOf item

        card
            tokens
            20.0
            [ DockPanel.create
                  [ DockPanel.children
                        [ Border.create
                              [ DockPanel.dock Dock.Right
                                Border.margin (10.0, 0.0, 0.0, 0.0)
                                Border.verticalAlignment VerticalAlignment.Center
                                Border.child (Ui.smallButton "Clear" (fun () -> dispatch ClearSelection)) ]
                          Ui.sectionTitle "Item details" ] ]

              TextBlock.create
                  [ TextBlock.text item.Title
                    TextBlock.fontSize Theme.Type.cardTitle
                    TextBlock.fontWeight FontWeight.SemiBold
                    TextBlock.foreground (Theme.text ())
                    TextBlock.textWrapping TextWrapping.Wrap ]

              // ---- original content ----
              Ui.sectionTitle "Original content"

              Border.create
                  [ Border.background (Theme.cardAlt ())
                    Border.cornerRadius (Tokens.radius tokens 10.0)
                    Border.padding (13.0, 10.0)
                    Border.child (
                        TextBlock.create
                            [ TextBlock.text (
                                  let body = item.Body.Trim()

                                  if body.Length > 0 then Format.truncate 400 body
                                  else sprintf "%s — no text content was captured." (InboxLogic.sourceLabel item)
                              )
                              TextBlock.fontSize Theme.Type.rowMeta
                              TextBlock.foreground (Theme.muted ())
                              TextBlock.textWrapping TextWrapping.Wrap ]
                    ) ]

              // ---- summary, under the heading its ACTUAL author earns ----
              match item.Analysis, analyser with
              | Some analysis, Some engine when analysis.Summary.Trim().Length > 0 ->
                  Ui.sectionTitle (InboxLogic.Analyser.summaryHeading engine)

                  TextBlock.create
                      [ TextBlock.text analysis.Summary
                        TextBlock.fontSize Theme.Type.rowMeta
                        TextBlock.foreground (Theme.text ())
                        TextBlock.textWrapping TextWrapping.Wrap
                        TextBlock.lineHeight (Theme.Type.rowMeta * 1.55) ]
              | _ -> ()

              // ---- extracted metadata ----
              Ui.sectionTitle "Extracted metadata"

              metadataRow
                  "Module"
                  (item.Analysis
                   |> Option.bind (fun a -> a.SuggestedModuleCode)
                   |> Option.defaultValue "Not detected")

              metadataRow "Source" (InboxLogic.sourceLabel item)
              metadataRow "Received" (Format.shortDate item.ReceivedAt + " " + Format.time item.ReceivedAt)
              metadataRow "Status" (InboxLogic.Status.label item.Status)

              match item.ProcessedAt with
              | Some processed ->
                  metadataRow "Processed" (Format.shortDate processed + " " + Format.time processed)
              | None -> ()

              match item.DocumentId with
              | Some _ -> metadataRow "Document" "Imported into the document store"
              | None -> ()

              // The redesign's confidence row would go here. There is no
              // confidence anywhere in `CaptureAnalysis`, so the row that says
              // what CAN be known takes its place.
              Ui.sectionTitle "Detection method"

              TextBlock.create
                  [ TextBlock.text (
                        match analyser with
                        | Some engine -> InboxLogic.Analyser.explanation engine
                        | None -> "Not analysed yet. Nothing has been extracted from this item."
                    )
                    TextBlock.fontSize Theme.Type.metricLabel
                    TextBlock.foreground (Theme.muted ())
                    TextBlock.textWrapping TextWrapping.Wrap
                    TextBlock.lineHeight (Theme.Type.metricLabel * 1.55) ]

              // ---- proposed changes ----
              Ui.sectionTitle "Proposed changes"

              if real.IsEmpty then
                  bulletRow
                      "•"
                      (Theme.faint ())
                      (if item.Analysis.IsSome then "Nothing needs doing with this item."
                       else "Analyse this item to see what would change.")
                      (Theme.muted ())
              else
                  StackPanel.create
                      [ StackPanel.spacing 3.0
                        StackPanel.children
                            (real
                             |> List.map (fun action ->
                                 bulletRow "→" (Theme.accent ()) (InboxLogic.actionLabel action) (Theme.text ()))) ]

              // ---- conflicts ----
              if not conflicts.IsEmpty then
                  Ui.sectionTitle "Needs confirmation"

                  StackPanel.create
                      [ StackPanel.spacing 4.0
                        StackPanel.children
                            (conflicts
                             |> List.map (fun c ->
                                 bulletRow
                                     "⚠"
                                     (Theme.brush (Theme.readableHex (conflictHex c)))
                                     (InboxLogic.Conflict.text c)
                                     (Theme.textSecondary ()))) ]

              // ---- planner impact ----
              Ui.sectionTitle "Planner impact"

              TextBlock.create
                  [ TextBlock.text (InboxLogic.plannerImpact real)
                    TextBlock.fontSize Theme.Type.metricLabel
                    TextBlock.foreground (Theme.muted ())
                    TextBlock.textWrapping TextWrapping.Wrap
                    TextBlock.lineHeight (Theme.Type.metricLabel * 1.55) ]

              // One clock reading per frame reaches even here, so the panel and
              // the card it describes can never disagree about "now".
              TextBlock.create
                  [ TextBlock.text (sprintf "Captured %s" (Format.relativeTime now item.ReceivedAt))
                    TextBlock.fontSize Theme.Type.metricLabel
                    TextBlock.foreground (Theme.faint ()) ] ]

// ---------------------------------------------------------------------------
// page
// ---------------------------------------------------------------------------

let view (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let spacing = Tokens.space tokens 22.0

    // ONE clock reading for the whole frame. Every relative timestamp on the
    // page derives from this single instant, so no two rows can disagree about
    // the same moment.
    let now = DateTime.Now

    // Conflicts computed ONCE per frame and looked up by id. The filter, the
    // card warning, the detail panel and the insight strip all read the same
    // map, so the Conflicts chip cannot select an item whose card shows nothing.
    let conflictMap =
        model.Items
        |> List.map (fun i -> i.Id, InboxLogic.conflictsFor model.Existing i)
        |> Map.ofList

    let conflictsOf (item: InboxItem) =
        conflictMap |> Map.tryFind item.Id |> Option.defaultValue []

    let visible = InboxLogic.visibleItems conflictsOf model.Filter model.Items

    let listColumn: IView =
        if model.Loading && model.Items.IsEmpty then
            StackPanel.create
                [ StackPanel.spacing spacing
                  StackPanel.children [ Ui.skeletonCard 5; Ui.skeletonCard 4; Ui.skeletonCard 4 ] ]
            :> IView
        elif visible.IsEmpty then
            // The list is empty, the PAGE is not: the metrics, the strip, the
            // filters and the detail panel all keep their places, and this card
            // explains which filter produced nothing.
            card
                tokens
                20.0
                [ panelHeading "Review queue" (Some(InboxLogic.Filter.label model.Filter))
                  bulletRow "•" (Theme.faint ()) (InboxLogic.Filter.emptyMessage model.Filter) (Theme.muted ())

                  if model.Filter <> InboxLogic.Filter.All && not model.Items.IsEmpty then
                      Border.create
                          [ Border.horizontalAlignment HorizontalAlignment.Left
                            Border.child (
                                Ui.smallButton
                                    (sprintf "Show all %d items" model.Items.Length)
                                    (fun () -> dispatch (SelectFilter InboxLogic.Filter.All))
                            ) ]

                  if model.Items.IsEmpty then
                      bulletRow
                          "•"
                          (Theme.faint ())
                          "Capture anything with Ctrl+N, drop a file anywhere in the window, or connect Moodle, email and calendars in Connections."
                          (Theme.faint ()) ]
        else
            StackPanel.create
                [ StackPanel.spacing (Tokens.space tokens 14.0)
                  StackPanel.children
                      (visible
                       |> List.map (fun item -> reviewCard tokens now model (conflictsOf item) dispatch item)) ]
            :> IView

    let workspace: IView =
        if InboxLogic.showsDetailColumn model.Width then
            Grid.create
                [ Grid.minHeight InboxLogic.Geometry.workspaceMinHeight
                  Grid.columnDefinitions (
                      sprintf
                          "%f*,%f,%f*"
                          InboxLogic.Geometry.mainLeftWeight
                          spacing
                          InboxLogic.Geometry.mainRightWeight
                  )
                  Grid.children
                      [ Border.create
                            [ Grid.column 0
                              Border.verticalAlignment VerticalAlignment.Top
                              Border.horizontalAlignment HorizontalAlignment.Stretch
                              Border.child listColumn ]
                        Border.create
                            [ Grid.column 2
                              // Top-aligned, so the panel's first line meets the
                              // first card's. Avalonia has no sticky positioning,
                              // so it scrolls with the page rather than pinning.
                              Border.verticalAlignment VerticalAlignment.Top
                              Border.horizontalAlignment HorizontalAlignment.Stretch
                              Border.child (detailPanel tokens now model conflictsOf dispatch) ] ] ]
            :> IView
        else
            // Medium and narrow: the list takes the full width and the details
            // move beneath it — the review card still comes before its details.
            StackPanel.create
                [ StackPanel.spacing spacing
                  StackPanel.children [ listColumn; detailPanel tokens now model conflictsOf dispatch ] ]
            :> IView

    let renderSection (section: InboxLogic.Section) : IView =
        match section with
        | InboxLogic.Section.Header -> header tokens
        | InboxLogic.Section.Metrics -> metricsSection tokens spacing model
        | InboxLogic.Section.Insights -> insightsSection tokens (InboxLogic.insights now conflictsOf model.Items)
        | InboxLogic.Section.Filters -> filtersSection tokens model dispatch
        | InboxLogic.Section.BulkActions -> bulkSection tokens model visible dispatch
        | InboxLogic.Section.Workspace -> workspace

    Border.create
        [ Border.onSizeChanged (
              (fun args ->
                  // Only when a layout decision would actually change, so a drag
                  // does not dispatch a message per pixel.
                  let decisions (width: float) =
                      InboxLogic.showsDetailColumn width, InboxLogic.metricColumns width

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
                                  // The banner's ink is walked against the wash
                                  // it sits on: the raw Danger role measures
                                  // 3.64:1 there on Light.
                                  let errorWash =
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
                                                                          errorWash
                                                                          (Theme.dangerHex ())
                                                                  )
                                                              )
                                                              TextBlock.verticalAlignment
                                                                  VerticalAlignment.Center
                                                              TextBlock.textWrapping TextWrapping.Wrap ] ] ]
                                        ) ]
                              | None -> ()

                              if not model.Notice.IsEmpty then
                                  Border.create
                                      [ Border.background (
                                            Theme.brush (Theme.withAlphaHex "1F" (Theme.successHex ()))
                                        )
                                        Border.cornerRadius (Tokens.radius tokens 12.0)
                                        Border.padding (16.0, 12.0)
                                        Border.child (
                                            StackPanel.create
                                                [ StackPanel.spacing 3.0
                                                  StackPanel.children
                                                      (model.Notice
                                                       |> List.map (fun line ->
                                                           TextBlock.create
                                                               [ TextBlock.text line
                                                                 TextBlock.fontSize Theme.Type.rowMeta
                                                                 TextBlock.foreground (Theme.text ())
                                                                 TextBlock.textWrapping TextWrapping.Wrap ]
                                                           :> IView)) ]
                                        ) ]

                              // The page IS this list — a section cannot be
                              // reordered or dropped without editing it.
                              yield! (InboxLogic.sectionOrder |> List.map renderSection) ] ] ) ) ]
    :> IView
