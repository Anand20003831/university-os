namespace UniversityOS.Infrastructure.Connectors

open System
open System.Net.Http
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.Infrastructure

/// Moodle via its mobile web-service REST API. Requires a "Moodle mobile web
/// service" token, which most institutions expose under
/// Preferences → User account → Security keys.
type MoodleConnector(secrets: ISecretStore, connections: IConnectionRepository, sink: IIngestionSink, http: HttpClient) =

    let key = "moodle"
    let tokenSecret = key + ":token"

    let metadata =
        { Key = key
          Name = "Moodle"
          Category = ConnectorCategory.Academic
          Description = "Imports upcoming Moodle calendar events and deadlines."
          Icon = "🎓"
          Auth = AuthKind.AccessToken
          Fields =
            [ { Key = "base_url"
                Label = "Moodle URL"
                Secret = false
                Placeholder = "https://moodle.university.ac.uk"
                Help = "The root address of your institution's Moodle." }
              { Key = "token"
                Label = "Web service token"
                Secret = true
                Placeholder = ""
                Help = "Preferences → User account → Security keys → 'Moodle mobile web service'." } ]
          SupportsSync = true }

    let baseUrl () =
        async {
            let! state = connections.Get key

            return
                state
                |> Option.bind (fun s -> s.Config.TryFind "base_url")
                |> Option.map (fun u -> u.TrimEnd '/')
        }

    let call (wsFunction: string) (parameters: (string * string) list) =
        async {
            let! url = baseUrl ()
            let! token = secrets.Get tokenSecret

            match url, token with
            | None, _ -> return Error "Moodle URL is not configured."
            | _, None -> return Error "Moodle token is not configured."
            | Some url, Some token ->
                let query =
                    [ "wstoken", token; "wsfunction", wsFunction; "moodlewsrestformat", "json" ] @ parameters
                    |> List.map (fun (k, v) -> sprintf "%s=%s" k (Uri.EscapeDataString v))
                    |> String.concat "&"

                let! result = Http.getJson http [] (sprintf "%s/webservice/rest/server.php?%s" url query)

                match result with
                | Error e -> return Error e
                | Ok doc ->
                    // Moodle reports errors as 200 + an exception object.
                    match Http.propStr "exception" doc.RootElement with
                    | Some _ ->
                        let message =
                            Http.propStr "message" doc.RootElement |> Option.defaultValue "Moodle rejected the request."

                        doc.Dispose()
                        return Error message
                    | None -> return Ok doc
        }

    let siteInfo () =
        async {
            let! result = call "core_webservice_get_site_info" []

            match result with
            | Error e -> return Error e
            | Ok doc ->
                use doc = doc
                let root = doc.RootElement

                return
                    Ok(
                        Http.propStr "fullname" root |> Option.defaultValue "unknown user",
                        Http.propStr "sitename" root |> Option.defaultValue "Moodle"
                    )
        }

    interface IConnector with

        member _.Metadata = metadata

        member _.Configure(values) =
            async {
                match values.TryFind "token" with
                | Some token when token.Trim().Length > 0 -> do! secrets.Set(tokenSecret, token.Trim())
                | _ -> ()

                // base_url is persisted by the registry (non-secret config); verify now.
                let! info = siteInfo ()

                match info with
                | Ok(user, site) -> return Ok(sprintf "Connected to %s as %s." site user)
                | Error e -> return Error e
            }

        member _.Test() =
            async {
                let! info = siteInfo ()

                match info with
                | Ok(user, site) -> return Ok(sprintf "Connected to %s as %s." site user)
                | Error e -> return Error e
            }

        member _.Sync() =
            async {
                let! result = call "core_calendar_get_calendar_upcoming_view" []

                match result with
                | Error e -> return Error e
                | Ok doc ->
                    use doc = doc
                    let events = Http.propArray "events" doc.RootElement
                    let mutable added = 0
                    let mutable updated = 0
                    let mutable unchanged = 0

                    for ev in events do
                        match Http.propInt64 "id" ev, Http.propStr "name" ev, Http.propInt64 "timestart" ev with
                        | Some id, Some name, Some timestart ->
                            let start = DateTimeOffset.FromUnixTimeSeconds(timestart).LocalDateTime
                            let duration = Http.propInt64 "timeduration" ev |> Option.defaultValue 0L

                            let kind =
                                match Http.propStr "eventtype" ev with
                                | Some "due" -> EventKind.Deadline
                                | Some "course" -> EventKind.Lecture
                                | _ -> EventKind.Other

                            let ingested =
                                { ExternalId = string id
                                  Title = name
                                  Kind = kind
                                  StartsAt = start
                                  EndsAt = start.AddSeconds(float (max 0L duration))
                                  AllDay = duration = 0L && start.TimeOfDay = TimeSpan.Zero
                                  Location = None
                                  Notes =
                                    Http.prop "course" ev
                                    |> Option.bind (Http.propStr "fullname") }

                            let! outcome = sink.UpsertEvent(key, ingested)

                            match outcome with
                            | IngestOutcome.Added -> added <- added + 1
                            | IngestOutcome.Updated -> updated <- updated + 1
                            | IngestOutcome.Unchanged -> unchanged <- unchanged + 1
                        | _ -> ()

                    return
                        Ok
                            { Imported = added
                              Updated = updated
                              Skipped = unchanged
                              Message = None }
            }

        member _.Disconnect() = secrets.Delete tokenSecret
