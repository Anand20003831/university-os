namespace UniversityOS.Domain

open System

/// A typed edge in the knowledge graph connecting any two entities:
/// assignment ↔ lecture, note ↔ document, module ↔ repository, …
type Link =
    { Id: LinkId
      From: EntityRef
      To: EntityRef
      Label: string option
      CreatedAt: DateTime }

[<RequireQualifiedAccess>]
module Link =

    let create (now: DateTime) (from: EntityRef) (target: EntityRef) (label: string option) =
        { Id = LinkId.New()
          From = from
          To = target
          Label = label
          CreatedAt = now }

    /// A link is undirected for display purposes: given one endpoint, return the other.
    let otherEnd (entity: EntityRef) (link: Link) =
        if link.From = entity then Some link.To
        elif link.To = entity then Some link.From
        else None
