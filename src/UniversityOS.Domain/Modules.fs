namespace UniversityOS.Domain

open System

/// A university module (course unit). The hub every other entity hangs off.
/// Named StudyModule because `Module` collides with the F# keyword in common usage.
type StudyModule =
    { Id: ModuleId
      Code: string
      Name: string
      Lecturer: string option
      Credits: int option
      /// Hex color used to identify the module across the whole UI, e.g. "#7C6FF0".
      Color: string
      Term: string option
      Description: string option
      Archived: bool
      CreatedAt: DateTime
      UpdatedAt: DateTime }

[<RequireQualifiedAccess>]
module StudyModule =

    /// A curated palette assigned round-robin to new modules.
    let palette =
        [ "#7C6FF0"; "#4FA8E8"; "#4FC98F"; "#E8B44F"; "#E86A6A"; "#C86FE0"; "#5FD4C8"; "#E88A4F" ]

    let create (now: DateTime) (code: string) (name: string) (colorIndex: int) =
        { Id = ModuleId.New()
          Code = code.Trim().ToUpperInvariant()
          Name = name.Trim()
          Lecturer = None
          Credits = None
          Color = palette.[abs colorIndex % palette.Length]
          Term = None
          Description = None
          Archived = false
          CreatedAt = now
          UpdatedAt = now }

    let displayName (m: StudyModule) =
        if String.IsNullOrWhiteSpace m.Code then m.Name
        else sprintf "%s · %s" m.Code m.Name
