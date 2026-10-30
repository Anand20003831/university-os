namespace UniversityOS.Domain

open System

[<RequireQualifiedAccess>]
type AssignmentStatus =
    | NotStarted
    | InProgress
    | Blocked
    | Submitted
    | Graded

[<RequireQualifiedAccess>]
module AssignmentStatus =
    let all =
        [ AssignmentStatus.NotStarted
          AssignmentStatus.InProgress
          AssignmentStatus.Blocked
          AssignmentStatus.Submitted
          AssignmentStatus.Graded ]

    let label =
        function
        | AssignmentStatus.NotStarted -> "Not started"
        | AssignmentStatus.InProgress -> "In progress"
        | AssignmentStatus.Blocked -> "Blocked"
        | AssignmentStatus.Submitted -> "Submitted"
        | AssignmentStatus.Graded -> "Graded"

    let toInt =
        function
        | AssignmentStatus.NotStarted -> 0
        | AssignmentStatus.InProgress -> 1
        | AssignmentStatus.Blocked -> 2
        | AssignmentStatus.Submitted -> 3
        | AssignmentStatus.Graded -> 4

    let ofInt =
        function
        | 1 -> AssignmentStatus.InProgress
        | 2 -> AssignmentStatus.Blocked
        | 3 -> AssignmentStatus.Submitted
        | 4 -> AssignmentStatus.Graded
        | _ -> AssignmentStatus.NotStarted

type ChecklistItem =
    { Id: ChecklistItemId
      Text: string
      Done: bool
      Position: int }

type Assignment =
    { Id: AssignmentId
      ModuleId: ModuleId option
      Title: string
      Description: string
      Deadline: DateTime option
      /// Percentage of the module grade this assignment is worth, 0–100.
      Weighting: float option
      Priority: Priority
      Status: AssignmentStatus
      EstimatedHours: float option
      ActualHours: float
      /// 0–100.
      Progress: int
      Checklist: ChecklistItem list
      SubmissionUrl: string option
      RepositoryUrl: string option
      CreatedAt: DateTime
      UpdatedAt: DateTime }

[<RequireQualifiedAccess>]
module Assignment =

    let create (now: DateTime) (title: string) =
        { Id = AssignmentId.New()
          ModuleId = None
          Title = title.Trim()
          Description = ""
          Deadline = None
          Weighting = None
          Priority = Priority.Medium
          Status = AssignmentStatus.NotStarted
          EstimatedHours = None
          ActualHours = 0.0
          Progress = 0
          Checklist = []
          SubmissionUrl = None
          RepositoryUrl = None
          CreatedAt = now
          UpdatedAt = now }

    let isOpen (a: Assignment) =
        match a.Status with
        | AssignmentStatus.Submitted
        | AssignmentStatus.Graded -> false
        | _ -> true

    /// Minutes of work believed to remain, derived from the estimate and progress.
    /// Falls back to a conservative default when no estimate exists so the planner
    /// still reserves time for the work.
    let remainingMinutes (a: Assignment) =
        let estimated = a.EstimatedHours |> Option.defaultValue 4.0
        let fraction = float (100 - max 0 (min 100 a.Progress)) / 100.0
        int (Math.Round(estimated * 60.0 * fraction))

    let checklistProgress (a: Assignment) =
        match a.Checklist with
        | [] -> None
        | items ->
            let total = List.length items
            let complete = items |> List.filter (fun i -> i.Done) |> List.length
            Some(complete, total)
