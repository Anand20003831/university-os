namespace UniversityOS.App

open System
open System.IO
open System.Net.Http
open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Media
open Avalonia.Platform.Storage
open Avalonia.Styling
open Avalonia.Themes.Fluent
open Avalonia.FuncUI.Hosts
open Avalonia.FuncUI.Elmish
open Elmish
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.Infrastructure
open UniversityOS.Infrastructure.Repositories
open UniversityOS.Infrastructure.Connectors
open UniversityOS.App.Rendering
open UniversityOS.App.Controls

/// Teaches Avalonia's Fluent theme the application's material palette.
///
/// Fluent ships its own neutral greys for the states a control paints itself:
/// button hover and pressed, text-box fill, border and focus. Views can set a
/// control's normal Background, but not those — they come from theme resources.
/// Left alone they are the last place the old cool-neutral character survived,
/// showing as a grey flash under the pointer and a grey field behind every text
/// box on an otherwise warm surface.
///
/// This overrides exactly those state resources with the resolved material
/// roles, and nothing else: it is not a wholesale restyle of the theme.
[<RequireQualifiedAccess>]
module FluentMaterial =

    open UniversityOS.App.Design

    /// Every key set here, so the list is reviewable in one place rather than
    /// scattered through a sequence of assignments.
    let keys =
        [ "ButtonBackgroundPointerOver"
          "ButtonBackgroundPressed"
          "TextControlBackground"
          "TextControlBackgroundPointerOver"
          "TextControlBackgroundFocused"
          "TextControlBorderBrush"
          "TextControlBorderBrushPointerOver"
          "ComboBoxBackground"
          "ComboBoxBackgroundPointerOver"
          "ComboBoxBackgroundPressed"
          "ComboBoxDropDownBackground" ]

    /// The role each key takes. Pure, so the mapping is testable without an
    /// Avalonia application.
    let resolve (s: Surfaces) (key: string) : Color =
        match key with
        | "ButtonBackgroundPointerOver" -> s.Hover
        | "ButtonBackgroundPressed" -> s.Selected
        | "TextControlBackground"
        | "ComboBoxBackground" -> s.CardAlt
        | "TextControlBackgroundPointerOver"
        | "ComboBoxBackgroundPointerOver" -> s.Hover
        | "TextControlBackgroundFocused"
        | "ComboBoxBackgroundPressed" -> s.Selected
        | "ComboBoxDropDownBackground" -> s.Overlay
        | "TextControlBorderBrush"
        | "TextControlBorderBrushPointerOver" -> s.Border
        // Total by construction: `keys` is the only caller, and a key added
        // there without a role here would land on the card rather than throwing
        // inside a render pass.
        | _ -> s.Card

    let apply (resources: Avalonia.Controls.IResourceDictionary) (s: Surfaces) =
        for key in keys do
            resources.[key] <- SolidColorBrush(resolve s key) :> IBrush

module Composition =

    type SystemClock() =
        interface IClock with
            member _.Now = DateTime.Now

    /// Builds the entire dependency graph. Called once at startup; everything
    /// downstream receives interfaces, never concretions.
    let build (pickFiles: unit -> Async<string list>) (applyTheme: ThemeMode -> unit) : AppServices =
        // Review mode redirects the ENTIRE data root, not just the database, so
        // the real store is never opened — no migration, no secrets, no files.
        // See ReviewMode.fs.
        let reviewing = ReviewMode.isActive ()

        let appData =
            if reviewing then
                ReviewMode.root ()
            else
                Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.ApplicationData, "UniversityOS")

        Directory.CreateDirectory appData |> ignore

        let db = Database(Path.Combine(appData, "universityos.db"))
        Migrations.apply db |> Async.RunSynchronously

        let clock =
            if reviewing then
                ReviewMode.FixedClock ReviewMode.fixedNow :> IClock
            else
                SystemClock() :> IClock
        let http = new HttpClient(Timeout = TimeSpan.FromSeconds 100.0)

        let modules = ModuleRepository(db) :> IModuleRepository
        let assignments = AssignmentRepository(db) :> IAssignmentRepository
        let events = EventRepository(db) :> IEventRepository
        let sessions = SessionRepository(db) :> ISessionRepository
        let notes = NoteRepository(db) :> INoteRepository
        let documents = DocumentRepository(db) :> IDocumentRepository
        let lectures = LectureRepository(db) :> ILectureRepository
        let revision = RevisionRepository(db) :> IRevisionRepository
        let milestones = MilestoneRepository(db) :> IMilestoneRepository
        let links = LinkRepository(db) :> ILinkRepository
        let inbox = InboxRepository(db) :> IInboxRepository
        let chat = ChatRepository(db) :> IChatRepository
        let connections = ConnectionRepository(db) :> IConnectionRepository
        let syncLog = SyncLogRepository(db) :> ISyncLogRepository
        let settings = SettingsStore(db) :> ISettingsStore
        let search = SearchRepository(db) :> ISearchRepository
        let activityLog = ActivityRepository(db) :> IActivityLog
        let memory = MemoryRepository(db) :> IMemoryRepository
        let uiState = UiStateStore(db) :> IUiStateStore
        let appearance = AppearanceStore(db) :> IAppearanceStore

        let secrets = DpapiSecretStore(Path.Combine(appData, "secrets")) :> ISecretStore
        let storage = FileStorage(Path.Combine(appData, "files"))

        let importService =
            DocumentImportService(storage, documents, activityLog, clock) :> IDocumentImportService

        let aiProvider = ClaudeProvider(secrets, http) :> IAiProvider
        let planner = PlannerService(assignments, events, sessions, modules, milestones, settings, activityLog, clock)

        let ai =
            AiService(aiProvider, modules, assignments, events, notes, sessions, chat, settings, planner, activityLog, memory, uiState, clock)

        let capture =
            CaptureService(inbox, modules, assignments, events, notes, documents, links, importService, ai, settings, activityLog, clock)

        let dashboard =
            DashboardService(assignments, events, sessions, notes, inbox, modules, settings, planner, activityLog, clock)

        let briefing =
            BriefingService(dashboard, events, sessions, planner, activityLog, ai, clock)

        let memoryService =
            MemoryService(memory, assignments, sessions, uiState, ai, activityLog, clock)

        let sink = IngestionSink(events, inbox, clock) :> IIngestionSink

        let connectors: IConnector list =
            [ ClaudeConnector(secrets, aiProvider)
              MoodleConnector(secrets, connections, sink, http)
              GitLabConnector(secrets, connections, sink, http)
              GoogleConnectors.gmail (secrets, connections, sink, http)
              GoogleConnectors.googleCalendar (secrets, connections, sink, http)
              GoogleConnectors.googleDrive (secrets, connections, sink, http)
              MicrosoftConnectors.outlookMail (secrets, connections, sink, http)
              MicrosoftConnectors.outlookCalendar (secrets, connections, sink, http)
              MicrosoftConnectors.oneDrive (secrets, connections, sink, http) ]

        let registry = ConnectorRegistry(connectors, connections, syncLog, activityLog, clock)

        // No background sync under review: a review pass must not reach the
        // network, and a sync landing mid-screenshot would defeat the frozen
        // clock it was frozen for.
        if reviewing then
            ReviewMode.seed
                { Modules = modules
                  Assignments = assignments
                  Events = events
                  Notes = notes
                  Sessions = sessions
                  Documents = documents
                  Lectures = lectures
                  Revision = revision
                  Milestones = milestones
                  Links = links
                  Inbox = inbox
                  Chat = chat
                  Connections = connections
                  SyncLog = syncLog
                  Activity = activityLog
                  Memory = memory
                  Marker = ReviewMode.SettingsTableMarker db }
                appData
            |> Async.RunSynchronously
        else
            registry.StartBackgroundSync() |> ignore

        { Clock = clock
          Modules = modules
          Assignments = assignments
          Events = events
          Sessions = sessions
          Notes = notes
          Documents = documents
          Lectures = lectures
          Revision = revision
          Milestones = milestones
          Links = links
          Inbox = inbox
          Chat = chat
          Search = search
          Settings = settings
          SyncLog = syncLog
          Activity = activityLog
          Memory = memory
          UiState = uiState
          Appearance = appearance
          Registry = registry
          Import = importService
          Ai = ai
          Planner = planner
          Capture = capture
          Dashboard = dashboard
          Briefing = briefing
          MemoryService = memoryService
          PickFiles = pickFiles
          ApplyTheme = applyTheme }

type MainWindow() as this =
    inherit HostWindow()

    do
        // Titled unmistakably, so a screenshot taken under fixture data can
        // never be presented as evidence about a real install.
        base.Title <-
            if ReviewMode.isActive () then
                "University OS - REVIEW MODE (fixture data)"
            else
                "University OS"
        base.Width <- 1380.0
        base.Height <- 880.0
        base.MinWidth <- 1100.0
        base.MinHeight <- 700.0
        this.FontFamily <- Design.Typography.ui

        // Modern desktop effects: Mica → acrylic → opaque fallback. Translucent
        // theme layers only engage on Windows 11+ (build 22000); everywhere else
        // the palette stays fully opaque, so effects can never hurt readability.
        base.TransparencyLevelHint <-
            [| Avalonia.Controls.WindowTransparencyLevel.Mica
               Avalonia.Controls.WindowTransparencyLevel.AcrylicBlur
               Avalonia.Controls.WindowTransparencyLevel.None |]
            :> System.Collections.Generic.IReadOnlyList<_>

        let glassCapable =
            OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build >= 22000

        if glassCapable then
            base.Background <- Brushes.Transparent

        Theme.setGlass glassCapable

        let pickFiles () =
            async {
                let! files =
                    this.StorageProvider.OpenFilePickerAsync(FilePickerOpenOptions(AllowMultiple = true, Title = "Import files"))
                    |> Async.AwaitTask

                return
                    files
                    |> Seq.choose (fun f ->
                        match f.TryGetLocalPath() with
                        | null -> None
                        | path -> Some path)
                    |> List.ofSeq
            }

        let applyTheme (mode: ThemeMode) =
            Theme.setMode mode

            match Application.Current with
            | null -> ()
            | app ->
                app.RequestedThemeVariant <-
                    match mode with
                    | ThemeMode.Dark -> ThemeVariant.Dark
                    | ThemeMode.Light -> ThemeVariant.Light

                FluentMaterial.apply app.Resources (Theme.surfaces ())

        // Compile every background shader once, before the first paint. Doing
        // this lazily would stall the first frame of whichever screen the user
        // navigated to — precisely when they are watching. Failure here is not
        // fatal: the renderer drops to Tier B (see Rendering/ShaderLibrary.fs).
        let shaderWatch = Diagnostics.Stopwatch.StartNew()
        ShaderLibrary.prewarmAll ()
        shaderWatch.Stop()

        printfn
            "[render] pre-warmed %d/%d background shaders in %dms"
            (ShaderLibrary.compiledCount ())
            4
            shaderWatch.ElapsedMilliseconds

        let services = Composition.build pickFiles applyTheme

        // Apply the persisted theme before the first render, from the ONE
        // authoritative source. This used to read UserSettings.Theme, which is
        // legacy persisted state the appearance system replaced — so a user who
        // chose Light in the Design Preview still started dark and then never
        // changed, because nothing downstream applied the appearance theme
        // either. Both halves of that are fixed: here, and in Shell's
        // publishAppearance.
        let initialAppearance = services.Appearance.Load() |> Async.RunSynchronously

        applyTheme (Design.Tokens.themeModeFor (SystemPreferences.osPrefersDark ()) initialAppearance)

        Avalonia.Input.DragDrop.SetAllowDrop(this, true)

        Program.mkProgram (fun () -> Shell.init services) (Shell.update services) Shell.view
        |> Program.withHost this
        |> Program.withSubscription (Shell.subscriptions this)
        |> Program.run

type App() =
    inherit Application()

    override this.Initialize() =
        this.Styles.Add(FluentTheme())
        this.RequestedThemeVariant <- ThemeVariant.Dark

        // Before the first paint, so no control is ever drawn with Fluent's own
        // neutral state colours. Re-applied on every theme change below.
        FluentMaterial.apply this.Resources (Theme.surfaces ())

        // Hover micro-interactions, applied by class/selector so views stay free
        // of per-item hover state. Opacity-based → works with any palette.
        let buttonHover =
            Style(fun (x: Avalonia.Styling.Selector) -> x.OfType<Avalonia.Controls.Button>().Class(":pointerover"))

        buttonHover.Setters.Add(Setter(Avalonia.Visual.OpacityProperty, 0.85))
        this.Styles.Add buttonHover

        // A text input inside a custom field surface must draw no chrome of its
        // own — see `Ui.bareInputClass`. Fluent styles the template part rather
        // than the control, so this has to reach the part too; a local value on
        // the TextBox cannot override it.
        let bareInput =
            Style(fun (x: Avalonia.Styling.Selector) ->
                x
                    .OfType<Avalonia.Controls.TextBox>()
                    .Class(Ui.bareInputClass ())
                    .Template()
                    .OfType<Avalonia.Controls.Border>()
                    .Name("PART_BorderElement"))

        bareInput.Setters.Add(Setter(Avalonia.Controls.Border.BackgroundProperty, Brushes.Transparent :> IBrush))
        bareInput.Setters.Add(Setter(Avalonia.Controls.Border.BorderThicknessProperty, Thickness 0.0))
        this.Styles.Add bareInput

        let hoverableHover =
            Style(fun (x: Avalonia.Styling.Selector) ->
                x.OfType<Avalonia.Controls.Border>().Class("hoverable").Class(":pointerover"))

        hoverableHover.Setters.Add(Setter(Avalonia.Visual.OpacityProperty, 0.88))
        this.Styles.Add hoverableHover

        // Smooth the opacity changes (120 ms) for buttons and hoverable rows.
        let makeTransitions () =
            let transitions = Avalonia.Animation.Transitions()

            transitions.Add(
                Avalonia.Animation.DoubleTransition(
                    Property = Avalonia.Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds 120.0
                )
            )

            transitions

        let buttonBase =
            Style(fun (x: Avalonia.Styling.Selector) -> x.OfType<Avalonia.Controls.Button>())

        buttonBase.Setters.Add(Setter(Avalonia.Visual.TransitionsProperty, makeTransitions ()))
        this.Styles.Add buttonBase

        let hoverableBase =
            Style(fun (x: Avalonia.Styling.Selector) -> x.OfType<Avalonia.Controls.Border>().Class("hoverable"))

        hoverableBase.Setters.Add(Setter(Avalonia.Visual.TransitionsProperty, makeTransitions ()))
        this.Styles.Add hoverableBase

        // Buttons whose visible surface is a GlassPanel in their content.
        //
        // Fluent draws a Button's fill on PART_ContentPresenter inside the
        // control template, and its :pointerover / :pressed / :disabled styles
        // set that presenter's Background *directly* — which outranks the
        // Button's own Background property however it is set. The Planner's
        // action row therefore rendered five flat grey blocks the moment those
        // actions were disabled, straight through the glass behind them.
        //
        // Scoped to one class on purpose: this is not a restyle of every Button
        // in the application, and no existing control carries the class.
        let glassActionClass = Ui.glassActionClass ()

        let glassActionSurface (state: string) =
            let style =
                Style(fun (x: Avalonia.Styling.Selector) ->
                    let button = x.OfType<Avalonia.Controls.Button>().Class(glassActionClass)
                    let stated = if state = "" then button else button.Class(state)

                    stated
                        .Template()
                        .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                        .Name("PART_ContentPresenter"))

            // The glass IS the surface. The presenter must contribute nothing in
            // any state, or the state colour composites through the tint.
            style.Setters.Add(
                Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Theme.transparent)
            )

            style.Setters.Add(
                Setter(Avalonia.Controls.Presenters.ContentPresenter.BorderBrushProperty, Theme.transparent)
            )

            this.Styles.Add style

        // Only the resting and disabled states are neutralised.
        //
        // `:pointerover` and `:pressed` are deliberately LEFT ALONE, because
        // Fluent paints those from `ButtonBackgroundPointerOver` and
        // `ButtonBackgroundPressed` — and `FluentMaterial` already maps both to
        // real material roles (Hover and Selected). That warm lift behind the
        // glass IS the hover feedback the Dashboard's quick actions have.
        // Suppressing it here, as an earlier version did, left these buttons
        // with no hover at all while every other button in the app had one.
        //
        // Resting needs no fill (the glass is the surface) and `:disabled` must
        // be killed outright, because `ButtonBackgroundDisabled` is the one
        // state Fluent still paints from its own cool grey.
        for state in Ui.glassActionNeutralisedStates () do
            glassActionSurface state

        // Disabled also dims, so it reads as unavailable rather than merely
        // unfilled — without changing geometry.
        let glassActionDisabled =
            Style(fun (x: Avalonia.Styling.Selector) ->
                x.OfType<Avalonia.Controls.Button>().Class(glassActionClass).Class(":disabled"))

        glassActionDisabled.Setters.Add(Setter(Avalonia.Visual.OpacityProperty, 0.55))
        this.Styles.Add glassActionDisabled

        // Hover and pressed feedback for glass controls.
        //
        // A GlassPanel paints through a Skia custom draw operation, and a
        // visual's Opacity does not reach one — verified by probe: at 0.35 the
        // label all but vanished while the glass fill was untouched. So the
        // app-wide `Button:pointerover → 0.85` does nothing to glass, and the
        // only other channel — the Fluent presenter fill showing through the
        // glass's transparency — is invisible behind a saturated accent.
        //
        // The veil is an ordinary Border inside the glass, so Opacity and
        // transitions work on it normally. It rests at 0 and is faded in here.
        let glassVeilClass = Ui.glassVeilClass ()

        // Selected on the veil ITSELF, never through its ancestor.
        //
        // A descendant selector hanging off `Button.glass-action:pointerover`
        // does not fire — verified with a fully opaque red veil, which never
        // appeared. Avalonia does not re-evaluate a descendant when an
        // ancestor's pseudo-class changes here. `Border.hoverable:pointerover`,
        // already used by `Ui.clickable`, is the shape that does work: the
        // element carries its own pseudo-class. The veil is therefore the
        // topmost, hit-testable child of the button, so the pointer lands on it
        // directly while clicks still bubble to the Button underneath.
        let veilBase =
            Style(fun (x: Avalonia.Styling.Selector) ->
                x.OfType<Avalonia.Controls.Border>().Class(glassVeilClass))

        veilBase.Setters.Add(Setter(Avalonia.Visual.TransitionsProperty, makeTransitions ()))

        // The RESTING opacity is a style setter, never a local value on the
        // Border. A local value outranks a style in Avalonia, so setting
        // `Border.opacity 0.0` in the view made the hover setter below
        // unreachable — the veil could never fade in. `Ui.clickable`'s hoverable
        // Border works for exactly this reason: it leaves opacity alone and lets
        // styles own it.
        veilBase.Setters.Add(Setter(Avalonia.Visual.OpacityProperty, 0.0))
        this.Styles.Add veilBase

        let veilHover =
            Style(fun (x: Avalonia.Styling.Selector) ->
                x.OfType<Avalonia.Controls.Border>().Class(glassVeilClass).Class(":pointerover"))

        veilHover.Setters.Add(Setter(Avalonia.Visual.OpacityProperty, 1.0))
        this.Styles.Add veilHover

    /// Teaches MemoryPressure how to release each tier, least valuable first.
    ///
    /// Registered here because this is the only place that legitimately sees
    /// every layer at once — the pipeline's surfaces, the painters' paints, the
    /// glass paints up in Controls, and the shader programs. Each action must
    /// leave its owner able to rebuild on next use; none of them may leave a
    /// control unable to render.
    member private _.RegisterMemoryRelease() =
        MemoryPressure.register ReleaseTier.TransientSurfaces (fun _ ->
            // Offscreen scene and blur surfaces; ensureScene/ensureBlur remake
            // them on the next frame.
            BackdropPipeline.dispose ())

        MemoryPressure.register ReleaseTier.BackdropImage (fun _ ->
            // Glass falls back to its tint for one frame, then resamples.
            BackdropCache.requestClear ()
            BackdropCache.applyPendingClear ())

        MemoryPressure.register ReleaseTier.GpuResourceCache (fun grContext ->
            if not (isNull (box grContext)) then
                // 0ms = everything not in use right now. Skia re-uploads on
                // demand, so this costs upload time, never correctness.
                grContext.PurgeUnusedResources 0L
                grContext.PurgeResources())

        MemoryPressure.register ReleaseTier.PainterCaches (fun _ ->
            // Reused SKPaint/SKShader sets; ensurePaints remakes them.
            BackgroundPainter.dispose ()
            GlassPainter.dispose ())

        MemoryPressure.register ReleaseTier.CompiledShaders (fun _ ->
            // Last resort. releaseUnderPressure (not dispose) arms the
            // on-demand recompile, so Tier A returns by itself.
            ShaderLibrary.releaseUnderPressure ())

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            this.RegisterMemoryRelease()
            // Low frequency by design: a safety net, not a hot path.
            MemoryPressure.start 5000
            desktop.MainWindow <- MainWindow()

            // Developer console diagnostics, off unless the run asks for them
            // (UNIVERSITYOS_RENDER_LOG=1). Same metrics as the in-app panel.
            let renderLog = RenderReport.startConsoleLogger 5000

            // Every native Skia resource the renderer owns is released here.
            // Each of these caches created its own objects, so each disposes
            // them — there is no shared ownership to reason about.
            desktop.Exit.Add(fun _ ->
                renderLog.Dispose()
                MemoryPressure.stop ()
                FrameClock.setRunning false
                BackdropCache.dispose ()
                BackdropPipeline.dispose ()
                BackgroundPainter.dispose ()
                GlassPainter.dispose ()
                ShaderLibrary.dispose ())
        | _ -> ()

        base.OnFrameworkInitializationCompleted()

module Program =

    [<EntryPoint>]
    let main argv =
        AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .StartWithClassicDesktopLifetime(argv)
