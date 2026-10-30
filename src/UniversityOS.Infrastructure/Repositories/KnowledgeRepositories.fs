namespace UniversityOS.Infrastructure.Repositories

open System
open Microsoft.Data.Sqlite
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.Infrastructure

[<AutoOpen>]
module private KnowledgeMapping =

    let mapNote (r: SqliteDataReader) : Note =
        { Id = NoteId(Sql.guid r "id")
          ModuleId = Sql.guidOpt r "module_id" |> Option.map ModuleId
          Title = Sql.str r "title"
          Content = Sql.str r "content"
          Tags =
            Sql.str r "tags"
            |> fun t -> t.Split(',')
            |> Array.map (fun s -> s.Trim())
            |> Array.filter (fun s -> s.Length > 0)
            |> List.ofArray
          Pinned = Sql.bool' r "pinned"
          CreatedAt = Sql.date r "created_at"
          UpdatedAt = Sql.date r "updated_at" }

    let mapDocument (r: SqliteDataReader) : Document =
        { Id = DocumentId(Sql.guid r "id")
          ModuleId = Sql.guidOpt r "module_id" |> Option.map ModuleId
          Title = Sql.str r "title"
          FileName = Sql.str r "file_name"
          StoredPath = Sql.str r "stored_path"
          Kind = DocumentKind.ofInt (Sql.int32 r "kind")
          SizeBytes = Sql.int64' r "size_bytes"
          ExtractedText = Sql.strOpt r "extracted_text"
          Summary = Sql.strOpt r "summary"
          ImportedAt = Sql.date r "imported_at" }

    let mapLecture (r: SqliteDataReader) : Lecture =
        { Id = LectureId(Sql.guid r "id")
          ModuleId = Sql.guidOpt r "module_id" |> Option.map ModuleId
          Title = Sql.str r "title"
          Date = Sql.dateOpt r "lecture_date"
          RecordingDocumentId = Sql.guidOpt r "recording_document_id" |> Option.map DocumentId
          SlidesDocumentId = Sql.guidOpt r "slides_document_id" |> Option.map DocumentId
          Transcript = Sql.strOpt r "transcript"
          Summary = Sql.strOpt r "summary"
          CreatedAt = Sql.date r "created_at"
          UpdatedAt = Sql.date r "updated_at" }

    let mapRevisionItem (r: SqliteDataReader) : RevisionItem =
        { Id = RevisionItemId(Sql.guid r "id")
          ModuleId = Sql.guidOpt r "module_id" |> Option.map ModuleId
          Question = Sql.str r "question"
          Answer = Sql.str r "answer"
          Source =
            match Sql.strOpt r "source_kind", Sql.guidOpt r "source_id" with
            | Some kindStr, Some id ->
                EntityKind.parse kindStr |> Option.map (fun kind -> { Kind = kind; Id = id })
            | _ -> None
          CreatedAt = Sql.date r "created_at" }

    let mapLink (r: SqliteDataReader) : Link option =
        match EntityKind.parse (Sql.str r "from_kind"), EntityKind.parse (Sql.str r "to_kind") with
        | Some fromKind, Some toKind ->
            Some
                { Id = LinkId(Sql.guid r "id")
                  From = { Kind = fromKind; Id = Sql.guid r "from_id" }
                  To = { Kind = toKind; Id = Sql.guid r "to_id" }
                  Label = Sql.strOpt r "label"
                  CreatedAt = Sql.date r "created_at" }
        | _ -> None

type NoteRepository(db: Database) =
    interface INoteRepository with

        member _.GetAll() =
            db.Query("SELECT * FROM notes ORDER BY pinned DESC, updated_at DESC", [], mapNote)

        member _.GetByModule(moduleId) =
            db.Query(
                "SELECT * FROM notes WHERE module_id = $m ORDER BY pinned DESC, updated_at DESC",
                [ "$m", Sql.ofGuid moduleId.Value ],
                mapNote
            )

        member _.GetById(id) =
            db.QueryOne("SELECT * FROM notes WHERE id = $id", [ "$id", Sql.ofGuid id.Value ], mapNote)

        member _.Save(n: Note) =
            async {
                do! db.Execute(
                        """
                        INSERT INTO notes(id, module_id, title, content, tags, pinned, created_at, updated_at)
                        VALUES ($id, $module, $title, $content, $tags, $pinned, $created, $updated)
                        ON CONFLICT(id) DO UPDATE SET
                            module_id=$module, title=$title, content=$content, tags=$tags, pinned=$pinned, updated_at=$updated
                        """,
                        [ "$id", Sql.ofGuid n.Id.Value
                          "$module", Sql.ofGuidOpt (n.ModuleId |> Option.map (fun m -> m.Value))
                          "$title", Sql.ofStr n.Title
                          "$content", Sql.ofStr n.Content
                          "$tags", Sql.ofStr (String.concat "," n.Tags)
                          "$pinned", Sql.ofBool n.Pinned
                          "$created", Sql.ofDate n.CreatedAt
                          "$updated", Sql.ofDate n.UpdatedAt ]
                    )

                do! Fts.upsert db EntityKind.Note n.Id.Value n.Title (n.Content + " " + String.concat " " n.Tags)
            }

        member _.Delete(id) =
            async {
                do! db.Execute("DELETE FROM notes WHERE id = $id", [ "$id", Sql.ofGuid id.Value ])
                do! Fts.remove db EntityKind.Note id.Value
            }

type DocumentRepository(db: Database) =
    interface IDocumentRepository with

        member _.GetAll() =
            db.Query("SELECT * FROM documents ORDER BY imported_at DESC", [], mapDocument)

        member _.GetByModule(moduleId) =
            db.Query(
                "SELECT * FROM documents WHERE module_id = $m ORDER BY imported_at DESC",
                [ "$m", Sql.ofGuid moduleId.Value ],
                mapDocument
            )

        member _.GetById(id) =
            db.QueryOne("SELECT * FROM documents WHERE id = $id", [ "$id", Sql.ofGuid id.Value ], mapDocument)

        member _.Save(d: Document) =
            async {
                do! db.Execute(
                        """
                        INSERT INTO documents(id, module_id, title, file_name, stored_path, kind, size_bytes, extracted_text, summary, imported_at)
                        VALUES ($id, $module, $title, $file, $path, $kind, $size, $text, $summary, $imported)
                        ON CONFLICT(id) DO UPDATE SET
                            module_id=$module, title=$title, file_name=$file, stored_path=$path, kind=$kind,
                            size_bytes=$size, extracted_text=$text, summary=$summary
                        """,
                        [ "$id", Sql.ofGuid d.Id.Value
                          "$module", Sql.ofGuidOpt (d.ModuleId |> Option.map (fun m -> m.Value))
                          "$title", Sql.ofStr d.Title
                          "$file", Sql.ofStr d.FileName
                          "$path", Sql.ofStr d.StoredPath
                          "$kind", Sql.ofInt (DocumentKind.toInt d.Kind)
                          "$size", box d.SizeBytes
                          "$text", Sql.ofStrOpt d.ExtractedText
                          "$summary", Sql.ofStrOpt d.Summary
                          "$imported", Sql.ofDate d.ImportedAt ]
                    )

                do!
                    Fts.upsert
                        db
                        EntityKind.Document
                        d.Id.Value
                        d.Title
                        (sprintf
                            "%s %s %s"
                            d.FileName
                            (d.ExtractedText |> Option.defaultValue "")
                            (d.Summary |> Option.defaultValue ""))
            }

        member _.Delete(id) =
            async {
                do! db.Execute("DELETE FROM documents WHERE id = $id", [ "$id", Sql.ofGuid id.Value ])
                do! Fts.remove db EntityKind.Document id.Value
            }

type LectureRepository(db: Database) =
    interface ILectureRepository with

        member _.GetAll() =
            db.Query("SELECT * FROM lectures ORDER BY lecture_date DESC", [], mapLecture)

        member _.GetByModule(moduleId) =
            db.Query(
                "SELECT * FROM lectures WHERE module_id = $m ORDER BY lecture_date DESC",
                [ "$m", Sql.ofGuid moduleId.Value ],
                mapLecture
            )

        member _.GetById(id) =
            db.QueryOne("SELECT * FROM lectures WHERE id = $id", [ "$id", Sql.ofGuid id.Value ], mapLecture)

        member _.Save(l: Lecture) =
            async {
                do! db.Execute(
                        """
                        INSERT INTO lectures(id, module_id, title, lecture_date, recording_document_id, slides_document_id, transcript, summary, created_at, updated_at)
                        VALUES ($id, $module, $title, $date, $recording, $slides, $transcript, $summary, $created, $updated)
                        ON CONFLICT(id) DO UPDATE SET
                            module_id=$module, title=$title, lecture_date=$date, recording_document_id=$recording,
                            slides_document_id=$slides, transcript=$transcript, summary=$summary, updated_at=$updated
                        """,
                        [ "$id", Sql.ofGuid l.Id.Value
                          "$module", Sql.ofGuidOpt (l.ModuleId |> Option.map (fun m -> m.Value))
                          "$title", Sql.ofStr l.Title
                          "$date", Sql.ofDateOpt l.Date
                          "$recording", Sql.ofGuidOpt (l.RecordingDocumentId |> Option.map (fun d -> d.Value))
                          "$slides", Sql.ofGuidOpt (l.SlidesDocumentId |> Option.map (fun d -> d.Value))
                          "$transcript", Sql.ofStrOpt l.Transcript
                          "$summary", Sql.ofStrOpt l.Summary
                          "$created", Sql.ofDate l.CreatedAt
                          "$updated", Sql.ofDate l.UpdatedAt ]
                    )

                do!
                    Fts.upsert
                        db
                        EntityKind.Lecture
                        l.Id.Value
                        l.Title
                        (sprintf "%s %s" (l.Transcript |> Option.defaultValue "") (l.Summary |> Option.defaultValue ""))
            }

        member _.Delete(id) =
            async {
                do! db.Execute("DELETE FROM lectures WHERE id = $id", [ "$id", Sql.ofGuid id.Value ])
                do! Fts.remove db EntityKind.Lecture id.Value
            }

type RevisionRepository(db: Database) =
    interface IRevisionRepository with

        member _.GetAll() =
            db.Query("SELECT * FROM revision_items ORDER BY created_at DESC", [], mapRevisionItem)

        member _.GetByModule(moduleId) =
            db.Query(
                "SELECT * FROM revision_items WHERE module_id = $m ORDER BY created_at DESC",
                [ "$m", Sql.ofGuid moduleId.Value ],
                mapRevisionItem
            )

        member _.SaveMany(items) =
            async {
                let statements =
                    items
                    |> List.map (fun (item: RevisionItem) ->
                        """
                        INSERT INTO revision_items(id, module_id, question, answer, source_kind, source_id, created_at)
                        VALUES ($id, $module, $q, $a, $skind, $sid, $created)
                        ON CONFLICT(id) DO UPDATE SET module_id=$module, question=$q, answer=$a
                        """,
                        [ "$id", Sql.ofGuid item.Id.Value
                          "$module", Sql.ofGuidOpt (item.ModuleId |> Option.map (fun m -> m.Value))
                          "$q", Sql.ofStr item.Question
                          "$a", Sql.ofStr item.Answer
                          "$skind", Sql.ofStrOpt (item.Source |> Option.map (fun s -> EntityKind.toString s.Kind))
                          "$sid", Sql.ofGuidOpt (item.Source |> Option.map (fun s -> s.Id))
                          "$created", Sql.ofDate item.CreatedAt ])

                do! db.ExecuteTransaction statements

                for item in items do
                    do! Fts.upsert db EntityKind.RevisionItem item.Id.Value item.Question item.Answer
            }

        member _.Delete(id) =
            async {
                do! db.Execute("DELETE FROM revision_items WHERE id = $id", [ "$id", Sql.ofGuid id.Value ])
                do! Fts.remove db EntityKind.RevisionItem id.Value
            }

type LinkRepository(db: Database) =
    interface ILinkRepository with

        member _.GetAll() =
            async {
                let! rows = db.Query("SELECT * FROM links ORDER BY created_at DESC", [], mapLink)
                return rows |> List.choose id
            }

        member _.GetFor(entity: EntityRef) =
            async {
                let! rows =
                    db.Query(
                        """
                        SELECT * FROM links
                        WHERE (from_kind = $kind AND from_id = $id) OR (to_kind = $kind AND to_id = $id)
                        ORDER BY created_at DESC
                        """,
                        [ "$kind", Sql.ofStr (EntityKind.toString entity.Kind); "$id", Sql.ofGuid entity.Id ],
                        mapLink
                    )

                return rows |> List.choose id
            }

        member _.Save(link: Link) =
            db.Execute(
                """
                INSERT INTO links(id, from_kind, from_id, to_kind, to_id, label, created_at)
                VALUES ($id, $fkind, $fid, $tkind, $tid, $label, $created)
                ON CONFLICT(id) DO NOTHING
                """,
                [ "$id", Sql.ofGuid link.Id.Value
                  "$fkind", Sql.ofStr (EntityKind.toString link.From.Kind)
                  "$fid", Sql.ofGuid link.From.Id
                  "$tkind", Sql.ofStr (EntityKind.toString link.To.Kind)
                  "$tid", Sql.ofGuid link.To.Id
                  "$label", Sql.ofStrOpt link.Label
                  "$created", Sql.ofDate link.CreatedAt ]
            )

        member _.Delete(id: LinkId) =
            db.Execute("DELETE FROM links WHERE id = $id", [ "$id", Sql.ofGuid id.Value ])
