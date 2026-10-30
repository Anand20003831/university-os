namespace UniversityOS.Domain

open System

/// A week of study, reduced to what a summary card shows.
///
/// Derived once from one list of sessions, so every figure on the card and the
/// "Studied this week" tile beside it come from the same read and cannot
/// disagree. `Last` is the most recent completed session that has already
/// finished — a session still running is not something you "did".
type StudySummary =
    { MinutesToday: int
      MinutesThisWeek: int
      /// Completed sessions in the week, counted once each.
      CompletedThisWeek: int
      /// Mean length of those sessions, rounded. Zero when there are none.
      AverageMinutes: int
      /// Title, length in minutes, and when it ended.
      Last: (string * int * DateTime) option }

/// How much studying actually happened, and how to say it.
///
/// One authoritative definition, shared by the Dashboard and the Timeline, so
/// the two screens can never disagree about "this week". Pure: given the same
/// sessions and the same clock it always returns the same number, which is what
/// makes the week-boundary and midnight cases testable without a database.
///
/// **What counts as study time.** Only `PlannedSession`s marked `Completed`.
/// That is the app's single record of elapsed study — the planner writes the
/// blocks, the user ticks them off, and `SessionCompleted` activity entries are
/// merely a *notification* that it happened. Counting activity entries instead
/// would double-count (one session can be ticked, unticked and ticked again,
/// writing several entries) and would miss sessions completed before the
/// activity log existed.
[<RequireQualifiedAccess>]
module StudyTime =

    /// The first instant of the calendar week containing `now`, for a given
    /// first-day-of-week.
    ///
    /// `firstDay` is a parameter rather than a constant because "the calendar
    /// week" is a cultural fact, not a universal one: `CultureInfo`'s
    /// `FirstDayOfWeek` is Monday under en-GB and Sunday under en-US. Callers
    /// pass the user's culture so both screens move together.
    let weekStart (firstDay: DayOfWeek) (now: DateTime) =
        let offset = ((int now.DayOfWeek - int firstDay) + 7) % 7
        now.Date.AddDays(float -offset)

    /// Minutes of `session` that fall inside [rangeStart, rangeEnd).
    ///
    /// Clipped rather than counted whole, which is what makes the two awkward
    /// cases correct at once:
    ///
    ///   * **Midnight and week boundaries.** A session running 23:30 Sunday to
    ///     00:30 Monday belongs half to each week. The repository returns it for
    ///     *both* week queries (its range filter is an overlap test), so summing
    ///     whole durations would count those 60 minutes twice — once in each
    ///     week. Clipping gives 30 and 30.
    ///   * **A session still in progress at the range end.** Only the elapsed
    ///     part is inside the window.
    ///
    /// Zero for a session that does not overlap the range at all, and never
    /// negative for an inverted session.
    let minutesInRange (rangeStart: DateTime) (rangeEnd: DateTime) (session: PlannedSession) =
        let from = max session.StartsAt rangeStart
        let until = min session.EndsAt rangeEnd
        max 0 (int (until - from).TotalMinutes)

    /// Total completed study minutes inside a range.
    ///
    /// De-duplicates by session id first: a repository join, a re-sync or an
    /// overlapping query can hand back the same session twice, and the sum must
    /// not depend on that.
    let minutesStudiedIn (rangeStart: DateTime) (rangeEnd: DateTime) (sessions: PlannedSession list) =
        sessions
        |> List.filter (fun s -> s.Completed)
        |> List.distinctBy (fun s -> s.Id)
        |> List.sumBy (minutesInRange rangeStart rangeEnd)

    /// Total completed study minutes in the calendar week containing `now`.
    let minutesStudiedThisWeek (firstDay: DayOfWeek) (now: DateTime) (sessions: PlannedSession list) =
        let start = weekStart firstDay now
        minutesStudiedIn start (start.AddDays 7.0) sessions

    /// Everything the Recent Focus card shows, from ONE list of sessions.
    ///
    /// The caller already reads the week's sessions for the "Studied this week"
    /// tile; this derives the rest from that same list rather than issuing a
    /// second query. `sessions` is whatever the repository returned for the week
    /// window — it may contain duplicates and sessions that merely overlap the
    /// week, both of which are handled here rather than at the call site.
    let summarise (firstDay: DayOfWeek) (now: DateTime) (sessions: PlannedSession list) =
        let weekStart = weekStart firstDay now
        let weekEnd = weekStart.AddDays 7.0

        let completed =
            sessions
            |> List.filter (fun s -> s.Completed)
            |> List.distinctBy (fun s -> s.Id)

        // Counted, not clipped: a session that straddles midnight is one
        // session. Its MINUTES are split across days, but it is not half a
        // session in each. The two questions have different answers and the
        // card asks both.
        let inWeek =
            completed |> List.filter (fun s -> s.StartsAt < weekEnd && s.EndsAt > weekStart)

        let minutesThisWeek = completed |> List.sumBy (minutesInRange weekStart weekEnd)
        let minutesToday = completed |> List.sumBy (minutesInRange now.Date (now.Date.AddDays 1.0))

        { MinutesToday = minutesToday
          MinutesThisWeek = minutesThisWeek
          CompletedThisWeek = inWeek.Length
          AverageMinutes =
            if inWeek.IsEmpty then
                0
            else
                // Away-from-zero, not .NET's default banker's rounding: two
                // sessions averaging 50.5 minutes should read 51, not 50.
                // Banker's rounding is right for money and surprising here.
                int (Math.Round(float minutesThisWeek / float inWeek.Length, MidpointRounding.AwayFromZero))
          Last =
            inWeek
            // Already finished: a session running right now has not been done.
            |> List.filter (fun s -> s.EndsAt <= now)
            |> List.sortByDescending (fun s -> s.EndsAt)
            |> List.tryHead
            |> Option.map (fun s -> s.Title, int (s.EndsAt - s.StartsAt).TotalMinutes, s.EndsAt) }

    /// An empty week — what the card shows before anything has been ticked off.
    let emptySummary =
        { MinutesToday = 0
          MinutesThisWeek = 0
          CompletedThisWeek = 0
          AverageMinutes = 0
          Last = None }

    /// A duration in the natural voice the summary cards use:
    /// `0 min`, `45 min`, `1 hr 20 min`, `2 hrs`, `6 hrs 5 min`.
    ///
    /// Deliberately not `Format.duration`, which renders `1h 20m` for dense
    /// list rows. A summary tile is read at a glance and gets words.
    let format (minutes: int) =
        let minutes = max 0 minutes
        let hours = minutes / 60
        let rest = minutes % 60
        let hourWord = if hours = 1 then "hr" else "hrs"

        if hours = 0 then sprintf "%d min" rest
        elif rest = 0 then sprintf "%d %s" hours hourWord
        else sprintf "%d %s %d min" hours hourWord rest
