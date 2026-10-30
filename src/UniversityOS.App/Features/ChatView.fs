module UniversityOS.App.Features.ChatView

open System
open Elmish
// `Avalonia` itself, for `CornerRadius` — the two roles are told apart by corner
// shape as well as fill, so the asymmetric radius is load-bearing.
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Controls

/// The Assistant.
///
/// Three panes: a conversation history rail, a dominant conversation workspace,
/// and a context rail that states exactly what the provider receives. Every
/// decision lives in `AssistantLogic`; this file draws what that module resolves.
///
/// **The provider returns a whole response, not a stream.** There is no typing
/// animation and no Stop button, because neither would be true.
///
/// **Applied changes are parsed, never re-rendered.** `AiService.Chat` appends
/// each applied change to the assistant message text as a `"✓ "` line;
/// `AssistantLogic.splitApplied` takes them back out, so the body renders once
/// and the strip renders once — for live replies and reloaded ones alike.

// ---------------------------------------------------------------------------
// model
// ---------------------------------------------------------------------------

type Model =
    { Conversations: Conversation list
      Current: Conversation option
      Messages: ChatMessage list
      Input: string
      /// The text of the last send, so a recoverable failure can put the user's
      /// words back in the composer. `Send` clears `Input` immediately, which is
      /// the established behaviour and is preserved.
      LastSent: string
      /// Whether `Input` arrived from another screen rather than being typed.
      Prefilled: bool
      Query: string
      Sending: bool
      Provider: AssistantLogic.ProviderState
      AiModel: string
      Facts: AssistantLogic.ContextFacts
      HistoryOpen: bool
      ContextOpen: bool
      PendingDelete: bool
      UserScrolledUp: bool
      DistanceFromBottom: float
      Width: float
      Loading: bool
      Error: string option }

type External = Goto of Page

type Msg =
    | Load
    | Loaded of Conversation list * bool * string * AssistantLogic.ContextFacts
    | Failed of exn
    | SelectConversation of Conversation
    | MessagesLoaded of ChatMessage list
    | NewConversation
    | RequestDelete
    | CancelDelete
    | ConfirmDelete
    | DeletedCurrent
    | SetInput of string
    | Prefill of string
    | ClearPrefill
    | UseSuggestion of string
    | SetQuery of string
    | ToggleHistory
    | ToggleContext
    | Send
    | GotReply of Result<ChatOutcome, AiError>
    | ScrollChanged of float * bool
    | JumpToLatest
    | Resized of float
    | GotoPage of Page

// ---------------------------------------------------------------------------
// loading
// ---------------------------------------------------------------------------

/// One command, one snapshot.
///
/// The counts loaded here are **only** the categories `AiService.BuildContext`
/// genuinely sends. Nothing else is read, because nothing else is shown.
let private loadCmd (services: AppServices) =
    Elm.ofAsync
        (async {
            let now = services.Clock.Now
            let! conversations = services.Chat.GetConversations()
            let! available = services.Ai.IsAvailable()
            let! settings = services.Settings.Load()
            let! modules = services.Modules.GetAll()
            let! openAssignments = services.Assignments.GetOpen()
            let! notes = services.Notes.GetAll()
            let! weekEvents = services.Events.GetBetween(now.Date, now.Date.AddDays 7.0)
            let! todaySessions = services.Sessions.GetBetween(now.Date, now.Date.AddDays 1.0)

            let facts =
                { AssistantLogic.Modules = modules.Length
                  AssistantLogic.OpenAssignments = openAssignments.Length
                  AssistantLogic.Notes = notes.Length
                  AssistantLogic.EventsThisWeek = weekEvents.Length
                  AssistantLogic.SessionsToday = todaySessions.Length }

            return conversations, available, settings.AiModel, facts
        })
        (fun (c, a, m, f) -> Loaded(c, a, m, f))
        Failed

let init (services: AppServices) : Model * Cmd<Msg> =
    { Conversations = []
      Current = None
      Messages = []
      Input = ""
      LastSent = ""
      Prefilled = false
      Query = ""
      Sending = false
      Provider = AssistantLogic.ProviderState.NotConfigured
      AiModel = ""
      Facts = AssistantLogic.ContextFacts.empty
      HistoryOpen = true
      ContextOpen = true
      PendingDelete = false
      UserScrolledUp = false
      DistanceFromBottom = 0.0
      Width = 1600.0
      Loading = true
      Error = None },
    loadCmd services

// ---------------------------------------------------------------------------
// update
// ---------------------------------------------------------------------------

let update (services: AppServices) (msg: Msg) (model: Model) : Model * Cmd<Msg> * External option =
    let plain m = m, Cmd.none, None

    match msg with
    | Load -> { model with Loading = true }, loadCmd services, None

    | Loaded(conversations, available, aiModel, facts) ->
        plain
            { model with
                Conversations = conversations
                Provider =
                    (if available then AssistantLogic.ProviderState.Ready
                     else AssistantLogic.ProviderState.NotConfigured)
                AiModel = aiModel
                Facts = facts
                Loading = false
                Error = None }

    // A failure keeps the conversation, the context and the previous messages —
    // only the in-flight flags are cleared.
    | Failed ex -> plain { model with Loading = false; Sending = false; Error = Some ex.Message }

    | SelectConversation conversation ->
        { model with Current = Some conversation; PendingDelete = false; Error = None; UserScrolledUp = false },
        // The repository is reached inside the async, not while the command is
        // being built, so the state transition itself stays independent of it.
        Elm.ofAsync (async { return! services.Chat.GetMessages conversation.Id }) MessagesLoaded Failed,
        None

    // A freshly opened conversation starts at the latest message.
    | MessagesLoaded messages ->
        plain { model with Messages = messages; UserScrolledUp = false; DistanceFromBottom = 0.0 }

    | NewConversation ->
        plain
            { model with
                Current = None
                Messages = []
                PendingDelete = false
                Error = None
                UserScrolledUp = false }

    | RequestDelete -> plain { model with PendingDelete = true }
    | CancelDelete -> plain { model with PendingDelete = false }
    | ConfirmDelete ->
        match model.Current with
        | Some conversation ->
            { model with PendingDelete = false },
            Elm.ofAsync
                (async { return! services.Chat.DeleteConversation conversation.Id })
                (fun _ -> DeletedCurrent)
                Failed,
            None
        | None -> plain { model with PendingDelete = false }

    | DeletedCurrent -> { model with Current = None; Messages = [] }, loadCmd services, None

    // Typing clears the prefilled marking: once edited it is the user's own text.
    | SetInput text -> plain { model with Input = text; Prefilled = false }

    // Arriving from another screen. This is a **prefilled prompt**, not attached
    // context — words in the composer the user can edit or delete.
    | Prefill text -> plain { model with Input = text; Prefilled = true }
    | ClearPrefill -> plain { model with Input = ""; Prefilled = false }

    // A suggestion populates the composer. It never sends.
    | UseSuggestion text -> plain { model with Input = text; Prefilled = false }

    | SetQuery query -> plain { model with Query = query }
    | ToggleHistory -> plain { model with HistoryOpen = not model.HistoryOpen }
    | ToggleContext -> plain { model with ContextOpen = not model.ContextOpen }

    | Send ->
        if not (AssistantLogic.canSend model.Provider model.Sending model.Input) then
            plain model
        else
            let text = model.Input.Trim()

            // `Input` is cleared here, as it always has been. `LastSent` keeps a
            // copy so a recoverable failure can hand it back.
            { model with
                Sending = true
                Input = ""
                LastSent = text
                Prefilled = false
                Error = None
                UserScrolledUp = false },
            Elm.ofAsync (async { return! services.Ai.Chat(model.Current, text) }) GotReply Failed,
            None

    | GotReply(Ok outcome) ->
        let conversations =
            if model.Conversations |> List.exists (fun c -> c.Id = outcome.Conversation.Id) then
                model.Conversations
            else
                outcome.Conversation :: model.Conversations

        plain
            { model with
                Sending = false
                Provider = AssistantLogic.ProviderState.Ready
                Current = Some outcome.Conversation
                Conversations = conversations
                LastSent = ""
                Messages = model.Messages @ [ outcome.UserMessage; outcome.AssistantMessage ] }

    | GotReply(Error e) ->
        // The draft comes back, so a failed request never costs the user their
        // words. The conversation and its messages are untouched.
        plain
            { model with
                Sending = false
                Input = (if model.Input.Trim().Length = 0 then model.LastSent else model.Input)
                Provider =
                    (match e with
                     | AiError.NotConfigured -> AssistantLogic.ProviderState.NotConfigured
                     | _ -> AssistantLogic.ProviderState.Failed(AssistantLogic.describeError e))
                Error = Some(AssistantLogic.describeError e) }

    | ScrollChanged(distance, userUp) ->
        plain { model with DistanceFromBottom = distance; UserScrolledUp = userUp }

    | JumpToLatest -> plain { model with UserScrolledUp = false; DistanceFromBottom = 0.0 }

    // Crossing the narrow breakpoint re-places the rails rather than deleting
    // them: entering narrow collapses both so the conversation keeps the width,
    // leaving it restores them. Either way both stay reachable from their
    // toggles, so the context statement is never lost with the window.
    | Resized width ->
        let wasNarrow = model.Width < AssistantLogic.narrowWidth
        let isNarrow = width < AssistantLogic.narrowWidth

        if isNarrow = wasNarrow then
            plain { model with Width = width }
        else
            plain { model with Width = width; HistoryOpen = not isNarrow; ContextOpen = not isNarrow }
    | GotoPage page -> model, Cmd.none, Some(Goto page)

// ---------------------------------------------------------------------------
// view — shared treatments
// ---------------------------------------------------------------------------

let private weight (w: int) = enum<FontWeight> w

let private sectionCaption (text: string) : IView =
    TextBlock.create
        [ TextBlock.text (text.ToUpperInvariant())
          TextBlock.fontSize 10.5
          TextBlock.fontWeight (weight 600)
          TextBlock.foreground (Theme.label ())
          TextBlock.textWrapping TextWrapping.Wrap ]
    :> IView

let private panel (tokens: Tokens) (padding: float) (children: IView list) : IView =
    GlassPanel.create
        [ GlassPanel.baseRadius DashboardLogic.Card.radius
          GlassPanel.elevation 1
          GlassPanel.highlightScale AssistantLogic.Geometry.contentHighlight
          GlassPanel.tint (Theme.surfaces ()).Card
          GlassPanel.child (
              Border.create
                  [ Border.padding (Tokens.space tokens padding)
                    Border.child (
                        StackPanel.create
                            [ StackPanel.spacing 8.0
                              StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                              StackPanel.children children ] ) ] ) ]
    :> IView

let private bulletRow (glyph: string) (glyphBrush: IBrush) (text: string) (textBrush: IBrush) : IView =
    Grid.create
        [ Grid.columnDefinitions AssistantLogic.Geometry.bulletColumns
          Grid.margin (0.0, 1.0)
          Grid.children
              [ TextBlock.create
                    [ Grid.column 0
                      TextBlock.text glyph
                      TextBlock.fontSize 11.0
                      TextBlock.foreground glyphBrush
                      TextBlock.verticalAlignment VerticalAlignment.Top
                      Ui.decorative () ]
                TextBlock.create
                    [ Grid.column 2
                      TextBlock.text text
                      TextBlock.fontSize 11.5
                      TextBlock.foreground textBrush
                      TextBlock.textWrapping TextWrapping.Wrap ] ] ]
    :> IView

let private chipWash (surface: Color) (hex: string) =
    Theme.compositeOver surface (Theme.withAlphaHex AssistantLogic.Geometry.chipFillAlpha hex)

let private glassAction
    (tokens: Tokens)
    (label: string)
    (primary: bool)
    (enabled: bool)
    (accessibleName: string)
    (onClick: unit -> unit)
    : IView =
    // Geometry only. The glass, the veil, the accent floor and the ink rule all
    // live in `Ui.glassAction` — see the note there on why eleven copies of
    // this button existed and only one of them kept its accent.
    Ui.glassAction
        tokens
        { Ui.GlassActionMetrics.Padding = (14.0, 8.0)
          Ui.GlassActionMetrics.Margin = (0.0, 0.0, 8.0, 8.0) }
        label
        primary
        enabled
        accessibleName
        onClick

/// A quiet chip — suggestions and context counts.
let private chip (tokens: Tokens) (label: string) (hex: string) (onClick: (unit -> unit) option) : IView =
    let surface = (Theme.surfaces ()).Card
    let ink = Theme.brush (Theme.readableOnColor (chipWash surface hex) hex)

    match onClick with
    | Some click ->
        Button.create
            [ Button.background (Theme.brush (Theme.withAlphaHex AssistantLogic.Geometry.chipFillAlpha hex))
              Button.borderThickness 1.0
              Button.borderBrush Theme.transparent
              Button.foreground ink
              Button.cornerRadius (Tokens.radius tokens 999.0)
              Button.minHeight 28.0
              Button.padding (14.0, 4.0)
              Button.fontSize 12.0
              Button.fontWeight FontWeight.SemiBold
              Button.margin (0.0, 0.0, 8.0, 8.0)
              Button.cursor Ui.hand
              Button.content label
              Ui.accessibleName (sprintf "Use this prompt: %s" label)
              Button.onClick ((fun _ -> click ()), SubPatchOptions.Always) ]
        :> IView
    | None ->
        Border.create
            [ Border.background (Theme.brush (Theme.withAlphaHex AssistantLogic.Geometry.chipFillAlpha hex))
              Border.cornerRadius (Tokens.radius tokens 999.0)
              Border.padding (10.0, 3.0)
              Border.margin (0.0, 0.0, 6.0, 6.0)
              Border.isHitTestVisible false
              Border.child (
                  TextBlock.create
                      [ TextBlock.text label
                        TextBlock.fontSize 11.0
                        TextBlock.fontWeight FontWeight.SemiBold
                        TextBlock.foreground ink ] ) ]
        :> IView

let private railToggle (glyph: string) (label: string) (onClick: unit -> unit) : IView =
    Button.create
        [ Button.content glyph
          Button.width 28.0
          Button.height 28.0
          Button.background Theme.transparent
          Button.borderThickness 0.0
          Button.padding 0.0
          Button.fontSize 12.0
          Button.foreground (Theme.muted ())
          Button.cursor Ui.hand
          ToolTip.tip label
          Ui.accessibleName label
          Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always) ]
    :> IView

// ---------------------------------------------------------------------------
// the applied-changes strip
// ---------------------------------------------------------------------------

/// **Driven entirely by `AssistantLogic.splitApplied`**, so a live reply and the
/// same reply after a reload produce the same strip.
///
/// No per-entity navigation: neither the outcome nor the stored text carries an
/// identifier, so linking would mean guessing one. The Timeline does record
/// successful changes, and that is the one navigation offered.
let private appliedStrip (tokens: Tokens) (dispatch: Msg -> unit) (actions: AssistantLogic.AppliedAction list) : IView list =
    if actions.IsEmpty then
        []
    else
        let outcome = AssistantLogic.outcomeOf actions

        let headingHex =
            match outcome with
            | AssistantLogic.AppliedOutcome.AllApplied -> Theme.successHex ()
            | AssistantLogic.AppliedOutcome.Partial -> Theme.warningHex ()
            | AssistantLogic.AppliedOutcome.NoneApplied -> Theme.dangerHex ()

        let surface = (Theme.surfaces ()).CardAlt
        let wash =
            Theme.compositeOver surface (Theme.withAlphaHex AssistantLogic.Geometry.appliedFillAlpha headingHex)

        [ Border.create
              [ Border.background (
                    Theme.brush (Theme.withAlphaHex AssistantLogic.Geometry.appliedFillAlpha headingHex)
                )
                Border.borderThickness 1.0
                Border.borderBrush (
                    Theme.brush (Theme.overlayEdgeHex AssistantLogic.Geometry.appliedEdgeAlpha headingHex)
                )
                Border.cornerRadius (Tokens.radius tokens 12.0)
                Border.padding (14.0, 10.0)
                Border.margin (0.0, 8.0, 0.0, 0.0)
                Border.child (
                    StackPanel.create
                        [ StackPanel.spacing 6.0
                          StackPanel.children
                              [ TextBlock.create
                                    [ TextBlock.text (AssistantLogic.AppliedOutcome.heading outcome)
                                      TextBlock.fontSize Theme.Type.metricLabel
                                      TextBlock.fontWeight FontWeight.SemiBold
                                      TextBlock.foreground (
                                          Theme.brush (Theme.readableOnColor wash headingHex)
                                      )
                                      TextBlock.textWrapping TextWrapping.Wrap ]

                                for action in actions do
                                    let hex =
                                        if action.Failed then Theme.dangerHex () else Theme.successHex ()

                                    // A glyph as well as a colour, so success and
                                    // failure are not distinguished by hue alone.
                                    bulletRow
                                        (if action.Failed then "✕" else "✓")
                                        (Theme.brush (Theme.readableOnColor surface hex))
                                        action.Description
                                        (Theme.textSecondary ())

                                Border.create
                                    [ Border.horizontalAlignment HorizontalAlignment.Left
                                      Border.margin (0.0, 4.0, 0.0, 0.0)
                                      Border.child (
                                          Ui.smallButton "View in Timeline" (fun () ->
                                              dispatch (GotoPage Page.Timeline))
                                      ) ] ] ]
                ) ]
          :> IView ]

// ---------------------------------------------------------------------------
// messages
// ---------------------------------------------------------------------------

let private messageBubble (tokens: Tokens) (now: DateTime) (dispatch: Msg -> unit) (message: ChatMessage) : IView =
    let isUser = message.Role = ChatRole.User
    let accent = Theme.accentHex ()

    // The two roles differ by ALIGNMENT and CORNER SHAPE as well as fill, so
    // colour is never the only cue.
    let radius =
        if isUser then
            CornerRadius(
                AssistantLogic.Geometry.bubbleRadius,
                AssistantLogic.Geometry.bubbleRadius,
                AssistantLogic.Geometry.bubbleTightRadius,
                AssistantLogic.Geometry.bubbleRadius
            )
        else
            CornerRadius(
                AssistantLogic.Geometry.bubbleTightRadius,
                AssistantLogic.Geometry.bubbleRadius,
                AssistantLogic.Geometry.bubbleRadius,
                AssistantLogic.Geometry.bubbleRadius
            )

    let body, actions =
        if isUser then message.Content, [] else AssistantLogic.splitApplied message.Content

    StackPanel.create
        [ StackPanel.spacing 4.0
          StackPanel.horizontalAlignment (if isUser then HorizontalAlignment.Right else HorizontalAlignment.Left)
          StackPanel.children
              [ Border.create
                    [ Border.background (
                          if isUser then Theme.brush (Theme.withAlphaHex AssistantLogic.Geometry.userBubbleAlpha accent)
                          else Theme.brush (Ui.hexOf (Theme.surfaces ()).Card)
                      )
                      Border.borderThickness 1.0
                      Border.borderBrush (
                          if isUser then
                              Theme.brush (Theme.overlayEdgeHex AssistantLogic.Geometry.userBubbleEdgeAlpha accent)
                          else
                              Theme.border ()
                      )
                      Border.cornerRadius radius
                      Border.padding (18.0, 14.0)
                      Border.child (
                          StackPanel.create
                              [ StackPanel.spacing 6.0
                                StackPanel.children
                                    [ if isUser then
                                          TextBlock.create
                                              [ TextBlock.text body
                                                TextBlock.fontSize 13.5
                                                TextBlock.foreground (Theme.text ())
                                                TextBlock.textWrapping TextWrapping.Wrap ]
                                      else
                                          // The body only — the "✓ " lines were
                                          // parsed out above and are rendered by
                                          // the strip instead, never twice.
                                          Ui.markdown body

                                      yield! appliedStrip tokens dispatch actions ] ] ) ]

                TextBlock.create
                    [ TextBlock.text (Format.relativeTime now message.CreatedAt)
                      TextBlock.fontSize 10.0
                      TextBlock.foreground (Theme.faint ())
                      TextBlock.horizontalAlignment (
                          if isUser then HorizontalAlignment.Right else HorizontalAlignment.Left
                      ) ] ] ]
    :> IView

// ---------------------------------------------------------------------------
// the history rail
// ---------------------------------------------------------------------------

let private historyRail (tokens: Tokens) (now: DateTime) (model: Model) (dispatch: Msg -> unit) : IView =
    let shown = AssistantLogic.searchConversations model.Query model.Conversations
    let groups = AssistantLogic.groupConversations now shown

    let row (conversation: Conversation) : IView =
        let isSelected = model.Current |> Option.map (fun c -> c.Id = conversation.Id) |> Option.defaultValue false
        let accent = Theme.accentHex ()

        Button.create
            [ Button.classes [ Ui.glassActionClass () ]
              Button.background (
                  if isSelected then
                      Theme.brush (Theme.withAlphaHex AssistantLogic.Geometry.selectedRowAlpha accent)
                  else
                      Theme.transparent
              )
              // Reserved in BOTH states, so selecting recolours and never resizes.
              Button.borderThickness 1.0
              Button.borderBrush (if isSelected then Theme.brush accent else Theme.transparent)
              Button.cornerRadius (Tokens.radius tokens 11.0)
              Button.padding (10.0, 9.0)
              Button.margin (0.0, 0.0, 0.0, 6.0)
              Button.horizontalAlignment HorizontalAlignment.Stretch
              Button.horizontalContentAlignment HorizontalAlignment.Stretch
              Button.cursor Ui.hand
              Ui.accessibleName (sprintf "Open conversation %s" conversation.Title)
              Button.onClick ((fun _ -> dispatch (SelectConversation conversation)), SubPatchOptions.Always)
              Button.content (
                  StackPanel.create
                      [ StackPanel.spacing 3.0
                        StackPanel.children
                            [ TextBlock.create
                                  [ TextBlock.text conversation.Title
                                    TextBlock.fontSize 12.5
                                    TextBlock.fontWeight FontWeight.SemiBold
                                    TextBlock.foreground (Theme.text ())
                                    TextBlock.textTrimming TextTrimming.CharacterEllipsis ]
                              TextBlock.create
                                  [ TextBlock.text (Format.relativeTime now conversation.UpdatedAt)
                                    TextBlock.fontSize 10.5
                                    TextBlock.foreground (Theme.faint ()) ] ] ]
              ) ]
        :> IView

    DockPanel.create
        [ DockPanel.children
              [ DockPanel.create
                    [ DockPanel.dock Dock.Top
                      DockPanel.margin (12.0, 8.0, 8.0, 8.0)
                      DockPanel.children
                          [ Border.create
                                [ DockPanel.dock Dock.Right
                                  Border.child (railToggle "‹" "Collapse the history" (fun () -> dispatch ToggleHistory)) ]
                            Border.create
                                [ Border.verticalAlignment VerticalAlignment.Center
                                  Border.child (sectionCaption "History") ] ] ]

                StackPanel.create
                    [ DockPanel.dock Dock.Top
                      StackPanel.margin (12.0, 0.0, 12.0, 10.0)
                      StackPanel.spacing 8.0
                      StackPanel.children
                          [ glassAction tokens "＋ New conversation" true true "Start a new conversation" (fun () ->
                                dispatch NewConversation)
                            Ui.textBox model.Query "Search conversations" (SetQuery >> dispatch) ] ]

                Ui.scrollNoChrome (
                    StackPanel.create
                        [ StackPanel.margin (12.0, 0.0, 12.0, 16.0)
                          StackPanel.spacing 12.0
                          StackPanel.children
                              [ if model.Conversations.IsEmpty then
                                    bulletRow
                                        "•"
                                        (Theme.faint ())
                                        "No conversations yet. Ask something and one is saved automatically."
                                        (Theme.muted ())
                                elif groups.IsEmpty then
                                    bulletRow "•" (Theme.faint ()) "No conversations match that search." (Theme.muted ())
                                else
                                    for label, conversations in groups do
                                        StackPanel.create
                                            [ StackPanel.spacing 5.0
                                              StackPanel.children
                                                  [ sectionCaption label
                                                    StackPanel.create
                                                        [ StackPanel.children (conversations |> List.map row) ] ] ]

                                TextBlock.create
                                    [ TextBlock.text AssistantLogic.titleNotice
                                      TextBlock.fontSize 10.0
                                      TextBlock.foreground (Theme.faint ())
                                      TextBlock.textWrapping TextWrapping.Wrap
                                      TextBlock.margin (0.0, 6.0, 0.0, 0.0) ] ] ]
                ) ] ]
    :> IView

// ---------------------------------------------------------------------------
// the conversation workspace
// ---------------------------------------------------------------------------

let private welcome (tokens: Tokens) (model: Model) : IView =
    panel
        tokens
        20.0
        [ TextBlock.create
              [ TextBlock.text "Ask about your modules, coursework, notes and schedule"
                TextBlock.fontSize 16.0
                TextBlock.fontWeight FontWeight.SemiBold
                TextBlock.foreground (Theme.text ())
                TextBlock.textWrapping TextWrapping.Wrap ]

          // Never phrased as an answer — this is an empty state, not a reply.
          TextBlock.create
              [ TextBlock.text AssistantLogic.privacyNotice
                TextBlock.fontSize Theme.Type.rowMeta
                TextBlock.foreground (Theme.muted ())
                TextBlock.textWrapping TextWrapping.Wrap ]

          if model.Facts = AssistantLogic.ContextFacts.empty then
              bulletRow
                  "•"
                  (Theme.faint ())
                  "There is nothing in the application yet, so the Assistant has no context to work from. Add a module, an assignment or a note first."
                  (Theme.muted ()) ]

let private suggestionRow (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView list =
    match AssistantLogic.suggestions model.Facts with
    | [] -> []
    | prompts ->
        [ StackPanel.create
              [ StackPanel.spacing 6.0
                StackPanel.children
                    [ sectionCaption "Try asking"
                      WrapPanel.create
                          [ WrapPanel.children
                                (prompts
                                 |> List.map (fun p ->
                                     // Populates the composer. Never sends.
                                     chip tokens p (Theme.accentHex ()) (Some(fun () -> dispatch (UseSuggestion p))))) ] ] ]
          :> IView ]

let private composer (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let blocked = AssistantLogic.sendBlockedReason model.Provider model.Sending model.Input

    Border.create
        [ Border.borderThickness (0.0, 1.0, 0.0, 0.0)
          Border.borderBrush (Theme.divider ())
          Border.padding (32.0, 14.0, 32.0, 20.0)
          Border.child (
              StackPanel.create
                  [ StackPanel.spacing 10.0
                    StackPanel.maxWidth 900.0
                    StackPanel.children
                        [ yield! suggestionRow tokens model dispatch

                          Border.create
                              [ Border.background (Theme.brush (Ui.hexOf (Theme.surfaces ()).Card))
                                Border.borderThickness 1.0
                                Border.borderBrush (Theme.border ())
                                Border.cornerRadius (Tokens.radius tokens AssistantLogic.Geometry.composerRadius)
                                Border.padding (16.0, 12.0)
                                Border.child (
                                    StackPanel.create
                                        [ StackPanel.spacing 8.0
                                          StackPanel.children
                                              [ // What is actually sent, in one line.
                                                TextBlock.create
                                                    [ TextBlock.text (
                                                          sprintf
                                                              "Context: %d modules · %d open assignments · %d notes · this week's schedule"
                                                              model.Facts.Modules
                                                              model.Facts.OpenAssignments
                                                              model.Facts.Notes
                                                      )
                                                      TextBlock.fontSize 11.0
                                                      TextBlock.foreground (Theme.faint ())
                                                      TextBlock.textWrapping TextWrapping.Wrap ]

                                                if model.Prefilled then
                                                    DockPanel.create
                                                        [ DockPanel.children
                                                              [ Border.create
                                                                    [ DockPanel.dock Dock.Right
                                                                      Border.child (
                                                                          Ui.smallButton "Clear" (fun () ->
                                                                              dispatch ClearPrefill)
                                                                      ) ]
                                                                TextBlock.create
                                                                    [ TextBlock.text AssistantLogic.prefilledNotice
                                                                      TextBlock.fontSize 11.0
                                                                      TextBlock.foreground (Theme.accent ())
                                                                      TextBlock.verticalAlignment
                                                                          VerticalAlignment.Center
                                                                      TextBlock.textWrapping TextWrapping.Wrap ] ] ]

                                                Ui.multilineBox
                                                    model.Input
                                                    "Ask about your modules, coursework, notes or schedule…"
                                                    90.0
                                                    (SetInput >> dispatch)

                                                DockPanel.create
                                                    [ DockPanel.children
                                                          [ Border.create
                                                                [ DockPanel.dock Dock.Right
                                                                  Border.child (
                                                                      glassAction
                                                                          tokens
                                                                          (if model.Sending then "Waiting…" else "Send")
                                                                          true
                                                                          blocked.IsNone
                                                                          (match blocked with
                                                                           | Some reason -> reason
                                                                           | None -> "Send this message")
                                                                          (fun () -> dispatch Send)
                                                                  ) ]

                                                            StackPanel.create
                                                                [ StackPanel.spacing 2.0
                                                                  StackPanel.verticalAlignment
                                                                      VerticalAlignment.Center
                                                                  StackPanel.children
                                                                      [ match blocked with
                                                                        | Some reason ->
                                                                            TextBlock.create
                                                                                [ TextBlock.text reason
                                                                                  TextBlock.fontSize 11.0
                                                                                  TextBlock.foreground (Theme.faint ())
                                                                                  TextBlock.textWrapping
                                                                                      TextWrapping.Wrap ]
                                                                        | None -> ()

                                                                        match AssistantLogic.counterText model.Input with
                                                                        | Some counter ->
                                                                            TextBlock.create
                                                                                [ TextBlock.text counter
                                                                                  TextBlock.fontSize 10.0
                                                                                  TextBlock.foreground (Theme.faint ()) ]
                                                                        | None -> () ] ] ] ] ] ] ) ] ] ] ) ]
    :> IView

let private workspace (tokens: Tokens) (now: DateTime) (model: Model) (dispatch: Msg -> unit) : IView =
    let messageViews: IView list =
        [ if model.Messages.IsEmpty && not model.Loading then
              welcome tokens model

          for message in model.Messages do
              messageBubble tokens now dispatch message

          // A genuine pending state. No token-by-token animation, because the
          // provider returns the whole response at once.
          if model.Sending then
              StackPanel.create
                  [ StackPanel.horizontalAlignment HorizontalAlignment.Left
                    StackPanel.children
                        [ Border.create
                              [ Border.background (Theme.brush (Ui.hexOf (Theme.surfaces ()).Card))
                                Border.borderThickness 1.0
                                Border.borderBrush (Theme.border ())
                                Border.cornerRadius (Tokens.radius tokens AssistantLogic.Geometry.bubbleRadius)
                                Border.padding (18.0, 14.0)
                                Border.child (
                                    TextBlock.create
                                        [ TextBlock.text "Waiting for the provider…"
                                          TextBlock.fontSize Theme.Type.rowMeta
                                          TextBlock.foreground (Theme.muted ()) ]
                                ) ] ] ]

          match model.Error with
          | Some error ->
              let wash =
                  Theme.compositeOver
                      (Theme.surfaces ()).Card
                      (Theme.withAlphaHex AssistantLogic.Geometry.errorFillAlpha (Theme.dangerHex ()))

              Border.create
                  [ Border.background (
                        Theme.brush (Theme.withAlphaHex AssistantLogic.Geometry.errorFillAlpha (Theme.dangerHex ()))
                    )
                    Border.borderThickness 1.0
                    Border.borderBrush (
                        Theme.brush (Theme.overlayEdgeHex AssistantLogic.Geometry.errorEdgeAlpha (Theme.dangerHex ()))
                    )
                    Border.cornerRadius (Tokens.radius tokens 12.0)
                    Border.padding (16.0, 12.0)
                    Border.child (
                        StackPanel.create
                            [ StackPanel.spacing 8.0
                              StackPanel.children
                                  [ TextBlock.create
                                        [ TextBlock.text error
                                          TextBlock.fontSize Theme.Type.rowMeta
                                          TextBlock.foreground (
                                              Theme.brush (Theme.readableOnColor wash (Theme.dangerHex ()))
                                          )
                                          TextBlock.textWrapping TextWrapping.Wrap ]

                                    WrapPanel.create
                                        [ WrapPanel.children
                                              [ Ui.smallButton "Try again" (fun () -> dispatch Send)
                                                if not (AssistantLogic.ProviderState.isReady model.Provider) then
                                                    Ui.smallButton "Open Settings" (fun () ->
                                                        dispatch (GotoPage Page.Settings)) ] ] ] ]
                    ) ]
          | None -> () ]

    DockPanel.create
        [ DockPanel.children
              [ Border.create [ DockPanel.dock Dock.Bottom; Border.child (composer tokens model dispatch) ]

                // Jump to latest, shown exactly when following is off.
                if AssistantLogic.showJumpToLatest model.UserScrolledUp model.DistanceFromBottom (not model.Messages.IsEmpty) then
                    Border.create
                        [ DockPanel.dock Dock.Bottom
                          Border.horizontalAlignment HorizontalAlignment.Center
                          Border.margin (0.0, 0.0, 0.0, 8.0)
                          Border.child (Ui.smallButton "↓ Jump to latest" (fun () -> dispatch JumpToLatest)) ]

                Ui.scrollNoChrome (
                    Border.create
                        [ Border.padding (32.0, 24.0)
                          Border.child (
                              StackPanel.create
                                  [ StackPanel.spacing 20.0
                                    StackPanel.maxWidth 900.0
                                    StackPanel.children messageViews ]
                          ) ]
                ) ] ]
    :> IView

// ---------------------------------------------------------------------------
// the context rail
// ---------------------------------------------------------------------------

let private contextRail (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let region (r: AssistantLogic.ContextRegion) : IView list =
        match r with
        | AssistantLogic.ContextRegion.Provider ->
            let ready = AssistantLogic.ProviderState.isReady model.Provider

            [ sectionCaption "Provider"

              chip
                  tokens
                  (AssistantLogic.ProviderState.label model.Provider)
                  (if ready then Theme.successHex () else Theme.warningHex ())
                  None

              if model.AiModel.Length > 0 then
                  TextBlock.create
                      [ TextBlock.text (sprintf "Model: %s" model.AiModel)
                        TextBlock.fontSize 11.0
                        TextBlock.foreground (Theme.faint ())
                        TextBlock.textWrapping TextWrapping.Wrap ]

              TextBlock.create
                  [ TextBlock.text (sprintf "Up to %d characters per message." AssistantLogic.maxMessageChars)
                    TextBlock.fontSize 11.0
                    TextBlock.foreground (Theme.faint ())
                    TextBlock.textWrapping TextWrapping.Wrap ]

              if not ready then
                  Border.create
                      [ Border.margin (0.0, 4.0, 0.0, 0.0)
                        Border.horizontalAlignment HorizontalAlignment.Left
                        Border.child (Ui.smallButton "Open Settings" (fun () -> dispatch (GotoPage Page.Settings))) ] ]

        | AssistantLogic.ContextRegion.Scope ->
            sectionCaption "What is sent"
            :: (AssistantLogic.contextScope
                |> List.map (fun c ->
                    Grid.create
                        [ Grid.columnDefinitions AssistantLogic.Geometry.detailColumns
                          Grid.margin (0.0, 1.0)
                          Grid.children
                              [ TextBlock.create
                                    [ Grid.column 0
                                      TextBlock.text c.Label
                                      TextBlock.fontSize 11.0
                                      TextBlock.fontWeight FontWeight.SemiBold
                                      TextBlock.foreground (Theme.textSecondary ()) ]
                                TextBlock.create
                                    [ Grid.column 2
                                      TextBlock.text c.Detail
                                      TextBlock.fontSize 10.5
                                      TextBlock.foreground (Theme.faint ())
                                      TextBlock.textWrapping TextWrapping.Wrap ] ] ]
                    :> IView))

        | AssistantLogic.ContextRegion.Excluded ->
            // Shown, not merely absent, so the boundary is visible.
            sectionCaption "Not sent"
            :: (AssistantLogic.contextExcluded
                |> List.map (fun label ->
                    bulletRow "–" (Theme.faint ()) label (Theme.faint ())))

        | AssistantLogic.ContextRegion.Privacy ->
            [ Border.create
                  [ Border.background (Theme.cardAlt ())
                    Border.cornerRadius (Tokens.radius tokens 10.0)
                    Border.padding (12.0, 10.0)
                    Border.child (
                        TextBlock.create
                            [ TextBlock.text AssistantLogic.privacyNotice
                              TextBlock.fontSize 11.0
                              TextBlock.foreground (Theme.textSecondary ())
                              TextBlock.textWrapping TextWrapping.Wrap ]
                    ) ] ]

    DockPanel.create
        [ DockPanel.children
              [ DockPanel.create
                    [ DockPanel.dock Dock.Top
                      DockPanel.margin (8.0, 8.0, 12.0, 8.0)
                      DockPanel.children
                          [ Border.create
                                [ DockPanel.dock Dock.Left
                                  Border.child (railToggle "›" "Collapse the context" (fun () -> dispatch ToggleContext)) ]
                            Border.create
                                [ Border.verticalAlignment VerticalAlignment.Center
                                  Border.horizontalAlignment HorizontalAlignment.Right
                                  Border.child (sectionCaption "Context") ] ] ]

                Ui.scrollNoChrome (
                    StackPanel.create
                        [ StackPanel.margin (14.0, 0.0, 14.0, 20.0)
                          StackPanel.spacing 14.0
                          StackPanel.children
                              (AssistantLogic.contextOrder
                               |> List.map (fun r ->
                                   StackPanel.create [ StackPanel.spacing 5.0; StackPanel.children (region r) ] :> IView)) ]
                ) ] ]
    :> IView

// ---------------------------------------------------------------------------
// page
// ---------------------------------------------------------------------------

let view (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let now = DateTime.Now
    let placement = AssistantLogic.placementFor model.Width model.HistoryOpen model.ContextOpen

    let collapsedRail (glyph: string) (label: string) (onClick: unit -> unit) : IView =
        Border.create
            [ Border.width AssistantLogic.Geometry.railWidth
              Border.padding (0.0, 8.0)
              Border.child (
                  StackPanel.create
                      [ StackPanel.verticalAlignment VerticalAlignment.Top
                        StackPanel.horizontalAlignment HorizontalAlignment.Center
                        StackPanel.children [ railToggle glyph label onClick ] ] ) ]
        :> IView

    let header: IView =
        Border.create
            [ Border.borderThickness (0.0, 0.0, 0.0, 1.0)
              Border.borderBrush (Theme.divider ())
              Border.padding (24.0, 12.0)
              Border.child (
                  DockPanel.create
                      [ DockPanel.children
                            [ WrapPanel.create
                                  [ DockPanel.dock Dock.Right
                                    WrapPanel.children
                                        [ if model.Current.IsSome then
                                              Ui.smallButton "Delete conversation" (fun () -> dispatch RequestDelete) ] ]

                              WrapPanel.create
                                  [ WrapPanel.children
                                        [ TextBlock.create
                                              [ TextBlock.text "Assistant"
                                                TextBlock.fontSize 19.0
                                                TextBlock.fontWeight FontWeight.SemiBold
                                                TextBlock.foreground (Theme.text ())
                                                TextBlock.margin (0.0, 0.0, 14.0, 0.0)
                                                TextBlock.verticalAlignment VerticalAlignment.Center ]

                                          chip
                                              tokens
                                              (AssistantLogic.ProviderState.label model.Provider)
                                              (if AssistantLogic.ProviderState.isReady model.Provider then
                                                   Theme.successHex ()
                                               else
                                                   Theme.warningHex ())
                                              None ] ] ] ]
              ) ]
        :> IView

    let deleteConfirm: IView list =
        if not model.PendingDelete then
            []
        else
            [ Border.create
                  [ Border.background (
                        Theme.brush (Theme.withAlphaHex AssistantLogic.Geometry.confirmFillAlpha (Theme.dangerHex ()))
                    )
                    Border.borderThickness 1.0
                    Border.borderBrush (
                        Theme.brush (Theme.overlayEdgeHex AssistantLogic.Geometry.errorEdgeAlpha (Theme.dangerHex ()))
                    )
                    Border.cornerRadius (Tokens.radius tokens 12.0)
                    Border.padding (16.0, 12.0)
                    Border.margin (24.0, 12.0, 24.0, 0.0)
                    Border.child (
                        DockPanel.create
                            [ DockPanel.children
                                  [ StackPanel.create
                                        [ DockPanel.dock Dock.Right
                                          StackPanel.orientation Orientation.Horizontal
                                          StackPanel.spacing 10.0
                                          StackPanel.margin (12.0, 0.0, 0.0, 0.0)
                                          StackPanel.children
                                              [ Ui.dangerButton "Yes, delete it" (fun () -> dispatch ConfirmDelete)
                                                Ui.ghostButton "Cancel" (fun () -> dispatch CancelDelete) ] ]
                                    TextBlock.create
                                        [ TextBlock.text
                                              "Delete this conversation permanently? Its messages go with it, and any changes the Assistant already applied to your data stay applied."
                                          TextBlock.fontSize Theme.Type.rowMeta
                                          TextBlock.foreground (Theme.text ())
                                          TextBlock.verticalAlignment VerticalAlignment.Center
                                          TextBlock.textWrapping TextWrapping.Wrap ] ] ]
                    ) ]
              :> IView ]

    // Explicit Grid columns: the centre must stay dominant, and a stack would
    // let the rails push it around.
    let columns =
        sprintf
            "%f,*,%f"
            (AssistantLogic.historyColumnWidth placement)
            (AssistantLogic.contextColumnWidth placement)

    /// A collapsed stacked rail: a full-width strip that names what is behind it
    /// and opens it. Narrow does not mean absent.
    let stackedStrip (edge: Thickness) (label: string) (glyph: string) (onClick: unit -> unit) : IView =
        Border.create
            [ Border.borderThickness edge
              Border.borderBrush (Theme.divider ())
              Border.padding (16.0, 6.0)
              Border.child (
                  DockPanel.create
                      [ DockPanel.children
                            [ Border.create
                                  [ DockPanel.dock Dock.Right
                                    Border.child (railToggle glyph (sprintf "Open the %s" (label.ToLowerInvariant())) onClick) ]
                              Border.create
                                  [ Border.verticalAlignment VerticalAlignment.Center
                                    Border.child (sectionCaption label) ] ] ]
              ) ]
        :> IView

    let body: IView =
        if placement.Stacked then
            // Narrow: the conversation takes the width, and each rail becomes a
            // stacked section — expanded and height-capped, or a strip that
            // opens it. Neither is dropped.
            DockPanel.create
                [ DockPanel.children
                      [ if placement.ShowHistory then
                            Border.create
                                [ DockPanel.dock Dock.Top
                                  Border.maxHeight 200.0
                                  Border.borderThickness (0.0, 0.0, 0.0, 1.0)
                                  Border.borderBrush (Theme.divider ())
                                  Border.child (historyRail tokens now model dispatch) ]
                        else
                            Border.create
                                [ DockPanel.dock Dock.Top
                                  Border.child (
                                      stackedStrip
                                          (Thickness(0.0, 0.0, 0.0, 1.0))
                                          "History"
                                          "⌄"
                                          (fun () -> dispatch ToggleHistory)
                                  ) ]

                        if placement.ShowContext then
                            Border.create
                                [ DockPanel.dock Dock.Bottom
                                  Border.maxHeight 240.0
                                  Border.borderThickness (0.0, 1.0, 0.0, 0.0)
                                  Border.borderBrush (Theme.divider ())
                                  Border.child (contextRail tokens model dispatch) ]
                        else
                            Border.create
                                [ DockPanel.dock Dock.Bottom
                                  Border.child (
                                      stackedStrip
                                          (Thickness(0.0, 1.0, 0.0, 0.0))
                                          "Context"
                                          "⌃"
                                          (fun () -> dispatch ToggleContext)
                                  ) ]

                        workspace tokens now model dispatch ] ]
            :> IView
        else
            Grid.create
                [ Grid.columnDefinitions columns
                  Grid.children
                      [ Border.create
                            [ Grid.column 0
                              Border.borderThickness (0.0, 0.0, 1.0, 0.0)
                              Border.borderBrush (Theme.divider ())
                              Border.child (
                                  if placement.ShowHistory then historyRail tokens now model dispatch
                                  else collapsedRail "›" "Open the history" (fun () -> dispatch ToggleHistory)
                              ) ]

                        Border.create
                            [ Grid.column 1
                              Border.minWidth AssistantLogic.Geometry.centreMinWidth
                              Border.child (workspace tokens now model dispatch) ]

                        Border.create
                            [ Grid.column 2
                              Border.borderThickness (1.0, 0.0, 0.0, 0.0)
                              Border.borderBrush (Theme.divider ())
                              Border.child (
                                  if placement.ShowContext then contextRail tokens model dispatch
                                  else collapsedRail "‹" "Open the context panel" (fun () -> dispatch ToggleContext)
                              ) ] ] ]
            :> IView

    Border.create
        [ Border.onSizeChanged (
              (fun args ->
                  if AssistantLogic.placementFor model.Width model.HistoryOpen model.ContextOpen
                     <> AssistantLogic.placementFor args.NewSize.Width model.HistoryOpen model.ContextOpen then
                      dispatch (Resized args.NewSize.Width)),
              SubPatchOptions.Always
          )
          Border.child (
              DockPanel.create
                  [ DockPanel.children
                        [ StackPanel.create
                              [ DockPanel.dock Dock.Top
                                StackPanel.children (header :: deleteConfirm) ]
                          body ] ]
          ) ]
    :> IView
