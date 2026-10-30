namespace UniversityOS.Domain

open System

/// A Markdown note. Content is always Markdown; the UI renders a live preview.
type Note =
    { Id: NoteId
      ModuleId: ModuleId option
      Title: string
      Content: string
      Tags: string list
      Pinned: bool
      CreatedAt: DateTime
      UpdatedAt: DateTime }

[<RequireQualifiedAccess>]
module Note =

    let create (now: DateTime) (title: string) =
        { Id = NoteId.New()
          ModuleId = None
          Title = title.Trim()
          Content = ""
          Tags = []
          Pinned = false
          CreatedAt = now
          UpdatedAt = now }

    /// First non-heading, non-empty line — used for list previews.
    let preview (n: Note) =
        n.Content.Split('\n')
        |> Array.map (fun l -> l.Trim())
        |> Array.tryFind (fun l -> l.Length > 0 && not (l.StartsWith "#"))
        |> Option.defaultValue ""
