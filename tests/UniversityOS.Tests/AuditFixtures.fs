module UniversityOS.Tests.AuditFixtures

open System
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Features

/// Every migrated screen, renderable at any width, from one place.
///
/// The eleven per-screen test files each build their own model, which is right
/// for them — each is testing that screen's own behaviour and wants a model
/// shaped for it. A cross-screen audit needs the opposite: **the same question
/// asked of all eleven**, which means one place that can say "render Notes at
/// 360px" without knowing anything about Notes.
///
/// The models here are deliberately the *populated* ones. An empty screen has
/// almost no layout to audit: no rows to wrap, no rails to move, no long strings
/// to overflow. Every fixture therefore carries enough real content that the
/// responsive rules actually engage.

let tokens = Tokens.ofAppearance true 0 AppearanceSettings.defaults
let tokensLight = Tokens.ofAppearance false 0 AppearanceSettings.defaults
let noServices: AppServices = Unchecked.defaultof<AppServices>

/// Frozen, and the same instant Review Mode uses, so a fixture here and a screen
/// in the running review app describe the same week.
let now = DateTime(2026, 3, 4, 9, 30, 0)
let today = now.Date

// ---------------------------------------------------------------------------
// shared domain fixtures
// ---------------------------------------------------------------------------

let private moduleId n = ModuleId(Guid(sprintf "00000000-0000-0000-0000-%012d" n))

let studyModules =
    [ moduleId 101, "COMP3811", "Computer Graphics", 0
      moduleId 102, "COMP3611", "Machine Learning", 1
      moduleId 103, "COMP3931", "Individual Project", 2 ]
    |> List.map (fun (id, code, name, colour) ->
        { StudyModule.create now code name colour with
            Id = id
            Lecturer = Some "Dr H. Nakamura"
            Credits = Some 20
            Term = Some "Semester 2" })

/// The pathological string the long-content audit uses. Long, and — critically —
/// *unbroken*: a long sentence can wrap at a space, so it proves nothing about
/// overflow. Only a token with no break opportunity does.
let longUnbroken =
    "Coursework2FinalSubmissionRayTracerImplementationAndEvaluationReportRevisionFourteenAppendixB"

let longSentence =
    "Final project report covering the design, implementation and evaluation of a physically based renderer, including the appendices agreed at the fourth supervision meeting and the revised evaluation chapter"

let longPath =
    @"C:\Users\student\OneDrive - University of Leeds\Semester 2\COMP3811 Computer Graphics\Coursework 2\submission\final\raytracer-report-v14-with-appendices.pdf"

/// A realistic provider failure. Real ones are long, contain a URL with no break
/// opportunity, and are exactly what pushes a card's actions off screen if the
/// message is not allowed to wrap.
let longProviderError =
    "The provider rejected the request: HTTP 401 Unauthorized from https://gitlab.example.ac.uk/api/v4/projects?membership=true&per_page=100 — the stored access token was not accepted. Check that the token has not expired and that it carries read_api scope."

// ---------------------------------------------------------------------------
// per-screen models
// ---------------------------------------------------------------------------

let settingsModel () : SettingsView.Model =
    { Stored = UserSettings.defaults
      Draft = UserSettings.defaults
      Category = SettingsLogic.Category.Profile
      SaveState = SettingsLogic.SaveState.Idle
      Saving = false
      Loading = false
      Error = None
      Memories = []
      MemoryEnabled = true
      NewMemoryText = ""
      MemoryBusy = None
      MemoryNotice = None
      PendingDeleteMemory = None
      Width = 1600.0 }

let private field key label secret : ConfigField =
    { Key = key
      Label = label
      Secret = secret
      Placeholder = label
      Help = "" }

let private connector key name category auth supportsSync fields status : ConnectorView =
    { Metadata =
        { Key = key
          Name = name
          Category = category
          Description = name + " description."
          Icon = "◆"
          Auth = auth
          Fields = fields
          SupportsSync = supportsSync }
      State =
        { Key = key
          Status = status
          LastSync = None
          SyncFrequencyMinutes = 30
          Config = Map.empty
          UpdatedAt = now } }

/// The nine real connectors, with their real keys, names, categories and auth
/// kinds. Nine is the number that makes the 3/2/1 provider grid meaningful.
let nineProviders () =
    [ connector "claude" "Claude" ConnectorCategory.Ai AuthKind.ApiKey false [ field "api_key" "API key" true ] ConnectionStatus.Connected
      connector "moodle" "Moodle" ConnectorCategory.Academic AuthKind.AccessToken true [ field "base_url" "Site URL" false; field "token" "Token" true ] ConnectionStatus.Connected
      connector "gitlab" "GitLab" ConnectorCategory.Development AuthKind.AccessToken true [ field "base_url" "Server" false; field "token" "Token" true ] (ConnectionStatus.Failed longProviderError)
      connector "gmail" "Gmail" ConnectorCategory.Communication AuthKind.OAuth true [ field "client_id" "Client id" false; field "client_secret" "Client secret" true ] ConnectionStatus.Disconnected
      connector "google-calendar" "Google Calendar" ConnectorCategory.Calendar AuthKind.OAuth true [ field "client_id" "Client id" false ] ConnectionStatus.NotConfigured
      connector "google-drive" "Google Drive" ConnectorCategory.Storage AuthKind.OAuth true [ field "client_id" "Client id" false ] ConnectionStatus.NotConfigured
      connector "outlook" "Outlook" ConnectorCategory.Communication AuthKind.OAuth true [ field "client_id" "Client id" false ] ConnectionStatus.NotConfigured
      connector "outlook-calendar" "Outlook Calendar" ConnectorCategory.Calendar AuthKind.OAuth true [ field "client_id" "Client id" false ] ConnectionStatus.NotConfigured
      connector "onedrive" "OneDrive" ConnectorCategory.Storage AuthKind.OAuth true [ field "client_id" "Client id" false ] ConnectionStatus.NotConfigured ]

let connectionsModel () : ConnectionsView.Model =
    { Connectors = nineProviders ()
      History = [ "moodle", now.AddHours -2.0, "Imported 4 events" ]
      Selected = Some "claude"
      FieldValues = Map.empty
      Busy = None
      Messages = Map.empty
      PendingDisconnect = None
      Now = now
      Width = 1600.0
      Loading = false
      Error = None }

let calendarEvents () =
    let at (d: float) (h: float) = today.AddDays(d).AddHours h

    [ { CalendarEvent.create now "Computer Graphics lecture" EventKind.Lecture (at 0.0 9.0) (at 0.0 10.0) with
          ModuleId = Some(moduleId 101)
          Location = Some "Roger Stevens LT 8" }
      { CalendarEvent.create now "Project supervision" EventKind.Meeting (at 0.0 10.0) (at 0.0 10.5) with
          ModuleId = Some(moduleId 103) }
      { CalendarEvent.create now "Machine Learning exam" EventKind.Exam (at 2.0 9.0) (at 2.0 12.0) with
          ModuleId = Some(moduleId 102)
          AllDay = true }
      { CalendarEvent.create now "Graphics lab" EventKind.Other (at 1.0 14.0) (at 1.0 16.0) with
          ModuleId = Some(moduleId 101) } ]

let calendarModel () : CalendarView.Model =
    { Anchor = today
      Mode = CalendarLogic.ViewMode.Week
      Events = calendarEvents ()
      Sessions = []
      Modules = studyModules
      Filter = CalendarLogic.Filter.none
      Selected = None
      Draft = None
      PendingDelete = false
      Saving = false
      Loading = false
      Now = now
      Width = 1600.0
      Error = None }

let searchModel () : SearchView.Model =
    let model, _ = SearchView.init noServices
    { model with Width = 1600.0; Query = "ray" }

let assistantModel () : ChatView.Model =
    let model, _ = ChatView.init noServices

    { model with
        Loading = false
        Provider = AssistantLogic.ProviderState.Ready
        AiModel = "claude-opus-5"
        Width = 1600.0 }

let private node (kind: EntityKind) (label: string) (n: int) : GraphLogic.GraphNode =
    let entity = { Kind = kind; Id = Guid(sprintf "00000000-0000-0000-0000-%012d" n) }
    { Key = GraphLogic.nodeKey entity; Ref = entity; Label = label }

let graphModel () : GraphView.Model =
    let a = node EntityKind.Note "Ray-triangle intersection" 1
    let b = node EntityKind.Module "COMP3811" 2
    let c = node EntityKind.Assignment "Ray tracer coursework 2" 3

    { Nodes = [ a; b; c ]
      Edges = [ GraphLogic.normaliseEdge (a.Key, b.Key); GraphLogic.normaliseEdge (b.Key, c.Key) ]
      Adjacency = GraphLogic.adjacency [ a.Key, b.Key; b.Key, c.Key ]
      BaseLayout = Map.ofList [ a.Key, (100.0, 100.0); b.Key, (300.0, 200.0); c.Key, (200.0, 320.0) ]
      Overrides = Map.empty
      Filter = Set.empty
      Query = ""
      Selected = Some a.Key
      FocusMode = false
      View = GraphLogic.Viewport.identity
      Interaction = GraphLogic.Interaction.Idle
      LegendOpen = true
      InsightsOpen = true
      MinimapOpen = true
      CanvasWidth = 800.0
      CanvasHeight = 600.0
      Width = 1600.0
      Loading = false
      Error = None }

let documentsModel () : DocumentsView.Model =
    { Facts = []
      Modules = studyModules
      Selected = None
      View = DocumentsLogic.ViewMode.Grid
      Filters = DocumentsLogic.Selection.empty
      ModuleFilter = Set.empty
      Query = ""
      ModuleIndex = 0
      Importing = false
      Summarizing = false
      PendingDelete = false
      Notice = None
      Loading = false
      Error = None
      Width = 1600.0 }

let notesModel () : NotesView.Model =
    { Facts = []
      Modules = studyModules
      Selected = None
      TitleText = "Ray-triangle intersection"
      ContentText = "# Möller–Trumbore\n\nThe fast path **avoids** the plane equation.\n\n- edge vectors\n- determinant\n\n```\nif abs(det) < EPSILON: return MISS\n```"
      TagsText = "graphics, revision"
      ModuleIndex = 0
      Pinned = false
      Dirty = false
      Reading = true
      Focus = false
      ExplorerOpen = true
      ContextOpen = true
      Filters = NotesLogic.Selection.empty
      ModuleFilter = Set.empty
      Query = ""
      PendingDelete = false
      PendingSwitch = None
      Saving = false
      AiBusy = None
      Notice = None
      Loading = false
      Error = None
      Width = 1600.0 }

let lecturesModel () : LecturesView.Model =
    { Facts = []
      Modules = studyModules
      Filters = LecturesLogic.Selection.empty
      ModuleFilter = Set.empty
      Expanded = None
      Tabs = Map.empty
      Draft = None
      PendingDelete = false
      Attaching = None
      AiBusy = None
      Notice = None
      Saving = false
      Loading = false
      Error = None
      Width = 1600.0 }

let modulesModel () : ModulesView.Model =
    { Facts = []
      Filters = ModulesLogic.Selection.empty
      Expanded = None
      Tabs = Map.empty
      Draft = None
      PendingDelete = false
      StudiedThisWeek = 3
      Saving = false
      Loading = false
      Error = None
      Width = 1600.0 }

let assignmentsModel () : AssignmentsView.Model =
    { Facts = []
      Modules = studyModules
      Filter = AssignmentsLogic.Filter.All
      Expanded = None
      ExpansionChosen = false
      Draft = None
      PendingDelete = false
      AiAvailable = false
      WeekStart = StudyTime.weekStart DayOfWeek.Monday now
      Saving = false
      Loading = false
      Error = None
      Width = 1600.0 }

// ---------------------------------------------------------------------------
// the eleven screens, uniformly
// ---------------------------------------------------------------------------

/// A screen, reduced to the only thing a cross-screen audit needs: a name, and a
/// way to render it at a width in a theme.
type Screen =
    { Name: string
      /// Render at this screen width, in this token set.
      Render: Tokens -> float -> IView }

let private screenOf name (render: Tokens -> float -> IView) = { Name = name; Render = render }

/// **In the review order the brief specifies**, so an audit that iterates this
/// list reports in the same order a human will inspect.
let screens: Screen list =
    [ screenOf "Settings" (fun t w -> SettingsView.view t { settingsModel () with Width = w } ignore)
      screenOf "Connections" (fun t w -> ConnectionsView.view t { connectionsModel () with Width = w } ignore)
      screenOf "Calendar" (fun t w -> CalendarView.view t { calendarModel () with Width = w } ignore)
      screenOf "Search" (fun t w -> SearchView.view t { searchModel () with Width = w } ignore)
      screenOf "Assistant" (fun t w -> ChatView.view t { assistantModel () with Width = w } ignore)
      screenOf "Graph" (fun t w -> GraphView.view t { graphModel () with Width = w } ignore)
      screenOf "Documents" (fun t w -> DocumentsView.view t { documentsModel () with Width = w } ignore)
      screenOf "Notes" (fun t w -> NotesView.view t { notesModel () with Width = w } ignore)
      screenOf "Lectures" (fun t w -> LecturesView.view t { lecturesModel () with Width = w } ignore)
      screenOf "Modules" (fun t w -> ModulesView.view t { modulesModel () with Width = w } ignore)
      screenOf "Assignments" (fun t w -> AssignmentsView.view t { assignmentsModel () with Width = w } ignore) ]

/// Screen names as xUnit `[<InlineData>]` fodder, so a per-screen failure names
/// the screen in the test output instead of hiding inside one aggregate assert.
let screenNames = screens |> List.map (fun s -> s.Name)

let screenNamed (name: string) =
    screens |> List.find (fun s -> s.Name = name)

/// Renders in Dark at the given screen width.
let render (name: string) (width: float) = (screenNamed name).Render tokens width

let renderIn (theme: Tokens) (name: string) (width: float) = (screenNamed name).Render theme width
