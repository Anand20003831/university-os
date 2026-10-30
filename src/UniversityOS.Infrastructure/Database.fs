namespace UniversityOS.Infrastructure

open System
open System.Globalization
open Microsoft.Data.Sqlite

/// Small, explicit data-access helpers over Microsoft.Data.Sqlite.
/// Dates are stored as ISO-8601 strings (sortable), booleans as INTEGER 0/1,
/// GUIDs as TEXT. All conversions live here so repositories stay declarative.
[<RequireQualifiedAccess>]
module Sql =

    [<Literal>]
    let DateFormat = "yyyy-MM-ddTHH:mm:ss.fff"

    let ofDate (d: DateTime) : obj = box (d.ToString(DateFormat, CultureInfo.InvariantCulture))

    let ofDateOpt (d: DateTime option) : obj =
        match d with
        | Some v -> ofDate v
        | None -> box DBNull.Value

    let ofStr (s: string) : obj = box s

    let ofStrOpt (s: string option) : obj =
        match s with
        | Some v -> box v
        | None -> box DBNull.Value

    let ofGuid (g: Guid) : obj = box (g.ToString "N")

    let ofGuidOpt (g: Guid option) : obj =
        match g with
        | Some v -> ofGuid v
        | None -> box DBNull.Value

    let ofBool (b: bool) : obj = box (if b then 1L else 0L)

    let ofInt (i: int) : obj = box (int64 i)

    let ofIntOpt (i: int option) : obj =
        match i with
        | Some v -> box (int64 v)
        | None -> box DBNull.Value

    let ofFloat (f: float) : obj = box f

    let ofFloatOpt (f: float option) : obj =
        match f with
        | Some v -> box v
        | None -> box DBNull.Value

    // ---- readers ------------------------------------------------------------

    let str (r: SqliteDataReader) (name: string) = r.GetString(r.GetOrdinal name)

    let strOpt (r: SqliteDataReader) (name: string) =
        let i = r.GetOrdinal name
        if r.IsDBNull i then None else Some(r.GetString i)

    let int32 (r: SqliteDataReader) (name: string) = int (r.GetInt64(r.GetOrdinal name))

    let int32Opt (r: SqliteDataReader) (name: string) =
        let i = r.GetOrdinal name
        if r.IsDBNull i then None else Some(int (r.GetInt64 i))

    let int64' (r: SqliteDataReader) (name: string) = r.GetInt64(r.GetOrdinal name)

    let float' (r: SqliteDataReader) (name: string) = r.GetDouble(r.GetOrdinal name)

    let floatOpt (r: SqliteDataReader) (name: string) =
        let i = r.GetOrdinal name
        if r.IsDBNull i then None else Some(r.GetDouble i)

    let bool' (r: SqliteDataReader) (name: string) = r.GetInt64(r.GetOrdinal name) <> 0L

    let guid (r: SqliteDataReader) (name: string) = Guid.Parse(str r name)

    let guidOpt (r: SqliteDataReader) (name: string) = strOpt r name |> Option.map Guid.Parse

    let date (r: SqliteDataReader) (name: string) =
        DateTime.Parse(str r name, CultureInfo.InvariantCulture, DateTimeStyles.None)

    let dateOpt (r: SqliteDataReader) (name: string) =
        strOpt r name
        |> Option.map (fun s -> DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.None))

/// Connection factory + command execution. One short-lived pooled connection per
/// operation; SQLite runs in WAL mode so readers never block the writer.
type Database(dbPath: string) =

    let connectionString =
        SqliteConnectionStringBuilder(DataSource = dbPath, Cache = SqliteCacheMode.Shared).ToString()

    member _.OpenConnection() : SqliteConnection =
        let conn = new SqliteConnection(connectionString)
        conn.Open()
        use pragma = conn.CreateCommand()
        pragma.CommandText <- "PRAGMA foreign_keys = ON;"
        pragma.ExecuteNonQuery() |> ignore
        conn

    member this.Execute(sql: string, parameters: (string * obj) list) : Async<unit> =
        async {
            use conn = this.OpenConnection()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sql

            for name, value in parameters do
                cmd.Parameters.AddWithValue(name, value) |> ignore

            cmd.ExecuteNonQuery() |> ignore
        }

    /// Executes several statements atomically.
    member this.ExecuteTransaction(statements: (string * (string * obj) list) list) : Async<unit> =
        async {
            use conn = this.OpenConnection()
            use tx = conn.BeginTransaction()

            for sql, parameters in statements do
                use cmd = conn.CreateCommand()
                cmd.Transaction <- tx
                cmd.CommandText <- sql

                for name, value in parameters do
                    cmd.Parameters.AddWithValue(name, value) |> ignore

                cmd.ExecuteNonQuery() |> ignore

            tx.Commit()
        }

    member this.Query(sql: string, parameters: (string * obj) list, map: SqliteDataReader -> 'T) : Async<'T list> =
        async {
            use conn = this.OpenConnection()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sql

            for name, value in parameters do
                cmd.Parameters.AddWithValue(name, value) |> ignore

            use reader = cmd.ExecuteReader()
            let results = ResizeArray()

            while reader.Read() do
                results.Add(map reader)

            return List.ofSeq results
        }

    member this.QueryOne(sql: string, parameters: (string * obj) list, map: SqliteDataReader -> 'T) : Async<'T option> =
        async {
            let! results = this.Query(sql, parameters, map)
            return List.tryHead results
        }

    member this.Scalar(sql: string, parameters: (string * obj) list) : Async<obj> =
        async {
            use conn = this.OpenConnection()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sql

            for name, value in parameters do
                cmd.Parameters.AddWithValue(name, value) |> ignore

            return cmd.ExecuteScalar()
        }

/// Versioned, forward-only migrations applied at startup.
[<RequireQualifiedAccess>]
module Migrations =

    let private migrations: (int * string) list =
        [ 1,
          """
          CREATE TABLE IF NOT EXISTS modules(
              id TEXT PRIMARY KEY,
              code TEXT NOT NULL,
              name TEXT NOT NULL,
              lecturer TEXT NULL,
              credits INTEGER NULL,
              color TEXT NOT NULL,
              term TEXT NULL,
              description TEXT NULL,
              archived INTEGER NOT NULL DEFAULT 0,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
          );

          CREATE TABLE IF NOT EXISTS assignments(
              id TEXT PRIMARY KEY,
              module_id TEXT NULL,
              title TEXT NOT NULL,
              description TEXT NOT NULL DEFAULT '',
              deadline TEXT NULL,
              weighting REAL NULL,
              priority INTEGER NOT NULL DEFAULT 1,
              status INTEGER NOT NULL DEFAULT 0,
              estimated_hours REAL NULL,
              actual_hours REAL NOT NULL DEFAULT 0,
              progress INTEGER NOT NULL DEFAULT 0,
              submission_url TEXT NULL,
              repository_url TEXT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
          );
          CREATE INDEX IF NOT EXISTS idx_assignments_deadline ON assignments(deadline);
          CREATE INDEX IF NOT EXISTS idx_assignments_module ON assignments(module_id);

          CREATE TABLE IF NOT EXISTS checklist_items(
              id TEXT PRIMARY KEY,
              assignment_id TEXT NOT NULL REFERENCES assignments(id) ON DELETE CASCADE,
              text TEXT NOT NULL,
              done INTEGER NOT NULL DEFAULT 0,
              position INTEGER NOT NULL DEFAULT 0
          );
          CREATE INDEX IF NOT EXISTS idx_checklist_assignment ON checklist_items(assignment_id);

          CREATE TABLE IF NOT EXISTS events(
              id TEXT PRIMARY KEY,
              title TEXT NOT NULL,
              kind INTEGER NOT NULL,
              starts_at TEXT NOT NULL,
              ends_at TEXT NOT NULL,
              all_day INTEGER NOT NULL DEFAULT 0,
              location TEXT NULL,
              module_id TEXT NULL,
              source TEXT NULL,
              external_id TEXT NULL,
              notes TEXT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
          );
          CREATE INDEX IF NOT EXISTS idx_events_starts ON events(starts_at);
          CREATE UNIQUE INDEX IF NOT EXISTS idx_events_external ON events(source, external_id)
              WHERE source IS NOT NULL AND external_id IS NOT NULL;

          CREATE TABLE IF NOT EXISTS planned_sessions(
              id TEXT PRIMARY KEY,
              assignment_id TEXT NULL,
              title TEXT NOT NULL,
              module_code TEXT NULL,
              starts_at TEXT NOT NULL,
              ends_at TEXT NOT NULL,
              reason TEXT NOT NULL DEFAULT '',
              completed INTEGER NOT NULL DEFAULT 0,
              created_at TEXT NOT NULL
          );
          CREATE INDEX IF NOT EXISTS idx_sessions_starts ON planned_sessions(starts_at);

          CREATE TABLE IF NOT EXISTS notes(
              id TEXT PRIMARY KEY,
              module_id TEXT NULL,
              title TEXT NOT NULL,
              content TEXT NOT NULL DEFAULT '',
              tags TEXT NOT NULL DEFAULT '',
              pinned INTEGER NOT NULL DEFAULT 0,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
          );

          CREATE TABLE IF NOT EXISTS documents(
              id TEXT PRIMARY KEY,
              module_id TEXT NULL,
              title TEXT NOT NULL,
              file_name TEXT NOT NULL,
              stored_path TEXT NOT NULL,
              kind INTEGER NOT NULL,
              size_bytes INTEGER NOT NULL DEFAULT 0,
              extracted_text TEXT NULL,
              summary TEXT NULL,
              imported_at TEXT NOT NULL
          );

          CREATE TABLE IF NOT EXISTS lectures(
              id TEXT PRIMARY KEY,
              module_id TEXT NULL,
              title TEXT NOT NULL,
              lecture_date TEXT NULL,
              recording_document_id TEXT NULL,
              slides_document_id TEXT NULL,
              transcript TEXT NULL,
              summary TEXT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
          );

          CREATE TABLE IF NOT EXISTS revision_items(
              id TEXT PRIMARY KEY,
              module_id TEXT NULL,
              question TEXT NOT NULL,
              answer TEXT NOT NULL,
              source_kind TEXT NULL,
              source_id TEXT NULL,
              created_at TEXT NOT NULL
          );

          CREATE TABLE IF NOT EXISTS links(
              id TEXT PRIMARY KEY,
              from_kind TEXT NOT NULL,
              from_id TEXT NOT NULL,
              to_kind TEXT NOT NULL,
              to_id TEXT NOT NULL,
              label TEXT NULL,
              created_at TEXT NOT NULL
          );
          CREATE INDEX IF NOT EXISTS idx_links_from ON links(from_kind, from_id);
          CREATE INDEX IF NOT EXISTS idx_links_to ON links(to_kind, to_id);

          CREATE TABLE IF NOT EXISTS inbox_items(
              id TEXT PRIMARY KEY,
              source TEXT NOT NULL,
              external_id TEXT NULL,
              title TEXT NOT NULL,
              body TEXT NOT NULL DEFAULT '',
              status INTEGER NOT NULL DEFAULT 0,
              analysis_json TEXT NULL,
              document_id TEXT NULL,
              received_at TEXT NOT NULL,
              processed_at TEXT NULL
          );
          CREATE UNIQUE INDEX IF NOT EXISTS idx_inbox_external ON inbox_items(source, external_id)
              WHERE external_id IS NOT NULL;

          CREATE TABLE IF NOT EXISTS conversations(
              id TEXT PRIMARY KEY,
              title TEXT NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
          );

          CREATE TABLE IF NOT EXISTS chat_messages(
              id TEXT PRIMARY KEY,
              conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
              role INTEGER NOT NULL,
              content TEXT NOT NULL,
              created_at TEXT NOT NULL
          );
          CREATE INDEX IF NOT EXISTS idx_chat_conversation ON chat_messages(conversation_id);

          CREATE TABLE IF NOT EXISTS connections(
              key TEXT PRIMARY KEY,
              status INTEGER NOT NULL DEFAULT 0,
              status_message TEXT NULL,
              last_sync TEXT NULL,
              sync_frequency_minutes INTEGER NOT NULL DEFAULT 30,
              config_json TEXT NOT NULL DEFAULT '{}',
              updated_at TEXT NOT NULL
          );

          CREATE TABLE IF NOT EXISTS sync_log(
              id TEXT PRIMARY KEY,
              connector_key TEXT NOT NULL,
              started_at TEXT NOT NULL,
              finished_at TEXT NOT NULL,
              message TEXT NOT NULL
          );

          CREATE TABLE IF NOT EXISTS settings(
              key TEXT PRIMARY KEY,
              value TEXT NOT NULL
          );

          -- unicode61 (no stemming): prefix queries must work for
          -- search-as-you-type, and porter stems break token prefixes.
          CREATE VIRTUAL TABLE IF NOT EXISTS search_index USING fts5(
              entity_kind UNINDEXED,
              entity_id UNINDEXED,
              title,
              body,
              tokenize='unicode61'
          );
          """

          // v1.1: academic timeline + transparent AI memory.
          2,
          """
          CREATE TABLE IF NOT EXISTS activity_log(
              id TEXT PRIMARY KEY,
              kind TEXT NOT NULL,
              title TEXT NOT NULL,
              detail TEXT NULL,
              entity_kind TEXT NULL,
              entity_id TEXT NULL,
              occurred_at TEXT NOT NULL
          );
          CREATE INDEX IF NOT EXISTS idx_activity_occurred ON activity_log(occurred_at);

          CREATE TABLE IF NOT EXISTS ai_memory(
              id TEXT PRIMARY KEY,
              category TEXT NOT NULL,
              content TEXT NOT NULL,
              source TEXT NOT NULL,
              enabled INTEGER NOT NULL DEFAULT 1,
              updated_at TEXT NOT NULL
          );
          """

          // **Milestones — the shared work item.**
          //
          // Additive only. The Assignments screen replaces its Status word with
          // this list, and the Planner, Modules and Calendar all read the same
          // rows rather than each inventing their own idea of a task.
          //
          // `ON DELETE CASCADE` because a milestone has no meaning without its
          // assignment; leaving orphans would put steps on the Planner for work
          // that no longer exists.
          3,
          """
          CREATE TABLE IF NOT EXISTS milestones(
              id TEXT PRIMARY KEY,
              assignment_id TEXT NOT NULL,
              title TEXT NOT NULL,
              state INTEGER NOT NULL DEFAULT 0,
              position INTEGER NOT NULL DEFAULT 0,
              estimated_minutes INTEGER NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              FOREIGN KEY(assignment_id) REFERENCES assignments(id) ON DELETE CASCADE
          );
          CREATE INDEX IF NOT EXISTS idx_milestones_assignment ON milestones(assignment_id, position);
          """

          // A planned session now names the milestone it advances, so the
          // Planner and the Calendar can show the real step rather than
          // repeating the assignment title down the page.
          //
          // Additive and nullable: every session written before this migration
          // keeps working and simply has no step. No `ON DELETE CASCADE` here —
          // deleting a milestone must not silently delete the study time
          // already scheduled against it; the reference just goes null.
          4,
          """
          ALTER TABLE planned_sessions ADD COLUMN milestone_id TEXT NULL;
          """ ]

    let apply (db: Database) : Async<unit> =
        async {
            do! db.Execute("CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);", [])

            // WAL once at startup — persistent for the database file.
            do! db.Execute("PRAGMA journal_mode = WAL;", [])

            let! applied = db.Query("SELECT version FROM schema_migrations", [], fun r -> int (r.GetInt64 0))
            let appliedSet = Set.ofList applied

            // A duplicated version number would make the second entry look
            // already-applied and then fail on its own INSERT — a confusing
            // runtime error for what is a typo. Caught here, once, at startup.
            let duplicates =
                migrations
                |> List.countBy fst
                |> List.filter (fun (_, count) -> count > 1)
                |> List.map fst

            if not duplicates.IsEmpty then
                failwithf "duplicate migration version(s): %A" duplicates

            for version, sql in migrations do
                if not (appliedSet.Contains version) then
                    do! db.Execute(sql, [])

                    do! db.Execute(
                            "INSERT INTO schema_migrations(version, applied_at) VALUES ($v, $at)",
                            [ "$v", box (int64 version); "$at", Sql.ofDate DateTime.Now ]
                        )
        }
