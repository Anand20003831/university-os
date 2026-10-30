namespace UniversityOS.Domain

/// A hit from global search, whatever entity kind it came from.
type SearchHit =
    { Kind: EntityKind
      Id: System.Guid
      Title: string
      Snippet: string
      Score: float }
