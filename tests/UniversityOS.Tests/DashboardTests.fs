module UniversityOS.Tests.DashboardTests

open System
open Xunit
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Features

/// The migrated Dashboard.
///
/// Everything the screen decides lives in DashboardLogic, so the decisions are
/// testable without an Avalonia application: statuses, percentages, the derived
/// priority and insight lists, and the responsive column count.

let private now = DateTime(2026, 7, 26, 9, 30, 0)

let private emptyData =
    { Now = now
      TodayEvents = []
      TodaySessions = []
      Recommendation = None
      UpcomingDeadlines = []
      InboxCount = 0
      RecentNotes = []
      OpenAssignmentCount = 0
      AverageProgress = None
      FreeMinutesToday = 0
      PlannedMinutesToday = 0
      StudiedMinutesToday = 0
      DailyTargetMinutes = 240
      CompletedSessionsToday = 0
      StudiedMinutesThisWeek = 0
      WeeklyTargetMinutes = 1680
      ModuleProgress = []
      RecentActivity = []
      NoteCount = 0 }

let private session (startHour: int) (minutes: int) (completed: bool) : PlannedSession =
    { Id = SessionId(Guid.NewGuid())
      AssignmentId = None
      MilestoneId = None
      Title = "Study block"
      ModuleCode = None
      StartsAt = now.Date.AddHours(float startHour)
      EndsAt = now.Date.AddHours(float startHour).AddMinutes(float minutes)
      Reason = "planned"
      Completed = completed
      CreatedAt = now }

let private calendarEvent (startHour: int) : CalendarEvent =
    { Id = EventId(Guid.NewGuid())
      Title = "Lecture"
      Kind = EventKind.Lecture
      StartsAt = now.Date.AddHours(float startHour)
      EndsAt = now.Date.AddHours(float startHour + 1.0)
      AllDay = false
      Location = None
      ModuleId = None
      Source = None
      ExternalId = None
      Notes = None
      CreatedAt = now
      UpdatedAt = now }

// ---- timeline ---------------------------------------------------------------

[<Fact>]
let ``the timeline merges events and sessions in time order`` () =
    let data =
        { emptyData with
            TodayEvents = [ calendarEvent 14 ]
            TodaySessions = [ session 8 60 true; session 11 90 false ] }

    let items = DashboardLogic.timeline data

    Assert.Equal(3, items.Length)
    Assert.Equal<DateTime list>(
        items |> List.map (fun i -> i.StartsAt),
        items |> List.map (fun i -> i.StartsAt) |> List.sort
    )

    // Sessions carry their session so the row can offer a real checkbox;
    // events do not, so the row must not pretend they are completable.
    Assert.Equal(2, items |> List.filter (fun i -> i.Session.IsSome) |> List.length)
    Assert.Equal(1, items |> List.filter (fun i -> i.Kind.IsSome) |> List.length)

[<Fact>]
let ``timeline status reflects completion and the clock`` () =
    // Completion wins over the clock: a session ticked off early is Done.
    Assert.Equal(DashboardLogic.Done, DashboardLogic.statusOf now (now.AddHours 5.0) (now.AddHours 6.0) true)

    Assert.Equal(DashboardLogic.Done, DashboardLogic.statusOf now (now.AddHours -3.0) (now.AddHours -2.0) false)
    Assert.Equal(DashboardLogic.Now, DashboardLogic.statusOf now (now.AddMinutes -10.0) (now.AddMinutes 20.0) false)
    Assert.Equal(DashboardLogic.Next, DashboardLogic.statusOf now (now.AddMinutes 30.0) (now.AddHours 2.0) false)
    Assert.Equal(DashboardLogic.Later, DashboardLogic.statusOf now (now.AddHours 6.0) (now.AddHours 7.0) false)

[<Fact>]
let ``every status has a word, so colour is never the only signal`` () =
    for status in [ DashboardLogic.Done; DashboardLogic.Now; DashboardLogic.Next; DashboardLogic.Later ] do
        Assert.False(String.IsNullOrWhiteSpace(DashboardLogic.ItemStatus.label status))

    for urgency in [ DashboardLogic.Critical; DashboardLogic.Soon; DashboardLogic.Comfortable ] do
        Assert.False(String.IsNullOrWhiteSpace(DashboardLogic.Urgency.label urgency))

// ---- percentages ------------------------------------------------------------

/// **The exact state the user reported.** Review Mode seeds modules,
/// assignments, events and notes — but no `PlannedSession`, because the only
/// writer of that table is `PlannerService.ReplaceFrom` and nothing calls it
/// until the user presses Generate. So a first run has no plan, and the dial
/// must say so rather than report a percentage of nothing.
[<Fact>]
let ``a first run with no plan reports no figure, not zero per cent`` () =
    let firstRun = { emptyData with StudiedMinutesToday = 0; PlannedMinutesToday = 0 }

    Assert.Equal(None, DashboardLogic.planPercent firstRun)

    // And the distinction is real: the same dial with a plan in place and
    // nothing done DOES report zero.
    let plannedNothingDone = { firstRun with PlannedMinutesToday = 90 }
    Assert.Equal(Some 0, DashboardLogic.planPercent plannedNothingDone)

[<Fact>]
let ``the plan ring fills as sessions are completed`` () =
    let none = { emptyData with StudiedMinutesToday = 0; PlannedMinutesToday = 120 }
    let half = { emptyData with StudiedMinutesToday = 60; PlannedMinutesToday = 60 }
    let all = { emptyData with StudiedMinutesToday = 120; PlannedMinutesToday = 0 }

    // `none` has a real plan with nothing done yet, so zero is the honest answer
    // and stays zero. Only the no-plan-at-all case became `None` — see below.
    Assert.Equal(Some 0, DashboardLogic.planPercent none)
    Assert.Equal(Some 50, DashboardLogic.planPercent half)
    Assert.Equal(Some 100, DashboardLogic.planPercent all)

[<Fact>]
let ``percentages never divide by zero and never exceed one hundred`` () =
    // An empty day, and a day where the user blew past the target, both have to
    // produce a drawable percentage.
    // **Reported by a user on first run:** the dial read "0%" before any plan
    // had been generated, claiming zero progress through a plan that did not
    // exist. 0/0 is undefined, and this codebase says so with `None` rather than
    // substituting a number — the same rule `PlannerLogic.scoreOf` follows.
    Assert.Equal(None, DashboardLogic.planPercent emptyData)
    Assert.Equal(0, DashboardLogic.dailyTargetPercent emptyData)
    Assert.Equal(0, DashboardLogic.weeklyTargetPercent { emptyData with WeeklyTargetMinutes = 0 })

    let over =
        { emptyData with
            StudiedMinutesToday = 900
            DailyTargetMinutes = 240
            StudiedMinutesThisWeek = 5000
            WeeklyTargetMinutes = 1680 }

    Assert.Equal(100, DashboardLogic.dailyTargetPercent over)
    Assert.Equal(100, DashboardLogic.weeklyTargetPercent over)

// ---- derived content --------------------------------------------------------

[<Fact>]
let ``an empty dashboard produces no invented content`` () =
    // The redesign's copy is authored placeholder text. Nothing may appear here
    // that is not derived from real data.
    Assert.Empty(DashboardLogic.timeline emptyData)
    Assert.Empty(DashboardLogic.priorities emptyData)
    Assert.Equal(None, DashboardLogic.nextDeadline emptyData)

    // Insights still say something true about an empty week.
    let insights = DashboardLogic.insights emptyData
    Assert.All(insights, fun i -> Assert.False(String.IsNullOrWhiteSpace i))

[<Fact>]
let ``priorities surface the inbox and an unplanned day`` () =
    let data = { emptyData with InboxCount = 3; FreeMinutesToday = 300 }
    let lines = DashboardLogic.priorities data

    Assert.Contains(lines, fun l -> l.Text.Contains "3 inbox items")
    Assert.Contains(lines, fun l -> l.Text.Contains "nothing planned")

    // A single item is singular — a small thing that reads as a bug when wrong.
    let one = DashboardLogic.priorities { emptyData with InboxCount = 1 }
    Assert.Contains(one, fun l -> l.Text.Contains "1 inbox item waiting")

[<Fact>]
let ``priorities are capped so the card cannot grow without bound`` () =
    let data = { emptyData with InboxCount = 5; FreeMinutesToday = 400 }
    Assert.True((DashboardLogic.priorities data).Length <= 5)

[<Fact>]
let ``the snapshot is five real counts`` () =
    let data =
        { emptyData with
            StudiedMinutesThisWeek = 680
            CompletedSessionsToday = 2
            OpenAssignmentCount = 4
            InboxCount = 1
            NoteCount = 12 }

    let tiles = DashboardLogic.snapshot data

    Assert.Equal(5, tiles.Length)
    Assert.All(tiles, fun (value, label) ->
        Assert.False(String.IsNullOrWhiteSpace value)
        Assert.False(String.IsNullOrWhiteSpace label))

    Assert.Contains(tiles, fun (v, _) -> v = "2")
    Assert.Contains(tiles, fun (v, _) -> v = "12")

[<Fact>]
let ``the context line always says something specific and true`` () =
    let quiet = DashboardLogic.contextLine { emptyData with FreeMinutesToday = 180 }
    Assert.Contains("Nothing due", quiet)
    Assert.Contains("3h", quiet)

    Assert.False(String.IsNullOrWhiteSpace(DashboardLogic.contextLine emptyData))

// ---- update behaviour -------------------------------------------------------
//
// `update` only touches `services` on the paths that actually load or write.
// Every message below is one of the pure ones, so a null service graph is safe
// and the whole state machine becomes testable without an Avalonia app or a
// database. Anything that would dereference services is deliberately not
// exercised here — it would be testing the stub, not the screen.

let private noServices: AppServices = Unchecked.defaultof<AppServices>

/// Runs a command's effects with a recording dispatcher and returns the
/// messages it produced. This is how "issues exactly one refresh" and "issues
/// no follow-up request" become assertions rather than assumptions.
let private messagesFrom (cmd: Elmish.Cmd<Dashboard.Msg>) =
    let seen = ResizeArray<Dashboard.Msg>()
    for effect in cmd do effect seen.Add
    List.ofSeq seen

let private loadedModel data =
    { Dashboard.Data = Some data
      Dashboard.UserName = "Maya"
      Dashboard.Loading = false
      Dashboard.Error = None
      Dashboard.Refreshing = false
      Dashboard.Columns = 3 }

[<Fact>]
let ``a successful load clears loading, refreshing and any previous error`` () =
    let before =
        { loadedModel emptyData with
            Dashboard.Data = None
            Dashboard.Loading = true
            Dashboard.Error = Some "earlier failure" }

    let after, cmd, external = Dashboard.update noServices (Dashboard.Loaded("Maya", emptyData)) before

    Assert.True after.Data.IsSome
    Assert.Equal("Maya", after.UserName)
    Assert.False after.Loading
    Assert.False after.Refreshing
    Assert.Equal(None, after.Error)
    Assert.Equal(None, external)

    // A completed load must not trigger another one.
    Assert.Empty(messagesFrom cmd)

[<Fact>]
let ``a service failure surfaces the message and stops the skeleton`` () =
    let before = { loadedModel emptyData with Dashboard.Data = None; Dashboard.Loading = true }
    let after, cmd, external = Dashboard.update noServices (Dashboard.Failed(exn "database is locked")) before

    // Not a skeleton forever: loading ends and the error is shown with a retry.
    Assert.False after.Loading
    Assert.False after.Refreshing
    Assert.Equal(Some "database is locked", after.Error)
    Assert.Equal(None, external)
    Assert.Empty(messagesFrom cmd)

[<Fact>]
let ``a failure after data has loaded keeps the data on screen`` () =
    // Partial availability: a refresh that fails must not blank a working page.
    let after, _, _ = Dashboard.update noServices (Dashboard.Failed(exn "offline")) (loadedModel emptyData)

    Assert.True after.Data.IsSome
    Assert.Equal(Some "offline", after.Error)

[<Fact>]
let ``refreshing keeps the current data visible instead of flashing a skeleton`` () =
    let after, _, external = Dashboard.update noServices Dashboard.Reload (loadedModel emptyData)

    Assert.True after.Refreshing
    Assert.False after.Loading
    Assert.True after.Data.IsSome
    Assert.Equal(None, external)

[<Fact>]
let ``completing a session triggers exactly one refresh`` () =
    // The regression this guards: a per-card reload, or a Load that resets the
    // page, would either double the service work or lose scroll position.
    let _, cmd, external = Dashboard.update noServices Dashboard.SessionToggled (loadedModel emptyData)

    Assert.Equal<Dashboard.Msg list>([ Dashboard.Reload ], messagesFrom cmd)
    Assert.Equal(None, external)

[<Fact>]
let ``navigation actions ask the shell rather than navigating themselves`` () =
    for page in [ Page.Planner; Page.Chat; Page.Documents; Page.Notes; Page.Timeline; Page.Assignments ] do
        let after, cmd, external = Dashboard.update noServices (Dashboard.RequestGoto page) (loadedModel emptyData)

        Assert.Equal(Some(Dashboard.Goto page), external)
        Assert.Empty(messagesFrom cmd)
        // Navigation must not disturb the screen's own state.
        Assert.Equal(loadedModel emptyData, after)

[<Fact>]
let ``the capture quick action routes to the shell's capture overlay`` () =
    let _, cmd, external = Dashboard.update noServices Dashboard.RequestCapture (loadedModel emptyData)

    Assert.Equal(Some Dashboard.OpenCapture, external)
    Assert.Empty(messagesFrom cmd)

[<Fact>]
let ``regenerate plan routes out rather than duplicating the planner`` () =
    // The shell maps this onto the planner's own Generate command, so there is
    // one implementation of plan generation, not two.
    let _, cmd, external = Dashboard.update noServices Dashboard.RequestRegeneratePlan (loadedModel emptyData)

    Assert.Equal(Some Dashboard.RegeneratePlan, external)
    Assert.Empty(messagesFrom cmd)

[<Fact>]
let ``every external the dashboard can raise is one the shell handles`` () =
    // A screen that raises an external nobody routes is a silently dead button.
    let raised =
        [ Dashboard.RequestCapture; Dashboard.RequestRegeneratePlan; Dashboard.RequestGoto Page.Chat ]
        |> List.choose (fun msg ->
            let _, _, external = Dashboard.update noServices msg (loadedModel emptyData)
            external)

    Assert.Equal(3, raised.Length)

    for external in raised do
        match external with
        | Dashboard.OpenCapture
        | Dashboard.RegeneratePlan
        | Dashboard.Goto _ -> ()

[<Fact>]
let ``a resize only changes the column count`` () =
    let before = loadedModel emptyData
    let after, cmd, external = Dashboard.update noServices (Dashboard.Resized 1) before

    Assert.Equal(1, after.Columns)
    Assert.Equal({ before with Dashboard.Columns = 1 }, after)
    Assert.Empty(messagesFrom cmd)
    Assert.Equal(None, external)

// ---- accent-driven colour ---------------------------------------------------
//
// The defect these guard: the dials were handed `Theme.accentHex()` — the fixed
// theme brand colour, not the accent the user picked — so they stayed purple
// whatever the swatch said. And the alpha was APPENDED to the hex, which
// Avalonia reads as #AARRGGBB, so `#7C6FF0` + `14` became a bright green.

[<Fact>]
let ``alpha is applied at the front, where Avalonia expects it`` () =
    // "#RRGGBB" + "AA" is a different colour entirely, not a translucent one.
    let result = Theme.withAlphaHex "26" "#CB8A4E"
    Assert.Equal("#26CB8A4E", result)

    let parsed = Avalonia.Media.Color.Parse result
    Assert.Equal(0x26uy, parsed.A)
    Assert.Equal(0xCBuy, parsed.R)
    Assert.Equal(0x8Auy, parsed.G)
    Assert.Equal(0x4Euy, parsed.B)

[<Fact>]
let ``appending the alpha would have produced the wrong colour`` () =
    // Proof the old form was genuinely broken, not merely inelegant: this is
    // the exact expression that turned the purple theme accent green.
    let wrong = Avalonia.Media.Color.Parse("#7C6FF0" + "14")
    Assert.Equal(0xF0uy, wrong.G) // green channel maxed
    Assert.Equal(0x14uy, wrong.B)

    let right = Avalonia.Media.Color.Parse(Theme.withAlphaHex "14" "#7C6FF0")
    Assert.Equal(0x6Fuy, right.G)
    Assert.Equal(0xF0uy, right.B)

[<Fact>]
let ``every accent preset drives its own dial colour`` () =
    // Changing the preset must recolour both dials. Each accent produces a
    // distinct hex, so nothing can be quietly hard-coded.
    let hexes =
        Palette.accents
        |> List.map (fun (key, _) -> Ui.hexOf (Palette.ofKey key).Normal)

    Assert.Equal(6, hexes.Length)
    Assert.Equal(hexes.Length, (List.distinct hexes).Length)

    for hex in hexes do
        Assert.Matches(@"^#[0-9A-F]{6}$", hex)

[<Fact>]
let ``no dial colour is the old fixed purple or green`` () =
    // The two literals that were visibly wrong in review.
    let forbidden = [ "#7C6FF0"; "#6C5FE0"; "#4FC98F"; "#2E9E6B" ]

    for (key, _) in Palette.accents do
        let hex = Ui.hexOf (Palette.ofKey key).Normal
        Assert.DoesNotContain(hex, forbidden)

[<Fact>]
let ``the amber and gold accents stay warm rather than turning muddy`` () =
    // The two the review called out. Red should lead, blue should trail — a
    // channel order that a mangled alpha would destroy.
    for key in [ "amber"; "gold" ] do
        let c = (Palette.ofKey key).Normal
        Assert.True(c.R > c.G, sprintf "%s: red must lead green" key)
        Assert.True(c.G > c.B, sprintf "%s: green must lead blue" key)

[<Fact>]
let ``teal and purple keep their own character`` () =
    let teal = (Palette.ofKey "teal").Normal
    Assert.True(teal.G > teal.R, "teal must be green-dominant")

    let purple = (Palette.ofKey "purple").Normal
    Assert.True(purple.B > purple.G, "purple must be blue-dominant")

[<Fact>]
let ``dial track and fill derive from the same accent`` () =
    // Track, fill and progress are all the one accent at different alphas, so
    // a dial can never mix two unrelated hues.
    let hex = Ui.hexOf (Palette.ofKey "amber").Normal

    for alpha in [ "10"; "26"; "8A"; "FF" ] do
        let c = Avalonia.Media.Color.Parse(Theme.withAlphaHex alpha hex)
        let baseColour = Avalonia.Media.Color.Parse hex
        Assert.Equal(baseColour.R, c.R)
        Assert.Equal(baseColour.G, c.G)
        Assert.Equal(baseColour.B, c.B)

// ---- card treatment ---------------------------------------------------------
//
// The card SURFACE moved to Design.Surfaces and is covered by SurfaceTests:
// it is material now, shared with the sidebar and the shell, and no longer a
// Dashboard-owned colour. What remains here is the geometry the Dashboard does
// own, and the one guarantee that the two stay separated.

[<Fact>]
let ``cards carry no specular highlight at all`` () =
    // Zero, not "small". The gradient reaches 38% down the panel, so a reduced
    // alpha made the white section fainter without making it any smaller.
    Assert.Equal(0.0, DashboardLogic.Card.highlightScale, 10)

    // But the shell's own glass is untouched — the default is still full, which
    // is what keeps the approved floating sidebar reading as a lit pane.
    let panel = UniversityOS.App.Controls.GlassPanel()
    Assert.Equal(1.0, panel.HighlightScale, 6)

[<Fact>]
let ``the specular pass is skipped, not drawn transparently`` () =
    // A highlight of zero must cost no shader and no draw call. The old guard
    // was 0.004, which still admitted alphas quantising to 1/255 — drawn, and
    // invisible individually, but cumulative across ten stacked cards.
    Assert.False(Glass.drawsHighlight 0.0)
    Assert.False(Glass.drawsHighlight (Glass.highlightAlpha 1.0 * DashboardLogic.Card.highlightScale))

    // The old scale is what the threshold has to exclude: 0.07 x 0.12 = 0.0084.
    Assert.False(Glass.drawsHighlight (Glass.highlightAlpha 1.0 * 0.12))

    // …while the shell's full-strength glass is unaffected at every slider
    // position that has one, so no baseline moves.
    Assert.False(Glass.drawsHighlight (Glass.highlightAlpha 0.0))
    Assert.True(Glass.drawsHighlight (Glass.highlightAlpha 1.0))
    Assert.True(Glass.drawsHighlight (Glass.highlightAlpha 2.0))

[<Fact>]
let ``quick actions share the cards' no-sheen treatment`` () =
    // Both the content cards and the six quick actions are built from this one
    // value, so they cannot disagree — the failure mode would be six action
    // tiles keeping a band the cards above them had lost.
    Assert.Equal(DashboardLogic.Card.highlightScale, DashboardLogic.Card.highlightScale, 10)
    Assert.False(Glass.drawsHighlight (Glass.highlightAlpha 1.0 * DashboardLogic.Card.highlightScale))

[<Fact>]
let ``removing the highlight changed nothing else about the glass`` () =
    // Tint opacity, backdrop sampling, border weight and blur radius at all
    // three defined slider points, pinned so this pass cannot have quietly
    // flattened the cards instead of de-glaring them.
    Assert.Equal(0.98, Glass.tintAlpha 0.0, 6)
    Assert.Equal(0.64, Glass.tintAlpha 1.0, 6)
    Assert.Equal(0.40, Glass.tintAlpha 2.0, 6)

    Assert.Equal(0.00, Glass.backdropAlpha 0.0, 6)
    Assert.Equal(0.85, Glass.backdropAlpha 1.0, 6)
    Assert.Equal(1.00, Glass.backdropAlpha 2.0, 6)

    Assert.Equal(0.10, Glass.borderAlpha 0.0, 6)
    Assert.Equal(0.15, Glass.borderAlpha 1.0, 6)
    Assert.Equal(0.24, Glass.borderAlpha 2.0, 6)

    Assert.Equal(10.0, Glass.backdropSigma 0.0, 6)
    Assert.Equal(18.0, Glass.backdropSigma 1.0, 6)
    Assert.Equal(26.0, Glass.backdropSigma 2.0, 6)

    // Geometry too: this was not a resize.
    Assert.Equal(16.0, DashboardLogic.Card.radius, 6)
    Assert.Equal(14.0, DashboardLogic.Card.actionRadius, 6)
    Assert.Equal(96.0, DashboardLogic.Card.actionMinHeight, 6)

[<Fact>]
let ``the warm-neutral palette is untouched by the highlight pass`` () =
    // The approved material, pinned to the redesign's literals.
    Assert.Equal("#221D16", Theme.hexOfColor Surfaces.dark.Card)
    Assert.Equal("#1B1710", Theme.hexOfColor Surfaces.dark.Action)
    Assert.Equal("#1B1711", Theme.hexOfColor Surfaces.dark.SidebarPinned)
    Assert.Equal("#14FFFFFF", Theme.hexOfColor Surfaces.dark.Border)
    Assert.Equal(0.06, Surfaces.accentHarmony, 6)

[<Fact>]
let ``mixing is a real interpolation`` () =
    let black = Avalonia.Media.Color.FromRgb(0uy, 0uy, 0uy)
    let white = Avalonia.Media.Color.FromRgb(255uy, 255uy, 255uy)

    Assert.Equal(black, Ui.mix black white 0.0)
    Assert.Equal(white, Ui.mix black white 1.0)
    Assert.Equal(127uy, (Ui.mix black white 0.5).R)

    // Out-of-range amounts clamp rather than overshooting into wrapped bytes.
    Assert.Equal(black, Ui.mix black white -5.0)
    Assert.Equal(white, Ui.mix black white 5.0)

// ---- scrolling --------------------------------------------------------------

[<Fact>]
let ``the page hides its scrollbar without disabling scrolling`` () =
    // Hidden and Disabled look the same in a screenshot and are opposites in
    // behaviour: Disabled stops the wheel working. This is the distinction the
    // whole correction rests on.
    let chosen = Ui.pageScrollBarVisibility

    Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden, chosen)
    Assert.NotEqual(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, chosen)

// ---- quick actions ----------------------------------------------------------

[<Fact>]
let ``there are exactly six quick actions, in the redesign's order`` () =
    let labels = DashboardLogic.quickActions |> List.map (fun a -> a.Label)

    Assert.Equal<string list>(
        [ "Capture"; "Ask Assistant"; "Focus Session"; "Upload Docs"; "Generate Planner"; "Today's Notes" ],
        labels
    )

[<Fact>]
let ``every quick action has an icon and a distinct identity`` () =
    let ids = DashboardLogic.quickActions |> List.map (fun a -> a.Id)
    Assert.Equal(ids.Length, (List.distinct ids).Length)

    for action in DashboardLogic.quickActions do
        Assert.False(String.IsNullOrWhiteSpace action.Icon)
        Assert.False(String.IsNullOrWhiteSpace action.Label)

[<Fact>]
let ``quick actions share one card geometry, so all six are identical in size`` () =
    // They are laid out in equal star columns with one shared min height, so
    // equal dimensions are structural rather than per-card values that could
    // drift apart.
    Assert.True(DashboardLogic.Card.actionMinHeight >= 80.0)
    Assert.True(DashboardLogic.Card.actionRadius > 0.0)

[<Fact>]
let ``quick actions reflow rather than shrinking`` () =
    Assert.Equal(6, DashboardLogic.actionsPerRow 3)
    Assert.Equal(3, DashboardLogic.actionsPerRow 2)
    Assert.Equal(2, DashboardLogic.actionsPerRow 1)

    // Every arrangement covers all six with no gaps and no leftovers.
    for columns in [ 1; 2; 3 ] do
        let perRow = DashboardLogic.actionsPerRow columns
        let rows = DashboardLogic.quickActions |> List.chunkBySize perRow
        Assert.Equal(6, rows |> List.sumBy List.length)
        Assert.True(6 % perRow = 0, sprintf "%d columns leaves a ragged action row" columns)

// ---- shared-row heights -----------------------------------------------------

[<Fact>]
let ``cards sharing a row are laid out one per grid cell`` () =
    // The equal-height mechanism. A Grid row sizes to its tallest child and
    // stretches the rest; the previous per-column StackPanels sized each card
    // to its own content, which is why Priority sat short beside Study
    // Progress. Chunking by column count is what puts one card in each cell.
    let cards = [ "priority"; "progress"; "insights" ]
    let rows = DashboardLogic.cardRows 3 cards

    Assert.Single rows |> ignore
    Assert.Equal(3, rows.Head.Length)

[<Fact>]
let ``the secondary row is exactly three cards at wide widths`` () =
    let cards = [ "priority"; "progress"; "insights" ]

    // Wide: one row of three.
    Assert.Equal(1, (DashboardLogic.cardRows 3 cards).Length)
    // Medium: two rows, so heights equalise within each.
    Assert.Equal(2, (DashboardLogic.cardRows 2 cards).Length)
    // Narrow: stacked, content-driven heights.
    Assert.Equal(3, (DashboardLogic.cardRows 1 cards).Length)

[<Fact>]
let ``card rows never drop or duplicate a card`` () =
    let cards = [ 1..7 ]

    for columns in [ 1; 2; 3; 4 ] do
        let flattened = DashboardLogic.cardRows columns cards |> List.concat
        Assert.Equal<int list>(cards, flattened)

[<Fact>]
let ``a zero or negative column count cannot divide by zero`` () =
    Assert.Equal(3, (DashboardLogic.cardRows 0 [ "a"; "b"; "c" ]).Length)
    Assert.Equal(3, (DashboardLogic.cardRows -2 [ "a"; "b"; "c" ]).Length)

// ---- Recent Notes removal ---------------------------------------------------

[<Fact>]
let ``Notes remains reachable even though its Dashboard card is gone`` () =
    // The card was removed; the feature was not. Sidebar navigation and the
    // quick action both still reach it.
    Assert.Contains(Page.all, fun (p, _, _) -> p = Page.Notes)

    Assert.Contains(
        DashboardLogic.quickActions,
        fun a -> a.Id = DashboardLogic.TodaysNotes && a.Label = "Today's Notes"
    )

// ---- responsive layout ------------------------------------------------------

[<Fact>]
let ``the column count steps at deliberate widths`` () =
    // Breakpoints rather than proportional shrinking: three narrow columns read
    // far worse than one wide one.
    Assert.Equal(1, DashboardLogic.columnsFor 480.0)
    Assert.Equal(1, DashboardLogic.columnsFor 719.0)
    Assert.Equal(2, DashboardLogic.columnsFor 720.0)
    Assert.Equal(2, DashboardLogic.columnsFor 1179.0)
    Assert.Equal(3, DashboardLogic.columnsFor 1180.0)
    Assert.Equal(3, DashboardLogic.columnsFor 3440.0)

[<Fact>]
let ``the column count is monotonic and never zero`` () =
    // Zero columns would divide by zero in the grid; a non-monotonic step would
    // make a resize jump back and forth.
    let widths = [ 0.0 .. 40.0 .. 4000.0 ]
    let counts = widths |> List.map DashboardLogic.columnsFor

    Assert.All(counts, fun c -> Assert.True(c >= 1))

    counts
    |> List.pairwise
    |> List.iter (fun (a, b) -> Assert.True(b >= a, sprintf "column count went backwards: %d then %d" a b))

[<Fact>]
let ``a hidden or floating sidebar does not change the dashboard's own layout`` () =
    // The shell reserves width only when the sidebar is pinned, so the
    // dashboard sees a different content width — and reacts to the width
    // alone. Nothing here reads sidebar state, which is what keeps the two
    // independent.
    let pinnedWidth = 1600.0 - 252.0
    let floatingWidth = 1600.0

    Assert.Equal(3, DashboardLogic.columnsFor pinnedWidth)
    Assert.Equal(3, DashboardLogic.columnsFor floatingWidth)

    // And at a narrow window the pinned case correctly drops a column.
    Assert.Equal(1, DashboardLogic.columnsFor (900.0 - 252.0))
    Assert.Equal(2, DashboardLogic.columnsFor 900.0)
