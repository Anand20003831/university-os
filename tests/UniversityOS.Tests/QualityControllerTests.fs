module UniversityOS.Tests.QualityControllerTests

open Xunit
open UniversityOS.App.Rendering

/// Adaptive quality. The important property is not that it adapts — it is that
/// it CANNOT oscillate, because flipping between levels looks far worse than
/// simply staying at the lower one.

let private feed (times: float seq) (state: QualityState) =
    times |> Seq.fold (fun s ms -> QualityController.step ms s) state

let private overBudget = QualityController.budgetMs + 4.0
let private headroom = QualityController.budgetMs * 0.3

[<Fact>]
let ``sustained over-budget frames drop exactly one level`` () =
    let start = QualityController.initial
    let after = feed (Seq.replicate QualityController.downgradeAfterFrames overBudget) start

    Assert.Equal(QualityLevel.Low, QualityLevel.lower QualityLevel.Medium)
    Assert.Equal(QualityLevel.lower start.Level, after.Level)

[<Fact>]
let ``a brief spike below the threshold does not change quality`` () =
    let start = QualityController.initial

    let after =
        feed (Seq.replicate (QualityController.downgradeAfterFrames - 1) overBudget) start

    Assert.Equal(start.Level, after.Level)

[<Fact>]
let ``recovering to the comfortable band resets the downgrade counter`` () =
    let start = QualityController.initial

    // Almost enough to downgrade, then one comfortable frame, then almost again.
    let after =
        start
        |> feed (Seq.replicate (QualityController.downgradeAfterFrames - 1) overBudget)
        |> feed [ QualityController.budgetMs * 0.8 ]
        |> feed (Seq.replicate (QualityController.downgradeAfterFrames - 1) overBudget)

    Assert.Equal(start.Level, after.Level)

[<Fact>]
let ``upgrading requires sustained headroom and waits out the cooldown`` () =
    // Force a downgrade first so there is room to climb back.
    let downgraded =
        feed (Seq.replicate QualityController.downgradeAfterFrames overBudget) QualityController.initial

    Assert.Equal(QualityLevel.Low, downgraded.Level)

    // Headroom for slightly less than the upgrade window: still no change.
    let waiting =
        feed (Seq.replicate (QualityController.upgradeAfterFrames - 1) headroom) downgraded

    Assert.Equal(QualityLevel.Low, waiting.Level)

    // One more frame of headroom crosses the threshold.
    let upgraded = feed [ headroom ] waiting
    Assert.Equal(QualityLevel.Medium, upgraded.Level)

[<Fact>]
let ``the automatic controller never selects Static`` () =
    // Static means "no animation" and is a motion decision, never a
    // performance one. Hammering the controller must floor at Low.
    let hammered = feed (Seq.replicate 5000 (overBudget * 10.0)) QualityController.initial
    Assert.Equal(QualityLevel.Low, hammered.Level)

[<Fact>]
let ``alternating good and bad frames cannot make quality oscillate`` () =
    // The adversarial case: a workload that sits exactly on the boundary.
    let alternating =
        Seq.init 4000 (fun i -> if i % 2 = 0 then overBudget else headroom)

    let mutable changes = 0
    let mutable state = QualityController.initial

    for ms in alternating do
        let next = QualityController.step ms state
        if next.Level <> state.Level then changes <- changes + 1
        state <- next

    // Alternating frames never accumulate the consecutive runs either
    // threshold needs, so the level must never move at all.
    Assert.Equal(0, changes)
    Assert.Equal(QualityController.initial.Level, state.Level)

[<Fact>]
let ``the background budget tracks the display's real frame interval`` () =
    // A fixed 4ms budget assumed a 60Hz frame. On a 144Hz display the whole
    // frame is 6.94ms, so 4ms would be 58% of it — a background could sit
    // "within budget" while visibly dropping frames.
    Assert.Equal(4.0, QualityController.budgetFor 16.67, 2) // 60Hz -> capped at the documented 4ms
    Assert.Equal(2.08, QualityController.budgetFor 6.94, 2) // 144Hz -> 30% of the frame
    Assert.Equal(1.20, QualityController.budgetFor 4.0, 2) // 240Hz

    // Never zero or negative, whatever the input.
    Assert.True(QualityController.budgetFor 0.0 > 0.0)
    Assert.True(QualityController.budgetFor -5.0 > 0.0)
    Assert.True(QualityController.budgetFor 0.1 >= 0.8)

[<Fact>]
let ``a load that fits a 60Hz budget still downgrades on a 144Hz display`` () =
    // The exact case measured in the Stage 1 sweep: Tier B's wave painter at
    // 3.15ms passed the fixed budget yet dropped the app to 104fps.
    let load = 3.15
    let sixtyHz = QualityController.budgetFor 16.67
    let oneFortyFour = QualityController.budgetFor 6.94

    Assert.True(load < sixtyHz, "this load fits a 60Hz frame")
    Assert.True(load > oneFortyFour, "the same load must not fit a 144Hz frame")

    let after =
        Seq.replicate QualityController.downgradeAfterFrames load
        |> Seq.fold (fun s ms -> QualityController.stepWith oneFortyFour ms s) QualityController.initial

    Assert.Equal(QualityLevel.lower QualityController.initial.Level, after.Level)

[<Fact>]
let ``quality levels expose the documented backdrop resolutions`` () =
    Assert.Equal(1, QualityLevel.backdropDivisor QualityLevel.Ultra)
    Assert.Equal(2, QualityLevel.backdropDivisor QualityLevel.High)
    Assert.Equal(4, QualityLevel.backdropDivisor QualityLevel.Medium)
    Assert.Equal(8, QualityLevel.backdropDivisor QualityLevel.Low)
    Assert.Equal(8, QualityLevel.backdropDivisor QualityLevel.Static)

[<Fact>]
let ``level stepping saturates at both ends`` () =
    Assert.Equal(QualityLevel.Ultra, QualityLevel.higher QualityLevel.Ultra)
    Assert.Equal(QualityLevel.Low, QualityLevel.lower QualityLevel.Low)
    Assert.Equal(QualityLevel.Static, QualityLevel.lower QualityLevel.Static)
    Assert.Equal(QualityLevel.Static, QualityLevel.higher QualityLevel.Static)
