module UniversityOS.App.Features.SearchView

open System
open Elmish
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Controls

/// Search.
///
/// One docked input over a centred results column, with a preview that is
/// re-placed rather than removed as the window narrows. Every decision lives in
/// `SearchLogic`; this file draws what that module resolves.
///
/// **The engine is one FTS5 query, ranked by bm25.** It is not semantic and not
/// AI-ranked, it returns no matched spans, and it is capped rather than paged —
/// so this screen highlights locally, says when the cap is reached, and never
/// prints a relevance percentage.

// ---------------------------------------------------------------------------
// model
// ---------------------------------------------------------------------------

type Model =
    { Query: string
      /// Every result the engine returned, before the scope filter.
      All: SearchHit list
      Scope: SearchLogic.Scope
      /// Index into the flattened, filtered, grouped list.
      Selected: int option
      Searching: bool
      /// Monotonic. A reply whose generation is stale is discarded, so a slow
      /// earlier query can never overwrite a newer one.
      Generation: int
      Reference: SearchLogic.Reference
      Suggestions: string list
      Recent: string list
      Activity: ActivityEntry list
      Width: float
      Error: string option }

type External = Goto of Page

type Msg =
    | Load
    | Loaded of
        SearchLogic.Reference *
        string list *
        ActivityEntry list
    | SetQuery of string
    | RunSearch of int
    | Results of int * SearchHit list
    | ClearQuery
    | ToggleKind of EntityKind
    | ShowAllKinds
    | Select of int
    | MoveSelection of int
    | ClearSelection
    | OpenSelected
    | OpenHit of SearchHit
    | AskAssistant of SearchHit
    | UseQuery of string
    | Failed of exn
    | Resized of float
    | GotoPage of Page

// ---------------------------------------------------------------------------
// loading
// ---------------------------------------------------------------------------

/// One snapshot: the facts every result is drawn with, the deterministic
/// suggestions, and the recent activity. Loaded once, never per keystroke and
/// never while rendering.
let private loadCmd (services: AppServices) =
    Elm.ofAsync
        (async {
            let now = services.Clock.Now
            let! modules = services.Modules.GetAll()
            let! assignments = services.Assignments.GetAll()
            let! notes = services.Notes.GetAll()
            let! documents = services.Documents.GetAll()
            let! lectures = services.Lectures.GetAll()
            // `IEventRepository` has no GetAll, so a wide but bounded window is
            // read. Events outside it resolve to no facts rather than to a
            // guessed date.
            let! events = services.Events.GetBetween(now.AddYears -2, now.AddYears 2)
            let! revision = services.Revision.GetAll()
            let! activity = services.Activity.GetRecent 20

            let reference =
                SearchLogic.buildReference modules assignments notes documents lectures events revision

            return reference, SearchLogic.suggestions modules notes assignments, activity
        })
        Loaded
        Failed

let init (services: AppServices) : Model * Cmd<Msg> =
    { Query = ""
      All = []
      Scope = SearchLogic.Scope.all
      Selected = None
      Searching = false
      Generation = 0
      Reference = SearchLogic.Reference.empty
      Suggestions = []
      Recent = []
      Activity = []
      Width = 1600.0
      Error = None },
    loadCmd services

// ---------------------------------------------------------------------------
// update
// ---------------------------------------------------------------------------

/// The visible list, after the scope filter and in the pinned group order.
let private visible (model: Model) =
    model.All |> SearchLogic.applyScope model.Scope |> SearchLogic.groupResults

let private flat (model: Model) = visible model |> SearchLogic.flatten

let update (services: AppServices) (msg: Msg) (model: Model) : Model * Cmd<Msg> * External option =
    let plain m = m, Cmd.none, None

    /// A selection that no longer points at the same entity is cleared.
    let keepSelection (before: Model) (after: Model) =
        let previous = SearchLogic.selectedHit (flat before) before.Selected
        { after with Selected = SearchLogic.reconcileSelection previous (flat after) }

    match msg with
    | Load -> model, loadCmd services, None

    | Loaded(reference, suggestions, activity) ->
        plain
            { model with
                Reference = reference
                Suggestions = suggestions
                Activity = activity
                Error = None }

    | SetQuery query ->
        let generation = model.Generation + 1

        if not (SearchLogic.isRunnable query) then
            // Below the floor nothing runs, and the previous results go with the
            // query that produced them.
            plain
                { model with
                    Query = query
                    All = []
                    Selected = None
                    Searching = false
                    Generation = generation
                    Error = None }
        else
            { model with Query = query; Searching = true; Generation = generation; Error = None },
            Elm.delayed SearchLogic.debounceMs (RunSearch generation),
            None

    // Fires after the debounce. A newer keystroke has already bumped the
    // generation, so only the last one in a burst reaches the repository.
    | RunSearch generation ->
        if generation <> model.Generation || not (SearchLogic.isRunnable model.Query) then
            plain model
        else
            model,
            Elm.ofAsync
                (async { return! services.Search.Query(SearchLogic.normalise model.Query, SearchLogic.resultLimit) })
                (fun hits -> Results(generation, hits))
                Failed,
            None

    | Results(generation, hits) ->
        if generation <> model.Generation then
            // A stale reply. Discarded rather than rendered over a newer query.
            plain model
        else
            let after =
                { model with
                    All = hits
                    Searching = false
                    Error = None
                    Recent = SearchLogic.remember model.Query (not hits.IsEmpty) model.Recent }

            plain (keepSelection model after)

    | ClearQuery ->
        plain
            { model with
                Query = ""
                All = []
                Selected = None
                Searching = false
                Generation = model.Generation + 1
                Error = None }

    | ToggleKind kind ->
        let after = { model with Scope = SearchLogic.Scope.toggle kind model.Scope }
        plain (keepSelection model after)

    | ShowAllKinds ->
        let after = { model with Scope = SearchLogic.Scope.all }
        plain (keepSelection model after)

    | Select index -> plain { model with Selected = Some index }

    | MoveSelection delta ->
        plain { model with Selected = SearchLogic.moveSelection (flat model).Length delta model.Selected }

    | ClearSelection -> plain { model with Selected = None }

    | OpenSelected ->
        match SearchLogic.selectedHit (flat model) model.Selected with
        | Some hit -> model, Cmd.none, Some(Goto(Page.forEntity hit.Kind))
        | None -> plain model

    | OpenHit hit -> model, Cmd.none, Some(Goto(Page.forEntity hit.Kind))

    // The handoff is **text in the composer**. The shell turns this into a
    // `ChatView.Prefill`, which the Assistant labels as a prefilled prompt.
    | AskAssistant _ -> plain model

    | UseQuery query ->
        let generation = model.Generation + 1

        { model with Query = query; Searching = true; Generation = generation; Selected = None; Error = None },
        Cmd.ofMsg (RunSearch generation),
        None

    | Failed ex -> plain { model with Searching = false; Error = Some ex.Message }

    | Resized width -> plain { model with Width = width }
    | GotoPage page -> model, Cmd.none, Some(Goto page)

// ---------------------------------------------------------------------------
// view — shared treatments
// ---------------------------------------------------------------------------

let private weight (w: int) = enum<FontWeight> w

let private sectionCaption (text: string) : IView =
    TextBlock.create
        [ TextBlock.text (text.ToUpperInvariant())
          TextBlock.fontSize 10.5
          TextBlock.fontWeight (weight 600)
          TextBlock.foreground (Theme.label ())
          TextBlock.textWrapping TextWrapping.Wrap ]
    :> IView

let private bulletRow (glyph: string) (glyphBrush: IBrush) (text: string) (textBrush: IBrush) : IView =
    Grid.create
        [ Grid.columnDefinitions SearchLogic.Geometry.bulletColumns
          Grid.margin (0.0, 1.0)
          Grid.children
              [ TextBlock.create
                    [ Grid.column 0
                      TextBlock.text glyph
                      TextBlock.fontSize 11.0
                      TextBlock.foreground glyphBrush
                      TextBlock.verticalAlignment VerticalAlignment.Top
                      Ui.decorative () ]
                TextBlock.create
                    [ Grid.column 2
                      TextBlock.text text
                      TextBlock.fontSize 11.5
                      TextBlock.foreground textBrush
                      TextBlock.textWrapping TextWrapping.Wrap ] ] ]
    :> IView

/// Text with the matched runs marked — **bold as well as tinted**, so the match
/// is never carried by colour alone.
let private highlighted (query: string) (text: string) (size: float) (baseBrush: IBrush) (bold: bool) : IView =
    let parts = SearchLogic.segments query text
    let accent = Theme.accentHex ()
    let surface = (Theme.surfaces ()).Card

    let wash =
        Theme.compositeOver surface (Theme.withAlphaHex SearchLogic.Geometry.highlightAlpha accent)

    let matchedInk = Theme.brush (Theme.readableOnColor wash accent)

    if parts |> List.forall (fun p -> not p.Matched) then
        TextBlock.create
            [ TextBlock.text text
              TextBlock.fontSize size
              TextBlock.fontWeight (if bold then FontWeight.SemiBold else FontWeight.Normal)
              TextBlock.foreground baseBrush
              TextBlock.textWrapping TextWrapping.Wrap ]
        :> IView
    else
        WrapPanel.create
            [ WrapPanel.children
                  (parts
                   |> List.map (fun part ->
                       TextBlock.create
                           [ TextBlock.text part.Text
                             TextBlock.fontSize size
                             TextBlock.fontWeight (
                                 if part.Matched then FontWeight.Bold
                                 elif bold then FontWeight.SemiBold
                                 else FontWeight.Normal
                             )
                             TextBlock.foreground (if part.Matched then matchedInk else baseBrush)
                             TextBlock.background (
                                 if part.Matched then
                                     Theme.brush (Theme.withAlphaHex SearchLogic.Geometry.highlightAlpha accent)
                                 else
                                     Theme.transparent
                             )
                             TextBlock.textWrapping TextWrapping.Wrap ]
                       :> IView)) ]
        :> IView

let private glassAction
    (tokens: Tokens)
    (label: string)
    (primary: bool)
    (accessibleName: string)
    (onClick: unit -> unit)
    : IView =
    // Geometry only. The glass, the veil, the accent floor and the ink rule all
    // live in `Ui.glassAction` — see the note there on why eleven copies of
    // this button existed and only one of them kept its accent.
    Ui.glassAction
        tokens
        { Ui.GlassActionMetrics.Padding = (14.0, 8.0)
          Ui.GlassActionMetrics.Margin = (0.0, 0.0, 8.0, 8.0) }
        label
        primary
        true
        accessibleName
        onClick

/// A pill. **Identical geometry selected and unselected** — only the fill and
/// the border colour change, so a filter row never reflows as it is used.
let private filterChip (tokens: Tokens) (label: string) (count: int option) (hex: string) (selected: bool) (onClick: unit -> unit) : IView =
    let surface = (Theme.surfaces ()).Card

    let wash =
        Theme.compositeOver surface (Theme.withAlphaHex SearchLogic.Geometry.chipFillAlpha hex)

    let ink = Theme.brush (Theme.readableOnColor wash hex)

    let text =
        match count with
        | Some n -> sprintf "%s  %d" label n
        | None -> label

    Button.create
        [ Button.background (
              if selected then
                  Theme.brush (Theme.withAlphaHex SearchLogic.Geometry.chipFillAlpha hex)
              else
                  Theme.transparent
          )
          // Reserved in BOTH states.
          Button.borderThickness 1.0
          Button.borderBrush (if selected then Theme.brush hex else Theme.border ())
          Button.foreground (if selected then ink else Theme.textSecondary ())
          Button.cornerRadius (Tokens.radius tokens 999.0)
          Button.minHeight 30.0
          Button.padding (13.0, 6.0)
          Button.fontSize 11.5
          Button.fontWeight FontWeight.SemiBold
          Button.margin (0.0, 0.0, 8.0, 8.0)
          Button.cursor Ui.hand
          Button.content text
          Ui.accessibleName (
              if selected then sprintf "%s filter, on" label else sprintf "%s filter, off" label
          )
          Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always) ]
    :> IView

let private quietLink (label: string) (onClick: unit -> unit) : IView =
    Button.create
        [ Button.content label
          Button.background Theme.transparent
          Button.borderThickness 0.0
          Button.padding (0.0, 2.0)
          Button.fontSize 12.5
          Button.foreground (Theme.textSecondary ())
          Button.horizontalContentAlignment HorizontalAlignment.Left
          Button.cursor Ui.hand
          Ui.accessibleName label
          Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always) ]
    :> IView

// ---------------------------------------------------------------------------
// the search input
// ---------------------------------------------------------------------------

/// Docked, so it never scrolls out of reach, and a **fixed height** across idle,
/// loading, populated and error.
let private searchInput (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let state = SearchLogic.queryState model.Query model.Searching model.Error (not model.All.IsEmpty)

    let glyph, glyphBrush =
        match state with
        | SearchLogic.QueryState.Running -> "◍", Theme.accent ()
        | SearchLogic.QueryState.Failed _ -> "!", Theme.danger ()
        | _ -> "⌕", Theme.muted ()

    Border.create
        [ Border.background (Theme.brush (Ui.hexOf (Theme.surfaces ()).Card))
          Border.borderThickness 1.0
          Border.borderBrush (Theme.border ())
          Border.cornerRadius (Tokens.radius tokens SearchLogic.Geometry.inputRadius)
          Border.padding (22.0, 14.0)
          Border.minHeight SearchLogic.Geometry.inputMinHeight
          Border.child (
              DockPanel.create
                  [ DockPanel.children
                        [ TextBlock.create
                              [ DockPanel.dock Dock.Left
                                TextBlock.text glyph
                                TextBlock.fontSize 19.0
                                TextBlock.foreground glyphBrush
                                TextBlock.margin (0.0, 0.0, 14.0, 0.0)
                                TextBlock.verticalAlignment VerticalAlignment.Center
                                Ui.decorative () ]

                          StackPanel.create
                              [ DockPanel.dock Dock.Right
                                StackPanel.orientation Orientation.Horizontal
                                StackPanel.spacing 8.0
                                StackPanel.verticalAlignment VerticalAlignment.Center
                                StackPanel.children
                                    [ if SearchLogic.normalise(model.Query).Length > 0 then
                                          Ui.smallButton "Clear" (fun () -> dispatch ClearQuery)

                                      Border.create
                                          [ Border.background (Theme.cardAlt ())
                                            Border.cornerRadius (Tokens.radius tokens 6.0)
                                            Border.padding (8.0, 4.0)
                                            Border.isHitTestVisible false
                                            Border.child (
                                                TextBlock.create
                                                    [ TextBlock.text SearchLogic.shortcutBadge
                                                      TextBlock.fontSize 10.5
                                                      TextBlock.foreground (Theme.faint ()) ]
                                            ) ] ] ]

                          Border.create
                              [ // **Stretch, not Center.** Centring the wrapper let
                                // the editor occupy only its own text height, so a
                                // click on the empty space above or below it landed
                                // on decoration and focused nothing — the field
                                // looked bigger than it was. Stretching the wrapper
                                // and the editor makes the whole visible field the
                                // hit target, and `verticalContentAlignment` then
                                // puts the text back in the middle of it.
                                Border.verticalAlignment VerticalAlignment.Stretch
                                Border.child (
                                    TextBox.create
                                        [ TextBox.text model.Query
                                          TextBox.watermark SearchLogic.watermark
                                          TextBox.fontSize 17.0
                                          TextBox.background Theme.transparent
                                          TextBox.borderThickness 0.0
                                          TextBox.padding 0.0
                                          // Fluent styles the template part directly,
                                          // so transparent-on-the-control is not
                                          // enough once focused. See Ui.bareInputClass.
                                          TextBox.classes [ Ui.bareInputClass () ]
                                          TextBox.verticalAlignment VerticalAlignment.Stretch
                                          TextBox.verticalContentAlignment VerticalAlignment.Center
                                          TextBox.foreground (Theme.text ())
                                          Ui.accessibleName "Search"
                                          TextBox.onTextChanged ((fun t -> dispatch (SetQuery t)), SubPatchOptions.OnChangeOf model.Query)
                                          TextBox.onKeyDown (
                                              (fun e ->
                                                  match e.Key with
                                                  | Key.Down ->
                                                      dispatch (MoveSelection 1)
                                                      e.Handled <- true
                                                  | Key.Up ->
                                                      dispatch (MoveSelection -1)
                                                      e.Handled <- true
                                                  | Key.Enter ->
                                                      dispatch OpenSelected
                                                      e.Handled <- true
                                                  | Key.Escape ->
                                                      // Two-stage: the selection
                                                      // first, then the query.
                                                      if model.Selected.IsSome then dispatch ClearSelection
                                                      else dispatch ClearQuery

                                                      e.Handled <- true
                                                  | _ -> ()),
                                              SubPatchOptions.Always
                                          ) ]
                                ) ] ] ]
          ) ]
    :> IView

// ---------------------------------------------------------------------------
// filters
// ---------------------------------------------------------------------------

let private filterRow (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let counts = SearchLogic.countsByKind model.All |> Map.ofList
    let hasResults = not model.All.IsEmpty

    WrapPanel.create
        [ WrapPanel.children
              [ filterChip
                    tokens
                    "All"
                    (if hasResults then Some model.All.Length else None)
                    (Theme.accentHex ())
                    (SearchLogic.Scope.isAll model.Scope)
                    (fun () -> dispatch ShowAllKinds)

                for kind in SearchLogic.searchableKinds do
                    filterChip
                        tokens
                        (SearchLogic.groupLabel kind)
                        (if hasResults then Some(Map.tryFind kind counts |> Option.defaultValue 0) else None)
                        (Theme.entityKindHex kind)
                        (Set.contains kind model.Scope)
                        (fun () -> dispatch (ToggleKind kind)) ] ]
    :> IView

// ---------------------------------------------------------------------------
// result cards
// ---------------------------------------------------------------------------

let private resultCard
    (tokens: Tokens)
    (model: Model)
    (dispatch: Msg -> unit)
    (index: int)
    (isBest: bool)
    (hit: SearchHit)
    : IView =
    let selected = model.Selected = Some index
    let facts = SearchLogic.factsFor model.Reference hit
    let kindHex = Theme.entityKindHex hit.Kind
    let accent = Theme.accentHex ()

    Border.create
        [ Border.background (
              if selected then
                  Theme.brush (Theme.withAlphaHex SearchLogic.Geometry.selectedFillAlpha accent)
              else
                  Theme.brush (Ui.hexOf (Theme.surfaces ()).Card)
          )
          // Reserved in BOTH states, so selecting recolours and never resizes.
          Border.borderThickness 1.0
          Border.borderBrush (
              if selected then
                  Theme.brush (Theme.overlayEdgeHex SearchLogic.Geometry.selectedEdgeAlpha accent)
              else
                  Theme.border ()
          )
          Border.cornerRadius (Tokens.radius tokens SearchLogic.Geometry.cardRadius)
          Border.padding (18.0, 16.0)
          Border.margin (0.0, 0.0, 0.0, 8.0)
          Border.cursor Ui.hand
          Ui.accessibleName (sprintf "%s: %s" (GraphLogic.kindLabel hit.Kind) hit.Title)
          Border.onPointerPressed ((fun _ -> dispatch (Select index)), SubPatchOptions.Always)
          Border.child (
              DockPanel.create
                  [ DockPanel.children
                        [ // The kind tile: glyph as well as colour.
                          Border.create
                              [ DockPanel.dock Dock.Left
                                Border.width SearchLogic.Geometry.iconTile
                                Border.height SearchLogic.Geometry.iconTile
                                Border.background (Theme.cardAlt ())
                                Border.cornerRadius (Tokens.radius tokens SearchLogic.Geometry.iconTileRadius)
                                Border.margin (0.0, 0.0, 12.0, 0.0)
                                Border.verticalAlignment VerticalAlignment.Top
                                Border.child (
                                    TextBlock.create
                                        [ TextBlock.text (GraphLogic.kindGlyph hit.Kind)
                                          TextBlock.fontSize 15.0
                                          TextBlock.foreground (
                                              Theme.brush (
                                                  Theme.readableOnColor (Theme.surfaces ()).CardAlt kindHex
                                              )
                                          )
                                          TextBlock.horizontalAlignment HorizontalAlignment.Center
                                          TextBlock.verticalAlignment VerticalAlignment.Center ]
                                ) ]

                          StackPanel.create
                              [ StackPanel.spacing 5.0
                                StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                                StackPanel.children
                                    [ // Title, matched runs marked.
                                      highlighted
                                          model.Query
                                          hit.Title
                                          14.0
                                          (Theme.text ())
                                          true

                                      WrapPanel.create
                                          [ WrapPanel.children
                                                [ TextBlock.create
                                                      [ TextBlock.text ((GraphLogic.kindLabel hit.Kind).ToUpperInvariant())
                                                        TextBlock.fontSize 10.0
                                                        TextBlock.fontWeight FontWeight.SemiBold
                                                        TextBlock.foreground (Theme.faint ())
                                                        TextBlock.margin (0.0, 0.0, 10.0, 0.0)
                                                        TextBlock.verticalAlignment VerticalAlignment.Center ]

                                                  match facts.ModuleLabel, facts.ModuleHex with
                                                  | Some label, Some hex ->
                                                      let wash =
                                                          Theme.compositeOver
                                                              (Theme.surfaces ()).Card
                                                              (Theme.withAlphaHex
                                                                  SearchLogic.Geometry.chipFillAlpha
                                                                  hex)

                                                      Border.create
                                                          [ Border.background (
                                                                Theme.brush (
                                                                    Theme.withAlphaHex
                                                                        SearchLogic.Geometry.chipFillAlpha
                                                                        hex
                                                                )
                                                            )
                                                            Border.cornerRadius (Tokens.radius tokens 999.0)
                                                            Border.padding (8.0, 2.0)
                                                            Border.margin (0.0, 0.0, 10.0, 0.0)
                                                            Border.isHitTestVisible false
                                                            Border.child (
                                                                TextBlock.create
                                                                    [ TextBlock.text label
                                                                      TextBlock.fontSize 10.0
                                                                      TextBlock.fontWeight FontWeight.SemiBold
                                                                      TextBlock.foreground (
                                                                          Theme.brush (
                                                                              Theme.readableOnColor wash hex
                                                                          )
                                                                      ) ] ) ]
                                                  | _ -> ()

                                                  match facts.TypeLabel with
                                                  | Some label ->
                                                      TextBlock.create
                                                          [ TextBlock.text label
                                                            TextBlock.fontSize 10.0
                                                            TextBlock.foreground (Theme.faint ())
                                                            TextBlock.margin (0.0, 0.0, 10.0, 0.0)
                                                            TextBlock.verticalAlignment VerticalAlignment.Center ]
                                                  | None -> ()

                                                  match facts.Meta with
                                                  | Some meta ->
                                                      TextBlock.create
                                                          [ TextBlock.text meta
                                                            TextBlock.fontSize 10.0
                                                            TextBlock.foreground (Theme.faint ())
                                                            TextBlock.margin (0.0, 0.0, 10.0, 0.0)
                                                            TextBlock.verticalAlignment VerticalAlignment.Center ]
                                                  | None -> ()

                                                  // The ONLY rank claim the
                                                  // ordering supports. No
                                                  // percentage, ever.
                                                  if isBest then
                                                      TextBlock.create
                                                          [ TextBlock.text SearchLogic.bestMatchLabel
                                                            TextBlock.fontSize 10.0
                                                            TextBlock.fontWeight FontWeight.SemiBold
                                                            TextBlock.foreground (
                                                                Theme.brush (
                                                                    Theme.readableOnColor
                                                                        (Theme.surfaces ()).Card
                                                                        (Theme.successHex ())
                                                                )
                                                            )
                                                            TextBlock.verticalAlignment VerticalAlignment.Center ] ] ]

                                      if hit.Snippet.Trim().Length > 0 then
                                          highlighted
                                              model.Query
                                              (Format.truncate 220 hit.Snippet)
                                              12.5
                                              (Theme.textSecondary ())
                                              false ] ] ] ]
          ) ]
    :> IView

// ---------------------------------------------------------------------------
// the preview
// ---------------------------------------------------------------------------

let private preview (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) (hit: SearchHit) : IView =
    let facts = SearchLogic.factsFor model.Reference hit
    let kindHex = Theme.entityKindHex hit.Kind

    let detail (label: string) (value: string) : IView =
        Grid.create
            [ Grid.columnDefinitions "Auto,10,*"
              Grid.margin (0.0, 1.0)
              Grid.children
                  [ TextBlock.create
                        [ Grid.column 0
                          TextBlock.text label
                          TextBlock.fontSize 11.0
                          TextBlock.fontWeight FontWeight.SemiBold
                          TextBlock.foreground (Theme.textSecondary ()) ]
                    TextBlock.create
                        [ Grid.column 2
                          TextBlock.text value
                          TextBlock.fontSize 10.5
                          TextBlock.foreground (Theme.faint ())
                          TextBlock.textWrapping TextWrapping.Wrap ] ] ]
        :> IView

    GlassPanel.create
        [ GlassPanel.baseRadius DashboardLogic.Card.radius
          GlassPanel.elevation 1
          GlassPanel.highlightScale SearchLogic.Geometry.contentHighlight
          GlassPanel.tint (Theme.surfaces ()).Card
          GlassPanel.child (
              Border.create
                  [ Border.padding (Tokens.space tokens 18.0)
                    Border.child (
                        StackPanel.create
                            [ StackPanel.spacing 12.0
                              StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                              StackPanel.children
                                  [ sectionCaption "Selected result"

                                    StackPanel.create
                                        [ StackPanel.spacing 4.0
                                          StackPanel.children
                                              [ StackPanel.create
                                                    [ StackPanel.orientation Orientation.Horizontal
                                                      StackPanel.spacing 8.0
                                                      StackPanel.children
                                                          [ TextBlock.create
                                                                [ TextBlock.text (GraphLogic.kindGlyph hit.Kind)
                                                                  TextBlock.fontSize 13.0
                                                                  TextBlock.foreground (
                                                                      Theme.brush (
                                                                          Theme.readableOnColor
                                                                              (Theme.surfaces ()).Card
                                                                              kindHex
                                                                      )
                                                                  )
                                                                  Ui.decorative () ]
                                                            TextBlock.create
                                                                [ TextBlock.text (GraphLogic.kindLabel hit.Kind)
                                                                  TextBlock.fontSize 11.0
                                                                  TextBlock.fontWeight FontWeight.SemiBold
                                                                  TextBlock.foreground (Theme.textSecondary ()) ] ] ]

                                                TextBlock.create
                                                    [ TextBlock.text hit.Title
                                                      TextBlock.fontSize 15.0
                                                      TextBlock.fontWeight FontWeight.SemiBold
                                                      TextBlock.foreground (Theme.text ())
                                                      TextBlock.textWrapping TextWrapping.Wrap ] ] ]

                                    if hit.Snippet.Trim().Length > 0 then
                                        StackPanel.create
                                            [ StackPanel.spacing 5.0
                                              StackPanel.children
                                                  [ sectionCaption "Matched text"
                                                    Border.create
                                                        [ Border.background (Theme.cardAlt ())
                                                          Border.cornerRadius (Tokens.radius tokens 10.0)
                                                          Border.padding (12.0, 10.0)
                                                          Border.child (
                                                              highlighted
                                                                  model.Query
                                                                  hit.Snippet
                                                                  11.5
                                                                  (Theme.textSecondary ())
                                                                  false
                                                          ) ] ] ]

                                    // Only what was genuinely resolved. A field
                                    // that could not be resolved is absent, not
                                    // dashed.
                                    if facts.ModuleLabel.IsSome || facts.Meta.IsSome || facts.TypeLabel.IsSome then
                                        StackPanel.create
                                            [ StackPanel.spacing 5.0
                                              StackPanel.children
                                                  [ sectionCaption "Details"

                                                    match facts.ModuleLabel with
                                                    | Some label -> detail "Module" label
                                                    | None -> ()

                                                    match facts.TypeLabel with
                                                    | Some label -> detail "File" label
                                                    | None -> ()

                                                    match facts.Meta with
                                                    | Some meta -> detail "Date" meta
                                                    | None -> () ] ]

                                    StackPanel.create
                                        [ StackPanel.spacing 5.0
                                          StackPanel.children
                                              [ sectionCaption "Actions"

                                                WrapPanel.create
                                                    [ WrapPanel.children
                                                          [ glassAction
                                                                tokens
                                                                (sprintf "Open %s" ((GraphLogic.kindLabel hit.Kind).ToLowerInvariant()))
                                                                true
                                                                (sprintf "Open the %s screen" ((GraphLogic.kindLabel hit.Kind).ToLowerInvariant()))
                                                                (fun () -> dispatch (OpenHit hit))

                                                            glassAction
                                                                tokens
                                                                "View in Graph"
                                                                false
                                                                "Open the knowledge graph"
                                                                (fun () -> dispatch (GotoPage Page.Graph))

                                                            glassAction
                                                                tokens
                                                                "Ask the Assistant"
                                                                false
                                                                "Prefill the Assistant with a question about this result"
                                                                (fun () -> dispatch (AskAssistant hit)) ] ]

                                                TextBlock.create
                                                    [ TextBlock.text SearchLogic.assistantNote
                                                      TextBlock.fontSize 10.5
                                                      TextBlock.foreground (Theme.faint ())
                                                      TextBlock.textWrapping TextWrapping.Wrap ]

                                                TextBlock.create
                                                    [ TextBlock.text SearchLogic.navigationNote
                                                      TextBlock.fontSize 10.5
                                                      TextBlock.foreground (Theme.faint ())
                                                      TextBlock.textWrapping TextWrapping.Wrap ] ] ] ] ] ) ] ) ]
    :> IView

// ---------------------------------------------------------------------------
// the empty state
// ---------------------------------------------------------------------------

let private emptyState (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let region (r: SearchLogic.EmptyRegion) : IView list =
        match r with
        | SearchLogic.EmptyRegion.Suggestions ->
            match model.Suggestions with
            | [] -> []
            | prompts ->
                [ sectionCaption SearchLogic.suggestionHeading
                  WrapPanel.create
                      [ WrapPanel.children
                            (prompts
                             |> List.map (fun p ->
                                 filterChip tokens p None (Theme.accentHex ()) false (fun () -> dispatch (UseQuery p)))) ] ]

        | SearchLogic.EmptyRegion.Recent ->
            [ sectionCaption SearchLogic.recentHeading

              if model.Recent.IsEmpty then
                  TextBlock.create
                      [ TextBlock.text SearchLogic.recentEmptyNotice
                        TextBlock.fontSize 11.5
                        TextBlock.foreground (Theme.faint ())
                        TextBlock.textWrapping TextWrapping.Wrap ]
              else
                  StackPanel.create
                      [ StackPanel.spacing 2.0
                        StackPanel.children
                            (model.Recent |> List.map (fun q -> quietLink ("⌕  " + q) (fun () -> dispatch (UseQuery q)))) ] ]

        | SearchLogic.EmptyRegion.Activity ->
            match SearchLogic.navigableActivity model.Activity with
            | [] -> []
            | entries ->
                [ sectionCaption SearchLogic.activityHeading
                  WrapPanel.create
                      [ WrapPanel.children
                            (entries
                             |> List.map (fun e ->
                                 let kind = (Option.get e.Entity).Kind

                                 quietLink
                                     (sprintf "%s  %s" (GraphLogic.kindGlyph kind) (Format.truncate 40 e.Title))
                                     (fun () -> dispatch (GotoPage(Page.forEntity kind))))) ] ]

        | SearchLogic.EmptyRegion.Scope ->
            [ sectionCaption "What is searched"

              for kind in SearchLogic.searchableKinds do
                  bulletRow
                      (GraphLogic.kindGlyph kind)
                      (Theme.brush (Theme.readableOnColor (Theme.surfaces ()).Card (Theme.entityKindHex kind)))
                      (sprintf "%s — %s" (SearchLogic.groupLabel kind) (SearchLogic.indexedBody kind))
                      (Theme.textSecondary ()) ]

    StackPanel.create
        [ StackPanel.spacing 24.0
          StackPanel.horizontalAlignment HorizontalAlignment.Stretch
          StackPanel.children
              (SearchLogic.emptyOrder
               |> List.collect (fun r ->
                   match region r with
                   | [] -> []
                   | children ->
                       [ StackPanel.create [ StackPanel.spacing 8.0; StackPanel.children children ] :> IView ])) ]
    :> IView

// ---------------------------------------------------------------------------
// page
// ---------------------------------------------------------------------------

let view (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let grouped = visible model
    let flattened = SearchLogic.flatten grouped
    let placement = SearchLogic.previewPlacement model.Width
    let state = SearchLogic.queryState model.Query model.Searching model.Error (not model.All.IsEmpty)
    let selectedHit = SearchLogic.selectedHit flattened model.Selected

    let previewView =
        selectedHit |> Option.map (preview tokens model dispatch)

    /// Every result card, with the preview inlined under the selection at the
    /// narrow breakpoint.
    let resultViews: IView list =
        [ let mutable index = 0

          for kind, hits in grouped do
              StackPanel.create
                  [ StackPanel.spacing 8.0
                    StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                    StackPanel.children
                        [ StackPanel.create
                              [ StackPanel.orientation Orientation.Horizontal
                                StackPanel.spacing 8.0
                                StackPanel.children
                                    [ sectionCaption (SearchLogic.groupLabel kind)
                                      TextBlock.create
                                          [ TextBlock.text (string hits.Length)
                                            TextBlock.fontSize 10.5
                                            TextBlock.foreground (Theme.faint ()) ] ] ]

                          StackPanel.create
                              [ StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                                StackPanel.children
                                    [ for hit in hits do
                                          let here = index
                                          index <- index + 1

                                          resultCard tokens model dispatch here (here = 0) hit

                                          if placement = SearchLogic.PreviewPlacement.Inline
                                             && model.Selected = Some here then
                                              match previewView with
                                              | Some v ->
                                                  Border.create [ Border.margin (0.0, 0.0, 0.0, 12.0); Border.child v ]
                                              | None -> () ] ] ] ] ]

    let body: IView list =
        [ match state with
          | SearchLogic.QueryState.Failed message ->
              let wash =
                  Theme.compositeOver
                      (Theme.surfaces ()).Card
                      (Theme.withAlphaHex "22" (Theme.dangerHex ()))

              Border.create
                  [ Border.background (Theme.brush (Theme.withAlphaHex "22" (Theme.dangerHex ())))
                    Border.borderThickness 1.0
                    Border.borderBrush (Theme.brush (Theme.overlayEdgeHex "55" (Theme.dangerHex ())))
                    Border.cornerRadius (Tokens.radius tokens 12.0)
                    Border.padding (16.0, 12.0)
                    Border.child (
                        StackPanel.create
                            [ StackPanel.spacing 8.0
                              StackPanel.children
                                  [ TextBlock.create
                                        [ TextBlock.text ("The search could not be run: " + message)
                                          TextBlock.fontSize Theme.Type.rowMeta
                                          TextBlock.foreground (
                                              Theme.brush (Theme.readableOnColor wash (Theme.dangerHex ()))
                                          )
                                          TextBlock.textWrapping TextWrapping.Wrap ]
                                    Border.create
                                        [ Border.horizontalAlignment HorizontalAlignment.Left
                                          Border.child (
                                              Ui.smallButton "Try again" (fun () -> dispatch (UseQuery model.Query))
                                          ) ] ] ]
                    ) ]

          | SearchLogic.QueryState.Idle -> emptyState tokens model dispatch

          | SearchLogic.QueryState.TooShort ->
              StackPanel.create
                  [ StackPanel.spacing 24.0
                    StackPanel.children
                        [ bulletRow "•" (Theme.faint ()) SearchLogic.tooShortNotice (Theme.muted ())
                          emptyState tokens model dispatch ] ]

          | SearchLogic.QueryState.Running ->
              bulletRow "◍" (Theme.accent ()) "Searching…" (Theme.muted ())

          | SearchLogic.QueryState.NoResults ->
              StackPanel.create
                  [ StackPanel.spacing 10.0
                    StackPanel.children
                        [ TextBlock.create
                              [ TextBlock.text (SearchLogic.summaryText model.Query 0 0)
                                TextBlock.fontSize 13.0
                                TextBlock.foreground (Theme.muted ())
                                TextBlock.textWrapping TextWrapping.Wrap ]
                          bulletRow
                              "•"
                              (Theme.faint ())
                              "Whole words are matched, and only the last word is completed as you type. Try a shorter word."
                              (Theme.faint ()) ] ]

          | SearchLogic.QueryState.Results ->
              StackPanel.create
                  [ StackPanel.spacing 10.0
                    StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                    StackPanel.children
                        [ TextBlock.create
                              [ TextBlock.text (
                                    SearchLogic.summaryText model.Query flattened.Length grouped.Length
                                )
                                TextBlock.fontSize 12.5
                                TextBlock.foreground (Theme.muted ())
                                TextBlock.textWrapping TextWrapping.Wrap ]

                          match SearchLogic.capNotice model.All with
                          | Some notice -> bulletRow "•" (Theme.faint ()) notice (Theme.faint ())
                          | None -> ()

                          if flattened.IsEmpty then
                              bulletRow
                                  "•"
                                  (Theme.faint ())
                                  "Every match is hidden by the filters above. Choose All to see them."
                                  (Theme.muted ())
                          else
                              StackPanel.create
                                  [ StackPanel.spacing 18.0
                                    StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                                    StackPanel.children resultViews ] ] ] ]

    let column: IView =
        Ui.scrollNoChrome (
            Border.create
                [ Border.padding (32.0, 24.0, 32.0, 60.0)
                  Border.child (
                      StackPanel.create
                          [ StackPanel.spacing 22.0
                            StackPanel.maxWidth SearchLogic.Geometry.columnWidth
                            StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                            StackPanel.children
                                [ yield! body

                                  // Below the results at the medium breakpoint.
                                  if placement = SearchLogic.PreviewPlacement.Below then
                                      match previewView with
                                      | Some v -> v
                                      | None -> () ] ] ) ]
        )

    let content: IView =
        if placement = SearchLogic.PreviewPlacement.Rail && previewView.IsSome then
            Grid.create
                [ Grid.columnDefinitions (sprintf "*,%f" SearchLogic.Geometry.previewWidth)
                  Grid.children
                      [ Border.create [ Grid.column 0; Border.child column ]
                        Border.create
                            [ Grid.column 1
                              Border.padding (0.0, 24.0, 24.0, 24.0)
                              Border.child (
                                  Ui.scrollNoChrome (Option.get previewView)
                              ) ] ] ]
            :> IView
        else
            column

    Border.create
        [ Border.onSizeChanged (
              (fun args ->
                  if SearchLogic.previewPlacement model.Width <> SearchLogic.previewPlacement args.NewSize.Width then
                      dispatch (Resized args.NewSize.Width)),
              SubPatchOptions.Always
          )
          Border.child (
              DockPanel.create
                  [ DockPanel.children
                        [ Border.create
                              [ DockPanel.dock Dock.Top
                                Border.padding (32.0, 24.0, 32.0, 0.0)
                                Border.child (
                                    Border.create
                                        [ Border.maxWidth SearchLogic.Geometry.columnWidth
                                          Border.child (searchInput tokens model dispatch) ]
                                ) ]

                          Border.create
                              [ DockPanel.dock Dock.Top
                                Border.padding (32.0, 14.0, 32.0, 0.0)
                                Border.child (
                                    Border.create
                                        [ Border.maxWidth SearchLogic.Geometry.columnWidth
                                          Border.child (filterRow tokens model dispatch) ]
                                ) ]

                          content ] ]
          ) ]
    :> IView
