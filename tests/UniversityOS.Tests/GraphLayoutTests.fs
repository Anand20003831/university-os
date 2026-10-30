module UniversityOS.Tests.GraphLayoutTests

open System
open Xunit
open UniversityOS.App

/// The knowledge graph's pure force-directed layout: must be deterministic
/// (same graph → same picture every launch) and numerically safe.

[<Fact>]
let ``layout is deterministic and independent of input order`` () =
    let nodes = [ "a"; "b"; "c"; "d"; "e" ]
    let edges = [ "a", "b"; "b", "c"; "c", "d" ]

    let first = GraphLayout.layout 1200.0 800.0 150 nodes edges
    let second = GraphLayout.layout 1200.0 800.0 150 (List.rev nodes) (List.rev edges)

    Assert.True((first = second))

[<Fact>]
let ``layout never produces NaN and keeps every node on the canvas`` () =
    // A dense hub: every node pulls on node-0, stressing the force math.
    let nodes = [ for i in 0..40 -> sprintf "node-%d" i ]
    let edges = [ for i in 1..40 -> "node-0", sprintf "node-%d" i ]

    let positions = GraphLayout.layout 1200.0 800.0 150 nodes edges
    Assert.Equal(41, positions.Count)

    for KeyValue(_, (x, y)) in positions do
        Assert.False(Double.IsNaN x || Double.IsNaN y, "position must be a number")
        Assert.InRange(x, 0.0, 1200.0)
        Assert.InRange(y, 0.0, 800.0)

[<Fact>]
let ``duplicate nodes, self-loops and unknown edge endpoints are tolerated`` () =
    let positions =
        GraphLayout.layout 1200.0 800.0 50 [ "a"; "a"; "b" ] [ "a", "ghost"; "a", "a"; "a", "b"; "b", "a" ]

    Assert.Equal(2, positions.Count)

[<Fact>]
let ``a single node sits at the canvas centre and an empty graph is empty`` () =
    Assert.True((GraphLayout.layout 1200.0 800.0 150 [ "only" ] [] = Map.ofList [ "only", (600.0, 400.0) ]))
    Assert.True(Map.isEmpty (GraphLayout.layout 1200.0 800.0 150 [] []))

[<Fact>]
let ``connected nodes end up closer than the unconnected far pair`` () =
    // a–b are linked; c floats free. The spring must pull a and b together.
    let positions =
        GraphLayout.layout 1200.0 800.0 150 [ "a"; "b"; "c" ] [ "a", "b" ]

    let distance k1 k2 =
        let x1, y1 = positions.[k1]
        let x2, y2 = positions.[k2]
        sqrt ((x1 - x2) ** 2.0 + (y1 - y2) ** 2.0)

    Assert.True(
        distance "a" "b" < max (distance "a" "c") (distance "b" "c"),
        "linked pair should be closer than the farthest unlinked pair"
    )
