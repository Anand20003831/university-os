namespace UniversityOS.Infrastructure.Connectors

open System
open System.Net.Http
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.Infrastructure

type OAuthEndpoints = { Auth: string; Token: string }

/// Everything that distinguishes one OAuth-based connector from another.
/// The generic OAuthConnector below turns a spec into a full IConnector, so
/// adding a new OAuth service is: write a spec, register it.
type OAuthConnectorSpec =
    { Metadata: ConnectorMetadata
      Scopes: string list
      /// Endpoints may depend on saved config (Microsoft varies by tenant).
      Endpoints: Map<string, string> -> OAuthEndpoints
      ExtraAuthParams: (string * string) list
      UsesClientSecret: bool
      Test: string -> Async<Result<string, string>>
      Sync: string -> IIngestionSink -> Async<Result<SyncOutcome, string>> }

/// Generic OAuth connector: browser sign-in with PKCE, encrypted token storage,
/// transparent refresh, and spec-provided test/sync behavior.
type OAuthConnector
    (
        spec: OAuthConnectorSpec,
        secrets: ISecretStore,
        connections: IConnectionRepository,
        sink: IIngestionSink,
        http: HttpClient
    ) =

    let tokens = TokenStore(secrets, http)
    let clientSecretKey = spec.Metadata.Key + ":client_secret"

    let config () =
        async {
            let! state = connections.Get spec.Metadata.Key

            return
                state
                |> Option.map (fun s -> s.Config)
                |> Option.defaultValue Map.empty
        }

    let accessToken () =
        async {
            let! cfg = config ()

            match cfg.TryFind "client_id" with
            | None -> return Error "Not configured — add the OAuth client id and connect first."
            | Some clientId ->
                let! secret =
                    if spec.UsesClientSecret then secrets.Get clientSecretKey
                    else async.Return None

                let endpoints = spec.Endpoints cfg
                return! tokens.GetValidAccessToken(spec.Metadata.Key, endpoints.Token, clientId, secret)
        }

    interface IConnector with

        member _.Metadata = spec.Metadata

        member _.Configure(values) =
            async {
                match values.TryFind "client_secret" with
                | Some s when s.Trim().Length > 0 -> do! secrets.Set(clientSecretKey, s.Trim())
                | _ -> ()

                // The registry persists non-secret values before calling Configure,
                // so config() already reflects them.
                let! cfg = config ()

                let clientId =
                    values.TryFind "client_id"
                    |> Option.filter (fun v -> v.Trim().Length > 0)
                    |> Option.orElse (cfg.TryFind "client_id")

                match clientId with
                | None -> return Error "A client id is required."
                | Some clientId ->
                    let! secret =
                        if spec.UsesClientSecret then secrets.Get clientSecretKey
                        else async.Return None

                    let endpoints = spec.Endpoints cfg
                    let! auth = OAuth.authorize http endpoints.Auth endpoints.Token clientId secret spec.Scopes spec.ExtraAuthParams

                    match auth with
                    | Error e -> return Error e
                    | Ok tokenSet ->
                        do! tokens.Save(spec.Metadata.Key, tokenSet)
                        return! spec.Test tokenSet.AccessToken
            }

        member _.Test() =
            async {
                let! token = accessToken ()

                match token with
                | Error e -> return Error e
                | Ok token -> return! spec.Test token
            }

        member _.Sync() =
            async {
                let! token = accessToken ()

                match token with
                | Error e -> return Error e
                | Ok token -> return! spec.Sync token sink
            }

        member _.Disconnect() =
            async {
                do! tokens.Delete spec.Metadata.Key
                do! secrets.Delete clientSecretKey
            }

/// Google Workspace connectors: Gmail, Google Calendar, Google Drive.
/// All three share Google's OAuth endpoints and an "installed app" client
/// (create one at console.cloud.google.com → Credentials → OAuth client → Desktop).
[<RequireQualifiedAccess>]
module GoogleConnectors =

    let private endpoints _ =
        { Auth = "https://accounts.google.com/o/oauth2/v2/auth"
          Token = "https://oauth2.googleapis.com/token" }

    // access_type=offline + prompt=consent makes Google issue a refresh token.
    let private extraAuthParams = [ "access_type", "offline"; "prompt", "consent" ]

    let private bearer token = [ "Authorization", "Bearer " + token ]

    let private clientFields =
        [ { Key = "client_id"
            Label = "OAuth client id"
            Secret = false
            Placeholder = "…apps.googleusercontent.com"
            Help = "Google Cloud Console → Credentials → OAuth client (Desktop app)." }
          { Key = "client_secret"
            Label = "OAuth client secret"
            Secret = true
            Placeholder = ""
            Help = "From the same OAuth client. Stored encrypted." } ]

    let gmail (secrets, connections, sink, http: HttpClient) : IConnector =
        let test token =
            async {
                let! result = Http.getJson http (bearer token) "https://gmail.googleapis.com/gmail/v1/users/me/profile"

                match result with
                | Error e -> return Error e
                | Ok doc ->
                    use doc = doc
                    let email = Http.propStr "emailAddress" doc.RootElement |> Option.defaultValue "unknown"
                    return Ok(sprintf "Connected as %s." email)
            }

        let sync token (sink: IIngestionSink) =
            async {
                let! listResult =
                    Http.getJson
                        http
                        (bearer token)
                        "https://gmail.googleapis.com/gmail/v1/users/me/messages?q=newer_than:7d&maxResults=20"

                match listResult with
                | Error e -> return Error e
                | Ok listDoc ->
                    use listDoc = listDoc
                    let ids = Http.propArray "messages" listDoc.RootElement |> List.choose (Http.propStr "id")
                    let mutable added = 0
                    let mutable unchanged = 0

                    for id in ids do
                        let! detail =
                            Http.getJson
                                http
                                (bearer token)
                                (sprintf
                                    "https://gmail.googleapis.com/gmail/v1/users/me/messages/%s?format=metadata&metadataHeaders=Subject&metadataHeaders=From"
                                    id)

                        match detail with
                        | Error _ -> ()
                        | Ok doc ->
                            use doc = doc
                            let root = doc.RootElement

                            let headers =
                                Http.prop "payload" root
                                |> Option.map (Http.propArray "headers")
                                |> Option.defaultValue []
                                |> List.choose (fun h ->
                                    match Http.propStr "name" h, Http.propStr "value" h with
                                    | Some n, Some v -> Some(n, v)
                                    | _ -> None)

                            let header name =
                                headers
                                |> List.tryFind (fun (n, _) -> String.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                                |> Option.map snd

                            let received =
                                Http.propStr "internalDate" root
                                |> Option.bind (fun ms ->
                                    match Int64.TryParse ms with
                                    | true, v -> Some(DateTimeOffset.FromUnixTimeMilliseconds(v).LocalDateTime)
                                    | _ -> None)
                                |> Option.defaultValue DateTime.Now

                            let! outcome =
                                sink.AddInboxItem(
                                    "gmail",
                                    { ExternalId = id
                                      Title = header "Subject" |> Option.defaultValue "(no subject)"
                                      Body =
                                        sprintf
                                            "From: %s\n\n%s"
                                            (header "From" |> Option.defaultValue "unknown sender")
                                            (Http.propStr "snippet" root |> Option.defaultValue "")
                                      ReceivedAt = received }
                                )

                            match outcome with
                            | IngestOutcome.Added -> added <- added + 1
                            | _ -> unchanged <- unchanged + 1

                    return Ok { Imported = added; Updated = 0; Skipped = unchanged; Message = None }
            }

        OAuthConnector(
            { Metadata =
                { Key = "gmail"
                  Name = "Gmail"
                  Category = ConnectorCategory.Communication
                  Description = "Imports recent email into the inbox for triage."
                  Icon = "✉"
                  Auth = AuthKind.OAuth
                  Fields = clientFields
                  SupportsSync = true }
              Scopes = [ "https://www.googleapis.com/auth/gmail.readonly" ]
              Endpoints = endpoints
              ExtraAuthParams = extraAuthParams
              UsesClientSecret = true
              Test = test
              Sync = sync },
            secrets,
            connections,
            sink,
            http
        )
        :> IConnector

    let googleCalendar (secrets, connections, sink, http: HttpClient) : IConnector =
        let test token =
            async {
                let! result =
                    Http.getJson http (bearer token) "https://www.googleapis.com/calendar/v3/users/me/calendarList?maxResults=1"

                match result with
                | Error e -> return Error e
                | Ok doc ->
                    doc.Dispose()
                    return Ok "Connected to Google Calendar."
            }

        let sync token (sink: IIngestionSink) =
            async {
                let timeMin = DateTime.UtcNow.AddDays(-1.0).ToString "yyyy-MM-dd'T'HH:mm:ss'Z'"
                let timeMax = DateTime.UtcNow.AddDays(60.0).ToString "yyyy-MM-dd'T'HH:mm:ss'Z'"

                let url =
                    sprintf
                        "https://www.googleapis.com/calendar/v3/calendars/primary/events?timeMin=%s&timeMax=%s&singleEvents=true&orderBy=startTime&maxResults=100"
                        (Uri.EscapeDataString timeMin)
                        (Uri.EscapeDataString timeMax)

                let! result = Http.getJson http (bearer token) url

                match result with
                | Error e -> return Error e
                | Ok doc ->
                    use doc = doc
                    let mutable added = 0
                    let mutable updated = 0
                    let mutable unchanged = 0

                    for item in Http.propArray "items" doc.RootElement do
                        let readTime name =
                            Http.prop name item
                            |> Option.bind (fun t ->
                                match Http.propStr "dateTime" t with
                                | Some dt -> Http.parseIsoDate dt |> Option.map (fun d -> d, false)
                                | None ->
                                    Http.propStr "date" t
                                    |> Option.bind Http.parseIsoDate
                                    |> Option.map (fun d -> d, true))

                        match Http.propStr "id" item, readTime "start", readTime "end" with
                        | Some id, Some(start, allDay), Some(finish, _) ->
                            let! outcome =
                                sink.UpsertEvent(
                                    "google-calendar",
                                    { ExternalId = id
                                      Title = Http.propStr "summary" item |> Option.defaultValue "(untitled)"
                                      Kind = EventKind.Personal
                                      StartsAt = start
                                      EndsAt = finish
                                      AllDay = allDay
                                      Location = Http.propStr "location" item
                                      Notes = Http.propStr "description" item }
                                )

                            match outcome with
                            | IngestOutcome.Added -> added <- added + 1
                            | IngestOutcome.Updated -> updated <- updated + 1
                            | IngestOutcome.Unchanged -> unchanged <- unchanged + 1
                        | _ -> ()

                    return Ok { Imported = added; Updated = updated; Skipped = unchanged; Message = None }
            }

        OAuthConnector(
            { Metadata =
                { Key = "google-calendar"
                  Name = "Google Calendar"
                  Category = ConnectorCategory.Calendar
                  Description = "Keeps your Google Calendar events on the University OS calendar."
                  Icon = "📅"
                  Auth = AuthKind.OAuth
                  Fields = clientFields
                  SupportsSync = true }
              Scopes = [ "https://www.googleapis.com/auth/calendar.readonly" ]
              Endpoints = endpoints
              ExtraAuthParams = extraAuthParams
              UsesClientSecret = true
              Test = test
              Sync = sync },
            secrets,
            connections,
            sink,
            http
        )
        :> IConnector

    let googleDrive (secrets, connections, sink, http: HttpClient) : IConnector =
        let test token =
            async {
                let! result =
                    Http.getJson http (bearer token) "https://www.googleapis.com/drive/v3/about?fields=user"

                match result with
                | Error e -> return Error e
                | Ok doc ->
                    use doc = doc

                    let name =
                        Http.prop "user" doc.RootElement
                        |> Option.bind (Http.propStr "displayName")
                        |> Option.defaultValue "unknown"

                    return Ok(sprintf "Connected as %s." name)
            }

        let sync token (sink: IIngestionSink) =
            async {
                let url =
                    "https://www.googleapis.com/drive/v3/files?orderBy=modifiedTime%20desc&pageSize=20&fields=files(id,name,webViewLink,modifiedTime)"

                let! result = Http.getJson http (bearer token) url

                match result with
                | Error e -> return Error e
                | Ok doc ->
                    use doc = doc
                    let mutable added = 0
                    let mutable unchanged = 0

                    for file in Http.propArray "files" doc.RootElement do
                        match Http.propStr "id" file, Http.propStr "name" file with
                        | Some id, Some name ->
                            let! outcome =
                                sink.AddInboxItem(
                                    "google-drive",
                                    { ExternalId = id
                                      Title = "Drive: " + name
                                      Body = Http.propStr "webViewLink" file |> Option.defaultValue ""
                                      ReceivedAt =
                                        Http.propStr "modifiedTime" file
                                        |> Option.bind Http.parseIsoDate
                                        |> Option.defaultValue DateTime.Now }
                                )

                            match outcome with
                            | IngestOutcome.Added -> added <- added + 1
                            | _ -> unchanged <- unchanged + 1
                        | _ -> ()

                    return Ok { Imported = added; Updated = 0; Skipped = unchanged; Message = None }
            }

        OAuthConnector(
            { Metadata =
                { Key = "google-drive"
                  Name = "Google Drive"
                  Category = ConnectorCategory.Storage
                  Description = "Surfaces recently modified Drive files in the inbox."
                  Icon = "🗂"
                  Auth = AuthKind.OAuth
                  Fields = clientFields
                  SupportsSync = true }
              Scopes = [ "https://www.googleapis.com/auth/drive.metadata.readonly" ]
              Endpoints = endpoints
              ExtraAuthParams = extraAuthParams
              UsesClientSecret = true
              Test = test
              Sync = sync },
            secrets,
            connections,
            sink,
            http
        )
        :> IConnector
