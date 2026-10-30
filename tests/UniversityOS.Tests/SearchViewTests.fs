module UniversityOS.Tests.SearchViewTests

open System
open Xunit
open Avalonia.Media
open Avalonia.Controls.Primitives
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Features

/// The rebuilt Search screen.
///
/// Decisions live in `SearchLogic`; the composition is asserted by building the
/// real `SearchView.view` tree and walking it, with no Avalonia application
/// behind it (see `AssistantViewTests` for why that works).
///
/// **The engine is one FTS5 query ranked by bm25.** Most of what these tests
/// protect is that the screen never claims more than that.

// ---------------------------------------------------------------------------
// harness
// ---------------------------------------------------------------------------

let private accessorName (accessor: Accessor) =
    match accessor with
    | Accessor.InstanceProperty p -> p.Name
    | Accessor.AvaloniaProperty p -> p.Name

let private kids (view: IView) : IView list =
    view.Attrs
    |> List.collect (fun attr ->
        match attr.Content with
        | ValueSome content ->
            match content.Content with
            | ViewContent.Single(Some child) -> [ child ]
            | ViewContent.Single None -> []
            | ViewContent.Multiple children -> children
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

let private floatProp name view =
    match propOf name view with
    | Some(:? float as f) -> Some f
    | _ -> None

let private ofType (name: string) (root: IView) =
    all root |> List.filter (fun v -> v.ViewType.Name = name)

let private words (root: IView) =
    all root
    |> List.collect (fun v -> [ stringProp "Text" v; stringProp "Content" v ] |> List.choose id)

let private says (needle: string) (root: IView) =
    words root |> List.exists (fun s -> s.Contains(needle, StringComparison.Ordinal))

let private countSaying (needle: string) (root: IView) =
    words root |> List.filter (fun s -> s.Contains(needle, StringComparison.Ordinal)) |> List.length

let private accessibleNames (root: IView) = all root |> List.choose (stringProp "Name")

let private watermarks (root: IView) = all root |> List.choose (stringProp "Watermark")

// ---------------------------------------------------------------------------
// fixtures
// ---------------------------------------------------------------------------

let private tokens = Tokens.ofAppearance true 0 AppearanceSettings.defaults
let private noServices: AppServices = Unchecked.defaultof<AppServices>

let private modelOf (m, _, _) = m
let private cmdOf (_, c, _) = c
let private externalOf (_, _, e) = e

let private now = DateTime(2026, 8, 1, 14, 0, 0)

let private hit kind title snippet score : SearchHit =
    { Kind = kind; Id = Guid.NewGuid(); Title = title; Snippet = snippet; Score = score }

let private keyed kind title id : SearchHit =
    { Kind = kind; Id = id; Title = title; Snippet = "some matching text"; Score = 1.0 }

let private moduleOf code name : StudyModule =
    { StudyModule.create (now.AddDays -60.0) code name 0 with Color = "#CB8A4E" }

let private ready () =
    let model, _ = SearchView.init noServices
    { model with Width = 1600.0 }

let private withResults (hits: SearchHit list) =
    { ready () with Query = "compiler"; All = hits; Searching = false }

let private render (model: SearchView.Model) = SearchView.view tokens model ignore

// ---------------------------------------------------------------------------
// 1. what is genuinely searchable
// ---------------------------------------------------------------------------

[<Fact>]
let ``exactly the seven indexed kinds are searchable`` () =
    Assert.Equal(7, SearchLogic.searchableKinds.Length)

    Assert.Equal<EntityKind list>(
        [ EntityKind.Assignment
          EntityKind.Module
          EntityKind.Note
          EntityKind.Document
          EntityKind.Lecture
          EntityKind.Event
          EntityKind.RevisionItem ],
        SearchLogic.searchableKinds
    )

[<Fact>]
let ``the kinds nothing indexes are absent, so no filter can promise them`` () =
    // Nothing anywhere calls `Fts.upsert` with these, so a chip for them could
    // only ever return nothing.
    for kind in [ EntityKind.InboxItem; EntityKind.Session; EntityKind.Conversation ] do
        Assert.DoesNotContain(kind, SearchLogic.searchableKinds)

    let root = render (ready ())

    for absent in [ "Inbox"; "Planner"; "Conversations" ] do
        Assert.False(says absent root, absent)

[<Fact>]
let ``the empty state says what each kind actually has indexed`` () =
    let root = render (ready ())

    Assert.True(says "extracted text" root)
    Assert.True(says "transcript" root)
    Assert.True(says "tags" root)

    for kind in SearchLogic.searchableKinds do
        Assert.NotEqual<string>("not indexed", SearchLogic.indexedBody kind)

// ---------------------------------------------------------------------------
// 2. query handling
// ---------------------------------------------------------------------------

[<Fact>]
let ``a query under two characters runs nothing`` () =
    Assert.False(SearchLogic.isRunnable "c")
    Assert.True(SearchLogic.isRunnable "co")
    Assert.False(SearchLogic.isRunnable "  ")

    let after = SearchView.update noServices (SearchView.SetQuery "c") (withResults [ hit EntityKind.Note "n" "s" 1.0 ])
    Assert.True((modelOf after).All.IsEmpty)
    Assert.False((modelOf after).Searching)
    Assert.True(List.isEmpty (cmdOf after))

[<Fact>]
let ``a runnable query is debounced, not fired per keystroke`` () =
    let one = SearchView.update noServices (SearchView.SetQuery "co") (ready ())
    Assert.True((modelOf one).Searching)
    Assert.False(List.isEmpty (cmdOf one))

    // A second keystroke bumps the generation, so the first debounce lapses.
    let two = SearchView.update noServices (SearchView.SetQuery "com") (modelOf one)
    Assert.NotEqual((modelOf one).Generation, (modelOf two).Generation)

    let lapsed = SearchView.update noServices (SearchView.RunSearch (modelOf one).Generation) (modelOf two)
    Assert.True(List.isEmpty (cmdOf lapsed))

    let current = SearchView.update noServices (SearchView.RunSearch (modelOf two).Generation) (modelOf two)
    Assert.False(List.isEmpty (cmdOf current))

[<Fact>]
let ``a stale reply can never overwrite a newer query`` () =
    let model = { ready () with Query = "compiler"; Generation = 7; Searching = true }

    let stale =
        SearchView.update noServices (SearchView.Results(3, [ hit EntityKind.Note "old" "s" 1.0 ])) model |> modelOf

    Assert.True(stale.All.IsEmpty)
    Assert.True(stale.Searching)

    let current =
        SearchView.update noServices (SearchView.Results(7, [ hit EntityKind.Note "new" "s" 1.0 ])) model |> modelOf

    Assert.Equal(1, current.All.Length)
    Assert.False(current.Searching)

[<Fact>]
let ``clearing the query clears the results with it`` () =
    let after = SearchView.update noServices SearchView.ClearQuery (withResults [ hit EntityKind.Note "n" "s" 1.0 ]) |> modelOf

    Assert.Equal("", after.Query)
    Assert.True(after.All.IsEmpty)
    Assert.True(after.Selected.IsNone)

[<Fact>]
let ``the query states are exactly the ones the engine can be in`` () =
    Assert.Equal(SearchLogic.QueryState.Idle, SearchLogic.queryState "" false None false)
    Assert.Equal(SearchLogic.QueryState.TooShort, SearchLogic.queryState "c" false None false)
    Assert.Equal(SearchLogic.QueryState.Running, SearchLogic.queryState "com" true None false)
    Assert.Equal(SearchLogic.QueryState.Results, SearchLogic.queryState "com" false None true)
    Assert.Equal(SearchLogic.QueryState.NoResults, SearchLogic.queryState "com" false None false)
    Assert.Equal(SearchLogic.QueryState.Failed "boom", SearchLogic.queryState "com" false (Some "boom") true)

[<Fact>]
let ``the terms split the way the engine splits them`` () =
    // `Fts.toMatchQuery` splits on whitespace and strips quotes; anything else
    // would highlight differently from how the match was made.
    Assert.Equal<string list>([ "graph"; "colouring" ], SearchLogic.terms "  graph   colouring ")
    Assert.Equal<string list>([ "ab" ], SearchLogic.terms "\"ab\"")

// ---------------------------------------------------------------------------
// 3. filters
// ---------------------------------------------------------------------------

[<Fact>]
let ``there are eight chips - All plus the seven real kinds`` () =
    let root = render (withResults [ hit EntityKind.Note "n" "s" 1.0 ])

    let chipNames =
        accessibleNames root |> List.filter (fun n -> n.EndsWith(" filter, on") || n.EndsWith(" filter, off"))

    Assert.Equal(8, chipNames.Length)
    Assert.Contains("All filter, on", chipNames)

[<Fact>]
let ``a filter chip keeps identical geometry selected and unselected`` () =
    let hits = [ hit EntityKind.Note "n" "s" 1.0 ]
    let off = render (withResults hits)
    let on = render { withResults hits with Scope = Set.ofList [ EntityKind.Note ] }

    let chipBox (root: IView) =
        ofType "Button" root
        |> List.filter (fun b ->
            accessibleNames b |> List.exists (fun n -> n.StartsWith("Notes filter", StringComparison.Ordinal)))
        |> List.map (fun b -> propOf "Padding" b, propOf "MinHeight" b, propOf "BorderThickness" b)

    Assert.NotEmpty(chipBox off)
    Assert.Equal<(obj option * obj option * obj option) list>(chipBox off, chipBox on)

[<Fact>]
let ``a filter preserves the query and runs no new search`` () =
    let before = withResults [ hit EntityKind.Note "n" "s" 1.0; hit EntityKind.Module "m" "s" 1.0 ]
    let after = SearchView.update noServices (SearchView.ToggleKind EntityKind.Note) before

    Assert.Equal(before.Query, (modelOf after).Query)
    Assert.Equal(before.All.Length, (modelOf after).All.Length)
    Assert.True(List.isEmpty (cmdOf after))

[<Fact>]
let ``filtering changes what is grouped and counted`` () =
    let hits =
        [ hit EntityKind.Note "note one" "s" 1.0
          hit EntityKind.Note "note two" "s" 1.0
          hit EntityKind.Module "a module" "s" 1.0 ]

    let all = render (withResults hits)
    Assert.True(says "3 matches across 2 kinds" all)

    let onlyNotes = render { withResults hits with Scope = Set.ofList [ EntityKind.Note ] }
    Assert.True(says "2 matches across 1 kind" onlyNotes)
    Assert.False(says "a module" onlyNotes)

[<Fact>]
let ``a hidden kind leaves no trace in the grouped counts`` () =
    let hits = [ hit EntityKind.Note "n" "s" 1.0; hit EntityKind.Document "d" "s" 1.0 ]

    let grouped =
        hits |> SearchLogic.applyScope (Set.ofList [ EntityKind.Note ]) |> SearchLogic.groupResults

    Assert.Equal<EntityKind list>([ EntityKind.Note ], grouped |> List.map fst)
    Assert.Equal(1, grouped |> List.sumBy (fun (_, h) -> h.Length))

[<Fact>]
let ``All is the complete supported scope, not a separate list`` () =
    Assert.True(SearchLogic.Scope.isAll SearchLogic.Scope.all)

    let hits = SearchLogic.searchableKinds |> List.map (fun k -> hit k (string k) "s" 1.0)
    Assert.Equal(hits.Length, (SearchLogic.applyScope SearchLogic.Scope.all hits).Length)

    let cleared = SearchView.update noServices SearchView.ShowAllKinds { ready () with Scope = Set.ofList [ EntityKind.Note ] }
    Assert.True(SearchLogic.Scope.isAll (modelOf cleared).Scope)

[<Fact>]
let ``a selection removed by a filter is cleared, never silently moved`` () =
    let noteId = Guid.NewGuid()

    let hits =
        [ keyed EntityKind.Note "the note" noteId; keyed EntityKind.Module "the module" (Guid.NewGuid()) ]

    // Select the note (index 1: modules group is second, notes third — so
    // resolve the real index from the flattened order).
    let model = withResults hits
    let flattened = SearchLogic.flatten (SearchLogic.groupResults hits)
    let index = flattened |> List.findIndex (fun h -> h.Id = noteId)

    let selected = SearchView.update noServices (SearchView.Select index) model |> modelOf
    Assert.Equal(Some index, selected.Selected)

    // Hide notes: the selection has nowhere to point.
    let filtered = SearchView.update noServices (SearchView.ToggleKind EntityKind.Module) selected |> modelOf
    Assert.True(filtered.Selected.IsNone)

// ---------------------------------------------------------------------------
// 4. grouping and ranking
// ---------------------------------------------------------------------------

[<Fact>]
let ``the group order is pinned`` () =
    Assert.Equal<EntityKind list>(
        [ EntityKind.Assignment
          EntityKind.Module
          EntityKind.Note
          EntityKind.Document
          EntityKind.Lecture
          EntityKind.Event
          EntityKind.RevisionItem ],
        SearchLogic.groupOrder
    )

    // Order does not depend on the order the engine returned them.
    let forward = SearchLogic.groupResults [ hit EntityKind.Note "n" "s" 1.0; hit EntityKind.Assignment "a" "s" 1.0 ]
    let reverse = SearchLogic.groupResults [ hit EntityKind.Assignment "a" "s" 1.0; hit EntityKind.Note "n" "s" 1.0 ]

    Assert.Equal<EntityKind list>(forward |> List.map fst, reverse |> List.map fst)
    Assert.Equal<EntityKind list>([ EntityKind.Assignment; EntityKind.Note ], forward |> List.map fst)

[<Fact>]
let ``empty groups are omitted entirely`` () =
    let grouped = SearchLogic.groupResults [ hit EntityKind.Note "n" "s" 1.0 ]

    Assert.Single grouped |> ignore
    Assert.Equal<EntityKind list>([ EntityKind.Note ], grouped |> List.map fst)

    let root = render (withResults [ hit EntityKind.Note "n" "s" 1.0 ])
    Assert.True(says "NOTES" root)
    Assert.False(says "LECTURES" root)

[<Fact>]
let ``within a group the engine's own order is preserved`` () =
    // bm25 order is the engine's; the screen never re-sorts it.
    let a = hit EntityKind.Note "first" "s" 9.0
    let b = hit EntityKind.Note "second" "s" 4.0
    let c = hit EntityKind.Note "third" "s" 7.0

    let grouped = SearchLogic.groupResults [ a; b; c ]
    Assert.Equal<string list>([ "first"; "second"; "third" ], snd grouped.Head |> List.map (fun h -> h.Title))

[<Fact>]
let ``counts match the results actually drawn`` () =
    let hits =
        [ hit EntityKind.Note "one" "s" 1.0
          hit EntityKind.Note "two" "s" 1.0
          hit EntityKind.Document "three" "s" 1.0 ]

    let grouped = SearchLogic.groupResults hits

    for _, groupHits in grouped do
        Assert.NotEmpty groupHits

    Assert.Equal(hits.Length, SearchLogic.flatten grouped |> List.length)

[<Fact>]
let ``no result appears in two groups`` () =
    let hits = SearchLogic.searchableKinds |> List.map (fun k -> hit k (string k) "s" 1.0)
    let flattened = SearchLogic.flatten (SearchLogic.groupResults hits)

    Assert.Equal(hits.Length, flattened.Length)
    Assert.Equal(hits.Length, (flattened |> List.map (fun h -> h.Id) |> List.distinct).Length)

[<Fact>]
let ``there is no relevance percentage anywhere`` () =
    let root = render (withResults [ hit EntityKind.Note "a note" "matching text" 0.87 ])

    // bm25 is unbounded and corpus-dependent. Printing it as a percentage would
    // be inventing a scale it does not have.
    for s in words root do
        Assert.False(s.Contains "%", s)

    let shown = String.concat " " (words root) |> fun s -> s.ToLowerInvariant()

    for absent in [ "confidence"; "relevance score"; "semantic"; "ai-ranked"; "ai ranked"; "predicted"; "importance" ] do
        Assert.DoesNotContain(absent, shown)

[<Fact>]
let ``only the first result claims to be the best match`` () =
    let hits = [ hit EntityKind.Note "one" "s" 9.0; hit EntityKind.Note "two" "s" 4.0 ]
    Assert.Equal(Some 0, SearchLogic.bestMatchIndex hits)
    Assert.Equal(None, SearchLogic.bestMatchIndex [])

    let root = render (withResults hits)
    Assert.Equal(1, countSaying SearchLogic.bestMatchLabel root)

[<Fact>]
let ``the result cap is stated, because it is otherwise invisible`` () =
    let few = List.init 5 (fun i -> hit EntityKind.Note (string i) "s" 1.0)
    Assert.True((SearchLogic.capNotice few).IsNone)

    let capped = List.init SearchLogic.resultLimit (fun i -> hit EntityKind.Note (string i) "s" 1.0)
    Assert.True((SearchLogic.capNotice capped).IsSome)

    Assert.True(says (sprintf "first %d matches" SearchLogic.resultLimit) (render (withResults capped)))

// ---------------------------------------------------------------------------
// 5. result cards
// ---------------------------------------------------------------------------

[<Fact>]
let ``a card shows only values that were genuinely resolved`` () =
    let noteId = Guid.NewGuid()
    let m = moduleOf "CS4001" "Compilers"

    let note =
        { Note.create now "Graph colouring" with
            Id = NoteId noteId
            ModuleId = Some m.Id
            UpdatedAt = now.AddDays -2.0 }

    let reference = SearchLogic.buildReference [ m ] [] [ note ] [] [] [] []

    let model =
        { withResults [ keyed EntityKind.Note "Graph colouring" noteId ] with Reference = reference }

    let root = render model

    Assert.True(says "Graph colouring" root)
    Assert.True(says "CS4001" root)
    Assert.True(says "Updated" root)

[<Fact>]
let ``an unresolved fact is absent, never a dash or a zero`` () =
    // A hit whose entity is not in the snapshot: nothing is invented for it.
    let model = withResults [ hit EntityKind.Event "an event" "somewhere" 1.0 ]
    let facts = SearchLogic.factsFor model.Reference (List.head model.All)

    Assert.True(facts.ModuleLabel.IsNone)
    Assert.True(facts.Meta.IsNone)
    Assert.True(facts.TypeLabel.IsNone)

    let root = render model

    for invented in [ "—"; "Unknown"; "N/A"; "0%" ] do
        Assert.False(says invented root, invented)

[<Fact>]
let ``a card fabricates nothing the engine cannot supply`` () =
    let root = render (withResults [ hit EntityKind.Document "Lecture 3 slides" "some extracted text" 1.0 ])
    let shown = String.concat " " (words root) |> fun s -> s.ToLowerInvariant()

    for absent in [ "reading time"; "pages"; "page count"; "summary:"; "because"; "why this matched" ] do
        Assert.DoesNotContain(absent, shown)

[<Fact>]
let ``a document's file type is real, from the resolved document`` () =
    let docId = Guid.NewGuid()

    let doc: Document =
        { Id = DocumentId docId
          ModuleId = None
          Title = "Slides"
          FileName = "slides.pptx"
          StoredPath = "C:\\store\\slides.pptx"
          Kind = DocumentKind.Slides
          SizeBytes = 1024L
          ExtractedText = None
          Summary = None
          ImportedAt = now.AddDays -1.0 }

    let reference = SearchLogic.buildReference [] [] [] [ doc ] [] [] []
    let facts = SearchLogic.factsFor reference (keyed EntityKind.Document "Slides" docId)

    Assert.Equal(Some "Slides", facts.TypeLabel)
    Assert.True(facts.Meta.IsSome)

[<Fact>]
let ``selecting a card recolours it without changing its geometry`` () =
    let hits = [ hit EntityKind.Note "a note" "s" 1.0 ]
    let plain = render (withResults hits)
    let selected = render { withResults hits with Selected = Some 0 }

    // Matched on the card's OWN accessible name — an ancestor that merely
    // contains the card is not the card.
    let cardBox (root: IView) =
        all root
        |> List.filter (fun v ->
            v.ViewType.Name = "Border"
            && (stringProp "Name" v
                |> Option.map (fun n -> n.StartsWith("Note:", StringComparison.Ordinal))
                |> Option.defaultValue false))
        |> List.map (fun v -> propOf "Padding" v, propOf "BorderThickness" v, propOf "CornerRadius" v)

    Assert.NotEmpty(cardBox plain)
    Assert.Equal<(obj option * obj option * obj option) list>(cardBox plain, cardBox selected)

[<Fact>]
let ``a long title and a long path stay usable`` () =
    let longTitle = String.replicate 30 "verylongtitleword"
    let longPath = "C:\\Users\\someone\\Documents\\" + String.replicate 20 "nested\\" + "file.pdf"

    let root = render (withResults [ hit EntityKind.Document longTitle longPath 1.0 ])

    let wrapped =
        all root
        |> List.filter (fun v -> stringProp "Text" v = Some longTitle)
        |> List.map (propOf "TextWrapping")

    Assert.NotEmpty wrapped
    Assert.All(wrapped, fun w -> Assert.Equal(Some(box TextWrapping.Wrap), w))

// ---------------------------------------------------------------------------
// 6. match highlighting
// ---------------------------------------------------------------------------

[<Fact>]
let ``highlighting is case-insensitive`` () =
    let parts = SearchLogic.segments "graph" "Graph colouring"
    Assert.Contains(parts, fun p -> p.Matched && p.Text = "Graph")

[<Fact>]
let ``every occurrence of a term is marked`` () =
    let parts = SearchLogic.segments "note note" "note about a note"
    Assert.Equal(2, parts |> List.filter (fun p -> p.Matched) |> List.length)

[<Fact>]
let ``a term that does not appear leaves the text untouched`` () =
    let parts = SearchLogic.segments "zebra" "Graph colouring"
    Assert.Single parts |> ignore
    Assert.False(parts.Head.Matched)
    Assert.Equal("Graph colouring", parts.Head.Text)

[<Fact>]
let ``highlighting mirrors what FTS5 actually matched`` () =
    // The last term carries the `*`, so it completes the word it starts.
    let prefix = SearchLogic.segments "colour" "colouring theory"
    Assert.Contains(prefix, fun p -> p.Matched && p.Text = "colouring")

    // An earlier term is an exact token match, so it marks only itself.
    let exact = SearchLogic.segments "colour theory" "colouring theory"
    Assert.Contains(exact, fun p -> p.Matched && p.Text = "theory")
    Assert.DoesNotContain(exact, fun p -> p.Matched && p.Text = "colouring")

[<Fact>]
let ``a term is only matched at a token start, as the engine does`` () =
    // FTS5 matches tokens, not substrings: "raph" finds nothing in "Graph".
    let parts = SearchLogic.segments "raph" "Graph colouring"
    Assert.DoesNotContain(parts, fun p -> p.Matched)

[<Fact>]
let ``segments always reconstruct the original text exactly`` () =
    for query, text in
        [ "graph", "Graph colouring theory"
          "a", "a a a"
          "note", ""
          "", "untouched"
          "colour theory", "colouring theory and colour" ] do
        let rebuilt = SearchLogic.segments query text |> List.map (fun p -> p.Text) |> String.concat ""
        Assert.Equal(text, rebuilt)

[<Fact>]
let ``no stored Markdown can be corrupted, because none is rendered`` () =
    // The screen highlights titles and FTS5 snippets — both plain text. It never
    // renders a stored body, so `Ui.markdown` is not on this screen at all.
    let root = render (withResults [ hit EntityKind.Note "# A heading" "**bold** in the body" 1.0 ])

    Assert.True(says "# A heading" root)
    Assert.True(says "**bold** in the body" root)

[<Fact>]
let ``a match is bold as well as tinted`` () =
    let root =
        render { withResults [ hit EntityKind.Note "Graph colouring" "graph theory" 1.0 ] with Query = "graph" }

    let bolded =
        all root
        |> List.filter (fun v -> propOf "FontWeight" v = Some(box FontWeight.Bold))

    Assert.NotEmpty bolded

// ---------------------------------------------------------------------------
// 7. the preview
// ---------------------------------------------------------------------------

[<Fact>]
let ``no preview until something is selected`` () =
    Assert.False(says "SELECTED RESULT" (render (withResults [ hit EntityKind.Note "n" "s" 1.0 ])))
    Assert.True(says "SELECTED RESULT" (render { withResults [ hit EntityKind.Note "n" "s" 1.0 ] with Selected = Some 0 }))

[<Fact>]
let ``the preview offers only operations that genuinely exist`` () =
    let root = render { withResults [ hit EntityKind.Note "a note" "s" 1.0 ] with Selected = Some 0 }

    Assert.True(says "Open note" root)
    Assert.True(says "View in Graph" root)
    Assert.True(says "Ask the Assistant" root)

    let shown = String.concat " " (words root) |> fun s -> s.ToLowerInvariant()

    for absent in [ "preview document"; "render pdf"; "summarise with ai"; "ai summary" ] do
        Assert.DoesNotContain(absent, shown)

[<Fact>]
let ``the preview says navigation opens the screen, not the item`` () =
    let root = render { withResults [ hit EntityKind.Note "a note" "s" 1.0 ] with Selected = Some 0 }

    Assert.True(says SearchLogic.navigationNote root)
    Assert.Contains("owns it", SearchLogic.navigationNote)

[<Fact>]
let ``opening a result navigates to the owning screen`` () =
    let h = hit EntityKind.Document "a doc" "s" 1.0
    let result = SearchView.update noServices (SearchView.OpenHit h) (withResults [ h ])
    Assert.Equal(Some(SearchView.Goto Page.Documents), externalOf result)

    let selected = { withResults [ h ] with Selected = Some 0 }
    Assert.Equal(Some(SearchView.Goto Page.Documents), externalOf (SearchView.update noServices SearchView.OpenSelected selected))

[<Fact>]
let ``opening with nothing selected does nothing`` () =
    let result = SearchView.update noServices SearchView.OpenSelected (withResults [ hit EntityKind.Note "n" "s" 1.0 ])
    Assert.True((externalOf result).IsNone)

[<Fact>]
let ``the graph action goes to the graph`` () =
    let result = SearchView.update noServices (SearchView.GotoPage Page.Graph) (ready ())
    Assert.Equal(Some(SearchView.Goto Page.Graph), externalOf result)

// ---------------------------------------------------------------------------
// 8. the Assistant handoff
// ---------------------------------------------------------------------------

[<Fact>]
let ``the Assistant prompt names the entity in words`` () =
    Assert.Equal(
        "Tell me about my note \"Graph colouring\".",
        SearchLogic.assistantPrompt EntityKind.Note "Graph colouring"
    )

    // Documents, lectures and flashcards are excluded from `BuildContext`, so
    // naming them in the words of the prompt is the ONLY way the Assistant could
    // know what is being asked about.
    for kind in SearchLogic.searchableKinds do
        let prompt = SearchLogic.assistantPrompt kind "Something"
        Assert.Contains("Something", prompt)
        Assert.DoesNotContain("attached", prompt.ToLowerInvariant())

[<Fact>]
let ``the handoff claims nothing is attached`` () =
    let root = render { withResults [ hit EntityKind.Document "a doc" "s" 1.0 ] with Selected = Some 0 }

    Assert.True(says SearchLogic.assistantNote root)
    Assert.Contains("Nothing is attached", SearchLogic.assistantNote)

    let shown = String.concat " " (words root) |> fun s -> s.ToLowerInvariant()

    for overclaim in [ "with full context"; "attached context"; "sends the document"; "reads the document" ] do
        Assert.DoesNotContain(overclaim, shown)

[<Fact>]
let ``asking the Assistant changes nothing in Search itself`` () =
    // The shell owns both screens and performs the prefill; Search only asks.
    let before = { withResults [ hit EntityKind.Note "n" "s" 1.0 ] with Selected = Some 0 }
    let after = SearchView.update noServices (SearchView.AskAssistant (List.head before.All)) before

    Assert.Equal(before.Query, (modelOf after).Query)
    Assert.Equal(before.Selected, (modelOf after).Selected)
    Assert.True((externalOf after).IsNone)

// ---------------------------------------------------------------------------
// 9. recent searches and suggestions
// ---------------------------------------------------------------------------

[<Fact>]
let ``recent searches are session-only, and the heading says so`` () =
    Assert.Contains("this session", SearchLogic.recentHeading)
    Assert.Contains("not saved to disk", SearchLogic.recentEmptyNotice)

    let root = render (ready ())
    Assert.True(says (SearchLogic.recentHeading.ToUpperInvariant()) root)
    Assert.True(says SearchLogic.recentEmptyNotice root)

[<Fact>]
let ``only a search that found something is remembered`` () =
    Assert.Equal<string list>([], SearchLogic.remember "compiler" false [])
    Assert.Equal<string list>([ "compiler" ], SearchLogic.remember "compiler" true [])
    Assert.Equal<string list>([], SearchLogic.remember "c" true [])

    // No duplicates, most recent first, capped.
    let twice = SearchLogic.remember "compiler" true [ "notes"; "compiler" ]
    Assert.Equal<string list>([ "compiler"; "notes" ], twice)

    let many = List.init 10 (fun i -> sprintf "query%d" i)
    Assert.Equal(SearchLogic.maxRecent, (SearchLogic.remember "new" true many).Length)

[<Fact>]
let ``a successful search is remembered through the real update path`` () =
    let model = { ready () with Query = "compiler"; Generation = 2 }

    let found =
        SearchView.update noServices (SearchView.Results(2, [ hit EntityKind.Note "n" "s" 1.0 ])) model |> modelOf

    Assert.Equal<string list>([ "compiler" ], found.Recent)

    let empty = SearchView.update noServices (SearchView.Results(2, [])) model |> modelOf
    Assert.True(empty.Recent.IsEmpty)

[<Fact>]
let ``suggestions are deterministic and never called AI`` () =
    let modules = [ moduleOf "CS4001" "Compilers"; moduleOf "CS4002" "Networks" ]
    let notes = [ { Note.create now "Graph colouring" with UpdatedAt = now } ]

    let once = SearchLogic.suggestions modules notes []
    let twice = SearchLogic.suggestions modules notes []

    Assert.Equal<string list>(once, twice)
    Assert.Contains("CS4001", once)
    Assert.Contains("Graph colouring", once)

    Assert.Equal("Try searching for", SearchLogic.suggestionHeading)

    let root = render { ready () with Suggestions = once }
    let shown = String.concat " " (words root)
    Assert.DoesNotContain("AI Suggestions", shown)
    Assert.DoesNotContain("AI suggestion", shown)

[<Fact>]
let ``a suggestion runs the search it names`` () =
    let after = SearchView.update noServices (SearchView.UseQuery "CS4001") (ready ())

    Assert.Equal("CS4001", (modelOf after).Query)
    Assert.True((modelOf after).Searching)
    Assert.False(List.isEmpty (cmdOf after))

[<Fact>]
let ``recent activity is called what it is`` () =
    // Nothing records *opening*. `IActivityLog` records what happened.
    Assert.Equal("Recent activity", SearchLogic.activityHeading)

    let withEntity =
        { Id = Guid.NewGuid()
          Kind = ActivityKind.NoteUpdated
          Title = "Edited a note"
          Detail = None
          Entity = Some { Kind = EntityKind.Note; Id = Guid.NewGuid() }
          OccurredAt = now }

    let without = { withEntity with Id = Guid.NewGuid(); Entity = None }

    // Only entries that genuinely point somewhere are offered.
    Assert.Equal(1, (SearchLogic.navigableActivity [ withEntity; without ]).Length)

    let root = render { ready () with Activity = [ withEntity; without ] }
    Assert.True(says "RECENT ACTIVITY" root)
    Assert.False(says "RECENTLY OPENED" root)

[<Fact>]
let ``nothing that has no store is offered`` () =
    let root = render (ready ())
    let shown = String.concat " " (words root) |> fun s -> s.ToLowerInvariant()

    // Pinned, saved and frequency-ranked searches all need a store that does not
    // exist. None of them is drawn.
    for absent in [ "pinned"; "saved search"; "frequently used"; "recently opened" ] do
        Assert.DoesNotContain(absent, shown)

// ---------------------------------------------------------------------------
// 10. keyboard
// ---------------------------------------------------------------------------

[<Fact>]
let ``the arrows walk the flattened list, crossing group boundaries`` () =
    let hits =
        [ hit EntityKind.Assignment "a1" "s" 1.0
          hit EntityKind.Note "n1" "s" 1.0
          hit EntityKind.Note "n2" "s" 1.0 ]

    let model = withResults hits

    let down n =
        List.fold (fun m _ -> SearchView.update noServices (SearchView.MoveSelection 1) m |> modelOf) model [ 1..n ]

    Assert.Equal(Some 0, (down 1).Selected)
    Assert.Equal(Some 1, (down 2).Selected)
    // Index 1 is in a different group from index 0 — the walk crosses it.
    Assert.Equal(Some 2, (down 3).Selected)

    // And it stops at the end rather than wrapping into nothing.
    Assert.Equal(Some 2, (down 9).Selected)

[<Fact>]
let ``moving up from nothing selects the last result`` () =
    Assert.Equal(Some 2, SearchLogic.moveSelection 3 -1 None)
    Assert.Equal(Some 0, SearchLogic.moveSelection 3 1 None)
    Assert.Equal(None, SearchLogic.moveSelection 0 1 None)
    Assert.Equal(Some 0, SearchLogic.moveSelection 3 -1 (Some 0))

[<Fact>]
let ``pointer and keyboard produce the same state`` () =
    let hits = [ hit EntityKind.Note "n1" "s" 1.0; hit EntityKind.Note "n2" "s" 1.0 ]
    let model = withResults hits

    let byPointer = SearchView.update noServices (SearchView.Select 1) model |> modelOf

    let byKeyboard =
        SearchView.update noServices (SearchView.MoveSelection 1) model
        |> modelOf
        |> fun m -> SearchView.update noServices (SearchView.MoveSelection 1) m |> modelOf

    Assert.Equal(byPointer.Selected, byKeyboard.Selected)

[<Fact>]
let ``Escape clears the selection first, then the query`` () =
    let hits = [ hit EntityKind.Note "n" "s" 1.0 ]
    let selected = { withResults hits with Selected = Some 0 }

    let firstEscape = SearchView.update noServices SearchView.ClearSelection selected |> modelOf
    Assert.True(firstEscape.Selected.IsNone)
    Assert.Equal("compiler", firstEscape.Query)

    let secondEscape = SearchView.update noServices SearchView.ClearQuery firstEscape |> modelOf
    Assert.Equal("", secondEscape.Query)

[<Fact>]
let ``the search box is reachable and named`` () =
    let root = render (ready ())
    Assert.Contains("Search", accessibleNames root)
    Assert.Contains(SearchLogic.watermark, watermarks root)

[<Fact>]
let ``the shortcut badge names the shortcut that actually works`` () =
    // Ctrl+K is the command palette. Ctrl+F is what brings you here.
    Assert.Equal("Ctrl+F", SearchLogic.shortcutBadge)

    let root = render (ready ())
    Assert.True(says "Ctrl+F" root)
    Assert.False(says "⌘K" root)

// ---------------------------------------------------------------------------
// 11. states
// ---------------------------------------------------------------------------

[<Fact>]
let ``the input keeps one height in every state`` () =
    let heights =
        [ ready ()
          { ready () with Query = "co"; Searching = true }
          withResults [ hit EntityKind.Note "n" "s" 1.0 ]
          { ready () with Query = "co"; Error = Some "the index is locked" } ]
        |> List.map (fun m ->
            render m
            |> all
            |> List.choose (floatProp "MinHeight")
            |> List.filter (fun h -> h = SearchLogic.Geometry.inputMinHeight))

    Assert.All(heights, fun h -> Assert.Single h |> ignore)

[<Fact>]
let ``the empty state keeps the screen's structure`` () =
    let root = render (ready ())

    // Input, filters and the scope statement are all still there — the screen
    // does not collapse into one detached card.
    Assert.Contains(SearchLogic.watermark, watermarks root)
    Assert.True(says "All" root)
    Assert.True(says "WHAT IS SEARCHED" root)

[<Fact>]
let ``a too-short query says the rule and keeps the empty state`` () =
    let root = render { ready () with Query = "c" }

    Assert.True(says SearchLogic.tooShortNotice root)
    Assert.True(says "WHAT IS SEARCHED" root)

[<Fact>]
let ``a search in progress says so`` () =
    Assert.True(says "Searching…" (render { ready () with Query = "compiler"; Searching = true }))

[<Fact>]
let ``no results explains the matching rule rather than just shrugging`` () =
    let root = render { ready () with Query = "zebra"; Searching = false }

    Assert.True(says "No matches for \"zebra\"." root)
    Assert.True(says "Whole words are matched" root)

[<Fact>]
let ``a filter that hides everything says so, and how to undo it`` () =
    let model = { withResults [ hit EntityKind.Note "n" "s" 1.0 ] with Scope = Set.ofList [ EntityKind.Document ] }
    let root = render model

    Assert.True(says "Every match is hidden by the filters above" root)
    Assert.True(says "Choose All" root)

[<Fact>]
let ``a failed search is reported and offers a retry`` () =
    let failed = SearchView.update noServices (SearchView.Failed(exn "the index is locked")) (withResults []) |> modelOf

    Assert.Equal(Some "the index is locked", failed.Error)
    Assert.False(failed.Searching)

    let root = render failed
    Assert.True(says "The search could not be run: the index is locked" root)
    Assert.True(says "Try again" root)

[<Fact>]
let ``a failure keeps the query, so the retry has something to run`` () =
    let model = { ready () with Query = "compiler"; Searching = true }
    let failed = SearchView.update noServices (SearchView.Failed(exn "offline")) model |> modelOf
    Assert.Equal("compiler", failed.Query)

// ---------------------------------------------------------------------------
// 12. responsive
// ---------------------------------------------------------------------------

[<Fact>]
let ``the preview is re-placed by width, never removed`` () =
    Assert.Equal(SearchLogic.PreviewPlacement.Rail, SearchLogic.previewPlacement 1600.0)
    Assert.Equal(SearchLogic.PreviewPlacement.Below, SearchLogic.previewPlacement 1000.0)
    Assert.Equal(SearchLogic.PreviewPlacement.Inline, SearchLogic.previewPlacement 700.0)

[<Fact>]
let ``the preview is reachable at every width`` () =
    for width in [ 1600.0; 1000.0; 700.0 ] do
        let model = { withResults [ hit EntityKind.Note "a note" "s" 1.0 ] with Selected = Some 0; Width = width }
        Assert.True(says "SELECTED RESULT" (render model), string width)

[<Fact>]
let ``only the wide layout gives the preview its own column`` () =
    let model = { withResults [ hit EntityKind.Note "n" "s" 1.0 ] with Selected = Some 0 }

    let hasRail (width: float) =
        render { model with Width = width }
        |> all
        |> List.exists (fun v ->
            v.ViewType.Name = "Grid"
            && (propOf "ColumnDefinitions" v
                |> Option.map (fun d -> d.ToString().Contains(string (int SearchLogic.Geometry.previewWidth)))
                |> Option.defaultValue false))

    Assert.True(hasRail 1600.0)
    Assert.False(hasRail 1000.0)
    Assert.False(hasRail 700.0)

[<Fact>]
let ``nothing in the screen scrolls sideways, at any width`` () =
    for width in [ 1600.0; 1000.0; 700.0 ] do
        let model = { withResults [ hit EntityKind.Note "n" "s" 1.0 ] with Selected = Some 0; Width = width }
        let viewers = ofType "ScrollViewer" (render model)

        Assert.NotEmpty viewers

        for viewer in viewers do
            Assert.Equal(Some(box ScrollBarVisibility.Disabled), propOf "HorizontalScrollBarVisibility" viewer)

[<Fact>]
let ``the results column keeps the redesign's measure`` () =
    Assert.Equal(900.0, SearchLogic.Geometry.columnWidth, 6)

    let caps =
        render (withResults [ hit EntityKind.Note "n" "s" 1.0 ])
        |> all
        |> List.choose (floatProp "MaxWidth")

    Assert.Contains(SearchLogic.Geometry.columnWidth, caps)

// ---------------------------------------------------------------------------
// 13. composition
// ---------------------------------------------------------------------------

[<Fact>]
let ``the page sections are the mapped ones, in order`` () =
    Assert.Equal<SearchLogic.Section list>(
        [ SearchLogic.Section.Input
          SearchLogic.Section.Filters
          SearchLogic.Section.Summary
          SearchLogic.Section.Results
          SearchLogic.Section.Preview ],
        SearchLogic.sectionOrder
    )

[<Fact>]
let ``the empty state runs suggestions, recent, activity, scope`` () =
    Assert.Equal<SearchLogic.EmptyRegion list>(
        [ SearchLogic.EmptyRegion.Suggestions
          SearchLogic.EmptyRegion.Recent
          SearchLogic.EmptyRegion.Activity
          SearchLogic.EmptyRegion.Scope ],
        SearchLogic.emptyOrder
    )

    let model =
        { ready () with
            Suggestions = [ "CS4001" ]
            Recent = [ "compiler" ]
            Activity =
                [ { Id = Guid.NewGuid()
                    Kind = ActivityKind.NoteUpdated
                    Title = "Edited"
                    Detail = None
                    Entity = Some { Kind = EntityKind.Note; Id = Guid.NewGuid() }
                    OccurredAt = now } ] }

    let ordered = words (render model)

    let indexOf (needle: string) =
        ordered |> List.findIndex (fun s -> s.Contains(needle, StringComparison.Ordinal))

    Assert.True(indexOf "TRY SEARCHING FOR" < indexOf "RECENT SEARCHES")
    Assert.True(indexOf "RECENT SEARCHES" < indexOf "RECENT ACTIVITY")
    Assert.True(indexOf "RECENT ACTIVITY" < indexOf "WHAT IS SEARCHED")

[<Fact>]
let ``the search input is docked, so it never scrolls away`` () =
    // The input sits above the scroll viewer, not inside it.
    let root = render (withResults (List.init 30 (fun i -> hit EntityKind.Note (sprintf "note %d" i) "s" 1.0)))

    let insideAScroller =
        ofType "ScrollViewer" root |> List.exists (fun v -> watermarks v |> List.contains SearchLogic.watermark)

    Assert.False insideAScroller
    Assert.Contains(SearchLogic.watermark, watermarks root)

[<Fact>]
let ``no command palette is duplicated inside Search`` () =
    // The shell already owns one, on Ctrl+K. A second implementation here would
    // diverge from it the first time a page was added.
    let root = render (ready ())
    let shown = String.concat " " (words root) |> fun s -> s.ToLowerInvariant()

    Assert.DoesNotContain("commands", shown)
    Assert.DoesNotContain("run command", shown)

[<Fact>]
let ``Search draws its own glass, so the shell must not put a surface behind it`` () =
    Assert.True(Page.usesGlassSurface Page.Search)

// ---------------------------------------------------------------------------
// 14. Dark and Light, every accent
// ---------------------------------------------------------------------------

let private themeModes = [ ThemeMode.Dark; ThemeMode.Light ]
let private accentKeys = Palette.accents |> List.map fst

let private washedBy (alphaHex: string) (colour: Color) (surface: Color) =
    let a = Int32.Parse(alphaHex, Globalization.NumberStyles.HexNumber)
    Palette.composite surface (Color.FromArgb(byte a, colour.R, colour.G, colour.B))

[<Fact>]
let ``a highlighted match is readable on the card it sits on`` () =
    for mode in themeModes do
        for key in accentKeys do
            let accent = Palette.ofKey key
            let s = Surfaces.resolve mode accent

            let wash = washedBy SearchLogic.Geometry.highlightAlpha accent.Normal s.Card
            let ink = Palette.readableOn wash s.Text accent.Normal

            Assert.True(
                Palette.contrastRatio ink wash >= Palette.aaText,
                sprintf "%A/%s highlight: %.2f" mode key (Palette.contrastRatio ink wash)
            )

[<Fact>]
let ``a highlighted match stays readable on a SELECTED card too`` () =
    // The selected card is already an accent wash; a highlight on top of it is a
    // second wash, and the ink must survive both.
    for mode in themeModes do
        for key in accentKeys do
            let accent = Palette.ofKey key
            let s = Surfaces.resolve mode accent

            let selected = washedBy SearchLogic.Geometry.selectedFillAlpha accent.Normal s.Card
            let both = washedBy SearchLogic.Geometry.highlightAlpha accent.Normal selected
            let ink = Palette.readableOn both s.Text accent.Normal

            Assert.True(
                Palette.contrastRatio ink both >= Palette.aaText,
                sprintf "%A/%s highlight on selected: %.2f" mode key (Palette.contrastRatio ink both)
            )

[<Fact>]
let ``a selected card is distinguishable from an unselected one`` () =
    for mode in themeModes do
        for key in accentKeys do
            let accent = Palette.ofKey key
            let s = Surfaces.resolve mode accent

            let selected = washedBy SearchLogic.Geometry.selectedFillAlpha accent.Normal s.Card
            Assert.True(Palette.contrastRatio selected s.Card > 1.0, sprintf "%A/%s selection invisible" mode key)

            let edge = washedBy SearchLogic.Geometry.selectedEdgeAlpha accent.Normal s.Card
            Assert.True(Palette.contrastRatio edge selected > 1.0, sprintf "%A/%s selection edge" mode key)

[<Fact>]
let ``a filter chip's ink is readable on its own wash, every kind`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for kind in SearchLogic.searchableKinds do
                let raw = Color.Parse(Theme.entityKindHex kind)
                let wash = washedBy SearchLogic.Geometry.chipFillAlpha raw s.Card
                let ink = Palette.readableOn wash s.Text raw

                Assert.True(
                    Palette.contrastRatio ink wash >= Palette.aaText,
                    sprintf "%A/%s %A chip: %.2f" mode key kind (Palette.contrastRatio ink wash)
                )

[<Fact>]
let ``a kind glyph on its tile is readable, every kind`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for kind in SearchLogic.searchableKinds do
                let raw = Color.Parse(Theme.entityKindHex kind)
                let ink = Palette.readableOn s.CardAlt s.Text raw

                Assert.True(
                    Palette.contrastRatio ink s.CardAlt >= Palette.aaText,
                    sprintf "%A/%s %A tile: %.2f" mode key kind (Palette.contrastRatio ink s.CardAlt)
                )

[<Fact>]
let ``snippets and metadata are readable on a card`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            Assert.True(
                Palette.contrastRatio s.Text s.Card >= Palette.aaText,
                sprintf "%A/%s card ink: %.2f" mode key (Palette.contrastRatio s.Text s.Card)
            )

            Assert.True(
                Palette.contrastRatio s.Text s.CardAlt >= Palette.aaText,
                sprintf "%A/%s matched-text panel: %.2f" mode key (Palette.contrastRatio s.Text s.CardAlt)
            )

[<Fact>]
let ``the error banner is readable on its own wash`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)
            let wash = washedBy "22" s.Danger s.Card
            let ink = Palette.readableOn wash s.Text s.Danger

            Assert.True(
                Palette.contrastRatio ink wash >= Palette.aaText,
                sprintf "%A/%s error: %.2f" mode key (Palette.contrastRatio ink wash)
            )

[<Fact>]
let ``the surfaces stay warm in both themes`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, c in [ "card", s.Card; "cardAlt", s.CardAlt; "action", s.Action ] do
                Assert.True(int c.R >= int c.B, sprintf "%A/%s %s is cool: %A" mode key name c)

// ---------------------------------------------------------------------------
// 15. mutation guards
// ---------------------------------------------------------------------------

[<Fact>]
let ``MUTATION filtering by entity kind that stops filtering is caught`` () =
    let hits = [ hit EntityKind.Note "n" "s" 1.0; hit EntityKind.Document "d" "s" 1.0 ]

    Assert.Equal(1, (SearchLogic.applyScope (Set.ofList [ EntityKind.Note ]) hits).Length)
    Assert.Equal(2, (SearchLogic.applyScope SearchLogic.Scope.all hits).Length)

    let filtered = render { withResults hits with Scope = Set.ofList [ EntityKind.Note ] }
    Assert.False(says "DOCUMENTS" filtered)
    Assert.True(says "NOTES" filtered)

[<Fact>]
let ``MUTATION a changed group order is caught`` () =
    Assert.Equal<EntityKind list>(SearchLogic.searchableKinds, SearchLogic.groupOrder)

    let hits =
        [ hit EntityKind.RevisionItem "r" "s" 1.0
          hit EntityKind.Assignment "a" "s" 1.0
          hit EntityKind.Note "n" "s" 1.0 ]

    Assert.Equal<EntityKind list>(
        [ EntityKind.Assignment; EntityKind.Note; EntityKind.RevisionItem ],
        SearchLogic.groupResults hits |> List.map fst
    )

[<Fact>]
let ``MUTATION drawing empty groups is caught`` () =
    let grouped = SearchLogic.groupResults [ hit EntityKind.Note "n" "s" 1.0 ]
    Assert.Single grouped |> ignore

    let root = render (withResults [ hit EntityKind.Note "n" "s" 1.0 ])

    for kind in SearchLogic.searchableKinds do
        if kind <> EntityKind.Note then
            Assert.False(says ((SearchLogic.groupLabel kind).ToUpperInvariant()) root, string kind)

[<Fact>]
let ``MUTATION a selection that survives its result is caught`` () =
    let noteId = Guid.NewGuid()
    let hits = [ keyed EntityKind.Note "the note" noteId ]

    Assert.Equal(Some 0, SearchLogic.reconcileSelection (Some(List.head hits)) hits)

    // The same title, a different entity: not the same result.
    let other = [ keyed EntityKind.Note "the note" (Guid.NewGuid()) ]
    Assert.Equal(None, SearchLogic.reconcileSelection (Some(List.head hits)) other)

    // And gone entirely.
    Assert.Equal(None, SearchLogic.reconcileSelection (Some(List.head hits)) [])

[<Fact>]
let ``MUTATION re-sorting the engine's ranking is caught`` () =
    // The screen must never re-sort: bm25 order IS the ranking.
    let hits =
        [ hit EntityKind.Note "low score first" "s" 0.1
          hit EntityKind.Note "high score second" "s" 99.0 ]

    let grouped = SearchLogic.groupResults hits

    Assert.Equal<string list>(
        [ "low score first"; "high score second" ],
        snd grouped.Head |> List.map (fun h -> h.Title)
    )

    // And only the first is the best match.
    Assert.Equal(Some 0, SearchLogic.bestMatchIndex (SearchLogic.flatten grouped))

[<Fact>]
let ``MUTATION broken match highlighting is caught`` () =
    // Substring matching (what a naive implementation does) would mark "raph".
    Assert.DoesNotContain(SearchLogic.segments "raph" "Graph colouring", fun p -> p.Matched)

    // Case folding must not be dropped.
    Assert.Contains(SearchLogic.segments "graph" "Graph colouring", fun p -> p.Matched)

    // And the text must survive intact.
    let rebuilt =
        SearchLogic.segments "graph" "Graph colouring" |> List.map (fun p -> p.Text) |> String.concat ""

    Assert.Equal("Graph colouring", rebuilt)

[<Fact>]
let ``MUTATION an Assistant handoff that overclaims is caught`` () =
    Assert.Contains("Nothing is attached", SearchLogic.assistantNote)

    let prompt = SearchLogic.assistantPrompt EntityKind.Document "Lecture 3 slides"
    Assert.Contains("Lecture 3 slides", prompt)

    // The prompt is a question in words, not a claim about what was sent.
    let lower = prompt.ToLowerInvariant()

    for absent in [ "attached"; "context"; "i have read"; "using the document" ] do
        Assert.DoesNotContain(absent, lower)

[<Fact>]
let ``MUTATION responsive preview movement that stops moving is caught`` () =
    let placements = [ 1600.0; 1000.0; 700.0 ] |> List.map SearchLogic.previewPlacement

    Assert.Equal<SearchLogic.PreviewPlacement list>(
        [ SearchLogic.PreviewPlacement.Rail
          SearchLogic.PreviewPlacement.Below
          SearchLogic.PreviewPlacement.Inline ],
        placements
    )

    Assert.Equal(3, (List.distinct placements).Length)

    // And the rendered tree really changes with it.
    let model = { withResults [ hit EntityKind.Note "n" "s" 1.0 ] with Selected = Some 0 }

    let columnCount (width: float) =
        render { model with Width = width }
        |> all
        |> List.filter (fun v -> v.ViewType.Name = "Grid" && (propOf "ColumnDefinitions" v |> Option.isSome))
        |> List.length

    Assert.True(columnCount 1600.0 > columnCount 700.0)

// ---------------------------------------------------------------------------
// 16. the shell contract
// ---------------------------------------------------------------------------

[<Fact>]
let ``every migrated screen still draws its own glass`` () =
    for page in
        [ Page.Timeline
          Page.Dashboard
          Page.Planner
          Page.Inbox
          Page.Assignments
          Page.Modules
          Page.Lectures
          Page.Notes
          Page.Documents
          Page.Graph
          Page.Chat
          Page.Search
          Page.Calendar ] do
        Assert.True(Page.usesGlassSurface page, sprintf "%A lost its glass surface" page)

    // Still to migrate.
    for page, _, _ in Page.all do
        Assert.True(Page.usesGlassSurface page, sprintf "%A lost its glass surface" page)

// ---------------------------------------------------------------------------
// 17. performance fixtures
// ---------------------------------------------------------------------------
//
// What is measured is what this harness can measure: the pure decision cost
// (filter, group, highlight) and the cost of BUILDING the view tree. No FPS
// figure is claimed — no Avalonia application runs here, and no repository is
// touched, which is itself the point: rendering must never reach one.

let private manyHits (count: int) =
    List.init count (fun i ->
        let kind = SearchLogic.searchableKinds.[i % SearchLogic.searchableKinds.Length]

        { Kind = kind
          Id = Guid.NewGuid()
          Title = sprintf "Compiler construction result number %d" i
          Snippet = sprintf "… a matching excerpt about compiler construction, result %d …" i
          Score = float (count - i) })

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

[<Fact>]
let ``Search fixtures stay inside their budget`` () =
    let report = System.Text.StringBuilder()
    report.AppendLine "Search fixtures" |> ignore
    report.AppendLine "fixture | median ms | KB per run" |> ignore

    let record name runs work =
        let ms, kb = measure runs work
        report.AppendLine(sprintf "%-42s | %8.3f | %9.1f" name ms kb) |> ignore
        Assert.True(ms < 60.0, sprintf "%s took %.2f ms" name ms)

    // --- decision cost -----------------------------------------------------
    let five hundred = manyHits hundred

    record "decide: group+flatten 500 hits" 50 (fun () ->
        SearchLogic.groupResults (five 500) |> SearchLogic.flatten |> List.length)

    record "decide: filter 500 hits to one kind" 50 (fun () ->
        (five 500) |> SearchLogic.applyScope (Set.ofList [ EntityKind.Note ]) |> List.length)

    record "decide: counts across 500 hits" 50 (fun () -> SearchLogic.countsByKind (five 500) |> List.length)

    record "decide: highlight a long snippet" 200 (fun () ->
        (SearchLogic.segments "compiler construction" (String.replicate 40 "compiler construction and more text ")).Length)

    // --- view construction -------------------------------------------------
    let build (model: SearchView.Model) () = (all (render model)).Length

    record "view: no results" 20 (build { ready () with Query = "zebra" })
    record "view: empty state" 20 (build (ready ()))
    record "view: 10 results" 20 (build (withResults (manyHits 10)))
    record "view: 100 results" 20 (build (withResults (manyHits 100)))
    record "view: 500 results" 10 (build (withResults (manyHits 500)))

    record "view: 500 results, one selected" 10 (build { withResults (manyHits 500) with Selected = Some 3 })

    record "view: all seven groups" 20 (build (withResults (manyHits 70)))

    record
        "view: long snippets"
        20
        (build (
            withResults (
                List.init 40 (fun i ->
                    { Kind = EntityKind.Document
                      Id = Guid.NewGuid()
                      Title = String.replicate 12 "verylongtitleword"
                      Snippet = String.replicate 30 "a long matching excerpt of extracted text "
                      Score = float i })
            )
        ))

    record "view: rapid filter change" 20 (fun () ->
        let model = withResults (manyHits 200)

        [ EntityKind.Note; EntityKind.Document; EntityKind.Assignment ]
        |> List.sumBy (fun k -> (all (render { model with Scope = Set.ofList [ k ] })).Length))

    record "view: repeated identical query" 20 (fun () ->
        let model = withResults (manyHits 100)
        (all (render model)).Length + (all (render model)).Length)

    record "decide: stale-reply rejection" 200 (fun () ->
        let model = { ready () with Query = "compiler"; Generation = 9 }
        let after = SearchView.update noServices (SearchView.Results(4, manyHits 100)) model |> modelOf
        after.All.Length)

    IO.File.WriteAllText(IO.Path.Combine(IO.Path.GetTempPath(), "search-fixtures.txt"), report.ToString())
