namespace UniversityOS.Application

open System
open System.Text.Json
open UniversityOS.Domain

/// Defensive JSON helpers for parsing model output. The model is asked for strict
/// JSON but is never trusted to produce it: every field read here is total.
module private AiJson =

    let tryStr (el: JsonElement) (name: string) =
        match el.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String ->
            let s = v.GetString()
            if String.IsNullOrWhiteSpace s then None else Some s
        | _ -> None

    let tryInt (el: JsonElement) (name: string) =
        match el.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Number -> Some(v.GetInt32())
        | true, v when v.ValueKind = JsonValueKind.String ->
            match Int32.TryParse(v.GetString()) with
            | true, n -> Some n
            | _ -> None
        | _ -> None

    let tryFloat (el: JsonElement) (name: string) =
        match el.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Number -> Some(v.GetDouble())
        | true, v when v.ValueKind = JsonValueKind.String ->
            match Double.TryParse(v.GetString(), Globalization.CultureInfo.InvariantCulture) with
            | true, n -> Some n
            | _ -> None
        | _ -> None

    let private dateFormats =
        [| "yyyy-MM-ddTHH:mm"; "yyyy-MM-ddTHH:mm:ss"; "yyyy-MM-dd HH:mm"; "yyyy-MM-dd" |]

    let parseDate (s: string) =
        match DateTime.TryParseExact(s, dateFormats, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.None) with
        | true, d -> Some d
        | _ ->
            match DateTime.TryParse(s, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.None) with
            | true, d -> Some d
            | _ -> None

    let tryDate (el: JsonElement) (name: string) =
        tryStr el name |> Option.bind parseDate

    /// Extracts the outermost JSON object from prose that may contain code fences.
    let extractObject (text: string) : JsonDocument option =
        let start = text.IndexOf '{'
        let finish = text.LastIndexOf '}'

        if start >= 0 && finish > start then
            try
                Some(JsonDocument.Parse(text.Substring(start, finish - start + 1)))
            with _ ->
                None
        else
            None

    let arrayItems (el: JsonElement) (name: string) =
        match el.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Array -> v.EnumerateArray() |> List.ofSeq
        | _ -> []

/// A change the assistant asked the app to make on the user's behalf.
type ChatAction =
    { Kind: string
      Target: string option
      Title: string option
      Date: DateTime option
      DurationMinutes: int option
      ModuleCode: string option
      EstimatedHours: float option
      Priority: string option
      Progress: int option
      EventKind: string option
      Body: string option }

type ChatOutcome =
    { Conversation: Conversation
      UserMessage: ChatMessage
      AssistantMessage: ChatMessage
      /// Human-readable descriptions of the data changes that were executed.
      AppliedActions: string list }

/// The application's AI brain. Assembles cross-application context into every request,
/// turns model output into typed domain changes, and degrades gracefully (to heuristics
/// or clear errors) when no provider is configured.
type AiService
    (
        provider: IAiProvider,
        modules: IModuleRepository,
        assignments: IAssignmentRepository,
        events: IEventRepository,
        notes: INoteRepository,
        sessions: ISessionRepository,
        chat: IChatRepository,
        settings: ISettingsStore,
        planner: PlannerService,
        activity: IActivityLog,
        memory: IMemoryRepository,
        uiState: IUiStateStore,
        clock: IClock
    ) =

    let truncate (n: int) (s: string) =
        if s.Length <= n then s else s.Substring(0, n) + "…"

    let normalizeCode (c: string) = c.Replace(" ", "").Replace("-", "").ToUpperInvariant()

    let resolveModule (allModules: StudyModule list) (code: string option) =
        code
        |> Option.bind (fun c ->
            allModules
            |> List.tryFind (fun m -> normalizeCode m.Code = normalizeCode c))

    let findAssignment (all: Assignment list) (fragment: string) =
        let f = fragment.Trim().ToLowerInvariant()

        all
        |> List.tryFind (fun a -> a.Title.ToLowerInvariant() = f)
        |> Option.orElse (all |> List.tryFind (fun a -> a.Title.ToLowerInvariant().Contains f))
        |> Option.orElse (
            match Guid.TryParse fragment with
            | true, g -> all |> List.tryFind (fun a -> a.Id.Value = g)
            | _ -> None
        )

    /// Cross-application context injected as the system prompt for every chat turn.
    member _.BuildContext() : Async<string> =
        async {
            let now = clock.Now
            let! userSettings = settings.Load()
            let! allModules = modules.GetAll()
            let! openAssignments = assignments.GetOpen()
            let! todayEvents = events.GetBetween(now.Date, now.Date.AddDays 1.0)
            let! weekEvents = events.GetBetween(now.Date, now.Date.AddDays 7.0)
            let! todaySessions = sessions.GetBetween(now.Date, now.Date.AddDays 1.0)
            let! allNotes = notes.GetAll()

            let sb = Text.StringBuilder()
            let line (s: string) = sb.AppendLine s |> ignore

            line "You are the AI assistant built into University OS, the user's personal academic operating system."
            line "You have live context below. Be concise, specific and practical. Use short Markdown. Never invent data that is not in the context; say when you don't know."
            line ""
            line (sprintf "Current date/time: %s" (now.ToString "dddd d MMMM yyyy, HH:mm"))

            if userSettings.UserName <> "" then
                line (sprintf "User: %s" userSettings.UserName)

            line ""
            line "## Modules"

            if allModules.IsEmpty then
                line "(none yet)"
            else
                for m in allModules |> List.filter (fun m -> not m.Archived) do
                    line (sprintf "- %s: %s%s" m.Code m.Name (m.Lecturer |> Option.map (sprintf " (lecturer: %s)") |> Option.defaultValue ""))

            line ""
            line "## Open assignments"

            if openAssignments.IsEmpty then
                line "(none)"
            else
                for a in openAssignments |> List.sortBy (fun a -> a.Deadline |> Option.defaultValue DateTime.MaxValue) |> List.truncate 30 do
                    line (
                        sprintf
                            "- \"%s\"%s — %s, %d%% done, %s priority%s"
                            a.Title
                            (a.ModuleId
                             |> Option.bind (fun mid -> allModules |> List.tryFind (fun m -> m.Id = mid))
                             |> Option.map (fun m -> " [" + m.Code + "]")
                             |> Option.defaultValue "")
                            (a.Deadline |> Option.map (fun d -> "due " + d.ToString "ddd d MMM HH:mm") |> Option.defaultValue "no deadline")
                            a.Progress
                            (Priority.label a.Priority)
                            (a.EstimatedHours |> Option.map (sprintf ", est. %gh") |> Option.defaultValue "")
                    )

            line ""
            line "## Today's fixed schedule"

            if todayEvents.IsEmpty then
                line "(no events today)"
            else
                for e in todayEvents |> List.sortBy (fun e -> e.StartsAt) do
                    line (sprintf "- %s–%s %s (%s)" (e.StartsAt.ToString "HH:mm") (e.EndsAt.ToString "HH:mm") e.Title (EventKind.label e.Kind))

            line ""
            line "## Today's planned study sessions"

            if todaySessions.IsEmpty then
                line "(none planned)"
            else
                for s in todaySessions |> List.sortBy (fun s -> s.StartsAt) do
                    line (sprintf "- %s–%s %s%s" (s.StartsAt.ToString "HH:mm") (s.EndsAt.ToString "HH:mm") s.Title (if s.Completed then " ✓" else ""))

            line ""
            line (sprintf "## This week: %d events. Recent notes: %s"
                    weekEvents.Length
                    (allNotes |> List.sortByDescending (fun n -> n.UpdatedAt) |> List.truncate 8 |> List.map (fun n -> "\"" + n.Title + "\"") |> String.concat ", "))

            // Long-term memory — only when the user has it enabled, and only the
            // entries they haven't disabled. Fully transparent (Settings → AI Memory).
            let! appState = uiState.Load()

            if appState.AiMemoryEnabled then
                let! memories = memory.GetAll()
                let enabled = memories |> List.filter (fun m -> m.Enabled)

                if not enabled.IsEmpty then
                    line ""
                    line "## What you've learned about this user (their editable memory)"

                    for m in enabled |> List.truncate 20 do
                        line (sprintf "- %s" m.Content)

            line ""
            line "## Making changes for the user"
            line "When (and only when) the user asks you to change data or their schedule — move a deadline, add/cancel events, replan the day, log progress, create assignments or notes — append at the VERY END of your reply exactly one fenced block:"
            line "```app_actions"
            line """{"actions":[{"kind":"..."}]}"""
            line "```"
            line """Action kinds: "regenerate_plan", "update_deadline", "add_event", "add_assignment", "add_note", "set_progress"."""
            line """Fields: "target" (existing assignment title fragment), "title", "date" ("yyyy-MM-ddTHH:mm"), "duration_minutes", "module_code", "estimated_hours", "priority" ("low"|"medium"|"high"|"critical"), "progress" (0–100), "event_kind" ("lecture"|"deadline"|"meeting"|"personal"|"exam"|"other"), "body"."""
            line "After changes that affect timing (new commitments, moved deadlines, a freed evening), include {\"kind\":\"regenerate_plan\"} as the LAST action so the schedule adapts."
            line "Do not mention the block or JSON in your prose — the app executes it and shows the user what changed. In prose, explain the change and the reasoning naturally."

            return sb.ToString()
        }

    member _.IsAvailable() = provider.IsConfigured()

    member private _.ParseChatActions(json: string) : ChatAction list =
        match AiJson.extractObject json with
        | None -> []
        | Some doc ->
            use doc = doc

            AiJson.arrayItems doc.RootElement "actions"
            |> List.choose (fun el ->
                match AiJson.tryStr el "kind" with
                | None -> None
                | Some kind ->
                    Some
                        { Kind = kind
                          Target = AiJson.tryStr el "target"
                          Title = AiJson.tryStr el "title"
                          Date = AiJson.tryDate el "date"
                          DurationMinutes = AiJson.tryInt el "duration_minutes"
                          ModuleCode = AiJson.tryStr el "module_code"
                          EstimatedHours = AiJson.tryFloat el "estimated_hours"
                          Priority = AiJson.tryStr el "priority"
                          Progress = AiJson.tryInt el "progress"
                          EventKind = AiJson.tryStr el "event_kind"
                          Body = AiJson.tryStr el "body" })

    /// Executes actions the assistant requested. Every branch returns a short
    /// past-tense description shown to the user, or a failure reason.
    member private _.ExecuteAction(action: ChatAction) : Async<Result<string, string>> =
        async {
            let now = clock.Now
            let! allModules = modules.GetAll()

            match action.Kind with
            | "regenerate_plan" ->
                let! result = planner.Regenerate()
                return Ok("Replanned: " + result.Summary)

            | "update_deadline" ->
                match action.Target |> Option.orElse action.Title, action.Date with
                | Some fragment, Some date ->
                    let! all = assignments.GetAll()

                    match findAssignment all fragment with
                    | Some a ->
                        do! assignments.Save { a with Deadline = Some date; UpdatedAt = now }
                        return Ok(sprintf "Moved deadline of \"%s\" to %s" a.Title (date.ToString "ddd d MMM HH:mm"))
                    | None -> return Error(sprintf "No assignment matching \"%s\"" fragment)
                | _ -> return Error "update_deadline needs a target and a date"

            | "add_event" ->
                match action.Title, action.Date with
                | Some title, Some date ->
                    let duration = action.DurationMinutes |> Option.defaultValue 60

                    let kind =
                        match action.EventKind |> Option.map (fun k -> k.ToLowerInvariant()) with
                        | Some "lecture" -> EventKind.Lecture
                        | Some "deadline" -> EventKind.Deadline
                        | Some "meeting" -> EventKind.Meeting
                        | Some "exam" -> EventKind.Exam
                        | Some "personal" -> EventKind.Personal
                        | _ -> EventKind.Personal

                    let ev =
                        { CalendarEvent.create now title kind date (date.AddMinutes(float duration)) with
                            ModuleId = resolveModule allModules action.ModuleCode |> Option.map (fun m -> m.Id) }

                    do! events.Save ev
                    return Ok(sprintf "Added \"%s\" on %s" title (date.ToString "ddd d MMM HH:mm"))
                | _ -> return Error "add_event needs a title and a date"

            | "add_assignment" ->
                match action.Title with
                | Some title ->
                    let a =
                        { Assignment.create now title with
                            ModuleId = resolveModule allModules action.ModuleCode |> Option.map (fun m -> m.Id)
                            Deadline = action.Date
                            EstimatedHours = action.EstimatedHours
                            Priority =
                                match action.Priority |> Option.map (fun p -> p.ToLowerInvariant()) with
                                | Some "low" -> Priority.Low
                                | Some "high" -> Priority.High
                                | Some "critical" -> Priority.Critical
                                | _ -> Priority.Medium }

                    do! assignments.Save a
                    return Ok(sprintf "Created assignment \"%s\"" title)
                | None -> return Error "add_assignment needs a title"

            | "add_note" ->
                match action.Title with
                | Some title ->
                    let note =
                        { Note.create now title with
                            Content = action.Body |> Option.defaultValue ""
                            ModuleId = resolveModule allModules action.ModuleCode |> Option.map (fun m -> m.Id) }

                    do! notes.Save note
                    return Ok(sprintf "Created note \"%s\"" title)
                | None -> return Error "add_note needs a title"

            | "set_progress" ->
                match action.Target |> Option.orElse action.Title, action.Progress with
                | Some fragment, Some progress ->
                    let! all = assignments.GetAll()

                    match findAssignment all fragment with
                    | Some a ->
                        do! assignments.Save { a with Progress = max 0 (min 100 progress); UpdatedAt = now }
                        return Ok(sprintf "Set \"%s\" to %d%%" a.Title progress)
                    | None -> return Error(sprintf "No assignment matching \"%s\"" fragment)
                | _ -> return Error "set_progress needs a target and a progress value"

            | other -> return Error(sprintf "Unknown action kind \"%s\"" other)
        }

    /// One full chat turn: context assembly → completion → action execution → persistence.
    member this.Chat(conversation: Conversation option, userText: string) : Async<Result<ChatOutcome, AiError>> =
        async {
            let now = clock.Now
            let! userSettings = settings.Load()

            let conv =
                match conversation with
                | Some c -> c
                | None -> Conversation.create now (truncate 48 (userText.Trim()))

            let! history =
                match conversation with
                | Some c -> chat.GetMessages c.Id
                | None -> async.Return []

            let! system = this.BuildContext()

            let request =
                { System = Some system
                  Messages =
                    (history
                     |> List.map (fun m ->
                         { Role = (match m.Role with ChatRole.User -> AiRole.User | ChatRole.Assistant -> AiRole.Assistant)
                           Content = m.Content }))
                    @ [ { Role = AiRole.User; Content = userText } ]
                  MaxTokens = 4096
                  Model = Some userSettings.AiModel }

            let! result = provider.Complete request

            match result with
            | Error e -> return Error e
            | Ok response ->
                // Split prose from the machine-readable action block, execute the actions.
                let prose, actionsJson =
                    let idx = response.Text.IndexOf "```app_actions"

                    if idx < 0 then
                        response.Text.Trim(), None
                    else
                        let afterFence = idx + "```app_actions".Length
                        let closing = response.Text.IndexOf("```", afterFence)

                        let json =
                            if closing > afterFence then response.Text.Substring(afterFence, closing - afterFence)
                            else response.Text.Substring afterFence

                        let rest = if closing > 0 then response.Text.Substring(closing + 3) else ""
                        (response.Text.Substring(0, idx) + rest).Trim(), Some(json.Trim())

                let actions =
                    actionsJson |> Option.map this.ParseChatActions |> Option.defaultValue []

                let applied = ResizeArray<string>()

                for action in actions do
                    let! outcome = this.ExecuteAction action

                    match outcome with
                    | Ok msg ->
                        applied.Add msg
                        // Assistant-made changes belong on the timeline too.
                        do! activity.Log(ActivityEntry.create clock.Now ActivityKind.AiAction msg (Some "Requested in chat") None)
                    | Error msg -> applied.Add("Could not apply a change: " + msg)

                let finalText =
                    if applied.Count = 0 then prose
                    else
                        prose
                        + "\n\n"
                        + (applied |> Seq.map (fun a -> "✓ " + a) |> String.concat "\n")

                let userMsg = ChatMessage.create now conv.Id ChatRole.User userText
                let assistantMsg = ChatMessage.create clock.Now conv.Id ChatRole.Assistant finalText

                do! chat.SaveConversation { conv with UpdatedAt = clock.Now }
                do! chat.SaveMessage userMsg
                do! chat.SaveMessage assistantMsg

                return
                    Ok
                        { Conversation = conv
                          UserMessage = userMsg
                          AssistantMessage = assistantMsg
                          AppliedActions = List.ofSeq applied }
        }

    /// A single short paragraph for briefings/reviews. Returns None whenever the
    /// provider is unconfigured or fails — callers always have a template fallback.
    member _.OneLiner(instruction: string, contextText: string) : Async<string option> =
        async {
            let! configured = provider.IsConfigured()

            if not configured then
                return None
            else
                let! userSettings = settings.Load()

                let request =
                    { AiRequest.simple (Some instruction) (truncate 6000 contextText) with
                        Model = Some userSettings.AiModel
                        MaxTokens = 300 }

                let! result = provider.Complete request

                match result with
                | Ok response -> return Some(response.Text.Trim())
                | Error _ -> return None
        }

    /// Reflect on recent behavior and produce habit observations as
    /// (category, content) pairs. Used by the memory system's AI refresh.
    member _.ObserveHabits(activitySummary: string, existingMemories: string) : Async<Result<(string * string) list, AiError>> =
        async {
            let! userSettings = settings.Load()

            let system =
                """You observe a student's working habits for a transparent, user-editable memory. Reply with ONLY JSON: {"observations":[{"category":"pace|schedule|habits","content":"..."}]}. 2–5 observations. Each must be a concrete, useful pattern grounded in the data ("often completes sessions in the evening"), never a judgment or a hidden profile. If the data is too thin, return an empty list."""

            let request =
                { AiRequest.simple
                    (Some system)
                    (sprintf "Recent activity:\n%s\n\nCurrent memory entries:\n%s" activitySummary existingMemories) with
                    Model = Some userSettings.AiModel }

            let! result = provider.Complete request

            match result with
            | Error e -> return Error e
            | Ok response ->
                match AiJson.extractObject response.Text with
                | None -> return Ok []
                | Some doc ->
                    use doc = doc

                    return
                        Ok(
                            AiJson.arrayItems doc.RootElement "observations"
                            |> List.choose (fun el ->
                                match AiJson.tryStr el "content" with
                                | Some content ->
                                    Some(AiJson.tryStr el "category" |> Option.defaultValue "habits", content)
                                | None -> None)
                        )
        }

    /// Summarize arbitrary content (documents, lectures, notes).
    member _.Summarize(title: string, text: string) : Async<Result<string, AiError>> =
        async {
            let! userSettings = settings.Load()

            let request =
                { AiRequest.simple
                    (Some
                        "You summarize academic material for a university student. Produce a tight Markdown summary: 2–3 sentence overview, then key points as bullets, then any deadlines/action items under an '**Action items**' heading (omit the heading if none). No preamble.")
                    (sprintf "Title: %s\n\n%s" title (truncate 24000 text)) with
                    Model = Some userSettings.AiModel
                    MaxTokens = 1500 }

            let! result = provider.Complete request
            return result |> Result.map (fun r -> r.Text.Trim())
        }

    /// Classify a captured item into proposed actions. Falls back to the offline
    /// heuristic classifier whenever the provider is missing or fails — capture
    /// must never be blocked by the network.
    member _.ClassifyCapture(title: string, body: string) : Async<CaptureAnalysis> =
        async {
            let now = clock.Now
            let! allModules = modules.GetAll()
            let knownCodes = allModules |> List.map (fun m -> m.Code)
            let heuristic () = CaptureHeuristics.analyze now knownCodes title body

            let! configured = provider.IsConfigured()

            if not configured then
                return heuristic ()
            else
                let! userSettings = settings.Load()
                let! openAssignments = assignments.GetOpen()

                let system =
                    let sb = Text.StringBuilder()
                    let line (s: string) = sb.AppendLine s |> ignore
                    line "You classify captured items for a university productivity app. Reply with ONLY a JSON object, no prose, no code fences:"
                    line """{"summary": "...", "module_code": "..." or null, "actions": [{"kind": "...", "title": "...", "module_code": "..." or null, "date": "yyyy-MM-ddTHH:mm" or null, "duration_minutes": 60 or null, "body": "..." or null, "target_id": "..." or null}]}"""
                    line """Kinds: "create_assignment" (new coursework with a deadline), "update_deadline" (an EXISTING assignment's deadline changed — set target_id from the list below), "create_event" (a scheduled occurrence: lecture/meeting/exam), "create_note" (information worth keeping), "link_to_module" (file belongs to a module, nothing else to do), "no_action"."""
                    line "Propose every action that applies, most important first. Extract dates precisely."
                    line (sprintf "Today is %s." (now.ToString "dddd d MMMM yyyy"))
                    line (sprintf "Known modules: %s." (if allModules.IsEmpty then "(none)" else allModules |> List.map (fun m -> m.Code + " = " + m.Name) |> String.concat "; "))

                    line (
                        sprintf
                            "Existing open assignments: %s."
                            (if openAssignments.IsEmpty then "(none)"
                             else
                                 openAssignments
                                 |> List.truncate 30
                                 |> List.map (fun a -> sprintf "%s = \"%s\"" (string a.Id.Value) a.Title)
                                 |> String.concat "; ")
                    )

                    sb.ToString()

                let request =
                    { AiRequest.simple (Some system) (sprintf "Title: %s\n\nContent:\n%s" title (truncate 12000 body)) with
                        Model = Some userSettings.AiModel }

                let! result = provider.Complete request

                match result with
                | Error _ -> return heuristic ()
                | Ok response ->
                    match AiJson.extractObject response.Text with
                    | None -> return heuristic ()
                    | Some doc ->
                        use doc = doc
                        let root = doc.RootElement

                        let actions =
                            AiJson.arrayItems root "actions"
                            |> List.choose (fun el ->
                                AiJson.tryStr el "kind"
                                |> Option.bind ProposedActionKind.parse
                                |> Option.map (fun kind ->
                                    { Kind = kind
                                      Title = AiJson.tryStr el "title" |> Option.defaultValue (truncate 80 title)
                                      ModuleCode = AiJson.tryStr el "module_code"
                                      Date = AiJson.tryDate el "date"
                                      DurationMinutes = AiJson.tryInt el "duration_minutes"
                                      Body = AiJson.tryStr el "body"
                                      TargetId = AiJson.tryStr el "target_id" }))

                        if actions.IsEmpty then
                            return heuristic ()
                        else
                            return
                                { Summary = AiJson.tryStr root "summary" |> Option.defaultValue (truncate 180 title)
                                  SuggestedModuleCode = AiJson.tryStr root "module_code"
                                  Actions = actions
                                  Analyzer = "ai" }
        }

    /// Generate flashcards from a note (or any text) for revision.
    member _.GenerateFlashcards(sourceTitle: string, text: string, moduleId: ModuleId option, source: EntityRef option) : Async<Result<RevisionItem list, AiError>> =
        async {
            let now = clock.Now
            let! userSettings = settings.Load()

            let system =
                """You create revision flashcards from a student's material. Reply with ONLY JSON, no prose: {"cards":[{"question":"...","answer":"..."}]}. 5–12 cards. Questions test understanding, not trivia; answers are complete but short."""

            let request =
                { AiRequest.simple (Some system) (sprintf "Material: %s\n\n%s" sourceTitle (truncate 16000 text)) with
                    Model = Some userSettings.AiModel }

            let! result = provider.Complete request

            match result with
            | Error e -> return Error e
            | Ok response ->
                match AiJson.extractObject response.Text with
                | None -> return Error(AiError.Provider "The model did not return valid flashcard JSON.")
                | Some doc ->
                    use doc = doc

                    let cards =
                        AiJson.arrayItems doc.RootElement "cards"
                        |> List.choose (fun el ->
                            match AiJson.tryStr el "question", AiJson.tryStr el "answer" with
                            | Some q, Some a ->
                                Some
                                    { RevisionItem.create now q a with
                                        ModuleId = moduleId
                                        Source = source }
                            | _ -> None)

                    return Ok cards
        }
