module UniversityOS.App.Features.GraphView

open System
open Elmish
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Shapes
open Avalonia.Layout
open Avalonia.Media
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Design

/// The knowledge graph: every entity the application stores, and every real
/// relationship between them.
///
/// **This is an interaction screen.** The engine it already had is preserved
/// whole — the deterministic force-directed layout, zoom, pan, kind filters,
/// search, one-hop neighbourhood focus and entity navigation. What the redesign
/// adds is a different composition: the canvas fills the page and the controls
/// float over it, plus a minimap and a selected-node panel.
///
/// Two things are new, and both are stated plainly in the UI:
///
/// * **Session-only node dragging.** Dragging writes to an in-memory override
///   map. Nothing persists it, there is no "save layout" action, and Reset
///   Layout clears it.
/// * **The graph is undirected** — see `GraphLogic.normaliseEdge`. No arrowheads.

// ---------------------------------------------------------------------------
// model
// ---------------------------------------------------------------------------

type Model =
    { Nodes: GraphLogic.GraphNode list
      Edges: (string * string) list
      Adjacency: Map<string, Set<string>>
      /// What the deterministic layout produced.
      BaseLayout: Map<string, float * float>
      /// Session-only positions from dragging. Never written to disk.
      Overrides: Map<string, float * float>
      /// Empty = show every kind.
      Filter: Set<EntityKind>
      Query: string
      Selected: string option
      FocusMode: bool
      View: GraphLogic.Viewport
      Interaction: GraphLogic.Interaction
      LegendOpen: bool
      InsightsOpen: bool
      MinimapOpen: bool
      CanvasWidth: float
      CanvasHeight: float
      Width: float
      Loading: bool
      Error: string option }

type External = Goto of Page

type Msg =
    | Load
    | Loaded of GraphLogic.GraphNode list * (string * string) list * Map<string, float * float>
    | Failed of exn
    | ToggleFilter of EntityKind
    | ClearFilter
    | SetQuery of string
    | SearchFocus
    | SelectNode of string
    | ClearSelection
    | ToggleFocus
    | RunControl of GraphLogic.Control
    | Wheel of float * float * float
    | NodePressed of string * float * float
    | CanvasPressed of float * float
    | PointerMoved of float * float
    | PointerReleased
    | CanvasResized of float * float
    | Resized of float
    | ToggleLegend
    | ToggleInsights
    | ToggleMinimap
    | OpenSelected

// ---------------------------------------------------------------------------
// loading — the graph assembly, preserved
// ---------------------------------------------------------------------------

let private entityRef (kind: EntityKind) (id: Guid) : EntityRef = { Kind = kind; Id = id }

/// One command, one snapshot.
///
/// Nodes are every entity the application stores; edges are the **implicit**
/// relations (module filing, lecture materials, flashcard sources) plus every
/// explicit row in the link table. Every edge is then normalised undirected and
/// de-duplicated by `GraphLogic.normaliseEdges`.
let private loadCmd (services: AppServices) =
    Elm.ofAsync
        (async {
            let now = services.Clock.Now
            let! modules = services.Modules.GetAll()
            let! assignments = services.Assignments.GetAll()
            let! notes = services.Notes.GetAll()
            let! documents = services.Documents.GetAll()
            let! lectures = services.Lectures.GetAll()
            let! revision = services.Revision.GetAll()
            let! events = services.Events.GetBetween(now, now.AddDays 30.0)
            let! links = services.Links.GetAll()

            let node (kind: EntityKind) (id: Guid) (label: string) =
                let entity = entityRef kind id

                { GraphLogic.Key = GraphLogic.nodeKey entity
                  GraphLogic.Ref = entity
                  GraphLogic.Label = label }

            let nodes =
                [ for m in modules -> node EntityKind.Module m.Id.Value (if m.Code.Length > 0 then m.Code else m.Name)
                  for a in assignments -> node EntityKind.Assignment a.Id.Value a.Title
                  for n in notes -> node EntityKind.Note n.Id.Value n.Title
                  for d in documents -> node EntityKind.Document d.Id.Value d.Title
                  for l in lectures -> node EntityKind.Lecture l.Id.Value l.Title
                  for r in revision -> node EntityKind.RevisionItem r.Id.Value r.Question
                  for e in events -> node EntityKind.Event e.Id.Value e.Title ]

            let known = nodes |> List.map (fun n -> n.Key) |> Set.ofList

            let moduleEdge (entity: EntityRef) (moduleId: ModuleId option) =
                moduleId
                |> Option.map (fun m ->
                    GraphLogic.nodeKey entity, GraphLogic.nodeKey (entityRef EntityKind.Module m.Value))

            let implicit =
                [ yield!
                      assignments
                      |> List.choose (fun a -> moduleEdge (entityRef EntityKind.Assignment a.Id.Value) a.ModuleId)
                  yield! notes |> List.choose (fun n -> moduleEdge (entityRef EntityKind.Note n.Id.Value) n.ModuleId)
                  yield!
                      documents
                      |> List.choose (fun d -> moduleEdge (entityRef EntityKind.Document d.Id.Value) d.ModuleId)
                  yield!
                      lectures |> List.choose (fun l -> moduleEdge (entityRef EntityKind.Lecture l.Id.Value) l.ModuleId)
                  yield!
                      revision
                      |> List.choose (fun r -> moduleEdge (entityRef EntityKind.RevisionItem r.Id.Value) r.ModuleId)
                  yield! events |> List.choose (fun e -> moduleEdge (entityRef EntityKind.Event e.Id.Value) e.ModuleId)
                  yield!
                      lectures
                      |> List.collect (fun l ->
                          [ l.SlidesDocumentId; l.RecordingDocumentId ]
                          |> List.choose (
                              Option.map (fun d ->
                                  GraphLogic.nodeKey (entityRef EntityKind.Lecture l.Id.Value),
                                  GraphLogic.nodeKey (entityRef EntityKind.Document d.Value))
                          ))
                  yield!
                      revision
                      |> List.choose (fun r ->
                          r.Source
                          |> Option.map (fun s ->
                              GraphLogic.nodeKey (entityRef EntityKind.RevisionItem r.Id.Value), GraphLogic.nodeKey s)) ]

            let explicit = links |> List.map (fun l -> GraphLogic.nodeKey l.From, GraphLogic.nodeKey l.To)

            let edges = GraphLogic.normaliseEdges known (implicit @ explicit)

            // Layout runs HERE, once per load — never during drag, pan, zoom or
            // selection.
            let positions =
                GraphLayout.layout
                    GraphLogic.Geometry.worldWidth
                    GraphLogic.Geometry.worldHeight
                    150
                    (nodes |> List.map (fun n -> n.Key))
                    edges

            return nodes, edges, positions
        })
        (fun (nodes, edges, positions) -> Loaded(nodes, edges, positions))
        Failed

let init (services: AppServices) : Model * Cmd<Msg> =
    { Nodes = []
      Edges = []
      Adjacency = Map.empty
      BaseLayout = Map.empty
      Overrides = Map.empty
      Filter = Set.empty
      Query = ""
      Selected = None
      FocusMode = false
      View = GraphLogic.Viewport.identity
      Interaction = GraphLogic.Interaction.Idle
      LegendOpen = true
      InsightsOpen = true
      MinimapOpen = true
      CanvasWidth = 1200.0
      CanvasHeight = 800.0
      Width = 1600.0
      Loading = true
      Error = None },
    loadCmd services

// ---------------------------------------------------------------------------
// derived state — computed from the model, never from a repository
// ---------------------------------------------------------------------------

let private shownNodes (model: Model) =
    GraphLogic.shownNodes model.Filter (if model.FocusMode then model.Selected else None) model.Adjacency model.Nodes

let private positionOf (model: Model) (key: string) =
    GraphLogic.effectivePosition model.BaseLayout model.Overrides key

let private shownPositions (model: Model) =
    GraphLogic.effectivePositions model.BaseLayout model.Overrides (shownNodes model |> List.map (fun n -> n.Key))

// ---------------------------------------------------------------------------
// update
// ---------------------------------------------------------------------------

let update (services: AppServices) (msg: Msg) (model: Model) : Model * Cmd<Msg> * External option =
    let plain m = m, Cmd.none, None
    let size = model.CanvasWidth, model.CanvasHeight

    match msg with
    | Load -> { model with Loading = true }, loadCmd services, None

    | Loaded(nodes, edges, positions) ->
        let known = nodes |> List.map (fun n -> n.Key) |> Set.ofList

        plain
            { model with
                Nodes = nodes
                Edges = edges
                Adjacency = GraphLogic.adjacency edges
                BaseLayout = positions
                // A refresh keeps the positions the user moved, for entities
                // that still exist, and drops the rest.
                Overrides = GraphLogic.retainOverrides known model.Overrides
                Selected = model.Selected |> Option.filter known.Contains
                Loading = false
                Error = None }

    | Failed ex -> plain { model with Loading = false; Error = Some ex.Message }

    | ToggleFilter kind ->
        let filter =
            if model.Filter.Contains kind then model.Filter.Remove kind else model.Filter.Add kind

        // Filtering never discards a dragged position — a hidden node keeps its
        // override and comes back where it was left.
        plain { model with Filter = filter }

    | ClearFilter -> plain { model with Filter = Set.empty }
    | SetQuery query -> plain { model with Query = query }

    | SearchFocus ->
        match GraphLogic.firstMatch model.Query (shownNodes model) with
        | Some node ->
            match positionOf model node.Key with
            | Some p -> plain { model with Selected = Some node.Key; View = GraphLogic.centreOn model.View size p }
            | None -> plain { model with Selected = Some node.Key }
        | None -> plain model

    | SelectNode key -> plain { model with Selected = Some key }
    | ClearSelection -> plain { model with Selected = None; FocusMode = false }
    | ToggleFocus -> plain { model with FocusMode = not model.FocusMode && model.Selected.IsSome }

    | RunControl control ->
        match control with
        | GraphLogic.Control.ZoomIn ->
            plain { model with View = GraphLogic.zoomAboutCentre model.View size GraphLogic.Geometry.zoomStep }
        | GraphLogic.Control.ZoomOut ->
            plain
                { model with
                    View = GraphLogic.zoomAboutCentre model.View size (1.0 / GraphLogic.Geometry.zoomStep) }
        | GraphLogic.Control.FitToView ->
            // Fits to where the nodes ACTUALLY are, dragged positions included.
            plain { model with View = GraphLogic.fitToView size (shownPositions model |> List.map snd) }
        | GraphLogic.Control.ResetView -> plain { model with View = GraphLogic.Viewport.identity }
        | GraphLogic.Control.ResetLayout ->
            // Clears the session moves only. Filters, selection and the viewport
            // are untouched — this is not Reset View.
            plain { model with Overrides = Map.empty }
        | GraphLogic.Control.CentreSelected ->
            match model.Selected |> Option.bind (positionOf model) with
            | Some p -> plain { model with View = GraphLogic.centreOn model.View size p }
            | None -> plain model

    | Wheel(x, y, delta) ->
        let factor =
            if delta > 0.0 then GraphLogic.Geometry.wheelStep else 1.0 / GraphLogic.Geometry.wheelStep

        plain { model with View = GraphLogic.zoomAboutPointer model.View (x, y) factor }

    // A node handles its own pointer-down and marks the event handled, so the
    // canvas beneath never sees it. That is the "hit test before pan" rule.
    | NodePressed(key, x, y) ->
        let offsetX, offsetY =
            match positionOf model key with
            | Some(nx, ny) ->
                let wx, wy = GraphLogic.screenToWorld model.View (x, y)
                nx - wx, ny - wy
            | None -> 0.0, 0.0

        plain
            { model with
                Interaction =
                    GraphLogic.Interaction.Pressed
                        { Node = Some key; StartX = x; StartY = y; OffsetX = offsetX; OffsetY = offsetY } }

    | CanvasPressed(x, y) ->
        plain
            { model with
                Interaction =
                    GraphLogic.Interaction.Pressed
                        { Node = None; StartX = x; StartY = y; OffsetX = 0.0; OffsetY = 0.0 } }

    | PointerMoved(x, y) ->
        match model.Interaction with
        | GraphLogic.Interaction.Idle -> plain model

        // Still undecided: only a movement past the threshold turns a press into
        // a drag or a pan. Below it, this is still a click.
        | GraphLogic.Interaction.Pressed press ->
            if GraphLogic.movedEnough press (x, y) then
                plain { model with Interaction = GraphLogic.beginMove press (x, y) }
            else
                plain model

        | GraphLogic.Interaction.Panning(lastX, lastY) ->
            plain
                { model with
                    View = { model.View with PanX = model.View.PanX + (x - lastX); PanY = model.View.PanY + (y - lastY) }
                    Interaction = GraphLogic.Interaction.Panning(x, y) }

        // Only the dragged node's position changes. No layout, no rebuild of
        // anything else — the edges follow because they read this map.
        | GraphLogic.Interaction.DraggingNode(key, offsetX, offsetY) ->
            let wx, wy = GraphLogic.screenToWorld model.View (x, y)
            plain { model with Overrides = model.Overrides.Add(key, (wx + offsetX, wy + offsetY)) }

    | PointerReleased ->
        match model.Interaction with
        // Never moved far enough to be a drag, so it was a click.
        | GraphLogic.Interaction.Pressed press ->
            match press.Node with
            | Some key -> plain { model with Selected = Some key; Interaction = GraphLogic.Interaction.Idle }
            | None -> plain { model with Selected = None; FocusMode = false; Interaction = GraphLogic.Interaction.Idle }
        | _ -> plain { model with Interaction = GraphLogic.Interaction.Idle }

    | CanvasResized(w, h) -> plain { model with CanvasWidth = w; CanvasHeight = h }
    | Resized width -> plain { model with Width = width }

    | ToggleLegend -> plain { model with LegendOpen = not model.LegendOpen }
    | ToggleInsights -> plain { model with InsightsOpen = not model.InsightsOpen }
    | ToggleMinimap -> plain { model with MinimapOpen = not model.MinimapOpen }

    | OpenSelected ->
        match model.Selected |> Option.bind (fun key -> model.Nodes |> List.tryFind (fun n -> n.Key = key)) with
        | Some node -> model, Cmd.none, Some(Goto(Page.forEntity node.Ref.Kind))
        | None -> model, Cmd.none, None

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

let private chipWash (surface: Color) (hex: string) =
    Theme.compositeOver surface (Theme.withAlphaHex GraphLogic.Geometry.chipFillAlpha hex)

/// A floating overlay. Opaque enough to read over the canvas, restrained enough
/// not to make it feel cramped.
let private overlay (tokens: Tokens) (padding: float) (children: IView list) : IView =
    Border.create
        [ Border.background (Theme.brush (Ui.hexOf (Theme.surfaces ()).Card))
          Border.borderThickness 1.0
          Border.borderBrush (Theme.border ())
          Border.cornerRadius (Tokens.radius tokens GraphLogic.Geometry.overlayRadius)
          Border.padding (Tokens.space tokens padding)
          Border.child (
              StackPanel.create [ StackPanel.spacing 8.0; StackPanel.children children ]
          ) ]
    :> IView

let private bulletRow (glyph: string) (glyphBrush: IBrush) (text: string) (textBrush: IBrush) : IView =
    Grid.create
        [ Grid.columnDefinitions GraphLogic.Geometry.bulletColumns
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

let private filterChip
    (tokens: Tokens)
    (label: string)
    (isOn: bool)
    (hex: string)
    (accessibleName: string)
    (onClick: unit -> unit)
    : IView =
    let page = (Theme.surfaces ()).Card
    let wash = chipWash page hex

    Button.create
        [ Button.background (
              if isOn then Theme.brush (Theme.withAlphaHex GraphLogic.Geometry.chipFillAlpha hex)
              else Theme.transparent
          )
          Button.borderThickness 1.0
          Button.borderBrush (if isOn then Theme.brush (Theme.visibleOnColor page hex) else Theme.border ())
          Button.foreground (if isOn then Theme.brush (Theme.readableOnColor wash hex) else Theme.muted ())
          Button.cornerRadius (Tokens.radius tokens 999.0)
          Button.minHeight 26.0
          Button.padding (11.0, 2.0)
          Button.fontSize 10.5
          Button.fontWeight FontWeight.SemiBold
          Button.margin (0.0, 0.0, 6.0, 6.0)
          Button.cursor Ui.hand
          Button.content label
          Ui.accessibleName accessibleName
          Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always) ]
    :> IView

let private iconButton (tokens: Tokens) (glyph: string) (label: string) (enabled: bool) (onClick: unit -> unit) : IView =
    Button.create
        [ Button.isEnabled enabled
          Button.content glyph
          Button.width 30.0
          Button.height 30.0
          Button.padding 0.0
          Button.margin (0.0, 0.0, 4.0, 0.0)
          Button.background (Theme.action ())
          Button.borderThickness 1.0
          Button.borderBrush (Theme.border ())
          Button.cornerRadius (Tokens.radius tokens 9.0)
          Button.fontSize 12.0
          Button.foreground (Theme.text ())
          Button.horizontalContentAlignment HorizontalAlignment.Center
          Button.verticalContentAlignment VerticalAlignment.Center
          Button.cursor Ui.hand
          ToolTip.tip label
          Ui.accessibleName label
          Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always) ]
    :> IView

// ---------------------------------------------------------------------------
// the canvas
// ---------------------------------------------------------------------------

/// Edges and nodes, drawn at **screen** coordinates.
///
/// The transform is applied by `GraphLogic.worldToScreen` rather than by a
/// render transform, so what is drawn and what is hit-tested can never drift
/// apart, and both are the same arithmetic the tests exercise.
let private canvasContent (model: Model) (dispatch: Msg -> unit) : IView list =
    let shown = shownNodes model
    let shownKeys = shown |> List.map (fun n -> n.Key) |> Set.ofList
    let edges = GraphLogic.visibleEdges shownKeys model.Edges

    let screenOf key =
        GraphLogic.effectivePosition model.BaseLayout model.Overrides key
        |> Option.map (GraphLogic.worldToScreen model.View)

    let neighbours =
        match model.Selected with
        | Some key -> GraphLogic.neighbourhood model.Adjacency key
        | None -> Set.empty

    let edgeViews =
        edges
        |> List.choose (fun (a, b) ->
            match screenOf a, screenOf b with
            | Some(x1, y1), Some(x2, y2) ->
                let incident =
                    match model.Selected with
                    | Some key -> a = key || b = key
                    | None -> false

                Some(
                    Line.create
                        [ Line.startPoint (Point(x1, y1))
                          Line.endPoint (Point(x2, y2))
                          // No arrowhead, no dash: the graph is undirected and
                          // nothing here is inferred.
                          //
                          // The resting colour is the text colour at
                          // `edgeAlpha` over the canvas — the same alpha the
                          // test measures with.
                          Line.stroke (
                              if incident then
                                  Theme.accent ()
                              else
                                  Theme.brush (
                                      Theme.withAlphaHex
                                          GraphLogic.Geometry.edgeAlpha
                                          (Ui.hexOf (Theme.surfaces ()).Text)
                                  )
                          )
                          Line.strokeThickness GraphLogic.Geometry.edgeThickness
                          Line.opacity (if model.Selected.IsSome && not incident then 0.35 else 1.0) ]
                    :> IView
                )
            // A dangling reference simply does not draw. It never crashes.
            | _ -> None)

    let nodeViews =
        shown
        |> List.collect (fun node ->
            match screenOf node.Key with
            | None -> []
            | Some(sx, sy) ->
                let isSelected = model.Selected = Some node.Key
                let isNeighbour = model.Selected.IsSome && neighbours.Contains node.Key
                let hex = Theme.entityKindHex node.Ref.Kind
                let surface = (Theme.surfaces ()).Card
                let wash = chipWash surface hex

                [ Border.create
                      [ Canvas.left (sx - GraphLogic.Geometry.nodeRadius)
                        Canvas.top (sy - GraphLogic.Geometry.nodeRadius)
                        // Size never changes with selection, hover or degree.
                        Border.width GraphLogic.Geometry.nodeSize
                        Border.height GraphLogic.Geometry.nodeSize
                        Border.cornerRadius GraphLogic.Geometry.nodeRadius
                        Border.background (
                            Theme.brush (
                                Theme.withAlphaHex
                                    (if isSelected then GraphLogic.Geometry.selectedNodeAlpha
                                     else GraphLogic.Geometry.chipFillAlpha)
                                    hex
                            )
                        )
                        Border.borderThickness GraphLogic.Geometry.nodeRingThickness
                        Border.borderBrush (
                            if isSelected then Theme.brush (Theme.visibleOnColor surface hex)
                            elif isNeighbour then Theme.brush hex
                            else Theme.border ()
                        )
                        Border.cursor Ui.hand
                        ToolTip.tip (sprintf "%s — %s" node.Label (GraphLogic.kindLabel node.Ref.Kind))
                        Ui.accessibleName (
                            sprintf
                                "%s, %s, %d connections"
                                node.Label
                                (GraphLogic.kindLabel node.Ref.Kind)
                                (GraphLogic.degree model.Adjacency node.Key)
                        )
                        // Handled here, so the canvas beneath never begins a pan:
                        // a node is hit-tested before the canvas, always.
                        Border.onPointerPressed (
                            (fun e ->
                                e.Handled <- true
                                let p = e.GetCurrentPoint(null).Position
                                dispatch (NodePressed(node.Key, p.X, p.Y))),
                            SubPatchOptions.Always
                        )
                        Border.child (
                            // A glyph as well as a colour, so the kind is not
                            // conveyed by colour alone.
                            TextBlock.create
                                [ TextBlock.text (GraphLogic.kindGlyph node.Ref.Kind)
                                  TextBlock.fontSize 13.0
                                  TextBlock.horizontalAlignment HorizontalAlignment.Center
                                  TextBlock.verticalAlignment VerticalAlignment.Center
                                  TextBlock.foreground (Theme.brush (Theme.readableOnColor wash hex))
                                  Ui.decorative () ]
                        ) ]
                  :> IView

                  TextBlock.create
                      [ Canvas.left (sx - 60.0)
                        Canvas.top (sy + GraphLogic.Geometry.nodeRadius + 3.0)
                        TextBlock.width 120.0
                        TextBlock.text (Format.truncate 22 node.Label)
                        TextBlock.fontSize 9.5
                        TextBlock.textAlignment TextAlignment.Center
                        TextBlock.textTrimming TextTrimming.CharacterEllipsis
                        TextBlock.foreground (if isSelected then Theme.text () else Theme.muted ())
                        TextBlock.isHitTestVisible false
                        Ui.decorative () ]
                  :> IView ])

    edgeViews @ nodeViews

let private canvasSurface (model: Model) (dispatch: Msg -> unit) : IView =
    Border.create
        [ Border.background (Theme.brush (Ui.hexOf (Theme.surfaces ()).Window))
          Border.clipToBounds true
          Border.cursor Ui.hand
          Border.onSizeChanged (
              (fun args ->
                  if abs (args.NewSize.Width - model.CanvasWidth) > 1.0
                     || abs (args.NewSize.Height - model.CanvasHeight) > 1.0 then
                      dispatch (CanvasResized(args.NewSize.Width, args.NewSize.Height))),
              SubPatchOptions.Always
          )
          Border.onPointerPressed (
              (fun e ->
                  let p = e.GetCurrentPoint(null).Position
                  dispatch (CanvasPressed(p.X, p.Y))),
              SubPatchOptions.Always
          )
          Border.onPointerMoved (
              (fun e ->
                  match model.Interaction with
                  | GraphLogic.Interaction.Idle -> ()
                  | _ ->
                      let p = e.GetCurrentPoint(null).Position
                      dispatch (PointerMoved(p.X, p.Y))),
              SubPatchOptions.Always
          )
          Border.onPointerReleased ((fun _ -> dispatch PointerReleased), SubPatchOptions.Always)
          // Capture loss ends the interaction too, so a drag can never be left
          // running after the pointer leaves.
          Border.onPointerCaptureLost ((fun _ -> dispatch PointerReleased), SubPatchOptions.Always)
          Border.onPointerWheelChanged (
              (fun e ->
                  e.Handled <- true
                  let p = e.GetCurrentPoint(null).Position
                  dispatch (Wheel(p.X, p.Y, e.Delta.Y))),
              SubPatchOptions.Always
          )
          Border.child (Canvas.create [ Canvas.children (canvasContent model dispatch) ]) ]
    :> IView

// ---------------------------------------------------------------------------
// overlays
// ---------------------------------------------------------------------------

let private titleAndSearch (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    StackPanel.create
        [ StackPanel.spacing 10.0
          StackPanel.maxWidth 420.0
          StackPanel.children
              [ overlay
                    tokens
                    14.0
                    [ TextBlock.create
                          [ TextBlock.text "Knowledge Graph"
                            TextBlock.fontSize 22.0
                            TextBlock.fontWeight FontWeight.SemiBold
                            TextBlock.foreground (Theme.text ()) ]
                      TextBlock.create
                          [ TextBlock.text "How is everything in my academic life connected?"
                            TextBlock.fontSize 12.5
                            TextBlock.foreground (Theme.muted ())
                            TextBlock.textWrapping TextWrapping.Wrap ]

                      Ui.textBox model.Query "Search titles and entity types" (SetQuery >> dispatch)

                      WrapPanel.create
                          [ WrapPanel.maxWidth 400.0
                            WrapPanel.children
                                [ yield!
                                      (GraphLogic.graphKinds
                                       |> List.map (fun kind ->
                                           let isOn = model.Filter.Contains kind
                                           let label = GraphLogic.kindLabel kind

                                           filterChip
                                               tokens
                                               label
                                               isOn
                                               (Theme.entityKindHex kind)
                                               (if isOn then sprintf "%s filter, on" label
                                                else sprintf "Show only %s" label)
                                               (fun () -> dispatch (ToggleFilter kind))))

                                  if not model.Filter.IsEmpty then
                                      filterChip
                                          tokens
                                          "Clear filters"
                                          false
                                          (Theme.accentHex ())
                                          "Clear every entity filter"
                                          (fun () -> dispatch ClearFilter)

                                  if model.Query.Trim().Length > 0 then
                                      filterChip
                                          tokens
                                          "Go to first match"
                                          false
                                          (Theme.accentHex ())
                                          "Select and centre the first matching node"
                                          (fun () -> dispatch SearchFocus) ] ] ] ] ]
    :> IView

let private focusBanner (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView list =
    match (if model.FocusMode then model.Selected else None)
          |> Option.bind (fun key -> model.Nodes |> List.tryFind (fun n -> n.Key = key))
        with
    | None -> []
    | Some node ->
        [ overlay
              tokens
              10.0
              [ StackPanel.create
                    [ StackPanel.orientation Orientation.Horizontal
                      StackPanel.spacing 10.0
                      StackPanel.children
                          [ TextBlock.create
                                [ TextBlock.text (
                                      sprintf "Focus: %s — this node and its direct links" (Format.truncate 28 node.Label)
                                  )
                                  TextBlock.fontSize 12.0
                                  TextBlock.fontWeight FontWeight.SemiBold
                                  TextBlock.verticalAlignment VerticalAlignment.Center
                                  TextBlock.foreground (
                                      Theme.brush (
                                          Theme.readableOnColor (Theme.surfaces ()).Card (Theme.accentHex ())
                                      )
                                  ) ]
                            Ui.smallButton "Exit focus ✕" (fun () -> dispatch ToggleFocus) ] ] ] ]

let private legendPanel (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let shown = shownNodes model

    overlay
        tokens
        12.0
        [ DockPanel.create
              [ DockPanel.children
                    [ Border.create
                          [ DockPanel.dock Dock.Right
                            Border.child (
                                Ui.namedSmallButton
                                    (if model.LegendOpen then "▾" else "▸")
                                    (if model.LegendOpen then "Collapse legend" else "Expand legend")
                                    (fun () -> dispatch ToggleLegend)
                            ) ]
                      Border.create
                          [ Border.verticalAlignment VerticalAlignment.Center
                            Border.child (sectionCaption "Legend") ] ] ]

          if model.LegendOpen then
              StackPanel.create
                  [ StackPanel.spacing 5.0
                    StackPanel.children
                        [ for kind, count in GraphLogic.legendItems shown do
                              let hex = Theme.entityKindHex kind

                              Grid.create
                                  [ Grid.columnDefinitions "Auto,8,*,6,Auto"
                                    Grid.children
                                        [ Border.create
                                              [ Grid.column 0
                                                Border.width 16.0
                                                Border.height 16.0
                                                Border.cornerRadius 8.0
                                                Border.background (
                                                    Theme.brush (
                                                        Theme.withAlphaHex GraphLogic.Geometry.chipFillAlpha hex
                                                    )
                                                )
                                                Border.borderThickness 1.5
                                                Border.borderBrush (Theme.brush hex)
                                                Ui.decorative ()
                                                Border.child (
                                                    TextBlock.create
                                                        [ TextBlock.text (GraphLogic.kindGlyph kind)
                                                          TextBlock.fontSize 8.0
                                                          TextBlock.horizontalAlignment HorizontalAlignment.Center
                                                          TextBlock.verticalAlignment VerticalAlignment.Center
                                                          TextBlock.foreground (
                                                              Theme.brush (
                                                                  Theme.readableOnColor
                                                                      (chipWash (Theme.surfaces ()).Card hex)
                                                                      hex
                                                              )
                                                          ) ]
                                                ) ]
                                          TextBlock.create
                                              [ Grid.column 2
                                                TextBlock.text (GraphLogic.kindLabel kind)
                                                TextBlock.fontSize 10.5
                                                TextBlock.foreground (Theme.muted ())
                                                TextBlock.textTrimming TextTrimming.CharacterEllipsis ]
                                          TextBlock.create
                                              [ Grid.column 4
                                                TextBlock.text (string count)
                                                TextBlock.fontSize 10.5
                                                TextBlock.foreground (Theme.faint ()) ] ] ]

                          Border.create
                              [ Border.height 1.0
                                Border.margin (0.0, 4.0)
                                Border.background (Theme.divider ())
                                Ui.decorative () ]

                          sectionCaption "Relationships"

                          for kind in GraphLogic.relationshipKinds do
                              TextBlock.create
                                  [ TextBlock.text ("— " + kind)
                                    TextBlock.fontSize 10.5
                                    TextBlock.foreground (Theme.muted ())
                                    TextBlock.textWrapping TextWrapping.Wrap ]

                          // The mockup's "Predicted (AI-inferred)" row is absent:
                          // nothing infers edges, so there is no such line to
                          // explain.
                          TextBlock.create
                              [ TextBlock.text "Every line is a stored or derived relationship. None is inferred, and none is directional."
                                TextBlock.fontSize 10.0
                                TextBlock.foreground (Theme.faint ())
                                TextBlock.textWrapping TextWrapping.Wrap
                                TextBlock.margin (0.0, 4.0, 0.0, 0.0) ] ] ] ]

let private insightsPanel (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let shown = shownNodes model
    let shownKeys = shown |> List.map (fun n -> n.Key) |> Set.ofList
    let edges = GraphLogic.visibleEdges shownKeys model.Edges
    let hidden = model.Nodes.Length - shown.Length

    overlay
        tokens
        12.0
        [ DockPanel.create
              [ DockPanel.children
                    [ Border.create
                          [ DockPanel.dock Dock.Right
                            Border.child (
                                Ui.namedSmallButton
                                    (if model.InsightsOpen then "▾" else "▸")
                                    (if model.InsightsOpen then "Collapse insights" else "Expand insights")
                                    (fun () -> dispatch ToggleInsights)
                            ) ]
                      Border.create
                          [ Border.verticalAlignment VerticalAlignment.Center
                            Border.child (sectionCaption "Rule-based insights") ] ] ]

          if model.InsightsOpen then
              StackPanel.create
                  [ StackPanel.children
                        (GraphLogic.insights shown edges (GraphLogic.adjacency edges) hidden
                         |> List.map (fun (glyph, text) ->
                             bulletRow glyph (Theme.accent ()) text (Theme.textSecondary ()))) ] ]

/// The minimap. Real, because the positions are real.
///
/// It projects the **same bounds Fit to View uses**, so the two can never
/// disagree. Clicking it fits the graph rather than recentring on a point:
/// converting a click to a world coordinate needs panel-local coordinates that
/// this pointer plumbing does not give reliably, and a control that recentres
/// somewhere slightly wrong is worse than one that does something exact.
let private minimapPanel (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let positions = shownPositions model
    let projection = GraphLogic.minimapScale (positions |> List.map snd)
    let vx, vy, vw, vh = GraphLogic.minimapViewport model.View (model.CanvasWidth, model.CanvasHeight) projection

    overlay
        tokens
        10.0
        [ DockPanel.create
              [ DockPanel.children
                    [ Border.create
                          [ DockPanel.dock Dock.Right
                            Border.child (
                                Ui.namedSmallButton
                                    (if model.MinimapOpen then "▾" else "▸")
                                    (if model.MinimapOpen then "Collapse mini map" else "Expand mini map")
                                    (fun () -> dispatch ToggleMinimap)
                            ) ]
                      Border.create
                          [ Border.verticalAlignment VerticalAlignment.Center
                            Border.child (sectionCaption "Mini map") ] ] ]

          if model.MinimapOpen then
              Border.create
                  [ Border.width GraphLogic.Geometry.minimapFieldWidth
                    Border.height GraphLogic.Geometry.minimapFieldHeight
                    Border.background (Theme.cardAlt ())
                    Border.cornerRadius (Tokens.radius tokens 8.0)
                    Border.clipToBounds true
                    Border.cursor Ui.hand
                    ToolTip.tip "Fit the whole graph into view"
                    Ui.accessibleName "Fit the whole graph into view"
                    Border.onPointerPressed (
                        (fun e ->
                            e.Handled <- true
                            dispatch (RunControl GraphLogic.Control.FitToView)),
                        SubPatchOptions.Always
                    )
                    Border.child (
                        Canvas.create
                            [ Canvas.children
                                  [ // The viewport rectangle, under the dots.
                                    Border.create
                                        [ Canvas.left vx
                                          Canvas.top vy
                                          Border.width vw
                                          Border.height vh
                                          Border.borderThickness 1.0
                                          Border.borderBrush (Theme.accent ())
                                          Border.background Theme.transparent
                                          Border.isHitTestVisible false
                                          Ui.decorative () ]

                                    // Only nodes that are actually on screen —
                                    // a filtered-out node must not appear here.
                                    for key, world in positions do
                                        let mx, my = GraphLogic.minimapProject projection world
                                        let node = model.Nodes |> List.tryFind (fun n -> n.Key = key)

                                        Border.create
                                            [ Canvas.left mx
                                              Canvas.top my
                                              Border.width GraphLogic.Geometry.minimapDotSize
                                              Border.height GraphLogic.Geometry.minimapDotSize
                                              Border.cornerRadius (GraphLogic.Geometry.minimapDotSize / 2.0)
                                              Border.isHitTestVisible false
                                              Border.background (
                                                  match node with
                                                  | Some n when model.Selected = Some key ->
                                                      Theme.brush (Theme.entityKindHex n.Ref.Kind)
                                                  | Some n ->
                                                      Theme.brush (
                                                          Theme.withAlphaHex "AA" (Theme.entityKindHex n.Ref.Kind)
                                                      )
                                                  | None -> Theme.faint ()
                                              )
                                              Ui.decorative () ] ] ]
                    ) ] ]

let private controlsPanel (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    overlay
        tokens
        8.0
        [ WrapPanel.create
              [ WrapPanel.children
                    (GraphLogic.Control.all
                     |> List.map (fun control ->
                         let enabled =
                             match control with
                             | GraphLogic.Control.CentreSelected -> model.Selected.IsSome
                             | GraphLogic.Control.ResetLayout -> not model.Overrides.IsEmpty
                             | _ -> true

                         iconButton
                             tokens
                             (GraphLogic.Control.glyph control)
                             (GraphLogic.Control.label control)
                             enabled
                             (fun () -> dispatch (RunControl control)))) ]

          TextBlock.create
              [ TextBlock.text (sprintf "%d%%" (int (Math.Round(model.View.Zoom * 100.0))))
                TextBlock.fontSize 10.0
                TextBlock.horizontalAlignment HorizontalAlignment.Center
                TextBlock.foreground (Theme.faint ()) ] ]

// ---------------------------------------------------------------------------
// the selected-node panel
// ---------------------------------------------------------------------------

let private nodePanel
    (tokens: Tokens)
    (model: Model)
    (dispatch: Msg -> unit)
    (node: GraphLogic.GraphNode)
    (width: float)
    : IView =
    let hex = Theme.entityKindHex node.Ref.Kind
    let surface = (Theme.surfaces ()).Card
    let wash = chipWash surface hex
    let deg = GraphLogic.degree model.Adjacency node.Key

    let connections =
        match model.Adjacency.TryFind node.Key with
        | Some keys ->
            keys
            |> Set.toList
            |> List.choose (fun k -> model.Nodes |> List.tryFind (fun n -> n.Key = k))
            |> List.sortBy (fun n -> GraphLogic.kindLabel n.Ref.Kind, n.Label)
        | None -> []

    let valueRow (label: string) (value: string) : IView =
        Grid.create
            [ Grid.columnDefinitions GraphLogic.Geometry.detailColumns
              Grid.margin (0.0, 1.0)
              Grid.children
                  [ TextBlock.create
                        [ Grid.column 0
                          TextBlock.text label
                          TextBlock.fontSize 11.0
                          TextBlock.foreground (Theme.faint ()) ]
                    TextBlock.create
                        [ Grid.column 2
                          TextBlock.text value
                          TextBlock.fontSize 11.5
                          TextBlock.foreground (Theme.text ())
                          TextBlock.textAlignment TextAlignment.Right
                          TextBlock.textWrapping TextWrapping.Wrap ] ] ]
        :> IView

    let region (r: GraphLogic.PanelRegion) : IView list =
        match r with
        | GraphLogic.PanelRegion.Identity ->
            [ Grid.create
                  [ Grid.columnDefinitions "Auto,10,*"
                    Grid.children
                        [ Border.create
                              [ Grid.column 0
                                Border.width 34.0
                                Border.height 34.0
                                Border.cornerRadius 17.0
                                Border.background (
                                    Theme.brush (Theme.withAlphaHex GraphLogic.Geometry.chipFillAlpha hex)
                                )
                                Border.borderThickness 1.5
                                Border.borderBrush (Theme.brush hex)
                                Border.verticalAlignment VerticalAlignment.Center
                                Ui.decorative ()
                                Border.child (
                                    TextBlock.create
                                        [ TextBlock.text (GraphLogic.kindGlyph node.Ref.Kind)
                                          TextBlock.fontSize 14.0
                                          TextBlock.horizontalAlignment HorizontalAlignment.Center
                                          TextBlock.verticalAlignment VerticalAlignment.Center
                                          TextBlock.foreground (Theme.brush (Theme.readableOnColor wash hex)) ]
                                ) ]
                          StackPanel.create
                              [ Grid.column 2
                                StackPanel.spacing 2.0
                                StackPanel.verticalAlignment VerticalAlignment.Center
                                StackPanel.children
                                    [ TextBlock.create
                                          [ TextBlock.text node.Label
                                            TextBlock.fontSize 13.5
                                            TextBlock.fontWeight FontWeight.SemiBold
                                            TextBlock.foreground (Theme.text ())
                                            TextBlock.textWrapping TextWrapping.Wrap ]
                                      TextBlock.create
                                          [ TextBlock.text (GraphLogic.kindLabel node.Ref.Kind)
                                            TextBlock.fontSize 10.5
                                            TextBlock.foreground (Theme.brush (Theme.readableOnColor surface hex)) ] ] ] ] ] ]

        | GraphLogic.PanelRegion.Facts ->
            // A factual count of relationships. Not an importance score, and the
            // panel says so rather than leaving the number to be misread.
            [ valueRow "Connections" (string deg)
              (if deg >= GraphLogic.Threshold.wellConnectedDegree then
                   TextBlock.create
                       [ TextBlock.text
                             (sprintf
                                 "%d or more relationships. That is a count of links, not a measure of importance."
                                 GraphLogic.Threshold.wellConnectedDegree)
                         TextBlock.fontSize 10.0
                         TextBlock.foreground (Theme.faint ())
                         TextBlock.textWrapping TextWrapping.Wrap ]
                   :> IView
               else
                   TextBlock.create
                       [ TextBlock.text (
                             if deg = 0 then "Nothing is linked to this yet." else "Direct relationships only."
                         )
                         TextBlock.fontSize 10.0
                         TextBlock.foreground (Theme.faint ())
                         TextBlock.textWrapping TextWrapping.Wrap ]
                   :> IView) ]

        | GraphLogic.PanelRegion.Connections ->
            // Omitted entirely when there is nothing connected.
            if connections.IsEmpty then
                []
            else
                [ sectionCaption "Connected entities"
                  StackPanel.create
                      [ StackPanel.children
                            (connections
                             |> List.truncate GraphLogic.Threshold.panelConnections
                             |> List.map (fun other ->
                                 let otherHex = Theme.entityKindHex other.Ref.Kind

                                 Button.create
                                     [ Button.background Theme.transparent
                                       Button.borderThickness 1.0
                                       Button.borderBrush (Theme.border ())
                                       Button.cornerRadius (Tokens.radius tokens 9.0)
                                       Button.padding (10.0, 6.0)
                                       Button.margin (0.0, 0.0, 0.0, 4.0)
                                       Button.horizontalAlignment HorizontalAlignment.Stretch
                                       Button.horizontalContentAlignment HorizontalAlignment.Left
                                       Button.cursor Ui.hand
                                       Ui.accessibleName (
                                           sprintf
                                               "Select %s, %s"
                                               other.Label
                                               (GraphLogic.kindLabel other.Ref.Kind)
                                       )
                                       Button.onClick (
                                           (fun _ -> dispatch (SelectNode other.Key)),
                                           SubPatchOptions.Always
                                       )
                                       Button.content (
                                           Grid.create
                                               [ Grid.columnDefinitions "Auto,8,*"
                                                 Grid.children
                                                     [ TextBlock.create
                                                           [ Grid.column 0
                                                             TextBlock.text (GraphLogic.kindGlyph other.Ref.Kind)
                                                             TextBlock.fontSize 11.0
                                                             TextBlock.foreground (
                                                                 Theme.brush (
                                                                     Theme.readableOnColor surface otherHex
                                                                 )
                                                             )
                                                             Ui.decorative () ]
                                                       TextBlock.create
                                                           [ Grid.column 2
                                                             TextBlock.text other.Label
                                                             TextBlock.fontSize 11.0
                                                             TextBlock.foreground (Theme.muted ())
                                                             TextBlock.textTrimming TextTrimming.CharacterEllipsis ] ] ]
                                       ) ]
                                 :> IView)) ]

                  if connections.Length > GraphLogic.Threshold.panelConnections then
                      TextBlock.create
                          [ TextBlock.text (
                                sprintf
                                    "and %d more"
                                    (connections.Length - GraphLogic.Threshold.panelConnections)
                            )
                            TextBlock.fontSize 10.0
                            TextBlock.foreground (Theme.faint ()) ] ]

        | GraphLogic.PanelRegion.Actions ->
            [ WrapPanel.create
                  [ WrapPanel.children
                        [ Ui.smallButton
                              (sprintf "Open %s" ((GraphLogic.kindLabel node.Ref.Kind).ToLowerInvariant()))
                              (fun () -> dispatch OpenSelected)
                          Ui.smallButton (if model.FocusMode then "Exit focus" else "Focus neighbours") (fun () ->
                              dispatch ToggleFocus)
                          Ui.smallButton "Centre" (fun () ->
                              dispatch (RunControl GraphLogic.Control.CentreSelected))
                          Ui.ghostButton "Clear" (fun () -> dispatch ClearSelection) ] ] ]

    Border.create
        [ Border.width width
          Border.child (
              overlay
                  tokens
                  16.0
                  [ DockPanel.create
                        [ DockPanel.children
                              [ Border.create
                                    [ DockPanel.dock Dock.Right
                                      Border.child (Ui.namedSmallButton "✕" "Clear selection" (fun () -> dispatch ClearSelection)) ]
                                Border.create
                                    [ Border.verticalAlignment VerticalAlignment.Center
                                      Border.child (sectionCaption "Node details") ] ] ]

                    StackPanel.create
                        [ StackPanel.spacing 12.0
                          StackPanel.children (GraphLogic.panelOrder |> List.collect region) ] ]
          ) ]
    :> IView

// ---------------------------------------------------------------------------
// page
// ---------------------------------------------------------------------------

let view (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let placement = GraphLogic.placementFor model.Width
    let shown = shownNodes model

    let selectedNode =
        model.Selected |> Option.bind (fun key -> model.Nodes |> List.tryFind (fun n -> n.Key = key))

    /// The one honest sentence about dragging, always on screen.
    let dragNotice: IView =
        TextBlock.create
            [ TextBlock.text GraphLogic.dragNotice
              TextBlock.fontSize 10.0
              TextBlock.foreground (Theme.faint ())
              TextBlock.textWrapping TextWrapping.Wrap ]
        :> IView

    /// Shown over the canvas when there is nothing to draw — the controls and
    /// the canvas boundary stay, so the page never becomes a blank rectangle.
    let emptyOverlay: IView list =
        if model.Loading then
            [ overlay tokens 20.0 [ bulletRow "•" (Theme.faint ()) "Building the graph…" (Theme.muted ()) ] ]
        elif model.Nodes.IsEmpty then
            [ overlay
                  tokens
                  20.0
                  [ bulletRow
                        "•"
                        (Theme.faint ())
                        "Nothing is in the graph yet. Create a module, a note, an assignment or a document and it appears here with its relationships."
                        (Theme.muted ()) ] ]
        elif shown.IsEmpty then
            [ overlay
                  tokens
                  20.0
                  [ bulletRow
                        "•"
                        (Theme.faint ())
                        (if model.FocusMode then
                             "That node has no direct links. Exit focus to see the whole graph."
                         else
                             "Every entity is hidden by the current filters.")
                        (Theme.muted ())
                    Ui.smallButton
                        (if model.FocusMode then "Exit focus" else "Clear filters")
                        (fun () -> dispatch (if model.FocusMode then ToggleFocus else ClearFilter)) ] ]
        else
            []

    let errorBanner: IView list =
        match model.Error with
        | Some error ->
            let wash =
                Theme.compositeOver (Theme.surfaces ()).Card (Theme.withAlphaHex "22" (Theme.dangerHex ()))

            [ Border.create
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
                                        [ TextBlock.text error
                                          TextBlock.fontSize Theme.Type.rowMeta
                                          TextBlock.foreground (
                                              Theme.brush (Theme.readableOnColor wash (Theme.dangerHex ()))
                                          )
                                          TextBlock.textWrapping TextWrapping.Wrap ]
                                    Ui.smallButton "Try again" (fun () -> dispatch Load) ] ]
                    ) ]
              :> IView ]
        | None -> []

    // The floating overlays, positioned as the redesign places them.
    let floatingLayer: IView =
        Canvas.create
            [ Canvas.children
                  [ Border.create
                        [ Canvas.left 20.0
                          Canvas.top 20.0
                          Border.child (titleAndSearch tokens model dispatch) ]

                    for banner in focusBanner tokens model dispatch do
                        Border.create [ Canvas.left 460.0; Canvas.top 20.0; Border.child banner ]

                    if placement.ShowLegend then
                        Border.create
                            [ Canvas.right 20.0
                              Canvas.top 20.0
                              Border.width GraphLogic.Geometry.legendWidth
                              Border.child (legendPanel tokens model dispatch) ]

                    Border.create
                        [ Canvas.left 20.0
                          Canvas.bottom 20.0
                          Border.width GraphLogic.Geometry.insightsWidth
                          Border.child (
                              StackPanel.create
                                  [ StackPanel.spacing 8.0
                                    StackPanel.children [ insightsPanel tokens model dispatch; dragNotice ] ]
                          ) ]

                    if placement.ShowMinimap then
                        Border.create
                            [ Canvas.right 20.0
                              Canvas.bottom 20.0
                              Border.width GraphLogic.Geometry.minimapWidth
                              Border.child (minimapPanel tokens model dispatch) ]

                    Border.create
                        [ Canvas.right (if placement.ShowMinimap then 200.0 else 20.0)
                          Canvas.bottom 20.0
                          Border.child (controlsPanel tokens model dispatch) ]

                    match selectedNode with
                    | Some node when placement.FloatingPanel ->
                        Border.create
                            [ Canvas.right (if placement.ShowLegend then 250.0 else 20.0)
                              Canvas.top 20.0
                              Border.child (nodePanel tokens model dispatch node placement.PanelWidth) ]
                    | _ -> ()

                    for banner in errorBanner do
                        Border.create [ Canvas.left 20.0; Canvas.top 260.0; Border.width 420.0; Border.child banner ]

                    for empty in emptyOverlay do
                        Border.create [ Canvas.left 20.0; Canvas.top 320.0; Border.width 420.0; Border.child empty ] ] ]
        :> IView

    let canvasArea: IView =
        Panel.create
            [ Panel.minHeight GraphLogic.Geometry.minCanvasHeight
              Panel.children [ canvasSurface model dispatch; floatingLayer ] ]
        :> IView

    Border.create
        [ Border.onSizeChanged (
              (fun args ->
                  if GraphLogic.placementFor model.Width <> GraphLogic.placementFor args.NewSize.Width then
                      dispatch (Resized args.NewSize.Width)),
              SubPatchOptions.Always
          )
          Border.child (
              // At narrow width the node panel stops floating and moves BELOW
              // the canvas, so a 280px panel cannot eat the whole graph.
              match selectedNode with
              | Some node when not placement.FloatingPanel ->
                  DockPanel.create
                      [ DockPanel.children
                            [ Border.create
                                  [ DockPanel.dock Dock.Bottom
                                    Border.margin (16.0, 12.0, 16.0, 16.0)
                                    Border.child (nodePanel tokens model dispatch node placement.PanelWidth) ]
                              canvasArea ] ]
                  :> IView
              | _ -> canvasArea
          ) ]
    :> IView
