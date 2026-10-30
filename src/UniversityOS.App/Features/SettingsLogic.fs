module UniversityOS.App.Features.SettingsLogic

open System
open UniversityOS.Domain
open UniversityOS.App

/// Every decision the Settings screen makes, as pure functions.
///
/// **Settings owns `UserSettings` and one field of `UiState`. Nothing else.**
///
/// * `AppearanceSettings` is owned by `Page.DesignPreview` and written by the
///   shell's single appearance writer. Settings **hands off** to it and never
///   writes it — a second writer for one store is how two screens end up
///   disagreeing about the theme.
/// * Credentials live in `DpapiSecretStore`, reachable only through
///   `IConnectorRegistry`. **`AppServices` carries no `ISecretStore`**, so a
///   secret field here is not a design choice — it cannot be built. Settings
///   hands off to Connections.

// ---------------------------------------------------------------------------
// categories
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type Category =
    | Profile
    | Planner
    | Ai
    | Appearance
    | Notifications
    | Integrations
    | Privacy
    | Advanced

[<RequireQualifiedAccess>]
module Category =
    /// The redesign's own order, pinned.
    let all =
        [ Category.Profile
          Category.Planner
          Category.Ai
          Category.Appearance
          Category.Notifications
          Category.Integrations
          Category.Privacy
          Category.Advanced ]

    let label =
        function
        | Category.Profile -> "Profile"
        | Category.Planner -> "Planner"
        | Category.Ai -> "AI"
        | Category.Appearance -> "Appearance"
        | Category.Notifications -> "Notifications"
        | Category.Integrations -> "Integrations"
        | Category.Privacy -> "Privacy"
        | Category.Advanced -> "Advanced"

    let glyph =
        function
        | Category.Profile -> "◍"
        | Category.Planner -> "◷"
        | Category.Ai -> "✦"
        | Category.Appearance -> "◐"
        | Category.Notifications -> "◔"
        | Category.Integrations -> "⇄"
        | Category.Privacy -> "▤"
        | Category.Advanced -> "⚙"

/// **Which categories edit anything.** The rest are handoffs or plain statements
/// of fact, and the screen must not dress them up with controls.
let isEditable =
    function
    | Category.Profile
    | Category.Planner
    | Category.Ai -> true
    | _ -> false

let categoryCaption =
    function
    | Category.Profile -> "Your name, and where it is used."
    | Category.Planner -> "How the planner schedules your work."
    | Category.Ai -> "What the Assistant may do on your behalf."
    | Category.Appearance -> "Configured in Design Preview."
    | Category.Notifications -> "Not delivered yet."
    | Category.Integrations -> "Configured in Connections."
    | Category.Privacy -> "Where your data lives."
    | Category.Advanced -> "Shortcuts and diagnostics."

// ---------------------------------------------------------------------------
// the draft
// ---------------------------------------------------------------------------

/// **`UserSettings.Theme` is deliberately excluded from every comparison.**
///
/// It is retired compatibility data — the domain says *"Nothing reads this
/// field… Do not wire it back up."* Including it in dirty-state would let a
/// stale legacy value mark the form dirty forever, and including it in a save
/// would write it back.
let sameSettings (a: UserSettings) (b: UserSettings) =
    a.UserName = b.UserName
    && a.WorkdayStartHour = b.WorkdayStartHour
    && a.WorkdayEndHour = b.WorkdayEndHour
    && a.SessionMinutes = b.SessionMinutes
    && a.BreakMinutes = b.BreakMinutes
    && a.MaxDailyStudyHours = b.MaxDailyStudyHours
    && a.DeadlineBufferHours = b.DeadlineBufferHours
    && a.PlannerHorizonDays = b.PlannerHorizonDays
    && a.AiModel = b.AiModel
    && a.AiAutoApply = b.AiAutoApply

let isDirty (saved: UserSettings) (draft: UserSettings) = not (sameSettings saved draft)

/// Why the draft cannot be saved. `None` means it can.
let validationError (draft: UserSettings) =
    if draft.WorkdayEndHour <= draft.WorkdayStartHour then
        Some "The study day must end after it starts."
    elif draft.SessionMinutes < 5 then
        Some "A session must be at least 5 minutes."
    elif draft.BreakMinutes < 0 then
        Some "A break cannot be negative."
    elif draft.MaxDailyStudyHours <= 0.0 then
        Some "The daily limit must be more than zero hours."
    elif draft.PlannerHorizonDays < 1 then
        Some "The planner needs at least one day to schedule into."
    elif draft.AiModel.Trim().Length = 0 then
        Some "A model identifier is required."
    else
        None

let canSave (saving: bool) (saved: UserSettings) (draft: UserSettings) =
    not saving && isDirty saved draft && (validationError draft).IsNone

/// Why Save is unavailable, for the text beside it.
let saveBlockedReason (saving: bool) (saved: UserSettings) (draft: UserSettings) =
    if saving then Some "Saving…"
    elif not (isDirty saved draft) then Some "No changes to save."
    else validationError draft

[<RequireQualifiedAccess>]
type SaveState =
    | Idle
    | Saving
    | Saved
    | Failed of string

let saveStateText =
    function
    | SaveState.Idle -> None
    | SaveState.Saving -> Some "Saving…"
    | SaveState.Saved -> Some "Saved."
    | SaveState.Failed message -> Some("Could not save: " + message)

// ---------------------------------------------------------------------------
// planner settings, and what each genuinely changes
// ---------------------------------------------------------------------------

/// Every planner preference, with **the runtime behaviour it actually
/// changes**. A setting whose description overstates its effect is worse than
/// one with no description.
let plannerEffects =
    [ "Study day", "The window the planner may place work in."
      "Session length", "How long one focused block is."
      "Break length", "The gap the planner leaves between blocks."
      "Daily limit", "The most work the planner will schedule in one day, and the Dashboard's daily target."
      "Planning horizon", "How far ahead the planner schedules." ]

/// **`DeadlineBufferHours` changes wording, not scheduling.**
///
/// `Planning.effectiveDeadline` has exactly two references: its definition, and
/// one use inside `lateNote`, which produces the string `" · tight against
/// deadline"`. Candidate selection sorts by score and never filters on it.
[<Literal>]
let deadlineBufferLabel = "Deadline warning threshold"

[<Literal>]
let deadlineBufferNotice =
    "Only changes when a scheduled block is labelled \"tight against deadline\". It does not change scheduling order, capacity, feasibility or when work is placed."

let hourOptions = [ 5 .. 23 ]
let sessionOptions = [ 25; 30; 45; 50; 60; 90 ]
let breakOptions = [ 5; 10; 15; 20 ]
let maxDailyOptions = [ 2.0; 3.0; 4.0; 6.0; 8.0; 10.0 ]
let bufferOptions = [ 0.0; 6.0; 12.0; 24.0; 48.0 ]
let horizonOptions = [ 7; 14; 21; 28 ]

let hourLabel (h: int) = sprintf "%02d:00" h
let bufferLabel (h: float) = if h = 0.0 then "No warning" else sprintf "%g h before" h

// ---------------------------------------------------------------------------
// AI
// ---------------------------------------------------------------------------

let modelOptions =
    [ "claude-opus-4-8"; "claude-opus-5"; "claude-sonnet-5"; "claude-haiku-4-5-20251001" ]

/// `CaptureService.fs:56` genuinely branches on this, so it is described by what
/// it does rather than by a bare label.
[<Literal>]
let autoApplyNotice =
    "When on, the Assistant applies a capture's proposed changes without asking first. When off, it proposes them and waits."

/// `AiService` and `MemoryService` both branch on `UiState.AiMemoryEnabled`.
[<Literal>]
let memoryNotice =
    "When on, the Assistant may keep long-term notes about you and use them in later conversations."

/// **No secret field, and no claim about one.**
[<Literal>]
let credentialHandoffNotice =
    "The API key for the AI provider is stored with the Claude connector, not here. Settings cannot read it, and does not know whether one is saved."

// ---------------------------------------------------------------------------
// handoffs
// ---------------------------------------------------------------------------

/// A navigation card. The label must say what opens.
type Handoff = { Title: string; Body: string; Action: string; Target: Page }

let appearanceHandoff =
    { Title = "Appearance is configured in Design Preview"
      Body =
        "Theme, accent, density, corner radius, glass, background style and motion all live there, and it applies them live. Settings does not edit them, so the two can never disagree."
      Action = "Open Design Preview"
      Target = Page.DesignPreview }

let integrationsHandoff =
    { Title = "External services are configured in Connections"
      Body =
        "Nine providers — Claude, Moodle, GitLab, Gmail, Google Calendar, Google Drive, Outlook, Outlook Calendar and OneDrive — with their credentials, sign-in and import schedules."
      Action = "Open Connections"
      Target = Page.Connections }

let aiCredentialHandoff =
    { Title = "Provider credentials are configured in Connections"
      Body = credentialHandoffNotice
      Action = "Open Connections"
      Target = Page.Connections }

// ---------------------------------------------------------------------------
// notifications — orphaned, and said plainly
// ---------------------------------------------------------------------------

/// `NotificationsEnabled` is stored and read by nobody. Verified: its only
/// references outside the domain are the old settings form's own toggle and its
/// own render.
[<Literal>]
let notificationsNotice =
    "University OS does not currently deliver notifications. There is no reminder scheduler, no desktop notification and no tray icon."

[<Literal>]
let notificationsLegacyNotice =
    "A legacy notification preference is still stored so older saved settings keep loading. Nothing reads it, so it is not shown as a setting."

// ---------------------------------------------------------------------------
// privacy — informational, evidence-backed
// ---------------------------------------------------------------------------

/// Each line is traceable to an implementation. Nothing here is aspirational.
let privacyFacts =
    [ "Your data is stored in a local SQLite database on this computer."
      "Connector credentials are stored with Windows DPAPI, encrypted for your Windows user account."
      "The Assistant sends your messages, modules, open assignments, notes and this week's schedule to the configured AI provider."
      "Removing a connector deletes its credentials from this computer. It does not revoke access at the provider."
      "Anything already imported stays in University OS after a connector is removed." ]

/// Claims that must never appear.
let forbiddenPrivacyClaims =
    [ "GDPR"; "secure wipe"; "permanently erased"; "no retention"; "end-to-end"; "cloud deletion" ]

// ---------------------------------------------------------------------------
// UiState classification
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type UiStateClass =
    /// A genuine user preference with a real consumer — belongs in Settings.
    | Exposed
    /// A real preference, but another surface already owns writing it.
    | RetainedInternally
    /// Runtime bookkeeping, not a preference.
    | Transient

/// **Every field of the third settings record, classified.**
let uiStateFields =
    [ "SidebarDensity", UiStateClass.RetainedInternally, "The shell owns it — Ctrl+B toggles it directly."
      "SidebarPinned", UiStateClass.RetainedInternally, "The shell owns it — the sidebar's own pin control."
      "LastBriefingDate", UiStateClass.Transient, "Bookkeeping: which day the Morning Briefing last opened."
      "LastReviewDate", UiStateClass.Transient, "Bookkeeping: which day the review last ran."
      "AiMemoryEnabled",
      UiStateClass.Exposed,
      "A real preference: AiService and MemoryService both branch on it." ]

let exposedUiStateFields =
    uiStateFields |> List.filter (fun (_, c, _) -> c = UiStateClass.Exposed) |> List.map (fun (n, _, _) -> n)

// ---------------------------------------------------------------------------
// orphaned appearance setting
// ---------------------------------------------------------------------------

/// `AppearanceSettings.AnimationSpeed` reaches `Tokens.AnimScale`, which is read
/// only by `Motion.durBase`/`durFast`/`scaled` — and **those have no production
/// callers**. Real animation timing is fixed (`ShellLayout.fs` says so
/// explicitly). So it is persisted, and changes nothing.
[<Literal>]
let animationSpeedFinding =
    "AnimationSpeed is persisted but has no runtime consumer, so it is not offered as a setting."

// ---------------------------------------------------------------------------
// shortcuts — fixed, from Shell.handleKeys
// ---------------------------------------------------------------------------

/// **Read-only.** `Shell.handleKeys` matches these as literals; nothing is read
/// from settings, so nothing here is editable.
let shortcuts =
    [ "Ctrl + K", "Command palette"
      "Ctrl + N", "Quick capture"
      "Ctrl + F", "Search"
      "Ctrl + B", "Sidebar density"
      "Ctrl + Shift + B", "Reveal the floating sidebar"
      "Ctrl + 1 … 9", "Jump to a section" ]

[<Literal>]
let shortcutsNotice = "These are fixed. University OS does not support reassigning them."

// ---------------------------------------------------------------------------
// composition and responsive layout
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type Section =
    | Header
    | Rail
    | Content

let sectionOrder = [ Section.Header; Section.Rail; Section.Content ]

[<RequireQualifiedAccess>]
module Geometry =

    [<Literal>]
    let contentHighlight = 0.0

    [<Literal>]
    let borderInset = 0.5

    [<Literal>]
    let railWidth = 220.0

    [<Literal>]
    let contentWidth = 620.0

    [<Literal>]
    let rowHeight = 40.0

    [<Literal>]
    let cardRadius = 18.0

    [<Literal>]
    let actionMinHeight = 38.0

    [<Literal>]
    let actionRadius = 11.0

    [<Literal>]
    let selectedFillAlpha = "1A"

    [<Literal>]
    let selectedEdgeAlpha = "40"

    [<Literal>]
    let chipFillAlpha = "1F"

    [<Literal>]
    let noticeFillAlpha = "1F"

    [<Literal>]
    let veilAlpha = "2E"

    [<Literal>]
    let detailColumns = "Auto,12,*"

[<Literal>]
let mediumWidth = 1100.0

[<Literal>]
let narrowWidth = 900.0

/// **The rail never disappears.** Below the narrow breakpoint it becomes a
/// wrapped row of chips above the content rather than a column beside it.
[<RequireQualifiedAccess>]
type RailPlacement =
    | Side
    | Top

let placementFor (width: float) =
    if width < narrowWidth then RailPlacement.Top else RailPlacement.Side

let railColumnWidth (width: float) =
    if width < mediumWidth then 180.0 else Geometry.railWidth
