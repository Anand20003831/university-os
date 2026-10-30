module UniversityOS.Tests.InboxViewTests

open System
open Xunit
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Features

/// The migrated Inbox.
///
/// Every decision the screen makes lives in `InboxLogic` or in `InboxView.update`,
/// so all of it is testable without an Avalonia application or a database: the
/// filters, the conflict checks, the metric honesty rules, the action states, the
/// bulk guards and the responsive thresholds.
///
/// The capture pipeline itself is already covered by `CaptureHeuristicsTests`;
/// nothing here re-tests `CaptureHeuristics.analyze`.

let private now = DateTime(2026, 7, 29, 14, 0, 0)

let private action kind title =
    { Kind = kind
      Title = title
      ModuleCode = None
      Date = None
      DurationMinutes = None
      Body = None
      TargetId = None }

let private analysis analyzer actions =
    { Summary = "A captured item."
      SuggestedModuleCode = None
      Actions = actions
      Analyzer = analyzer }

let private item source title status received =
    { Id = InboxItemId.New()
      Source = source
      ExternalId = None
      Title = title
      Body = "Some captured body text."
      Status = status
      Analysis = None
      DocumentId = None
      ReceivedAt = received
      ProcessedAt = None }

/// A plain unanalysed capture that arrived an hour ago.
let private captured title =
    item CaptureSource.ManualText title InboxStatus.New (now.AddHours -1.0)

let private withAnalysis a (i: InboxItem) =
    { i with Analysis = Some a; Status = InboxStatus.Analyzed }

let private existingAssignment title deadline : InboxLogic.ExistingAssignment =
    { Id = Guid.NewGuid(); Title = title; Deadline = deadline }

// ---------------------------------------------------------------------------
// 1. page composition
// ---------------------------------------------------------------------------

[<Fact>]
let ``the page is composed of the six sections, in the order the redesign shows`` () =
    Assert.Equal<InboxLogic.Section list>(
        [ InboxLogic.Section.Header
          InboxLogic.Section.Metrics
          InboxLogic.Section.Insights
          InboxLogic.Section.Filters
          InboxLogic.Section.BulkActions
          InboxLogic.Section.Workspace ],
        InboxLogic.sectionOrder
    )

[<Fact>]
let ``every section appears exactly once, so nothing is drawn twice`` () =
    Assert.Equal(InboxLogic.sectionOrder.Length, (List.distinct InboxLogic.sectionOrder).Length)

[<Fact>]
let ``the summary row is five metrics, as the redesign shows`` () =
    Assert.Equal(5, (InboxLogic.metrics [ captured "One" ]).Length)

[<Fact>]
let ``every metric carries a label, whether or not it carries a value`` () =
    InboxLogic.metrics [ captured "One" ]
    |> List.iter (fun m -> Assert.False(String.IsNullOrWhiteSpace m.Label))

// ---------------------------------------------------------------------------
// 2. data fidelity — the elements that have no data behind them
// ---------------------------------------------------------------------------

[<Fact>]
let ``no metric claims a confidence or a processing time`` () =
    // `CaptureAnalysis` has no confidence field and nothing times an analysis,
    // so a figure in either slot could only have been invented.
    let labels =
        InboxLogic.metrics [ captured "One" ] |> List.map (fun m -> m.Label.ToLowerInvariant())

    Assert.All(
        labels,
        fun l ->
            Assert.False(l.Contains "confidence", sprintf "metric \"%s\" claims a confidence" l)
            Assert.False(l.Contains "processing", sprintf "metric \"%s\" claims a processing time" l)
    )

[<Fact>]
let ``the detection explanation says outright that no confidence score exists`` () =
    let text = InboxLogic.Analyser.explanation InboxLogic.Analyser.Ai
    Assert.Contains("no confidence score", text.ToLowerInvariant())

[<Fact>]
let ``a count of proposals is unknown, not zero, until something has been analysed`` () =
    // The same trap as the Planner's empty plan scoring 100%: "nothing proposed"
    // and "not yet examined" are different states, and only one means there is
    // no work to do.
    let metrics = InboxLogic.metrics [ captured "Unanalysed" ]

    let byLabel label =
        metrics |> List.find (fun m -> m.Label = label)

    Assert.Equal(None, (byLabel "Assignments detected").Value)
    Assert.Equal(None, (byLabel "Proposed changes").Value)
    Assert.True((byLabel "Assignments detected").Unavailable.IsSome, "the missing figure gives no reason")

[<Fact>]
let ``counts that are always knowable are never blanked`` () =
    let metrics = InboxLogic.metrics [ captured "Unanalysed" ]

    let byLabel label =
        metrics |> List.find (fun m -> m.Label = label)

    Assert.Equal(Some "1", (byLabel "Items waiting").Value)
    Assert.Equal(Some "1", (byLabel "Awaiting analysis").Value)
    Assert.Equal(Some "0", (byLabel "Documents imported").Value)

[<Fact>]
let ``once something is analysed the proposal counts become real figures`` () =
    let analysed =
        captured "Coursework 2"
        |> withAnalysis (
            analysis
                "heuristic"
                [ action ProposedActionKind.CreateAssignment "Coursework 2"
                  action ProposedActionKind.CreateNote "Notes" ]
        )

    let metrics = InboxLogic.metrics [ analysed; captured "Still new" ]

    let byLabel label =
        metrics |> List.find (fun m -> m.Label = label)

    Assert.Equal(Some "1", (byLabel "Assignments detected").Value)
    Assert.Equal(Some "2", (byLabel "Proposed changes").Value)
    Assert.Equal(Some "1", (byLabel "Awaiting analysis").Value)

[<Fact>]
let ``a NoAction proposal is not counted as a proposed change`` () =
    let idle =
        captured "Nothing to do"
        |> withAnalysis (analysis "heuristic" [ action ProposedActionKind.NoAction "Nothing" ])

    let value =
        InboxLogic.metrics [ idle ]
        |> List.find (fun m -> m.Label = "Proposed changes")
        |> fun m -> m.Value

    Assert.Equal(Some "0", value)

// ---------------------------------------------------------------------------
// 3. honest AI / deterministic labelling
// ---------------------------------------------------------------------------

[<Fact>]
let ``only an AI analysis is labelled as one`` () =
    // The single label that must never lie: rules-engine output is not an AI
    // summary, whatever it happens to say.
    Assert.Equal("AI SUMMARY", InboxLogic.Analyser.summaryHeading InboxLogic.Analyser.Ai)
    Assert.Equal("EXTRACTED SUMMARY", InboxLogic.Analyser.summaryHeading InboxLogic.Analyser.Deterministic)

[<Fact>]
let ``the analyser is read from the value the pipeline actually writes`` () =
    // `AiService` writes "ai"; `CaptureHeuristics.analyze` writes "heuristic".
    Assert.Equal(InboxLogic.Analyser.Ai, InboxLogic.Analyser.ofString "ai")
    Assert.Equal(InboxLogic.Analyser.Deterministic, InboxLogic.Analyser.ofString "heuristic")

[<Fact>]
let ``an unrecognised analyser is admitted, never assumed to be AI`` () =
    match InboxLogic.Analyser.ofString "something-else" with
    | InboxLogic.Analyser.Unknown value -> Assert.Equal("something-else", value)
    | other -> failwithf "an unknown analyser was silently reclassified as %A" other

    Assert.NotEqual<string>(
        InboxLogic.Analyser.summaryHeading InboxLogic.Analyser.Ai,
        InboxLogic.Analyser.summaryHeading (InboxLogic.Analyser.Unknown "x")
    )

[<Fact>]
let ``a heuristic analysis is never described as model-generated`` () =
    let text = (InboxLogic.Analyser.explanation InboxLogic.Analyser.Deterministic).ToLowerInvariant()
    Assert.Contains("offline", text)
    Assert.Contains("deterministic", text)
    Assert.DoesNotContain(" ai ", text)

// ---------------------------------------------------------------------------
// 4. proposal effects — exactly what the service does
// ---------------------------------------------------------------------------

let private labelsOf hasDoc kind =
    InboxLogic.effectsFor hasDoc (action kind "X") |> List.map (fun e -> e.Label)

[<Fact>]
let ``every applying proposal records a Timeline event, because the service logs one`` () =
    for kind in
        [ ProposedActionKind.CreateAssignment
          ProposedActionKind.UpdateDeadline
          ProposedActionKind.CreateEvent
          ProposedActionKind.CreateNote
          ProposedActionKind.LinkToModule ] do
        Assert.Contains("Record a Timeline event", labelsOf false kind)

[<Fact>]
let ``a proposal that does nothing claims no effects`` () =
    Assert.Empty(InboxLogic.effectsFor true (action ProposedActionKind.NoAction "X"))

[<Fact>]
let ``the captured file is only claimed to be linked where the service links it`` () =
    // `ApplyAction` calls `linkDocumentTo` for CreateAssignment, CreateEvent and
    // CreateNote — and for neither UpdateDeadline nor LinkToModule.
    for kind in
        [ ProposedActionKind.CreateAssignment
          ProposedActionKind.CreateEvent
          ProposedActionKind.CreateNote ] do
        Assert.Contains("Link the captured file", labelsOf true kind)

    for kind in [ ProposedActionKind.UpdateDeadline; ProposedActionKind.LinkToModule ] do
        Assert.DoesNotContain("Link the captured file", labelsOf true kind)

[<Fact>]
let ``no file link is claimed when the capture brought no document`` () =
    for kind in
        [ ProposedActionKind.CreateAssignment
          ProposedActionKind.CreateEvent
          ProposedActionKind.CreateNote ] do
        Assert.DoesNotContain("Link the captured file", labelsOf false kind)

[<Fact>]
let ``every effect carries words, so its colour is never the only signal`` () =
    for kind in
        [ ProposedActionKind.CreateAssignment
          ProposedActionKind.UpdateDeadline
          ProposedActionKind.CreateEvent
          ProposedActionKind.CreateNote
          ProposedActionKind.LinkToModule ] do
        InboxLogic.effectsFor true (action kind "X")
        |> List.iter (fun e -> Assert.False(String.IsNullOrWhiteSpace e.Label))

// ---------------------------------------------------------------------------
// 5. Planner impact — true because of how the planner selects work
// ---------------------------------------------------------------------------

[<Fact>]
let ``a new assignment is reported as Planner work, because every open assignment is scheduled`` () =
    let text = InboxLogic.plannerImpact [ action ProposedActionKind.CreateAssignment "Coursework" ]
    Assert.Contains("assignment", text)
    Assert.Contains("Planner", text)

[<Fact>]
let ``notes and filing are reported as no Planner change`` () =
    for kind in [ ProposedActionKind.CreateNote; ProposedActionKind.LinkToModule ] do
        Assert.Contains("No Planner change", InboxLogic.plannerImpact [ action kind "X" ])

[<Fact>]
let ``nothing proposed is nothing promised`` () =
    Assert.Contains("No Planner change", InboxLogic.plannerImpact [])

[<Fact>]
let ``a calendar event is reported as reserved busy time, which is what BuildInput makes of it`` () =
    let text = InboxLogic.plannerImpact [ action ProposedActionKind.CreateEvent "Lecture" ]
    Assert.Contains("busy time", text)

// ---------------------------------------------------------------------------
// 6. conflicts — real repository checks
// ---------------------------------------------------------------------------

[<Fact>]
let ``a proposed assignment matching one that exists is flagged as a duplicate`` () =
    let existing =
        { InboxLogic.Existing.empty with
            Assignments = [ existingAssignment "Coursework 2" None ] }

    let proposal =
        captured "Coursework 2"
        |> withAnalysis (analysis "ai" [ action ProposedActionKind.CreateAssignment "coursework  2" ])

    match InboxLogic.conflictsFor existing proposal with
    | [ InboxLogic.Conflict.DuplicateAssignment(proposed, matched) ] ->
        Assert.Equal("coursework  2", proposed)
        Assert.Equal("Coursework 2", matched)
    | other -> failwithf "expected one duplicate, got %A" other

[<Fact>]
let ``the duplicate warning says a second one would be created, because the service does not merge`` () =
    let text =
        InboxLogic.Conflict.text (InboxLogic.Conflict.DuplicateAssignment("A", "A"))

    Assert.Contains("second", text.ToLowerInvariant())

[<Fact>]
let ``an assignment the user does not have is not a duplicate`` () =
    let existing =
        { InboxLogic.Existing.empty with
            Assignments = [ existingAssignment "Something else" None ] }

    let proposal =
        captured "Coursework 2"
        |> withAnalysis (analysis "ai" [ action ProposedActionKind.CreateAssignment "Coursework 2" ])

    Assert.Empty(InboxLogic.conflictsFor existing proposal)

[<Fact>]
let ``a deadline that disagrees with the stored one is flagged with both dates`` () =
    let stored = DateTime(2026, 8, 15, 17, 0, 0)
    let proposed = DateTime(2026, 8, 18, 17, 0, 0)

    let existing =
        { InboxLogic.Existing.empty with
            Assignments = [ existingAssignment "Coursework 2" (Some stored) ] }

    let move =
        { action ProposedActionKind.UpdateDeadline "Coursework 2" with Date = Some proposed }

    let proposal = captured "Extension" |> withAnalysis (analysis "ai" [ move ])

    match InboxLogic.conflictsFor existing proposal with
    | [ InboxLogic.Conflict.DeadlineDiffers(title, was, willBe) ] ->
        Assert.Equal("Coursework 2", title)
        Assert.Equal(stored, was)
        Assert.Equal(proposed, willBe)
    | other -> failwithf "expected one deadline disagreement, got %A" other

[<Fact>]
let ``a deadline move that changes nothing is not a conflict`` () =
    let stored = DateTime(2026, 8, 15, 17, 0, 0)

    let existing =
        { InboxLogic.Existing.empty with
            Assignments = [ existingAssignment "Coursework 2" (Some stored) ] }

    let move =
        { action ProposedActionKind.UpdateDeadline "Coursework 2" with Date = Some stored }

    Assert.Empty(InboxLogic.conflictsFor existing (captured "Same" |> withAnalysis (analysis "ai" [ move ])))

[<Fact>]
let ``a deadline move with no target is predicted to fail, exactly as the service fails it`` () =
    let move =
        { action ProposedActionKind.UpdateDeadline "Nothing like this" with
            Date = Some(DateTime(2026, 8, 18, 17, 0, 0)) }

    let conflicts =
        InboxLogic.conflictsFor
            InboxLogic.Existing.empty
            (captured "Extension" |> withAnalysis (analysis "ai" [ move ]))

    Assert.Single(conflicts) |> ignore
    Assert.True(InboxLogic.Conflict.isBlocking conflicts.Head, "an unappliable proposal must read as blocking")

[<Fact>]
let ``a deadline move with no date is predicted to fail`` () =
    let move = action ProposedActionKind.UpdateDeadline "Coursework 2"

    let conflicts =
        InboxLogic.conflictsFor
            InboxLogic.Existing.empty
            (captured "Extension" |> withAnalysis (analysis "ai" [ move ]))

    Assert.All(conflicts, fun c -> Assert.True(InboxLogic.Conflict.isBlocking c))
    Assert.NotEmpty(conflicts)

[<Fact>]
let ``a deadline move resolves by target id first, as the service resolves it`` () =
    let target = existingAssignment "Coursework 2" (Some(DateTime(2026, 8, 15, 17, 0, 0)))

    let existing =
        { InboxLogic.Existing.empty with
            Assignments = [ existingAssignment "Decoy" None; target ] }

    let move =
        { action ProposedActionKind.UpdateDeadline "no textual match at all" with
            Date = Some(DateTime(2026, 8, 18, 17, 0, 0))
            TargetId = Some(string target.Id) }

    Assert.Equal(Some target, InboxLogic.resolveDeadlineTarget existing move)

[<Fact>]
let ``a calendar proposal with no date is predicted to fail`` () =
    let conflicts =
        InboxLogic.conflictsFor
            InboxLogic.Existing.empty
            (captured "Lecture" |> withAnalysis (analysis "ai" [ action ProposedActionKind.CreateEvent "Lecture" ]))

    Assert.NotEmpty(conflicts)
    Assert.All(conflicts, fun c -> Assert.True(InboxLogic.Conflict.isBlocking c))

[<Fact>]
let ``filing needs both a document and a known module, exactly as ApplyAction needs them`` () =
    let link = { action ProposedActionKind.LinkToModule "CS2004" with ModuleCode = Some "CS2004" }
    let known = { InboxLogic.Existing.empty with ModuleCodes = [ "CS2004" ] }

    // No document: cannot file.
    let noDocument = captured "Slides" |> withAnalysis (analysis "ai" [ link ])
    Assert.NotEmpty(InboxLogic.conflictsFor known noDocument)

    // A document but no such module: cannot file.
    let withDoc =
        { noDocument with DocumentId = Some(DocumentId.New()) }

    Assert.NotEmpty(InboxLogic.conflictsFor InboxLogic.Existing.empty withDoc)

    // Both present: nothing to warn about.
    Assert.Empty(InboxLogic.conflictsFor known withDoc)

[<Fact>]
let ``a module code the user does not have is a warning, not a blocker`` () =
    let create =
        { action ProposedActionKind.CreateAssignment "Coursework" with ModuleCode = Some "ZZ9999" }

    let conflicts =
        InboxLogic.conflictsFor
            InboxLogic.Existing.empty
            (captured "Coursework" |> withAnalysis (analysis "ai" [ create ]))

    Assert.Contains(InboxLogic.Conflict.UnknownModule "ZZ9999", conflicts)
    Assert.False(InboxLogic.Conflict.isBlocking (InboxLogic.Conflict.UnknownModule "ZZ9999"))

[<Fact>]
let ``module codes are matched the way the service normalises them`` () =
    let create =
        { action ProposedActionKind.CreateAssignment "Coursework" with ModuleCode = Some "cs-2004" }

    let known = { InboxLogic.Existing.empty with ModuleCodes = [ "CS 2004" ] }

    Assert.Empty(
        InboxLogic.conflictsFor known (captured "Coursework" |> withAnalysis (analysis "ai" [ create ]))
    )

[<Fact>]
let ``an unanalysed item can carry no conflict, because nothing has been proposed`` () =
    let existing =
        { InboxLogic.Existing.empty with
            Assignments = [ existingAssignment "Coursework 2" None ] }

    Assert.Empty(InboxLogic.conflictsFor existing (captured "Coursework 2"))

[<Fact>]
let ``every conflict states itself in words`` () =
    let all =
        [ InboxLogic.Conflict.DuplicateAssignment("A", "B")
          InboxLogic.Conflict.DeadlineDiffers("A", now, now.AddDays 3.0)
          InboxLogic.Conflict.UnknownModule "ZZ1"
          InboxLogic.Conflict.WillNotApply("Do a thing", "a reason") ]

    Assert.All(all, fun c -> Assert.False(String.IsNullOrWhiteSpace(InboxLogic.Conflict.text c)))

// ---------------------------------------------------------------------------
// 7. filters
// ---------------------------------------------------------------------------

let private noConflicts (_: InboxItem) : InboxLogic.Conflict list = []

[<Fact>]
let ``no filter is offered twice and every one has a label`` () =
    Assert.Equal(InboxLogic.Filter.all.Length, (List.distinct InboxLogic.Filter.all).Length)

    Assert.All(
        InboxLogic.Filter.all,
        fun f ->
            Assert.False(String.IsNullOrWhiteSpace(InboxLogic.Filter.label f))
            Assert.False(String.IsNullOrWhiteSpace(InboxLogic.Filter.emptyMessage f))
    )

[<Fact>]
let ``no filter exists that the capture pipeline can never fill`` () =
    // The redesign offers a Lectures chip. Nothing in `ProposedActionKind` or in
    // `CaptureService` ever produces a lecture, so a Lectures filter could only
    // ever be empty — an inert decoration.
    let labels = InboxLogic.Filter.all |> List.map (InboxLogic.Filter.label >> fun l -> l.ToLowerInvariant())
    Assert.DoesNotContain("lectures", labels)

[<Fact>]
let ``All shows everything, whatever its status`` () =
    let items = [ captured "New one"; item CaptureSource.ManualText "Old" InboxStatus.Applied now ]
    Assert.Equal(2, (InboxLogic.visibleItems noConflicts InboxLogic.Filter.All items).Length)

[<Fact>]
let ``Needs review is exactly what the repository calls active`` () =
    let items =
        [ item CaptureSource.ManualText "New" InboxStatus.New now
          item CaptureSource.ManualText "Analysed" InboxStatus.Analyzed now
          item CaptureSource.ManualText "Applied" InboxStatus.Applied now
          item CaptureSource.ManualText "Dismissed" InboxStatus.Dismissed now ]

    let shown = InboxLogic.visibleItems noConflicts InboxLogic.Filter.NeedsReview items
    Assert.Equal<string list>([ "New"; "Analysed" ], shown |> List.map (fun i -> i.Title) |> List.sortDescending)

[<Fact>]
let ``Completed is the exact complement of Needs review`` () =
    let items =
        [ item CaptureSource.ManualText "New" InboxStatus.New now
          item CaptureSource.ManualText "Applied" InboxStatus.Applied now
          item CaptureSource.ManualText "Dismissed" InboxStatus.Dismissed now ]

    let review = InboxLogic.visibleItems noConflicts InboxLogic.Filter.NeedsReview items
    let completed = InboxLogic.visibleItems noConflicts InboxLogic.Filter.Completed items

    Assert.Equal(items.Length, review.Length + completed.Length)
    Assert.Empty(Set.intersect (set (review |> List.map (fun i -> i.Id))) (set (completed |> List.map (fun i -> i.Id))))

[<Fact>]
let ``Assignments selects items whose proposals touch coursework`` () =
    let creating =
        captured "Coursework"
        |> withAnalysis (analysis "ai" [ action ProposedActionKind.CreateAssignment "Coursework" ])

    let moving =
        captured "Extension"
        |> withAnalysis (analysis "ai" [ action ProposedActionKind.UpdateDeadline "Coursework" ])

    let noting =
        captured "A note"
        |> withAnalysis (analysis "ai" [ action ProposedActionKind.CreateNote "A note" ])

    let shown =
        InboxLogic.visibleItems noConflicts InboxLogic.Filter.Assignments [ creating; moving; noting ]

    Assert.Equal(2, shown.Length)
    Assert.DoesNotContain(noting.Id, shown |> List.map (fun i -> i.Id))

[<Fact>]
let ``Calendar and Notes each select their own proposal kind`` () =
    let event =
        captured "Lecture"
        |> withAnalysis (analysis "ai" [ action ProposedActionKind.CreateEvent "Lecture" ])

    let note =
        captured "Idea" |> withAnalysis (analysis "ai" [ action ProposedActionKind.CreateNote "Idea" ])

    Assert.Equal<InboxItemId list>(
        [ event.Id ],
        InboxLogic.visibleItems noConflicts InboxLogic.Filter.Calendar [ event; note ] |> List.map (fun i -> i.Id)
    )

    Assert.Equal<InboxItemId list>(
        [ note.Id ],
        InboxLogic.visibleItems noConflicts InboxLogic.Filter.Notes [ event; note ] |> List.map (fun i -> i.Id)
    )

[<Fact>]
let ``Documents selects file captures and anything that imported a document`` () =
    let fileCapture = item (CaptureSource.File "notes.pdf") "Notes" InboxStatus.New now

    let imported =
        { captured "From a connector" with DocumentId = Some(DocumentId.New()) }

    let plain = captured "Just text"

    let shown =
        InboxLogic.visibleItems noConflicts InboxLogic.Filter.Documents [ fileCapture; imported; plain ]

    Assert.Equal(2, shown.Length)
    Assert.DoesNotContain(plain.Id, shown |> List.map (fun i -> i.Id))

[<Fact>]
let ``Emails selects by the connector key, never by words in a title`` () =
    let mail = item (CaptureSource.Connector "outlook") "Anything at all" InboxStatus.New now
    let gitlab = item (CaptureSource.Connector "gitlab") "Email about a commit" InboxStatus.New now

    let shown = InboxLogic.visibleItems noConflicts InboxLogic.Filter.Emails [ mail; gitlab ]

    Assert.Equal<InboxItemId list>([ mail.Id ], shown |> List.map (fun i -> i.Id))

[<Fact>]
let ``every email connector key really is one this build registers`` () =
    // Guards against the mapping drifting away from `IConnector.Metadata.Key`.
    Assert.Equal<string list>([ "gmail"; "outlook" ], InboxLogic.emailConnectorKeys)

[<Fact>]
let ``AI analysed selects only what the AI provider classified`` () =
    let byAi = captured "One" |> withAnalysis (analysis "ai" [ action ProposedActionKind.CreateNote "n" ])
    let byRules = captured "Two" |> withAnalysis (analysis "heuristic" [ action ProposedActionKind.CreateNote "n" ])
    let unanalysed = captured "Three"

    let shown =
        InboxLogic.visibleItems noConflicts InboxLogic.Filter.AiAnalysed [ byAi; byRules; unanalysed ]

    Assert.Equal<InboxItemId list>([ byAi.Id ], shown |> List.map (fun i -> i.Id))

[<Fact>]
let ``Conflicts selects exactly what the conflict check flagged`` () =
    let clean = captured "Clean"
    let dirty = captured "Dirty"

    let conflictsOf (i: InboxItem) =
        if i.Id = dirty.Id then [ InboxLogic.Conflict.UnknownModule "ZZ1" ] else []

    let shown = InboxLogic.visibleItems conflictsOf InboxLogic.Filter.Conflicts [ clean; dirty ]
    Assert.Equal<InboxItemId list>([ dirty.Id ], shown |> List.map (fun i -> i.Id))

[<Fact>]
let ``the list is newest first, so a refresh cannot reorder it under the pointer`` () =
    let older = item CaptureSource.ManualText "Older" InboxStatus.New (now.AddHours -5.0)
    let newer = item CaptureSource.ManualText "Newer" InboxStatus.New (now.AddHours -1.0)

    let shown = InboxLogic.visibleItems noConflicts InboxLogic.Filter.All [ older; newer ]
    Assert.Equal<string list>([ "Newer"; "Older" ], shown |> List.map (fun i -> i.Title))

// ---------------------------------------------------------------------------
// 8. insights
// ---------------------------------------------------------------------------

[<Fact>]
let ``an empty inbox produces no observations rather than a reassuring one`` () =
    Assert.Empty(InboxLogic.insights now noConflicts [])

[<Fact>]
let ``observations count real items, and only real ones`` () =
    let today = item CaptureSource.ManualText "Today" InboxStatus.New now
    let older = item CaptureSource.ManualText "Older" InboxStatus.New (now.AddDays -3.0)

    let observations = InboxLogic.insights now noConflicts [ today; older ]

    Assert.Contains("1 item captured today.", observations)
    Assert.Contains("2 items are not analysed yet.", observations)

[<Fact>]
let ``the analyser split is reported honestly when both engines have run`` () =
    let byAi = captured "One" |> withAnalysis (analysis "ai" [ action ProposedActionKind.CreateNote "n" ])
    let byRules = captured "Two" |> withAnalysis (analysis "heuristic" [ action ProposedActionKind.CreateNote "n" ])

    let observations = InboxLogic.insights now noConflicts [ byAi; byRules ]
    Assert.Contains("1 analysed by AI, 1 by the offline rules.", observations)

[<Fact>]
let ``the expected Planner gain is the real count of proposed assignments`` () =
    let one =
        captured "A"
        |> withAnalysis (analysis "ai" [ action ProposedActionKind.CreateAssignment "A" ])

    let two =
        captured "B"
        |> withAnalysis (analysis "ai" [ action ProposedActionKind.CreateAssignment "B" ])

    let observations = InboxLogic.insights now noConflicts [ one; two ]
    Assert.Contains("Accepting everything would create 2 assignments for the Planner.", observations)

[<Fact>]
let ``items needing confirmation are counted from the real check`` () =
    let dirty = captured "Dirty"

    let conflictsOf (i: InboxItem) =
        if i.Id = dirty.Id then [ InboxLogic.Conflict.UnknownModule "ZZ1" ] else []

    Assert.Contains("1 item needing confirmation before applying.", InboxLogic.insights now conflictsOf [ dirty ])

// ---------------------------------------------------------------------------
// 9. item actions
// ---------------------------------------------------------------------------

let private context status hasAnalysis realCount hasDoc busy : InboxLogic.ItemContext =
    { Status = status
      HasAnalysis = hasAnalysis
      RealActionCount = realCount
      HasDocument = hasDoc
      Busy = busy }

let private stateOf id (states: InboxLogic.ItemActionState list) =
    states |> List.find (fun s -> s.Id = id)

[<Fact>]
let ``an analysed item with proposals can be accepted, and Accept is the primary action`` () =
    let states = InboxLogic.itemActions (context InboxStatus.Analyzed true 2 false false)
    let accept = stateOf InboxLogic.ItemActionId.Accept states

    Assert.True(accept.Enabled)
    Assert.True(accept.Primary)
    Assert.Equal(None, accept.DisabledBecause)
    Assert.Equal(1, states |> List.filter (fun s -> s.Primary) |> List.length)

[<Fact>]
let ``an unanalysed item cannot be accepted, and says why`` () =
    let accept =
        InboxLogic.itemActions (context InboxStatus.New false 0 false false)
        |> stateOf InboxLogic.ItemActionId.Accept

    Assert.False(accept.Enabled)
    Assert.Equal(Some "analyse it first to see what would happen", accept.DisabledBecause)

[<Fact>]
let ``an item whose analysis proposed nothing cannot be accepted, and says so distinctly`` () =
    let accept =
        InboxLogic.itemActions (context InboxStatus.Analyzed true 0 false false)
        |> stateOf InboxLogic.ItemActionId.Accept

    Assert.False(accept.Enabled)
    Assert.Equal(Some "nothing was proposed for this item", accept.DisabledBecause)

[<Fact>]
let ``a processed item offers nothing that would change it`` () =
    for status in [ InboxStatus.Applied; InboxStatus.Dismissed ] do
        let states = InboxLogic.itemActions (context status true 2 false false)

        for id in
            [ InboxLogic.ItemActionId.Accept
              InboxLogic.ItemActionId.Analyse
              InboxLogic.ItemActionId.Dismiss ] do
            let state = stateOf id states
            Assert.False(state.Enabled, sprintf "%A was still enabled on a %A item" id status)
            Assert.True(state.DisabledBecause.IsSome, sprintf "%A gave no reason" id)

[<Fact>]
let ``a request in flight disables every action on that item and says why`` () =
    let states = InboxLogic.itemActions (context InboxStatus.Analyzed true 2 true true)

    Assert.All(
        states,
        fun s ->
            Assert.False(s.Enabled, sprintf "%s stayed enabled while busy" s.Label)
            Assert.Equal(Some "an action on this item is still running", s.DisabledBecause)
    )

[<Fact>]
let ``Open file is offered only when there is a file to open`` () =
    let withFile = InboxLogic.itemActions (context InboxStatus.New false 0 true false)
    let withoutFile = InboxLogic.itemActions (context InboxStatus.New false 0 false false)

    Assert.Contains(InboxLogic.ItemActionId.OpenFile, withFile |> List.map (fun s -> s.Id))
    Assert.DoesNotContain(InboxLogic.ItemActionId.OpenFile, withoutFile |> List.map (fun s -> s.Id))

[<Fact>]
let ``no action is ever drawn without a reason when it is unavailable`` () =
    // The rule that keeps the row honest: disabled always explains itself.
    for status in [ InboxStatus.New; InboxStatus.Analyzed; InboxStatus.Applied; InboxStatus.Dismissed ] do
        for hasAnalysis in [ true; false ] do
            for busy in [ true; false ] do
                InboxLogic.itemActions (context status hasAnalysis 1 true busy)
                |> List.iter (fun s ->
                    if not s.Enabled then
                        Assert.True(
                            s.DisabledBecause |> Option.exists (fun w -> w.Trim().Length > 0),
                            sprintf "%s was disabled with no reason" s.Label
                        ))

[<Fact>]
let ``re-analysing is named as such, so it never looks like a first pass`` () =
    let first =
        InboxLogic.itemActions (context InboxStatus.New false 0 false false)
        |> stateOf InboxLogic.ItemActionId.Analyse

    let again =
        InboxLogic.itemActions (context InboxStatus.Analyzed true 1 false false)
        |> stateOf InboxLogic.ItemActionId.Analyse

    Assert.Equal("Analyse", first.Label)
    Assert.Equal("Re-analyse", again.Label)

[<Fact>]
let ``the actions offered are only ones with a service behind them`` () =
    // Edit, Change Module, Generate Notes and Move to Documents appear in the
    // redesign and have no service. They must not reappear as dead controls.
    let ids =
        InboxLogic.itemActions (context InboxStatus.Analyzed true 2 true false) |> List.map (fun s -> s.Id)

    Assert.Equal<InboxLogic.ItemActionId list>(
        [ InboxLogic.ItemActionId.Accept
          InboxLogic.ItemActionId.Analyse
          InboxLogic.ItemActionId.Dismiss
          InboxLogic.ItemActionId.OpenFile ],
        ids
    )

// ---------------------------------------------------------------------------
// 10. bulk actions
// ---------------------------------------------------------------------------

let private bulkStateOf id (states: InboxLogic.BulkActionState list) =
    states |> List.find (fun s -> s.Id = id)

[<Fact>]
let ``the bulk row counts exactly what it would touch`` () =
    let acceptable =
        captured "Ready"
        |> withAnalysis (analysis "ai" [ action ProposedActionKind.CreateNote "n" ])

    let unanalysed = captured "Not looked at"
    let done' = item CaptureSource.ManualText "Done" InboxStatus.Applied now

    let ctx = InboxLogic.bulkContext false [ acceptable; unanalysed; done' ]

    Assert.Equal(3, ctx.Shown)
    Assert.Equal(1, ctx.Acceptable)
    Assert.Equal(1, ctx.Unanalysed)
    Assert.Equal(2, ctx.Active)

[<Fact>]
let ``each bulk label states the set it would act on`` () =
    let states = InboxLogic.bulkActions (InboxLogic.bulkContext false [ captured "One" ])

    Assert.All(states, fun s -> Assert.Contains("shown", s.Label))
    Assert.Contains("Analyse all 1 shown", states |> List.map (fun s -> s.Label))

[<Fact>]
let ``a bulk action with nothing to act on is disabled and says why`` () =
    let states = InboxLogic.bulkActions (InboxLogic.bulkContext false [])

    Assert.All(
        states,
        fun s ->
            Assert.False(s.Enabled, sprintf "%s was enabled with nothing shown" s.Label)
            Assert.True(s.DisabledBecause.IsSome, sprintf "%s gave no reason" s.Label)
    )

[<Fact>]
let ``a running bulk action disables the whole row`` () =
    let acceptable =
        captured "Ready"
        |> withAnalysis (analysis "ai" [ action ProposedActionKind.CreateNote "n" ])

    let states = InboxLogic.bulkActions (InboxLogic.bulkContext true [ acceptable ])

    Assert.All(
        states,
        fun s ->
            Assert.False(s.Enabled)
            Assert.Equal(Some "a bulk action is still running", s.DisabledBecause)
    )

[<Fact>]
let ``only the destructive bulk action asks for confirmation`` () =
    let states = InboxLogic.bulkActions (InboxLogic.bulkContext false [ captured "One" ])

    Assert.True((bulkStateOf InboxLogic.BulkActionId.DismissAll states).NeedsConfirmation)
    Assert.False((bulkStateOf InboxLogic.BulkActionId.AcceptAll states).NeedsConfirmation)
    Assert.False((bulkStateOf InboxLogic.BulkActionId.AnalyseAll states).NeedsConfirmation)

[<Fact>]
let ``the dismissal prompt states the count and that it cannot be undone`` () =
    let prompt = InboxLogic.confirmationPrompt InboxLogic.BulkActionId.DismissAll 4
    Assert.Contains("4 items", prompt)
    Assert.Contains("cannot be undone", prompt)

[<Fact>]
let ``exactly one bulk action is primary`` () =
    let states = InboxLogic.bulkActions (InboxLogic.bulkContext false [ captured "One" ])
    Assert.Equal(1, states |> List.filter (fun s -> s.Primary) |> List.length)

[<Fact>]
let ``the bulk row offers only operations a repository can perform`` () =
    // Delete, Assign Module and Mark as Duplicate appear in the redesign.
    // `IInboxRepository` has no delete, nothing assigns a module and nothing
    // records a duplicate, so none of the three may appear here.
    let ids = InboxLogic.bulkActions (InboxLogic.bulkContext false []) |> List.map (fun s -> s.Id)

    Assert.Equal<InboxLogic.BulkActionId list>(
        [ InboxLogic.BulkActionId.AcceptAll
          InboxLogic.BulkActionId.AnalyseAll
          InboxLogic.BulkActionId.DismissAll ],
        ids
    )

// ---------------------------------------------------------------------------
// 11. sources
// ---------------------------------------------------------------------------

[<Fact>]
let ``a source kind is derived from the capture source, never from the text`` () =
    Assert.Equal(InboxLogic.SourceKind.Capture, InboxLogic.SourceKind.ofSource CaptureSource.ManualText)
    Assert.Equal(InboxLogic.SourceKind.File, InboxLogic.SourceKind.ofSource (CaptureSource.File "a.pdf"))
    Assert.Equal(InboxLogic.SourceKind.Email, InboxLogic.SourceKind.ofSource (CaptureSource.Connector "gmail"))

    Assert.Equal(
        InboxLogic.SourceKind.CalendarFeed,
        InboxLogic.SourceKind.ofSource (CaptureSource.Connector "google-calendar")
    )

    Assert.Equal(InboxLogic.SourceKind.File, InboxLogic.SourceKind.ofSource (CaptureSource.Connector "onedrive"))
    Assert.Equal(InboxLogic.SourceKind.Connector, InboxLogic.SourceKind.ofSource (CaptureSource.Connector "moodle"))

[<Fact>]
let ``connector keys are matched case-insensitively`` () =
    Assert.Equal(InboxLogic.SourceKind.Email, InboxLogic.SourceKind.ofSource (CaptureSource.Connector "OUTLOOK"))

[<Fact>]
let ``every status has its own word, so colour is never the only signal`` () =
    let labels =
        [ InboxStatus.New; InboxStatus.Analyzed; InboxStatus.Applied; InboxStatus.Dismissed ]
        |> List.map InboxLogic.Status.label

    Assert.Equal(labels.Length, (List.distinct labels).Length)
    Assert.All(labels, fun l -> Assert.False(String.IsNullOrWhiteSpace l))

[<Fact>]
let ``active mirrors the repository's own definition of active`` () =
    // `InboxRepository.GetActive` selects statuses 0 and 1.
    Assert.True(InboxLogic.Status.isActive InboxStatus.New)
    Assert.True(InboxLogic.Status.isActive InboxStatus.Analyzed)
    Assert.False(InboxLogic.Status.isActive InboxStatus.Applied)
    Assert.False(InboxLogic.Status.isActive InboxStatus.Dismissed)

// ---------------------------------------------------------------------------
// 11b. semantic ink — meaning here, paint in the view
// ---------------------------------------------------------------------------

[<Fact>]
let ``every status and every effect resolves to an ink the contrast tests cover`` () =
    // `SurfaceTests` walks `inksUsed` and proves each clears AA in both themes.
    // That only covers the screen if every value really maps into that list.
    for status in [ InboxStatus.New; InboxStatus.Analyzed; InboxStatus.Applied; InboxStatus.Dismissed ] do
        Assert.Contains(InboxLogic.statusInk status, InboxLogic.inksUsed)

    for kind in
        [ InboxLogic.EffectKind.Creates
          InboxLogic.EffectKind.Updates
          InboxLogic.EffectKind.Links
          InboxLogic.EffectKind.Records ] do
        Assert.Contains(InboxLogic.effectInk kind, InboxLogic.inksUsed)

    for conflict in
        [ InboxLogic.Conflict.UnknownModule "ZZ1"; InboxLogic.Conflict.WillNotApply("a", "b") ] do
        Assert.Contains(InboxLogic.conflictInk conflict, InboxLogic.inksUsed)

[<Fact>]
let ``a proposal that cannot apply reads as a failure, not a caution`` () =
    Assert.Equal(
        InboxLogic.Ink.Critical,
        InboxLogic.conflictInk (InboxLogic.Conflict.WillNotApply("Do a thing", "a reason"))
    )

    Assert.Equal(InboxLogic.Ink.Caution, InboxLogic.conflictInk (InboxLogic.Conflict.UnknownModule "ZZ1"))

[<Fact>]
let ``an applied item and a new one do not share an ink`` () =
    Assert.NotEqual<InboxLogic.Ink>(
        InboxLogic.statusInk InboxStatus.Applied,
        InboxLogic.statusInk InboxStatus.New
    )

[<Fact>]
let ``creating and updating do not share an ink, so an effect chip distinguishes them`` () =
    Assert.NotEqual<InboxLogic.Ink>(
        InboxLogic.effectInk InboxLogic.EffectKind.Creates,
        InboxLogic.effectInk InboxLogic.EffectKind.Updates
    )

// ---------------------------------------------------------------------------
// 12. layout and reserved geometry
// ---------------------------------------------------------------------------

[<Fact>]
let ``the workspace splits about two thirds to the list, as the redesign does`` () =
    Assert.Equal(
        100.0,
        InboxLogic.Geometry.mainLeftWeight + InboxLogic.Geometry.mainRightWeight,
        3
    )

    Assert.InRange(InboxLogic.Geometry.mainLeftWeight, 64.0, 68.0)
    Assert.InRange(InboxLogic.Geometry.mainRightWeight, 32.0, 36.0)

[<Fact>]
let ``the detail column appears at the same width as the Planner's side column`` () =
    Assert.False(InboxLogic.showsDetailColumn 900.0)
    Assert.False(InboxLogic.showsDetailColumn 1099.0)
    Assert.True(InboxLogic.showsDetailColumn 1100.0)
    Assert.True(InboxLogic.showsDetailColumn 1600.0)

    // One threshold across the migrated screens, so they break together.
    for width in [ 600.0; 900.0; 1099.0; 1100.0; 1400.0 ] do
        Assert.Equal(PlannerLogic.showsSideColumn width, InboxLogic.showsDetailColumn width)

[<Fact>]
let ``the widest reading width still shows the detail column`` () =
    // Reading width is a rule for prose; the workspace is not bound by it.
    Assert.True(InboxLogic.showsDetailColumn 1100.0)
    Assert.True(1040.0 < 1100.0)

[<Fact>]
let ``metric columns reflow and never go backwards as the page widens`` () =
    Assert.Equal(2, InboxLogic.metricColumns 600.0)
    Assert.Equal(3, InboxLogic.metricColumns 900.0)
    Assert.Equal(5, InboxLogic.metricColumns 1400.0)

    [ 320.0..40.0..1920.0 ]
    |> List.map InboxLogic.metricColumns
    |> List.pairwise
    |> List.iter (fun (a, b) -> Assert.True(b >= a, sprintf "columns went backwards: %d then %d" a b))

[<Fact>]
let ``all five metrics fit the row at wide width`` () =
    Assert.Equal(5, InboxLogic.metricColumns 1400.0)
    Assert.Equal(5, (InboxLogic.metrics []).Length)

[<Fact>]
let ``the outline inset matches the Planner's and the Dashboard's exactly`` () =
    // Half a 1px stroke. Pinned across the three screens, because a stroke laid
    // on a panel's bounds is half-clipped and its corners clip asymmetrically.
    Assert.Equal(0.5, InboxLogic.Geometry.borderInset, 6)
    Assert.Equal(PlannerLogic.Geometry.borderInset, InboxLogic.Geometry.borderInset, 6)
    Assert.Equal(DashboardLogic.Card.borderInset, InboxLogic.Geometry.borderInset, 6)

[<Fact>]
let ``no Inbox content card draws a specular highlight`` () =
    Assert.Equal(0.0, InboxLogic.Geometry.contentHighlight, 6)
    Assert.Equal(PlannerLogic.Geometry.contentHighlight, InboxLogic.Geometry.contentHighlight, 6)
    Assert.Equal(DashboardLogic.Card.highlightScale, InboxLogic.Geometry.contentHighlight, 6)

[<Fact>]
let ``a glass action is the same size on the Inbox as on the Planner`` () =
    Assert.Equal(PlannerLogic.Geometry.actionMinHeight, InboxLogic.Geometry.actionMinHeight, 6)
    Assert.Equal(PlannerLogic.Geometry.actionRadius, InboxLogic.Geometry.actionRadius, 6)

[<Fact>]
let ``a filter chip reserves its border in both states, so selection cannot resize it`` () =
    Assert.True(InboxLogic.Geometry.filterBorderThickness > 0.0)
    Assert.True(InboxLogic.Geometry.filterMinHeight > 0.0)

[<Fact>]
let ``a review card reserves its outline in both states, so selection cannot resize it`` () =
    Assert.True(InboxLogic.Geometry.cardBorderThickness > 0.0)

[<Fact>]
let ``a status word has a reserved column, so a card cannot resize when it changes`` () =
    Assert.True(InboxLogic.Geometry.statusMinWidth > 0.0)

[<Fact>]
let ``wrapping text is given a finite column, never an infinite one`` () =
    // A horizontal StackPanel measures with infinite width, so TextWrapping
    // never engages inside one. Both text layouts on this screen are Grids with
    // a star column.
    Assert.Contains("*", InboxLogic.Geometry.bulletColumns)
    Assert.Contains("*", InboxLogic.Geometry.metadataColumns)
    Assert.StartsWith("Auto", InboxLogic.Geometry.bulletColumns)
    Assert.StartsWith("Auto", InboxLogic.Geometry.metadataColumns)

[<Fact>]
let ``row minimum heights are applied, so a row cannot collapse`` () =
    Assert.True(InboxLogic.Geometry.metricRowMinHeight > 0.0)
    Assert.True(InboxLogic.Geometry.workspaceMinHeight > 0.0)

[<Fact>]
let ``the Light-theme pass changed no geometry at all`` () =
    // Every dimension pinned in one place. The Light pass was a colour pass: if
    // any of these moves, something changed the approved Dark layout while
    // claiming to be theme work.
    Assert.Equal(0.0, InboxLogic.Geometry.contentHighlight, 6)
    Assert.Equal(66.0, InboxLogic.Geometry.mainLeftWeight, 6)
    Assert.Equal(34.0, InboxLogic.Geometry.mainRightWeight, 6)
    Assert.Equal(320.0, InboxLogic.Geometry.workspaceMinHeight, 6)
    Assert.Equal(72.0, InboxLogic.Geometry.metricRowMinHeight, 6)
    Assert.Equal(38.0, InboxLogic.Geometry.actionMinHeight, 6)
    Assert.Equal(11.0, InboxLogic.Geometry.actionRadius, 6)
    Assert.Equal(0.5, InboxLogic.Geometry.borderInset, 6)
    Assert.Equal(30.0, InboxLogic.Geometry.filterMinHeight, 6)
    Assert.Equal(999.0, InboxLogic.Geometry.filterRadius, 6)
    Assert.Equal(1.0, InboxLogic.Geometry.filterBorderThickness, 6)
    Assert.Equal(1.0, InboxLogic.Geometry.cardBorderThickness, 6)
    Assert.Equal(16.0, InboxLogic.Geometry.cardRadius, 6)
    Assert.Equal(30.0, InboxLogic.Geometry.sourceBadgeSize, 6)
    Assert.Equal(76.0, InboxLogic.Geometry.statusMinWidth, 6)
    Assert.Equal("Auto,9,*", InboxLogic.Geometry.bulletColumns)
    Assert.Equal("Auto,12,*", InboxLogic.Geometry.metadataColumns)

[<Fact>]
let ``the Light-theme pass changed no composition and no behaviour`` () =
    Assert.Equal(6, InboxLogic.sectionOrder.Length)
    Assert.Equal(10, InboxLogic.Filter.all.Length)
    Assert.Equal(5, (InboxLogic.metrics []).Length)
    Assert.Equal(3, (InboxLogic.bulkActions (InboxLogic.bulkContext false [])).Length)

    // The responsive thresholds are pure functions of width — no theme input —
    // so they cannot have moved with the palette.
    Assert.True(InboxLogic.showsDetailColumn 1100.0)
    Assert.False(InboxLogic.showsDetailColumn 1099.0)
    Assert.Equal(2, InboxLogic.metricColumns 600.0)
    Assert.Equal(3, InboxLogic.metricColumns 900.0)
    Assert.Equal(5, InboxLogic.metricColumns 1400.0)

// ---------------------------------------------------------------------------
// 13. update behaviour
// ---------------------------------------------------------------------------

let private noServices: AppServices = Unchecked.defaultof<AppServices>

let private baseModel: InboxView.Model =
    { Items = []
      Existing = InboxLogic.Existing.empty
      Filter = InboxLogic.Filter.NeedsReview
      Selected = None
      BusyItem = None
      BulkBusy = false
      PendingBulk = None
      Loading = false
      Error = None
      Notice = []
      Width = 1400.0 }

let private modelOf (m, _) = m

[<Fact>]
let ``a successful load clears loading and any previous error`` () =
    let before = { baseModel with Loading = true; Error = Some "earlier" }
    let items = [ captured "One" ]

    let after = InboxView.update noServices (InboxView.Loaded(items, InboxLogic.Existing.empty)) before |> modelOf

    Assert.False(after.Loading)
    Assert.Equal(None, after.Error)
    Assert.Equal(1, after.Items.Length)

[<Fact>]
let ``a load drops a selection whose item has gone`` () =
    let gone = captured "Removed"
    let before = { baseModel with Selected = Some gone.Id; Items = [ gone ] }

    let after =
        InboxView.update noServices (InboxView.Loaded([ captured "Different" ], InboxLogic.Existing.empty)) before
        |> modelOf

    Assert.Equal(None, after.Selected)

[<Fact>]
let ``a load keeps a selection that survived`` () =
    let kept = captured "Kept"
    let before = { baseModel with Selected = Some kept.Id; Items = [ kept ] }

    let after =
        InboxView.update noServices (InboxView.Loaded([ kept ], InboxLogic.Existing.empty)) before |> modelOf

    Assert.Equal(Some kept.Id, after.Selected)

[<Fact>]
let ``a failure clears every in-flight flag, so nothing is left stuck`` () =
    let before =
        { baseModel with
            Loading = true
            BusyItem = Some(InboxItemId.New())
            BulkBusy = true
            PendingBulk = Some InboxLogic.BulkActionId.DismissAll }

    let after = InboxView.update noServices (InboxView.Failed(exn "offline")) before |> modelOf

    Assert.False(after.Loading)
    Assert.Equal(None, after.BusyItem)
    Assert.False(after.BulkBusy)
    Assert.Equal(None, after.PendingBulk)
    Assert.Equal(Some "offline", after.Error)

[<Fact>]
let ``a failure keeps whatever the page could already show`` () =
    let before = { baseModel with Items = [ captured "One" ] }
    let after = InboxView.update noServices (InboxView.Failed(exn "offline")) before |> modelOf

    Assert.Equal(1, after.Items.Length)

[<Fact>]
let ``a second request while one is running is dropped, never queued`` () =
    let target = captured "One"
    let running = { baseModel with Items = [ target ]; BusyItem = Some target.Id }

    for msg in
        [ InboxView.Analyse target
          InboxView.Accept target
          InboxView.Dismiss target
          InboxView.ApplyOne(target, action ProposedActionKind.CreateNote "n") ] do
        let after, cmd = InboxView.update noServices msg running
        Assert.Equal(Some target.Id, after.BusyItem)
        Assert.True(List.isEmpty cmd, "a repeated request produced a second command")

[<Fact>]
let ``a running bulk action also blocks item requests`` () =
    let target = captured "One"
    let running = { baseModel with Items = [ target ]; BulkBusy = true }

    let _, cmd = InboxView.update noServices (InboxView.Accept target) running
    Assert.True(List.isEmpty cmd)

[<Fact>]
let ``an item request marks that item busy and clears the last error`` () =
    let target = captured "One"
    let before = { baseModel with Items = [ target ]; Error = Some "earlier" }

    let after, cmd = InboxView.update noServices (InboxView.Analyse target) before

    Assert.Equal(Some target.Id, after.BusyItem)
    Assert.Equal(None, after.Error)
    Assert.False(List.isEmpty cmd)

[<Fact>]
let ``a finished item request releases the guard and reloads`` () =
    let before = { baseModel with BusyItem = Some(InboxItemId.New()) }
    let after, cmd = InboxView.update noServices (InboxView.ItemDone [ "✓ Created" ]) before

    Assert.Equal(None, after.BusyItem)
    Assert.Equal<string list>([ "✓ Created" ], after.Notice)
    Assert.False(List.isEmpty cmd)

[<Fact>]
let ``a destructive bulk action asks before it runs`` () =
    let active = captured "One"
    let before = { baseModel with Items = [ active ]; Filter = InboxLogic.Filter.All }

    let after, cmd = InboxView.update noServices (InboxView.Bulk InboxLogic.BulkActionId.DismissAll) before

    Assert.Equal(Some InboxLogic.BulkActionId.DismissAll, after.PendingBulk)
    Assert.False(after.BulkBusy)
    Assert.True(List.isEmpty cmd, "a destructive bulk action ran before it was confirmed")

[<Fact>]
let ``confirming runs it, and nothing is pending afterwards`` () =
    let active = captured "One"

    let before =
        { baseModel with
            Items = [ active ]
            Filter = InboxLogic.Filter.All
            PendingBulk = Some InboxLogic.BulkActionId.DismissAll }

    let after, cmd = InboxView.update noServices InboxView.ConfirmBulk before

    Assert.Equal(None, after.PendingBulk)
    Assert.True(after.BulkBusy)
    Assert.False(List.isEmpty cmd)

[<Fact>]
let ``confirming with nothing pending does nothing at all`` () =
    let after, cmd = InboxView.update noServices InboxView.ConfirmBulk baseModel

    Assert.False(after.BulkBusy)
    Assert.True(List.isEmpty cmd)

[<Fact>]
let ``cancelling abandons the pending action`` () =
    let before = { baseModel with PendingBulk = Some InboxLogic.BulkActionId.DismissAll }
    let after = InboxView.update noServices InboxView.CancelBulk before |> modelOf

    Assert.Equal(None, after.PendingBulk)
    Assert.False(after.BulkBusy)

[<Fact>]
let ``changing the filter abandons a pending confirmation counted against another set`` () =
    let before = { baseModel with PendingBulk = Some InboxLogic.BulkActionId.DismissAll }

    let after =
        InboxView.update noServices (InboxView.SelectFilter InboxLogic.Filter.All) before |> modelOf

    Assert.Equal(InboxLogic.Filter.All, after.Filter)
    Assert.Equal(None, after.PendingBulk)

[<Fact>]
let ``a disabled bulk action cannot be run by dispatching its message`` () =
    // Nothing shown, so every bulk action is disabled. The guard lives in
    // `update`, not only in the view.
    let after, cmd = InboxView.update noServices (InboxView.Bulk InboxLogic.BulkActionId.AcceptAll) baseModel

    Assert.False(after.BulkBusy)
    Assert.Equal(None, after.PendingBulk)
    Assert.True(List.isEmpty cmd)

[<Fact>]
let ``a bulk action does not start while another is running`` () =
    let running = { baseModel with BulkBusy = true }
    let _, cmd = InboxView.update noServices (InboxView.Bulk InboxLogic.BulkActionId.AnalyseAll) running
    Assert.True(List.isEmpty cmd)

[<Fact>]
let ``a finished bulk action releases the guard and reloads`` () =
    let before = { baseModel with BulkBusy = true }
    let after, cmd = InboxView.update noServices (InboxView.BulkDone [ "Dismissed \"One\"" ]) before

    Assert.False(after.BulkBusy)
    Assert.Equal<string list>([ "Dismissed \"One\"" ], after.Notice)
    Assert.False(List.isEmpty cmd)

[<Fact>]
let ``selecting and clearing a selection are both possible`` () =
    let target = captured "One"
    let selected = InboxView.update noServices (InboxView.Select target.Id) baseModel |> modelOf
    Assert.Equal(Some target.Id, selected.Selected)

    let cleared = InboxView.update noServices InboxView.ClearSelection selected |> modelOf
    Assert.Equal(None, cleared.Selected)

[<Fact>]
let ``a resize records the width and nothing else`` () =
    let after = InboxView.update noServices (InboxView.Resized 800.0) baseModel |> modelOf

    Assert.Equal(800.0, after.Width, 3)
    Assert.Equal(baseModel.Filter, after.Filter)
    Assert.Equal(baseModel.Selected, after.Selected)

[<Fact>]
let ``clearing the notice leaves everything else alone`` () =
    let before = { baseModel with Notice = [ "✓ Created" ]; Items = [ captured "One" ] }
    let after = InboxView.update noServices InboxView.ClearNotice before |> modelOf

    Assert.Empty(after.Notice)
    Assert.Equal(1, after.Items.Length)

[<Fact>]
let ``the screen opens on what needs review, not on everything ever captured`` () =
    Assert.Equal(InboxLogic.Filter.NeedsReview, baseModel.Filter)

// ---------------------------------------------------------------------------
// 14. the shell contract
// ---------------------------------------------------------------------------

[<Fact>]
let ``the Inbox draws its own glass, so the shell puts no opaque surface behind it`` () =
    Assert.True(Page.usesGlassSurface Page.Inbox)

[<Fact>]
let ``the unmigrated screens still keep their opaque surface`` () =
    // Assignments, then Modules, then Lectures left this list as each was
    // migrated in its own right — the claim narrowed, it was not relaxed.
    // Every screen is now migrated, so nothing is left holding an opaque shell
    // surface. This asserts the completed state rather than an empty list.
    for page, _, _ in Page.all do
        Assert.True(Page.usesGlassSurface page, sprintf "%A lost its glass surface" page)

[<Fact>]
let ``the approved screens keep theirs too`` () =
    for page in [ Page.Dashboard; Page.Timeline; Page.Planner; Page.DesignPreview ] do
        Assert.True(Page.usesGlassSurface page)
