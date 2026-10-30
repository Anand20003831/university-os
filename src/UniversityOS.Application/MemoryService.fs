namespace UniversityOS.Application

open System
open UniversityOS.Domain

/// Manages the transparent long-term memory: deterministic stats recomputation
/// and AI habit reflection. Both replace only their own rows (by source) so
/// user-authored entries are never touched by automation.
type MemoryService
    (
        memory: IMemoryRepository,
        assignments: IAssignmentRepository,
        sessions: ISessionRepository,
        uiState: IUiStateStore,
        ai: AiService,
        activity: IActivityLog,
        clock: IClock
    ) =

    /// Recompute the deterministic habit statistics and replace all
    /// source=Stats entries with fresh ones. Returns what was written.
    member _.ComputeStats() : Async<MemoryItem list> =
        async {
            let now = clock.Now
            let! allAssignments = assignments.GetAll()
            let! recentSessions = sessions.GetBetween(now.AddDays -60.0, now)
            let completed = recentSessions |> List.filter (fun s -> s.Completed)

            let items =
                [ match MemoryStats.paceFactor allAssignments with
                  | Some pace ->
                      MemoryItem.create
                          now
                          MemorySource.Stats
                          "pace"
                          (sprintf
                              "Finished work typically takes about %.1f× the original estimate — the planner already accounts for this."
                              pace)
                  | None -> ()

                  match MemoryStats.averageSessionMinutes completed with
                  | Some minutes ->
                      MemoryItem.create
                          now
                          MemorySource.Stats
                          "sessions"
                          (sprintf "Completed study sessions average about %d minutes." minutes)
                  | None -> ()

                  match MemoryStats.preferredHour completed with
                  | Some hour ->
                      MemoryItem.create
                          now
                          MemorySource.Stats
                          "schedule"
                          (sprintf "Study sessions are most often completed around %02d:00." hour)
                  | None -> ()

                  match MemoryStats.productiveWeekday completed with
                  | Some day ->
                      MemoryItem.create
                          now
                          MemorySource.Stats
                          "schedule"
                          (sprintf "Recently, %A has been the most productive day." day)
                  | None -> () ]

            do! memory.DeleteBySource MemorySource.Stats

            for item in items do
                do! memory.Save item

            return items
        }

    /// Ask the AI to reflect on the last two weeks of activity and refresh its
    /// observations (source=Ai rows only). No-op when memory is disabled.
    member _.Reflect() : Async<Result<int, string>> =
        async {
            let! state = uiState.Load()

            if not state.AiMemoryEnabled then
                return Error "AI memory is disabled in Settings."
            else
                let now = clock.Now
                let! recent = activity.GetBetween(now.AddDays -14.0, now)
                let! existing = memory.GetAll()

                let activitySummary =
                    recent
                    |> List.countBy (fun a -> a.Kind)
                    |> List.map (fun (kind, count) -> sprintf "%s ×%d" (ActivityKind.label kind) count)
                    |> String.concat "; "
                    |> fun s ->
                        let sessionTimes =
                            recent
                            |> List.filter (fun a -> a.Kind = ActivityKind.SessionCompleted)
                            |> List.map (fun a -> a.OccurredAt.ToString "ddd HH:mm")
                            |> String.concat ", "

                        if sessionTimes = "" then s
                        else s + ".\nCompleted-session times: " + sessionTimes

                let existingText =
                    existing
                    |> List.map (fun m -> sprintf "[%s] %s" (MemorySource.toString m.Source) m.Content)
                    |> String.concat "\n"

                let! result = ai.ObserveHabits(activitySummary, existingText)

                match result with
                | Error e -> return Error(AiError.describe e)
                | Ok observations ->
                    do! memory.DeleteBySource MemorySource.Ai

                    for category, content in observations |> List.truncate 5 do
                        do! memory.Save(MemoryItem.create now MemorySource.Ai category content)

                    return Ok observations.Length
        }
