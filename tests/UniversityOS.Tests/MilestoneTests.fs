module UniversityOS.Tests.MilestoneTests

open System
open System.IO
open Xunit
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.Infrastructure
open UniversityOS.Infrastructure.Repositories
open UniversityOS.App

/// **The shared work item.**
///
/// The review asks four screens for the same thing in four different words:
/// Assignments wants a milestone list instead of a Status word, the Planner
/// wants to stop scheduling anonymous blocks that carry only the assignment
/// name, Modules wants "next actual milestone/task" on a collapsed card, and the
/// Calendar wants real task names. That is one model, and these tests pin the
/// behaviour all four will depend on.

let private now = DateTime(2026, 3, 4, 9, 30, 0)
let private assignment = AssignmentId(Guid "00000000-0000-0000-0000-000000000201")
let private other = AssignmentId(Guid "00000000-0000-0000-0000-000000000202")

let private milestone (n: int) (title: string) (state: MilestoneState) (order: int) (estimate: int option) =
    { Id = MilestoneId(Guid(sprintf "00000000-0000-0000-0000-%012d" n))
      AssignmentId = assignment
      Title = title
      State = state
      Order = order
      EstimatedMinutes = estimate
      CreatedAt = now.AddDays -30.0
      UpdatedAt = now }

let private sample =
    [ milestone 1 "Scope" MilestoneState.Graded 0 (Some 90)
      milestone 2 "Loaders" MilestoneState.Submitted 1 (Some 240)
      milestone 3 "Seeds" MilestoneState.InProgress 2 (Some 300)
      milestone 4 "Chapter" MilestoneState.NotStarted 3 (Some 600)
      milestone 5 "Proofread" MilestoneState.NotStarted 4 None ]

// ---------------------------------------------------------------------------
// States and bands
// ---------------------------------------------------------------------------

/// The review asks for three treatments — red, intermediate, green — over four
/// states. Submitted and Graded share the green one: the work left the user's
/// hands either way.
[<Fact>]
let ``every state maps to one of the three treatments, and both finished states share one`` () =
    Assert.Equal(MilestoneState.NotBegun, MilestoneState.band MilestoneState.NotStarted)
    Assert.Equal(MilestoneState.Underway, MilestoneState.band MilestoneState.InProgress)
    Assert.Equal(MilestoneState.Finished, MilestoneState.band MilestoneState.Submitted)
    Assert.Equal(MilestoneState.Finished, MilestoneState.band MilestoneState.Graded)

    Assert.Equal(3, MilestoneState.all |> List.map MilestoneState.band |> List.distinct |> List.length)

/// Colour is never the only signal, which is the rule the rest of the
/// application already follows.
[<Fact>]
let ``every state has a word of its own`` () =
    let labels = MilestoneState.all |> List.map MilestoneState.label
    Assert.Equal(4, labels |> List.distinct |> List.length)

    for label in labels do
        Assert.False(String.IsNullOrWhiteSpace label)

[<Fact>]
let ``state survives the round trip through storage`` () =
    for state in MilestoneState.all do
        Assert.Equal(state, MilestoneState.ofInt (MilestoneState.toInt state))

    // An unknown code from a future version reads as the safest state rather
    // than throwing on load.
    Assert.Equal(MilestoneState.NotStarted, MilestoneState.ofInt 99)

// ---------------------------------------------------------------------------
// Derived answers the four screens need
// ---------------------------------------------------------------------------

[<Fact>]
let ``completion counts submitted and graded, and nothing else`` () =
    Assert.Equal(2, Milestone.completedCount sample)
    Assert.Equal(3, (Milestone.outstanding sample).Length)

/// **`None`, not 0%.** An assignment with no milestones and one with eleven
/// untouched ones are different situations, and a bar reading 0% for the first
/// claims a plan exists that does not — the same rule `planPercent` follows.
[<Fact>]
let ``progress is unknown rather than zero when there are no milestones`` () =
    Assert.Equal(None, Milestone.progressPercent [])
    Assert.Equal(Some 0, Milestone.progressPercent [ milestone 9 "Only" MilestoneState.NotStarted 0 None ])
    Assert.Equal(Some 40, Milestone.progressPercent sample)

/// "Next actual milestone/task" — what Modules puts on a collapsed card and what
/// the Planner schedules.
[<Fact>]
let ``next is the first outstanding milestone in display order`` () =
    Assert.Equal(Some "Seeds", Milestone.next sample |> Option.map (fun m -> m.Title))

    // All done means there is nothing next, rather than the last one repeating.
    let finished = sample |> List.map (fun m -> { m with State = MilestoneState.Graded })
    Assert.Equal(None, Milestone.next finished)

/// A partial total presented as a complete one is a lie about the estimate, so
/// the count of unestimated steps comes back with the sum.
[<Fact>]
let ``the estimate reports how much of it is unknown`` () =
    let total, unknown = Milestone.estimateOf sample
    Assert.Equal(1230, total)
    Assert.Equal(1, unknown)

    let none, allUnknown = Milestone.estimateOf [ milestone 9 "x" MilestoneState.NotStarted 0 None ]
    Assert.Equal(0, none)
    Assert.Equal(1, allUnknown)

// ---------------------------------------------------------------------------
// Ordering
// ---------------------------------------------------------------------------

[<Fact>]
let ``ordering is by position and is stable for equal positions`` () =
    let shuffled = [ sample.[3]; sample.[0]; sample.[4]; sample.[2]; sample.[1] ]

    Assert.Equal<string list>(
        [ "Scope"; "Loaders"; "Seeds"; "Chapter"; "Proofread" ],
        Milestone.ordered shuffled |> List.map (fun m -> m.Title)
    )

    // Two rows sharing a position must still come back in a fixed order — a
    // list that reshuffles between renders is worse than an imperfect one.
    let clashing =
        [ milestone 7 "B" MilestoneState.NotStarted 0 None
          milestone 6 "A" MilestoneState.NotStarted 0 None ]

    Assert.Equal<string list>(
        Milestone.ordered clashing |> List.map (fun m -> m.Title),
        Milestone.ordered (List.rev clashing) |> List.map (fun m -> m.Title)
    )

/// After an insert, a delete or a move, positions must be contiguous from zero
/// or a later sort resolves the gaps arbitrarily.
[<Fact>]
let ``renumbering closes gaps and leaves untouched rows alone`` () =
    let gappy =
        [ milestone 1 "A" MilestoneState.NotStarted 0 None
          milestone 2 "B" MilestoneState.NotStarted 5 None
          milestone 3 "C" MilestoneState.NotStarted 9 None ]

    let renumbered = Milestone.renumber now gappy
    Assert.Equal<int list>([ 0; 1; 2 ], renumbered |> List.map (fun m -> m.Order))

    // The row that was already correct is returned unchanged, so its UpdatedAt
    // does not move for a change it did not experience.
    Assert.Equal(gappy.[0].UpdatedAt, renumbered.[0].UpdatedAt)

[<Fact>]
let ``moving a milestone reorders and renumbers`` () =
    let moved = Milestone.move now 4 0 sample

    Assert.Equal<string list>(
        [ "Proofread"; "Scope"; "Loaders"; "Seeds"; "Chapter" ],
        moved |> List.map (fun m -> m.Title)
    )

    Assert.Equal<int list>([ 0; 1; 2; 3; 4 ], moved |> List.map (fun m -> m.Order))

/// Out-of-range indices leave the list untouched rather than throwing — a drag
/// that ends outside the list is a normal gesture, not an error.
[<Fact>]
let ``an out-of-range move changes nothing`` () =
    let titles (items: Milestone list) = items |> List.map (fun m -> m.Title)

    Assert.Equal<string list>(titles (Milestone.ordered sample), titles (Milestone.move now -1 2 sample))
    Assert.Equal<string list>(titles (Milestone.ordered sample), titles (Milestone.move now 2 99 sample))
    Assert.Equal<string list>(titles (Milestone.ordered sample), titles (Milestone.move now 2 2 sample))

// ---------------------------------------------------------------------------
// Persistence, against a real database
// ---------------------------------------------------------------------------

/// A milestone has no meaning without its assignment, so the schema says so with
/// a foreign key and `ON DELETE CASCADE`. That makes the parent rows part of the
/// setup here — which is the right shape: a test that could save a milestone
/// against a missing assignment would be testing a table the application does
/// not have.
type private Store() =
    let root =
        Path.Combine(Path.GetTempPath(), "UniversityOS-Tests", Guid.NewGuid().ToString "N")

    do Directory.CreateDirectory root |> ignore
    let db = Database(Path.Combine(root, "universityos.db"))
    do Migrations.apply db |> Async.RunSynchronously

    let assignments = AssignmentRepository db :> IAssignmentRepository

    do
        for id in [ assignment; other ] do
            assignments.Save { Assignment.create now "Parent" with Id = id }
            |> Async.RunSynchronously

    member _.Repo = MilestoneRepository db :> IMilestoneRepository
    member _.Assignments = assignments
    member _.Root = root

    interface IDisposable with
        member _.Dispose() =
            try
                Directory.Delete(root, true)
            with _ ->
                ()

let private run x = Async.RunSynchronously x

[<Fact>]
let ``milestones round-trip through the database with every field intact`` () =
    use store = new Store()
    let repo = store.Repo

    for m in sample do
        repo.Save m |> run

    let loaded = repo.GetForAssignment assignment |> run

    Assert.Equal(sample.Length, loaded.Length)
    Assert.Equal<string list>(sample |> List.map (fun m -> m.Title), loaded |> List.map (fun m -> m.Title))
    Assert.Equal<MilestoneState list>(sample |> List.map (fun m -> m.State), loaded |> List.map (fun m -> m.State))

    // The honest-unknown estimate survives as `None`, not as zero.
    Assert.Equal(None, (loaded |> List.find (fun m -> m.Title = "Proofread")).EstimatedMinutes)
    Assert.Equal(Some 600, (loaded |> List.find (fun m -> m.Title = "Chapter")).EstimatedMinutes)

/// The repository sorts, so four callers do not each sort identically.
[<Fact>]
let ``the repository returns milestones in the user's order`` () =
    use store = new Store()
    let repo = store.Repo

    for m in List.rev sample do
        repo.Save m |> run

    Assert.Equal<int list>([ 0; 1; 2; 3; 4 ], repo.GetForAssignment assignment |> run |> List.map (fun m -> m.Order))

/// **`ReplaceFor` is one transaction.** Reordering rewrites every position at
/// once; applied as separate statements a reader lands on duplicates, and a
/// failure halfway leaves the assignment with some of its steps gone.
[<Fact>]
let ``replacing an assignment's milestones swaps the whole list atomically`` () =
    use store = new Store()
    let repo = store.Repo

    for m in sample do
        repo.Save m |> run

    let replacement =
        [ milestone 20 "One" MilestoneState.InProgress 0 (Some 30)
          milestone 21 "Two" MilestoneState.NotStarted 1 None ]

    repo.ReplaceFor(assignment, replacement) |> run

    let loaded = repo.GetForAssignment assignment |> run
    Assert.Equal<string list>([ "One"; "Two" ], loaded |> List.map (fun m -> m.Title))

[<Fact>]
let ``replacing one assignment leaves another assignment's milestones alone`` () =
    use store = new Store()
    let repo = store.Repo

    repo.Save(milestone 1 "Mine" MilestoneState.NotStarted 0 None) |> run

    repo.Save { milestone 2 "Theirs" MilestoneState.NotStarted 0 None with AssignmentId = other }
    |> run

    repo.ReplaceFor(assignment, []) |> run

    Assert.Empty(repo.GetForAssignment assignment |> run)
    Assert.Single(repo.GetForAssignment other |> run) |> ignore

[<Fact>]
let ``saving the same milestone twice updates rather than duplicating`` () =
    use store = new Store()
    let repo = store.Repo
    let m = milestone 1 "Scope" MilestoneState.NotStarted 0 None

    repo.Save m |> run
    repo.Save { m with State = MilestoneState.Graded; Title = "Scope agreed" } |> run

    let loaded = repo.GetForAssignment assignment |> run |> List.exactlyOne
    Assert.Equal(MilestoneState.Graded, loaded.State)
    Assert.Equal<string>("Scope agreed", loaded.Title)

[<Fact>]
let ``deleting removes one milestone and only that one`` () =
    use store = new Store()
    let repo = store.Repo

    for m in sample do
        repo.Save m |> run

    repo.Delete sample.[2].Id |> run

    let remaining = repo.GetForAssignment assignment |> run
    Assert.Equal(4, remaining.Length)
    Assert.DoesNotContain("Seeds", remaining |> List.map (fun m -> m.Title))
    Assert.Equal(None, repo.GetById sample.[2].Id |> run)

/// **Deleting an assignment takes its milestones with it.**
///
/// Orphaned steps would keep appearing on the Planner as work for something that
/// no longer exists — which is why the constraint is in the schema rather than
/// left to whichever screen happens to delete the assignment.
[<Fact>]
let ``deleting an assignment cascades to its milestones`` () =
    use store = new Store()

    for m in sample do
        store.Repo.Save m |> run

    Assert.NotEmpty(store.Repo.GetForAssignment assignment |> run)

    store.Assignments.Delete assignment |> run

    Assert.Empty(store.Repo.GetForAssignment assignment |> run)

// ---------------------------------------------------------------------------
// The fixture
// ---------------------------------------------------------------------------

/// The fixture has to put all three treatments on screen, or two of them go
/// uninspected on every review pass.
[<Fact>]
let ``the review fixture covers every milestone state`` () =
    for state in MilestoneState.all do
        Assert.Contains(ReviewMode.milestones, fun (m: Milestone) -> m.State = state)

    // And both the unestimated case and a finished list.
    Assert.Contains(ReviewMode.milestones, fun (m: Milestone) -> m.EstimatedMinutes.IsNone)

    let byAssignment = ReviewMode.milestones |> List.groupBy (fun m -> m.AssignmentId)

    Assert.Contains(byAssignment, fun (_, items) -> items |> List.forall (fun m -> MilestoneState.isComplete m.State))
    Assert.Contains(byAssignment, fun (_, items) -> items |> List.forall (fun m -> not (MilestoneState.isComplete m.State)))

/// Every milestone must hang off an assignment the fixture really seeds, or the
/// Planner schedules work for something that is not there.
[<Fact>]
let ``every fixture milestone belongs to a fixture assignment`` () =
    let assignmentIds = ReviewMode.assignments |> List.map (fun a -> a.Id) |> Set.ofList

    for m in ReviewMode.milestones do
        Assert.True(assignmentIds.Contains m.AssignmentId, sprintf "%s hangs off a missing assignment" m.Title)

/// Positions must be contiguous from zero within each assignment, or the order
/// the user sees is decided by a tiebreak rather than by them.
[<Fact>]
let ``fixture milestones are numbered contiguously within each assignment`` () =
    for _, items in ReviewMode.milestones |> List.groupBy (fun m -> m.AssignmentId) do
        Assert.Equal<int list>([ 0 .. items.Length - 1 ], items |> List.map (fun m -> m.Order) |> List.sort)

/// **A session remembers the step it advances, across a save and a reload.**
///
/// Without this the Planner and Calendar could name the milestone on screen and
/// lose it the moment the plan was reloaded, which is the same as never having
/// had it.
[<Fact>]
let ``a planned session round-trips its milestone link`` () =
    use store = new Store()
    let repo = store.Repo
    let sessions = SessionRepository(Database(Path.Combine(store.Root, "universityos.db"))) :> ISessionRepository

    let step = milestone 1 "Run the seeds" MilestoneState.InProgress 0 (Some 120)
    repo.Save step |> run

    let planned: PlannedSession =
        { Id = SessionId(Guid "00000000-0000-0000-0000-000000000501")
          AssignmentId = Some assignment
          MilestoneId = Some step.Id
          Title = step.Title
          ModuleCode = Some "COMP3931"
          StartsAt = now
          EndsAt = now.AddMinutes 50.0
          Reason = "Deadline in 3 days"
          Completed = false
          CreatedAt = now }

    sessions.ReplaceFrom(now.AddDays -1.0, [ planned ]) |> run

    let loaded = sessions.GetBetween(now.AddDays -1.0, now.AddDays 1.0) |> run |> List.exactlyOne

    Assert.Equal(Some step.Id, loaded.MilestoneId)
    Assert.Equal<string>("Run the seeds", loaded.Title)

/// Deleting a milestone must NOT delete study time already scheduled against
/// it. The reference goes away; the session stays, because the user's plan is
/// theirs and a schema constraint should not silently rewrite it.
[<Fact>]
let ``deleting a milestone leaves the sessions scheduled against it in place`` () =
    use store = new Store()
    let db = Database(Path.Combine(store.Root, "universityos.db"))
    let sessions = SessionRepository(db) :> ISessionRepository

    let step = milestone 1 "Run the seeds" MilestoneState.InProgress 0 (Some 120)
    store.Repo.Save step |> run

    sessions.ReplaceFrom(
        now.AddDays -1.0,
        [ { Id = SessionId(Guid "00000000-0000-0000-0000-000000000502")
            AssignmentId = Some assignment
            MilestoneId = Some step.Id
            Title = step.Title
            ModuleCode = None
            StartsAt = now
            EndsAt = now.AddMinutes 50.0
            Reason = "planned"
            Completed = false
            CreatedAt = now } ]
    )
    |> run

    store.Repo.Delete step.Id |> run

    Assert.Single(sessions.GetBetween(now.AddDays -1.0, now.AddDays 1.0) |> run) |> ignore

/// Every fixture session that names a step must name one the fixture seeds, or
/// the Planner shows a task that does not exist.
[<Fact>]
let ``every fixture session's milestone link resolves`` () =
    let ids = ReviewMode.milestones |> List.map (fun m -> m.Id) |> Set.ofList

    for s in ReviewMode.sessions do
        match s.MilestoneId with
        | Some id -> Assert.True(ids.Contains id, sprintf "session %s names a missing step" s.Title)
        | None -> ()

    // And most of them do name one, or the fixture would not show the
    // behaviour at all.
    Assert.True(ReviewMode.sessions |> List.filter (fun s -> s.MilestoneId.IsSome) |> List.length >= 8)
