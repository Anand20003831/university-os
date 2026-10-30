module UniversityOS.App.Features.GraphLogic

open System
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Design

/// Every decision and every coordinate calculation the Graph screen makes, as
/// pure functions — testable without an Avalonia application, a database or a
/// pointer device.
///
/// **Graph is an interaction screen, not a card dashboard**, and the engine it
/// already had is real: a deterministic force-directed layout, genuine zoom and
/// pan, kind filters, search and neighbourhood focus. This module does not
/// replace any of that. It lifts the arithmetic out of the view so the
/// world/screen/minimap transforms can be tested, and adds the two things the
/// redesign needs that were missing: a minimap and session-only node dragging.
///
/// Two settled decisions are encoded here rather than left to the view:
///
/// **1. The graph is undirected.** `Link` stores `From` and `To`, but most edges
/// are implicit — "assignment filed under module", "lecture has slides",
/// "flashcard came from a note" — and those have no consistent direction. Every
/// edge is normalised to `(min, max)` and de-duplicated, and nothing draws an
/// arrowhead.
///
/// **2. Node positions are adjustable for the session but are not persisted.**
/// Dragging writes to an in-memory override map. Nothing stores it, no "save
/// layout" action exists, and `resetLayout` clears it.

// ---------------------------------------------------------------------------
// geometry
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module Geometry =

    /// No specular pass on a content card.
    [<Literal>]
    let contentHighlight = 0.0

    [<Literal>]
    let borderInset = 0.5

    /// The logical field the layout runs on. Zoom and pan are view transforms
    /// over it. Kept at the existing values — a larger world would only add
    /// empty space to pan through.
    [<Literal>]
    let worldWidth = 1200.0

    [<Literal>]
    let worldHeight = 800.0

    /// A node is a fixed-size circle. **Size never varies with degree**: nothing
    /// stores importance, and area is the most misleading way to imply it.
    [<Literal>]
    let nodeSize = 34.0

    let nodeRadius = nodeSize / 2.0

    [<Literal>]
    let nodeRingThickness = 1.5

    /// Pointer slack when hit-testing a node, in world units. Without it a node
    /// needs pixel-perfect pointing at low zoom.
    [<Literal>]
    let hitPadding = 6.0

    [<Literal>]
    let edgeThickness = 1.0

    /// How far the pointer must travel before a press becomes a drag rather
    /// than a click. In screen pixels.
    [<Literal>]
    let dragThreshold = 4.0

    /// Zoom limits, preserved from the existing implementation.
    [<Literal>]
    let minZoom = 0.2

    [<Literal>]
    let maxZoom = 3.0

    [<Literal>]
    let zoomStep = 1.25

    /// The wheel's gentler factor.
    [<Literal>]
    let wheelStep = 1.1

    /// Margin left around the graph by Fit to View, in world units.
    [<Literal>]
    let fitMargin = 60.0

    /// Overlay widths, from the redesign.
    [<Literal>]
    let legendWidth = 210.0

    [<Literal>]
    let insightsWidth = 280.0

    [<Literal>]
    let minimapWidth = 160.0

    [<Literal>]
    let nodePanelWidth = 280.0

    /// The narrower node panel used at medium width.
    [<Literal>]
    let nodePanelNarrow = 240.0

    /// The minimap's inner field, from the redesign.
    [<Literal>]
    let minimapFieldWidth = 144.0

    [<Literal>]
    let minimapFieldHeight = 88.0

    [<Literal>]
    let minimapDotSize = 3.0

    /// The canvas never shrinks below this, however narrow the window.
    [<Literal>]
    let minCanvasHeight = 420.0

    [<Literal>]
    let overlayRadius = 16.0

    [<Literal>]
    let chipFillAlpha = "1F"

    [<Literal>]
    let selectedNodeAlpha = "2E"

    /// **One alpha, used BOTH to paint a resting edge and to measure it.**
    ///
    /// The edge is the text colour at this alpha over the canvas, rather than the
    /// `Divider` role: `Divider` is white-with-alpha, so its raw RGB is `FFFFFF`
    /// and anything reading it without compositing would call every edge
    /// near-white. Deriving from the text colour keeps paint and measurement on
    /// the same footing, in both themes.
    [<Literal>]
    let edgeAlpha = "3D"

    [<Literal>]
    let bulletColumns = "Auto,9,*"

    [<Literal>]
    let detailColumns = "Auto,10,*"

// ---------------------------------------------------------------------------
// documented thresholds
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module Threshold =

    /// A node with at least this many edges is called **well connected** — a
    /// factual statement about the number of relationships, never about
    /// academic importance.
    [<Literal>]
    let wellConnectedDegree = 6

    /// How many connected entities the node panel lists before truncating.
    [<Literal>]
    let panelConnections = 8

// ---------------------------------------------------------------------------
// nodes and edges
// ---------------------------------------------------------------------------

type GraphNode = { Key: string; Ref: EntityRef; Label: string }

/// The kinds that can appear as nodes, in chip order. Seven — one per entity
/// kind the loader genuinely produces.
let graphKinds =
    [ EntityKind.Module
      EntityKind.Assignment
      EntityKind.Note
      EntityKind.Document
      EntityKind.Lecture
      EntityKind.RevisionItem
      EntityKind.Event ]

let kindLabel (kind: EntityKind) =
    match kind with
    | EntityKind.Module -> "Module"
    | EntityKind.Assignment -> "Assignment"
    | EntityKind.Event -> "Event"
    | EntityKind.Note -> "Note"
    | EntityKind.Document -> "Document"
    | EntityKind.Lecture -> "Lecture"
    | EntityKind.InboxItem -> "Inbox item"
    | EntityKind.Session -> "Session"
    | EntityKind.Conversation -> "Conversation"
    | EntityKind.RevisionItem -> "Flashcard"

/// A glyph, so a node's kind is not conveyed by colour alone.
let kindGlyph (kind: EntityKind) =
    match kind with
    | EntityKind.Module -> "◫"
    // Text presentation, not the colour emoji Windows would otherwise
    // substitute — see `Typography.asText`.
    | EntityKind.Assignment -> Typography.asText "☑"
    | EntityKind.Note -> "▤"
    | EntityKind.Document -> "▧"
    | EntityKind.Lecture -> "▶"
    | EntityKind.RevisionItem -> "◈"
    | EntityKind.Event -> "▦"
    | EntityKind.InboxItem -> "▣"
    | EntityKind.Session -> "◷"
    | EntityKind.Conversation -> "✦"

let nodeKey (entity: EntityRef) =
    EntityKind.toString entity.Kind + ":" + entity.Id.ToString "N"

/// **The undirected rule, in one place.**
///
/// `Link` is directional, but the graph mixes explicit links with implicit ones
/// whose direction is not consistent, so every edge is ordered by key and the
/// pair de-duplicated. `a → b` and `b → a` are the same edge, and a self-edge is
/// not an edge at all.
let normaliseEdge (a: string, b: string) = (min a b, max a b)

let normaliseEdges (known: Set<string>) (edges: (string * string) list) =
    edges
    |> List.filter (fun (a, b) -> a <> b && known.Contains a && known.Contains b)
    |> List.map normaliseEdge
    |> List.distinct
    |> List.sort

/// Adjacency, computed once per load rather than per frame.
let adjacency (edges: (string * string) list) : Map<string, Set<string>> =
    edges
    |> List.collect (fun (a, b) -> [ a, b; b, a ])
    |> List.groupBy fst
    |> List.map (fun (k, pairs) -> k, pairs |> List.map snd |> Set.ofList)
    |> Map.ofList

/// **A factual count of relationships.** Not a centrality score, not importance.
let degree (adj: Map<string, Set<string>>) (key: string) =
    match adj.TryFind key with
    | Some set -> set.Count
    | None -> 0

// ---------------------------------------------------------------------------
// the coordinate model
// ---------------------------------------------------------------------------

/// The viewport transform. World coordinates are what the layout produces;
/// screen coordinates are what the canvas draws at.
type Viewport = { Zoom: float; PanX: float; PanY: float }

[<RequireQualifiedAccess>]
module Viewport =
    let identity = { Zoom = 1.0; PanX = 0.0; PanY = 0.0 }

let clampZoom (z: float) = max Geometry.minZoom (min Geometry.maxZoom z)

let worldToScreen (v: Viewport) (x: float, y: float) = x * v.Zoom + v.PanX, y * v.Zoom + v.PanY

let screenToWorld (v: Viewport) (sx: float, sy: float) = (sx - v.PanX) / v.Zoom, (sy - v.PanY) / v.Zoom

/// Zoom keeping the world point under the pointer stationary.
let zoomAboutPointer (v: Viewport) (px: float, py: float) (factor: float) =
    let next = clampZoom (v.Zoom * factor)
    let wx, wy = screenToWorld v (px, py)
    { Zoom = next; PanX = px - wx * next; PanY = py - wy * next }

let zoomAboutCentre (v: Viewport) (width: float, height: float) (factor: float) =
    zoomAboutPointer v (width / 2.0, height / 2.0) factor

/// The bounding box of a set of positions, or `None` when there are none.
let bounds (positions: (float * float) seq) =
    let points = positions |> Seq.toList

    match points with
    | [] -> None
    | _ ->
        let xs = points |> List.map fst
        let ys = points |> List.map snd
        Some(List.min xs, List.min ys, List.max xs, List.max ys)

/// Fit every given position into the viewport, with a margin.
///
/// **It reads the positions it is handed**, so a graph whose nodes have been
/// dragged fits to where they actually are, not to where the layout first put
/// them.
let fitToView (width: float, height: float) (positions: (float * float) seq) =
    match bounds positions with
    | None -> Viewport.identity
    | Some(minX, minY, maxX, maxY) ->
        let spanX = max 1.0 (maxX - minX)
        let spanY = max 1.0 (maxY - minY)

        let zoom =
            clampZoom (
                min
                    ((width - 2.0 * Geometry.fitMargin) / spanX)
                    ((height - 2.0 * Geometry.fitMargin) / spanY)
            )

        let centreX = (minX + maxX) / 2.0
        let centreY = (minY + maxY) / 2.0

        { Zoom = zoom
          PanX = width / 2.0 - centreX * zoom
          PanY = height / 2.0 - centreY * zoom }

/// Centre one world point in the viewport, keeping the current zoom.
let centreOn (v: Viewport) (width: float, height: float) (x: float, y: float) =
    { v with PanX = width / 2.0 - x * v.Zoom; PanY = height / 2.0 - y * v.Zoom }

/// The node under a screen point, if any. Nearest centre wins, so overlapping
/// nodes resolve predictably rather than by draw order.
let hitTest (v: Viewport) (positions: (string * (float * float)) list) (sx: float, sy: float) =
    let wx, wy = screenToWorld v (sx, sy)
    let reach = Geometry.nodeRadius + Geometry.hitPadding

    positions
    |> List.choose (fun (key, (x, y)) ->
        let dx = x - wx
        let dy = y - wy
        let d = sqrt (dx * dx + dy * dy)
        if d <= reach then Some(d, key) else None)
    |> function
        | [] -> None
        | hits -> hits |> List.minBy fst |> snd |> Some

// ---------------------------------------------------------------------------
// the minimap
// ---------------------------------------------------------------------------

/// The minimap projects the **same bounds Fit to View uses**, so the two can
/// never disagree about where the graph is.
let minimapScale (positions: (float * float) seq) =
    match bounds positions with
    | None -> 1.0, 0.0, 0.0
    | Some(minX, minY, maxX, maxY) ->
        let spanX = max 1.0 (maxX - minX)
        let spanY = max 1.0 (maxY - minY)
        let scale = min (Geometry.minimapFieldWidth / spanX) (Geometry.minimapFieldHeight / spanY)
        scale, minX, minY

let minimapProject (scale: float, originX: float, originY: float) (x: float, y: float) =
    (x - originX) * scale, (y - originY) * scale

let minimapUnproject (scale: float, originX: float, originY: float) (mx: float, my: float) =
    mx / scale + originX, my / scale + originY

/// The rectangle of world currently on screen, in minimap coordinates.
let minimapViewport
    (v: Viewport)
    (width: float, height: float)
    (projection: float * float * float)
    : float * float * float * float =
    let topLeft = screenToWorld v (0.0, 0.0)
    let bottomRight = screenToWorld v (width, height)
    let x1, y1 = minimapProject projection topLeft
    let x2, y2 = minimapProject projection bottomRight
    x1, y1, max 1.0 (x2 - x1), max 1.0 (y2 - y1)

// ---------------------------------------------------------------------------
// layout state — base, overrides, viewport
// ---------------------------------------------------------------------------

/// Where a node actually is: the session override when it has one, otherwise the
/// deterministic layout's answer.
let effectivePosition (baseLayout: Map<string, float * float>) (overrides: Map<string, float * float>) (key: string) =
    match overrides.TryFind key with
    | Some p -> Some p
    | None -> baseLayout.TryFind key

let effectivePositions
    (baseLayout: Map<string, float * float>)
    (overrides: Map<string, float * float>)
    (keys: string list)
    =
    keys |> List.choose (fun k -> effectivePosition baseLayout overrides k |> Option.map (fun p -> k, p))

/// A refresh keeps the overrides of entities that still exist and drops the rest,
/// so deleting a node does not leave a ghost position behind.
let retainOverrides (known: Set<string>) (overrides: Map<string, float * float>) =
    overrides |> Map.filter (fun k _ -> known.Contains k)

// ---------------------------------------------------------------------------
// interaction state
// ---------------------------------------------------------------------------

/// A press that has not yet decided whether it is a click or a drag.
type Press =
    { Node: string option
      StartX: float
      StartY: float
      /// Pointer-to-node offset in WORLD units, so the node does not jump to the
      /// pointer when the drag begins.
      OffsetX: float
      OffsetY: float }

/// **Explicit states**, so a node press can never become a canvas pan.
[<RequireQualifiedAccess>]
type Interaction =
    | Idle
    | Pressed of Press
    | Panning of lastX: float * lastY: float
    | DraggingNode of key: string * offsetX: float * offsetY: float

let movedEnough (p: Press) (x: float, y: float) =
    let dx = x - p.StartX
    let dy = y - p.StartY
    sqrt (dx * dx + dy * dy) >= Geometry.dragThreshold

/// What a press becomes once the pointer has moved far enough: a node drag if it
/// started on a node, a canvas pan otherwise.
let beginMove (p: Press) (x: float, y: float) =
    match p.Node with
    | Some key -> Interaction.DraggingNode(key, p.OffsetX, p.OffsetY)
    | None -> Interaction.Panning(x, y)

// ---------------------------------------------------------------------------
// filtering, search and focus
// ---------------------------------------------------------------------------

/// An **inclusion** set: empty means every kind, matching the existing screen.
let visibleNodes (filter: Set<EntityKind>) (nodes: GraphNode list) =
    if filter.IsEmpty then nodes else nodes |> List.filter (fun n -> filter.Contains n.Ref.Kind)

/// Hiding a node hides every edge that touches it.
let visibleEdges (visibleKeys: Set<string>) (edges: (string * string) list) =
    edges |> List.filter (fun (a, b) -> visibleKeys.Contains a && visibleKeys.Contains b)

/// **Neighbourhood focus is exactly one hop.** The selected node and the nodes
/// sharing an edge with it — a deterministic graph operation, not a cluster and
/// not anything a model produced.
let neighbourhood (adj: Map<string, Set<string>>) (selected: string) =
    match adj.TryFind selected with
    | Some set -> set.Add selected
    | None -> Set.singleton selected

/// The nodes on screen, after filters and any focus.
let shownNodes
    (filter: Set<EntityKind>)
    (focus: string option)
    (adj: Map<string, Set<string>>)
    (nodes: GraphNode list)
    =
    let byFilter = visibleNodes filter nodes

    match focus with
    | Some selected ->
        let keep = neighbourhood adj selected
        byFilter |> List.filter (fun n -> keep.Contains n.Key)
    | None -> byFilter

let searchMatches (query: string) (node: GraphNode) =
    let q = query.Trim()

    if q.Length = 0 then
        true
    else
        node.Label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
        || (kindLabel node.Ref.Kind).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0

/// The node a search should focus. Deterministic: the first match in the shown
/// order, so pressing Enter twice does not land somewhere else.
let firstMatch (query: string) (nodes: GraphNode list) =
    if query.Trim().Length = 0 then None
    else nodes |> List.filter (searchMatches query) |> List.tryHead

// ---------------------------------------------------------------------------
// rule-based insights
// ---------------------------------------------------------------------------

/// Connected components, by union over the edge list. Deterministic, and a real
/// property of the graph rather than an inferred cluster.
let componentCount (keys: string list) (edges: (string * string) list) =
    let parent = System.Collections.Generic.Dictionary<string, string>()
    for k in keys do parent.[k] <- k

    let rec find (k: string) =
        if parent.[k] = k then k
        else
            let root = find parent.[k]
            parent.[k] <- root
            root

    for a, b in edges do
        if parent.ContainsKey a && parent.ContainsKey b then
            let ra = find a
            let rb = find b
            if ra <> rb then parent.[ra] <- rb

    keys |> List.map find |> List.distinct |> List.length

/// The redesign calls this "AI Insights". **Nothing here comes from a model.**
/// Every line is a count over the graph the user is looking at.
let insights
    (nodes: GraphNode list)
    (edges: (string * string) list)
    (adj: Map<string, Set<string>>)
    (hiddenCount: int)
    : (string * string) list =
    let keys = nodes |> List.map (fun n -> n.Key)
    let isolated = keys |> List.filter (fun k -> degree adj k = 0) |> List.length

    let wellConnected =
        keys |> List.filter (fun k -> degree adj k >= Threshold.wellConnectedDegree) |> List.length

    let components = componentCount keys edges

    [ if nodes.IsEmpty then
          "•", "Nothing is in the graph yet. Create a module, a note or a document and it appears here."
      else
          "⛓",
          sprintf
              "%d %s and %d %s."
              nodes.Length
              (if nodes.Length = 1 then "entity" else "entities")
              edges.Length
              (if edges.Length = 1 then "relationship" else "relationships")

          if isolated > 0 then
              "○",
              sprintf
                  "%d %s nothing linked to %s."
                  isolated
                  (if isolated = 1 then "entity has" else "entities have")
                  (if isolated = 1 then "it" else "them")

          if wellConnected > 0 then
              "✦",
              sprintf
                  "%d %s %d or more relationships. That is a count of links, not a measure of importance."
                  wellConnected
                  (if wellConnected = 1 then "entity has" else "entities have")
                  Threshold.wellConnectedDegree

          if components > 1 then
              "▚", sprintf "The graph falls into %d separate groups with no link between them." components

      if hiddenCount > 0 then
          "◍", sprintf "%d %s hidden by the current filters." hiddenCount (if hiddenCount = 1 then "entity is" else "entities are") ]

/// The legend's entity rows: every kind actually present, with its count.
let legendItems (nodes: GraphNode list) =
    graphKinds
    |> List.map (fun kind -> kind, nodes |> List.filter (fun n -> n.Ref.Kind = kind) |> List.length)
    |> List.filter (fun (_, n) -> n > 0)

/// The real relationship kinds this graph contains, for the legend's lower half.
/// Each is a genuine rule in the loader — none is inferred.
let relationshipKinds =
    [ "Filed under a module"
      "Lecture slides and recordings"
      "Flashcards and their source"
      "Explicit links, including [[wiki links]]" ]

// ---------------------------------------------------------------------------
// page composition
// ---------------------------------------------------------------------------

/// The overlays, in the redesign's own arrangement. The canvas is not in this
/// list because it is the surface they float over, not a sibling.
[<RequireQualifiedAccess>]
type Overlay =
    | TitleAndSearch
    | FocusBanner
    | Legend
    | Insights
    | Minimap
    | Controls
    | NodePanel

let overlayOrder =
    [ Overlay.TitleAndSearch
      Overlay.FocusBanner
      Overlay.Legend
      Overlay.Insights
      Overlay.Minimap
      Overlay.Controls
      Overlay.NodePanel ]

/// The node panel's regions, in order.
[<RequireQualifiedAccess>]
type PanelRegion =
    | Identity
    | Facts
    | Connections
    | Actions

let panelOrder = [ PanelRegion.Identity; PanelRegion.Facts; PanelRegion.Connections; PanelRegion.Actions ]

/// The toolbar's controls. **Reset View and Reset Layout are separate**, because
/// they undo different things and merging them would misdescribe both.
[<RequireQualifiedAccess>]
type Control =
    | ZoomIn
    | ZoomOut
    | FitToView
    | ResetView
    | ResetLayout
    | CentreSelected

[<RequireQualifiedAccess>]
module Control =

    let all =
        [ Control.ZoomIn
          Control.ZoomOut
          Control.FitToView
          Control.ResetView
          Control.ResetLayout
          Control.CentreSelected ]

    let glyph =
        function
        | Control.ZoomIn -> "＋"
        | Control.ZoomOut -> "－"
        | Control.FitToView -> "⤢"
        | Control.ResetView -> "⟲"
        | Control.ResetLayout -> "⁘"
        | Control.CentreSelected -> "◎"

    let label =
        function
        | Control.ZoomIn -> "Zoom in"
        | Control.ZoomOut -> "Zoom out"
        | Control.FitToView -> "Fit the graph to the view"
        | Control.ResetView -> "Reset the pan and zoom"
        | Control.ResetLayout -> "Reset moved nodes to the computed layout"
        | Control.CentreSelected -> "Centre on the selected node"

/// **The sentence this screen must not get wrong.**
[<Literal>]
let dragNotice =
    "Node positions are adjustable for the current session but are not persisted — reopening Graph recomputes the layout."

// ---------------------------------------------------------------------------
// responsive layout
// ---------------------------------------------------------------------------

[<Literal>]
let mediumWidth = 1280.0

[<Literal>]
let narrowWidth = 900.0

/// Which overlays float over the canvas at this width.
///
/// Below `narrowWidth` the node panel stops floating and moves **below** the
/// canvas, because a 280px panel over a 900px window leaves no graph.
type Placement = { FloatingPanel: bool; ShowLegend: bool; ShowMinimap: bool; PanelWidth: float }

let placementFor (width: float) =
    if width < narrowWidth then
        { FloatingPanel = false
          ShowLegend = false
          ShowMinimap = false
          PanelWidth = Geometry.nodePanelWidth }
    elif width < mediumWidth then
        { FloatingPanel = true
          ShowLegend = false
          ShowMinimap = true
          PanelWidth = Geometry.nodePanelNarrow }
    else
        { FloatingPanel = true
          ShowLegend = true
          ShowMinimap = true
          PanelWidth = Geometry.nodePanelWidth }
