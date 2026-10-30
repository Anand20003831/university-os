namespace UniversityOS.Domain

open System

[<RequireQualifiedAccess>]
type ConnectorCategory =
    | Academic
    | Communication
    | Calendar
    | Development
    | Ai
    | Storage

[<RequireQualifiedAccess>]
module ConnectorCategory =
    let label =
        function
        | ConnectorCategory.Academic -> "Academic"
        | ConnectorCategory.Communication -> "Communication"
        | ConnectorCategory.Calendar -> "Calendar"
        | ConnectorCategory.Development -> "Development"
        | ConnectorCategory.Ai -> "AI"
        | ConnectorCategory.Storage -> "Storage"

    let ordered =
        [ ConnectorCategory.Ai
          ConnectorCategory.Academic
          ConnectorCategory.Communication
          ConnectorCategory.Calendar
          ConnectorCategory.Development
          ConnectorCategory.Storage ]

[<RequireQualifiedAccess>]
type AuthKind =
    | OAuth
    | ApiKey
    | AccessToken

[<RequireQualifiedAccess>]
module AuthKind =
    let label =
        function
        | AuthKind.OAuth -> "OAuth 2.0"
        | AuthKind.ApiKey -> "API key"
        | AuthKind.AccessToken -> "Access token"

[<RequireQualifiedAccess>]
type ConnectionStatus =
    | NotConfigured
    | Disconnected
    | Connected
    | Failed of string

[<RequireQualifiedAccess>]
module ConnectionStatus =
    let label =
        function
        | ConnectionStatus.NotConfigured -> "Not configured"
        | ConnectionStatus.Disconnected -> "Disconnected"
        | ConnectionStatus.Connected -> "Connected"
        | ConnectionStatus.Failed _ -> "Error"

    let toStrings =
        function
        | ConnectionStatus.NotConfigured -> 0, None
        | ConnectionStatus.Disconnected -> 1, None
        | ConnectionStatus.Connected -> 2, None
        | ConnectionStatus.Failed msg -> 3, Some msg

    let ofStrings (code: int) (msg: string option) =
        match code with
        | 1 -> ConnectionStatus.Disconnected
        | 2 -> ConnectionStatus.Connected
        | 3 -> ConnectionStatus.Failed(msg |> Option.defaultValue "Unknown error")
        | _ -> ConnectionStatus.NotConfigured

/// A field a connector needs to be configured (API key, base URL, client id, …).
/// The Connections UI is generated from these descriptors.
type ConfigField =
    { Key: string
      Label: string
      Secret: bool
      Placeholder: string
      Help: string }

type ConnectorMetadata =
    { Key: string
      Name: string
      Category: ConnectorCategory
      Description: string
      /// Short glyph shown in the UI.
      Icon: string
      Auth: AuthKind
      Fields: ConfigField list
      /// Whether Sync imports data (the Claude connector, for instance, only provides AI).
      SupportsSync: bool }

/// Persisted, non-secret state of a connection.
type ConnectionState =
    { Key: string
      Status: ConnectionStatus
      LastSync: DateTime option
      SyncFrequencyMinutes: int
      Config: Map<string, string>
      UpdatedAt: DateTime }

[<RequireQualifiedAccess>]
module ConnectionState =
    let initial (key: string) (now: DateTime) =
        { Key = key
          Status = ConnectionStatus.NotConfigured
          LastSync = None
          SyncFrequencyMinutes = 30
          Config = Map.empty
          UpdatedAt = now }

type SyncOutcome =
    { Imported: int
      Updated: int
      Skipped: int
      Message: string option }

[<RequireQualifiedAccess>]
module SyncOutcome =
    let empty = { Imported = 0; Updated = 0; Skipped = 0; Message = None }

    let describe (o: SyncOutcome) =
        match o.Message with
        | Some m -> m
        | None -> sprintf "%d new, %d updated, %d unchanged" o.Imported o.Updated o.Skipped
