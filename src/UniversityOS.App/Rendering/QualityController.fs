namespace UniversityOS.App.Rendering

/// Adaptive background quality. Lower levels render the background and its
/// blurred backdrop at reduced resolution and detail.
[<RequireQualifiedAccess>]
type QualityLevel =
    | Ultra
    | High
    | Medium
    | Low
    /// Rendered once and reused — no animation. Reserved for reduce-motion and
    /// pause; the automatic controller never selects it.
    | Static

[<RequireQualifiedAccess>]
module QualityLevel =

    let label =
        function
        | QualityLevel.Ultra -> "Ultra"
        | QualityLevel.High -> "High"
        | QualityLevel.Medium -> "Medium"
        | QualityLevel.Low -> "Low"
        | QualityLevel.Static -> "Static"

    /// Linear downscale factor for the offscreen backdrop. 4 means quarter
    /// resolution, i.e. one sixteenth of the pixels.
    let backdropDivisor =
        function
        | QualityLevel.Ultra -> 1
        | QualityLevel.High -> 2
        | QualityLevel.Medium -> 4
        | QualityLevel.Low -> 8
        | QualityLevel.Static -> 8

    /// Detail multiplier handed to painters (fewer layers at lower quality).
    let detail =
        function
        | QualityLevel.Ultra -> 1.0
        | QualityLevel.High -> 1.0
        | QualityLevel.Medium -> 1.0
        | QualityLevel.Low -> 0.6
        | QualityLevel.Static -> 0.6

    let all =
        [ QualityLevel.Ultra
          QualityLevel.High
          QualityLevel.Medium
          QualityLevel.Low
          QualityLevel.Static ]

    /// One step cheaper. Static is excluded: automatic control floors at Low so
    /// "no animation" stays a motion decision, never a performance one.
    let lower =
        function
        | QualityLevel.Ultra -> QualityLevel.High
        | QualityLevel.High -> QualityLevel.Medium
        | QualityLevel.Medium -> QualityLevel.Low
        | QualityLevel.Low -> QualityLevel.Low
        | QualityLevel.Static -> QualityLevel.Static

    let higher =
        function
        | QualityLevel.Ultra -> QualityLevel.Ultra
        | QualityLevel.High -> QualityLevel.Ultra
        | QualityLevel.Medium -> QualityLevel.High
        | QualityLevel.Low -> QualityLevel.Medium
        | QualityLevel.Static -> QualityLevel.Static

/// Controller state. Kept as a plain record with a pure `step` so the
/// thresholds — and crucially the guarantee that it cannot oscillate — are
/// unit-testable without rendering anything.
type QualityState =
    { Level: QualityLevel
      OverBudgetFrames: int
      UnderBudgetFrames: int
      CooldownFrames: int
      /// Why the level last changed, for the diagnostics overlay.
      LastChange: string }

/// Measures the background's own render time and adjusts quality to fit the
/// frame budget.
///
/// The thresholds are deliberately asymmetric. Downgrading is fast because
/// janking is immediately visible; upgrading is slow and gated by a cooldown
/// because flipping between levels looks far worse than simply staying lower.
[<RequireQualifiedAccess>]
module QualityController =

    /// Milliseconds of a 60Hz (16.6ms) frame allocated to the background.
    /// Kept as the documented default and the upper bound; the live controller
    /// derives a tighter budget from the real frame interval (see budgetFor).
    let budgetMs = 4.0

    /// Share of the frame the background may consume.
    let frameShare = 0.30

    /// The budget for a display actually running at `frameIntervalMs`.
    ///
    /// A fixed 4ms was wrong on fast displays: at 144Hz the entire frame is
    /// 6.94ms, so 4ms is 58% of it — Tier B's wave painter measured 3.15ms,
    /// stayed "within budget", and still dropped the app to 104fps. Deriving the
    /// budget from the observed interval makes the controller react to what the
    /// display can actually deliver.
    let budgetFor (frameIntervalMs: float) =
        if frameIntervalMs <= 0.0 then
            budgetMs
        else
            max 0.8 (min budgetMs (frameIntervalMs * frameShare))

    /// ~0.5s at 60fps before dropping a level.
    let downgradeAfterFrames = 30

    /// ~10s at 60fps of sustained headroom before raising one.
    let upgradeAfterFrames = 600

    /// ~5s at 60fps during which no upgrade may follow a change.
    let cooldownFrames = 300

    /// Fraction of budget below which a frame counts as "headroom".
    let upgradeThreshold = 0.6

    let initial =
        { Level = QualityLevel.Medium
          OverBudgetFrames = 0
          UnderBudgetFrames = 0
          CooldownFrames = 0
          LastChange = "initial" }

    /// Pure state transition for one measured background frame against an
    /// explicit budget. `step` below applies the 60Hz default.
    let stepWith (budget: float) (backgroundMs: float) (s: QualityState) =
        let cooled = max 0 (s.CooldownFrames - 1)

        if backgroundMs > budget then
            let over = s.OverBudgetFrames + 1
            let next = QualityLevel.lower s.Level

            if over >= downgradeAfterFrames && next <> s.Level then
                { Level = next
                  OverBudgetFrames = 0
                  UnderBudgetFrames = 0
                  CooldownFrames = cooldownFrames
                  LastChange = sprintf "over budget (%.1fms)" backgroundMs }
            else
                { s with
                    OverBudgetFrames = over
                    UnderBudgetFrames = 0
                    CooldownFrames = cooled }

        elif backgroundMs < budget * upgradeThreshold then
            let under = s.UnderBudgetFrames + 1
            let next = QualityLevel.higher s.Level

            if under >= upgradeAfterFrames && cooled = 0 && next <> s.Level then
                { Level = next
                  OverBudgetFrames = 0
                  UnderBudgetFrames = 0
                  CooldownFrames = cooldownFrames
                  LastChange = sprintf "headroom (%.1fms)" backgroundMs }
            else
                { s with
                    OverBudgetFrames = 0
                    UnderBudgetFrames = under
                    CooldownFrames = cooled }

        else
            // Inside the comfortable band: decay both counters so a single
            // outlier cannot accumulate towards a change.
            { s with
                OverBudgetFrames = 0
                UnderBudgetFrames = 0
                CooldownFrames = cooled }

    // ---- runtime holder ----------------------------------------------------

    let mutable private state = initial
    let mutable private forced: QualityLevel option = None

    /// Developer override; None returns to automatic control.
    let force (level: QualityLevel option) = forced <- level

    let forcedLevel () = forced

    /// The level in force right now.
    let current () =
        match forced with
        | Some level -> level
        | None -> state.Level

    let currentState () = state

    /// The 60Hz-default transition, used by tests and as documentation.
    let step (backgroundMs: float) (s: QualityState) = stepWith budgetMs backgroundMs s

    /// Feed one measured background render time. Called on the render thread.
    /// The budget tracks the display's real frame interval.
    let observe (backgroundMs: float) (frameIntervalMs: float) =
        state <- stepWith (budgetFor frameIntervalMs) backgroundMs state

    /// Pins quality lower while memory pressure is being relieved.
    let pinLower () =
        state <-
            { state with
                Level = QualityLevel.lower state.Level
                CooldownFrames = cooldownFrames
                LastChange = "memory pressure" }

    let resetForTesting () =
        state <- initial
        forced <- None
