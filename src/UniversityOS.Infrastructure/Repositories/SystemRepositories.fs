namespace UniversityOS.Infrastructure.Repositories

open System
open System.Text.Json
open System.Text.Json.Nodes
open Microsoft.Data.Sqlite
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.Infrastructure

[<AutoOpen>]
module private SystemMapping =

    let mapInboxItem (r: SqliteDataReader) : InboxItem =
        { Id = InboxItemId(Sql.guid r "id")
          Source = CaptureSource.parse (Sql.str r "source")
          ExternalId = Sql.strOpt r "external_id"
          Title = Sql.str r "title"
          Body = Sql.str r "body"
          Status = InboxStatus.ofInt (Sql.int32 r "status")
          Analysis = Sql.strOpt r "analysis_json" |> Option.bind Json.deserialize<CaptureAnalysis>
          DocumentId = Sql.guidOpt r "document_id" |> Option.map DocumentId
          ReceivedAt = Sql.date r "received_at"
          ProcessedAt = Sql.dateOpt r "processed_at" }

    let mapConversation (r: SqliteDataReader) : Conversation =
        { Id = ConversationId(Sql.guid r "id")
          Title = Sql.str r "title"
          CreatedAt = Sql.date r "created_at"
          UpdatedAt = Sql.date r "updated_at" }

    let mapChatMessage (r: SqliteDataReader) : ChatMessage =
        { Id = MessageId(Sql.guid r "id")
          ConversationId = ConversationId(Sql.guid r "conversation_id")
          Role = ChatRole.ofInt (Sql.int32 r "role")
          Content = Sql.str r "content"
          CreatedAt = Sql.date r "created_at" }

    let mapConnection (r: SqliteDataReader) : ConnectionState =
        { Key = Sql.str r "key"
          Status = ConnectionStatus.ofStrings (Sql.int32 r "status") (Sql.strOpt r "status_message")
          LastSync = Sql.dateOpt r "last_sync"
          SyncFrequencyMinutes = Sql.int32 r "sync_frequency_minutes"
          Config =
            Sql.str r "config_json"
            |> Json.deserialize<Map<string, string>>
            |> Option.defaultValue Map.empty
          UpdatedAt = Sql.date r "updated_at" }

type InboxRepository(db: Database) =

    let save (item: InboxItem) =
        db.Execute(
            """
            INSERT INTO inbox_items(id, source, external_id, title, body, status, analysis_json, document_id, received_at, processed_at)
            VALUES ($id, $source, $external, $title, $body, $status, $analysis, $doc, $received, $processed)
            ON CONFLICT(id) DO UPDATE SET
                source=$source, external_id=$external, title=$title, body=$body, status=$status,
                analysis_json=$analysis, document_id=$doc, processed_at=$processed
            """,
            [ "$id", Sql.ofGuid item.Id.Value
              "$source", Sql.ofStr (CaptureSource.toString item.Source)
              "$external", Sql.ofStrOpt item.ExternalId
              "$title", Sql.ofStr item.Title
              "$body", Sql.ofStr item.Body
              "$status", Sql.ofInt (InboxStatus.toInt item.Status)
              "$analysis", Sql.ofStrOpt (item.Analysis |> Option.map Json.serialize)
              "$doc", Sql.ofGuidOpt (item.DocumentId |> Option.map (fun d -> d.Value))
              "$received", Sql.ofDate item.ReceivedAt
              "$processed", Sql.ofDateOpt item.ProcessedAt ]
        )

    interface IInboxRepository with

        member _.GetActive() =
            db.Query("SELECT * FROM inbox_items WHERE status IN (0, 1) ORDER BY received_at DESC", [], mapInboxItem)

        member _.GetAll() =
            db.Query("SELECT * FROM inbox_items ORDER BY received_at DESC LIMIT 500", [], mapInboxItem)

        member _.GetById(id) =
            db.QueryOne("SELECT * FROM inbox_items WHERE id = $id", [ "$id", Sql.ofGuid id.Value ], mapInboxItem)

        member _.ExistsExternal(source, externalId) =
            async {
                let! result =
                    db.Scalar(
                        "SELECT COUNT(*) FROM inbox_items WHERE source = $source AND external_id = $external",
                        [ "$source", Sql.ofStr source; "$external", Sql.ofStr externalId ]
                    )

                return (result :?> int64) > 0L
            }

        member _.Save(item) = save item

type ChatRepository(db: Database) =
    interface IChatRepository with

        member _.GetConversations() =
            db.Query("SELECT * FROM conversations ORDER BY updated_at DESC", [], mapConversation)

        member _.GetMessages(conversationId) =
            db.Query(
                "SELECT * FROM chat_messages WHERE conversation_id = $c ORDER BY created_at",
                [ "$c", Sql.ofGuid conversationId.Value ],
                mapChatMessage
            )

        member _.SaveConversation(c) =
            db.Execute(
                """
                INSERT INTO conversations(id, title, created_at, updated_at)
                VALUES ($id, $title, $created, $updated)
                ON CONFLICT(id) DO UPDATE SET title=$title, updated_at=$updated
                """,
                [ "$id", Sql.ofGuid c.Id.Value
                  "$title", Sql.ofStr c.Title
                  "$created", Sql.ofDate c.CreatedAt
                  "$updated", Sql.ofDate c.UpdatedAt ]
            )

        member _.SaveMessage(m) =
            db.Execute(
                "INSERT OR REPLACE INTO chat_messages(id, conversation_id, role, content, created_at) VALUES ($id, $c, $role, $content, $created)",
                [ "$id", Sql.ofGuid m.Id.Value
                  "$c", Sql.ofGuid m.ConversationId.Value
                  "$role", Sql.ofInt (ChatRole.toInt m.Role)
                  "$content", Sql.ofStr m.Content
                  "$created", Sql.ofDate m.CreatedAt ]
            )

        member _.DeleteConversation(id) =
            db.Execute("DELETE FROM conversations WHERE id = $id", [ "$id", Sql.ofGuid id.Value ])

type ConnectionRepository(db: Database) =
    interface IConnectionRepository with

        member _.GetAll() =
            db.Query("SELECT * FROM connections", [], mapConnection)

        member _.Get(key) =
            db.QueryOne("SELECT * FROM connections WHERE key = $key", [ "$key", Sql.ofStr key ], mapConnection)

        member _.Save(state) =
            let statusCode, statusMessage = ConnectionStatus.toStrings state.Status

            db.Execute(
                """
                INSERT INTO connections(key, status, status_message, last_sync, sync_frequency_minutes, config_json, updated_at)
                VALUES ($key, $status, $message, $sync, $freq, $config, $updated)
                ON CONFLICT(key) DO UPDATE SET
                    status=$status, status_message=$message, last_sync=$sync,
                    sync_frequency_minutes=$freq, config_json=$config, updated_at=$updated
                """,
                [ "$key", Sql.ofStr state.Key
                  "$status", Sql.ofInt statusCode
                  "$message", Sql.ofStrOpt statusMessage
                  "$sync", Sql.ofDateOpt state.LastSync
                  "$freq", Sql.ofInt state.SyncFrequencyMinutes
                  "$config", Sql.ofStr (Json.serialize state.Config)
                  "$updated", Sql.ofDate state.UpdatedAt ]
            )

type SyncLogRepository(db: Database) =
    interface ISyncLogRepository with

        member _.Log(connectorKey, startedAt, finishedAt, outcome) =
            let message =
                match outcome with
                | Ok o -> "OK: " + SyncOutcome.describe o
                | Error e -> "Failed: " + e

            db.Execute(
                "INSERT INTO sync_log(id, connector_key, started_at, finished_at, message) VALUES ($id, $key, $start, $finish, $msg)",
                [ "$id", Sql.ofGuid (Guid.NewGuid())
                  "$key", Sql.ofStr connectorKey
                  "$start", Sql.ofDate startedAt
                  "$finish", Sql.ofDate finishedAt
                  "$msg", Sql.ofStr message ]
            )

        member _.GetRecent(limit) =
            db.Query(
                "SELECT connector_key, finished_at, message FROM sync_log ORDER BY finished_at DESC LIMIT $limit",
                [ "$limit", box (int64 limit) ],
                fun r -> Sql.str r "connector_key", Sql.date r "finished_at", Sql.str r "message"
            )

/// Typed user settings persisted as a JSON blob in the settings key-value table.
type SettingsStore(db: Database) =

    let settingsKey = "user_settings"

    interface ISettingsStore with

        member _.Load() =
            async {
                let! row =
                    db.QueryOne(
                        "SELECT value FROM settings WHERE key = $key",
                        [ "$key", Sql.ofStr settingsKey ],
                        fun r -> r.GetString 0
                    )

                return
                    row
                    |> Option.bind Json.deserialize<UserSettings>
                    |> Option.defaultValue UserSettings.defaults
            }

        member _.Save(settings) =
            db.Execute(
                "INSERT INTO settings(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value=$value",
                [ "$key", Sql.ofStr settingsKey; "$value", Sql.ofStr (Json.serialize settings) ]
            )

type SearchRepository(db: Database) =
    interface ISearchRepository with
        member _.Query(text, limit) = Fts.query db text limit
        member _.Index(kind, id, title, body) = Fts.upsert db kind id title body
        member _.Remove(kind, id) = Fts.remove db kind id

[<AutoOpen>]
module private ActivityMapping =

    let mapActivity (r: SqliteDataReader) : ActivityEntry option =
        ActivityKind.parse (Sql.str r "kind")
        |> Option.map (fun kind ->
            { Id = Sql.guid r "id"
              Kind = kind
              Title = Sql.str r "title"
              Detail = Sql.strOpt r "detail"
              Entity =
                (match Sql.strOpt r "entity_kind", Sql.guidOpt r "entity_id" with
                 | Some kindStr, Some id -> EntityKind.parse kindStr |> Option.map (fun k -> { Kind = k; Id = id })
                 | _ -> None)
              OccurredAt = Sql.date r "occurred_at" })

type ActivityRepository(db: Database) =
    interface IActivityLog with

        member _.Log(entry) =
            db.Execute(
                """
                INSERT INTO activity_log(id, kind, title, detail, entity_kind, entity_id, occurred_at)
                VALUES ($id, $kind, $title, $detail, $ekind, $eid, $at)
                """,
                [ "$id", Sql.ofGuid entry.Id
                  "$kind", Sql.ofStr (ActivityKind.toString entry.Kind)
                  "$title", Sql.ofStr entry.Title
                  "$detail", Sql.ofStrOpt entry.Detail
                  "$ekind", Sql.ofStrOpt (entry.Entity |> Option.map (fun e -> EntityKind.toString e.Kind))
                  "$eid", Sql.ofGuidOpt (entry.Entity |> Option.map (fun e -> e.Id))
                  "$at", Sql.ofDate entry.OccurredAt ]
            )

        member _.GetRecent(limit) =
            async {
                let! rows =
                    db.Query(
                        "SELECT * FROM activity_log ORDER BY occurred_at DESC LIMIT $limit",
                        [ "$limit", box (int64 limit) ],
                        mapActivity
                    )

                return rows |> List.choose id
            }

        member _.GetBetween(rangeStart, rangeEnd) =
            async {
                let! rows =
                    db.Query(
                        "SELECT * FROM activity_log WHERE occurred_at >= $start AND occurred_at < $end ORDER BY occurred_at DESC",
                        [ "$start", Sql.ofDate rangeStart; "$end", Sql.ofDate rangeEnd ],
                        mapActivity
                    )

                return rows |> List.choose id
            }

type MemoryRepository(db: Database) =
    interface IMemoryRepository with

        member _.GetAll() =
            db.Query(
                "SELECT * FROM ai_memory ORDER BY source, category, updated_at DESC",
                [],
                fun r ->
                    { Id = Sql.guid r "id"
                      Category = Sql.str r "category"
                      Content = Sql.str r "content"
                      Source = MemorySource.parse (Sql.str r "source")
                      Enabled = Sql.bool' r "enabled"
                      UpdatedAt = Sql.date r "updated_at" }
            )

        member _.Save(item) =
            db.Execute(
                """
                INSERT INTO ai_memory(id, category, content, source, enabled, updated_at)
                VALUES ($id, $category, $content, $source, $enabled, $at)
                ON CONFLICT(id) DO UPDATE SET
                    category=$category, content=$content, source=$source, enabled=$enabled, updated_at=$at
                """,
                [ "$id", Sql.ofGuid item.Id
                  "$category", Sql.ofStr item.Category
                  "$content", Sql.ofStr item.Content
                  "$source", Sql.ofStr (MemorySource.toString item.Source)
                  "$enabled", Sql.ofBool item.Enabled
                  "$at", Sql.ofDate item.UpdatedAt ]
            )

        member _.Delete(id) =
            db.Execute("DELETE FROM ai_memory WHERE id = $id", [ "$id", Sql.ofGuid id ])

        member _.DeleteBySource(source) =
            db.Execute(
                "DELETE FROM ai_memory WHERE source = $source",
                [ "$source", Sql.ofStr (MemorySource.toString source) ]
            )

/// Field-level, total decoding for UiState.
///
/// The whole-record deserializer throws when a field is missing, and the caller
/// then falls back to defaults — so adding one field to the record silently
/// discarded the user's collapsed groups AND their last-briefing date, which
/// made the morning briefing reappear. Reading field by field means an older
/// row keeps everything it does carry, which is the same guarantee
/// AppearanceJson already gives (PRESENTATION-ARCHITECTURE §14.2).
[<RequireQualifiedAccess>]
module UiStateJson =

    let private density =
        function
        | "compact" -> SidebarDensity.Compact
        | _ -> SidebarDensity.Expanded

    let private densityText =
        function
        | SidebarDensity.Compact -> "compact"
        | SidebarDensity.Expanded -> "expanded"

    let encode (s: UiState) =
        let o = JsonObject()
        o["SidebarDensity"] <- JsonValue.Create(densityText s.SidebarDensity)
        o["SidebarPinned"] <- JsonValue.Create s.SidebarPinned
        o["LastBriefingDate"] <- (match s.LastBriefingDate with Some d -> JsonValue.Create d :> JsonNode | None -> null)
        o["LastReviewDate"] <- (match s.LastReviewDate with Some d -> JsonValue.Create d :> JsonNode | None -> null)
        o["AiMemoryEnabled"] <- JsonValue.Create s.AiMemoryEnabled
        o.ToJsonString()

    let decode (raw: string) =
        try
            use doc = JsonDocument.Parse raw
            let root = doc.RootElement
            let d = UiState.defaults

            if root.ValueKind <> JsonValueKind.Object then
                d
            else
                let str name fallback =
                    match root.TryGetProperty(name: string) with
                    | true, el when el.ValueKind = JsonValueKind.String ->
                        match el.GetString() with
                        | null -> fallback
                        | v -> v
                    | _ -> fallback

                let optStr name =
                    match root.TryGetProperty(name: string) with
                    | true, el when el.ValueKind = JsonValueKind.String -> el.GetString() |> Option.ofObj
                    | _ -> None

                let bool name fallback =
                    match root.TryGetProperty(name: string) with
                    | true, el when el.ValueKind = JsonValueKind.True || el.ValueKind = JsonValueKind.False ->
                        el.GetBoolean()
                    | _ -> fallback

                { SidebarDensity = str "SidebarDensity" (densityText d.SidebarDensity) |> density
                  SidebarPinned = bool "SidebarPinned" d.SidebarPinned
                  LastBriefingDate = optStr "LastBriefingDate"
                  LastReviewDate = optStr "LastReviewDate"
                  AiMemoryEnabled = bool "AiMemoryEnabled" d.AiMemoryEnabled }
        with _ ->
            UiState.defaults

/// Persisted UI/app state in its own settings row — same pattern as
/// SettingsStore, deliberately separate record (see Abstractions.fs).
type UiStateStore(db: Database) =

    let stateKey = "ui_state"

    interface IUiStateStore with

        member _.Load() =
            async {
                let! row =
                    db.QueryOne(
                        "SELECT value FROM settings WHERE key = $key",
                        [ "$key", Sql.ofStr stateKey ],
                        fun r -> r.GetString 0
                    )

                return row |> Option.map UiStateJson.decode |> Option.defaultValue UiState.defaults
            }

        member _.Save(state) =
            db.Execute(
                "INSERT INTO settings(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value=$value",
                [ "$key", Sql.ofStr stateKey; "$value", Sql.ofStr (UiStateJson.encode state) ]
            )
