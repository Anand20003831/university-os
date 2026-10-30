module UniversityOS.Tests.NotesViewTests

open System
open Xunit
open Avalonia.Media
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Features

/// The rebuilt Notes screen.
///
/// Every decision lives in `NotesLogic` or in `NotesView.update`, so all of it is
/// testable without an Avalonia application or a database.

let private now = DateTime(2026, 8, 1, 12, 0, 0)

let private moduleOf code name : StudyModule =
    { StudyModule.create (now.AddDays -60.0) code name 0 with Color = "#CB8A4E" }

let private noteOf title : Note = Note.create (now.AddDays -3.0) title
let private facts (n: Note) : NotesLogic.Facts = NotesLogic.Facts.ofNote n

let private refOf (kind: EntityKind) = { Kind = kind; Id = Guid.NewGuid() }

/// Long enough to clear the AI gate's 20-word floor.
let private body = String.replicate 40 "word "

// ---------------------------------------------------------------------------
// 1. composition — the redesign's own shape
// ---------------------------------------------------------------------------

[<Fact>]
let ``the page is the redesign's four sections, in order`` () =
    Assert.Equal<NotesLogic.Section list>(
        [ NotesLogic.Section.TopBar
          NotesLogic.Section.Explorer
          NotesLogic.Section.Workspace
          NotesLogic.Section.Context ],
        NotesLogic.sectionOrder
    )

[<Fact>]
let ``the Explorer runs search, filters, modules, pinned, then all`` () =
    Assert.Equal<NotesLogic.ExplorerRegion list>(
        [ NotesLogic.ExplorerRegion.Search
          NotesLogic.ExplorerRegion.Filters
          NotesLogic.ExplorerRegion.ModuleChips
          NotesLogic.ExplorerRegion.Pinned
          NotesLogic.ExplorerRegion.All ],
        NotesLogic.explorerOrder
    )

[<Fact>]
let ``the context rail runs the redesign's eight regions, in order`` () =
    Assert.Equal<NotesLogic.ContextRegion list>(
        [ NotesLogic.ContextRegion.AiSummary
          NotesLogic.ContextRegion.Concepts
          NotesLogic.ContextRegion.Related
          NotesLogic.ContextRegion.History
          NotesLogic.ContextRegion.GraphNodes
          NotesLogic.ContextRegion.Backlinks
          NotesLogic.ContextRegion.Timestamps
          NotesLogic.ContextRegion.Suggestions ],
        NotesLogic.contextOrder
    )

    Assert.Equal(NotesLogic.contextOrder.Length, (List.distinct NotesLogic.contextOrder).Length)

[<Fact>]
let ``the top bar carries the redesign's six inline metrics`` () =
    Assert.Equal(6, (NotesLogic.metrics [ facts (noteOf "One") ]).Length)

[<Fact>]
let ``the Explorer offers the redesign's eight filter chips`` () =
    Assert.Equal(8, NotesLogic.Filter.all.Length)

    let labels = NotesLogic.Filter.all |> List.map NotesLogic.Filter.label
    Assert.Equal(labels.Length, (List.distinct labels).Length)
    Assert.All(labels, fun l -> Assert.False(String.IsNullOrWhiteSpace l))

// ---------------------------------------------------------------------------
// 2. data honesty
// ---------------------------------------------------------------------------

[<Fact>]
let ``an empty screen reads as six honest zeroes`` () =
    let values = NotesLogic.metrics [] |> List.map (fun m -> m.Label, m.Value)

    Assert.Equal<(string * string option) list>(
        [ "notes", Some "0"
          "pinned", Some "0"
          "tags", Some "0"
          "links", Some "0"
          "words", Some "0"
          "unfiled", Some "0" ],
        values
    )

[<Fact>]
let ``no metric claims a version, a favourite or an extracted concept`` () =
    // None of the three is stored anywhere in the domain.
    let labels = NotesLogic.metrics [] |> List.map (fun m -> m.Label.ToLowerInvariant())

    for banned in [ "version"; "favourite"; "concept"; "confidence" ] do
        Assert.All(labels, fun l -> Assert.DoesNotContain(banned, l))

[<Fact>]
let ``the tag count is distinct across notes, not a running total`` () =
    let a = { noteOf "A" with Tags = [ "sql"; "joins" ] }
    let b = { noteOf "B" with Tags = [ "sql" ] }

    let tags = NotesLogic.metrics [ facts a; facts b ] |> List.find (fun m -> m.Label = "tags")
    Assert.Equal(Some "2", tags.Value)

[<Fact>]
let ``word count and reading time are deterministic, and empty is not one minute`` () =
    Assert.Equal(0, NotesLogic.wordCount "")
    Assert.Equal(0, NotesLogic.wordCount "   \n  ")
    Assert.Equal(3, NotesLogic.wordCount "one two three")

    Assert.Equal(0, NotesLogic.readingMinutes "")
    Assert.Equal("empty", NotesLogic.readingLabel "")

    // Any words at all round up to a minute — "0 min read" reads as an error.
    Assert.Equal(1, NotesLogic.readingMinutes "one two three")
    Assert.Equal("1 min read", NotesLogic.readingLabel "one two three")

    Assert.Equal(200, NotesLogic.Threshold.wordsPerMinute)
    Assert.Equal(2, NotesLogic.readingMinutes (String.replicate 300 "word "))

[<Fact>]
let ``the excerpt skips structure and shows real prose`` () =
    let content = "# Heading\n\n- a bullet\n\n> a quote\n\nThe first real sentence."
    Assert.Equal("The first real sentence.", NotesLogic.excerpt content)

    // A note that is nothing but structure has no excerpt, rather than showing
    // "# Heading" as if it were prose.
    Assert.Equal("", NotesLogic.excerpt "# Heading\n\n- a bullet")

[<Fact>]
let ``a summary is detected only from the heading the AI actually writes`` () =
    let plain = facts (noteOf "One")
    Assert.False(NotesLogic.hasSummary plain)

    let summarised =
        facts { noteOf "One" with Content = "text\n\n" + NotesLogic.Threshold.summaryHeading + "\n\nthe summary" }

    Assert.True(NotesLogic.hasSummary summarised)

    // A note that merely mentions summaries is not summarised.
    Assert.False(NotesLogic.hasSummary (facts { noteOf "One" with Content = "I should write a summary" }))

[<Fact>]
let ``a related category with nothing in it is omitted entirely`` () =
    // The mockup lists Lecture, Assignment, Document, Flashcards and Planner
    // whether or not anything is there. A decorative zero is not shown here.
    Assert.Empty(NotesLogic.related (facts (noteOf "One")))

    let linked =
        { facts (noteOf "One") with
            LinksOut = [ refOf EntityKind.Lecture ]
            LinksIn = [ refOf EntityKind.Note ] }

    let kinds = NotesLogic.related linked |> List.map fst
    Assert.Contains(NotesLogic.RelatedKind.Lectures, kinds)
    Assert.Contains(NotesLogic.RelatedKind.Notes, kinds)
    Assert.DoesNotContain(NotesLogic.RelatedKind.Documents, kinds)

[<Fact>]
let ``flashcards count comes from the real source link, not from an edge`` () =
    let withCards = { facts (noteOf "One") with Flashcards = 3 }
    let found = NotesLogic.related withCards |> List.tryFind (fun (k, _) -> k = NotesLogic.RelatedKind.Flashcards)

    Assert.True(found.IsSome)
    Assert.Equal(3, snd found.Value)

[<Fact>]
let ``every related kind navigates to a real page`` () =
    for kind in
        [ NotesLogic.RelatedKind.Notes
          NotesLogic.RelatedKind.Lectures
          NotesLogic.RelatedKind.Assignments
          NotesLogic.RelatedKind.Documents
          NotesLogic.RelatedKind.Modules
          NotesLogic.RelatedKind.Flashcards ] do
        Assert.Contains(NotesLogic.RelatedKind.page kind, Page.all |> List.map (fun (p, _, _) -> p))

// ---------------------------------------------------------------------------
// 3. rule-based suggestions
// ---------------------------------------------------------------------------

[<Fact>]
let ``suggestions are rules over stored fields, and a complete note gets none`` () =
    let bare = facts (noteOf "Bare")
    Assert.NotEmpty(NotesLogic.suggestions bare)

    let complete =
        { facts
            { noteOf "Complete" with
                ModuleId = Some (moduleOf "CS2004" "Graphics").Id
                Tags = [ "sql" ]
                Content = "some real prose here" } with
            LinksOut = [ refOf EntityKind.Note ] }

    Assert.Empty(NotesLogic.suggestions complete)

[<Fact>]
let ``an orphan is told so, and a linked note is not`` () =
    let orphan = facts (noteOf "Orphan")
    Assert.True(NotesLogic.isOrphan orphan)
    Assert.Contains(NotesLogic.suggestions orphan, fun (_, t) -> t.Contains "wiki link")

    let linked = { facts (noteOf "Linked") with LinksIn = [ refOf EntityKind.Note ] }
    Assert.False(NotesLogic.isOrphan linked)

// ---------------------------------------------------------------------------
// 4. filtering, search and selection
// ---------------------------------------------------------------------------

[<Fact>]
let ``chips toggle on and off independently`` () =
    let s = NotesLogic.Selection.empty
    let s = NotesLogic.Selection.toggle NotesLogic.Filter.Pinned s
    let s = NotesLogic.Selection.toggle NotesLogic.Filter.Untagged s

    Assert.True(s.Contains NotesLogic.Filter.Pinned)
    Assert.True(s.Contains NotesLogic.Filter.Untagged)

    let s = NotesLogic.Selection.toggle NotesLogic.Filter.Pinned s
    Assert.False(s.Contains NotesLogic.Filter.Pinned)

[<Fact>]
let ``every filter reads a real stored field`` () =
    let m = moduleOf "CS2004" "Graphics"

    let rich =
        { facts
            { noteOf "Rich" with
                Pinned = true
                Tags = [ "sql" ]
                ModuleId = Some m.Id
                Content = "- [ ] a task\n\n" + NotesLogic.Threshold.summaryHeading
                UpdatedAt = now } with
            LinksOut = [ refOf EntityKind.Note ] }

    Assert.True(NotesLogic.matches now NotesLogic.Filter.Pinned rich)
    Assert.True(NotesLogic.matches now NotesLogic.Filter.RecentlyEdited rich)
    Assert.False(NotesLogic.matches now NotesLogic.Filter.Unfiled rich)
    Assert.False(NotesLogic.matches now NotesLogic.Filter.Untagged rich)
    Assert.True(NotesLogic.matches now NotesLogic.Filter.HasLinks rich)
    Assert.False(NotesLogic.matches now NotesLogic.Filter.Orphans rich)
    Assert.True(NotesLogic.matches now NotesLogic.Filter.HasTasks rich)
    Assert.True(NotesLogic.matches now NotesLogic.Filter.Summarised rich)

[<Fact>]
let ``an old note is not recently edited`` () =
    let old' = facts { noteOf "Old" with UpdatedAt = now.AddDays -30.0 }
    Assert.False(NotesLogic.matches now NotesLogic.Filter.RecentlyEdited old')
    Assert.Equal(7, NotesLogic.Threshold.recentDays)

[<Fact>]
let ``search matches title, tags and body, and is case-insensitive`` () =
    let n = { noteOf "SQL joins" with Tags = [ "database" ]; Content = "inner and outer" }
    let f = facts n

    Assert.True(NotesLogic.searchMatches "" f)
    Assert.True(NotesLogic.searchMatches "sql" f)
    Assert.True(NotesLogic.searchMatches "DATABASE" f)
    Assert.True(NotesLogic.searchMatches "outer" f)
    Assert.False(NotesLogic.searchMatches "kubernetes" f)

[<Fact>]
let ``module chips list only modules that really have notes`` () =
    let m = moduleOf "CS2004" "Graphics"
    let filed = { facts { noteOf "Filed" with ModuleId = Some m.Id } with Module = Some m }
    let loose = facts (noteOf "Loose")

    let chips = NotesLogic.moduleChips [ filed; loose ]
    Assert.Equal(1, chips.Length)
    Assert.Equal("CS2004", (fst chips.Head).Code)
    Assert.Equal(1, snd chips.Head)

[<Fact>]
let ``module chips are ORed among themselves`` () =
    let m1 = moduleOf "AA1000" "One"
    let m2 = moduleOf "BB2000" "Two"
    let m3 = moduleOf "CC3000" "Three"

    let inModule (m: StudyModule) title =
        { facts { noteOf title with ModuleId = Some m.Id } with Module = Some m }

    let all = [ inModule m1 "First"; inModule m2 "Second"; inModule m3 "Third" ]
    let picked = Set.ofList [ m1.Id.Value; m2.Id.Value ]

    let shown =
        NotesLogic.visible now NotesLogic.Selection.empty picked "" all
        |> List.map (fun f -> f.Note.Title)

    Assert.Equal(2, shown.Length)
    Assert.DoesNotContain("Third", shown)

[<Fact>]
let ``the list is most-recently-edited first, so a refresh cannot reorder it`` () =
    let at title days = facts { noteOf title with UpdatedAt = now.AddDays days }
    let all = [ at "Old" -10.0; at "Newest" -1.0; at "Middle" -5.0 ]

    Assert.Equal<string list>(
        [ "Newest"; "Middle"; "Old" ],
        NotesLogic.visible now NotesLogic.Selection.empty Set.empty "" all |> List.map (fun f -> f.Note.Title)
    )

[<Fact>]
let ``pinned notes are split out, as the redesign's two sections are`` () =
    let pinned = facts { noteOf "Pinned" with Pinned = true }
    let plain = facts (noteOf "Plain")
    let rows = [ pinned; plain ]

    Assert.Equal<string list>([ "Pinned" ], NotesLogic.pinned rows |> List.map (fun f -> f.Note.Title))
    Assert.Equal<string list>([ "Plain" ], NotesLogic.unpinned rows |> List.map (fun f -> f.Note.Title))

[<Fact>]
let ``the empty message explains a contradiction rather than leaving a blank panel`` () =
    let both =
        NotesLogic.Selection.empty
        |> NotesLogic.Selection.toggle NotesLogic.Filter.HasLinks
        |> NotesLogic.Selection.toggle NotesLogic.Filter.Orphans

    Assert.Contains("cannot both", NotesLogic.emptyMessage both Set.empty "" 3)

[<Fact>]
let ``an empty library is told how to start, and a search miss names the query`` () =
    Assert.Contains("No notes yet", NotesLogic.emptyMessage NotesLogic.Selection.empty Set.empty "" 0)
    Assert.Contains("kubernetes", NotesLogic.emptyMessage NotesLogic.Selection.empty Set.empty "kubernetes" 4)

// ---------------------------------------------------------------------------
// 5. the three real AI actions
// ---------------------------------------------------------------------------

[<Fact>]
let ``only three AI actions exist, because only three have a service`` () =
    // The redesign offers eleven.
    Assert.Equal(3, NotesLogic.AiAction.all.Length)

    // Only the two provider calls need something to read; opening the Assistant
    // does not.
    Assert.True(NotesLogic.AiAction.needsBody NotesLogic.AiAction.Summarise)
    Assert.True(NotesLogic.AiAction.needsBody NotesLogic.AiAction.GenerateFlashcards)
    Assert.False(NotesLogic.AiAction.needsBody NotesLogic.AiAction.AskAssistant)

[<Fact>]
let ``the AI gate is closed on an empty body, and says why`` () =
    let reason = NotesLogic.aiDisabledReason ""
    Assert.True(reason.IsSome)
    Assert.Contains("write something", reason.Value)

[<Fact>]
let ``a stub body is refused, and the reason counts the words`` () =
    let reason = NotesLogic.aiDisabledReason "three words only"
    Assert.True(reason.IsSome)
    Assert.Contains("3 words", reason.Value)

[<Fact>]
let ``a real body opens the gate`` () =
    Assert.Equal(None, NotesLogic.aiDisabledReason body)

// ---------------------------------------------------------------------------
// 6. responsive layout
// ---------------------------------------------------------------------------

[<Fact>]
let ``at wide width both rails honour their toggles`` () =
    let panes = NotesLogic.panesFor 1600.0 false true true
    Assert.True(panes.Explorer)
    Assert.True(panes.Context)

    let closed = NotesLogic.panesFor 1600.0 false false false
    Assert.False(closed.Explorer)
    Assert.False(closed.Context)

[<Fact>]
let ``at medium width the context rail gives way but the Explorer does not`` () =
    let panes = NotesLogic.panesFor 1100.0 false true true
    Assert.True(panes.Explorer)
    Assert.False(panes.Context)

[<Fact>]
let ``at narrow width both rails collapse so the workspace still fits`` () =
    // A 320px rail beside a 340px minimum centre does not fit, and forcing it
    // would produce the horizontal scrollbar this project does not allow.
    let panes = NotesLogic.panesFor 700.0 false true true
    Assert.False(panes.Explorer)
    Assert.False(panes.Context)

[<Fact>]
let ``focus mode hides both rails at any width`` () =
    let panes = NotesLogic.panesFor 1900.0 true true true
    Assert.False(panes.Explorer)
    Assert.False(panes.Context)

[<Fact>]
let ``a collapsed rail keeps a width, so its toggle is never itself hidden`` () =
    let collapsed = { NotesLogic.Explorer = false; NotesLogic.Context = false }
    Assert.Equal(NotesLogic.Geometry.railWidth, NotesLogic.explorerColumnWidth collapsed, 6)
    Assert.Equal(NotesLogic.Geometry.railWidth, NotesLogic.contextColumnWidth collapsed, 6)
    Assert.True(NotesLogic.Geometry.railWidth > 0.0)

    let open' = { NotesLogic.Explorer = true; NotesLogic.Context = true }
    Assert.Equal(NotesLogic.Geometry.explorerWidth, NotesLogic.explorerColumnWidth open', 6)
    Assert.Equal(NotesLogic.Geometry.contextWidth, NotesLogic.contextColumnWidth open', 6)

[<Fact>]
let ``the workspace is reading-width but the page is not`` () =
    // Constraining the whole page to prose width is the mistake this guards.
    Assert.True(NotesLogic.Geometry.editorMaxWidth > NotesLogic.Geometry.centreMinWidth)
    Assert.True(NotesLogic.Geometry.editorMaxWidth < 1000.0)

[<Fact>]
let ``geometry matches every other migrated screen`` () =
    Assert.Equal(0.5, NotesLogic.Geometry.borderInset, 6)
    Assert.Equal(ModulesLogic.Geometry.borderInset, NotesLogic.Geometry.borderInset, 6)
    Assert.Equal(0.0, NotesLogic.Geometry.contentHighlight, 6)
    Assert.Equal(LecturesLogic.Geometry.actionMinHeight, NotesLogic.Geometry.actionMinHeight, 6)
    Assert.Equal(LecturesLogic.Geometry.actionRadius, NotesLogic.Geometry.actionRadius, 6)
    Assert.Equal(LecturesLogic.Geometry.iconActionSize, NotesLogic.Geometry.iconActionSize, 6)

[<Fact>]
let ``chips and rows reserve their outline in both states`` () =
    Assert.True(NotesLogic.Geometry.filterBorderThickness > 0.0)
    Assert.True(NotesLogic.Geometry.filterMinHeight > 0.0)
    Assert.True(NotesLogic.Geometry.noteRowBorderThickness > 0.0)

[<Fact>]
let ``wrapping text is given a finite column, never an infinite one`` () =
    Assert.Contains("*", NotesLogic.Geometry.bulletColumns)
    Assert.Contains("*", NotesLogic.Geometry.detailColumns)

// ---------------------------------------------------------------------------
// 7. update behaviour
// ---------------------------------------------------------------------------

let private noServices: AppServices = Unchecked.defaultof<AppServices>

let private baseModel: NotesView.Model =
    { Facts = []
      Modules = []
      Selected = None
      TitleText = ""
      ContentText = ""
      TagsText = ""
      ModuleIndex = 0
      Pinned = false
      Dirty = false
      Reading = true
      Focus = false
      ExplorerOpen = true
      ContextOpen = true
      Filters = NotesLogic.Selection.empty
      ModuleFilter = Set.empty
      Query = ""
      PendingDelete = false
      PendingSwitch = None
      Saving = false
      AiBusy = None
      Notice = None
      Loading = false
      Error = None
      Width = 1600.0 }

let private modelOf (m, _, _) = m
let private cmdOf (_, c, _) = c
let private externalOf (_, _, e) = e

[<Fact>]
let ``a successful load clears loading and any previous error`` () =
    let before = { baseModel with Loading = true; Error = Some "earlier" }
    let after = NotesView.update noServices (NotesView.Loaded([ facts (noteOf "One") ], [])) before |> modelOf

    Assert.False(after.Loading)
    Assert.Equal(None, after.Error)
    Assert.Equal(1, after.Facts.Length)

[<Fact>]
let ``a load drops a selection whose note has gone`` () =
    let gone = facts (noteOf "Gone")
    let before = { baseModel with Selected = Some gone.Note.Id; Facts = [ gone ] }
    let after = NotesView.update noServices (NotesView.Loaded([ facts (noteOf "Other") ], [])) before |> modelOf

    Assert.Equal(None, after.Selected)

[<Fact>]
let ``switching away from a dirty draft asks before discarding it`` () =
    let a = facts (noteOf "A")
    let b = facts (noteOf "B")

    let dirty =
        { baseModel with Facts = [ a; b ]; Selected = Some a.Note.Id; Dirty = true; ContentText = "typed" }

    let asked = NotesView.update noServices (NotesView.Select b.Note.Id) dirty |> modelOf

    // Nothing switched, and nothing was lost.
    Assert.Equal(Some b.Note.Id, asked.PendingSwitch)
    Assert.Equal(Some a.Note.Id, asked.Selected)
    Assert.Equal("typed", asked.ContentText)

[<Fact>]
let ``cancelling the guard stays put and keeps the draft`` () =
    let a = facts (noteOf "A")
    let b = facts (noteOf "B")

    let asked =
        { baseModel with
            Facts = [ a; b ]
            Selected = Some a.Note.Id
            Dirty = true
            ContentText = "typed"
            PendingSwitch = Some b.Note.Id }

    let stayed = NotesView.update noServices NotesView.CancelSwitch asked |> modelOf

    Assert.Equal(None, stayed.PendingSwitch)
    Assert.Equal(Some a.Note.Id, stayed.Selected)
    Assert.True(stayed.Dirty)
    Assert.Equal("typed", stayed.ContentText)

[<Fact>]
let ``confirming the guard switches and loads the other note`` () =
    let a = facts (noteOf "A")
    let b = facts { noteOf "B" with Content = "b body" }

    let asked =
        { baseModel with
            Facts = [ a; b ]
            Selected = Some a.Note.Id
            Dirty = true
            PendingSwitch = Some b.Note.Id }

    let switched = NotesView.update noServices NotesView.ConfirmSwitch asked |> modelOf

    Assert.Equal(Some b.Note.Id, switched.Selected)
    Assert.Equal("b body", switched.ContentText)
    Assert.False(switched.Dirty)
    Assert.Equal(None, switched.PendingSwitch)

[<Fact>]
let ``selecting a clean note switches straight away`` () =
    let a = facts (noteOf "A")
    let b = facts { noteOf "B" with Content = "b body"; Tags = [ "x" ] }
    let clean = { baseModel with Facts = [ a; b ]; Selected = Some a.Note.Id; Dirty = false }

    let after = NotesView.update noServices (NotesView.Select b.Note.Id) clean |> modelOf

    Assert.Equal(Some b.Note.Id, after.Selected)
    Assert.Equal("x", after.TagsText)
    Assert.Equal(None, after.PendingSwitch)

[<Fact>]
let ``every edit marks the draft dirty`` () =
    for msg in
        [ NotesView.SetTitle "t"
          NotesView.SetContent "c"
          NotesView.SetTags "a, b"
          NotesView.SetModuleIndex 1
          NotesView.TogglePinned ] do
        Assert.True((NotesView.update noServices msg baseModel |> modelOf).Dirty, sprintf "%A did not mark dirty" msg)

[<Fact>]
let ``toggling a view mode never marks the draft dirty`` () =
    for msg in
        [ NotesView.ToggleReading; NotesView.ToggleFocus; NotesView.ToggleExplorer; NotesView.ToggleContext ] do
        Assert.False((NotesView.update noServices msg baseModel |> modelOf).Dirty, sprintf "%A marked dirty" msg)

[<Fact>]
let ``a failure clears every in-flight flag but keeps the draft`` () =
    let before =
        { baseModel with
            Loading = true
            Saving = true
            PendingDelete = true
            AiBusy = Some NotesLogic.AiAction.Summarise
            Dirty = true
            ContentText = "unsaved work" }

    let after = NotesView.update noServices (NotesView.Failed(exn "offline")) before |> modelOf

    Assert.False(after.Loading)
    Assert.False(after.Saving)
    Assert.False(after.PendingDelete)
    Assert.Equal(None, after.AiBusy)
    Assert.Equal(Some "offline", after.Error)

    // The draft survives the failure — losing it would be the worst outcome.
    Assert.True(after.Dirty)
    Assert.Equal("unsaved work", after.ContentText)

[<Fact>]
let ``a second save while one is running is dropped`` () =
    let running = { baseModel with Saving = true; Dirty = true }
    Assert.True(List.isEmpty (cmdOf (NotesView.update noServices NotesView.Save running)))

[<Fact>]
let ``a second wiki navigation while one is running is dropped`` () =
    let running = { baseModel with Saving = true }
    Assert.True(List.isEmpty (cmdOf (NotesView.update noServices (NotesView.OpenWikiLink "Other") running)))

[<Fact>]
let ``deleting asks before it runs, and cancelling abandons it`` () =
    let selected = { baseModel with Selected = Some (noteOf "One").Id }
    let asked = NotesView.update noServices NotesView.RequestDelete selected |> modelOf
    Assert.True(asked.PendingDelete)

    let cancelled = NotesView.update noServices NotesView.CancelDelete asked |> modelOf
    Assert.False(cancelled.PendingDelete)

[<Fact>]
let ``confirming a delete while saving is dropped`` () =
    let running = { baseModel with Saving = true; PendingDelete = true; Selected = Some (noteOf "One").Id }
    Assert.True(List.isEmpty (cmdOf (NotesView.update noServices NotesView.ConfirmDelete running)))

[<Fact>]
let ``an AI run with nothing to read never reaches the provider`` () =
    let result = NotesView.update noServices (NotesView.RunAi NotesLogic.AiAction.Summarise) baseModel
    Assert.True(List.isEmpty (cmdOf result))
    Assert.Equal(None, (modelOf result).AiBusy)

[<Fact>]
let ``a second AI run while one is in flight is dropped`` () =
    let running =
        { baseModel with ContentText = body; AiBusy = Some NotesLogic.AiAction.Summarise }

    let result = NotesView.update noServices (NotesView.RunAi NotesLogic.AiAction.GenerateFlashcards) running
    Assert.True(List.isEmpty (cmdOf result))

[<Fact>]
let ``asking the Assistant navigates rather than calling a provider`` () =
    let result = NotesView.update noServices (NotesView.RunAi NotesLogic.AiAction.AskAssistant) baseModel
    Assert.Equal(Some(NotesView.Goto Page.Chat), externalOf result)
    Assert.True(List.isEmpty (cmdOf result))

[<Fact>]
let ``toggling a task rewrites the Markdown source and marks it dirty`` () =
    let before = { baseModel with ContentText = "- [ ] a task" }
    let after = NotesView.update noServices (NotesView.ToggleTask 0) before |> modelOf

    Assert.True(after.Dirty)
    Assert.Contains("- [x]", after.ContentText)

[<Fact>]
let ``a navigation action asks the shell to move`` () =
    let result = NotesView.update noServices (NotesView.GotoPage Page.Graph) baseModel
    Assert.Equal(Some(NotesView.Goto Page.Graph), externalOf result)

[<Fact>]
let ``a resize records the width and nothing else`` () =
    let after = NotesView.update noServices (NotesView.Resized 800.0) baseModel |> modelOf
    Assert.Equal(800.0, after.Width, 3)
    Assert.Equal<NotesLogic.Selection>(baseModel.Filters, after.Filters)

[<Fact>]
let ``a new note starts dirty and unselected, so Save creates rather than overwrites`` () =
    let after = NotesView.update noServices NotesView.NewNote baseModel |> modelOf
    Assert.True(after.Dirty)
    Assert.Equal(None, after.Selected)
    Assert.Equal("", after.TitleText)

[<Fact>]
let ``the screen opens in reading mode with both rails out`` () =
    Assert.True(baseModel.Reading)
    Assert.False(baseModel.Focus)
    Assert.True(baseModel.ExplorerOpen)
    Assert.True(baseModel.ContextOpen)

// ---------------------------------------------------------------------------
// 8. new composited roles, in BOTH themes
// ---------------------------------------------------------------------------

let private themeModes = [ ThemeMode.Dark; ThemeMode.Light ]
let private accentKeys = Palette.accents |> List.map fst

let private washedBy (alphaHex: string) (colour: Color) (surface: Color) =
    let a = Int32.Parse(alphaHex, Globalization.NumberStyles.HexNumber)
    Palette.composite surface (Color.FromArgb(byte a, colour.R, colour.G, colour.B))

[<Fact>]
let ``a tag chip's label is readable on its own wash, every accent and theme`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for hex in Theme.Identity.neutral :: StudyModule.palette do
                let raw = Color.Parse hex
                let wash = washedBy NotesLogic.Geometry.chipFillAlpha raw s.Card
                let ink = Palette.readableOn wash s.Text raw

                Assert.True(
                    Palette.contrastRatio ink wash >= Palette.aaText,
                    sprintf "%A/%s tag chip %s: %.2f" mode key hex (Palette.contrastRatio ink wash)
                )

[<Fact>]
let ``a selected note row's title stays readable on the stronger selected wash`` () =
    // The row is filled at selectedRowAlpha, not the chip alpha, so measuring it
    // against the chip wash would be measuring the wrong surface.
    Assert.NotEqual<string>(NotesLogic.Geometry.chipFillAlpha, NotesLogic.Geometry.selectedRowAlpha)

    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for hex in Theme.Identity.neutral :: StudyModule.palette do
                let raw = Color.Parse hex
                let row = washedBy NotesLogic.Geometry.selectedRowAlpha raw s.Window

                Assert.True(
                    Palette.contrastRatio s.Text row >= Palette.aaText,
                    sprintf "%A/%s selected row %s: %.2f" mode key hex (Palette.contrastRatio s.Text row)
                )

[<Fact>]
let ``no Notes surface is a near-white sheet in Light theme`` () =
    for key in accentKeys do
        let s = Surfaces.resolve ThemeMode.Light (Palette.ofKey key)

        for name, colour in [ "Card", s.Card; "CardAlt", s.CardAlt; "Action", s.Action; "Window", s.Window ] do
            let nearWhite = colour.R > 250uy && colour.G > 250uy && colour.B > 250uy
            Assert.False(nearWhite, sprintf "Light/%s %s is near-white" key name)

// ---------------------------------------------------------------------------
// 9. the shell contract
// ---------------------------------------------------------------------------

[<Fact>]
let ``Notes draws its own glass`` () =
    Assert.True(Page.usesGlassSurface Page.Notes)

[<Fact>]
let ``the screens migrated so far all draw their own glass`` () =
    for page in
        [ Page.Dashboard
          Page.Timeline
          Page.Planner
          Page.Inbox
          Page.Assignments
          Page.Modules
          Page.Lectures
          Page.Notes ] do
        Assert.True(Page.usesGlassSurface page)

[<Fact>]
let ``the screens still to migrate keep their opaque surface`` () =
    // Every screen is now migrated, so nothing is left holding an opaque shell
    // surface. This asserts the completed state rather than an empty list.
    for page, _, _ in Page.all do
        Assert.True(Page.usesGlassSurface page, sprintf "%A lost its glass surface" page)
