namespace UniversityOS.Domain

open System

/// Strongly-typed identifiers. Wrapping Guids in single-case unions makes it a compile
/// error to pass, say, a NoteId where an AssignmentId is expected.

type ModuleId =
    | ModuleId of Guid
    member this.Value = let (ModuleId v) = this in v
    static member New() = ModuleId(Guid.NewGuid())

type AssignmentId =
    | AssignmentId of Guid
    member this.Value = let (AssignmentId v) = this in v
    static member New() = AssignmentId(Guid.NewGuid())

type ChecklistItemId =
    | ChecklistItemId of Guid
    member this.Value = let (ChecklistItemId v) = this in v
    static member New() = ChecklistItemId(Guid.NewGuid())

type EventId =
    | EventId of Guid
    member this.Value = let (EventId v) = this in v
    static member New() = EventId(Guid.NewGuid())

type NoteId =
    | NoteId of Guid
    member this.Value = let (NoteId v) = this in v
    static member New() = NoteId(Guid.NewGuid())

type DocumentId =
    | DocumentId of Guid
    member this.Value = let (DocumentId v) = this in v
    static member New() = DocumentId(Guid.NewGuid())

type LectureId =
    | LectureId of Guid
    member this.Value = let (LectureId v) = this in v
    static member New() = LectureId(Guid.NewGuid())

type InboxItemId =
    | InboxItemId of Guid
    member this.Value = let (InboxItemId v) = this in v
    static member New() = InboxItemId(Guid.NewGuid())

type SessionId =
    | SessionId of Guid
    member this.Value = let (SessionId v) = this in v
    static member New() = SessionId(Guid.NewGuid())

type ConversationId =
    | ConversationId of Guid
    member this.Value = let (ConversationId v) = this in v
    static member New() = ConversationId(Guid.NewGuid())

type MessageId =
    | MessageId of Guid
    member this.Value = let (MessageId v) = this in v
    static member New() = MessageId(Guid.NewGuid())

type LinkId =
    | LinkId of Guid
    member this.Value = let (LinkId v) = this in v
    static member New() = LinkId(Guid.NewGuid())

type RevisionItemId =
    | RevisionItemId of Guid
    member this.Value = let (RevisionItemId v) = this in v
    static member New() = RevisionItemId(Guid.NewGuid())

type MilestoneId =
    | MilestoneId of Guid
    member this.Value = let (MilestoneId v) = this in v
    static member New() = MilestoneId(Guid.NewGuid())

/// The kinds of entity that participate in the connected knowledge graph and in search.
[<RequireQualifiedAccess>]
type EntityKind =
    | Module
    | Assignment
    | Event
    | Note
    | Document
    | Lecture
    | InboxItem
    | Session
    | Conversation
    | RevisionItem

[<RequireQualifiedAccess>]
module EntityKind =
    let toString =
        function
        | EntityKind.Module -> "module"
        | EntityKind.Assignment -> "assignment"
        | EntityKind.Event -> "event"
        | EntityKind.Note -> "note"
        | EntityKind.Document -> "document"
        | EntityKind.Lecture -> "lecture"
        | EntityKind.InboxItem -> "inbox"
        | EntityKind.Session -> "session"
        | EntityKind.Conversation -> "conversation"
        | EntityKind.RevisionItem -> "revision"

    let parse (s: string) =
        match s with
        | "module" -> Some EntityKind.Module
        | "assignment" -> Some EntityKind.Assignment
        | "event" -> Some EntityKind.Event
        | "note" -> Some EntityKind.Note
        | "document" -> Some EntityKind.Document
        | "lecture" -> Some EntityKind.Lecture
        | "inbox" -> Some EntityKind.InboxItem
        | "session" -> Some EntityKind.Session
        | "conversation" -> Some EntityKind.Conversation
        | "revision" -> Some EntityKind.RevisionItem
        | _ -> None

/// A reference to any entity in the system — the edge endpoint of the knowledge graph.
type EntityRef =
    { Kind: EntityKind
      Id: Guid }

[<RequireQualifiedAccess>]
type Priority =
    | Low
    | Medium
    | High
    | Critical

[<RequireQualifiedAccess>]
module Priority =
    let all = [ Priority.Low; Priority.Medium; Priority.High; Priority.Critical ]

    let rank =
        function
        | Priority.Low -> 0
        | Priority.Medium -> 1
        | Priority.High -> 2
        | Priority.Critical -> 3

    let ofRank =
        function
        | 0 -> Priority.Low
        | 1 -> Priority.Medium
        | 2 -> Priority.High
        | _ -> Priority.Critical

    let label =
        function
        | Priority.Low -> "Low"
        | Priority.Medium -> "Medium"
        | Priority.High -> "High"
        | Priority.Critical -> "Critical"
