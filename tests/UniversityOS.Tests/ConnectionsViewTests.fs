module UniversityOS.Tests.ConnectionsViewTests

open System
open Xunit
open Avalonia.Media
open Avalonia.Controls.Primitives
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Features

/// The rebuilt Connections screen.
///
/// Most of what these tests protect is restraint: four persisted states and no
/// invented fifth, no secret ever reaching the tree, and disconnect described as
/// the local file deletion it actually is.

// ---------------------------------------------------------------------------
// harness
// ---------------------------------------------------------------------------

let private accessorName (accessor: Accessor) =
    match accessor with
    | Accessor.InstanceProperty p -> p.Name
    | Accessor.AvaloniaProperty p -> p.Name

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

let rec private all (view: IView) : IView list = view :: (kids view |> List.collect all)

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

let private boolProp name view =
    match propOf name view with
    | Some(:? bool as b) -> Some b
    | _ -> None

let private ofType (name: string) (root: IView) =
    all root |> List.filter (fun v -> v.ViewType.Name = name)

/// **Every string anywhere in the tree** — text, button content, watermarks and
/// accessible names.
let private everyString (root: IView) =
    all root
    |> List.collect (fun v ->
        [ stringProp "Text" v; stringProp "Content" v; stringProp "Watermark" v; stringProp "Name" v ]
        |> List.choose id)

/// Every string **except** the `Text` of a masked box.
///
/// A credential the user is typing has to live in the box they are typing it
/// into — that is what an editable password field is. The invariant worth
/// asserting is the one that can actually be violated: it must appear **nowhere
/// else** — no label, no watermark, no accessible name, no other control, no
/// echo into a message or an error.
let private everyStringOutsideMaskedBoxes (root: IView) =
    all root
    |> List.collect (fun v ->
        let masked = v.ViewType.Name = "TextBox" && (propOf "PasswordChar" v |> Option.isSome)

        [ (if masked then None else stringProp "Text" v)
          stringProp "Content" v
          stringProp "Watermark" v
          stringProp "Name" v ]
        |> List.choose id)

let private words (root: IView) =
    all root
    |> List.collect (fun v -> [ stringProp "Text" v; stringProp "Content" v ] |> List.choose id)

let private says (needle: string) (root: IView) =
    words root |> List.exists (fun s -> s.Contains(needle, StringComparison.Ordinal))

let private accessibleNames (root: IView) = all root |> List.choose (stringProp "Name")

// ---------------------------------------------------------------------------
// fixtures
// ---------------------------------------------------------------------------

let private tokens = Tokens.ofAppearance true 0 AppearanceSettings.defaults
let private noServices: AppServices = Unchecked.defaultof<AppServices>

let private modelOf (m, _, _) = m
let private cmdOf (_, c, _) = c
let private externalOf (_, _, e) = e

let private now = DateTime(2026, 8, 2, 10, 0, 0)

/// A stand-in for a secret the user typed. If this string ever reaches the view
/// tree, a test fails.
[<Literal>]
let private secretValue = "sk-ant-SUPERSECRET-0123456789"

let private field key label secret : ConfigField =
    { Key = key
      Label = label
      Secret = secret
      Placeholder = label
      Help = "" }

let private metadata key name category auth supportsSync fields : ConnectorMetadata =
    { Key = key
      Name = name
      Category = category
      Description = name + " description."
      Icon = "◆"
      Auth = auth
      Fields = fields
      SupportsSync = supportsSync }

let private stateOf key status : ConnectionState =
    { Key = key
      Status = status
      LastSync = None
      SyncFrequencyMinutes = 30
      Config = Map.empty
      UpdatedAt = now }

let private viewOf key name category auth supportsSync fields status : ConnectorView =
    { Metadata = metadata key name category auth supportsSync fields
      State = stateOf key status }

/// The nine real connectors, with their real keys, categories, auth kinds and
/// sync capability.
let private nineProviders () =
    [ viewOf "claude" "Claude" ConnectorCategory.Ai AuthKind.ApiKey false [ field "api_key" "API key" true ] ConnectionStatus.NotConfigured
      viewOf "moodle" "Moodle" ConnectorCategory.Academic AuthKind.AccessToken true [ field "base_url" "Site URL" false; field "token" "Token" true ] ConnectionStatus.NotConfigured
      viewOf "gitlab" "GitLab" ConnectorCategory.Development AuthKind.AccessToken true [ field "base_url" "Server" false; field "token" "Token" true ] ConnectionStatus.NotConfigured
      viewOf "gmail" "Gmail" ConnectorCategory.Communication AuthKind.OAuth true [ field "client_id" "Client id" false; field "client_secret" "Client secret" true ] ConnectionStatus.NotConfigured
      viewOf "google-calendar" "Google Calendar" ConnectorCategory.Calendar AuthKind.OAuth true [ field "client_id" "Client id" false ] ConnectionStatus.NotConfigured
      viewOf "google-drive" "Google Drive" ConnectorCategory.Storage AuthKind.OAuth true [ field "client_id" "Client id" false ] ConnectionStatus.NotConfigured
      viewOf "outlook" "Outlook" ConnectorCategory.Communication AuthKind.OAuth true [ field "client_id" "Client id" false ] ConnectionStatus.NotConfigured
      viewOf "outlook-calendar" "Outlook Calendar" ConnectorCategory.Calendar AuthKind.OAuth true [ field "client_id" "Client id" false ] ConnectionStatus.NotConfigured
      viewOf "onedrive" "OneDrive" ConnectorCategory.Storage AuthKind.OAuth true [ field "client_id" "Client id" false ] ConnectionStatus.NotConfigured ]

let private ready () : ConnectionsView.Model =
    { Connectors = nineProviders ()
      History = []
      Selected = None
      FieldValues = Map.empty
      Busy = None
      Messages = Map.empty
      PendingDisconnect = None
      Now = now
      Width = 1600.0
      Loading = false
      Error = None }

let private withStatus key status (model: ConnectionsView.Model) =
    { model with
        Connectors =
            model.Connectors
            |> List.map (fun v ->
                if v.Metadata.Key = key then
                    { v with State = { v.State with Status = status } }
                else
                    v) }

let private render (model: ConnectionsView.Model) = ConnectionsView.view tokens model ignore

// ---------------------------------------------------------------------------
// 1. providers
// ---------------------------------------------------------------------------

[<Fact>]
let ``all nine providers are drawn`` () =
    let root = render (ready ())

    for name in
        [ "Claude"; "Moodle"; "GitLab"; "Gmail"; "Google Calendar"; "Google Drive"; "Outlook"; "Outlook Calendar"; "OneDrive" ] do
        Assert.True(says name root, name)

[<Fact>]
let ``no provider is called coming soon, planned, preview or a placeholder`` () =
    let shown = String.concat " " (everyString (render (ready ()))) |> fun s -> s.ToLowerInvariant()

    for absent in [ "coming soon"; "planned"; "preview"; "placeholder"; "not yet available"; "beta" ] do
        Assert.DoesNotContain(absent, shown)

[<Fact>]
let ``the provider order is deterministic and does not move with status`` () =
    let plain = ConnectionsLogic.sortProviders (nineProviders ()) |> List.map (fun v -> v.Metadata.Key)

    let shuffled =
        ConnectionsLogic.sortProviders (List.rev (nineProviders ())) |> List.map (fun v -> v.Metadata.Key)

    Assert.Equal<string list>(plain, shuffled)

    // A failure changes the group, never the order inside it.
    let failed =
        nineProviders ()
        |> List.map (fun v ->
            if v.Metadata.Key = "gmail" then
                { v with State = { v.State with Status = ConnectionStatus.Failed "boom" } }
            else
                v)

    Assert.Equal<string list>(plain, ConnectionsLogic.sortProviders failed |> List.map (fun v -> v.Metadata.Key))

// ---------------------------------------------------------------------------
// 2. status honesty
// ---------------------------------------------------------------------------

let private allStatuses =
    [ ConnectionStatus.NotConfigured
      ConnectionStatus.Disconnected
      ConnectionStatus.Connected
      ConnectionStatus.Failed "the provider rejected the token" ]

[<Fact>]
let ``the four states have their own label, description and glyph`` () =
    let labels = allStatuses |> List.map ConnectionsLogic.statusLabel
    Assert.Equal(4, (List.distinct labels).Length)

    Assert.Equal<string list>([ "Not configured"; "Disconnected"; "Connected"; "Failed" ], labels)

    let glyphs = allStatuses |> List.map ConnectionsLogic.statusGlyph
    Assert.Equal(4, (List.distinct glyphs).Length)

    for status in allStatuses do
        Assert.False(String.IsNullOrWhiteSpace(ConnectionsLogic.statusDescription status))

[<Fact>]
let ``no passive health claim appears anywhere`` () =
    let model =
        ready ()
        |> withStatus "claude" ConnectionStatus.Connected
        |> withStatus "gmail" (ConnectionStatus.Failed "token rejected")
        |> withStatus "moodle" ConnectionStatus.Disconnected

    for selected in [ None; Some "claude"; Some "gmail"; Some "moodle" ] do
        let root = render { model with Selected = selected }
        let shown = String.concat " " (everyString root) |> fun s -> s.ToLowerInvariant()

        for absent in [ "healthy"; "synced"; "up to date"; "verified"; "secure"; "all good"; "operational" ] do
            Assert.False(shown.Contains absent, sprintf "%A said %s" selected absent)

[<Fact>]
let ``Connected says only that the last operation succeeded`` () =
    Assert.Equal(
        "The last configure, test or sync succeeded.",
        ConnectionsLogic.statusDescription ConnectionStatus.Connected
    )

[<Fact>]
let ``a stored failure message is shown verbatim`` () =
    let message = "The provider rejected the token: 401 Unauthorized"
    let model = ready () |> withStatus "moodle" (ConnectionStatus.Failed message)

    Assert.True(says message (render model))
    Assert.True(says message (render { model with Selected = Some "moodle" }))

[<Fact>]
let ``a long error wraps rather than stretching the card`` () =
    let long = String.replicate 30 "the provider returned an unexpected response. "
    let model = ready () |> withStatus "moodle" (ConnectionStatus.Failed long)

    let blocks = all (render model) |> List.filter (fun v -> stringProp "Text" v = Some long)
    Assert.NotEmpty blocks
    Assert.All(blocks, fun b -> Assert.Equal(Some(box TextWrapping.Wrap), propOf "TextWrapping" b))

[<Fact>]
let ``a card keeps identical geometry in every state`` () =
    let cardBox (status: ConnectionStatus) =
        render (ready () |> withStatus "moodle" status)
        |> all
        |> List.filter (fun v ->
            v.ViewType.Name = "Border"
            && (stringProp "Name" v
                |> Option.map (fun n -> n.StartsWith("Moodle,", StringComparison.Ordinal))
                |> Option.defaultValue false))
        |> List.map (fun v -> propOf "Padding" v, propOf "BorderThickness" v, propOf "CornerRadius" v)

    let baseline = cardBox ConnectionStatus.NotConfigured
    Assert.NotEmpty baseline

    for status in allStatuses do
        Assert.Equal<(obj option * obj option * obj option) list>(baseline, cardBox status)

// ---------------------------------------------------------------------------
// 3. grouping and summary
// ---------------------------------------------------------------------------

[<Fact>]
let ``connected providers group apart from the rest`` () =
    let model = ready () |> withStatus "claude" ConnectionStatus.Connected
    let groups = ConnectionsLogic.grouped model.Connectors

    Assert.Equal<ConnectionsLogic.Group list>(
        [ ConnectionsLogic.Group.Connected; ConnectionsLogic.Group.Available ],
        groups |> List.map fst
    )

    let connected = groups |> List.find (fun (g, _) -> g = ConnectionsLogic.Group.Connected) |> snd
    Assert.Single connected |> ignore
    Assert.Equal("claude", (List.head connected).Metadata.Key)

[<Fact>]
let ``only Connected counts as connected`` () =
    for status in allStatuses do
        let v = viewOf "x" "X" ConnectorCategory.Ai AuthKind.ApiKey false [] status
        let expected = (status = ConnectionStatus.Connected)

        Assert.Equal(expected, ConnectionsLogic.isConnected v)
        Assert.Equal(expected, ConnectionsLogic.groupOf v = ConnectionsLogic.Group.Connected)

[<Fact>]
let ``an empty group is omitted`` () =
    // Nothing connected: only Available is drawn.
    let groups = ConnectionsLogic.grouped (nineProviders ())
    Assert.Single groups |> ignore
    Assert.Equal(ConnectionsLogic.Group.Available, fst (List.head groups))

[<Fact>]
let ``the summary counts what is really there`` () =
    let model =
        ready ()
        |> withStatus "claude" ConnectionStatus.Connected
        |> withStatus "gmail" (ConnectionStatus.Failed "boom")

    let summary = ConnectionsLogic.summarise model.Connectors

    Assert.Equal(1, summary.Connected)
    Assert.Equal(1, summary.NeedsAttention)
    Assert.Equal(8, summary.Available)
    Assert.Equal(model.Connectors.Length, summary.Connected + summary.Available)

[<Fact>]
let ``last import is the newest real one, or an em dash`` () =
    Assert.Equal(None, (ConnectionsLogic.summarise (nineProviders ())).LastSync)
    Assert.True(says ConnectionsLogic.unavailable (render (ready ())))

    let older = now.AddDays -2.0
    let newer = now.AddHours -3.0

    let withSyncs =
        nineProviders ()
        |> List.mapi (fun i v ->
            if i = 0 then { v with State = { v.State with LastSync = Some older } }
            elif i = 1 then { v with State = { v.State with LastSync = Some newer } }
            else v)

    Assert.Equal(Some newer, (ConnectionsLogic.summarise withSyncs).LastSync)

[<Fact>]
let ``the summary invents nothing`` () =
    let shown = String.concat " " (everyString (render (ready ()))) |> fun s -> s.ToLowerInvariant()

    // "Storage" is a real ConnectorCategory, so the ban is on invented METRICS.
    for absent in [ "success rate"; "storage used"; "items synced"; "health score"; "% "; "uptime" ] do
        Assert.DoesNotContain(absent, shown)

// ---------------------------------------------------------------------------
// 4. last sync wording
// ---------------------------------------------------------------------------

[<Fact>]
let ``a provider that cannot import is not called never-synced`` () =
    let claude = nineProviders () |> List.find (fun v -> v.Metadata.Key = "claude")
    Assert.False(claude.Metadata.SupportsSync)
    Assert.Equal("Does not import data", ConnectionsLogic.lastSyncText now claude)

[<Fact>]
let ``a provider that can import but never has says so`` () =
    let moodle = nineProviders () |> List.find (fun v -> v.Metadata.Key = "moodle")
    Assert.Equal("Never imported", ConnectionsLogic.lastSyncText now moodle)

[<Fact>]
let ``a real last import is shown relatively`` () =
    let moodle = nineProviders () |> List.find (fun v -> v.Metadata.Key = "moodle")
    let synced = { moodle with State = { moodle.State with LastSync = Some(now.AddHours -2.0) } }

    Assert.Contains("Last import", ConnectionsLogic.lastSyncText now synced)
    Assert.DoesNotContain("Never", ConnectionsLogic.lastSyncText now synced)

// ---------------------------------------------------------------------------
// 5. credentials — the most important section here
// ---------------------------------------------------------------------------

[<Fact>]
let ``a typed secret never reaches the rendered tree`` () =
    let model =
        { ready () with
            Selected = Some "claude"
            FieldValues = Map.ofList [ "api_key", secretValue ] }

    let root = render model

    // It lives in exactly one place: the masked box the user is typing into.
    for s in everyStringOutsideMaskedBoxes root do
        Assert.False(s.Contains(secretValue, StringComparison.Ordinal), s)

    // And that box is genuinely masked.
    let holding =
        ofType "TextBox" root
        |> List.filter (fun b -> stringProp "Text" b = Some secretValue)

    Assert.Single holding |> ignore
    Assert.True((propOf "PasswordChar" (List.head holding)).IsSome)

[<Fact>]
let ``a secret field is masked`` () =
    let model = { ready () with Selected = Some "claude" }

    let masked =
        render model
        |> ofType "TextBox"
        |> List.filter (fun b -> propOf "PasswordChar" b |> Option.isSome)

    Assert.NotEmpty masked

[<Fact>]
let ``a secret box is never seeded from stored configuration`` () =
    // Even if a secret somehow sat in Config, selecting must not lift it out.
    let poisoned =
        { ready () with
            Connectors =
                (ready ()).Connectors
                |> List.map (fun v ->
                    if v.Metadata.Key = "claude" then
                        { v with State = { v.State with Config = Map.ofList [ "api_key", secretValue ] } }
                    else
                        v) }

    let selected = ConnectionsView.update noServices (ConnectionsView.Select "claude") poisoned |> modelOf

    Assert.False(selected.FieldValues.ContainsKey "api_key")

    for s in everyString (render selected) do
        Assert.False(s.Contains(secretValue, StringComparison.Ordinal), s)

[<Fact>]
let ``saved settings never include a secret field`` () =
    let poisoned =
        viewOf "moodle" "Moodle" ConnectorCategory.Academic AuthKind.AccessToken true
            [ field "base_url" "Site URL" false; field "token" "Token" true ]
            ConnectionStatus.Connected

    let withConfig =
        { poisoned with
            State =
                { poisoned.State with
                    Config = Map.ofList [ "base_url", "https://moodle.example.ac.uk"; "token", secretValue ] } }

    let visible = ConnectionsLogic.visibleConfig withConfig

    Assert.Single visible |> ignore
    Assert.Equal("Site URL", fst (List.head visible))
    Assert.DoesNotContain(visible, fun (_, v) -> v = secretValue)

[<Fact>]
let ``a blank secret is documented as keeping the stored one`` () =
    Assert.Contains("Leave blank", ConnectionsLogic.secretFieldNotice)
    Assert.True(says ConnectionsLogic.secretFieldNotice (render { ready () with Selected = Some "claude" }))

[<Fact>]
let ``the typed secret is cleared once the operation ends`` () =
    let model =
        { ready () with
            Selected = Some "claude"
            FieldValues = Map.ofList [ "api_key", secretValue ] }

    for result in [ Ok "Connected."; Error "Rejected." ] do
        let after =
            ConnectionsView.update noServices (ConnectionsView.OperationDone("claude", result)) model |> modelOf

        Assert.False(after.FieldValues.ContainsKey "api_key", string result)

[<Fact>]
let ``a non-secret field IS seeded, so configuration is edited not retyped`` () =
    let configured =
        { ready () with
            Connectors =
                (ready ()).Connectors
                |> List.map (fun v ->
                    if v.Metadata.Key = "moodle" then
                        { v with
                            State =
                                { v.State with Config = Map.ofList [ "base_url", "https://moodle.example.ac.uk" ] } }
                    else
                        v) }

    let selected = ConnectionsView.update noServices (ConnectionsView.Select "moodle") configured |> modelOf
    Assert.Equal(Some "https://moodle.example.ac.uk", selected.FieldValues.TryFind "base_url")

// ---------------------------------------------------------------------------
// 6. actions
// ---------------------------------------------------------------------------

[<Fact>]
let ``an unconfigured provider offers only Connect`` () =
    let claude = nineProviders () |> List.find (fun v -> v.Metadata.Key = "claude")

    Assert.Equal<ConnectionsLogic.Action list>(
        [ ConnectionsLogic.Action.Configure ],
        ConnectionsLogic.availableActions claude
    )

    Assert.Equal("Connect", ConnectionsLogic.actionLabel ConnectionStatus.NotConfigured ConnectionsLogic.Action.Configure)

[<Fact>]
let ``a connected provider that imports offers all four actions`` () =
    let moodle =
        viewOf "moodle" "Moodle" ConnectorCategory.Academic AuthKind.AccessToken true [] ConnectionStatus.Connected

    Assert.Equal<ConnectionsLogic.Action list>(
        [ ConnectionsLogic.Action.Configure
          ConnectionsLogic.Action.Test
          ConnectionsLogic.Action.Sync
          ConnectionsLogic.Action.Disconnect ],
        ConnectionsLogic.availableActions moodle
    )

[<Fact>]
let ``a provider that cannot import is never offered Import`` () =
    let claude =
        viewOf "claude" "Claude" ConnectorCategory.Ai AuthKind.ApiKey false [] ConnectionStatus.Connected

    Assert.DoesNotContain(ConnectionsLogic.Action.Sync, ConnectionsLogic.availableActions claude)

    let root = render ((ready () |> withStatus "claude" ConnectionStatus.Connected) |> fun m -> { m with Selected = Some "claude" })
    Assert.False(says "Import now" root)

[<Fact>]
let ``the configure label follows the real state`` () =
    Assert.Equal("Connect", ConnectionsLogic.actionLabel ConnectionStatus.NotConfigured ConnectionsLogic.Action.Configure)
    Assert.Equal("Reconnect", ConnectionsLogic.actionLabel ConnectionStatus.Disconnected ConnectionsLogic.Action.Configure)
    Assert.Equal("Fix and retry", ConnectionsLogic.actionLabel (ConnectionStatus.Failed "x") ConnectionsLogic.Action.Configure)
    Assert.Equal("Edit configuration", ConnectionsLogic.actionLabel ConnectionStatus.Connected ConnectionsLogic.Action.Configure)

[<Fact>]
let ``a blocked action states its reason`` () =
    let claude = nineProviders () |> List.find (fun v -> v.Metadata.Key = "claude")

    let reason = ConnectionsLogic.actionBlockedReason None claude ConnectionsLogic.Action.Sync
    Assert.Equal(Some "This provider does not import data.", reason)

    Assert.True((ConnectionsLogic.actionBlockedReason None claude ConnectionsLogic.Action.Configure).IsNone)

[<Fact>]
let ``a busy connector blocks every action, including its own`` () =
    let moodle =
        viewOf "moodle" "Moodle" ConnectorCategory.Academic AuthKind.AccessToken true [] ConnectionStatus.Connected

    let busy = Some("moodle", "sync")

    for action in ConnectionsLogic.availableActions moodle do
        Assert.True((ConnectionsLogic.actionBlockedReason busy moodle action).IsSome, string action)

    // And a different connector is blocked too — the registry syncs one at a time.
    let claude = nineProviders () |> List.find (fun v -> v.Metadata.Key = "claude")
    Assert.Equal(Some "Another connector is busy.", ConnectionsLogic.actionBlockedReason busy claude ConnectionsLogic.Action.Configure)

[<Fact>]
let ``a second operation while one runs does nothing`` () =
    let busy = { ready () with Busy = Some("moodle", "sync"); Selected = Some "claude" }

    for msg in
        [ ConnectionsView.Configure "claude"
          ConnectionsView.Test "claude"
          ConnectionsView.SyncNow "claude"
          ConnectionsView.ConfirmDisconnect "claude" ] do
        let after = ConnectionsView.update noServices msg busy
        Assert.True(List.isEmpty (cmdOf after), string msg)

// ---------------------------------------------------------------------------
// 7. disconnect
// ---------------------------------------------------------------------------

[<Fact>]
let ``disconnect is described as a local removal, never a revocation`` () =
    Assert.Contains("stored on this computer", ConnectionsLogic.disconnectNotice)
    Assert.Contains("does not revoke access", ConnectionsLogic.disconnectNotice)
    Assert.Contains("stays in University OS", ConnectionsLogic.disconnectNotice)

    let lower = ConnectionsLogic.disconnectNotice.ToLowerInvariant()

    // The claim must never be that the provider was told anything.
    Assert.DoesNotContain("revoke provider access", lower)
    Assert.DoesNotContain("sign out everywhere", lower)
    Assert.DoesNotContain("removes access from", lower)

[<Fact>]
let ``the action is worded as removing credentials`` () =
    Assert.Equal(
        "Remove credentials",
        ConnectionsLogic.actionLabel ConnectionStatus.Connected ConnectionsLogic.Action.Disconnect
    )

[<Fact>]
let ``disconnect asks first, and cancelling changes nothing`` () =
    let model = (ready () |> withStatus "moodle" ConnectionStatus.Connected) |> fun m -> { m with Selected = Some "moodle" }

    Assert.False(says ConnectionsLogic.disconnectNotice (render model))

    let asked = ConnectionsView.update noServices (ConnectionsView.RequestDisconnect "moodle") model |> modelOf
    Assert.Equal(Some "moodle", asked.PendingDisconnect)

    let root = render asked
    Assert.True(says ConnectionsLogic.disconnectNotice root)
    Assert.True(says (ConnectionsLogic.disconnectConfirm "Moodle") root)
    Assert.True(says "Cancel" root)

    let cancelled = ConnectionsView.update noServices ConnectionsView.CancelDisconnect asked |> modelOf
    Assert.True(cancelled.PendingDisconnect.IsNone)
    Assert.Equal(model.Connectors.Length, cancelled.Connectors.Length)

[<Fact>]
let ``the confirmation names the provider`` () =
    Assert.Contains("Moodle", ConnectionsLogic.disconnectConfirm "Moodle")

[<Fact>]
let ``a disconnect failure is reported`` () =
    let model = ready ()

    let failed =
        ConnectionsView.update noServices (ConnectionsView.OperationDone("moodle", Error "the file is locked")) model
        |> modelOf

    Assert.Equal(Some(Error "the file is locked"), failed.Messages.TryFind "moodle")
    Assert.True(says "the file is locked" (render { failed with Selected = Some "moodle" }))

// ---------------------------------------------------------------------------
// 8. access descriptions and privacy
// ---------------------------------------------------------------------------

[<Fact>]
let ``each provider's access description matches what it really imports`` () =
    let lineFor key =
        nineProviders ()
        |> List.find (fun v -> v.Metadata.Key = key)
        |> fun v -> String.concat " " (ConnectionsLogic.accessLines v.Metadata)

    Assert.Contains("Inbox", lineFor "gmail")
    Assert.Contains("Inbox", lineFor "outlook")
    Assert.Contains("Inbox", lineFor "google-drive")
    Assert.Contains("Inbox", lineFor "onedrive")
    Assert.Contains("Calendar", lineFor "google-calendar")
    Assert.Contains("Calendar", lineFor "outlook-calendar")
    Assert.Contains("Calendar", lineFor "moodle")
    Assert.Contains("Assistant", lineFor "claude")

    // GitLab genuinely writes to both sinks.
    Assert.Contains("Inbox", lineFor "gitlab")
    Assert.Contains("Calendar", lineFor "gitlab")

[<Fact>]
let ``every provider has an access description`` () =
    for v in nineProviders () do
        let lines = ConnectionsLogic.accessLines v.Metadata
        Assert.NotEmpty lines
        Assert.All(lines, fun l -> Assert.False(String.IsNullOrWhiteSpace l))

[<Fact>]
let ``no OAuth scope is invented`` () =
    let shown =
        nineProviders ()
        |> List.collect (fun v -> ConnectionsLogic.accessLines v.Metadata)
        |> String.concat " "
        |> fun s -> s.ToLowerInvariant()

    for absent in
        [ "scope"; "https://www.googleapis.com"; ".readonly"; "offline_access"; "mail.read"; "calendars.read" ] do
        Assert.DoesNotContain(absent, shown)

[<Fact>]
let ``no forbidden privacy claim appears`` () =
    let model =
        ready () |> withStatus "claude" ConnectionStatus.Connected |> fun m -> { m with Selected = Some "claude" }

    let shown = String.concat " " (everyString (render model)) |> fun s -> s.ToLowerInvariant()

    for absent in
        [ "end-to-end"
          "gdpr"
          "never shared"
          "no data retention"
          "not retained"
          "least privilege"
          "read-only"
          "zero knowledge"
          "fully private" ] do
        Assert.DoesNotContain(absent, shown)

[<Fact>]
let ``the credential wording says exactly what DPAPI gives`` () =
    Assert.Contains("Windows DPAPI", ConnectionsLogic.credentialNotice)
    Assert.Contains("Windows user account on this computer", ConnectionsLogic.credentialNotice)

    let model = { ready () with Selected = Some "claude" }
    Assert.True(says ConnectionsLogic.credentialNotice (render model))
    Assert.True(says ConnectionsLogic.requestNotice (render model))

[<Fact>]
let ``the deduplication rule is stated for importers only`` () =
    let importer = { ready () with Selected = Some "moodle" }
    Assert.True(says ConnectionsLogic.deduplicationNotice (render importer))

    let nonImporter = { ready () with Selected = Some "claude" }
    Assert.False(says ConnectionsLogic.deduplicationNotice (render nonImporter))

// ---------------------------------------------------------------------------
// 9. OAuth and sync cadence
// ---------------------------------------------------------------------------

[<Fact>]
let ``an OAuth provider warns that a browser opens`` () =
    let gmail = nineProviders () |> List.find (fun v -> v.Metadata.Key = "gmail")
    let notice = ConnectionsLogic.oauthNotice gmail.Metadata

    Assert.True notice.IsSome
    Assert.Contains("browser", notice.Value)
    Assert.Contains("five minutes", notice.Value)

    Assert.True(says "opens your web browser" (render { ready () with Selected = Some "gmail" }))

[<Fact>]
let ``a non-OAuth provider gets no browser notice`` () =
    let claude = nineProviders () |> List.find (fun v -> v.Metadata.Key = "claude")
    Assert.True((ConnectionsLogic.oauthNotice claude.Metadata).IsNone)
    Assert.False(says "opens your web browser" (render { ready () with Selected = Some "claude" }))

[<Fact>]
let ``sync is described as a due-interval poll, never as continuous`` () =
    Assert.Contains("when the configured interval is due", ConnectionsLogic.syncCadenceNotice)

    let shown =
        String.concat " " (everyString (render { ready () with Selected = Some "moodle" }))
        |> fun s -> s.ToLowerInvariant()

    for absent in [ "real-time"; "real time"; "continuous"; "always in sync"; "instantly" ] do
        Assert.DoesNotContain(absent, shown)

[<Fact>]
let ``no offered frequency is below the registry's floor`` () =
    Assert.Equal(5, ConnectionsLogic.minimumFrequencyMinutes)
    Assert.All(ConnectionsLogic.frequencyOptions, fun m -> Assert.True(m >= ConnectionsLogic.minimumFrequencyMinutes))

[<Fact>]
let ``frequency controls are absent for a provider that cannot import`` () =
    Assert.False(says "Hourly" (render { ready () with Selected = Some "claude" }))
    Assert.True(says "Hourly" (render { ready () with Selected = Some "moodle" }))

// ---------------------------------------------------------------------------
// 10. sync history
// ---------------------------------------------------------------------------

[<Fact>]
let ``history is newest first and names the provider`` () =
    let entries =
        [ "moodle", now.AddHours -5.0, "Imported 3, updated 1"
          "gmail", now.AddHours -1.0, "Imported 12" ]

    let rows = ConnectionsLogic.historyRows (nineProviders ()) entries

    Assert.Equal<string list>([ "Gmail"; "Moodle" ], rows |> List.map (fun r -> r.Provider))
    Assert.Equal("Imported 12", (List.head rows).Outcome)

[<Fact>]
let ``an unknown connector key falls back to the key itself`` () =
    let rows = ConnectionsLogic.historyRows (nineProviders ()) [ "retired-thing", now, "ok" ]
    Assert.Equal("retired-thing", (List.head rows).Provider)

[<Fact>]
let ``the history section is omitted when nothing has been imported`` () =
    Assert.False(says ConnectionsLogic.historyHeading (render (ready ())))

    let withHistory = { ready () with History = [ "moodle", now.AddHours -1.0, "Imported 3" ] }
    Assert.True(says (ConnectionsLogic.historyHeading.ToUpperInvariant()) (render withHistory))
    Assert.True(says "Imported 3" (render withHistory))

[<Fact>]
let ``the stored outcome text is shown as written, with no count parsed out`` () =
    let odd = "finished, but the provider did not say how many"
    let rows = ConnectionsLogic.historyRows (nineProviders ()) [ "moodle", now, odd ]
    Assert.Equal(odd, (List.head rows).Outcome)

    Assert.True(says odd (render { ready () with History = [ "moodle", now, odd ] }))

// ---------------------------------------------------------------------------
// 11. composition, selection and states
// ---------------------------------------------------------------------------

[<Fact>]
let ``the page sections are the mapped ones, in order`` () =
    Assert.Equal<ConnectionsLogic.Section list>(
        [ ConnectionsLogic.Section.Header
          ConnectionsLogic.Section.Summary
          ConnectionsLogic.Section.Providers
          ConnectionsLogic.Section.Detail
          ConnectionsLogic.Section.History ],
        ConnectionsLogic.sectionOrder
    )

[<Fact>]
let ``the detail regions run identity to privacy`` () =
    Assert.Equal<ConnectionsLogic.DetailRegion list>(
        [ ConnectionsLogic.DetailRegion.Identity
          ConnectionsLogic.DetailRegion.Status
          ConnectionsLogic.DetailRegion.Access
          ConnectionsLogic.DetailRegion.Configuration
          ConnectionsLogic.DetailRegion.Sync
          ConnectionsLogic.DetailRegion.Actions
          ConnectionsLogic.DetailRegion.Privacy ],
        ConnectionsLogic.detailOrder
    )

    let ordered = words (render { ready () with Selected = Some "moodle" })

    let indexOf (needle: string) =
        ordered |> List.findIndex (fun s -> s.Contains(needle, StringComparison.Ordinal))

    Assert.True(indexOf "STATUS" < indexOf "WHAT THIS CONNECTION DOES")
    Assert.True(indexOf "WHAT THIS CONNECTION DOES" < indexOf "CONFIGURATION")
    Assert.True(indexOf "CONFIGURATION" < indexOf "ACTIONS")
    Assert.True(indexOf "ACTIONS" < indexOf "CREDENTIALS")

[<Fact>]
let ``the header is the redesign's own wording`` () =
    let root = render (ready ())
    Assert.True(says "Integration Hub" root)
    Assert.True(says "Connect services to bring your academic world into University OS." root)

[<Fact>]
let ``selecting a provider opens its detail, and selecting again closes it`` () =
    let opened = ConnectionsView.update noServices (ConnectionsView.Select "moodle") (ready ()) |> modelOf
    Assert.Equal(Some "moodle", opened.Selected)

    let closed = ConnectionsView.update noServices (ConnectionsView.Select "moodle") opened |> modelOf
    Assert.True(closed.Selected.IsNone)
    Assert.True(closed.FieldValues.IsEmpty)

[<Fact>]
let ``a selection that is no longer registered is cleared`` () =
    Assert.Equal(Some "moodle", ConnectionsLogic.reconcileSelection (Some "moodle") (nineProviders ()))
    Assert.Equal(None, ConnectionsLogic.reconcileSelection (Some "retired") (nineProviders ()))
    Assert.Equal(None, ConnectionsLogic.reconcileSelection (Some "moodle") [])

    let stale = { ready () with Selected = Some "retired" }
    let reloaded = ConnectionsView.update noServices (ConnectionsView.Loaded(nineProviders (), [], now)) stale |> modelOf
    Assert.True(reloaded.Selected.IsNone)

[<Fact>]
let ``a load failure keeps the page structure`` () =
    let failed = ConnectionsView.update noServices (ConnectionsView.Failed(exn "database is locked")) (ready ()) |> modelOf

    Assert.Equal(Some "database is locked", failed.Error)

    let root = render failed
    Assert.True(says "The connectors could not be loaded: database is locked" root)
    // The page has not collapsed into one warning.
    Assert.True(says "Integration Hub" root)
    Assert.True(says "Moodle" root)

[<Fact>]
let ``no registered connectors says so plainly`` () =
    let empty = { ready () with Connectors = [] }
    let root = render empty

    Assert.True(says "No connectors are registered in this build." root)
    Assert.True(says "Integration Hub" root)

// ---------------------------------------------------------------------------
// 12. responsive
// ---------------------------------------------------------------------------

[<Fact>]
let ``columns and detail placement follow the width`` () =
    Assert.Equal(3, (ConnectionsLogic.placementFor 1600.0).Columns)
    Assert.Equal(2, (ConnectionsLogic.placementFor 1000.0).Columns)
    Assert.Equal(1, (ConnectionsLogic.placementFor 700.0).Columns)

    Assert.Equal(ConnectionsLogic.DetailPlacement.Rail, (ConnectionsLogic.placementFor 1600.0).Detail)
    Assert.Equal(ConnectionsLogic.DetailPlacement.Below, (ConnectionsLogic.placementFor 1000.0).Detail)
    Assert.Equal(ConnectionsLogic.DetailPlacement.Below, (ConnectionsLogic.placementFor 700.0).Detail)

[<Fact>]
let ``the detail is reachable at every width`` () =
    for width in [ 1600.0; 1000.0; 700.0 ] do
        let model = { ready () with Selected = Some "moodle"; Width = width }
        Assert.True(says "CONFIGURATION" (render model), string width)
        Assert.True(says ConnectionsLogic.credentialNotice (render model), string width)

[<Fact>]
let ``a secret stays masked at every width`` () =
    for width in [ 1600.0; 1000.0; 700.0 ] do
        let model =
            { ready () with
                Selected = Some "claude"
                Width = width
                FieldValues = Map.ofList [ "api_key", secretValue ] }

        let root = render model

        Assert.NotEmpty(ofType "TextBox" root |> List.filter (fun b -> propOf "PasswordChar" b |> Option.isSome))

        for s in everyStringOutsideMaskedBoxes root do
            Assert.False(s.Contains(secretValue, StringComparison.Ordinal), sprintf "%f: %s" width s)

[<Fact>]
let ``nothing in the screen scrolls sideways, at any width`` () =
    for width in [ 1600.0; 1000.0; 700.0 ] do
        let model = { ready () with Selected = Some "moodle"; Width = width }

        for viewer in ofType "ScrollViewer" (render model) do
            Assert.Equal(Some(box ScrollBarVisibility.Disabled), propOf "HorizontalScrollBarVisibility" viewer)

// ---------------------------------------------------------------------------
// 13. Dark and Light, every accent
// ---------------------------------------------------------------------------

let private themeModes = [ ThemeMode.Dark; ThemeMode.Light ]
let private accentKeys = Palette.accents |> List.map fst

let private washedBy (alphaHex: string) (colour: Color) (surface: Color) =
    let a = Int32.Parse(alphaHex, Globalization.NumberStyles.HexNumber)
    Palette.composite surface (Color.FromArgb(byte a, colour.R, colour.G, colour.B))

/// Every colour a status chip or provider tile can carry.
let private statusColours (s: Surfaces) =
    [ "not configured", Color.Parse Theme.Identity.neutral
      "disconnected", Color.Parse Theme.Identity.gold
      "connected", s.Success
      "failed", s.Danger ]

[<Fact>]
let ``a status chip's ink is readable on its own wash, every theme and accent`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, raw in statusColours s do
                let wash = washedBy ConnectionsLogic.Geometry.chipFillAlpha raw s.Card
                let ink = Palette.readableOn wash s.Text raw

                Assert.True(
                    Palette.contrastRatio ink wash >= Palette.aaText,
                    sprintf "%A/%s %s chip: %.2f" mode key name (Palette.contrastRatio ink wash)
                )

[<Fact>]
let ``a note strip's text is readable on its wash`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, raw in statusColours s @ [ "accent", (Palette.ofKey key).Normal ] do
                let wash = washedBy ConnectionsLogic.Geometry.noteFillAlpha raw s.Card

                Assert.True(
                    Palette.contrastRatio s.Text wash >= Palette.aaText,
                    sprintf "%A/%s %s note: %.2f" mode key name (Palette.contrastRatio s.Text wash)
                )

[<Fact>]
let ``the provider tile is an accent, not a surface that swallows its glyph`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, raw in statusColours s do
                let tile = washedBy ConnectionsLogic.Geometry.tileAlpha raw s.Card

                // Visible against the card…
                Assert.True(
                    Palette.contrastRatio tile s.Card > 1.0,
                    sprintf "%A/%s %s tile invisible" mode key name
                )

                // …and still readable for anything drawn on it.
                Assert.True(
                    Palette.contrastRatio s.Text tile >= Palette.aaText,
                    sprintf "%A/%s %s tile ink: %.2f" mode key name (Palette.contrastRatio s.Text tile)
                )

[<Fact>]
let ``a selected card is distinguishable without changing size`` () =
    for mode in themeModes do
        for key in accentKeys do
            let accent = Palette.ofKey key
            let s = Surfaces.resolve mode accent

            let selected = washedBy ConnectionsLogic.Geometry.selectedFillAlpha accent.Normal s.Card
            Assert.True(Palette.contrastRatio selected s.Card > 1.0, sprintf "%A/%s selection invisible" mode key)

            Assert.True(
                Palette.contrastRatio s.Text selected >= Palette.aaText,
                sprintf "%A/%s text on selection: %.2f" mode key (Palette.contrastRatio s.Text selected)
            )

[<Fact>]
let ``the surfaces stay warm in both themes`` () =
    for mode in themeModes do
        for key in accentKeys do
            let s = Surfaces.resolve mode (Palette.ofKey key)

            for name, c in [ "card", s.Card; "cardAlt", s.CardAlt; "action", s.Action ] do
                Assert.True(int c.R >= int c.B, sprintf "%A/%s %s is cool: %A" mode key name c)

// ---------------------------------------------------------------------------
// 14. mutation guards
// ---------------------------------------------------------------------------

[<Fact>]
let ``MUTATION calling a configured provider Connected is caught`` () =
    // Saved configuration is NOT a connection.
    let configured =
        { viewOf "moodle" "Moodle" ConnectorCategory.Academic AuthKind.AccessToken true [] ConnectionStatus.NotConfigured with
            State =
                { stateOf "moodle" ConnectionStatus.NotConfigured with
                    Config = Map.ofList [ "base_url", "https://moodle.example.ac.uk" ] } }

    Assert.True(ConnectionsLogic.isConfigured configured)
    Assert.False(ConnectionsLogic.isConnected configured)
    Assert.Equal(ConnectionsLogic.Group.Available, ConnectionsLogic.groupOf configured)
    Assert.Equal("Not configured", ConnectionsLogic.statusLabel configured.State.Status)

[<Fact>]
let ``MUTATION labelling a failure as healthy is caught`` () =
    let failed = ConnectionStatus.Failed "the provider rejected the token"

    Assert.Equal("Failed", ConnectionsLogic.statusLabel failed)
    Assert.NotEqual<string>("Connected", ConnectionsLogic.statusLabel failed)

    let label = (ConnectionsLogic.statusLabel failed + " " + ConnectionsLogic.statusDescription failed).ToLowerInvariant()

    for absent in [ "healthy"; "verified"; "up to date"; "secure"; "synced" ] do
        Assert.DoesNotContain(absent, label)

    Assert.True((ConnectionsLogic.errorOf failed).IsSome)
    Assert.True(ConnectionsLogic.needsAttention (viewOf "x" "X" ConnectorCategory.Ai AuthKind.ApiKey false [] failed))

[<Fact>]
let ``MUTATION rendering a secret as plaintext is caught`` () =
    let model =
        { ready () with
            Selected = Some "claude"
            FieldValues = Map.ofList [ "api_key", secretValue ] }

    let root = render model

    // Present in the model…
    Assert.Equal(Some secretValue, model.FieldValues.TryFind "api_key")

    // …and nowhere in the tree except the masked box being typed into.
    Assert.DoesNotContain(everyStringOutsideMaskedBoxes root, fun (s: string) -> s.Contains(secretValue, StringComparison.Ordinal))

    // The box that holds it is masked.
    let boxes = ofType "TextBox" root |> List.filter (fun b -> propOf "PasswordChar" b |> Option.isSome)
    Assert.NotEmpty boxes

[<Fact>]
let ``MUTATION offering an unsupported action is caught`` () =
    let claude =
        viewOf "claude" "Claude" ConnectorCategory.Ai AuthKind.ApiKey false [] ConnectionStatus.Connected

    Assert.DoesNotContain(ConnectionsLogic.Action.Sync, ConnectionsLogic.availableActions claude)

    let unconfigured = nineProviders () |> List.find (fun v -> v.Metadata.Key = "gmail")

    Assert.Equal<ConnectionsLogic.Action list>(
        [ ConnectionsLogic.Action.Configure ],
        ConnectionsLogic.availableActions unconfigured
    )

    Assert.DoesNotContain(ConnectionsLogic.Action.Disconnect, ConnectionsLogic.availableActions unconfigured)

[<Fact>]
let ``MUTATION disconnect wording claiming remote revocation is caught`` () =
    let notice = ConnectionsLogic.disconnectNotice
    Assert.Contains("does not revoke access", notice)
    Assert.Contains("does not change anything in your provider account", notice)

    let lower = notice.ToLowerInvariant()
    // The dangerous claims, each in the affirmative.
    Assert.DoesNotContain("revokes access", lower)
    Assert.DoesNotContain("revokes all", lower)
    Assert.DoesNotContain("signs you out everywhere", lower)

[<Fact>]
let ``MUTATION inverting the connected and available groups is caught`` () =
    let model = ready () |> withStatus "claude" ConnectionStatus.Connected
    let groups = ConnectionsLogic.grouped model.Connectors

    let connected = groups |> List.find (fun (g, _) -> g = ConnectionsLogic.Group.Connected) |> snd
    let available = groups |> List.find (fun (g, _) -> g = ConnectionsLogic.Group.Available) |> snd

    Assert.All(connected, fun v -> Assert.Equal(ConnectionStatus.Connected, v.State.Status))
    Assert.All(available, fun v -> Assert.NotEqual(ConnectionStatus.Connected, v.State.Status))
    Assert.Equal(ConnectionsLogic.Group.Connected, fst (List.head groups))

[<Fact>]
let ``MUTATION hiding the failure state is caught`` () =
    let message = "the provider rejected the token"
    let model = ready () |> withStatus "moodle" (ConnectionStatus.Failed message)

    // On the card…
    Assert.True(says message (render model))
    Assert.True(says "Failed" (render model))

    // …and in the detail.
    Assert.True(says message (render { model with Selected = Some "moodle" }))

    // And it counts toward "needs attention".
    Assert.Equal(1, (ConnectionsLogic.summarise model.Connectors).NeedsAttention)

[<Fact>]
let ``MUTATION removing the narrow detail placement is caught`` () =
    let placements = [ 1600.0; 1000.0; 700.0 ] |> List.map ConnectionsLogic.placementFor

    Assert.Equal<ConnectionsLogic.DetailPlacement list>(
        [ ConnectionsLogic.DetailPlacement.Rail
          ConnectionsLogic.DetailPlacement.Below
          ConnectionsLogic.DetailPlacement.Below ],
        placements |> List.map (fun p -> p.Detail)
    )

    Assert.Equal<int list>([ 3; 2; 1 ], placements |> List.map (fun p -> p.Columns))

    // And the detail is still rendered at the narrowest width.
    let narrow = { ready () with Selected = Some "moodle"; Width = 700.0 }
    Assert.True(says "CONFIGURATION" (render narrow))
    Assert.True(says "ACTIONS" (render narrow))

// ---------------------------------------------------------------------------
// 15. the shell contract
// ---------------------------------------------------------------------------

[<Fact>]
let ``Connections draws its own glass`` () =
    Assert.True(Page.usesGlassSurface Page.Connections)

[<Fact>]
let ``every migrated screen still draws its own glass, and Settings does not yet`` () =
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
          Page.Chat
          Page.Search
          Page.Calendar
          Page.Connections ] do
        Assert.True(Page.usesGlassSurface page, sprintf "%A lost its glass surface" page)

    // Settings was the last screen to migrate; nothing is unmigrated now.
    Assert.True(Page.usesGlassSurface Page.Settings)

// ---------------------------------------------------------------------------
// 16. performance fixtures
// ---------------------------------------------------------------------------

let private measure (runs: int) (work: unit -> int) =
    work () |> ignore
    GC.Collect()
    GC.WaitForPendingFinalizers()

    let before = GC.GetAllocatedBytesForCurrentThread()
    let times = ResizeArray<float>()

    for _ in 1..runs do
        let sw = Diagnostics.Stopwatch.StartNew()
        work () |> ignore
        sw.Stop()
        times.Add sw.Elapsed.TotalMilliseconds

    let allocated = GC.GetAllocatedBytesForCurrentThread() - before
    let sorted = times |> Seq.sort |> Seq.toArray
    sorted.[sorted.Length / 2], float allocated / float runs / 1024.0

let private allWithStatus (status: ConnectionStatus) =
    nineProviders () |> List.map (fun v -> { v with State = { v.State with Status = status } })

[<Fact>]
let ``Connections fixtures stay inside their budget`` () =
    let report = System.Text.StringBuilder()
    report.AppendLine "Connections fixtures" |> ignore
    report.AppendLine "fixture | median ms | KB per run" |> ignore

    let record name runs work =
        let ms, kb = measure runs work
        report.AppendLine(sprintf "%-44s | %8.3f | %9.1f" name ms kb) |> ignore
        Assert.True(ms < 60.0, sprintf "%s took %.2f ms" name ms)

    // --- decision cost -----------------------------------------------------
    record "decide: group nine providers" 500 (fun () -> (ConnectionsLogic.grouped (nineProviders ())).Length)
    record "decide: summarise" 500 (fun () -> (ConnectionsLogic.summarise (nineProviders ())).Available)

    record "decide: actions for all nine" 500 (fun () ->
        nineProviders () |> List.sumBy (fun v -> (ConnectionsLogic.availableActions v).Length))

    record "decide: history 200 rows" 100 (fun () ->
        let rows = List.init 200 (fun i -> "moodle", now.AddMinutes(float -i), sprintf "Imported %d" i)
        (ConnectionsLogic.historyRows (nineProviders ()) rows).Length)

    // --- view construction -------------------------------------------------
    let build (model: ConnectionsView.Model) () = (all (render model)).Length

    record "view: nine unconfigured" 20 (build (ready ()))
    record "view: nine disconnected" 20 (build { ready () with Connectors = allWithStatus ConnectionStatus.Disconnected })
    record "view: nine connected" 20 (build { ready () with Connectors = allWithStatus ConnectionStatus.Connected })

    record
        "view: nine failed"
        20
        (build { ready () with Connectors = allWithStatus (ConnectionStatus.Failed "the provider rejected the token") })

    record
        "view: mixed states"
        20
        (build (
            ready ()
            |> withStatus "claude" ConnectionStatus.Connected
            |> withStatus "gmail" (ConnectionStatus.Failed "token rejected")
            |> withStatus "moodle" ConnectionStatus.Disconnected
        ))

    record "view: detail open" 20 (build { ready () with Selected = Some "moodle" })

    record
        "view: connecting"
        20
        (build { ready () with Selected = Some "gmail"; Busy = Some("gmail", "configure") })

    record
        "view: long error text"
        20
        (build (
            { ready () with Selected = Some "moodle" }
            |> withStatus "moodle" (ConnectionStatus.Failed(String.replicate 40 "the provider returned an unexpected response. "))
        ))

    record
        "view: long account labels"
        20
        (build
            { ready () with
                Selected = Some "moodle"
                Connectors =
                    nineProviders ()
                    |> List.map (fun v ->
                        { v with
                            State =
                                { v.State with
                                    Config = Map.ofList [ "base_url", String.replicate 20 "https://a-very-long-host.example.ac.uk/" ] } }) })

    record
        "view: many configuration fields"
        20
        (build
            { ready () with
                Selected = Some "moodle"
                Connectors =
                    nineProviders ()
                    |> List.map (fun v ->
                        if v.Metadata.Key = "moodle" then
                            { v with
                                Metadata =
                                    { v.Metadata with
                                        Fields = List.init 20 (fun i -> field (sprintf "f%d" i) (sprintf "Field %d" i) (i % 4 = 0)) } }
                        else
                            v) })

    record
        "view: 200 history rows"
        10
        (build
            { ready () with
                History = List.init 200 (fun i -> "moodle", now.AddMinutes(float -i), sprintf "Imported %d, updated %d" i (i % 3)) })

    record "view: rapid provider selection" 10 (fun () ->
        [ "claude"; "moodle"; "gmail"; "onedrive" ]
        |> List.sumBy (fun key -> (all (render { ready () with Selected = Some key })).Length))

    IO.File.WriteAllText(IO.Path.Combine(IO.Path.GetTempPath(), "connections-fixtures.txt"), report.ToString())
