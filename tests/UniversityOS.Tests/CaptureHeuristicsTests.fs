module UniversityOS.Tests.CaptureHeuristicsTests

open System
open Xunit
open UniversityOS.Domain
open UniversityOS.Application

/// The offline capture classifier must stay dependable — it is the fallback
/// whenever the AI is unconfigured or unreachable.

let private now = DateTime(2026, 7, 20, 10, 0, 0)
let private knownCodes = [ "CS2004"; "MA1002" ]

let private analyze title body =
    CaptureHeuristics.analyze now knownCodes title body

[<Fact>]
let ``iso dates are found`` () =
    let dates = CaptureHeuristics.findDates now "submit by 2026-08-15 please"
    Assert.Contains(DateTime(2026, 8, 15), dates)

[<Fact>]
let ``uk slash dates are found`` () =
    let dates = CaptureHeuristics.findDates now "deadline 15/08/2026"
    Assert.Contains(DateTime(2026, 8, 15), dates)

[<Fact>]
let ``day-month names resolve to the next occurrence`` () =
    let dates = CaptureHeuristics.findDates now "hand in on 15 August"
    Assert.Contains(DateTime(2026, 8, 15), dates)

[<Fact>]
let ``known module codes are matched even with spacing differences`` () =
    Assert.Equal(Some "CS2004", CaptureHeuristics.findModuleCode knownCodes "the CS 2004 coursework")
    Assert.Equal(Some "CS2004", CaptureHeuristics.findModuleCode knownCodes "CS2004 lab sheet")

[<Fact>]
let ``assignment with due date proposes create_assignment with a deadline`` () =
    let analysis = analyze "CS2004 coursework" "The report is due 2026-08-15 at 17:00."

    let action =
        analysis.Actions |> List.find (fun a -> a.Kind = ProposedActionKind.CreateAssignment)

    Assert.Equal(Some "CS2004", action.ModuleCode)
    Assert.Equal(Some(DateTime(2026, 8, 15, 17, 0, 0)), action.Date)

[<Fact>]
let ``due date without a time defaults to 5pm`` () =
    let analysis = analyze "Essay" "essay due 2026-08-10"

    let action =
        analysis.Actions |> List.find (fun a -> a.Kind = ProposedActionKind.CreateAssignment)

    Assert.Equal(Some(DateTime(2026, 8, 10, 17, 0, 0)), action.Date)

[<Fact>]
let ``event keyword with a date proposes create_event`` () =
    let analysis = analyze "Guest lecture" "There is a lecture on 2026-07-25 at 14:00 in LT1."

    let action =
        analysis.Actions |> List.find (fun a -> a.Kind = ProposedActionKind.CreateEvent)

    Assert.Equal(Some(DateTime(2026, 7, 25, 14, 0, 0)), action.Date)

[<Fact>]
let ``plain prose falls back to create_note`` () =
    let analysis = analyze "Thoughts" "Some ideas about the group project architecture and testing strategy."

    Assert.Contains(analysis.Actions, fun a -> a.Kind = ProposedActionKind.CreateNote)

[<Fact>]
let ``empty body yields no_action, never an empty action list`` () =
    let analysis = analyze "Ping" ""

    Assert.NotEmpty analysis.Actions
    Assert.Contains(analysis.Actions, fun a -> a.Kind = ProposedActionKind.NoAction)

[<Fact>]
let ``analyzer is marked heuristic`` () =
    Assert.Equal("heuristic", (analyze "x" "y").Analyzer)
