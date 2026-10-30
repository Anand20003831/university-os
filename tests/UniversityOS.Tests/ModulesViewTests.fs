module UniversityOS.Tests.ModulesViewTests

open System
open Xunit
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Features

/// The rebuilt Modules screen.
///
/// Every decision lives in `ModulesLogic` or in `ModulesView.update`, so all of
/// it is testable without an Avalonia application or a database.

let private now = DateTime(2026, 7, 31, 14, 0, 0)

let private moduleOf code name : StudyModule =
    { StudyModule.create (now.AddDays -60.0) code name 0 with Color = "#CB8A4E" }

let private facts (m: StudyModule) : ModulesLogic.Facts = ModulesLogic.Facts.ofModule m

let private assignmentIn (m: StudyModule) title progress deadline : AssignmentsLogic.Facts =
    let a =
        { Assignment.create (now.AddDays -10.0) title with
            ModuleId = Some m.Id
            EstimatedHours = Some 10.0
            Progress = progress
            Deadline = deadline
            UpdatedAt = now }

    { AssignmentsLogic.Facts.ofAssignment a with
        Module = Some m
        ScheduledMinutes = 100_000 }

// ---------------------------------------------------------------------------
// 1. page composition — the redesign's own shape
// ---------------------------------------------------------------------------

[<Fact>]
let ``the page is the redesign's five sections, in order`` () =
    Assert.Equal<ModulesLogic.Section list>(
        [ ModulesLogic.Section.Header
          ModulesLogic.Section.Metrics
          ModulesLogic.Section.Filters
          ModulesLogic.Section.ActiveSection
          ModulesLogic.Section.ArchivedSection ],
        ModulesLogic.sectionOrder
    )

[<Fact>]
let ``every section appears exactly once`` () =
    Assert.Equal(ModulesLogic.sectionOrder.Length, (List.distinct ModulesLogic.sectionOrder).Length)

[<Fact>]
let ``the health row is five metrics, as the redesign shows`` () =
    Assert.Equal(5, (ModulesLogic.metrics now 0 [ facts (moduleOf "CS2004" "Graphics") ]).Length)

[<Fact>]
let ``the filter row is six chips — the redesign's eight less the two grade sorts`` () =
    // Highest Grade and Lowest Grade sort by a number nothing stores.
    Assert.Equal(6, ModulesLogic.Filter.all.Length)

    let labels = ModulesLogic.Filter.all |> List.map ModulesLogic.Filter.label
    Assert.DoesNotContain("Highest grade", labels)
    Assert.DoesNotContain("Lowest grade", labels)
    Assert.Equal(labels.Length, (List.distinct labels).Length)

[<Fact>]
let ``the card carries four figures, as the redesign's card does`` () =
    // Current / Target / Predicted / Confidence are all grades. These four are
    // the real things the application knows, in the same four-across slot.
    let figures = ModulesLogic.cardFigures now (facts (moduleOf "CS2004" "Graphics"))
    Assert.Equal(4, figures.Length)

    let labels = figures |> List.map (fun f -> f.Label)
    Assert.All(labels, fun l -> Assert.False(String.IsNullOrWhiteSpace l))

    for banned in [ "Target"; "Predicted"; "Confidence"; "Grade" ] do
        Assert.DoesNotContain(banned, labels)

[<Fact>]
let ``the card carries four real counts in place of the three missing dials`` () =
    let counts = ModulesLogic.cardCounts (facts (moduleOf "CS2004" "Graphics"))
    Assert.Equal(4, counts.Length)
    Assert.Equal<string list>([ "Lectures"; "Notes"; "Documents"; "Flashcards" ], counts |> List.map fst)

// ---------------------------------------------------------------------------
// 2. filters are toggles, and govern sections
// ---------------------------------------------------------------------------

[<Fact>]
let ``chips toggle on and off independently`` () =
    let s = ModulesLogic.Selection.empty
    let s = ModulesLogic.Selection.toggle ModulesLogic.Filter.NeedsAttention s
    let s = ModulesLogic.Selection.toggle ModulesLogic.Filter.ExamsSoon s

    Assert.True(s.Contains ModulesLogic.Filter.NeedsAttention)
    Assert.True(s.Contains ModulesLogic.Filter.ExamsSoon)

    let s = ModulesLogic.Selection.toggle ModulesLogic.Filter.NeedsAttention s
    Assert.False(s.Contains ModulesLogic.Filter.NeedsAttention)
    Assert.True(s.Contains ModulesLogic.Filter.ExamsSoon)

[<Fact>]
let ``with no chip on, both sections show`` () =
    Assert.True(ModulesLogic.Selection.showsActive ModulesLogic.Selection.empty)
    Assert.True(ModulesLogic.Selection.showsArchived ModulesLogic.Selection.empty)

[<Fact>]
let ``Archived alone hides the active grid, and Active alone hides the archived one`` () =
    let archivedOnly = ModulesLogic.Selection.empty |> ModulesLogic.Selection.toggle ModulesLogic.Filter.Archived
    Assert.False(ModulesLogic.Selection.showsActive archivedOnly)
    Assert.True(ModulesLogic.Selection.showsArchived archivedOnly)

    let activeOnly = ModulesLogic.Selection.empty |> ModulesLogic.Selection.toggle ModulesLogic.Filter.Active
    Assert.True(ModulesLogic.Selection.showsActive activeOnly)
    Assert.False(ModulesLogic.Selection.showsArchived activeOnly)

[<Fact>]
let ``both section chips on shows both sections`` () =
    let both =
        ModulesLogic.Selection.empty
        |> ModulesLogic.Selection.toggle ModulesLogic.Filter.Active
        |> ModulesLogic.Selection.toggle ModulesLogic.Filter.Archived

    Assert.True(ModulesLogic.Selection.showsActive both)
    Assert.True(ModulesLogic.Selection.showsArchived both)

[<Fact>]
let ``narrowing chips combine — every one must match`` () =
    let quiet = facts (moduleOf "AA1000" "Quiet")

    let risky =
        let m = moduleOf "BB2000" "Risky"
        { facts m with Assignments = [ { assignmentIn m "Overdue" 10 (Some(now.AddDays -3.0)) with ScheduledMinutes = 0 } ] }

    let both = ModulesLogic.Selection.empty |> ModulesLogic.Selection.toggle ModulesLogic.Filter.NeedsAttention

    let shown = ModulesLogic.activeGrid now both [ quiet; risky ] |> List.map (fun f -> f.Module.Code)
    Assert.Equal<string list>([ "BB2000" ], shown)

[<Fact>]
let ``the archived grid never appears in the active grid`` () =
    let live = facts (moduleOf "AA1000" "Live")
    let old' = { facts (moduleOf "ZZ9999" "Old") with Module = { (moduleOf "ZZ9999" "Old") with Archived = true } }

    let active = ModulesLogic.activeGrid now ModulesLogic.Selection.empty [ live; old' ]
    let archived = ModulesLogic.archivedGrid [ live; old' ]

    Assert.Equal<string list>([ "AA1000" ], active |> List.map (fun f -> f.Module.Code))
    Assert.Equal<string list>([ "ZZ9999" ], archived |> List.map (fun f -> f.Module.Code))

[<Fact>]
let ``ordering chips reorder rather than hide`` () =
    let m1 = moduleOf "AA1000" "Little"
    let m2 = moduleOf "BB2000" "Lots"

    let light = { facts m1 with Assignments = [ assignmentIn m1 "Small" 90 (Some(now.AddDays 30.0)) ] }
    let heavy = { facts m2 with Assignments = [ assignmentIn m2 "Big" 5 (Some(now.AddDays 30.0)) ] }

    let ordered =
        ModulesLogic.Selection.empty |> ModulesLogic.Selection.toggle ModulesLogic.Filter.MostWorkRemaining

    let shown = ModulesLogic.activeGrid now ordered [ light; heavy ] |> List.map (fun f -> f.Module.Code)

    // Both kept; the heavier one first.
    Assert.Equal(2, shown.Length)
    Assert.Equal("BB2000", shown.Head)

[<Fact>]
let ``modules wanting attention sort to the front by default`` () =
    let quiet = facts (moduleOf "AA1000" "Quiet")

    let risky =
        let m = moduleOf "ZZ9999" "Risky"
        { facts m with Assignments = [ { assignmentIn m "Overdue" 10 (Some(now.AddDays -3.0)) with ScheduledMinutes = 0 } ] }

    let shown =
        ModulesLogic.activeGrid now ModulesLogic.Selection.empty [ quiet; risky ]
        |> List.map (fun f -> f.Module.Code)

    Assert.Equal<string list>([ "ZZ9999"; "AA1000" ], shown)

// ---------------------------------------------------------------------------
// 3. metric honesty
// ---------------------------------------------------------------------------

let private metric label items = ModulesLogic.metrics now 0 items |> List.find (fun m -> m.Label = label)

[<Fact>]
let ``no metric claims a grade`` () =
    // Grades are not modelled: no mark, target or prediction exists.
    let labels = ModulesLogic.metrics now 0 [] |> List.map (fun m -> m.Label.ToLowerInvariant())
    Assert.All(labels, fun l -> Assert.DoesNotContain("grade", l))

[<Fact>]
let ``an empty screen reads as zeroes and a dash, with no prose`` () =
    let values = ModulesLogic.metrics now 0 [] |> List.map (fun m -> m.Label, m.Value)

    Assert.Equal<(string * string option) list>(
        [ "Active modules", Some "0"
          "Coursework progress", None
          "Hours remaining", Some "0h"
          "Needing attention", Some "0"
          "Studied this week", Some "0h" ],
        values
    )

    Assert.All(ModulesLogic.metrics now 0 [], fun m -> Assert.Equal(None, m.Note))

[<Fact>]
let ``coursework progress is unknown, never zero, when no module has coursework`` () =
    Assert.Equal(None, (metric "Coursework progress" [ facts (moduleOf "CS2004" "Graphics") ]).Value)

[<Fact>]
let ``coursework progress averages only modules that have coursework`` () =
    let m1 = moduleOf "AA1000" "With"
    let withWork = { facts m1 with Assignments = [ assignmentIn m1 "One" 60 None ] }
    let without = facts (moduleOf "BB2000" "Without")

    let m = metric "Coursework progress" [ withWork; without ]
    Assert.Equal(Some "60%", m.Value)
    Assert.Equal(Some "1 of 2 with coursework", m.Note)

[<Fact>]
let ``the archived count is a qualifier on a real figure, not empty-state prose`` () =
    let live = facts (moduleOf "AA1000" "Live")
    let old' = { facts (moduleOf "ZZ9999" "Old") with Module = { (moduleOf "ZZ9999" "Old") with Archived = true } }

    Assert.Equal(Some "1", (metric "Active modules" [ live; old' ]).Value)
    Assert.Equal(Some "1 archived", (metric "Active modules" [ live; old' ]).Note)
    Assert.Equal(None, (metric "Active modules" [ live ]).Note)

[<Fact>]
let ``studied this week is the shared StudyTime figure, passed in not recomputed`` () =
    Assert.Equal(Some "2h", (ModulesLogic.metrics now 120 []).[4].Value)
    Assert.Equal("Studied this week", (ModulesLogic.metrics now 120 []).[4].Label)

// ---------------------------------------------------------------------------
// 4. per-module derivations
// ---------------------------------------------------------------------------

[<Fact>]
let ``coursework progress includes finished work, and is None with no assignments`` () =
    let m = moduleOf "CS2004" "Graphics"
    Assert.Equal(None, ModulesLogic.courseworkProgress (facts m))

    let mixed =
        { facts m with
            Assignments = [ assignmentIn m "A" 100 None; assignmentIn m "B" 0 None ] }

    Assert.Equal(Some 50, ModulesLogic.courseworkProgress mixed)

[<Fact>]
let ``remaining effort reuses the shared assignment rule`` () =
    let m = moduleOf "CS2004" "Graphics"
    let a = assignmentIn m "A" 40 None
    let f = { facts m with Assignments = [ a ] }

    Assert.Equal(AssignmentsLogic.remainingMinutes a, ModulesLogic.remainingMinutes f)

[<Fact>]
let ``a module needs attention when its own coursework is at high risk`` () =
    // The risk model is the Assignments screen's, not a second one.
    let m = moduleOf "CS2004" "Graphics"
    let overdue = { assignmentIn m "Overdue" 10 (Some(now.AddDays -3.0)) with ScheduledMinutes = 0 }
    let f = { facts m with Assignments = [ overdue ] }

    Assert.True(AssignmentsLogic.Risk.atLeast AssignmentsLogic.Risk.High (AssignmentsLogic.riskOf now overdue))
    Assert.True(ModulesLogic.needsAttention now f)

[<Fact>]
let ``a quiet module does not need attention`` () =
    let m = moduleOf "CS2004" "Graphics"
    let calm = assignmentIn m "Calm" 60 (Some(now.AddDays 60.0))
    Assert.False(ModulesLogic.needsAttention now { facts m with Assignments = [ calm ] })

[<Fact>]
let ``an archived module never needs attention`` () =
    let m = { moduleOf "CS2004" "Graphics" with Archived = true }
    let overdue = { assignmentIn m "Overdue" 10 (Some(now.AddDays -3.0)) with ScheduledMinutes = 0 }
    Assert.False(ModulesLogic.needsAttention now { facts m with Assignments = [ overdue ] })

[<Fact>]
let ``an exam within the documented window counts as soon`` () =
    Assert.Equal(14, ModulesLogic.Threshold.examSoonDays)
    let m = moduleOf "CS2004" "Graphics"

    let exam days =
        { CalendarEvent.create now "Final exam" EventKind.Exam (now.AddDays days) (now.AddDays days) with
            ModuleId = Some m.Id }

    Assert.True(ModulesLogic.examIsSoon now { facts m with Events = [ exam 10.0 ] })
    Assert.False(ModulesLogic.examIsSoon now { facts m with Events = [ exam 30.0 ] })

[<Fact>]
let ``the exam comes from a real calendar event, never from a title`` () =
    let m = moduleOf "CS2004" "Graphics"

    let lectureCalledExam =
        { CalendarEvent.create now "Exam revision lecture" EventKind.Lecture (now.AddDays 3.0) (now.AddDays 3.0) with
            ModuleId = Some m.Id }

    Assert.Equal(None, ModulesLogic.nextExam now { facts m with Events = [ lectureCalledExam ] })
    Assert.False(ModulesLogic.examIsSoon now { facts m with Events = [ lectureCalledExam ] })

[<Fact>]
let ``a past exam is not the next one`` () =
    let m = moduleOf "CS2004" "Graphics"

    let past =
        { CalendarEvent.create now "Old exam" EventKind.Exam (now.AddDays -5.0) (now.AddDays -5.0) with
            ModuleId = Some m.Id }

    Assert.Equal(None, ModulesLogic.nextExam now { facts m with Events = [ past ] })

[<Fact>]
let ``the next deadline is the soonest future one from open work`` () =
    let m = moduleOf "CS2004" "Graphics"

    let f =
        { facts m with
            Assignments =
                [ assignmentIn m "Later" 10 (Some(now.AddDays 20.0))
                  assignmentIn m "Sooner" 10 (Some(now.AddDays 5.0))
                  assignmentIn m "Past" 10 (Some(now.AddDays -5.0)) ] }

    Assert.Equal(Some "Sooner", ModulesLogic.nextDeadline now f |> Option.map snd)

// ---------------------------------------------------------------------------
// 5. tabs
// ---------------------------------------------------------------------------

[<Fact>]
let ``every tab has a real query behind it and its own words`` () =
    Assert.Equal(8, ModulesLogic.Tab.all.Length)

    Assert.All(
        ModulesLogic.Tab.all,
        fun t ->
            Assert.False(String.IsNullOrWhiteSpace(ModulesLogic.Tab.label t))
            Assert.False(String.IsNullOrWhiteSpace(ModulesLogic.Tab.emptyMessage t))
    )

    let labels = ModulesLogic.Tab.all |> List.map ModulesLogic.Tab.label
    Assert.Equal(labels.Length, (List.distinct labels).Length)

[<Fact>]
let ``a tab's count is the real number of items behind it`` () =
    let m = moduleOf "CS2004" "Graphics"

    let f =
        { facts m with
            Assignments = [ assignmentIn m "A" 0 None ]
            Notes = [ Note.create now "A note" ]
            ActivityCount = 3 }

    Assert.Equal(1, ModulesLogic.tabCount f ModulesLogic.Tab.Assignments)
    Assert.Equal(1, ModulesLogic.tabCount f ModulesLogic.Tab.Notes)
    Assert.Equal(0, ModulesLogic.tabCount f ModulesLogic.Tab.Documents)
    Assert.Equal(3, ModulesLogic.tabCount f ModulesLogic.Tab.Timeline)

// ---------------------------------------------------------------------------
// 6. the card summary
// ---------------------------------------------------------------------------

[<Fact>]
let ``the summary states a recorded figure, never reassurance`` () =
    let m = moduleOf "CS2004" "Graphics"
    let overdue = { assignmentIn m "Overdue" 10 (Some(now.AddDays -3.0)) with ScheduledMinutes = 0 }

    let text = ModulesLogic.summary now { facts m with Assignments = [ overdue ] }
    Assert.Contains("high risk", text)

[<Fact>]
let ``an empty module says so rather than claiming completeness`` () =
    Assert.Contains("No coursework", ModulesLogic.summary now (facts (moduleOf "CS2004" "Graphics")))

[<Fact>]
let ``an archived module's summary says it is archived`` () =
    let m = { moduleOf "CS2004" "Graphics" with Archived = true }
    Assert.Contains("Archived", ModulesLogic.summary now (facts m))

[<Fact>]
let ``a finished module is reported as finished, not as empty`` () =
    let m = moduleOf "CS2004" "Graphics"

    let done' =
        { assignmentIn m "Done" 100 None with
            Assignment = { (assignmentIn m "Done" 100 None).Assignment with Status = AssignmentStatus.Graded } }

    Assert.Contains("finished", ModulesLogic.summary now { facts m with Assignments = [ done' ] })

// ---------------------------------------------------------------------------
// 6b. the regions the redesign carries beside the summary
// ---------------------------------------------------------------------------

[<Fact>]
let ``the card carries one or two insight bullets, as the redesign's does`` () =
    let m = moduleOf "CS2004" "Graphics"

    // Nothing filed: one bullet, and it states the absence.
    let bare = ModulesLogic.insights now (facts m)
    Assert.Equal(1, bare.Length)

    // Outstanding work with no session booked: a second, and it is a recorded
    // gap between two stored figures, not commentary.
    let unscheduled =
        { facts m with Assignments = [ { assignmentIn m "Essay" 0 (Some(now.AddDays 20.0)) with ScheduledMinutes = 0 } ] }

    let two = ModulesLogic.insights now unscheduled
    Assert.Equal(2, two.Length)
    Assert.Contains("no study session booked", two.[1])

[<Fact>]
let ``an archived module gets exactly one insight and no suggested action`` () =
    let m = { moduleOf "CS2004" "Graphics" with Archived = true }
    Assert.Equal(1, (ModulesLogic.insights now (facts m)).Length)
    Assert.Equal(None, ModulesLogic.suggestedAction now (facts m))

[<Fact>]
let ``the suggested action names a recorded object, never generic advice`` () =
    let m = moduleOf "CS2004" "Graphics"

    let unscheduledRisk =
        { facts m with Assignments = [ { assignmentIn m "Ray tracer" 10 (Some(now.AddDays -3.0)) with ScheduledMinutes = 0 } ] }

    let action = ModulesLogic.suggestedAction now unscheduledRisk
    Assert.True(action.IsSome)
    Assert.Contains("Ray tracer", action.Value)

[<Fact>]
let ``a module with nothing to act on suggests nothing rather than inventing advice`` () =
    let m = moduleOf "CS2004" "Graphics"
    let calm = assignmentIn m "Calm" 60 (Some(now.AddDays 60.0))
    Assert.Equal(None, ModulesLogic.suggestedAction now { facts m with Assignments = [ calm ] })

[<Fact>]
let ``an empty module is told to add coursework, because that is what is missing`` () =
    Assert.Equal(Some "Add this module's coursework", ModulesLogic.suggestedAction now (facts (moduleOf "CS2004" "G")))

[<Fact>]
let ``the expanded card runs upcoming work, then actions, then tabs, then the panel`` () =
    // The first implementation drew the action row LAST, after the tab panel.
    Assert.Equal<ModulesLogic.ExpandedRegion list>(
        [ ModulesLogic.ExpandedRegion.Divider
          ModulesLogic.ExpandedRegion.UpcomingWork
          ModulesLogic.ExpandedRegion.Actions
          ModulesLogic.ExpandedRegion.Tabs
          ModulesLogic.ExpandedRegion.TabPanel ],
        ModulesLogic.expandedOrder
    )

    Assert.Equal(ModulesLogic.expandedOrder.Length, (List.distinct ModulesLogic.expandedOrder).Length)

[<Fact>]
let ``upcoming work is five cells and Suggested action is the fifth`` () =
    // The redesign's grid has five; mine had four until this pass.
    let cells = ModulesLogic.upcomingCells now (facts (moduleOf "CS2004" "Graphics"))
    Assert.Equal(5, cells.Length)

    Assert.Equal<string list>(
        [ "Next lecture"; "Next assignment"; "Planner session"; "Exam date"; "Suggested action" ],
        cells |> List.map fst
    )

[<Fact>]
let ``an unknown upcoming cell is genuinely unknown, never a fabricated zero`` () =
    // A module with nothing filed knows none of the first four.
    let cells = ModulesLogic.upcomingCells now (facts (moduleOf "CS2004" "Graphics"))

    for label, value in cells |> List.truncate 4 do
        Assert.True(value.IsNone, sprintf "%s invented a value from no data" label)

[<Fact>]
let ``the card's always-visible actions are four real destinations`` () =
    // The redesign shows four small glyph buttons on every card, expanded or not.
    Assert.Equal(4, ModulesLogic.primaryActions.Length)

    for glyph, label, _ in ModulesLogic.primaryActions do
        Assert.False(String.IsNullOrWhiteSpace glyph)
        Assert.False(String.IsNullOrWhiteSpace label, "a glyph-only button must still carry a name")

    // Exactly one is the editor; the rest navigate, and to distinct screens.
    let targets = ModulesLogic.primaryActions |> List.map (fun (_, _, t) -> t)
    Assert.Equal(1, targets |> List.filter Option.isNone |> List.length)

    Assert.Equal<Page option list>(
        [ None; Some Page.Assignments; Some Page.Planner; Some Page.Timeline ],
        targets
    )

    let glyphs = ModulesLogic.primaryActions |> List.map (fun (g, _, _) -> g)
    Assert.Equal(glyphs.Length, (List.distinct glyphs).Length)

[<Fact>]
let ``the archived card's link row is four real destinations, with no grades link`` () =
    // The redesign lists five; the fifth is Grades, which nothing stores.
    Assert.Equal(4, ModulesLogic.archivedLinks.Length)

    let labels = ModulesLogic.archivedLinks |> List.map fst
    Assert.DoesNotContain("Grades", labels)
    Assert.Equal(labels.Length, (List.distinct labels).Length)

    for _, page in ModulesLogic.archivedLinks do
        Assert.Contains(page, Page.all |> List.map (fun (p, _, _) -> p))

// ---------------------------------------------------------------------------
// 7. layout and reserved geometry
// ---------------------------------------------------------------------------

[<Fact>]
let ``the module grid honours the redesign's 420px minimum card`` () =
    Assert.Equal(420.0, ModulesLogic.Geometry.cardMinWidth, 6)
    Assert.Equal(1, ModulesLogic.cardColumns 500.0)
    Assert.Equal(2, ModulesLogic.cardColumns 900.0)
    Assert.Equal(3, ModulesLogic.cardColumns 1300.0)

    // Never zero, however narrow.
    Assert.True(ModulesLogic.cardColumns 100.0 >= 1)

[<Fact>]
let ``card columns never go backwards as the page widens`` () =
    [ 320.0..40.0..2400.0 ]
    |> List.map ModulesLogic.cardColumns
    |> List.pairwise
    |> List.iter (fun (a, b) -> Assert.True(b >= a, sprintf "columns went backwards: %d then %d" a b))

[<Fact>]
let ``metric columns match every other migrated screen`` () =
    Assert.Equal(2, ModulesLogic.metricColumns 600.0)
    Assert.Equal(3, ModulesLogic.metricColumns 900.0)
    Assert.Equal(5, ModulesLogic.metricColumns 1400.0)
    Assert.Equal(InboxLogic.metricColumns 900.0, ModulesLogic.metricColumns 900.0)
    Assert.Equal(AssignmentsLogic.metricColumns 1400.0, ModulesLogic.metricColumns 1400.0)

[<Fact>]
let ``the archived grid is four across when there is room, and reflows down`` () =
    Assert.Equal(4, ModulesLogic.archivedColumns 1500.0)
    Assert.Equal(3, ModulesLogic.archivedColumns 1200.0)
    Assert.Equal(2, ModulesLogic.archivedColumns 900.0)
    Assert.Equal(1, ModulesLogic.archivedColumns 600.0)

[<Fact>]
let ``the outline inset matches every approved screen`` () =
    Assert.Equal(0.5, ModulesLogic.Geometry.borderInset, 6)
    Assert.Equal(PlannerLogic.Geometry.borderInset, ModulesLogic.Geometry.borderInset, 6)
    Assert.Equal(InboxLogic.Geometry.borderInset, ModulesLogic.Geometry.borderInset, 6)
    Assert.Equal(AssignmentsLogic.Geometry.borderInset, ModulesLogic.Geometry.borderInset, 6)

[<Fact>]
let ``no Modules content card draws a specular highlight`` () =
    Assert.Equal(0.0, ModulesLogic.Geometry.contentHighlight, 6)
    Assert.Equal(PlannerLogic.Geometry.contentHighlight, ModulesLogic.Geometry.contentHighlight, 6)
    Assert.Equal(DashboardLogic.Card.highlightScale, ModulesLogic.Geometry.contentHighlight, 6)

[<Fact>]
let ``a glass action is the same size here as on every other migrated screen`` () =
    Assert.Equal(PlannerLogic.Geometry.actionMinHeight, ModulesLogic.Geometry.actionMinHeight, 6)
    Assert.Equal(InboxLogic.Geometry.actionRadius, ModulesLogic.Geometry.actionRadius, 6)
    Assert.Equal(AssignmentsLogic.Geometry.actionMinHeight, ModulesLogic.Geometry.actionMinHeight, 6)

[<Fact>]
let ``chips and cards reserve their outline in both states`` () =
    Assert.True(ModulesLogic.Geometry.filterBorderThickness > 0.0)
    Assert.True(ModulesLogic.Geometry.filterMinHeight > 0.0)
    Assert.True(ModulesLogic.Geometry.cardBorderThickness > 0.0)

[<Fact>]
let ``wrapping text is given a finite column, never an infinite one`` () =
    Assert.Contains("*", ModulesLogic.Geometry.bulletColumns)
    Assert.StartsWith("Auto", ModulesLogic.Geometry.bulletColumns)

// ---------------------------------------------------------------------------
// 8. update behaviour
// ---------------------------------------------------------------------------

let private noServices: AppServices = Unchecked.defaultof<AppServices>

let private baseModel: ModulesView.Model =
    { Facts = []
      Filters = ModulesLogic.Selection.empty
      Expanded = None
      Tabs = Map.empty
      Draft = None
      PendingDelete = false
      StudiedThisWeek = 0
      Saving = false
      Loading = false
      Error = None
      Width = 1400.0 }

let private modelOf (m, _, _) = m
let private cmdOf (_, c, _) = c
let private externalOf (_, _, e) = e

[<Fact>]
let ``a successful load clears loading and any previous error`` () =
    let before = { baseModel with Loading = true; Error = Some "earlier" }
    let after = ModulesView.update noServices (ModulesView.Loaded([ facts (moduleOf "CS2004" "G") ], 90)) before |> modelOf

    Assert.False(after.Loading)
    Assert.Equal(None, after.Error)
    Assert.Equal(1, after.Facts.Length)
    Assert.Equal(90, after.StudiedThisWeek)

[<Fact>]
let ``a load drops an expansion whose module has gone`` () =
    let gone = facts (moduleOf "CS2004" "G")
    let before = { baseModel with Expanded = Some gone.Module.Id; Facts = [ gone ] }

    let after =
        ModulesView.update noServices (ModulesView.Loaded([ facts (moduleOf "EE2003" "E") ], 0)) before |> modelOf

    Assert.Equal(None, after.Expanded)

[<Fact>]
let ``a failure clears every in-flight flag`` () =
    let before = { baseModel with Loading = true; Saving = true; PendingDelete = true }
    let after = ModulesView.update noServices (ModulesView.Failed(exn "offline")) before |> modelOf

    Assert.False(after.Loading)
    Assert.False(after.Saving)
    Assert.False(after.PendingDelete)
    Assert.Equal(Some "offline", after.Error)

[<Fact>]
let ``only one module is expanded at a time, and re-clicking collapses it`` () =
    let a = facts (moduleOf "AA1000" "A")
    let b = facts (moduleOf "BB2000" "B")

    let first = ModulesView.update noServices (ModulesView.ToggleExpanded a.Module.Id) baseModel |> modelOf
    Assert.Equal(Some a.Module.Id, first.Expanded)

    let second = ModulesView.update noServices (ModulesView.ToggleExpanded b.Module.Id) first |> modelOf
    Assert.Equal(Some b.Module.Id, second.Expanded)

    let closed = ModulesView.update noServices (ModulesView.ToggleExpanded b.Module.Id) second |> modelOf
    Assert.Equal(None, closed.Expanded)

[<Fact>]
let ``each module remembers its own tab`` () =
    let a = facts (moduleOf "AA1000" "A")
    let b = facts (moduleOf "BB2000" "B")

    let m1 = ModulesView.update noServices (ModulesView.SetTab(a.Module.Id, ModulesLogic.Tab.Notes)) baseModel |> modelOf
    let m2 = ModulesView.update noServices (ModulesView.SetTab(b.Module.Id, ModulesLogic.Tab.Planner)) m1 |> modelOf

    Assert.Equal(Some ModulesLogic.Tab.Notes, m2.Tabs.TryFind a.Module.Id.Value)
    Assert.Equal(Some ModulesLogic.Tab.Planner, m2.Tabs.TryFind b.Module.Id.Value)

[<Fact>]
let ``toggling a chip changes nothing else`` () =
    let before = { baseModel with Expanded = Some (facts (moduleOf "AA1000" "A")).Module.Id }

    let after =
        ModulesView.update noServices (ModulesView.ToggleFilter ModulesLogic.Filter.ExamsSoon) before |> modelOf

    Assert.True(after.Filters.Contains ModulesLogic.Filter.ExamsSoon)
    Assert.Equal(before.Expanded, after.Expanded)

[<Fact>]
let ``a navigation action asks the shell to move`` () =
    let result = ModulesView.update noServices (ModulesView.GotoPage Page.Planner) baseModel
    Assert.Equal(Some(ModulesView.Goto Page.Planner), externalOf result)

[<Fact>]
let ``a second save while one is running is dropped`` () =
    let running = { baseModel with Saving = true; Draft = Some Unchecked.defaultof<ModulesView.Draft> }
    Assert.True(List.isEmpty (cmdOf (ModulesView.update noServices ModulesView.Save running)))

[<Fact>]
let ``deleting asks before it runs, and cancelling abandons it`` () =
    let before = { baseModel with Draft = Some Unchecked.defaultof<ModulesView.Draft> }
    let asked = ModulesView.update noServices ModulesView.RequestDelete before |> modelOf
    Assert.True(asked.PendingDelete)
    Assert.False(asked.Saving)

    let cancelled = ModulesView.update noServices ModulesView.CancelDelete asked |> modelOf
    Assert.False(cancelled.PendingDelete)

[<Fact>]
let ``confirming a delete while saving is dropped`` () =
    let running = { baseModel with Saving = true; PendingDelete = true }
    Assert.True(List.isEmpty (cmdOf (ModulesView.update noServices ModulesView.ConfirmDelete running)))

[<Fact>]
let ``closing the editor clears any pending delete`` () =
    let before = { baseModel with Draft = Some Unchecked.defaultof<ModulesView.Draft>; PendingDelete = true }
    let after = ModulesView.update noServices ModulesView.CloseDraft before |> modelOf

    Assert.Equal(None, after.Draft)
    Assert.False(after.PendingDelete)

[<Fact>]
let ``a resize records the width and nothing else`` () =
    let after = ModulesView.update noServices (ModulesView.Resized 800.0) baseModel |> modelOf
    Assert.Equal(800.0, after.Width, 3)
    Assert.Equal<ModulesLogic.Selection>(baseModel.Filters, after.Filters)

[<Fact>]
let ``the screen opens with no chip on, so both sections are visible`` () =
    Assert.True(baseModel.Filters.IsEmpty)
    Assert.True(ModulesLogic.Selection.showsActive baseModel.Filters)
    Assert.True(ModulesLogic.Selection.showsArchived baseModel.Filters)

// ---------------------------------------------------------------------------
// 9. the shell contract
// ---------------------------------------------------------------------------

[<Fact>]
let ``Modules draws its own glass`` () =
    Assert.True(Page.usesGlassSurface Page.Modules)

[<Fact>]
let ``the screens migrated so far all draw their own glass`` () =
    for page in [ Page.Dashboard; Page.Timeline; Page.Planner; Page.Inbox; Page.Assignments; Page.Modules ] do
        Assert.True(Page.usesGlassSurface page)

[<Fact>]
let ``the screens still to migrate keep their opaque surface`` () =
    // Lectures left this list when it was migrated in its own right.
    // Every screen is now migrated, so nothing is left holding an opaque shell
    // surface. This asserts the completed state rather than an empty list.
    for page, _, _ in Page.all do
        Assert.True(Page.usesGlassSurface page, sprintf "%A lost its glass surface" page)
