namespace UniversityOS.Domain

open System

/// The pure scheduling engine. No I/O, fully deterministic: given the same input it
/// always produces the same plan, which makes it trivially unit-testable.
module Planning =

    type PlannerPreferences =
        { WorkdayStart: TimeSpan
          WorkdayEnd: TimeSpan
          SessionMinutes: int
          BreakMinutes: int
          MaxDailyStudyMinutes: int
          /// Aim to have work finished this long before its deadline.
          DeadlineBufferHours: float
          HorizonDays: int }

    [<RequireQualifiedAccess>]
    module PlannerPreferences =
        let ofSettings (s: UserSettings) =
            { WorkdayStart = TimeSpan.FromHours(float s.WorkdayStartHour)
              WorkdayEnd = TimeSpan.FromHours(float s.WorkdayEndHour)
              SessionMinutes = max 25 s.SessionMinutes
              BreakMinutes = max 5 s.BreakMinutes
              MaxDailyStudyMinutes = int (s.MaxDailyStudyHours * 60.0)
              DeadlineBufferHours = s.DeadlineBufferHours
              HorizonDays = max 1 s.PlannerHorizonDays }

    /// An assignment reduced to what scheduling needs.
    type PlannableTask =
        { AssignmentId: AssignmentId
          Title: string
          ModuleCode: string option
          Deadline: DateTime option
          RemainingMinutes: int
          Priority: Priority
          Weighting: float option
          /// Outstanding milestones, in the user's order.
          ///
          /// Empty is a real case, not a missing one: an assignment nobody has
          /// broken down still schedules, and its blocks carry the assignment's
          /// own title as they always did.
          Milestones: Milestone list }

    /// Time already committed: lectures, meetings, personal events.
    type BusyBlock =
        { Start: DateTime
          End: DateTime
          Label: string }

    type PlannedBlock =
        { AssignmentId: AssignmentId
          /// The real step this block advances, when the assignment has been
          /// broken into milestones.
          ///
          /// **This is what stops the plan being a column of identical blocks
          /// all called "Final project report".** The review's words: "do not
          /// pretend that a generic assignment chunk is an actual task."
          MilestoneId: MilestoneId option
          /// The milestone's title when there is one, the assignment's when
          /// there is not. Never a synthesised "Session 3 of 7".
          Title: string
          ModuleCode: string option
          Start: DateTime
          End: DateTime
          Reason: string }

    type UnplannedTask =
        { Task: PlannableTask
          Reason: string }

    type PlannerInput =
        { Now: DateTime
          Tasks: PlannableTask list
          Busy: BusyBlock list
          Prefs: PlannerPreferences }

    type PlannerResult =
        { Blocks: PlannedBlock list
          Unplanned: UnplannedTask list
          Summary: string }

    /// Minimum block worth scheduling — anything shorter is not real study time.
    [<Literal>]
    let MinBlockMinutes = 25

    /// How much a task deserves attention right now. Combines urgency (work remaining
    /// versus wall-clock time left before the deadline) with importance (priority and
    /// grade weighting). Higher = schedule sooner.
    let score (now: DateTime) (t: PlannableTask) : float =
        let importance =
            1.5 * float (Priority.rank t.Priority)
            + (t.Weighting |> Option.map (fun w -> w / 20.0) |> Option.defaultValue 0.0)

        let urgency =
            match t.Deadline with
            | Some d when d <= now -> 1000.0 // overdue: always first
            | Some d ->
                let hoursLeft = max 1.0 (d - now).TotalHours
                let remainingHours = float t.RemainingMinutes / 60.0
                // Pressure: fraction of the remaining wall-clock the work itself needs,
                // plus a proximity kicker so near deadlines dominate even for small tasks.
                (remainingHours / hoursLeft) * 100.0 + 150.0 / hoursLeft
            | None -> 0.5

        urgency + importance

    let private describeDeadline (now: DateTime) (deadline: DateTime option) =
        match deadline with
        | None -> "no deadline"
        | Some d when d <= now -> "OVERDUE"
        | Some d ->
            let span = d - now
            if span.TotalHours < 24.0 then sprintf "due in %dh" (int span.TotalHours)
            elif span.TotalDays < 7.0 then sprintf "due in %d days" (int (ceil span.TotalDays))
            else sprintf "due %s" (d.ToString "d MMM")

    /// **Why this block is here, in words rather than fragments.**
    ///
    /// This used to emit `OVERDUE · 5.5h remaining · Critical priority · worth
    /// 35%` — four fragments that name quantities without saying what they
    /// imply. The first runtime review asked for reasoning a reader can act on,
    /// so each part is now a statement.
    ///
    /// **Priority is deliberately absent.** It sits on the session row's title
    /// as a badge of its own; repeating it here said the same thing twice in one
    /// row and pushed the parts that were *not* duplicated out of view.
    ///
    /// Every part is still a fact the scheduler actually holds. Nothing here is
    /// authored copy, and a fact the task does not carry is omitted rather than
    /// softened into a guess.
    let private reasonFor (now: DateTime) (t: PlannableTask) =
        let hours = float t.RemainingMinutes / 60.0

        let parts =
            [ match t.Deadline with
              | None -> "No deadline set, so it is scheduled around the rest"
              | Some d when d <= now -> "Already past its deadline"
              | Some d ->
                  let span = d - now

                  if span.TotalHours < 24.0 then
                      sprintf "Due in %d hours" (int span.TotalHours)
                  elif span.TotalDays < 7.0 then
                      sprintf "Deadline in %d days" (int (ceil span.TotalDays))
                  else
                      sprintf "Deadline on %s" (d.ToString "d MMMM")

              if hours >= 1.0 then
                  sprintf "%.1f hours of work still estimated" hours
              elif t.RemainingMinutes > 0 then
                  sprintf "%d minutes of work still estimated" t.RemainingMinutes

              match t.Weighting with
              | Some w when w > 0.0 -> sprintf "Worth %g%% of the module" w
              | _ -> () ]

        String.concat " · " parts

    /// Free intervals inside one day's workday window after subtracting busy blocks.
    /// `notBefore` clips the window (used so today's plan starts now, not at 09:00).
    let freeSlotsForDay
        (prefs: PlannerPreferences)
        (busy: BusyBlock list)
        (day: DateTime)
        (notBefore: DateTime)
        : (DateTime * DateTime) list =
        let windowStart = max notBefore (day.Date + prefs.WorkdayStart)
        let windowEnd = day.Date + prefs.WorkdayEnd

        if windowStart >= windowEnd then
            []
        else
            let relevant =
                busy
                |> List.filter (fun b -> b.Start < windowEnd && b.End > windowStart)
                |> List.sortBy (fun b -> b.Start)

            let mutable cursor = windowStart
            let slots = ResizeArray()

            for b in relevant do
                if b.Start > cursor then
                    slots.Add(cursor, b.Start)
                cursor <- max cursor b.End

            if cursor < windowEnd then
                slots.Add(cursor, windowEnd)

            slots
            |> Seq.filter (fun (s, e) -> (e - s).TotalMinutes >= float MinBlockMinutes)
            |> List.ofSeq

    /// Produce a realistic schedule. Greedy over days and free slots: each slot is
    /// filled with sessions of the highest-scoring task at that moment, separated by
    /// breaks, honouring the daily study cap and the pre-deadline buffer.
    /// **How an assignment's remaining time is shared across its milestones.**
    ///
    /// Walked in the user's order, so the plan advances the work in the sequence
    /// they arranged rather than in whatever order the scheduler happens to
    /// reach it.
    ///
    /// An estimated milestone takes its estimate. Unestimated ones split
    /// whatever is left over evenly — a deterministic share, not a guess dressed
    /// up as one, and never zero, or an unestimated step would be skipped
    /// entirely and never appear on the plan.
    ///
    /// Returns `[]` for an assignment with no milestones, which is the
    /// pre-milestone case and still schedules under the assignment's own title.
    let milestoneAllocation (remainingMinutes: int) (milestones: Milestone list) =
        match milestones with
        | [] -> []
        | items ->
            let known = items |> List.sumBy (fun m -> m.EstimatedMinutes |> Option.defaultValue 0)
            let unestimated = items |> List.filter (fun m -> m.EstimatedMinutes.IsNone) |> List.length
            let spare = max 0 (remainingMinutes - known)
            let share = if unestimated = 0 then 0 else spare / unestimated

            items
            |> List.map (fun m ->
                m.Id, m.Title, (match m.EstimatedMinutes with
                                | Some e -> max 1 e
                                | None -> max 1 share))

    let plan (input: PlannerInput) : PlannerResult =
        let prefs = input.Prefs
        let now = input.Now

        // One queue per assignment, consumed from the head as blocks are
        // placed. Mutable because the scheduler is a loop over days and slots;
        // the allocation itself is pure and tested separately.
        let milestoneQueues =
            System.Collections.Generic.Dictionary<AssignmentId, (MilestoneId * string * int) list>()

        for t in input.Tasks do
            milestoneQueues.[t.AssignmentId] <- milestoneAllocation t.RemainingMinutes t.Milestones

        /// The step a block of `minutes` advances, and the queue afterwards.
        ///
        /// A block longer than the head milestone's share still names that
        /// milestone: it is the step being worked on, and inventing a split
        /// across two names for one sitting would be worse than rounding.
        let takeMilestone (assignmentId: AssignmentId) (minutes: int) =
            match milestoneQueues.TryGetValue assignmentId with
            | true, (milestoneId, title, share) :: rest ->
                milestoneQueues.[assignmentId] <-
                    if minutes >= share then
                        rest
                    else
                        (milestoneId, title, share - minutes) :: rest

                Some(milestoneId, title)
            | _ -> None

        let remaining = System.Collections.Generic.Dictionary<AssignmentId, int>()

        for t in input.Tasks do
            if t.RemainingMinutes > 0 then
                remaining.[t.AssignmentId] <- t.RemainingMinutes

        let tasksById =
            input.Tasks |> List.map (fun t -> t.AssignmentId, t) |> dict

        let blocks = ResizeArray<PlannedBlock>()

        let effectiveDeadline (t: PlannableTask) =
            t.Deadline |> Option.map (fun d -> d.AddHours(-prefs.DeadlineBufferHours))

        for dayOffset in 0 .. prefs.HorizonDays - 1 do
            let day = now.Date.AddDays(float dayOffset)
            let notBefore = if dayOffset = 0 then now else day

            let mutable dailyUsed = 0

            for (slotStart, slotEnd) in freeSlotsForDay prefs input.Busy day notBefore do
                let mutable cursor = slotStart

                let mutable exhausted = false

                while not exhausted
                      && (slotEnd - cursor).TotalMinutes >= float MinBlockMinutes
                      && dailyUsed < prefs.MaxDailyStudyMinutes do

                    // Prefer tasks whose (buffered) deadline has not already passed this
                    // point in the plan; overdue tasks stay eligible — they score highest.
                    let candidates =
                        remaining
                        |> Seq.filter (fun kv -> kv.Value > 0)
                        |> Seq.map (fun kv -> tasksById.[kv.Key])
                        |> Seq.sortByDescending (score cursor)
                        |> List.ofSeq

                    match candidates with
                    | [] -> exhausted <- true
                    | best :: _ ->
                        let capLeft = prefs.MaxDailyStudyMinutes - dailyUsed
                        let slotLeft = int (slotEnd - cursor).TotalMinutes

                        let chunk =
                            [ prefs.SessionMinutes
                              slotLeft
                              remaining.[best.AssignmentId]
                              capLeft ]
                            |> List.min

                        if chunk < MinBlockMinutes && remaining.[best.AssignmentId] >= MinBlockMinutes then
                            // Not enough contiguous time left for a meaningful session.
                            exhausted <- true
                        else
                            let chunk = max chunk (min MinBlockMinutes remaining.[best.AssignmentId])
                            let blockEnd = cursor.AddMinutes(float chunk)

                            let lateNote =
                                match effectiveDeadline best with
                                | Some ed when blockEnd > ed && best.Deadline.Value > cursor ->
                                    " · Tight against the deadline buffer"
                                | Some _ when cursor > best.Deadline.Value -> " · Scheduled after the deadline has passed"
                                | _ -> ""

                            let step = takeMilestone best.AssignmentId chunk

                            blocks.Add
                                { AssignmentId = best.AssignmentId
                                  MilestoneId = step |> Option.map fst
                                  // The milestone's own title, so the block says
                                  // what is actually being done rather than
                                  // repeating the assignment name down the page.
                                  Title = step |> Option.map snd |> Option.defaultValue best.Title
                                  ModuleCode = best.ModuleCode
                                  Start = cursor
                                  End = blockEnd
                                  Reason = reasonFor cursor best + lateNote }

                            remaining.[best.AssignmentId] <- remaining.[best.AssignmentId] - chunk
                            dailyUsed <- dailyUsed + chunk
                            cursor <- blockEnd.AddMinutes(float prefs.BreakMinutes)

        let unplanned =
            remaining
            |> Seq.filter (fun kv -> kv.Value > 0)
            |> Seq.map (fun kv ->
                let t = tasksById.[kv.Key]

                let reason =
                    match t.Deadline with
                    | Some d when d < now.AddDays(float prefs.HorizonDays) ->
                        sprintf
                            "%.1fh could not be scheduled before the deadline — consider extending your workday, raising the daily cap, or reducing scope"
                            (float kv.Value / 60.0)
                    | _ ->
                        sprintf
                            "%.1fh left after the %d-day horizon — it will be planned as time frees up"
                            (float kv.Value / 60.0)
                            prefs.HorizonDays

                { Task = t; Reason = reason })
            |> List.ofSeq

        let totalPlanned = blocks |> Seq.sumBy (fun b -> (b.End - b.Start).TotalMinutes)

        let summary =
            if blocks.Count = 0 && List.isEmpty unplanned then
                "Nothing needs scheduling — you're on top of everything."
            else
                let daysUsed = blocks |> Seq.map (fun b -> b.Start.Date) |> Seq.distinct |> Seq.length

                sprintf
                    "Planned %.1fh across %d day(s) for %d task(s)%s."
                    (totalPlanned / 60.0)
                    daysUsed
                    (blocks |> Seq.map (fun b -> b.AssignmentId) |> Seq.distinct |> Seq.length)
                    (if List.isEmpty unplanned then ""
                     else sprintf "; %d task(s) could not be fully placed" (List.length unplanned))

        { Blocks = List.ofSeq blocks
          Unplanned = unplanned
          Summary = summary }

    /// The dashboard's "what should I do right now?" answer.
    let recommendNext (now: DateTime) (tasks: PlannableTask list) : (PlannableTask * string) option =
        tasks
        |> List.filter (fun t -> t.RemainingMinutes > 0)
        |> List.sortByDescending (score now)
        |> List.tryHead
        |> Option.map (fun t -> t, reasonFor now t)
