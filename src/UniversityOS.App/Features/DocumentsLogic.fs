module UniversityOS.App.Features.DocumentsLogic

open System
open UniversityOS.Domain
open UniversityOS.App

/// Every decision the Documents screen makes, as pure functions over the real
/// domain types — testable without an Avalonia application or a database.
///
/// **The import flow is the load-bearing truth on this screen.** Traced through
/// `Shell.subscriptions` → `Shell.FilesPicked` → `CaptureService.CaptureFile`,
/// a dropped or picked file does two things at once:
///
/// 1. it is **imported immediately** — `ImportFile` copies it into the managed
///    store, extracts its text where the format allows, and indexes it, so a
///    real `Document` exists straight away;
/// 2. an **Inbox capture** is also created, carrying the `DocumentId`, where the
///    analysis proposes what to file it under.
///
/// Module, assignment and lecture filing stays **proposed** until reviewed. Every
/// word this screen says about importing has to carry both halves.
///
/// | Redesign | Status |
/// |---|---|
/// | Collections chips | **absent** — no collection concept exists |
/// | "Last opened" | **absent** — nothing records an open |
/// | Key concepts / extracted topics | **absent** — nothing extracts either, and `Document` has no tags |
/// | In-app file preview | **omitted** — there is no renderer; metadata and a real Open action instead |
/// | Page count, OCR quality, AI confidence, reading time | **absent** — none is stored |
/// | Drag-active overlay | **omitted** — the shell exposes a drop handler but no drag-enter state |

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

    /// The mockup's `repeat(auto-fill, minmax(220px, 1fr))`.
    [<Literal>]
    let cardMinWidth = 220.0

    [<Literal>]
    let cardRadius = 18.0

    /// Drawn in BOTH states, so selecting a card recolours it and never resizes
    /// it.
    [<Literal>]
    let cardBorderThickness = 1.0

    /// The sticky detail column.
    [<Literal>]
    let detailPanelWidth = 340.0

    /// The mockup's dashed preview box. It shows the file's type and name — not
    /// its contents, because nothing here can render a PDF.
    [<Literal>]
    let previewBoxHeight = 140.0

    /// The six analytics cards.
    [<Literal>]
    let metricRowMinHeight = 64.0

    /// Glass action geometry, pinned equal to every other migrated screen.
    [<Literal>]
    let actionMinHeight = 38.0

    [<Literal>]
    let actionRadius = 11.0

    /// The mockup's smaller in-card glyph buttons.
    [<Literal>]
    let iconActionSize = 28.0

    [<Literal>]
    let iconActionRadius = 8.0

    /// A filter chip. Height and border reserved in BOTH states.
    [<Literal>]
    let filterMinHeight = 28.0

    [<Literal>]
    let filterRadius = 999.0

    [<Literal>]
    let filterBorderThickness = 1.0

    /// The file-type tile on a card.
    [<Literal>]
    let typeTileSize = 36.0

    [<Literal>]
    let typeTileRadius = 9.0

    /// **One alpha, used BOTH to paint a chip's wash and to measure the contrast
    /// of the text on it.** Two constants would silently drift, and the text
    /// would then be checked against a surface it is not actually sitting on —
    /// exactly the defect found on the Lectures pipeline dot.
    [<Literal>]
    let chipFillAlpha = "1F"

    /// The status pill and the selected card are filled more strongly.
    [<Literal>]
    let statusAlpha = "2E"

    /// Glyph column, gap, wrapping text column.
    [<Literal>]
    let bulletColumns = "Auto,9,*"

    /// Label column, gap, value column.
    [<Literal>]
    let detailColumns = "Auto,10,*"

// ---------------------------------------------------------------------------
// documented thresholds
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module Threshold =

    /// "Recently imported" horizon.
    [<Literal>]
    let recentDays = 7

    /// How much extracted text a card's summary line shows.
    [<Literal>]
    let excerptChars = 150

    /// How much extracted text the detail panel shows before truncating. The
    /// full text can be many megabytes; the panel is a window onto it, not a
    /// reader.
    [<Literal>]
    let extractedTextChars = 4000

    /// Below this there is too little text for a summary to be honest.
    [<Literal>]
    let usableTextChars = 200

    /// How many activity entries the detail panel shows.
    [<Literal>]
    let historyRows = 6

// ---------------------------------------------------------------------------
// file size
// ---------------------------------------------------------------------------

/// Stored bytes, in the units a person reads. Preserved from the legacy screen.
let sizeLabel (bytes: int64) =
    if bytes < 1024L then sprintf "%d B" bytes
    elif bytes < 1024L * 1024L then sprintf "%.0f KB" (float bytes / 1024.0)
    else sprintf "%.1f MB" (float bytes / 1024.0 / 1024.0)

/// A file type's identity colour. Preserved from the legacy screen so a PDF is
/// the same colour it has always been.
let kindHex (kind: DocumentKind) =
    match kind with
    | DocumentKind.Pdf -> Theme.Identity.terracotta
    | DocumentKind.Slides -> Theme.Identity.gold
    | DocumentKind.Word -> Theme.Identity.sky
    | DocumentKind.Markdown
    | DocumentKind.Text -> Theme.Identity.green
    | DocumentKind.Image -> Theme.Identity.orchid
    | DocumentKind.Audio -> Theme.Identity.teal
    | DocumentKind.Other -> Theme.Identity.neutral

/// A glyph for the type tile.
let kindGlyph (kind: DocumentKind) =
    match kind with
    | DocumentKind.Pdf -> "▤"
    | DocumentKind.Slides -> "▦"
    | DocumentKind.Word -> "▥"
    | DocumentKind.Markdown -> "≡"
    | DocumentKind.Text -> "≡"
    | DocumentKind.Image -> "◨"
    | DocumentKind.Audio -> "▶"
    | DocumentKind.Other -> "▧"

/// **Text extraction is impossible for some formats, not merely absent.**
/// `DocumentImportService` extracts from PDF, Word, Slides, Markdown and Text.
/// An image or an audio file has no text to extract, so its extraction stage is
/// *unavailable* rather than *pending*, and no action can change that.
let canExtractText (kind: DocumentKind) =
    match kind with
    | DocumentKind.Image
    | DocumentKind.Audio
    | DocumentKind.Other -> false
    | _ -> true

// ---------------------------------------------------------------------------
// facts
// ---------------------------------------------------------------------------

/// Everything the screen knows about one document, gathered in a single load.
type Facts =
    { Document: Document
      Module: StudyModule option
      /// `File.Exists StoredPath`, resolved once per load. A missing file is a
      /// real, previously unhandled state.
      FileExists: bool
      /// Edges where this document is the `From` end.
      LinksOut: EntityRef list
      /// Edges where this document is the `To` end.
      LinksIn: EntityRef list
      /// Lectures carrying this document as slides or a recording — a real
      /// foreign key, not an edge.
      LectureCount: int
      /// Cards whose `Source` is this document.
      Flashcards: int
      /// The Inbox capture created when the file was imported, if it is still
      /// there.
      InboxPending: bool
      Activity: ActivityEntry list }

[<RequireQualifiedAccess>]
module Facts =

    /// A bare snapshot, for a document with nothing filed against it.
    let ofDocument (d: Document) =
        { Document = d
          Module = None
          FileExists = true
          LinksOut = []
          LinksIn = []
          LectureCount = 0
          Flashcards = 0
          InboxPending = false
          Activity = [] }

let private trimmed (s: string option) =
    s |> Option.map (fun t -> t.Trim()) |> Option.filter (fun t -> t.Length > 0)

let extractedText (f: Facts) = trimmed f.Document.ExtractedText

/// **`Document.Summary` is written by exactly one thing.** `DocumentImportService`
/// sets it to `None`; only `Ai.Summarize` ever fills it. That is why this screen
/// may label it `AI SUMMARY` where the Notes screen may not.
let aiSummary (f: Facts) = trimmed f.Document.Summary

let hasText (f: Facts) = (extractedText f).IsSome
let hasSummary (f: Facts) = (aiSummary f).IsSome
let isFiled (f: Facts) = f.Document.ModuleId.IsSome
let isMissing (f: Facts) = not f.FileExists

let linkCount (f: Facts) =
    f.LinksOut.Length + f.LinksIn.Length + f.LectureCount + f.Flashcards

let isUnlinked (f: Facts) = linkCount f = 0 && not (isFiled f)

let isRecent (now: DateTime) (f: Facts) =
    (now - f.Document.ImportedAt).TotalDays <= float Threshold.recentDays

/// Enough text for a summary to be worth asking for.
let hasUsableText (f: Facts) =
    match extractedText f with
    | Some t -> t.Length >= Threshold.usableTextChars
    | None -> false

/// Why the Summarise action is off. `None` when it is on.
let summariseDisabledReason (f: Facts) =
    if isMissing f then
        Some "The file is missing from the store, so there is nothing to read."
    elif not (canExtractText f.Document.Kind) then
        Some(sprintf "No text can be extracted from %s files." ((DocumentKind.label f.Document.Kind).ToLowerInvariant()))
    else
        match extractedText f with
        | None -> Some "No text was extracted from this file."
        | Some t when t.Length < Threshold.usableTextChars ->
            Some(sprintf "Only %d characters were extracted — too little to summarise honestly." t.Length)
        | Some _ -> None

/// The card's one-line description, and **the label that must go with it**.
///
/// A summary written by the provider and an excerpt sliced off the extracted
/// text are different things, and the card says which it is showing.
[<RequireQualifiedAccess>]
type BlurbSource =
    | Ai
    | Extracted
    | None'

let blurb (f: Facts) : BlurbSource * string =
    match aiSummary f with
    | Some s -> BlurbSource.Ai, Format.truncate Threshold.excerptChars s
    | None ->
        match extractedText f with
        | Some t -> BlurbSource.Extracted, Format.truncate Threshold.excerptChars (t.Replace('\n', ' '))
        | None -> BlurbSource.None', ""

[<RequireQualifiedAccess>]
module BlurbSource =

    /// The one label that must never lie.
    let label =
        function
        | BlurbSource.Ai -> "AI SUMMARY"
        | BlurbSource.Extracted -> "EXTRACTED TEXT"
        | BlurbSource.None' -> "NO TEXT"

// ---------------------------------------------------------------------------
// the processing pipeline
// ---------------------------------------------------------------------------

/// A stage's state. Four, not two, because this pipeline has genuine failure and
/// genuine impossibility in it — a missing file has **failed**, and text
/// extraction from an image is **unavailable** rather than merely pending.
[<RequireQualifiedAccess>]
type StageState =
    | Done
    | Pending
    | Unavailable
    | Failed

[<RequireQualifiedAccess>]
module StageState =

    /// Carried in text as well as colour, so the state does not depend on being
    /// able to tell two dots apart.
    let mark =
        function
        | StageState.Done -> "✓"
        | StageState.Pending -> "·"
        | StageState.Unavailable -> "–"
        | StageState.Failed -> "!"

    let label =
        function
        | StageState.Done -> "done"
        | StageState.Pending -> "not yet"
        | StageState.Unavailable -> "not possible for this file type"
        | StageState.Failed -> "failed"

/// The redesign draws nine steps. Six of them exist here; the other three have
/// no stored state, so they are not drawn.
[<RequireQualifiedAccess>]
type Stage =
    | Imported
    | Stored
    | TextExtracted
    | InboxCapture
    | Summarised
    | Linked

[<RequireQualifiedAccess>]
module Stage =

    let all =
        [ Stage.Imported
          Stage.Stored
          Stage.TextExtracted
          Stage.InboxCapture
          Stage.Summarised
          Stage.Linked ]

    let label =
        function
        | Stage.Imported -> "Imported"
        | Stage.Stored -> "File in store"
        | Stage.TextExtracted -> "Text extracted"
        | Stage.InboxCapture -> "Inbox review"
        | Stage.Summarised -> "AI summary"
        | Stage.Linked -> "Linked"

let stageState (f: Facts) (stage: Stage) =
    match stage with
    | Stage.Imported -> StageState.Done
    | Stage.Stored -> if f.FileExists then StageState.Done else StageState.Failed
    | Stage.TextExtracted ->
        if hasText f then StageState.Done
        elif canExtractText f.Document.Kind then StageState.Pending
        else StageState.Unavailable
    // The capture is created at import and cleared when the user reviews it, so
    // "no pending capture" means reviewed — which is the finished state.
    | Stage.InboxCapture -> if f.InboxPending then StageState.Pending else StageState.Done
    | Stage.Summarised ->
        if hasSummary f then StageState.Done
        elif (summariseDisabledReason f).IsSome then StageState.Unavailable
        else StageState.Pending
    | Stage.Linked -> if linkCount f > 0 || isFiled f then StageState.Done else StageState.Pending

let completedStages (f: Facts) =
    Stage.all |> List.filter (fun s -> stageState f s = StageState.Done) |> List.length

/// The card's status pill. A count, and the one word that matters most.
let statusLabel (f: Facts) =
    if isMissing f then "File missing"
    else sprintf "%d of %d" (completedStages f) Stage.all.Length

// ---------------------------------------------------------------------------
// filters
// ---------------------------------------------------------------------------

/// Ten chips, every one a deterministic predicate over stored fields.
///
/// `Unlinked` and `LinkedToModule` can never both match; `emptyMessage` names
/// that contradiction rather than leaving a blank grid unexplained.
[<RequireQualifiedAccess>]
type Filter =
    | RecentlyImported
    | HasText
    | NoText
    | HasSummary
    | NeedsSummary
    | Unfiled
    | Unlinked
    | MissingFile
    | InboxPending
    | LinkedToModule

[<RequireQualifiedAccess>]
module Filter =

    let all =
        [ Filter.RecentlyImported
          Filter.HasText
          Filter.NoText
          Filter.HasSummary
          Filter.NeedsSummary
          Filter.Unfiled
          Filter.Unlinked
          Filter.MissingFile
          Filter.InboxPending
          Filter.LinkedToModule ]

    let label =
        function
        | Filter.RecentlyImported -> "Recent"
        | Filter.HasText -> "Has text"
        | Filter.NoText -> "No text"
        | Filter.HasSummary -> "Summarised"
        | Filter.NeedsSummary -> "Needs summary"
        | Filter.Unfiled -> "Unfiled"
        | Filter.Unlinked -> "Unlinked"
        | Filter.MissingFile -> "File missing"
        | Filter.InboxPending -> "Inbox pending"
        | Filter.LinkedToModule -> "Filed"

type Selection = Set<Filter>

[<RequireQualifiedAccess>]
module Selection =

    let empty: Selection = Set.empty

    let toggle (filter: Filter) (selection: Selection) : Selection =
        if selection.Contains filter then selection.Remove filter else selection.Add filter

let matches (now: DateTime) (filter: Filter) (f: Facts) =
    match filter with
    | Filter.RecentlyImported -> isRecent now f
    | Filter.HasText -> hasText f
    | Filter.NoText -> not (hasText f)
    | Filter.HasSummary -> hasSummary f
    // Not merely "no summary": a file with nothing readable is not waiting on a
    // summary, it is waiting on text — or can never have one.
    | Filter.NeedsSummary -> not (hasSummary f) && (summariseDisabledReason f).IsNone
    | Filter.Unfiled -> not (isFiled f)
    | Filter.Unlinked -> isUnlinked f
    | Filter.MissingFile -> isMissing f
    | Filter.InboxPending -> f.InboxPending
    | Filter.LinkedToModule -> isFiled f

/// The search box.
///
/// **This filters the loaded list; it is not the global index.**
/// `ISearchRepository` backs the Search screen. Here the user narrows a visible
/// list, so an in-memory match over title, filename and extracted text is both
/// what they expect and instant.
let searchMatches (query: string) (f: Facts) =
    let q = query.Trim()

    if q.Length = 0 then
        true
    else
        let contains (s: string) =
            not (isNull s) && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0

        contains f.Document.Title
        || contains f.Document.FileName
        || (match extractedText f with
            | Some t -> contains t
            | None -> false)

/// Modules that genuinely have documents, with real counts.
let moduleChips (facts: Facts list) : (StudyModule * int) list =
    facts
    |> List.choose (fun f -> f.Module)
    |> List.groupBy (fun m -> m.Id)
    |> List.map (fun (_, ms) -> List.head ms, ms.Length)
    |> List.sortBy (fun (m, _) -> m.Code)

/// Every lit chip applied, then the module chips, then the search box.
///
/// Ordering is explicit rather than inherited from the repository, so a refresh
/// cannot move a card out from under the pointer. Missing files sort first —
/// they are the one state the user has to act on.
let visible (now: DateTime) (selection: Selection) (modules: Set<Guid>) (query: string) (facts: Facts list) =
    facts
    |> List.filter (fun f -> selection |> Set.forall (fun chip -> matches now chip f))
    |> List.filter (fun f ->
        modules.IsEmpty
        || (match f.Document.ModuleId with
            | Some id -> modules.Contains id.Value
            | None -> false))
    |> List.filter (searchMatches query)
    |> List.sortBy (fun f -> (if isMissing f then 0 else 1), -f.Document.ImportedAt.Ticks)

/// Why the grid is empty, given what is switched on.
let emptyMessage (selection: Selection) (modules: Set<Guid>) (query: string) (total: int) =
    if total = 0 then
        "No documents yet. Import a file above — it lands in the library straight away, and a capture goes to the Inbox for filing."
    elif selection.Contains Filter.Unlinked && selection.Contains Filter.LinkedToModule then
        "A document cannot be both unlinked and filed — turn one of those two chips off."
    elif selection.Contains Filter.HasText && selection.Contains Filter.NoText then
        "A document cannot both have and not have extracted text — turn one of those two chips off."
    elif query.Trim().Length > 0 then
        sprintf "Nothing matches \"%s\"." (Format.truncate 30 (query.Trim()))
    elif not modules.IsEmpty && selection.IsEmpty then
        "No documents are filed under the modules you have picked."
    else
        "No document matches every chip you have switched on."

// ---------------------------------------------------------------------------
// the six analytics cards
// ---------------------------------------------------------------------------

/// A summary figure. `Value` is an option because some figures are genuinely
/// unknown rather than zero.
type Metric = { Label: string; Value: string option }

/// The redesign's six analytics cards. Every one is a count or a sum of stored
/// fields — no page counts, no OCR accuracy, no confidence, no average
/// processing time, none of which is stored.
let metrics (now: DateTime) (facts: Facts list) : Metric list =
    let count predicate = facts |> List.filter predicate |> List.length

    [ { Label = "documents"; Value = Some(string facts.Length) }
      { Label = "imported this week"; Value = Some(string (count (isRecent now))) }
      { Label = "with text"; Value = Some(string (count hasText)) }
      { Label = "summarised"; Value = Some(string (count hasSummary)) }
      { Label = "unfiled"; Value = Some(string (count (isFiled >> not))) }
      { Label = "missing files"; Value = Some(string (count isMissing)) } ]

/// Total stored size, shown beside the metrics rather than as a seventh card.
let totalSize (facts: Facts list) =
    facts |> List.sumBy (fun f -> f.Document.SizeBytes) |> sizeLabel

// ---------------------------------------------------------------------------
// rule-based insights
// ---------------------------------------------------------------------------

/// The redesign calls this "AI Insights". **No model produces any of it**, so it
/// is labelled `RULE-BASED INSIGHTS` and every line is a count over stored
/// fields. An empty list means there is genuinely nothing to point at, and the
/// panel is omitted rather than padded.
let insights (now: DateTime) (facts: Facts list) : (string * string) list =
    let count predicate = facts |> List.filter predicate |> List.length
    let missing = count isMissing
    let pending = count (fun f -> f.InboxPending)
    let unfiled = count (isFiled >> not)
    let noText = count (fun f -> not (hasText f) && canExtractText f.Document.Kind)
    let summarisable = count (fun f -> matches now Filter.NeedsSummary f)

    [ if missing > 0 then
          "!",
          sprintf
              "%d %s missing from the store. The record and its links survive; the file itself does not."
              missing
              (if missing = 1 then "file is" else "files are")

      if pending > 0 then
          "▣", sprintf "%d %s still waiting in the Inbox for you to confirm where they belong." pending (if pending = 1 then "capture is" else "captures are")

      if unfiled > 0 then
          "◫", sprintf "%d %s not filed under a module." unfiled (if unfiled = 1 then "document is" else "documents are")

      if noText > 0 then
          "≡",
          sprintf
              "%d %s no extracted text, although the format allows it."
              noText
              (if noText = 1 then "document has" else "documents have")

      if summarisable > 0 then
          "✦", sprintf "%d %s enough text to be summarised." summarisable (if summarisable = 1 then "document has" else "documents have") ]

// ---------------------------------------------------------------------------
// related knowledge
// ---------------------------------------------------------------------------

/// The categories the detail panel can show. **A category with nothing in it is
/// omitted entirely**, rather than drawn as a decorative zero.
[<RequireQualifiedAccess>]
type RelatedKind =
    | Modules
    | Assignments
    | Lectures
    | Notes
    | Documents
    | Flashcards

[<RequireQualifiedAccess>]
module RelatedKind =

    let label =
        function
        | RelatedKind.Modules -> "Module"
        | RelatedKind.Assignments -> "Assignments"
        | RelatedKind.Lectures -> "Lectures"
        | RelatedKind.Notes -> "Notes"
        | RelatedKind.Documents -> "Documents"
        | RelatedKind.Flashcards -> "Flashcards"

    let ofEntity (kind: EntityKind) =
        match kind with
        | EntityKind.Module -> Some RelatedKind.Modules
        | EntityKind.Assignment -> Some RelatedKind.Assignments
        | EntityKind.Lecture -> Some RelatedKind.Lectures
        | EntityKind.Note -> Some RelatedKind.Notes
        | EntityKind.Document -> Some RelatedKind.Documents
        | EntityKind.RevisionItem -> Some RelatedKind.Flashcards
        | _ -> None

    /// **Focused navigation is not supported.** No screen in this application
    /// takes an entity id on entry, so these open the owning screen. The
    /// limitation is documented rather than hidden behind a link that would
    /// appear to do more than it does.
    let page =
        function
        | RelatedKind.Modules -> Page.Modules
        | RelatedKind.Assignments -> Page.Assignments
        | RelatedKind.Lectures -> Page.Lectures
        | RelatedKind.Notes -> Page.Notes
        | RelatedKind.Documents -> Page.Documents
        | RelatedKind.Flashcards -> Page.Modules

/// Every related category that genuinely has something in it, with its count.
let related (f: Facts) : (RelatedKind * int) list =
    let fromEdges =
        f.LinksOut @ f.LinksIn
        |> List.choose (fun e -> RelatedKind.ofEntity e.Kind)
        |> List.countBy id

    let extra =
        [ if isFiled f then RelatedKind.Modules, 1
          if f.LectureCount > 0 then RelatedKind.Lectures, f.LectureCount
          if f.Flashcards > 0 then RelatedKind.Flashcards, f.Flashcards ]

    (fromEdges @ extra)
    |> List.groupBy fst
    |> List.map (fun (k, xs) -> k, xs |> List.sumBy snd)
    |> List.filter (fun (_, n) -> n > 0)
    |> List.sortBy (fun (k, _) -> RelatedKind.label k)

// ---------------------------------------------------------------------------
// page composition
// ---------------------------------------------------------------------------

/// The page, as data — the redesign's own order.
[<RequireQualifiedAccess>]
type Section =
    | Header
    | DropZone
    | Metrics
    | Insights
    | Controls
    | Filters
    | ModuleFilters
    | Content

let sectionOrder =
    [ Section.Header
      Section.DropZone
      Section.Metrics
      Section.Insights
      Section.Controls
      Section.Filters
      Section.ModuleFilters
      Section.Content ]

/// The detail panel's regions, in the redesign's order.
///
/// `FileLocation` and `ImportSource` replace the mockup's fake preview and its
/// "Extracted Topics"; `GraphNodes` replaces its dashed canvas with the real
/// edges.
[<RequireQualifiedAccess>]
type DetailRegion =
    | Preview
    | MissingWarning
    | Metadata
    | AiSummary
    | ExtractedText
    | Pipeline
    | Related
    | GraphNodes
    | History
    | Actions

let detailOrder =
    [ DetailRegion.Preview
      DetailRegion.MissingWarning
      DetailRegion.Metadata
      DetailRegion.AiSummary
      DetailRegion.ExtractedText
      DetailRegion.Pipeline
      DetailRegion.Related
      DetailRegion.GraphNodes
      DetailRegion.History
      DetailRegion.Actions ]

/// Grid or List. The mockup offers both, and both are real.
[<RequireQualifiedAccess>]
type ViewMode =
    | Grid
    | List

[<RequireQualifiedAccess>]
module ViewMode =

    let label =
        function
        | ViewMode.Grid -> "Grid"
        | ViewMode.List -> "List"

// ---------------------------------------------------------------------------
// the import wording — the load-bearing sentence on this screen
// ---------------------------------------------------------------------------

/// **Both halves of the truth, in one sentence.**
///
/// It must say the file is imported *now*, and it must say the Inbox step is
/// about *proposed filing* — never that filing happens automatically.
[<Literal>]
let dropZoneMessage =
    "Drop files anywhere, or use Import — they are copied into the library immediately, with text extracted and indexed where the format allows. A capture also goes to the Inbox, where suggested filing waits for your review. Nothing is linked to a module, assignment or lecture until you accept it."

/// The shorter form, for the notice after an import completes.
let importedNotice (count: int) =
    sprintf
        "%d file%s imported. Review the suggested filing in the Inbox."
        count
        (if count = 1 then "" else "s")

// ---------------------------------------------------------------------------
// responsive layout
// ---------------------------------------------------------------------------

/// Below this the detail panel moves under the grid instead of beside it.
[<Literal>]
let detailBesideWidth = 1180.0

/// Document cards per row, inside the content column.
let cardColumns (contentWidth: float) =
    max 1 (int (contentWidth / Geometry.cardMinWidth))

/// The six analytics cards reflow rather than shrinking below readability.
let metricColumns (contentWidth: float) =
    if contentWidth < 640.0 then 2
    elif contentWidth < 980.0 then 3
    elif contentWidth < 1320.0 then 4
    else 6

/// Whether the detail panel sits beside the grid. Below the threshold it follows
/// the selected card instead, which keeps the reading order intact and avoids
/// the horizontal scrollbar this project does not allow.
let detailBeside (contentWidth: float) = contentWidth >= detailBesideWidth
