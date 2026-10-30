module UniversityOS.Tests.MemoryStatsTests

open System
open Xunit
open UniversityOS.Domain
open UniversityOS.Application

/// The pure habit statistics behind the transparent AI memory and the
/// planner's pace personalization.

let private now = DateTime(2026, 7, 20, 12, 0, 0)

let private closedAssignment (estimated: float) (actual: float) =
    { Assignment.create now "Done" with
        Status = AssignmentStatus.Submitted
        EstimatedHours = Some estimated
        ActualHours = actual }

let private sessionAt (start: DateTime) (minutes: float) =
    { Id = SessionId.New()
      AssignmentId = None
      MilestoneId = None
      Title = "Study"
      ModuleCode = None
      StartsAt = start
      EndsAt = start.AddMinutes minutes
      Reason = "test"
      Completed = true
      CreatedAt = now }

[<Fact>]
let ``pace factor needs at least two closed assignments with both numbers`` () =
    Assert.Equal(None, MemoryStats.paceFactor [])
    Assert.Equal(None, MemoryStats.paceFactor [ closedAssignment 10.0 12.0 ])

    // Open assignments carry no signal even with both numbers present.
    let openOne =
        { Assignment.create now "Open" with EstimatedHours = Some 4.0; ActualHours = 5.0 }

    Assert.Equal(None, MemoryStats.paceFactor [ closedAssignment 10.0 12.0; openOne ])

[<Fact>]
let ``pace factor is total actual over total estimated`` () =
    // 30h actual vs 20h estimated = 1.5× slower than estimates.
    let result =
        MemoryStats.paceFactor [ closedAssignment 10.0 15.0; closedAssignment 10.0 15.0 ]

    Assert.Equal(Some 1.5, result)

[<Fact>]
let ``pace factor clamps to the 0.5 – 2.0 band`` () =
    Assert.Equal(Some 2.0, MemoryStats.paceFactor [ closedAssignment 1.0 10.0; closedAssignment 1.0 10.0 ])
    Assert.Equal(Some 0.5, MemoryStats.paceFactor [ closedAssignment 10.0 1.0; closedAssignment 10.0 1.0 ])

[<Fact>]
let ``session stats need at least three completed sessions`` () =
    let two = [ sessionAt now 50.0; sessionAt (now.AddHours 2.0) 50.0 ]

    Assert.Equal(None, MemoryStats.averageSessionMinutes two)
    Assert.Equal(None, MemoryStats.preferredHour two)
    Assert.Equal(None, MemoryStats.productiveWeekday two)

[<Fact>]
let ``session stats compute average, preferred hour and productive weekday`` () =
    let sessions =
        [ sessionAt (DateTime(2026, 7, 20, 9, 0, 0)) 60.0 // Monday 09:00
          sessionAt (DateTime(2026, 7, 21, 9, 0, 0)) 30.0 // Tuesday 09:00
          sessionAt (DateTime(2026, 7, 22, 14, 0, 0)) 30.0 ] // Wednesday 14:00

    Assert.Equal(Some 40, MemoryStats.averageSessionMinutes sessions)
    Assert.Equal(Some 9, MemoryStats.preferredHour sessions)
    Assert.Equal(Some DayOfWeek.Monday, MemoryStats.productiveWeekday sessions)
