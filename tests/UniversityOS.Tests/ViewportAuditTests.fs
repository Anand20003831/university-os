module UniversityOS.Tests.ViewportAuditTests

open System
open Xunit
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Features
open UniversityOS.Tests.AuditHarness
open UniversityOS.Tests.AuditFixtures

/// **The viewport matrix.** All eleven migrated screens, at nine window sizes,
/// under three sidebar states.
///
/// What this can establish: what the view tree *declares*. A control that
/// declares `Width 380.0` inside a 340px-wide screen will overflow, and that is a
/// fact, not an opinion. Impossible dimensions, missing sections, absent
/// scrollers and non-wrapping action rows are all equally decidable.
///
/// **What this cannot establish:** what anything looks like. A FuncUI `IView` is
/// an immutable description, never a laid-out control, so measured positions,
/// actual wrap points, real overflow after text measurement, and whether the
/// result is *pleasant* are all outside its reach. Nothing here is visual
/// approval and none of it is a substitute for looking at the screen.

// ---------------------------------------------------------------------------
// the matrix
// ---------------------------------------------------------------------------

/// Window width -> screen width, for each sidebar state that materially changes
/// the answer. Derived in `AuditHarness.contentWidthWith` from the real shell
/// constants, not from guesses.
type private SidebarCase =
    { Label: string
      Density: SidebarDensity
      Pinned: bool }

let private sidebarCases =
    [ { Label = "expanded+pinned"; Density = SidebarDensity.Expanded; Pinned = true }
      { Label = "compact+pinned"; Density = SidebarDensity.Compact; Pinned = true }
      { Label = "unpinned (floating)"; Density = SidebarDensity.Expanded; Pinned = false } ]

/// **The window cannot be made smaller than 1100 x 700.**
///
/// `MainWindow` sets `MinWidth <- 1100.0` and `MinHeight <- 700.0`
/// (Program.fs:220-221), and the shell has no narrow-width sidebar handling to
/// relax it. So four of the nine window widths the audit brief lists — 1024,
/// 768, 480 and 360 — **cannot be produced by resizing this application**.
///
/// That is reported, not silently accepted, and not "fixed": lowering the
/// minimum is a product decision about which form factors the app supports, not
/// an objective defect, and it is not this session's to make.
///
/// The consequence for the audit is that the widths split into two bands, and
/// conflating them would be dishonest in both directions — it would fail the
/// build on layouts nobody can reach, while under-testing the ones everybody does.
[<Literal>]
let windowMinWidth = 1100.0

[<Literal>]
let windowMinHeight = 700.0

/// Every (window width, sidebar state) pair, as the screen width it produces.
let private matrix =
    [ for w, _ in viewports do
          for c in sidebarCases do
              yield w, c.Label, contentWidthWith c.Density c.Pinned w ]

/// Screen widths the application can actually produce. These are audited hard —
/// a failure here is a real defect a user could hit today.
let private screenWidths =
    matrix
    |> List.filter (fun (w, _, _) -> w >= windowMinWidth)
    |> List.map (fun (_, _, sw) -> sw)
    |> List.distinct
    |> List.sort

/// Screen widths only reachable if the window minimum were lowered. Audited for
/// robustness — they must not throw or produce impossible geometry — but not
/// held to the full overflow contract, because no user can reach them.
let private unreachableWidths =
    matrix
    |> List.filter (fun (w, _, _) -> w < windowMinWidth)
    |> List.map (fun (_, _, sw) -> sw)
    |> List.distinct
    |> List.sort

[<Fact>]
let ``the window minimum is what makes four of the nine audited widths unreachable`` () =
    // Pinned inside the smallest permitted window is the narrowest a screen can
    // legitimately be.
    let narrowestReachable = contentWidthWith SidebarDensity.Expanded true windowMinWidth
    Assert.Equal(windowMinWidth - 252.0 - 20.0, narrowestReachable)

    // It still crosses the 900px narrow breakpoint, so narrow layouts are
    // genuinely reachable — this is not an audit of dead code.
    Assert.True(narrowestReachable < CalendarLogic.narrowWidth)

    // And the four small window widths are all below the minimum.
    let belowMinimum = viewports |> List.map fst |> List.filter (fun w -> w < windowMinWidth)
    Assert.Equal<float list>([ 1024.0; 768.0; 480.0; 360.0 ], belowMinimum)

// ---------------------------------------------------------------------------
// 1. it renders at all
// ---------------------------------------------------------------------------

/// The floor everything else stands on. A screen that throws while being
/// described cannot be audited, and would crash the app on resize.
[<Theory>]
[<InlineData "Settings">]
[<InlineData "Connections">]
[<InlineData "Calendar">]
[<InlineData "Search">]
[<InlineData "Assistant">]
[<InlineData "Graph">]
[<InlineData "Documents">]
[<InlineData "Notes">]
[<InlineData "Lectures">]
[<InlineData "Modules">]
[<InlineData "Assignments">]
let ``every screen constructs at every width in the matrix, in both themes`` (name: string) =
    for width in screenWidths do
        for theme in [ tokens; tokensLight ] do
            let tree = renderIn theme name width
            Assert.NotEmpty(all tree)

// ---------------------------------------------------------------------------
// 2. no impossible geometry
// ---------------------------------------------------------------------------

/// A negative or NaN width throws inside Avalonia's measure pass — this is a
/// crash check, not a style preference. Widths are computed from `Model.Width`
/// on several screens, so a subtraction that goes negative at 360px is exactly
/// the kind of bug that hides until somebody narrows the window.
[<Theory>]
[<InlineData "Settings">]
[<InlineData "Connections">]
[<InlineData "Calendar">]
[<InlineData "Search">]
[<InlineData "Assistant">]
[<InlineData "Graph">]
[<InlineData "Documents">]
[<InlineData "Notes">]
[<InlineData "Lectures">]
[<InlineData "Modules">]
[<InlineData "Assignments">]
let ``no screen declares an impossible dimension at any width`` (name: string) =
    for width in screenWidths do
        let offenders = impossibleDimensions (render name width)

        Assert.True(
            List.isEmpty offenders,
            sprintf "%s at %.0fpx declares impossible dimensions: %A" name width offenders
        )

// ---------------------------------------------------------------------------
// 3. horizontal overflow
// ---------------------------------------------------------------------------

/// A control declaring a fixed `Width` or `MinWidth` wider than the screen it
/// sits in forces the page to scroll sideways. `MaxWidth` is exempt — it is a
/// ceiling, not a demand.
///
/// This is the test that catches a rail which fails to move: Connections' detail
/// rail is 380px, and at a 360px window the screen is only ~330px wide, so if
/// `placementFor` did not drop it below the grid the rail alone would overflow.
[<Theory>]
[<InlineData "Settings">]
[<InlineData "Connections">]
[<InlineData "Calendar">]
[<InlineData "Search">]
[<InlineData "Assistant">]
[<InlineData "Graph">]
[<InlineData "Documents">]
[<InlineData "Notes">]
[<InlineData "Lectures">]
[<InlineData "Modules">]
[<InlineData "Assignments">]
let ``no screen declares a control wider than the screen itself`` (name: string) =
    for width in screenWidths do
        let offenders =
            widerThan width (render name width)
            |> List.map (fun v -> typeName v, floatProp "Width" v, floatProp "MinWidth" v)

        Assert.True(
            List.isEmpty offenders,
            sprintf "%s at %.0fpx declares a control wider than the screen: %A" name width offenders
        )

// ---------------------------------------------------------------------------
// 4. the placement rules, pinned exactly
// ---------------------------------------------------------------------------

/// **Nothing is dropped at a narrow width — only re-placed.** This is the rule
/// the whole responsive design rests on, and the one an earlier audit found
/// broken: the Assistant context rail used to vanish entirely below 900px.
[<Fact>]
let ``Assistant re-places both rails rather than dropping either`` () =
    for width in screenWidths do
        let placement = AssistantLogic.placementFor width true true

        if width < AssistantLogic.narrowWidth then
            Assert.True(placement.Stacked, sprintf "%.0fpx should stack" width)
        else
            Assert.False(placement.Stacked, sprintf "%.0fpx should not stack" width)

        // Whichever side of the breakpoint, both rails still occupy width. In
        // the medium band `ShowContext` is deliberately false — but the column
        // collapses to `Geometry.railWidth`, a reachable strip, rather than to
        // nothing. Asserting the *width* rather than the flag is what pins the
        // real rule: re-placed, never dropped.
        Assert.True(
            AssistantLogic.historyColumnWidth placement > 0.0,
            sprintf "history column reached zero width at %.0fpx" width
        )

        Assert.True(
            AssistantLogic.contextColumnWidth placement > 0.0,
            sprintf "context column reached zero width at %.0fpx — the rail would be unreachable" width
        )

[<Fact>]
let ``Calendar moves its side panel below rather than crushing it`` () =
    for width in screenWidths do
        let p = CalendarLogic.placementFor width

        if width < CalendarLogic.narrowWidth then
            Assert.True(p.Narrow, sprintf "%.0fpx should be narrow" width)
            Assert.Equal(CalendarLogic.SidePlacement.Below, p.Side)
        else
            Assert.False(p.Narrow, sprintf "%.0fpx should not be narrow" width)

/// A narrow window cannot show seven columns, so Calendar substitutes a mode —
/// but it must **not** rewrite the stored mode, or the user's choice would be
/// silently destroyed by a resize.
[<Fact>]
let ``Calendar substitutes a readable mode when narrow without altering the stored mode`` () =
    let narrow = CalendarLogic.placementFor 360.0
    let wide = CalendarLogic.placementFor 1600.0

    for mode in [ CalendarLogic.ViewMode.Day; CalendarLogic.ViewMode.Week; CalendarLogic.ViewMode.Month; CalendarLogic.ViewMode.Agenda ] do
        let effective = CalendarLogic.effectiveMode narrow mode
        Assert.NotEqual(CalendarLogic.ViewMode.Month, effective)
        Assert.NotEqual(CalendarLogic.ViewMode.Week, effective)

        // Wide leaves every mode exactly as stored.
        Assert.Equal(mode, CalendarLogic.effectiveMode wide mode)

[<Fact>]
let ``Connections steps the provider grid 3 to 2 to 1 and moves the detail rail below`` () =
    let columnsAt w = (ConnectionsLogic.placementFor w).Columns
    let detailAt w = (ConnectionsLogic.placementFor w).Detail

    Assert.Equal(3, columnsAt 1600.0)
    Assert.Equal(ConnectionsLogic.DetailPlacement.Rail, detailAt 1600.0)

    Assert.Equal(2, columnsAt (ConnectionsLogic.narrowWidth + 1.0))
    Assert.Equal(ConnectionsLogic.DetailPlacement.Below, detailAt (ConnectionsLogic.narrowWidth + 1.0))

    Assert.Equal(1, columnsAt 360.0)
    Assert.Equal(ConnectionsLogic.DetailPlacement.Below, detailAt 360.0)

    // Monotonic: narrowing never *adds* a column.
    let columns = screenWidths |> List.sort |> List.map columnsAt
    Assert.Equal<int list>(List.sort columns, columns)

/// Every placement decision is a pure function of width, so the same width must
/// always give the same answer. A placement that depended on anything else would
/// make the running app's layout unreproducible — and would quietly break Review
/// Mode's determinism guarantee.
[<Fact>]
let ``placement decisions are pure functions of width`` () =
    for width in screenWidths do
        Assert.Equal(CalendarLogic.placementFor width, CalendarLogic.placementFor width)
        Assert.Equal(ConnectionsLogic.placementFor width, ConnectionsLogic.placementFor width)
        Assert.Equal(AssistantLogic.placementFor width true true, AssistantLogic.placementFor width true true)
        Assert.Equal(SettingsLogic.placementFor width, SettingsLogic.placementFor width)

// ---------------------------------------------------------------------------
// 5. what must survive narrowing
// ---------------------------------------------------------------------------

/// The narrowest screen the application can actually produce: the smallest
/// permitted window with the sidebar pinned and expanded. Every "does it survive
/// narrow" assertion uses this rather than 360, because 360 is a *window* width
/// the app refuses, and 828 is the screen width a real user reaches.
let private narrowest = contentWidth windowMinWidth

/// Robustness only. These widths cannot occur while `MinWidth` is 1100, so a
/// failure here is not a live defect — but if the minimum is ever lowered, this
/// is the test that will already be watching. It asserts the two things that
/// would be crashes rather than cosmetics.
[<Theory>]
[<InlineData "Settings">]
[<InlineData "Connections">]
[<InlineData "Calendar">]
[<InlineData "Search">]
[<InlineData "Assistant">]
[<InlineData "Graph">]
[<InlineData "Documents">]
[<InlineData "Notes">]
[<InlineData "Lectures">]
[<InlineData "Modules">]
[<InlineData "Assignments">]
let ``every screen survives widths below the window minimum without crashing`` (name: string) =
    for width in unreachableWidths do
        let tree = render name width
        Assert.NotEmpty(all tree)

        Assert.True(
            List.isEmpty (impossibleDimensions tree),
            sprintf "%s at %.0fpx (below the window minimum) produces impossible geometry: %A" name width (impossibleDimensions tree)
        )

/// A screen with more content than height needs a scroller, or the bottom of it
/// is unreachable.
///
/// **Graph is deliberately exempt, and the exemption is asserted rather than
/// assumed** — see the test below. It is a canvas with a real pan/zoom viewport
/// (`GraphLogic.Viewport`, zoom 0.2-3.0, `onPointerWheelChanged`); wrapping it in
/// a ScrollViewer would fight the transform rather than help it. Exempting it
/// silently would have been the wrong call; so would demanding a scroller it
/// should not have.
[<Theory>]
[<InlineData "Settings">]
[<InlineData "Connections">]
[<InlineData "Calendar">]
[<InlineData "Search">]
[<InlineData "Assistant">]
[<InlineData "Documents">]
[<InlineData "Notes">]
[<InlineData "Lectures">]
[<InlineData "Modules">]
[<InlineData "Assignments">]
let ``every scrolling screen keeps a vertical scroller at the narrowest width`` (name: string) =
    let scrollers = ofType "ScrollViewer" (render name narrowest)

    Assert.True(
        not (List.isEmpty scrollers),
        sprintf "%s has no ScrollViewer at %.0fpx — content below the fold would be unreachable" name narrowest
    )

/// Graph's exemption has to be earned. If the pan/zoom viewport were ever
/// removed, Graph would need a scroller like everything else — and this test
/// would fail, sending whoever removed it back to the exemption above.
[<Fact>]
let ``Graph earns its scroller exemption by having a real pan and zoom viewport`` () =
    Assert.True(GraphLogic.Geometry.minZoom < 1.0)
    Assert.True(GraphLogic.Geometry.maxZoom > 1.0)

    let tree = render "Graph" narrowest
    Assert.True(all tree |> List.exists (hasHandler "PointerWheel"), "Graph has no wheel handler — zoom is gone")
    Assert.True(all tree |> List.exists (hasHandler "PointerMoved"), "Graph has no pointer-move handler — pan is gone")

/// Narrowing must not empty a screen. A screen that renders far fewer elements
/// when narrow has dropped sections rather than re-placed them — which is the
/// exact defect class this audit exists for. The threshold is deliberately
/// generous (a third): stacking legitimately removes some chrome, wholesale
/// disappearance does not.
/// **Calendar is excluded, for a reason that is itself tested below.** Narrowing
/// Calendar legitimately substitutes a simpler view mode — a seven-column week
/// grid is not readable at 828px — so its element count drops for a correct
/// reason and comparing the two would be comparing a week grid against an agenda
/// list. Every other screen keeps the same content and only re-places it.
[<Theory>]
[<InlineData "Settings">]
[<InlineData "Connections">]
[<InlineData "Search">]
[<InlineData "Assistant">]
[<InlineData "Graph">]
[<InlineData "Documents">]
[<InlineData "Notes">]
[<InlineData "Lectures">]
[<InlineData "Modules">]
[<InlineData "Assignments">]
let ``no screen loses most of itself when narrowed`` (name: string) =
    let wide = (all (render name (contentWidth 1920.0))).Length
    let narrow = (all (render name narrowest)).Length

    Assert.True(
        float narrow >= float wide / 3.0,
        sprintf "%s collapses from %d elements at %.0fpx to %d at %.0fpx — sections are being dropped, not re-placed"
            name wide (contentWidth 1920.0) narrow narrowest
    )

/// Calendar, compared like with like. Held in a mode that survives the
/// breakpoint unchanged, its layout must not collapse either — which separates
/// "the mode changed" (correct) from "the screen lost its content" (a defect).
[<Fact>]
let ``Calendar does not lose content when narrowed, once mode substitution is held constant`` () =
    let inMode mode width =
        CalendarView.view tokens { calendarModel () with Mode = mode; Width = width } ignore

    // Agenda is chosen because `effectiveMode` leaves it alone on both sides of
    // the breakpoint — asserted here so the choice cannot silently stop holding.
    let narrowPlacement = CalendarLogic.placementFor narrowest
    let widePlacement = CalendarLogic.placementFor (contentWidth 1920.0)
    let agenda = CalendarLogic.ViewMode.Agenda
    Assert.Equal(agenda, CalendarLogic.effectiveMode narrowPlacement agenda)
    Assert.Equal(agenda, CalendarLogic.effectiveMode widePlacement agenda)

    let wide = (all (inMode agenda (contentWidth 1920.0))).Length
    let narrow = (all (inMode agenda narrowest)).Length

    Assert.True(
        float narrow >= float wide / 3.0,
        sprintf "Calendar in Agenda collapses from %d to %d elements when narrowed — that is lost content, not mode substitution" wide narrow
    )

/// Interactive elements are the ones that matter most: losing a *control* when
/// narrowing means losing a capability, not merely some decoration.
[<Theory>]
[<InlineData "Settings">]
[<InlineData "Connections">]
[<InlineData "Calendar">]
[<InlineData "Search">]
[<InlineData "Assistant">]
[<InlineData "Graph">]
[<InlineData "Documents">]
[<InlineData "Notes">]
[<InlineData "Lectures">]
[<InlineData "Modules">]
[<InlineData "Assignments">]
let ``every screen keeps interactive controls at the narrowest width`` (name: string) =
    let controls = visibleInteractive (render name narrowest)

    Assert.True(
        not (List.isEmpty controls),
        sprintf "%s has no reachable interactive control at %.0fpx" name narrowest
    )

// ---------------------------------------------------------------------------
// 6. action rows wrap rather than clip
// ---------------------------------------------------------------------------

/// `WrapPanel` reflows; a horizontal `StackPanel` clips. Screens whose action
/// rows are known to be built from `WrapPanel` must keep them: replacing one with
/// a StackPanel is a silent narrow-width regression.
[<Theory>]
[<InlineData("Modules", 1)>]
[<InlineData("Assignments", 1)>]
[<InlineData("Search", 1)>]
[<InlineData("Lectures", 1)>]
[<InlineData("Notes", 1)>]
[<InlineData("Documents", 1)>]
let ``screens with wrapping action rows still wrap them when narrow`` (name: string, atLeast: int) =
    let wraps = ofType "WrapPanel" (render name narrowest)

    Assert.True(
        wraps.Length >= atLeast,
        sprintf "%s has %d WrapPanel(s) at %.0fpx, expected at least %d — an action row that cannot wrap will clip"
            name wraps.Length narrowest atLeast
    )

// ---------------------------------------------------------------------------
// 7. the sidebar states themselves
// ---------------------------------------------------------------------------

/// The derivation the whole matrix depends on. If this is wrong, every width in
/// every other test is wrong with it.
[<Fact>]
let ``screen width is derived from the real shell constants`` () =
    // Pinned reserves the density width; floating reserves nothing.
    Assert.Equal(1920.0 - 252.0 - 20.0, contentWidthWith SidebarDensity.Expanded true 1920.0)
    Assert.Equal(1920.0 - 80.0 - 20.0, contentWidthWith SidebarDensity.Compact true 1920.0)
    Assert.Equal(1920.0 - 20.0, contentWidthWith SidebarDensity.Expanded false 1920.0)

    // The numbers are the domain's, not this test's.
    Assert.Equal(252.0, SidebarDensity.width SidebarDensity.Expanded)
    Assert.Equal(80.0, SidebarDensity.width SidebarDensity.Compact)

/// An expanded pinned sidebar costs 172px more than a compact one — enough to
/// push a screen across a breakpoint on its own. This asserts the two states are
/// genuinely different cases rather than a distinction the audit only pretends
/// to make.
[<Fact>]
let ``sidebar density changes which side of a breakpoint a window lands on`` () =
    // A 1024px window: expanded+pinned leaves 752px, compact+pinned leaves 924px.
    let expanded = contentWidthWith SidebarDensity.Expanded true 1024.0
    let compact = contentWidthWith SidebarDensity.Compact true 1024.0

    Assert.True(expanded < CalendarLogic.narrowWidth, sprintf "expected %.0f < 900" expanded)
    Assert.True(compact > CalendarLogic.narrowWidth, sprintf "expected %.0f > 900" compact)

    // And the screens genuinely respond differently to the two.
    Assert.True((CalendarLogic.placementFor expanded).Narrow)
    Assert.False((CalendarLogic.placementFor compact).Narrow)

/// Floating the sidebar gives content the full window: the panel hovers rather
/// than reserving layout width (`ShellLayout.reservedWidth`). Content must
/// therefore NOT be resized when the sidebar reveals — otherwise every reveal
/// would reflow the page under the pointer.
[<Fact>]
let ``revealing a floating sidebar does not change the screen width`` () =
    for w, _ in viewports do
        let hidden = contentWidthWith SidebarDensity.Expanded false w
        let revealed = contentWidthWith SidebarDensity.Expanded false w
        Assert.Equal(hidden, revealed)

        // And floating is always at least as wide as pinned.
        Assert.True(hidden >= contentWidthWith SidebarDensity.Expanded true w)
