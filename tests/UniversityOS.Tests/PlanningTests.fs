module UniversityOS.Tests.PlanningTests

open System
open Xunit
open UniversityOS.Domain
open UniversityOS.Domain.Planning

/// The planner is pure — these tests pin down its contract: realistic schedules
/// that respect busy time, the daily cap, the workday window and deadlines.

let private prefs =
    { WorkdayStart = TimeSpan.FromHours 9.0
      WorkdayEnd = TimeSpan.FromHours 18.0
      SessionMinutes = 50
      BreakMinutes = 10
      MaxDailyStudyMinutes = 240
      DeadlineBufferHours = 12.0
      HorizonDays = 7 }

// A fixed Monday morning so tests are deterministic.
let private monday9 = DateTime(2026, 7, 20, 9, 0, 0)

let private task title deadline minutes priority =
    { AssignmentId = AssignmentId.New()
      Title = title
      ModuleCode = Some "CS101"
      Deadline = deadline
      RemainingMinutes = minutes
      Priority = priority
      Weighting = None 
      Milestones = [] }

let private input tasks busy =
    { Now = monday9
      Tasks = tasks
      Busy = busy
      Prefs = prefs }

[<Fact>]
let ``plans work for a single task with a deadline`` () =
    let result = plan (input [ task "Essay" (Some(monday9.AddDays 3.0)) 120 Priority.Medium ] [])

    Assert.NotEmpty result.Blocks
    Assert.Equal(120, result.Blocks |> List.sumBy (fun b -> int (b.End - b.Start).TotalMinutes))
    Assert.Empty result.Unplanned

[<Fact>]
let ``planned blocks never overlap busy time`` () =
    let busy =
        [ { Start = monday9.AddHours 1.0; End = monday9.AddHours 3.0; Label = "Lecture" } ]

    let result = plan (input [ task "Report" (Some(monday9.AddDays 2.0)) 180 Priority.High ] busy)

    for block in result.Blocks do
        let overlaps =
            busy
            |> List.exists (fun b -> block.Start < b.End && block.End > b.Start)

        Assert.False(overlaps, sprintf "Block %A–%A overlaps a busy slot" block.Start block.End)

[<Fact>]
let ``respects the daily study cap`` () =
    let result = plan (input [ task "Big project" (Some(monday9.AddDays 6.0)) 2000 Priority.Medium ] [])

    let byDay =
        result.Blocks
        |> List.groupBy (fun b -> b.Start.Date)
        |> List.map (fun (_, blocks) -> blocks |> List.sumBy (fun b -> int (b.End - b.Start).TotalMinutes))

    for minutes in byDay do
        Assert.True(minutes <= prefs.MaxDailyStudyMinutes, sprintf "Planned %d min in one day (cap %d)" minutes prefs.MaxDailyStudyMinutes)

[<Fact>]
let ``stays inside the workday window`` () =
    let result = plan (input [ task "Lab" (Some(monday9.AddDays 4.0)) 400 Priority.Medium ] [])

    for block in result.Blocks do
        Assert.True(block.Start.TimeOfDay >= prefs.WorkdayStart)
        Assert.True(block.End.TimeOfDay <= prefs.WorkdayEnd)

[<Fact>]
let ``reports work that cannot fit before the horizon`` () =
    // 100h of work cannot fit in 7 days × 4h cap.
    let result = plan (input [ task "Impossible" (Some(monday9.AddDays 2.0)) 6000 Priority.High ] [])

    Assert.NotEmpty result.Unplanned

[<Fact>]
let ``urgent deadline outranks distant high priority in recommendations`` () =
    let urgent = task "Due tomorrow" (Some(monday9.AddDays 1.0)) 120 Priority.Low
    let distant = task "Due next month" (Some(monday9.AddDays 30.0)) 120 Priority.High

    match recommendNext monday9 [ distant; urgent ] with
    | Some(recommended, _) -> Assert.Equal("Due tomorrow", recommended.Title)
    | None -> failwith "Expected a recommendation"

[<Fact>]
let ``every planned block carries a human-readable reason`` () =
    let result = plan (input [ task "Essay" (Some(monday9.AddDays 3.0)) 120 Priority.High ] [])

    for block in result.Blocks do
        Assert.False(String.IsNullOrWhiteSpace block.Reason)

[<Fact>]
let ``planning is deterministic`` () =
    let tasks =
        [ task "A" (Some(monday9.AddDays 2.0)) 200 Priority.Medium
          task "B" (Some(monday9.AddDays 4.0)) 300 Priority.High ]

    let r1 = plan (input tasks [])
    let r2 = plan (input tasks [])

    Assert.Equal<(DateTime * DateTime) list>(
        r1.Blocks |> List.map (fun b -> b.Start, b.End),
        r2.Blocks |> List.map (fun b -> b.Start, b.End)
    )

[<Fact>]
let ``free slots subtract busy blocks`` () =
    let busy =
        [ { Start = monday9.AddHours 2.0; End = monday9.AddHours 4.0; Label = "Lecture" } ]

    let slots = freeSlotsForDay prefs busy monday9 monday9

    // 09:00–11:00 and 13:00–18:00.
    Assert.Equal(2, slots.Length)
    Assert.Equal(monday9, fst slots.[0])
    Assert.Equal(monday9.AddHours 2.0, snd slots.[0])
    Assert.Equal(monday9.AddHours 4.0, fst slots.[1])
