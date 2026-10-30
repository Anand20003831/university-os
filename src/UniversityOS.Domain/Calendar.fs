namespace UniversityOS.Domain

open System

[<RequireQualifiedAccess>]
type EventKind =
    | Lecture
    | Deadline
    | Meeting
    | Personal
    | StudySession
    | Exam
    | Other

[<RequireQualifiedAccess>]
module EventKind =
    let all =
        [ EventKind.Lecture
          EventKind.Deadline
          EventKind.Meeting
          EventKind.Personal
          EventKind.StudySession
          EventKind.Exam
          EventKind.Other ]

    let label =
        function
        | EventKind.Lecture -> "Lecture"
        | EventKind.Deadline -> "Deadline"
        | EventKind.Meeting -> "Meeting"
        | EventKind.Personal -> "Personal"
        | EventKind.StudySession -> "Study session"
        | EventKind.Exam -> "Exam"
        | EventKind.Other -> "Other"

    let toInt =
        function
        | EventKind.Lecture -> 0
        | EventKind.Deadline -> 1
        | EventKind.Meeting -> 2
        | EventKind.Personal -> 3
        | EventKind.StudySession -> 4
        | EventKind.Exam -> 5
        | EventKind.Other -> 6

    let ofInt =
        function
        | 0 -> EventKind.Lecture
        | 1 -> EventKind.Deadline
        | 2 -> EventKind.Meeting
        | 3 -> EventKind.Personal
        | 4 -> EventKind.StudySession
        | 5 -> EventKind.Exam
        | _ -> EventKind.Other

type CalendarEvent =
    { Id: EventId
      Title: string
      Kind: EventKind
      StartsAt: DateTime
      EndsAt: DateTime
      AllDay: bool
      Location: string option
      ModuleId: ModuleId option
      /// Connector key when the event was imported ("google-calendar", "outlook-calendar", "moodle", …).
      Source: string option
      /// The id in the source system, used to upsert instead of duplicating on re-sync.
      ExternalId: string option
      Notes: string option
      CreatedAt: DateTime
      UpdatedAt: DateTime }

[<RequireQualifiedAccess>]
module CalendarEvent =

    let create (now: DateTime) (title: string) (kind: EventKind) (startsAt: DateTime) (endsAt: DateTime) =
        { Id = EventId.New()
          Title = title.Trim()
          Kind = kind
          StartsAt = startsAt
          EndsAt = endsAt
          AllDay = false
          Location = None
          ModuleId = None
          Source = None
          ExternalId = None
          Notes = None
          CreatedAt = now
          UpdatedAt = now }

    let overlaps (rangeStart: DateTime) (rangeEnd: DateTime) (e: CalendarEvent) =
        e.StartsAt < rangeEnd && e.EndsAt > rangeStart
