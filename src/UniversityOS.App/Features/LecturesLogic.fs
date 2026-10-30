module UniversityOS.App.Features.LecturesLogic

open System
open UniversityOS.Domain
open UniversityOS.App

/// Every decision the Lectures screen makes, as pure functions over the real
/// domain types — testable without an Avalonia application or a database.
///
/// **This screen keeps more of its redesign than Modules did.** The mockup's
/// centrepiece is a nine-step "AI Processing Pipeline", and unlike the grade
/// machinery on the Modules card, that pipeline is real: every stage of it is a
/// field the application already stores. So is every one of the nine tabs.
///
/// What the domain genuinely cannot support:
///
/// | Redesign | Status |
/// |---|---|
/// | Attendance chip | **absent** — `Lecture` has no attended/watched flag, the same gap that removed the Modules lectures dial |
/// | "Week N" | **absent** — no term start date is stored anywhere, so a week number cannot be derived |
/// | Lecture duration | **absent** — nothing records how long a lecture ran |
/// | "Due for review" flashcard count | **absent** — `RevisionItem` has no review schedule |
/// | 5 of the 7 "AI learning hub" actions | **absent** — only `Summarize` and `GenerateFlashcards` reach a provider |
/// | Drag-active overlay | **omitted** — see `dropZoneMessage` |
///
/// One field is adapted rather than dropped: the mockup's per-lecture
/// **lecturer** does not exist on `Lecture`, but `StudyModule.Lecturer` does, so
/// the module's lecturer is shown. That is honest but not identical — a guest
/// lecture would be attributed to the module's usual lecturer.

// ---------------------------------------------------------------------------
// geometry
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module Geometry =

    /// No specular pass on a content card.
    [<Literal>]
    let contentHighlight = 0.0

    /// Half the hairline's 1px stroke.
    [<Literal>]
    let borderInset = 0.5

    /// The redesign's grid is `repeat(auto-fit, minmax(460px, 1fr))`.
    [<Literal>]
    let cardMinWidth = 460.0

    [<Literal>]
    let cardRadius = 22.0

    /// Drawn in both states so expanding recolours a card and never resizes it.
    [<Literal>]
    let cardBorderThickness = 1.0

    /// Glass action geometry, pinned equal to every other migrated screen.
    [<Literal>]
    let actionMinHeight = 38.0

    [<Literal>]
    let actionRadius = 11.0

    /// The redesign's small square glyph buttons.
    [<Literal>]
    let iconActionSize = 30.0

    [<Literal>]
    let iconActionRadius = 9.0

    /// A filter chip. Height and border reserved in BOTH states.
    [<Literal>]
    let filterMinHeight = 30.0

    [<Literal>]
    let filterRadius = 999.0

    [<Literal>]
    let filterBorderThickness = 1.0

    /// An asset / hub chip — square-ish, not a pill.
    [<Literal>]
    let chipRadius = 8.0

    /// The five analytics cards.
    [<Literal>]
    let metricRowMinHeight = 72.0

    /// The pipeline stepper: a 22px dot, a fixed-width label column beneath it,
    /// and a 22px connector between steps.
    [<Literal>]
    let pipelineDotSize = 22.0

    [<Literal>]
    let pipelineStepWidth = 100.0

    [<Literal>]
    let pipelineConnectorWidth = 22.0

    /// The same pipeline again, shrunk onto a card.
    [<Literal>]
    let miniDotSize = 14.0

    [<Literal>]
    let miniConnectorWidth = 12.0

    /// The expanded card's tab content panel.
    [<Literal>]
    let tabPanelMinHeight = 100.0

    /// The dashed banner at the top of the page.
    [<Literal>]
    let dropZoneRadius = 16.0

    /// Glyph column, gap, wrapping text column.
    [<Literal>]
    let bulletColumns = "Auto,9,*"

    /// **One alpha, used BOTH to paint a chip's wash and to measure the contrast
    /// of the text on it.** Two constants would silently drift, and the text
    /// would then be checked against a surface it is not actually sitting on.
    [<Literal>]
    let chipFillAlpha = "1F"

    /// The same rule again for the completed pipeline dot, which is filled more
    /// strongly than a chip because it carries a tick rather than a word.
    [<Literal>]
    let stageDotAlpha = "2E"

// ---------------------------------------------------------------------------
// documented thresholds
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module Threshold =

    /// A transcript shorter than this is treated as a stub rather than usable
    /// source text. Below roughly this length there is not enough for the model
    /// to summarise without inventing, so the AI actions stay disabled and say
    /// why.
    [<Literal>]
    let usableTextChars = 200

    /// How many recent activity entries the Timeline tab shows.
    [<Literal>]
    let timelineRows = 8

// ---------------------------------------------------------------------------
// facts
// ---------------------------------------------------------------------------

/// Everything the screen knows about one lecture, gathered in a single load so
/// no two cards can describe different moments.
///
/// Every field is a real query result. `LinkedNotes` and `LinkedDocuments` come
/// from `ILinkRepository` edges, not from the module — a note is attached to a
/// lecture only if someone actually linked it.
type Facts =
    { Lecture: Lecture
      Module: StudyModule option
      Slides: Document option
      Recording: Document option
      /// Cards whose `Source` is this lecture.
      Flashcards: RevisionItem list
      LinkedNotes: Note list
      LinkedDocuments: Document list
      /// The module's coursework. Shown, and labelled, as the module's.
      ModuleAssignments: Assignment list
      /// Future planner sessions carrying this lecture's module code.
      Sessions: PlannedSession list
      /// Activity entries pointing at this lecture.
      Activity: ActivityEntry list }

[<RequireQualifiedAccess>]
module Facts =

    /// A bare snapshot, for a lecture with nothing attached to it yet.
    let ofLecture (l: Lecture) =
        { Lecture = l
          Module = None
          Slides = None
          Recording = None
          Flashcards = []
          LinkedNotes = []
          LinkedDocuments = []
          ModuleAssignments = []
          Sessions = []
          Activity = [] }

let private trimmed (s: string option) =
    s |> Option.map (fun t -> t.Trim()) |> Option.filter (fun t -> t.Length > 0)

let hasTranscript (f: Facts) = (trimmed f.Lecture.Transcript).IsSome
let hasSummary (f: Facts) = (trimmed f.Lecture.Summary).IsSome
let isFiled (f: Facts) = f.Lecture.ModuleId.IsSome
let isDated (f: Facts) = f.Lecture.Date.IsSome

/// Text extracted from an attachment at import time.
let extractedText (f: Facts) =
    [ f.Slides; f.Recording ]
    |> List.choose (Option.bind (fun d -> trimmed d.ExtractedText))
    |> List.tryHead

/// **The one gate on both AI actions.** A model given nothing to read either
/// refuses or invents, so the buttons stay disabled and say which of the two
/// sources is missing rather than failing after the click.
let sourceText (f: Facts) =
    match trimmed f.Lecture.Transcript with
    | Some t -> Some t
    | None -> extractedText f

let hasUsableSource (f: Facts) =
    match sourceText f with
    | Some t -> t.Length >= Threshold.usableTextChars
    | None -> false

/// Why the AI actions are off, in the user's terms. `None` when they are on.
let aiDisabledReason (f: Facts) =
    match sourceText f with
    | None -> Some "Nothing to read yet — paste a transcript, or attach slides whose text can be extracted."
    | Some t when t.Length < Threshold.usableTextChars ->
        Some(sprintf "Only %d characters of text so far — too little to summarise honestly." t.Length)
    | Some _ -> None

// ---------------------------------------------------------------------------
// the processing pipeline — nine real stages
// ---------------------------------------------------------------------------

/// The redesign's nine-step stepper.
///
/// **Every stage is a stored field**, so this is a genuine progress report and
/// not an illustration. Each is `done` or `pending` and nothing else: no field
/// records "in progress", so no third state may be drawn.
[<RequireQualifiedAccess>]
type Stage =
    | Recorded
    | Filed
    | Dated
    | Slides
    | Recording
    | TextExtracted
    | Transcript
    | Summary
    | Flashcards

[<RequireQualifiedAccess>]
module Stage =

    let all =
        [ Stage.Recorded
          Stage.Filed
          Stage.Dated
          Stage.Slides
          Stage.Recording
          Stage.TextExtracted
          Stage.Transcript
          Stage.Summary
          Stage.Flashcards ]

    let label =
        function
        | Stage.Recorded -> "Lecture added"
        | Stage.Filed -> "Filed to a module"
        | Stage.Dated -> "Dated"
        | Stage.Slides -> "Slides attached"
        | Stage.Recording -> "Recording attached"
        | Stage.TextExtracted -> "Text extracted"
        | Stage.Transcript -> "Transcript"
        | Stage.Summary -> "AI summary"
        | Stage.Flashcards -> "Flashcards"

    /// What to do next, phrased as an action rather than a state.
    let action =
        function
        | Stage.Recorded -> "add the lecture"
        | Stage.Filed -> "file it to a module"
        | Stage.Dated -> "give it a date"
        | Stage.Slides -> "attach the slides"
        | Stage.Recording -> "attach the recording"
        | Stage.TextExtracted -> "attach a file whose text can be read"
        | Stage.Transcript -> "paste the transcript"
        | Stage.Summary -> "summarise it"
        | Stage.Flashcards -> "generate flashcards"

let stageDone (f: Facts) (stage: Stage) =
    match stage with
    | Stage.Recorded -> true
    | Stage.Filed -> isFiled f
    | Stage.Dated -> isDated f
    | Stage.Slides -> f.Lecture.SlidesDocumentId.IsSome
    | Stage.Recording -> f.Lecture.RecordingDocumentId.IsSome
    | Stage.TextExtracted -> (extractedText f).IsSome
    | Stage.Transcript -> hasTranscript f
    | Stage.Summary -> hasSummary f
    | Stage.Flashcards -> not f.Flashcards.IsEmpty

let completedStages (f: Facts) =
    Stage.all |> List.filter (stageDone f) |> List.length

/// The first stage not yet done — what the card's insight points at.
let nextStage (f: Facts) =
    Stage.all |> List.tryFind (stageDone f >> not)

/// The mockup's processing chip. A count, not a mood.
let processingLabel (f: Facts) =
    sprintf "%d of %d steps" (completedStages f) Stage.all.Length

let summaryStatus (f: Facts) = if hasSummary f then "written" else "not yet"

// ---------------------------------------------------------------------------
// the card's one-line insight
// ---------------------------------------------------------------------------

/// One deterministic sentence. Every branch states a recorded fact or names the
/// next recorded gap — never encouragement.
let insight (f: Facts) =
    match nextStage f with
    | None -> sprintf "Fully processed — summary written and %d flashcards generated." f.Flashcards.Length
    | Some stage ->
        let done' = completedStages f

        match stage with
        | Stage.Summary when hasUsableSource f ->
            sprintf "%d of %d steps done — there is text to work from, so %s." done' Stage.all.Length (Stage.action stage)
        | Stage.Summary -> sprintf "%d of %d steps done, but there is no readable text yet." done' Stage.all.Length
        | _ -> sprintf "%d of %d steps done — next, %s." done' Stage.all.Length (Stage.action stage)

// ---------------------------------------------------------------------------
// learning assets — the redesign's seven chips
// ---------------------------------------------------------------------------

/// The seven chips under "Learning assets". All seven are real, and each says
/// whether it is present rather than being hidden when absent — an absent asset
/// is exactly what the user needs to see.
let assets (f: Facts) : (string * string * bool) list =
    [ "▦", "Slides", f.Lecture.SlidesDocumentId.IsSome
      "▶", "Recording", f.Lecture.RecordingDocumentId.IsSome
      "≡", "Transcript", hasTranscript f
      "✳", "Summary", hasSummary f
      "◈", (if f.Flashcards.IsEmpty then "Flashcards" else sprintf "%d flashcards" f.Flashcards.Length), not f.Flashcards.IsEmpty
      "▤", (if f.LinkedNotes.IsEmpty then "Notes" else sprintf "%d notes" f.LinkedNotes.Length), not f.LinkedNotes.IsEmpty
      "▧",
      (if f.LinkedDocuments.IsEmpty then "Documents" else sprintf "%d documents" f.LinkedDocuments.Length),
      not f.LinkedDocuments.IsEmpty ]

// ---------------------------------------------------------------------------
// filters
// ---------------------------------------------------------------------------

/// The redesign's eight chips, as toggles. Several can be on at once and they
/// combine with AND, so a lecture must match every one that is lit.
///
/// `Summarised` and `NeedsSummary` are deliberately both offered even though
/// they can never both match — `emptyMessage` names that contradiction rather
/// than leaving a blank page unexplained.
[<RequireQualifiedAccess>]
type Filter =
    | Unfiled
    | Undated
    | HasSlides
    | HasRecording
    | HasTranscript
    | Summarised
    | NeedsSummary
    | HasFlashcards

[<RequireQualifiedAccess>]
module Filter =

    let all =
        [ Filter.Unfiled
          Filter.Undated
          Filter.HasSlides
          Filter.HasRecording
          Filter.HasTranscript
          Filter.Summarised
          Filter.NeedsSummary
          Filter.HasFlashcards ]

    let label =
        function
        | Filter.Unfiled -> "Unfiled"
        | Filter.Undated -> "Undated"
        | Filter.HasSlides -> "Has slides"
        | Filter.HasRecording -> "Has recording"
        | Filter.HasTranscript -> "Has transcript"
        | Filter.Summarised -> "Summarised"
        | Filter.NeedsSummary -> "Needs summary"
        | Filter.HasFlashcards -> "Has flashcards"

type Selection = Set<Filter>

[<RequireQualifiedAccess>]
module Selection =

    let empty: Selection = Set.empty

    let toggle (filter: Filter) (selection: Selection) : Selection =
        if selection.Contains filter then selection.Remove filter else selection.Add filter

/// Whether one chip keeps this lecture.
let matches (filter: Filter) (f: Facts) =
    match filter with
    | Filter.Unfiled -> not (isFiled f)
    | Filter.Undated -> not (isDated f)
    | Filter.HasSlides -> f.Lecture.SlidesDocumentId.IsSome
    | Filter.HasRecording -> f.Lecture.RecordingDocumentId.IsSome
    | Filter.HasTranscript -> hasTranscript f
    | Filter.Summarised -> hasSummary f
    // Not merely "no summary": a lecture with nothing to read is not waiting on
    // a summary, it is waiting on material.
    | Filter.NeedsSummary -> not (hasSummary f) && hasUsableSource f
    | Filter.HasFlashcards -> not f.Flashcards.IsEmpty

/// The module chip row. Only modules that genuinely have lectures appear, with
/// their real counts — an empty chip would be a dead end.
let moduleChips (facts: Facts list) : (StudyModule * int) list =
    facts
    |> List.choose (fun f -> f.Module)
    |> List.groupBy (fun m -> m.Id)
    |> List.map (fun (_, ms) -> List.head ms, ms.Length)
    |> List.sortBy (fun (m, _) -> m.Code)

/// The grid: every lit chip applied, then any lit module, then ordered.
///
/// Ordering is explicit rather than inherited from the repository, so a refresh
/// cannot move a card out from under the pointer. Dated lectures come first,
/// most recent first, then undated ones by title.
let visible (selection: Selection) (modules: Set<Guid>) (facts: Facts list) =
    let kept =
        facts
        |> List.filter (fun f -> selection |> Set.forall (fun chip -> matches chip f))
        |> List.filter (fun f ->
            modules.IsEmpty
            || (match f.Lecture.ModuleId with
                | Some id -> modules.Contains id.Value
                | None -> false))

    let dated, undated = kept |> List.partition isDated

    (dated |> List.sortByDescending (fun f -> f.Lecture.Date))
    @ (undated |> List.sortBy (fun f -> f.Lecture.Title))

/// Why the grid is empty, given what is switched on.
let emptyMessage (selection: Selection) (modules: Set<Guid>) (total: int) =
    if total = 0 then
        "No lectures yet. Add one, attach its slides or recording, and the pipeline above starts filling in."
    elif selection.Contains Filter.Summarised && selection.Contains Filter.NeedsSummary then
        "A lecture cannot be both summarised and waiting for a summary — turn one of those two chips off."
    elif not modules.IsEmpty && selection.IsEmpty then
        "No lectures are filed under the modules you have picked."
    else
        "No lecture matches every chip you have switched on."

// ---------------------------------------------------------------------------
// learning analytics — the five summary cards
// ---------------------------------------------------------------------------

/// A summary figure. `Value` is an option because some figures are genuinely
/// unknown rather than zero. `Note` is a qualifier on a real figure, never
/// empty-state prose.
type Metric = { Label: string; Value: string option; Note: string option }

/// The redesign's five analytics cards. Every one is a count of stored records.
let metrics (facts: Facts list) : Metric list =
    let count predicate = facts |> List.filter predicate |> List.length

    let withMaterial =
        count (fun f -> f.Lecture.SlidesDocumentId.IsSome || f.Lecture.RecordingDocumentId.IsSome)

    let transcribed = count (fun f -> hasTranscript f || (extractedText f).IsSome)
    let summarised = count hasSummary
    let cards = facts |> List.sumBy (fun f -> f.Flashcards.Length)
    let unfiled = count (isFiled >> not)

    [ { Label = "Lectures"
        Value = Some(string facts.Length)
        Note = if unfiled > 0 then Some(sprintf "%d unfiled" unfiled) else None }

      { Label = "With materials"
        Value = Some(string withMaterial)
        Note = None }

      { Label = "Transcribed"
        Value = Some(string transcribed)
        Note = None }

      { Label = "Summarised"
        Value = Some(string summarised)
        Note =
          let waiting = facts |> List.filter (fun f -> matches Filter.NeedsSummary f) |> List.length
          if waiting > 0 then Some(sprintf "%d ready to summarise" waiting) else None }

      { Label = "Flashcards"
        Value = Some(string cards)
        Note = None } ]

/// The lecture the page-level pipeline illustrates: the least-processed one, so
/// the panel shows what the pipeline is actually waiting on rather than a
/// finished example. `None` on an empty screen, where the panel still renders
/// with every step pending.
let pipelineExample (facts: Facts list) =
    facts
    |> List.sortBy (fun f -> completedStages f, f.Lecture.Title)
    |> List.tryHead

// ---------------------------------------------------------------------------
// the expanded card's tabs
// ---------------------------------------------------------------------------

/// The redesign shows nine tabs. **All nine have a real query behind them.**
///
/// The mockup's ninth is a dashed "graph preview" placeholder over a count of
/// linked concepts. The count is genuinely computable — `ILinkRepository` stores
/// real edges — so this shows the edges themselves rather than a placeholder.
[<RequireQualifiedAccess>]
type Tab =
    | Summary
    | Transcript
    | Notes
    | Assignments
    | Documents
    | Flashcards
    | Planner
    | Timeline
    | Links

[<RequireQualifiedAccess>]
module Tab =

    let all =
        [ Tab.Summary
          Tab.Transcript
          Tab.Notes
          Tab.Assignments
          Tab.Documents
          Tab.Flashcards
          Tab.Planner
          Tab.Timeline
          Tab.Links ]

    let label =
        function
        | Tab.Summary -> "Summary"
        | Tab.Transcript -> "Transcript"
        | Tab.Notes -> "Notes"
        | Tab.Assignments -> "Assignments"
        | Tab.Documents -> "Documents"
        | Tab.Flashcards -> "Flashcards"
        | Tab.Planner -> "Planner"
        | Tab.Timeline -> "Timeline"
        | Tab.Links -> "Links"

    let emptyMessage =
        function
        | Tab.Summary -> "No summary yet."
        | Tab.Transcript -> "No transcript. Paste one via Edit and the summary gets much sharper."
        | Tab.Notes -> "No notes are linked to this lecture."
        | Tab.Assignments -> "No coursework is filed under this lecture's module."
        | Tab.Documents -> "Nothing attached and nothing linked."
        | Tab.Flashcards -> "No flashcards generated from this lecture yet."
        | Tab.Planner -> "The planner has not scheduled this lecture's module."
        | Tab.Timeline -> "Nothing has been recorded against this lecture."
        | Tab.Links -> "Nothing is linked to this lecture yet."

/// How many real items sit behind each tab, so a tab can show its count and an
/// empty one can be told apart from an unvisited one.
let tabCount (f: Facts) (tab: Tab) =
    match tab with
    | Tab.Summary -> if hasSummary f then 1 else 0
    | Tab.Transcript -> if hasTranscript f then 1 else 0
    | Tab.Notes -> f.LinkedNotes.Length
    | Tab.Assignments -> f.ModuleAssignments.Length
    | Tab.Documents ->
        [ f.Lecture.SlidesDocumentId; f.Lecture.RecordingDocumentId ]
        |> List.filter Option.isSome
        |> List.length
        |> (+) f.LinkedDocuments.Length
    | Tab.Flashcards -> f.Flashcards.Length
    | Tab.Planner -> f.Sessions.Length
    | Tab.Timeline -> f.Activity.Length
    | Tab.Links -> f.LinkedNotes.Length + f.LinkedDocuments.Length

// ---------------------------------------------------------------------------
// actions
// ---------------------------------------------------------------------------

/// The two AI operations that genuinely reach a provider.
///
/// The redesign's "AI learning hub" offers seven. `AiService` exposes
/// `Summarize` and `GenerateFlashcards`; the other five — quiz, explain, mind
/// map, podcast, practice paper — have no implementation anywhere, so they are
/// **omitted rather than drawn as dead buttons**. The section is titled "AI
/// actions" rather than "AI learning hub" for the same reason.
[<RequireQualifiedAccess>]
type AiAction =
    | Summarise
    | GenerateFlashcards

[<RequireQualifiedAccess>]
module AiAction =

    let all = [ AiAction.Summarise; AiAction.GenerateFlashcards ]

    let label =
        function
        | AiAction.Summarise -> "✳ Summarise"
        | AiAction.GenerateFlashcards -> "◈ Generate flashcards"

    let busyLabel =
        function
        | AiAction.Summarise -> "✳ Summarising…"
        | AiAction.GenerateFlashcards -> "◈ Generating…"

/// The four always-visible glyph buttons on a card. Each carries its label as
/// an accessible name and a tooltip, so a glyph-only control is still readable.
[<RequireQualifiedAccess>]
type CardAction =
    | Edit
    | AttachSlides
    | AttachRecording
    | OpenMaterial

[<RequireQualifiedAccess>]
module CardAction =

    let all =
        [ CardAction.Edit; CardAction.AttachSlides; CardAction.AttachRecording; CardAction.OpenMaterial ]

    let glyph =
        function
        | CardAction.Edit -> "✎"
        | CardAction.AttachSlides -> "▦"
        | CardAction.AttachRecording -> "▶"
        | CardAction.OpenMaterial -> "↗"

    let label (f: Facts) =
        function
        | CardAction.Edit -> "Edit this lecture"
        | CardAction.AttachSlides -> if f.Lecture.SlidesDocumentId.IsSome then "Replace the slides" else "Attach slides"
        | CardAction.AttachRecording ->
            if f.Lecture.RecordingDocumentId.IsSome then "Replace the recording" else "Attach a recording"
        | CardAction.OpenMaterial ->
            if f.Slides.IsSome || f.Recording.IsSome then "Open the attached file"
            else "Nothing attached to open"

    /// Geometry never changes: a disabled button is still drawn, still the same
    /// size, and still says why it is off.
    let enabled (f: Facts) =
        function
        | CardAction.OpenMaterial -> f.Slides.IsSome || f.Recording.IsSome
        | _ -> true

/// The nine destinations in the expanded card's action row. Every one is a real
/// `Page`; `Modules` is disabled for an unfiled lecture rather than hidden.
let expandedActions: (string * Page) list =
    [ "Module", Page.Modules
      "Assignments", Page.Assignments
      "Notes", Page.Notes
      "Documents", Page.Documents
      "Planner", Page.Planner
      "Timeline", Page.Timeline
      "Assistant", Page.Chat
      "Graph", Page.Graph
      "Search", Page.Search ]

// ---------------------------------------------------------------------------
// page composition
// ---------------------------------------------------------------------------

/// The page, as data — the redesign's own shape.
[<RequireQualifiedAccess>]
type Section =
    | Header
    | DropZone
    | Analytics
    | Pipeline
    | Filters
    | ModuleFilters
    | Grid

let sectionOrder =
    [ Section.Header
      Section.DropZone
      Section.Analytics
      Section.Pipeline
      Section.Filters
      Section.ModuleFilters
      Section.Grid ]

/// The regions inside an expanded card, in the redesign's order.
[<RequireQualifiedAccess>]
type ExpandedRegion =
    | Divider
    | Actions
    | Tabs
    | TabPanel

let expandedOrder =
    [ ExpandedRegion.Divider
      ExpandedRegion.Actions
      ExpandedRegion.Tabs
      ExpandedRegion.TabPanel ]

/// **The corrected claim on the drop banner.**
///
/// The redesign promises that a dropped file is detected as a lecture, assigned
/// to a module and summarised automatically. The real path, traced through
/// `Shell.subscriptions` → `CaptureService.CaptureFile`, is two things at once:
///
/// 1. the file **is** imported straight away — copied into the managed store,
///    text-extracted and indexed, so a real `Document` exists immediately;
/// 2. a capture also lands in the **Inbox**, where the analysis proposes what to
///    file it under.
///
/// What does **not** happen automatically is the second half of the mockup's
/// claim: no lecture is created and no module is assigned until the user accepts
/// a proposal. The banner states exactly that split.
///
/// The mockup's second drag region — a full-page dashed overlay that appears
/// while a drag is in flight — is **omitted**. Its trigger is a live DragOver
/// event, which cannot be exercised from here, and a region that silently never
/// appears is worse than one honestly absent.
[<Literal>]
let dropZoneMessage =
    "Drop a file anywhere in the window and it is imported into Documents straight away — copied, text-extracted and indexed. A capture also goes to the Inbox, where the AI proposes what to file it under. It is not attached to this or any lecture until you accept a proposal."

// ---------------------------------------------------------------------------
// responsive layout
// ---------------------------------------------------------------------------

/// Lecture cards per row. The redesign's grid is
/// `repeat(auto-fit, minmax(460px, 1fr))`.
let cardColumns (contentWidth: float) =
    max 1 (int (contentWidth / Geometry.cardMinWidth))

/// Columns for the five analytics cards, matching every other migrated screen.
let metricColumns (contentWidth: float) =
    if contentWidth < 700.0 then 2
    elif contentWidth < 1180.0 then 3
    else 5
