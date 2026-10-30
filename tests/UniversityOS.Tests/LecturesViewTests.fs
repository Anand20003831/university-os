module UniversityOS.Tests.LecturesViewTests

open System
open Xunit
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Features

/// The rebuilt Lectures screen.
///
/// Every decision lives in `LecturesLogic` or in `LecturesView.update`, so all
/// of it is testable without an Avalonia application or a database.

let private now = DateTime(2026, 8, 1, 10, 0, 0)

let private moduleOf code name : StudyModule =
    { StudyModule.create (now.AddDays -60.0) code name 0 with
        Color = "#CB8A4E"
        Lecturer = Some "Dr Vance" }

let private lectureOf title : Lecture = Lecture.create (now.AddDays -7.0) title

let private facts (l: Lecture) : LecturesLogic.Facts = LecturesLogic.Facts.ofLecture l

/// Long enough to clear `Threshold.usableTextChars`.
let private realText = String.replicate 30 "lecture content. "

let private documentOf title text : Document =
    { Id = DocumentId.New()
      ModuleId = None
      Title = title
      FileName = title + ".pdf"
      StoredPath = "C:\\store\\" + title + ".pdf"
      Kind = DocumentKind.Pdf
      SizeBytes = 1024L
      ExtractedText = text
      Summary = None
      ImportedAt = now }

// ---------------------------------------------------------------------------
// 1. page composition — the redesign's own shape
// ---------------------------------------------------------------------------

[<Fact>]
let ``the page is the redesign's seven sections, in order`` () =
    Assert.Equal<LecturesLogic.Section list>(
        [ LecturesLogic.Section.Header
          LecturesLogic.Section.DropZone
          LecturesLogic.Section.Analytics
          LecturesLogic.Section.Pipeline
          LecturesLogic.Section.Filters
          LecturesLogic.Section.ModuleFilters
          LecturesLogic.Section.Grid ],
        LecturesLogic.sectionOrder
    )

    Assert.Equal(LecturesLogic.sectionOrder.Length, (List.distinct LecturesLogic.sectionOrder).Length)

[<Fact>]
let ``the expanded card runs actions, then tabs, then the panel`` () =
    Assert.Equal<LecturesLogic.ExpandedRegion list>(
        [ LecturesLogic.ExpandedRegion.Divider
          LecturesLogic.ExpandedRegion.Actions
          LecturesLogic.ExpandedRegion.Tabs
          LecturesLogic.ExpandedRegion.TabPanel ],
        LecturesLogic.expandedOrder
    )

[<Fact>]
let ``the analytics row is five cards, as the redesign shows`` () =
    Assert.Equal(5, (LecturesLogic.metrics [ facts (lectureOf "Week 1") ]).Length)

[<Fact>]
let ``the filter row is the redesign's eight chips`` () =
    Assert.Equal(8, LecturesLogic.Filter.all.Length)

    let labels = LecturesLogic.Filter.all |> List.map LecturesLogic.Filter.label
    Assert.Equal(labels.Length, (List.distinct labels).Length)
    Assert.All(labels, fun l -> Assert.False(String.IsNullOrWhiteSpace l))

[<Fact>]
let ``the card carries the redesign's seven learning-asset chips`` () =
    let assets = LecturesLogic.assets (facts (lectureOf "Week 1"))
    Assert.Equal(7, assets.Length)

    // Every chip stays on screen when its asset is absent — the absence is the
    // point of the row.
    Assert.All(assets, fun (glyph, label, _) ->
        Assert.False(String.IsNullOrWhiteSpace glyph)
        Assert.False(String.IsNullOrWhiteSpace label))

[<Fact>]
let ``the expanded action row is nine real destinations`` () =
    Assert.Equal(9, LecturesLogic.expandedActions.Length)

    let pages = LecturesLogic.expandedActions |> List.map snd
    Assert.Equal(pages.Length, (List.distinct pages).Length)

    for _, page in LecturesLogic.expandedActions do
        Assert.Contains(page, Page.all |> List.map (fun (p, _, _) -> p))

[<Fact>]
let ``the card's always-visible actions are the redesign's four glyph buttons`` () =
    Assert.Equal(4, LecturesLogic.CardAction.all.Length)

    let f = facts (lectureOf "Week 1")
    let glyphs = LecturesLogic.CardAction.all |> List.map LecturesLogic.CardAction.glyph
    Assert.Equal(glyphs.Length, (List.distinct glyphs).Length)

    // A glyph-only control must still carry a name.
    for action in LecturesLogic.CardAction.all do
        Assert.False(String.IsNullOrWhiteSpace(LecturesLogic.CardAction.label f action))

// ---------------------------------------------------------------------------
// 2. the nine-stage pipeline — every stage is a stored field
// ---------------------------------------------------------------------------

[<Fact>]
let ``the pipeline is the redesign's nine stages, each with its own words`` () =
    Assert.Equal(9, LecturesLogic.Stage.all.Length)

    let labels = LecturesLogic.Stage.all |> List.map LecturesLogic.Stage.label
    Assert.Equal(labels.Length, (List.distinct labels).Length)

    Assert.All(
        LecturesLogic.Stage.all,
        fun s ->
            Assert.False(String.IsNullOrWhiteSpace(LecturesLogic.Stage.label s))
            Assert.False(String.IsNullOrWhiteSpace(LecturesLogic.Stage.action s))
    )

[<Fact>]
let ``a bare lecture has completed exactly the one stage it really has`` () =
    // "Lecture added" is true because the record exists. Nothing else is.
    let f = facts (lectureOf "Week 1")
    Assert.Equal(1, LecturesLogic.completedStages f)
    Assert.True(LecturesLogic.stageDone f LecturesLogic.Stage.Recorded)
    Assert.Equal(Some LecturesLogic.Stage.Filed, LecturesLogic.nextStage f)

[<Fact>]
let ``each stage tracks its own stored field and no other`` () =
    let m = moduleOf "CS2004" "Graphics"
    let l = lectureOf "Week 1"

    let filed = { facts l with Lecture = { l with ModuleId = Some m.Id }; Module = Some m }
    Assert.True(LecturesLogic.stageDone filed LecturesLogic.Stage.Filed)
    Assert.False(LecturesLogic.stageDone filed LecturesLogic.Stage.Dated)

    let dated = { facts l with Lecture = { l with Date = Some now } }
    Assert.True(LecturesLogic.stageDone dated LecturesLogic.Stage.Dated)

    let slides = documentOf "Deck" (Some realText)

    let withSlides =
        { facts l with Lecture = { l with SlidesDocumentId = Some slides.Id }; Slides = Some slides }

    Assert.True(LecturesLogic.stageDone withSlides LecturesLogic.Stage.Slides)
    Assert.True(LecturesLogic.stageDone withSlides LecturesLogic.Stage.TextExtracted)
    Assert.False(LecturesLogic.stageDone withSlides LecturesLogic.Stage.Recording)

[<Fact>]
let ``an attachment with no extractable text does not complete the extraction stage`` () =
    let l = lectureOf "Week 1"
    let silent = documentOf "Scan" None

    let f = { facts l with Lecture = { l with SlidesDocumentId = Some silent.Id }; Slides = Some silent }

    Assert.True(LecturesLogic.stageDone f LecturesLogic.Stage.Slides)
    Assert.False(LecturesLogic.stageDone f LecturesLogic.Stage.TextExtracted)

[<Fact>]
let ``a whitespace-only transcript is not a transcript`` () =
    let l = lectureOf "Week 1"
    let f = { facts l with Lecture = { l with Transcript = Some "   \n  " } }

    Assert.False(LecturesLogic.hasTranscript f)
    Assert.False(LecturesLogic.stageDone f LecturesLogic.Stage.Transcript)

[<Fact>]
let ``the processing chip is a count, never a mood`` () =
    let text = LecturesLogic.processingLabel (facts (lectureOf "Week 1"))
    Assert.Equal("1 of 9 steps", text)

// ---------------------------------------------------------------------------
// 3. the AI gate — no provider call without something to read
// ---------------------------------------------------------------------------

[<Fact>]
let ``only two AI actions exist, because only two reach a provider`` () =
    // The redesign offers seven. AiService exposes Summarize and
    // GenerateFlashcards; the other five have no implementation anywhere.
    Assert.Equal(2, LecturesLogic.AiAction.all.Length)

[<Fact>]
let ``AI actions are disabled, with a reason, when there is nothing to read`` () =
    let f = facts (lectureOf "Week 1")

    Assert.False(LecturesLogic.hasUsableSource f)
    Assert.Equal(None, LecturesLogic.sourceText f)

    let reason = LecturesLogic.aiDisabledReason f
    Assert.True(reason.IsSome)
    Assert.Contains("transcript", reason.Value)

[<Fact>]
let ``a stub transcript is refused, and the reason says how short it is`` () =
    let l = lectureOf "Week 1"
    let f = { facts l with Lecture = { l with Transcript = Some "Ray tracing." } }

    Assert.False(LecturesLogic.hasUsableSource f)
    Assert.True((LecturesLogic.aiDisabledReason f).IsSome)
    Assert.Contains("characters", (LecturesLogic.aiDisabledReason f).Value)

[<Fact>]
let ``real text enables the AI actions and clears the reason`` () =
    let l = lectureOf "Week 1"
    let f = { facts l with Lecture = { l with Transcript = Some realText } }

    Assert.True(LecturesLogic.hasUsableSource f)
    Assert.Equal(None, LecturesLogic.aiDisabledReason f)

[<Fact>]
let ``a transcript is preferred over extracted slide text`` () =
    let l = lectureOf "Week 1"
    let slides = documentOf "Deck" (Some "slide text")

    let f =
        { facts l with
            Lecture = { l with Transcript = Some realText; SlidesDocumentId = Some slides.Id }
            Slides = Some slides }

    // Trimmed, because a transcript is whitespace-normalised before use.
    Assert.Equal(Some(realText.Trim()), LecturesLogic.sourceText f)

// ---------------------------------------------------------------------------
// 4. filters
// ---------------------------------------------------------------------------

[<Fact>]
let ``chips toggle on and off independently`` () =
    let s = LecturesLogic.Selection.empty
    let s = LecturesLogic.Selection.toggle LecturesLogic.Filter.Unfiled s
    let s = LecturesLogic.Selection.toggle LecturesLogic.Filter.HasSlides s

    Assert.True(s.Contains LecturesLogic.Filter.Unfiled)
    Assert.True(s.Contains LecturesLogic.Filter.HasSlides)

    let s = LecturesLogic.Selection.toggle LecturesLogic.Filter.Unfiled s
    Assert.False(s.Contains LecturesLogic.Filter.Unfiled)
    Assert.True(s.Contains LecturesLogic.Filter.HasSlides)

[<Fact>]
let ``needs summary means it has text to work from, not merely no summary`` () =
    let l = lectureOf "Week 1"

    // No summary, but nothing to read: not waiting on a summary, waiting on
    // material.
    Assert.False(LecturesLogic.matches LecturesLogic.Filter.NeedsSummary (facts l))

    let ready = { facts l with Lecture = { l with Transcript = Some realText } }
    Assert.True(LecturesLogic.matches LecturesLogic.Filter.NeedsSummary ready)

    let done' =
        { facts l with Lecture = { l with Transcript = Some realText; Summary = Some "done" } }

    Assert.False(LecturesLogic.matches LecturesLogic.Filter.NeedsSummary done')

[<Fact>]
let ``narrowing chips combine — every one must match`` () =
    let a = lectureOf "Has slides"
    let slides = documentOf "Deck" (Some realText)

    let withSlides =
        { facts a with Lecture = { a with SlidesDocumentId = Some slides.Id }; Slides = Some slides }

    let bare = facts (lectureOf "Bare")

    let selection =
        LecturesLogic.Selection.empty |> LecturesLogic.Selection.toggle LecturesLogic.Filter.HasSlides

    let shown =
        LecturesLogic.visible selection Set.empty [ withSlides; bare ]
        |> List.map (fun f -> f.Lecture.Title)

    Assert.Equal<string list>([ "Has slides" ], shown)

[<Fact>]
let ``module chips are ORed among themselves`` () =
    let m1 = moduleOf "AA1000" "One"
    let m2 = moduleOf "BB2000" "Two"
    let m3 = moduleOf "CC3000" "Three"

    let inModule (m: StudyModule) title =
        let l = lectureOf title
        { facts l with Lecture = { l with ModuleId = Some m.Id; Date = Some now }; Module = Some m }

    let all = [ inModule m1 "First"; inModule m2 "Second"; inModule m3 "Third" ]
    let picked = Set.ofList [ m1.Id.Value; m2.Id.Value ]

    let shown =
        LecturesLogic.visible LecturesLogic.Selection.empty picked all
        |> List.map (fun f -> f.Lecture.Title)

    Assert.Equal(2, shown.Length)
    Assert.DoesNotContain("Third", shown)

[<Fact>]
let ``the module chip row lists only modules that really have lectures`` () =
    let m1 = moduleOf "AA1000" "One"
    let l = lectureOf "First"

    let filed = { facts l with Lecture = { l with ModuleId = Some m1.Id }; Module = Some m1 }
    let unfiled = facts (lectureOf "Loose")

    let chips = LecturesLogic.moduleChips [ filed; unfiled ]
    Assert.Equal(1, chips.Length)
    Assert.Equal("AA1000", (fst chips.Head).Code)
    Assert.Equal(1, snd chips.Head)

[<Fact>]
let ``dated lectures come first, most recent first, then undated by title`` () =
    let dated title day =
        let l = lectureOf title
        { facts l with Lecture = { l with Date = Some(now.AddDays day) } }

    let all = [ facts (lectureOf "Zebra"); dated "Older" -10.0; facts (lectureOf "Apple"); dated "Newer" -1.0 ]

    let shown =
        LecturesLogic.visible LecturesLogic.Selection.empty Set.empty all
        |> List.map (fun f -> f.Lecture.Title)

    Assert.Equal<string list>([ "Newer"; "Older"; "Apple"; "Zebra" ], shown)

[<Fact>]
let ``the empty message explains a contradiction rather than leaving a blank page`` () =
    let both =
        LecturesLogic.Selection.empty
        |> LecturesLogic.Selection.toggle LecturesLogic.Filter.Summarised
        |> LecturesLogic.Selection.toggle LecturesLogic.Filter.NeedsSummary

    Assert.Contains("cannot be both", LecturesLogic.emptyMessage both Set.empty 3)

[<Fact>]
let ``an empty library is told how to start, not that nothing matches`` () =
    Assert.Contains("No lectures yet", LecturesLogic.emptyMessage LecturesLogic.Selection.empty Set.empty 0)

// ---------------------------------------------------------------------------
// 5. metric honesty
// ---------------------------------------------------------------------------

[<Fact>]
let ``an empty screen reads as five honest zeroes with no prose`` () =
    let values = LecturesLogic.metrics [] |> List.map (fun m -> m.Label, m.Value)

    Assert.Equal<(string * string option) list>(
        [ "Lectures", Some "0"
          "With materials", Some "0"
          "Transcribed", Some "0"
          "Summarised", Some "0"
          "Flashcards", Some "0" ],
        values
    )

    Assert.All(LecturesLogic.metrics [], fun m -> Assert.Equal(None, m.Note))

[<Fact>]
let ``no metric claims attendance, a week number or a duration`` () =
    // None of the three is stored anywhere in the domain.
    let labels = LecturesLogic.metrics [] |> List.map (fun m -> m.Label.ToLowerInvariant())

    for banned in [ "attend"; "week"; "duration"; "minutes" ] do
        Assert.All(labels, fun l -> Assert.DoesNotContain(banned, l))

[<Fact>]
let ``the unfiled count is a qualifier on a real figure, not empty-state prose`` () =
    let m = moduleOf "CS2004" "Graphics"
    let l = lectureOf "Filed"

    let filed = { facts l with Lecture = { l with ModuleId = Some m.Id }; Module = Some m }
    let loose = facts (lectureOf "Loose")

    let lectures = LecturesLogic.metrics [ filed; loose ] |> List.head
    Assert.Equal(Some "2", lectures.Value)
    Assert.Equal(Some "1 unfiled", lectures.Note)

    Assert.Equal(None, (LecturesLogic.metrics [ filed ] |> List.head).Note)

[<Fact>]
let ``the pipeline example is the least-processed lecture, and None when empty`` () =
    let bare = facts (lectureOf "Bare")
    let l = lectureOf "Further along"

    let along =
        { facts l with Lecture = { l with Date = Some now; Transcript = Some realText } }

    Assert.Equal(Some "Bare", LecturesLogic.pipelineExample [ along; bare ] |> Option.map (fun f -> f.Lecture.Title))
    Assert.Equal(None, LecturesLogic.pipelineExample [] |> Option.map (fun f -> f.Lecture.Title))

// ---------------------------------------------------------------------------
// 6. tabs
// ---------------------------------------------------------------------------

[<Fact>]
let ``all nine tabs exist and each has its own words`` () =
    Assert.Equal(9, LecturesLogic.Tab.all.Length)

    Assert.All(
        LecturesLogic.Tab.all,
        fun t ->
            Assert.False(String.IsNullOrWhiteSpace(LecturesLogic.Tab.label t))
            Assert.False(String.IsNullOrWhiteSpace(LecturesLogic.Tab.emptyMessage t))
    )

    let labels = LecturesLogic.Tab.all |> List.map LecturesLogic.Tab.label
    Assert.Equal(labels.Length, (List.distinct labels).Length)

[<Fact>]
let ``the ninth tab is real Links, not the mockup's graph placeholder`` () =
    Assert.Contains(LecturesLogic.Tab.Links, LecturesLogic.Tab.all)
    Assert.DoesNotContain("Graph", LecturesLogic.Tab.all |> List.map LecturesLogic.Tab.label)

[<Fact>]
let ``a tab's count is the real number of items behind it`` () =
    let l = lectureOf "Week 1"
    let slides = documentOf "Deck" (Some realText)

    let f =
        { facts l with
            Lecture = { l with SlidesDocumentId = Some slides.Id; Summary = Some "written" }
            Slides = Some slides
            LinkedNotes = [ Note.create now "A note" ]
            Activity = [ ActivityEntry.create now ActivityKind.LectureAdded "Added" None None ] }

    Assert.Equal(1, LecturesLogic.tabCount f LecturesLogic.Tab.Summary)
    Assert.Equal(0, LecturesLogic.tabCount f LecturesLogic.Tab.Transcript)
    Assert.Equal(1, LecturesLogic.tabCount f LecturesLogic.Tab.Notes)
    Assert.Equal(1, LecturesLogic.tabCount f LecturesLogic.Tab.Documents)
    Assert.Equal(1, LecturesLogic.tabCount f LecturesLogic.Tab.Timeline)
    Assert.Equal(1, LecturesLogic.tabCount f LecturesLogic.Tab.Links)

// ---------------------------------------------------------------------------
// 7. the card's insight
// ---------------------------------------------------------------------------

[<Fact>]
let ``the insight names the next recorded gap, never encouragement`` () =
    let text = LecturesLogic.insight (facts (lectureOf "Week 1"))
    Assert.Contains("1 of 9", text)
    Assert.Contains("file it to a module", text)

[<Fact>]
let ``a lecture with material but no readable text says exactly that`` () =
    let l = lectureOf "Week 1"
    let m = moduleOf "CS2004" "Graphics"
    let silent = documentOf "Scan" None

    let f =
        { facts l with
            Lecture =
                { l with
                    ModuleId = Some m.Id
                    Date = Some now
                    SlidesDocumentId = Some silent.Id
                    RecordingDocumentId = Some silent.Id }
            Module = Some m
            Slides = Some silent
            Recording = Some silent }

    // Five stages are genuinely done, so the gap it names is the extraction
    // step — not the summary, which is not what is actually blocking.
    Assert.Equal(5, LecturesLogic.completedStages f)
    Assert.Equal(Some LecturesLogic.Stage.TextExtracted, LecturesLogic.nextStage f)
    Assert.Contains("text can be read", LecturesLogic.insight f)

[<Fact>]
let ``a fully processed lecture is reported as finished, with its real card count`` () =
    let m = moduleOf "CS2004" "Graphics"
    let slides = documentOf "Deck" (Some realText)
    let l = lectureOf "Week 1"

    let f =
        { facts l with
            Lecture =
                { l with
                    ModuleId = Some m.Id
                    Date = Some now
                    SlidesDocumentId = Some slides.Id
                    RecordingDocumentId = Some slides.Id
                    Transcript = Some realText
                    Summary = Some "a summary" }
            Module = Some m
            Slides = Some slides
            Recording = Some slides
            Flashcards = [ RevisionItem.create now "Q" "A" ] }

    Assert.Equal(9, LecturesLogic.completedStages f)
    Assert.Equal(None, LecturesLogic.nextStage f)
    Assert.Contains("1 flashcards", LecturesLogic.insight f)

// ---------------------------------------------------------------------------
// 8. the drop banner states what the application actually does
// ---------------------------------------------------------------------------

[<Fact>]
let ``the drop banner promises an Inbox capture, not an auto-filed lecture`` () =
    // The real path, traced through Shell.subscriptions -> CaptureService
    // .CaptureFile, is BOTH: the Document is created immediately, and a capture
    // goes to the Inbox. What is not automatic is the attachment to a lecture or
    // a module, and that is exactly what the banner must not overclaim.
    let text = LecturesLogic.dropZoneMessage

    Assert.Contains("imported into Documents straight away", text)
    Assert.Contains("Inbox", text)
    Assert.Contains("until you accept", text)
    Assert.DoesNotContain("automatically", text)

// ---------------------------------------------------------------------------
// 9. layout and reserved geometry
// ---------------------------------------------------------------------------

[<Fact>]
let ``the lecture grid honours the redesign's 460px minimum card`` () =
    Assert.Equal(460.0, LecturesLogic.Geometry.cardMinWidth, 6)
    Assert.Equal(1, LecturesLogic.cardColumns 500.0)
    Assert.Equal(2, LecturesLogic.cardColumns 1000.0)
    Assert.Equal(3, LecturesLogic.cardColumns 1450.0)
    Assert.True(LecturesLogic.cardColumns 100.0 >= 1)

[<Fact>]
let ``card columns never go backwards as the page widens`` () =
    [ 320.0..40.0..2400.0 ]
    |> List.map LecturesLogic.cardColumns
    |> List.pairwise
    |> List.iter (fun (a, b) -> Assert.True(b >= a, sprintf "columns went backwards: %d then %d" a b))

[<Fact>]
let ``metric columns match every other migrated screen`` () =
    Assert.Equal(2, LecturesLogic.metricColumns 600.0)
    Assert.Equal(3, LecturesLogic.metricColumns 900.0)
    Assert.Equal(5, LecturesLogic.metricColumns 1400.0)
    Assert.Equal(ModulesLogic.metricColumns 900.0, LecturesLogic.metricColumns 900.0)
    Assert.Equal(InboxLogic.metricColumns 1400.0, LecturesLogic.metricColumns 1400.0)

[<Fact>]
let ``the outline inset matches every approved screen`` () =
    Assert.Equal(0.5, LecturesLogic.Geometry.borderInset, 6)
    Assert.Equal(PlannerLogic.Geometry.borderInset, LecturesLogic.Geometry.borderInset, 6)
    Assert.Equal(ModulesLogic.Geometry.borderInset, LecturesLogic.Geometry.borderInset, 6)

[<Fact>]
let ``no Lectures content card draws a specular highlight`` () =
    Assert.Equal(0.0, LecturesLogic.Geometry.contentHighlight, 6)
    Assert.Equal(DashboardLogic.Card.highlightScale, LecturesLogic.Geometry.contentHighlight, 6)

[<Fact>]
let ``a glass action is the same size here as on every other migrated screen`` () =
    Assert.Equal(PlannerLogic.Geometry.actionMinHeight, LecturesLogic.Geometry.actionMinHeight, 6)
    Assert.Equal(ModulesLogic.Geometry.actionRadius, LecturesLogic.Geometry.actionRadius, 6)
    Assert.Equal(ModulesLogic.Geometry.iconActionSize, LecturesLogic.Geometry.iconActionSize, 6)

[<Fact>]
let ``chips and cards reserve their outline in both states`` () =
    Assert.True(LecturesLogic.Geometry.filterBorderThickness > 0.0)
    Assert.True(LecturesLogic.Geometry.filterMinHeight > 0.0)
    Assert.True(LecturesLogic.Geometry.cardBorderThickness > 0.0)

[<Fact>]
let ``the pipeline caption is given a finite width, never an infinite one`` () =
    // A horizontal StackPanel measures children with infinite width, so a
    // wrapping caption needs an explicit width or it silently never wraps.
    Assert.True(LecturesLogic.Geometry.pipelineStepWidth > 0.0)
    Assert.Contains("*", LecturesLogic.Geometry.bulletColumns)

[<Fact>]
let ``the tab panel reserves the redesign's minimum height`` () =
    Assert.Equal(100.0, LecturesLogic.Geometry.tabPanelMinHeight, 6)

// ---------------------------------------------------------------------------
// 10. update behaviour
// ---------------------------------------------------------------------------

let private noServices: AppServices = Unchecked.defaultof<AppServices>

let private baseModel: LecturesView.Model =
    { Facts = []
      Modules = []
      Filters = LecturesLogic.Selection.empty
      ModuleFilter = Set.empty
      Expanded = None
      Tabs = Map.empty
      Draft = None
      PendingDelete = false
      Attaching = None
      AiBusy = None
      Notice = None
      Saving = false
      Loading = false
      Error = None
      Width = 1400.0 }

let private modelOf (m, _, _) = m
let private cmdOf (_, c, _) = c
let private externalOf (_, _, e) = e

[<Fact>]
let ``a successful load clears loading and any previous error`` () =
    let before = { baseModel with Loading = true; Error = Some "earlier" }

    let after =
        LecturesView.update noServices (LecturesView.Loaded([ facts (lectureOf "Week 1") ], [])) before |> modelOf

    Assert.False(after.Loading)
    Assert.Equal(None, after.Error)
    Assert.Equal(1, after.Facts.Length)

[<Fact>]
let ``a load drops an expansion whose lecture has gone`` () =
    let gone = facts (lectureOf "Week 1")
    let before = { baseModel with Expanded = Some gone.Lecture.Id; Facts = [ gone ] }

    let after =
        LecturesView.update noServices (LecturesView.Loaded([ facts (lectureOf "Week 2") ], [])) before |> modelOf

    Assert.Equal(None, after.Expanded)

[<Fact>]
let ``a load drops a module chip whose module has gone`` () =
    let before = { baseModel with ModuleFilter = Set.ofList [ Guid.NewGuid() ] }
    let after = LecturesView.update noServices (LecturesView.Loaded([], [])) before |> modelOf
    Assert.True(after.ModuleFilter.IsEmpty)

[<Fact>]
let ``a failure clears every in-flight flag`` () =
    let before =
        { baseModel with
            Loading = true
            Saving = true
            PendingDelete = true
            Attaching = Some(LectureId.New())
            AiBusy = Some(LectureId.New(), LecturesLogic.AiAction.Summarise) }

    let after = LecturesView.update noServices (LecturesView.Failed(exn "offline")) before |> modelOf

    Assert.False(after.Loading)
    Assert.False(after.Saving)
    Assert.False(after.PendingDelete)
    Assert.Equal(None, after.Attaching)
    Assert.Equal(None, after.AiBusy)
    Assert.Equal(Some "offline", after.Error)

[<Fact>]
let ``only one lecture is expanded at a time, and re-clicking collapses it`` () =
    let a = facts (lectureOf "One")
    let b = facts (lectureOf "Two")

    let first = LecturesView.update noServices (LecturesView.ToggleExpanded a.Lecture.Id) baseModel |> modelOf
    Assert.Equal(Some a.Lecture.Id, first.Expanded)

    let second = LecturesView.update noServices (LecturesView.ToggleExpanded b.Lecture.Id) first |> modelOf
    Assert.Equal(Some b.Lecture.Id, second.Expanded)

    let closed = LecturesView.update noServices (LecturesView.ToggleExpanded b.Lecture.Id) second |> modelOf
    Assert.Equal(None, closed.Expanded)

[<Fact>]
let ``each lecture remembers its own tab`` () =
    let a = facts (lectureOf "One")
    let b = facts (lectureOf "Two")

    let m1 =
        LecturesView.update noServices (LecturesView.SetTab(a.Lecture.Id, LecturesLogic.Tab.Notes)) baseModel |> modelOf

    let m2 =
        LecturesView.update noServices (LecturesView.SetTab(b.Lecture.Id, LecturesLogic.Tab.Planner)) m1 |> modelOf

    Assert.Equal(Some LecturesLogic.Tab.Notes, m2.Tabs.TryFind a.Lecture.Id.Value)
    Assert.Equal(Some LecturesLogic.Tab.Planner, m2.Tabs.TryFind b.Lecture.Id.Value)

[<Fact>]
let ``a navigation action asks the shell to move`` () =
    let result = LecturesView.update noServices (LecturesView.GotoPage Page.Planner) baseModel
    Assert.Equal(Some(LecturesView.Goto Page.Planner), externalOf result)

[<Fact>]
let ``a second attach while one is running is dropped`` () =
    let running = { baseModel with Attaching = Some(LectureId.New()) }
    let result = LecturesView.update noServices (LecturesView.AttachSlides(lectureOf "Week 1")) running
    Assert.True(List.isEmpty (cmdOf result))

[<Fact>]
let ``an AI run with nothing to read never reaches the provider`` () =
    // The button is disabled too; this is the second line of defence.
    let f = facts (lectureOf "Week 1")

    let result =
        LecturesView.update noServices (LecturesView.RunAi(LecturesLogic.AiAction.Summarise, f)) baseModel

    Assert.True(List.isEmpty (cmdOf result))
    Assert.Equal(None, (modelOf result).AiBusy)

[<Fact>]
let ``a second AI run while one is in flight is dropped`` () =
    let l = lectureOf "Week 1"
    let f = { facts l with Lecture = { l with Transcript = Some realText } }
    let running = { baseModel with AiBusy = Some(l.Id, LecturesLogic.AiAction.Summarise) }

    let result =
        LecturesView.update noServices (LecturesView.RunAi(LecturesLogic.AiAction.GenerateFlashcards, f)) running

    Assert.True(List.isEmpty (cmdOf result))

[<Fact>]
let ``a second save while one is running is dropped`` () =
    let running = { baseModel with Saving = true; Draft = Some Unchecked.defaultof<LecturesView.Draft> }
    Assert.True(List.isEmpty (cmdOf (LecturesView.update noServices LecturesView.Save running)))

[<Fact>]
let ``deleting asks before it runs, and cancelling abandons it`` () =
    let before = { baseModel with Draft = Some Unchecked.defaultof<LecturesView.Draft> }
    let asked = LecturesView.update noServices LecturesView.RequestDelete before |> modelOf
    Assert.True(asked.PendingDelete)
    Assert.False(asked.Saving)

    let cancelled = LecturesView.update noServices LecturesView.CancelDelete asked |> modelOf
    Assert.False(cancelled.PendingDelete)

[<Fact>]
let ``closing the editor clears any pending delete`` () =
    let before =
        { baseModel with Draft = Some Unchecked.defaultof<LecturesView.Draft>; PendingDelete = true }

    let after = LecturesView.update noServices LecturesView.CloseDraft before |> modelOf

    Assert.Equal(None, after.Draft)
    Assert.False(after.PendingDelete)

[<Fact>]
let ``a resize records the width and nothing else`` () =
    let after = LecturesView.update noServices (LecturesView.Resized 800.0) baseModel |> modelOf
    Assert.Equal(800.0, after.Width, 3)
    Assert.Equal<LecturesLogic.Selection>(baseModel.Filters, after.Filters)

[<Fact>]
let ``the screen opens with no chip on, so every lecture is visible`` () =
    Assert.True(baseModel.Filters.IsEmpty)
    Assert.True(baseModel.ModuleFilter.IsEmpty)

// ---------------------------------------------------------------------------
// 11. the shell contract
// ---------------------------------------------------------------------------

[<Fact>]
let ``Lectures draws its own glass`` () =
    Assert.True(Page.usesGlassSurface Page.Lectures)

[<Fact>]
let ``the screens migrated so far all draw their own glass`` () =
    for page in
        [ Page.Dashboard; Page.Timeline; Page.Planner; Page.Inbox; Page.Assignments; Page.Modules; Page.Lectures ] do
        Assert.True(Page.usesGlassSurface page)

[<Fact>]
let ``the screens still to migrate keep their opaque surface`` () =
    // Every screen is now migrated, so nothing is left holding an opaque shell
    // surface. This asserts the completed state rather than an empty list.
    for page, _, _ in Page.all do
        Assert.True(Page.usesGlassSurface page, sprintf "%A lost its glass surface" page)

// ---------------------------------------------------------------------------
// 12. the two new composited roles, in BOTH themes
// ---------------------------------------------------------------------------
//
// Lectures introduces two surfaces no earlier screen had: an asset chip washed
// at `chipFillAlpha`, and a completed pipeline dot washed more strongly at
// `stageDotAlpha`. Both carry text or a glyph, and both take their colour from
// the owning module, which varies. So both are measured against the surface the
// mark ACTUALLY sits on — the card composited with the same alpha used to paint
// it — under every accent, every module colour and both themes.

open Avalonia.Media
open UniversityOS.App.Design

let private themeModes = [ ThemeMode.Dark; ThemeMode.Light ]
let private accentKeys = Palette.accents |> List.map fst

let private alphaOf (hex: string) =
    Int32.Parse(hex, Globalization.NumberStyles.HexNumber)

let private washedBy (alphaHex: string) (colour: Color) (surface: Color) =
    Palette.composite surface (Color.FromArgb(byte (alphaOf alphaHex), colour.R, colour.G, colour.B))

let private ratio (fg: Color) (bg: Color) = Palette.contrastRatio fg bg

[<Fact>]
let ``an asset chip's label is readable on its own wash, every accent and theme`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for hex in Theme.Identity.neutral :: StudyModule.palette do
                let raw = Color.Parse hex
                let wash = washedBy LecturesLogic.Geometry.chipFillAlpha raw s.Card
                let ink = Palette.readableOn wash s.Text raw

                Assert.True(
                    ratio ink wash >= Palette.aaText,
                    sprintf "%A/%s asset chip %s: %.2f" mode key hex (ratio ink wash)
                )

[<Fact>]
let ``a completed pipeline dot's tick is readable on the dot, every accent and theme`` () =
    // The dot is painted at stageDotAlpha, which is STRONGER than a chip's, so
    // measuring it against the chip wash would be measuring the wrong surface.
    Assert.NotEqual<string>(LecturesLogic.Geometry.chipFillAlpha, LecturesLogic.Geometry.stageDotAlpha)

    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for hex in Theme.Identity.neutral :: StudyModule.palette do
                let raw = Color.Parse hex
                let dot = washedBy LecturesLogic.Geometry.stageDotAlpha raw s.Card
                let ink = Palette.readableOn dot s.Text raw

                Assert.True(
                    ratio ink dot >= Palette.aaText,
                    sprintf "%A/%s pipeline dot %s: %.2f" mode key hex (ratio ink dot)
                )

[<Fact>]
let ``a pending pipeline dot's outline stays visible against the card, both themes`` () =
    // No text on it, so the non-text bar applies.
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            Assert.True(
                ratio s.Divider s.Card >= 1.1,
                sprintf "%A/%s pending dot outline vanished into the card" mode key
            )

[<Fact>]
let ``no Lectures surface is a near-white sheet in Light theme`` () =
    // The redesign's Light theme is warm paper, not white. A card that reaches
    // near-white is the "white sheen" defect the design system exists to avoid.
    for key in accentKeys do
        let s = Surfaces.resolve ThemeMode.Light (Palette.ofKey key)

        for name, colour in [ "Card", s.Card; "CardAlt", s.CardAlt; "Action", s.Action ] do
            let isNearWhite = colour.R > 250uy && colour.G > 250uy && colour.B > 250uy
            Assert.False(isNearWhite, sprintf "Light/%s %s is near-white" key name)
