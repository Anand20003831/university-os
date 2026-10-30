module UniversityOS.App.Features.AssistantLogic

open System
open UniversityOS.Domain
// `AiError` is the Application layer's typed provider-failure union. Mapping it
// here rather than in the view is what keeps a generic exception from ever being
// reported as, say, a rate limit.
open UniversityOS.Application
open UniversityOS.App

/// Every decision the Assistant screen makes, as pure functions — testable
/// without a provider, a database or an Avalonia application.
///
/// **Two verified facts govern this screen.**
///
/// 1. `AiService.BuildContext` sends a *specific* slice of the application, not
///    the whole database. See `contextScope`.
/// 2. `AiService.Chat` genuinely applies data changes, and records them by
///    appending lines to the assistant message text. See `splitApplied`.

// ---------------------------------------------------------------------------
// applied actions — the persistence contract
// ---------------------------------------------------------------------------

/// **The exact format `AiService.Chat` composes** (`AiService.fs:420-426`):
///
/// ```
/// finalText =
///     if applied.Count = 0 then prose
///     else prose + "\n\n" + (applied |> Seq.map (fun a -> "✓ " + a) |> String.concat "\n")
/// ```
///
/// and a failed action's description is itself prefixed by the service with
/// `"Could not apply a change: "`.
///
/// `ChatOutcome.AppliedActions` carries the same strings, but **only on the live
/// response** — nothing persists it. A reloaded conversation has only the
/// composed text. So the text is the source of truth for both, and this module
/// parses it.
[<Literal>]
let appliedLinePrefix = "✓ "

[<Literal>]
let failurePrefix = "Could not apply a change: "

/// One row of the applied-changes strip.
type AppliedAction =
    { /// The user-facing description, with the failure prefix removed.
      Description: string
      Failed: bool }

[<RequireQualifiedAccess>]
module AppliedAction =

    /// Turn one raw `AppliedActions` entry — or one parsed line body — into a
    /// presentation row. The service writes the failure prefix into the
    /// description itself, so that prefix is the **only** discriminator.
    let ofDescription (raw: string) =
        let text = raw.Trim()

        if text.StartsWith failurePrefix then
            { Description = text.Substring(failurePrefix.Length).Trim(); Failed = true }
        else
            { Description = text; Failed = false }

/// **The parsing contract.**
///
/// The appended block is identified structurally, exactly as it was composed:
///
/// * it is a run of **contiguous lines at the very end** of the message;
/// * every line in the run starts with `"✓ "`;
/// * the run is **preceded by a blank line**, which is the `"\n\n"` the service
///   inserts between the prose and the block.
///
/// That last rule is what stops an ordinary `✓ ` line in the middle of a reply
/// from being mistaken for an applied change. A `✓ ` line that has prose
/// directly above it is body text and is left alone.
///
/// Returns the prose with the block removed, and one row per action, in order.
let splitApplied (message: string) : string * AppliedAction list =
    if String.IsNullOrEmpty message then
        "", []
    else
        // Normalised so a CRLF round-trip through the repository cannot change
        // the answer.
        let lines = message.Replace("\r\n", "\n").Split('\n')

        let isAction (line: string) = line.StartsWith appliedLinePrefix

        // Walk back over the trailing run of action lines.
        let mutable start = lines.Length

        while start > 0 && isAction lines.[start - 1] do
            start <- start - 1

        // No trailing run at all.
        if start = lines.Length then
            message.TrimEnd(), []
        else
            // The run must be preceded by a blank line — the separator the
            // service writes. Without one, these are body lines, not the block.
            let precededByBlank = start > 0 && String.IsNullOrWhiteSpace lines.[start - 1]

            // A message that is *nothing but* the block is the empty-prose case:
            // the service still writes the separator, so the leading lines are
            // blank and `start` lands past them.
            let wholeMessageIsBlock = start = 0

            if not (precededByBlank || wholeMessageIsBlock) then
                message.TrimEnd(), []
            else
                let body = lines.[.. start - 1] |> String.concat "\n"

                let actions =
                    lines.[start ..]
                    |> Array.map (fun line -> AppliedAction.ofDescription (line.Substring appliedLinePrefix.Length))
                    |> List.ofArray

                body.TrimEnd(), actions

/// The same presentation, built from a **live** `ChatOutcome.AppliedActions`
/// list. Kept beside `splitApplied` so the two can be asserted equivalent.
let presentLive (appliedActions: string list) =
    appliedActions |> List.map AppliedAction.ofDescription

/// The strip's heading, from what actually happened.
[<RequireQualifiedAccess>]
type AppliedOutcome =
    | AllApplied
    | Partial
    | NoneApplied

let outcomeOf (actions: AppliedAction list) =
    if actions |> List.forall (fun a -> not a.Failed) then AppliedOutcome.AllApplied
    elif actions |> List.forall (fun a -> a.Failed) then AppliedOutcome.NoneApplied
    else AppliedOutcome.Partial

[<RequireQualifiedAccess>]
module AppliedOutcome =

    /// Never celebratory: these describe changes to the user's own data, some of
    /// which may be unwanted.
    let heading =
        function
        | AppliedOutcome.AllApplied -> "Changes applied"
        | AppliedOutcome.Partial -> "Some changes applied"
        | AppliedOutcome.NoneApplied -> "Changes could not be applied"

/// **The navigation limitation, stated once.**
///
/// A live `ChatOutcome` knows which entities changed only as prose; nothing
/// carries an entity id, and the persisted text carries even less. So the strip
/// **never** offers per-entity navigation — guessing an id out of a description
/// would be exactly the kind of invention this project refuses.
///
/// Successful actions are logged as `ActivityKind.AiAction`, so the Timeline is
/// the real place to follow them, and that is the one navigation offered.
[<Literal>]
let appliedNavigationNote =
    "Applied changes are also recorded on the Timeline. This strip does not link to individual entities, because neither the response nor the stored message carries their identifiers."

// ---------------------------------------------------------------------------
// what the provider actually receives
// ---------------------------------------------------------------------------

/// **The verified scope of `AiService.BuildContext`.**
///
/// Each entry is a category the system prompt genuinely contains. Anything not
/// in this list is not sent, and the screen must not imply otherwise.
type ContextCategory = { Label: string; Detail: string }

let contextScope =
    [ { Label = "Date and time"; Detail = "the current date and time" }
      { Label = "Your name"; Detail = "from Settings" }
      { Label = "Modules"; Detail = "every module, with its code, name and lecturer" }
      { Label = "Open assignments"; Detail = "open coursework only — completed work is not sent" }
      { Label = "Notes"; Detail = "every note" }
      { Label = "Today's events"; Detail = "calendar events for today" }
      { Label = "This week's events"; Detail = "calendar events for the next seven days" }
      { Label = "Today's sessions"; Detail = "planner sessions for today" } ]

/// Categories the provider does **not** receive. Rendered so the boundary is
/// visible rather than merely absent.
let contextExcluded =
    [ "Documents and their extracted text"
      "Lectures and transcripts"
      "Flashcards"
      "Graph links"
      "Inbox captures"
      "Completed assignments" ]

/// The strongest claim the repository supports. No encryption, retention or
/// local-only assertion appears anywhere on this screen.
[<Literal>]
let privacyNotice =
    "Messages, plus your modules, open assignments, notes and this week's schedule, are sent to the configured AI provider."

/// Text arriving from another screen is **not** structured context — it is words
/// placed in the composer, which the user can edit or delete. Labelling it as
/// attached context would overstate what happened.
[<Literal>]
let prefilledNotice = "Prefilled prompt — edit or clear it before sending."

// ---------------------------------------------------------------------------
// provider state
// ---------------------------------------------------------------------------

/// Only the states the service genuinely exposes. There is no streaming, so
/// there is no "connecting" and no "stopped".
[<RequireQualifiedAccess>]
type ProviderState =
    | Ready
    | NotConfigured
    | Failed of string

[<RequireQualifiedAccess>]
module ProviderState =

    let label =
        function
        | ProviderState.Ready -> "Connected"
        | ProviderState.NotConfigured -> "No provider configured"
        | ProviderState.Failed _ -> "Last request failed"

    let isReady =
        function
        | ProviderState.Ready -> true
        | _ -> false

/// `AiError` is a real typed union, so each case maps to its own sentence. A
/// generic exception is **never** reported as a rate limit.
let describeError (error: AiError) =
    match error with
    | AiError.NotConfigured ->
        "No AI provider is configured. Open Settings and add an API key to enable the Assistant."
    | AiError.Auth message -> "The provider rejected the credentials: " + message
    | AiError.RateLimited message -> "The provider is rate limiting requests: " + message
    | AiError.Transport message -> "Could not reach the provider: " + message
    | AiError.Provider message -> "The provider returned an error: " + message

// ---------------------------------------------------------------------------
// the composer
// ---------------------------------------------------------------------------

/// The request cap `AiService.Chat` actually sets.
[<Literal>]
let maxTokens = 4096

/// A character cap on the composer, matching the request's own limit so a
/// message cannot be written that the request would truncate.
[<Literal>]
let maxMessageChars = 4096

/// Why Send is unavailable. `None` means it is available.
let sendBlockedReason (provider: ProviderState) (sending: bool) (draft: string) =
    if not (ProviderState.isReady provider) then
        Some "No AI provider is configured, so nothing can be sent."
    elif sending then
        Some "Waiting for the current reply."
    elif draft.Trim().Length = 0 then
        Some "Write a message first."
    elif draft.Length > maxMessageChars then
        Some(sprintf "That is %d characters; the limit is %d." draft.Length maxMessageChars)
    else
        None

let canSend provider sending draft =
    (sendBlockedReason provider sending draft).IsNone

/// Shown only when it is close enough to matter.
let counterText (draft: string) =
    if draft.Length < maxMessageChars * 4 / 5 then None
    else Some(sprintf "%d / %d" draft.Length maxMessageChars)

// ---------------------------------------------------------------------------
// suggested prompts
// ---------------------------------------------------------------------------

/// What the screen knows about the user's data, for gating suggestions. Every
/// field corresponds to something `BuildContext` genuinely sends.
type ContextFacts =
    { Modules: int
      OpenAssignments: int
      Notes: int
      EventsThisWeek: int
      SessionsToday: int }

[<RequireQualifiedAccess>]
module ContextFacts =
    let empty =
        { Modules = 0; OpenAssignments = 0; Notes = 0; EventsThisWeek = 0; SessionsToday = 0 }

/// Suggestions are **gated by the data that is actually sent**. A suggestion
/// about documents, lectures, transcripts, flashcards, the graph or the Inbox
/// can never appear, because none of that reaches the provider.
///
/// Clicking one **populates the composer**. Nothing is sent until the user
/// presses Send.
let suggestions (facts: ContextFacts) : string list =
    [ if facts.OpenAssignments > 0 then
          "Help me prioritise my open assignments."

      if facts.OpenAssignments > 1 then
          "Compare the deadlines on my open coursework."

      if facts.SessionsToday > 0 || facts.OpenAssignments > 0 then
          "What should I study today?"

      if facts.EventsThisWeek > 0 then
          "Where does my schedule look overloaded this week?"

      if facts.Notes > 0 then
          "Summarise my notes on one of my modules."

      if facts.Notes > 0 then
          "Turn my notes into revision questions."

      if facts.Modules > 1 then
          "Which module am I furthest behind on?" ]

// ---------------------------------------------------------------------------
// conversations
// ---------------------------------------------------------------------------

/// **The title is not a summary.** `Conversation.create` stores the first 48
/// characters of the opening message, so the screen calls it what it is.
[<Literal>]
let titleNotice = "Titles are the opening line of each conversation."

/// Date grouping for the history rail, mirroring the redesign's grouped list.
let groupLabel (now: DateTime) (updated: DateTime) =
    let days = (now.Date - updated.Date).TotalDays

    if days <= 0.0 then "Today"
    elif days <= 1.0 then "Yesterday"
    elif days <= 7.0 then "This week"
    else "Older"

let groupOrder = [ "Today"; "Yesterday"; "This week"; "Older" ]

/// Conversations grouped and ordered deterministically, so a refresh cannot
/// reorder the rail under the pointer.
let groupConversations (now: DateTime) (conversations: Conversation list) =
    let matching label =
        conversations
        |> List.filter (fun c -> groupLabel now c.UpdatedAt = label)
        |> List.sortByDescending (fun c -> c.UpdatedAt)

    groupOrder |> List.map (fun label -> label, matching label) |> List.filter (fun (_, cs) -> not cs.IsEmpty)

let searchConversations (query: string) (conversations: Conversation list) =
    let q = query.Trim()

    if q.Length = 0 then
        conversations
    else
        conversations |> List.filter (fun c -> c.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)

// ---------------------------------------------------------------------------
// scrolling
// ---------------------------------------------------------------------------

/// How close to the bottom still counts as "at the bottom", in pixels.
[<Literal>]
let followThreshold = 120.0

/// Whether the view should scroll to the newest message.
///
/// Someone who has scrolled up to read is **never** dragged back down; the
/// screen offers Jump to latest instead.
let shouldFollow (userScrolledUp: bool) (distanceFromBottom: float) =
    not userScrolledUp && distanceFromBottom <= followThreshold

/// The affordance appears exactly when following is off and there is somewhere
/// to jump to.
let showJumpToLatest (userScrolledUp: bool) (distanceFromBottom: float) (hasMessages: bool) =
    hasMessages && (userScrolledUp || distanceFromBottom > followThreshold)

// ---------------------------------------------------------------------------
// page composition
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type Section =
    | Header
    | History
    | Conversation
    | Composer
    | Context

let sectionOrder =
    [ Section.Header; Section.History; Section.Conversation; Section.Composer; Section.Context ]

/// The centre column's regions, in order.
[<RequireQualifiedAccess>]
type CentreRegion =
    | Messages
    | Suggestions
    | Composer

let centreOrder = [ CentreRegion.Messages; CentreRegion.Suggestions; CentreRegion.Composer ]

/// The context rail's regions, in order.
[<RequireQualifiedAccess>]
type ContextRegion =
    | Provider
    | Scope
    | Excluded
    | Privacy

let contextOrder =
    [ ContextRegion.Provider; ContextRegion.Scope; ContextRegion.Excluded; ContextRegion.Privacy ]

// ---------------------------------------------------------------------------
// geometry and responsive layout
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module Geometry =

    [<Literal>]
    let contentHighlight = 0.0

    [<Literal>]
    let borderInset = 0.5

    [<Literal>]
    let historyWidth = 260.0

    [<Literal>]
    let contextWidth = 300.0

    [<Literal>]
    let railWidth = 34.0

    /// The centre column never goes below this, which is what stops the
    /// conversation being squeezed into a strip.
    [<Literal>]
    let centreMinWidth = 420.0

    /// The redesign's bubble caps.
    [<Literal>]
    let userBubbleFraction = 0.64

    [<Literal>]
    let assistantBubbleFraction = 0.78

    [<Literal>]
    let bubbleRadius = 16.0

    /// The tight corner that points at the speaker — layout, not colour, is what
    /// tells the two roles apart.
    [<Literal>]
    let bubbleTightRadius = 4.0

    [<Literal>]
    let actionMinHeight = 38.0

    [<Literal>]
    let actionRadius = 11.0

    [<Literal>]
    let composerRadius = 18.0

    // **Every alpha that is painted is named here**, because the alpha used to
    // paint must be the alpha used to measure. A literal typed into the view and
    // a different one typed into a contrast test is how a screen ends up
    // "verified readable" while being unreadable.

    [<Literal>]
    let chipFillAlpha = "1F"

    [<Literal>]
    let userBubbleAlpha = "1F"

    /// The user bubble's edge.
    [<Literal>]
    let userBubbleEdgeAlpha = "4D"

    /// The selected conversation row in the history rail.
    [<Literal>]
    let selectedRowAlpha = "2E"

    /// The applied-changes strip: fill and edge.
    [<Literal>]
    let appliedFillAlpha = "1F"

    [<Literal>]
    let appliedEdgeAlpha = "40"

    /// The delete-confirmation banner's fill.
    [<Literal>]
    let confirmFillAlpha = "1F"

    /// The error banner: fill and edge.
    [<Literal>]
    let errorFillAlpha = "22"

    [<Literal>]
    let errorEdgeAlpha = "55"

    /// The hover veil over a glass action — `Opacity` does not reach a
    /// `GlassPanel`, so hover is an ordinary Border painted at this alpha.
    [<Literal>]
    let veilAlpha = "2E"

    [<Literal>]
    let bulletColumns = "Auto,9,*"

    [<Literal>]
    let detailColumns = "Auto,10,*"

[<Literal>]
let mediumWidth = 1280.0

[<Literal>]
let narrowWidth = 900.0

type Placement =
    { ShowHistory: bool
      ShowContext: bool
      /// Below the narrow breakpoint both rails become sections stacked with the
      /// conversation rather than columns beside it.
      Stacked: bool }

/// **Nothing is dropped at a narrow width, only re-placed.**
///
/// Below the narrow breakpoint both rails become stacked, collapsible sections;
/// they are not removed, because the context rail is where the screen states
/// what the provider actually receives, and that must not disappear with the
/// window. `ChatView` collapses both on crossing into narrow, so the
/// conversation still owns the width until the reader asks for a rail back.
let placementFor (width: float) (historyOpen: bool) (contextOpen: bool) =
    if width < narrowWidth then
        { ShowHistory = historyOpen; ShowContext = contextOpen; Stacked = true }
    elif width < mediumWidth then
        { ShowHistory = historyOpen; ShowContext = false; Stacked = false }
    else
        { ShowHistory = historyOpen; ShowContext = contextOpen; Stacked = false }

let historyColumnWidth (p: Placement) =
    if p.ShowHistory then Geometry.historyWidth else Geometry.railWidth

let contextColumnWidth (p: Placement) =
    if p.ShowContext then Geometry.contextWidth else Geometry.railWidth
