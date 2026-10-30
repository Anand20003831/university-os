namespace UniversityOS.App

/// Deterministic force-directed layout for the knowledge graph (a light
/// Fruchterman–Reingold). Pure: the same nodes + edges always produce the same
/// positions. Initial placement is seeded from a stable FNV-1a hash of each
/// node key — never Random or String.GetHashCode, which .NET randomizes per
/// process and would make layouts jump between launches.
[<RequireQualifiedAccess>]
module GraphLayout =

    let private fnv1a (seed: uint32) (s: string) =
        let mutable h = 2166136261u ^^^ seed

        for ch in s do
            h <- (h ^^^ uint32 ch) * 16777619u

        h

    /// Hash → [0, 1).
    let private toUnit (h: uint32) = float h / 4294967296.0

    /// Lays out `nodes` on a width×height logical canvas. Edges referencing
    /// unknown keys are ignored; input order never affects the result (nodes
    /// are sorted internally).
    let layout
        (width: float)
        (height: float)
        (iterations: int)
        (nodes: string list)
        (edges: (string * string) list)
        : Map<string, float * float> =

        let keys = nodes |> List.distinct |> List.sort |> Array.ofList
        let n = keys.Length

        if n = 0 then
            Map.empty
        elif n = 1 then
            Map.ofList [ keys.[0], (width / 2.0, height / 2.0) ]
        else
            let margin = 60.0
            let indexOf = keys |> Array.mapi (fun i k -> k, i) |> Map.ofArray

            let edgePairs =
                edges
                |> List.choose (fun (a, b) ->
                    match indexOf.TryFind a, indexOf.TryFind b with
                    | Some i, Some j when i <> j -> Some(min i j, max i j)
                    | _ -> None)
                |> List.distinct
                |> Array.ofList

            let xs =
                keys |> Array.map (fun k -> margin + toUnit (fnv1a 17u k) * (width - 2.0 * margin))

            let ys =
                keys |> Array.map (fun k -> margin + toUnit (fnv1a 89u k) * (height - 2.0 * margin))

            // Ideal pairwise distance for this many nodes on this much canvas.
            let k = sqrt (width * height / float n)
            let dispX = Array.zeroCreate<float> n
            let dispY = Array.zeroCreate<float> n

            for iter in 0 .. iterations - 1 do
                Array.fill dispX 0 n 0.0
                Array.fill dispY 0 n 0.0

                // Repulsion between every pair. Coincident nodes get a small
                // deterministic separation so no force is ever NaN.
                for i in 0 .. n - 2 do
                    for j in i + 1 .. n - 1 do
                        let dx = xs.[i] - xs.[j]
                        let dy = ys.[i] - ys.[j]
                        let dist = sqrt (dx * dx + dy * dy)
                        let dx, dy, dist = if dist < 0.01 then 0.01, 0.01 * float (i + 1), 0.02 else dx, dy, dist
                        let force = k * k / dist / dist

                        dispX.[i] <- dispX.[i] + dx * force
                        dispY.[i] <- dispY.[i] + dy * force
                        dispX.[j] <- dispX.[j] - dx * force
                        dispY.[j] <- dispY.[j] - dy * force

                // Spring attraction along edges.
                for (i, j) in edgePairs do
                    let dx = xs.[i] - xs.[j]
                    let dy = ys.[i] - ys.[j]
                    let dist = max 0.01 (sqrt (dx * dx + dy * dy))
                    let force = dist / k

                    dispX.[i] <- dispX.[i] - dx * force
                    dispY.[i] <- dispY.[i] - dy * force
                    dispX.[j] <- dispX.[j] + dx * force
                    dispY.[j] <- dispY.[j] + dy * force

                // Cooling: big early moves settle into fine adjustments.
                let temperature =
                    (width / 8.0) * (1.0 - float iter / float (max 1 iterations)) + 1.0

                for i in 0 .. n - 1 do
                    let len = max 0.01 (sqrt (dispX.[i] * dispX.[i] + dispY.[i] * dispY.[i]))
                    let step = min len temperature

                    xs.[i] <- xs.[i] + dispX.[i] / len * step
                    ys.[i] <- ys.[i] + dispY.[i] / len * step
                    xs.[i] <- max (margin / 2.0) (min (width - margin / 2.0) xs.[i])
                    ys.[i] <- max (margin / 2.0) (min (height - margin / 2.0) ys.[i])

            keys |> Array.mapi (fun i key -> key, (xs.[i], ys.[i])) |> Map.ofArray
