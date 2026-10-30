namespace UniversityOS.Infrastructure.Connectors

open System
open System.Net.Http
open UniversityOS.Domain
open UniversityOS.Application

/// Microsoft 365 connectors over the Graph API: Outlook Mail, Outlook Calendar,
/// OneDrive. Uses the public-client PKCE flow — the user registers a (free)
/// Azure app once and only needs its client id, no secret.
[<RequireQualifiedAccess>]
module MicrosoftConnectors =

    let private endpoints (config: Map<string, string>) =
        let tenant =
            config.TryFind "tenant"
            |> Option.filter (fun t -> t.Trim().Length > 0)
            |> Option.defaultValue "common"

        { Auth = sprintf "https://login.microsoftonline.com/%s/oauth2/v2.0/authorize" tenant
          Token = sprintf "https://login.microsoftonline.com/%s/oauth2/v2.0/token" tenant }

    let private bearer token = [ "Authorization", "Bearer " + token ]

    let private clientFields =
        [ { Key = "client_id"
            Label = "Application (client) id"
            Secret = false
            Placeholder = "00000000-0000-…"
            Help = "Azure Portal → App registrations → New (public client, redirect http://localhost). Free personal accounts work." }
          { Key = "tenant"
            Label = "Tenant"
            Secret = false
            Placeholder = "common"
            Help = "'common' for any account, 'consumers' for personal, or your university's tenant id." } ]

    let private testMe (http: HttpClient) token =
        async {
            let! result = Http.getJson http (bearer token) "https://graph.microsoft.com/v1.0/me"

            match result with
            | Error e -> return Error e
            | Ok doc ->
                use doc = doc
                let root = doc.RootElement

                let name =
                    Http.propStr "displayName" root
                    |> Option.orElse (Http.propStr "userPrincipalName" root)
                    |> Option.defaultValue "unknown"

                return Ok(sprintf "Connected as %s." name)
        }

    let outlookMail (secrets, connections, sink, http: HttpClient) : IConnector =
        let sync token (sink: IIngestionSink) =
            async {
                let url =
                    "https://graph.microsoft.com/v1.0/me/messages?$top=20&$orderby=receivedDateTime%20desc&$select=id,subject,from,bodyPreview,receivedDateTime"

                let! result = Http.getJson http (bearer token) url

                match result with
                | Error e -> return Error e
                | Ok doc ->
                    use doc = doc
                    let mutable added = 0
                    let mutable unchanged = 0

                    for message in Http.propArray "value" doc.RootElement do
                        match Http.propStr "id" message with
                        | Some id ->
                            let sender =
                                Http.prop "from" message
                                |> Option.bind (Http.prop "emailAddress")
                                |> Option.bind (fun e ->
                                    match Http.propStr "name" e, Http.propStr "address" e with
                                    | Some n, Some a -> Some(sprintf "%s <%s>" n a)
                                    | Some n, None -> Some n
                                    | None, Some a -> Some a
                                    | _ -> None)
                                |> Option.defaultValue "unknown sender"

                            let! outcome =
                                sink.AddInboxItem(
                                    "outlook",
                                    { ExternalId = id
                                      Title =
                                        Http.propStr "subject" message
                                        |> Option.filter (fun s -> s.Trim().Length > 0)
                                        |> Option.defaultValue "(no subject)"
                                      Body =
                                        sprintf
                                            "From: %s\n\n%s"
                                            sender
                                            (Http.propStr "bodyPreview" message |> Option.defaultValue "")
                                      ReceivedAt =
                                        Http.propStr "receivedDateTime" message
                                        |> Option.bind Http.parseIsoDate
                                        |> Option.defaultValue DateTime.Now }
                                )

                            match outcome with
                            | IngestOutcome.Added -> added <- added + 1
                            | _ -> unchanged <- unchanged + 1
                        | None -> ()

                    return Ok { Imported = added; Updated = 0; Skipped = unchanged; Message = None }
            }

        OAuthConnector(
            { Metadata =
                { Key = "outlook"
                  Name = "Outlook"
                  Category = ConnectorCategory.Communication
                  Description = "Imports recent Outlook email into the inbox for triage."
                  Icon = "📧"
                  Auth = AuthKind.OAuth
                  Fields = clientFields
                  SupportsSync = true }
              Scopes = [ "offline_access"; "User.Read"; "Mail.Read" ]
              Endpoints = endpoints
              ExtraAuthParams = []
              UsesClientSecret = false
              Test = testMe http
              Sync = sync },
            secrets,
            connections,
            sink,
            http
        )
        :> IConnector

    let outlookCalendar (secrets, connections, sink, http: HttpClient) : IConnector =
        let sync token (sink: IIngestionSink) =
            async {
                let fmt (d: DateTime) = d.ToString "yyyy-MM-dd'T'HH:mm:ss"

                let url =
                    sprintf
                        "https://graph.microsoft.com/v1.0/me/calendarView?startDateTime=%s&endDateTime=%s&$top=100&$select=id,subject,location,start,end,isAllDay"
                        (fmt (DateTime.UtcNow.AddDays -1.0))
                        (fmt (DateTime.UtcNow.AddDays 60.0))

                let! result = Http.getJson http (bearer token) url

                match result with
                | Error e -> return Error e
                | Ok doc ->
                    use doc = doc
                    let mutable added = 0
                    let mutable updated = 0
                    let mutable unchanged = 0

                    for item in Http.propArray "value" doc.RootElement do
                        let readTime name =
                            Http.prop name item
                            |> Option.bind (Http.propStr "dateTime")
                            |> Option.bind Http.parseIsoDate

                        match Http.propStr "id" item, readTime "start", readTime "end" with
                        | Some id, Some start, Some finish ->
                            let allDay =
                                Http.prop "isAllDay" item
                                |> Option.map (fun v -> v.GetBoolean())
                                |> Option.defaultValue false

                            let! outcome =
                                sink.UpsertEvent(
                                    "outlook-calendar",
                                    { ExternalId = id
                                      Title = Http.propStr "subject" item |> Option.defaultValue "(untitled)"
                                      Kind = EventKind.Meeting
                                      StartsAt = start
                                      EndsAt = finish
                                      AllDay = allDay
                                      Location =
                                        Http.prop "location" item
                                        |> Option.bind (Http.propStr "displayName")
                                        |> Option.filter (fun l -> l.Trim().Length > 0)
                                      Notes = None }
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
                { Key = "outlook-calendar"
                  Name = "Outlook Calendar"
                  Category = ConnectorCategory.Calendar
                  Description = "Keeps your Outlook calendar events on the University OS calendar."
                  Icon = "🗓"
                  Auth = AuthKind.OAuth
                  Fields = clientFields
                  SupportsSync = true }
              Scopes = [ "offline_access"; "User.Read"; "Calendars.Read" ]
              Endpoints = endpoints
              ExtraAuthParams = []
              UsesClientSecret = false
              Test = testMe http
              Sync = sync },
            secrets,
            connections,
            sink,
            http
        )
        :> IConnector

    let oneDrive (secrets, connections, sink, http: HttpClient) : IConnector =
        let sync token (sink: IIngestionSink) =
            async {
                let! result =
                    Http.getJson http (bearer token) "https://graph.microsoft.com/v1.0/me/drive/recent?$top=20"

                match result with
                | Error e -> return Error e
                | Ok doc ->
                    use doc = doc
                    let mutable added = 0
                    let mutable unchanged = 0

                    for item in Http.propArray "value" doc.RootElement do
                        match Http.propStr "id" item, Http.propStr "name" item with
                        | Some id, Some name ->
                            let! outcome =
                                sink.AddInboxItem(
                                    "onedrive",
                                    { ExternalId = id
                                      Title = "OneDrive: " + name
                                      Body = Http.propStr "webUrl" item |> Option.defaultValue ""
                                      ReceivedAt =
                                        Http.propStr "lastModifiedDateTime" item
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
                { Key = "onedrive"
                  Name = "OneDrive"
                  Category = ConnectorCategory.Storage
                  Description = "Surfaces recently modified OneDrive files in the inbox."
                  Icon = "☁"
                  Auth = AuthKind.OAuth
                  Fields = clientFields
                  SupportsSync = true }
              Scopes = [ "offline_access"; "User.Read"; "Files.Read" ]
              Endpoints = endpoints
              ExtraAuthParams = []
              UsesClientSecret = false
              Test = testMe http
              Sync = sync },
            secrets,
            connections,
            sink,
            http
        )
        :> IConnector
