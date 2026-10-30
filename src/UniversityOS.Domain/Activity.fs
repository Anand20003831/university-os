namespace UniversityOS.Domain

open System

/// One entry in the academic timeline — everything that happens inside
/// University OS becomes an activity: imports, plan changes, completed sessions,
/// AI actions, connector syncs. Feeds the Timeline, Morning Briefing and
/// Evening Review.
[<RequireQualifiedAccess>]
type ActivityKind =
    | AssignmentCreated
    | AssignmentUpdated
    | DeadlineChanged
    | NoteCreated
    | NoteUpdated
    | DocumentImported
    | LectureAdded
    | FlashcardsGenerated
    | SessionCompleted
    | PlanGenerated
    | CaptureApplied
    | ConnectorSynced
    | EventAdded
    | AiAction
    | ModuleUpdated

[<RequireQualifiedAccess>]
module ActivityKind =

    let all =
        [ ActivityKind.AssignmentCreated
          ActivityKind.AssignmentUpdated
          ActivityKind.DeadlineChanged
          ActivityKind.NoteCreated
          ActivityKind.NoteUpdated
          ActivityKind.DocumentImported
          ActivityKind.LectureAdded
          ActivityKind.FlashcardsGenerated
          ActivityKind.SessionCompleted
          ActivityKind.PlanGenerated
          ActivityKind.CaptureApplied
          ActivityKind.ConnectorSynced
          ActivityKind.EventAdded
          ActivityKind.AiAction
          ActivityKind.ModuleUpdated ]

    let toString =
        function
        | ActivityKind.AssignmentCreated -> "assignment-created"
        | ActivityKind.AssignmentUpdated -> "assignment-updated"
        | ActivityKind.DeadlineChanged -> "deadline-changed"
        | ActivityKind.NoteCreated -> "note-created"
        | ActivityKind.NoteUpdated -> "note-updated"
        | ActivityKind.DocumentImported -> "document-imported"
        | ActivityKind.LectureAdded -> "lecture-added"
        | ActivityKind.FlashcardsGenerated -> "flashcards-generated"
        | ActivityKind.SessionCompleted -> "session-completed"
        | ActivityKind.PlanGenerated -> "plan-generated"
        | ActivityKind.CaptureApplied -> "capture-applied"
        | ActivityKind.ConnectorSynced -> "connector-synced"
        | ActivityKind.EventAdded -> "event-added"
        | ActivityKind.AiAction -> "ai-action"
        | ActivityKind.ModuleUpdated -> "module-updated"

    let parse (s: string) =
        all |> List.tryFind (fun k -> toString k = s)

    let label =
        function
        | ActivityKind.AssignmentCreated -> "Assignment created"
        | ActivityKind.AssignmentUpdated -> "Assignment updated"
        | ActivityKind.DeadlineChanged -> "Deadline changed"
        | ActivityKind.NoteCreated -> "Note created"
        | ActivityKind.NoteUpdated -> "Note updated"
        | ActivityKind.DocumentImported -> "Document imported"
        | ActivityKind.LectureAdded -> "Lecture added"
        | ActivityKind.FlashcardsGenerated -> "Flashcards generated"
        | ActivityKind.SessionCompleted -> "Study session completed"
        | ActivityKind.PlanGenerated -> "Plan generated"
        | ActivityKind.CaptureApplied -> "Capture applied"
        | ActivityKind.ConnectorSynced -> "Connector synced"
        | ActivityKind.EventAdded -> "Event added"
        | ActivityKind.AiAction -> "AI action"
        | ActivityKind.ModuleUpdated -> "Module updated"

type ActivityEntry =
    { Id: Guid
      Kind: ActivityKind
      Title: string
      Detail: string option
      /// The entity this activity refers to, for jump-to navigation.
      Entity: EntityRef option
      OccurredAt: DateTime }

[<RequireQualifiedAccess>]
module ActivityEntry =

    let create (now: DateTime) (kind: ActivityKind) (title: string) (detail: string option) (entity: EntityRef option) =
        { Id = Guid.NewGuid()
          Kind = kind
          Title = title
          Detail = detail
          Entity = entity
          OccurredAt = now }
