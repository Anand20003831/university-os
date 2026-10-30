namespace UniversityOS.Infrastructure.Connectors

open System
open System.Threading
open UniversityOS.Domain
open UniversityOS.Application

/// The single write-path for connector imports: deduplicates against existing
/// data and routes everything into the same entities the rest of the app uses.
type IngestionSink(events: IEventRepository, inbox: IInboxRepository, clock: IClock) =
    interface IIngestionSink with

        member _.UpsertEvent(connectorKey, ingested) =
            async {
                let! existing = events.GetByExternalId(connectorKey, ingested.ExternalId)

                match existing with
                | Some current ->
                    let changed =
                        current.Title <> ingested.Title
                        || current.StartsAt <> ingested.StartsAt
                        || current.EndsAt <> ingested.EndsAt
                        || current.Location <> ingested.Location

                    if changed then
                        do! events.Save
                                { current with
                                    Title = ingested.Title
                                    StartsAt = ingested.StartsAt
                                    EndsAt = ingested.EndsAt
                                    AllDay = ingested.AllDay
                                    Location = ingested.Location
                                    Notes = ingested.Notes
                                    UpdatedAt = clock.Now }

                        return IngestOutcome.Updated
                    else
                        return IngestOutcome.Unchanged
                | None ->
                    let event =
                        { CalendarEvent.create clock.Now ingested.Title ingested.Kind ingested.StartsAt ingested.EndsAt with
                            AllDay = ingested.AllDay
                            Location = ingested.Location
                            Source = Some connectorKey
                            ExternalId = Some ingested.ExternalId
                            Notes = ingested.Notes }

                    do! events.Save event
                    return IngestOutcome.Added
            }

        member _.AddInboxItem(connectorKey, ingested) =
            async {
                let source = CaptureSource.Connector connectorKey
                let sourceString = CaptureSource.toString source
                let! exists = inbox.ExistsExternal(sourceString, ingested.ExternalId)

                if exists then
                    return IngestOutcome.Unchanged
                else
                    let item =
                        { InboxItem.create clock.Now source ingested.Title ingested.Body with
                            ExternalId = Some ingested.ExternalId
                            ReceivedAt = ingested.ReceivedAt }

                    do! inbox.Save item
                    return IngestOutcome.Added
            }

/// Owns connector instances, persists their state, and runs manual + scheduled
/// synchronization. The UI's Connections page talks exclusively to this.
type ConnectorRegistry
    (
        connectors: IConnector list,
        connections: IConnectionRepository,
        syncLog: ISyncLogRepository,
        activity: IActivityLog,
        clock: IClock
    ) =

    let byKey =
        connectors |> List.map (fun c -> c.Metadata.Key, c) |> dict

    let getState (key: string) =
        async {
            let! state = connections.Get key

            return
                state
                |> Option.defaultValue (ConnectionState.initial key clock.Now)
        }

    // One sync at a time per connector; SemaphoreSlim guards the whole registry
    // (syncs are short and infrequent — simplicity wins over parallelism here).
    let syncGate = new SemaphoreSlim(1, 1)

    let runSync (connector: IConnector) =
        async {
            let key = connector.Metadata.Key
            let startedAt = clock.Now
            do! syncGate.WaitAsync() |> Async.AwaitTask

            try
                let! result =
                    async {
                        try
                            return! connector.Sync()
                        with ex ->
                            return Error ex.Message
                    }

                do! syncLog.Log(key, startedAt, clock.Now, result)
                let! state = getState key

                match result with
                | Ok outcome ->
                    do! connections.Save
                            { state with
                                Status = ConnectionStatus.Connected
                                LastSync = Some clock.Now
                                UpdatedAt = clock.Now }

                    // Only meaningful syncs land on the timeline — a quiet no-change
                    // poll every half hour would drown everything else out.
                    if outcome.Imported > 0 || outcome.Updated > 0 then
                        do! activity.Log(
                                ActivityEntry.create
                                    clock.Now
                                    ActivityKind.ConnectorSynced
                                    (sprintf "%s synced" connector.Metadata.Name)
                                    (Some(SyncOutcome.describe outcome))
                                    None
                            )
                | Error message ->
                    do! connections.Save
                            { state with
                                Status = ConnectionStatus.Failed message
                                UpdatedAt = clock.Now }

                return result
            finally
                syncGate.Release() |> ignore
        }

    member private _.Connector(key: string) =
        match byKey.TryGetValue key with
        | true, c -> Some c
        | _ -> None

    /// Starts periodic background sync; returns a handle that stops it.
    member this.StartBackgroundSync() : IDisposable =
        let registry = this :> IConnectorRegistry

        let timer =
            new Timer(
                (fun _ -> registry.SyncAllDue() |> Async.Start),
                null,
                TimeSpan.FromMinutes 1.0,
                TimeSpan.FromMinutes 5.0
            )

        timer :> IDisposable

    interface IConnectorRegistry with

        member _.List() =
            async {
                let results = ResizeArray()

                for connector in connectors do
                    let! state = getState connector.Metadata.Key
                    results.Add { Metadata = connector.Metadata; State = state }

                return List.ofSeq results
            }

        member this.Get(key) = this.Connector key

        member this.Configure(key, values) =
            async {
                match this.Connector key with
                | None -> return Error(sprintf "Unknown connector '%s'." key)
                | Some connector ->
                    // Persist non-secret values first so the connector can read them.
                    let secretKeys =
                        connector.Metadata.Fields |> List.filter (fun f -> f.Secret) |> List.map (fun f -> f.Key) |> Set.ofList

                    let! state = getState key

                    let mergedConfig =
                        values
                        |> Map.filter (fun k v -> not (secretKeys.Contains k) && v.Trim().Length > 0)
                        |> Map.fold (fun acc k v -> Map.add k v acc) state.Config

                    do! connections.Save { state with Config = mergedConfig; UpdatedAt = clock.Now }

                    let! result =
                        async {
                            try
                                return! connector.Configure values
                            with ex ->
                                return Error ex.Message
                        }

                    let! state = getState key

                    match result with
                    | Ok message ->
                        do! connections.Save
                                { state with
                                    Status = ConnectionStatus.Connected
                                    UpdatedAt = clock.Now }

                        return Ok message
                    | Error message ->
                        do! connections.Save
                                { state with
                                    Status = ConnectionStatus.Failed message
                                    UpdatedAt = clock.Now }

                        return Error message
            }

        member this.Test(key) =
            async {
                match this.Connector key with
                | None -> return Error(sprintf "Unknown connector '%s'." key)
                | Some connector ->
                    try
                        return! connector.Test()
                    with ex ->
                        return Error ex.Message
            }

        member this.Sync(key) =
            async {
                match this.Connector key with
                | None -> return Error(sprintf "Unknown connector '%s'." key)
                | Some connector -> return! runSync connector
            }

        member this.Disconnect(key) =
            async {
                match this.Connector key with
                | None -> ()
                | Some connector ->
                    do! connector.Disconnect()
                    let! state = getState key

                    do! connections.Save
                            { state with
                                Status = ConnectionStatus.Disconnected
                                UpdatedAt = clock.Now }
            }

        member _.SetSyncFrequency(key, minutes) =
            async {
                let! state = getState key

                do! connections.Save
                        { state with
                            SyncFrequencyMinutes = max 5 minutes
                            UpdatedAt = clock.Now }
            }

        member this.SyncAllDue() =
            async {
                for connector in connectors do
                    if connector.Metadata.SupportsSync then
                        let! state = getState connector.Metadata.Key

                        let due =
                            match state.Status, state.LastSync with
                            | ConnectionStatus.Connected, None -> true
                            | ConnectionStatus.Connected, Some last ->
                                last.AddMinutes(float state.SyncFrequencyMinutes) <= clock.Now
                            | _ -> false

                        if due then
                            let! _ = runSync connector
                            ()
            }
