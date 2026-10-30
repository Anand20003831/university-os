module UniversityOS.App.Features.InboxLogic

open System
open UniversityOS.Domain

/// Every decision the Inbox screen makes, as pure functions over the real domain
/// types — so the whole screen is testable without an Avalonia application, a
/// window or a database. The view maps over what is defined here; it decides
/// nothing of its own.
///
/// **The honesty rule that shapes this screen.** The redesign shows a confidence
/// percentage on every proposal, an "AI confidence" summary metric and an average
/// processing time. None of the three exists:
///
/// - `CaptureAnalysis` carries `Summary`, `SuggestedModuleCode`, `Actions` and
///   `Analyzer` — and no confidence of any kind. The AI prompt in
///   `AiService.ClassifyCapture` does not ask for one either, so there is nothing
///   to read even when Claude is configured.
/// - Nothing times or persists how long an analysis took.
///
/// A number invented for those slots would be indistinguishable from a real one,
/// so they are omitted and replaced with figures the model can actually produce.
/// The same rule applies one level down: a count of proposals is `None` — not
/// zero — while nothing has been analysed, because "nothing proposed" and "not
/// yet examined" are different states and only one of them means there is no work
/// to do.

// ---------------------------------------------------------------------------
// geometry
// ---------------------------------------------------------------------------

/// Every dimension the screen depends on, named once.
///
/// Each is a **reservation**: space held whether or not the thing occupying it is
/// present, so a card cannot change size when a badge appears, a warning resolves
/// or a selection moves. The Planner earned these the hard way
/// and the same defects apply here.
[<RequireQualifiedAccess>]
module Geometry =

    /// No specular pass on a content card. Zero, not "small": the gradient's
    /// REACH is 38% of the panel and does not shrink with its alpha, so anything
    /// above zero is a pale band across the top third, not an edge highlight.
    [<Literal>]
    let contentHighlight = 0.0

    /// The main workspace split — list against detail panel.
    [<Literal>]
    let mainLeftWeight = 66.0

    [<Literal>]
    let mainRightWeight = 34.0

    /// Applied to the workspace ROW, never to either column, so the two sides
    /// share a height by construction rather than by coincidence.
    [<Literal>]
    let workspaceMinHeight = 320.0

    /// The summary metric row. A floor on the row keeps all five cards equal
    /// even when one label wraps to two lines and the others do not.
    [<Literal>]
    let metricRowMinHeight = 72.0

    /// Glass action geometry, identical in every state.
    [<Literal>]
    let actionMinHeight = 38.0

    [<Literal>]
    let actionRadius = 11.0

    /// Half the hairline's 1px stroke. A stroke is centred on its path, so an
    /// outline laid on the panel bounds puts half its width outside them where
    /// the draw operation clips it — a half-weight line whose corners clip
    /// asymmetrically as the curve turns. Pinned equal to the Planner's and the
    /// Dashboard's by test.
    [<Literal>]
    let borderInset = 0.5

    /// A filter chip. Height and border are reserved in BOTH states so selecting
    /// one changes its colour and never its size — nothing in the row may move
    /// when the selection does.
    [<Literal>]
    let filterMinHeight = 30.0

    [<Literal>]
    let filterRadius = 999.0

    [<Literal>]
    let filterBorderThickness = 1.0

    /// A review card's outline, drawn in both states — accent when selected,
    /// ordinary border when not. Reserved, so selection never resizes a card.
    [<Literal>]
    let cardBorderThickness = 1.0

    [<Literal>]
    let cardRadius = 16.0

    /// The square source badge at the head of a review card.
    [<Literal>]
    let sourceBadgeSize = 30.0

    /// Glyph column, gap, wrapping text column.
    ///
    /// **This is the fix for text running outside its card.** A horizontal
    /// `StackPanel` measures its children with infinite available width, so a
    /// `TextBlock` inside one is told it has unlimited room and `TextWrapping`
    /// never engages. A Grid gives the text a real finite width instead.
    [<Literal>]
    let bulletColumns = "Auto,9,*"

    /// A metadata row: label, gap, value pushed right.
    [<Literal>]
    let metadataColumns = "Auto,12,*"

    /// The status word beside a review card's title. Reserved so a card cannot
    /// resize when "New" becomes "Analysed".
    [<Literal>]
    let statusMinWidth = 76.0

    /// The alpha a chip's fill is drawn at.
    ///
    /// Named because it is used **twice** and the two must never diverge: once
    /// to paint the fill, and once to composite that fill so the chip's ink can
    /// be walked against the colour it will actually sit on. A fill drawn at one
    /// alpha and measured at another is a contrast claim about a surface that is
    /// not on screen.
    [<Literal>]
    let chipFillAlpha = "1F"

    /// The same, for a conflict banner's fill.
    [<Literal>]
    let bannerFillAlpha = "1A"

    /// A conflict banner's edge, before the theme's own edge weighting.
    [<Literal>]
    let bannerEdgeAlpha = "4D"

// ---------------------------------------------------------------------------
// sources
// ---------------------------------------------------------------------------

/// Connector keys whose items are mail.
///
/// These are the real `IConnector.Metadata.Key` values registered in
/// `Infrastructure/Connectors` — `CaptureSource.Connector` is constructed from
/// exactly that key in `ConnectorRegistry.AddInboxItem`, so this is a lookup
/// against real data rather than a guess at the wording of a title.
let emailConnectorKeys = [ "gmail"; "outlook" ]

/// Connector keys whose items are files from a drive.
let fileConnectorKeys = [ "google-drive"; "onedrive" ]

/// Connector keys whose items are calendar entries.
let calendarConnectorKeys = [ "google-calendar"; "outlook-calendar" ]

/// How an item arrived. Derived from `CaptureSource` only — never from the words
/// in a title, which would be presentation masquerading as data.
[<RequireQualifiedAccess>]
type SourceKind =
    | Capture
    | File
    | Email
    | CalendarFeed
    | Connector

[<RequireQualifiedAccess>]
module SourceKind =

    let ofSource (source: CaptureSource) =
        match source with
        | CaptureSource.ManualText -> SourceKind.Capture
        | CaptureSource.File _ -> SourceKind.File
        | CaptureSource.Connector key ->
            let k = key.ToLowerInvariant()

            if List.contains k emailConnectorKeys then SourceKind.Email
            elif List.contains k calendarConnectorKeys then SourceKind.CalendarFeed
            elif List.contains k fileConnectorKeys then SourceKind.File
            else SourceKind.Connector

    /// A glyph is never the only signal — every use of one sits beside the
    /// source's own words.
    let glyph =
        function
        | SourceKind.Capture -> "✎"
        | SourceKind.File -> "▧"
        | SourceKind.Email -> "✉"
        | SourceKind.CalendarFeed -> "▦"
        | SourceKind.Connector -> "⇄"

let sourceKindOf (item: InboxItem) = SourceKind.ofSource item.Source

/// The source, in the user's words. Connector keys are shown as they are
/// registered — they are the name the Connections screen uses too.
let sourceLabel (item: InboxItem) =
    match item.Source with
    | CaptureSource.ManualText -> "Quick capture"
    | CaptureSource.File name -> name
    | CaptureSource.Connector key -> key

/// True when the item brought a file into the managed document store, so
/// "Open file" is a real possibility rather than a dead control.
let hasDocument (item: InboxItem) = item.DocumentId.IsSome

// ---------------------------------------------------------------------------
// status
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module Status =

    /// A word, always. Status is never communicated by colour alone.
    let label =
        function
        | InboxStatus.New -> "New"
        | InboxStatus.Analyzed -> "Analysed"
        | InboxStatus.Applied -> "Applied"
        | InboxStatus.Dismissed -> "Dismissed"

    /// Whether the item is still awaiting the user. Mirrors the repository's own
    /// `GetActive`, which selects statuses 0 and 1.
    let isActive =
        function
        | InboxStatus.New
        | InboxStatus.Analyzed -> true
        | _ -> false

let isActive (item: InboxItem) = Status.isActive item.Status

// ---------------------------------------------------------------------------
// what applying a proposal actually does
// ---------------------------------------------------------------------------

/// The nature of one consequence, so it can carry a semantic colour without the
/// colour being the only signal.
[<RequireQualifiedAccess>]
type EffectKind =
    | Creates
    | Updates
    | Links
    | Records

type Effect = { Label: string; Kind: EffectKind }

/// Everything `CaptureService.ApplyAction` really does for one proposal — read
/// off that method, branch by branch, not imagined from the redesign's chips.
///
/// The details matter and are not symmetrical: `CreateAssignment`, `CreateEvent`
/// and `CreateNote` each call `linkDocumentTo`, so they link the captured file
/// when there is one; `UpdateDeadline` and `LinkToModule` do not. Every branch
/// writes one activity entry, so every branch records a Timeline event.
let effectsFor (itemHasDocument: bool) (action: ProposedAction) : Effect list =
    let link =
        if itemHasDocument then
            [ { Label = "Link the captured file"; Kind = EffectKind.Links } ]
        else
            []

    let recorded = [ { Label = "Record a Timeline event"; Kind = EffectKind.Records } ]

    match action.Kind with
    | ProposedActionKind.CreateAssignment ->
        [ { Label = "Create an assignment"; Kind = EffectKind.Creates } ] @ link @ recorded
    | ProposedActionKind.CreateEvent ->
        [ { Label = "Add a calendar event"; Kind = EffectKind.Creates } ] @ link @ recorded
    | ProposedActionKind.CreateNote ->
        [ { Label = "Create a note"; Kind = EffectKind.Creates } ] @ link @ recorded
    | ProposedActionKind.UpdateDeadline ->
        [ { Label = "Move an existing deadline"; Kind = EffectKind.Updates } ] @ recorded
    | ProposedActionKind.LinkToModule ->
        [ { Label = "File the document under its module"; Kind = EffectKind.Updates } ] @ recorded
    | ProposedActionKind.NoAction -> []

/// The proposals that would actually change something. `NoAction` is the
/// pipeline's way of saying "nothing to do", so an item carrying only that is not
/// an item with work waiting on it.
let realActions (analysis: CaptureAnalysis) =
    analysis.Actions |> List.filter (fun a -> a.Kind <> ProposedActionKind.NoAction)

let realActionsOf (item: InboxItem) =
    item.Analysis |> Option.map realActions |> Option.defaultValue []

/// One proposal, in the application's own voice.
let actionLabel (action: ProposedAction) =
    let withDate text =
        match action.Date with
        | Some d -> sprintf "%s — %s" text (d.ToString "ddd d MMM HH:mm")
        | None -> text

    match action.Kind with
    | ProposedActionKind.CreateAssignment -> withDate (sprintf "Create assignment \"%s\"" action.Title)
    | ProposedActionKind.UpdateDeadline -> withDate (sprintf "Move deadline of \"%s\"" action.Title)
    | ProposedActionKind.CreateEvent -> withDate (sprintf "Add \"%s\" to the calendar" action.Title)
    | ProposedActionKind.CreateNote -> sprintf "Create note \"%s\"" action.Title
    | ProposedActionKind.LinkToModule ->
        sprintf "File under %s" (action.ModuleCode |> Option.defaultValue "its module")
    | ProposedActionKind.NoAction -> "No action needed"

/// What accepting an item would mean for the Planner.
///
/// Truthful because `PlannerService.BuildPlannables` schedules **every open
/// assignment**, and `BuildInput` turns every timed, non-study calendar event
/// into a busy block. Notes and filing change neither.
let plannerImpact (actions: ProposedAction list) : string =
    let countOf kind = actions |> List.filter (fun a -> a.Kind = kind) |> List.length
    let created = countOf ProposedActionKind.CreateAssignment
    let moved = countOf ProposedActionKind.UpdateDeadline
    let events = countOf ProposedActionKind.CreateEvent

    let parts =
        [ if created = 1 then "adds one assignment for the Planner to schedule"
          elif created > 1 then sprintf "adds %d assignments for the Planner to schedule" created

          if moved = 1 then "moves a deadline the Planner already schedules around"
          elif moved > 1 then sprintf "moves %d deadlines the Planner schedules around" moved

          if events = 1 then "reserves one calendar event as busy time"
          elif events > 1 then sprintf "reserves %d calendar events as busy time" events ]

    match parts with
    | [] -> "No Planner change — nothing here schedules work or reserves time."
    | _ ->
        let sentence = String.Join(", ", parts)
        sprintf "Accepting %s. The Planner picks this up on its next run." sentence

// ---------------------------------------------------------------------------
// conflicts — real repository checks, never presentation text
// ---------------------------------------------------------------------------

/// An assignment the user already has, reduced to what a conflict check needs.
type ExistingAssignment =
    { Id: Guid
      Title: string
      Deadline: DateTime option }

/// What already exists, so a proposal can be checked against it rather than
/// against the words in its own title.
type Existing =
    { Assignments: ExistingAssignment list
      ModuleCodes: string list }

[<RequireQualifiedAccess>]
module Existing =
    let empty = { Assignments = []; ModuleCodes = [] }

/// `CaptureService.normalizeCode`, mirrored so a module either resolves in both
/// places or in neither.
let normaliseCode (code: string) =
    code.Replace(" ", "").Replace("-", "").ToUpperInvariant()

/// Case- and whitespace-insensitive title comparison, so "Coursework  2 " and
/// "coursework 2" are recognised as the same piece of work.
let normaliseTitle (title: string) =
    if isNull (box title) then
        ""
    else
        String.Join(
            " ",
            title.ToLowerInvariant().Split([| ' '; '\t'; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
        )

/// Resolves the assignment an `UpdateDeadline` proposal targets, exactly as
/// `CaptureService.ApplyAction` resolves it: by `TargetId` first, then by the
/// first existing title containing the proposed one.
let resolveDeadlineTarget (existing: Existing) (action: ProposedAction) =
    let byId =
        action.TargetId
        |> Option.bind (fun tid ->
            match Guid.TryParse tid with
            | true, g -> existing.Assignments |> List.tryFind (fun a -> a.Id = g)
            | _ -> None)

    match byId with
    | Some _ -> byId
    | None ->
        let needle = action.Title.ToLowerInvariant()

        existing.Assignments
        |> List.tryFind (fun a -> a.Title.ToLowerInvariant().Contains needle)

/// Something the user should look at before accepting.
[<RequireQualifiedAccess>]
type Conflict =
    /// A new assignment is proposed under a title the user already has.
    /// Accepting creates a SECOND one — the service does not merge.
    | DuplicateAssignment of proposed: string * existing: string
    /// A deadline move whose target already holds a different date.
    | DeadlineDiffers of title: string * existing: DateTime * proposed: DateTime
    /// A module code was detected that matches none of the user's modules.
    | UnknownModule of code: string
    /// `ApplyAction` would return `Error` for this proposal. The reason is the
    /// service's own guard, not a guess.
    | WillNotApply of action: string * why: string

[<RequireQualifiedAccess>]
module Conflict =

    /// Words, always — the warning is readable with colour removed.
    let text =
        function
        | Conflict.DuplicateAssignment(proposed, existing) ->
            sprintf
                "An assignment called \"%s\" already exists — accepting \"%s\" creates a second one."
                existing
                proposed
        | Conflict.DeadlineDiffers(title, existing, proposed) ->
            sprintf
                "\"%s\" is currently due %s; this proposes %s."
                title
                (existing.ToString "ddd d MMM HH:mm")
                (proposed.ToString "ddd d MMM HH:mm")
        | Conflict.UnknownModule code ->
            sprintf "Module %s was detected but you have no module with that code — it will be filed unlinked." code
        | Conflict.WillNotApply(action, why) -> sprintf "%s cannot be applied: %s" action why

    /// Whether the proposal is guaranteed to fail. A duplicate or an unknown
    /// module still applies — it just applies to something the user may not want,
    /// which is a warning rather than a blocker.
    let isBlocking =
        function
        | Conflict.WillNotApply _ -> true
        | _ -> false

/// Every conflict on one item, checked against what really exists.
///
/// The `WillNotApply` cases mirror `CaptureService.ApplyAction`'s own guards one
/// for one, so this predicts the outcome rather than describing the wording of a
/// proposal.
let conflictsFor (existing: Existing) (item: InboxItem) : Conflict list =
    match item.Analysis with
    | None -> []
    | Some analysis ->
        let moduleKnown (code: string) =
            existing.ModuleCodes |> List.exists (fun c -> normaliseCode c = normaliseCode code)

        let perAction (action: ProposedAction) =
            [ match action.Kind with
              | ProposedActionKind.CreateAssignment ->
                  match
                      existing.Assignments
                      |> List.tryFind (fun a -> normaliseTitle a.Title = normaliseTitle action.Title)
                  with
                  | Some existingAssignment ->
                      Conflict.DuplicateAssignment(action.Title, existingAssignment.Title)
                  | None -> ()

              | ProposedActionKind.UpdateDeadline ->
                  match action.Date with
                  | None -> Conflict.WillNotApply(actionLabel action, "the proposal carries no date")
                  | Some proposed ->
                      match resolveDeadlineTarget existing action with
                      | None ->
                          Conflict.WillNotApply(
                              actionLabel action,
                              sprintf "no existing assignment matches \"%s\"" action.Title
                          )
                      | Some target ->
                          match target.Deadline with
                          | Some current when current <> proposed ->
                              Conflict.DeadlineDiffers(target.Title, current, proposed)
                          | _ -> ()

              | ProposedActionKind.CreateEvent ->
                  if action.Date.IsNone then
                      Conflict.WillNotApply(actionLabel action, "the proposal carries no date")

              | ProposedActionKind.LinkToModule ->
                  // `ApplyAction` needs BOTH a document and a resolvable module,
                  // and returns Error when either is missing. One match over the
                  // pair, so the prediction cannot drift from the guard.
                  match hasDocument item, action.ModuleCode with
                  | false, _ ->
                      Conflict.WillNotApply(actionLabel action, "this capture has no document to file")
                  | true, None -> Conflict.WillNotApply(actionLabel action, "no module was recognised")
                  | true, Some code when not (moduleKnown code) ->
                      Conflict.WillNotApply(actionLabel action, sprintf "no module matches %s" code)
                  | true, Some _ -> ()

              | ProposedActionKind.CreateNote
              | ProposedActionKind.NoAction -> ()

              // A detected code the user does not have is worth saying once per
              // action that carries it, whatever the action does with it.
              match action.Kind, action.ModuleCode with
              | ProposedActionKind.LinkToModule, _ -> ()
              | _, Some code when not (moduleKnown code) -> Conflict.UnknownModule code
              | _ -> () ]

        analysis.Actions |> List.collect perAction |> List.distinct

// ---------------------------------------------------------------------------
// how the item was analysed
// ---------------------------------------------------------------------------

/// Which engine produced an analysis. `CaptureAnalysis.Analyzer` is written as
/// "ai" by `AiService.ClassifyCapture` when a provider answered, and "heuristic"
/// by `CaptureHeuristics.analyze` otherwise — including when the provider was
/// configured but failed. Anything else is treated as unknown rather than
/// guessed at.
[<RequireQualifiedAccess>]
type Analyser =
    | Ai
    | Deterministic
    | Unknown of string

[<RequireQualifiedAccess>]
module Analyser =

    let ofString (s: string) =
        match (if isNull (box s) then "" else s.Trim().ToLowerInvariant()) with
        | "ai" -> Analyser.Ai
        | "heuristic" -> Analyser.Deterministic
        | other -> Analyser.Unknown other

    /// The heading over the summary. **The one label that must never lie**: a
    /// summary the offline rules produced is not an AI summary, and calling it
    /// one would misrepresent where the words came from.
    let summaryHeading =
        function
        | Analyser.Ai -> "AI SUMMARY"
        | Analyser.Deterministic -> "EXTRACTED SUMMARY"
        | Analyser.Unknown _ -> "SUMMARY"

    let shortLabel =
        function
        | Analyser.Ai -> "AI"
        | Analyser.Deterministic -> "Rules"
        | Analyser.Unknown _ -> "Unknown"

    /// Replaces the redesign's "confidence explanation" with the one thing that
    /// can be stated truthfully: which engine ran, and what that means.
    let explanation =
        function
        | Analyser.Ai ->
            "Classified by the configured AI provider from the item's title and content. No confidence score is produced, so none is shown."
        | Analyser.Deterministic ->
            "Classified offline by the rules engine — dates, module codes and intent keywords extracted from the text. Deterministic: the same input always gives the same proposals."
        | Analyser.Unknown other -> sprintf "Recorded analyser \"%s\" is not one this build knows how to describe." other

let analyserOf (item: InboxItem) =
    item.Analysis |> Option.map (fun a -> Analyser.ofString a.Analyzer)

// ---------------------------------------------------------------------------
// semantic ink
// ---------------------------------------------------------------------------

/// The MEANING a coloured value carries. The logic decides the meaning; the view
/// decides the paint — so no colour literal is chosen here, and the set of roles
/// the screen can write a word in is a list a contrast test can walk.
///
/// **Colour is never the only signal.** Every value that takes one of these also
/// prints its own word, which is why `Status.label`, `Conflict.text` and
/// `Effect.Label` all exist and are asserted non-empty.
[<RequireQualifiedAccess>]
type Ink =
    | Positive
    | Caution
    | Informational
    | Critical
    | Neutral
    | Accent
    /// A source's fixed identity hue — the same colour wherever that source
    /// appears, and deliberately not the user's accent.
    | Identity

/// Every role the Inbox can write text in. Walked by the contrast tests.
let inksUsed =
    [ Ink.Positive
      Ink.Caution
      Ink.Informational
      Ink.Critical
      Ink.Neutral
      Ink.Accent
      Ink.Identity ]

let statusInk =
    function
    | InboxStatus.New -> Ink.Caution
    | InboxStatus.Analyzed -> Ink.Informational
    | InboxStatus.Applied -> Ink.Positive
    | InboxStatus.Dismissed -> Ink.Neutral

let effectInk =
    function
    | EffectKind.Creates -> Ink.Positive
    | EffectKind.Updates -> Ink.Caution
    | EffectKind.Links -> Ink.Informational
    | EffectKind.Records -> Ink.Neutral

/// A proposal that cannot apply is a failure; everything else is a caution.
let conflictInk (conflict: Conflict) =
    if Conflict.isBlocking conflict then Ink.Critical else Ink.Caution

// ---------------------------------------------------------------------------
// filters
// ---------------------------------------------------------------------------

/// The categories the real model supports.
///
/// Two of the redesign's ten are not here:
///
/// - **Lectures.** Nothing in the capture pipeline produces a lecture;
///   `ProposedActionKind` has no such case and `ILectureRepository` is never
///   touched by `CaptureService`. A chip that could only ever be empty is an
///   inert decoration.
/// - **Duplicates** is widened to **Conflicts**, because the real check finds
///   duplicate assignments, disagreeing deadlines, unknown modules and proposals
///   that cannot apply — and hiding four of those behind a chip named for one of
///   them would be the misleading choice.
///
/// **Notes** is added, because `CreateNote` is the pipeline's most common
/// proposal and had nowhere to be filtered.
[<RequireQualifiedAccess>]
type Filter =
    | All
    | NeedsReview
    | Assignments
    | Calendar
    | Notes
    | Documents
    | Emails
    | AiAnalysed
    | Conflicts
    | Completed

[<RequireQualifiedAccess>]
module Filter =

    let all =
        [ Filter.All
          Filter.NeedsReview
          Filter.Assignments
          Filter.Calendar
          Filter.Notes
          Filter.Documents
          Filter.Emails
          Filter.AiAnalysed
          Filter.Conflicts
          Filter.Completed ]

    let label =
        function
        | Filter.All -> "All"
        | Filter.NeedsReview -> "Needs review"
        | Filter.Assignments -> "Assignments"
        | Filter.Calendar -> "Calendar"
        | Filter.Notes -> "Notes"
        | Filter.Documents -> "Documents"
        | Filter.Emails -> "Emails"
        | Filter.AiAnalysed -> "AI analysed"
        | Filter.Conflicts -> "Conflicts"
        | Filter.Completed -> "Completed"

    /// Shown when a filter selects nothing, so an empty list explains itself
    /// rather than looking broken.
    let emptyMessage =
        function
        | Filter.All -> "Nothing has arrived yet."
        | Filter.NeedsReview -> "Nothing is waiting for you — every item has been applied or dismissed."
        | Filter.Assignments -> "No proposal would create or move an assignment."
        | Filter.Calendar -> "No proposal would add a calendar event."
        | Filter.Notes -> "No proposal would create a note."
        | Filter.Documents -> "Nothing here arrived as a file."
        | Filter.Emails -> "Nothing here arrived from a connected mailbox."
        | Filter.AiAnalysed -> "Nothing here was classified by the AI provider."
        | Filter.Conflicts -> "Nothing needs confirmation — no duplicates or disagreements were found."
        | Filter.Completed -> "Nothing has been applied or dismissed yet."

    /// Whether the item's proposals include any of the given kinds.
    let private proposes (kinds: ProposedActionKind list) (item: InboxItem) =
        realActionsOf item |> List.exists (fun a -> List.contains a.Kind kinds)

    /// `conflictsOf` is passed in rather than recomputed, so one snapshot of what
    /// exists serves the whole frame and the Conflicts chip cannot disagree with
    /// the warning drawn on the card.
    let matches (conflictsOf: InboxItem -> Conflict list) (filter: Filter) (item: InboxItem) =
        match filter with
        | Filter.All -> true
        | Filter.NeedsReview -> isActive item
        | Filter.Assignments ->
            proposes [ ProposedActionKind.CreateAssignment; ProposedActionKind.UpdateDeadline ] item
        | Filter.Calendar -> proposes [ ProposedActionKind.CreateEvent ] item
        | Filter.Notes -> proposes [ ProposedActionKind.CreateNote ] item
        | Filter.Documents -> hasDocument item || sourceKindOf item = SourceKind.File
        | Filter.Emails -> sourceKindOf item = SourceKind.Email
        | Filter.AiAnalysed -> analyserOf item = Some Analyser.Ai
        | Filter.Conflicts -> not (List.isEmpty (conflictsOf item))
        | Filter.Completed -> not (isActive item)

/// The visible list: filtered, newest first.
///
/// Ordering is explicit rather than inherited from the repository, so the list
/// cannot silently reorder under a refresh and move a card out from under the
/// pointer.
let visibleItems (conflictsOf: InboxItem -> Conflict list) (filter: Filter) (items: InboxItem list) =
    items
    |> List.filter (Filter.matches conflictsOf filter)
    |> List.sortByDescending (fun i -> i.ReceivedAt)

// ---------------------------------------------------------------------------
// summary metrics
// ---------------------------------------------------------------------------

/// A summary figure.
///
/// `Value` is an option because some figures are genuinely unknown rather than
/// zero. Before anything has been analysed, "0 assignments detected" claims the
/// pipeline looked and found none; it has not looked. `None` renders as an em
/// dash with the reason beside it, exactly as the Planner's unscored dial does.
type Metric =
    { Label: string
      Value: string option
      /// Why the value is missing. Only meaningful when `Value` is `None`.
      Unavailable: string option }

/// The five summary cards.
///
/// **"AI confidence" and "Avg processing time" are deliberately absent** — see
/// the module header. They are replaced by two figures the model can produce:
/// how much has not been looked at yet, and how much change is actually
/// proposed.
let metrics (items: InboxItem list) : Metric list =
    let active = items |> List.filter isActive
    let analysed = active |> List.filter (fun i -> i.Analysis.IsSome)

    // Nothing analysed is not the same as nothing proposed.
    let unexamined =
        if List.isEmpty active then None
        elif List.isEmpty analysed then Some "nothing analysed yet"
        else None

    let countProposals (kinds: ProposedActionKind list) =
        analysed
        |> List.sumBy (fun i -> realActionsOf i |> List.filter (fun a -> List.contains a.Kind kinds) |> List.length)

    let derived (value: unit -> int) =
        match unexamined with
        | Some why -> { Value = None; Unavailable = Some why; Label = "" }
        | None -> { Value = Some(string (value ())); Unavailable = None; Label = "" }

    [ { Label = "Items waiting"
        Value = Some(string active.Length)
        Unavailable = None }

      { Label = "Awaiting analysis"
        Value = Some(string (active |> List.filter (fun i -> i.Analysis.IsNone) |> List.length))
        Unavailable = None }

      { derived (fun () ->
            countProposals [ ProposedActionKind.CreateAssignment; ProposedActionKind.UpdateDeadline ]) with
          Label = "Assignments detected" }

      { Label = "Documents imported"
        Value = Some(string (items |> List.filter hasDocument |> List.length))
        Unavailable = None }

      { derived (fun () -> analysed |> List.sumBy (fun i -> (realActionsOf i).Length)) with
          Label = "Proposed changes" } ]

// ---------------------------------------------------------------------------
// insight strip
// ---------------------------------------------------------------------------

/// Concise observations, every one derived from the loaded items and the real
/// conflict check. Never a stock sentence: an observation that would report
/// nothing is not produced at all, and an empty list is rendered as a calm
/// status rather than filled with reassurance.
let insights (now: DateTime) (conflictsOf: InboxItem -> Conflict list) (items: InboxItem list) : string list =
    let active = items |> List.filter isActive

    let plural n singular plural' =
        if n = 1 then sprintf "%d %s" n singular else sprintf "%d %s" n plural'

    let capturedToday = items |> List.filter (fun i -> i.ReceivedAt.Date = now.Date) |> List.length
    let unanalysed = active |> List.filter (fun i -> i.Analysis.IsNone) |> List.length
    let conflicted = active |> List.filter (fun i -> not (List.isEmpty (conflictsOf i))) |> List.length
    let analysed = active |> List.filter (fun i -> i.Analysis.IsSome)
    let byAi = analysed |> List.filter (fun i -> analyserOf i = Some Analyser.Ai) |> List.length
    let byRules = analysed.Length - byAi

    let proposalCount kind =
        analysed
        |> List.sumBy (fun i -> realActionsOf i |> List.filter (fun a -> a.Kind = kind) |> List.length)

    let newAssignments = proposalCount ProposedActionKind.CreateAssignment
    let newEvents = proposalCount ProposedActionKind.CreateEvent

    [ if capturedToday > 0 then
          sprintf "%s captured today." (plural capturedToday "item" "items")

      if unanalysed > 0 then
          sprintf "%s not analysed yet." (plural unanalysed "item is" "items are")

      if conflicted > 0 then
          sprintf "%s needing confirmation before applying." (plural conflicted "item" "items")

      if byAi > 0 && byRules > 0 then
          sprintf "%d analysed by AI, %d by the offline rules." byAi byRules
      elif byAi > 0 then
          sprintf "%s analysed by AI." (plural byAi "item" "items")
      elif byRules > 0 then
          sprintf "%s analysed by the offline rules." (plural byRules "item" "items")

      if newAssignments > 0 then
          sprintf "Accepting everything would create %s for the Planner." (plural newAssignments "assignment" "assignments")

      if newEvents > 0 then
          sprintf "Accepting everything would add %s to the calendar." (plural newEvents "event" "events") ]

// ---------------------------------------------------------------------------
// item actions
// ---------------------------------------------------------------------------

/// The actions one review card really offers.
///
/// The redesign shows Accept, Edit, Reject, Open Preview, Change Module,
/// Generate Notes and Move to Documents. Four have no service behind them and are
/// omitted rather than drawn dead:
///
/// - **Edit** — nothing edits a proposal; `ProposedAction` is written by the
///   analyser and read by `ApplyAction`.
/// - **Change Module** — no service reassigns an item's module.
/// - **Generate Notes** — `CreateNote` is already a proposal when the analyser
///   judges one applies; there is no separate note generator.
/// - **Move to Documents** — a captured file is imported into the document store
///   by `CaptureService.CaptureFile` at capture time. It is already there.
///
/// **Reject** is `Dismiss` and **Open Preview** is `Open file`, which are this
/// application's own words for the same operations.
[<RequireQualifiedAccess>]
type ItemActionId =
    | Analyse
    | Accept
    | Dismiss
    | OpenFile

type ItemActionState =
    { Id: ItemActionId
      Label: string
      Primary: bool
      Enabled: bool
      /// Printed as visible text beneath the row when the action is disabled.
      DisabledBecause: string option }

type ItemContext =
    { Status: InboxStatus
      HasAnalysis: bool
      RealActionCount: int
      HasDocument: bool
      /// True while any request for this item is in flight. The re-entry guard:
      /// every action is disabled and every message ignored, so a double click
      /// cannot submit the same operation twice.
      Busy: bool }

let itemContext (busyId: InboxItemId option) (item: InboxItem) =
    { Status = item.Status
      HasAnalysis = item.Analysis.IsSome
      RealActionCount = (realActionsOf item).Length
      HasDocument = hasDocument item
      Busy = busyId = Some item.Id }

let itemActions (ctx: ItemContext) : ItemActionState list =
    let processed = not (Status.isActive ctx.Status)

    let state id label primary enabled why =
        { Id = id
          Label = label
          Primary = primary
          Enabled = enabled && not ctx.Busy
          DisabledBecause =
            if ctx.Busy then Some "an action on this item is still running"
            elif enabled then None
            else Some why }

    [ state
          ItemActionId.Accept
          "Accept"
          true
          (not processed && ctx.HasAnalysis && ctx.RealActionCount > 0)
          (if processed then "this item has already been processed"
           elif not ctx.HasAnalysis then "analyse it first to see what would happen"
           else "nothing was proposed for this item")

      state
          ItemActionId.Analyse
          (if ctx.HasAnalysis then "Re-analyse" else "Analyse")
          false
          (not processed)
          "this item has already been processed"

      state ItemActionId.Dismiss "Dismiss" false (not processed) "this item has already been processed"

      // Only offered when there is a file to open. A control that could never do
      // anything is not drawn at all.
      if ctx.HasDocument then
          state ItemActionId.OpenFile "Open file" false true "" ]

// ---------------------------------------------------------------------------
// bulk actions
// ---------------------------------------------------------------------------

/// Bulk actions operate on **everything the current filter shows**.
///
/// The Inbox has no multi-selection model and inventing one would mean a new
/// selection state, new persistence and new keyboard semantics for a row of six
/// buttons — so the bulk row is adapted to the selection model that exists. The
/// set is explicit in every label ("Accept all 4 shown"), so the user always
/// knows what a bulk action will touch.
///
/// Three of the redesign's six have no service behind them and are omitted:
/// **Delete** (`IInboxRepository` has no delete — nothing can remove an item),
/// **Assign Module** (no service assigns one) and **Mark as Duplicate** (nothing
/// records a duplicate; the Conflicts filter surfaces the real check instead).
/// **Archive** is `Dismiss`, which is what leaving the active inbox means here.
/// **Generate Summaries** is `Analyse`, which is what produces a summary.
[<RequireQualifiedAccess>]
type BulkActionId =
    | AcceptAll
    | AnalyseAll
    | DismissAll

type BulkActionState =
    { Id: BulkActionId
      Label: string
      Primary: bool
      Enabled: bool
      /// Destructive enough to need a confirmation step before it runs.
      NeedsConfirmation: bool
      DisabledBecause: string option }

type BulkContext =
    { Shown: int
      /// Shown items with no analysis yet.
      Unanalysed: int
      /// Shown items that could be accepted right now.
      Acceptable: int
      /// Shown items still awaiting the user.
      Active: int
      Busy: bool }

let bulkContext (busy: bool) (visible: InboxItem list) =
    { Shown = visible.Length
      Unanalysed = visible |> List.filter (fun i -> isActive i && i.Analysis.IsNone) |> List.length
      Acceptable =
        visible
        |> List.filter (fun i -> isActive i && i.Analysis.IsSome && not (List.isEmpty (realActionsOf i)))
        |> List.length
      Active = visible |> List.filter isActive |> List.length
      Busy = busy }

let bulkActions (ctx: BulkContext) : BulkActionState list =
    let state id label primary confirm enabled why =
        { Id = id
          Label = label
          Primary = primary
          NeedsConfirmation = confirm
          Enabled = enabled && not ctx.Busy
          DisabledBecause =
            if ctx.Busy then Some "a bulk action is still running"
            elif enabled then None
            else Some why }

    [ state
          BulkActionId.AcceptAll
          (sprintf "Accept all %d shown" ctx.Acceptable)
          true
          false
          (ctx.Acceptable > 0)
          "nothing shown has proposals ready to accept"

      state
          BulkActionId.AnalyseAll
          (sprintf "Analyse all %d shown" ctx.Unanalysed)
          false
          false
          (ctx.Unanalysed > 0)
          "everything shown has already been analysed"

      state
          BulkActionId.DismissAll
          (sprintf "Dismiss all %d shown" ctx.Active)
          false
          true
          (ctx.Active > 0)
          "nothing shown is still waiting" ]

/// The sentence shown while a destructive bulk action waits for confirmation.
let confirmationPrompt (id: BulkActionId) (count: int) =
    match id with
    | BulkActionId.DismissAll ->
        sprintf
            "Dismiss %d item%s? They leave the active inbox and their proposals are not applied. Dismissal cannot be undone — no repository here keeps a prior state."
            count
            (if count = 1 then "" else "s")
    | BulkActionId.AcceptAll -> sprintf "Accept %d items?" count
    | BulkActionId.AnalyseAll -> sprintf "Analyse %d items?" count

// ---------------------------------------------------------------------------
// page composition
// ---------------------------------------------------------------------------

/// The page, as data. The view maps over this list, so a section cannot be
/// reordered or dropped without editing it — which the layout tests assert.
[<RequireQualifiedAccess>]
type Section =
    | Header
    | Metrics
    | Insights
    | Filters
    | BulkActions
    | Workspace

let sectionOrder =
    [ Section.Header
      Section.Metrics
      Section.Insights
      Section.Filters
      Section.BulkActions
      Section.Workspace ]

// ---------------------------------------------------------------------------
// responsive layout
// ---------------------------------------------------------------------------

/// Whether the detail panel sits beside the list or beneath it. The same
/// threshold as the Planner and the Timeline, so the three screens break at one
/// width rather than three.
let showsDetailColumn (contentWidth: float) = contentWidth >= 1100.0

/// Columns for the five summary cards. They reflow rather than compress, so a
/// metric is never squeezed to an unreadable width.
let metricColumns (contentWidth: float) =
    if contentWidth < 700.0 then 2
    elif contentWidth < 1180.0 then 3
    else 5
