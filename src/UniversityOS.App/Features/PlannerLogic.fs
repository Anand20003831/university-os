namespace UniversityOS.App.Features

open System
open UniversityOS.Domain
open UniversityOS.Domain.Planning
open UniversityOS.App

/// Everything the Planner decides, separated from how it is drawn.
///
/// The redesign's Planner is largely *derived* values — a workload split, a set
/// of constraint checks, an optimisation score, a list of observations. Deriving
/// them in the view would make them untestable and would recompute them on every
/// repaint, including repaints caused by a background frame tick. They are pure
/// functions of the loaded sessions and the user's real planner preferences
/// instead, computed once per load.
///
/// **Nothing here is invented.** Where the redesign shows a figure the domain
/// cannot produce, this module either derives an honest equivalent from real
/// data and renames the element to match what it actually measures, or omits it.
/// Both kinds of decision are named at the definition that makes them:
///
/// - "Completion confidence" → `coverage`, which measures scheduled minutes
///   against outstanding minutes. A measurement, not a prediction.
/// - "Deadline risk" → `deadlineRisk`, derived from work the scheduler could
///   not place and blocks it flagged as tight. Deterministic, not a forecast.
/// - "Future prediction" → `outlook`, four deterministic reads. The card is
///   renamed UPCOMING OUTLOOK because nothing here predicts anything.
/// - "AI reasoning" → `reasoningBullets`, which is the *scheduler's* reasoning.
///   No AI provider is involved anywhere in this path, so the card is labelled
///   PLAN REASONING.
/// - "Revision Focused" alternative → omitted from `Strategy`; the planner
///   never schedules revision items.
[<RequireQualifiedAccess>]
module PlannerLogic =

    // ---- sessions -----------------------------------------------------------

    /// Where a session sits relative to now.
    ///
    /// Carried as a case rather than a colour so the view can render both a word
    /// and a colour — status is never communicated by colour alone.
    type SessionStatus =
        | Done
        | InProgress
        | Missed
        | UpNext
        | Scheduled

    [<RequireQualifiedAccess>]
    module SessionStatus =
        let label =
            function
            | Done -> "DONE"
            | InProgress -> "IN PROGRESS"
            | Missed -> "MISSED"
            | UpNext -> "UP NEXT"
            | Scheduled -> "SCHEDULED"

        let all = [ Done; InProgress; Missed; UpNext; Scheduled ]

    /// A ticked-off session is Done whatever the clock says. One that ended
    /// without being ticked off is **Missed** — the clock alone never means the
    /// work happened.
    let statusOf (now: DateTime) (session: PlannedSession) =
        if session.Completed then Done
        elif session.StartsAt <= now && session.EndsAt > now then InProgress
        // **Missed, not Done.** A session that ended without being ticked off was
        // not completed — reporting it as Done credited the user with work they
        // had not done, and made the two indistinguishable on the screen. Only
        // `Completed` means done; the clock alone never does.
        elif session.EndsAt <= now then Missed
        elif session.StartsAt <= now.AddHours 2.0 then UpNext
        else Scheduled

    /// How much of a session has elapsed, 0–100.
    ///
    /// Real elapsed time, not an invented "progress estimate": a completed
    /// session is 100, a future one is 0, and one in progress is however far
    /// through it you are. The redesign's per-session progress bar has no data
    /// source behind it — this is the honest thing the same bar can show.
    let elapsedPercent (now: DateTime) (session: PlannedSession) =
        if session.Completed then 100
        else
            let total = (session.EndsAt - session.StartsAt).TotalMinutes

            if total <= 0.0 then 0
            else
                let gone = (now - session.StartsAt).TotalMinutes
                max 0 (min 100 (int (round (100.0 * gone / total))))

    /// **The priority of the assignment a session was scheduled for.**
    ///
    /// `None` whenever it cannot be established honestly: the session is not
    /// linked to an assignment, or the link no longer resolves because the
    /// assignment has been completed or deleted since the plan was made. There is
    /// deliberately no fallback — an unlinked session has no priority, and
    /// defaulting one to Medium would invent a fact about the user's work.
    ///
    /// It is emphatically **not** derived from duration. `effortOf` reads the
    /// block's length; this reads the linked assignment's own stored priority,
    /// and the two are independent: a long block on a low-priority assignment is
    /// high effort and low priority at the same time.
    let priorityOf (tasks: PlannableTask list) (session: PlannedSession) =
        session.AssignmentId
        |> Option.bind (fun id -> tasks |> List.tryFind (fun t -> t.AssignmentId = id))
        |> Option.map (fun t -> t.Priority)

    /// Named so the badge says what the adjective refers to, exactly as the
    /// effort labels do.
    ///
    /// Four cases, not three: the domain distinguishes Critical from High, and
    /// collapsing them would throw away a distinction the user set deliberately.
    let priorityLabel (priority: Priority) =
        sprintf "%s priority" (Priority.label priority)

    /// How demanding a block is, from its length.
    type Effort =
        | Light
        | Moderate
        | Deep

    [<RequireQualifiedAccess>]
    module Effort =
        /// Named so the badge says what the adjective refers to. A bare "Light"
        /// beside a bare "High" told the reader nothing about which scale either
        /// belonged to — reported in the first runtime review.
        let label =
            function
            | Light -> "Low effort"
            | Moderate -> "Medium effort"
            | Deep -> "High effort"

    let effortOf (session: PlannedSession) =
        match PlannedSession.durationMinutes session with
        | m when m < 40 -> Light
        | m when m < 75 -> Moderate
        | _ -> Deep

    /// The planner's own explanation for a block, split into chips.
    ///
    /// `Planning.reasonFor` joins its parts with " · ", so this reverses exactly
    /// that rather than parsing free text — the redesign shows the reasoning as
    /// separate pills and this is where they come from. Real output of the real
    /// scheduler, never authored copy.
    let reasonChips (session: PlannedSession) =
        if String.IsNullOrWhiteSpace session.Reason then
            []
        else
            session.Reason.Split([| " · " |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun s -> s.Trim())
            |> Array.filter (fun s -> s.Length > 0)
            |> List.ofArray

    // ---- days ---------------------------------------------------------------

    /// One day of the plan.
    type PlanDay =
        { Date: DateTime
          Sessions: PlannedSession list
          Minutes: int }

    /// The plan grouped by day, earliest first, sessions in time order.
    let days (sessions: PlannedSession list) : PlanDay list =
        sessions
        |> List.groupBy (fun s -> s.StartsAt.Date)
        |> List.map (fun (date, items) ->
            let ordered = items |> List.sortBy (fun s -> s.StartsAt)

            { Date = date
              Sessions = ordered
              Minutes = ordered |> List.sumBy PlannedSession.durationMinutes })
        |> List.sortBy (fun d -> d.Date)

    /// Today's sessions, which is what the redesign's main panel shows.
    let today (now: DateTime) (sessions: PlannedSession list) =
        sessions
        |> List.filter (fun s -> s.StartsAt.Date = now.Date)
        |> List.sortBy (fun s -> s.StartsAt)

    /// How many of today's sessions the card shows inline before offering the
    /// full list. A whole teaching day can hold far more blocks than a summary
    /// card should carry, and an unbounded list pushed everything below it off
    /// the screen.
    [<Literal>]
    let todayPreviewLimit = 7

    /// The sessions shown inline: today's, in time order, capped. Chronological
    /// order is preserved by `today`, so the cap always keeps the EARLIEST
    /// sessions rather than an arbitrary subset — the ones still to come are the
    /// ones a preview should show.
    let todayPreview (now: DateTime) (sessions: PlannedSession list) =
        today now sessions |> List.truncate todayPreviewLimit

    /// How many of today's sessions the preview leaves out. Zero means the card
    /// is showing the whole day and needs no "view all" affordance.
    let todayHiddenCount (now: DateTime) (sessions: PlannedSession list) =
        max 0 ((today now sessions).Length - todayPreviewLimit)

    // ---- workload distribution ---------------------------------------------

    /// A module's share of the planned time.
    type WorkloadSlice =
        { Label: string
          Minutes: int
          /// Whole per cent of the total. Rounded, so a set can total 99 or 101;
          /// the view uses `Minutes` for the bar so the widths stay exact.
          Percent: int }

    /// Planned minutes per module, largest first.
    ///
    /// Sessions with no module code are grouped under "Other" rather than
    /// dropped — the total has to match the plan or the bar lies.
    let workload (sessions: PlannedSession list) : WorkloadSlice list =
        let total = sessions |> List.sumBy PlannedSession.durationMinutes

        if total <= 0 then
            []
        else
            sessions
            |> List.groupBy (fun s -> s.ModuleCode |> Option.defaultValue "Other")
            |> List.map (fun (label, items) ->
                let minutes = items |> List.sumBy PlannedSession.durationMinutes

                { Label = label
                  Minutes = minutes
                  Percent = int (round (100.0 * float minutes / float total)) })
            |> List.sortByDescending (fun s -> s.Minutes)

    /// The module codes of a plan, in the same order as `workload`.
    ///
    /// Colour is taken from a module's position in this list, so a module's dot
    /// in a session row and its segment in the workload bar are always the same
    /// colour. Indexing by row position instead — as an earlier version did —
    /// gave the same module a different colour on every line.
    let moduleOrder (sessions: PlannedSession list) =
        workload sessions |> List.map (fun s -> s.Label)

    let moduleIndexOf (order: string list) (code: string option) =
        let label = code |> Option.defaultValue "Other"
        order |> List.tryFindIndex ((=) label) |> Option.defaultValue 0

    // ---- constraint validation ---------------------------------------------

    /// One rule the generated plan either honours or does not.
    type Check =
        { Passed: bool
          /// Stated as the outcome, so a passing and a failing check read as
          /// different facts rather than the same label with a different tick.
          Text: string }

    let private overlaps (sessions: PlannedSession list) =
        sessions
        |> List.sortBy (fun s -> s.StartsAt)
        |> List.pairwise
        |> List.exists (fun (a, b) -> b.StartsAt < a.EndsAt)

    /// Validates the plan against the user's REAL preferences.
    ///
    /// Every check is something `Planning.plan` actually promises, so a failure
    /// here is a genuine defect rather than a decorative warning. The redesign's
    /// "Lunch break preserved" is absent: nothing in `PlannerPreferences` models
    /// a lunch break, so there is no rule to check.
    let checks (prefs: PlannerPreferences) (unplanned: UnplannedTask list) (sessions: PlannedSession list) : Check list =
        let byDay = sessions |> List.groupBy (fun s -> s.StartsAt.Date)

        let worstDay =
            byDay
            |> List.map (fun (_, items) -> items |> List.sumBy PlannedSession.durationMinutes)
            |> function
                | [] -> 0
                | totals -> List.max totals

        let outsideHours =
            sessions
            |> List.filter (fun s ->
                s.StartsAt.TimeOfDay < prefs.WorkdayStart || s.EndsAt.TimeOfDay > prefs.WorkdayEnd)
            |> List.length

        let tooShort =
            sessions
            |> List.filter (fun s -> PlannedSession.durationMinutes s < Planning.MinBlockMinutes)
            |> List.length

        [ { Passed = worstDay <= prefs.MaxDailyStudyMinutes
            Text =
              if worstDay <= prefs.MaxDailyStudyMinutes then
                  sprintf
                      "Daily maximum respected — busiest day is %s of %s"
                      (Format.duration worstDay)
                      (Format.duration prefs.MaxDailyStudyMinutes)
              else
                  sprintf
                      "Busiest day is %s, over your %s maximum"
                      (Format.duration worstDay)
                      (Format.duration prefs.MaxDailyStudyMinutes) }

          { Passed = not (overlaps sessions)
            Text = if overlaps sessions then "Two sessions overlap" else "No overlapping sessions" }

          { Passed = outsideHours = 0
            Text =
              if outsideHours = 0 then
                  sprintf
                      "Everything inside your %02d:00–%02d:00 study hours"
                      (int prefs.WorkdayStart.TotalHours)
                      (int prefs.WorkdayEnd.TotalHours)
              else
                  sprintf "%d session(s) fall outside your study hours" outsideHours }

          { Passed = tooShort = 0
            Text =
              if tooShort = 0 then
                  sprintf "Every block is at least %d minutes" Planning.MinBlockMinutes
              else
                  sprintf "%d block(s) are shorter than %d minutes" tooShort Planning.MinBlockMinutes }

          { Passed = unplanned.IsEmpty
            Text =
              if unplanned.IsEmpty then
                  "All outstanding work fitted before its deadline"
              else
                  sprintf "%d task(s) could not be fully scheduled" unplanned.Length } ]

    /// Share of checks the plan passes, 0–100. The redesign's "planner score".
    let score (checks: Check list) =
        if checks.IsEmpty then 0
        else int (round (100.0 * float (checks |> List.filter (fun c -> c.Passed) |> List.length) / float checks.Length))

    /// The constraints the planner works to, named without reference to any plan.
    ///
    /// One entry per check `checks` produces, in the same order, so the card can
    /// list the rules as *pending* before a plan exists instead of silently
    /// showing nothing. A test pins the two lengths together so the two lists
    /// cannot drift apart.
    let constraintNames (prefs: PlannerPreferences) =
        [ sprintf "Daily maximum of %s respected" (Format.duration prefs.MaxDailyStudyMinutes)
          "No overlapping sessions"
          sprintf
              "Everything inside your %02d:00–%02d:00 study hours"
              (int prefs.WorkdayStart.TotalHours)
              (int prefs.WorkdayEnd.TotalHours)
          sprintf "Every block at least %d minutes" Planning.MinBlockMinutes
          "All outstanding work fitted before its deadline" ]

    /// Whether the plan has been judged at all.
    ///
    /// **An empty plan is not a satisfied plan.** Every rule in `checks` is
    /// vacuously true when there are no sessions: no day exceeds the daily
    /// maximum, nothing overlaps, nothing falls outside the study hours, no
    /// block is too short. Feeding that straight into `score` awards 100% to a
    /// blank planner — the exact opposite of the truth, and the single most
    /// misleading number the screen could show. So the absence of a plan is
    /// carried as its own case and rendered as "not evaluated" instead.
    type Evaluation =
        | NotEvaluated
        | Evaluated of Check list

    let evaluate (prefs: PlannerPreferences) (unplanned: UnplannedTask list) (sessions: PlannedSession list) =
        if List.isEmpty sessions then NotEvaluated
        else Evaluated(checks prefs unplanned sessions)

    /// The optimisation score, or `None` when there is no plan to score.
    /// Never 100 for an empty plan — never any number at all.
    let scoreOf (evaluation: Evaluation) =
        match evaluation with
        | NotEvaluated -> None
        | Evaluated checks -> Some(score checks)

    let evaluatedChecks (evaluation: Evaluation) =
        match evaluation with
        | NotEvaluated -> []
        | Evaluated checks -> checks

    // ---- optimisation figures ----------------------------------------------

    /// The right-hand summary rows. Every one is a real count or a real ratio.
    ///
    /// The redesign also shows "Completion confidence" and "Deadline risk" as
    /// percentages. Nothing in the domain predicts either, so they are omitted
    /// rather than given a number that would look authoritative and mean
    /// nothing.
    let optimisation (prefs: PlannerPreferences) (checks: Check list) (sessions: PlannedSession list) =
        let planned = sessions |> List.sumBy PlannedSession.durationMinutes
        let dayCount = sessions |> List.map (fun s -> s.StartsAt.Date) |> List.distinct |> List.length
        let capacity = dayCount * prefs.MaxDailyStudyMinutes

        [ "Constraints satisfied", sprintf "%d of %d" (checks |> List.filter (fun c -> c.Passed) |> List.length) checks.Length
          "Sessions scheduled", string sessions.Length
          "Days with study", string dayCount
          "Daily allowance used",
          (if capacity <= 0 then "—" else sprintf "%d%%" (min 100 (int (round (100.0 * float planned / float capacity))))) ]

    /// Total minutes of coursework still outstanding, from the real open
    /// assignments the planner was given.
    let outstandingMinutes (tasks: PlannableTask list) =
        tasks |> List.sumBy (fun t -> max 0 t.RemainingMinutes)

    /// Share of outstanding coursework that has a scheduled block, 0–100.
    ///
    /// The redesign's row here is "completion confidence". Nothing in this
    /// application predicts whether work will be completed — there is no model
    /// of future behaviour to draw one from — so a confidence percentage would
    /// be a number that looks authoritative and means nothing. This is the
    /// honest measurable thing the same row can carry: planned minutes against
    /// the minutes the open assignments still need, which is a fact about the
    /// plan rather than a claim about the future. `None` when there is no
    /// outstanding work, because a ratio with a zero denominator is not 100%.
    let coverage (tasks: PlannableTask list) (sessions: PlannedSession list) =
        let required = outstandingMinutes tasks

        if required <= 0 then
            None
        else
            let planned = sessions |> List.sumBy PlannedSession.durationMinutes
            Some(max 0 (min 100 (int (round (100.0 * float planned / float required)))))

    /// How exposed the plan is to its deadlines.
    ///
    /// Deterministic, not a prediction. Work the scheduler could not place at
    /// all is the high signal; blocks the scheduler itself marked as tight
    /// against a deadline (or overdue) are the medium one. Both come straight
    /// out of `Planning.plan` — the first as `UnplannedTask`s, the second as
    /// text `reasonFor` appended. Nothing is estimated.
    type Risk =
        | NoPlan
        | Low
        | Medium
        | High

    [<RequireQualifiedAccess>]
    module Risk =
        let label =
            function
            | NoPlan -> "Not evaluated"
            | Low -> "Low"
            | Medium -> "Medium"
            | High -> "High"

        /// Whether this is a real assessment or the absence of one.
        ///
        /// The view inks an absence with ordinary muted text. `NoPlan` is not a
        /// status, so colouring it with the neutral identity marker — a dot
        /// colour, not an ink role — put a 2.4:1 value on a light card, well
        /// under the AA floor every other ink on this screen clears.
        let isAssessed = function NoPlan -> false | _ -> true

    let deadlineRisk (unplanned: UnplannedTask list) (sessions: PlannedSession list) =
        if List.isEmpty sessions then NoPlan
        elif not (List.isEmpty unplanned) then High
        elif
            sessions
            |> List.exists (fun s -> s.Reason.Contains "tight against deadline" || s.Reason.Contains "OVERDUE")
        then
            Medium
        else
            Low

    // ---- observations -------------------------------------------------------

    /// Rule-based readings of the plan.
    ///
    /// Deliberately NOT called AI insights, for the same reason as the Timeline's
    /// Patterns card: these are rules over real numbers, and presenting them as
    /// model output would misrepresent how the application works.
    let observations
        (now: DateTime)
        (prefs: PlannerPreferences)
        (unplanned: UnplannedTask list)
        (sessions: PlannedSession list)
        : string list =
        if sessions.IsEmpty then
            []
        else
            let planned = sessions |> List.sumBy PlannedSession.durationMinutes
            let todays = today now sessions
            let todayMinutes = todays |> List.sumBy PlannedSession.durationMinutes

            let heaviest =
                days sessions |> List.sortByDescending (fun d -> d.Minutes) |> List.tryHead

            [ if not todays.IsEmpty then
                  sprintf "%s planned today across %d session(s)." (Format.duration todayMinutes) todays.Length

              match heaviest with
              | Some day when day.Minutes > 0 && day.Date <> now.Date ->
                  sprintf "%s is the heaviest day, with %s planned." (Format.dayLabel now day.Date) (Format.duration day.Minutes)
              | _ -> ()

              if todayMinutes > 0 && todayMinutes < prefs.MaxDailyStudyMinutes then
                  sprintf
                      "Today is %s below your %s daily maximum."
                      (Format.duration (prefs.MaxDailyStudyMinutes - todayMinutes))
                      (Format.duration prefs.MaxDailyStudyMinutes)

              match workload sessions |> List.tryHead with
              | Some slice when slice.Percent >= 40 ->
                  sprintf "%s takes %d%% of the planned time." slice.Label slice.Percent
              | _ -> ()

              if not unplanned.IsEmpty then
                  sprintf
                      "%d task(s) still need time the next %d days cannot give."
                      unplanned.Length
                      prefs.HorizonDays

              sprintf "%s of study planned in total." (Format.duration planned) ]
            |> List.truncate 5

    // ---- plan reasoning -----------------------------------------------------

    /// Why the scheduler placed what it placed.
    ///
    /// **This is deliberately not called AI reasoning.** Every bullet is either
    /// a real component of `Planning.reasonFor`'s output for a real block, or a
    /// statement of a rule `Planning.plan` demonstrably applied to produce this
    /// plan. No AI provider is called anywhere on this path, and none of this
    /// text is authored copy. Labelling rules-based output as model output would
    /// misrepresent how the application works, so the card says PLAN REASONING.
    let reasoningBullets
        (prefs: PlannerPreferences)
        (busy: BusyBlock list)
        (sessions: PlannedSession list)
        : string list =
        if List.isEmpty sessions then
            []
        else
            let perTask =
                sessions
                |> List.sortBy (fun s -> s.StartsAt)
                |> List.groupBy (fun s -> s.Title)
                |> List.map (fun (title, items) ->
                    let blocks =
                        if items.Length = 1 then "1 block" else sprintf "%d blocks" items.Length

                    match reasonChips items.Head with
                    | [] -> sprintf "%s — %s scheduled." title blocks
                    | chips -> sprintf "%s — %s scheduled: %s." title blocks (String.concat ", " chips))
                |> List.truncate 3

            [ yield! perTask

              if not (List.isEmpty busy) then
                  yield
                      sprintf
                          "%d fixed commitment(s) in the window were treated as busy time and worked around."
                          busy.Length

              yield
                  sprintf
                      "Blocks were placed only inside your %02d:00–%02d:00 study hours, in chunks of up to %s."
                      (int prefs.WorkdayStart.TotalHours)
                      (int prefs.WorkdayEnd.TotalHours)
                      (Format.duration prefs.SessionMinutes)

              yield
                  sprintf
                      "No day was filled past your %s daily maximum, with %s between blocks."
                      (Format.duration prefs.MaxDailyStudyMinutes)
                      (Format.duration prefs.BreakMinutes)

              if prefs.DeadlineBufferHours > 0.0 then
                  yield
                      sprintf
                          "Work was aimed to finish %s before each deadline rather than up against it."
                          (Format.hours prefs.DeadlineBufferHours) ]

    // ---- alternative plans --------------------------------------------------

    /// A planning strategy the application can genuinely carry out.
    ///
    /// Each case is a real transform of the user's own `PlannerPreferences`, fed
    /// through the same `Planning.plan` as every other plan here. Selecting one
    /// and previewing it performs an actual scheduling run and reports its
    /// actual output — these are not inert pills with descriptive text.
    ///
    /// The redesign also offers **Revision Focused**. It is absent, not
    /// disabled: `PlannerService.BuildPlannables` reads open *assignments* only,
    /// `RevisionItem` never reaches `Planning.plan`, and no preference weights
    /// revision. A fifth pill would therefore produce a plan identical to
    /// Balanced while claiming to do something else, which is exactly the kind
    /// of decorative control this migration is not allowed to add.
    type Strategy =
        | Balanced
        | DeadlineFocused
        | HighProductivity
        | LowStress

    [<RequireQualifiedAccess>]
    module Strategy =
        /// Every strategy the planner can actually represent.
        let supported = [ Balanced; DeadlineFocused; HighProductivity; LowStress ]

        let label =
            function
            | Balanced -> "Balanced"
            | DeadlineFocused -> "Deadline Focused"
            | HighProductivity -> "High Productivity"
            | LowStress -> "Low Stress"

        /// What the transform actually changes, stated in terms of the settings
        /// it moves — so the description can be checked against the behaviour.
        let description =
            function
            | Balanced -> "Your own planner settings, exactly as saved."
            | DeadlineFocused -> "Widens the pre-deadline buffer by a day, so work lands further ahead of each deadline."
            | HighProductivity -> "Longer blocks and a higher daily ceiling — more work per day, fewer context switches."
            | LowStress -> "Shorter blocks, longer breaks and a lower daily ceiling — spread wider, lighter each day."

        /// The real preference transform behind the strategy.
        let applyTo (prefs: PlannerPreferences) =
            function
            | Balanced -> prefs
            | DeadlineFocused -> { prefs with DeadlineBufferHours = prefs.DeadlineBufferHours + 24.0 }
            | HighProductivity ->
                { prefs with
                    SessionMinutes = prefs.SessionMinutes + 25
                    MaxDailyStudyMinutes = int (float prefs.MaxDailyStudyMinutes * 1.25) }
            | LowStress ->
                { prefs with
                    SessionMinutes = max Planning.MinBlockMinutes (prefs.SessionMinutes - 15)
                    BreakMinutes = prefs.BreakMinutes + 5
                    MaxDailyStudyMinutes = max Planning.MinBlockMinutes (int (float prefs.MaxDailyStudyMinutes * 0.7)) }

    /// A previewed alternative, reduced to what the card compares.
    type Alternative =
        { Strategy: Strategy
          Sessions: int
          Minutes: int
          Days: int
          Unplanned: int }

    /// Summarises a real `Planning.plan` run. Every field is counted from the
    /// blocks it produced; nothing is estimated.
    let summarise (strategy: Strategy) (result: PlannerResult) =
        { Strategy = strategy
          Sessions = result.Blocks.Length
          Minutes = result.Blocks |> List.sumBy (fun b -> int (b.End - b.Start).TotalMinutes)
          Days = result.Blocks |> List.map (fun b -> b.Start.Date) |> List.distinct |> List.length
          Unplanned = result.Unplanned.Length }

    /// The daily ceiling that spreads all outstanding work evenly across the
    /// planning horizon, never above the user's own maximum.
    ///
    /// This is what **Balance Workload** does. It is derived from the real
    /// outstanding minutes rather than being a preset, which is what makes it a
    /// different operation from any `Strategy` above rather than a duplicate of
    /// Low Stress: with little work it lowers the cap a long way, with a lot of
    /// work it does not move it at all.
    let balancedCap (prefs: PlannerPreferences) (tasks: PlannableTask list) =
        let outstanding = outstandingMinutes tasks

        if outstanding <= 0 then
            prefs.MaxDailyStudyMinutes
        else
            int (ceil (float outstanding / float (max 1 prefs.HorizonDays)))
            |> max Planning.MinBlockMinutes
            |> min prefs.MaxDailyStudyMinutes

    // ---- upcoming outlook ---------------------------------------------------

    type OutlookRow = { Label: string; Value: string }

    /// The redesign's FUTURE PREDICTION card.
    ///
    /// It keeps the redesign's title, and every row is a **deterministic
    /// projection** — a fact about deadlines and the plan already on disk,
    /// carried forward. Nothing here is a model output or an estimate: there is
    /// no completion model, no velocity series, and the one historical signal
    /// that exists (`MemoryStats.paceFactor`) is already folded into
    /// `RemainingMinutes` before the planner sees it. A row with no data says so
    /// rather than guessing, which is why "None set" and "Not evaluated" are
    /// values this returns.
    let outlook
        (now: DateTime)
        (tasks: PlannableTask list)
        (unplanned: UnplannedTask list)
        (sessions: PlannedSession list)
        : OutlookRow list =
        let nextDeadline =
            tasks
            |> List.choose (fun t -> t.Deadline |> Option.map (fun d -> t, d))
            |> List.filter (fun (_, d) -> d >= now)
            |> List.sortBy snd
            |> List.tryHead

        let ahead =
            sessions
            |> List.filter (fun s -> not s.Completed && s.EndsAt > now)

        let nextWeek =
            ahead
            |> List.filter (fun s -> s.StartsAt < now.Date.AddDays 7.0)
            |> List.sumBy PlannedSession.durationMinutes

        [ { Label = "Next deadline"
            Value =
              match nextDeadline with
              | Some(task, d) -> sprintf "%s · %s" (Format.truncate 28 task.Title) (Format.deadline now d)
              | None -> "None set" }

          { Label = "Study time still scheduled"
            Value =
              if ahead.IsEmpty then "None"
              else Format.duration (ahead |> List.sumBy PlannedSession.durationMinutes) }

          { Label = "Planned in the next 7 days"
            Value = if nextWeek <= 0 then "None" else Format.duration nextWeek }

          { Label = "Outstanding coursework"
            Value =
              match outstandingMinutes tasks with
              | 0 -> "None"
              | m -> Format.duration m }

          { Label = "Deadline risk"
            Value = Risk.label (deadlineRisk unplanned sessions) } ]

    // ---- planner history ----------------------------------------------------

    /// Activity kinds that describe something happening TO the plan.
    ///
    /// Real entries from the real log — the redesign's history is mock copy, but
    /// the app genuinely records plan regenerations and session completions, and
    /// those are exactly what a planner history is.
    let historyKinds =
        [ ActivityKind.PlanGenerated
          ActivityKind.SessionCompleted
          ActivityKind.DeadlineChanged ]

    let history (entries: ActivityEntry list) =
        entries
        |> List.filter (fun e -> historyKinds |> List.contains e.Kind)
        |> List.sortByDescending (fun e -> e.OccurredAt)
        |> List.truncate 6

    // ---- the action row -----------------------------------------------------

    /// The six actions the redesign puts under the header.
    type ActionId =
        | RegeneratePlan
        | OptimiseAgain
        | BalanceWorkload
        | FocusCurrentSession
        | ViewAlternatives

    /// One action, resolved against the current state.
    ///
    /// `DisabledBecause` is rendered as **visible text**, not a tooltip: an
    /// action that cannot run has to say why where the user can read it. No
    /// action in this row is ever a silent no-op — each one either performs real
    /// work or explains itself.
    type ActionState =
        { Id: ActionId
          Label: string
          Primary: bool
          Enabled: bool
          DisabledBecause: string option }

    /// Everything the action row needs to decide, gathered so the row is a pure
    /// function of state and can be asserted without rendering it.
    type ActionContext =
        { Generating: bool
          Strategy: Strategy
          HasSessions: bool
          HasOutstandingWork: bool
          HasFocusTarget: bool }

    /// The session **Focus Current Session** acts on: the one running right now,
    /// or failing that the next one still to come today. `None` when there is
    /// neither, which is precisely when the action disables itself and says so.
    let focusTarget (now: DateTime) (sessions: PlannedSession list) =
        let todays = today now sessions |> List.filter (fun s -> not s.Completed)

        todays
        |> List.tryFind (fun s -> s.StartsAt <= now && s.EndsAt > now)
        |> Option.orElseWith (fun () -> todays |> List.filter (fun s -> s.StartsAt > now) |> List.tryHead)
        |> Option.map (fun s -> s.Id)

    let actions (ctx: ActionContext) : ActionState list =
        let entry id label primary enabled because =
            { Id = id
              Label = label
              Primary = primary
              Enabled = enabled && not ctx.Generating
              DisabledBecause = if enabled && not ctx.Generating then None else because }

        [ entry
              RegeneratePlan
              (if ctx.Generating then "Planning…" else "Regenerate Planner")
              true
              true
              (Some "A plan is being generated.")

          // Deliberately not a second Regenerate. `Planning.plan` is
          // deterministic, so re-running the same preferences returns the
          // identical plan; the only honest "optimise again" is a re-run under a
          // different strategy, which is what this does.
          entry
              OptimiseAgain
              "Optimise Again"
              false
              (ctx.Strategy <> Balanced && ctx.HasOutstandingWork)
              (Some(
                  if not ctx.HasOutstandingWork then
                      "No outstanding coursework to optimise."
                  else
                      "The planner is deterministic — re-running your saved settings gives the same plan. Choose an alternative strategy first."
              ))

          entry
              BalanceWorkload
              "Balance Workload"
              false
              ctx.HasOutstandingWork
              (Some "No outstanding coursework to spread across your days.")

          entry
              FocusCurrentSession
              "Focus Current Session"
              false
              ctx.HasFocusTarget
              (Some "No session is running or due next today.")

          entry
              ViewAlternatives
              "Re-run Alternatives"
              false
              ctx.HasOutstandingWork
              (Some "Alternatives need outstanding coursework to schedule.") ]

    // ---- geometry -----------------------------------------------------------

    /// Fixed geometry for the action row and the session rows.
    ///
    /// Held here, as data, for the reason every other decision on this screen is:
    /// a dimension that only exists inside a view cannot be asserted, and the
    /// defects these values fix are dimensional. Each one is a *reservation* —
    /// space held whether or not the thing that occupies it is present — so a
    /// row cannot change size when a badge appears, a status changes word, a
    /// progress bar starts, or a control becomes hoverable, focused or disabled.
    [<RequireQualifiedAccess>]
    module Geometry =

        /// Content cards draw no specular highlight. Zero, not "small": the
        /// gradient's *reach* does not shrink with its alpha, so any non-zero
        /// value is a pale band across the top third rather than a faint edge.
        [<Literal>]
        let contentHighlight = 0.0

        /// The floating sidebar keeps the full highlight and is approved that
        /// way. Recorded here only so a test can state that the Planner's zero
        /// is deliberate and does not apply to the shell.
        [<Literal>]
        let sidebarHighlight = 1.0

        /// The main planning row's column weights, as the redesign's split.
        ///
        /// Star weights rather than pixels, so the ratio holds at every width
        /// the row is shown at.
        [<Literal>]
        let mainLeftWeight = 65.0

        [<Literal>]
        let mainRightWeight = 35.0

        /// Shared floors, one per row rather than one per card.
        ///
        /// A row keeps the redesign's proportions even when its cards are empty,
        /// and because the floor is on the ROW every card in it starts equal —
        /// which is half of what makes their top and bottom edges line up. The
        /// other half is that each cell stretches.
        [<Literal>]
        let mainRowMinHeight = 340.0

        [<Literal>]
        let analysisRowMinHeight = 190.0

        [<Literal>]
        let bottomRowMinHeight = 170.0

        /// Column definition for a glyph-plus-wrapping-text row.
        ///
        /// The star column is the whole point. A horizontal `StackPanel`
        /// measures children with infinite available width, so a wrapping
        /// `TextBlock` inside one is never given a width to wrap at and simply
        /// runs past the card edge. `Auto` for the glyph and `*` for the text
        /// gives the text a real finite width. Replacing the `*` with a fixed
        /// width would reintroduce the overflow at a different window size,
        /// which is why the string is pinned by a test.
        [<Literal>]
        let bulletColumns = "Auto,9,*"

        /// Every action button, in every state, is exactly this tall.
        [<Literal>]
        let actionMinHeight = 38.0

        [<Literal>]
        let actionRadius = 11.0

        /// Half the glass hairline's 1px stroke.
        ///
        /// A stroke is centred on its path, so an outline laid exactly on a
        /// panel's bounds puts half its width outside them, where the draw
        /// operation clips it — a half-weight line whose corners are clipped
        /// asymmetrically as the curve turns, which is what made the action
        /// buttons look chewed at the corners. Pulling the path in by this much
        /// puts the whole stroke inside the panel.
        [<Literal>]
        let borderInset = 0.5

        /// Reserved for the status pill. The widest label is "IN PROGRESS"; a
        /// row must not reflow when a session moves from SCHEDULED to it.
        [<Literal>]
        let statusMinWidth = 96.0

        /// Reserved for the time range, so 09:00–09:50 and 10:00–11:30 occupy
        /// the same column and the duration beside them never shifts.
        [<Literal>]
        let timeMinWidth = 104.0

        [<Literal>]
        let durationMinWidth = 62.0

        /// The elapsed-progress strip is always this tall, whether or not a bar
        /// is drawn in it. A session crossing its start time used to insert the
        /// bar and push every row below it down.
        [<Literal>]
        let progressStripHeight = 10.0

        /// A session row's floor, so the loading skeleton can reserve the same
        /// space the real rows will occupy.
        [<Literal>]
        let sessionRowMinHeight = 74.0

        /// Every strategy control is this tall in both states. Selection changes
        /// colour, never size.
        [<Literal>]
        let strategyMinHeight = 84.0

        /// Reserved for the SELECTED badge and the preview metrics line, both of
        /// which appear only sometimes.
        [<Literal>]
        let strategyBadgeHeight = 16.0

        [<Literal>]
        let strategyMetricsHeight = 15.0

        /// Border thickness is identical selected and unselected — an unselected
        /// control reserves it with a transparent brush rather than dropping it,
        /// which is what used to make the selected pill a pixel taller.
        [<Literal>]
        let strategyBorderThickness = 1.0

    // ---- page composition ---------------------------------------------------

    /// Every card on the page, in the order the redesign presents them.
    ///
    /// Held as data so the wide-width layout can be asserted without rendering
    /// anything, and so no card can be quietly dropped: a test checks that the
    /// rows below partition this list exactly.
    type Card =
        | TodayPlan
        | ScheduleOptimisation
        | PlanReasoning
        | WorkloadDistribution
        | ConstraintValidation
        | PlannerInsights
        | AlternativePlans
        | PlannerHistory
        | FuturePrediction

    /// The main planning row: the plan on the left, optimisation and reasoning
    /// stacked on the right.
    let mainRow = [ TodayPlan ]
    let sideColumn = [ ScheduleOptimisation; PlanReasoning ]

    /// The secondary analysis row — three equal cards at wide width.
    let analysisRow = [ WorkloadDistribution; ConstraintValidation; PlannerInsights ]

    /// Full width, on its own.
    let fullWidthRow = [ AlternativePlans ]

    /// The bottom pair.
    let bottomRow = [ PlannerHistory; FuturePrediction ]

    let cardOrder =
        mainRow @ sideColumn @ analysisRow @ fullWidthRow @ bottomRow

    // ---- responsive layout --------------------------------------------------

    /// Whether the optimisation and reasoning column sits beside the plan or
    /// beneath it. Same threshold as the Timeline, so the two screens break at
    /// the same width.
    let showsSideColumn (contentWidth: float) = contentWidth >= 1100.0

    /// Columns for the three analysis cards.
    let summaryColumns (contentWidth: float) =
        if contentWidth < 720.0 then 1
        elif contentWidth < 1180.0 then 2
        else 3

    /// Columns for the history / outlook row. Two side by side until the page is
    /// too narrow to read them that way.
    let bottomColumns (contentWidth: float) = if contentWidth < 900.0 then 1 else 2

    /// The alternative-strategy pills per row, so they wrap rather than
    /// compress. The pills carry a description, so they need real width.
    let strategyColumns (contentWidth: float) =
        if contentWidth < 720.0 then 1
        elif contentWidth < 1180.0 then 2
        else 4
