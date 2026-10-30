namespace UniversityOS.Application

open System
open UniversityOS.Domain

/// Pure, deterministic habit statistics derived from the user's own history.
/// These feed both the transparent memory entries and the planner's
/// personalization — and being pure, they are directly unit-tested.
[<RequireQualifiedAccess>]
module MemoryStats =

    /// How the user's actual effort compares to their estimates, from closed
    /// assignments that carried both numbers. > 1 = slower than estimated.
    /// Clamped to a sane band; None until there's enough signal.
    let paceFactor (assignments: Assignment list) : float option =
        let closed =
            assignments
            |> List.filter (fun a -> not (Assignment.isOpen a))
            |> List.filter (fun a -> a.EstimatedHours.IsSome && a.ActualHours > 0.0)

        if closed.Length < 2 then
            None
        else
            let estimated = closed |> List.sumBy (fun a -> a.EstimatedHours.Value)
            let actual = closed |> List.sumBy (fun a -> a.ActualHours)

            if estimated <= 0.0 then None
            else Some(max 0.5 (min 2.0 (actual / estimated)))

    let averageSessionMinutes (completed: PlannedSession list) : int option =
        if completed.Length < 3 then None
        else
            Some(completed |> List.averageBy (fun s -> float (PlannedSession.durationMinutes s)) |> int)

    /// The hour of day the user most often completes study sessions.
    let preferredHour (completed: PlannedSession list) : int option =
        if completed.Length < 3 then
            None
        else
            completed
            |> List.countBy (fun s -> s.StartsAt.Hour)
            |> List.sortByDescending snd
            |> List.tryHead
            |> Option.map fst

    /// The weekday with the most completed study minutes.
    let productiveWeekday (completed: PlannedSession list) : DayOfWeek option =
        if completed.Length < 3 then
            None
        else
            completed
            |> List.groupBy (fun s -> s.StartsAt.DayOfWeek)
            |> List.map (fun (day, sessions) -> day, sessions |> List.sumBy PlannedSession.durationMinutes)
            |> List.sortByDescending snd
            |> List.tryHead
            |> Option.map fst
