namespace UniversityOS.App

open UniversityOS.Domain

/// What the sidebar is doing right now.
///
/// This is interaction state, not a preference: it lives in the shell model and
/// is never persisted. Only `Pinned` has a persisted counterpart
/// (`UiState.SidebarPinned`).
type SidebarState =
    /// Docked. Reserves layout width; hover is irrelevant.
    | Pinned
    /// Floating and off screen. Only the edge strip is live.
    | Hidden
    /// Floating and on screen, pointer inside the interaction region.
    | Revealed
    /// Floating and still on screen, but the pointer has left and a hide is
    /// scheduled. The token identifies THIS scheduled hide, so a delayed
    /// message that has been overtaken by events can be recognised and ignored.
    | HidePending of token: int

/// Geometry and visibility rules for the application shell.
///
/// Pulled out of the view because these are the decisions that go wrong: the
/// sidebar is visible under two different conditions, it reserves layout width
/// under only one of them, and the reveal strip must exist under exactly the
/// complement of the visibility rule or it will either be unreachable or sit on
/// top of a screen's own controls. All three are one-liners — and all three are
/// easy to get subtly inconsistent when they live in three separate places in a
/// 200-line view function.
[<RequireQualifiedAccess>]
module ShellLayout =

    /// Inset of the floating panel from the window edges, in px. From the
    /// redesign.
    [<Literal>]
    let floatingInset = 14.0

    /// Width of the strip along the left edge that brings an auto-hidden
    /// sidebar back.
    [<Literal>]
    let hotzoneWidth = 20.0

    /// Corner radius of the floating panel, before the user's radius scale.
    [<Literal>]
    let floatingRadius = 22.0

    /// How long the sidebar stays up after the pointer leaves, in ms.
    ///
    /// Long enough that crossing a boundary — the inset gap, the panel's own
    /// rounded corner, a momentary gap between two hit-test regions — cannot
    /// collapse it; short enough that a deliberate move away feels immediate.
    [<Literal>]
    let hideDelayMs = 200

    let sidebarVisible (state: SidebarState) =
        match state with
        | Pinned
        | Revealed
        | HidePending _ -> true
        | Hidden -> false

    /// Layout width the sidebar takes from the content.
    ///
    /// Only a pinned sidebar reserves space. A floating one hovers over content
    /// that runs the full width of the window — which is what makes the glass
    /// read as a layer above the interface rather than as another column of it.
    /// Layout width the sidebar takes from the content, at a transition phase.
    ///
    /// Continuous in the phase, which is what makes the content slide rather
    /// than jump when the sidebar detaches or docks.
    let reservedWidth (eased: float) (density: SidebarDensity) =
        SidebarDensity.width density * (1.0 - max 0.0 (min 1.0 eased))

    /// Inset in px at a given transition phase. 0 docked, 14 fully floating.
    let insetAt (eased: float) = floatingInset * max 0.0 (min 1.0 eased)

    /// Width of the ONE interaction region that owns all sidebar hover.
    ///
    /// This is the fix for the flicker. The edge strip, the 14px inset gap and
    /// the panel itself are a single always-mounted control whose width
    /// changes — never separate elements that appear and disappear as the state
    /// they control changes. Revealing only ever GROWS the region, so a pointer
    /// that just triggered the reveal is still inside it afterwards and cannot
    /// generate an exit.
    /// The panel keeps its density width throughout the pin transition; only the
    /// inset around it changes. So the region is width + 2×inset, and at phase 0
    /// that is exactly the docked width with the panel flush to the edge.
    let regionWidth (state: SidebarState) (density: SidebarDensity) (eased: float) =
        if sidebarVisible state then
            SidebarDensity.width density + 2.0 * insetAt eased
        else
            hotzoneWidth

    /// Whether nav entries are drawn with their labels.
    let showsLabels (density: SidebarDensity) = density = SidebarDensity.Expanded

/// The reveal/hide state machine.
///
/// Every transition is a pure function of the current state and one event, so
/// the interaction can be tested without a window — which matters because the
/// bug it replaces was a feedback loop between two elements whose existence
/// depended on the state they were controlling, and that is invisible to any
/// test that only checks the final rendered output.
[<RequireQualifiedAccess>]
module SidebarInteraction =

    /// Pointer entered the interaction region — or moved within it. Cancels any
    /// pending hide by leaving `HidePending`, which makes the already-scheduled
    /// message stale rather than trying to unschedule it.
    let pointerEntered (state: SidebarState) =
        match state with
        | Pinned -> Pinned
        | Hidden
        | Revealed
        | HidePending _ -> Revealed

    /// Pointer left the interaction region entirely.
    ///
    /// An exit while a hide is already pending keeps the ORIGINAL token: two
    /// exits in a row must not stack two timers, or a later re-entry would be
    /// cancelled by the first and hidden by the second.
    let pointerExited (nextToken: int) (state: SidebarState) =
        match state with
        | Revealed -> HidePending nextToken
        | HidePending existing -> HidePending existing
        | Pinned -> Pinned
        | Hidden -> Hidden

    /// A scheduled hide came due. It only acts if it is still the scheduled
    /// hide: pinning, re-entering, or a newer schedule all leave it inert.
    let hideElapsed (token: int) (state: SidebarState) =
        match state with
        | HidePending pending when pending = token -> Hidden
        | other -> other

    /// True when this transition needs a delayed hide scheduled.
    let schedules (before: SidebarState) (after: SidebarState) =
        match before, after with
        | HidePending _, HidePending _ -> None // already scheduled; do not stack
        | _, HidePending token -> Some token
        | _ -> None

    let pin (_: SidebarState) = Pinned

    /// Unpinning keeps the panel up when the pointer is genuinely over it — the
    /// usual case, because the control that unpins lives inside the panel. From
    /// anywhere else (the command palette) it goes straight to hidden, since no
    /// exit event would ever arrive to hide it.
    let unpin (pointerOverSidebar: bool) (_: SidebarState) =
        if pointerOverSidebar then Revealed else Hidden

    /// Developer-only reveal toggle (Ctrl+Shift+B). Exists so the floating
    /// sidebar can be exercised from the keyboard during review without
    /// synthesising pointer input.
    let devToggle (state: SidebarState) =
        match state with
        | Pinned -> Pinned
        | Hidden -> Revealed
        | Revealed
        | HidePending _ -> Hidden

    /// The state a freshly loaded session starts in.
    let restore (pinned: bool) = if pinned then Pinned else Hidden

/// The pin ↔ unpin transition.
///
/// A single scalar phase drives every animated property, so there is no way for
/// the inset, the radius, the glass blend and the content offset to disagree
/// mid-flight — they are all functions of the same number. Retargeting is a
/// change of destination, not a new animation, which is what makes rapid
/// clicking reverse smoothly instead of queueing.
[<RequireQualifiedAccess>]
module SidebarTransition =

    /// Seconds. Inside the redesign's restrained 220–300ms band.
    ///
    /// Deliberately NOT scaled by the animation-speed preference: the brief
    /// specifies this window, and Slow would put it at 400ms. Background speed
    /// has nothing to do with shell chrome and is not consulted either.
    [<Literal>]
    let durationSeconds = 0.26

    /// 0 = docked, 1 = floating.
    let targetFor (pinned: bool) = if pinned then 0.0 else 1.0

    let private clamp01 (v: float) = max 0.0 (min 1.0 v)

    /// Cubic ease-out: quick to leave, settling into the destination. Monotonic
    /// on [0,1], so it can never overshoot or bounce.
    let ease (phase: float) =
        let t = clamp01 phase
        1.0 - (1.0 - t) ** 3.0

    /// Advances the phase toward the target by real elapsed time.
    ///
    /// Time-based rather than per-frame, so the transition takes the same
    /// wall-clock duration at 60Hz and 144Hz, and a dropped frame costs
    /// smoothness rather than making the animation longer.
    let step (deltaSeconds: float) (phase: float) (target: float) =
        if System.Double.IsNaN deltaSeconds || deltaSeconds <= 0.0 then
            clamp01 phase
        else
            let travel = deltaSeconds / durationSeconds
            let current = clamp01 phase
            let goal = clamp01 target

            if goal > current then min goal (current + travel)
            else max goal (current - travel)

    /// True while the phase has not yet arrived. Drives the tick subscription,
    /// so exactly one loop exists no matter how often the target changes.
    let isAnimating (phase: float) (target: float) =
        abs (clamp01 phase - clamp01 target) > 1e-6

    /// Reduce Motion jumps straight to the destination. The interaction is
    /// unchanged — only the interpolation is skipped.
    let settle (target: float) = clamp01 target
