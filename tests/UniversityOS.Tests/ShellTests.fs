module UniversityOS.Tests.ShellTests

open Xunit
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Design

/// The Stage 2 shell: navigation model and layout rules.
///
/// The view itself needs a running Avalonia application, so what is tested here
/// is everything the view *decides* — which is where shells actually go wrong.

// ---- navigation model ------------------------------------------------------

[<Fact>]
let ``every page appears exactly once in the sidebar`` () =
    // A page missing from `groups` is unreachable except by keyboard; a page
    // listed twice gets two highlighted entries when it is selected.
    let listed = Page.all |> List.map (fun (p, _, _) -> p)
    Assert.Equal(listed.Length, (List.distinct listed).Length)

    let allCases =
        [ Page.Dashboard; Page.Planner; Page.Calendar; Page.Modules; Page.Assignments
          Page.Notes; Page.Documents; Page.Lectures; Page.Graph; Page.Inbox
          Page.Chat; Page.Search; Page.Timeline; Page.Connections; Page.Settings
          Page.DesignPreview ]

    for page in allCases do
        Assert.Contains(page, listed)

[<Fact>]
let ``every entry has a label and a glyph`` () =
    for (page, icon, label) in Page.all do
        Assert.False(System.String.IsNullOrWhiteSpace icon, sprintf "%A has no glyph" page)
        Assert.False(System.String.IsNullOrWhiteSpace label, sprintf "%A has no label" page)
        Assert.Equal(label, Page.title page)
        Assert.Equal(icon, Page.icon page)

[<Fact>]
let ``sidebar groups line up with the adaptive background sections`` () =
    // The redesign names the sidebar groups after the background families, so
    // an Adaptive background changes exactly when the group heading does. If
    // these drift apart, moving between two screens under one heading changes
    // the background — which is the opposite of the intended sense of place.
    let expected =
        [ "Overview", Overview; "Work", Work; "Knowledge", Knowledge; "System", SystemArea ]
        |> Map.ofList

    for (groupName, items) in Page.groups do
        match expected.TryFind groupName with
        | None -> Assert.Fail(sprintf "sidebar group '%s' has no matching background section" groupName)
        | Some section ->
            for (page, _, _) in items do
                // The developer surface is deliberately exempt: it is not a
                // destination the background design has an opinion about.
                if page <> Page.DesignPreview then
                    Assert.Equal(section, Page.backgroundSection page)

[<Fact>]
let ``the first nine entries are the ones Ctrl+1..9 reaches`` () =
    // The shortcut indexes into Page.all, so reordering groups silently
    // reassigns the shortcuts. This pins what the first nine are.
    let firstNine = Page.all |> List.truncate 9 |> List.map (fun (_, _, label) -> label)

    Assert.Equal<string list>(
        [ "Dashboard"; "Timeline"; "Planner"; "Inbox"; "Assignments"; "Modules"; "Lectures"; "Notes"; "Documents" ],
        firstNine
    )

// ---- layout ----------------------------------------------------------------

[<Fact>]
let ``a pinned sidebar is always visible, whatever the pointer did`` () =
    Assert.True(ShellLayout.sidebarVisible Pinned)

[<Fact>]
let ``an unpinned sidebar shows while revealed and while a hide is pending`` () =
    Assert.False(ShellLayout.sidebarVisible Hidden)
    Assert.True(ShellLayout.sidebarVisible Revealed)
    // Still on screen during the grace period — that is the point of it.
    Assert.True(ShellLayout.sidebarVisible (HidePending 1))

[<Fact>]
let ``only a docked sidebar takes width from the content`` () =
    // Phase 0 is docked, 1 is floating.
    Assert.Equal(252.0, ShellLayout.reservedWidth 0.0 SidebarDensity.Expanded, 3)
    Assert.Equal(80.0, ShellLayout.reservedWidth 0.0 SidebarDensity.Compact, 3)

    // Floating: content runs the full width and the panel hovers over it.
    Assert.Equal(0.0, ShellLayout.reservedWidth 1.0 SidebarDensity.Expanded, 3)
    Assert.Equal(0.0, ShellLayout.reservedWidth 1.0 SidebarDensity.Compact, 3)

[<Fact>]
let ``density controls width and labels, and nothing else`` () =
    Assert.True(ShellLayout.showsLabels SidebarDensity.Expanded)
    Assert.False(ShellLayout.showsLabels SidebarDensity.Compact)

    Assert.True(SidebarDensity.width SidebarDensity.Expanded > SidebarDensity.width SidebarDensity.Compact)

    // Compact still has room for a 16px glyph plus padding.
    Assert.True(SidebarDensity.width SidebarDensity.Compact >= 48.0)

// ---- the reveal/hide state machine -----------------------------------------
//
// The defect these replace: the edge strip and the panel were separate,
// conditionally-mounted siblings, each with its own immediate pointer handler.
// Revealing removed the element under the pointer and shifted the panel into
// its sibling index — FuncUI patches children positionally, so the strip
// control was mutated into the panel underneath the cursor — and the panel's
// exit handler then fired, because the pointer was standing in the 14px inset
// gap rather than inside the panel. That hid it, remounting the strip under the
// pointer, which revealed it again. At frame rate.

[<Fact>]
let ``entering the region reveals a hidden sidebar`` () =
    Assert.Equal(Revealed, SidebarInteraction.pointerEntered Hidden)

[<Fact>]
let ``the region grows on reveal, so the pointer that triggered it stays inside`` () =
    // This is the property that makes the loop impossible. If revealing could
    // ever SHRINK the region, the pointer could end up outside it and generate
    // the exit that starts the oscillation.
    for density in [ SidebarDensity.Expanded; SidebarDensity.Compact ] do
        let hidden = ShellLayout.regionWidth Hidden density 1.0
        let revealed = ShellLayout.regionWidth Revealed density 1.0

        Assert.True(revealed > hidden, sprintf "%A: region must grow on reveal (%f -> %f)" density hidden revealed)

        // And it must not shrink while a hide is merely pending, or leaving
        // would immediately re-trigger an enter.
        Assert.Equal(revealed, ShellLayout.regionWidth (HidePending 1) density 1.0, 3)

[<Fact>]
let ``the inset gap is inside the region, not a dead zone`` () =
    // The pointer travelling from the edge strip to the panel crosses x = 14.
    // If that gap were outside the interaction region it would read as "left
    // the sidebar" every single time.
    let revealedWidth = ShellLayout.regionWidth Revealed SidebarDensity.Expanded 1.0

    Assert.True(ShellLayout.floatingInset < revealedWidth)
    Assert.True(ShellLayout.hotzoneWidth < revealedWidth)

    // The region spans the gap on BOTH sides of the panel plus the panel itself.
    Assert.Equal(
        ShellLayout.floatingInset * 2.0 + SidebarDensity.width SidebarDensity.Expanded,
        revealedWidth,
        3
    )

[<Fact>]
let ``moving within the region keeps it revealed`` () =
    // Movement is reported as an enter. Repeated enters must be idempotent, or
    // every mouse-move inside the panel would churn state.
    let mutable state = SidebarInteraction.pointerEntered Hidden

    for _ in 1..50 do
        state <- SidebarInteraction.pointerEntered state

    Assert.Equal(Revealed, state)

[<Fact>]
let ``leaving schedules a hide rather than hiding immediately`` () =
    let after = SidebarInteraction.pointerExited 7 Revealed
    Assert.Equal(HidePending 7, after)

    // Still visible during the grace period.
    Assert.True(ShellLayout.sidebarVisible after)
    Assert.Equal(Some 7, SidebarInteraction.schedules Revealed after)

[<Fact>]
let ``re-entering during the grace period cancels the hide`` () =
    let pending = SidebarInteraction.pointerExited 7 Revealed
    let reentered = SidebarInteraction.pointerEntered pending

    Assert.Equal(Revealed, reentered)

    // The already-scheduled message is now stale and must do nothing.
    Assert.Equal(Revealed, SidebarInteraction.hideElapsed 7 reentered)

[<Fact>]
let ``the hide happens when its own token comes due`` () =
    let pending = SidebarInteraction.pointerExited 7 Revealed
    Assert.Equal(Hidden, SidebarInteraction.hideElapsed 7 pending)

[<Fact>]
let ``a stale token cannot hide a sidebar that was revealed again`` () =
    // Leave, come back, leave again: two hides are in flight. The first must
    // not close the second's window.
    let first = SidebarInteraction.pointerExited 1 Revealed
    let back = SidebarInteraction.pointerEntered first
    let second = SidebarInteraction.pointerExited 2 back

    Assert.Equal(HidePending 2, second)
    Assert.Equal(second, SidebarInteraction.hideElapsed 1 second)
    Assert.Equal(Hidden, SidebarInteraction.hideElapsed 2 second)

[<Fact>]
let ``exiting twice does not stack timers`` () =
    // Two exits in a row must keep the ORIGINAL token and schedule nothing new,
    // or a later re-entry would be cancelled by one timer and hidden by another.
    let first = SidebarInteraction.pointerExited 1 Revealed
    let second = SidebarInteraction.pointerExited 2 first

    Assert.Equal(HidePending 1, second)
    Assert.Equal(None, SidebarInteraction.schedules first second)

[<Fact>]
let ``pinning cancels any pending hide`` () =
    let pending = SidebarInteraction.pointerExited 3 Revealed
    let pinned = SidebarInteraction.pin pending

    Assert.Equal(Pinned, pinned)
    // The delayed message is already in flight and cannot be unscheduled — it
    // must simply have no effect.
    Assert.Equal(Pinned, SidebarInteraction.hideElapsed 3 pinned)

[<Fact>]
let ``a pinned sidebar ignores all pointer traffic`` () =
    Assert.Equal(Pinned, SidebarInteraction.pointerEntered Pinned)
    Assert.Equal(Pinned, SidebarInteraction.pointerExited 9 Pinned)
    Assert.Equal(None, SidebarInteraction.schedules Pinned (SidebarInteraction.pointerExited 9 Pinned))

[<Fact>]
let ``unpinning under the pointer stays revealed until it genuinely leaves`` () =
    // The control that unpins lives inside the panel, so the pointer is over it
    // by construction.
    let state = SidebarInteraction.unpin true Pinned
    Assert.Equal(Revealed, state)

    // And it only goes away on a real exit, then the delay.
    let pending = SidebarInteraction.pointerExited 1 state
    Assert.Equal(HidePending 1, pending)
    Assert.Equal(Hidden, SidebarInteraction.hideElapsed 1 pending)

[<Fact>]
let ``unpinning from the command palette hides, because no exit will ever come`` () =
    Assert.Equal(Hidden, SidebarInteraction.unpin false Pinned)

[<Fact>]
let ``rapid alternating enter and leave cannot oscillate visibility`` () =
    // The old implementation flickered because each transition destroyed the
    // element that produced the next event. Here, a burst of alternating events
    // must leave the sidebar VISIBLE throughout — the hide only lands when a
    // token comes due, and every one of these is overtaken.
    let mutable state = Hidden
    let mutable token = 0
    let mutable everHiddenMidBurst = false

    for i in 1..200 do
        state <-
            if i % 2 = 1 then
                SidebarInteraction.pointerEntered state
            else
                token <- token + 1
                SidebarInteraction.pointerExited token state

        // Any token that came due mid-burst is stale by now; apply them all.
        for stale in 1 .. token - 1 do
            state <- SidebarInteraction.hideElapsed stale state

        if not (ShellLayout.sidebarVisible state) then
            everHiddenMidBurst <- true

    Assert.False(everHiddenMidBurst, "the sidebar collapsed during a burst of boundary events")

[<Fact>]
let ``the developer reveal toggle drives the same state machine`` () =
    // Keyboard only — it is a way to exercise the interaction during review,
    // not a second code path that could behave differently.
    Assert.Equal(Revealed, SidebarInteraction.devToggle Hidden)
    Assert.Equal(Hidden, SidebarInteraction.devToggle Revealed)
    Assert.Equal(Hidden, SidebarInteraction.devToggle (HidePending 1))
    Assert.Equal(Pinned, SidebarInteraction.devToggle Pinned)

[<Fact>]
let ``a restored session starts from the persisted pin state only`` () =
    Assert.Equal(Pinned, SidebarInteraction.restore true)
    Assert.Equal(Hidden, SidebarInteraction.restore false)

[<Fact>]
let ``persisted state carries pin and density only, never hover state`` () =
    // Hover is not a preference. If it were persisted, an app closed with the
    // pointer at the left edge would reopen with a sidebar showing that nothing
    // will ever hide.
    let fields =
        typeof<UiState>.GetProperties() |> Array.map (fun p -> p.Name) |> Set.ofArray

    Assert.Contains("SidebarPinned", fields)
    Assert.Contains("SidebarDensity", fields)

    for forbidden in [ "SidebarRevealed"; "SidebarState"; "HidePending"; "Hovering"; "NextHideToken" ] do
        Assert.DoesNotContain(forbidden, fields)

[<Fact>]
let ``the hide delay is long enough to cross a boundary and short enough to feel deliberate`` () =
    Assert.InRange(ShellLayout.hideDelayMs, 150, 250)

// ---- the pin / unpin transition --------------------------------------------

/// Runs the transition to completion at a given frame interval, returning every
/// intermediate phase.
let private runTransition (frameSeconds: float) (start: float) (target: float) =
    let mutable phase = start
    let visited = ResizeArray<float>()
    let mutable guard = 0

    while SidebarTransition.isAnimating phase target && guard < 10_000 do
        phase <- SidebarTransition.step frameSeconds phase target
        visited.Add phase
        guard <- guard + 1

    phase, List.ofSeq visited

[<Fact>]
let ``pinned to floating reaches the correct final geometry`` () =
    let final, _ = runTransition (1.0 / 60.0) 0.0 (SidebarTransition.targetFor false)
    let eased = SidebarTransition.ease final

    Assert.Equal(1.0, final, 6)
    Assert.Equal(1.0, eased, 6)
    Assert.Equal(ShellLayout.floatingInset, ShellLayout.insetAt eased, 6)
    Assert.Equal(0.0, ShellLayout.reservedWidth eased SidebarDensity.Expanded, 6)

    Assert.Equal(
        SidebarDensity.width SidebarDensity.Expanded + 2.0 * ShellLayout.floatingInset,
        ShellLayout.regionWidth Revealed SidebarDensity.Expanded eased,
        6
    )

[<Fact>]
let ``floating to pinned reaches the correct final geometry`` () =
    let final, _ = runTransition (1.0 / 60.0) 1.0 (SidebarTransition.targetFor true)
    let eased = SidebarTransition.ease final

    Assert.Equal(0.0, final, 6)
    Assert.Equal(0.0, eased, 6)
    // Docked: flush to the edge, full width reserved, region exactly the panel.
    Assert.Equal(0.0, ShellLayout.insetAt eased, 6)

    Assert.Equal(
        SidebarDensity.width SidebarDensity.Expanded,
        ShellLayout.reservedWidth eased SidebarDensity.Expanded,
        6
    )

    Assert.Equal(
        SidebarDensity.width SidebarDensity.Expanded,
        ShellLayout.regionWidth Pinned SidebarDensity.Expanded eased,
        6
    )

[<Fact>]
let ``every midpoint stays within valid bounds`` () =
    // A geometry that leaves its range mid-flight is what produces a blank gap
    // or a panel that overhangs the region it lives in.
    for start, target in [ 0.0, 1.0; 1.0, 0.0 ] do
        let _, visited = runTransition (1.0 / 144.0) start target

        for phase in visited do
            let eased = SidebarTransition.ease phase
            Assert.InRange(phase, 0.0, 1.0)
            Assert.InRange(eased, 0.0, 1.0)
            Assert.InRange(ShellLayout.insetAt eased, 0.0, ShellLayout.floatingInset)

            let reserved = ShellLayout.reservedWidth eased SidebarDensity.Expanded
            Assert.InRange(reserved, 0.0, SidebarDensity.width SidebarDensity.Expanded)

            // The panel must always fit its region exactly: width + both insets.
            let region = ShellLayout.regionWidth Revealed SidebarDensity.Expanded eased
            Assert.Equal(SidebarDensity.width SidebarDensity.Expanded + 2.0 * ShellLayout.insetAt eased, region, 6)

[<Fact>]
let ``the transition never overshoots or reverses direction`` () =
    // No bounce. Monotonic in both directions.
    let _, forward = runTransition (1.0 / 60.0) 0.0 1.0

    forward
    |> List.map SidebarTransition.ease
    |> List.pairwise
    |> List.iter (fun (a, b) -> Assert.True(b >= a - 1e-9, sprintf "forward eased value went backwards: %f then %f" a b))

    let _, backward = runTransition (1.0 / 60.0) 1.0 0.0

    backward
    |> List.map SidebarTransition.ease
    |> List.pairwise
    |> List.iter (fun (a, b) -> Assert.True(b <= a + 1e-9, sprintf "reverse eased value went forwards: %f then %f" a b))

[<Fact>]
let ``the transition takes the same wall-clock time at any frame rate`` () =
    for fps in [ 30.0; 60.0; 144.0; 240.0 ] do
        let frame = 1.0 / fps
        let _, visited = runTransition frame 0.0 1.0
        let elapsed = float visited.Length * frame

        // Within one frame of the specified duration.
        Assert.InRange(elapsed, SidebarTransition.durationSeconds, SidebarTransition.durationSeconds + frame)

[<Fact>]
let ``rapid reversal retargets rather than queueing`` () =
    // Flip the destination halfway. The phase must continue from where it is —
    // no restart, no second animation, no jump.
    let mutable phase = 0.0

    for _ in 1..8 do
        phase <- SidebarTransition.step (1.0 / 60.0) phase 1.0

    let midpoint = phase
    Assert.InRange(midpoint, 0.05, 0.95)

    // Reverse: the very next step must move DOWN from the midpoint, not from 1.
    let afterReversal = SidebarTransition.step (1.0 / 60.0) midpoint 0.0
    Assert.True(afterReversal < midpoint)
    Assert.True(afterReversal > midpoint - 0.2, "reversal must continue from the current phase, not snap")

    // And it still lands exactly on the destination.
    let final, _ = runTransition (1.0 / 60.0) afterReversal 0.0
    Assert.Equal(0.0, final, 6)

[<Fact>]
let ``a hundred alternating clicks leave exactly one animation converging`` () =
    // Each flip only changes the destination; there is never more than one
    // phase, so there is never more than one thing to drive.
    let mutable phase = 0.0
    let mutable target = 0.0

    for i in 1..100 do
        target <- if i % 2 = 0 then 0.0 else 1.0
        phase <- SidebarTransition.step (1.0 / 60.0) phase target
        Assert.InRange(phase, 0.0, 1.0)

    let final, _ = runTransition (1.0 / 60.0) phase target
    Assert.Equal(target, final, 6)

[<Fact>]
let ``Reduce Motion applies the destination without interpolating`` () =
    Assert.Equal(1.0, SidebarTransition.settle (SidebarTransition.targetFor false), 6)
    Assert.Equal(0.0, SidebarTransition.settle (SidebarTransition.targetFor true), 6)

    // And nothing is left to animate afterwards, so no tick loop starts.
    Assert.False(SidebarTransition.isAnimating (SidebarTransition.settle 1.0) 1.0)
    Assert.False(SidebarTransition.isAnimating (SidebarTransition.settle 0.0) 0.0)

[<Fact>]
let ``the transition drives nothing once it has arrived`` () =
    Assert.False(SidebarTransition.isAnimating 0.0 0.0)
    Assert.False(SidebarTransition.isAnimating 1.0 1.0)
    Assert.True(SidebarTransition.isAnimating 0.0 1.0)
    Assert.True(SidebarTransition.isAnimating 0.5 1.0)

[<Fact>]
let ``a zero or nonsensical frame delta cannot move or corrupt the phase`` () =
    Assert.Equal(0.4, SidebarTransition.step 0.0 0.4 1.0, 6)
    Assert.Equal(0.4, SidebarTransition.step -1.0 0.4 1.0, 6)
    Assert.Equal(0.4, SidebarTransition.step nan 0.4 1.0, 6)

[<Fact>]
let ``density is preserved across the whole transition`` () =
    // Only the inset moves. The panel keeps the width its density asks for at
    // every phase, in both densities.
    for density in [ SidebarDensity.Expanded; SidebarDensity.Compact ] do
        let _, visited = runTransition (1.0 / 60.0) 0.0 1.0

        for phase in visited do
            let eased = SidebarTransition.ease phase
            let region = ShellLayout.regionWidth Revealed density eased
            Assert.Equal(SidebarDensity.width density, region - 2.0 * ShellLayout.insetAt eased, 6)

[<Fact>]
let ``the duration sits in the specified band and ignores the speed settings`` () =
    Assert.InRange(SidebarTransition.durationSeconds, 0.22, 0.30)

    // Background speed must not reach shell chrome. Proven by construction —
    // durationSeconds is a literal, not a function of Speed — so this asserts
    // the shape that keeps it true.
    for speed in [ Speed.Slow; Speed.Normal; Speed.Fast ] do
        Assert.NotEqual(0.0, BackgroundParams.speedMultiplier speed)
        Assert.Equal(0.26, SidebarTransition.durationSeconds, 6)

[<Fact>]
let ``no transition state is persisted`` () =
    // The phase is animation state, not a preference. Persisting it would mean
    // reopening the app mid-transition.
    let fields =
        typeof<UiState>.GetProperties() |> Array.map (fun p -> p.Name) |> Set.ofArray

    for forbidden in [ "SidebarPhase"; "Phase"; "Animating"; "Transition"; "SidebarTarget" ] do
        Assert.DoesNotContain(forbidden, fields)

    // Only the final pin state is stored.
    Assert.Contains("SidebarPinned", fields)

[<Fact>]
let ``density and pinning are independent`` () =
    // The whole point of replacing the old Expanded -> Compact -> Hidden cycle:
    // every combination must be reachable, which a single tri-state could not
    // express.
    let combinations =
        [ for density in [ SidebarDensity.Expanded; SidebarDensity.Compact ] do
              for pinned in [ true; false ] -> density, pinned ]

    Assert.Equal(4, combinations.Length)

    for (density, pinned) in combinations do
        let state = { UiState.defaults with SidebarDensity = density; SidebarPinned = pinned }
        Assert.Equal(density, state.SidebarDensity)
        Assert.Equal(pinned, state.SidebarPinned)

    // And toggling one never moves the other.
    Assert.Equal(SidebarDensity.Compact, SidebarDensity.toggle SidebarDensity.Expanded)
    Assert.Equal(SidebarDensity.Expanded, SidebarDensity.toggle SidebarDensity.Compact)

[<Fact>]
let ``the floating panel is inset far enough for its own shadow`` () =
    // The panel's elevation shadow is drawn outside its bounds. Too small an
    // inset clips it against the window edge and the panel stops looking lifted.
    Assert.True(ShellLayout.floatingInset >= 8.0)
    Assert.True(ShellLayout.floatingRadius > 0.0)
    Assert.True(ShellLayout.hotzoneWidth > 0.0)

    // The reveal strip must be narrower than the inset gap plus the panel, or
    // it would still be under the pointer once the panel appears.
    Assert.True(ShellLayout.hotzoneWidth < SidebarDensity.width SidebarDensity.Compact)
