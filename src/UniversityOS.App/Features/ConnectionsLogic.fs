module UniversityOS.App.Features.ConnectionsLogic

open System
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.App

/// Every decision the Connections screen makes, as pure functions.
///
/// **Three verified facts govern this screen.**
///
/// 1. `ConnectionStatus` has exactly four states and they are **persisted**.
///    Nothing moves them but a real `Configure`, `Test`, `Sync` or `Disconnect`,
///    so no passive claim — healthy, verified, up to date — can be made.
/// 2. Every connector's `Disconnect()` is a **local** `secrets.Delete`. **No
///    connector calls a remote revocation endpoint.**
/// 3. `ISecretStore` exposes `Get`, `Set`, `Delete` — there is **no `Exists`**.
///    Confirming that a credential is stored would mean reading its value, so
///    this screen does not claim to know.

// ---------------------------------------------------------------------------
// status
// ---------------------------------------------------------------------------

/// The label for each real state. **`ConnectionStatus.label` already exists in
/// the domain**; this re-states it only so the screen's wording is pinned by a
/// test rather than by a shared helper that another screen might change.
let statusLabel (status: ConnectionStatus) =
    match status with
    | ConnectionStatus.NotConfigured -> "Not configured"
    | ConnectionStatus.Disconnected -> "Disconnected"
    | ConnectionStatus.Connected -> "Connected"
    | ConnectionStatus.Failed _ -> "Failed"

/// What each state actually means, in terms of what the application did.
let statusDescription (status: ConnectionStatus) =
    match status with
    | ConnectionStatus.NotConfigured -> "No connection details have been saved."
    | ConnectionStatus.Disconnected ->
        "The stored credentials were removed. Configuration you saved is still here."
    | ConnectionStatus.Connected -> "The last configure, test or sync succeeded."
    | ConnectionStatus.Failed _ -> "The last attempt failed. The message below is what the provider returned."

/// A glyph as well as a colour, so state is never carried by hue alone.
let statusGlyph (status: ConnectionStatus) =
    match status with
    | ConnectionStatus.NotConfigured -> "○"
    | ConnectionStatus.Disconnected -> "◌"
    | ConnectionStatus.Connected -> "●"
    | ConnectionStatus.Failed _ -> "!"

let statusHex (status: ConnectionStatus) =
    match status with
    | ConnectionStatus.NotConfigured -> Theme.Identity.neutral
    | ConnectionStatus.Disconnected -> Theme.Identity.gold
    | ConnectionStatus.Connected -> Theme.successHex ()
    | ConnectionStatus.Failed _ -> Theme.dangerHex ()

let errorOf (status: ConnectionStatus) =
    match status with
    | ConnectionStatus.Failed message -> Some message
    | _ -> None

let isConnected (view: ConnectorView) = view.State.Status = ConnectionStatus.Connected

/// A connector the user has told something about — either it is connected, or it
/// has saved configuration, or it failed after being tried.
let isConfigured (view: ConnectorView) =
    match view.State.Status with
    | ConnectionStatus.NotConfigured -> not (Map.isEmpty view.State.Config)
    | _ -> true

let needsAttention (view: ConnectorView) =
    match view.State.Status with
    | ConnectionStatus.Failed _ -> true
    | _ -> false

// ---------------------------------------------------------------------------
// grouping and sorting
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type Group =
    | Connected
    | Available

[<RequireQualifiedAccess>]
module Group =
    let label =
        function
        | Group.Connected -> "Connected"
        | Group.Available -> "Available"

    let caption =
        function
        | Group.Connected -> "Providers with a working connection."
        | Group.Available -> "Providers you have not connected yet, or that need attention."

let groupOrder = [ Group.Connected; Group.Available ]

let groupOf (view: ConnectorView) =
    if isConnected view then Group.Connected else Group.Available

/// Deterministic: by the domain's own category order, then by name. Sorting by
/// status instead would make cards jump between groups *and* positions when a
/// sync failed.
let sortProviders (views: ConnectorView list) =
    views
    |> List.sortBy (fun v ->
        let categoryIndex =
            ConnectorCategory.ordered
            |> List.tryFindIndex ((=) v.Metadata.Category)
            |> Option.defaultValue ConnectorCategory.ordered.Length

        categoryIndex, v.Metadata.Name)

let grouped (views: ConnectorView list) =
    groupOrder
    |> List.map (fun g -> g, sortProviders (views |> List.filter (fun v -> groupOf v = g)))
    |> List.filter (fun (_, vs) -> not vs.IsEmpty)

// ---------------------------------------------------------------------------
// the summary
// ---------------------------------------------------------------------------

/// `None` renders as an em dash. **Never a zero standing in for "unknown"** —
/// a zero here is a real count of zero.
type Summary =
    { Connected: int
      NeedsAttention: int
      Available: int
      /// The most recent successful sync across every connector, or `None` when
      /// nothing has ever synced.
      LastSync: DateTime option }

let summarise (views: ConnectorView list) =
    { Connected = views |> List.filter isConnected |> List.length
      NeedsAttention = views |> List.filter needsAttention |> List.length
      Available = views |> List.filter (isConnected >> not) |> List.length
      LastSync = views |> List.choose (fun v -> v.State.LastSync) |> function [] -> None | ds -> Some(List.max ds) }

[<Literal>]
let unavailable = "—"

// ---------------------------------------------------------------------------
// last sync
// ---------------------------------------------------------------------------

/// **Three genuinely different answers.** A connector that cannot import is not
/// "never synced" — it has nothing to sync.
let lastSyncText (now: DateTime) (view: ConnectorView) =
    if not view.Metadata.SupportsSync then
        "Does not import data"
    else
        match view.State.LastSync with
        | Some at -> "Last import " + Format.relativeTime now at
        | None -> "Never imported"

/// The real rule from `ConnectorRegistry.SetSyncFrequency`.
[<Literal>]
let minimumFrequencyMinutes = 5

let frequencyOptions = [ 15; 30; 60; 180; 360 ]

let frequencyLabel (minutes: int) =
    if minutes < 60 then sprintf "Every %d min" minutes
    elif minutes = 60 then "Hourly"
    else sprintf "Every %d h" (minutes / 60)

/// Background sync is real, but it is a poll on a timer — never continuous.
[<Literal>]
let syncCadenceNotice =
    "Checked automatically when the configured interval is due, while University OS is running."

// ---------------------------------------------------------------------------
// access descriptions
// ---------------------------------------------------------------------------

/// **Exact OAuth scopes are not available to the UI** — they are passed straight
/// into the authorisation URL and never surface in `ConnectorMetadata`. So this
/// describes what the connector genuinely *does with the data it fetches*,
/// traced to the `IIngestionSink` call each one makes.
///
/// | Connector | Sink call |
/// |---|---|
/// | Gmail, Google Drive, Outlook, OneDrive, GitLab | `AddInboxItem` |
/// | Google Calendar, Outlook Calendar, Moodle | `UpsertEvent` |
/// | Claude | none — it is the AI provider |
let accessLines (metadata: ConnectorMetadata) =
    match metadata.Key with
    | "claude" -> [ "Sends your Assistant messages and their context to this provider." ]
    | "moodle" -> [ "Imports course dates into your Calendar." ]
    | "gitlab" -> [ "Imports project activity into your Inbox."; "Imports dated project items into your Calendar." ]
    | "gmail"
    | "outlook" -> [ "Imports recent mail into your Inbox." ]
    | "google-calendar"
    | "outlook-calendar" -> [ "Imports calendar events into your Calendar." ]
    | "google-drive"
    | "onedrive" -> [ "Imports recently changed files into your Inbox." ]
    | _ -> [ metadata.Description ]

/// Imported records are matched on the provider's own identifier, so a repeated
/// import updates rather than duplicates. That is `IngestionSink`'s real rule.
[<Literal>]
let deduplicationNotice =
    "Imported items are matched on the provider's own identifier, so importing again updates them instead of adding copies."

// ---------------------------------------------------------------------------
// credentials
// ---------------------------------------------------------------------------

/// **What the credential store genuinely guarantees**, and nothing beyond it.
[<Literal>]
let credentialNotice =
    "Secrets are stored with Windows DPAPI, encrypted for your Windows user account on this computer. They are kept apart from the rest of a connection's saved settings."

[<Literal>]
let requestNotice = "Requests go from this computer straight to the provider."

/// **The disconnect truth.** Every connector's `Disconnect()` deletes a local
/// secret. Nothing contacts the provider.
[<Literal>]
let disconnectNotice =
    "Removes the credentials stored on this computer. It does not change anything in your provider account, and it does not revoke access there — do that from the provider's own security settings. Anything already imported stays in University OS."

let disconnectConfirm (name: string) =
    sprintf "Remove the stored credentials for %s from this computer?" name

/// A secret field is **always rendered blank**. `ISecretStore` has no existence
/// check, so the only way to know whether one is stored is to read its value —
/// which this screen will not do to decorate a placeholder.
[<Literal>]
let secretFieldNotice = "Leave blank to keep the credential already stored."

/// Non-secret configuration only. `ConnectorRegistry.Configure` filters secret
/// keys out before persisting, so a stored config can never hold one — but this
/// filters again rather than trusting that at the point of display.
let visibleConfig (view: ConnectorView) =
    let secretKeys =
        view.Metadata.Fields |> List.filter (fun f -> f.Secret) |> List.map (fun f -> f.Key) |> Set.ofList

    view.Metadata.Fields
    |> List.filter (fun f -> not f.Secret)
    |> List.choose (fun f ->
        match Map.tryFind f.Key view.State.Config with
        | Some value when not (secretKeys.Contains f.Key) && value.Trim().Length > 0 -> Some(f.Label, value)
        | _ -> None)

let configuredFieldCount (view: ConnectorView) = (visibleConfig view).Length

// ---------------------------------------------------------------------------
// actions
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type Action =
    | Configure
    | Test
    | Sync
    | Disconnect

[<RequireQualifiedAccess>]
module Action =
    let key =
        function
        | Action.Configure -> "configure"
        | Action.Test -> "test"
        | Action.Sync -> "sync"
        | Action.Disconnect -> "disconnect"

/// The label depends on where the connection already is: the same operation is
/// "Connect" the first time and "Reconnect" after a failure.
let actionLabel (status: ConnectionStatus) (action: Action) =
    match action with
    | Action.Configure ->
        match status with
        | ConnectionStatus.NotConfigured -> "Connect"
        | ConnectionStatus.Disconnected -> "Reconnect"
        | ConnectionStatus.Failed _ -> "Fix and retry"
        | ConnectionStatus.Connected -> "Edit configuration"
    | Action.Test -> "Test connection"
    | Action.Sync -> "Import now"
    | Action.Disconnect -> "Remove credentials"

/// **Only operations the connector can actually perform.**
///
/// * `Sync` needs `SupportsSync`.
/// * `Test`, `Sync` and `Disconnect` need something to have been saved — testing
///   a connector that has never been configured would just produce a confusing
///   provider error.
let availableActions (view: ConnectorView) =
    [ Action.Configure

      if isConfigured view then
          Action.Test

          if view.Metadata.SupportsSync then
              Action.Sync

          Action.Disconnect ]

/// Why an action cannot be used right now. `None` means it can.
let actionBlockedReason (busy: (string * string) option) (view: ConnectorView) (action: Action) =
    match busy with
    | Some(key, running) when key = view.Metadata.Key ->
        Some(
            match running with
            | "configure" -> "Connecting…"
            | "test" -> "Testing…"
            | "sync" -> "Importing…"
            | "disconnect" -> "Removing…"
            | _ -> "Working…"
        )
    | Some _ -> Some "Another connector is busy."
    | None ->
        match action with
        | Action.Sync when not view.Metadata.SupportsSync -> Some "This provider does not import data."
        | Action.Test when not (isConfigured view) -> Some "Save the connection details first."
        | Action.Sync when not (isConfigured view) -> Some "Save the connection details first."
        | Action.Disconnect when not (isConfigured view) -> Some "There is nothing stored to remove."
        | _ -> None

let canRun busy view action = (actionBlockedReason busy view action).IsNone

// ---------------------------------------------------------------------------
// the OAuth flow
// ---------------------------------------------------------------------------

/// `OAuth.runInteractive` opens the **system browser** and listens on a loopback
/// callback, timing out after five minutes. The user is told before it happens,
/// because an application that silently opens a browser is alarming.
let oauthNotice (metadata: ConnectorMetadata) =
    if metadata.Auth = AuthKind.OAuth then
        Some
            "Connecting opens your web browser to sign in with this provider. University OS waits up to five minutes for you to finish, then stores the result on this computer."
    else
        None

let authLabel (metadata: ConnectorMetadata) = AuthKind.label metadata.Auth

// ---------------------------------------------------------------------------
// sync history
// ---------------------------------------------------------------------------

/// One row of `ISyncLogRepository.GetRecent` — `(connectorKey, finishedAt, outcome)`.
type HistoryRow =
    { Key: string
      Provider: string
      At: DateTime
      Outcome: string }

/// The log stores its outcome as free text. **No count is parsed out of it** —
/// inventing a number from a sentence is exactly the kind of guess this project
/// refuses. The stored text is shown as written.
let historyRows (views: ConnectorView list) (entries: (string * DateTime * string) list) =
    let nameOf key =
        views
        |> List.tryFind (fun v -> v.Metadata.Key = key)
        |> Option.map (fun v -> v.Metadata.Name)
        |> Option.defaultValue key

    entries
    |> List.map (fun (key, at, outcome) -> { Key = key; Provider = nameOf key; At = at; Outcome = outcome })
    |> List.sortByDescending (fun r -> r.At)

[<Literal>]
let historyHeading = "Recent imports"

[<Literal>]
let historyEmptyNotice = "Nothing has been imported yet."

// ---------------------------------------------------------------------------
// composition and responsive layout
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type Section =
    | Header
    | Summary
    | Providers
    | Detail
    | History

let sectionOrder =
    [ Section.Header; Section.Summary; Section.Providers; Section.Detail; Section.History ]

/// The selected provider's regions, in order.
[<RequireQualifiedAccess>]
type DetailRegion =
    | Identity
    | Status
    | Access
    | Configuration
    | Sync
    | Actions
    | Privacy

let detailOrder =
    [ DetailRegion.Identity
      DetailRegion.Status
      DetailRegion.Access
      DetailRegion.Configuration
      DetailRegion.Sync
      DetailRegion.Actions
      DetailRegion.Privacy ]

[<RequireQualifiedAccess>]
module Geometry =

    [<Literal>]
    let contentHighlight = 0.0

    [<Literal>]
    let borderInset = 0.5

    [<Literal>]
    let cardMinWidth = 330.0

    [<Literal>]
    let detailWidth = 380.0

    [<Literal>]
    let iconTile = 42.0

    [<Literal>]
    let iconTileRadius = 12.0

    [<Literal>]
    let cardRadius = 18.0

    [<Literal>]
    let actionMinHeight = 38.0

    [<Literal>]
    let actionRadius = 11.0

    /// The provider's own colour, used as a restrained accent. The redesign
    /// paints the whole card at `{{c.color}}14`; that is kept faint deliberately
    /// — a full-strength brand fill behind body text is unreadable at several of
    /// these hues.
    [<Literal>]
    let brandWashAlpha = "14"

    [<Literal>]
    let tileAlpha = "22"

    [<Literal>]
    let chipFillAlpha = "1F"

    [<Literal>]
    let selectedFillAlpha = "24"

    [<Literal>]
    let selectedEdgeAlpha = "59"

    [<Literal>]
    let noteFillAlpha = "1F"

    [<Literal>]
    let veilAlpha = "2E"

    [<Literal>]
    let detailColumns = "Auto,10,*"

[<Literal>]
let mediumWidth = 1280.0

[<Literal>]
let narrowWidth = 900.0

[<RequireQualifiedAccess>]
type DetailPlacement =
    | Rail
    | Below

type Placement =
    { Detail: DetailPlacement
      Columns: int }

/// Columns come from the redesign's `minmax(330px,1fr)` grid, resolved to a
/// count so the layout is a real Grid rather than a wrap that can leave a lone
/// card stretched across a row.
let placementFor (width: float) =
    if width < narrowWidth then { Detail = DetailPlacement.Below; Columns = 1 }
    elif width < mediumWidth then { Detail = DetailPlacement.Below; Columns = 2 }
    else { Detail = DetailPlacement.Rail; Columns = 3 }

/// A selection that is no longer in the list is cleared, never moved — offering
/// Disconnect on the wrong provider would be the worst possible slip here.
let reconcileSelection (selected: string option) (views: ConnectorView list) =
    selected |> Option.filter (fun key -> views |> List.exists (fun v -> v.Metadata.Key = key))

let selectedView (views: ConnectorView list) (selected: string option) =
    selected |> Option.bind (fun key -> views |> List.tryFind (fun v -> v.Metadata.Key = key))
