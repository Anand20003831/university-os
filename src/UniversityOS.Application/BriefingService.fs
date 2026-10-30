namespace UniversityOS.Application

open System
open UniversityOS.Domain

/// Data for the Morning Briefing — the "personal assistant opens your day" view.
type MorningBriefing =
    { Now: DateTime
      Data: DashboardData
      /// Today's lecture/exam events, pulled out for their own section.
      Lectures: CalendarEvent list
      /// What connectors/captures changed since yesterday morning.
      RecentChanges: ActivityEntry list
      /// What got done yesterday — the "✓" list.
      Yesterday: ActivityEntry list
      /// AI-written motivational line; None → the view renders a template line.
      AiSummary: string option }

/// Data for the Evening Review — reflection, not statistics.
type EveningReview =
    { Now: DateTime
      TodayActivity: ActivityEntry list
      CompletedSessionCount: int
      MinutesStudied: int
      NotesCreated: int
      AssignmentsProgressed: int
      TomorrowEvents: CalendarEvent list
      TomorrowRecommendation: (Planning.PlannableTask * string) option
      AiReflection: string option }

/// Builds the Morning Briefing and Evening Review from live data + the activity
/// timeline, with optional AI-written summary lines.
type BriefingService
    (
        dashboard: DashboardService,
        events: IEventRepository,
        sessions: ISessionRepository,
        planner: PlannerService,
        activity: IActivityLog,
        ai: AiService,
        clock: IClock
    ) =

    let changeKinds =
        [ ActivityKind.ConnectorSynced
          ActivityKind.CaptureApplied
          ActivityKind.DeadlineChanged
          ActivityKind.DocumentImported
          ActivityKind.EventAdded
          ActivityKind.AiAction ]

    let accomplishmentKinds =
        [ ActivityKind.SessionCompleted
          ActivityKind.AssignmentCreated
          ActivityKind.AssignmentUpdated
          ActivityKind.NoteCreated
          ActivityKind.NoteUpdated
          ActivityKind.FlashcardsGenerated
          ActivityKind.LectureAdded ]

    member _.GetMorning() : Async<MorningBriefing> =
        async {
            let now = clock.Now
            let! data = dashboard.GetData()

            let lectures =
                data.TodayEvents
                |> List.filter (fun e -> e.Kind = EventKind.Lecture || e.Kind = EventKind.Exam)

            let! sinceYesterday = activity.GetBetween(now.Date.AddDays -1.0, now)

            let recentChanges =
                sinceYesterday
                |> List.filter (fun a -> changeKinds |> List.contains a.Kind)
                |> List.truncate 8

            let yesterday =
                sinceYesterday
                |> List.filter (fun a ->
                    a.OccurredAt < now.Date && accomplishmentKinds |> List.contains a.Kind)
                |> List.truncate 8

            let context =
                let sb = Text.StringBuilder()
                let line (s: string) = sb.AppendLine s |> ignore
                line (sprintf "Free study time today: %d minutes." data.FreeMinutesToday)
                line (sprintf "Fixed events today: %d (%d lectures)." data.TodayEvents.Length lectures.Length)

                match data.Recommendation with
                | Some(task, reason) -> line (sprintf "Recommended first task: %s (%s)." task.Title reason)
                | None -> line "No urgent task recommendation."

                match data.UpcomingDeadlines |> List.tryHead with
                | Some(a, _) ->
                    line (
                        sprintf
                            "Next deadline: %s%s."
                            a.Title
                            (a.Deadline |> Option.map (fun d -> " on " + d.ToString "ddd d MMM") |> Option.defaultValue "")
                    )
                | None -> ()

                sb.ToString()

            let! aiSummary =
                ai.OneLiner(
                    "You write the one-paragraph morning summary for a student's dashboard. Two sentences max: concrete, warm, specific to the data given, no fluff, no greetings.",
                    context
                )

            return
                { Now = now
                  Data = data
                  Lectures = lectures
                  RecentChanges = recentChanges
                  Yesterday = yesterday
                  AiSummary = aiSummary }
        }

    member _.GetEvening() : Async<EveningReview> =
        async {
            let now = clock.Now
            let! todayActivity = activity.GetBetween(now.Date, now)
            let! todaySessions = sessions.GetBetween(now.Date, now.Date.AddDays 1.0)

            let completed = todaySessions |> List.filter (fun s -> s.Completed)
            let minutes = completed |> List.sumBy PlannedSession.durationMinutes

            let count kind =
                todayActivity |> List.filter (fun a -> a.Kind = kind) |> List.length

            let! tomorrowEvents = events.GetBetween(now.Date.AddDays 1.0, now.Date.AddDays 2.0)
            let! recommendation = planner.RecommendNext()

            let context =
                let sb = Text.StringBuilder()
                let line (s: string) = sb.AppendLine s |> ignore
                line (sprintf "Completed study sessions today: %d (%d minutes)." completed.Length minutes)
                line (sprintf "Assignments progressed: %d. Notes written: %d." (count ActivityKind.AssignmentUpdated) (count ActivityKind.NoteCreated + count ActivityKind.NoteUpdated))
                line (sprintf "Tomorrow has %d fixed events." tomorrowEvents.Length)

                match recommendation with
                | Some(task, reason) -> line (sprintf "Best next task: %s (%s)." task.Title reason)
                | None -> ()

                sb.ToString()

            let! aiReflection =
                ai.OneLiner(
                    "You write the one-paragraph evening reflection for a student. Two sentences max: acknowledge today's effort honestly based on the data, then one concrete suggestion for this evening or tomorrow. No fluff.",
                    context
                )

            return
                { Now = now
                  TodayActivity = todayActivity |> List.truncate 12
                  CompletedSessionCount = completed.Length
                  MinutesStudied = minutes
                  NotesCreated = count ActivityKind.NoteCreated
                  AssignmentsProgressed = count ActivityKind.AssignmentUpdated
                  TomorrowEvents = tomorrowEvents |> List.sortBy (fun e -> e.StartsAt)
                  TomorrowRecommendation = recommendation
                  AiReflection = aiReflection }
        }
