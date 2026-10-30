namespace UniversityOS.Domain

open System

/// **The shared work item.**
///
/// The review asks four screens for the same thing in four different words:
/// Assignments wants a milestone list instead of a Status word, the Planner
/// wants to stop scheduling "many anonymous 30–45 minute blocks containing only
/// the assignment name", Modules wants "next actual milestone/task" on a
/// collapsed card, and the Calendar wants "actual task names rather than generic
/// assignment blocks".
///
/// That is one model, not four. Building it four times would give four
/// vocabularies for the same rows and no way for the Planner to schedule the
/// thing Assignments edits.
///
/// A milestone belongs to exactly one assignment. It is deliberately **not** an
/// `EntityKind`: the graph, search and the activity log speak in assignments,
/// and promoting a checklist row to a first-class entity would put every
/// sub-task of every assignment on the knowledge graph, which is noise rather
/// than structure.
[<RequireQualifiedAccess>]
type MilestoneState =
    | NotStarted
    | InProgress
    | Submitted
    | Graded

[<RequireQualifiedAccess>]
module MilestoneState =

    let all =
        [ MilestoneState.NotStarted
          MilestoneState.InProgress
          MilestoneState.Submitted
          MilestoneState.Graded ]

    let label =
        function
        | MilestoneState.NotStarted -> "Not started"
        | MilestoneState.InProgress -> "In progress"
        | MilestoneState.Submitted -> "Submitted"
        | MilestoneState.Graded -> "Graded"

    let toInt =
        function
        | MilestoneState.NotStarted -> 0
        | MilestoneState.InProgress -> 1
        | MilestoneState.Submitted -> 2
        | MilestoneState.Graded -> 3

    let ofInt =
        function
        | 1 -> MilestoneState.InProgress
        | 2 -> MilestoneState.Submitted
        | 3 -> MilestoneState.Graded
        | _ -> MilestoneState.NotStarted

    /// **The three visual treatments the review asks for**, as cases rather than
    /// colours: red for not started, an intermediate for in progress, green for
    /// submitted or graded.
    ///
    /// Returned as data so the view pairs each with a *word* — status is never
    /// communicated by colour alone, which is the rule the rest of this
    /// application already follows.
    type Band =
        | NotBegun
        | Underway
        | Finished

    let band =
        function
        | MilestoneState.NotStarted -> NotBegun
        | MilestoneState.InProgress -> Underway
        | MilestoneState.Submitted
        | MilestoneState.Graded -> Finished

    /// Whether a milestone still represents outstanding work. Submitted counts
    /// as done: the work left the user's hands even if no mark has come back.
    let isComplete (state: MilestoneState) = band state = Finished

/// One step toward finishing an assignment.
type Milestone =
    { Id: MilestoneId
      AssignmentId: AssignmentId
      Title: string
      State: MilestoneState
      /// Position within its assignment. Stored rather than derived from
      /// creation time, because reordering is a thing the user does and a
      /// created-at sort would silently undo it.
      Order: int
      /// Honest-unknown: `None` means nobody has estimated this, which is a
      /// different fact from "estimated at zero".
      EstimatedMinutes: int option
      CreatedAt: DateTime
      UpdatedAt: DateTime }

[<RequireQualifiedAccess>]
module Milestone =

    let create (now: DateTime) (assignmentId: AssignmentId) (title: string) (order: int) =
        { Id = MilestoneId.New()
          AssignmentId = assignmentId
          Title = title.Trim()
          State = MilestoneState.NotStarted
          Order = order
          EstimatedMinutes = None
          CreatedAt = now
          UpdatedAt = now }

    /// Display order: by `Order`, then by creation for anything that somehow
    /// shares one. Deterministic either way — a list that reshuffles between
    /// renders is worse than one in an imperfect order.
    let ordered (milestones: Milestone list) =
        milestones |> List.sortBy (fun m -> m.Order, m.CreatedAt, m.Id.Value)

    let outstanding (milestones: Milestone list) =
        milestones |> List.filter (fun m -> not (MilestoneState.isComplete m.State))

    /// "3 of 11 done" — the count the review says must not be the *only* thing
    /// shown, but which is still worth showing beside the list.
    let completedCount (milestones: Milestone list) =
        milestones |> List.filter (fun m -> MilestoneState.isComplete m.State) |> List.length

    /// Progress as a percentage, or `None` when there are no milestones.
    ///
    /// `None` rather than 0: an assignment with no milestones and one with
    /// eleven untouched ones are different situations, and a bar reading 0% for
    /// the first claims a plan exists that does not. The same rule
    /// `DashboardLogic.planPercent` already follows.
    let progressPercent (milestones: Milestone list) =
        match milestones with
        | [] -> None
        | items -> Some(completedCount items * 100 / items.Length)

    /// The next thing to do: the first outstanding milestone in display order.
    let next (milestones: Milestone list) =
        milestones |> ordered |> outstanding |> List.tryHead

    /// Total estimated minutes across milestones that carry an estimate.
    ///
    /// Returns the sum **and** how many had none, so a caller can say "4h across
    /// 3 of 5 steps" rather than presenting a partial total as a complete one.
    let estimateOf (milestones: Milestone list) =
        let known = milestones |> List.choose (fun m -> m.EstimatedMinutes)
        List.sum known, milestones.Length - known.Length

    /// Renumbers a list into contiguous positions from zero, preserving the
    /// given order. Used after an insert, a delete or a move, so `Order` never
    /// develops gaps or duplicates that a later sort would resolve arbitrarily.
    let renumber (now: DateTime) (milestones: Milestone list) =
        milestones
        |> List.mapi (fun index m ->
            if m.Order = index then
                m
            else
                { m with Order = index; UpdatedAt = now })

    /// Moves the milestone at `from` to `target`, renumbering the result.
    /// Out-of-range indices leave the list untouched rather than throwing.
    let move (now: DateTime) (from: int) (target: int) (milestones: Milestone list) =
        let items = ordered milestones

        if from < 0 || from >= items.Length || target < 0 || target >= items.Length || from = target then
            items
        else
            let moved = items.[from]
            let without = items |> List.mapi (fun i m -> i, m) |> List.filter (fun (i, _) -> i <> from) |> List.map snd

            let before, after = List.splitAt target without
            renumber now (before @ [ moved ] @ after)
