module UniversityOS.App.Features.NotesLogic

open System
open UniversityOS.Domain
open UniversityOS.App

/// Every decision the Notes screen makes, as pure functions over the real domain
/// types — testable without an Avalonia application or a database.
///
/// **This screen is a three-pane layout because the redesign is**, not because
/// the legacy screen was. The mockup's grid is
/// `{{leftPanelWidth}} minmax(340px,1fr) {{rightPanelWidth}}` with two
/// independently collapsible rails, and that is what is built here.
///
/// The domain is unusually generous to this screen. `Note` carries `Pinned` and
/// `Tags`; `Markdown` already parses callouts, tables, code blocks and task
/// items, and can rewrite a task checkbox in the source; `ILinkRepository`
/// stores **directed** edges, so backlinks are real rather than inferred. What
/// it cannot support:
///
/// | Redesign | Status |
/// |---|---|
/// | Formatting toolbar (8 icons) | **omitted** — notes are Markdown text, and a toolbar that does not format would be a fake rich editor |
/// | 8 of the 11 AI actions | **omitted** — no service behind them |
/// | Key-concept extraction | **absent** — nothing extracts concepts; `Note.Tags` fills the slot |
/// | Version history | **absent** — only `CreatedAt` and `UpdatedAt` are stored |
/// | Graph preview canvas | **omitted** — a dashed placeholder in the mockup; the real edges are listed instead |
/// | Embedded-file block | **omitted** — a dashed placeholder in the mockup |
/// | Favourite / archived | **absent** — only `Pinned` exists |

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

    /// The two rails, open and collapsed.
    [<Literal>]
    let explorerWidth = 300.0

    [<Literal>]
    let contextWidth = 320.0

    /// A collapsed rail keeps its toggle, so the page never loses the control
    /// that would bring the panel back.
    [<Literal>]
    let railWidth = 34.0

    /// The mockup's `minmax(340px, 1fr)` on the centre column.
    [<Literal>]
    let centreMinWidth = 340.0

    /// The workspace is reading-width; the PAGE is not. Constraining the whole
    /// page to prose width is the mistake this constant exists to avoid.
    [<Literal>]
    let editorMaxWidth = 760.0

    /// A note row in the Explorer.
    [<Literal>]
    let noteRowRadius = 11.0

    [<Literal>]
    let noteRowBorderThickness = 1.0

    /// Glass action geometry, pinned equal to every other migrated screen.
    [<Literal>]
    let actionMinHeight = 38.0

    [<Literal>]
    let actionRadius = 11.0

    [<Literal>]
    let iconActionSize = 30.0

    [<Literal>]
    let iconActionRadius = 9.0

    /// A filter chip. Height and border reserved in BOTH states.
    [<Literal>]
    let filterMinHeight = 26.0

    [<Literal>]
    let filterRadius = 999.0

    [<Literal>]
    let filterBorderThickness = 1.0

    /// **One alpha, used BOTH to paint a chip's wash and to measure the contrast
    /// of the text on it.** Two constants would silently drift, and the text
    /// would then be checked against a surface it is not actually sitting on.
    [<Literal>]
    let chipFillAlpha = "1F"

    /// The selected note row is filled more strongly than a chip.
    [<Literal>]
    let selectedRowAlpha = "2E"

    /// Glyph column, gap, wrapping text column.
    [<Literal>]
    let bulletColumns = "Auto,9,*"

    /// Label column, gap, value column — the Related and History rows.
    [<Literal>]
    let detailColumns = "Auto,10,*"

// ---------------------------------------------------------------------------
// documented thresholds
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module Threshold =

    /// Words per minute used for the reading estimate. 200 is the usual figure
    /// for adult reading of prose on screen. It is a **deterministic estimate**
    /// and is labelled as a reading time, never as a measurement.
    [<Literal>]
    let wordsPerMinute = 200

    /// How much of the body the Explorer row shows.
    [<Literal>]
    let excerptChars = 150

    /// "Recently edited" horizon.
    [<Literal>]
    let recentDays = 7

    /// The heading `Ai.Summarize` writes into a note's body. The summary is
    /// persisted **inside the note**, because `Note` has no summary field — so
    /// this heading is the only honest way to know one exists.
    [<Literal>]
    let summaryHeading = "## AI summary"

// ---------------------------------------------------------------------------
// facts
// ---------------------------------------------------------------------------

/// Everything the screen knows about one note, gathered in a single load.
///
/// `LinksOut` and `LinksIn` are kept apart on purpose: `Link` stores `From` and
/// `To`, so the direction is real. The redesign shows three backlink categories
/// — "Linked From", "Links To" and "Referenced By" — but the first and last are
/// the same relation, so showing both would count the same edge twice.
type Facts =
    { Note: Note
      Module: StudyModule option
      /// Edges where this note is the `From` end.
      LinksOut: EntityRef list
      /// Edges where this note is the `To` end.
      LinksIn: EntityRef list
      /// Cards whose `Source` is this note.
      Flashcards: int
      /// Activity entries pointing at this note.
      Activity: ActivityEntry list }

[<RequireQualifiedAccess>]
module Facts =

    /// A bare snapshot, for a note with nothing attached to it yet.
    let ofNote (n: Note) =
        { Note = n
          Module = None
          LinksOut = []
          LinksIn = []
          Flashcards = 0
          Activity = [] }

let linkCount (f: Facts) = f.LinksOut.Length + f.LinksIn.Length

let isPinned (f: Facts) = f.Note.Pinned
let isFiled (f: Facts) = f.Note.ModuleId.IsSome
let isTagged (f: Facts) = not f.Note.Tags.IsEmpty

/// Words in the body. Whitespace-separated, which is what a reading estimate
/// wants — not characters, and not Markdown tokens.
let wordCount (content: string) =
    if String.IsNullOrWhiteSpace content then
        0
    else
        content.Split([| ' '; '\t'; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries).Length

/// A deterministic reading estimate, never a measurement. Always at least one
/// minute for a note with any words in it, because "0 min read" reads as an
/// error rather than as a very short note.
let readingMinutes (content: string) =
    let words = wordCount content
    if words = 0 then 0 else max 1 (int (ceil (float words / float Threshold.wordsPerMinute)))

let readingLabel (content: string) =
    match readingMinutes content with
    | 0 -> "empty"
    | m -> sprintf "%d min read" m

/// The first real line of prose, for the Explorer row.
///
/// Headings, list markers, quotes and fences are skipped rather than shown: a
/// row whose excerpt reads "## Overview" tells the user nothing.
let excerpt (content: string) =
    let isStructural (line: string) =
        let t = line.TrimStart()

        t.StartsWith "#"
        || t.StartsWith "-"
        || t.StartsWith ">"
        || t.StartsWith "|"
        || t.StartsWith "```"
        || t.StartsWith "*"

    match
        content.Split('\n')
        |> Array.map (fun l -> l.Trim())
        |> Array.filter (fun l -> l.Length > 0 && not (isStructural l))
        |> Array.tryHead
    with
    | Some line -> Format.truncate Threshold.excerptChars line
    | None -> ""

let hasSummary (f: Facts) =
    f.Note.Content.Contains Threshold.summaryHeading

let hasTasks (f: Facts) =
    f.Note.Content.Contains "- [ ]" || f.Note.Content.Contains "- [x]" || f.Note.Content.Contains "- [X]"

let isOrphan (f: Facts) = linkCount f = 0

let isRecent (now: DateTime) (f: Facts) =
    (now - f.Note.UpdatedAt).TotalDays <= float Threshold.recentDays

// ---------------------------------------------------------------------------
// filters
// ---------------------------------------------------------------------------

/// The redesign's eight chips, as toggles combined with AND.
///
/// `HasLinks` and `Orphans` can never both match; `emptyMessage` names that
/// contradiction rather than leaving a blank panel unexplained.
[<RequireQualifiedAccess>]
type Filter =
    | Pinned
    | RecentlyEdited
    | Unfiled
    | Untagged
    | HasLinks
    | Orphans
    | HasTasks
    | Summarised

[<RequireQualifiedAccess>]
module Filter =

    let all =
        [ Filter.Pinned
          Filter.RecentlyEdited
          Filter.Unfiled
          Filter.Untagged
          Filter.HasLinks
          Filter.Orphans
          Filter.HasTasks
          Filter.Summarised ]

    let label =
        function
        | Filter.Pinned -> "Pinned"
        | Filter.RecentlyEdited -> "Recent"
        | Filter.Unfiled -> "Unfiled"
        | Filter.Untagged -> "Untagged"
        | Filter.HasLinks -> "Linked"
        | Filter.Orphans -> "Orphans"
        | Filter.HasTasks -> "Has tasks"
        | Filter.Summarised -> "Summarised"

type Selection = Set<Filter>

[<RequireQualifiedAccess>]
module Selection =

    let empty: Selection = Set.empty

    let toggle (filter: Filter) (selection: Selection) : Selection =
        if selection.Contains filter then selection.Remove filter else selection.Add filter

let matches (now: DateTime) (filter: Filter) (f: Facts) =
    match filter with
    | Filter.Pinned -> isPinned f
    | Filter.RecentlyEdited -> isRecent now f
    | Filter.Unfiled -> not (isFiled f)
    | Filter.Untagged -> not (isTagged f)
    | Filter.HasLinks -> linkCount f > 0
    | Filter.Orphans -> isOrphan f
    | Filter.HasTasks -> hasTasks f
    | Filter.Summarised -> hasSummary f

/// The Explorer's search box.
///
/// **This filters the loaded list; it is not the global index.**
/// `ISearchRepository` backs the Search screen. Here the user is narrowing a
/// visible list, so an in-memory match over title, tags and body is both what
/// they expect and instant.
let searchMatches (query: string) (f: Facts) =
    let q = query.Trim()

    if q.Length = 0 then
        true
    else
        let contains (s: string) =
            not (isNull s) && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0

        contains f.Note.Title || contains f.Note.Content || f.Note.Tags |> List.exists contains

/// Modules that genuinely have notes, with their real counts. A chip that could
/// never match anything would be a dead end.
let moduleChips (facts: Facts list) : (StudyModule * int) list =
    facts
    |> List.choose (fun f -> f.Module)
    |> List.groupBy (fun m -> m.Id)
    |> List.map (fun (_, ms) -> List.head ms, ms.Length)
    |> List.sortBy (fun (m, _) -> m.Code)

/// Every chip applied, then the module chips, then the search box.
///
/// Ordering is explicit rather than inherited from the repository, so a refresh
/// cannot move a row out from under the pointer.
let visible (now: DateTime) (selection: Selection) (modules: Set<Guid>) (query: string) (facts: Facts list) =
    facts
    |> List.filter (fun f -> selection |> Set.forall (fun chip -> matches now chip f))
    |> List.filter (fun f ->
        modules.IsEmpty
        || (match f.Note.ModuleId with
            | Some id -> modules.Contains id.Value
            | None -> false))
    |> List.filter (searchMatches query)
    |> List.sortByDescending (fun f -> f.Note.UpdatedAt)

/// The redesign's two Explorer sections. Pinned first, then everything else.
let pinned (rows: Facts list) = rows |> List.filter isPinned
let unpinned (rows: Facts list) = rows |> List.filter (isPinned >> not)

/// Why the Explorer is empty, given what is switched on.
let emptyMessage (selection: Selection) (modules: Set<Guid>) (query: string) (total: int) =
    if total = 0 then
        "No notes yet. Create one — everything else on this screen hangs off a note."
    elif selection.Contains Filter.HasLinks && selection.Contains Filter.Orphans then
        "A note cannot both have links and be an orphan — turn one of those two chips off."
    elif query.Trim().Length > 0 then
        sprintf "Nothing matches \"%s\"." (Format.truncate 30 (query.Trim()))
    elif not modules.IsEmpty && selection.IsEmpty then
        "No notes are filed under the modules you have picked."
    else
        "No note matches every chip you have switched on."

// ---------------------------------------------------------------------------
// the top bar's inline metrics
// ---------------------------------------------------------------------------

/// A summary figure. `Value` is an option because some figures are genuinely
/// unknown rather than zero.
type Metric = { Label: string; Value: string option }

/// The redesign's six inline knowledge metrics. Not cards — the mockup runs them
/// as value/label pairs along the top bar, beside the title.
let metrics (facts: Facts list) : Metric list =
    let tags = facts |> List.collect (fun f -> f.Note.Tags) |> List.distinct

    [ { Label = "notes"; Value = Some(string facts.Length) }
      { Label = "pinned"; Value = Some(string (facts |> List.filter isPinned |> List.length)) }
      { Label = "tags"; Value = Some(string tags.Length) }
      { Label = "links"; Value = Some(string (facts |> List.sumBy linkCount)) }
      { Label = "words"; Value = Some(string (facts |> List.sumBy (fun f -> wordCount f.Note.Content))) }
      { Label = "unfiled"; Value = Some(string (facts |> List.filter (isFiled >> not) |> List.length)) } ]

// ---------------------------------------------------------------------------
// rule-based suggestions
// ---------------------------------------------------------------------------

/// The redesign calls this "Smart Suggestions" and shows it inside an AI panel.
///
/// **No model produces these.** Every one is a rule over a stored field, so the
/// section is labelled `RULE-BASED SUGGESTIONS` and never claims otherwise. An
/// empty list means there is genuinely nothing to point at, and the section is
/// omitted rather than padded.
let suggestions (f: Facts) : (string * string) list =
    [ if not (isFiled f) then
          "◫", "Not filed under a module — filing it brings it into planning and module context."

      if not (isTagged f) then
          "⌗", "No tags yet. Tags are what the concept chips and the tag filters read."

      if isOrphan f then
          "⛓", "Nothing links here and it links nowhere. A [[wiki link]] to another note creates a real edge."

      if wordCount f.Note.Content = 0 then
          "✎", "The body is empty."
      elif not (hasSummary f) && wordCount f.Note.Content > 200 then
          "✦", "Long enough to be worth summarising."

      if hasTasks f then
          "☑", "This note has task checkboxes — they can be ticked in reading mode." ]

// ---------------------------------------------------------------------------
// related knowledge
// ---------------------------------------------------------------------------

/// The categories the right rail can show for a note.
///
/// **A category with nothing in it is omitted entirely**, rather than drawn as a
/// decorative zero copied from the mockup.
[<RequireQualifiedAccess>]
type RelatedKind =
    | Notes
    | Lectures
    | Assignments
    | Documents
    | Modules
    | Flashcards

[<RequireQualifiedAccess>]
module RelatedKind =

    let label =
        function
        | RelatedKind.Notes -> "Notes"
        | RelatedKind.Lectures -> "Lectures"
        | RelatedKind.Assignments -> "Assignments"
        | RelatedKind.Documents -> "Documents"
        | RelatedKind.Modules -> "Modules"
        | RelatedKind.Flashcards -> "Flashcards"

    let ofEntity (kind: EntityKind) =
        match kind with
        | EntityKind.Note -> Some RelatedKind.Notes
        | EntityKind.Lecture -> Some RelatedKind.Lectures
        | EntityKind.Assignment -> Some RelatedKind.Assignments
        | EntityKind.Document -> Some RelatedKind.Documents
        | EntityKind.Module -> Some RelatedKind.Modules
        | EntityKind.RevisionItem -> Some RelatedKind.Flashcards
        | _ -> None

    let page =
        function
        | RelatedKind.Notes -> Page.Notes
        | RelatedKind.Lectures -> Page.Lectures
        | RelatedKind.Assignments -> Page.Assignments
        | RelatedKind.Documents -> Page.Documents
        | RelatedKind.Modules -> Page.Modules
        | RelatedKind.Flashcards -> Page.Modules

/// Every related category that genuinely has something in it, with its count.
let related (f: Facts) : (RelatedKind * int) list =
    let fromEdges =
        f.LinksOut @ f.LinksIn
        |> List.choose (fun e -> RelatedKind.ofEntity e.Kind)
        |> List.countBy id

    let withCards =
        if f.Flashcards > 0 then
            fromEdges
            |> List.filter (fun (k, _) -> k <> RelatedKind.Flashcards)
            |> fun rest -> rest @ [ RelatedKind.Flashcards, f.Flashcards ]
        else
            fromEdges

    withCards |> List.filter (fun (_, n) -> n > 0) |> List.sortBy (fun (k, _) -> RelatedKind.label k)

// ---------------------------------------------------------------------------
// page composition
// ---------------------------------------------------------------------------

/// The page, as data — the redesign's own shape.
[<RequireQualifiedAccess>]
type Section =
    | TopBar
    | Explorer
    | Workspace
    | Context

let sectionOrder = [ Section.TopBar; Section.Explorer; Section.Workspace; Section.Context ]

/// The Explorer's regions, in the redesign's order.
[<RequireQualifiedAccess>]
type ExplorerRegion =
    | Search
    | Filters
    | ModuleChips
    | Pinned
    | All

let explorerOrder =
    [ ExplorerRegion.Search
      ExplorerRegion.Filters
      ExplorerRegion.ModuleChips
      ExplorerRegion.Pinned
      ExplorerRegion.All ]

/// The right rail's regions, in the redesign's order.
///
/// `GraphPreview` is the mockup's dashed canvas reduced to the part that is
/// real: the linked entities themselves, as buttons. `Timestamps` replaces its
/// "Version History", which nothing stores.
[<RequireQualifiedAccess>]
type ContextRegion =
    | AiSummary
    /// The mockup's "Key Concepts". Named for the mockup rather than for
    /// the field, and F# reserves the case name `Tags` on a union anyway.
    | Concepts
    | Related
    | History
    | GraphNodes
    | Backlinks
    | Timestamps
    | Suggestions

let contextOrder =
    [ ContextRegion.AiSummary
      ContextRegion.Concepts
      ContextRegion.Related
      ContextRegion.History
      ContextRegion.GraphNodes
      ContextRegion.Backlinks
      ContextRegion.Timestamps
      ContextRegion.Suggestions ]

// ---------------------------------------------------------------------------
// the three real AI actions
// ---------------------------------------------------------------------------

/// The redesign offers eleven. Three have a service behind them.
///
/// `Summarise` and `GenerateFlashcards` call the provider; `AskAssistant` opens
/// the Assistant, which is a real screen. The other eight are omitted rather
/// than drawn as dead buttons.
[<RequireQualifiedAccess>]
type AiAction =
    | Summarise
    | GenerateFlashcards
    | AskAssistant

[<RequireQualifiedAccess>]
module AiAction =

    let all = [ AiAction.Summarise; AiAction.GenerateFlashcards; AiAction.AskAssistant ]

    let label =
        function
        | AiAction.Summarise -> "✦ Summarise"
        | AiAction.GenerateFlashcards -> "◈ Flashcards"
        | AiAction.AskAssistant -> "✧ Ask the Assistant"

    let busyLabel =
        function
        | AiAction.Summarise -> "✦ Summarising…"
        | AiAction.GenerateFlashcards -> "◈ Generating…"
        | AiAction.AskAssistant -> "✧ Ask the Assistant"

    /// Only the two provider calls need something to read.
    let needsBody =
        function
        | AiAction.Summarise
        | AiAction.GenerateFlashcards -> true
        | AiAction.AskAssistant -> false

/// Why the provider-backed actions are off. `None` when they are on.
let aiDisabledReason (content: string) =
    if wordCount content = 0 then
        Some "Nothing to read yet — write something first."
    elif wordCount content < 20 then
        Some(sprintf "Only %d words so far — too little to summarise honestly." (wordCount content))
    else
        None

// ---------------------------------------------------------------------------
// responsive layout
// ---------------------------------------------------------------------------

/// Below this the right rail is hidden whatever its toggle says.
[<Literal>]
let mediumWidth = 1280.0

/// Below this both rails are hidden and the workspace fills the page.
[<Literal>]
let narrowWidth = 900.0

type Panes = { Explorer: bool; Context: bool }

/// Which rails are on screen.
///
/// The user's toggles are honoured, but width **caps** them: a 320px rail beside
/// a 340px minimum centre column does not fit on a narrow window, and forcing it
/// would produce the horizontal scrollbar this project does not allow. Focus mode
/// hides both at any width, which is what the mockup's focus toggle does.
let panesFor (width: float) (focus: bool) (explorerOpen: bool) (contextOpen: bool) =
    if focus || width < narrowWidth then { Explorer = false; Context = false }
    elif width < mediumWidth then { Explorer = explorerOpen; Context = false }
    else { Explorer = explorerOpen; Context = contextOpen }

/// The width actually given to each rail, including the collapsed rail that
/// keeps the toggle reachable.
let explorerColumnWidth (panes: Panes) =
    if panes.Explorer then Geometry.explorerWidth else Geometry.railWidth

let contextColumnWidth (panes: Panes) =
    if panes.Context then Geometry.contextWidth else Geometry.railWidth
