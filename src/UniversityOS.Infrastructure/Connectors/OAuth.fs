namespace UniversityOS.Infrastructure.Connectors

open System
open System.Diagnostics
open System.Net
open System.Security.Cryptography
open System.Net.Http
open System.Text
open System.Threading.Tasks
open UniversityOS.Application
open UniversityOS.Infrastructure

/// A stored OAuth token set. Serialized (encrypted) into the secret store.
type TokenSet =
    { AccessToken: string
      RefreshToken: string option
      ExpiresAt: DateTime }

/// OAuth 2.0 authorization-code flow with PKCE and a loopback redirect:
/// the standard pattern for native desktop apps (no embedded browser, no
/// client secret required for public clients).
[<RequireQualifiedAccess>]
module OAuth =

    let private base64Url (bytes: byte[]) =
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let private generatePkce () =
        let verifier = base64Url (RandomNumberGenerator.GetBytes 32)
        let challenge = base64Url (SHA256.HashData(Encoding.ASCII.GetBytes verifier))
        verifier, challenge

    let private freePort () =
        let listener = new Sockets.TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port
        listener.Stop()
        port

    let private parseTokenResponse (now: DateTime) (doc: Text.Json.JsonDocument) : Result<TokenSet, string> =
        let root = doc.RootElement

        match Http.propStr "access_token" root with
        | None -> Error "The token response contained no access token."
        | Some accessToken ->
            let expiresIn = Http.propInt64 "expires_in" root |> Option.defaultValue 3600L

            Ok
                { AccessToken = accessToken
                  RefreshToken = Http.propStr "refresh_token" root
                  ExpiresAt = now.AddSeconds(float expiresIn) }

    /// Runs the interactive flow: opens the system browser at the provider's
    /// consent screen, catches the redirect on a local HTTP listener, exchanges
    /// the code for tokens. Times out after five minutes if the user walks away.
    let authorize
        (http: HttpClient)
        (authEndpoint: string)
        (tokenEndpoint: string)
        (clientId: string)
        (clientSecret: string option)
        (scopes: string list)
        (extraAuthParams: (string * string) list)
        : Async<Result<TokenSet, string>> =
        async {
            try
                let verifier, challenge = generatePkce ()
                let state = Guid.NewGuid().ToString "N"
                let port = freePort ()
                let redirectUri = sprintf "http://localhost:%d/callback/" port

                let query =
                    [ "response_type", "code"
                      "client_id", clientId
                      "redirect_uri", redirectUri
                      "scope", String.concat " " scopes
                      "state", state
                      "code_challenge", challenge
                      "code_challenge_method", "S256" ]
                    @ extraAuthParams
                    |> List.map (fun (k, v) -> sprintf "%s=%s" k (Uri.EscapeDataString v))
                    |> String.concat "&"

                let authUrl = sprintf "%s?%s" authEndpoint query

                use listener = new HttpListener()
                listener.Prefixes.Add(sprintf "http://localhost:%d/" port)
                listener.Start()

                Process.Start(ProcessStartInfo(FileName = authUrl, UseShellExecute = true)) |> ignore

                let contextTask = listener.GetContextAsync()
                let! completed = Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes 5.0)) |> Async.AwaitTask

                if not (Object.ReferenceEquals(completed, contextTask)) then
                    return Error "Sign-in timed out — no response from the browser within five minutes."
                else
                    let context = contextTask.Result
                    let code = context.Request.QueryString.["code"]
                    let returnedState = context.Request.QueryString.["state"]
                    let errorParam = context.Request.QueryString.["error"]

                    // Always answer the browser, whatever happened.
                    let html =
                        if String.IsNullOrEmpty code then
                            "<html><body style='font-family:sans-serif'><h2>Sign-in failed</h2><p>You can close this window and return to University OS.</p></body></html>"
                        else
                            "<html><body style='font-family:sans-serif'><h2>Connected ✔</h2><p>You can close this window and return to University OS.</p></body></html>"

                    let buffer = Encoding.UTF8.GetBytes html
                    context.Response.ContentType <- "text/html"
                    context.Response.ContentLength64 <- int64 buffer.Length
                    do! context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length) |> Async.AwaitTask
                    context.Response.Close()

                    if not (String.IsNullOrEmpty errorParam) then
                        return Error(sprintf "The provider reported: %s" errorParam)
                    elif String.IsNullOrEmpty code then
                        return Error "No authorization code was returned."
                    elif returnedState <> state then
                        return Error "State mismatch — possible interception; sign-in aborted."
                    else
                        let form =
                            [ "grant_type", "authorization_code"
                              "code", code
                              "redirect_uri", redirectUri
                              "client_id", clientId
                              "code_verifier", verifier ]
                            @ (match clientSecret with
                               | Some s -> [ "client_secret", s ]
                               | None -> [])

                        let! tokenResult = Http.postForm http tokenEndpoint form

                        match tokenResult with
                        | Error e -> return Error("Token exchange failed: " + e)
                        | Ok doc ->
                            use doc = doc
                            return parseTokenResponse DateTime.Now doc
            with ex ->
                return Error(sprintf "Sign-in could not start: %s" ex.Message)
        }

    let refresh
        (http: HttpClient)
        (tokenEndpoint: string)
        (clientId: string)
        (clientSecret: string option)
        (refreshToken: string)
        : Async<Result<TokenSet, string>> =
        async {
            let form =
                [ "grant_type", "refresh_token"
                  "refresh_token", refreshToken
                  "client_id", clientId ]
                @ (match clientSecret with
                   | Some s -> [ "client_secret", s ]
                   | None -> [])

            let! result = Http.postForm http tokenEndpoint form

            match result with
            | Error e -> return Error("Token refresh failed: " + e)
            | Ok doc ->
                use doc = doc

                return
                    parseTokenResponse DateTime.Now doc
                    // Providers often omit the refresh token on refresh — keep the old one.
                    |> Result.map (fun t ->
                        { t with
                            RefreshToken = t.RefreshToken |> Option.orElse (Some refreshToken) })
        }

/// Encrypted persistence for a connector's token set, with automatic refresh.
type TokenStore(secrets: ISecretStore, http: HttpClient) =

    let secretKey (connectorKey: string) = connectorKey + ":tokens"

    member _.Save(connectorKey: string, tokens: TokenSet) =
        secrets.Set(secretKey connectorKey, Json.serialize tokens)

    member _.Load(connectorKey: string) : Async<TokenSet option> =
        async {
            let! stored = secrets.Get(secretKey connectorKey)
            return stored |> Option.bind Json.deserialize<TokenSet>
        }

    member _.Delete(connectorKey: string) = secrets.Delete(secretKey connectorKey)

    /// A valid access token, refreshing transparently when near expiry.
    member this.GetValidAccessToken
        (connectorKey: string, tokenEndpoint: string, clientId: string, clientSecret: string option)
        : Async<Result<string, string>> =
        async {
            let! tokens = this.Load connectorKey

            match tokens with
            | None -> return Error "Not connected — run Connect first."
            | Some t when t.ExpiresAt > DateTime.Now.AddMinutes 2.0 -> return Ok t.AccessToken
            | Some t ->
                match t.RefreshToken with
                | None -> return Error "The access token expired and no refresh token is available — reconnect."
                | Some refreshToken ->
                    let! refreshed = OAuth.refresh http tokenEndpoint clientId clientSecret refreshToken

                    match refreshed with
                    | Error e -> return Error e
                    | Ok newTokens ->
                        do! this.Save(connectorKey, newTokens)
                        return Ok newTokens.AccessToken
        }
