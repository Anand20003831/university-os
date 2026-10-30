namespace UniversityOS.Infrastructure

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open UniversityOS.Application

/// Claude (Anthropic Messages API) — the first IAiProvider implementation.
/// Raw HTTP: there is no official F# SDK, and the wire protocol is stable.
///
/// Notes that matter here:
///  - `x-api-key` + `anthropic-version: 2023-06-01` headers on every request.
///  - No sampling parameters — current Claude models reject `temperature` etc.
///  - A refusal is HTTP 200 with `stop_reason: "refusal"` — must be handled.
type ClaudeProvider(secrets: ISecretStore, http: HttpClient) =

    static let ApiKeySecret = "claude:api_key"
    static let Endpoint = "https://api.anthropic.com/v1/messages"
    static let DefaultModel = "claude-opus-4-8"

    static member ApiKeySecretName = ApiKeySecret

    member private _.BuildBody(request: AiRequest) : string =
        let body = JsonObject()
        body.["model"] <- JsonValue.Create(request.Model |> Option.defaultValue DefaultModel)
        body.["max_tokens"] <- JsonValue.Create request.MaxTokens

        match request.System with
        | Some system -> body.["system"] <- JsonValue.Create system
        | None -> ()

        let messages = JsonArray()

        for message in request.Messages do
            let m = JsonObject()

            m.["role"] <-
                JsonValue.Create(
                    match message.Role with
                    | AiRole.User -> "user"
                    | AiRole.Assistant -> "assistant"
                )

            m.["content"] <- JsonValue.Create message.Content
            messages.Add m

        body.["messages"] <- messages
        body.ToJsonString()

    member private _.ParseSuccess(json: string) : Result<AiResponse, AiError> =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement

            let stopReason =
                match root.TryGetProperty "stop_reason" with
                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                | _ -> ""

            if stopReason = "refusal" then
                Error(AiError.Provider "The model declined to answer this request.")
            else
                let text =
                    match root.TryGetProperty "content" with
                    | true, content when content.ValueKind = JsonValueKind.Array ->
                        content.EnumerateArray()
                        |> Seq.filter (fun block ->
                            match block.TryGetProperty "type" with
                            | true, t -> t.GetString() = "text"
                            | _ -> false)
                        |> Seq.map (fun block -> block.GetProperty("text").GetString())
                        |> String.concat ""
                    | _ -> ""

                let model =
                    match root.TryGetProperty "model" with
                    | true, v -> v.GetString()
                    | _ -> DefaultModel

                let usage =
                    match root.TryGetProperty "usage" with
                    | true, u ->
                        let get name =
                            match u.TryGetProperty(name: string) with
                            | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
                            | _ -> 0

                        Some
                            { InputTokens = get "input_tokens"
                              OutputTokens = get "output_tokens" }
                    | _ -> None

                Ok
                    { Text = text
                      Model = model
                      Usage = usage }
        with ex ->
            Error(AiError.Provider("Could not parse the model response: " + ex.Message))

    member private _.ParseError(status: HttpStatusCode) (json: string) : AiError =
        let message =
            try
                use doc = JsonDocument.Parse json

                match doc.RootElement.TryGetProperty "error" with
                | true, err ->
                    match err.TryGetProperty "message" with
                    | true, m -> m.GetString()
                    | _ -> json
                | _ -> json
            with _ ->
                json

        match int status with
        | 401
        | 403 -> AiError.Auth message
        | 429 -> AiError.RateLimited message
        | s when s >= 500 -> AiError.Transport(sprintf "Service unavailable (%d): %s" s message)
        | _ -> AiError.Provider message

    interface IAiProvider with

        member _.Name = "Claude"

        member _.IsConfigured() =
            async {
                let! key = secrets.Get ApiKeySecret
                return key |> Option.exists (fun k -> k.Trim().Length > 0)
            }

        member this.Complete(request) =
            async {
                let! key = secrets.Get ApiKeySecret

                match key with
                | None -> return Error AiError.NotConfigured
                | Some apiKey ->
                    try
                        use msg = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                        msg.Headers.Add("x-api-key", apiKey.Trim())
                        msg.Headers.Add("anthropic-version", "2023-06-01")
                        msg.Content <- new StringContent(this.BuildBody request, Encoding.UTF8, "application/json")

                        let! response = http.SendAsync msg |> Async.AwaitTask
                        let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                        if response.IsSuccessStatusCode then
                            return this.ParseSuccess body
                        else
                            return Error(this.ParseError response.StatusCode body)
                    with
                    | :? HttpRequestException as ex -> return Error(AiError.Transport ex.Message)
                    | :? TaskCanceledException -> return Error(AiError.Transport "The request timed out.")
                    | ex -> return Error(AiError.Transport ex.Message)
            }
