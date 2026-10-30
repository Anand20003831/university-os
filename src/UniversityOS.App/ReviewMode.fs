namespace UniversityOS.App

open System
open System.IO
open System.Text
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.Infrastructure

/// **Development-only visual review mode.**
///
/// Manual review of the migrated screens is slow because the reviewer's own
/// database decides what is on them: an empty install shows empty states, and a
/// real install shows whatever happens to be true that week. Neither exercises
/// the layouts that need looking at.
///
/// Review mode fixes both. It runs the *real* application — the same views, the
/// same shell, the same renderer — against an isolated database seeded with a
/// fixed, representative dataset, and a clock frozen at a known instant.
///
/// **What it never touches.** Review mode redirects the entire data root, so the
/// real `%APPDATA%\UniversityOS` folder is not opened, not migrated and not
/// written. That covers the database, the DPAPI secret store and imported files
/// alike. It also never seeds a database that already has content, so a review
/// session's own edits survive a restart and a mistargeted root cannot silently
/// overwrite anything.
///
/// **What it never does.** No network call, no AI request, no secret. The
/// Connections and Assistant fixtures are stored state only, and both say so in
/// their own visible text — see `connections` and `conversation` below.
///
/// **How to start it** — either of:
///
/// ```
/// UniversityOS.exe --review
/// $env:UNIVERSITYOS_REVIEW = "1"; UniversityOS.exe
/// ```
///
/// The window title becomes `University OS — REVIEW MODE (fixture data)` so a
/// screenshot from review mode can never be mistaken for one from real use.
module ReviewMode =

    /// Both switches are honoured. The environment variable matches the house
    /// style already used by `UNIVERSITYOS_RENDER_LOG` and
    /// `UNIVERSITYOS_ACCEPTANCE`; the flag is read from the process command line
    /// directly so no argv has to be threaded through Avalonia's startup.
    let isActive () =
        let byFlag =
            Environment.GetCommandLineArgs()
            |> Array.exists (fun a -> String.Equals(a, "--review", StringComparison.OrdinalIgnoreCase))

        let byEnv =
            match Environment.GetEnvironmentVariable "UNIVERSITYOS_REVIEW" with
            | null -> false
            | v -> v.Trim() = "1"

        byFlag || byEnv

    /// The isolated data root. Deliberately under TEMP rather than beside the
    /// real store: somebody clearing temporary files should lose a review
    /// fixture, never their own work.
    let root () =
        Path.Combine(Path.GetTempPath(), "UniversityOS-Review")

    /// **The fixture version.**
    ///
    /// Bumped whenever the fixture set changes. The store records the version it
    /// was built from, and a build expecting a newer one refuses to write rather
    /// than merging new rows into an old fixture — see `seed`.
    ///
    /// `2026.03-2` added the ten kinds the first fixture never seeded: activity,
    /// sessions, documents (with real files), lectures, revision, links, inbox,
    /// memory, connections and one Assistant conversation. Before it, Timeline,
    /// Planner, Inbox, Lectures, Documents and Assistant were all empty on every
    /// review pass, which is why several reported issues could not be seen.
    ///
    /// `2026.03-3` added milestones — the shared work item Assignments, Planner,
    /// Modules and Calendar all read.
    [<Literal>]
    let fixtureVersion = "2026.03-3"

    /// The settings-table key holding the version above.
    [<Literal>]
    let fixtureVersionKey = "review-fixture-version"

    /// **The frozen instant: Wednesday 4 March 2026, 09:30 local.**
    ///
    /// Mid-week on purpose. A week view anchored here shows two days behind and
    /// two ahead, so "today" highlighting, past-vs-future styling and the
    /// week-boundary arithmetic are all visible at once — which a Monday or a
    /// Sunday would each hide half of.
    let fixedNow = DateTime(2026, 3, 4, 9, 30, 0, DateTimeKind.Local)

    /// Frozen so that two runs a week apart produce identical screens. A
    /// review that drifts is a review you cannot compare against yesterday's.
    type FixedClock(now: DateTime) =
        interface IClock with
            member _.Now = now

    /// The one label every fixture row that could be mistaken for real activity
    /// carries. Used by the Connections and Assistant fixtures, and asserted by
    /// the tests, so the wording cannot drift apart from the promise.
    [<Literal>]
    let fixtureLabel = "Review fixture"

    // ------------------------------------------------------------------
    // Fixture data
    // ------------------------------------------------------------------
    //
    // Ids are fixed Guids, not `New()`. Determinism is the whole point: the
    // same module keeps the same colour, the same ordering and the same
    // position in every list, on every run, so two review passes are
    // genuinely comparable. Every date is derived from `fixedNow`; nothing
    // here reads the wall clock, and nothing is random.
    //
    // Ids are banded by kind so a Guid seen on screen says what it is:
    //   1xx modules   2xx assignments  3xx events    4xx notes
    //   5xx sessions  6xx documents    7xx lectures  8xx revision
    //   9xx activity  10xx links       11xx inbox    12xx memory
    //   13xx chat

    let private guid (n: int) =
        Guid(sprintf "00000000-0000-0000-0000-%012d" n)

    let private entity (kind: EntityKind) (n: int) = { Kind = kind; Id = guid n }

    /// Day/hour offsets from the frozen instant, so every fixture date moves
    /// together if the frozen instant ever changes.
    let private at (dayOffset: float) (hour: float) =
        fixedNow.Date.AddDays(dayOffset).AddHours hour

    let modules =
        // Six modules: enough to exercise the module colour palette, the
        // Modules grid at more than one row, and an archived module — which
        // has its own five-link row that an all-active fixture would never show.
        [ ModuleId(guid 101), "COMP3811", "Computer Graphics", "Dr H. Nakamura", 20, 0, false
          ModuleId(guid 102), "COMP3611", "Machine Learning", "Prof. A. Osei", 20, 1, false
          ModuleId(guid 103), "COMP3931", "Individual Project", "Dr M. Lindqvist", 40, 2, false
          ModuleId(guid 104), "COMP3221", "Parallel Computation", "Dr S. Vasquez", 10, 3, false
          ModuleId(guid 105), "MATH2750", "Stochastic Processes", "Dr R. Adeyemi", 10, 4, false
          ModuleId(guid 106), "COMP2211", "Operating Systems", "Prof. J. Whitfield", 20, 5, true ]
        |> List.map (fun (id, code, name, lecturer, credits, colour, archived) ->
            { StudyModule.create fixedNow code name colour with
                Id = id
                Lecturer = Some lecturer
                Credits = Some credits
                Term = Some "Semester 2"
                Archived = archived })

    let assignments =
        // Deadlines are *relative to the frozen instant*, so the urgency bands,
        // the overdue state and the High Risk label all light up predictably.
        // Covers every AssignmentStatus and every Priority at least once, plus
        // the honest-unknown cases: no deadline, no weighting, no estimate.
        [ guid 201, Some(ModuleId(guid 103)), "Final project report", 4.0, 60.0, Priority.Critical, AssignmentStatus.InProgress, 40, Some 45.0
          guid 202, Some(ModuleId(guid 101)), "Ray tracer coursework 2", 1.5, 35.0, Priority.High, AssignmentStatus.InProgress, 70, Some 20.0
          guid 203, Some(ModuleId(guid 102)), "Classification report", -2.0, 25.0, Priority.Critical, AssignmentStatus.Blocked, 15, Some 12.0
          guid 204, Some(ModuleId(guid 104)), "MPI benchmark writeup", 11.0, 30.0, Priority.Medium, AssignmentStatus.NotStarted, 0, Some 10.0
          guid 205, Some(ModuleId(guid 105)), "Problem sheet 4", 6.0, 10.0, Priority.Low, AssignmentStatus.NotStarted, 0, None
          guid 206, Some(ModuleId(guid 101)), "Shading lab exercises", -9.0, 15.0, Priority.Medium, AssignmentStatus.Submitted, 100, Some 8.0
          guid 207, Some(ModuleId(guid 102)), "Coursework 1 — regression", -21.0, 25.0, Priority.High, AssignmentStatus.Graded, 100, Some 14.0
          guid 208, None, "Read the ethics handbook", 25.0, 0.0, Priority.Low, AssignmentStatus.NotStarted, 0, None ]
        |> List.map (fun (id, moduleId, title, daysOut, weight, priority, status, progress, estimate) ->
            { Assignment.create fixedNow title with
                Id = AssignmentId id
                ModuleId = moduleId
                Deadline = Some(fixedNow.Date.AddDays(daysOut).AddHours 17.0)
                Weighting = (if weight > 0.0 then Some weight else None)
                Priority = priority
                Status = status
                Progress = progress
                EstimatedHours = estimate
                ActualHours = (if progress > 0 then float progress / 10.0 else 0.0) })

    let events =
        // A realistic teaching week: repeated lectures, a lab, a supervision
        // meeting, an exam and a personal entry. Deliberately includes two
        // overlapping entries on the Thursday, because overlap is the case a
        // day view is most likely to lay out badly.
        [ guid 301, "Computer Graphics lecture", EventKind.Lecture, at -2.0 9.0, at -2.0 10.0, Some(ModuleId(guid 101)), Some "Roger Stevens LT 8"
          guid 302, "Machine Learning lecture", EventKind.Lecture, at -1.0 11.0, at -1.0 12.0, Some(ModuleId(guid 102)), Some "Chemistry LT C"
          guid 303, "Graphics lab", EventKind.Other, at 0.0 14.0, at 0.0 16.0, Some(ModuleId(guid 101)), Some "Bragg Cluster"
          guid 304, "Project supervision", EventKind.Meeting, at 1.0 10.0, at 1.0 10.5, Some(ModuleId(guid 103)), Some "EC Stoner 9.34"
          guid 305, "Parallel Computation lecture", EventKind.Lecture, at 1.0 10.0, at 1.0 11.0, Some(ModuleId(guid 104)), Some "Roger Stevens LT 25"
          guid 306, "Stochastic Processes tutorial", EventKind.Other, at 2.0 13.0, at 2.0 14.0, Some(ModuleId(guid 105)), Some "MALL 1"
          guid 307, "Machine Learning exam", EventKind.Exam, at 16.0 9.0, at 16.0 12.0, Some(ModuleId(guid 102)), Some "Sports Hall"
          guid 308, "Dentist", EventKind.Personal, at 3.0 16.0, at 3.0 16.75, None, None
          guid 309, "Computer Graphics lecture", EventKind.Lecture, at 5.0 9.0, at 5.0 10.0, Some(ModuleId(guid 101)), Some "Roger Stevens LT 8" ]
        |> List.map (fun (id, title, kind, startsAt, endsAt, moduleId, location) ->
            { CalendarEvent.create fixedNow title kind startsAt endsAt with
                Id = EventId id
                ModuleId = moduleId
                Location = location })

    let notes =
        // Markdown that exercises the renderer: headings, lists, emphasis,
        // inline code and a fenced block. A one-line note would prove nothing.
        [ guid 401,
          Some(ModuleId(guid 101)),
          "Ray-triangle intersection",
          true,
          [ "graphics"; "revision" ],
          "# Möller–Trumbore\n\nThe fast path **avoids computing the plane equation** entirely.\n\n- Compute the edge vectors from vertex 0\n- Take the determinant; near zero means the ray is *parallel*\n- Barycentric `u` and `v` fall out of the same cross products\n\n```\nif abs(det) < EPSILON: return MISS\n```\n\nWorth re-deriving before the exam."
          guid 402,
          Some(ModuleId(guid 102)),
          "Bias and variance",
          false,
          [ "ml" ],
          "## The decomposition\n\nExpected error splits into **bias²**, **variance** and irreducible noise.\n\n1. High bias shows up as underfitting on *both* train and test\n2. High variance shows up as a wide train/test gap\n\nRegularisation trades the second for the first."
          guid 403,
          Some(ModuleId(guid 103)),
          "Project — supervisor meeting 4",
          true,
          [ "project" ],
          "Agreed scope for the evaluation chapter.\n\n- Two datasets, not four\n- Report variance across five seeds\n- Draft due the week after reading week"
          guid 404, None, "Reading list", false, [], "Things to get from the library.\n\n- Pharr, *Physically Based Rendering*\n- Murphy, *Probabilistic Machine Learning*" ]
        |> List.map (fun (id, moduleId, title, pinned, tags, content) ->
            { Note.create fixedNow title with
                Id = NoteId id
                ModuleId = moduleId
                Pinned = pinned
                Tags = tags
                Content = content })

    /// **Milestones — the shared work item, on real assignments.**
    ///
    /// Every state appears, because the review asks for three distinct
    /// treatments (not started red, in progress intermediate, submitted or
    /// graded green) and a fixture that only ever showed one would leave two of
    /// them uninspected. Estimates are present on most and absent on some: an
    /// unestimated step is a real situation and has its own rendering.
    ///
    /// Guid band 14xx.
    let milestones =
        [ // Final project report — a long assignment, part-done.
          guid 1401, guid 201, "Agree the evaluation scope", MilestoneState.Graded, Some 90
          guid 1402, guid 201, "Build the two dataset loaders", MilestoneState.Submitted, Some 240
          guid 1403, guid 201, "Run five seeds per configuration", MilestoneState.InProgress, Some 300
          guid 1404, guid 201, "Write the evaluation chapter", MilestoneState.NotStarted, Some 600
          guid 1405, guid 201, "Proofread and submit", MilestoneState.NotStarted, None

          // Ray tracer coursework 2 — nearly finished.
          guid 1406, guid 202, "Implement the BVH", MilestoneState.Submitted, Some 300
          guid 1407, guid 202, "Add soft shadows", MilestoneState.InProgress, Some 180
          guid 1408, guid 202, "Benchmark against the reference", MilestoneState.NotStarted, Some 120

          // Classification report — blocked and overdue, nothing started.
          guid 1409, guid 203, "Clean the training set", MilestoneState.NotStarted, Some 120
          guid 1410, guid 203, "Fit the baseline model", MilestoneState.NotStarted, Some 90
          guid 1411, guid 203, "Write up the confusion analysis", MilestoneState.NotStarted, None

          // MPI benchmark writeup — not begun.
          guid 1412, guid 204, "Collect timings on 1-16 ranks", MilestoneState.NotStarted, Some 180
          guid 1413, guid 204, "Plot strong and weak scaling", MilestoneState.NotStarted, Some 120

          // Coursework 1 — finished, so the all-green list is on screen too.
          guid 1414, guid 207, "Fit the regression", MilestoneState.Graded, Some 120
          guid 1415, guid 207, "Write the discussion", MilestoneState.Graded, Some 180 ]
        |> List.groupBy (fun (_, assignmentId, _, _, _) -> assignmentId)
        |> List.collect (fun (_, items) ->
            items
            |> List.mapi (fun index (id, assignmentId, title, state, estimate) ->
                { Id = MilestoneId id
                  AssignmentId = AssignmentId assignmentId
                  Title = title
                  State = state
                  Order = index
                  EstimatedMinutes = estimate
                  CreatedAt = fixedNow.AddDays -30.0
                  UpdatedAt = fixedNow.AddDays -1.0 }))

    /// Planned study sessions: completed, missed, in progress today and future.
    ///
    /// **Every state the Planner draws differently is present**, because a plan
    /// of nothing but future blocks leaves the completed tick, the missed
    /// styling and the "studied this week" total all uninspected. 501/504/506
    /// are completed, 505 is in the past and NOT completed — the missed case.
    let sessions =
        [ guid 501, Some(guid 203), Some(guid 1409), "Clean the training set", Some "COMP3611", at 0.0 8.0, at 0.0 8.834, "Already past its deadline · Worth 25% of the module", true
          guid 502, Some(guid 201), Some(guid 1403), "Run five seeds per configuration", Some "COMP3931", at 0.0 10.0, at 0.0 10.834, "Deadline in 4 days · Worth 60% of the module", false
          guid 503, Some(guid 202), Some(guid 1407), "Add soft shadows", Some "COMP3811", at 0.0 16.5, at 0.0 17.334, "Due in 31 hours · Worth 35% of the module", false
          guid 504, Some(guid 201), Some(guid 1403), "Run five seeds per configuration", Some "COMP3931", at -1.0 9.0, at -1.0 9.834, "Deadline in 5 days · Worth 60% of the module", true
          guid 505, Some(guid 203), Some(guid 1409), "Clean the training set", Some "COMP3611", at -1.0 15.0, at -1.0 15.834, "Already past its deadline · Worth 25% of the module", false
          guid 506, Some(guid 202), Some(guid 1406), "Implement the BVH", Some "COMP3811", at -2.0 10.5, at -2.0 11.334, "Deadline in 3 days · Worth 35% of the module", true
          guid 507, Some(guid 201), Some(guid 1404), "Write the evaluation chapter", Some "COMP3931", at 1.0 13.0, at 1.0 13.834, "Deadline in 3 days · Worth 60% of the module", false
          guid 508, Some(guid 204), Some(guid 1412), "Collect timings on 1-16 ranks", Some "COMP3221", at 1.0 15.0, at 1.0 15.834, "Deadline in 10 days · Worth 30% of the module", false
          guid 509, Some(guid 201), Some(guid 1404), "Write the evaluation chapter", Some "COMP3931", at 2.0 9.0, at 2.0 9.834, "Deadline in 2 days · Worth 60% of the module", false
          guid 510, Some(guid 205), None, "Problem sheet 4", Some "MATH2750", at 3.0 11.0, at 3.0 11.834, "Deadline in 3 days · Worth 10% of the module", false
          guid 511, Some(guid 201), Some(guid 1405), "Proofread and submit", Some "COMP3931", at 5.0 14.0, at 5.0 14.834, "Already past its deadline · Worth 60% of the module", false
          guid 512, Some(guid 204), Some(guid 1413), "Plot strong and weak scaling", Some "COMP3221", at 6.0 10.0, at 6.0 10.834, "Deadline in 5 days · Worth 30% of the module", false ]
        |> List.map (fun (id, assignmentId, milestoneId, title, moduleCode, startsAt, endsAt, reason, completed) ->
            { Id = SessionId id
              AssignmentId = assignmentId |> Option.map AssignmentId
              MilestoneId = milestoneId |> Option.map MilestoneId
              Title = title
              ModuleCode = moduleCode
              StartsAt = startsAt
              EndsAt = endsAt
              Reason = reason
              Completed = completed
              CreatedAt = fixedNow })

    // ------------------------------------------------------------------
    // Document fixtures — metadata AND the real files behind it
    // ------------------------------------------------------------------
    //
    // A Document row whose `StoredPath` points at nothing renders as "missing"
    // and cannot be opened, so seeding metadata alone would put a broken state
    // on screen and call it a fixture. These write real bytes, under the review
    // root and nowhere else.

    /// A minimal but genuinely valid single-page PDF.
    ///
    /// Assembled here rather than checked in as a byte blob because the
    /// cross-reference table holds byte offsets into the file itself — a
    /// hand-written one is wrong the moment a character changes, and a viewer
    /// that refuses to open it would make the Documents fixture worthless. The
    /// output is a pure function of its inputs, so it stays deterministic.
    let private minimalPdf (heading: string) (body: string) : byte[] =
        let escape (s: string) =
            s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")

        let content =
            sprintf
                "BT /F1 16 Tf 72 760 Td (%s) Tj ET\nBT /F1 11 Tf 72 736 Td (%s) Tj ET\n"
                (escape heading)
                (escape body)

        let objects =
            [ "<< /Type /Catalog /Pages 2 0 R >>"
              "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"
              "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"
              "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
              sprintf "<< /Length %d >>\nstream\n%s\nendstream" content.Length content ]

        let sb = StringBuilder()
        sb.Append "%PDF-1.4\n" |> ignore
        let offsets = ResizeArray<int>()

        objects
        |> List.iteri (fun i body ->
            offsets.Add sb.Length
            sb.Append(sprintf "%d 0 obj\n%s\nendobj\n" (i + 1) body) |> ignore)

        let startXref = sb.Length
        sb.Append(sprintf "xref\n0 %d\n" (objects.Length + 1)) |> ignore
        sb.Append "0000000000 65535 f \n" |> ignore

        for o in offsets do
            sb.Append(sprintf "%010d 00000 n \n" o) |> ignore

        sb.Append(
            sprintf "trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" (objects.Length + 1) startXref
        )
        |> ignore

        // ASCII throughout, so `sb.Length` above is a byte count and the xref
        // offsets are correct. Nothing in this function may use non-ASCII text.
        Encoding.ASCII.GetBytes(sb.ToString())

    /// Where fixture files live: one folder under the review store's own file
    /// root, never anywhere else. Taking the data root as a parameter is what
    /// lets the tests prove nothing escapes it.
    let fixtureFilesRoot (dataRoot: string) =
        Path.Combine(dataRoot, "files", "review-fixtures")

    /// File name, bytes, and the metadata that must agree with them.
    ///
    /// Processing states differ on purpose: 601 is fully processed, 602 has text
    /// but no summary, 605 has both, and 604 has neither — the state a file has
    /// the moment it lands. A fixture where everything is processed leaves the
    /// unprocessed rendering uninspected.
    let private documentFixtures =
        [ guid 601,
          Some(ModuleId(guid 101)),
          "Ray tracing — lecture notes",
          "ray-tracing-notes.md",
          DocumentKind.Markdown,
          Some "Ray tracing lecture notes. Primary rays, shadow rays, and the recursion depth cutoff. Moller-Trumbore for triangle intersection.",
          Some "Covers primary and shadow rays and the recursion cutoff.",
          -14.0,
          Encoding.UTF8.GetBytes(
              "# Ray tracing — lecture notes\n\n_University OS review fixture. Generated by Review Mode, not imported from a real file._\n\n## Primary rays\n\nOne ray per pixel through the image plane.\n\n## Shadow rays\n\nCast toward each light; anything hit before the light means the point is occluded.\n\n## Recursion\n\nStop at a fixed depth — usually 4 or 5 — or when the contribution falls below a threshold.\n"
          )

          guid 602,
          Some(ModuleId(guid 101)),
          "Ray tracing — slides",
          "ray-tracing-slides.txt",
          DocumentKind.Slides,
          Some "Slide 1 Ray tracing. Slide 2 The rendering equation. Slide 3 Acceleration structures. Slide 4 BVH construction.",
          None,
          -14.0,
          Encoding.UTF8.GetBytes(
              "University OS review fixture — slide text extract\n(Generated by Review Mode. Not a real lecture deck.)\n\nSlide 1 — Ray tracing\nSlide 2 — The rendering equation\nSlide 3 — Acceleration structures\nSlide 4 — BVH construction\nSlide 5 — Summary and reading\n"
          )

          guid 603,
          Some(ModuleId(guid 103)),
          "Project handbook 2026",
          "project-handbook.pdf",
          DocumentKind.Pdf,
          Some "Individual project handbook. Submission deadlines, the evaluation chapter requirements, and the marking scheme.",
          Some "Deadlines, evaluation requirements and the marking scheme.",
          -21.0,
          minimalPdf "University OS - review fixture" "Individual project handbook 2026. Generated by Review Mode; not a real document."

          guid 604,
          None,
          "Reading list export",
          "reading-list.txt",
          DocumentKind.Text,
          None,
          None,
          -3.0,
          Encoding.UTF8.GetBytes(
              "University OS review fixture — reading list export\n\nPharr, Jakob, Humphreys — Physically Based Rendering\nMurphy — Probabilistic Machine Learning\nPacheco — An Introduction to Parallel Programming\n"
          )

          guid 605,
          Some(ModuleId(guid 102)),
          "Model selection — slides",
          "model-selection-slides.txt",
          DocumentKind.Slides,
          Some "Slide 1 Model selection. Slide 2 Cross validation. Slide 3 The bias variance tradeoff. Slide 4 Regularisation.",
          Some "Cross-validation, the bias/variance trade-off and regularisation.",
          -8.0,
          Encoding.UTF8.GetBytes(
              "University OS review fixture — slide text extract\n(Generated by Review Mode. Not a real lecture deck.)\n\nSlide 1 — Model selection\nSlide 2 — Cross-validation\nSlide 3 — The bias/variance trade-off\nSlide 4 — Regularisation\n"
          ) ]

    /// The document rows, with `StoredPath` and `SizeBytes` derived from the
    /// files that `writeFixtureFiles` puts on disk — so the two can never
    /// disagree about where a file is or how big it is.
    let documentsIn (dataRoot: string) : Document list =
        let folder = fixtureFilesRoot dataRoot

        documentFixtures
        |> List.map (fun (id, moduleId, title, fileName, kind, extracted, summary, daysAgo, bytes) ->
            { Id = DocumentId id
              ModuleId = moduleId
              Title = title
              FileName = fileName
              StoredPath = Path.Combine(folder, fileName)
              Kind = kind
              SizeBytes = int64 bytes.Length
              ExtractedText = extracted
              Summary = summary
              ImportedAt = at daysAgo 10.0 })

    /// Writes the fixture files. Deterministic content, so re-running produces
    /// byte-identical files, and confined to `fixtureFilesRoot`.
    let writeFixtureFiles (dataRoot: string) =
        let folder = fixtureFilesRoot dataRoot
        Directory.CreateDirectory folder |> ignore

        for (_, _, _, fileName, _, _, _, _, bytes) in documentFixtures do
            File.WriteAllBytes(Path.Combine(folder, fileName), bytes)

    /// Lectures, including the bare state (703) that has no slides, no
    /// transcript and no summary — the case the detail pane must still render.
    let lectures =
        [ guid 701,
          Some(ModuleId(guid 101)),
          "Lecture 12 — Ray tracing",
          Some(at -2.0 9.0),
          Some(DocumentId(guid 602)),
          Some
              "Today we are going to build the whole path from a pixel to a colour. Start with the primary ray, then shadow rays, then bounce. The important thing is the recursion cutoff — without it you will sit there forever on a mirrored scene.",
          Some "Primary rays, shadow rays, bounce, and why the recursion cutoff matters."
          guid 702,
          Some(ModuleId(guid 102)),
          "Lecture 9 — Model selection",
          Some(at -1.0 11.0),
          Some(DocumentId(guid 605)),
          Some
              "Cross-validation is the tool, but the thing to understand is what it is estimating. Every split gives you a slightly different model; the spread across splits is telling you about variance.",
          None
          guid 703, Some(ModuleId(guid 104)), "Lecture 4 — MPI collectives", Some(at -6.0 10.0), None, None, None ]
        |> List.map (fun (id, moduleId, title, date, slides, transcript, summary) ->
            { Lecture.create fixedNow title with
                Id = LectureId id
                ModuleId = moduleId
                Date = date
                SlidesDocumentId = slides
                Transcript = transcript
                Summary = summary })

    /// Flashcards, with sources spanning notes, lectures and documents so the
    /// "generated from" attribution has more than one shape to draw.
    let revision =
        [ guid 801,
          Some(ModuleId(guid 101)),
          "What does a near-zero determinant mean in Möller–Trumbore?",
          "The ray is parallel to the triangle's plane, so there is no intersection.",
          Some(entity EntityKind.Note 401)
          guid 802,
          Some(ModuleId(guid 101)),
          "Why does a ray tracer need a recursion cutoff?",
          "Reflective surfaces can bounce indefinitely; the cutoff bounds the work per pixel.",
          Some(entity EntityKind.Lecture 701)
          guid 803,
          Some(ModuleId(guid 102)),
          "How does high variance show up in train and test error?",
          "As a wide gap between them — low training error with much higher test error.",
          Some(entity EntityKind.Note 402)
          guid 804,
          Some(ModuleId(guid 102)),
          "What does regularisation trade away?",
          "Variance, at the cost of additional bias.",
          Some(entity EntityKind.Document 605)
          guid 805,
          Some(ModuleId(guid 105)),
          "State the memoryless property.",
          "P(X > s + t | X > s) = P(X > t): the past does not change the remaining wait.",
          None ]
        |> List.map (fun (id, moduleId, question, answer, source) ->
            { RevisionItem.create fixedNow question answer with
                Id = RevisionItemId id
                ModuleId = moduleId
                Source = source })

    /// Explicit graph edges, on top of the implicit module filing the Graph
    /// already derives. Every pair the related-items and backlink panels can
    /// draw is represented: module↔assignment, note↔lecture, note↔document,
    /// assignment↔document, assignment↔note.
    let links =
        [ guid 1001, entity EntityKind.Module 102, entity EntityKind.Assignment 203, Some "coursework"
          guid 1002, entity EntityKind.Note 401, entity EntityKind.Lecture 701, Some "notes from"
          guid 1003, entity EntityKind.Note 402, entity EntityKind.Document 605, Some "based on"
          guid 1004, entity EntityKind.Assignment 201, entity EntityKind.Document 603, Some "specification"
          guid 1005, entity EntityKind.Assignment 202, entity EntityKind.Note 401, Some "reference"
          guid 1006, entity EntityKind.Assignment 203, entity EntityKind.Note 402, Some "reference"
          guid 1007, entity EntityKind.Lecture 702, entity EntityKind.Assignment 203, Some "sets" ]
        |> List.map (fun (id, from, target, label) ->
            { Link.create fixedNow from target label with Id = LinkId id })

    /// Inbox items awaiting triage: one New, one already Analyzed (so the
    /// proposed-action rendering has something to draw), and one file capture
    /// carrying a document id.
    ///
    /// The analyzer is recorded as `heuristic`, which is the truth: no AI ran.
    let inbox =
        [ { InboxItem.create fixedNow (CaptureSource.Connector "moodle") "COMP3611: Coursework 2 released" "Coursework 2 (classification report) is now available on Minerva. Submission is via Gradescope by 17:00 on the deadline date." with
              Id = InboxItemId(guid 1101)
              ExternalId = Some "moodle-evt-88213"
              Status = InboxStatus.New
              ReceivedAt = at 0.0 7.25 }

          { InboxItem.create fixedNow (CaptureSource.Connector "gmail") "Re: Project supervision — room change" "Thursday's supervision has moved to EC Stoner 9.34. Same time." with
              Id = InboxItemId(guid 1102)
              ExternalId = Some "gmail-18f2a9c4"
              Status = InboxStatus.Analyzed
              ReceivedAt = at -1.0 18.5
              Analysis =
                Some
                    { Summary = "A supervision meeting has moved room."
                      SuggestedModuleCode = Some "COMP3931"
                      Analyzer = "heuristic"
                      Actions =
                        [ { Kind = ProposedActionKind.CreateEvent
                            Title = "Project supervision (EC Stoner 9.34)"
                            ModuleCode = Some "COMP3931"
                            Date = Some(at 1.0 10.0)
                            DurationMinutes = Some 30
                            Body = None
                            TargetId = None } ] } }

          { InboxItem.create fixedNow (CaptureSource.File "project-handbook.pdf") "Project handbook 2026" "Imported file awaiting triage." with
              Id = InboxItemId(guid 1103)
              Status = InboxStatus.New
              DocumentId = Some(DocumentId(guid 603))
              ReceivedAt = at -3.0 10.5 } ]

    /// **The activity log — what the Timeline draws.**
    ///
    /// Three properties are deliberate and are asserted by the tests, because
    /// each one is a way the Timeline can end up looking empty or broken:
    ///
    /// 1. **Every `ActivityKind` appears**, so no filter chip is dead.
    /// 2. **Every `Period` appears** — Today through Earlier — so the grouped
    ///    feed shows more than one heading.
    /// 3. **Most entries carry an `Entity`**, because Open and History render
    ///    only for an entry that has one. Entry 901 links to a *Session*, which
    ///    is the case that sends Open to the Planner.
    ///
    /// A few entries have no entity on purpose: that path renders a row with no
    /// action buttons, and it needs looking at too.
    let activity =
        [ guid 901, ActivityKind.SessionCompleted, "Classification report", Some "Study session · 50 minutes", Some(entity EntityKind.Session 501), at 0.0 8.834
          guid 902, ActivityKind.PlanGenerated, "Study plan regenerated", Some "Planned 10.0h across 7 day(s) for 5 task(s).", None, at 0.0 9.084
          guid 903, ActivityKind.AssignmentUpdated, "Ray tracer coursework 2", Some "Progress 60% → 70%", Some(entity EntityKind.Assignment 202), at 0.0 9.25
          guid 904, ActivityKind.ConnectorSynced, "Moodle", Some "Review fixture — demonstration entry, no network sync occurred", None, at 0.0 7.667
          guid 905, ActivityKind.CaptureApplied, "COMP3611: Coursework 2 released", Some "Captured into the inbox", Some(entity EntityKind.InboxItem 1101), at 0.0 7.75
          guid 906, ActivityKind.SessionCompleted, "Final project report", Some "Study session · 50 minutes", Some(entity EntityKind.Session 504), at -1.0 9.834
          guid 907, ActivityKind.NoteUpdated, "Bias and variance", Some "Edited", Some(entity EntityKind.Note 402), at -1.0 16.334
          guid 908, ActivityKind.AiAction, "Summarised Lecture 9 — Model selection", Some "Review fixture — demonstration entry, no AI request was made", Some(entity EntityKind.Lecture 702), at -1.0 13.167
          guid 909, ActivityKind.EventAdded, "Machine Learning exam", Some "Added to the calendar", Some(entity EntityKind.Event 307), at -1.0 11.084
          guid 910, ActivityKind.SessionCompleted, "Ray tracer coursework 2", Some "Study session · 50 minutes", Some(entity EntityKind.Session 506), at -2.0 11.334
          guid 911, ActivityKind.LectureAdded, "Lecture 12 — Ray tracing", Some "Slides attached", Some(entity EntityKind.Lecture 701), at -2.0 10.25
          guid 912, ActivityKind.DocumentImported, "Ray tracing — slides", Some "Imported from file", Some(entity EntityKind.Document 602), at -2.0 10.2
          guid 913, ActivityKind.FlashcardsGenerated, "Ray tracing — 2 cards", Some "Review fixture — demonstration entry, no AI request was made", Some(entity EntityKind.Lecture 701), at -2.0 10.667
          guid 914, ActivityKind.ModuleUpdated, "COMP3611 Machine Learning", Some "Lecturer set", Some(entity EntityKind.Module 102), at -2.0 8.5
          guid 915, ActivityKind.AssignmentCreated, "Classification report", Some "Added to COMP3611", Some(entity EntityKind.Assignment 203), at -9.0 12.0
          guid 916, ActivityKind.DeadlineChanged, "Shading lab exercises", Some "Moved to 23 February", Some(entity EntityKind.Assignment 206), at -9.0 14.5
          guid 917, ActivityKind.NoteCreated, "Ray-triangle intersection", Some "Created in COMP3811", Some(entity EntityKind.Note 401), at -10.0 19.084
          guid 918, ActivityKind.DocumentImported, "Project handbook 2026", Some "Imported from file", Some(entity EntityKind.Document 603), at -21.0 9.334
          guid 919, ActivityKind.AssignmentCreated, "Coursework 1 — regression", Some "Added to COMP3611", Some(entity EntityKind.Assignment 207), at -30.0 10.0
          guid 920, ActivityKind.ModuleUpdated, "COMP2211 Operating Systems", Some "Archived", Some(entity EntityKind.Module 106), at -25.0 15.75
          guid 921, ActivityKind.ModuleUpdated, "COMP3931 Individual Project", Some "Created", Some(entity EntityKind.Module 103), at -75.0 9.0
          guid 922, ActivityKind.NoteCreated, "Reading list", Some "Created", Some(entity EntityKind.Note 404), at -70.0 20.25 ]
        |> List.map (fun (id, kind, title, detail, ent, occurredAt) ->
            { Id = id
              Kind = kind
              Title = title
              Detail = detail
              Entity = ent
              OccurredAt = occurredAt })

    /// AI memory items for Settings. One is disabled, because the disabled
    /// rendering is otherwise never on screen.
    let memory =
        [ guid 1201, MemorySource.Stats, "pace", "You complete about three study sessions a week.", true
          guid 1202, MemorySource.Stats, "schedule", "Most completed sessions start between 09:00 and 11:00.", true
          guid 1203, MemorySource.Ai, "habits", "Review fixture — example observation. Written into the fixture, not produced by an AI model.", true
          guid 1204, MemorySource.User, "preferences", "Prefers 50-minute blocks with a 10-minute break.", true
          guid 1205, MemorySource.Ai, "pace", "Review fixture — example observation. Ray tracing work takes longer than estimated.", false ]
        |> List.map (fun (id, source, category, content, enabled) ->
            { Id = id
              Category = category
              Content = content
              Source = source
              Enabled = enabled
              UpdatedAt = fixedNow })

    // ------------------------------------------------------------------
    // Connections — demonstration state only
    // ------------------------------------------------------------------
    //
    // Review mode makes no network call, so nothing here may imply one
    // happened. The status field is the app's own enum and cannot carry a
    // caveat, so the caveat goes everywhere text is free: the config value the
    // Connections screen displays, the error message, and every sync-history
    // line. All of them start with `fixtureLabel`.

    let connections =
        [ { ConnectionState.initial "moodle" fixedNow with
              Status = ConnectionStatus.Connected
              LastSync = Some(at 0.0 7.667)
              SyncFrequencyMinutes = 30
              Config = Map.ofList [ "base_url", "Review fixture — demonstration data (no server contacted)" ] }

          { ConnectionState.initial "gmail" fixedNow with
              Status = ConnectionStatus.Connected
              LastSync = Some(at -1.0 18.5)
              SyncFrequencyMinutes = 60 }

          { ConnectionState.initial "gitlab" fixedNow with
              Status = ConnectionStatus.Failed "Review fixture — demonstration error state, no connection was attempted"
              LastSync = Some(at -2.0 8.0)
              Config = Map.ofList [ "base_url", "Review fixture — demonstration data (no server contacted)" ] }

          { ConnectionState.initial "google-calendar" fixedNow with
              Status = ConnectionStatus.Disconnected } ]

    /// Sync history rows. `SyncOutcome.describe` returns `Message` verbatim when
    /// it is set, so the label reaches the Connections history list intact.
    let syncHistory: (string * DateTime * DateTime * Result<SyncOutcome, string>) list =
        [ "moodle",
          at 0.0 7.65,
          at 0.0 7.667,
          Ok { Imported = 2; Updated = 1; Skipped = 5; Message = Some "Review fixture — demonstration data, no network sync occurred" }

          "gmail",
          at -1.0 18.48,
          at -1.0 18.5,
          Ok { Imported = 1; Updated = 0; Skipped = 12; Message = Some "Review fixture — demonstration data, no network sync occurred" }

          "gitlab", at -2.0 7.98, at -2.0 8.0, Error "Review fixture — demonstration error state, no connection was attempted"

          "moodle",
          at -2.0 7.65,
          at -2.0 7.667,
          Ok { Imported = 0; Updated = 0; Skipped = 8; Message = Some "Review fixture — demonstration data, no network sync occurred" } ]

    // ------------------------------------------------------------------
    // Assistant — one example exchange, labelled in its own text
    // ------------------------------------------------------------------
    //
    // No API call, no secret, no network. The reply below was written here, and
    // says so in its first line, so nothing on the Assistant screen can be read
    // as something a model produced.

    let conversation =
        { Id = ConversationId(guid 1301)
          Title = "Review fixture — example conversation"
          CreatedAt = at -1.0 20.0
          UpdatedAt = at -1.0 20.05 }

    let chatMessages =
        [ { Id = MessageId(guid 1302)
            ConversationId = conversation.Id
            Role = ChatRole.User
            Content =
                "Example question (Review Mode fixture): how should I split the remaining work on the final project report?"
            CreatedAt = at -1.0 20.0 }

          { Id = MessageId(guid 1303)
            ConversationId = conversation.Id
            Role = ChatRole.Assistant
            Content =
                "**Review fixture — example response.** This text was written into the Review Mode fixture. It is not a live AI response, and no request was made to any model.\n\nWith 45 estimated hours and 27 already spent, roughly 18 remain. Across the four planner blocks currently scheduled before the deadline that is about four and a half hours each.\n\n- The evaluation chapter is the only part with a hard dependency, so it goes first\n- Leave the introduction until last; it is the cheapest to write and the most likely to change\n- The 12-hour deadline buffer in Settings already holds back the final afternoon"
            CreatedAt = at -1.0 20.05 } ]

    // ------------------------------------------------------------------
    // Seeding
    // ------------------------------------------------------------------

    /// Reads and writes the fixture-version marker. An interface rather than a
    /// direct database call so the tests can supply their own store, and so
    /// `seed` has no opinion about where the marker lives.
    type IFixtureMarker =
        abstract Read: unit -> Async<string option>
        abstract Write: version: string -> Async<unit>

    /// The marker, kept in the isolated review database's own settings table.
    /// Under its own key, so it neither reads nor disturbs `UserSettings`.
    type SettingsTableMarker(db: Database) =
        interface IFixtureMarker with
            member _.Read() =
                async {
                    let! row =
                        db.QueryOne(
                            "SELECT value FROM settings WHERE key = $key",
                            [ "$key", Sql.ofStr fixtureVersionKey ],
                            fun r -> Sql.str r "value"
                        )

                    return row
                }

            member _.Write(version) =
                db.Execute(
                    "INSERT INTO settings(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value=$value",
                    [ "$key", Sql.ofStr fixtureVersionKey; "$value", Sql.ofStr version ]
                )

    /// Everything `seed` writes through. A record rather than fifteen
    /// parameters, so a new fixture kind is one named field at both ends
    /// instead of a positional argument somebody can transpose.
    type Targets =
        { Modules: IModuleRepository
          Assignments: IAssignmentRepository
          Events: IEventRepository
          Notes: INoteRepository
          Sessions: ISessionRepository
          Documents: IDocumentRepository
          Lectures: ILectureRepository
          Revision: IRevisionRepository
          Milestones: IMilestoneRepository
          Links: ILinkRepository
          Inbox: IInboxRepository
          Chat: IChatRepository
          Connections: IConnectionRepository
          SyncLog: ISyncLogRepository
          Activity: IActivityLog
          Memory: IMemoryRepository
          Marker: IFixtureMarker }

    /// Whether the store holds nothing at all.
    ///
    /// **This is the safety catch, and it is deliberately broad.** If the review
    /// root were ever mistargeted at a real database, any one of these being
    /// non-empty must stop the seed — a check that only looked at modules would
    /// happily merge fixture rows into somebody's notes.
    let private isEmpty (t: Targets) =
        async {
            let! modules = t.Modules.GetAll()
            let! assignments = t.Assignments.GetAll()
            let! notes = t.Notes.GetAll()
            let! documents = t.Documents.GetAll()
            let! lectures = t.Lectures.GetAll()
            let! inboxItems = t.Inbox.GetAll()
            let! activityEntries = t.Activity.GetRecent 1
            let! events = t.Events.GetBetween(fixedNow.AddYears -5, fixedNow.AddYears 5)
            let! sessions = t.Sessions.GetBetween(fixedNow.AddYears -5, fixedNow.AddYears 5)

            return
                List.isEmpty modules
                && List.isEmpty assignments
                && List.isEmpty notes
                && List.isEmpty documents
                && List.isEmpty lectures
                && List.isEmpty inboxItems
                && List.isEmpty activityEntries
                && List.isEmpty events
                && List.isEmpty sessions
        }

    let private writeAll (t: Targets) (dataRoot: string) =
        async {
            for m in modules do
                do! t.Modules.Save m

            for a in assignments do
                do! t.Assignments.Save a

            for e in events do
                do! t.Events.Save e

            for n in notes do
                do! t.Notes.Save n

            // Files before rows: a Document whose bytes are not there yet would
            // be "missing" to anything that looked in between.
            writeFixtureFiles dataRoot

            for d in documentsIn dataRoot do
                do! t.Documents.Save d

            for l in lectures do
                do! t.Lectures.Save l

            do! t.Revision.SaveMany revision

            for m in milestones do
                do! t.Milestones.Save m

            for l in links do
                do! t.Links.Save l

            for i in inbox do
                do! t.Inbox.Save i

            // `ReplaceFrom` is the only write the session repository offers. The
            // range starts before the oldest fixture session so the completed
            // ones are inserted too, rather than being treated as history that
            // predates the replacement window.
            do! t.Sessions.ReplaceFrom(fixedNow.AddYears -1, sessions)

            for e in activity do
                do! t.Activity.Log e

            for m in memory do
                do! t.Memory.Save m

            for c in connections do
                do! t.Connections.Save c

            for (key, startedAt, finishedAt, outcome) in syncHistory do
                do! t.SyncLog.Log(key, startedAt, finishedAt, outcome)

            do! t.Chat.SaveConversation conversation

            for m in chatMessages do
                do! t.Chat.SaveMessage m
        }

    /// **Writes the fixture — only into an empty store, and only once.**
    ///
    /// Four outcomes, and only one of them writes anything:
    ///
    /// | marker | store | outcome |
    /// |---|---|---|
    /// | current | any | nothing — already seeded, so a review session's own edits survive |
    /// | stale | any | nothing — says how to rebuild, and stops |
    /// | absent | non-empty | nothing — the safety catch, unchanged |
    /// | absent | empty | seeds, then records the version |
    ///
    /// The stale case refuses rather than topping up. Adding this version's new
    /// kinds to a store built from the previous one would produce a fixture that
    /// is neither version — reproducible for nobody, which is the one thing a
    /// review fixture must never be.
    let seed (t: Targets) (dataRoot: string) =
        async {
            printfn "[review] fixture version %s — store %s" fixtureVersion dataRoot

            let! marker = t.Marker.Read()
            let! storeIsEmpty = isEmpty t

            match marker with
            | Some existing when existing = fixtureVersion ->
                printfn "[review] fixture %s already present — store left untouched" existing

            | Some existing ->
                printfn "[review] STALE FIXTURE — store holds %s, this build expects %s." existing fixtureVersion
                printfn "[review] Nothing was written. Close the app, delete %s, and relaunch." dataRoot

            | None when not storeIsEmpty ->
                printfn "[review] store already has content and no fixture marker — left untouched"

            | None ->
                do! writeAll t dataRoot
                do! t.Marker.Write fixtureVersion

                printfn
                    "[review] seeded %d modules, %d assignments, %d events, %d notes, %d sessions, %d documents, %d lectures, %d flashcards, %d milestones, %d links, %d inbox items, %d activity entries, %d memory items, %d connections, %d sync rows, 1 conversation into %s"
                    modules.Length
                    assignments.Length
                    events.Length
                    notes.Length
                    sessions.Length
                    documentFixtures.Length
                    lectures.Length
                    revision.Length
                    milestones.Length
                    links.Length
                    inbox.Length
                    activity.Length
                    memory.Length
                    connections.Length
                    syncHistory.Length
                    dataRoot
        }

    /// The review queue, in the order the migration batch is to be inspected.
    /// Kept here rather than in a document so the running app and the review
    /// notes cannot drift apart.
    let queue =
        [ "Settings"
          "Connections"
          "Calendar"
          "Search"
          "Assistant"
          "Graph"
          "Documents"
          "Notes"
          "Lectures"
          "Modules"
          "Assignments" ]
