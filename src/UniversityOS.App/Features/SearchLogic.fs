module UniversityOS.App.Features.SearchLogic

open System
open UniversityOS.Domain
open UniversityOS.App

/// Every decision the Search screen makes, as pure functions.
///
/// **One verified fact governs this screen.** Search is a single SQLite **FTS5**
/// query over one `search_index` table, ranked by **bm25**. It is not semantic,
/// it is not AI-ranked, and it returns no matched spans — `Fts.query` calls both
/// `highlight()` and `snippet()` with empty markers. Everything this module does
/// is arithmetic over what that one query genuinely returns.

// ---------------------------------------------------------------------------
// the query
// ---------------------------------------------------------------------------

/// The legacy screen's rule, preserved: fewer than two characters runs nothing.
[<Literal>]
let minQueryLength = 2

/// The hard cap `ISearchRepository.Query` is given. There is no paging and no
/// total count, so this is a real ceiling and the screen says so when it is hit.
[<Literal>]
let resultLimit = 60

/// Typing pause before a query runs. Real: FTS5 is fast, but a query per
/// keystroke also means a repository round-trip per keystroke.
[<Literal>]
let debounceMs = 180

let normalise (query: string) = query.Trim()

let isRunnable (query: string) = (normalise query).Length >= minQueryLength

/// The terms the engine will actually see. `Fts.toMatchQuery` splits on
/// whitespace, so this splits the same way — anything else would highlight
/// differently from how the match was made.
let terms (query: string) =
    (normalise query).Split([| ' '; '\t'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun t -> t.Replace("\"", ""))
    |> Array.filter (fun t -> t.Length > 0)
    |> List.ofArray

[<RequireQualifiedAccess>]
type QueryState =
    | Idle
    | TooShort
    | Running
    | Results
    | NoResults
    | Failed of string

let queryState (query: string) (searching: bool) (error: string option) (hasResults: bool) =
    match error with
    | Some message -> QueryState.Failed message
    | None ->
        let text = normalise query

        if text.Length = 0 then QueryState.Idle
        elif text.Length < minQueryLength then QueryState.TooShort
        elif searching then QueryState.Running
        elif hasResults then QueryState.Results
        else QueryState.NoResults

/// Said in full, because "type at least two characters" without saying what is
/// searched leaves the user guessing at the scope.
[<Literal>]
let watermark = "Search assignments, modules, notes, documents, lectures, calendar and flashcards…"

[<Literal>]
let tooShortNotice = "Type at least two characters."

/// The real shortcut. `Ctrl+K` is the command palette; `Ctrl+F` is what brings
/// you here. The redesign's ⌘K badge would name the wrong one.
[<Literal>]
let shortcutBadge = "Ctrl+F"

// ---------------------------------------------------------------------------
// what is genuinely indexed
// ---------------------------------------------------------------------------

/// **The seven kinds `Fts.upsert` is ever called with.**
///
/// `InboxItem`, `Session` and `Conversation` are deliberately absent: nothing
/// writes them to `search_index`, so a filter for them could only ever return
/// nothing. The redesign draws thirteen chips; there are seven real kinds.
let searchableKinds =
    [ EntityKind.Assignment
      EntityKind.Module
      EntityKind.Note
      EntityKind.Document
      EntityKind.Lecture
      EntityKind.Event
      EntityKind.RevisionItem ]

/// Group order, pinned. Ordering groups by their best score instead would make
/// them jump around as the query is typed.
let groupOrder = searchableKinds

/// What each kind's index actually contains — shown so the scope is visible
/// rather than assumed.
let indexedBody (kind: EntityKind) =
    match kind with
    | EntityKind.Assignment -> "title and description"
    | EntityKind.Module -> "code, name, lecturer and description"
    | EntityKind.Note -> "title, body and tags"
    | EntityKind.Document -> "title, file name, extracted text and summary"
    | EntityKind.Lecture -> "title, transcript and summary"
    | EntityKind.Event -> "title, location and notes"
    | EntityKind.RevisionItem -> "question and answer"
    | _ -> "not indexed"

let groupLabel (kind: EntityKind) =
    match kind with
    | EntityKind.Assignment -> "Assignments"
    | EntityKind.Module -> "Modules"
    | EntityKind.Note -> "Notes"
    | EntityKind.Document -> "Documents"
    | EntityKind.Lecture -> "Lectures"
    | EntityKind.Event -> "Calendar"
    | EntityKind.RevisionItem -> "Flashcards"
    | EntityKind.InboxItem -> "Inbox"
    | EntityKind.Session -> "Planner"
    | EntityKind.Conversation -> "Conversations"

// ---------------------------------------------------------------------------
// resolved facts
// ---------------------------------------------------------------------------

/// **`SearchHit` carries five fields: kind, id, title, snippet, score.** No
/// module, no date, no file type. Anything richer has to be resolved against the
/// repositories, which happens **once per snapshot load** — never while
/// rendering, and never per keystroke.
///
/// Every field is an option, and an unresolved field renders as nothing at all.
/// A dash or a zero would both be inventions.
type ResultFacts =
    { ModuleLabel: string option
      ModuleHex: string option
      /// One dated fact, named for what it actually is per kind.
      Meta: string option
      /// A document's file type; nothing else has one.
      TypeLabel: string option }

[<RequireQualifiedAccess>]
module ResultFacts =
    let none =
        { ModuleLabel = None; ModuleHex = None; Meta = None; TypeLabel = None }

/// Facts by entity id. Built once, read purely.
type Reference = { Facts: Map<Guid, ResultFacts> }

[<RequireQualifiedAccess>]
module Reference =
    let empty = { Facts = Map.empty }

let private moduleFacts (modules: Map<Guid, StudyModule>) (moduleId: ModuleId option) =
    match moduleId |> Option.bind (fun m -> Map.tryFind m.Value modules) with
    | Some m -> Some m.Code, Some m.Color
    | None -> None, None

/// Pure: lists in, lookup out. The whole resolution step is testable with no
/// repository behind it.
let buildReference
    (modules: StudyModule list)
    (assignments: Assignment list)
    (notes: Note list)
    (documents: Document list)
    (lectures: Lecture list)
    (events: CalendarEvent list)
    (revision: RevisionItem list)
    : Reference =

    let moduleMap = modules |> List.map (fun m -> m.Id.Value, m) |> Map.ofList

    let entries =
        [ for m in modules do
              m.Id.Value,
              { ModuleLabel = Some m.Code
                ModuleHex = Some m.Color
                Meta = m.Term
                TypeLabel = None }

          for a in assignments do
              let label, hex = moduleFacts moduleMap a.ModuleId

              a.Id.Value,
              { ModuleLabel = label
                ModuleHex = hex
                Meta = a.Deadline |> Option.map (fun d -> "Due " + Format.shortDate d)
                TypeLabel = None }

          for n in notes do
              let label, hex = moduleFacts moduleMap n.ModuleId

              n.Id.Value,
              { ModuleLabel = label
                ModuleHex = hex
                Meta = Some("Updated " + Format.shortDate n.UpdatedAt)
                TypeLabel = None }

          for d in documents do
              let label, hex = moduleFacts moduleMap d.ModuleId

              d.Id.Value,
              { ModuleLabel = label
                ModuleHex = hex
                Meta = Some("Imported " + Format.shortDate d.ImportedAt)
                TypeLabel = Some(DocumentKind.label d.Kind) }

          for l in lectures do
              let label, hex = moduleFacts moduleMap l.ModuleId

              l.Id.Value,
              { ModuleLabel = label
                ModuleHex = hex
                Meta = l.Date |> Option.map Format.shortDate
                TypeLabel = None }

          for e in events do
              let label, hex = moduleFacts moduleMap e.ModuleId

              e.Id.Value,
              { ModuleLabel = label
                ModuleHex = hex
                Meta = Some(Format.shortDate e.StartsAt)
                TypeLabel = None }

          for r in revision do
              let label, hex = moduleFacts moduleMap r.ModuleId

              r.Id.Value,
              { ModuleLabel = label
                ModuleHex = hex
                Meta = None
                TypeLabel = None } ]

    { Facts = Map.ofList entries }

let factsFor (reference: Reference) (hit: SearchHit) =
    reference.Facts |> Map.tryFind hit.Id |> Option.defaultValue ResultFacts.none

// ---------------------------------------------------------------------------
// filtering, grouping, ranking
// ---------------------------------------------------------------------------

/// `None` is All. Only ever holds kinds from `searchableKinds`.
type Scope = Set<EntityKind>

[<RequireQualifiedAccess>]
module Scope =
    let all: Scope = Set.empty
    let isAll (scope: Scope) = Set.isEmpty scope

    let toggle (kind: EntityKind) (scope: Scope) : Scope =
        if Set.contains kind scope then Set.remove kind scope else Set.add kind scope

let inScope (scope: Scope) (hit: SearchHit) =
    Scope.isAll scope || Set.contains hit.Kind scope

let applyScope (scope: Scope) (hits: SearchHit list) = hits |> List.filter (inScope scope)

/// Counts across the **unfiltered** result set, so a chip can show how many
/// results it would reveal. Kinds with nothing stay visible with a zero, which
/// is a fact about the search, not a decoration.
let countsByKind (hits: SearchHit list) =
    searchableKinds
    |> List.map (fun kind -> kind, hits |> List.filter (fun h -> h.Kind = kind) |> List.length)

/// Grouped in the pinned order, empty groups omitted, each group's own hits kept
/// in the order the engine returned them — which is bm25 order.
let groupResults (hits: SearchHit list) =
    groupOrder
    |> List.map (fun kind -> kind, hits |> List.filter (fun h -> h.Kind = kind))
    |> List.filter (fun (_, hits) -> not hits.IsEmpty)

/// The flattened, on-screen order — what the arrow keys walk.
let flatten (grouped: (EntityKind * SearchHit list) list) = grouped |> List.collect snd

/// **The only rank claim the ordering supports.** bm25 is an unbounded relevance
/// number whose scale depends on corpus statistics; it is not a percentage and
/// it is not a confidence. What is true is that the engine returned one hit
/// first, so exactly that one is labelled.
let bestMatchIndex (hits: SearchHit list) = if hits.IsEmpty then None else Some 0

[<Literal>]
let bestMatchLabel = "Best match"

/// Stated because the cap is otherwise invisible: a 61st result simply is not
/// there, and nothing on screen would say so.
let capNotice (hits: SearchHit list) =
    if hits.Length >= resultLimit then
        Some(sprintf "Showing the first %d matches. Narrow the search to see fewer, better ones." resultLimit)
    else
        None

let summaryText (query: string) (shown: int) (groups: int) =
    if shown = 0 then
        sprintf "No matches for \"%s\"." (normalise query)
    else
        sprintf
            "%d %s across %d %s"
            shown
            (if shown = 1 then "match" else "matches")
            groups
            (if groups = 1 then "kind" else "kinds")

// ---------------------------------------------------------------------------
// match highlighting
// ---------------------------------------------------------------------------

/// One run of text, and whether the engine would have matched it.
type Segment = { Text: string; Matched: bool }

/// **Local, deterministic highlighting**, because the service returns no spans.
///
/// It mirrors what FTS5 actually did: terms match at a **token start**, and the
/// **last term is a prefix** (that is the `*` `Fts.toMatchQuery` appends).
/// Matching anywhere inside a word would highlight text the engine never
/// matched on.
///
/// Operates on plain text only — titles and FTS5 snippets. **No stored Markdown
/// is touched**, because no stored body is rendered on this screen.
let segments (query: string) (text: string) : Segment list =
    let allTerms = terms query

    if allTerms.IsEmpty || String.IsNullOrEmpty text then
        [ { Text = text; Matched = false } ]
    else
        let lower = text.ToLowerInvariant()
        let lastIndex = allTerms.Length - 1

        // A token starts at 0 or after a non-letter-or-digit.
        let isTokenStart i =
            i = 0 || not (Char.IsLetterOrDigit text.[i - 1])

        let matched = Array.create text.Length false

        allTerms
        |> List.iteri (fun termIndex term ->
            let t = term.ToLowerInvariant()
            let isPrefixTerm = termIndex = lastIndex
            let mutable from = 0

            while from >= 0 && from <= lower.Length - t.Length do
                let at = lower.IndexOf(t, from, StringComparison.Ordinal)

                if at < 0 then
                    from <- -1
                else
                    if isTokenStart at then
                        // A prefix term highlights to the end of the word it
                        // completes; an exact term highlights only itself.
                        let stop =
                            if isPrefixTerm then
                                let mutable e = at + t.Length

                                while e < text.Length && Char.IsLetterOrDigit text.[e] do
                                    e <- e + 1

                                e
                            else
                                at + t.Length

                        for i in at .. stop - 1 do
                            matched.[i] <- true

                    from <- at + 1)

        // Collapse the flag array into runs.
        let result = ResizeArray<Segment>()
        let mutable start = 0

        for i in 1 .. text.Length do
            if i = text.Length || matched.[i] <> matched.[start] then
                result.Add { Text = text.Substring(start, i - start); Matched = matched.[start] }
                start <- i

        if result.Count = 0 then [ { Text = text; Matched = false } ] else List.ofSeq result

// ---------------------------------------------------------------------------
// selection and keyboard movement
// ---------------------------------------------------------------------------

/// Selection is **one index into the flattened list**, so the pointer and the
/// arrow keys cannot disagree — they set the same field.
let moveSelection (count: int) (delta: int) (selected: int option) =
    if count = 0 then
        None
    else
        match selected with
        | None -> Some(if delta > 0 then 0 else count - 1)
        | Some current ->
            let next = current + delta
            Some(if next < 0 then 0 elif next >= count then count - 1 else next)

/// A selection that no longer points at anything is **cleared**, not clamped —
/// silently moving it to a different result would open the wrong thing.
let reconcileSelection (previous: SearchHit option) (hits: SearchHit list) =
    match previous with
    | None -> None
    | Some hit -> hits |> List.tryFindIndex (fun h -> h.Id = hit.Id && h.Kind = hit.Kind)

let selectedHit (hits: SearchHit list) (selected: int option) =
    selected |> Option.bind (fun i -> if i >= 0 && i < hits.Length then Some hits.[i] else None)

// ---------------------------------------------------------------------------
// recent searches — session only
// ---------------------------------------------------------------------------

/// **Nothing persists a query.** There is no table, no settings field and no
/// file. So recent searches live for the lifetime of this screen's model and the
/// heading says exactly that.
[<Literal>]
let recentHeading = "Recent searches — this session"

[<Literal>]
let recentEmptyNotice = "Searches you run in this session appear here. They are not saved to disk."

[<Literal>]
let maxRecent = 6

/// Only a search that found something is worth offering again.
let remember (query: string) (foundAnything: bool) (recent: string list) =
    let text = normalise query

    if not foundAnything || text.Length < minQueryLength then
        recent
    else
        text :: (recent |> List.filter (fun r -> not (String.Equals(r, text, StringComparison.OrdinalIgnoreCase))))
        |> List.truncate maxRecent

// ---------------------------------------------------------------------------
// suggestions — deterministic, from the user's own data
// ---------------------------------------------------------------------------

/// **Not AI.** The redesign calls these "AI Suggestions"; nothing here contacts
/// a provider. They are module codes and recent titles, in a fixed order, so the
/// same data always produces the same suggestions.
[<Literal>]
let suggestionHeading = "Try searching for"

let suggestions (modules: StudyModule list) (notes: Note list) (assignments: Assignment list) =
    [ yield! modules |> List.filter (fun m -> not m.Archived) |> List.truncate 3 |> List.map (fun m -> m.Code)

      yield!
          assignments
          |> List.sortByDescending (fun a -> a.Deadline |> Option.defaultValue DateTime.MinValue)
          |> List.truncate 2
          |> List.map (fun a -> a.Title)

      yield!
          notes
          |> List.sortByDescending (fun n -> n.UpdatedAt)
          |> List.truncate 2
          |> List.map (fun n -> n.Title) ]
    |> List.filter (fun s -> s.Trim().Length >= minQueryLength)
    |> List.distinct
    |> List.truncate 6

// ---------------------------------------------------------------------------
// recent activity
// ---------------------------------------------------------------------------

/// The redesign's "Recently Opened". **Nothing records opening.** `IActivityLog`
/// records what *happened* — created, updated, an AI action — so that is what
/// this is called.
[<Literal>]
let activityHeading = "Recent activity"

/// Only entries that genuinely point at something navigable.
let navigableActivity (entries: ActivityEntry list) =
    entries |> List.filter (fun e -> e.Entity.IsSome) |> List.truncate 6

// ---------------------------------------------------------------------------
// navigation honesty
// ---------------------------------------------------------------------------

/// **`Page.forEntity` opens the owning screen, not the item.** No screen accepts
/// an entity id on entry, so this is stated rather than quietly implied by an
/// "Open" button that lands somewhere general.
[<Literal>]
let navigationNote = "Opening a result goes to the screen that owns it. No screen can be opened focused on one item yet."

/// The Assistant handoff **prefills the composer**. It attaches nothing.
///
/// Documents, lectures, flashcards and graph links are excluded from
/// `AiService.BuildContext`, so the prompt names the thing **in words** — which
/// is the only way the Assistant could know about it at all.
let assistantPrompt (kind: EntityKind) (title: string) =
    let noun =
        match kind with
        | EntityKind.Assignment -> "assignment"
        | EntityKind.Module -> "module"
        | EntityKind.Note -> "note"
        | EntityKind.Document -> "document"
        | EntityKind.Lecture -> "lecture"
        | EntityKind.Event -> "calendar event"
        | EntityKind.RevisionItem -> "flashcard"
        | EntityKind.InboxItem -> "inbox item"
        | EntityKind.Session -> "study session"
        | EntityKind.Conversation -> "conversation"

    sprintf "Tell me about my %s \"%s\"." noun title

[<Literal>]
let assistantNote = "Sends the question as text you can edit. Nothing is attached."

// ---------------------------------------------------------------------------
// composition and responsive layout
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type Section =
    | Input
    | Filters
    | Summary
    | Results
    | Preview

let sectionOrder =
    [ Section.Input; Section.Filters; Section.Summary; Section.Results; Section.Preview ]

/// The empty state's regions, in order.
[<RequireQualifiedAccess>]
type EmptyRegion =
    | Suggestions
    | Recent
    | Activity
    | Scope

let emptyOrder =
    [ EmptyRegion.Suggestions; EmptyRegion.Recent; EmptyRegion.Activity; EmptyRegion.Scope ]

[<RequireQualifiedAccess>]
module Geometry =

    [<Literal>]
    let contentHighlight = 0.0

    [<Literal>]
    let borderInset = 0.5

    /// The redesign's centred column.
    [<Literal>]
    let columnWidth = 900.0

    [<Literal>]
    let previewWidth = 320.0

    /// The input card. Height must not change between states.
    [<Literal>]
    let inputRadius = 20.0

    [<Literal>]
    let inputMinHeight = 62.0

    [<Literal>]
    let cardRadius = 16.0

    [<Literal>]
    let iconTile = 36.0

    [<Literal>]
    let iconTileRadius = 10.0

    [<Literal>]
    let actionMinHeight = 38.0

    [<Literal>]
    let actionRadius = 11.0

    [<Literal>]
    let chipFillAlpha = "1F"

    /// A selected result card.
    [<Literal>]
    let selectedFillAlpha = "24"

    [<Literal>]
    let selectedEdgeAlpha = "59"

    /// A matched run inside a title or snippet.
    [<Literal>]
    let highlightAlpha = "3D"

    [<Literal>]
    let veilAlpha = "2E"

    [<Literal>]
    let bulletColumns = "Auto,9,*"

[<Literal>]
let mediumWidth = 1240.0

[<Literal>]
let narrowWidth = 900.0

/// Where the preview sits. It is never removed — only re-placed.
[<RequireQualifiedAccess>]
type PreviewPlacement =
    /// Beside the results, as its own column.
    | Rail
    /// Below the results.
    | Below
    /// Inline, directly under the selected result.
    | Inline

let previewPlacement (width: float) =
    if width < narrowWidth then PreviewPlacement.Inline
    elif width < mediumWidth then PreviewPlacement.Below
    else PreviewPlacement.Rail
