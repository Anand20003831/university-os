namespace UniversityOS.Application

open System
open UniversityOS.Domain

/// What connectors are allowed to write. They never touch repositories directly:
/// every import flows through this sink (and therefore through dedup + indexing),
/// keeping providers fully decoupled from storage.

type IngestedEvent =
    { ExternalId: string
      Title: string
      Kind: EventKind
      StartsAt: DateTime
      EndsAt: DateTime
      AllDay: bool
      Location: string option
      Notes: string option }

type IngestedItem =
    { ExternalId: string
      Title: string
      Body: string
      ReceivedAt: DateTime }

[<RequireQualifiedAccess>]
type IngestOutcome =
    | Added
    | Updated
    | Unchanged

type IIngestionSink =
    abstract UpsertEvent: connectorKey: string * event: IngestedEvent -> Async<IngestOutcome>
    abstract AddInboxItem: connectorKey: string * item: IngestedItem -> Async<IngestOutcome>

/// The contract every external integration implements. The Connections UI and the
/// sync scheduler operate exclusively against this interface.
type IConnector =
    abstract Metadata: ConnectorMetadata
    /// Stores configuration (secrets go to the secret store) and attempts to
    /// establish the connection. For OAuth connectors this launches the browser flow.
    abstract Configure: values: Map<string, string> -> Async<Result<string, string>>
    /// Verifies the stored credentials actually work, returning a human-readable status.
    abstract Test: unit -> Async<Result<string, string>>
    /// Pulls new data through the ingestion sink.
    abstract Sync: unit -> Async<Result<SyncOutcome, string>>
    /// Removes stored credentials.
    abstract Disconnect: unit -> Async<unit>

type ConnectorView =
    { Metadata: ConnectorMetadata
      State: ConnectionState }

type IConnectorRegistry =
    abstract List: unit -> Async<ConnectorView list>
    abstract Get: key: string -> IConnector option
    abstract Configure: key: string * values: Map<string, string> -> Async<Result<string, string>>
    abstract Test: key: string -> Async<Result<string, string>>
    abstract Sync: key: string -> Async<Result<SyncOutcome, string>>
    abstract Disconnect: key: string -> Async<unit>
    abstract SetSyncFrequency: key: string * minutes: int -> Async<unit>
    /// Runs Sync for every connected connector whose frequency interval has elapsed.
    /// Called by the background scheduler; safe to call at any time.
    abstract SyncAllDue: unit -> Async<unit>
