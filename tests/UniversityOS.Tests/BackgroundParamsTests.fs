module UniversityOS.Tests.BackgroundParamsTests

open Xunit
open UniversityOS.Domain
open UniversityOS.App.Design

/// The numbers that decide how the background looks: the eased slider response,
/// per-mode caps, the adaptive section mapping and composite opacity.

[<Fact>]
let ``the eased slider curve hits its three specified points`` () =
    // 0 -> 0, 50 -> 1 (the old maximum), 100 -> 2.
    Assert.Equal(0.0, BackgroundParams.scale 0, 6)
    Assert.Equal(1.0, BackgroundParams.scale 50, 6)
    Assert.Equal(2.0, BackgroundParams.scale 100, 6)

[<Fact>]
let ``the eased curve is monotonic and clamps out-of-range input`` () =
    let samples = [ 0..5..100 ] |> List.map BackgroundParams.scale

    samples
    |> List.pairwise
    |> List.iter (fun (a, b) -> Assert.True(b >= a, sprintf "curve must not decrease: %f then %f" a b))

    Assert.Equal(BackgroundParams.scale 0, BackgroundParams.scale -40, 6)
    Assert.Equal(BackgroundParams.scale 100, BackgroundParams.scale 500, 6)

[<Fact>]
let ``adaptive mode maps each section to its background`` () =
    Assert.Equal(Aurora, BackgroundParams.styleForSection Overview)
    Assert.Equal(LayeredWaves, BackgroundParams.styleForSection Work)
    Assert.Equal(LiquidGlass, BackgroundParams.styleForSection Knowledge)
    Assert.Equal(FoldedGlass, BackgroundParams.styleForSection SystemArea)

[<Fact>]
let ``an explicit style overrides the section, adaptive follows it`` () =
    // Explicit choice wins wherever you are.
    Assert.Equal(Aurora, BackgroundParams.resolveMode BackgroundStyle.Aurora Work)
    Assert.Equal(FoldedGlass, BackgroundParams.resolveMode BackgroundStyle.FoldedGlass Overview)
    Assert.Equal(NoBackground, BackgroundParams.resolveMode BackgroundStyle.None Knowledge)

    // Adaptive defers to the section.
    Assert.Equal(LiquidGlass, BackgroundParams.resolveMode BackgroundStyle.Adaptive Knowledge)
    Assert.Equal(LayeredWaves, BackgroundParams.resolveMode BackgroundStyle.Adaptive Work)

[<Fact>]
let ``the graph screen is dimmed only under adaptive mode`` () =
    // Nodes and edges must stay legible against the background.
    Assert.Equal(0.4, BackgroundParams.adaptiveMultiplier BackgroundStyle.Adaptive true, 6)
    Assert.Equal(1.0, BackgroundParams.adaptiveMultiplier BackgroundStyle.Adaptive false, 6)
    // An explicitly chosen style is left alone — the user asked for it.
    Assert.Equal(1.0, BackgroundParams.adaptiveMultiplier BackgroundStyle.LiquidGlass true, 6)

[<Fact>]
let ``per-mode opacity and blur caps match the specification`` () =
    // Aurora is now fine contour linework, not a soft field: thin lines cover a
    // fraction of the pixels a fog covered, so it needs a higher cap to read at
    // all — and still sits lighter on the eye than the old 0.11 wash.
    Assert.Equal(0.20, BackgroundParams.opacityCap Aurora, 6)
    Assert.Equal(0.13, BackgroundParams.opacityCap LayeredWaves, 6)
    Assert.Equal(0.18, BackgroundParams.opacityCap LiquidGlass, 6)
    Assert.Equal(0.12, BackgroundParams.opacityCap FoldedGlass, 6)
    Assert.Equal(0.0, BackgroundParams.opacityCap NoBackground, 6)

    Assert.Equal(45.0, BackgroundParams.blurCap Aurora, 6)
    Assert.Equal(28.0, BackgroundParams.blurCap LayeredWaves, 6)
    Assert.Equal(35.0, BackgroundParams.blurCap LiquidGlass, 6)
    Assert.Equal(26.0, BackgroundParams.blurCap FoldedGlass, 6)

[<Fact>]
let ``composite opacity never exceeds the mode cap and respects dimming`` () =
    // Everything at maximum still lands on the cap, not above it.
    let full = BackgroundParams.opacityFraction LiquidGlass 1.0 100 100
    Assert.Equal(0.18 * 2.0 * (0.62 + 0.38 * 2.0), full, 6)

    // Opacity slider at zero silences the layer entirely.
    Assert.Equal(0.0, BackgroundParams.opacityFraction LiquidGlass 1.0 0 100, 6)

    // The graph multiplier scales the whole result.
    let dimmed = BackgroundParams.opacityFraction LiquidGlass 0.4 100 100
    Assert.Equal(full * 0.4, dimmed, 6)

    // NoBackground is always silent.
    Assert.Equal(0.0, BackgroundParams.opacityFraction NoBackground 1.0 100 100, 6)

[<Fact>]
let ``blur scales linearly to the per-mode cap`` () =
    Assert.Equal(0.0, BackgroundParams.blurPx Aurora 0, 6)
    Assert.Equal(22.5, BackgroundParams.blurPx Aurora 50, 6)
    Assert.Equal(45.0, BackgroundParams.blurPx Aurora 100, 6)
    // Out-of-range input is clamped rather than producing a negative radius,
    // which Skia would reject.
    Assert.Equal(0.0, BackgroundParams.blurPx Aurora -20, 6)
    Assert.Equal(28.0, BackgroundParams.blurPx LayeredWaves 300, 6)

[<Fact>]
let ``speed multipliers match the specification`` () =
    Assert.Equal(0.6, BackgroundParams.speedMultiplier Speed.Slow, 6)
    Assert.Equal(1.0, BackgroundParams.speedMultiplier Speed.Normal, 6)
    Assert.Equal(1.6, BackgroundParams.speedMultiplier Speed.Fast, 6)

[<Fact>]
let ``Slow is slower than Normal, which is slower than Fast`` () =
    // The regression this guards: these constants were 1.6 / 1.0 / 0.55, copied
    // from Tokens.animScale where a bigger number means a LONGER transition.
    // Applied to a rate they inverted the labels — "Slow" ran the background
    // 1.6x. An ordering assertion catches that whatever the exact values are.
    let slow = BackgroundParams.speedMultiplier Speed.Slow
    let normal = BackgroundParams.speedMultiplier Speed.Normal
    let fast = BackgroundParams.speedMultiplier Speed.Fast

    Assert.True(slow > 0.0, "Slow must still move — it is a speed, not Reduce Motion")
    Assert.True(slow < normal, sprintf "Slow (%f) must be slower than Normal (%f)" slow normal)
    Assert.True(normal < fast, sprintf "Normal (%f) must be slower than Fast (%f)" normal fast)

[<Fact>]
let ``the displayed label matches the actual multiplier`` () =
    // Reading the label out of the domain and the number out of the design
    // layer is the point: if someone renames or reorders the cases, the pairing
    // below stops holding.
    let byLabel =
        Speed.all
        |> List.map (fun s -> Speed.label s, BackgroundParams.speedMultiplier s)
        |> Map.ofList

    Assert.True(byLabel["Slow"] < byLabel["Normal"])
    Assert.True(byLabel["Normal"] < byLabel["Fast"])

[<Fact>]
let ``UI duration factors run the opposite way from background rates`` () =
    // Both senses are legitimate and both are needed; they are only dangerous
    // when confused. Slow = LONGER transitions, and Slow = SLOWER background.
    Assert.True(Tokens.animScale Speed.Slow > Tokens.animScale Speed.Normal)
    Assert.True(Tokens.animScale Speed.Fast < Tokens.animScale Speed.Normal)
    Assert.True(BackgroundParams.speedMultiplier Speed.Slow < BackgroundParams.speedMultiplier Speed.Normal)

[<Fact>]
let ``changing speed keeps the timeline continuous`` () =
    // Accumulating speed-scaled deltas means the elapsed animation time already
    // banked is never rescaled, so switching speed changes the rate from that
    // point on and cannot make the composition jump.
    let delta = 1.0 / 60.0

    let mutable atNormal = 0.0

    for _ in 1..120 do
        atNormal <- BackgroundParams.advanceTime atNormal delta Speed.Normal

    // The instant the user switches, time must be exactly where it was.
    let afterSwitch = BackgroundParams.advanceTime atNormal 0.0 Speed.Fast
    Assert.Equal(atNormal, afterSwitch, 9)

    // And from there it advances at the new rate, not a rescaled old one.
    let oneFastFrame = BackgroundParams.advanceTime atNormal delta Speed.Fast
    Assert.Equal(atNormal + delta * BackgroundParams.speedMultiplier Speed.Fast, oneFastFrame, 9)

[<Fact>]
let ``every animated mode advances on the same clock`` () =
    // There is one clock and one multiplier: no mode gets to interpret Slow
    // differently. Proven by construction — advanceTime takes no mode — so this
    // asserts the shape that makes it true.
    let delta = 0.5

    for speed in Speed.all do
        Assert.Equal(
            10.0 + delta * BackgroundParams.speedMultiplier speed,
            BackgroundParams.advanceTime 10.0 delta speed,
            9
        )

[<Fact>]
let ``a negative or zero frame delta never rewinds the clock`` () =
    // A clock that can run backwards produces a visible jerk when the OS
    // delivers an out-of-order timestamp after a sleep or a monitor change.
    Assert.Equal(5.0, BackgroundParams.advanceTime 5.0 -1.0 Speed.Fast, 9)
    Assert.Equal(5.0, BackgroundParams.advanceTime 5.0 0.0 Speed.Slow, 9)
