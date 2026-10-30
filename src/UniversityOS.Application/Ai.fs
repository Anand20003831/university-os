namespace UniversityOS.Application

/// Transport-level AI abstraction. Providers (Claude today, others tomorrow) implement
/// exactly this; everything above it is provider-agnostic.

[<RequireQualifiedAccess>]
type AiRole =
    | User
    | Assistant

type AiMessage =
    { Role: AiRole
      Content: string }

/// Note: no sampling parameters (temperature/top_p) — current Claude models reject
/// them; behavior is steered through prompting instead.
type AiRequest =
    { System: string option
      Messages: AiMessage list
      MaxTokens: int
      /// Overrides the provider's default model when set.
      Model: string option }

[<RequireQualifiedAccess>]
module AiRequest =
    let simple (system: string option) (user: string) =
        { System = system
          Messages = [ { Role = AiRole.User; Content = user } ]
          MaxTokens = 2048
          Model = None }

type AiUsage =
    { InputTokens: int
      OutputTokens: int }

type AiResponse =
    { Text: string
      Model: string
      Usage: AiUsage option }

[<RequireQualifiedAccess>]
type AiError =
    | NotConfigured
    | Auth of string
    | RateLimited of string
    | Transport of string
    | Provider of string

[<RequireQualifiedAccess>]
module AiError =
    let describe =
        function
        | AiError.NotConfigured ->
            "AI is not configured. Connect Claude in the Connections section to enable the assistant."
        | AiError.Auth msg -> "AI authentication failed: " + msg
        | AiError.RateLimited msg -> "AI rate limit reached: " + msg
        | AiError.Transport msg -> "Could not reach the AI service: " + msg
        | AiError.Provider msg -> "AI provider error: " + msg

type IAiProvider =
    abstract Name: string
    abstract IsConfigured: unit -> Async<bool>
    abstract Complete: request: AiRequest -> Async<Result<AiResponse, AiError>>
