namespace UniversityOS.Application

open System
open UniversityOS.Domain
open UniversityOS.Domain.Planning

/// Everything the dashboard needs, gathered in one round trip.
type DashboardData =
    { Now: DateTime
      TodayEvents: CalendarEvent list
      TodaySessions: PlannedSession list
      Recommendation: (PlannableTask * string) option
      /// Deadlines within the next 14 days, soonest first, with module code.
      UpcomingDeadlines: (Assignment * string option) list
      InboxCount: int
      RecentNotes: Note list
      OpenAssignmentCount: int
      /// Mean progress across open assignments, when any exist.
      AverageProgress: float option
      /// Free minutes remaining today inside the workday window.
      FreeMinutesToday: int
      PlannedMinutesToday: int
      /// Study actually completed today, and the user's configured daily target.
      StudiedMinutesToday: int
      DailyTargetMinutes: int
      CompletedSessionsToday: int
      /// The same, over the current week (Monday-based).
      StudiedMinutesThisWeek: int
      WeeklyTargetMinutes: int
      /// Mean progress per module across its open assignments, module code first.
      /// Only modules that actually have open work appear.
      ModuleProgress: (string * int) list
      /// Most recent activity-log entries, newest first.
      RecentActivity: ActivityEntry list
      /// Total notes held, for the productivity snapshot.
      NoteCount: int }

type DashboardService
    (
        assignments: IAssignmentRepository,
        events: IEventRepository,
        sessions: ISessionRepository,
        notes: INoteRepository,
        inbox: IInboxRepository,
        modules: IModuleRepository,
        settings: ISettingsStore,
        planner: PlannerService,
        activity: IActivityLog,
        clock: IClock
    ) =

    member _.GetData() : Async<DashboardData> =
        async {
            let now = clock.Now
            let! userSettings = settings.Load()
            let prefs = PlannerPreferences.ofSettings userSettings

            let! todayEvents = events.GetBetween(now.Date, now.Date.AddDays 1.0)
            let! todaySessions = sessions.GetBetween(now.Date, now.Date.AddDays 1.0)
            let! openAssignments = assignments.GetOpen()
            let! allModules = modules.GetAll()
            let! allNotes = notes.GetAll()
            let! activeInbox = inbox.GetActive()
            let! recommendation = planner.RecommendNext()

            let codeOf moduleId =
                moduleId
                |> Option.bind (fun mid -> allModules |> List.tryFind (fun m -> m.Id = mid))
                |> Option.map (fun m -> m.Code)

            let upcomingDeadlines =
                openAssignments
                |> List.choose (fun a -> a.Deadline |> Option.map (fun d -> a, d))
                |> List.filter (fun (_, d) -> d >= now && d <= now.AddDays 14.0)
                |> List.sortBy snd
                |> List.map (fun (a, _) -> a, codeOf a.ModuleId)

            let busy =
                todayEvents
                |> List.filter (fun e -> not e.AllDay)
                |> List.map (fun e -> { Start = e.StartsAt; End = e.EndsAt; Label = e.Title })

            let freeMinutes =
                Planning.freeSlotsForDay prefs busy now now
                |> List.sumBy (fun (s, e) -> int (e - s).TotalMinutes)

            let plannedMinutes =
                todaySessions
                |> List.filter (fun s -> not s.Completed && s.EndsAt > now)
                |> List.sumBy PlannedSession.durationMinutes

            // --- study progress -------------------------------------------
            // Everything below is derived in this same pass from data already
            // fetched, apart from one week-range session query and one activity
            // read. The dashboard makes ONE service call; adding a second call
            // from the view would double the work on every navigation.

            let completedToday = todaySessions |> List.filter (fun s -> s.Completed)
            let studiedToday = completedToday |> List.sumBy PlannedSession.durationMinutes

            let dailyTargetMinutes = int (userSettings.MaxDailyStudyHours * 60.0)

            // **The one week definition**, shared with Assignments, Planner and
            // Calendar. This was a Monday-based calculation of its own, which
            // silently disagreed with every other screen under a culture whose
            // week starts on Sunday.
            let weekStart =
                StudyTime.weekStart
                    System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek
                    now
            let! weekSessions = sessions.GetBetween(weekStart, weekStart.AddDays 7.0)

            let studiedThisWeek =
                weekSessions
                |> List.filter (fun s -> s.Completed)
                |> List.sumBy PlannedSession.durationMinutes

            // Mean progress per module, over that module's OPEN assignments —
            // a module with nothing outstanding has no meaningful progress bar
            // and is left out rather than shown at 0%.
            let moduleProgress =
                allModules
                |> List.choose (fun m ->
                    match openAssignments |> List.filter (fun a -> a.ModuleId = Some m.Id) with
                    | [] -> None
                    | items -> Some(m.Code, int (items |> List.averageBy (fun a -> float a.Progress))))

            let! recentActivity = activity.GetRecent 6

            return
                { Now = now
                  TodayEvents = todayEvents |> List.sortBy (fun e -> e.StartsAt)
                  TodaySessions = todaySessions |> List.sortBy (fun s -> s.StartsAt)
                  Recommendation = recommendation
                  UpcomingDeadlines = upcomingDeadlines
                  InboxCount = activeInbox.Length
                  RecentNotes = allNotes |> List.sortByDescending (fun n -> n.UpdatedAt) |> List.truncate 5
                  OpenAssignmentCount = openAssignments.Length
                  AverageProgress =
                    match openAssignments with
                    | [] -> None
                    | items -> Some(items |> List.averageBy (fun a -> float a.Progress))
                  FreeMinutesToday = freeMinutes
                  PlannedMinutesToday = plannedMinutes
                  StudiedMinutesToday = studiedToday
                  DailyTargetMinutes = dailyTargetMinutes
                  CompletedSessionsToday = completedToday.Length
                  StudiedMinutesThisWeek = studiedThisWeek
                  WeeklyTargetMinutes = dailyTargetMinutes * 7
                  ModuleProgress = moduleProgress
                  RecentActivity = recentActivity
                  NoteCount = allNotes.Length }
        }
