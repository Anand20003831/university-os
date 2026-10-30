namespace UniversityOS.App

/// The application shell: sidebar navigation, page hosting, the global capture
/// overlay, the command palette, keyboard shortcuts, drag-and-drop capture and
/// toasts. Pages reload on navigation (see docs/ARCHITECTURE.md §3).
module Shell =

    open System
    open Elmish
    open Avalonia.Controls
    open Avalonia.Input
    open Avalonia.Layout
    open Avalonia.Media
    open Avalonia.Platform.Storage
    open Avalonia.FuncUI
    open Avalonia.FuncUI.DSL
    open Avalonia.FuncUI.Types
    open Avalonia.FuncUI.Hosts
    open UniversityOS.Domain
    open UniversityOS.App.Design
    open UniversityOS.App.Rendering
    open UniversityOS.App.Controls
    open UniversityOS.App.Features

    type Model =
        { Page: Page
          /// Persisted UI state (sidebar density/pin, briefing dates, …).
          Ui: UiState
          /// Sidebar interaction state. Deliberately not persisted: "the
          /// pointer is near the left edge" is not a preference. Only the
          /// pinned/floating choice is mirrored into UiState.
          SidebarState: SidebarState
          /// Monotonic source of hide tokens. A delayed hide carries the token
          /// it was scheduled with, so one that has been overtaken by a
          /// re-entry or a pin can be recognised and ignored rather than
          /// cancelled — Elmish commands cannot be unscheduled.
          NextHideToken: int
          /// Pin transition phase: 0 docked, 1 floating. One scalar drives the
          /// inset, radius, glass blend, shadow and the content's left offset,
          /// so they cannot disagree mid-flight. Transient — never persisted.
          SidebarPhase: float
          Dashboard: Dashboard.Model
          Planner: Planner.Model
          Inbox: InboxView.Model
          Assignments: AssignmentsView.Model
          Modules: ModulesView.Model
          Notes: NotesView.Model
          Chat: ChatView.Model
          Search: SearchView.Model
          Calendar: CalendarView.Model
          Documents: DocumentsView.Model
          Connections: ConnectionsView.Model
          Settings: SettingsView.Model
          Timeline: TimelineView.Model
          Graph: GraphView.Model
          Lectures: LecturesView.Model
          DesignPreview: DesignPreview.Model
          /// Persisted appearance and the tokens derived from it. One record in,
          /// one token set out — every surface reads these.
          Appearance: AppearanceSettings
          Tokens: Tokens
          Briefing: UniversityOS.Application.MorningBriefing option
          BriefingOpen: bool
          Review: UniversityOS.Application.EveningReview option
          ReviewOpen: bool
          CaptureOpen: bool
          CaptureText: string
          CaptureBusy: bool
          PaletteOpen: bool
          PaletteQuery: string
          Toast: string option }

    type Msg =
        | Ignore
        | Navigate of Page
        | UiStateLoaded of UiState
        /// Expanded ↔ Compact. Independent of whether the sidebar is showing.
        | ToggleSidebarDensity
        /// Docked-and-reserving-space ↔ floating-and-auto-hiding. Carries
        /// whether the pointer is over the sidebar at the moment of the change,
        /// which the caller always knows: the pin control lives inside the
        /// panel, the palette command does not.
        | SetSidebarPinned of pinned: bool * pointerOverSidebar: bool
        /// Pointer entered — or moved within — the combined interaction region.
        | SidebarPointerEntered
        /// Pointer left the combined interaction region entirely.
        | SidebarPointerExited
        /// A scheduled hide came due. Acts only if it is still the current one.
        | SidebarHideElapsed of token: int
        /// Developer-only reveal toggle (Ctrl+Shift+B).
        | DevToggleSidebarReveal
        /// One frame of the pin transition, carrying real elapsed seconds.
        | SidebarTransitionTick of deltaSeconds: float
        | DashboardMsg of Dashboard.Msg
        | PlannerMsg of Planner.Msg
        | InboxMsg of InboxView.Msg
        | AssignmentsMsg of AssignmentsView.Msg
        | ModulesMsg of ModulesView.Msg
        | NotesMsg of NotesView.Msg
        | ChatMsg of ChatView.Msg
        | SearchMsg of SearchView.Msg
        | CalendarMsg of CalendarView.Msg
        | DocumentsMsg of DocumentsView.Msg
        | ConnectionsMsg of ConnectionsView.Msg
        | SettingsMsg of SettingsView.Msg
        | TimelineMsg of TimelineView.Msg
        | GraphMsg of GraphView.Msg
        | LecturesMsg of LecturesView.Msg
        | DesignPreviewMsg of DesignPreview.Msg
        | AppearanceLoaded of AppearanceSettings
        /// Appearance changed by the user: persist it and republish the tokens.
        | ApplyAppearance of AppearanceSettings
        /// The OS light/dark preference changed. Only matters under "System".
        | OsThemeChanged
        | OpenBriefing
        | BriefingLoaded of UniversityOS.Application.MorningBriefing
        | CloseBriefing
        | OpenReview
        | ReviewLoaded of UniversityOS.Application.EveningReview
        | CloseReview
        | OpenCapture
        | CloseCapture
        | SetCaptureText of string
        | SubmitCapture
        | CaptureFiles
        | FilesPicked of string list
        | CaptureDone of Result<string, string>
        | OpenPalette
        | ClosePalette
        | SetPaletteQuery of string
        | RunFirstPaletteCommand
        | ShowToast of string
        | ClearToast

    let init (services: AppServices) : Model * Cmd<Msg> =
        let dashboard, dashboardCmd = Dashboard.init services
        let planner, plannerCmd = Planner.init services
        let inbox, inboxCmd = InboxView.init services
        let assignments, assignmentsCmd = AssignmentsView.init services
        let modules, modulesCmd = ModulesView.init services
        let notes, notesCmd = NotesView.init services
        let chat, chatCmd = ChatView.init services
        let search, searchCmd = SearchView.init services
        let calendar, calendarCmd = CalendarView.init services
        let documents, documentsCmd = DocumentsView.init services
        let connections, connectionsCmd = ConnectionsView.init services
        let settings, settingsCmd = SettingsView.init services
        let timeline, timelineCmd = TimelineView.init services
        let graph, graphCmd = GraphView.init services
        let lectures, lecturesCmd = LecturesView.init services
        let designPreview, designPreviewCmd = DesignPreview.init services

        { Page = Page.Dashboard
          Ui = UiState.defaults
          SidebarState = SidebarInteraction.restore UiState.defaults.SidebarPinned
          NextHideToken = 1
          SidebarPhase = SidebarTransition.targetFor UiState.defaults.SidebarPinned
          DesignPreview = designPreview
          Appearance = AppearanceSettings.defaults
          Tokens = Tokens.defaults
          Dashboard = dashboard
          Planner = planner
          Inbox = inbox
          Assignments = assignments
          Modules = modules
          Notes = notes
          Chat = chat
          Search = search
          Calendar = calendar
          Documents = documents
          Connections = connections
          Settings = settings
          Timeline = timeline
          Graph = graph
          Lectures = lectures
          Briefing = None
          BriefingOpen = false
          Review = None
          ReviewOpen = false
          CaptureOpen = false
          CaptureText = ""
          CaptureBusy = false
          PaletteOpen = false
          PaletteQuery = ""
          Toast = None },
        Cmd.batch
            [ Elm.ofAsync (services.UiState.Load()) UiStateLoaded (fun _ -> Ignore)
              Elm.ofAsync (services.Appearance.Load()) AppearanceLoaded (fun _ -> Ignore)
              Cmd.map DesignPreviewMsg designPreviewCmd
              Cmd.map DashboardMsg dashboardCmd
              Cmd.map PlannerMsg plannerCmd
              Cmd.map InboxMsg inboxCmd
              Cmd.map AssignmentsMsg assignmentsCmd
              Cmd.map ModulesMsg modulesCmd
              Cmd.map NotesMsg notesCmd
              Cmd.map ChatMsg chatCmd
              Cmd.map SearchMsg searchCmd
              Cmd.map CalendarMsg calendarCmd
              Cmd.map DocumentsMsg documentsCmd
              Cmd.map ConnectionsMsg connectionsCmd
              Cmd.map SettingsMsg settingsCmd
              Cmd.map TimelineMsg timelineCmd
              Cmd.map GraphMsg graphCmd
              Cmd.map LecturesMsg lecturesCmd ]

    let private toastCmd (message: string) =
        Cmd.batch [ Cmd.ofMsg (ShowToast message); Elm.delayed 4000 ClearToast ]

    // ---- command palette ---------------------------------------------------

    type private Command = { Label: string; Hint: string; Msg: Msg }

    let private commands: Command list =
        [ yield! Page.all |> List.map (fun (page, icon, label) -> { Label = sprintf "%s  Go to %s" icon label; Hint = "navigate"; Msg = Navigate page })
          yield { Label = "＋  Capture anything"; Hint = "Ctrl+N"; Msg = OpenCapture }
          yield { Label = "⟳  Generate study plan"; Hint = "planner"; Msg = PlannerMsg Planner.Generate }
          yield { Label = "☰  Sidebar density"; Hint = "Ctrl+B"; Msg = ToggleSidebarDensity }
          // From the palette the pointer is nowhere near the sidebar, so an
          // unpin here must hide rather than reveal — no exit event would ever
          // arrive to hide it otherwise.
          yield { Label = "⇱  Pin sidebar"; Hint = "sidebar"; Msg = SetSidebarPinned(true, false) }
          yield { Label = "⇲  Unpin sidebar"; Hint = "sidebar"; Msg = SetSidebarPinned(false, false) }
          yield { Label = "☀  Morning briefing"; Hint = "daily"; Msg = OpenBriefing }
          yield { Label = "☾  Evening review"; Hint = "reflect"; Msg = OpenReview } ]

    let private matchingCommands (query: string) =
        let q = query.Trim()

        if q.Length = 0 then commands
        else
            commands
            |> List.filter (fun c -> c.Label.Contains(q, StringComparison.OrdinalIgnoreCase))

    // -------------------------------------------------------------------------

    /// Fire-and-forget persistence of the UI state record.
    let private persistUi (services: AppServices) (ui: UiState) =
        Elm.ofAsync (services.UiState.Save ui) (fun _ -> Ignore) (fun _ -> Ignore)

    /// The ONE place appearance becomes visible state.
    ///
    /// Regenerates the tokens, publishes them to the custom-drawn controls, and
    /// applies the resolved theme to the legacy palette and Avalonia's theme
    /// variant. Theme switching was broken because this last step did not exist:
    /// the preference was stored, persisted and even shown as selected, but
    /// nothing ever told Theme/Fluent about it, so every surface stayed dark.
    ///
    /// Every caller that changes appearance goes through here, so there is
    /// exactly one writer for the applied theme.
    let private publishAppearance (services: AppServices) (generation: int) (appearance: AppearanceSettings) =
        let osPrefersDark = SystemPreferences.osPrefersDark ()
        let tokens = Tokens.ofAppearance osPrefersDark generation appearance
        TokenStore.publish tokens
        // The accent first, then the theme — both are setters on the same
        // resolved material palette, and `ApplyTheme` ends in `Theme.setMode`,
        // so this order leaves the palette rebuilt from BOTH inputs.
        Theme.setAccent tokens.Accent
        services.ApplyTheme tokens.Theme
        tokens

    let update (services: AppServices) (msg: Msg) (model: Model) : Model * Cmd<Msg> =
        match msg with
        | Ignore -> model, Cmd.none

        | UiStateLoaded ui ->
            // First open of the day → show the Morning Briefing automatically.
            let today = services.Clock.Now.ToString "yyyy-MM-dd"

            let briefingCmd =
                if ui.LastBriefingDate <> Some today then Cmd.ofMsg OpenBriefing else Cmd.none

            // Restored state is applied, never animated: the window is opening.
            { model with
                Ui = ui
                SidebarState = SidebarInteraction.restore ui.SidebarPinned
                SidebarPhase = SidebarTransition.targetFor ui.SidebarPinned },
            briefingCmd

        | OpenBriefing ->
            model, Elm.ofAsync (services.Briefing.GetMorning()) BriefingLoaded (fun _ -> Ignore)

        | BriefingLoaded briefing ->
            { model with Briefing = Some briefing; BriefingOpen = true; ReviewOpen = false }, Cmd.none

        | CloseBriefing ->
            let ui =
                { model.Ui with LastBriefingDate = Some(services.Clock.Now.ToString "yyyy-MM-dd") }

            { model with BriefingOpen = false; Ui = ui }, persistUi services ui

        | OpenReview ->
            model, Elm.ofAsync (services.Briefing.GetEvening()) ReviewLoaded (fun _ -> Ignore)

        | ReviewLoaded review ->
            { model with Review = Some review; ReviewOpen = true; BriefingOpen = false }, Cmd.none

        | CloseReview ->
            let ui =
                { model.Ui with LastReviewDate = Some(services.Clock.Now.ToString "yyyy-MM-dd") }

            { model with ReviewOpen = false; Ui = ui }, persistUi services ui

        | ToggleSidebarDensity ->
            let ui =
                { model.Ui with SidebarDensity = SidebarDensity.toggle model.Ui.SidebarDensity }

            { model with Ui = ui }, persistUi services ui

        | SetSidebarPinned(pinned, pointerOverSidebar) ->
            let ui = { model.Ui with SidebarPinned = pinned }

            let state =
                if pinned then
                    SidebarInteraction.pin model.SidebarState
                else
                    SidebarInteraction.unpin pointerOverSidebar model.SidebarState

            // Reduce Motion applies the destination immediately. The transition
            // is the only thing skipped — the state machine, the persistence and
            // the final geometry are identical either way.
            let reduceMotion =
                Motion.reduceMotionActive model.Appearance.ReduceMotion (SystemPreferences.prefersReducedMotion ())

            let phase =
                if reduceMotion then
                    SidebarTransition.settle (SidebarTransition.targetFor pinned)
                else
                    // Left where it is: a click during a transition retargets
                    // the running animation rather than restarting it.
                    model.SidebarPhase

            // Pinning also makes any hide already in flight inert, because the
            // state is no longer HidePending and the token check will reject it.
            { model with Ui = ui; SidebarState = state; SidebarPhase = phase }, persistUi services ui

        | SidebarTransitionTick delta ->
            let target = SidebarTransition.targetFor model.Ui.SidebarPinned
            { model with SidebarPhase = SidebarTransition.step delta model.SidebarPhase target }, Cmd.none

        | SidebarPointerEntered ->
            { model with SidebarState = SidebarInteraction.pointerEntered model.SidebarState }, Cmd.none

        | SidebarPointerExited ->
            let before = model.SidebarState
            let after = SidebarInteraction.pointerExited model.NextHideToken before

            match SidebarInteraction.schedules before after with
            | Some token ->
                // One timer per transition into HidePending. A second exit while
                // one is already pending keeps the original token and schedules
                // nothing, so timers cannot accumulate.
                { model with SidebarState = after; NextHideToken = model.NextHideToken + 1 },
                Elm.delayed ShellLayout.hideDelayMs (SidebarHideElapsed token)
            | None -> { model with SidebarState = after }, Cmd.none

        | SidebarHideElapsed token ->
            { model with SidebarState = SidebarInteraction.hideElapsed token model.SidebarState }, Cmd.none

        | DevToggleSidebarReveal ->
            { model with SidebarState = SidebarInteraction.devToggle model.SidebarState }, Cmd.none

        | Navigate page ->
            Diagnostics.setSurface (Page.title page)

            // Reload-on-navigate keeps cross-feature data fresh without an event bus.
            let model = { model with PaletteOpen = false }

            // **Every screen keeps its measured width.**
            //
            // Each `init` returns an arbitrary default — 1400 or 1600 — which is
            // wrong for the shipped window: 1380px less the sidebar and margins
            // leaves about 1108px. The first visit corrects itself when the
            // screen measures itself, but a later visit cannot: FuncUI diffs the
            // view tree rather than rebuilding it, so the measuring border
            // survives navigation and never raises `SizeChanged` again.
            //
            // Left alone, every responsive screen renders at a width the window
            // does not have after the user navigates away and back — Connections
            // at three columns instead of two, Calendar with the wrong side
            // placement. This is the same defect reported on the Dashboard, and
            // it was never Dashboard-specific.
            //
            // Navigating does not resize the window, so carrying the measurement
            // over is simply keeping what is still true. Same reasoning as
            // `DesignPreview`'s appearance below.

            match page with
            | Page.Dashboard ->
                // `reinit`, not `init`: the data reloads but the viewport-derived
                // column count carries over. Navigating does not resize the
                // window, and nothing would correct a reset value afterwards.
                let m, cmd = Dashboard.reinit model.Dashboard services
                { model with Page = page; Dashboard = m }, Cmd.map DashboardMsg cmd
            | Page.Planner ->
                let m, cmd = Planner.init services
                { model with Page = page; Planner = { m with Width = model.Planner.Width } }, Cmd.map PlannerMsg cmd
            | Page.Inbox ->
                let m, cmd = InboxView.init services
                { model with Page = page; Inbox = { m with Width = model.Inbox.Width } }, Cmd.map InboxMsg cmd
            | Page.Assignments ->
                let m, cmd = AssignmentsView.init services
                { model with Page = page; Assignments = { m with Width = model.Assignments.Width } }, Cmd.map AssignmentsMsg cmd
            | Page.Modules ->
                let m, cmd = ModulesView.init services
                { model with Page = page; Modules = { m with Width = model.Modules.Width } }, Cmd.map ModulesMsg cmd
            | Page.Notes ->
                let m, cmd = NotesView.init services
                { model with Page = page; Notes = { m with Width = model.Notes.Width } }, Cmd.map NotesMsg cmd
            | Page.Chat ->
                let m, cmd = ChatView.init services
                { model with Page = page; Chat = { m with Width = model.Chat.Width } }, Cmd.map ChatMsg cmd
            | Page.Calendar ->
                let m, cmd = CalendarView.init services
                { model with Page = page; Calendar = { m with Width = model.Calendar.Width } }, Cmd.map CalendarMsg cmd
            | Page.Documents ->
                let m, cmd = DocumentsView.init services
                { model with Page = page; Documents = { m with Width = model.Documents.Width } }, Cmd.map DocumentsMsg cmd
            | Page.Connections ->
                let m, cmd = ConnectionsView.init services
                { model with Page = page; Connections = { m with Width = model.Connections.Width } }, Cmd.map ConnectionsMsg cmd
            | Page.Settings ->
                let m, cmd = SettingsView.init services
                { model with Page = page; Settings = { m with Width = model.Settings.Width } }, Cmd.map SettingsMsg cmd
            | Page.Timeline ->
                let m, cmd = TimelineView.init services
                { model with Page = page; Timeline = { m with Width = model.Timeline.Width } }, Cmd.map TimelineMsg cmd
            | Page.Graph ->
                let m, cmd = GraphView.init services
                { model with Page = page; Graph = { m with Width = model.Graph.Width } }, Cmd.map GraphMsg cmd
            | Page.Lectures ->
                let m, cmd = LecturesView.init services
                { model with Page = page; Lectures = { m with Width = model.Lectures.Width } }, Cmd.map LecturesMsg cmd
            | Page.DesignPreview ->
                // Instrumentation switches on only when the developer surface is
                // actually opened, so a normal launch stays uninstrumented.
                Diagnostics.setEnabled true
                let m, cmd = DesignPreview.init services
                // The preview edits live appearance, so seed it from the shell's
                // current record rather than the defaults its init returns.
                { model with
                    Page = page
                    DesignPreview = { m with Appearance = model.Appearance } },
                Cmd.map DesignPreviewMsg cmd
            // Search deliberately keeps its query/results across visits, but the
            // facts each result is drawn with are re-read, so a module renamed
            // elsewhere is not still shown by its old code.
            | Page.Search -> { model with Page = page }, Cmd.ofMsg (SearchMsg SearchView.Load)

        | DashboardMsg dm ->
            let m, cmd, external = Dashboard.update services dm model.Dashboard
            let model = { model with Dashboard = m }

            match external with
            | Some Dashboard.OpenCapture -> { model with CaptureOpen = true }, Cmd.map DashboardMsg cmd
            | Some(Dashboard.Goto page) ->
                model, Cmd.batch [ Cmd.map DashboardMsg cmd; Cmd.ofMsg (Navigate page) ]
            | Some Dashboard.RegeneratePlan ->
                // Reuses the planner's own command and its existing
                // navigate-on-generate behaviour rather than duplicating it.
                model, Cmd.batch [ Cmd.map DashboardMsg cmd; Cmd.ofMsg (PlannerMsg Planner.Generate) ]
            | None -> model, Cmd.map DashboardMsg cmd

        | PlannerMsg pm ->
            let m, cmd = Planner.update services pm model.Planner
            // Palette's "generate plan" should also land the user on the planner.
            let extra = if model.Page <> Page.Planner && pm = Planner.Generate then Cmd.ofMsg (Navigate Page.Planner) else Cmd.none
            { model with Planner = m }, Cmd.batch [ Cmd.map PlannerMsg cmd; extra ]

        | InboxMsg im ->
            let m, cmd = InboxView.update services im model.Inbox
            { model with Inbox = m }, Cmd.map InboxMsg cmd

        | AssignmentsMsg am ->
            let m, cmd, external = AssignmentsView.update services am model.Assignments
            let model = { model with Assignments = m }

            match external with
            | Some(AssignmentsView.Goto page) ->
                model, Cmd.batch [ Cmd.map AssignmentsMsg cmd; Cmd.ofMsg (Navigate page) ]
            | Some(AssignmentsView.ShowInTimeline(entity, title)) ->
                // Reuses the Timeline's own entity-history message rather than
                // duplicating it, so "View Timeline" lands on the assignment's
                // story rather than merely on the screen.
                model,
                Cmd.batch
                    [ Cmd.map AssignmentsMsg cmd
                      Cmd.ofMsg (TimelineMsg(TimelineView.FocusOn(entity, title)))
                      Cmd.ofMsg (Navigate Page.Timeline) ]
            | Some(AssignmentsView.AskAssistantAbout question) ->
                // Pre-fills a real question with this assignment's own figures
                // and navigates. It does not send: the user still asks.
                model,
                Cmd.batch
                    [ Cmd.map AssignmentsMsg cmd
                      // Prefill, not SetInput: text arriving from another screen
                      // is labelled a prefilled prompt rather than passing as
                      // something the user typed.
                      Cmd.ofMsg (ChatMsg(ChatView.Prefill question))
                      Cmd.ofMsg (Navigate Page.Chat) ]
            | None -> model, Cmd.map AssignmentsMsg cmd

        | ModulesMsg mm ->
            let m, cmd, external = ModulesView.update services mm model.Modules
            let model = { model with Modules = m }

            match external with
            | Some(ModulesView.Goto page) ->
                model, Cmd.batch [ Cmd.map ModulesMsg cmd; Cmd.ofMsg (Navigate page) ]
            | None -> model, Cmd.map ModulesMsg cmd

        | NotesMsg nm ->
            let m, cmd, external = NotesView.update services nm model.Notes
            let model = { model with Notes = m }

            match external with
            | Some(NotesView.Goto page) ->
                model, Cmd.batch [ Cmd.map NotesMsg cmd; Cmd.ofMsg (Navigate page) ]
            | None -> model, Cmd.map NotesMsg cmd

        | ChatMsg cm ->
            let m, cmd, external = ChatView.update services cm model.Chat
            let model = { model with Chat = m }

            match external with
            | Some(ChatView.Goto page) -> model, Cmd.batch [ Cmd.map ChatMsg cmd; Cmd.ofMsg (Navigate page) ]
            | None -> model, Cmd.map ChatMsg cmd

        // The Assistant handoff is intercepted here, because it is the shell that
        // owns both screens. It is a **prefill** — words in the composer — and
        // the Assistant labels it as one. Nothing is attached.
        | SearchMsg(SearchView.AskAssistant hit) ->
            let question = SearchLogic.assistantPrompt hit.Kind hit.Title

            model,
            Cmd.batch [ Cmd.ofMsg (ChatMsg(ChatView.Prefill question)); Cmd.ofMsg (Navigate Page.Chat) ]

        | SearchMsg sm ->
            let m, cmd, external = SearchView.update services sm model.Search
            let model = { model with Search = m }

            match external with
            | Some(SearchView.Goto page) ->
                model, Cmd.batch [ Cmd.map SearchMsg cmd; Cmd.ofMsg (Navigate page) ]
            | None -> model, Cmd.map SearchMsg cmd

        | CalendarMsg cm ->
            let m, cmd, external = CalendarView.update services cm model.Calendar
            let model = { model with Calendar = m }

            match external with
            | Some(CalendarView.Goto page) ->
                model, Cmd.batch [ Cmd.map CalendarMsg cmd; Cmd.ofMsg (Navigate page) ]
            | None -> model, Cmd.map CalendarMsg cmd

        | DocumentsMsg dm ->
            let m, cmd, external = DocumentsView.update services dm model.Documents
            let model = { model with Documents = m }

            match external with
            | Some(DocumentsView.Goto page) ->
                model, Cmd.batch [ Cmd.map DocumentsMsg cmd; Cmd.ofMsg (Navigate page) ]
            | None -> model, Cmd.map DocumentsMsg cmd

        | ConnectionsMsg cm ->
            let m, cmd, external = ConnectionsView.update services cm model.Connections
            let model = { model with Connections = m }

            match external with
            | Some(ConnectionsView.Goto page) ->
                model, Cmd.batch [ Cmd.map ConnectionsMsg cmd; Cmd.ofMsg (Navigate page) ]
            | None -> model, Cmd.map ConnectionsMsg cmd

        | SettingsMsg sm ->
            let m, cmd, external = SettingsView.update services sm model.Settings
            let model = { model with Settings = m }

            match external with
            | Some(SettingsView.Goto page) ->
                model, Cmd.batch [ Cmd.map SettingsMsg cmd; Cmd.ofMsg (Navigate page) ]
            | None -> model, Cmd.map SettingsMsg cmd

        | AppearanceLoaded appearance ->
            { model with
                Appearance = appearance
                Tokens = publishAppearance services (model.Tokens.Generation + 1) appearance
                DesignPreview = { model.DesignPreview with Appearance = appearance } },
            Cmd.none

        | ApplyAppearance appearance ->
            // One write, global effect: bump the generation so every derived
            // cache (paints, brushes, backdrop) invalidates, then persist.
            { model with
                Appearance = appearance
                Tokens = publishAppearance services (model.Tokens.Generation + 1) appearance },
            Elm.ofAsync (services.Appearance.Save appearance) (fun _ -> Ignore) (fun _ -> Ignore)

        | OsThemeChanged ->
            // Only "System" cares, but recomputing unconditionally is one cheap
            // record build and keeps the branch honest.
            { model with
                Tokens = publishAppearance services (model.Tokens.Generation + 1) model.Appearance },
            Cmd.none

        | DesignPreviewMsg pm ->
            let m, cmd, external = DesignPreview.update services pm model.DesignPreview
            let model = { model with DesignPreview = m }

            match external with
            | Some(DesignPreview.AppearanceChanged appearance) ->
                model, Cmd.batch [ Cmd.map DesignPreviewMsg cmd; Cmd.ofMsg (ApplyAppearance appearance) ]
            | None -> model, Cmd.map DesignPreviewMsg cmd

        | TimelineMsg tm ->
            let m, cmd, external = TimelineView.update services tm model.Timeline
            let model = { model with Timeline = m }

            match external with
            | Some(TimelineView.Goto page) ->
                model, Cmd.batch [ Cmd.map TimelineMsg cmd; Cmd.ofMsg (Navigate page) ]
            | None -> model, Cmd.map TimelineMsg cmd

        | GraphMsg gm ->
            let m, cmd, external = GraphView.update services gm model.Graph
            let model = { model with Graph = m }

            match external with
            | Some(GraphView.Goto page) ->
                model, Cmd.batch [ Cmd.map GraphMsg cmd; Cmd.ofMsg (Navigate page) ]
            | None -> model, Cmd.map GraphMsg cmd

        | LecturesMsg lm ->
            let m, cmd, external = LecturesView.update services lm model.Lectures
            let model = { model with Lectures = m }

            match external with
            | Some(LecturesView.Goto page) ->
                model, Cmd.batch [ Cmd.map LecturesMsg cmd; Cmd.ofMsg (Navigate page) ]
            | None -> model, Cmd.map LecturesMsg cmd

        | OpenCapture -> { model with CaptureOpen = true; PaletteOpen = false }, Cmd.none
        | CloseCapture -> { model with CaptureOpen = false; CaptureText = "" }, Cmd.none
        | SetCaptureText text -> { model with CaptureText = text }, Cmd.none

        | SubmitCapture when not (String.IsNullOrWhiteSpace model.CaptureText) ->
            { model with CaptureBusy = true },
            Elm.ofAsync
                (async {
                    let! item = services.Capture.CaptureText model.CaptureText
                    return Ok item.Title
                })
                CaptureDone
                (fun ex -> CaptureDone(Error ex.Message))
        | SubmitCapture -> model, Cmd.none

        | CaptureFiles ->
            model, Elm.ofAsync (services.PickFiles()) FilesPicked (fun _ -> FilesPicked [])

        | FilesPicked [] -> model, Cmd.none
        | FilesPicked paths ->
            { model with CaptureBusy = true; CaptureOpen = true },
            Elm.ofAsync
                (async {
                    let mutable imported = 0

                    for path in paths do
                        let! result = services.Capture.CaptureFile path

                        match result with
                        | Ok _ -> imported <- imported + 1
                        | Error _ -> ()

                    return Ok(sprintf "%d file(s) imported and analyzed" imported)
                })
                CaptureDone
                (fun ex -> CaptureDone(Error ex.Message))

        | CaptureDone result ->
            let model =
                { model with CaptureBusy = false; CaptureOpen = false; CaptureText = "" }

            match result with
            | Ok title ->
                model,
                Cmd.batch
                    [ toastCmd (sprintf "Captured: %s" (Format.truncate 40 title))
                      Cmd.ofMsg (Navigate model.Page) ] // refresh current page
            | Error message -> model, toastCmd ("Capture failed: " + message)

        | OpenPalette -> { model with PaletteOpen = true; PaletteQuery = ""; CaptureOpen = false }, Cmd.none
        | ClosePalette -> { model with PaletteOpen = false; PaletteQuery = "" }, Cmd.none
        | SetPaletteQuery query -> { model with PaletteQuery = query }, Cmd.none
        | RunFirstPaletteCommand ->
            match matchingCommands model.PaletteQuery with
            | first :: _ -> { model with PaletteOpen = false; PaletteQuery = "" }, Cmd.ofMsg first.Msg
            | [] -> model, Cmd.none

        | ShowToast message -> { model with Toast = Some message }, Cmd.none
        | ClearToast -> { model with Toast = None }, Cmd.none

    // ---- keyboard ------------------------------------------------------------

    /// The global shortcut policy, in one place.
    ///
    /// Defined here rather than beside the view because it is now attached in two
    /// places, and F# compile order means the subscription below has to be able
    /// to see it.
    /// Which overlays are open — everything the shortcut policy needs to know
    /// about the shell, and nothing else.
    type ShortcutContext =
        { CaptureOpen: bool
          PaletteOpen: bool
          BriefingOpen: bool
          ReviewOpen: bool }

    let shortcutContext (model: Model) =
        { CaptureOpen = model.CaptureOpen
          PaletteOpen = model.PaletteOpen
          BriefingOpen = model.BriefingOpen
          ReviewOpen = model.ReviewOpen }

    /// **The global shortcut policy, as a pure decision.**
    ///
    /// Separated from the handler so it can be tested at all. Attaching a routed
    /// handler needs a live Avalonia window, but *what a key press should do* is
    /// an ordinary function, and that is where the policy can actually go wrong.
    ///
    /// Returns the messages to dispatch and whether the press is consumed.
    /// Escape deliberately does **not** consume: it closes whatever is open and
    /// lets anything else that cares still see it.
    let resolveShortcut (ctx: ShortcutContext) (ctrl: bool) (shift: bool) (key: Key) : Msg list * bool =
        if ctrl && key = Key.K then
            [ OpenPalette ], true
        elif ctrl && key = Key.N then
            [ OpenCapture ], true
        elif ctrl && key = Key.F then
            [ Navigate Page.Search ], true
        elif ctrl && shift && key = Key.B then
            // Developer aid for reviewing the floating sidebar without a mouse.
            // Keyboard only — it drives the same state machine the pointer does,
            // it does not synthesise pointer input.
            [ DevToggleSidebarReveal ], true
        elif ctrl && key = Key.B then
            [ ToggleSidebarDensity ], true
        elif ctrl then
            // Ctrl+1..9 jump to the first nine sections.
            let digits = [ Key.D1; Key.D2; Key.D3; Key.D4; Key.D5; Key.D6; Key.D7; Key.D8; Key.D9 ]

            match digits |> List.tryFindIndex (fun k -> k = key) with
            | Some index when index < Page.all.Length ->
                let page, _, _ = Page.all.[index]
                [ Navigate page ], true
            | _ -> [], false
        elif key = Key.Escape then
            [ if ctx.CaptureOpen then CloseCapture
              if ctx.PaletteOpen then ClosePalette
              if ctx.BriefingOpen then CloseBriefing
              if ctx.ReviewOpen then CloseReview ],
            false
        else
            [], false

    let internal handleKeys (model: Model) (dispatch: Msg -> unit) (e: KeyEventArgs) =
        let ctrl = e.KeyModifiers.HasFlag KeyModifiers.Control
        let shift = e.KeyModifiers.HasFlag KeyModifiers.Shift

        let messages, handled = resolveShortcut (shortcutContext model) ctrl shift e.Key

        for message in messages do
            dispatch message

        if handled then e.Handled <- true

    /// The model fields `handleKeys` actually reads. A subscription captures the
    /// model at the moment it is created, so the Escape branch would go stale
    /// unless the subscription is re-created when one of these changes. Elmish
    /// diffs subscriptions by key, so putting them in the key is what keeps the
    /// captured model current — and re-subscribing is only a handler swap.
    let private shortcutKey (model: Model) =
        sprintf "shortcuts:%b%b%b%b" model.CaptureOpen model.PaletteOpen model.BriefingOpen model.ReviewOpen

    // ---- subscriptions -------------------------------------------------------

    /// Window-level drag & drop: files dropped anywhere become captures.
    let subscriptions (window: HostWindow) (model: Model) : Sub<Msg> =
        [
          // **Global shortcuts, at the window.**
          //
          // These were handled only by `Panel.onKeyDown` on the shell root. Key
          // events bubble *from the focused element*, and a Panel is neither
          // focusable nor ever focused — so on a fresh window, with nothing
          // clicked yet, there was no focused descendant to bubble from and the
          // handler never ran. A user reported exactly that: Ctrl+N did nothing
          // until some page control had been clicked first.
          //
          // The window is the fallback focus target, so a handler here fires in
          // precisely the case the Panel could not see. The Panel handler stays:
          // it runs first when focus *is* inside the page, marks the event
          // handled, and this handler is registered without `handledEventsToo`,
          // so the two can never both act on one press. That is what stops a
          // second Capture opening on top of the first.
          [ shortcutKey model ],
          fun dispatch ->
              let handler =
                  EventHandler<KeyEventArgs>(fun _ e -> handleKeys model dispatch e)

              window.AddHandler(InputElement.KeyDownEvent, handler)

              { new IDisposable with
                  member _.Dispose() = window.RemoveHandler(InputElement.KeyDownEvent, handler) }

          [ "dragdrop" ],
          fun dispatch ->
              let handler =
                  EventHandler<DragEventArgs>(fun _ e ->
                      let files =
                          match e.Data.GetFiles() with
                          | null -> []
                          | items ->
                              items
                              |> Seq.choose (fun item ->
                                  match item.TryGetLocalPath() with
                                  | null -> None
                                  | path -> Some path)
                              |> List.ofSeq

                      if not files.IsEmpty then
                          dispatch (FilesPicked files))

              window.AddHandler(DragDrop.DropEvent, handler)

              { new IDisposable with
                  member _.Dispose() = window.RemoveHandler(DragDrop.DropEvent, handler) }

          // "System" theme has to follow the OS while the app is running, not
          // only at the moment it is selected.
          [ "os-theme" ],
          fun dispatch ->
              match Avalonia.Application.Current with
              | null ->
                  { new IDisposable with
                      member _.Dispose() = () }
              | app ->
                  match app.PlatformSettings with
                  | null ->
                      { new IDisposable with
                          member _.Dispose() = () }
                  | settings ->
                      let handler =
                          EventHandler<Avalonia.Platform.PlatformColorValues>(fun _ _ -> dispatch OsThemeChanged)

                      settings.ColorValuesChanged.AddHandler handler

                      { new IDisposable with
                          member _.Dispose() = settings.ColorValuesChanged.RemoveHandler handler }

          // The pin transition, driven only while the phase is actually
          // travelling. Elmish diffs subscriptions by key, so retargeting
          // mid-flight keeps this ONE loop alive rather than starting a second
          // one — which is what makes rapid clicking retarget instead of queue.
          if SidebarTransition.isAnimating model.SidebarPhase (SidebarTransition.targetFor model.Ui.SidebarPinned) then
              [ "sidebar-transition" ],
              fun dispatch ->
                  let watch = Diagnostics.Stopwatch.StartNew()
                  let mutable previous = 0.0

                  // On the UI thread by construction, and stopped the moment
                  // the phase arrives — no timer outlives the animation.
                  //
                  // ~60Hz rather than faster: every tick is a full Elmish
                  // update and view diff, and the motion is time-based, so a
                  // higher rate would buy smoothness the frame budget then
                  // spends back.
                  let timer =
                      Avalonia.Threading.DispatcherTimer(Interval = TimeSpan.FromMilliseconds 16.0)

                  timer.Tick.Add(fun _ ->
                      let now = watch.Elapsed.TotalSeconds
                      let delta = now - previous
                      previous <- now
                      // Real elapsed time, so the transition takes the same
                      // wall-clock duration whatever the frame rate.
                      dispatch (SidebarTransitionTick delta))

                  timer.Start()

                  { new IDisposable with
                      member _.Dispose() = timer.Stop() }

          if AcceptanceSweep.requested () then
              // Drives the acceptance sweep through the ordinary appearance
              // message, so each step exercises the real production path rather
              // than a private test hook.
              [ "acceptance-sweep" ],
              fun dispatch ->
                  // Measure on the Design Preview, where the glass panels are.
                  dispatch (Navigate Page.DesignPreview)

                  AcceptanceSweep.run
                      (fun appearance -> dispatch (ApplyAppearance appearance))
                      (fun () -> window.Close())

                  { new IDisposable with
                      member _.Dispose() = () } ]

    // ---- view -----------------------------------------------------------------

    // ---- the sidebar --------------------------------------------------------
    //
    // Redesigned as a floating glass panel inset
    // from the window edges rather than a flat docked column. Two independent
    // controls replace the old three-state cycle —
    //
    //   pin      docked (inset 0, square, opaque, reserves layout width)
    //            vs floating (inset, rounded, glass, content runs underneath)
    //   density  Expanded 252px vs Compact 80px
    //
    // — because the old Expanded → Compact → Hidden cycle made "how wide" and
    // "is it showing" the same setting, so neither could be chosen freely.

    let private sidebarIsVisible (model: Model) =
        ShellLayout.sidebarVisible model.SidebarState

    /// One navigation entry. Compact density shows only the glyph, with the page
    /// name moved into a tooltip so it stays discoverable.
    let private navItem (model: Model) (dispatch: Msg -> unit) (page: Page, icon: string, label: string) : IView =
        let selected = model.Page = page
        let iconOnly = not (ShellLayout.showsLabels model.Ui.SidebarDensity)
        let accent = model.Tokens.Accent

        Button.create
            [ Button.horizontalAlignment HorizontalAlignment.Stretch
              Button.horizontalContentAlignment (if iconOnly then HorizontalAlignment.Center else HorizontalAlignment.Left)
              Button.background (
                  if selected then
                      SolidColorBrush accent.A1F :> IBrush
                  else
                      Theme.transparent
              )
              Button.cornerRadius (Tokens.radius model.Tokens 10.0)
              Button.padding ((if iconOnly then 0.0 else 9.0), 8.0)
              Button.margin (0.0, 0.0, 0.0, 2.0)
              Button.cursor Ui.hand
              ToolTip.tip (if iconOnly then label else null)
              Button.onClick ((fun _ -> dispatch (Navigate page)), SubPatchOptions.Always)
              Button.content (
                  StackPanel.create
                      [ StackPanel.orientation Orientation.Horizontal
                        StackPanel.spacing 12.0
                        StackPanel.children
                            [ yield
                                  TextBlock.create
                                      [ TextBlock.text icon
                                        TextBlock.fontSize 13.0
                                        TextBlock.width 16.0
                                        TextBlock.textAlignment TextAlignment.Center
                                        TextBlock.verticalAlignment VerticalAlignment.Center
                                        TextBlock.foreground (
                                            if selected then SolidColorBrush accent.Light :> IBrush else Theme.muted ()
                                        ) ]
                              if not iconOnly then
                                  yield
                                      TextBlock.create
                                          [ TextBlock.text label
                                            TextBlock.fontSize 13.5
                                            TextBlock.verticalAlignment VerticalAlignment.Center
                                            TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                            TextBlock.fontWeight (
                                                if selected then FontWeight.SemiBold else FontWeight.Medium
                                            )
                                            TextBlock.foreground (
                                                if selected then SolidColorBrush accent.Light :> IBrush else Theme.muted ()
                                            ) ] ] ]
              ) ]

    /// A small glyph button in the sidebar header.
    let private headerButton (glyph: string) (tip: string) (colour: IBrush) (onClick: unit -> unit) : IView =
        Button.create
            [ Button.content glyph
              Button.fontSize 12.5
              Button.background Theme.transparent
              Button.foreground colour
              Button.borderThickness 0.0
              Button.cornerRadius 7.0
              Button.padding (7.0, 5.0)
              Button.cursor Ui.hand
              ToolTip.tip tip
              Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always) ]
        :> IView

    /// Everything inside the sidebar panel, independent of how the panel itself
    /// is presented (docked or floating).
    let private sidebarContent (model: Model) (dispatch: Msg -> unit) : IView =
        let expanded = ShellLayout.showsLabels model.Ui.SidebarDensity
        let accent = model.Tokens.Accent

        let navigation: IView list =
            Page.groups
            |> List.collect (fun (groupName, items) ->
                [ if expanded then
                      yield
                          TextBlock.create
                              [ TextBlock.text (groupName.ToUpperInvariant())
                                TextBlock.fontSize 10.0
                                TextBlock.fontWeight FontWeight.SemiBold
                                TextBlock.foreground (Theme.faint ())
                                TextBlock.margin (10.0, 4.0, 10.0, 7.0) ]
                  else
                      // Compact has no room for a heading, so groups are told
                      // apart by a hairline instead of losing their structure.
                      yield
                          Border.create
                              [ Border.height 1.0
                                Border.background (Theme.divider ())
                                Border.margin (10.0, 8.0, 10.0, 8.0) ]
                          :> IView

                  yield! items |> List.map (navItem model dispatch)

                  yield Border.create [ Border.height 12.0 ] :> IView ])

        // One button, two densities. It uses the USER's accent rather than the
        // theme accent so the sidebar's primary action matches the swatch they
        // picked — the redesign's `var(--accent)`, not a fixed brand colour.
        let captureButton: IView =
            Button.create
                [ Button.content (if expanded then "＋  Capture  ·  Ctrl N" else "＋")
                  Button.horizontalAlignment HorizontalAlignment.Stretch
                  Button.horizontalContentAlignment HorizontalAlignment.Center
                  Button.background (SolidColorBrush accent.Normal)
                  Button.foreground (Theme.onAccent ())
                  Button.fontSize (if expanded then 13.0 else 15.0)
                  Button.fontWeight FontWeight.SemiBold
                  Button.cornerRadius (Tokens.radius model.Tokens 11.0)
                  Button.padding ((if expanded then 12.0 else 0.0), (if expanded then 10.0 else 9.0))
                  Button.cursor Ui.hand
                  ToolTip.tip (if expanded then null else "Capture (Ctrl+N)")
                  Button.onClick ((fun _ -> dispatch OpenCapture), SubPatchOptions.Always) ]

        let header =
            DockPanel.create
                [ DockPanel.margin (2.0, 2.0, 2.0, 16.0)
                  DockPanel.children
                      [ StackPanel.create
                            [ DockPanel.dock Dock.Right
                              StackPanel.orientation Orientation.Horizontal
                              StackPanel.spacing 2.0
                              StackPanel.children
                                  [ if expanded then
                                        headerButton
                                            (if model.Ui.SidebarPinned then "UNPIN" else "PIN")
                                            (if model.Ui.SidebarPinned then
                                                 "Unpin — let the sidebar float and auto-hide"
                                             else
                                                 "Pin — keep the sidebar docked")
                                            (if model.Ui.SidebarPinned then
                                                 SolidColorBrush accent.Light :> IBrush
                                             else
                                                 Theme.faint ())
                                            (fun () ->
                                                // Dispatched from inside the panel, so the
                                                // pointer is over it by construction.
                                                dispatch (SetSidebarPinned(not model.Ui.SidebarPinned, true)))

                                    headerButton
                                        (if expanded then "«" else "»")
                                        "Switch sidebar density (Ctrl+B)"
                                        (Theme.faint ())
                                        (fun () -> dispatch ToggleSidebarDensity) ] ]

                        StackPanel.create
                            [ StackPanel.orientation Orientation.Horizontal
                              StackPanel.spacing 10.0
                              StackPanel.verticalAlignment VerticalAlignment.Center
                              StackPanel.children
                                  [ yield
                                        Border.create
                                            [ Border.width 26.0
                                              Border.height 26.0
                                              Border.cornerRadius (Tokens.radius model.Tokens 8.0)
                                              Border.background (SolidColorBrush accent.Normal) ]
                                        :> IView
                                    if expanded then
                                        yield
                                            TextBlock.create
                                                [ TextBlock.text "University OS"
                                                  TextBlock.fontSize 14.0
                                                  TextBlock.fontWeight FontWeight.SemiBold
                                                  TextBlock.verticalAlignment VerticalAlignment.Center
                                                  TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                                  TextBlock.foreground (Theme.text ()) ] ] ] ] ]

        DockPanel.create
            [ DockPanel.lastChildFill true
              DockPanel.margin (10.0, 16.0, 10.0, 14.0)
              DockPanel.children
                  [ Border.create
                        [ DockPanel.dock Dock.Bottom
                          Border.margin (4.0, 8.0, 4.0, 0.0)
                          Border.child captureButton ]

                    Border.create [ DockPanel.dock Dock.Top; Border.child header ]

                    Ui.scroll (
                        StackPanel.create [ StackPanel.margin (2.0, 0.0); StackPanel.children navigation ]
                    ) ] ]
        :> IView

    /// The sidebar interaction region: edge strip, inset gap and panel, as ONE
    /// always-mounted control.
    ///
    /// This shape is the fix for the reveal flicker. Previously the edge strip
    /// and the panel were separate siblings, each conditionally present, each
    /// with its own immediate pointer handler — so revealing removed the
    /// element the pointer was standing on, shifted the panel into its sibling
    /// index (FuncUI patches children positionally, so the strip control was
    /// mutated into the panel underneath the cursor), and the panel's exit
    /// handler then fired because the pointer was in the 14px inset gap rather
    /// than inside the panel. That hid it, which remounted the strip under the
    /// pointer, which revealed it again, at frame rate.
    ///
    /// Now there is one control at one fixed index. Its width changes with the
    /// state and only ever GROWS on reveal, so the pointer that triggered the
    /// reveal is still inside afterwards and cannot generate an exit. The inset
    /// gap is inside the region, so it is a bridge rather than a dead zone.
    let private sidebarRegion (model: Model) (dispatch: Msg -> unit) : IView =
        let pinned = model.Ui.SidebarPinned
        let material = Theme.surfaces ()
        let eased = SidebarTransition.ease model.SidebarPhase
        let inset = ShellLayout.insetAt eased

        // Docked and floating are the same warm-neutral family — the redesign
        // uses one RGB for both and varies only the transparency — so the tint
        // interpolates between two material roles rather than toward the accent.
        let sidebarColour =
            Ui.mix material.SidebarPinned material.SidebarFloating eased

        // ONE panel for both states. The docked and floating treatments differ
        // only by the inset around it and the GlassPanel's presentation value,
        // both continuous in the phase — so pinning interpolates rather than
        // swapping one control for a structurally different one. Two branches
        // here would remount the panel mid-gesture and there would be nothing
        // to interpolate.
        let panel: IView =
            Border.create
                [ Border.width (SidebarDensity.width model.Ui.SidebarDensity)
                  Border.horizontalAlignment HorizontalAlignment.Left
                  Border.margin (inset, inset, inset, inset)
                  Border.isVisible (sidebarIsVisible model)
                  Border.child (
                      GlassPanel.create
                          [ GlassPanel.baseRadius ShellLayout.floatingRadius
                            GlassPanel.elevation 2
                            GlassPanel.tint sidebarColour
                            // 0 docked-opaque, 1 floating glass. Radius, tint,
                            // backdrop, highlight and shadow all follow it.
                            GlassPanel.presentation eased
                            GlassPanel.child (sidebarContent model dispatch) ]
                  ) ]

        Border.create
            [ Border.width (ShellLayout.regionWidth model.SidebarState model.Ui.SidebarDensity eased)
              Border.horizontalAlignment HorizontalAlignment.Left
              Border.verticalAlignment VerticalAlignment.Stretch
              // Transparent rather than absent: a hit-testable surface is what
              // makes the region one hover area, and it also stops content
              // underneath stealing hover while the panel is over it.
              Border.background Brushes.Transparent
              Border.onPointerEntered ((fun _ -> dispatch SidebarPointerEntered), SubPatchOptions.Always)
              // Movement inside the region cancels a pending hide. Guarded to
              // that one state on purpose: dispatching on every pointer move
              // would rebuild the whole shell view tree while the pointer is
              // simply travelling across the sidebar, which is a far more
              // expensive kind of churn than the one being fixed.
              Border.onPointerMoved (
                  (fun _ ->
                      match model.SidebarState with
                      | HidePending _ -> dispatch SidebarPointerEntered
                      | Pinned
                      | Hidden
                      | Revealed -> ()),
                  SubPatchOptions.Always
              )
              Border.onPointerExited ((fun _ -> dispatch SidebarPointerExited), SubPatchOptions.Always)
              Border.child (
                  Panel.create
                      [ Panel.children
                            [ // The discoverability tab, visible only while the
                              // panel is away.
                              Border.create
                                  [ Border.width 3.0
                                    Border.height 64.0
                                    Border.horizontalAlignment HorizontalAlignment.Left
                                    Border.verticalAlignment VerticalAlignment.Center
                                    Border.margin (6.0, 0.0, 0.0, 0.0)
                                    Border.cornerRadius 2.0
                                    Border.isVisible (not pinned && not (sidebarIsVisible model))
                                    Border.background (SolidColorBrush model.Tokens.Accent.B55) ]

                              panel ] ]
              ) ]

    let private captureOverlay (model: Model) (dispatch: Msg -> unit) : IView =
        Border.create
            [ Border.background (Theme.scrim ())
              Border.child (
                  Border.create
                      [ Border.maxWidth 620.0
                        Border.verticalAlignment VerticalAlignment.Center
                        Border.horizontalAlignment HorizontalAlignment.Stretch
                        Border.margin 24.0
                        Border.background (Theme.overlay ())
                        Border.cornerRadius 12.0
                        Border.borderThickness 1.0
                        Border.borderBrush (Theme.border ())
                        Border.padding 20.0
                        Border.child (
                            StackPanel.create
                                [ StackPanel.spacing 12.0
                                  StackPanel.children
                                      [ Ui.pageHeader
                                            "Capture anything"
                                            "Deadlines, announcements, ideas, emails — the AI files it for you and proposes actions in the Inbox. You can also drop files anywhere in the window."
                                        Ui.multilineBox
                                            model.CaptureText
                                            "e.g.  CS2004 coursework 2 due 15 August 5pm, worth 40% — report + demo video"
                                            120.0
                                            (fun t -> dispatch (SetCaptureText t))
                                        DockPanel.create
                                            [ DockPanel.children
                                                  [ StackPanel.create
                                                        [ DockPanel.dock Dock.Right
                                                          StackPanel.orientation Orientation.Horizontal
                                                          StackPanel.spacing 8.0
                                                          StackPanel.children
                                                              [ Ui.ghostButton "Cancel" (fun () -> dispatch CloseCapture)
                                                                Ui.primaryButton
                                                                    (if model.CaptureBusy then "Capturing…" else "Capture")
                                                                    (fun () -> if not model.CaptureBusy then dispatch SubmitCapture) ] ]
                                                    Ui.ghostButton "⇪ Import files…" (fun () -> dispatch CaptureFiles) ] ] ] ]
                        ) ]
              ) ]

    let private paletteOverlay (model: Model) (dispatch: Msg -> unit) : IView =
        Border.create
            [ Border.background (Theme.scrim ())
              Border.child (
                  Border.create
                      [ Border.maxWidth 520.0
                        Border.verticalAlignment VerticalAlignment.Top
                        Border.horizontalAlignment HorizontalAlignment.Center
                        Border.margin (0.0, 90.0, 0.0, 0.0)
                        Border.background (Theme.overlay ())
                        Border.cornerRadius 12.0
                        Border.borderThickness 1.0
                        Border.borderBrush (Theme.border ())
                        Border.padding 12.0
                        Border.width 520.0
                        Border.child (
                            StackPanel.create
                                [ StackPanel.spacing 8.0
                                  StackPanel.children
                                      [ TextBox.create
                                            [ TextBox.text model.PaletteQuery
                                              TextBox.watermark "Type a command or destination…"
                                              TextBox.fontSize Theme.fontBase
                                              TextBox.cornerRadius 6.0
                                              TextBox.onTextChanged ((SetPaletteQuery >> dispatch), SubPatchOptions.Always)
                                              TextBox.onKeyDown (
                                                  (fun e ->
                                                      if e.Key = Key.Enter then
                                                          dispatch RunFirstPaletteCommand
                                                          e.Handled <- true),
                                                  SubPatchOptions.Always
                                              ) ]
                                        StackPanel.create
                                            [ StackPanel.spacing 2.0
                                              StackPanel.children
                                                  (matchingCommands model.PaletteQuery
                                                   |> List.truncate 10
                                                   |> List.map (fun command ->
                                                       Button.create
                                                           [ Button.horizontalAlignment HorizontalAlignment.Stretch
                                                             Button.horizontalContentAlignment HorizontalAlignment.Stretch
                                                             Button.background Theme.transparent
                                                             Button.cornerRadius 6.0
                                                             Button.padding (10.0, 7.0)
                                                             Button.cursor Ui.hand
                                                             Button.onClick (
                                                                 (fun _ ->
                                                                     dispatch ClosePalette
                                                                     dispatch command.Msg),
                                                                 SubPatchOptions.Always
                                                             )
                                                             Button.content (
                                                                 DockPanel.create
                                                                     [ DockPanel.children
                                                                           [ TextBlock.create
                                                                                 [ DockPanel.dock Dock.Right
                                                                                   TextBlock.text command.Hint
                                                                                   TextBlock.fontSize Theme.fontXs
                                                                                   TextBlock.foreground (Theme.faint ()) ]
                                                                             TextBlock.create
                                                                                 [ TextBlock.text command.Label
                                                                                   TextBlock.fontSize Theme.fontSm
                                                                                   TextBlock.foreground (Theme.text ()) ] ] ]
                                                             ) ]
                                                       :> IView)) ] ] ]
                        ) ]
              ) ]

    // ---- morning briefing & evening review overlays --------------------------

    let private overlayShell (maxWidth: float) (content: IView) : IView =
        Border.create
            [ Border.background (Theme.scrim ())
              Border.child (
                  Border.create
                      [ Border.maxWidth maxWidth
                        Border.maxHeight 640.0
                        Border.verticalAlignment VerticalAlignment.Center
                        Border.horizontalAlignment HorizontalAlignment.Center
                        Border.margin 24.0
                        Border.background (Theme.overlay ())
                        Border.cornerRadius 14.0
                        Border.borderThickness 1.0
                        Border.borderBrush (Theme.border ())
                        Border.padding 24.0
                        Border.boxShadow (BoxShadow.Parse "0 8 40 0 #66000000")
                        Border.child content ]
              ) ]

    let private activityLine (entry: ActivityEntry) : IView =
        StackPanel.create
            [ StackPanel.orientation Orientation.Horizontal
              StackPanel.spacing 8.0
              StackPanel.children
                  [ TextBlock.create
                        [ TextBlock.text "✓"
                          TextBlock.foreground (Theme.success ())
                          TextBlock.fontSize Theme.fontSm ]
                    TextBlock.create
                        [ TextBlock.text entry.Title
                          TextBlock.fontSize Theme.fontSm
                          TextBlock.foreground (Theme.text ())
                          TextBlock.textWrapping TextWrapping.Wrap ] ] ]

    let private briefingOverlay (b: UniversityOS.Application.MorningBriefing) (dispatch: Msg -> unit) : IView =
        let summary =
            match b.AiSummary with
            | Some s -> s
            | None ->
                match b.Data.Recommendation with
                | Some(task, _) ->
                    sprintf
                        "You have %s of free study time today — a good window to move \"%s\" forward."
                        (Format.duration b.Data.FreeMinutesToday)
                        task.Title
                | None ->
                    sprintf
                        "You have %s of free study time today and nothing urgent — a good day to get ahead."
                        (Format.duration b.Data.FreeMinutesToday)

        overlayShell
            760.0
            (Ui.scroll (
                StackPanel.create
                    [ StackPanel.spacing 14.0
                      StackPanel.children
                          [ DockPanel.create
                                [ DockPanel.children
                                      [ StackPanel.create
                                            [ DockPanel.dock Dock.Right
                                              StackPanel.children
                                                  [ Ui.primaryButton "Let's go" (fun () -> dispatch CloseBriefing) ] ]
                                        StackPanel.create
                                            [ StackPanel.spacing 2.0
                                              StackPanel.children
                                                  [ TextBlock.create
                                                        [ TextBlock.text ("☀  " + Format.greeting b.Now "")
                                                          TextBlock.fontSize Theme.fontXl
                                                          TextBlock.fontWeight FontWeight.Bold
                                                          TextBlock.foreground (Theme.text ()) ]
                                                    StackPanel.create
                                                        [ StackPanel.orientation Orientation.Horizontal
                                                          StackPanel.spacing 10.0
                                                          StackPanel.children
                                                              [ Ui.subtle (Format.fullDate b.Now)
                                                                Ui.faintText "· weather coming soon" ] ] ] ] ] ]

                            Border.create
                                [ Border.background (Theme.accentSoft ())
                                  Border.cornerRadius 10.0
                                  Border.padding 14.0
                                  Border.child (Ui.body summary) ]

                            Grid.create
                                [ Grid.columnDefinitions "1*,14,1*"
                                  Grid.children
                                      [ StackPanel.create
                                            [ Grid.column 0
                                              StackPanel.spacing 12.0
                                              StackPanel.children
                                                  [ Ui.card
                                                        [ Ui.sectionTitle "Today's schedule"
                                                          if b.Data.TodayEvents.IsEmpty && b.Data.TodaySessions.IsEmpty then
                                                              Ui.subtle "Nothing scheduled — the day is yours."
                                                          else
                                                              StackPanel.create
                                                                  [ StackPanel.spacing 5.0
                                                                    StackPanel.children
                                                                        [ for e in b.Data.TodayEvents do
                                                                              StackPanel.create
                                                                                  [ StackPanel.orientation Orientation.Horizontal
                                                                                    StackPanel.spacing 8.0
                                                                                    StackPanel.children
                                                                                        [ Ui.faintText (Format.timeRange e.StartsAt e.EndsAt)
                                                                                          Ui.colorDot (Theme.eventKindHex e.Kind)
                                                                                          TextBlock.create
                                                                                              [ TextBlock.text e.Title
                                                                                                TextBlock.fontSize Theme.fontSm
                                                                                                TextBlock.foreground (Theme.text ()) ] ] ]
                                                                          for s in b.Data.TodaySessions do
                                                                              StackPanel.create
                                                                                  [ StackPanel.orientation Orientation.Horizontal
                                                                                    StackPanel.spacing 8.0
                                                                                    StackPanel.children
                                                                                        [ Ui.faintText (Format.timeRange s.StartsAt s.EndsAt)
                                                                                          Ui.colorDot (Theme.eventKindHex EventKind.StudySession)
                                                                                          TextBlock.create
                                                                                              [ TextBlock.text s.Title
                                                                                                TextBlock.fontSize Theme.fontSm
                                                                                                TextBlock.foreground (Theme.text ()) ] ] ] ] ]
                                                          if not b.Lectures.IsEmpty then
                                                              Ui.divider ()

                                                              Ui.faintText (
                                                                  sprintf
                                                                      "%d lecture%s today"
                                                                      b.Lectures.Length
                                                                      (if b.Lectures.Length = 1 then "" else "s")
                                                              ) ]

                                                    if not b.Yesterday.IsEmpty then
                                                        Ui.card
                                                            [ Ui.sectionTitle "Yesterday you…"
                                                              StackPanel.create
                                                                  [ StackPanel.spacing 4.0
                                                                    StackPanel.children (b.Yesterday |> List.map activityLine) ] ] ] ]

                                        StackPanel.create
                                            [ Grid.column 2
                                              StackPanel.spacing 12.0
                                              StackPanel.children
                                                  [ (match b.Data.Recommendation with
                                                     | Some(task, reason) ->
                                                         Ui.card
                                                             [ Ui.sectionTitle "Start with this"
                                                               TextBlock.create
                                                                   [ TextBlock.text task.Title
                                                                     TextBlock.fontSize Theme.fontMd
                                                                     TextBlock.fontWeight FontWeight.SemiBold
                                                                     TextBlock.foreground (Theme.text ())
                                                                     TextBlock.textWrapping TextWrapping.Wrap ]
                                                               Ui.subtle reason ]
                                                     | None -> Border.create [] :> IView)

                                                    Ui.card
                                                        [ Ui.sectionTitle "Due soon"
                                                          if b.Data.UpcomingDeadlines.IsEmpty then
                                                              Ui.subtle "No deadlines in the next two weeks."
                                                          else
                                                              StackPanel.create
                                                                  [ StackPanel.spacing 4.0
                                                                    StackPanel.children
                                                                        [ for a, code in b.Data.UpcomingDeadlines |> List.truncate 5 do
                                                                              StackPanel.create
                                                                                  [ StackPanel.orientation Orientation.Horizontal
                                                                                    StackPanel.spacing 6.0
                                                                                    StackPanel.children
                                                                                        [ (match code with
                                                                                           | Some c -> Ui.pill c (Theme.accentHex ())
                                                                                           | None -> Border.create [])
                                                                                          TextBlock.create
                                                                                              [ TextBlock.text (Format.truncate 26 a.Title)
                                                                                                TextBlock.fontSize Theme.fontSm
                                                                                                TextBlock.foreground (Theme.text ()) ]
                                                                                          (match a.Deadline with
                                                                                           | Some d -> Ui.faintText (Format.deadline b.Now d)
                                                                                           | None -> Border.create []) ] ] ] ]

                                                          Ui.divider ()
                                                          Ui.subtle (sprintf "Free study time today: %s" (Format.duration b.Data.FreeMinutesToday)) ]

                                                    if not b.RecentChanges.IsEmpty then
                                                        Ui.card
                                                            [ Ui.sectionTitle "What changed"
                                                              StackPanel.create
                                                                  [ StackPanel.spacing 4.0
                                                                    StackPanel.children
                                                                        [ for entry in b.RecentChanges do
                                                                              StackPanel.create
                                                                                  [ StackPanel.spacing 1.0
                                                                                    StackPanel.children
                                                                                        [ TextBlock.create
                                                                                              [ TextBlock.text entry.Title
                                                                                                TextBlock.fontSize Theme.fontSm
                                                                                                TextBlock.foreground (Theme.text ())
                                                                                                TextBlock.textWrapping TextWrapping.Wrap ]
                                                                                          Ui.faintText (Format.relativeTime b.Now entry.OccurredAt) ] ] ] ] ] ] ] ] ] ] ]
            ))

    let private reviewOverlay (r: UniversityOS.Application.EveningReview) (dispatch: Msg -> unit) : IView =
        let reflection =
            match r.AiReflection with
            | Some s -> s
            | None ->
                if r.CompletedSessionCount > 0 then
                    sprintf
                        "You completed %d study session%s (%s) today. Tomorrow has %d fixed event%s — a plan regeneration this evening will set you up well."
                        r.CompletedSessionCount
                        (if r.CompletedSessionCount = 1 then "" else "s")
                        (Format.duration r.MinutesStudied)
                        r.TomorrowEvents.Length
                        (if r.TomorrowEvents.Length = 1 then "" else "s")
                else
                    "A quiet day. Tomorrow is a fresh start — check the planner this evening so the morning begins with clarity."

        overlayShell
            640.0
            (Ui.scroll (
                StackPanel.create
                    [ StackPanel.spacing 14.0
                      StackPanel.children
                          [ DockPanel.create
                                [ DockPanel.children
                                      [ StackPanel.create
                                            [ DockPanel.dock Dock.Right
                                              StackPanel.children [ Ui.primaryButton "Good night" (fun () -> dispatch CloseReview) ] ]
                                        StackPanel.create
                                            [ StackPanel.spacing 2.0
                                              StackPanel.children
                                                  [ TextBlock.create
                                                        [ TextBlock.text "☾  Evening review"
                                                          TextBlock.fontSize Theme.fontXl
                                                          TextBlock.fontWeight FontWeight.Bold
                                                          TextBlock.foreground (Theme.text ()) ]
                                                    Ui.subtle (Format.fullDate r.Now) ] ] ] ]

                            Border.create
                                [ Border.background (Theme.accentSoft ())
                                  Border.cornerRadius 10.0
                                  Border.padding 14.0
                                  Border.child (Ui.body reflection) ]

                            Ui.card
                                [ Ui.sectionTitle "Today in numbers"
                                  Grid.create
                                      [ Grid.columnDefinitions "1*,1*,1*,1*"
                                        Grid.children
                                            [ for i, (value, label) in
                                                  List.indexed
                                                      [ string r.CompletedSessionCount, "sessions"
                                                        Format.duration r.MinutesStudied, "studied"
                                                        string r.AssignmentsProgressed, "assignments"
                                                        string r.NotesCreated, "notes" ] do
                                                  StackPanel.create
                                                      [ Grid.column i
                                                        StackPanel.spacing 0.0
                                                        StackPanel.horizontalAlignment HorizontalAlignment.Center
                                                        StackPanel.children
                                                            [ TextBlock.create
                                                                  [ TextBlock.text value
                                                                    TextBlock.fontSize Theme.fontLg
                                                                    TextBlock.fontWeight FontWeight.Bold
                                                                    TextBlock.foreground (Theme.text ())
                                                                    TextBlock.horizontalAlignment HorizontalAlignment.Center ]
                                                              Ui.faintText label ] ] ] ] ]

                            if not r.TodayActivity.IsEmpty then
                                Ui.card
                                    [ Ui.sectionTitle "What happened today"
                                      StackPanel.create
                                          [ StackPanel.spacing 4.0
                                            StackPanel.children (r.TodayActivity |> List.truncate 10 |> List.map activityLine) ] ]

                            Ui.card
                                [ Ui.sectionTitle "Tomorrow"
                                  if r.TomorrowEvents.IsEmpty then
                                      Ui.subtle "No fixed events tomorrow."
                                  else
                                      StackPanel.create
                                          [ StackPanel.spacing 4.0
                                            StackPanel.children
                                                [ for e in r.TomorrowEvents |> List.truncate 6 do
                                                      StackPanel.create
                                                          [ StackPanel.orientation Orientation.Horizontal
                                                            StackPanel.spacing 8.0
                                                            StackPanel.children
                                                                [ Ui.faintText (Format.timeRange e.StartsAt e.EndsAt)
                                                                  Ui.colorDot (Theme.eventKindHex e.Kind)
                                                                  TextBlock.create
                                                                      [ TextBlock.text e.Title
                                                                        TextBlock.fontSize Theme.fontSm
                                                                        TextBlock.foreground (Theme.text ()) ] ] ] ] ]

                                  match r.TomorrowRecommendation with
                                  | Some(task, reason) ->
                                      Ui.divider ()
                                      Ui.subtle (sprintf "First up tomorrow: %s — %s" task.Title reason)
                                  | None -> () ] ] ]
            ))

    let view (model: Model) (dispatch: Msg -> unit) : IView =
        let content: IView =
            match model.Page with
            | Page.Dashboard -> Dashboard.view model.Tokens model.Dashboard (DashboardMsg >> dispatch)
            | Page.Planner -> Planner.view model.Tokens model.Planner (PlannerMsg >> dispatch)
            | Page.Inbox -> InboxView.view model.Tokens model.Inbox (InboxMsg >> dispatch)
            | Page.Assignments -> AssignmentsView.view model.Tokens model.Assignments (AssignmentsMsg >> dispatch)
            | Page.Modules -> ModulesView.view model.Tokens model.Modules (ModulesMsg >> dispatch)
            | Page.Notes -> NotesView.view model.Tokens model.Notes (NotesMsg >> dispatch)
            | Page.Chat -> ChatView.view model.Tokens model.Chat (ChatMsg >> dispatch)
            | Page.Search -> SearchView.view model.Tokens model.Search (SearchMsg >> dispatch)
            | Page.Calendar -> CalendarView.view model.Tokens model.Calendar (CalendarMsg >> dispatch)
            | Page.Documents -> DocumentsView.view model.Tokens model.Documents (DocumentsMsg >> dispatch)
            | Page.Connections -> ConnectionsView.view model.Tokens model.Connections (ConnectionsMsg >> dispatch)
            | Page.Settings -> SettingsView.view model.Tokens model.Settings (SettingsMsg >> dispatch)
            | Page.Timeline -> TimelineView.view model.Tokens model.Timeline (TimelineMsg >> dispatch)
            | Page.Graph -> GraphView.view model.Tokens model.Graph (GraphMsg >> dispatch)
            | Page.Lectures -> LecturesView.view model.Tokens model.Lectures (LecturesMsg >> dispatch)
            | Page.DesignPreview -> DesignPreview.view model.Tokens model.DesignPreview (DesignPreviewMsg >> dispatch)

        // The design preview shows glass over the live background, so its
        // content surface is transparent. Every other page keeps its opaque
        // surface until that screen is migrated.
        let contentBackground: IBrush =
            if Page.usesGlassSurface model.Page then
                Brushes.Transparent :> IBrush
            else
                Theme.content ()

        Panel.create
            [ // On the root, so a shortcut still fires when focus is inside the
              // sidebar. Key events bubble, so one handler here covers the whole
              // window; hanging it off the content border only worked while the
              // sidebar was a sibling inside it.
              Panel.onKeyDown ((handleKeys model dispatch), SubPatchOptions.Always)
              Panel.children
                  [ // Base colour, then the animated background, then the UI.
                    Border.create [ Border.background (Theme.window ()) ]

                    BackgroundHost.create
                        [ BackgroundHost.appearance model.Appearance
                          BackgroundHost.section (Page.backgroundSection model.Page)
                          BackgroundHost.isGraphScreen (model.Page = Page.Graph)
                          BackgroundHost.accent model.Tokens.Accent.Normal
                          BackgroundHost.generation model.Tokens.Generation
                          // Developer freeze, only ever set from the Design Preview.
                          BackgroundHost.isFrozen (model.Page = Page.DesignPreview && model.DesignPreview.Frozen) ]

                    // Content. When the sidebar is pinned it reserves layout
                    // width; when floating, content runs full width and the
                    // panel hovers over it — which is what makes the glass read
                    // as a layer rather than a column.
                    Border.create
                        [ Border.background Brushes.Transparent
                          // Continuous in the phase, so content slides with the
                          // panel instead of jumping when it detaches or docks.
                          Border.margin (
                              ShellLayout.reservedWidth
                                  (SidebarTransition.ease model.SidebarPhase)
                                  model.Ui.SidebarDensity,
                              0.0,
                              0.0,
                              0.0
                          )
                          Border.child (
                              Border.create
                                  [ Border.margin (10.0, 10.0, 10.0, 10.0)
                                    Border.background contentBackground
                                    Border.cornerRadius (Tokens.radius model.Tokens 12.0)
                                    Border.clipToBounds true
                                    Border.child (
                                        Border.create
                                            [ Border.padding (Tokens.space model.Tokens 24.0)
                                              Border.child content ]
                                    ) ]
                          ) ]

                    // ONE child, always present, at a fixed sibling index. Its
                    // presence must not depend on the state it controls — that
                    // dependency is what produced the reveal flicker.
                    sidebarRegion model dispatch

                    if model.CaptureOpen then
                        captureOverlay model dispatch

                    if model.PaletteOpen then
                        paletteOverlay model dispatch

                    match model.BriefingOpen, model.Briefing with
                    | true, Some briefing -> briefingOverlay briefing dispatch
                    | _ -> ()

                    match model.ReviewOpen, model.Review with
                    | true, Some review -> reviewOverlay review dispatch
                    | _ -> ()

                    match model.Toast with
                    | Some message ->
                        Border.create
                            [ Border.horizontalAlignment HorizontalAlignment.Center
                              Border.verticalAlignment VerticalAlignment.Bottom
                              Border.margin (0.0, 0.0, 0.0, 24.0)
                              Border.background (Theme.overlay ())
                              Border.borderBrush (Theme.border ())
                              Border.borderThickness 1.0
                              Border.cornerRadius 8.0
                              Border.padding (16.0, 10.0)
                              Border.child (Ui.body message) ]
                    | None -> () ] ]
        :> IView
