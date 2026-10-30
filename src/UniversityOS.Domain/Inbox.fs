namespace UniversityOS.Domain

open System

/// Where a captured item came from.
[<RequireQualifiedAccess>]
type CaptureSource =
    | ManualText
    | File of fileName: string
    | Connector of key: string

[<RequireQualifiedAccess>]
module CaptureSource =
    let toString =
        function
        | CaptureSource.ManualText -> "manual"
        | CaptureSource.File name -> "file:" + name
        | CaptureSource.Connector key -> "connector:" + key

    let parse (s: string) =
        if s = "manual" then CaptureSource.ManualText
        elif s.StartsWith "file:" then CaptureSource.File(s.Substring 5)
        elif s.StartsWith "connector:" then CaptureSource.Connector(s.Substring 10)
        else CaptureSource.ManualText

    let label =
        function
        | CaptureSource.ManualText -> "Capture"
        | CaptureSource.File name -> name
        | CaptureSource.Connector key -> key

[<RequireQualifiedAccess>]
type InboxStatus =
    | New
    | Analyzed
    | Applied
    | Dismissed

[<RequireQualifiedAccess>]
module InboxStatus =
    let toInt =
        function
        | InboxStatus.New -> 0
        | InboxStatus.Analyzed -> 1
        | InboxStatus.Applied -> 2
        | InboxStatus.Dismissed -> 3

    let ofInt =
        function
        | 1 -> InboxStatus.Analyzed
        | 2 -> InboxStatus.Applied
        | 3 -> InboxStatus.Dismissed
        | _ -> InboxStatus.New

[<RequireQualifiedAccess>]
type ProposedActionKind =
    | CreateAssignment
    | UpdateDeadline
    | CreateEvent
    | CreateNote
    | LinkToModule
    | NoAction

[<RequireQualifiedAccess>]
module ProposedActionKind =
    let toString =
        function
        | ProposedActionKind.CreateAssignment -> "create_assignment"
        | ProposedActionKind.UpdateDeadline -> "update_deadline"
        | ProposedActionKind.CreateEvent -> "create_event"
        | ProposedActionKind.CreateNote -> "create_note"
        | ProposedActionKind.LinkToModule -> "link_to_module"
        | ProposedActionKind.NoAction -> "no_action"

    let parse =
        function
        | "create_assignment" -> Some ProposedActionKind.CreateAssignment
        | "update_deadline" -> Some ProposedActionKind.UpdateDeadline
        | "create_event" -> Some ProposedActionKind.CreateEvent
        | "create_note" -> Some ProposedActionKind.CreateNote
        | "link_to_module" -> Some ProposedActionKind.LinkToModule
        | "no_action" -> Some ProposedActionKind.NoAction
        | _ -> None

/// An action the capture pipeline proposes. Proposed, never silently applied:
/// the user (or the auto-apply setting) decides.
type ProposedAction =
    { Kind: ProposedActionKind
      Title: string
      ModuleCode: string option
      Date: DateTime option
      DurationMinutes: int option
      Body: string option
      /// Existing entity this action targets (e.g. the assignment whose deadline moves).
      TargetId: string option }

type CaptureAnalysis =
    { Summary: string
      SuggestedModuleCode: string option
      Actions: ProposedAction list
      /// "ai" or "heuristic" — shown in the UI so the user knows what judged the item.
      Analyzer: string }

/// A unit of incoming information awaiting triage, whatever its origin.
type InboxItem =
    { Id: InboxItemId
      Source: CaptureSource
      /// Id in the source system for connector imports; used to deduplicate on re-sync.
      ExternalId: string option
      Title: string
      Body: string
      Status: InboxStatus
      Analysis: CaptureAnalysis option
      /// Set when the capture was a file import.
      DocumentId: DocumentId option
      ReceivedAt: DateTime
      ProcessedAt: DateTime option }

[<RequireQualifiedAccess>]
module InboxItem =

    let create (now: DateTime) (source: CaptureSource) (title: string) (body: string) =
        { Id = InboxItemId.New()
          Source = source
          ExternalId = None
          Title = title.Trim()
          Body = body
          Status = InboxStatus.New
          Analysis = None
          DocumentId = None
          ReceivedAt = now
          ProcessedAt = None }
