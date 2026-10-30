module UniversityOS.Tests.DocumentsViewTests

open System
open Xunit
open Avalonia.Media
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Features

/// The rebuilt Documents screen.
///
/// Every decision lives in `DocumentsLogic` or in `DocumentsView.update`, so all
/// of it is testable without an Avalonia application, a database or a filesystem.

let private now = DateTime(2026, 8, 1, 14, 0, 0)

let private moduleOf code name : StudyModule =
    { StudyModule.create (now.AddDays -60.0) code name 0 with Color = "#CB8A4E" }

let private documentOf title kind : Document =
    { Id = DocumentId.New()
      ModuleId = None
      Title = title
      FileName = title + ".pdf"
      StoredPath = "C:\\store\\" + title + ".pdf"
      Kind = kind
      SizeBytes = 2048L
      ExtractedText = None
      Summary = None
      ImportedAt = now.AddDays -1.0 }

let private facts (d: Document) : DocumentsLogic.Facts = DocumentsLogic.Facts.ofDocument d

/// Long enough to clear the summarise floor.
let private realText = String.replicate 30 "extracted content. "

let private refOf (kind: EntityKind) = { Kind = kind; Id = Guid.NewGuid() }

// ---------------------------------------------------------------------------
// 1. composition
// ---------------------------------------------------------------------------

[<Fact>]
let ``the page is the redesign's eight sections, in order`` () =
    Assert.Equal<DocumentsLogic.Section list>(
        [ DocumentsLogic.Section.Header
          DocumentsLogic.Section.DropZone
          DocumentsLogic.Section.Metrics
          DocumentsLogic.Section.Insights
          DocumentsLogic.Section.Controls
          DocumentsLogic.Section.Filters
          DocumentsLogic.Section.ModuleFilters
          DocumentsLogic.Section.Content ],
        DocumentsLogic.sectionOrder
    )

    Assert.Equal(DocumentsLogic.sectionOrder.Length, (List.distinct DocumentsLogic.sectionOrder).Length)

[<Fact>]
let ``the detail panel runs the redesign's regions, in order`` () =
    Assert.Equal<DocumentsLogic.DetailRegion list>(
        [ DocumentsLogic.DetailRegion.Preview
          DocumentsLogic.DetailRegion.MissingWarning
          DocumentsLogic.DetailRegion.Metadata
          DocumentsLogic.DetailRegion.AiSummary
          DocumentsLogic.DetailRegion.ExtractedText
          DocumentsLogic.DetailRegion.Pipeline
          DocumentsLogic.DetailRegion.Related
          DocumentsLogic.DetailRegion.GraphNodes
          DocumentsLogic.DetailRegion.History
          DocumentsLogic.DetailRegion.Actions ],
        DocumentsLogic.detailOrder
    )

    // The missing-file warning sits directly under the preview, so a broken
    // document explains itself before any metadata is read.
    Assert.Equal(1, List.findIndex ((=) DocumentsLogic.DetailRegion.MissingWarning) DocumentsLogic.detailOrder)

[<Fact>]
let ``the analytics row is the redesign's six cards`` () =
    Assert.Equal(6, (DocumentsLogic.metrics now [ facts (documentOf "One" DocumentKind.Pdf) ]).Length)

[<Fact>]
let ``the filter row is ten deterministic chips`` () =
    Assert.Equal(10, DocumentsLogic.Filter.all.Length)

    let labels = DocumentsLogic.Filter.all |> List.map DocumentsLogic.Filter.label
    Assert.Equal(labels.Length, (List.distinct labels).Length)
    Assert.All(labels, fun l -> Assert.False(String.IsNullOrWhiteSpace l))

[<Fact>]
let ``both view modes exist, as the redesign offers both`` () =
    Assert.Equal("Grid", DocumentsLogic.ViewMode.label DocumentsLogic.ViewMode.Grid)
    Assert.Equal("List", DocumentsLogic.ViewMode.label DocumentsLogic.ViewMode.List)

// ---------------------------------------------------------------------------
// 2. the import truth — both halves
// ---------------------------------------------------------------------------

[<Fact>]
let ``the drop wording states immediate import AND the Inbox review step`` () =
    // Traced through Shell.subscriptions -> CaptureService.CaptureFile: the
    // Document is created at once, and a capture also goes to the Inbox.
    let text = DocumentsLogic.dropZoneMessage

    Assert.Contains("immediately", text)
    Assert.Contains("Inbox", text)
    Assert.Contains("review", text)

[<Fact>]
let ``the drop wording never claims automatic filing`` () =
    let text = DocumentsLogic.dropZoneMessage.ToLowerInvariant()

    Assert.DoesNotContain("automatically", text)
    Assert.DoesNotContain("automatic", text)

    // It must say filing waits for the user.
    Assert.Contains("until you accept", text)

[<Fact>]
let ``the import notice repeats the Inbox review step`` () =
    let one = DocumentsLogic.importedNotice 1
    Assert.Contains("1 file imported", one)
    Assert.Contains("Inbox", one)

    Assert.Contains("3 files imported", DocumentsLogic.importedNotice 3)

[<Fact>]
let ``the empty-library message states both halves too`` () =
    let text = DocumentsLogic.emptyMessage DocumentsLogic.Selection.empty Set.empty "" 0
    Assert.Contains("straight away", text)
    Assert.Contains("Inbox", text)

// ---------------------------------------------------------------------------
// 3. missing-file handling
// ---------------------------------------------------------------------------

[<Fact>]
let ``a present file is not missing, and a missing one is`` () =
    let present = facts (documentOf "Deck" DocumentKind.Pdf)
    Assert.False(DocumentsLogic.isMissing present)

    let gone = { present with FileExists = false }
    Assert.True(DocumentsLogic.isMissing gone)

[<Fact>]
let ``a missing file fails its store stage rather than sitting pending`` () =
    let gone = { facts (documentOf "Deck" DocumentKind.Pdf) with FileExists = false }

    Assert.Equal(DocumentsLogic.StageState.Failed, DocumentsLogic.stageState gone DocumentsLogic.Stage.Stored)
    Assert.Equal("!", DocumentsLogic.StageState.mark DocumentsLogic.StageState.Failed)
    Assert.Equal("failed", DocumentsLogic.StageState.label DocumentsLogic.StageState.Failed)

[<Fact>]
let ``a missing file cannot be summarised, and the reason says why`` () =
    let gone =
        { facts { documentOf "Deck" DocumentKind.Pdf with ExtractedText = Some realText } with FileExists = false }

    let reason = DocumentsLogic.summariseDisabledReason gone
    Assert.True(reason.IsSome)
    Assert.Contains("missing", reason.Value)

[<Fact>]
let ``a missing file sorts to the front, because it is the state to act on`` () =
    let fine = facts (documentOf "Fine" DocumentKind.Pdf)
    let gone = { facts (documentOf "Gone" DocumentKind.Pdf) with FileExists = false }

    let shown =
        DocumentsLogic.visible now DocumentsLogic.Selection.empty Set.empty "" [ fine; gone ]
        |> List.map (fun f -> f.Document.Title)

    Assert.Equal<string list>([ "Gone"; "Fine" ], shown)

[<Fact>]
let ``the status label names the missing file rather than a count`` () =
    let gone = { facts (documentOf "Deck" DocumentKind.Pdf) with FileExists = false }
    Assert.Equal("File missing", DocumentsLogic.statusLabel gone)

    let fine = facts (documentOf "Deck" DocumentKind.Pdf)
    Assert.Contains("of", DocumentsLogic.statusLabel fine)

// ---------------------------------------------------------------------------
// 4. labelling honesty
// ---------------------------------------------------------------------------

[<Fact>]
let ``AI SUMMARY is used only when Document.Summary is filled`` () =
    // DocumentImportService sets Summary to None; only Ai.Summarize fills it.
    let bare = facts (documentOf "Deck" DocumentKind.Pdf)
    let source, _ = DocumentsLogic.blurb bare
    Assert.Equal(DocumentsLogic.BlurbSource.None', source)

    let extracted = facts { documentOf "Deck" DocumentKind.Pdf with ExtractedText = Some realText }
    let source, _ = DocumentsLogic.blurb extracted
    Assert.Equal(DocumentsLogic.BlurbSource.Extracted, source)
    Assert.Equal("EXTRACTED TEXT", DocumentsLogic.BlurbSource.label source)

    let summarised =
        facts { documentOf "Deck" DocumentKind.Pdf with ExtractedText = Some realText; Summary = Some "the summary" }

    let source, text = DocumentsLogic.blurb summarised
    Assert.Equal(DocumentsLogic.BlurbSource.Ai, source)
    Assert.Equal("AI SUMMARY", DocumentsLogic.BlurbSource.label source)
    Assert.Contains("the summary", text)

[<Fact>]
let ``extracted text is never labelled as an AI summary`` () =
    let extracted = facts { documentOf "Deck" DocumentKind.Pdf with ExtractedText = Some realText }
    let source, _ = DocumentsLogic.blurb extracted
    Assert.NotEqual<string>("AI SUMMARY", DocumentsLogic.BlurbSource.label source)

[<Fact>]
let ``no metric claims a page count, confidence or processing time`` () =
    let labels = DocumentsLogic.metrics now [] |> List.map (fun m -> m.Label.ToLowerInvariant())

    for banned in [ "page"; "confidence"; "ocr"; "processing time"; "accuracy"; "opened" ] do
        Assert.All(labels, fun l -> Assert.DoesNotContain(banned, l))

// ---------------------------------------------------------------------------
// 5. the processing pipeline
// ---------------------------------------------------------------------------

[<Fact>]
let ``the pipeline is six real stages, each with its own words`` () =
    Assert.Equal(6, DocumentsLogic.Stage.all.Length)

    let labels = DocumentsLogic.Stage.all |> List.map DocumentsLogic.Stage.label
    Assert.Equal(labels.Length, (List.distinct labels).Length)

[<Fact>]
let ``every stage state carries a text mark, not only a colour`` () =
    for state in
        [ DocumentsLogic.StageState.Done
          DocumentsLogic.StageState.Pending
          DocumentsLogic.StageState.Unavailable
          DocumentsLogic.StageState.Failed ] do
        Assert.False(String.IsNullOrWhiteSpace(DocumentsLogic.StageState.mark state))
        Assert.False(String.IsNullOrWhiteSpace(DocumentsLogic.StageState.label state))

[<Fact>]
let ``text extraction is unavailable for images and audio, not merely pending`` () =
    // An image has no text to extract; no action can change that, so calling it
    // "pending" would promise something that can never happen.
    let image = facts (documentOf "Scan" DocumentKind.Image)

    Assert.False(DocumentsLogic.canExtractText DocumentKind.Image)
    Assert.False(DocumentsLogic.canExtractText DocumentKind.Audio)
    Assert.True(DocumentsLogic.canExtractText DocumentKind.Pdf)

    Assert.Equal(
        DocumentsLogic.StageState.Unavailable,
        DocumentsLogic.stageState image DocumentsLogic.Stage.TextExtracted
    )

    let pdf = facts (documentOf "Deck" DocumentKind.Pdf)
    Assert.Equal(DocumentsLogic.StageState.Pending, DocumentsLogic.stageState pdf DocumentsLogic.Stage.TextExtracted)

[<Fact>]
let ``an image's summary stage is unavailable rather than pending`` () =
    let image = facts (documentOf "Scan" DocumentKind.Image)

    Assert.Equal(
        DocumentsLogic.StageState.Unavailable,
        DocumentsLogic.stageState image DocumentsLogic.Stage.Summarised
    )

    let reason = DocumentsLogic.summariseDisabledReason image
    Assert.True(reason.IsSome)
    Assert.Contains("No text can be extracted", reason.Value)

[<Fact>]
let ``a pending Inbox capture is pending, and a reviewed one is done`` () =
    let pending = { facts (documentOf "Deck" DocumentKind.Pdf) with InboxPending = true }

    Assert.Equal(
        DocumentsLogic.StageState.Pending,
        DocumentsLogic.stageState pending DocumentsLogic.Stage.InboxCapture
    )

    let reviewed = facts (documentOf "Deck" DocumentKind.Pdf)
    Assert.Equal(DocumentsLogic.StageState.Done, DocumentsLogic.stageState reviewed DocumentsLogic.Stage.InboxCapture)

[<Fact>]
let ``a freshly imported document has exactly the stages it really has`` () =
    // Imported and stored are true; the Inbox capture is reviewed by default in
    // this fixture. Nothing else is.
    let bare = facts (documentOf "Deck" DocumentKind.Pdf)
    Assert.Equal(3, DocumentsLogic.completedStages bare)
    Assert.Equal(DocumentsLogic.StageState.Done, DocumentsLogic.stageState bare DocumentsLogic.Stage.Imported)

// ---------------------------------------------------------------------------
// 6. related knowledge
// ---------------------------------------------------------------------------

[<Fact>]
let ``a document with no relationships shows no categories at all`` () =
    Assert.Empty(DocumentsLogic.related (facts (documentOf "Deck" DocumentKind.Pdf)))

[<Fact>]
let ``only categories with something in them appear`` () =
    let m = moduleOf "CS2004" "Graphics"

    let linked =
        { facts { documentOf "Deck" DocumentKind.Pdf with ModuleId = Some m.Id } with
            Module = Some m
            LinksOut = [ refOf EntityKind.Note ]
            LectureCount = 2
            Flashcards = 5 }

    let found = DocumentsLogic.related linked |> Map.ofList

    Assert.Equal(1, found.[DocumentsLogic.RelatedKind.Modules])
    Assert.Equal(1, found.[DocumentsLogic.RelatedKind.Notes])
    Assert.Equal(2, found.[DocumentsLogic.RelatedKind.Lectures])
    Assert.Equal(5, found.[DocumentsLogic.RelatedKind.Flashcards])
    Assert.False(found.ContainsKey DocumentsLogic.RelatedKind.Assignments)

[<Fact>]
let ``related never returns a zero count, whatever the shape of the facts`` () =
    // The zero-count filter in `related` is a guard no current input reaches, so
    // this pins the invariant it protects across every shape instead of relying
    // on one example.
    let m = moduleOf "CS2004" "Graphics"
    let d = documentOf "Deck" DocumentKind.Pdf

    let shapes =
        [ facts d
          { facts d with LinksOut = [ refOf EntityKind.Note ] }
          { facts d with LinksIn = [ refOf EntityKind.Assignment ] }
          { facts d with LectureCount = 0 }
          { facts d with Flashcards = 0 }
          { facts d with LectureCount = 3; Flashcards = 2 }
          { facts { d with ModuleId = Some m.Id } with Module = Some m }
          { facts { d with ModuleId = Some m.Id } with
              Module = Some m
              LinksOut = [ refOf EntityKind.Note; refOf EntityKind.Document ]
              LinksIn = [ refOf EntityKind.Lecture ]
              LectureCount = 1
              Flashcards = 4 } ]

    for shape in shapes do
        for kind, count in DocumentsLogic.related shape do
            Assert.True(count > 0, sprintf "%A was rendered with a count of %d" kind count)

[<Fact>]
let ``every related kind opens a real page`` () =
    for kind in
        [ DocumentsLogic.RelatedKind.Modules
          DocumentsLogic.RelatedKind.Assignments
          DocumentsLogic.RelatedKind.Lectures
          DocumentsLogic.RelatedKind.Notes
          DocumentsLogic.RelatedKind.Documents
          DocumentsLogic.RelatedKind.Flashcards ] do
        Assert.Contains(DocumentsLogic.RelatedKind.page kind, Page.all |> List.map (fun (p, _, _) -> p))

[<Fact>]
let ``lecture attachments count as links even without a graph edge`` () =
    // Lecture.SlidesDocumentId is a real foreign key, not an edge.
    let attached = { facts (documentOf "Deck" DocumentKind.Pdf) with LectureCount = 1 }
    Assert.Equal(1, DocumentsLogic.linkCount attached)
    Assert.False(DocumentsLogic.isUnlinked attached)

// ---------------------------------------------------------------------------
// 7. metrics, insights and filters
// ---------------------------------------------------------------------------

[<Fact>]
let ``an empty screen reads as six honest zeroes`` () =
    Assert.Equal<(string * string option) list>(
        [ "documents", Some "0"
          "imported this week", Some "0"
          "with text", Some "0"
          "summarised", Some "0"
          "unfiled", Some "0"
          "missing files", Some "0" ],
        DocumentsLogic.metrics now [] |> List.map (fun m -> m.Label, m.Value)
    )

[<Fact>]
let ``the size total is a real sum of stored bytes`` () =
    let a = facts { documentOf "A" DocumentKind.Pdf with SizeBytes = 1024L }
    let b = facts { documentOf "B" DocumentKind.Pdf with SizeBytes = 1024L }
    Assert.Equal("2 KB", DocumentsLogic.totalSize [ a; b ])
    Assert.Equal("0 B", DocumentsLogic.totalSize [])

[<Fact>]
let ``insights are rules over stored fields, and a clean library gets none`` () =
    Assert.Empty(DocumentsLogic.insights now [])

    let m = moduleOf "CS2004" "Graphics"

    let clean =
        { facts
            { documentOf "Deck" DocumentKind.Pdf with
                ModuleId = Some m.Id
                ExtractedText = Some realText
                Summary = Some "done" } with
            Module = Some m }

    Assert.Empty(DocumentsLogic.insights now [ clean ])

[<Fact>]
let ``a missing file produces an insight that says the record survives`` () =
    let gone = { facts (documentOf "Deck" DocumentKind.Pdf) with FileExists = false }
    let lines = DocumentsLogic.insights now [ gone ] |> List.map snd

    Assert.Contains(lines, fun l -> l.Contains "missing from the store")
    Assert.Contains(lines, fun l -> l.Contains "links survive")

[<Fact>]
let ``every filter reads a real stored field`` () =
    let m = moduleOf "CS2004" "Graphics"

    let rich =
        { facts
            { documentOf "Deck" DocumentKind.Pdf with
                ModuleId = Some m.Id
                ExtractedText = Some realText
                Summary = Some "done"
                ImportedAt = now } with
            Module = Some m
            InboxPending = true }

    Assert.True(DocumentsLogic.matches now DocumentsLogic.Filter.RecentlyImported rich)
    Assert.True(DocumentsLogic.matches now DocumentsLogic.Filter.HasText rich)
    Assert.False(DocumentsLogic.matches now DocumentsLogic.Filter.NoText rich)
    Assert.True(DocumentsLogic.matches now DocumentsLogic.Filter.HasSummary rich)
    Assert.False(DocumentsLogic.matches now DocumentsLogic.Filter.NeedsSummary rich)
    Assert.False(DocumentsLogic.matches now DocumentsLogic.Filter.Unfiled rich)
    Assert.False(DocumentsLogic.matches now DocumentsLogic.Filter.Unlinked rich)
    Assert.False(DocumentsLogic.matches now DocumentsLogic.Filter.MissingFile rich)
    Assert.True(DocumentsLogic.matches now DocumentsLogic.Filter.InboxPending rich)
    Assert.True(DocumentsLogic.matches now DocumentsLogic.Filter.LinkedToModule rich)

[<Fact>]
let ``needs summary means it can be summarised, not merely that it lacks one`` () =
    // An image lacks a summary and can never have one, so it is not waiting.
    let image = facts (documentOf "Scan" DocumentKind.Image)
    Assert.False(DocumentsLogic.matches now DocumentsLogic.Filter.NeedsSummary image)

    let ready = facts { documentOf "Deck" DocumentKind.Pdf with ExtractedText = Some realText }
    Assert.True(DocumentsLogic.matches now DocumentsLogic.Filter.NeedsSummary ready)

[<Fact>]
let ``search matches title, filename and extracted text, case-insensitively`` () =
    let f = facts { documentOf "Graphics deck" DocumentKind.Pdf with ExtractedText = Some "rasterization notes" }

    Assert.True(DocumentsLogic.searchMatches "" f)
    Assert.True(DocumentsLogic.searchMatches "GRAPHICS" f)
    Assert.True(DocumentsLogic.searchMatches "deck.pdf" f)
    Assert.True(DocumentsLogic.searchMatches "rasterization" f)
    Assert.False(DocumentsLogic.searchMatches "kubernetes" f)

[<Fact>]
let ``the empty message explains each contradiction rather than leaving a blank grid`` () =
    let linkedAndUnlinked =
        DocumentsLogic.Selection.empty
        |> DocumentsLogic.Selection.toggle DocumentsLogic.Filter.Unlinked
        |> DocumentsLogic.Selection.toggle DocumentsLogic.Filter.LinkedToModule

    Assert.Contains("cannot be both", DocumentsLogic.emptyMessage linkedAndUnlinked Set.empty "" 3)

    let textAndNoText =
        DocumentsLogic.Selection.empty
        |> DocumentsLogic.Selection.toggle DocumentsLogic.Filter.HasText
        |> DocumentsLogic.Selection.toggle DocumentsLogic.Filter.NoText

    Assert.Contains("cannot both have and not have", DocumentsLogic.emptyMessage textAndNoText Set.empty "" 3)

[<Fact>]
let ``module chips list only modules that really have documents`` () =
    let m = moduleOf "CS2004" "Graphics"
    let filed = { facts { documentOf "Filed" DocumentKind.Pdf with ModuleId = Some m.Id } with Module = Some m }
    let loose = facts (documentOf "Loose" DocumentKind.Pdf)

    let chips = DocumentsLogic.moduleChips [ filed; loose ]
    Assert.Equal(1, chips.Length)
    Assert.Equal("CS2004", (fst chips.Head).Code)

// ---------------------------------------------------------------------------
// 8. layout and responsiveness
// ---------------------------------------------------------------------------

[<Fact>]
let ``the grid honours the redesign's 220px minimum card`` () =
    Assert.Equal(220.0, DocumentsLogic.Geometry.cardMinWidth, 6)
    Assert.Equal(1, DocumentsLogic.cardColumns 300.0)
    Assert.Equal(4, DocumentsLogic.cardColumns 900.0)
    Assert.True(DocumentsLogic.cardColumns 100.0 >= 1)

[<Fact>]
let ``card columns never go backwards as the page widens`` () =
    [ 320.0..40.0..2400.0 ]
    |> List.map DocumentsLogic.cardColumns
    |> List.pairwise
    |> List.iter (fun (a, b) -> Assert.True(b >= a, sprintf "columns went backwards: %d then %d" a b))

[<Fact>]
let ``the six metric cards reflow rather than shrinking below readability`` () =
    Assert.Equal(6, DocumentsLogic.metricColumns 1600.0)
    Assert.Equal(4, DocumentsLogic.metricColumns 1200.0)
    Assert.Equal(3, DocumentsLogic.metricColumns 800.0)
    Assert.Equal(2, DocumentsLogic.metricColumns 500.0)

[<Fact>]
let ``the detail panel sits beside the grid only when there is room for both`` () =
    Assert.True(DocumentsLogic.detailBeside 1600.0)
    Assert.False(DocumentsLogic.detailBeside 900.0)

    // Below the threshold it follows the selection instead, which is what keeps
    // the reading order intact and avoids a horizontal scrollbar.
    Assert.True(DocumentsLogic.Geometry.detailPanelWidth + DocumentsLogic.Geometry.cardMinWidth < DocumentsLogic.detailBesideWidth)

[<Fact>]
let ``geometry matches every other migrated screen`` () =
    Assert.Equal(0.5, DocumentsLogic.Geometry.borderInset, 6)
    Assert.Equal(NotesLogic.Geometry.borderInset, DocumentsLogic.Geometry.borderInset, 6)
    Assert.Equal(0.0, DocumentsLogic.Geometry.contentHighlight, 6)
    Assert.Equal(LecturesLogic.Geometry.actionMinHeight, DocumentsLogic.Geometry.actionMinHeight, 6)
    Assert.Equal(LecturesLogic.Geometry.actionRadius, DocumentsLogic.Geometry.actionRadius, 6)

[<Fact>]
let ``chips and cards reserve their outline in both states`` () =
    Assert.True(DocumentsLogic.Geometry.filterBorderThickness > 0.0)
    Assert.True(DocumentsLogic.Geometry.filterMinHeight > 0.0)
    Assert.True(DocumentsLogic.Geometry.cardBorderThickness > 0.0)

[<Fact>]
let ``wrapping text is given a finite column, never an infinite one`` () =
    Assert.Contains("*", DocumentsLogic.Geometry.bulletColumns)
    Assert.Contains("*", DocumentsLogic.Geometry.detailColumns)

// ---------------------------------------------------------------------------
// 9. update behaviour
// ---------------------------------------------------------------------------

let private noServices: AppServices = Unchecked.defaultof<AppServices>

let private baseModel: DocumentsView.Model =
    { Facts = []
      Modules = []
      Selected = None
      View = DocumentsLogic.ViewMode.Grid
      Filters = DocumentsLogic.Selection.empty
      ModuleFilter = Set.empty
      Query = ""
      ModuleIndex = 0
      Importing = false
      Summarizing = false
      PendingDelete = false
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

    let after =
        DocumentsView.update noServices (DocumentsView.Loaded([ facts (documentOf "One" DocumentKind.Pdf) ], [])) before
        |> modelOf

    Assert.False(after.Loading)
    Assert.Equal(None, after.Error)
    Assert.Equal(1, after.Facts.Length)

[<Fact>]
let ``a load drops a selection whose document has gone`` () =
    let gone = facts (documentOf "Gone" DocumentKind.Pdf)
    let before = { baseModel with Selected = Some gone.Document.Id; Facts = [ gone ] }

    let after =
        DocumentsView.update noServices (DocumentsView.Loaded([ facts (documentOf "Other" DocumentKind.Pdf) ], [])) before
        |> modelOf

    Assert.Equal(None, after.Selected)

[<Fact>]
let ``a failure clears every in-flight flag`` () =
    let before =
        { baseModel with Loading = true; Importing = true; Summarizing = true; PendingDelete = true }

    let after = DocumentsView.update noServices (DocumentsView.Failed(exn "offline")) before |> modelOf

    Assert.False(after.Loading)
    Assert.False(after.Importing)
    Assert.False(after.Summarizing)
    Assert.False(after.PendingDelete)
    Assert.Equal(Some "offline", after.Error)

[<Fact>]
let ``a second import while one is running is dropped`` () =
    let running = { baseModel with Importing = true }
    Assert.True(List.isEmpty (cmdOf (DocumentsView.update noServices DocumentsView.Import running)))

[<Fact>]
let ``a second summarise while one is running is dropped`` () =
    let f = facts { documentOf "Deck" DocumentKind.Pdf with ExtractedText = Some realText }
    let running = { baseModel with Facts = [ f ]; Selected = Some f.Document.Id; Summarizing = true }
    Assert.True(List.isEmpty (cmdOf (DocumentsView.update noServices DocumentsView.Summarize running)))

[<Fact>]
let ``summarising a document with nothing readable never reaches the provider`` () =
    let image = facts (documentOf "Scan" DocumentKind.Image)
    let model = { baseModel with Facts = [ image ]; Selected = Some image.Document.Id }

    let result = DocumentsView.update noServices DocumentsView.Summarize model
    Assert.True(List.isEmpty (cmdOf result))
    Assert.False((modelOf result).Summarizing)

[<Fact>]
let ``opening a missing file explains itself instead of failing silently`` () =
    let gone = { facts (documentOf "Gone" DocumentKind.Pdf) with FileExists = false }
    let model = { baseModel with Facts = [ gone ]; Selected = Some gone.Document.Id }

    let after = DocumentsView.update noServices DocumentsView.OpenFile model |> modelOf
    Assert.True(after.Notice.IsSome)
    Assert.Contains("no longer in the store", after.Notice.Value)

[<Fact>]
let ``an empty stored path counts as missing rather than crashing`` () =
    let blank = { facts { documentOf "Blank" DocumentKind.Pdf with StoredPath = "" } with FileExists = false }
    Assert.True(DocumentsLogic.isMissing blank)
    Assert.Equal(DocumentsLogic.StageState.Failed, DocumentsLogic.stageState blank DocumentsLogic.Stage.Stored)

[<Fact>]
let ``deleting asks before it runs, and cancelling abandons it`` () =
    let f = facts (documentOf "Deck" DocumentKind.Pdf)
    let model = { baseModel with Facts = [ f ]; Selected = Some f.Document.Id }

    let asked = DocumentsView.update noServices DocumentsView.RequestDelete model |> modelOf
    Assert.True(asked.PendingDelete)

    let cancelled = DocumentsView.update noServices DocumentsView.CancelDelete asked |> modelOf
    Assert.False(cancelled.PendingDelete)

[<Fact>]
let ``selecting a document loads its module into the picker`` () =
    let m = moduleOf "CS2004" "Graphics"
    let f = { facts { documentOf "Deck" DocumentKind.Pdf with ModuleId = Some m.Id } with Module = Some m }
    let model = { baseModel with Facts = [ f ]; Modules = [ m ] }

    let after = DocumentsView.update noServices (DocumentsView.Select f.Document.Id) model |> modelOf
    Assert.Equal(Some f.Document.Id, after.Selected)
    Assert.Equal(1, after.ModuleIndex)

[<Fact>]
let ``clearing the selection closes the panel and any pending delete`` () =
    let f = facts (documentOf "Deck" DocumentKind.Pdf)
    let model = { baseModel with Facts = [ f ]; Selected = Some f.Document.Id; PendingDelete = true }

    let after = DocumentsView.update noServices DocumentsView.ClearSelection model |> modelOf
    Assert.Equal(None, after.Selected)
    Assert.False(after.PendingDelete)

[<Fact>]
let ``switching the view mode changes nothing else`` () =
    let before = { baseModel with Query = "deck" }
    let after = DocumentsView.update noServices (DocumentsView.SetView DocumentsLogic.ViewMode.List) before |> modelOf

    Assert.Equal(DocumentsLogic.ViewMode.List, after.View)
    Assert.Equal("deck", after.Query)

[<Fact>]
let ``a navigation action asks the shell to move`` () =
    let result = DocumentsView.update noServices (DocumentsView.GotoPage Page.Inbox) baseModel
    Assert.Equal(Some(DocumentsView.Goto Page.Inbox), externalOf result)

[<Fact>]
let ``a resize records the width and nothing else`` () =
    let after = DocumentsView.update noServices (DocumentsView.Resized 800.0) baseModel |> modelOf
    Assert.Equal(800.0, after.Width, 3)
    Assert.Equal<DocumentsLogic.Selection>(baseModel.Filters, after.Filters)

[<Fact>]
let ``the import notice is only shown when something was actually imported`` () =
    let none' = DocumentsView.update noServices (DocumentsView.Imported 0) baseModel |> modelOf
    Assert.Equal(None, none'.Notice)

    let some = DocumentsView.update noServices (DocumentsView.Imported 2) baseModel |> modelOf
    Assert.True(some.Notice.IsSome)
    Assert.Contains("Inbox", some.Notice.Value)

// ---------------------------------------------------------------------------
// 10. new composited roles, in BOTH themes
// ---------------------------------------------------------------------------

let private themeModes = [ ThemeMode.Dark; ThemeMode.Light ]
let private accentKeys = Palette.accents |> List.map fst

let private washedBy (alphaHex: string) (colour: Color) (surface: Color) =
    let a = Int32.Parse(alphaHex, Globalization.NumberStyles.HexNumber)
    Palette.composite surface (Color.FromArgb(byte a, colour.R, colour.G, colour.B))

/// Every colour this screen writes a word on.
let private inkColours (s: Surfaces) =
    [ "success", s.Success
      "danger", s.Danger
      "warning", s.Warning
      "neutral", Color.Parse Theme.Identity.neutral ]
    @ ([ DocumentKind.Pdf; DocumentKind.Slides; DocumentKind.Word; DocumentKind.Text; DocumentKind.Image; DocumentKind.Audio; DocumentKind.Other ]
       |> List.map (fun k -> DocumentKind.label k, Color.Parse(DocumentsLogic.kindHex k)))

[<Fact>]
let ``a file-type chip's glyph is readable on its own wash, every accent and theme`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, raw in inkColours s do
                let wash = washedBy DocumentsLogic.Geometry.chipFillAlpha raw s.Card
                let ink = Palette.readableOn wash s.Text raw

                Assert.True(
                    Palette.contrastRatio ink wash >= Palette.aaText,
                    sprintf "%A/%s chip %s: %.2f" mode key name (Palette.contrastRatio ink wash)
                )

[<Fact>]
let ``a status pill is measured against its OWN stronger wash, not the chip's`` () =
    // The mismatch found on the Lectures pipeline dot: painted at one alpha,
    // measured at another. These two constants must differ AND each must be used
    // for both jobs.
    Assert.NotEqual<string>(DocumentsLogic.Geometry.chipFillAlpha, DocumentsLogic.Geometry.statusAlpha)

    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, raw in inkColours s do
                let wash = washedBy DocumentsLogic.Geometry.statusAlpha raw s.Card
                let ink = Palette.readableOn wash s.Text raw

                Assert.True(
                    Palette.contrastRatio ink wash >= Palette.aaText,
                    sprintf "%A/%s status %s: %.2f" mode key name (Palette.contrastRatio ink wash)
                )

[<Fact>]
let ``the missing-file warning banner stays readable in both themes`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)
            let wash = washedBy "22" s.Danger s.Card
            let ink = Palette.readableOn wash s.Text s.Danger

            Assert.True(
                Palette.contrastRatio ink wash >= Palette.aaText,
                sprintf "%A/%s warning: %.2f" mode key (Palette.contrastRatio ink wash)
            )

[<Fact>]
let ``the drop zone's text stays readable on the accent wash in both themes`` () =
    for mode in themeModes do
        for key in accentKeys do
            let accent = Palette.ofKey key
            let s = Surfaces.resolve mode accent
            let wash = Palette.composite s.Window accent.Soft
            let ink = Palette.readableOn wash s.Text accent.Normal

            Assert.True(
                Palette.contrastRatio ink wash >= Palette.aaText,
                sprintf "%A/%s drop zone: %.2f" mode key (Palette.contrastRatio ink wash)
            )

[<Fact>]
let ``no Documents surface is a near-white sheet in Light theme`` () =
    for key in accentKeys do
        let s = Surfaces.resolve ThemeMode.Light (Palette.ofKey key)

        for name, colour in [ "Card", s.Card; "CardAlt", s.CardAlt; "Action", s.Action; "Window", s.Window ] do
            let nearWhite = colour.R > 250uy && colour.G > 250uy && colour.B > 250uy
            Assert.False(nearWhite, sprintf "Light/%s %s is near-white" key name)

[<Fact>]
let ``every file type keeps its own identity colour`` () =
    let hexes =
        [ DocumentKind.Pdf; DocumentKind.Slides; DocumentKind.Word; DocumentKind.Image; DocumentKind.Audio ]
        |> List.map DocumentsLogic.kindHex

    Assert.Equal(hexes.Length, (List.distinct hexes).Length)

    let glyphs = [ DocumentKind.Pdf; DocumentKind.Slides; DocumentKind.Word; DocumentKind.Image; DocumentKind.Audio ] |> List.map DocumentsLogic.kindGlyph
    Assert.All(glyphs, fun g -> Assert.False(String.IsNullOrWhiteSpace g))

// ---------------------------------------------------------------------------
// 11. the shell contract
// ---------------------------------------------------------------------------

[<Fact>]
let ``Documents draws its own glass`` () =
    Assert.True(Page.usesGlassSurface Page.Documents)

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
          Page.Notes
          Page.Documents ] do
        Assert.True(Page.usesGlassSurface page)

[<Fact>]
    // Every screen is now migrated, so there is nothing left holding an opaque
    // shell surface. This asserts the completed state rather than an empty list.
let ``the screens still to migrate keep their opaque surface`` () =
    for page, _, _ in Page.all do
        Assert.True(Page.usesGlassSurface page, sprintf "%A lost its glass surface" page)
