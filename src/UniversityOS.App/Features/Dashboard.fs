module UniversityOS.App.Features.Dashboard

open System
open Elmish
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Controls

/// The morning answer: what should I do, what's on, what's due, what changed.

type Model =
    { Data: DashboardData option
      UserName: string
      Loading: bool
      Error: string option
      /// True while a reload triggered by an action is in flight, so the whole
      /// screen does not fall back to the skeleton every time a session is
      /// ticked off.
      Refreshing: bool
      /// Content width, used only to choose a column count. Updated when the
      /// count would actually change, not on every pixel of a drag.
      Columns: int }

/// Things the dashboard asks the shell to do.
type External =
    | OpenCapture
    | Goto of Page
    | RegeneratePlan

type Msg =
    | Load
    | Reload
    | Loaded of string * DashboardData
    | Failed of exn
    | ToggleSession of SessionId * bool
    | SessionToggled
    | RequestCapture
    | RequestGoto of Page
    | RequestRegeneratePlan
    | Resized of int

/// The single load. One settings read and one dashboard read — the dashboard
/// service does the rest in one pass, so nothing here fans out into repeated
/// per-card service calls.
let private loadCmd (services: AppServices) =
    Elm.ofAsync
        (async {
            let! settings = services.Settings.Load()
            let! data = services.Dashboard.GetData()
            return settings.UserName, data
        })
        Loaded
        Failed

let init (services: AppServices) : Model * Cmd<Msg> =
    { Data = None
      UserName = ""
      Loading = true
      Error = None
      Refreshing = false
      Columns = 3 },
    loadCmd services

/// Re-entry after navigating away and back.
///
/// **`Columns` is a measurement of the viewport, not data.** Navigating does not
/// resize the window, so re-deriving it from scratch is wrong — and it cannot
/// correct itself afterwards: the size handler dispatches `Resized` only when the
/// column count *would change*, and FuncUI diffs the view tree rather than
/// rebuilding it, so the measuring `Border` survives navigation and never raises
/// `SizeChanged` again.
///
/// The result was the misalignment reported in the first runtime review. On the
/// default 1380px window the content is about 1108px wide, so `columnsFor` gives
/// **2** — but `init` hardcodes **3**. Load the Dashboard and it corrects itself
/// once; leave and come back and it renders a three-column grid in a two-column
/// space, with nothing left to fix it. "Regenerate plan" was simply the most
/// natural way to leave and return.
///
/// Everything else *is* reloaded, which is the point of reload-on-navigate.
let reinit (previous: Model) (services: AppServices) : Model * Cmd<Msg> =
    let fresh, cmd = init services
    { fresh with Columns = previous.Columns }, cmd

let update (services: AppServices) (msg: Msg) (model: Model) : Model * Cmd<Msg> * External option =
    match msg with
    | Load ->
        let m, cmd = init services
        m, cmd, None

    | Reload ->
        // Keeps the current data on screen while refreshing, so an action does
        // not flash the whole page back to a skeleton.
        { model with Refreshing = true }, loadCmd services, None

    | Loaded(name, data) ->
        { model with
            Data = Some data
            UserName = name
            Loading = false
            Refreshing = false
            Error = None },
        Cmd.none,
        None

    | Failed ex ->
        { model with
            Loading = false
            Refreshing = false
            Error = Some ex.Message },
        Cmd.none,
        None
    | ToggleSession(id, completed) ->
        let title =
            model.Data
            |> Option.bind (fun d -> d.TodaySessions |> List.tryFind (fun s -> s.Id = id))
            |> Option.map (fun s -> s.Title)
            |> Option.defaultValue "Study session"

        model,
        Elm.ofAsync
            (async {
                do! services.Sessions.SetCompleted(id, completed)

                if completed then
                    do! services.Activity.Log(
                            // The session id travels with the entry. Without it a
                            // timeline entry can say a session was completed but
                            // not WHICH one, so the completion cannot be undone.
                            // Nothing else changes: `Entity` is an existing,
                            // already-persisted field and older rows keep None.
                            ActivityEntry.create
                                services.Clock.Now
                                ActivityKind.SessionCompleted
                                title
                                None
                                (Some { Kind = EntityKind.Session; Id = id.Value })
                        )
            })
            (fun _ -> SessionToggled)
            Failed,
        None
    // Refresh rather than a full Load: the data changed, the page did not.
    | SessionToggled -> model, Cmd.ofMsg Reload, None
    | RequestCapture -> model, Cmd.none, Some OpenCapture
    | RequestGoto page -> model, Cmd.none, Some(Goto page)
    | RequestRegeneratePlan -> model, Cmd.none, Some RegeneratePlan
    | Resized columns -> { model with Columns = columns }, Cmd.none, None


// ---------------------------------------------------------------------------
// view — the redesign's Dashboard composition, on real data
// ---------------------------------------------------------------------------

/// Colour for a derived urgency. Always paired with `Urgency.label` at the call
/// site: status is never communicated by colour alone.
let private urgencyHex (u: DashboardLogic.Urgency) =
    match u with
    | DashboardLogic.Critical -> Theme.dangerHex ()
    | DashboardLogic.Soon -> Theme.warningHex ()
    | DashboardLogic.Comfortable -> Theme.infoHex ()

/// "Done" is the only status that means *your* progress, so it takes the user's
/// accent; the rest are semantic states with fixed meanings of their own.
let private statusHex (tokens: Tokens) (s: DashboardLogic.ItemStatus) =
    match s with
    | DashboardLogic.Done -> Ui.hexOf tokens.Accent.Normal
    | DashboardLogic.Now -> Theme.dangerHex ()
    | DashboardLogic.Next -> Theme.warningHex ()
    | DashboardLogic.Later -> Theme.infoHex ()

// The card treatment itself lives in DashboardLogic.Card, so the values are
// assertable; the view only applies them.
let private cardHighlightScale = DashboardLogic.Card.highlightScale

/// The card surface: the shared material role, not a Dashboard colour.
///
/// `tokens` is unused on purpose — that is the correction. A content card is
/// material, so it must not move with the accent; the accent shows up in this
/// file only on the dials, the insight line, the action icons and the "done"
/// status, all below.
let private cardTint () = (Theme.surfaces ()).Card

/// A quick action's surface — the same family, one step deeper, as in the
/// redesign's `background:#1B1710` action tiles.
let private actionTint () = (Theme.surfaces ()).Action

/// A Dashboard card. Same shared backdrop and Tier C fallback as every other
/// glass surface — only the tint, highlight and padding differ, all through
/// existing GlassPanel properties rather than a forked renderer.
let private glassCardOf (tokens: Tokens) (padding: float) (children: IView list) : IView =
    GlassPanel.create
        [ GlassPanel.baseRadius DashboardLogic.Card.radius
          GlassPanel.elevation 1
          GlassPanel.highlightScale cardHighlightScale
          GlassPanel.tint (cardTint ())
          GlassPanel.child (
              Border.create
                  [ Border.padding (Tokens.space tokens padding)
                    // Top-aligned content inside a card that stretches to its
                    // row height: the empty lower portion is intentional and
                    // keeps the grid aligned.
                    Border.verticalAlignment VerticalAlignment.Stretch
                    Border.child (
                        StackPanel.create
                            [ StackPanel.spacing (Tokens.space tokens 12.0)
                              StackPanel.verticalAlignment VerticalAlignment.Top
                              StackPanel.children children ]
                    ) ]
          ) ]
    :> IView

let private glassCard (tokens: Tokens) (children: IView list) : IView =
    glassCardOf tokens 22.0 children

/// A quick action, on the same warm glass as every other Dashboard card.
///
/// Built here rather than in the shared toolkit because it is Dashboard
/// styling: a real Button hosting a GlassPanel, so it keeps focus, keyboard
/// activation, hover and pressed states while looking like the redesign's broad
/// action card rather than a filled toolbar button.
let private quickActionCard (tokens: Tokens) (icon: string) (label: string) (onClick: unit -> unit) : IView =
    Button.create
        [ Button.horizontalAlignment HorizontalAlignment.Stretch
          Button.verticalAlignment VerticalAlignment.Stretch
          Button.horizontalContentAlignment HorizontalAlignment.Stretch
          Button.verticalContentAlignment VerticalAlignment.Stretch
          Button.background Theme.transparent
          Button.borderThickness 0.0
          Button.padding 0.0
          // The Button's own corner radius must agree with the glass inside it.
          // Left at Fluent's default it is a much tighter curve than the panel's,
          // so anything the template rounds cuts across the glass corner rather
          // than following it.
          Button.cornerRadius (Tokens.radius tokens DashboardLogic.Card.actionRadius)
          Button.clipToBounds false
          Button.minHeight DashboardLogic.Card.actionMinHeight
          Button.cursor Ui.hand
          Ui.accessibleName label
          Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always)
          Button.content (
              GlassPanel.create
                  [ GlassPanel.baseRadius DashboardLogic.Card.actionRadius
                    GlassPanel.elevation 0
                    GlassPanel.highlightScale cardHighlightScale
                    // The whole hairline inside the panel, so the corners are not
                    // half-clipped. Same correction the Planner's action row got.
                    GlassPanel.borderInset DashboardLogic.Card.borderInset
                    GlassPanel.tint (actionTint ())
                    GlassPanel.child (
                        // Icon above label, both centred as one group on both
                        // axes — not left-aligned, not top-aligned.
                        StackPanel.create
                            [ StackPanel.spacing 9.0
                              StackPanel.horizontalAlignment HorizontalAlignment.Center
                              StackPanel.verticalAlignment VerticalAlignment.Center
                              StackPanel.children
                                  [ TextBlock.create
                                        [ TextBlock.text icon
                                          TextBlock.fontSize 18.0
                                          TextBlock.foreground (Theme.brush (Ui.hexOf tokens.Accent.Normal))
                                          TextBlock.horizontalAlignment HorizontalAlignment.Center ]
                                    TextBlock.create
                                        [ TextBlock.text label
                                          TextBlock.fontSize Theme.Type.actionLabel
                                          TextBlock.foreground (Theme.text ())
                                          TextBlock.textAlignment TextAlignment.Center
                                          TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                          TextBlock.horizontalAlignment HorizontalAlignment.Center ] ] ]
                    ) ]
          ) ]
    :> IView

/// One timeline row: time, status glyph, title + detail, status word.
let private timelineRow (tokens: Tokens) (item: DashboardLogic.TimelineItem) (dispatch: Msg -> unit) : IView =
    let hex = statusHex tokens item.Status

    DockPanel.create
        // The height the timeline card's viewport arithmetic is derived from.
        // Read from one place so a change here moves the viewport with it.
        [ DockPanel.minHeight DashboardLogic.Timeline.rowHeight
          DockPanel.children
              [ TextBlock.create
                    [ DockPanel.dock Dock.Left
                      TextBlock.text (Format.timeRange item.StartsAt item.EndsAt)
                      TextBlock.width 104.0
                      TextBlock.fontSize Theme.Type.rowMeta
                      TextBlock.foreground (Theme.muted ())
                      TextBlock.verticalAlignment VerticalAlignment.Center ]

                Border.create
                    [ DockPanel.dock Dock.Right
                      Border.verticalAlignment VerticalAlignment.Center
                      Border.child (Ui.pill (DashboardLogic.ItemStatus.label item.Status) hex) ]

                StackPanel.create
                    [ StackPanel.orientation Orientation.Horizontal
                      StackPanel.spacing 8.0
                      StackPanel.children
                          [ // A session can be completed, so it gets a real
                            // control; an event cannot, so it gets a kind dot
                            // rather than something that looks interactive.
                            (match item.Session with
                             | Some s ->
                                 Ui.checkGlyph s.Completed (fun () -> dispatch (ToggleSession(s.Id, not s.Completed)))
                             | None ->
                                 Ui.colorDot (item.Kind |> Option.map Theme.eventKindHex |> Option.defaultValue hex))
                            StackPanel.create
                                [ StackPanel.spacing 1.0
                                  StackPanel.children
                                      [ TextBlock.create
                                            [ TextBlock.text item.Title
                                              TextBlock.fontSize Theme.Type.rowTitle
                                              TextBlock.foreground (Theme.text ())
                                              TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                              TextBlock.textDecorations (
                                                  match item.Session with
                                                  | Some s when s.Completed -> TextDecorations.Strikethrough
                                                  | _ -> null
                                              ) ]
                                        Ui.faintText item.Detail ] ] ] ] ] ]
    :> IView

let private headerBlock (tokens: Tokens) (model: Model) (data: DashboardData) : IView =
    let plan = DashboardLogic.planPercent data

    // Weather has no data source yet. The redesign labels it "Coming soon", so
    // it is rendered as a deliberate unavailable state that holds its place in
    // the header rather than being omitted and shifting the dial.
    let weatherChip =
        Border.create
            [ Border.background (Theme.brush (Theme.withAlphaHex "0F" (Ui.hexOf tokens.Accent.Normal)))
              Border.borderBrush (Theme.brush (Theme.withAlphaHex "1F" (Ui.hexOf tokens.Accent.Normal)))
              Border.borderThickness 1.0
              Border.cornerRadius (Tokens.radius tokens 12.0)
              Border.padding (16.0, 10.0)
              Border.verticalAlignment VerticalAlignment.Center
              Border.child (
                  StackPanel.create
                      [ StackPanel.orientation Orientation.Horizontal
                        StackPanel.spacing 10.0
                        StackPanel.children
                            [ TextBlock.create
                                  [ TextBlock.text "☀"
                                    TextBlock.fontSize 17.0
                                    TextBlock.foreground (Theme.muted ())
                                    TextBlock.verticalAlignment VerticalAlignment.Center ]
                              StackPanel.create
                                  [ StackPanel.spacing 1.0
                                    StackPanel.children
                                        [ TextBlock.create
                                              [ TextBlock.text "Weather"
                                                TextBlock.fontSize Theme.Type.metricLabel
                                                TextBlock.foreground (Theme.muted ()) ]
                                          TextBlock.create
                                              [ TextBlock.text "COMING SOON"
                                                TextBlock.fontSize 9.5
                                                TextBlock.fontWeight FontWeight.SemiBold
                                                TextBlock.foreground (Theme.faint ()) ] ] ] ] ]
              ) ]

    DockPanel.create
        [ DockPanel.children
              [ StackPanel.create
                    [ DockPanel.dock Dock.Right
                      StackPanel.orientation Orientation.Horizontal
                      StackPanel.spacing 20.0
                      StackPanel.verticalAlignment VerticalAlignment.Center
                      StackPanel.margin (24.0, 0.0, 0.0, 0.0)
                      StackPanel.children
                          [ weatherChip
                            // An em dash, not "0%", when there is no plan to
                            // measure. Same treatment the Planner dial already
                            // uses for an unevaluated plan.
                            Ui.ringGauge
                                76.0
                                (plan |> Option.defaultValue 0)
                                tokens.Accent
                                (match plan with
                                 | Some value -> sprintf "%d%%" value
                                 | None -> "—")
                                "Today's plan" ] ]

                // Left-aligned, explicitly. Without this the panel's MaxWidth
                // makes the DockPanel's fill child centre itself, which is what
                // put the greeting in the middle of the page.
                StackPanel.create
                    [ StackPanel.spacing 6.0
                      StackPanel.horizontalAlignment HorizontalAlignment.Left
                      StackPanel.verticalAlignment VerticalAlignment.Center
                      StackPanel.maxWidth tokens.ReadingWidth
                      StackPanel.children
                          [ TextBlock.create
                                [ TextBlock.text (Format.greeting data.Now model.UserName)
                                  TextBlock.fontSize Theme.Type.pageTitle
                                  TextBlock.fontWeight FontWeight.Bold
                                  TextBlock.foreground (Theme.text ())
                                  TextBlock.textWrapping TextWrapping.Wrap ]
                            TextBlock.create
                                [ TextBlock.text (Format.fullDate data.Now)
                                  TextBlock.fontSize Theme.Type.pageMeta
                                  TextBlock.foreground (Theme.muted ()) ]
                            // The redesign's accent-coloured insight line.
                            TextBlock.create
                                [ TextBlock.text (DashboardLogic.contextLine data)
                                  TextBlock.fontSize Theme.Type.pageLead
                                  TextBlock.foreground (Theme.brush (Ui.hexOf tokens.Accent.Light))
                                  TextBlock.textWrapping TextWrapping.Wrap ]
                            if model.Refreshing then
                                Ui.faintText "Refreshing…" ] ] ] ]
    :> IView

let private quickActions (tokens: Tokens) (columns: int) (dispatch: Msg -> unit) : IView =
    // The set and its order are data (DashboardLogic.quickActions); the view's
    // only job is to map an id onto a dispatch. "Focus Session" opens the
    // Planner, where study sessions are created and started — the genuine
    // equivalent, rather than a button that does nothing.
    let handlerFor (id: DashboardLogic.QuickActionId) =
        match id with
        | DashboardLogic.Capture -> fun () -> dispatch RequestCapture
        | DashboardLogic.AskAssistant -> fun () -> dispatch (RequestGoto Page.Chat)
        | DashboardLogic.FocusSession -> fun () -> dispatch (RequestGoto Page.Planner)
        | DashboardLogic.UploadDocs -> fun () -> dispatch (RequestGoto Page.Documents)
        | DashboardLogic.GeneratePlanner -> fun () -> dispatch RequestRegeneratePlan
        | DashboardLogic.TodaysNotes -> fun () -> dispatch (RequestGoto Page.Notes)

    // Equal star columns rather than fixed widths, so the cards share the row
    // and stay broad. They reflow 6 / 3 / 2 instead of shrinking into chips.
    // (FuncUI 1.5.1 exposes no UniformGrid.)
    let perRow = DashboardLogic.actionsPerRow columns
    let gap = 12.0
    let columnDefs = List.replicate perRow "1*" |> String.concat (sprintf ",%f," gap)

    let rows =
        DashboardLogic.quickActions
        |> List.chunkBySize perRow
        |> List.map (fun chunk ->
            Grid.create
                [ Grid.columnDefinitions columnDefs
                  Grid.margin (0.0, 0.0, 0.0, gap)
                  Grid.children
                      (chunk
                       |> List.mapi (fun i action ->
                           Border.create
                               [ Grid.column (i * 2)
                                 Border.verticalAlignment VerticalAlignment.Stretch
                                 Border.child (
                                     quickActionCard tokens action.Icon action.Label (handlerFor action.Id)
                                 ) ]
                           :> IView)) ]
            :> IView)

    StackPanel.create [ StackPanel.children rows ] :> IView

/// Equal columns, one child each.
///
/// A Grid rather than a WrapPanel: these are label/value pairs that must line up
/// as a row, and a WrapPanel gives each child its desired width, so a long value
/// pushes the rest out of alignment.
let private evenRow (children: IView list) : IView =
    let n = max 1 children.Length

    Grid.create
        [ Grid.columnDefinitions (List.replicate n "1*" |> String.concat ",")
          Grid.children (children |> List.mapi (fun i c -> Border.create [ Grid.column i; Border.child c ] :> IView)) ]
    :> IView

/// One fact about the recommended task: a faint label over a real value.
let private factCell (label: string, value: string) : IView =
    StackPanel.create
        [ StackPanel.spacing 2.0
          StackPanel.children
              [ Ui.faintText (label.ToUpperInvariant())
                TextBlock.create
                    [ TextBlock.text value
                      TextBlock.fontSize Theme.Type.rowTitle
                      TextBlock.foreground (Theme.text ())
                      TextBlock.textWrapping TextWrapping.Wrap ] ] ]

let private briefingCard (tokens: Tokens) (data: DashboardData) (dispatch: Msg -> unit) : IView =
    let facts = DashboardLogic.recommendationFacts data

    glassCard
        tokens
        [ Ui.sectionTitle "Morning briefing"

          (match data.Recommendation with
           | Some(task, reason) ->
               StackPanel.create
                   [ StackPanel.spacing 6.0
                     StackPanel.children
                         [ Ui.faintText "RECOMMENDED FIRST TASK"
                           StackPanel.create
                               [ StackPanel.orientation Orientation.Horizontal
                                 StackPanel.spacing 10.0
                                 StackPanel.children
                                     [ TextBlock.create
                                           [ TextBlock.text task.Title
                                             TextBlock.fontSize Theme.fontLg
                                             TextBlock.fontWeight FontWeight.SemiBold
                                             TextBlock.foreground (Theme.text ())
                                             TextBlock.textWrapping TextWrapping.Wrap ]
                                       (match task.ModuleCode with
                                        | Some c -> Ui.pill c (Ui.hexOf tokens.Accent.Normal)
                                        | None -> Border.create []) ] ]
                           Ui.subtle reason

                           // **Why the card is no longer mostly empty.** It said
                           // what to start and gave one reason line, so beside a
                           // full timeline it was a headline over blank glass —
                           // and the user had nothing to check the
                           // recommendation against. These are the recommended
                           // task's own fields, not filler and not a second copy
                           // of another card: Priority lists deadlines across
                           // assignments, Progress measures study time, and
                           // neither says anything about *this* task.
                           if not facts.IsEmpty then
                               evenRow (facts |> List.map factCell) ] ]
               :> IView
           | None -> Ui.subtle "Nothing urgent. Add assignments or capture work to get a recommendation.")

          Ui.divider ()

          evenRow (DashboardLogic.briefingTiles data |> List.map (fun (value, label) -> Ui.statTile value label))

          WrapPanel.create
              [ WrapPanel.orientation Orientation.Horizontal
                WrapPanel.children
                    [ Border.create
                          [ Border.margin (0.0, 0.0, 8.0, 0.0)
                            Border.child (Ui.primaryButton "Open planner" (fun () -> dispatch (RequestGoto Page.Planner))) ]
                      Border.create
                          [ Border.margin (0.0, 0.0, 8.0, 0.0)
                            Border.child (Ui.ghostButton "Regenerate plan" (fun () -> dispatch RequestRegeneratePlan)) ]
                      Ui.ghostButton "Ask Assistant" (fun () -> dispatch (RequestGoto Page.Chat)) ] ] ]

let private timelineCard (tokens: Tokens) (data: DashboardData) (dispatch: Msg -> unit) : IView =
    let items = DashboardLogic.timeline data

    glassCard
        tokens
        [ Ui.sectionTitle "Today's timeline"
          if items.IsEmpty then
              Ui.subtle "Nothing scheduled today — the planner can fill your free time."
              Ui.ghostButton "Generate a plan" (fun () -> dispatch RequestRegeneratePlan)
          else
              // **Five rows visible, every row present.**
              //
              // The viewport is what stops at five — the list does not. A
              // truncated model would put the last item of a busy day
              // permanently out of reach, and the card would be lying about
              // what is scheduled. `scrollNoChrome` is `Hidden`, not
              // `Disabled`: wheel, trackpad, keyboard and touch all still
              // scroll, only the bar is suppressed.
              Border.create
                  [ Border.maxHeight DashboardLogic.Timeline.viewportHeight
                    Border.child (
                        Ui.scrollNoChrome (
                            StackPanel.create
                                [ StackPanel.spacing DashboardLogic.Timeline.rowSpacing
                                  StackPanel.children (items |> List.map (fun i -> timelineRow tokens i dispatch)) ]
                        )
                    ) ]

              // A list that scrolls with no scrollbar and no hint looks like a
              // list that ends.
              if DashboardLogic.Timeline.scrolls items.Length then
                  Ui.faintText (
                      sprintf "%d more today — scroll for the rest" (DashboardLogic.Timeline.hiddenCount items.Length)
                  ) ]

let private priorityCard (tokens: Tokens) (data: DashboardData) (dispatch: Msg -> unit) : IView =
    let lines = DashboardLogic.priorities data

    glassCard
        tokens
        [ Ui.sectionTitle "Priority"
          if lines.IsEmpty then
              // An intentional all-clear rather than an empty box: the card
              // keeps its place in the row and says why it is quiet.
              StackPanel.create
                  [ StackPanel.spacing 6.0
                    StackPanel.minHeight 132.0
                    StackPanel.children
                        [ TextBlock.create
                              [ TextBlock.text "All clear"
                                TextBlock.fontSize Theme.Type.cardTitle
                                TextBlock.fontWeight FontWeight.SemiBold
                                TextBlock.foreground (Theme.text ()) ]
                          Ui.subtle "No near deadlines, and the inbox is empty." ] ]
          else
              StackPanel.create
                  [ StackPanel.spacing 8.0
                    StackPanel.children
                        (lines
                         |> List.map (fun line ->
                             StackPanel.create
                                 [ StackPanel.orientation Orientation.Horizontal
                                   StackPanel.spacing 8.0
                                   StackPanel.children
                                       [ Ui.colorDot (urgencyHex line.Urgency)
                                         StackPanel.create
                                             [ StackPanel.spacing 1.0
                                               StackPanel.children
                                                   [ TextBlock.create
                                                         [ TextBlock.text line.Text
                                                           TextBlock.fontSize Theme.Type.cardBody
                                                           TextBlock.foreground (Theme.text ())
                                                           TextBlock.textWrapping TextWrapping.Wrap ]
                                                     // The word, not only the colour.
                                                     Ui.faintText (DashboardLogic.Urgency.label line.Urgency) ] ] ] ]
                             :> IView)) ]

          // Deadlines live INSIDE Priority, as in the redesign, rather than in
          // a separate card that would halve the three-column row.
          if not data.UpcomingDeadlines.IsEmpty then
              Ui.divider ()
              Ui.faintText "UPCOMING DEADLINES"

              StackPanel.create
                  [ StackPanel.spacing 6.0
                    StackPanel.children
                        (data.UpcomingDeadlines
                         |> List.truncate 4
                         |> List.map (fun (a, code) ->
                             let deadline = a.Deadline |> Option.defaultValue data.Now

                             DockPanel.create
                                 [ DockPanel.children
                                       [ TextBlock.create
                                             [ DockPanel.dock Dock.Right
                                               TextBlock.text (Format.deadline data.Now deadline)
                                               TextBlock.fontSize Theme.Type.rowMeta
                                               TextBlock.fontWeight FontWeight.SemiBold
                                               TextBlock.foreground (
                                                   Theme.brush (urgencyHex (DashboardLogic.urgencyOf data.Now deadline))
                                               )
                                               TextBlock.verticalAlignment VerticalAlignment.Center ]
                                         StackPanel.create
                                             [ StackPanel.orientation Orientation.Horizontal
                                               StackPanel.spacing 6.0
                                               StackPanel.children
                                                   [ (match code with
                                                      | Some c -> Ui.pill c (Ui.hexOf tokens.Accent.Normal)
                                                      | None -> Border.create [])
                                                     TextBlock.create
                                                         [ TextBlock.text (Format.truncate 30 a.Title)
                                                           TextBlock.fontSize Theme.Type.rowMeta
                                                           TextBlock.foreground (Theme.muted ())
                                                           TextBlock.verticalAlignment VerticalAlignment.Center ] ] ] ] ]
                             :> IView)) ]

              Ui.smallButton "Open assignments →" (fun () -> dispatch (RequestGoto Page.Assignments)) ]

let private progressCard (tokens: Tokens) (data: DashboardData) : IView =
    glassCard
        tokens
        [ Ui.sectionTitle "Study progress"

          // Same visual language as the header dial: same component, same
          // accent palette, only the size and caption differ.
          Ui.ringGauge
              96.0
              (DashboardLogic.dailyTargetPercent data)
              tokens.Accent
              (Format.duration data.StudiedMinutesToday)
              (sprintf "Studied today, of a %s target" (Format.duration data.DailyTargetMinutes))

          StackPanel.create
              [ StackPanel.spacing 4.0
                StackPanel.children
                    [ DockPanel.create
                          [ DockPanel.children
                                [ TextBlock.create
                                      [ DockPanel.dock Dock.Right
                                        TextBlock.text (
                                            sprintf
                                                "%s / %s"
                                                (Format.duration data.StudiedMinutesThisWeek)
                                                (Format.duration data.WeeklyTargetMinutes)
                                        )
                                        TextBlock.fontSize Theme.fontXs
                                        TextBlock.foreground (Theme.text ()) ]
                                  Ui.faintText "Weekly target" ] ]
                      Ui.progressBar (DashboardLogic.weeklyTargetPercent data) (Ui.hexOf tokens.Accent.Normal) ] ]

          if not data.ModuleProgress.IsEmpty then
              Ui.divider ()

              StackPanel.create
                  [ StackPanel.spacing 6.0
                    StackPanel.children
                        (data.ModuleProgress
                         |> List.truncate 5
                         |> List.map (fun (code, pct) ->
                             StackPanel.create
                                 [ StackPanel.spacing 3.0
                                   StackPanel.children
                                       [ DockPanel.create
                                             [ DockPanel.children
                                                   [ TextBlock.create
                                                         [ DockPanel.dock Dock.Right
                                                           TextBlock.text (sprintf "%d%%" pct)
                                                           TextBlock.fontSize Theme.fontXs
                                                           TextBlock.foreground (Theme.muted ()) ]
                                                     TextBlock.create
                                                         [ TextBlock.text code
                                                           TextBlock.fontSize Theme.fontXs
                                                           TextBlock.foreground (Theme.text ()) ] ] ]
                                         Ui.progressBar pct (Ui.hexOf tokens.Accent.Normal) ] ]
                             :> IView)) ] ]

let private insightsCard (tokens: Tokens) (data: DashboardData) : IView =
    let items = DashboardLogic.insights data

    glassCard
        tokens
        [ Ui.sectionTitle "Insights"
          if items.IsEmpty then
              Ui.subtle "Not enough data yet — plan some study or add an assignment."
          else
              StackPanel.create
                  [ StackPanel.spacing 8.0
                    StackPanel.children
                        (items
                         |> List.map (fun t ->
                             StackPanel.create
                                 [ StackPanel.orientation Orientation.Horizontal
                                   StackPanel.spacing 8.0
                                   StackPanel.children
                                       [ TextBlock.create
                                             [ TextBlock.text "✦"
                                               TextBlock.fontSize Theme.fontSm
                                               TextBlock.foreground (Theme.brush (Ui.hexOf tokens.Accent.Normal)) ]
                                         TextBlock.create
                                             [ TextBlock.text t
                                               TextBlock.fontSize Theme.fontSm
                                               TextBlock.foreground (Theme.text ())
                                               TextBlock.textWrapping TextWrapping.Wrap ] ] ]
                             :> IView)) ] ]

let private activityCard (tokens: Tokens) (data: DashboardData) (dispatch: Msg -> unit) : IView =
    glassCard
        tokens
        [ DockPanel.create
              [ DockPanel.children
                    [ Border.create
                          [ DockPanel.dock Dock.Right
                            Border.child (
                                Ui.smallButton "Open timeline →" (fun () -> dispatch (RequestGoto Page.Timeline))
                            ) ]
                      Ui.sectionTitle "Recent activity" ] ]

          if data.RecentActivity.IsEmpty then
              Ui.subtle "Nothing recorded yet."
          else
              StackPanel.create
                  [ StackPanel.spacing 8.0
                    StackPanel.children
                        (data.RecentActivity
                         |> List.map (fun entry ->
                             DockPanel.create
                                 [ DockPanel.children
                                       [ TextBlock.create
                                             [ DockPanel.dock Dock.Right
                                               TextBlock.text (Format.relativeTime data.Now entry.OccurredAt)
                                               TextBlock.fontSize Theme.fontXs
                                               TextBlock.foreground (Theme.faint ())
                                               TextBlock.verticalAlignment VerticalAlignment.Center ]
                                         TextBlock.create
                                             [ TextBlock.text entry.Title
                                               TextBlock.fontSize Theme.fontSm
                                               TextBlock.foreground (Theme.text ())
                                               TextBlock.textTrimming TextTrimming.CharacterEllipsis ] ] ]
                             :> IView)) ] ]

let private snapshotCard (tokens: Tokens) (data: DashboardData) : IView =
    glassCard
        tokens
        [ Ui.sectionTitle "Productivity snapshot"
          WrapPanel.create
              [ WrapPanel.orientation Orientation.Horizontal
                WrapPanel.children
                    (DashboardLogic.snapshot data
                     |> List.map (fun (value, label) ->
                         Border.create
                             [ Border.width 132.0
                               Border.margin (0.0, 0.0, 10.0, 10.0)
                               Border.child (Ui.statTile value label) ]
                         :> IView)) ] ]

/// Lays cards out in rows of `columns`, each row its own Grid.
///
/// One card per grid cell — not a stack of cards per column. A Grid row sizes
/// to its tallest child and stretches the rest, so cards sharing a row match
/// height automatically; stacking several cards into one column instead made
/// each size to its own content, which is why Priority sat short beside Study
/// Progress. When the layout collapses to one column the rows are single-cell
/// and heights go back to content-driven, which is what a stacked layout wants.
let private cardGrid (columns: int) (spacing: float) (cards: IView list) : IView =
    let n = max 1 columns
    let columnDefs = List.replicate n "1*" |> String.concat (sprintf ",%f," spacing)

    let rows =
        cards
        |> DashboardLogic.cardRows n
        |> List.map (fun chunk ->
            Grid.create
                [ Grid.columnDefinitions columnDefs
                  Grid.margin (0.0, 0.0, 0.0, spacing)
                  Grid.children
                      (chunk
                       |> List.mapi (fun i card ->
                           Border.create
                               [ Grid.column (i * 2)
                                 Border.verticalAlignment VerticalAlignment.Stretch
                                 Border.child card ]
                           :> IView)) ]
            :> IView)

    StackPanel.create [ StackPanel.children rows ] :> IView

let view (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let content: IView =
        match model.Data with
        | None when model.Loading ->
            // Mirrors the loaded layout so nothing jumps when data lands.
            StackPanel.create
                [ StackPanel.spacing 16.0
                  StackPanel.children
                      [ Ui.skeletonBlock 280.0 30.0
                        Ui.skeletonBlock 180.0 14.0
                        Grid.create
                            [ Grid.columnDefinitions "2*,16,1*"
                              Grid.children
                                  [ StackPanel.create
                                        [ Grid.column 0
                                          StackPanel.spacing 16.0
                                          StackPanel.children [ Ui.skeletonCard 5; Ui.skeletonCard 3 ] ]
                                    StackPanel.create
                                        [ Grid.column 2
                                          StackPanel.spacing 16.0
                                          StackPanel.children [ Ui.skeletonCard 4; Ui.skeletonCard 3 ] ] ] ] ] ]
            :> IView

        | None ->
            // A failure gets a real retry, not a skeleton that never resolves.
            StackPanel.create
                [ StackPanel.spacing 12.0
                  StackPanel.children
                      [ Ui.emptyState "!" "Could not load the dashboard" (model.Error |> Option.defaultValue "")
                        StackPanel.create
                            [ StackPanel.orientation Orientation.Horizontal
                              StackPanel.horizontalAlignment HorizontalAlignment.Center
                              StackPanel.children [ Ui.primaryButton "Try again" (fun () -> dispatch Load) ] ] ] ]
            :> IView

        | Some data ->
            let spacing = Tokens.space tokens 22.0

            /// A two-column row with an explicit weight ratio, collapsing to a
            /// stack when there is not enough width for both to stay readable.
            // Both children stretch, so the pair matches the taller of the two.
            let pairRow (leftWeight: int) (rightWeight: int) (left: IView) (right: IView) : IView =
                if model.Columns >= 2 then
                    Grid.create
                        [ Grid.columnDefinitions (sprintf "%d*,%f,%d*" leftWeight spacing rightWeight)
                          Grid.children
                              [ Border.create
                                    [ Grid.column 0
                                      Border.verticalAlignment VerticalAlignment.Stretch
                                      Border.child left ]
                                Border.create
                                    [ Grid.column 2
                                      Border.verticalAlignment VerticalAlignment.Stretch
                                      Border.child right ] ] ]
                    :> IView
                else
                    StackPanel.create [ StackPanel.spacing spacing; StackPanel.children [ left; right ] ]
                    :> IView

            StackPanel.create
                [ StackPanel.spacing spacing
                  StackPanel.children
                      [ headerBlock tokens model data
                        quickActions tokens model.Columns dispatch

                        // Primary row: briefing two-thirds, timeline one-third.
                        pairRow 2 1 (briefingCard tokens data dispatch) (timelineCard tokens data dispatch)

                        // Secondary row: exactly the redesign's three columns.
                        // Deadlines are folded INTO Priority rather than
                        // becoming a fourth card that halves two of them.
                        cardGrid
                            model.Columns
                            spacing
                            [ priorityCard tokens data dispatch
                              progressCard tokens data
                              insightsCard tokens data ]

                        // Lower row, and the last: wide activity, snapshot
                        // beside it. The page ends here, as the redesign does.
                        pairRow 2 1 (activityCard tokens data dispatch) (snapshotCard tokens data) ] ]
            :> IView

    // One size handler for the page, dispatching only when the column count
    // would actually change — a message per pixel of a resize drag would
    // rebuild the whole card tree continuously.
    Border.create
        [ Border.onSizeChanged (
              (fun args ->
                  let next = DashboardLogic.columnsFor args.NewSize.Width

                  if next <> model.Columns then
                      dispatch (Resized next)),
              SubPatchOptions.Always
          )
          Border.child (Ui.scrollNoChrome content) ]
    :> IView
