namespace UniversityOS.Application

open System
open UniversityOS.Domain

/// The unified capture pipeline: everything the user throws at the app — text,
/// files, connector imports — becomes an InboxItem, gets analyzed into proposed
/// actions, and is applied (by the user, or automatically when enabled).
type CaptureService
    (
        inbox: IInboxRepository,
        modules: IModuleRepository,
        assignments: IAssignmentRepository,
        events: IEventRepository,
        notes: INoteRepository,
        documents: IDocumentRepository,
        links: ILinkRepository,
        importService: IDocumentImportService,
        ai: AiService,
        settings: ISettingsStore,
        activity: IActivityLog,
        clock: IClock
    ) =

    let normalizeCode (c: string) = c.Replace(" ", "").Replace("-", "").ToUpperInvariant()

    let resolveModuleId (allModules: StudyModule list) (code: string option) =
        code
        |> Option.bind (fun c -> allModules |> List.tryFind (fun m -> normalizeCode m.Code = normalizeCode c))
        |> Option.map (fun m -> m.Id)

    let titleFromText (text: string) =
        let first =
            text.Split('\n')
            |> Array.map (fun l -> l.Trim().TrimStart('#', '-', '*', ' '))
            |> Array.tryFind (fun l -> l.Length > 0)
            |> Option.defaultValue "Captured text"

        if first.Length > 80 then first.Substring(0, 79).TrimEnd() + "…" else first

    /// Analyze an item and persist the analysis. Applies automatically when the
    /// user has enabled auto-apply.
    member this.Analyze(item: InboxItem) : Async<InboxItem> =
        async {
            let! analysis = ai.ClassifyCapture(item.Title, item.Body)

            let analyzed =
                { item with
                    Analysis = Some analysis
                    Status = InboxStatus.Analyzed }

            do! inbox.Save analyzed

            let! userSettings = settings.Load()

            if userSettings.AiAutoApply then
                let! applied, _ = this.ApplyAll analyzed
                return applied
            else
                return analyzed
        }

    /// Capture free text (quick capture, paste, voice-note transcript, …).
    member this.CaptureText(text: string) : Async<InboxItem> =
        async {
            let item = InboxItem.create clock.Now CaptureSource.ManualText (titleFromText text) text
            do! inbox.Save item
            return! this.Analyze item
        }

    /// Capture a file: import into the managed document store, then run the
    /// pipeline over its extracted text so deadlines/assignments inside the file
    /// are discovered.
    member this.CaptureFile(path: string) : Async<Result<InboxItem, string>> =
        async {
            let! imported = importService.ImportFile(path, None)

            match imported with
            | Error e -> return Error e
            | Ok doc ->
                let body =
                    match doc.ExtractedText with
                    | Some text when text.Trim().Length > 0 ->
                        if text.Length > 8000 then text.Substring(0, 8000) else text
                    | _ -> sprintf "(no text could be extracted from %s)" doc.FileName

                let item =
                    { InboxItem.create clock.Now (CaptureSource.File doc.FileName) doc.Title body with
                        DocumentId = Some doc.Id }

                do! inbox.Save item
                let! analyzed = this.Analyze item
                return Ok analyzed
        }

    /// Apply one proposed action. Returns a description of what was created/changed.
    member _.ApplyAction(item: InboxItem, action: ProposedAction) : Async<Result<string, string>> =
        async {
            let now = clock.Now
            let! allModules = modules.GetAll()
            let moduleId = resolveModuleId allModules action.ModuleCode

            let linkDocumentTo (target: EntityRef) =
                async {
                    match item.DocumentId with
                    | Some docId ->
                        do! links.Save(Link.create now { Kind = EntityKind.Document; Id = docId.Value } target (Some "captured with"))
                    | None -> ()
                }

            // Every successfully applied action lands on the timeline.
            let logApplied (title: string) (entity: EntityRef option) =
                activity.Log(
                    ActivityEntry.create now ActivityKind.CaptureApplied title (Some("From capture: " + item.Title)) entity
                )

            match action.Kind with
            | ProposedActionKind.CreateAssignment ->
                let assignment =
                    { Assignment.create now action.Title with
                        ModuleId = moduleId
                        Deadline = action.Date
                        Description = action.Body |> Option.defaultValue "" }

                do! assignments.Save assignment
                do! linkDocumentTo { Kind = EntityKind.Assignment; Id = assignment.Id.Value }

                do! logApplied
                        (sprintf "Created assignment \"%s\"" action.Title)
                        (Some { Kind = EntityKind.Assignment; Id = assignment.Id.Value })

                return
                    Ok(
                        sprintf
                            "Created assignment \"%s\"%s"
                            action.Title
                            (action.Date |> Option.map (fun d -> ", due " + d.ToString "ddd d MMM HH:mm") |> Option.defaultValue "")
                    )

            | ProposedActionKind.UpdateDeadline ->
                match action.Date with
                | None -> return Error "The proposed deadline has no date."
                | Some date ->
                    let! all = assignments.GetAll()

                    let target =
                        action.TargetId
                        |> Option.bind (fun tid ->
                            match Guid.TryParse tid with
                            | true, g -> all |> List.tryFind (fun a -> a.Id.Value = g)
                            | _ -> None)
                        |> Option.orElse (
                            all
                            |> List.tryFind (fun a -> a.Title.ToLowerInvariant().Contains(action.Title.ToLowerInvariant()))
                        )

                    match target with
                    | Some a ->
                        do! assignments.Save { a with Deadline = Some date; UpdatedAt = now }

                        do! activity.Log(
                                ActivityEntry.create
                                    now
                                    ActivityKind.DeadlineChanged
                                    (sprintf "Deadline of \"%s\" moved to %s" a.Title (date.ToString "ddd d MMM HH:mm"))
                                    (Some("From capture: " + item.Title))
                                    (Some { Kind = EntityKind.Assignment; Id = a.Id.Value })
                            )

                        return Ok(sprintf "Moved deadline of \"%s\" to %s" a.Title (date.ToString "ddd d MMM HH:mm"))
                    | None -> return Error(sprintf "No existing assignment matches \"%s\"." action.Title)

            | ProposedActionKind.CreateEvent ->
                match action.Date with
                | None -> return Error "The proposed event has no date."
                | Some date ->
                    let duration = action.DurationMinutes |> Option.defaultValue 60

                    let kind =
                        let t = action.Title.ToLowerInvariant()
                        if t.Contains "exam" || t.Contains "test" then EventKind.Exam
                        elif t.Contains "lecture" then EventKind.Lecture
                        elif t.Contains "meeting" then EventKind.Meeting
                        else EventKind.Other

                    let ev =
                        { CalendarEvent.create now action.Title kind date (date.AddMinutes(float duration)) with
                            ModuleId = moduleId }

                    do! events.Save ev
                    do! linkDocumentTo { Kind = EntityKind.Event; Id = ev.Id.Value }

                    do! logApplied
                            (sprintf "Added \"%s\" to the calendar" action.Title)
                            (Some { Kind = EntityKind.Event; Id = ev.Id.Value })

                    return Ok(sprintf "Added \"%s\" to the calendar on %s" action.Title (date.ToString "ddd d MMM HH:mm"))

            | ProposedActionKind.CreateNote ->
                let note =
                    { Note.create now action.Title with
                        Content = action.Body |> Option.defaultValue item.Body
                        ModuleId = moduleId }

                do! notes.Save note
                do! linkDocumentTo { Kind = EntityKind.Note; Id = note.Id.Value }

                do! logApplied
                        (sprintf "Created note \"%s\"" action.Title)
                        (Some { Kind = EntityKind.Note; Id = note.Id.Value })

                return Ok(sprintf "Created note \"%s\"" action.Title)

            | ProposedActionKind.LinkToModule ->
                match item.DocumentId, moduleId with
                | Some docId, Some mid ->
                    let! doc = documents.GetById docId

                    match doc with
                    | Some d ->
                        do! documents.Save { d with ModuleId = Some mid }
                        let name = allModules |> List.tryFind (fun m -> m.Id = mid) |> Option.map (fun m -> m.Code) |> Option.defaultValue "module"

                        do! logApplied
                                (sprintf "Filed \"%s\" under %s" d.Title name)
                                (Some { Kind = EntityKind.Document; Id = d.Id.Value })

                        return Ok(sprintf "Filed \"%s\" under %s" d.Title name)
                    | None -> return Error "The captured document no longer exists."
                | _ -> return Error "Nothing to link: needs both a document and a recognized module."

            | ProposedActionKind.NoAction -> return Ok "No action needed."
        }

    /// Apply every proposed action on an item and mark it processed.
    member this.ApplyAll(item: InboxItem) : Async<InboxItem * string list> =
        async {
            let results = ResizeArray<string>()

            match item.Analysis with
            | None -> ()
            | Some analysis ->
                for action in analysis.Actions do
                    let! result = this.ApplyAction(item, action)

                    match result with
                    | Ok msg -> results.Add msg
                    | Error msg -> results.Add("Skipped: " + msg)

            let processed =
                { item with
                    Status = InboxStatus.Applied
                    ProcessedAt = Some clock.Now }

            do! inbox.Save processed
            return processed, List.ofSeq results
        }

    member _.Dismiss(item: InboxItem) : Async<InboxItem> =
        async {
            let dismissed =
                { item with
                    Status = InboxStatus.Dismissed
                    ProcessedAt = Some clock.Now }

            do! inbox.Save dismissed
            return dismissed
        }
