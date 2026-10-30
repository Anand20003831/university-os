namespace UniversityOS.Infrastructure.Repositories

open System
open Microsoft.Data.Sqlite
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.Infrastructure

[<AutoOpen>]
module private CoreMapping =

    let mapModule (r: SqliteDataReader) : StudyModule =
        { Id = ModuleId(Sql.guid r "id")
          Code = Sql.str r "code"
          Name = Sql.str r "name"
          Lecturer = Sql.strOpt r "lecturer"
          Credits = Sql.int32Opt r "credits"
          Color = Sql.str r "color"
          Term = Sql.strOpt r "term"
          Description = Sql.strOpt r "description"
          Archived = Sql.bool' r "archived"
          CreatedAt = Sql.date r "created_at"
          UpdatedAt = Sql.date r "updated_at" }

    let mapChecklistItem (r: SqliteDataReader) : ChecklistItem =
        { Id = ChecklistItemId(Sql.guid r "id")
          Text = Sql.str r "text"
          Done = Sql.bool' r "done"
          Position = Sql.int32 r "position" }

    let mapAssignment (checklist: ChecklistItem list) (r: SqliteDataReader) : Assignment =
        { Id = AssignmentId(Sql.guid r "id")
          ModuleId = Sql.guidOpt r "module_id" |> Option.map ModuleId
          Title = Sql.str r "title"
          Description = Sql.str r "description"
          Deadline = Sql.dateOpt r "deadline"
          Weighting = Sql.floatOpt r "weighting"
          Priority = Priority.ofRank (Sql.int32 r "priority")
          Status = AssignmentStatus.ofInt (Sql.int32 r "status")
          EstimatedHours = Sql.floatOpt r "estimated_hours"
          ActualHours = Sql.float' r "actual_hours"
          Progress = Sql.int32 r "progress"
          Checklist = checklist
          SubmissionUrl = Sql.strOpt r "submission_url"
          RepositoryUrl = Sql.strOpt r "repository_url"
          CreatedAt = Sql.date r "created_at"
          UpdatedAt = Sql.date r "updated_at" }

    let mapEvent (r: SqliteDataReader) : CalendarEvent =
        { Id = EventId(Sql.guid r "id")
          Title = Sql.str r "title"
          Kind = EventKind.ofInt (Sql.int32 r "kind")
          StartsAt = Sql.date r "starts_at"
          EndsAt = Sql.date r "ends_at"
          AllDay = Sql.bool' r "all_day"
          Location = Sql.strOpt r "location"
          ModuleId = Sql.guidOpt r "module_id" |> Option.map ModuleId
          Source = Sql.strOpt r "source"
          ExternalId = Sql.strOpt r "external_id"
          Notes = Sql.strOpt r "notes"
          CreatedAt = Sql.date r "created_at"
          UpdatedAt = Sql.date r "updated_at" }

    let mapSession (r: SqliteDataReader) : PlannedSession =
        { Id = SessionId(Sql.guid r "id")
          AssignmentId = Sql.guidOpt r "assignment_id" |> Option.map AssignmentId
          MilestoneId = Sql.guidOpt r "milestone_id" |> Option.map MilestoneId
          Title = Sql.str r "title"
          ModuleCode = Sql.strOpt r "module_code"
          StartsAt = Sql.date r "starts_at"
          EndsAt = Sql.date r "ends_at"
          Reason = Sql.str r "reason"
          Completed = Sql.bool' r "completed"
          CreatedAt = Sql.date r "created_at" }

type ModuleRepository(db: Database) =
    interface IModuleRepository with

        member _.GetAll() =
            db.Query("SELECT * FROM modules ORDER BY code", [], mapModule)

        member _.GetById(id: ModuleId) =
            db.QueryOne("SELECT * FROM modules WHERE id = $id", [ "$id", Sql.ofGuid id.Value ], mapModule)

        member _.Save(m: StudyModule) =
            async {
                do! db.Execute(
                        """
                        INSERT INTO modules(id, code, name, lecturer, credits, color, term, description, archived, created_at, updated_at)
                        VALUES ($id, $code, $name, $lecturer, $credits, $color, $term, $description, $archived, $created, $updated)
                        ON CONFLICT(id) DO UPDATE SET
                            code=$code, name=$name, lecturer=$lecturer, credits=$credits, color=$color,
                            term=$term, description=$description, archived=$archived, updated_at=$updated
                        """,
                        [ "$id", Sql.ofGuid m.Id.Value
                          "$code", Sql.ofStr m.Code
                          "$name", Sql.ofStr m.Name
                          "$lecturer", Sql.ofStrOpt m.Lecturer
                          "$credits", Sql.ofIntOpt m.Credits
                          "$color", Sql.ofStr m.Color
                          "$term", Sql.ofStrOpt m.Term
                          "$description", Sql.ofStrOpt m.Description
                          "$archived", Sql.ofBool m.Archived
                          "$created", Sql.ofDate m.CreatedAt
                          "$updated", Sql.ofDate m.UpdatedAt ]
                    )

                do!
                    Fts.upsert
                        db
                        EntityKind.Module
                        m.Id.Value
                        (StudyModule.displayName m)
                        (sprintf "%s %s" (m.Lecturer |> Option.defaultValue "") (m.Description |> Option.defaultValue ""))
            }

        member _.Delete(id: ModuleId) =
            async {
                do! db.Execute("DELETE FROM modules WHERE id = $id", [ "$id", Sql.ofGuid id.Value ])
                do! Fts.remove db EntityKind.Module id.Value
            }

type AssignmentRepository(db: Database) =

    let loadChecklist (assignmentId: AssignmentId) =
        db.Query(
            "SELECT * FROM checklist_items WHERE assignment_id = $id ORDER BY position",
            [ "$id", Sql.ofGuid assignmentId.Value ],
            mapChecklistItem
        )

    /// Loads assignments for an arbitrary WHERE clause, then attaches checklists
    /// in one extra query (avoids N+1).
    let loadMany (whereSql: string) (parameters: (string * obj) list) =
        async {
            let! rows = db.Query(sprintf "SELECT * FROM assignments %s" whereSql, parameters, mapAssignment [])

            let! checklistRows =
                db.Query(
                    "SELECT * FROM checklist_items ORDER BY position",
                    [],
                    fun r -> Sql.guid r "assignment_id", mapChecklistItem r
                )

            let byAssignment =
                checklistRows |> List.groupBy fst |> List.map (fun (k, v) -> k, List.map snd v) |> dict

            return
                rows
                |> List.map (fun a ->
                    match byAssignment.TryGetValue a.Id.Value with
                    | true, items -> { a with Checklist = items }
                    | _ -> a)
        }

    let save (a: Assignment) =
        async {
            let upsertSql =
                """
                INSERT INTO assignments(id, module_id, title, description, deadline, weighting, priority, status,
                                        estimated_hours, actual_hours, progress, submission_url, repository_url, created_at, updated_at)
                VALUES ($id, $module, $title, $desc, $deadline, $weighting, $priority, $status,
                        $est, $actual, $progress, $submission, $repo, $created, $updated)
                ON CONFLICT(id) DO UPDATE SET
                    module_id=$module, title=$title, description=$desc, deadline=$deadline, weighting=$weighting,
                    priority=$priority, status=$status, estimated_hours=$est, actual_hours=$actual,
                    progress=$progress, submission_url=$submission, repository_url=$repo, updated_at=$updated
                """

            let upsertParams =
                [ "$id", Sql.ofGuid a.Id.Value
                  "$module", Sql.ofGuidOpt (a.ModuleId |> Option.map (fun m -> m.Value))
                  "$title", Sql.ofStr a.Title
                  "$desc", Sql.ofStr a.Description
                  "$deadline", Sql.ofDateOpt a.Deadline
                  "$weighting", Sql.ofFloatOpt a.Weighting
                  "$priority", Sql.ofInt (Priority.rank a.Priority)
                  "$status", Sql.ofInt (AssignmentStatus.toInt a.Status)
                  "$est", Sql.ofFloatOpt a.EstimatedHours
                  "$actual", Sql.ofFloat a.ActualHours
                  "$progress", Sql.ofInt a.Progress
                  "$submission", Sql.ofStrOpt a.SubmissionUrl
                  "$repo", Sql.ofStrOpt a.RepositoryUrl
                  "$created", Sql.ofDate a.CreatedAt
                  "$updated", Sql.ofDate a.UpdatedAt ]

            let checklistInserts =
                a.Checklist
                |> List.mapi (fun i item ->
                    "INSERT INTO checklist_items(id, assignment_id, text, done, position) VALUES ($id, $aid, $text, $done, $pos)",
                    [ "$id", Sql.ofGuid item.Id.Value
                      "$aid", Sql.ofGuid a.Id.Value
                      "$text", Sql.ofStr item.Text
                      "$done", Sql.ofBool item.Done
                      "$pos", Sql.ofInt i ])

            let statements =
                (upsertSql, upsertParams)
                :: ("DELETE FROM checklist_items WHERE assignment_id = $id", [ "$id", Sql.ofGuid a.Id.Value ])
                :: checklistInserts

            do! db.ExecuteTransaction statements
            do! Fts.upsert db EntityKind.Assignment a.Id.Value a.Title a.Description
        }

    interface IAssignmentRepository with

        member _.GetAll() = loadMany "ORDER BY deadline IS NULL, deadline" []

        member _.GetOpen() =
            loadMany "WHERE status NOT IN (3, 4) ORDER BY deadline IS NULL, deadline" []

        member _.GetByModule(moduleId: ModuleId) =
            loadMany "WHERE module_id = $module ORDER BY deadline IS NULL, deadline" [ "$module", Sql.ofGuid moduleId.Value ]

        member _.GetById(id: AssignmentId) =
            async {
                let! row =
                    db.QueryOne("SELECT * FROM assignments WHERE id = $id", [ "$id", Sql.ofGuid id.Value ], mapAssignment [])

                match row with
                | None -> return None
                | Some a ->
                    let! checklist = loadChecklist a.Id
                    return Some { a with Checklist = checklist }
            }

        member _.Save(a: Assignment) = save a

        member _.Delete(id: AssignmentId) =
            async {
                do! db.Execute("DELETE FROM assignments WHERE id = $id", [ "$id", Sql.ofGuid id.Value ])
                do! Fts.remove db EntityKind.Assignment id.Value
            }

type EventRepository(db: Database) =

    let save (e: CalendarEvent) =
        async {
            do! db.Execute(
                    """
                    INSERT INTO events(id, title, kind, starts_at, ends_at, all_day, location, module_id, source, external_id, notes, created_at, updated_at)
                    VALUES ($id, $title, $kind, $starts, $ends, $allday, $location, $module, $source, $external, $notes, $created, $updated)
                    ON CONFLICT(id) DO UPDATE SET
                        title=$title, kind=$kind, starts_at=$starts, ends_at=$ends, all_day=$allday, location=$location,
                        module_id=$module, source=$source, external_id=$external, notes=$notes, updated_at=$updated
                    """,
                    [ "$id", Sql.ofGuid e.Id.Value
                      "$title", Sql.ofStr e.Title
                      "$kind", Sql.ofInt (EventKind.toInt e.Kind)
                      "$starts", Sql.ofDate e.StartsAt
                      "$ends", Sql.ofDate e.EndsAt
                      "$allday", Sql.ofBool e.AllDay
                      "$location", Sql.ofStrOpt e.Location
                      "$module", Sql.ofGuidOpt (e.ModuleId |> Option.map (fun m -> m.Value))
                      "$source", Sql.ofStrOpt e.Source
                      "$external", Sql.ofStrOpt e.ExternalId
                      "$notes", Sql.ofStrOpt e.Notes
                      "$created", Sql.ofDate e.CreatedAt
                      "$updated", Sql.ofDate e.UpdatedAt ]
                )

            do!
                Fts.upsert
                    db
                    EntityKind.Event
                    e.Id.Value
                    e.Title
                    (sprintf "%s %s" (e.Location |> Option.defaultValue "") (e.Notes |> Option.defaultValue ""))
        }

    interface IEventRepository with

        member _.GetBetween(rangeStart, rangeEnd) =
            db.Query(
                "SELECT * FROM events WHERE starts_at < $end AND ends_at > $start ORDER BY starts_at",
                [ "$start", Sql.ofDate rangeStart; "$end", Sql.ofDate rangeEnd ],
                mapEvent
            )

        member _.GetById(id: EventId) =
            db.QueryOne("SELECT * FROM events WHERE id = $id", [ "$id", Sql.ofGuid id.Value ], mapEvent)

        member _.GetByExternalId(source, externalId) =
            db.QueryOne(
                "SELECT * FROM events WHERE source = $source AND external_id = $external",
                [ "$source", Sql.ofStr source; "$external", Sql.ofStr externalId ],
                mapEvent
            )

        member _.Save(e: CalendarEvent) = save e

        member _.Delete(id: EventId) =
            async {
                do! db.Execute("DELETE FROM events WHERE id = $id", [ "$id", Sql.ofGuid id.Value ])
                do! Fts.remove db EntityKind.Event id.Value
            }

type MilestoneRepository(db: Database) =

    let mapMilestone (r: SqliteDataReader) : Milestone =
        { Id = MilestoneId(Sql.guid r "id")
          AssignmentId = AssignmentId(Sql.guid r "assignment_id")
          Title = Sql.str r "title"
          State = MilestoneState.ofInt (Sql.int32 r "state")
          Order = Sql.int32 r "position"
          EstimatedMinutes = Sql.int32Opt r "estimated_minutes"
          CreatedAt = Sql.date r "created_at"
          UpdatedAt = Sql.date r "updated_at" }

    let upsertSql =
        """
        INSERT INTO milestones(id, assignment_id, title, state, position, estimated_minutes, created_at, updated_at)
        VALUES ($id, $assignment, $title, $state, $position, $estimate, $created, $updated)
        ON CONFLICT(id) DO UPDATE SET
            assignment_id = $assignment,
            title = $title,
            state = $state,
            position = $position,
            estimated_minutes = $estimate,
            updated_at = $updated
        """

    let parametersOf (m: Milestone) =
        [ "$id", Sql.ofGuid m.Id.Value
          "$assignment", Sql.ofGuid m.AssignmentId.Value
          "$title", Sql.ofStr m.Title
          "$state", Sql.ofInt (MilestoneState.toInt m.State)
          "$position", Sql.ofInt m.Order
          "$estimate", Sql.ofIntOpt m.EstimatedMinutes
          "$created", Sql.ofDate m.CreatedAt
          "$updated", Sql.ofDate m.UpdatedAt ]

    // Ordered by `position` here rather than left to the caller: every screen
    // that reads milestones wants them in the user's order, and a repository
    // that returned insertion order would have four callers sorting identically.
    let ordered = "ORDER BY position, created_at, id"

    interface IMilestoneRepository with

        member _.GetAll() =
            db.Query(sprintf "SELECT * FROM milestones %s" ordered, [], mapMilestone)

        member _.GetForAssignment(assignmentId) =
            db.Query(
                sprintf "SELECT * FROM milestones WHERE assignment_id = $a %s" ordered,
                [ "$a", Sql.ofGuid assignmentId.Value ],
                mapMilestone
            )

        member _.GetById(id) =
            db.QueryOne("SELECT * FROM milestones WHERE id = $id", [ "$id", Sql.ofGuid id.Value ], mapMilestone)

        member _.Save(milestone) = db.Execute(upsertSql, parametersOf milestone)

        /// **One transaction, not a delete followed by a loop of inserts.**
        ///
        /// Reordering rewrites every row's position at once. Applied as separate
        /// statements, a reader landing between them sees duplicate positions —
        /// and a failure halfway leaves the assignment with some of its steps
        /// gone, which is data loss rather than a stale read.
        member _.ReplaceFor(assignmentId, milestones) =
            db.ExecuteTransaction(
                [ "DELETE FROM milestones WHERE assignment_id = $a", [ "$a", Sql.ofGuid assignmentId.Value ]

                  for m in milestones do
                      upsertSql, parametersOf { m with AssignmentId = assignmentId } ]
            )

        member _.Delete(id) =
            db.Execute("DELETE FROM milestones WHERE id = $id", [ "$id", Sql.ofGuid id.Value ])

type SessionRepository(db: Database) =
    interface ISessionRepository with

        member _.GetBetween(rangeStart, rangeEnd) =
            db.Query(
                "SELECT * FROM planned_sessions WHERE starts_at < $end AND ends_at > $start ORDER BY starts_at",
                [ "$start", Sql.ofDate rangeStart; "$end", Sql.ofDate rangeEnd ],
                mapSession
            )

        member _.ReplaceFrom(from, sessions) =
            let inserts =
                sessions
                |> List.map (fun s ->
                    """
                    INSERT INTO planned_sessions(id, assignment_id, milestone_id, title, module_code, starts_at, ends_at, reason, completed, created_at)
                    VALUES ($id, $aid, $mid, $title, $code, $starts, $ends, $reason, $completed, $created)
                    """,
                    [ "$id", Sql.ofGuid s.Id.Value
                      "$aid", Sql.ofGuidOpt (s.AssignmentId |> Option.map (fun a -> a.Value))
                      "$mid", Sql.ofGuidOpt (s.MilestoneId |> Option.map (fun m -> m.Value))
                      "$title", Sql.ofStr s.Title
                      "$code", Sql.ofStrOpt s.ModuleCode
                      "$starts", Sql.ofDate s.StartsAt
                      "$ends", Sql.ofDate s.EndsAt
                      "$reason", Sql.ofStr s.Reason
                      "$completed", Sql.ofBool s.Completed
                      "$created", Sql.ofDate s.CreatedAt ])

            db.ExecuteTransaction(
                ("DELETE FROM planned_sessions WHERE starts_at >= $from AND completed = 0", [ "$from", Sql.ofDate from ])
                :: inserts
            )

        member _.SetCompleted(id, completed) =
            db.Execute(
                "UPDATE planned_sessions SET completed = $done WHERE id = $id",
                [ "$done", Sql.ofBool completed; "$id", Sql.ofGuid id.Value ]
            )
