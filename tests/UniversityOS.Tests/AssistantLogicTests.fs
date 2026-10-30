module UniversityOS.Tests.AssistantLogicTests

open System
open Xunit
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.App
open UniversityOS.App.Features

/// The Assistant's decision layer.
///
/// The load-bearing piece is `splitApplied`: `AiService.Chat` records the data
/// changes it applies by appending them to the assistant message **text**, and
/// the structured `AppliedActions` list is never persisted. So a reloaded
/// conversation must reconstruct the strip by parsing, and the two paths must
/// agree exactly.

let private now = DateTime(2026, 8, 1, 16, 0, 0)

/// **Composes exactly what `AiService.Chat` composes** (`AiService.fs:420-426`),
/// so these tests exercise the real format rather than an approximation of it.
let private compose (prose: string) (applied: string list) =
    if applied.IsEmpty then
        prose
    else
        prose + "\n\n" + (applied |> List.map (fun a -> "✓ " + a) |> String.concat "\n")

let private failed (description: string) = AssistantLogic.failurePrefix + description

// ---------------------------------------------------------------------------
// 1. splitApplied — the parsing contract
// ---------------------------------------------------------------------------

[<Fact>]
let ``a message with no action block parses to itself and no actions`` () =
    let body, actions = AssistantLogic.splitApplied "Here is a plain answer."
    Assert.Equal("Here is a plain answer.", body)
    Assert.Empty(actions)

[<Fact>]
let ``an empty message is handled without crashing`` () =
    let body, actions = AssistantLogic.splitApplied ""
    Assert.Equal("", body)
    Assert.Empty(actions)

[<Fact>]
let ``one successful action is separated from the prose`` () =
    let text = compose "I have added that." [ "Created assignment \"Essay 2\"" ]
    let body, actions = AssistantLogic.splitApplied text

    Assert.Equal("I have added that.", body)
    Assert.Equal(1, actions.Length)
    Assert.Equal("Created assignment \"Essay 2\"", actions.Head.Description)
    Assert.False(actions.Head.Failed)

[<Fact>]
let ``several actions keep their order`` () =
    let text = compose "Done." [ "Created A"; "Created B"; "Created C" ]
    let _, actions = AssistantLogic.splitApplied text

    Assert.Equal<string list>([ "Created A"; "Created B"; "Created C" ], actions |> List.map (fun a -> a.Description))

[<Fact>]
let ``a failed action is detected by its prefix, and the prefix is removed`` () =
    let text = compose "I tried." [ failed "the module does not exist" ]
    let _, actions = AssistantLogic.splitApplied text

    Assert.True(actions.Head.Failed)
    // The user-facing description no longer repeats the prefix.
    Assert.Equal("the module does not exist", actions.Head.Description)
    Assert.DoesNotContain(AssistantLogic.failurePrefix, actions.Head.Description)

[<Fact>]
let ``mixed success and failure are both recognised`` () =
    let text = compose "Partly done." [ "Created A"; failed "B was rejected"; "Created C" ]
    let _, actions = AssistantLogic.splitApplied text

    Assert.Equal<bool list>([ false; true; false ], actions |> List.map (fun a -> a.Failed))

[<Fact>]
let ``a response that is nothing but actions still parses`` () =
    // `prose` is trimmed to empty by the service, so the composed text opens
    // with the separator.
    let text = compose "" [ "Created A"; "Created B" ]
    let body, actions = AssistantLogic.splitApplied text

    Assert.Equal("", body.Trim())
    Assert.Equal(2, actions.Length)

[<Fact>]
let ``an ordinary tick line inside the body is NOT an applied change`` () =
    // The block is identified structurally — a trailing run preceded by a blank
    // line. A tick with prose directly above it is body text.
    let text = "Here is a list:\n✓ this is just prose\nand it continues."
    let body, actions = AssistantLogic.splitApplied text

    Assert.Empty(actions)
    Assert.Contains("✓ this is just prose", body)

[<Fact>]
let ``a trailing tick line with no blank line above it is body text, not the block`` () =
    // This is the case the separator rule exists for. The tick line IS at the
    // end, so the trailing-run walk alone would claim it; only the missing blank
    // line above tells the two apart. `AiService` always writes "\n\n" before
    // the block, so prose directly above means this is prose.
    let text = "Here is what I checked:\n✓ the deadline is next Friday"
    let body, actions = AssistantLogic.splitApplied text

    Assert.Empty(actions)
    Assert.Contains("✓ the deadline is next Friday", body)

[<Fact>]
let ``the same line WITH the separator above it is the block`` () =
    let text = "Here is what I checked:\n\n✓ the deadline is next Friday"
    let body, actions = AssistantLogic.splitApplied text

    Assert.Equal(1, actions.Length)
    Assert.Equal("Here is what I checked:", body)

[<Fact>]
let ``a markdown task list is never mistaken for the action block`` () =
    let text = "Steps:\n\n- [ ] first\n- [x] second"
    let body, actions = AssistantLogic.splitApplied text

    Assert.Empty(actions)
    Assert.Contains("- [x] second", body)

[<Fact>]
let ``the action lines never remain in the rendered body`` () =
    let text = compose "The answer." [ "Created A"; failed "B failed" ]
    let body, _ = AssistantLogic.splitApplied text

    Assert.DoesNotContain("✓", body)
    Assert.DoesNotContain(AssistantLogic.failurePrefix, body)
    Assert.Equal("The answer.", body)

[<Fact>]
let ``a CRLF round-trip through the repository does not change the answer`` () =
    let text = compose "Body." [ "Created A"; "Created B" ]
    let crlf = text.Replace("\n", "\r\n")

    let bodyA, actionsA = AssistantLogic.splitApplied text
    let bodyB, actionsB = AssistantLogic.splitApplied crlf

    Assert.Equal(bodyA, bodyB)
    Assert.Equal<string list>(
        actionsA |> List.map (fun a -> a.Description),
        actionsB |> List.map (fun a -> a.Description)
    )

// ---------------------------------------------------------------------------
// 2. live / reloaded equivalence — the round trip §4 asks for
// ---------------------------------------------------------------------------

[<Fact>]
let ``the live outcome and the reloaded message present identically`` () =
    // 1. A provider response with successes and a failure.
    let prose = "I made the changes you asked for."

    let appliedActions =
        [ "Created assignment \"Essay 2\""
          failed "the module CS9999 does not exist"
          "Moved the deadline for \"Report\"" ]

    // 2. The persisted assistant message, composed exactly as the service does.
    let persisted = compose prose appliedActions

    // 3. The live presentation, from the structured outcome.
    let live = AssistantLogic.presentLive appliedActions

    // 4. The reloaded presentation, from the persisted text alone.
    let body, reloaded = AssistantLogic.splitApplied persisted

    // 5. Identical body, count, order and success/failure states.
    Assert.Equal(prose, body)
    Assert.Equal(live.Length, reloaded.Length)

    Assert.Equal<string list>(
        live |> List.map (fun a -> a.Description),
        reloaded |> List.map (fun a -> a.Description)
    )

    Assert.Equal<bool list>(live |> List.map (fun a -> a.Failed), reloaded |> List.map (fun a -> a.Failed))

    // And the heading agrees, which is what the user actually sees.
    Assert.Equal(AssistantLogic.outcomeOf live, AssistantLogic.outcomeOf reloaded)

[<Fact>]
let ``a reply with no actions presents identically both ways`` () =
    let persisted = compose "Just an answer." []
    let body, reloaded = AssistantLogic.splitApplied persisted

    Assert.Equal("Just an answer.", body)
    Assert.Empty(reloaded)
    Assert.Empty(AssistantLogic.presentLive [])

// ---------------------------------------------------------------------------
// 3. the strip's heading
// ---------------------------------------------------------------------------

[<Fact>]
let ``the heading reflects what actually happened`` () =
    let ok = AssistantLogic.presentLive [ "Created A"; "Created B" ]
    let mixed = AssistantLogic.presentLive [ "Created A"; failed "B" ]
    let bad = AssistantLogic.presentLive [ failed "A"; failed "B" ]

    Assert.Equal(AssistantLogic.AppliedOutcome.AllApplied, AssistantLogic.outcomeOf ok)
    Assert.Equal(AssistantLogic.AppliedOutcome.Partial, AssistantLogic.outcomeOf mixed)
    Assert.Equal(AssistantLogic.AppliedOutcome.NoneApplied, AssistantLogic.outcomeOf bad)

    Assert.Equal("Changes applied", AssistantLogic.AppliedOutcome.heading (AssistantLogic.outcomeOf ok))
    Assert.Equal("Some changes applied", AssistantLogic.AppliedOutcome.heading (AssistantLogic.outcomeOf mixed))
    Assert.Equal("Changes could not be applied", AssistantLogic.AppliedOutcome.heading (AssistantLogic.outcomeOf bad))

[<Fact>]
let ``no heading is celebratory, because these are changes to the user's own data`` () =
    for outcome in
        [ AssistantLogic.AppliedOutcome.AllApplied
          AssistantLogic.AppliedOutcome.Partial
          AssistantLogic.AppliedOutcome.NoneApplied ] do
        let heading = (AssistantLogic.AppliedOutcome.heading outcome).ToLowerInvariant()

        for banned in [ "success"; "great"; "done!"; "nice" ] do
            Assert.DoesNotContain(banned, heading)

[<Fact>]
let ``the navigation limitation is stated rather than worked around`` () =
    let note = AssistantLogic.appliedNavigationNote
    Assert.Contains("Timeline", note)
    Assert.Contains("does not link to individual entities", note)
    Assert.Contains("identifiers", note)

// ---------------------------------------------------------------------------
// 4. context honesty
// ---------------------------------------------------------------------------

[<Fact>]
let ``the context scope is exactly what BuildContext sends`` () =
    let labels = AssistantLogic.contextScope |> List.map (fun c -> c.Label.ToLowerInvariant())

    for expected in [ "date and time"; "your name"; "modules"; "open assignments"; "notes"; "today's events"; "this week's events"; "today's sessions" ] do
        Assert.Contains(expected, labels)

    Assert.Equal(8, AssistantLogic.contextScope.Length)

[<Fact>]
let ``the scope never claims to include what BuildContext omits`` () =
    let text =
        (AssistantLogic.contextScope |> List.map (fun c -> c.Label + " " + c.Detail) |> String.concat " ")
            .ToLowerInvariant()

    // Plural forms, because a module's *lecturer* genuinely is sent and must not
    // trip a check aimed at *lectures*.
    for banned in [ "documents"; "lectures"; "transcript"; "flashcard"; "graph"; "inbox" ] do
        Assert.DoesNotContain(banned, text)

[<Fact>]
let ``the excluded list names every area that is genuinely not sent`` () =
    let text = (AssistantLogic.contextExcluded |> String.concat " ").ToLowerInvariant()

    for expected in [ "document"; "lecture"; "transcript"; "flashcard"; "graph"; "inbox"; "completed assignments" ] do
        Assert.Contains(expected, text)

[<Fact>]
let ``open assignments are described as open only`` () =
    let openAssignments = AssistantLogic.contextScope |> List.find (fun c -> c.Label = "Open assignments")
    Assert.Contains("completed work is not sent", openAssignments.Detail)

[<Fact>]
let ``the privacy notice claims no more than the repository supports`` () =
    let text = AssistantLogic.privacyNotice
    Assert.Contains("sent to the configured AI provider", text)

    let lower = text.ToLowerInvariant()

    for banned in [ "encrypt"; "never"; "not retain"; "local only"; "locally"; "private"; "anonym" ] do
        Assert.DoesNotContain(banned, lower)

[<Fact>]
let ``prefilled text is labelled as a prompt, not as attached context`` () =
    let text = AssistantLogic.prefilledNotice.ToLowerInvariant()
    Assert.Contains("prefilled prompt", text)
    Assert.DoesNotContain("context", text)

// ---------------------------------------------------------------------------
// 5. suggested prompts
// ---------------------------------------------------------------------------

[<Fact>]
let ``no suggestion is offered when there is no data at all`` () =
    Assert.Empty(AssistantLogic.suggestions AssistantLogic.ContextFacts.empty)

[<Fact>]
let ``note suggestions appear only when notes exist`` () =
    let withNotes = { AssistantLogic.ContextFacts.empty with Notes = 3 }
    let text = AssistantLogic.suggestions withNotes |> String.concat " "

    Assert.Contains("notes", text)
    Assert.Empty(AssistantLogic.suggestions { AssistantLogic.ContextFacts.empty with Notes = 0 })

[<Fact>]
let ``assignment suggestions appear only when open assignments exist`` () =
    let one = AssistantLogic.suggestions { AssistantLogic.ContextFacts.empty with OpenAssignments = 1 }
    Assert.Contains(one, fun s -> s.Contains "prioritise")

    // The comparison prompt needs more than one to compare.
    Assert.DoesNotContain(one, fun s -> s.Contains "Compare")

    let many = AssistantLogic.suggestions { AssistantLogic.ContextFacts.empty with OpenAssignments = 3 }
    Assert.Contains(many, fun s -> s.Contains "Compare")

[<Fact>]
let ``schedule suggestions appear only when there is schedule data`` () =
    let none' = AssistantLogic.suggestions AssistantLogic.ContextFacts.empty
    Assert.DoesNotContain(none', fun s -> s.Contains "overloaded")

    let withEvents = AssistantLogic.suggestions { AssistantLogic.ContextFacts.empty with EventsThisWeek = 4 }
    Assert.Contains(withEvents, fun s -> s.Contains "overloaded")

[<Fact>]
let ``no suggestion mentions anything the provider never receives`` () =
    let everything =
        { AssistantLogic.Modules = 5
          AssistantLogic.OpenAssignments = 5
          AssistantLogic.Notes = 5
          AssistantLogic.EventsThisWeek = 5
          AssistantLogic.SessionsToday = 5 }

    let text = (AssistantLogic.suggestions everything |> String.concat " ").ToLowerInvariant()

    for banned in [ "document"; "lecture"; "transcript"; "flashcard"; "graph"; "inbox"; "completed" ] do
        Assert.DoesNotContain(banned, text)

// ---------------------------------------------------------------------------
// 6. send eligibility
// ---------------------------------------------------------------------------

[<Fact>]
let ``an empty draft cannot be sent, and the reason says so`` () =
    let reason = AssistantLogic.sendBlockedReason AssistantLogic.ProviderState.Ready false "   "
    Assert.True(reason.IsSome)
    Assert.Contains("Write a message", reason.Value)
    Assert.False(AssistantLogic.canSend AssistantLogic.ProviderState.Ready false "")

[<Fact>]
let ``a draft over the cap cannot be sent, and the reason gives both numbers`` () =
    let long = String.replicate (AssistantLogic.maxMessageChars + 1) "x"
    let reason = AssistantLogic.sendBlockedReason AssistantLogic.ProviderState.Ready false long

    Assert.True(reason.IsSome)
    Assert.Contains(string (AssistantLogic.maxMessageChars + 1), reason.Value)
    Assert.Contains(string AssistantLogic.maxMessageChars, reason.Value)

    // Exactly at the cap is fine.
    Assert.True(AssistantLogic.canSend AssistantLogic.ProviderState.Ready false (String.replicate AssistantLogic.maxMessageChars "x"))

[<Fact>]
let ``the cap matches the request limit the service actually sets`` () =
    Assert.Equal(4096, AssistantLogic.maxTokens)
    Assert.Equal(4096, AssistantLogic.maxMessageChars)

[<Fact>]
let ``a send in flight blocks a second one`` () =
    let reason = AssistantLogic.sendBlockedReason AssistantLogic.ProviderState.Ready true "hello"
    Assert.True(reason.IsSome)
    Assert.Contains("Waiting", reason.Value)

[<Fact>]
let ``an unconfigured provider blocks sending with a visible reason`` () =
    let reason = AssistantLogic.sendBlockedReason AssistantLogic.ProviderState.NotConfigured false "hello"
    Assert.True(reason.IsSome)
    Assert.Contains("No AI provider is configured", reason.Value)

[<Fact>]
let ``a valid draft with a ready provider can be sent`` () =
    Assert.True(AssistantLogic.canSend AssistantLogic.ProviderState.Ready false "hello")
    Assert.Equal(None, AssistantLogic.sendBlockedReason AssistantLogic.ProviderState.Ready false "hello")

[<Fact>]
let ``the counter appears only when it is close enough to matter`` () =
    Assert.Equal(None, AssistantLogic.counterText "short")
    Assert.True((AssistantLogic.counterText (String.replicate 4000 "x")).IsSome)

// ---------------------------------------------------------------------------
// 7. provider state and typed errors
// ---------------------------------------------------------------------------

[<Fact>]
let ``every typed provider error maps to its own sentence`` () =
    let cases =
        [ AiError.NotConfigured
          AiError.Auth "bad key"
          AiError.RateLimited "slow down"
          AiError.Transport "no route"
          AiError.Provider "upstream" ]

    let messages = cases |> List.map AssistantLogic.describeError
    Assert.Equal(messages.Length, (List.distinct messages).Length)

    // A rate limit is only ever reported when the service said so.
    let notConfigured = AssistantLogic.describeError AiError.NotConfigured
    Assert.DoesNotContain("rate", notConfigured.ToLowerInvariant())
    Assert.Contains("Settings", notConfigured)

[<Fact>]
let ``provider state labels never promise streaming`` () =
    for state in
        [ AssistantLogic.ProviderState.Ready
          AssistantLogic.ProviderState.NotConfigured
          AssistantLogic.ProviderState.Failed "x" ] do
        let label = (AssistantLogic.ProviderState.label state).ToLowerInvariant()

        for banned in [ "streaming"; "connecting"; "stopped"; "typing" ] do
            Assert.DoesNotContain(banned, label)

    Assert.True(AssistantLogic.ProviderState.isReady AssistantLogic.ProviderState.Ready)
    Assert.False(AssistantLogic.ProviderState.isReady AssistantLogic.ProviderState.NotConfigured)

// ---------------------------------------------------------------------------
// 8. conversations
// ---------------------------------------------------------------------------

[<Fact>]
let ``the title is described as the opening line, never as a summary`` () =
    let notice = AssistantLogic.titleNotice.ToLowerInvariant()
    Assert.Contains("opening line", notice)
    Assert.DoesNotContain("summar", notice)
    Assert.DoesNotContain("generated", notice)

[<Fact>]
let ``conversations group by how recently they were updated`` () =
    Assert.Equal("Today", AssistantLogic.groupLabel now now)
    Assert.Equal("Yesterday", AssistantLogic.groupLabel now (now.AddDays -1.0))
    Assert.Equal("This week", AssistantLogic.groupLabel now (now.AddDays -4.0))
    Assert.Equal("Older", AssistantLogic.groupLabel now (now.AddDays -30.0))

[<Fact>]
let ``grouping is deterministic, newest first, and drops empty groups`` () =
    let convo title days =
        { Conversation.create (now.AddDays days) title with UpdatedAt = now.AddDays days }

    let all = [ convo "old" -30.0; convo "newer today" -0.1; convo "earlier today" -0.5 ]
    let groups = AssistantLogic.groupConversations now all

    Assert.Equal<string list>([ "Today"; "Older" ], groups |> List.map fst)
    Assert.Equal<string list>([ "newer today"; "earlier today" ], snd groups.Head |> List.map (fun c -> c.Title))

[<Fact>]
let ``conversation search matches the title, case-insensitively`` () =
    let all = [ Conversation.create now "SQL joins"; Conversation.create now "Planner help" ]

    Assert.Equal(2, (AssistantLogic.searchConversations "" all).Length)
    Assert.Equal(1, (AssistantLogic.searchConversations "sql" all).Length)
    Assert.Empty(AssistantLogic.searchConversations "kubernetes" all)

// ---------------------------------------------------------------------------
// 9. scrolling
// ---------------------------------------------------------------------------

[<Fact>]
let ``the view follows when the reader is already at the bottom`` () =
    Assert.True(AssistantLogic.shouldFollow false 0.0)
    Assert.True(AssistantLogic.shouldFollow false (AssistantLogic.followThreshold - 1.0))

[<Fact>]
let ``someone reading earlier messages is never dragged back down`` () =
    Assert.False(AssistantLogic.shouldFollow true 0.0)
    Assert.False(AssistantLogic.shouldFollow false (AssistantLogic.followThreshold + 1.0))

[<Fact>]
let ``jump to latest appears exactly when following is off`` () =
    Assert.False(AssistantLogic.showJumpToLatest false 0.0 true)
    Assert.True(AssistantLogic.showJumpToLatest true 0.0 true)
    Assert.True(AssistantLogic.showJumpToLatest false 500.0 true)

    // Nothing to jump to on an empty conversation.
    Assert.False(AssistantLogic.showJumpToLatest true 500.0 false)

// ---------------------------------------------------------------------------
// 10. composition and responsive placement
// ---------------------------------------------------------------------------

[<Fact>]
let ``the page is the redesign's five sections, in order`` () =
    Assert.Equal<AssistantLogic.Section list>(
        [ AssistantLogic.Section.Header
          AssistantLogic.Section.History
          AssistantLogic.Section.Conversation
          AssistantLogic.Section.Composer
          AssistantLogic.Section.Context ],
        AssistantLogic.sectionOrder
    )

[<Fact>]
let ``the centre runs messages, suggestions, then composer`` () =
    Assert.Equal<AssistantLogic.CentreRegion list>(
        [ AssistantLogic.CentreRegion.Messages
          AssistantLogic.CentreRegion.Suggestions
          AssistantLogic.CentreRegion.Composer ],
        AssistantLogic.centreOrder
    )

[<Fact>]
let ``the context rail leads with provider state and ends with the privacy line`` () =
    Assert.Equal<AssistantLogic.ContextRegion list>(
        [ AssistantLogic.ContextRegion.Provider
          AssistantLogic.ContextRegion.Scope
          AssistantLogic.ContextRegion.Excluded
          AssistantLogic.ContextRegion.Privacy ],
        AssistantLogic.contextOrder
    )

[<Fact>]
let ``at wide width all three panes are present`` () =
    let p = AssistantLogic.placementFor 1600.0 true true
    Assert.True(p.ShowHistory)
    Assert.True(p.ShowContext)
    Assert.False(p.Stacked)

[<Fact>]
let ``at medium width the context rail gives way but history stays`` () =
    let p = AssistantLogic.placementFor 1100.0 true true
    Assert.True(p.ShowHistory)
    Assert.False(p.ShowContext)

[<Fact>]
let ``at narrow width both rails stack with the conversation`` () =
    // Collapsed — which is how `ChatView` leaves them on crossing the
    // breakpoint, so the conversation owns the width.
    let closed = AssistantLogic.placementFor 700.0 false false
    Assert.False(closed.ShowHistory)
    Assert.False(closed.ShowContext)
    Assert.True(closed.Stacked)

    // But neither is *dropped*: opening either brings it back as a stacked
    // section. Narrow must not delete the statement of what is sent.
    let opened = AssistantLogic.placementFor 700.0 true true
    Assert.True(opened.ShowHistory)
    Assert.True(opened.ShowContext)
    Assert.True(opened.Stacked)

[<Fact>]
let ``a collapsed rail keeps a width, so its toggle is never itself hidden`` () =
    let collapsed = { AssistantLogic.ShowHistory = false; AssistantLogic.ShowContext = false; AssistantLogic.Stacked = false }
    Assert.Equal(AssistantLogic.Geometry.railWidth, AssistantLogic.historyColumnWidth collapsed, 6)
    Assert.Equal(AssistantLogic.Geometry.railWidth, AssistantLogic.contextColumnWidth collapsed, 6)

[<Fact>]
let ``the conversation column keeps the redesign's minimum width`` () =
    Assert.Equal(420.0, AssistantLogic.Geometry.centreMinWidth, 6)

    // The centre is genuinely dominant: its minimum out-measures the mean of
    // the two rails, so the conversation can never become the narrow column.
    let railMean = (AssistantLogic.Geometry.historyWidth + AssistantLogic.Geometry.contextWidth) / 2.0
    Assert.True(AssistantLogic.Geometry.centreMinWidth > railMean)

[<Fact>]
let ``the two roles differ by shape as well as colour`` () =
    // The tight corner points at the speaker; colour is not the only cue.
    Assert.NotEqual(AssistantLogic.Geometry.bubbleRadius, AssistantLogic.Geometry.bubbleTightRadius)
    Assert.True(AssistantLogic.Geometry.userBubbleFraction < AssistantLogic.Geometry.assistantBubbleFraction)

[<Fact>]
let ``geometry matches every other migrated screen`` () =
    Assert.Equal(0.5, AssistantLogic.Geometry.borderInset, 6)
    Assert.Equal(0.0, AssistantLogic.Geometry.contentHighlight, 6)
    Assert.Equal(NotesLogic.Geometry.actionMinHeight, AssistantLogic.Geometry.actionMinHeight, 6)
    Assert.Equal(NotesLogic.Geometry.actionRadius, AssistantLogic.Geometry.actionRadius, 6)
    Assert.Equal(GraphLogic.Geometry.borderInset, AssistantLogic.Geometry.borderInset, 6)
