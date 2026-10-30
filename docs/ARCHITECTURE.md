# University OS — Architecture

This document is the reference for every structural decision in the codebase. Read it before
adding a feature, a table, a connector, or a view.

## 1. Technology decisions

| Concern | Choice | Rationale |
|---|---|---|
| Language | F# (.NET 10 LTS) | Strong typing, exhaustive pattern matching, immutability by default |
| Desktop UI | Avalonia 11 + Avalonia.FuncUI | Native cross-platform desktop, declarative view DSL |
| UI architecture | Elmish MVU | Single immutable model, pure update functions, testable state machines |
| Structured storage | SQLite (Microsoft.Data.Sqlite) | Offline-first, zero-config, FTS5 full-text search built in |
| Documents | Managed local file store under `%APPDATA%\UniversityOS\files` | Offline-first, user owns the data |
| Secrets | Windows DPAPI (`ProtectedData`, per-user scope) | API keys/tokens never stored in plain text |
| AI | Provider abstraction, Claude (Anthropic Messages API) as first provider | Future OpenAI/local models without touching call-sites |
| Serialization | System.Text.Json + FSharp.SystemTextJson | Clean records/options/DU handling |

> A note on "Fable": Fable is the F#→JavaScript compiler and does not apply to a native
> desktop UI. The requirement's own preference — Avalonia with Elmish MVU patterns — is what
> this codebase implements, in modern idiomatic F#.

## 2. Layering (clean architecture)

Dependencies point strictly downward. The Domain knows nothing about SQLite, Avalonia or HTTP.

```
┌────────────────────────────────────────────────────┐
│  UniversityOS.App          (Presentation, Elmish)  │
│    Shell, feature views, theme, components         │
├────────────────────────────────────────────────────┤
│  UniversityOS.Infrastructure                       │
│    SQLite repos, FTS5, file store, text extraction,│
│    DPAPI secrets, Claude provider, connectors      │
├────────────────────────────────────────────────────┤
│  UniversityOS.Application   (Use cases)            │
│    Repository/service interfaces, AI abstraction,  │
│    connector abstraction, planner/capture/dashboard│
│    orchestration services                          │
├────────────────────────────────────────────────────┤
│  UniversityOS.Domain        (Pure)                 │
│    Entities, value types, the scheduling algorithm │
└────────────────────────────────────────────────────┘
```

- **Domain** — records, discriminated unions, and pure functions only. The planner algorithm
  (`Planning.fs`) lives here because it is pure business logic: `plan : PlannerInput -> PlannerOutput`.
- **Application** — interfaces (`IAssignmentRepository`, `IAiProvider`, `IConnector`, …) and
  use-case services that orchestrate them (`CaptureService`, `PlannerService`,
  `DashboardService`, `AiService`). No I/O implementations here.
- **Infrastructure** — every implementation detail: SQL, HTTP, file system, cryptography.
- **App** — composition root (`Program.fs`) wires concrete implementations into an
  `AppServices` record which is closed over by Elmish `init`/`update`. Views never touch
  infrastructure types directly.

## 3. MVU shape

One Elmish program hosts the whole shell. Each feature is a self-contained module with its own
`Model`, `Msg`, `init`, `update`, `view`; the shell composes them and owns cross-cutting UI
(navigation, command palette, capture overlay, toasts).

- Cross-feature freshness is handled by **reload-on-navigate**: switching to a page dispatches
  that page's load command. This avoids a fragile event bus while data volumes are small.
- All I/O runs through `Cmd.OfAsync.either` — `update` stays pure.
- Long-running work (sync, AI calls) reports back via messages; failures surface as toasts,
  never silent.

## 4. Data model

SQLite database at `%APPDATA%\UniversityOS\universityos.db`, versioned migrations applied at
startup (`schema_migrations` table). Dates are stored as ISO-8601 strings (sortable). IDs are
GUIDs stored as TEXT, wrapped in single-case unions in the Domain (`AssignmentId of Guid`) so
they cannot be confused.

Core tables: `modules`, `assignments`, `checklist_items`, `events`, `notes`, `documents`,
`lectures`, `inbox_items`, `planned_sessions`, `links`, `conversations`, `chat_messages`,
`connections`, `settings`, `revision_items`, `sync_log`, and the FTS5 virtual table
`search_index(entity_kind, entity_id, title, body)`.

**Connected knowledge:** the `links` table stores typed edges between any two entities
(`EntityRef = kind + id`). Repositories update `search_index` on every write, so global search
is always current.

## 5. Capture pipeline

Everything the user throws at the app (text, files, emails, connector items) enters one
pipeline:

```
input ──► InboxItem (status New)
              │
              ▼
   CaptureAnalysis  ◄── AI classifier (Claude) when configured,
              │          heuristic classifier (dates, module codes) otherwise
              ▼
   ProposedAction list  (CreateAssignment | UpdateDeadline | CreateEvent |
              │          CreateNote | LinkToModule | …) stored on the item
              ▼
   user applies / dismisses in Inbox  ──► entities created + linked + indexed
```

Actions are *proposed*, then *applied* — the AI never silently mutates the user's data unless
the auto-apply setting is enabled.

## 6. AI architecture

- `IAiProvider` — transport-level: `Complete : AiRequest -> Async<Result<AiResponse, AiError>>`.
  `ClaudeProvider` is the first implementation (Anthropic Messages API). Adding OpenAI or a
  local model is one new class + one registry entry.
- `AiService` (Application) — the brain. Assembles application context (modules, deadlines,
  schedule, recent notes) into the system prompt, exposes use-cases: chat, summarize,
  classify capture, generate flashcards/revision questions, explain plans. Structured outputs
  are requested as JSON and parsed defensively.
- The provider is *optional*: every AI feature degrades gracefully (heuristics or a clear
  "connect Claude in Connections" hint) so the app is fully usable offline.

## 7. Connector architecture

Every external service implements one interface (`IConnector`): metadata (category, auth kind,
config fields), `Configure`, `TestConnection`, `Sync`, `Disconnect`. Connectors write through
an `IIngestionSink` — they cannot touch repositories directly, which keeps providers decoupled
from storage and makes every import flow through the capture pipeline.

- Secrets go to `ISecretStore` (DPAPI); non-secret config to the `connections` table.
- OAuth connectors (Google, Microsoft) share a loopback-redirect OAuth2+PKCE helper.
- Token/API-key connectors: Moodle (web-service token), GitLab (PAT), Claude (API key).
- `ConnectorRegistry` owns instances, persists status, runs manual + background sync, and
  records every run in `sync_log`.

The UI's Connections page is generated from connector metadata — adding a connector requires
zero UI changes.

## 8. Planner algorithm (Domain/Planning.fs)

Pure, deterministic, unit-tested. Inputs: open assignments (deadline, estimated vs actual
hours, priority, weighting, progress), fixed calendar events, and preferences (workday window,
session length, break length, daily study cap). It:

1. Scores each assignment: urgency (time to deadline vs remaining effort) × importance
   (priority, weighting).
2. Walks each day in the horizon, carving free slots between fixed events inside the workday
   window.
3. Fills slots greedily with the highest-scored assignment, in sessions of the preferred
   length, inserting breaks, respecting the daily cap and a pre-deadline buffer.
4. Emits `PlannedSession`s each carrying a human-readable *reason* ("due in 2 days, 6h
   remaining, worth 40%") — the UI shows the why, not just the what.

## 9. Conventions

- F# compilation order in each `.fsproj` is the dependency order — keep it curated.
- No nulls cross a layer boundary: options and Results only.
- Repository methods are `Async` and total (Result or option for fallible lookups).
- UI text formatting helpers live in `App/Format.fs`; views never format dates ad hoc.
- New entity checklist: domain record → migration → repository → search indexing → links
  support → feature view → (optional) capture action.
