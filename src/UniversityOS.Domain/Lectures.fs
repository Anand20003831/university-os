namespace UniversityOS.Domain

open System

/// A lecture: the unit that ties together slides, a recording, a transcript and notes.
type Lecture =
    { Id: LectureId
      ModuleId: ModuleId option
      Title: string
      Date: DateTime option
      RecordingDocumentId: DocumentId option
      SlidesDocumentId: DocumentId option
      Transcript: string option
      Summary: string option
      CreatedAt: DateTime
      UpdatedAt: DateTime }

[<RequireQualifiedAccess>]
module Lecture =

    let create (now: DateTime) (title: string) =
        { Id = LectureId.New()
          ModuleId = None
          Title = title.Trim()
          Date = None
          RecordingDocumentId = None
          SlidesDocumentId = None
          Transcript = None
          Summary = None
          CreatedAt = now
          UpdatedAt = now }
