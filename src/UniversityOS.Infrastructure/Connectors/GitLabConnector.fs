namespace UniversityOS.Infrastructure.Connectors

open System
open System.Net.Http
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.Infrastructure

/// GitLab via a personal access token (scope: read_api). Imports open issues
/// assigned to the user into the inbox, and their due dates into the calendar.
type GitLabConnector(secrets: ISecretStore, connections: IConnectionRepository, sink: IIngestionSink, http: HttpClient) =

    let key = "gitlab"
    let tokenSecret = key + ":token"

    let metadata =
        { Key = key
          Name = "GitLab"
          Category = ConnectorCategory.Development
          Description = "Imports your open issues and their due dates."
          Icon = "🦊"
          Auth = AuthKind.AccessToken
          Fields =
            [ { Key = "base_url"
                Label = "GitLab URL"
                Secret = false
                Placeholder = "https://gitlab.com"
                Help = "Leave as gitlab.com, or your university's self-hosted instance." }
              { Key = "token"
                Label = "Personal access token"
                Secret = true
                Placeholder = "glpat-…"
                Help = "Profile → Access tokens → create with the read_api scope." } ]
          SupportsSync = true }

    let baseUrl () =
        async {
            let! state = connections.Get key

            return
                state
                |> Option.bind (fun s -> s.Config.TryFind "base_url")
                |> Option.filter (fun u -> u.Trim().Length > 0)
                |> Option.defaultValue "https://gitlab.com"
                |> fun u -> u.TrimEnd '/'
        }

    let get (path: string) =
        async {
            let! url = baseUrl ()
            let! token = secrets.Get tokenSecret

            match token with
            | None -> return Error "GitLab token is not configured."
            | Some token -> return! Http.getJson http [ "PRIVATE-TOKEN", token ] (url + path)
        }

    let currentUser () =
        async {
            let! result = get "/api/v4/user"

            match result with
            | Error e -> return Error e
            | Ok doc ->
                use doc = doc
                return Ok(Http.propStr "username" doc.RootElement |> Option.defaultValue "unknown")
        }

    interface IConnector with

        member _.Metadata = metadata

        member _.Configure(values) =
            async {
                match values.TryFind "token" with
                | Some token when token.Trim().Length > 0 -> do! secrets.Set(tokenSecret, token.Trim())
                | _ -> ()

                let! user = currentUser ()

                match user with
                | Ok username -> return Ok(sprintf "Connected as @%s." username)
                | Error e -> return Error e
            }

        member _.Test() =
            async {
                let! user = currentUser ()

                match user with
                | Ok username -> return Ok(sprintf "Connected as @%s." username)
                | Error e -> return Error e
            }

        member _.Sync() =
            async {
                let! result = get "/api/v4/issues?scope=assigned_to_me&state=opened&per_page=50"

                match result with
                | Error e -> return Error e
                | Ok doc ->
                    use doc = doc
                    let mutable added = 0
                    let mutable unchanged = 0

                    for issue in doc.RootElement.EnumerateArray() do
                        match Http.propInt64 "id" issue, Http.propStr "title" issue with
                        | Some id, Some title ->
                            let body =
                                [ Http.propStr "web_url" issue |> Option.defaultValue ""
                                  Http.prop "references" issue
                                  |> Option.bind (Http.propStr "full")
                                  |> Option.defaultValue ""
                                  Http.propStr "description" issue
                                  |> Option.map (fun d -> if d.Length > 800 then d.Substring(0, 800) else d)
                                  |> Option.defaultValue "" ]
                                |> List.filter (fun s -> s.Length > 0)
                                |> String.concat "\n\n"

                            let! outcome =
                                sink.AddInboxItem(
                                    key,
                                    { ExternalId = string id
                                      Title = "GitLab issue: " + title
                                      Body = body
                                      ReceivedAt =
                                        Http.propStr "created_at" issue
                                        |> Option.bind Http.parseIsoDate
                                        |> Option.defaultValue DateTime.Now }
                                )

                            match outcome with
                            | IngestOutcome.Added -> added <- added + 1
                            | _ -> unchanged <- unchanged + 1

                            // Issues with a due date also land on the calendar.
                            match Http.propStr "due_date" issue |> Option.bind Http.parseIsoDate with
                            | Some due ->
                                let! _ =
                                    sink.UpsertEvent(
                                        key,
                                        { ExternalId = sprintf "issue-due-%d" id
                                          Title = "Due: " + title
                                          Kind = EventKind.Deadline
                                          StartsAt = due.Date.AddHours 17.0
                                          EndsAt = due.Date.AddHours 17.5
                                          AllDay = false
                                          Location = None
                                          Notes = Http.propStr "web_url" issue }
                                    )

                                ()
                            | None -> ()
                        | _ -> ()

                    return
                        Ok
                            { Imported = added
                              Updated = 0
                              Skipped = unchanged
                              Message = None }
            }

        member _.Disconnect() = secrets.Delete tokenSecret
