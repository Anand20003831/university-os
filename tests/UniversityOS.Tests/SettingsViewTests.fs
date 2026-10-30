module UniversityOS.Tests.SettingsViewTests

open System
open Xunit
open Avalonia.Media
open Avalonia.Controls.Primitives
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Features

/// The rebuilt Settings screen.
///
/// Most of these tests are absence tests. Settings is the screen with the most
/// ways to lie — a retired field, an orphaned toggle, a credential it cannot
/// see, an appearance store it must not write, and destructive operations that
/// do not exist.

let private accessorName (a: Accessor) =
    match a with
    | Accessor.InstanceProperty p -> p.Name
    | Accessor.AvaloniaProperty p -> p.Name

let private kids (view: IView) : IView list =
    view.Attrs
    |> List.collect (fun attr ->
        match attr.Content with
        | ValueSome content ->
            match content.Content with
            | ViewContent.Single(Some c) -> [ c ]
            | ViewContent.Single None -> []
            | ViewContent.Multiple cs -> cs
        | ValueNone -> [])

let rec private all (view: IView) : IView list = view :: (kids view |> List.collect all)

let private propOf (name: string) (view: IView) : obj option =
    view.Attrs
    |> List.tryPick (fun attr ->
        match attr.Property with
        | ValueSome p when accessorName p.Accessor = name -> Some p.Value
        | _ -> None)

let private stringProp name view =
    match propOf name view with
    | Some(:? string as s) -> Some s
    | _ -> None

let private boolProp name view =
    match propOf name view with
    | Some(:? bool as b) -> Some b
    | _ -> None

let private ofType (name: string) (root: IView) =
    all root |> List.filter (fun v -> v.ViewType.Name = name)

let private everyString (root: IView) =
    all root
    |> List.collect (fun v ->
        [ stringProp "Text" v; stringProp "Content" v; stringProp "Watermark" v; stringProp "Name" v ]
        |> List.choose id)

let private words (root: IView) =
    all root |> List.collect (fun v -> [ stringProp "Text" v; stringProp "Content" v ] |> List.choose id)

let private says (needle: string) (root: IView) =
    words root |> List.exists (fun s -> s.Contains(needle, StringComparison.Ordinal))

let private accessibleNames (root: IView) = all root |> List.choose (stringProp "Name")

let private tokens = Tokens.ofAppearance true 0 AppearanceSettings.defaults
let private noServices: AppServices = Unchecked.defaultof<AppServices>

let private modelOf (m, _, _) = m
let private cmdOf (_, c, _) = c
let private externalOf (_, _, e) = e

let private now = DateTime(2026, 8, 2, 10, 0, 0)

let private ready () : SettingsView.Model =
    { Stored = UserSettings.defaults
      Draft = UserSettings.defaults
      Category = SettingsLogic.Category.Profile
      SaveState = SettingsLogic.SaveState.Idle
      Saving = false
      Loading = false
      Error = None
      Memories = []
      MemoryEnabled = true
      NewMemoryText = ""
      MemoryBusy = None
      MemoryNotice = None
      PendingDeleteMemory = None
      Width = 1600.0 }

let private at (c: SettingsLogic.Category) = { ready () with Category = c }

let private memory text : MemoryItem =
    { Id = Guid.NewGuid()
      Category = "user"
      Content = text
      Source = MemorySource.User
      Enabled = true
      UpdatedAt = now }

let private render (m: SettingsView.Model) = SettingsView.view tokens m ignore

/// Every category, rendered — the net most absence tests are cast over.
let private everywhere () = SettingsLogic.Category.all |> List.map (fun c -> render (at c))

// ---------------------------------------------------------------------------
// 1. composition
// ---------------------------------------------------------------------------

[<Fact>]
let ``the rail carries all eight categories, in order`` () =
    let root = render (ready ())

    let railNames =
        accessibleNames root |> List.filter (fun n -> n.EndsWith(" settings", StringComparison.Ordinal))

    Assert.Equal<string list>(
        SettingsLogic.Category.all |> List.map (fun c -> sprintf "%s settings" (SettingsLogic.Category.label c)),
        railNames
    )

    Assert.Equal(8, railNames.Length)

[<Fact>]
let ``the header is the redesign's own wording`` () =
    let root = render (ready ())
    Assert.True(says "Control Center" root)
    Assert.True(says "Configure University OS to work the way you do." root)

[<Fact>]
let ``the selected category names itself and says what it is for`` () =
    for c in SettingsLogic.Category.all do
        let root = render (at c)
        Assert.True(says (SettingsLogic.Category.label c) root, string c)
        Assert.True(says (SettingsLogic.categoryCaption c) root, string c)

[<Fact>]
let ``switching category keeps the draft`` () =
    let edited = SettingsView.update noServices (SettingsView.SetName "Maya") (ready ()) |> modelOf

    let switched =
        SettingsView.update noServices (SettingsView.SelectCategory SettingsLogic.Category.Planner) edited |> modelOf

    Assert.Equal("Maya", switched.Draft.UserName)
    Assert.Equal(SettingsLogic.Category.Planner, switched.Category)

// ---------------------------------------------------------------------------
// 2. absence — the things that must never render
// ---------------------------------------------------------------------------

[<Fact>]
let ``MUTATION the retired theme field appears nowhere`` () =
    for root in everywhere () do
        let shown = String.concat " " (everyString root) |> fun s -> s.ToLowerInvariant()

        for absent in [ "dark mode"; "light mode"; "colour theme"; "color theme" ] do
            Assert.DoesNotContain(absent, shown)

    // And no edit can touch it.
    let edited = SettingsView.update noServices (SettingsView.SetName "Maya") (ready ()) |> modelOf
    Assert.Equal(UserSettings.defaults.Theme, edited.Draft.Theme)

[<Fact>]
let ``MUTATION there is no notifications toggle`` () =
    let root = render (at SettingsLogic.Category.Notifications)

    Assert.True(says SettingsLogic.notificationsNotice root)

    // No control of any kind in the panel — the category is a statement, not a
    // form. (The rail's own eight buttons are part of the page, not the panel.)
    Assert.Empty(ofType "CheckBox" root)
    Assert.Empty(ofType "TextBox" root)

    let panelButtons =
        accessibleNames root
        |> List.filter (fun n -> not (n.EndsWith(" settings", StringComparison.Ordinal)))

    Assert.Empty panelButtons

[<Fact>]
let ``MUTATION no secret input exists on any category`` () =
    // Saying WHERE the key is not is honest; offering a box for one is not.
    for c in SettingsLogic.Category.all do
        let root = render (at c)

        Assert.Empty(ofType "TextBox" root |> List.filter (fun b -> propOf "PasswordChar" b |> Option.isSome))

        for name in accessibleNames root do
            let n = name.ToLowerInvariant()

            for absent in [ "api key"; "access token"; "client secret"; "token" ] do
                Assert.DoesNotContain(absent, n)

[<Fact>]
let ``MUTATION no appearance control exists on any category`` () =
    // Naming a setting in a handoff explanation is not offering a control, so
    // this asserts the absence of CONTROLS, not of nouns.
    for c in SettingsLogic.Category.all do
        let root = render (at c)
        let names = accessibleNames root

        for absent in [ "Accent"; "Density"; "Corner radius"; "Glass intensity"; "Animation speed"; "Reduce motion" ] do
            Assert.DoesNotContain(absent, names)

[<Fact>]
let ``MUTATION no destructive or unsupported data operation appears`` () =
    // Connections legitimately mentions "import schedules"; what must not exist
    // is an ACTION for any of these.
    for c in SettingsLogic.Category.all do
        for name in accessibleNames (render (at c)) do
            let n = name.ToLowerInvariant()

            for absent in [ "export"; "import"; "backup"; "reset to default"; "clear data"; "delete database"; "clear history" ] do
                Assert.DoesNotContain(absent, n)

[<Fact>]
let ``no shell-owned or transient UiState field is exposed`` () =
    // "Sidebar density" appears as a SHORTCUT description (Ctrl+B), which is a
    // reference to shell behaviour, not a setting. So this checks controls.
    for c in SettingsLogic.Category.all do
        let names = accessibleNames (render (at c))

        for absent in [ "Sidebar density"; "Sidebar pinned"; "Last briefing"; "Last review" ] do
            Assert.DoesNotContain(absent, names)

[<Fact>]
let ``no shortcut is editable`` () =
    let root = render (at SettingsLogic.Category.Advanced)

    for chord, _ in SettingsLogic.shortcuts do
        Assert.True(says chord root, chord)

    // The only text boxes on Advanced would be shortcut editors, and there are
    // none.
    Assert.Empty(ofType "TextBox" root)
    Assert.True(says SettingsLogic.shortcutsNotice root)

// ---------------------------------------------------------------------------
// 3. the save lifecycle
// ---------------------------------------------------------------------------

[<Fact>]
let ``a freshly loaded screen is clean`` () =
    let loaded =
        SettingsView.update noServices (SettingsView.Loaded { UserSettings.defaults with UserName = "Maya" }) (ready ())
        |> modelOf

    Assert.False(SettingsLogic.isDirty loaded.Stored loaded.Draft)
    Assert.True(says "No changes to save." (render loaded))

[<Fact>]
let ``editing makes it dirty and Save becomes available`` () =
    let edited = SettingsView.update noServices (SettingsView.SetName "Maya") (ready ()) |> modelOf

    Assert.True(SettingsLogic.canSave false edited.Stored edited.Draft)
    Assert.False(says "No changes to save." (render edited))

[<Fact>]
let ``MUTATION an invalid draft cannot be saved, and says why`` () =
    let invalid =
        { at SettingsLogic.Category.Planner with
            Draft = { UserSettings.defaults with UserName = "Maya"; WorkdayEndHour = 1 } }

    Assert.True(List.isEmpty (cmdOf (SettingsView.update noServices SettingsView.Save invalid)))
    Assert.True(says "The study day must end after it starts." (render invalid))

[<Fact>]
let ``a second Save while saving does nothing`` () =
    let saving = { ready () with Draft = { UserSettings.defaults with UserName = "Maya" }; Saving = true }
    Assert.True(List.isEmpty (cmdOf (SettingsView.update noServices SettingsView.Save saving)))

[<Fact>]
let ``a successful save makes the form clean again`` () =
    let edited = SettingsView.update noServices (SettingsView.SetName "Maya") (ready ()) |> modelOf
    let saved = SettingsView.update noServices SettingsView.SavedOk edited |> modelOf

    Assert.False(SettingsLogic.isDirty saved.Stored saved.Draft)
    Assert.Equal("Maya", saved.Stored.UserName)
    Assert.True(says "Saved." (render saved))

[<Fact>]
let ``MUTATION a failed save preserves the draft`` () =
    let edited = SettingsView.update noServices (SettingsView.SetName "Maya") (ready ()) |> modelOf
    let failed = SettingsView.update noServices (SettingsView.SaveFailed "the disk is full") edited |> modelOf

    Assert.Equal("Maya", failed.Draft.UserName)
    Assert.True(SettingsLogic.isDirty failed.Stored failed.Draft)
    Assert.True(says "the disk is full" (render failed))

[<Fact>]
let ``MUTATION a handoff does not dirty the draft`` () =
    let result = SettingsView.update noServices (SettingsView.GotoPage Page.DesignPreview) (ready ())

    Assert.Equal(Some(SettingsView.Goto Page.DesignPreview), externalOf result)
    Assert.False(SettingsLogic.isDirty (modelOf result).Stored (modelOf result).Draft)

// ---------------------------------------------------------------------------
// 4. Planner
// ---------------------------------------------------------------------------

[<Fact>]
let ``the planner panel states what each setting changes`` () =
    let root = render (at SettingsLogic.Category.Planner)

    Assert.True(says "The most work the planner will schedule in one day" root)
    Assert.True(says "How far ahead the planner schedules." root)
    Assert.True(says "How long one focused block is." root)

[<Fact>]
let ``MUTATION the deadline buffer is never described as scheduling`` () =
    let root = render (at SettingsLogic.Category.Planner)

    Assert.True(says SettingsLogic.deadlineBufferLabel root)
    Assert.True(says SettingsLogic.deadlineBufferNotice root)
    Assert.True(says "tight against deadline" root)

    let shown = String.concat " " (everyString root)
    Assert.DoesNotContain("changes scheduling order", shown)
    Assert.DoesNotContain("affects capacity", shown)

[<Fact>]
let ``every planner edit reaches the draft`` () =
    let m = ready ()

    Assert.Equal(8, (SettingsView.update noServices (SettingsView.SetWorkStart 8) m |> modelOf).Draft.WorkdayStartHour)
    Assert.Equal(22, (SettingsView.update noServices (SettingsView.SetWorkEnd 22) m |> modelOf).Draft.WorkdayEndHour)
    Assert.Equal(45, (SettingsView.update noServices (SettingsView.SetSession 45) m |> modelOf).Draft.SessionMinutes)
    Assert.Equal(15, (SettingsView.update noServices (SettingsView.SetBreak 15) m |> modelOf).Draft.BreakMinutes)
    Assert.Equal(21, (SettingsView.update noServices (SettingsView.SetHorizon 21) m |> modelOf).Draft.PlannerHorizonDays)

// ---------------------------------------------------------------------------
// 5. AI
// ---------------------------------------------------------------------------

[<Fact>]
let ``the AI panel offers the two real non-secret settings`` () =
    let root = render (at SettingsLogic.Category.Ai)

    Assert.True(says "Model" root)
    Assert.True(says "Apply captured changes automatically" root)
    Assert.True(says SettingsLogic.autoApplyNotice root)

[<Fact>]
let ``auto-apply reaches the draft`` () =
    let toggled = SettingsView.update noServices SettingsView.ToggleAutoApply (ready ()) |> modelOf
    Assert.True(toggled.Draft.AiAutoApply)
    Assert.True(SettingsLogic.isDirty toggled.Stored toggled.Draft)

[<Fact>]
let ``the AI panel hands credentials to Connections and claims nothing about them`` () =
    let root = render (at SettingsLogic.Category.Ai)

    Assert.True(says SettingsLogic.credentialHandoffNotice root)
    Assert.True(says "Open Connections" root)

    let shown = String.concat " " (everyString root) |> fun s -> s.ToLowerInvariant()

    for absent in [ "key is set"; "no key configured"; "token usage"; "latency" ] do
        Assert.DoesNotContain(absent, shown)

// ---------------------------------------------------------------------------
// 6. AI memory — preserved from the legacy screen
// ---------------------------------------------------------------------------

[<Fact>]
let ``MUTATION the memory section exists and lists real fields`` () =
    let m = { at SettingsLogic.Category.Ai with Memories = [ memory "Prefers mornings" ] }
    let root = render m

    Assert.True(says "What the Assistant remembers".[0..10] root)
    Assert.True(says "Prefers mornings" root)
    Assert.True(says "Use long-term memory" root)

[<Fact>]
let ``an empty memory list says so`` () =
    Assert.True(says "Nothing is remembered yet." (render (at SettingsLogic.Category.Ai)))

[<Fact>]
let ``a long memory wraps`` () =
    let long = String.replicate 40 "a long remembered observation about how I work. "
    let m = { at SettingsLogic.Category.Ai with Memories = [ memory long ] }

    let blocks = all (render m) |> List.filter (fun v -> stringProp "Text" v = Some long)
    Assert.NotEmpty blocks
    Assert.All(blocks, fun b -> Assert.Equal(Some(box TextWrapping.Wrap), propOf "TextWrapping" b))

[<Fact>]
let ``an empty memory cannot be added`` () =
    let m = at SettingsLogic.Category.Ai
    Assert.True(List.isEmpty (cmdOf (SettingsView.update noServices SettingsView.AddMemory m)))
    Assert.True(List.isEmpty (cmdOf (SettingsView.update noServices SettingsView.AddMemory { m with NewMemoryText = "   " })))

[<Fact>]
let ``a second add while one is running does nothing`` () =
    let busy = { at SettingsLogic.Category.Ai with NewMemoryText = "remember this"; MemoryBusy = Some "add" }
    Assert.True(List.isEmpty (cmdOf (SettingsView.update noServices SettingsView.AddMemory busy)))

[<Fact>]
let ``MUTATION a failed add keeps the typed text`` () =
    let typed = { at SettingsLogic.Category.Ai with NewMemoryText = "remember this"; MemoryBusy = Some "add" }

    let failed =
        SettingsView.update noServices (SettingsView.AddMemoryFailed "the database is locked") typed |> modelOf

    Assert.Equal("remember this", failed.NewMemoryText)
    Assert.True(failed.MemoryBusy.IsNone)
    Assert.True(says "the database is locked" (render failed))

[<Fact>]
let ``only a confirmed round-trip clears the compose box`` () =
    let typed = { at SettingsLogic.Category.Ai with NewMemoryText = "remember this" }
    let stored = SettingsView.update noServices SettingsView.MemoryChanged typed |> modelOf

    Assert.Equal("", stored.NewMemoryText)

[<Fact>]
let ``deleting a memory asks first, and cancelling keeps it`` () =
    let item = memory "Prefers mornings"
    let m = { at SettingsLogic.Category.Ai with Memories = [ item ] }

    Assert.False(says "Delete this memory permanently?" (render m))

    let asked = SettingsView.update noServices (SettingsView.RequestDeleteMemory item.Id) m |> modelOf
    Assert.True(says "Delete this memory permanently?" (render asked))

    let cancelled = SettingsView.update noServices SettingsView.CancelDeleteMemory asked |> modelOf
    Assert.True(cancelled.PendingDeleteMemory.IsNone)
    Assert.True(says "Prefers mornings" (render cancelled))

[<Fact>]
let ``a delete removes the row only after the repository confirms`` () =
    let item = memory "Prefers mornings"
    let m = { at SettingsLogic.Category.Ai with Memories = [ item ]; PendingDeleteMemory = Some item.Id }

    let confirmed = SettingsView.update noServices (SettingsView.ConfirmDeleteMemory item.Id) m

    // Nothing is removed optimistically, so a failed delete cannot lose it.
    Assert.Equal(1, (modelOf confirmed).Memories.Length)
    Assert.False(List.isEmpty (cmdOf confirmed))

[<Fact>]
let ``disabling memory keeps every memory and says nothing was deleted`` () =
    let m =
        { at SettingsLogic.Category.Ai with Memories = [ memory "Prefers mornings" ]; MemoryEnabled = false }

    let root = render m

    Assert.True(says "Prefers mornings" root)
    Assert.True(says "Nothing has been deleted" root)
    Assert.True(says "will not use anything below" root)

// ---------------------------------------------------------------------------
// 7. handoffs and informational categories
// ---------------------------------------------------------------------------

[<Fact>]
let ``MUTATION appearance is a handoff and cannot be edited here`` () =
    let root = render (at SettingsLogic.Category.Appearance)

    Assert.True(says SettingsLogic.appearanceHandoff.Title root)
    Assert.True(says "Open Design Preview" root)
    Assert.True(says "Settings does not edit them" root)

    Assert.Empty(ofType "CheckBox" root)
    Assert.Empty(ofType "TextBox" root)

[<Fact>]
let ``every handoff navigates where it says`` () =
    for target in [ Page.DesignPreview; Page.Connections ] do
        let result = SettingsView.update noServices (SettingsView.GotoPage target) (ready ())
        Assert.Equal(Some(SettingsView.Goto target), externalOf result)

[<Fact>]
let ``integrations hands off without duplicating a connector form`` () =
    let root = render (at SettingsLogic.Category.Integrations)

    Assert.True(says "Open Connections" root)
    Assert.Empty(ofType "TextBox" root)

    let shown = String.concat " " (everyString root) |> fun s -> s.ToLowerInvariant()
    Assert.DoesNotContain("site url", shown)
    Assert.DoesNotContain("client id", shown)

[<Fact>]
let ``privacy states only facts this codebase supports`` () =
    let root = render (at SettingsLogic.Category.Privacy)

    for fact in SettingsLogic.privacyFacts do
        Assert.True(says fact root, fact)

    let shown = String.concat " " (everyString root) |> fun s -> s.ToLowerInvariant()

    for claim in SettingsLogic.forbiddenPrivacyClaims do
        Assert.DoesNotContain(claim.ToLowerInvariant(), shown)

// ---------------------------------------------------------------------------
// 8. states and responsive
// ---------------------------------------------------------------------------

[<Fact>]
let ``a load failure keeps the rail and the page structure`` () =
    let failed =
        SettingsView.update noServices (SettingsView.Failed(exn "the database is locked")) (ready ()) |> modelOf

    let root = render failed
    Assert.True(says "Could not load settings: the database is locked" root)
    Assert.True(says "Control Center" root)

    Assert.Equal(
        8,
        accessibleNames root |> List.filter (fun n -> n.EndsWith(" settings", StringComparison.Ordinal)) |> List.length
    )

[<Fact>]
let ``MUTATION the category rail is present at every width`` () =
    for width in [ 1600.0; 1000.0; 700.0 ] do
        let root = render { ready () with Width = width }

        let rail =
            accessibleNames root |> List.filter (fun n -> n.EndsWith(" settings", StringComparison.Ordinal))

        Assert.Equal(8, rail.Length)

[<Fact>]
let ``nothing scrolls sideways, at any width or category`` () =
    for width in [ 1600.0; 1000.0; 700.0 ] do
        for c in SettingsLogic.Category.all do
            for viewer in ofType "ScrollViewer" (render { at c with Width = width }) do
                Assert.Equal(Some(box ScrollBarVisibility.Disabled), propOf "HorizontalScrollBarVisibility" viewer)

// ---------------------------------------------------------------------------
// 9. Dark and Light, every accent
// ---------------------------------------------------------------------------

let private themeModes = [ ThemeMode.Dark; ThemeMode.Light ]
let private accentKeys = Palette.accents |> List.map fst

let private washedBy (alphaHex: string) (colour: Color) (surface: Color) =
    let a = Int32.Parse(alphaHex, Globalization.NumberStyles.HexNumber)
    Palette.composite surface (Color.FromArgb(byte a, colour.R, colour.G, colour.B))

[<Fact>]
let ``a selected category row is readable and distinguishable`` () =
    for mode in themeModes do
        for key in accentKeys do
            let accent = Palette.ofKey key
            let s = Surfaces.resolve mode accent
            let selected = washedBy SettingsLogic.Geometry.selectedFillAlpha accent.Normal s.Card

            Assert.True(Palette.contrastRatio selected s.Card > 1.0, sprintf "%A/%s invisible" mode key)

            let ink = Palette.readableOn selected s.Text accent.Normal

            Assert.True(
                Palette.contrastRatio ink selected >= Palette.aaText,
                sprintf "%A/%s selected row: %.2f" mode key (Palette.contrastRatio ink selected)
            )

[<Fact>]
let ``every notice is readable on its own wash`` () =
    for mode in themeModes do
        for key in accentKeys do
            let accent = Palette.ofKey key
            let s = Surfaces.resolve mode accent

            for name, raw in
                [ "success", s.Success; "danger", s.Danger; "warning", s.Warning; "accent", accent.Normal ] do
                let wash = washedBy SettingsLogic.Geometry.noticeFillAlpha raw s.Card

                Assert.True(
                    Palette.contrastRatio s.Text wash >= Palette.aaText,
                    sprintf "%A/%s %s notice: %.2f" mode key name (Palette.contrastRatio s.Text wash)
                )

[<Fact>]
let ``a selected option chip is readable`` () =
    for mode in themeModes do
        for key in accentKeys do
            let accent = Palette.ofKey key
            let s = Surfaces.resolve mode accent
            let wash = washedBy SettingsLogic.Geometry.chipFillAlpha accent.Normal s.Card
            let ink = Palette.readableOn wash s.Text accent.Normal

            Assert.True(
                Palette.contrastRatio ink wash >= Palette.aaText,
                sprintf "%A/%s chip: %.2f" mode key (Palette.contrastRatio ink wash)
            )

[<Fact>]
let ``the surfaces stay warm in both themes`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, c in [ "card", s.Card; "cardAlt", s.CardAlt; "action", s.Action ] do
                Assert.True(int c.R >= int c.B, sprintf "%A/%s %s is cool: %A" mode key name c)

// ---------------------------------------------------------------------------
// 10. the shell contract — the migration is complete
// ---------------------------------------------------------------------------

[<Fact>]
let ``Settings draws its own glass, and every screen is migrated`` () =
    Assert.True(Page.usesGlassSurface Page.Settings)

    for page, _, _ in Page.all do
        Assert.True(Page.usesGlassSurface page, sprintf "%A lost its glass surface" page)

// ---------------------------------------------------------------------------
// 11. performance fixtures
// ---------------------------------------------------------------------------
//
// Settings' own measurements. The decision cost (validation, dirty state,
// category selection) and the cost of BUILDING each panel. No FPS is claimed —
// no Avalonia application runs here, and no repository is reachable.

let private measure (runs: int) (work: unit -> int) =
    work () |> ignore
    GC.Collect()
    GC.WaitForPendingFinalizers()

    let before = GC.GetAllocatedBytesForCurrentThread()
    let times = ResizeArray<float>()

    for _ in 1..runs do
        let sw = Diagnostics.Stopwatch.StartNew()
        work () |> ignore
        sw.Stop()
        times.Add sw.Elapsed.TotalMilliseconds

    let allocated = GC.GetAllocatedBytesForCurrentThread() - before
    let sorted = times |> Seq.sort |> Seq.toArray
    sorted.[sorted.Length / 2], float allocated / float runs / 1024.0

let private memories (n: int) =
    List.init n (fun i -> memory (sprintf "Observation number %d about how this student works." i))

let private populated () =
    { UserSettings.defaults with
        UserName = "Maya Okafor"
        WorkdayStartHour = 8
        WorkdayEndHour = 22
        SessionMinutes = 45
        BreakMinutes = 15
        MaxDailyStudyHours = 8.0
        DeadlineBufferHours = 24.0
        PlannerHorizonDays = 21
        AiModel = "claude-opus-5"
        AiAutoApply = true }

[<Fact>]
let ``Settings fixtures stay inside their budget`` () =
    let report = System.Text.StringBuilder()
    report.AppendLine "Settings fixtures" |> ignore
    report.AppendLine "fixture | median ms | KB per run" |> ignore

    let record name runs work =
        let ms, kb = measure runs work
        report.AppendLine(sprintf "%-42s | %8.4f | %9.1f" name ms kb) |> ignore
        Assert.True(ms < 60.0, sprintf "%s took %.2f ms" name ms)

    // --- decision cost -----------------------------------------------------
    record "decide: validation" 2000 (fun () ->
        (SettingsLogic.validationError (populated ()) |> Option.isSome |> ignore)
        SettingsLogic.Category.all.Length)

    record "decide: dirty state" 2000 (fun () ->
        if SettingsLogic.isDirty UserSettings.defaults (populated ()) then 1 else 0)

    record "decide: save eligibility" 2000 (fun () ->
        if SettingsLogic.canSave false UserSettings.defaults (populated ()) then 1 else 0)

    record "decide: category selection" 2000 (fun () ->
        SettingsLogic.Category.all
        |> List.sumBy (fun c -> (SettingsLogic.Category.label c).Length + (SettingsLogic.categoryCaption c).Length))

    record "decide: memory presentation, 100" 200 (fun () ->
        memories 100 |> List.filter (fun m -> m.Enabled) |> List.length)

    // --- view construction -------------------------------------------------
    let build (m: SettingsView.Model) () = (all (render m)).Length

    record "view: default model" 50 (build (ready ()))
    record "view: profile populated" 50 (build { at SettingsLogic.Category.Profile with Draft = populated () })
    record "view: planner populated" 50 (build { at SettingsLogic.Category.Planner with Draft = populated () })
    record "view: AI, no memories" 50 (build (at SettingsLogic.Category.Ai))

    for n in [ 10; 100 ] do
        record
            (sprintf "view: AI, %d memories" n)
            (if n >= 100 then 20 else 50)
            (build { at SettingsLogic.Category.Ai with Memories = memories n })

    record
        "view: one very long memory"
        50
        (build
            { at SettingsLogic.Category.Ai with
                Memories = [ memory (String.replicate 200 "a long remembered observation. ") ] })

    record
        "view: memory add pending"
        50
        (build
            { at SettingsLogic.Category.Ai with
                Memories = memories 10
                NewMemoryText = "remember this"
                MemoryBusy = Some "add" })

    record
        "view: memory error"
        50
        (build
            { at SettingsLogic.Category.Ai with
                Memories = memories 10
                MemoryNotice = Some(String.replicate 20 "the database is locked and would not accept the write. ") })

    record "view: informational category" 50 (build (at SettingsLogic.Category.Privacy))
    record "view: advanced with shortcuts" 50 (build (at SettingsLogic.Category.Advanced))

    record "view: dirty draft" 50 (build { at SettingsLogic.Category.Profile with Draft = populated () })

    record
        "view: save pending"
        50
        (build { at SettingsLogic.Category.Profile with Draft = populated (); Saving = true })

    record
        "view: save failure"
        50
        (build
            { at SettingsLogic.Category.Profile with
                Draft = populated ()
                SaveState = SettingsLogic.SaveState.Failed(String.replicate 12 "the disk is full. ") })

    record "view: rapid category switching" 20 (fun () ->
        SettingsLogic.Category.all |> List.sumBy (fun c -> (all (render (at c))).Length))

    IO.File.WriteAllText(IO.Path.Combine(IO.Path.GetTempPath(), "settings-fixtures.txt"), report.ToString())
