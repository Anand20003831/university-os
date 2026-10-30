namespace UniversityOS.Domain

open System

type Conversation =
    { Id: ConversationId
      Title: string
      CreatedAt: DateTime
      UpdatedAt: DateTime }

[<RequireQualifiedAccess>]
type ChatRole =
    | User
    | Assistant

[<RequireQualifiedAccess>]
module ChatRole =
    let toInt = function ChatRole.User -> 0 | ChatRole.Assistant -> 1
    let ofInt = function 1 -> ChatRole.Assistant | _ -> ChatRole.User

type ChatMessage =
    { Id: MessageId
      ConversationId: ConversationId
      Role: ChatRole
      Content: string
      CreatedAt: DateTime }

[<RequireQualifiedAccess>]
module Conversation =

    let create (now: DateTime) (title: string) =
        { Id = ConversationId.New()
          Title = title
          CreatedAt = now
          UpdatedAt = now }

[<RequireQualifiedAccess>]
module ChatMessage =

    let create (now: DateTime) (conversationId: ConversationId) (role: ChatRole) (content: string) =
        { Id = MessageId.New()
          ConversationId = conversationId
          Role = role
          Content = content
          CreatedAt = now }
