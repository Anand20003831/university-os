namespace UniversityOS.Domain

open System

/// Where a memory observation came from. The distinction is user-facing: stats
/// are recomputed deterministically, AI observations come from reflection, user
/// entries are typed in by hand.
[<RequireQualifiedAccess>]
type MemorySource =
    | Stats
    | Ai
    | User

[<RequireQualifiedAccess>]
module MemorySource =
    let toString =
        function
        | MemorySource.Stats -> "stats"
        | MemorySource.Ai -> "ai"
        | MemorySource.User -> "user"

    let parse =
        function
        | "stats" -> MemorySource.Stats
        | "ai" -> MemorySource.Ai
        | _ -> MemorySource.User

    let label =
        function
        | MemorySource.Stats -> "Computed"
        | MemorySource.Ai -> "AI observation"
        | MemorySource.User -> "Added by you"

/// One transparent piece of long-term learning about the user's habits.
/// Always visible, editable, deletable and disableable — never a hidden profile.
type MemoryItem =
    { Id: Guid
      /// Loose grouping, e.g. "pace", "schedule", "habits".
      Category: string
      Content: string
      Source: MemorySource
      Enabled: bool
      UpdatedAt: DateTime }

[<RequireQualifiedAccess>]
module MemoryItem =

    let create (now: DateTime) (source: MemorySource) (category: string) (content: string) =
        { Id = Guid.NewGuid()
          Category = category
          Content = content
          Source = source
          Enabled = true
          UpdatedAt = now }
