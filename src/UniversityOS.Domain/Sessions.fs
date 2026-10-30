namespace UniversityOS.Domain

open System

/// A planner-generated (or user-created) study session. Kept separate from calendar
/// events so a plan regeneration can replace future sessions without touching
/// lectures, meetings or imported events.
type PlannedSession =
    { Id: SessionId
      AssignmentId: AssignmentId option
      /// The milestone this session advances, when the assignment has been
      /// broken into steps. `None` for a plan made before milestones existed,
      /// or for an assignment nobody has broken down.
      MilestoneId: MilestoneId option
      Title: string
      ModuleCode: string option
      StartsAt: DateTime
      EndsAt: DateTime
      /// Why the planner placed this block here — always shown to the user.
      Reason: string
      Completed: bool
      CreatedAt: DateTime }

[<RequireQualifiedAccess>]
module PlannedSession =

    let durationMinutes (s: PlannedSession) = int (s.EndsAt - s.StartsAt).TotalMinutes
