module UniversityOS.Tests.AssistantViewTests

open System
open Xunit
open Avalonia.Media
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Controls.Primitives
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Features

/// The **rendered** Assistant screen.
///
/// `AssistantLogicTests` covers the decisions. This file covers the composition
/// those decisions produce: it builds the real `ChatView.view` tree and asserts
/// what is in it.
///
/// No Avalonia application is started. A FuncUI `IView` is an immutable
/// description — type, attributes, children — so the whole tree can be built and
/// walked with nothing behind it. (`Ui.hand` degrades to `null` without a cursor
/// factory, which is the one platform service the tree would otherwise need.)

// ---------------------------------------------------------------------------
// the harness
// ---------------------------------------------------------------------------

let private accessorName (accessor: Accessor) =
    match accessor with
    | Accessor.InstanceProperty p -> p.Name
    | Accessor.AvaloniaProperty p -> p.Name

/// Every direct child of a node, whether it was set as single content or as a
/// children list.
let private kids (view: IView) : IView list =
    view.Attrs
    |> List.collect (fun attr ->
        match attr.Content with
        | ValueSome content ->
            match content.Content with
            | ViewContent.Single(Some child) -> [ child ]
            | ViewContent.Single None -> []
            | ViewContent.Multiple children -> children
        | ValueNone -> [])

/// The node and everything under it.
let rec private all (view: IView) : IView list =
    view :: (kids view |> List.collect all)

let private propOf (name: string) (view: IView) : obj option =
    view.Attrs
    |> List.tryPick (fun attr ->
        match attr.Property with
        | ValueSome p when accessorName p.Accessor = name -> Some p.Value
        | _ -> None)

let private stringProp name view =
    match propOf name view with
    | Some(:? string as s) -> Some s
    | _ -> None

let private floatProp name view =
    match propOf name view with
    | Some(:? float as f) -> Some f
    | _ -> None

let private intProp name view =
    match propOf name view with
    | Some(:? int as i) -> Some i
    | _ -> None

let private boolProp name view =
    match propOf name view with
    | Some(:? bool as b) -> Some b
    | _ -> None

let private ofType (name: string) (root: IView) =
    all root |> List.filter (fun v -> v.ViewType.Name = name)

/// Every word the screen puts on the glass: `TextBlock.Text`, plus the string
/// content of a Button (`Ui.smallButton` and friends set `Content` directly).
let private words (root: IView) =
    all root
    |> List.collect (fun v -> [ stringProp "Text" v; stringProp "Content" v ] |> List.choose id)

let private says (needle: string) (root: IView) =
    words root |> List.exists (fun s -> s.Contains(needle, StringComparison.Ordinal))

let private countSaying (needle: string) (root: IView) =
    words root |> List.filter (fun s -> s.Contains(needle, StringComparison.Ordinal)) |> List.length

/// Watermarks are the composer's and the search box's placeholder text.
let private watermarks (root: IView) = all root |> List.choose (stringProp "Watermark")

/// Accessible names carry the blocked-Send reason and every rail toggle's
/// purpose, so they are part of what the screen communicates.
let private accessibleNames (root: IView) = all root |> List.choose (stringProp "Name")

// ---------------------------------------------------------------------------
// fixtures
// ---------------------------------------------------------------------------

let private tokens = Tokens.ofAppearance true 0 AppearanceSettings.defaults

let private noServices: AppServices = Unchecked.defaultof<AppServices>

let private modelOf (m, _, _) = m
let private cmdOf (_, c, _) = c
let private externalOf (_, _, e) = e

let private now = DateTime(2026, 8, 1, 14, 0, 0)

let private conversationAt (title: string) (updated: DateTime) : Conversation =
    { Id = ConversationId.New(); Title = title; CreatedAt = updated; UpdatedAt = updated }

let private conversation title = conversationAt title (now.AddHours -1.0)

let private messageIn (c: Conversation) (role: ChatRole) (content: string) : ChatMessage =
    { Id = MessageId.New()
      ConversationId = c.Id
      Role = role
      Content = content
      CreatedAt = now.AddMinutes -5.0 }

let private facts =
    { AssistantLogic.Modules = 4
      AssistantLogic.OpenAssignments = 3
      AssistantLogic.Notes = 12
      AssistantLogic.EventsThisWeek = 5
      AssistantLogic.SessionsToday = 2 }

/// A loaded, provider-ready screen at the wide breakpoint.
let private ready () =
    let model, _ = ChatView.init noServices

    { model with
        Loading = false
        Provider = AssistantLogic.ProviderState.Ready
        AiModel = "claude-opus-5"
        Facts = facts
        Width = 1600.0 }

let private withConversation () =
    let c = conversation "How should I plan revision week?"

    let m =
        { ready () with
            Conversations = [ c ]
            Current = Some c
            Messages =
                [ messageIn c ChatRole.User "How should I plan revision week?"
                  messageIn c ChatRole.Assistant "Start with the modules you are furthest behind on." ] }

    c, m

let private render (model: ChatView.Model) = ChatView.view tokens model ignore

/// An assistant message composed exactly the way `AiService.Chat` composes one.
let private composed (prose: string) (applied: string list) =
    if applied.IsEmpty then
        prose
    else
        prose + "\n\n" + (applied |> List.map (fun a -> AssistantLogic.appliedLinePrefix + a) |> String.concat "\n")

let private assistantSaying (text: string) =
    let c = conversation "Applied"

    { ready () with
        Conversations = [ c ]
        Current = Some c
        Messages = [ messageIn c ChatRole.Assistant text ] }

// ---------------------------------------------------------------------------
// 1. wide composition — three panes, centre dominant
// ---------------------------------------------------------------------------

/// The pane grid: the one Grid whose three children sit in columns 0, 1 and 2.
let private paneGrid (root: IView) =
    all root
    |> List.tryFind (fun v ->
        v.ViewType.Name = "Grid"
        && (let cols = kids v |> List.choose (intProp "Column")
            cols = [ 0; 1; 2 ]))

[<Fact>]
let ``at a wide width the screen is exactly three principal panes`` () =
    let root = render (ready ())

    match paneGrid root with
    | None -> failwith "no three-column pane grid"
    | Some grid -> Assert.Equal(3, (kids grid).Length)

[<Fact>]
let ``the panes run history, conversation, context — left to right`` () =
    let _, model = withConversation ()
    let root = render model
    let grid = (paneGrid root).Value
    let panes = kids grid

    let pane i = panes |> List.item i

    // Column 0 is the history: it is the pane holding the search box and the
    // title notice.
    Assert.Equal(Some 0, intProp "Column" (pane 0))
    Assert.True(says AssistantLogic.titleNotice (pane 0))
    Assert.Contains("Search conversations", watermarks (pane 0))

    // Column 1 is the conversation: the messages and the composer.
    Assert.Equal(Some 1, intProp "Column" (pane 1))
    Assert.True(says "How should I plan revision week?" (pane 1))
    Assert.True(says "Send" (pane 1))

    // Column 2 is the context: what is sent, what is not, and the privacy line.
    Assert.Equal(Some 2, intProp "Column" (pane 2))
    Assert.True(says "WHAT IS SENT" (pane 2))
    Assert.True(says AssistantLogic.privacyNotice (pane 2))

[<Fact>]
let ``the centre column keeps the redesign's 420px floor`` () =
    let root = render (ready ())
    let centre = (paneGrid root).Value |> kids |> List.item 1

    Assert.Equal(Some AssistantLogic.Geometry.centreMinWidth, floatProp "MinWidth" centre)
    Assert.Equal(420.0, AssistantLogic.Geometry.centreMinWidth, 6)

[<Fact>]
let ``the centre is the only column that grows`` () =
    let root = render (ready ())
    let grid = (paneGrid root).Value

    match propOf "ColumnDefinitions" grid with
    | Some(:? ColumnDefinitions as defs) ->
        Assert.Equal(3, defs.Count)
        Assert.False(defs.[0].Width.IsStar)
        Assert.True(defs.[1].Width.IsStar)
        Assert.False(defs.[2].Width.IsStar)
    | other -> failwithf "column definitions were %A" other

[<Fact>]
let ``the composer shares the message column's measure`` () =
    let _, model = withConversation ()
    let root = render model

    // Both the message stack and the composer stack cap at the same width, so
    // the composer cannot sit wider or narrower than the words above it.
    let caps = all root |> List.choose (floatProp "MaxWidth") |> List.distinct
    Assert.Contains(900.0, caps)
    Assert.Equal(2, all root |> List.choose (floatProp "MaxWidth") |> List.filter ((=) 900.0) |> List.length)

// ---------------------------------------------------------------------------
// 2. medium composition — the context rail gives way, nothing is lost
// ---------------------------------------------------------------------------

[<Fact>]
let ``at a medium width the context rail collapses and the history stays`` () =
    let _, model = withConversation ()
    let root = render { model with Width = 1100.0 }
    let panes = (paneGrid root).Value |> kids

    // History intact.
    Assert.True(says AssistantLogic.titleNotice (panes |> List.item 0))

    // Context is a strip, not a panel — but it is still there and still opens.
    Assert.False(says AssistantLogic.privacyNotice (panes |> List.item 2))
    Assert.Contains("Open the context panel", accessibleNames (panes |> List.item 2))

[<Fact>]
let ``the conversation is not compressed at a medium width`` () =
    let _, model = withConversation ()
    let root = render { model with Width = 1100.0 }
    let centre = (paneGrid root).Value |> kids |> List.item 1

    Assert.Equal(Some AssistantLogic.Geometry.centreMinWidth, floatProp "MinWidth" centre)
    Assert.True(says "Send" centre)

    // The message column and the composer keep the same measure at every
    // breakpoint, so the composer never becomes wider or narrower than the words
    // it answers.
    Assert.Equal(2, all centre |> List.choose (floatProp "MaxWidth") |> List.filter ((=) 900.0) |> List.length)

[<Fact>]
let ``a collapsed rail still reserves its own width`` () =
    let placement =
        { AssistantLogic.ShowHistory = false
          AssistantLogic.ShowContext = false
          AssistantLogic.Stacked = false }

    Assert.Equal(AssistantLogic.Geometry.railWidth, AssistantLogic.historyColumnWidth placement, 6)
    Assert.Equal(AssistantLogic.Geometry.railWidth, AssistantLogic.contextColumnWidth placement, 6)
    Assert.True(AssistantLogic.Geometry.railWidth > 0.0)

// ---------------------------------------------------------------------------
// 3. narrow composition — stacked, never deleted
// ---------------------------------------------------------------------------

[<Fact>]
let ``at a narrow width the conversation takes the width and the rails become strips`` () =
    let _, model = withConversation ()

    let narrow =
        ChatView.update noServices (ChatView.Resized 760.0) { model with Width = 1600.0 } |> modelOf

    let root = render narrow

    // No pane grid at all: the columns are gone.
    Assert.True((paneGrid root).IsNone)

    // The conversation is still whole.
    Assert.True(says "How should I plan revision week?" root)
    Assert.True(says "Send" root)

    // And both rails are named strips that open.
    Assert.True(says "HISTORY" root)
    Assert.True(says "CONTEXT" root)
    Assert.Contains("Open the history", accessibleNames root)
    Assert.Contains("Open the context", accessibleNames root)

[<Fact>]
let ``crossing into narrow collapses the rails, and leaving restores them`` () =
    let model = ready ()

    let narrow = ChatView.update noServices (ChatView.Resized 700.0) model |> modelOf
    Assert.False(narrow.HistoryOpen)
    Assert.False(narrow.ContextOpen)

    let wide = ChatView.update noServices (ChatView.Resized 1600.0) narrow |> modelOf
    Assert.True(wide.HistoryOpen)
    Assert.True(wide.ContextOpen)

    // A resize that does not cross the breakpoint leaves the rails alone.
    let stillWide =
        ChatView.update noServices (ChatView.Resized 1400.0) { wide with HistoryOpen = false } |> modelOf

    Assert.False(stillWide.HistoryOpen)

[<Fact>]
let ``a narrow window can still be told exactly what is sent`` () =
    let _, model = withConversation ()

    let opened =
        { model with Width = 760.0; HistoryOpen = false; ContextOpen = true }

    let root = render opened

    Assert.True(says AssistantLogic.privacyNotice root)
    Assert.True(says "WHAT IS SENT" root)
    Assert.True(says "NOT SENT" root)

[<Fact>]
let ``nothing in the screen scrolls sideways`` () =
    for width in [ 1600.0; 1100.0; 760.0 ] do
        let _, model = withConversation ()
        let root = render { model with Width = width; ContextOpen = true; HistoryOpen = true }

        let viewers = ofType "ScrollViewer" root
        Assert.NotEmpty viewers

        for viewer in viewers do
            Assert.Equal(
                Some(box ScrollBarVisibility.Disabled),
                propOf "HorizontalScrollBarVisibility" viewer
            )

[<Fact>]
let ``an applied-change row wraps rather than widening the strip`` () =
    let long =
        "Rescheduled the Distributed Systems coursework deadline from Friday 14 August to Monday 17 August and moved the two revision sessions that clashed with it"

    let root = render (assistantSaying (composed "Done." [ long ]))

    let row = all root |> List.filter (fun v -> stringProp "Text" v = Some long)
    Assert.Single row |> ignore
    Assert.Equal(Some(box TextWrapping.Wrap), propOf "TextWrapping" (List.head row))

[<Fact>]
let ``a long code fence wraps inside its own block instead of overflowing the page`` () =
    let code = String.replicate 40 "someVeryLongIdentifierName."

    let root =
        render (assistantSaying (sprintf "Here it is:\n\n```\n%s\n```\n" code))

    let block = all root |> List.filter (fun v -> stringProp "Text" v = Some code)
    Assert.Single block |> ignore

    // The shared Markdown renderer wraps code rather than scrolling it
    // horizontally. That is a deliberate app-wide choice: it is what keeps the
    // page itself from ever scrolling sideways.
    Assert.Equal(Some(box TextWrapping.Wrap), propOf "TextWrapping" (List.head block))

// ---------------------------------------------------------------------------
// 4. section composition
// ---------------------------------------------------------------------------

[<Fact>]
let ``every section the logic names is actually drawn`` () =
    let _, model = withConversation ()
    let root = render { model with Error = Some "Could not reach the provider: timed out" }

    for section in AssistantLogic.sectionOrder do
        let present =
            match section with
            | AssistantLogic.Section.Header -> says "Assistant" root
            | AssistantLogic.Section.History -> says "HISTORY" root
            | AssistantLogic.Section.Conversation -> says "How should I plan revision week?" root
            | AssistantLogic.Section.Composer -> says "Send" root
            | AssistantLogic.Section.Context -> says "CONTEXT" root

        Assert.True(present, sprintf "%A was not drawn" section)

[<Fact>]
let ``the context rail runs provider, scope, excluded, privacy — in that order`` () =
    let root = render (ready ())

    // Read the rail itself: the privacy line also appears in the welcome state,
    // and ordering the whole page would compare the two.
    let rail = (paneGrid root).Value |> kids |> List.item 2
    let ordered = words rail

    let indexOf (needle: string) =
        ordered |> List.findIndex (fun s -> s.Contains(needle, StringComparison.Ordinal))

    Assert.True(indexOf "PROVIDER" < indexOf "WHAT IS SENT")
    Assert.True(indexOf "WHAT IS SENT" < indexOf "NOT SENT")
    Assert.True(indexOf "NOT SENT" < indexOf AssistantLogic.privacyNotice)

[<Fact>]
let ``the workspace runs messages, suggestions, composer`` () =
    let _, model = withConversation ()
    let ordered = words (render model)

    let indexOf (needle: string) =
        ordered |> List.findIndex (fun s -> s.Contains(needle, StringComparison.Ordinal))

    Assert.Equal<AssistantLogic.CentreRegion list>(
        [ AssistantLogic.CentreRegion.Messages
          AssistantLogic.CentreRegion.Suggestions
          AssistantLogic.CentreRegion.Composer ],
        AssistantLogic.centreOrder
    )

    Assert.True(indexOf "How should I plan revision week?" < indexOf "TRY ASKING")
    Assert.True(indexOf "TRY ASKING" < indexOf "Send")

// ---------------------------------------------------------------------------
// 5. the applied-changes strip, as rendered
// ---------------------------------------------------------------------------

[<Fact>]
let ``an ordinary reply draws no strip`` () =
    let root = render (assistantSaying "You have three open assignments this week.")

    for outcome in
        [ AssistantLogic.AppliedOutcome.AllApplied
          AssistantLogic.AppliedOutcome.Partial
          AssistantLogic.AppliedOutcome.NoneApplied ] do
        Assert.False(says (AssistantLogic.AppliedOutcome.heading outcome) root)

    Assert.False(says "View in Timeline" root)

[<Fact>]
let ``one applied change draws one row under the right heading`` () =
    let root = render (assistantSaying (composed "Done." [ "Created assignment Compiler coursework" ]))

    Assert.True(says "Changes applied" root)
    Assert.True(says "Created assignment Compiler coursework" root)
    Assert.False(says "Some changes applied" root)
    Assert.False(says "Changes could not be applied" root)

[<Fact>]
let ``several applied changes each get their own row`` () =
    let actions =
        [ "Created assignment Compiler coursework"
          "Moved the Networks deadline to 20 August"
          "Added a revision session on Thursday" ]

    let root = render (assistantSaying (composed "All done." actions))

    Assert.True(says "Changes applied" root)

    for action in actions do
        Assert.Equal(1, countSaying action root)

[<Fact>]
let ``a mixed result is called partial and shows both glyphs`` () =
    let root =
        render (
            assistantSaying (
                composed
                    "Here is what happened."
                    [ "Created assignment Compiler coursework"
                      AssistantLogic.failurePrefix + "the module code did not match anything" ]
            )
        )

    Assert.True(says "Some changes applied" root)
    Assert.True(says "Created assignment Compiler coursework" root)
    Assert.True(says "the module code did not match anything" root)

    // Glyph as well as hue: a reader who cannot separate red from green still
    // sees which row failed.
    Assert.Contains("✓", words root)
    Assert.Contains("✕", words root)

[<Fact>]
let ``an all-failed result never claims anything was applied`` () =
    let root =
        render (
            assistantSaying (
                composed
                    "I could not do that."
                    [ AssistantLogic.failurePrefix + "no module called Compilers"
                      AssistantLogic.failurePrefix + "the date could not be read" ]
            )
        )

    Assert.True(says "Changes could not be applied" root)
    Assert.False(says "Changes applied" root)
    Assert.DoesNotContain("✓", words root |> List.filter (fun s -> s = "✓"))

[<Fact>]
let ``the parsed action lines never reach the Markdown body`` () =
    let action = "Created assignment Compiler coursework"
    let root = render (assistantSaying (composed "Done." [ action ]))

    // The composed line itself is gone…
    Assert.False(says (AssistantLogic.appliedLinePrefix + action) root)

    // …and the description survives exactly once, in the strip.
    Assert.Equal(1, countSaying action root)

    // The prose is still there, once.
    Assert.Equal(1, countSaying "Done." root)

[<Fact>]
let ``the strip is drawn exactly once per message`` () =
    let root =
        render (assistantSaying (composed "Done." [ "Created assignment Compiler coursework" ]))

    Assert.Equal(1, countSaying "Changes applied" root)
    Assert.Equal(1, countSaying "View in Timeline" root)

[<Fact>]
let ``two messages with changes get two strips, not one shared strip`` () =
    let c = conversation "Two"

    let model =
        { ready () with
            Conversations = [ c ]
            Current = Some c
            Messages =
                [ messageIn c ChatRole.Assistant (composed "First." [ "Created assignment A" ])
                  messageIn c ChatRole.Assistant (composed "Second." [ "Created assignment B" ]) ] }

    let root = render model
    Assert.Equal(2, countSaying "Changes applied" root)
    Assert.Equal(1, countSaying "Created assignment A" root)
    Assert.Equal(1, countSaying "Created assignment B" root)

[<Fact>]
let ``the strip offers the Timeline and nothing it cannot honour`` () =
    let root =
        render (assistantSaying (composed "Done." [ "Created assignment Compiler coursework" ]))

    Assert.True(says "View in Timeline" root)

    // No per-entity navigation: nothing offers to open the thing that changed,
    // because neither the reply nor the stored text carries its identifier.
    let buttonLabels = ofType "Button" root |> List.choose (stringProp "Content")
    Assert.DoesNotContain("Open Compiler coursework", buttonLabels)
    Assert.DoesNotContain("View assignment", buttonLabels)

    for label in buttonLabels do
        Assert.False(label.Contains("Compiler coursework", StringComparison.Ordinal))

[<Fact>]
let ``the navigation note says why there are no per-entity links`` () =
    Assert.Contains("Timeline", AssistantLogic.appliedNavigationNote)
    Assert.Contains("identifiers", AssistantLogic.appliedNavigationNote)

[<Fact>]
let ``View in Timeline actually navigates to the Timeline`` () =
    let result = ChatView.update noServices (ChatView.GotoPage Page.Timeline) (ready ())
    Assert.Equal(Some(ChatView.Goto Page.Timeline), externalOf result)

[<Fact>]
let ``a reply that is nothing but applied changes still renders`` () =
    let root = render (assistantSaying (composed "" [ "Created assignment Compiler coursework" ]))

    Assert.True(says "Changes applied" root)
    Assert.True(says "Created assignment Compiler coursework" root)

[<Fact>]
let ``a live reply and the same reply reloaded compose the same strip`` () =
    let descriptions =
        [ "Created assignment Compiler coursework"
          AssistantLogic.failurePrefix + "no module called Compilers" ]

    // Live: the outcome carries the descriptions.
    let live = AssistantLogic.presentLive descriptions

    // Reloaded: only the composed text survives.
    let body, reloaded = AssistantLogic.splitApplied (composed "Here is what happened." descriptions)

    Assert.Equal<AssistantLogic.AppliedAction list>(live, reloaded)
    Assert.Equal("Here is what happened.", body)
    Assert.Equal(AssistantLogic.outcomeOf live, AssistantLogic.outcomeOf reloaded)

[<Fact>]
let ``a reply rendered live and the same reply rendered after a reload look the same`` () =
    let descriptions = [ "Created assignment Compiler coursework" ]
    let text = composed "Done." descriptions

    // The view has one path for both: the message text. A live reply is appended
    // to `Messages` exactly as a reloaded one arrives, so rendering the same
    // text twice is the equivalence.
    let c = conversation "Round trip"

    let liveModel =
        { ready () with
            Conversations = [ c ]
            Current = Some c
            Messages = [ messageIn c ChatRole.Assistant text ] }

    // A CRLF round-trip through the repository must not change the answer.
    let reloadedModel =
        { liveModel with
            Messages = [ messageIn c ChatRole.Assistant (text.Replace("\n", "\r\n")) ] }

    Assert.Equal<string list>(words (render liveModel), words (render reloadedModel))

// ---------------------------------------------------------------------------
// 6. context honesty, as rendered
// ---------------------------------------------------------------------------

[<Fact>]
let ``the context rail lists exactly the eight categories that are sent`` () =
    let root = render (ready ())

    Assert.Equal(8, AssistantLogic.contextScope.Length)

    for category in AssistantLogic.contextScope do
        Assert.True(says category.Label root, category.Label)
        Assert.True(says category.Detail root, category.Detail)

[<Fact>]
let ``the eight categories are the ones BuildContext genuinely assembles`` () =
    let labels = AssistantLogic.contextScope |> List.map (fun c -> c.Label)

    Assert.Equal<string list>(
        [ "Date and time"
          "Your name"
          "Modules"
          "Open assignments"
          "Notes"
          "Today's events"
          "This week's events"
          "Today's sessions" ],
        labels
    )

[<Fact>]
let ``the boundary is shown, not merely absent`` () =
    let root = render (ready ())

    Assert.True(says "NOT SENT" root)

    for excluded in AssistantLogic.contextExcluded do
        Assert.True(says excluded root, excluded)

    // Every area the screen must not imply is sent.
    let shown = String.concat " | " (words root)

    for area in [ "Documents"; "Lectures"; "Flashcards"; "Graph"; "Inbox"; "Completed assignments" ] do
        Assert.Contains(area, shown)

[<Fact>]
let ``the privacy line is the one the repository can support`` () =
    let root = render (ready ())
    Assert.True(says AssistantLogic.privacyNotice root)

    let claim = String.concat " " (words root) |> fun s -> s.ToLowerInvariant()

    for overclaim in
        [ "whole database"
          "entire database"
          "all your data"
          "encrypted"
          "end-to-end"
          "not retained"
          "never stored"
          "no retention"
          "stays on your device"
          "processed locally"
          "runs locally" ] do
        Assert.False(claim.Contains overclaim, overclaim)

[<Fact>]
let ``the composer restates what travels with each message`` () =
    let root = render (ready ())
    Assert.True(says "4 modules · 3 open assignments · 12 notes" root)

[<Fact>]
let ``a prefilled prompt is labelled as one and can be cleared`` () =
    let prefilled =
        ChatView.update noServices (ChatView.Prefill "Explain this assignment brief") (ready ()) |> modelOf

    Assert.True(prefilled.Prefilled)
    Assert.Equal("Explain this assignment brief", prefilled.Input)

    let root = render prefilled
    Assert.True(says AssistantLogic.prefilledNotice root)
    Assert.True(says "Clear" root)

    // It is words in a box, never "attached context".
    let shown = String.concat " " (words root) |> fun s -> s.ToLowerInvariant()
    Assert.DoesNotContain("attached", shown)

    let cleared = ChatView.update noServices ChatView.ClearPrefill prefilled |> modelOf
    Assert.Equal("", cleared.Input)
    Assert.False(cleared.Prefilled)
    Assert.False(says AssistantLogic.prefilledNotice (render cleared))

[<Fact>]
let ``editing a prefilled prompt makes it the user's own text`` () =
    let prefilled = ChatView.update noServices (ChatView.Prefill "Explain this") (ready ()) |> modelOf
    let edited = ChatView.update noServices (ChatView.SetInput "Explain this in plain terms") prefilled |> modelOf

    Assert.False(edited.Prefilled)
    Assert.False(says AssistantLogic.prefilledNotice (render edited))

// ---------------------------------------------------------------------------
// 7. the composer and the send lifecycle
// ---------------------------------------------------------------------------

/// The Send control: the one Button whose glass carries the Send label.
let private sendButton (root: IView) =
    ofType "Button" root
    |> List.filter (fun b ->
        all b
        |> List.exists (fun v -> stringProp "Text" v = Some "Send" || stringProp "Text" v = Some "Waiting…"))

[<Fact>]
let ``an empty composer cannot send, and says why`` () =
    let root = render (ready ())

    Assert.True(says "Write a message first." root)
    Assert.Equal(Some false, sendButton root |> List.head |> boolProp "IsEnabled")

[<Fact>]
let ``a written message can be sent, with no reason left on screen`` () =
    let root = render { ready () with Input = "What should I do first?" }

    Assert.False(says "Write a message first." root)
    Assert.True(says "Send this message" (render { ready () with Input = "x" }) |> not)
    Assert.Contains("Send this message", accessibleNames root)
    Assert.Equal(Some true, sendButton root |> List.head |> boolProp "IsEnabled")

[<Fact>]
let ``a message past the cap is refused with its own count`` () =
    let over = String.replicate (AssistantLogic.maxMessageChars + 1) "x"
    let root = render { ready () with Input = over }

    Assert.True(says (sprintf "That is %d characters" over.Length) root)
    Assert.True(says (sprintf "the limit is %d" AssistantLogic.maxMessageChars) root)
    Assert.Equal(Some false, sendButton root |> List.head |> boolProp "IsEnabled")

[<Fact>]
let ``the counter only appears when the cap is close`` () =
    Assert.True((AssistantLogic.counterText (String.replicate 100 "x")).IsNone)
    Assert.True((AssistantLogic.counterText (String.replicate 4000 "x")).IsSome)

    let root = render { ready () with Input = String.replicate 4000 "x" }
    Assert.True(says (sprintf "4000 / %d" AssistantLogic.maxMessageChars) root)

[<Fact>]
let ``a second send while one is in flight is refused`` () =
    let sending = { ready () with Input = "again"; Sending = true }
    let root = render sending

    Assert.True(says "Waiting for the current reply." root)
    Assert.Equal(Some false, sendButton root |> List.head |> boolProp "IsEnabled")

    // And the message really is not sent.
    let after = ChatView.update noServices ChatView.Send sending
    Assert.Equal("again", (modelOf after).Input)
    Assert.True(List.isEmpty (cmdOf after))

[<Fact>]
let ``with no provider the composer is closed and Settings is offered`` () =
    let blocked =
        { ready () with
            Provider = AssistantLogic.ProviderState.NotConfigured
            Input = "anything" }

    let root = render blocked

    Assert.True(says "No AI provider is configured, so nothing can be sent." root)
    Assert.Equal(Some false, sendButton root |> List.head |> boolProp "IsEnabled")
    Assert.True(says "Open Settings" root)
    Assert.True(says "No provider configured" root)

    let after = ChatView.update noServices ChatView.Send blocked
    Assert.Equal("anything", (modelOf after).Input)
    Assert.True(List.isEmpty (cmdOf after))

[<Fact>]
let ``the pending state is a wait, not a fake stream`` () =
    let root = render { ready () with Sending = true }

    Assert.True(says "Waiting for the provider…" root)
    Assert.True(says "Waiting…" root)

    // Nothing that implies a stream, and nothing the service cannot do.
    let shown = String.concat " " (words root) |> fun s -> s.ToLowerInvariant()

    for absent in [ "stop"; "cancel generation"; "generating"; "typing"; "streaming"; "regenerate"; "tokens" ] do
        Assert.DoesNotContain(absent, shown)

[<Fact>]
let ``the composer offers only what the screen can honour`` () =
    let _, model = withConversation ()
    let shown = String.concat " " (words (render model)) |> fun s -> s.ToLowerInvariant()

    for absent in
        [ "attach"; "upload"; "microphone"; "voice"; "record"; "web search"; "browse"; "tools"; "plugins"; "temperature" ] do
        Assert.DoesNotContain(absent, shown)

[<Fact>]
let ``a suggested prompt fills the composer and sends nothing`` () =
    let root = render (ready ())
    Assert.True(says "Help me prioritise my open assignments." root)

    let after =
        ChatView.update noServices (ChatView.UseSuggestion "Help me prioritise my open assignments.") (ready ())

    Assert.Equal("Help me prioritise my open assignments.", (modelOf after).Input)
    Assert.False((modelOf after).Sending)
    Assert.True(List.isEmpty (cmdOf after))
    Assert.True((modelOf after).Messages.IsEmpty)

[<Fact>]
let ``suggestions are gated by the data that is actually sent`` () =
    let empty = { ready () with Facts = AssistantLogic.ContextFacts.empty }
    Assert.False(says "TRY ASKING" (render empty))

    // And the empty state says so rather than showing an empty rail of chips.
    Assert.True(says "There is nothing in the application yet" (render empty))

    // No suggestion can ever mention something outside the sent scope.
    let all = AssistantLogic.suggestions { facts with Modules = 6 } |> String.concat " " |> fun s -> s.ToLowerInvariant()

    for outside in [ "document"; "lecture"; "transcript"; "flashcard"; "graph"; "inbox" ] do
        Assert.DoesNotContain(outside, all)

[<Fact>]
let ``Send clears the composer at once but keeps the words`` () =
    let model = { ready () with Input = "  What should I study today?  " }
    let after = ChatView.update noServices ChatView.Send model |> modelOf

    Assert.Equal("", after.Input)
    Assert.Equal("What should I study today?", after.LastSent)
    Assert.True(after.Sending)
    Assert.True(after.Error.IsNone)

[<Fact>]
let ``a failed reply hands the words back`` () =
    let model = { ready () with Input = "What should I study today?" }
    let sent = ChatView.update noServices ChatView.Send model |> modelOf

    let failed =
        ChatView.update noServices (ChatView.GotReply(Error(AiError.Transport "timed out"))) sent |> modelOf

    Assert.Equal("What should I study today?", failed.Input)
    Assert.False(failed.Sending)
    Assert.Equal(Some(AssistantLogic.describeError (AiError.Transport "timed out")), failed.Error)

    let root = render failed
    Assert.True(says "Could not reach the provider: timed out" root)
    Assert.True(says "Try again" root)

[<Fact>]
let ``a successful reply does not put the words back`` () =
    let c = conversation "Study"
    let model = { ready () with Input = "What should I study today?" }
    let sent = ChatView.update noServices ChatView.Send model |> modelOf

    let outcome =
        { Conversation = c
          UserMessage = messageIn c ChatRole.User "What should I study today?"
          AssistantMessage = messageIn c ChatRole.Assistant "Start with Compilers."
          AppliedActions = [] }

    let ok = ChatView.update noServices (ChatView.GotReply(Ok outcome)) sent |> modelOf

    Assert.Equal("", ok.Input)
    Assert.Equal("", ok.LastSent)
    Assert.False(ok.Sending)
    Assert.Equal(2, ok.Messages.Length)

[<Fact>]
let ``a later failure cannot resurrect an older draft`` () =
    let c = conversation "Study"
    let model = { ready () with Input = "first question" }
    let sent = ChatView.update noServices ChatView.Send model |> modelOf

    let outcome =
        { Conversation = c
          UserMessage = messageIn c ChatRole.User "first question"
          AssistantMessage = messageIn c ChatRole.Assistant "An answer."
          AppliedActions = [] }

    let ok = ChatView.update noServices (ChatView.GotReply(Ok outcome)) sent |> modelOf

    // Something unrelated fails later, with an empty composer.
    let failed =
        ChatView.update noServices (ChatView.GotReply(Error(AiError.RateLimited "slow down"))) ok |> modelOf

    Assert.Equal("", failed.Input)
    Assert.NotEqual<string>("first question", failed.Input)

[<Fact>]
let ``a draft the user has already replaced is not overwritten by a failure`` () =
    let model = { ready () with Input = "first" }
    let sent = ChatView.update noServices ChatView.Send model |> modelOf
    let retyped = ChatView.update noServices (ChatView.SetInput "second") sent |> modelOf

    let failed =
        ChatView.update noServices (ChatView.GotReply(Error(AiError.Transport "no route"))) retyped |> modelOf

    Assert.Equal("second", failed.Input)

[<Fact>]
let ``retrying after a failure does not double the user's message`` () =
    let c = conversation "Retry"
    let model = { ready () with Input = "one question" }

    let failed =
        ChatView.update noServices ChatView.Send model
        |> modelOf
        |> fun m -> ChatView.update noServices (ChatView.GotReply(Error(AiError.Transport "down"))) m |> modelOf

    Assert.True(failed.Messages.IsEmpty)

    // The retry sends the restored draft, and only the outcome adds messages.
    let retried = ChatView.update noServices ChatView.Send failed |> modelOf
    Assert.True(retried.Messages.IsEmpty)

    let outcome =
        { Conversation = c
          UserMessage = messageIn c ChatRole.User "one question"
          AssistantMessage = messageIn c ChatRole.Assistant "An answer."
          AppliedActions = [] }

    let ok = ChatView.update noServices (ChatView.GotReply(Ok outcome)) retried |> modelOf

    Assert.Equal(2, ok.Messages.Length)
    Assert.Equal(1, ok.Messages |> List.filter (fun m -> m.Role = ChatRole.User) |> List.length)

[<Fact>]
let ``each provider failure is described as itself`` () =
    let cases =
        [ AiError.NotConfigured, AssistantLogic.ProviderState.NotConfigured
          AiError.Auth "bad key", AssistantLogic.ProviderState.Failed(AssistantLogic.describeError (AiError.Auth "bad key"))
          AiError.RateLimited "slow", AssistantLogic.ProviderState.Failed(AssistantLogic.describeError (AiError.RateLimited "slow")) ]

    for error, expected in cases do
        let after = ChatView.update noServices (ChatView.GotReply(Error error)) (ready ()) |> modelOf
        Assert.Equal(expected, after.Provider)
        Assert.Equal(Some(AssistantLogic.describeError error), after.Error)

    // A transport failure is never reported as a rate limit.
    Assert.DoesNotContain("rate", (AssistantLogic.describeError (AiError.Transport "socket")).ToLowerInvariant())

// ---------------------------------------------------------------------------
// 8. conversations
// ---------------------------------------------------------------------------

[<Fact>]
let ``the history rail groups by date, in the order the redesign uses`` () =
    let model =
        { ready () with
            Conversations =
                [ conversationAt "Today's question" now
                  conversationAt "Yesterday's question" (now.AddDays -1.0)
                  conversationAt "Midweek question" (now.AddDays -4.0)
                  conversationAt "An old question" (now.AddDays -40.0) ] }

    let root = render model
    let ordered = words root

    let indexOf (needle: string) =
        ordered |> List.findIndex (fun s -> s.Contains(needle, StringComparison.Ordinal))

    // `groupConversations` is given the real clock in the view, so the grouping
    // itself is asserted directly and the rail is asserted to render each group.
    let groups = AssistantLogic.groupConversations now model.Conversations |> List.map fst
    Assert.Equal<string list>([ "Today"; "Yesterday"; "This week"; "Older" ], groups)

    for title in [ "Today's question"; "Yesterday's question"; "Midweek question"; "An old question" ] do
        Assert.True(says title root, title)

    Assert.True(indexOf "Today's question" < indexOf "An old question")

[<Fact>]
let ``an empty group is not drawn`` () =
    let only = [ conversationAt "Just today" now ]
    let groups = AssistantLogic.groupConversations now only

    Assert.Equal<string list>([ "Today" ], groups |> List.map fst)
    Assert.False(says "Yesterday" (render { ready () with Conversations = only }))

[<Fact>]
let ``the rail says what a title actually is`` () =
    let root = render { ready () with Conversations = [ conversation "Anything" ] }

    Assert.True(says AssistantLogic.titleNotice root)
    Assert.Contains("opening line", AssistantLogic.titleNotice)

    // Never called a summary.
    let shown = String.concat " " (words root) |> fun s -> s.ToLowerInvariant()
    Assert.DoesNotContain("summarised title", shown)
    Assert.DoesNotContain("auto-titled", shown)

[<Fact>]
let ``search narrows the rail and says so when nothing matches`` () =
    let model =
        { ready () with Conversations = [ conversation "Revision plan"; conversation "Compiler coursework" ] }

    let searched = ChatView.update noServices (ChatView.SetQuery "compiler") model |> modelOf
    let root = render searched

    Assert.True(says "Compiler coursework" root)
    Assert.False(says "Revision plan" root)

    let nothing = ChatView.update noServices (ChatView.SetQuery "zzzz") model |> modelOf
    Assert.True(says "No conversations match that search." (render nothing))

[<Fact>]
let ``selecting a conversation marks it and asks for its messages`` () =
    let c = conversation "Revision plan"
    let model = { ready () with Conversations = [ c ] }

    let result = ChatView.update noServices (ChatView.SelectConversation c) model
    let after = modelOf result

    Assert.Equal(Some c, after.Current)
    Assert.False(after.UserScrolledUp)
    Assert.False(List.isEmpty (cmdOf result))

[<Fact>]
let ``a selected row recolours without resizing`` () =
    let c = conversation "Revision plan"
    let model = { ready () with Conversations = [ c ] }

    let unselected = render model
    let selected = render { model with Current = Some c }

    let rowBorders (root: IView) =
        ofType "Button" root
        |> List.filter (fun b -> accessibleNames b |> List.exists (fun n -> n.StartsWith("Open conversation", StringComparison.Ordinal)))
        |> List.choose (propOf "BorderThickness")

    Assert.NotEmpty(rowBorders unselected)
    Assert.Equal<obj list>(rowBorders unselected, rowBorders selected)

[<Fact>]
let ``a new conversation empties the workspace without deleting anything`` () =
    let c, model = withConversation ()
    let after = ChatView.update noServices ChatView.NewConversation model |> modelOf

    Assert.True(after.Current.IsNone)
    Assert.True(after.Messages.IsEmpty)
    Assert.Equal<Conversation list>([ c ], after.Conversations)

    // Back to the welcome, not to a blank pane.
    Assert.True(says "Ask about your modules, coursework, notes and schedule" (render after))

[<Fact>]
let ``deleting asks first, and says what will and will not be undone`` () =
    let _, model = withConversation ()

    Assert.True(says "Delete conversation" (render model))
    Assert.False(says "Yes, delete it" (render model))

    let asked = ChatView.update noServices ChatView.RequestDelete model |> modelOf
    let root = render asked

    Assert.True(asked.PendingDelete)
    Assert.True(says "Yes, delete it" root)
    Assert.True(says "Cancel" root)
    Assert.True(says "stay applied" root)

[<Fact>]
let ``a cancelled delete changes nothing`` () =
    let _, model = withConversation ()

    let cancelled =
        ChatView.update noServices ChatView.RequestDelete model
        |> modelOf
        |> fun m -> ChatView.update noServices ChatView.CancelDelete m |> modelOf

    Assert.False(cancelled.PendingDelete)
    Assert.Equal(model.Current, cancelled.Current)
    Assert.Equal(model.Messages.Length, cancelled.Messages.Length)
    Assert.False(says "Yes, delete it" (render cancelled))

[<Fact>]
let ``a confirmed delete clears the pane and reloads the rail`` () =
    let _, model = withConversation ()
    let asked = ChatView.update noServices ChatView.RequestDelete model |> modelOf

    let confirmed = ChatView.update noServices ChatView.ConfirmDelete asked
    Assert.False((modelOf confirmed).PendingDelete)
    Assert.False(List.isEmpty (cmdOf confirmed))

    let deleted = ChatView.update noServices ChatView.DeletedCurrent (modelOf confirmed)
    Assert.True((modelOf deleted).Current.IsNone)
    Assert.True((modelOf deleted).Messages.IsEmpty)
    Assert.False(List.isEmpty (cmdOf deleted))

[<Fact>]
let ``a failure while deleting is reported and loses nothing`` () =
    let _, model = withConversation ()

    let failed =
        ChatView.update noServices (ChatView.Failed(exn "the database is locked")) model |> modelOf

    Assert.Equal(Some "the database is locked", failed.Error)
    Assert.Equal(model.Messages.Length, failed.Messages.Length)
    Assert.Equal(model.Current, failed.Current)
    Assert.False(failed.Loading)
    Assert.False(failed.Sending)
    Assert.True(says "the database is locked" (render failed))

[<Fact>]
let ``a failure while loading keeps the screen usable`` () =
    let model, _ = ChatView.init noServices
    let failed = ChatView.update noServices (ChatView.Failed(exn "offline")) model |> modelOf

    Assert.False(failed.Loading)
    Assert.True(says "offline" (render failed))
    Assert.True(says "Try again" (render failed))

[<Fact>]
let ``with no conversations the rail explains itself`` () =
    let root = render (ready ())
    Assert.True(says "No conversations yet. Ask something and one is saved automatically." root)

[<Fact>]
let ``an empty conversation shows the welcome, not an answer`` () =
    let root = render (ready ())

    Assert.True(says "Ask about your modules, coursework, notes and schedule" root)
    Assert.True(says AssistantLogic.privacyNotice root)

[<Fact>]
let ``the rail offers nothing the repository cannot do`` () =
    let _, model = withConversation ()
    let shown = String.concat " " (words (render model)) |> fun s -> s.ToLowerInvariant()

    for absent in [ "pin"; "rename"; "archive"; "export"; "share" ] do
        Assert.DoesNotContain(absent, shown)

// ---------------------------------------------------------------------------
// 9. messages and Markdown
// ---------------------------------------------------------------------------

let private bubbleBorders (root: IView) =
    all root
    |> List.filter (fun v ->
        v.ViewType.Name = "Border"
        && (propOf "CornerRadius" v |> Option.isSome)
        && (propOf "Padding" v
            |> Option.map (fun p -> p.ToString().Contains "18")
            |> Option.defaultValue false))

[<Fact>]
let ``the two roles are told apart by shape and side, not only by fill`` () =
    let c = conversation "Roles"

    let model =
        { ready () with
            Conversations = [ c ]
            Current = Some c
            Messages =
                [ messageIn c ChatRole.User "A question"
                  messageIn c ChatRole.Assistant "An answer" ] }

    let root = render model

    let stacks =
        all root
        |> List.filter (fun v ->
            v.ViewType.Name = "StackPanel"
            && (propOf "HorizontalAlignment" v |> Option.isSome)
            && (all v |> List.exists (fun c -> stringProp "Text" c = Some "A question" || says "An answer" c)))

    let alignments = stacks |> List.choose (propOf "HorizontalAlignment") |> List.distinct

    Assert.Contains(box HorizontalAlignment.Right, alignments)
    Assert.Contains(box HorizontalAlignment.Left, alignments)

    // The corner that points at the speaker is a different radius from the rest.
    Assert.NotEqual(AssistantLogic.Geometry.bubbleRadius, AssistantLogic.Geometry.bubbleTightRadius)

[<Fact>]
let ``a user message keeps its own tighter measure`` () =
    Assert.True(AssistantLogic.Geometry.userBubbleFraction < AssistantLogic.Geometry.assistantBubbleFraction)
    Assert.Equal(0.64, AssistantLogic.Geometry.userBubbleFraction, 6)
    Assert.Equal(0.78, AssistantLogic.Geometry.assistantBubbleFraction, 6)

[<Fact>]
let ``a user message is never run through the Markdown renderer`` () =
    let c = conversation "Literal"

    let raw = "# not a heading\n- not a bullet"

    let model =
        { ready () with
            Conversations = [ c ]
            Current = Some c
            Messages = [ messageIn c ChatRole.User raw ] }

    // The user's own text is shown as written; only the assistant's reply is
    // structured Markdown.
    Assert.True(says raw (render model))

[<Fact>]
let ``assistant Markdown renders its structures`` () =
    let source =
        String.concat
            "\n"
            [ "## Revision plan"
              ""
              "- Compilers first"
              "- Networks second"
              ""
              "1. Read the notes"
              ""
              "| Module | Due |"
              "| --- | --- |"
              "| Compilers | Friday |"
              ""
              "```"
              "let x = 1"
              "```"
              ""
              "See https://example.org/a/very/long/path/that/keeps/going/for/a/while" ]

    let root = render (assistantSaying source)

    Assert.True(says "Revision plan" root)
    Assert.True(says "Compilers first" root)
    Assert.True(says "Read the notes" root)
    Assert.True(says "Module" root)
    Assert.True(says "let x = 1" root)
    Assert.True(says "https://example.org/a/very/long/path" root)

[<Fact>]
let ``long unbroken text wraps rather than pushing the page sideways`` () =
    let long = String.replicate 60 "unbreakableword"
    let root = render (assistantSaying long)

    let block = all root |> List.filter (fun v -> stringProp "Text" v = Some long)
    Assert.NotEmpty block
    Assert.Equal(Some(box TextWrapping.Wrap), propOf "TextWrapping" (List.head block))

[<Fact>]
let ``there is no copy action, because the screen does not have one`` () =
    let root = render (assistantSaying "```\nlet x = 1\n```")

    // The shared Markdown renderer has no clipboard affordance. Rather than
    // draw a button that does nothing, the screen draws none — and this test
    // exists so that a future copy action arrives with its own behaviour.
    let shown = String.concat " " (words root) |> fun s -> s.ToLowerInvariant()
    Assert.DoesNotContain("copy", shown)

[<Fact>]
let ``every message carries when it was said`` () =
    let _, model = withConversation ()
    let root = render model

    // `Format.relativeTime` is what the bubble prints; asserting it is present
    // rather than asserting a wording that belongs to Format.
    Assert.True(words root |> List.exists (fun s -> s = Format.relativeTime DateTime.Now (now.AddMinutes -5.0)))

// ---------------------------------------------------------------------------
// 10. scrolling
// ---------------------------------------------------------------------------

[<Fact>]
let ``a freshly opened conversation starts at the latest message`` () =
    let c = conversation "Long one"

    let loaded =
        ChatView.update
            noServices
            (ChatView.MessagesLoaded [ messageIn c ChatRole.User "one"; messageIn c ChatRole.Assistant "two" ])
            { ready () with UserScrolledUp = true; DistanceFromBottom = 900.0 }
        |> modelOf

    Assert.False(loaded.UserScrolledUp)
    Assert.Equal(0.0, loaded.DistanceFromBottom, 6)
    Assert.True(AssistantLogic.shouldFollow loaded.UserScrolledUp loaded.DistanceFromBottom)

[<Fact>]
let ``following continues while the reader is near the bottom`` () =
    Assert.True(AssistantLogic.shouldFollow false 0.0)
    Assert.True(AssistantLogic.shouldFollow false AssistantLogic.followThreshold)
    Assert.False(AssistantLogic.shouldFollow false (AssistantLogic.followThreshold + 1.0))

[<Fact>]
let ``someone who has scrolled up is never dragged back down`` () =
    let scrolled =
        ChatView.update noServices (ChatView.ScrollChanged(600.0, true)) (ready ()) |> modelOf

    Assert.True(scrolled.UserScrolledUp)
    Assert.False(AssistantLogic.shouldFollow scrolled.UserScrolledUp scrolled.DistanceFromBottom)

    // Even a completed reply does not move them.
    let c = conversation "Reply"

    let outcome =
        { Conversation = c
          UserMessage = messageIn c ChatRole.User "q"
          AssistantMessage = messageIn c ChatRole.Assistant "a"
          AppliedActions = [] }

    let replied = ChatView.update noServices (ChatView.GotReply(Ok outcome)) scrolled |> modelOf
    Assert.True(replied.UserScrolledUp)
    Assert.False(AssistantLogic.shouldFollow replied.UserScrolledUp replied.DistanceFromBottom)

[<Fact>]
let ``Jump to latest appears exactly when following is off`` () =
    let _, model = withConversation ()

    Assert.False(says "Jump to latest" (render model))

    let scrolled = { model with UserScrolledUp = true; DistanceFromBottom = 700.0 }
    Assert.True(says "Jump to latest" (render scrolled))

    // Nothing to jump to when there is nothing to read.
    Assert.False(AssistantLogic.showJumpToLatest true 700.0 false)
    Assert.False(says "Jump to latest" (render { ready () with UserScrolledUp = true }))

[<Fact>]
let ``Jump to latest puts the reader back in following`` () =
    let _, model = withConversation ()

    let jumped =
        ChatView.update noServices ChatView.JumpToLatest { model with UserScrolledUp = true; DistanceFromBottom = 700.0 }
        |> modelOf

    Assert.False(jumped.UserScrolledUp)
    Assert.Equal(0.0, jumped.DistanceFromBottom, 6)
    Assert.True(AssistantLogic.shouldFollow jumped.UserScrolledUp jumped.DistanceFromBottom)
    Assert.False(says "Jump to latest" (render jumped))

[<Fact>]
let ``the applied strip is inside the scrolled extent, so it counts toward the bottom`` () =
    let root = render (assistantSaying (composed "Done." [ "Created assignment Compiler coursework" ]))

    let viewers = ofType "ScrollViewer" root
    let stripInside = viewers |> List.exists (fun v -> says "Changes applied" v)

    Assert.True(stripInside)

[<Fact>]
let ``switching conversations returns the reader to the latest message`` () =
    let c = conversation "Other"
    let _, model = withConversation ()

    let switched =
        ChatView.update noServices (ChatView.SelectConversation c) { model with UserScrolledUp = true }
        |> modelOf

    Assert.False(switched.UserScrolledUp)

[<Fact>]
let ``sending follows your own message, and the pending bubble with it`` () =
    let _, model = withConversation ()

    let sent =
        ChatView.update noServices ChatView.Send { model with Input = "next"; UserScrolledUp = true }
        |> modelOf

    // Sending is a deliberate act, so the screen follows it — but only because
    // the reader asked for it, never because a reply arrived on its own.
    Assert.False(sent.UserScrolledUp)
    Assert.True(sent.Sending)
    Assert.True(says "Waiting for the provider…" (render sent))

// ---------------------------------------------------------------------------
// 11. Dark and Light, every accent
// ---------------------------------------------------------------------------

let private themeModes = [ ThemeMode.Dark; ThemeMode.Light ]
let private accentKeys = Palette.accents |> List.map fst

let private washedBy (alphaHex: string) (colour: Color) (surface: Color) =
    let a = Int32.Parse(alphaHex, Globalization.NumberStyles.HexNumber)
    Palette.composite surface (Color.FromArgb(byte a, colour.R, colour.G, colour.B))

let private eachSurface (f: ThemeMode -> string -> Surfaces -> Color -> unit) =
    for mode in themeModes do
        for key in accentKeys do
            let accent = Palette.ofKey key
            let s = Surfaces.resolve mode accent
            f mode key s accent.Normal

[<Fact>]
let ``a selected conversation row keeps its title readable on the accent wash`` () =
    eachSurface (fun mode key s accent ->
        let wash = washedBy AssistantLogic.Geometry.selectedRowAlpha accent s.Card
        let ratio = Palette.contrastRatio s.Text wash

        Assert.True(ratio >= Palette.aaText, sprintf "%A/%s selected row: %.2f" mode key ratio))

[<Fact>]
let ``a user bubble's words are readable on the accent wash it is painted with`` () =
    eachSurface (fun mode key s accent ->
        let wash = washedBy AssistantLogic.Geometry.userBubbleAlpha accent s.Card
        let ratio = Palette.contrastRatio s.Text wash

        Assert.True(ratio >= Palette.aaText, sprintf "%A/%s user bubble: %.2f" mode key ratio))

[<Fact>]
let ``a user bubble's edge is visible against the surface behind it`` () =
    eachSurface (fun mode key s accent ->
        let edge = washedBy AssistantLogic.Geometry.userBubbleEdgeAlpha accent s.Card
        let ratio = Palette.contrastRatio edge s.Card

        // A hairline is non-text: it must be seen, not read.
        Assert.True(ratio >= 1.1, sprintf "%A/%s user bubble edge: %.2f" mode key ratio))

[<Fact>]
let ``an assistant bubble and a code block both carry readable ink`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            Assert.True(
                Palette.contrastRatio s.Text s.Card >= Palette.aaText,
                sprintf "%A/%s assistant bubble: %.2f" mode key (Palette.contrastRatio s.Text s.Card)
            )

            // The Markdown renderer paints code on the alternate surface.
            Assert.True(
                Palette.contrastRatio s.Text s.CardAlt >= Palette.aaText,
                sprintf "%A/%s code block: %.2f" mode key (Palette.contrastRatio s.Text s.CardAlt)
            )

[<Fact>]
let ``a context chip is readable in both its states`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, raw in [ "connected", s.Success; "warning", s.Warning ] do
                let wash = washedBy AssistantLogic.Geometry.chipFillAlpha raw s.Card
                let ink = Palette.readableOn wash s.Text raw

                Assert.True(
                    Palette.contrastRatio ink wash >= Palette.aaText,
                    sprintf "%A/%s %s chip: %.2f" mode key name (Palette.contrastRatio ink wash)
                )

[<Fact>]
let ``the applied strip's heading is readable in all three outcomes`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            let byOutcome =
                [ AssistantLogic.AppliedOutcome.AllApplied, s.Success
                  AssistantLogic.AppliedOutcome.Partial, s.Warning
                  AssistantLogic.AppliedOutcome.NoneApplied, s.Danger ]

            for outcome, raw in byOutcome do
                // Painted on CardAlt at `appliedFillAlpha`, and measured on the
                // same composite. One constant does both jobs.
                let wash = washedBy AssistantLogic.Geometry.appliedFillAlpha raw s.CardAlt
                let ink = Palette.readableOn wash s.Text raw

                Assert.True(
                    Palette.contrastRatio ink wash >= Palette.aaText,
                    sprintf "%A/%s %A heading: %.2f" mode key outcome (Palette.contrastRatio ink wash)
                )

                // The row glyph sits on the card itself, not on the wash.
                let glyph = Palette.readableOn s.CardAlt s.Text raw

                Assert.True(
                    Palette.contrastRatio glyph s.CardAlt >= Palette.aaText,
                    sprintf "%A/%s %A glyph: %.2f" mode key outcome (Palette.contrastRatio glyph s.CardAlt)
                )

[<Fact>]
let ``the applied strip's edge is visible without being a second border`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for raw in [ s.Success; s.Warning; s.Danger ] do
                let fill = washedBy AssistantLogic.Geometry.appliedFillAlpha raw s.CardAlt
                let edge = washedBy AssistantLogic.Geometry.appliedEdgeAlpha raw s.CardAlt

                Assert.True(Palette.contrastRatio edge fill > 1.0, sprintf "%A/%s strip edge" mode key)

[<Fact>]
let ``the error banner's words are readable on its own wash`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)
            let wash = washedBy AssistantLogic.Geometry.errorFillAlpha s.Danger s.Card
            let ink = Palette.readableOn wash s.Text s.Danger

            Assert.True(
                Palette.contrastRatio ink wash >= Palette.aaText,
                sprintf "%A/%s error banner: %.2f" mode key (Palette.contrastRatio ink wash)
            )

[<Fact>]
let ``the delete confirmation is readable, and is not the error banner's wash`` () =
    // Two different jobs, two different alphas — each used for both painting and
    // measuring.
    Assert.NotEqual<string>(AssistantLogic.Geometry.confirmFillAlpha, AssistantLogic.Geometry.errorFillAlpha)

    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)
            let wash = washedBy AssistantLogic.Geometry.confirmFillAlpha s.Danger s.Card

            Assert.True(
                Palette.contrastRatio s.Text wash >= Palette.aaText,
                sprintf "%A/%s delete confirm: %.2f" mode key (Palette.contrastRatio s.Text wash)
            )

[<Fact>]
let ``the hover veil is felt without washing the glass out`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)
            let veiled = washedBy AssistantLogic.Geometry.veilAlpha s.Text s.Action

            let shift = Palette.contrastRatio veiled s.Action
            Assert.True(shift > 1.0, sprintf "%A/%s veil is invisible" mode key)
            Assert.True(shift < 2.0, sprintf "%A/%s veil is a wash-out: %.2f" mode key shift)

[<Fact>]
let ``the surfaces stay warm in both themes`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, c in [ "card", s.Card; "cardAlt", s.CardAlt; "action", s.Action ] do
                // Warm neutral: never a cool blue-grey cast.
                Assert.True(int c.R >= int c.B, sprintf "%A/%s %s is cool: %A" mode key name c)

[<Fact>]
let ``a disabled Send still says what it is`` () =
    // The label does not change to a symbol, and the reason is written beside
    // it rather than being carried by opacity alone.
    let root = render (ready ())

    Assert.True(says "Send" root)
    Assert.True(says "Write a message first." root)

// ---------------------------------------------------------------------------
// 12. mutation guards
// ---------------------------------------------------------------------------
//
// Each of these fails if the named change is made to the source. They are the
// tests that would have caught the six view-level mutations.

[<Fact>]
let ``MUTATION rendering the strip twice is caught`` () =
    let root = render (assistantSaying (composed "Done." [ "Created assignment Compiler coursework" ]))

    // Counting, not presence: a second `appliedStrip` call renders a second
    // heading, a second row and a second Timeline action.
    Assert.Equal(1, countSaying "Changes applied" root)
    Assert.Equal(1, countSaying "Created assignment Compiler coursework" root)
    Assert.Equal(1, countSaying "View in Timeline" root)

[<Fact>]
let ``MUTATION leaving the action lines in the Markdown body is caught`` () =
    let action = "Created assignment Compiler coursework"
    let text = composed "Done." [ action ]
    let root = render (assistantSaying text)

    // Passing `message.Content` to `Ui.markdown` instead of the parsed body puts
    // the raw "✓ " line back on screen, and doubles the description.
    Assert.False(says (AssistantLogic.appliedLinePrefix + action) root)
    Assert.Equal(1, countSaying action root)

[<Fact>]
let ``MUTATION dropping Page.Chat from the glass migration is caught`` () =
    Assert.True(Page.usesGlassSurface Page.Chat)

    // Every migrated screen, so removing one is visible here too.
    for page in
        [ Page.Timeline
          Page.Dashboard
          Page.Planner
          Page.Inbox
          Page.Assignments
          Page.Modules
          Page.Lectures
          Page.Notes
          Page.Documents
          Page.Graph
          Page.Chat ] do
        Assert.True(Page.usesGlassSurface page, sprintf "%A lost its glass surface" page)

[<Fact>]
let ``MUTATION enabling Send with no provider is caught`` () =
    let blocked =
        { ready () with
            Provider = AssistantLogic.ProviderState.NotConfigured
            Input = "a perfectly good message" }

    Assert.False(AssistantLogic.canSend blocked.Provider blocked.Sending blocked.Input)
    Assert.Equal(Some false, sendButton (render blocked) |> List.head |> boolProp "IsEnabled")

    // Even a failed provider, which still has a key, cannot send until it is
    // reachable again — the screen would otherwise offer an action it cannot do.
    let failed = { blocked with Provider = AssistantLogic.ProviderState.Failed "down" }
    Assert.False(AssistantLogic.canSend failed.Provider failed.Sending failed.Input)
    Assert.Equal(Some false, sendButton (render failed) |> List.head |> boolProp "IsEnabled")

[<Fact>]
let ``MUTATION a suggestion that submits instead of populating is caught`` () =
    let prompt = "Help me prioritise my open assignments."
    let after = ChatView.update noServices (ChatView.UseSuggestion prompt) (ready ())

    Assert.Equal(prompt, (modelOf after).Input)
    Assert.False((modelOf after).Sending)
    Assert.True(List.isEmpty (cmdOf after))
    Assert.True((modelOf after).Messages.IsEmpty)
    Assert.True((modelOf after).LastSent = "")

[<Fact>]
let ``MUTATION disabling or inverting the responsive rail movement is caught`` () =
    // Wide: both rails beside the conversation.
    let wide = AssistantLogic.placementFor 1600.0 true true
    Assert.True(wide.ShowHistory && wide.ShowContext && not wide.Stacked)

    // Medium: the context rail gives way first, never the history.
    let medium = AssistantLogic.placementFor 1100.0 true true
    Assert.True(medium.ShowHistory)
    Assert.False(medium.ShowContext)
    Assert.False(medium.Stacked)

    // Narrow: stacked, and still reachable.
    let narrow = AssistantLogic.placementFor 700.0 true true
    Assert.True(narrow.Stacked)
    Assert.True(narrow.ShowHistory && narrow.ShowContext)

    // The rendered tree really changes with the breakpoint.
    let _, model = withConversation ()
    Assert.True((paneGrid (render { model with Width = 1600.0 })).IsSome)
    Assert.True((paneGrid (render { model with Width = 1100.0 })).IsSome)
    Assert.True((paneGrid (render { model with Width = 700.0 })).IsNone)

    // And the context rail is present at wide, a strip at medium, stacked at
    // narrow — never simply deleted.
    Assert.True(says AssistantLogic.privacyNotice (render { model with Width = 1600.0 }))
    Assert.Contains("Open the context panel", accessibleNames (render { model with Width = 1100.0 }))
    Assert.True(says AssistantLogic.privacyNotice (render { model with Width = 700.0; ContextOpen = true }))

// ---------------------------------------------------------------------------
// 13. performance fixtures
// ---------------------------------------------------------------------------
//
// What is measured is what this harness can honestly measure: the cost of
// BUILDING the view tree for a given conversation, its node count, and the bytes
// allocated doing it. That is the part of a frame this screen owns.
//
// It is NOT a frame rate. No Avalonia application is running, nothing is
// measured, arranged or rasterised here, so no FPS figure is claimed. The shell
// sweep on the published build is where render cost is measured.

let private longMarkdown =
    String.concat
        "\n\n"
        [ "## A long answer"
          String.replicate 40 "This is an ordinary sentence of prose. "
          "- " + String.replicate 20 "a bullet point "
          "- " + String.replicate 20 "another bullet point "
          "1. " + String.replicate 20 "a numbered step "
          "| Module | Deadline | Status |"
          "| --- | --- | --- |"
          "| Compilers | Friday | Open |"
          "| Networks | Monday | Open |"
          String.replicate 40 "And a closing paragraph that keeps going. " ]

let private largeCodeBlock =
    "Here is the whole file:\n\n```\n"
    + (List.init 400 (fun i -> sprintf "let line%d = compute (line%d) |> normalise |> persist" i (max 0 (i - 1)))
       |> String.concat "\n")
    + "\n```\n"

/// Median of `runs`, after a warm-up — a single first build pays for JIT and
/// every static initialiser in the design system.
let private measure (runs: int) (build: unit -> IView) =
    build () |> ignore
    GC.Collect()
    GC.WaitForPendingFinalizers()

    let before = GC.GetAllocatedBytesForCurrentThread()
    let times = ResizeArray<float>()
    let mutable nodes = 0

    for _ in 1..runs do
        let sw = Diagnostics.Stopwatch.StartNew()
        let tree = build ()
        sw.Stop()
        nodes <- (all tree).Length
        times.Add sw.Elapsed.TotalMilliseconds

    let allocated = GC.GetAllocatedBytesForCurrentThread() - before
    let sorted = times |> Seq.sort |> Seq.toArray
    sorted.[sorted.Length / 2], nodes, float allocated / float runs / 1024.0

let private manyMessages (count: int) =
    let c = conversation "A long conversation"

    { ready () with
        Conversations = [ c ]
        Current = Some c
        Messages =
            [ for i in 1..count do
                  if i % 2 = 1 then
                      messageIn c ChatRole.User (sprintf "Question number %d about my coursework." i)
                  else
                      messageIn c ChatRole.Assistant (sprintf "### Answer %d\n\n- one point\n- another point" i) ] }

[<Fact>]
let ``Assistant fixtures build inside their budget`` () =
    let manyConversations =
        [ for i in 1..200 ->
            conversationAt (sprintf "Conversation number %d about module %d" i (i % 7)) (now.AddDays(float -i / 4.0)) ]

    let fixtures: (string * (unit -> ChatView.Model)) list =
        [ "empty conversation", fun () -> ready ()
          "10 messages", fun () -> manyMessages 10
          "100 messages", fun () -> manyMessages 100
          "long Markdown response", fun () -> assistantSaying longMarkdown
          "large code block", fun () -> assistantSaying largeCodeBlock
          "multiple applied actions",
          fun () ->
              assistantSaying (
                  composed "Done." [ for i in 1..12 -> sprintf "Created assignment number %d" i ]
              )
          "mixed success and failure actions",
          fun () ->
              assistantSaying (
                  composed
                      "Here is what happened."
                      [ for i in 1..12 ->
                          if i % 3 = 0 then
                              AssistantLogic.failurePrefix + sprintf "could not read date %d" i
                          else
                              sprintf "Created assignment number %d" i ]
              )
          "many context records",
          fun () ->
              { ready () with
                  Facts =
                      { AssistantLogic.Modules = 40
                        AssistantLogic.OpenAssignments = 120
                        AssistantLogic.Notes = 900
                        AssistantLogic.EventsThisWeek = 60
                        AssistantLogic.SessionsToday = 18 } }
          "pending response", fun () -> { manyMessages 20 with Sending = true }
          "conversation search over 200 titles",
          fun () -> { ready () with Conversations = manyConversations; Query = "module 3" } ]

    let report = System.Text.StringBuilder()
    report.AppendLine "Assistant view-construction fixtures" |> ignore
    report.AppendLine "fixture | median build ms | nodes | KB per build" |> ignore

    let mutable worst = 0.0

    for name, makeModel in fixtures do
        let model = makeModel ()
        let ms, nodes, kb = measure 20 (fun () -> render model)
        worst <- max worst ms
        report.AppendLine(sprintf "%-38s | %8.3f | %6d | %9.1f" name ms nodes kb) |> ignore

        // A generous ceiling: this is a regression guard, not a benchmark. A
        // single build must stay far inside one 60 Hz frame even on a slow
        // machine, because the shell rebuilds it on every model change.
        Assert.True(ms < 60.0, sprintf "%s took %.2f ms to build" name ms)

    IO.File.WriteAllText(
        IO.Path.Combine(IO.Path.GetTempPath(), "assistant-fixtures.txt"),
        report.ToString()
    )

    Assert.True(worst < 60.0)
