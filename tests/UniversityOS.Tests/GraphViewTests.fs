module UniversityOS.Tests.GraphViewTests

open System
open Xunit
open Avalonia.Media
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Features

/// The migrated Graph screen.
///
/// Graph is an interaction screen, so most of what matters is arithmetic and a
/// state machine — both of which live in `GraphLogic` and are exercised here
/// without an Avalonia application, a database or a pointer device.

let private node (kind: EntityKind) (label: string) : GraphLogic.GraphNode =
    let entity = { Kind = kind; Id = Guid.NewGuid() }
    { Key = GraphLogic.nodeKey entity; Ref = entity; Label = label }

let private keyed (kind: EntityKind) (label: string) (id: Guid) : GraphLogic.GraphNode =
    let entity = { Kind = kind; Id = id }
    { Key = GraphLogic.nodeKey entity; Ref = entity; Label = label }

// ---------------------------------------------------------------------------
// 1. composition
// ---------------------------------------------------------------------------

[<Fact>]
let ``the overlays are the redesign's, in order`` () =
    Assert.Equal<GraphLogic.Overlay list>(
        [ GraphLogic.Overlay.TitleAndSearch
          GraphLogic.Overlay.FocusBanner
          GraphLogic.Overlay.Legend
          GraphLogic.Overlay.Insights
          GraphLogic.Overlay.Minimap
          GraphLogic.Overlay.Controls
          GraphLogic.Overlay.NodePanel ],
        GraphLogic.overlayOrder
    )

    Assert.Equal(GraphLogic.overlayOrder.Length, (List.distinct GraphLogic.overlayOrder).Length)

[<Fact>]
let ``the node panel runs identity, facts, connections, actions`` () =
    Assert.Equal<GraphLogic.PanelRegion list>(
        [ GraphLogic.PanelRegion.Identity
          GraphLogic.PanelRegion.Facts
          GraphLogic.PanelRegion.Connections
          GraphLogic.PanelRegion.Actions ],
        GraphLogic.panelOrder
    )

[<Fact>]
let ``there are seven real entity kinds, one chip each`` () =
    Assert.Equal(7, GraphLogic.graphKinds.Length)
    Assert.Equal(GraphLogic.graphKinds.Length, (List.distinct GraphLogic.graphKinds).Length)

    for kind in GraphLogic.graphKinds do
        Assert.False(String.IsNullOrWhiteSpace(GraphLogic.kindLabel kind))
        // A glyph as well as a colour, so kind is never conveyed by colour alone.
        Assert.False(String.IsNullOrWhiteSpace(GraphLogic.kindGlyph kind))

[<Fact>]
let ``Reset View and Reset Layout are separate controls with different words`` () =
    Assert.Contains(GraphLogic.Control.ResetView, GraphLogic.Control.all)
    Assert.Contains(GraphLogic.Control.ResetLayout, GraphLogic.Control.all)

    let viewLabel = GraphLogic.Control.label GraphLogic.Control.ResetView
    let layoutLabel = GraphLogic.Control.label GraphLogic.Control.ResetLayout

    Assert.NotEqual<string>(viewLabel, layoutLabel)
    Assert.Contains("pan and zoom", viewLabel)
    Assert.Contains("moved nodes", layoutLabel)

    for control in GraphLogic.Control.all do
        Assert.False(String.IsNullOrWhiteSpace(GraphLogic.Control.glyph control))
        Assert.False(String.IsNullOrWhiteSpace(GraphLogic.Control.label control))

[<Fact>]
let ``the drag notice says positions are not persisted`` () =
    let text = GraphLogic.dragNotice
    Assert.Contains("not persisted", text)
    Assert.DoesNotContain("saved", text.ToLowerInvariant())

// ---------------------------------------------------------------------------
// 2. edge semantics — undirected, deduplicated
// ---------------------------------------------------------------------------

[<Fact>]
let ``an edge is normalised so direction cannot survive`` () =
    Assert.Equal(("a", "b"), GraphLogic.normaliseEdge ("a", "b"))
    Assert.Equal(("a", "b"), GraphLogic.normaliseEdge ("b", "a"))

[<Fact>]
let ``a reversed duplicate collapses into one edge`` () =
    let known = Set.ofList [ "a"; "b" ]
    Assert.Equal<(string * string) list>([ "a", "b" ], GraphLogic.normaliseEdges known [ "a", "b"; "b", "a" ])

[<Fact>]
let ``a self-edge is not an edge, and an unknown endpoint is dropped`` () =
    let known = Set.ofList [ "a"; "b" ]

    // Self-edge, and an edge to something that is not a node.
    Assert.Equal<(string * string) list>(
        [ "a", "b" ],
        GraphLogic.normaliseEdges known [ "a", "a"; "a", "b"; "a", "ghost" ]
    )

[<Fact>]
let ``edges come back in a deterministic order`` () =
    let known = Set.ofList [ "a"; "b"; "c" ]
    let one = GraphLogic.normaliseEdges known [ "c", "a"; "b", "a"; "a", "c" ]
    let two = GraphLogic.normaliseEdges known [ "a", "b"; "a", "c"; "c", "a" ]
    Assert.Equal<(string * string) list>(one, two)

[<Fact>]
let ``degree is a count of relationships, both directions included`` () =
    let adj = GraphLogic.adjacency [ "a", "b"; "a", "c" ]
    Assert.Equal(2, GraphLogic.degree adj "a")
    Assert.Equal(1, GraphLogic.degree adj "b")
    Assert.Equal(0, GraphLogic.degree adj "orphan")

// ---------------------------------------------------------------------------
// 3. the coordinate model
// ---------------------------------------------------------------------------

let private approx (expected: float) (actual: float) =
    Assert.True(abs (expected - actual) < 1e-9, sprintf "expected %f, got %f" expected actual)

[<Fact>]
let ``world and screen conversions round-trip exactly`` () =
    let v = { GraphLogic.Zoom = 1.7; GraphLogic.PanX = -120.0; GraphLogic.PanY = 46.0 }
    let sx, sy = GraphLogic.worldToScreen v (300.0, 200.0)
    let wx, wy = GraphLogic.screenToWorld v (sx, sy)

    approx 300.0 wx
    approx 200.0 wy

[<Fact>]
let ``zoom is clamped to the real range`` () =
    Assert.Equal(0.2, GraphLogic.Geometry.minZoom, 6)
    Assert.Equal(3.0, GraphLogic.Geometry.maxZoom, 6)
    Assert.Equal(0.2, GraphLogic.clampZoom 0.01, 6)
    Assert.Equal(3.0, GraphLogic.clampZoom 99.0, 6)
    Assert.Equal(1.5, GraphLogic.clampZoom 1.5, 6)

[<Fact>]
let ``zooming about the pointer keeps that world point under it`` () =
    let v = GraphLogic.Viewport.identity
    let pointer = 400.0, 250.0
    let before = GraphLogic.screenToWorld v pointer
    let after = GraphLogic.zoomAboutPointer v pointer 1.25
    let stillThere = GraphLogic.screenToWorld after pointer

    approx (fst before) (fst stillThere)
    approx (snd before) (snd stillThere)
    Assert.Equal(1.25, after.Zoom, 6)

[<Fact>]
let ``zooming about the pointer respects the clamp`` () =
    let v = { GraphLogic.Viewport.identity with Zoom = 2.9 }
    Assert.Equal(3.0, (GraphLogic.zoomAboutPointer v (10.0, 10.0) 5.0).Zoom, 6)

[<Fact>]
let ``fit to view centres the graph and stays inside the zoom range`` () =
    let positions = [ 100.0, 100.0; 500.0, 400.0 ]
    let v = GraphLogic.fitToView (800.0, 600.0) positions

    Assert.True(v.Zoom >= GraphLogic.Geometry.minZoom && v.Zoom <= GraphLogic.Geometry.maxZoom)

    // The bounding-box centre lands in the middle of the viewport.
    let cx, cy = GraphLogic.worldToScreen v (300.0, 250.0)
    approx 400.0 cx
    approx 300.0 cy

[<Fact>]
let ``fit to view on an empty graph is the identity, not a crash`` () =
    Assert.Equal(GraphLogic.Viewport.identity, GraphLogic.fitToView (800.0, 600.0) [])

[<Fact>]
let ``fit to view reads the positions it is given, so dragged nodes are included`` () =
    // The same call with a node moved far away must produce a different fit.
    let before = GraphLogic.fitToView (800.0, 600.0) [ 100.0, 100.0; 200.0, 200.0 ]
    let after = GraphLogic.fitToView (800.0, 600.0) [ 100.0, 100.0; 900.0, 900.0 ]
    Assert.NotEqual(before.Zoom, after.Zoom)

[<Fact>]
let ``centring puts a world point in the middle without changing zoom`` () =
    let v = { GraphLogic.Viewport.identity with Zoom = 2.0 }
    let centred = GraphLogic.centreOn v (800.0, 600.0) (150.0, 90.0)

    Assert.Equal(2.0, centred.Zoom, 6)
    let sx, sy = GraphLogic.worldToScreen centred (150.0, 90.0)
    approx 400.0 sx
    approx 300.0 sy

[<Fact>]
let ``hit testing finds the nearest node within tolerance and nothing beyond it`` () =
    let v = GraphLogic.Viewport.identity
    let positions = [ "a", (100.0, 100.0); "b", (300.0, 100.0) ]

    Assert.Equal(Some "a", GraphLogic.hitTest v positions (104.0, 100.0))
    Assert.Equal(Some "b", GraphLogic.hitTest v positions (296.0, 100.0))
    Assert.Equal(None, GraphLogic.hitTest v positions (200.0, 100.0))

// ---------------------------------------------------------------------------
// 4. the minimap
// ---------------------------------------------------------------------------

[<Fact>]
let ``the minimap projects the same bounds fit-to-view uses`` () =
    let positions = [ 100.0, 100.0; 500.0, 400.0 ]
    let scale, ox, oy = GraphLogic.minimapScale positions

    // The origin is the bounding-box corner both share.
    approx 100.0 ox
    approx 100.0 oy

    // The far corner lands inside the field, not outside it.
    let mx, my = GraphLogic.minimapProject (scale, ox, oy) (500.0, 400.0)
    Assert.True(mx <= GraphLogic.Geometry.minimapFieldWidth + 0.001)
    Assert.True(my <= GraphLogic.Geometry.minimapFieldHeight + 0.001)

[<Fact>]
let ``minimap projection round-trips`` () =
    let projection = GraphLogic.minimapScale [ 0.0, 0.0; 400.0, 300.0 ]
    let mx, my = GraphLogic.minimapProject projection (250.0, 120.0)
    let wx, wy = GraphLogic.minimapUnproject projection (mx, my)

    approx 250.0 wx
    approx 120.0 wy

[<Fact>]
let ``the viewport rectangle moves when the view pans, and shrinks when it zooms`` () =
    let projection = GraphLogic.minimapScale [ 0.0, 0.0; 1200.0, 800.0 ]
    let size = 800.0, 600.0

    let x1, _, w1, _ = GraphLogic.minimapViewport GraphLogic.Viewport.identity size projection

    let panned =
        GraphLogic.minimapViewport { GraphLogic.Viewport.identity with PanX = -200.0 } size projection

    let x2, _, _, _ = panned
    Assert.True(x2 > x1, "panning left did not move the viewport rectangle right")

    let zoomed =
        GraphLogic.minimapViewport { GraphLogic.Viewport.identity with Zoom = 2.0 } size projection

    let _, _, w2, _ = zoomed
    Assert.True(w2 < w1, "zooming in did not shrink the viewport rectangle")

// ---------------------------------------------------------------------------
// 5. filters, focus and search
// ---------------------------------------------------------------------------

[<Fact>]
let ``an empty filter shows every kind, and a non-empty one includes`` () =
    let nodes = [ node EntityKind.Note "n"; node EntityKind.Module "m" ]

    Assert.Equal(2, (GraphLogic.visibleNodes Set.empty nodes).Length)
    Assert.Equal(1, (GraphLogic.visibleNodes (Set.singleton EntityKind.Note) nodes).Length)

[<Fact>]
let ``hiding a node hides every edge that touches it`` () =
    let visible = Set.ofList [ "a"; "b" ]
    Assert.Equal<(string * string) list>(
        [ "a", "b" ],
        GraphLogic.visibleEdges visible [ "a", "b"; "a", "hidden"; "hidden", "b" ]
    )

[<Fact>]
let ``neighbourhood focus is exactly one hop, and includes the node itself`` () =
    let adj = GraphLogic.adjacency [ "a", "b"; "b", "c" ]
    let hood = GraphLogic.neighbourhood adj "a"

    Assert.True(hood.Contains "a")
    Assert.True(hood.Contains "b")
    Assert.False(hood.Contains "c", "focus reached two hops")

[<Fact>]
let ``a node with no links focuses to itself alone`` () =
    Assert.Equal<Set<string>>(Set.singleton "lonely", GraphLogic.neighbourhood Map.empty "lonely")

[<Fact>]
let ``focus and filters compose`` () =
    let a = keyed EntityKind.Note "A" (Guid.NewGuid())
    let b = keyed EntityKind.Module "B" (Guid.NewGuid())
    let c = keyed EntityKind.Note "C" (Guid.NewGuid())
    let adj = GraphLogic.adjacency [ a.Key, b.Key; b.Key, c.Key ]

    let focused = GraphLogic.shownNodes Set.empty (Some a.Key) adj [ a; b; c ]
    Assert.Equal<string list>([ a.Key; b.Key ], focused |> List.map (fun n -> n.Key))

[<Fact>]
let ``search matches label and kind, case-insensitively, and is deterministic`` () =
    let nodes = [ keyed EntityKind.Note "SQL joins" (Guid.NewGuid()); keyed EntityKind.Module "CS2004" (Guid.NewGuid()) ]

    Assert.True(GraphLogic.searchMatches "" nodes.Head)
    Assert.True(GraphLogic.searchMatches "sql" nodes.Head)
    Assert.True(GraphLogic.searchMatches "MODULE" nodes.[1])
    Assert.False(GraphLogic.searchMatches "kubernetes" nodes.Head)

    Assert.Equal(None, GraphLogic.firstMatch "" nodes)
    Assert.Equal(Some "SQL joins", GraphLogic.firstMatch "sql" nodes |> Option.map (fun n -> n.Label))
    Assert.Equal(GraphLogic.firstMatch "s" nodes, GraphLogic.firstMatch "s" nodes)

// ---------------------------------------------------------------------------
// 6. layout state — base, overrides, reset
// ---------------------------------------------------------------------------

[<Fact>]
let ``a session override wins over the computed layout`` () =
    let base' = Map.ofList [ "a", (10.0, 10.0) ]
    let overrides = Map.ofList [ "a", (99.0, 99.0) ]

    Assert.Equal(Some(99.0, 99.0), GraphLogic.effectivePosition base' overrides "a")
    Assert.Equal(Some(10.0, 10.0), GraphLogic.effectivePosition base' Map.empty "a")
    Assert.Equal(None, GraphLogic.effectivePosition base' overrides "missing")

[<Fact>]
let ``a refresh keeps overrides for entities that still exist and drops the rest`` () =
    let overrides = Map.ofList [ "kept", (1.0, 1.0); "deleted", (2.0, 2.0) ]
    let retained = GraphLogic.retainOverrides (Set.singleton "kept") overrides

    Assert.True(retained.ContainsKey "kept")
    Assert.False(retained.ContainsKey "deleted")

[<Fact>]
let ``the deterministic layout gives the same answer twice`` () =
    let keys = [ "c"; "a"; "b"; "d" ]
    let edges = [ "a", "b"; "b", "c" ]

    let one = GraphLayout.layout 1200.0 800.0 40 keys edges
    let two = GraphLayout.layout 1200.0 800.0 40 keys edges

    Assert.Equal<(string * (float * float)) list>(Map.toList one, Map.toList two)

[<Fact>]
let ``the layout is seeded from sorted keys, so input order does not matter`` () =
    let edges = [ "a", "b" ]
    let one = GraphLayout.layout 1200.0 800.0 40 [ "a"; "b"; "c" ] edges
    let two = GraphLayout.layout 1200.0 800.0 40 [ "c"; "b"; "a" ] edges

    Assert.Equal<(string * (float * float)) list>(Map.toList one, Map.toList two)

// ---------------------------------------------------------------------------
// 7. the interaction state machine
// ---------------------------------------------------------------------------

let private press (nodeKey: string option) (x: float) (y: float) : GraphLogic.Press =
    { Node = nodeKey; StartX = x; StartY = y; OffsetX = 0.0; OffsetY = 0.0 }

[<Fact>]
let ``a small movement is still a click, and a larger one becomes a move`` () =
    let p = press None 100.0 100.0

    Assert.False(GraphLogic.movedEnough p (101.0, 101.0))
    Assert.True(GraphLogic.movedEnough p (100.0 + GraphLogic.Geometry.dragThreshold, 100.0))
    Assert.True(GraphLogic.Geometry.dragThreshold > 0.0)

[<Fact>]
let ``a press on a node becomes a node drag, and on empty canvas a pan`` () =
    match GraphLogic.beginMove (press (Some "n") 0.0 0.0) (10.0, 10.0) with
    | GraphLogic.Interaction.DraggingNode(key, _, _) -> Assert.Equal("n", key)
    | other -> failwithf "expected a node drag, got %A" other

    match GraphLogic.beginMove (press None 0.0 0.0) (10.0, 10.0) with
    | GraphLogic.Interaction.Panning _ -> ()
    | other -> failwithf "expected a pan, got %A" other

// ---------------------------------------------------------------------------
// 8. update behaviour
// ---------------------------------------------------------------------------

let private noServices: AppServices = Unchecked.defaultof<AppServices>

let private a = keyed EntityKind.Note "A note" (Guid.NewGuid())
let private b = keyed EntityKind.Module "CS2004" (Guid.NewGuid())

let private baseModel: GraphView.Model =
    { Nodes = [ a; b ]
      Edges = [ GraphLogic.normaliseEdge (a.Key, b.Key) ]
      Adjacency = GraphLogic.adjacency [ a.Key, b.Key ]
      BaseLayout = Map.ofList [ a.Key, (100.0, 100.0); b.Key, (300.0, 200.0) ]
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
      CanvasWidth = 800.0
      CanvasHeight = 600.0
      Width = 1600.0
      Loading = false
      Error = None }

let private modelOf (m, _, _) = m
let private cmdOf (_, c, _) = c
let private externalOf (_, _, e) = e

[<Fact>]
let ``pressing a node records the press without selecting or panning yet`` () =
    let after = GraphView.update noServices (GraphView.NodePressed(a.Key, 100.0, 100.0)) baseModel |> modelOf

    match after.Interaction with
    | GraphLogic.Interaction.Pressed p -> Assert.Equal(Some a.Key, p.Node)
    | other -> failwithf "expected a press, got %A" other

    Assert.Equal(GraphLogic.Viewport.identity, after.View)

[<Fact>]
let ``releasing without moving selects the node`` () =
    let pressed = GraphView.update noServices (GraphView.NodePressed(a.Key, 100.0, 100.0)) baseModel |> modelOf
    let released = GraphView.update noServices GraphView.PointerReleased pressed |> modelOf

    Assert.Equal(Some a.Key, released.Selected)
    Assert.Equal(GraphLogic.Interaction.Idle, released.Interaction)
    Assert.True(released.Overrides.IsEmpty, "a click created a session position")

[<Fact>]
let ``dragging a node moves only that node, and never the viewport`` () =
    let pressed = GraphView.update noServices (GraphView.NodePressed(a.Key, 100.0, 100.0)) baseModel |> modelOf
    let moving = GraphView.update noServices (GraphView.PointerMoved(160.0, 140.0)) pressed |> modelOf
    let dragged = GraphView.update noServices (GraphView.PointerMoved(200.0, 180.0)) moving |> modelOf

    Assert.Equal(1, dragged.Overrides.Count)
    Assert.True(dragged.Overrides.ContainsKey a.Key)
    Assert.False(dragged.Overrides.ContainsKey b.Key, "an unrelated node moved")

    // The canvas did not pan.
    Assert.Equal(GraphLogic.Viewport.identity, dragged.View)

    // And the base layout is untouched — the override is the only change.
    Assert.Equal(Some(100.0, 100.0), dragged.BaseLayout.TryFind a.Key)

[<Fact>]
let ``dragging empty canvas pans and never creates a session position`` () =
    let pressed = GraphView.update noServices (GraphView.CanvasPressed(100.0, 100.0)) baseModel |> modelOf
    let moving = GraphView.update noServices (GraphView.PointerMoved(160.0, 140.0)) pressed |> modelOf
    let panned = GraphView.update noServices (GraphView.PointerMoved(200.0, 180.0)) moving |> modelOf

    Assert.True(panned.Overrides.IsEmpty, "panning moved a node")
    Assert.NotEqual(GraphLogic.Viewport.identity.PanX, panned.View.PanX)

[<Fact>]
let ``a press below the threshold has not begun anything`` () =
    let pressed = GraphView.update noServices (GraphView.CanvasPressed(100.0, 100.0)) baseModel |> modelOf
    let nudged = GraphView.update noServices (GraphView.PointerMoved(101.0, 101.0)) pressed |> modelOf

    match nudged.Interaction with
    | GraphLogic.Interaction.Pressed _ -> ()
    | other -> failwithf "a 1px nudge started %A" other

    Assert.Equal(GraphLogic.Viewport.identity, nudged.View)

[<Fact>]
let ``releasing always returns to idle, so a drag cannot be left running`` () =
    for interaction in
        [ GraphLogic.Interaction.Panning(1.0, 1.0); GraphLogic.Interaction.DraggingNode(a.Key, 0.0, 0.0) ] do
        let after =
            GraphView.update noServices GraphView.PointerReleased { baseModel with Interaction = interaction }
            |> modelOf

        Assert.Equal(GraphLogic.Interaction.Idle, after.Interaction)

[<Fact>]
let ``clicking empty canvas clears the selection and any focus`` () =
    let selected = { baseModel with Selected = Some a.Key; FocusMode = true }
    let pressed = GraphView.update noServices (GraphView.CanvasPressed(10.0, 10.0)) selected |> modelOf
    let released = GraphView.update noServices GraphView.PointerReleased pressed |> modelOf

    Assert.Equal(None, released.Selected)
    Assert.False(released.FocusMode)

[<Fact>]
let ``Reset Layout clears session moves and leaves the viewport alone`` () =
    let moved =
        { baseModel with
            Overrides = Map.ofList [ a.Key, (500.0, 500.0) ]
            View = { GraphLogic.Viewport.identity with Zoom = 2.0; PanX = 30.0 } }

    let after = GraphView.update noServices (GraphView.RunControl GraphLogic.Control.ResetLayout) moved |> modelOf

    Assert.True(after.Overrides.IsEmpty)
    Assert.Equal(2.0, after.View.Zoom, 6)
    Assert.Equal(30.0, after.View.PanX, 6)

[<Fact>]
let ``Reset View restores pan and zoom and leaves session moves alone`` () =
    let moved =
        { baseModel with
            Overrides = Map.ofList [ a.Key, (500.0, 500.0) ]
            View = { GraphLogic.Viewport.identity with Zoom = 2.0; PanX = 30.0 } }

    let after = GraphView.update noServices (GraphView.RunControl GraphLogic.Control.ResetView) moved |> modelOf

    Assert.Equal(GraphLogic.Viewport.identity, after.View)
    Assert.Equal(1, after.Overrides.Count)

[<Fact>]
let ``a refresh keeps a dragged position for a node that still exists`` () =
    let moved = { baseModel with Overrides = Map.ofList [ a.Key, (500.0, 500.0) ] }

    let after =
        GraphView.update noServices (GraphView.Loaded([ a; b ], [], baseModel.BaseLayout)) moved |> modelOf

    Assert.Equal(Some(500.0, 500.0), after.Overrides.TryFind a.Key)

[<Fact>]
let ``a refresh drops the position and the selection of a deleted node`` () =
    let moved = { baseModel with Overrides = Map.ofList [ a.Key, (500.0, 500.0) ]; Selected = Some a.Key }

    let after = GraphView.update noServices (GraphView.Loaded([ b ], [], Map.empty)) moved |> modelOf

    Assert.False(after.Overrides.ContainsKey a.Key)
    Assert.Equal(None, after.Selected)

[<Fact>]
let ``filtering never discards a dragged position`` () =
    let moved = { baseModel with Overrides = Map.ofList [ a.Key, (500.0, 500.0) ] }

    let after =
        GraphView.update noServices (GraphView.ToggleFilter EntityKind.Module) moved |> modelOf

    Assert.Equal(Some(500.0, 500.0), after.Overrides.TryFind a.Key)

[<Fact>]
let ``zoom controls stay inside the range`` () =
    let mutable m = baseModel

    for _ in 1..40 do
        m <- GraphView.update noServices (GraphView.RunControl GraphLogic.Control.ZoomIn) m |> modelOf

    Assert.Equal(GraphLogic.Geometry.maxZoom, m.View.Zoom, 6)

    for _ in 1..80 do
        m <- GraphView.update noServices (GraphView.RunControl GraphLogic.Control.ZoomOut) m |> modelOf

    Assert.Equal(GraphLogic.Geometry.minZoom, m.View.Zoom, 6)

[<Fact>]
let ``the wheel zooms about the pointer`` () =
    let after = GraphView.update noServices (GraphView.Wheel(400.0, 300.0, 1.0)) baseModel |> modelOf
    Assert.True(after.View.Zoom > 1.0)

    let stillThere = GraphLogic.screenToWorld after.View (400.0, 300.0)
    let before = GraphLogic.screenToWorld baseModel.View (400.0, 300.0)
    approx (fst before) (fst stillThere)

[<Fact>]
let ``focus needs a selection, and clearing the selection ends it`` () =
    let noSelection = GraphView.update noServices GraphView.ToggleFocus baseModel |> modelOf
    Assert.False(noSelection.FocusMode, "focus turned on with nothing selected")

    let selected = { baseModel with Selected = Some a.Key }
    let focused = GraphView.update noServices GraphView.ToggleFocus selected |> modelOf
    Assert.True(focused.FocusMode)

    let cleared = GraphView.update noServices GraphView.ClearSelection focused |> modelOf
    Assert.False(cleared.FocusMode)

[<Fact>]
let ``searching selects and centres the first match`` () =
    let after =
        GraphView.update noServices GraphView.SearchFocus { baseModel with Query = "A note" } |> modelOf

    Assert.Equal(Some a.Key, after.Selected)

    let sx, sy = GraphLogic.worldToScreen after.View (100.0, 100.0)
    approx 400.0 sx
    approx 300.0 sy

[<Fact>]
let ``opening a node asks the shell for that entity's own screen`` () =
    let selected = { baseModel with Selected = Some b.Key }
    let result = GraphView.update noServices GraphView.OpenSelected selected
    Assert.Equal(Some(GraphView.Goto Page.Modules), externalOf result)

[<Fact>]
let ``a failure surfaces the message and stops loading`` () =
    let after = GraphView.update noServices (GraphView.Failed(exn "offline")) baseModel |> modelOf
    Assert.False(after.Loading)
    Assert.Equal(Some "offline", after.Error)

// ---------------------------------------------------------------------------
// 9. performance rules
// ---------------------------------------------------------------------------

[<Fact>]
let ``no interaction message issues a command, so nothing touches a repository`` () =
    // Layout and every repository read live in the load command. If any of these
    // returned a command, something would be running per pointer event.
    let interactions =
        [ GraphView.NodePressed(a.Key, 1.0, 1.0)
          GraphView.CanvasPressed(1.0, 1.0)
          GraphView.PointerMoved(50.0, 50.0)
          GraphView.PointerReleased
          GraphView.Wheel(1.0, 1.0, 1.0)
          GraphView.RunControl GraphLogic.Control.ZoomIn
          GraphView.RunControl GraphLogic.Control.ZoomOut
          GraphView.RunControl GraphLogic.Control.FitToView
          GraphView.RunControl GraphLogic.Control.ResetView
          GraphView.RunControl GraphLogic.Control.ResetLayout
          GraphView.RunControl GraphLogic.Control.CentreSelected
          GraphView.SelectNode a.Key
          GraphView.ClearSelection
          GraphView.ToggleFocus
          GraphView.ToggleFilter EntityKind.Note
          GraphView.ClearFilter
          GraphView.SetQuery "x"
          GraphView.SearchFocus
          GraphView.ToggleLegend
          GraphView.ToggleInsights
          GraphView.ToggleMinimap
          GraphView.CanvasResized(900.0, 700.0)
          GraphView.Resized 1000.0 ]

    for msg in interactions do
        Assert.True(List.isEmpty (cmdOf (GraphView.update noServices msg baseModel)), sprintf "%A issued a command" msg)

[<Fact>]
let ``dragging leaves the base layout untouched, so no layout can have run`` () =
    let pressed = GraphView.update noServices (GraphView.NodePressed(a.Key, 100.0, 100.0)) baseModel |> modelOf
    let moving = GraphView.update noServices (GraphView.PointerMoved(200.0, 200.0)) pressed |> modelOf
    let dragged = GraphView.update noServices (GraphView.PointerMoved(260.0, 260.0)) moving |> modelOf

    Assert.Equal<(string * (float * float)) list>(
        Map.toList baseModel.BaseLayout,
        Map.toList dragged.BaseLayout
    )

[<Fact>]
let ``a large graph stays bounded through the pure pipeline`` () =
    // 300 nodes, sparse edges — the fixture the map calls for.
    let nodes = [ for i in 1..300 -> keyed EntityKind.Note (sprintf "Note %d" i) (Guid.NewGuid()) ]
    let keys = nodes |> List.map (fun n -> n.Key)
    let known = Set.ofList keys

    let edges =
        GraphLogic.normaliseEdges known [ for i in 0 .. keys.Length - 2 -> keys.[i], keys.[i + 1] ]

    let adj = GraphLogic.adjacency edges

    Assert.Equal(299, edges.Length)
    Assert.Equal(300, (GraphLogic.shownNodes Set.empty None adj nodes).Length)
    Assert.Equal(2, GraphLogic.degree adj keys.[1])

    // One connected chain.
    Assert.Equal(1, GraphLogic.componentCount keys edges)

[<Fact>]
let ``many isolated nodes are counted as separate components`` () =
    let keys = [ for i in 1..20 -> sprintf "k%d" i ]
    Assert.Equal(20, GraphLogic.componentCount keys [])

// ---------------------------------------------------------------------------
// 10. rule-based insights
// ---------------------------------------------------------------------------

[<Fact>]
let ``insights never use AI wording`` () =
    let nodes = [ a; b ]
    let adj = GraphLogic.adjacency [ a.Key, b.Key ]
    let lines = GraphLogic.insights nodes [ a.Key, b.Key ] adj 0 |> List.map snd

    for line in lines do
        let lower = line.ToLowerInvariant()
        Assert.DoesNotContain("ai ", lower)
        Assert.DoesNotContain("predict", lower)
        Assert.DoesNotContain("cluster", lower)
        Assert.DoesNotContain("confidence", lower)

[<Fact>]
let ``an empty graph says how to start rather than reporting zero`` () =
    let lines = GraphLogic.insights [] [] Map.empty 0 |> List.map snd
    Assert.Contains(lines, fun l -> l.Contains "Nothing is in the graph yet")

[<Fact>]
let ``a high degree is described as a link count, never as importance`` () =
    let hub = keyed EntityKind.Module "Hub" (Guid.NewGuid())
    let spokes = [ for i in 1..8 -> keyed EntityKind.Note (sprintf "N%d" i) (Guid.NewGuid()) ]
    let edges = spokes |> List.map (fun s -> GraphLogic.normaliseEdge (hub.Key, s.Key))
    let adj = GraphLogic.adjacency edges

    let lines = GraphLogic.insights (hub :: spokes) edges adj 0 |> List.map snd
    Assert.Contains(lines, fun l -> l.Contains "not a measure of importance")

[<Fact>]
let ``hidden entities are reported when filters hide some`` () =
    let lines = GraphLogic.insights [ a ] [] Map.empty 4 |> List.map snd
    Assert.Contains(lines, fun l -> l.Contains "hidden by the current filters")

[<Fact>]
let ``the legend lists only kinds actually present`` () =
    let items = GraphLogic.legendItems [ a; b ] |> List.map fst
    Assert.Contains(EntityKind.Note, items)
    Assert.Contains(EntityKind.Module, items)
    Assert.DoesNotContain(EntityKind.Lecture, items)

[<Fact>]
let ``every relationship kind named in the legend is one the loader really makes`` () =
    Assert.NotEmpty(GraphLogic.relationshipKinds)

    for kind in GraphLogic.relationshipKinds do
        Assert.False(String.IsNullOrWhiteSpace kind)
        Assert.DoesNotContain("predict", kind.ToLowerInvariant())

// ---------------------------------------------------------------------------
// 11. responsive placement
// ---------------------------------------------------------------------------

[<Fact>]
let ``at wide width every overlay is on the canvas`` () =
    let p = GraphLogic.placementFor 1600.0
    Assert.True(p.FloatingPanel)
    Assert.True(p.ShowLegend)
    Assert.True(p.ShowMinimap)

[<Fact>]
let ``at medium width the legend gives way but the panel still floats`` () =
    let p = GraphLogic.placementFor 1100.0
    Assert.True(p.FloatingPanel)
    Assert.False(p.ShowLegend)
    Assert.True(p.ShowMinimap)
    Assert.Equal(GraphLogic.Geometry.nodePanelNarrow, p.PanelWidth, 6)

[<Fact>]
let ``at narrow width the node panel stops floating and moves below`` () =
    let p = GraphLogic.placementFor 700.0
    Assert.False(p.FloatingPanel)
    Assert.False(p.ShowLegend)
    Assert.False(p.ShowMinimap)

[<Fact>]
let ``the canvas keeps a usable minimum height`` () =
    Assert.True(GraphLogic.Geometry.minCanvasHeight >= 400.0)

// ---------------------------------------------------------------------------
// 12. themes
// ---------------------------------------------------------------------------

let private themeModes = [ ThemeMode.Dark; ThemeMode.Light ]
let private accentKeys = Palette.accents |> List.map fst

let private washedBy (alphaHex: string) (colour: Color) (surface: Color) =
    let v = Int32.Parse(alphaHex, Globalization.NumberStyles.HexNumber)
    Palette.composite surface (Color.FromArgb(byte v, colour.R, colour.G, colour.B))

[<Fact>]
let ``a node's glyph is readable on its own wash, every kind, accent and theme`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for kind in GraphLogic.graphKinds do
                let raw = Color.Parse(Theme.entityKindHex kind)
                let wash = washedBy GraphLogic.Geometry.chipFillAlpha raw s.Card
                let ink = Palette.readableOn wash s.Text raw

                Assert.True(
                    Palette.contrastRatio ink wash >= Palette.aaText,
                    sprintf "%A/%s %A node: %.2f" mode key kind (Palette.contrastRatio ink wash)
                )

[<Fact>]
let ``a selected node is filled more strongly, and stays readable`` () =
    Assert.NotEqual<string>(GraphLogic.Geometry.chipFillAlpha, GraphLogic.Geometry.selectedNodeAlpha)

    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for kind in GraphLogic.graphKinds do
                let raw = Color.Parse(Theme.entityKindHex kind)
                let wash = washedBy GraphLogic.Geometry.selectedNodeAlpha raw s.Card
                let ink = Palette.readableOn wash s.Text raw

                Assert.True(
                    Palette.contrastRatio ink wash >= Palette.aaText,
                    sprintf "%A/%s %A selected: %.2f" mode key kind (Palette.contrastRatio ink wash)
                )

[<Fact>]
let ``an edge stays visible against the canvas in both themes, without glowing`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            // The edge as it is actually painted: the text colour at edgeAlpha,
            // composited over the canvas. Measuring the raw role instead is what
            // made this test wrong the first time — `Divider` is white-with-alpha,
            // so its RGB is FFFFFF and it reads as near-white until composited.
            let edge = washedBy GraphLogic.Geometry.edgeAlpha s.Text s.Window

            Assert.True(
                Palette.contrastRatio edge s.Window >= 1.15,
                sprintf "%A/%s edge only reached %.2f against the canvas" mode key (Palette.contrastRatio edge s.Window)
            )

            // And never a near-white line, which is the sheen this design avoids.
            Assert.False(
                edge.R > 250uy && edge.G > 250uy && edge.B > 250uy,
                sprintf "%A/%s edge is near-white" mode key
            )

[<Fact>]
let ``no Graph surface is a near-white sheet in Light theme`` () =
    for key in accentKeys do
        let s = Surfaces.resolve ThemeMode.Light (Palette.ofKey key)

        for name, colour in [ "Window", s.Window; "Card", s.Card; "CardAlt", s.CardAlt ] do
            let nearWhite = colour.R > 250uy && colour.G > 250uy && colour.B > 250uy
            Assert.False(nearWhite, sprintf "Light/%s %s is near-white" key name)

// ---------------------------------------------------------------------------
// 13. the shell contract
// ---------------------------------------------------------------------------

[<Fact>]
let ``Graph draws its own glass`` () =
    Assert.True(Page.usesGlassSurface Page.Graph)

[<Fact>]
let ``the screens migrated so far all draw their own glass`` () =
    for page in
        [ Page.Dashboard
          Page.Timeline
          Page.Planner
          Page.Inbox
          Page.Assignments
          Page.Modules
          Page.Lectures
          Page.Notes
          Page.Documents
          Page.Graph ] do
        Assert.True(Page.usesGlassSurface page)

[<Fact>]
    // Every screen is now migrated, so there is nothing left holding an opaque
    // shell surface. This asserts the completed state rather than an empty list.
let ``the screens still to migrate keep their opaque surface`` () =
    for page, _, _ in Page.all do
        Assert.True(Page.usesGlassSurface page, sprintf "%A lost its glass surface" page)
