namespace UniversityOS.App

open System
open Elmish
open Avalonia.Threading
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.App.Design

/// Top-level navigation targets. Defined here rather than in the shell so
/// feature views can request navigation without referencing shell types.
[<RequireQualifiedAccess>]
type Page =
    | Dashboard
    | Planner
    | Calendar
    | Modules
    | Assignments
    | Notes
    | Documents
    | Lectures
    | Graph
    | Inbox
    | Chat
    | Search
    | Timeline
    | Connections
    | Settings
    /// Developer surface for the design system: every background mode, glass
    /// over the live background, and the appearance controls driving both.
    | DesignPreview

[<RequireQualifiedAccess>]
module Page =
    /// Sidebar structure: named groups, each independently collapsible.
    /// One place to change navigation — the sidebar, command palette and
    /// keyboard shortcuts all derive from this.
    /// Groups, order and glyphs come from the redesign, which is the source of
    /// truth for the interface. They also line up with `backgroundSection`
    /// below, so Adaptive backgrounds change exactly when the sidebar group
    /// heading does — moving within a group keeps a stable sense of place.
    let groups: (string * (Page * string * string) list) list =
        [ "Overview",
          [ Page.Dashboard, "⌂", "Dashboard"
            Page.Timeline, "↷", "Timeline" ]
          "Work",
          [ Page.Planner, "◷", "Planner"
            Page.Inbox, "▣", "Inbox"
            Page.Assignments, Typography.asText "☑", "Assignments"
            Page.Modules, "◫", "Modules"
            Page.Lectures, "▶", "Lectures" ]
          "Knowledge",
          [ Page.Notes, "▤", "Notes"
            Page.Documents, "▧", "Documents"
            Page.Graph, "◈", "Graph"
            Page.Chat, "✦", "Assistant"
            Page.Search, "⌕", "Search"
            Page.Calendar, "▦", "Calendar" ]
          "System",
          [ Page.Connections, "⇄", "Connections"
            Page.Settings, "⚙", "Settings"
            Page.DesignPreview, "◐", "Design Preview" ] ]

    /// Flat navigation order (drives Ctrl+1..9 and the command palette).
    let all = groups |> List.collect snd

    let title (page: Page) =
        all |> List.tryFind (fun (p, _, _) -> p = page) |> Option.map (fun (_, _, t) -> t) |> Option.defaultValue ""

    let icon (page: Page) =
        all |> List.tryFind (fun (p, _, _) -> p = page) |> Option.map (fun (_, i, _) -> i) |> Option.defaultValue "•"

    /// Where each entity kind lives — search hits, timeline entries and graph
    /// nodes all navigate through this one map.
    let forEntity (kind: UniversityOS.Domain.EntityKind) =
        match kind with
        | EntityKind.Module -> Page.Modules
        | EntityKind.Assignment -> Page.Assignments
        | EntityKind.Event -> Page.Calendar
        | EntityKind.Note -> Page.Notes
        | EntityKind.Document -> Page.Documents
        | EntityKind.Lecture -> Page.Lectures
        | EntityKind.InboxItem -> Page.Inbox
        | EntityKind.Session -> Page.Planner
        | EntityKind.Conversation -> Page.Chat
        | EntityKind.RevisionItem -> Page.Modules

    /// Screens migrated to the redesign draw their own glass cards over the
    /// live background, so the shell must not put an opaque surface behind
    /// them. Every screen is now migrated, so every case is `true` — but the
    /// list stays enumerated rather than collapsing to a constant. Written this
    /// way, adding a page raises an incomplete-match warning and forces the
    /// question to be answered; a bare `true` would answer it silently.
    let usesGlassSurface (page: Page) =
        match page with
        | Page.Timeline
        | Page.Dashboard
        | Page.Planner
        | Page.Inbox
        | Page.Assignments
        | Page.Modules
        | Page.Lectures
        | Page.Notes
        | Page.Documents
        | Page.Graph
        | Page.Chat
        | Page.Search
        | Page.Calendar
        | Page.Connections
        | Page.Settings
        | Page.DesignPreview -> true

    /// Which background family a page belongs to. Adaptive mode reads this, so
    /// moving between related screens keeps a stable sense of place.
    let backgroundSection (page: Page) =
        match page with
        | Page.Dashboard
        | Page.Timeline -> Overview
        | Page.Planner
        | Page.Inbox
        | Page.Assignments
        | Page.Modules
        | Page.Lectures -> Work
        | Page.Notes
        | Page.Documents
        | Page.Graph
        | Page.Chat
        | Page.Search
        | Page.Calendar -> Knowledge
        | Page.Connections
        | Page.Settings
        | Page.DesignPreview -> SystemArea

/// The composition root's output: every dependency a feature can use, wired in
/// Program.fs and closed over by init/update. Views never see infrastructure
/// types — only these interfaces and services.
type AppServices =
    { Clock: IClock
      Modules: IModuleRepository
      Assignments: IAssignmentRepository
      Events: IEventRepository
      Sessions: ISessionRepository
      Notes: INoteRepository
      Documents: IDocumentRepository
      Lectures: ILectureRepository
      Revision: IRevisionRepository
      Milestones: IMilestoneRepository
      Links: ILinkRepository
      Inbox: IInboxRepository
      Chat: IChatRepository
      Search: ISearchRepository
      Settings: ISettingsStore
      SyncLog: ISyncLogRepository
      Activity: IActivityLog
      Memory: IMemoryRepository
      UiState: IUiStateStore
      Appearance: IAppearanceStore
      Registry: IConnectorRegistry
      Import: IDocumentImportService
      Ai: AiService
      Planner: PlannerService
      Capture: CaptureService
      Dashboard: DashboardService
      Briefing: BriefingService
      MemoryService: MemoryService
      /// Opens the OS file picker (implemented against the main window).
      PickFiles: unit -> Async<string list>
      /// Applies a theme change to the running Avalonia application.
      ApplyTheme: ThemeMode -> unit }

/// Elmish command helpers that always dispatch on the Avalonia UI thread —
/// async work completes on the thread pool, but view patching must not.
[<RequireQualifiedAccess>]
module Elm =

    let ofAsync (work: Async<'a>) (ok: 'a -> 'msg) (err: exn -> 'msg) : Cmd<'msg> =
        Cmd.ofEffect (fun dispatch ->
            Async.StartWithContinuations(
                work,
                (fun result -> Dispatcher.UIThread.Post(fun () -> dispatch (ok result))),
                (fun ex -> Dispatcher.UIThread.Post(fun () -> dispatch (err ex))),
                (fun _ -> ())
            ))

    let delayed (ms: int) (msg: 'msg) : Cmd<'msg> =
        ofAsync (Async.Sleep ms) (fun _ -> msg) (fun _ -> msg)
