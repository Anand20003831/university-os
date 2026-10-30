namespace UniversityOS.Application

open System
open UniversityOS.Domain
open UniversityOS.Domain.Planning

/// Orchestrates the pure planning algorithm: loads state, runs `Planning.plan`,
/// persists the resulting study sessions.
type PlannerService
    (
        assignments: IAssignmentRepository,
        events: IEventRepository,
        sessions: ISessionRepository,
        modules: IModuleRepository,
        milestoneRepo: IMilestoneRepository,
        settings: ISettingsStore,
        activity: IActivityLog,
        clock: IClock
    ) =

    /// Open assignments as planner tasks, with module codes resolved for display.
    /// Personalization: remaining effort is scaled by the user's historical pace
    /// (actual vs estimated hours on closed work), so plans reflect how they
    /// really work — not just their optimism.
    member _.BuildPlannables() : Async<PlannableTask list> =
        async {
            let! allAssignments = assignments.GetAll()
            let! allModules = modules.GetAll()
            let! allMilestones = milestoneRepo.GetAll()

            let openAssignments = allAssignments |> List.filter Assignment.isOpen
            let pace = MemoryStats.paceFactor allAssignments |> Option.defaultValue 1.0

            let codeOf moduleId =
                moduleId
                |> Option.bind (fun mid -> allModules |> List.tryFind (fun m -> m.Id = mid))
                |> Option.map (fun m -> m.Code)

            return
                openAssignments
                |> List.map (fun a ->
                    { AssignmentId = a.Id
                      Title = a.Title
                      ModuleCode = codeOf a.ModuleId
                      Deadline = a.Deadline
                      RemainingMinutes = int (float (Assignment.remainingMinutes a) * pace)
                      Priority = a.Priority
                      Weighting = a.Weighting
                      // Outstanding only: a step already submitted is not work
                      // left to schedule, and planning against it would book
                      // time for something that is finished.
                      Milestones =
                        allMilestones
                        |> List.filter (fun m -> m.AssignmentId = a.Id)
                        |> Milestone.outstanding
                        |> Milestone.ordered })
        }

    member this.BuildInput() : Async<PlannerInput> =
        async {
            let now = clock.Now
            let! userSettings = settings.Load()
            let prefs = PlannerPreferences.ofSettings userSettings
            let! tasks = this.BuildPlannables()
            let! fixedEvents = events.GetBetween(now.Date, now.Date.AddDays(float prefs.HorizonDays + 1.0))

            let busy =
                fixedEvents
                |> List.filter (fun e -> not e.AllDay && e.Kind <> EventKind.StudySession)
                |> List.map (fun e ->
                    { Start = e.StartsAt
                      End = e.EndsAt
                      Label = e.Title })

            return
                { Now = now
                  Tasks = tasks
                  Busy = busy
                  Prefs = prefs }
        }

    /// Runs the real scheduler against adjusted preferences and persists NOTHING.
    ///
    /// This is what an "alternative plan" is here: a genuine planning run whose
    /// blocks, unplanned tasks and summary can be compared against the current
    /// plan — not a description of a plan, and not a decorative pill. The caller
    /// gets a real `PlannerResult` produced by the same `Planning.plan` as every
    /// other plan in the application.
    member this.PreviewWith(adjust: PlannerPreferences -> PlannerPreferences) : Async<PlannerResult> =
        async {
            let! input = this.BuildInput()
            return Planning.plan { input with Prefs = adjust input.Prefs }
        }

    /// Regenerates the plan from now onward and persists it. Completed sessions and
    /// the past are never rewritten.
    member this.Regenerate() : Async<PlannerResult> = this.RegenerateWith("", id)

    /// Regenerates and persists using adjusted preferences.
    ///
    /// `label` names the strategy in the activity log so the planner history
    /// records WHICH plan was generated, not merely that one was. An empty label
    /// is the user's own saved settings and logs exactly as it always did — the
    /// Timeline reads these entries, so the default wording must not drift.
    member this.RegenerateWith(label: string, adjust: PlannerPreferences -> PlannerPreferences) : Async<PlannerResult> =
        async {
            let now = clock.Now
            let! input = this.BuildInput()
            let result = Planning.plan { input with Prefs = adjust input.Prefs }

            let newSessions =
                result.Blocks
                |> List.map (fun b ->
                    { Id = SessionId.New()
                      AssignmentId = Some b.AssignmentId
                      MilestoneId = b.MilestoneId
                      Title = b.Title
                      ModuleCode = b.ModuleCode
                      StartsAt = b.Start
                      EndsAt = b.End
                      Reason = b.Reason
                      Completed = false
                      CreatedAt = now })

            do! sessions.ReplaceFrom(now, newSessions)

            let detail =
                if System.String.IsNullOrWhiteSpace label then result.Summary
                else sprintf "%s · %s" label result.Summary

            do! activity.Log(
                    ActivityEntry.create now ActivityKind.PlanGenerated "Study plan regenerated" (Some detail) None
                )

            return result
        }

    member _.GetSessions(rangeStart: DateTime, rangeEnd: DateTime) =
        sessions.GetBetween(rangeStart, rangeEnd)

    /// The single best thing to do right now, with the reasoning.
    member this.RecommendNext() : Async<(PlannableTask * string) option> =
        async {
            let! tasks = this.BuildPlannables()
            return Planning.recommendNext clock.Now tasks
        }
