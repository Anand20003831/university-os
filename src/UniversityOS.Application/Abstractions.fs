namespace UniversityOS.Application

open System
open UniversityOS.Domain

/// Injectable time source so services and tests never call DateTime.Now directly.
type IClock =
    abstract Now: DateTime

/// Encrypted storage for API keys, tokens and other secrets.
/// Implementations must never write plaintext to disk.
type ISecretStore =
    abstract Set: key: string * value: string -> Async<unit>
    abstract Get: key: string -> Async<string option>
    abstract Delete: key: string -> Async<unit>

// ---------------------------------------------------------------------------
// Repositories. All Async, all total: lookups return options, never throw for
// "not found". Save is an upsert keyed on the entity id.
// ---------------------------------------------------------------------------

type IModuleRepository =
    abstract GetAll: unit -> Async<StudyModule list>
    abstract GetById: id: ModuleId -> Async<StudyModule option>
    abstract Save: entity: StudyModule -> Async<unit>
    abstract Delete: id: ModuleId -> Async<unit>

type IAssignmentRepository =
    abstract GetAll: unit -> Async<Assignment list>
    abstract GetOpen: unit -> Async<Assignment list>
    abstract GetByModule: moduleId: ModuleId -> Async<Assignment list>
    abstract GetById: id: AssignmentId -> Async<Assignment option>
    abstract Save: entity: Assignment -> Async<unit>
    abstract Delete: id: AssignmentId -> Async<unit>

type IEventRepository =
    abstract GetBetween: rangeStart: DateTime * rangeEnd: DateTime -> Async<CalendarEvent list>
    abstract GetById: id: EventId -> Async<CalendarEvent option>
    abstract GetByExternalId: source: string * externalId: string -> Async<CalendarEvent option>
    abstract Save: entity: CalendarEvent -> Async<unit>
    abstract Delete: id: EventId -> Async<unit>

/// The shared work item, read by Assignments, Planner, Modules and Calendar.
///
/// `ReplaceFor` rather than per-row saves: reordering, inserting and deleting
/// all change several rows' `Order` at once, and applying that as a sequence of
/// individual writes leaves the list briefly holding duplicate positions.
type IMilestoneRepository =
    abstract GetAll: unit -> Async<Milestone list>
    abstract GetForAssignment: assignmentId: AssignmentId -> Async<Milestone list>
    abstract GetById: id: MilestoneId -> Async<Milestone option>
    abstract Save: milestone: Milestone -> Async<unit>
    /// Replaces every milestone of one assignment with the given list.
    abstract ReplaceFor: assignmentId: AssignmentId * milestones: Milestone list -> Async<unit>
    abstract Delete: id: MilestoneId -> Async<unit>

type ISessionRepository =
    abstract GetBetween: rangeStart: DateTime * rangeEnd: DateTime -> Async<PlannedSession list>
    /// Replaces every not-yet-completed session starting at or after `from` with the
    /// given sessions. Completed sessions are history and are never touched.
    abstract ReplaceFrom: from: DateTime * sessions: PlannedSession list -> Async<unit>
    abstract SetCompleted: id: SessionId * completed: bool -> Async<unit>

type INoteRepository =
    abstract GetAll: unit -> Async<Note list>
    abstract GetByModule: moduleId: ModuleId -> Async<Note list>
    abstract GetById: id: NoteId -> Async<Note option>
    abstract Save: entity: Note -> Async<unit>
    abstract Delete: id: NoteId -> Async<unit>

type IDocumentRepository =
    abstract GetAll: unit -> Async<Document list>
    abstract GetByModule: moduleId: ModuleId -> Async<Document list>
    abstract GetById: id: DocumentId -> Async<Document option>
    abstract Save: entity: Document -> Async<unit>
    abstract Delete: id: DocumentId -> Async<unit>

type ILectureRepository =
    abstract GetAll: unit -> Async<Lecture list>
    abstract GetByModule: moduleId: ModuleId -> Async<Lecture list>
    abstract GetById: id: LectureId -> Async<Lecture option>
    abstract Save: entity: Lecture -> Async<unit>
    abstract Delete: id: LectureId -> Async<unit>

type IRevisionRepository =
    abstract GetAll: unit -> Async<RevisionItem list>
    abstract GetByModule: moduleId: ModuleId -> Async<RevisionItem list>
    abstract SaveMany: items: RevisionItem list -> Async<unit>
    abstract Delete: id: RevisionItemId -> Async<unit>

type ILinkRepository =
    abstract GetAll: unit -> Async<Link list>
    abstract GetFor: entity: EntityRef -> Async<Link list>
    abstract Save: link: Link -> Async<unit>
    abstract Delete: id: LinkId -> Async<unit>

type IInboxRepository =
    /// Items awaiting user attention (New or Analyzed).
    abstract GetActive: unit -> Async<InboxItem list>
    abstract GetAll: unit -> Async<InboxItem list>
    abstract GetById: id: InboxItemId -> Async<InboxItem option>
    abstract ExistsExternal: source: string * externalId: string -> Async<bool>
    abstract Save: item: InboxItem -> Async<unit>

type IChatRepository =
    abstract GetConversations: unit -> Async<Conversation list>
    abstract GetMessages: conversationId: ConversationId -> Async<ChatMessage list>
    abstract SaveConversation: conversation: Conversation -> Async<unit>
    abstract SaveMessage: message: ChatMessage -> Async<unit>
    abstract DeleteConversation: id: ConversationId -> Async<unit>

type IConnectionRepository =
    abstract GetAll: unit -> Async<ConnectionState list>
    abstract Get: key: string -> Async<ConnectionState option>
    abstract Save: state: ConnectionState -> Async<unit>

type ISyncLogRepository =
    abstract Log: connectorKey: string * startedAt: DateTime * finishedAt: DateTime * outcome: Result<SyncOutcome, string> -> Async<unit>
    abstract GetRecent: limit: int -> Async<(string * DateTime * string) list>

type ISettingsStore =
    abstract Load: unit -> Async<UserSettings>
    abstract Save: settings: UserSettings -> Async<unit>

/// Full-text index over every entity. Repositories keep it current on write;
/// global search queries it.
type ISearchRepository =
    abstract Query: text: string * limit: int -> Async<SearchHit list>
    abstract Index: kind: EntityKind * id: Guid * title: string * body: string -> Async<unit>
    abstract Remove: kind: EntityKind * id: Guid -> Async<unit>

/// Imports an external file into the managed store: copies it, extracts text,
/// persists the Document and indexes it for search.
type IDocumentImportService =
    abstract ImportFile: sourcePath: string * moduleId: ModuleId option -> Async<Result<Document, string>>

/// The academic timeline: services and views record activities, the Timeline /
/// Briefing / Review read them back.
type IActivityLog =
    abstract Log: entry: ActivityEntry -> Async<unit>
    abstract GetRecent: limit: int -> Async<ActivityEntry list>
    abstract GetBetween: rangeStart: DateTime * rangeEnd: DateTime -> Async<ActivityEntry list>

/// Transparent long-term AI memory. Nothing here is hidden from the user.
type IMemoryRepository =
    abstract GetAll: unit -> Async<MemoryItem list>
    abstract Save: item: MemoryItem -> Async<unit>
    abstract Delete: id: Guid -> Async<unit>
    /// Replace-style updates: stats/AI refreshes clear their own source first.
    abstract DeleteBySource: source: MemorySource -> Async<unit>

/// Persisted UI/app state (sidebar mode, briefing dates, memory toggle) —
/// deliberately separate from UserSettings.
type IUiStateStore =
    abstract Load: unit -> Async<UiState>
    abstract Save: state: UiState -> Async<unit>

/// Appearance preferences driving the design-token system. Its own row again,
/// for the same serialization-stability reason as IUiStateStore. Load is total:
/// unreadable or future-versioned records come back as defaults, never an error.
type IAppearanceStore =
    abstract Load: unit -> Async<AppearanceSettings>
    abstract Save: settings: AppearanceSettings -> Async<unit>
