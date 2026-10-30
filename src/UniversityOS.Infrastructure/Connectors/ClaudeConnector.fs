namespace UniversityOS.Infrastructure.Connectors

open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.Infrastructure

/// The AI-provider connection. Not a data source — Sync is a no-op — but managed
/// through the same connector surface so all external services live in one place.
type ClaudeConnector(secrets: ISecretStore, provider: IAiProvider) =

    let metadata =
        { Key = "claude"
          Name = "Claude"
          Category = ConnectorCategory.Ai
          Description = "Powers the assistant, capture classification, summaries and flashcards."
          Icon = "✳"
          Auth = AuthKind.ApiKey
          Fields =
            [ { Key = "api_key"
                Label = "Anthropic API key"
                Secret = true
                Placeholder = "sk-ant-…"
                Help = "Create a key at console.anthropic.com → API keys. Stored encrypted on this machine." } ]
          SupportsSync = false }

    interface IConnector with

        member _.Metadata = metadata

        member _.Configure(values) =
            async {
                match values.TryFind "api_key" with
                | None
                | Some "" -> return Error "An API key is required."
                | Some key ->
                    do! secrets.Set(ClaudeProvider.ApiKeySecretName, key.Trim())

                    // Verify the key actually works before reporting success.
                    let! test = provider.Complete(AiRequest.simple None "Reply with the single word: OK")

                    match test with
                    | Ok _ -> return Ok "Claude is connected — the AI assistant is now available."
                    | Error e ->
                        do! secrets.Delete ClaudeProvider.ApiKeySecretName
                        return Error(AiError.describe e)
            }

        member _.Test() =
            async {
                let! result = provider.Complete(AiRequest.simple None "Reply with the single word: OK")

                match result with
                | Ok response -> return Ok(sprintf "Working — responded via %s." response.Model)
                | Error e -> return Error(AiError.describe e)
            }

        member _.Sync() =
            async { return Ok { SyncOutcome.empty with Message = Some "Claude is an AI provider — nothing to sync." } }

        member _.Disconnect() = secrets.Delete ClaudeProvider.ApiKeySecretName
