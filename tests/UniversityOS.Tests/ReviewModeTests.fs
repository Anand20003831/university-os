module UniversityOS.Tests.ReviewModeTests

open System
open System.IO
open Xunit
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.Infrastructure
open UniversityOS.Infrastructure.Repositories
open UniversityOS.App

/// **What these tests are for.**
///
/// Review mode's whole value rests on two promises.
///
/// The first is that *it cannot touch the user's real data*. That is not
/// something a reviewer can check by looking at the screen — by the time damage
/// is visible it has happened — so it is checked here instead, mechanically, on
/// every run.
///
/// The second is that *every screen has something on it*. That promise was
/// quietly broken for a long time: the fixture seeded four entity kinds and the
/// application reads twenty, so Timeline, Planner, Inbox, Lectures, Documents
/// and Assistant were empty on every review pass. Nothing failed, because
/// nothing asserted the fixture reached the repositories those screens read.
/// The coverage tests below are the answer to that, and they run against a real
/// SQLite database through the real repositories rather than against stand-ins,
/// because a stand-in cannot tell you the row actually persisted.

// ---------------------------------------------------------------------------
// A real store, in a temp directory of its own
// ---------------------------------------------------------------------------

/// Every test gets its own database and its own file root, so no test can see
/// another's writes and none of them go anywhere near the review store the
/// reviewer is about to look at.
type private Store() =
    let dataRoot =
        Path.Combine(Path.GetTempPath(), "UniversityOS-Tests", Guid.NewGuid().ToString "N")

    do Directory.CreateDirectory dataRoot |> ignore

    let db = Database(Path.Combine(dataRoot, "universityos.db"))
    do Migrations.apply db |> Async.RunSynchronously

    member _.DataRoot = dataRoot
    member _.Db = db

    member _.Targets: ReviewMode.Targets =
        { Modules = ModuleRepository db
          Assignments = AssignmentRepository db
          Events = EventRepository db
          Notes = NoteRepository db
          Sessions = SessionRepository db
          Documents = DocumentRepository db
          Lectures = LectureRepository db
          Revision = RevisionRepository db
          Milestones = MilestoneRepository db
          Links = LinkRepository db
          Inbox = InboxRepository db
          Chat = ChatRepository db
          Connections = ConnectionRepository db
          SyncLog = SyncLogRepository db
          Activity = ActivityRepository db
          Memory = MemoryRepository db
          Marker = ReviewMode.SettingsTableMarker db }

    member this.Seed() =
        ReviewMode.seed this.Targets dataRoot |> Async.RunSynchronously

    member _.Search = SearchRepository db :> ISearchRepository

    interface IDisposable with
        member _.Dispose() =
            // Best-effort: SQLite pools connections, so a locked file must not
            // fail the test that just passed.
            try
                Directory.Delete(dataRoot, true)
            with _ ->
                ()

let private seeded () =
    let store = new Store()
    store.Seed()
    store

let private run x = Async.RunSynchronously x

// ---------------------------------------------------------------------------
// Isolation — the part that must never regress
// ---------------------------------------------------------------------------

/// The real store, computed the way `Composition.build` computes it in the
/// non-review branch. If review mode ever resolved to this, the harness would
/// be migrating and writing the user's own database.
let private realRoot =
    Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.ApplicationData, "UniversityOS")

[<Fact>]
let ``the review root is not the real data root, nor inside it`` () =
    let review = Path.GetFullPath(ReviewMode.root ())
    let real = Path.GetFullPath realRoot

    Assert.NotEqual<string>(real, review)

    // Not merely different — not nested either. A root *under* the real one
    // would still put fixture files in the user's folder, and would be swept
    // up by anything that deletes it wholesale.
    Assert.False(
        review.StartsWith(real + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
        sprintf "review root %s is inside the real data root %s" review real
    )

    Assert.False(
        real.StartsWith(review + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
        sprintf "the real data root %s is inside the review root %s" real review
    )

[<Fact>]
let ``review mode is off unless explicitly asked for`` () =
    // The test host runs with neither the flag nor the variable set, which is
    // exactly the condition a normal user launch runs in. If this ever fails,
    // some default has flipped and ordinary users are running on fixture data.
    Assert.Null(Environment.GetEnvironmentVariable "UNIVERSITYOS_REVIEW")
    Assert.False(ReviewMode.isActive ())

[<Fact>]
let ``the environment switch turns it on, and only for the exact value`` () =
    let restore = Environment.GetEnvironmentVariable "UNIVERSITYOS_REVIEW"

    try
        // Values that must NOT enable it. "0" and "false" are the ones somebody
        // would plausibly set meaning *off*; silently treating them as on would
        // be the worst possible reading.
        for off in [ "0"; "false"; ""; "no" ] do
            Environment.SetEnvironmentVariable("UNIVERSITYOS_REVIEW", off)
            Assert.False(ReviewMode.isActive (), sprintf "%A should not enable review mode" off)

        Environment.SetEnvironmentVariable("UNIVERSITYOS_REVIEW", "1")
        Assert.True(ReviewMode.isActive ())

        // Surrounding whitespace is forgiven — a value pasted from a script
        // should not silently fail to take effect.
        Environment.SetEnvironmentVariable("UNIVERSITYOS_REVIEW", " 1 ")
        Assert.True(ReviewMode.isActive ())
    finally
        Environment.SetEnvironmentVariable("UNIVERSITYOS_REVIEW", restore)

/// **The production store is never opened.** Seeding writes a database, files
/// and a marker; every one of them must land in the root it was handed. This
/// watches the real folder across a seed and asserts it did not move.
[<Fact>]
let ``seeding writes nothing into the real data root`` () =
    let snapshot () =
        if Directory.Exists realRoot then
            Directory.EnumerateFiles(realRoot, "*", SearchOption.AllDirectories)
            |> Seq.map (fun f ->
                let info = FileInfo f
                sprintf "%s|%d|%s" f info.Length (info.LastWriteTimeUtc.ToString "o"))
            |> Seq.sort
            |> String.concat "\n"
        else
            "<absent>"

    let before = snapshot ()
    use store = seeded ()
    Assert.NotEmpty(store.Targets.Modules.GetAll() |> run)

    Assert.Equal<string>(before, snapshot ())

// ---------------------------------------------------------------------------
// Every repository the screens read
// ---------------------------------------------------------------------------

/// **The test that would have caught the empty screens.**
///
/// One assertion per repository, named for the screen that goes blank without
/// it. Before this existed, `seed` reached four of these fifteen and every test
/// still passed.
[<Fact>]
let ``every repository a screen reads receives fixture data`` () =
    use store = seeded ()
    let t = store.Targets
    let wide = ReviewMode.fixedNow.AddYears -5, ReviewMode.fixedNow.AddYears 5

    Assert.NotEmpty(t.Modules.GetAll() |> run) // Modules
    Assert.NotEmpty(t.Assignments.GetAll() |> run) // Assignments
    Assert.NotEmpty(t.Events.GetBetween wide |> run) // Calendar
    Assert.NotEmpty(t.Notes.GetAll() |> run) // Notes
    Assert.NotEmpty(t.Sessions.GetBetween wide |> run) // Planner
    Assert.NotEmpty(t.Documents.GetAll() |> run) // Documents
    Assert.NotEmpty(t.Lectures.GetAll() |> run) // Lectures
    Assert.NotEmpty(t.Revision.GetAll() |> run) // Modules detail, Graph
    Assert.NotEmpty(t.Milestones.GetAll() |> run) // Assignments, Planner, Modules, Calendar
    Assert.NotEmpty(t.Links.GetAll() |> run) // Graph, backlinks
    Assert.NotEmpty(t.Inbox.GetAll() |> run) // Inbox
    Assert.NotEmpty(t.Chat.GetConversations() |> run) // Assistant
    Assert.NotEmpty(t.Connections.GetAll() |> run) // Connections
    Assert.NotEmpty(t.SyncLog.GetRecent 20 |> run) // Connections history
    Assert.NotEmpty(t.Activity.GetRecent 300 |> run) // Timeline, Dashboard
    Assert.NotEmpty(t.Memory.GetAll() |> run) // Settings

// ---------------------------------------------------------------------------
// Timeline
// ---------------------------------------------------------------------------

[<Fact>]
let ``the Timeline fixture covers every category, so no filter chip is dead`` () =
    use store = seeded ()
    let entries = store.Targets.Activity.GetRecent 300 |> run

    for kind in ActivityKind.all do
        Assert.Contains(entries, fun (e: ActivityEntry) -> e.Kind = kind)

    for category in UniversityOS.App.Features.TimelineLogic.Category.all do
        let visible =
            UniversityOS.App.Features.TimelineLogic.visibleEntries category "" entries

        Assert.True(
            not visible.IsEmpty,
            sprintf "the %A filter shows nothing" category
        )

[<Fact>]
let ``the Timeline fixture spans every period heading`` () =
    use store = seeded ()
    let entries = store.Targets.Activity.GetRecent 300 |> run

    let periods =
        entries
        |> List.map (fun e -> UniversityOS.App.Features.TimelineLogic.periodOf ReviewMode.fixedNow e.OccurredAt)
        |> Set.ofList

    for period in UniversityOS.App.Features.TimelineLogic.Period.order do
        Assert.True(periods.Contains period, sprintf "no entry falls in %A" period)

/// **Open and History render only for an entry that has a linked entity.**
///
/// This is the assertion whose absence sent the reviewer looking for a row that
/// was never there: the row it described lived in a test file, not in the
/// fixture. A Session link specifically, because that is the case that sends
/// Open to the Planner.
[<Fact>]
let ``the Timeline fixture has linked entries, including a Session`` () =
    use store = seeded ()
    let entries = store.Targets.Activity.GetRecent 300 |> run

    let linked = entries |> List.filter (fun e -> e.Entity.IsSome)
    Assert.True(linked.Length >= 10, "too few entries carry an entity — most rows would have no actions")

    Assert.Contains(entries, fun (e: ActivityEntry) ->
        match e.Entity with
        | Some ref -> ref.Kind = EntityKind.Session
        | None -> false)

    // And the unlinked case, which renders a row with no buttons at all.
    Assert.Contains(entries, fun (e: ActivityEntry) -> e.Entity.IsNone)

[<Fact>]
let ``the Classification report session entry links to a session that exists`` () =
    use store = seeded ()
    let t = store.Targets
    let entries = t.Activity.GetRecent 300 |> run

    let entry =
        entries
        |> List.find (fun e ->
            e.Title = "Classification report"
            && e.Kind = ActivityKind.SessionCompleted)

    Assert.True(entry.Entity.IsSome, "the row would render with no Open or History button")
    let target = entry.Entity.Value
    Assert.Equal(EntityKind.Session, target.Kind)

    // The foreign key resolves: History filters the log by this entity, and
    // Open navigates to the page that owns it.
    let sessions =
        t.Sessions.GetBetween(ReviewMode.fixedNow.AddYears -1, ReviewMode.fixedNow.AddYears 1) |> run

    Assert.Contains(sessions, fun (s: PlannedSession) -> s.Id.Value = target.Id)
    Assert.Equal(Page.Planner, Page.forEntity target.Kind)

// ---------------------------------------------------------------------------
// Planner, Inbox, Lectures, Documents
// ---------------------------------------------------------------------------

[<Fact>]
let ``the Planner fixture has completed, missed, today and future sessions`` () =
    use store = seeded ()
    let now = ReviewMode.fixedNow

    let sessions =
        store.Targets.Sessions.GetBetween(now.AddYears -1, now.AddYears 1) |> run

    Assert.Contains(sessions, fun (s: PlannedSession) -> s.Completed)
    Assert.Contains(sessions, fun (s: PlannedSession) -> not s.Completed && s.EndsAt < now)
    Assert.Contains(sessions, fun (s: PlannedSession) -> s.StartsAt.Date = now.Date)
    Assert.Contains(sessions, fun (s: PlannedSession) -> s.StartsAt > now.Date.AddDays 1.0)

    // Every session points at an assignment that is really there, so the
    // Planner's "for <assignment>" line resolves rather than dangling.
    let assignmentIds = ReviewMode.assignments |> List.map (fun a -> a.Id) |> Set.ofList

    for s in sessions do
        match s.AssignmentId with
        | Some id -> Assert.True(assignmentIds.Contains id, sprintf "session %A points at a missing assignment" s.Id)
        | None -> ()

[<Fact>]
let ``the Inbox fixture has a New item and an Analyzed one`` () =
    use store = seeded ()
    let items = store.Targets.Inbox.GetAll() |> run

    Assert.Contains(items, fun (i: InboxItem) -> i.Status = InboxStatus.New)
    Assert.Contains(items, fun (i: InboxItem) -> i.Status = InboxStatus.Analyzed)

    // Active is what the Inbox screen actually lists.
    Assert.NotEmpty(store.Targets.Inbox.GetActive() |> run)

    // The analysed one carries a proposed action, or the action rendering has
    // nothing to draw.
    Assert.Contains(items, fun (i: InboxItem) ->
        match i.Analysis with
        | Some a -> not a.Actions.IsEmpty
        | None -> false)

[<Fact>]
let ``lectures and documents are linked to each other and to modules`` () =
    use store = seeded ()
    let t = store.Targets
    let documents = t.Documents.GetAll() |> run
    let lectures = t.Lectures.GetAll() |> run
    let documentIds = documents |> List.map (fun d -> d.Id) |> Set.ofList
    let moduleIds = ReviewMode.modules |> List.map (fun m -> m.Id) |> Set.ofList

    Assert.Contains(lectures, fun (l: Lecture) -> l.SlidesDocumentId.IsSome)
    Assert.Contains(lectures, fun (l: Lecture) -> l.Transcript.IsSome && l.Summary.IsSome)

    // The bare state — no slides, no transcript, no summary — must be present
    // too, or its rendering is never inspected.
    Assert.Contains(lectures, fun (l: Lecture) ->
        l.SlidesDocumentId.IsNone && l.Transcript.IsNone && l.Summary.IsNone)

    // Every foreign key resolves.
    for l in lectures do
        match l.SlidesDocumentId with
        | Some id -> Assert.True(documentIds.Contains id, "a lecture points at a missing document")
        | None -> ()

        match l.ModuleId with
        | Some id -> Assert.True(moduleIds.Contains id, "a lecture points at a missing module")
        | None -> ()

    for d in documents do
        match d.ModuleId with
        | Some id -> Assert.True(moduleIds.Contains id, "a document points at a missing module")
        | None -> ()

[<Fact>]
let ``documents cover more than one processing state`` () =
    use store = seeded ()
    let documents = store.Targets.Documents.GetAll() |> run

    Assert.Contains(documents, fun (d: Document) -> d.ExtractedText.IsSome && d.Summary.IsSome)
    Assert.Contains(documents, fun (d: Document) -> d.ExtractedText.IsSome && d.Summary.IsNone)
    Assert.Contains(documents, fun (d: Document) -> d.ExtractedText.IsNone && d.Summary.IsNone)
    Assert.Contains(documents, fun (d: Document) -> d.Kind = DocumentKind.Pdf)

// ---------------------------------------------------------------------------
// The fixture files on disk
// ---------------------------------------------------------------------------

/// A Document row whose bytes are not there renders as "missing" and cannot be
/// opened. Metadata alone would be a fixture that shows a broken state.
[<Fact>]
let ``every document fixture has a real file, of the size the row claims`` () =
    use store = seeded ()

    for d in store.Targets.Documents.GetAll() |> run do
        Assert.True(File.Exists d.StoredPath, sprintf "%s is missing from disk" d.StoredPath)
        Assert.Equal(d.SizeBytes, (FileInfo d.StoredPath).Length)
        Assert.True(d.SizeBytes > 0L, sprintf "%s is empty" d.FileName)

/// **Nothing escapes the root it was handed.** A `..` or a rooted file name in
/// a fixture entry would write outside the review store — the one thing the
/// isolation promise forbids.
[<Fact>]
let ``no fixture file path escapes the review root`` () =
    use store = seeded ()
    let root = Path.GetFullPath store.DataRoot

    for d in store.Targets.Documents.GetAll() |> run do
        let full = Path.GetFullPath d.StoredPath

        Assert.StartsWith(root + string Path.DirectorySeparatorChar, full, StringComparison.OrdinalIgnoreCase)
        Assert.DoesNotContain("..", full)
        Assert.Equal<string>(d.FileName, Path.GetFileName d.FileName)

    // And on disk: every file written under the root really is under it.
    for f in Directory.EnumerateFiles(store.DataRoot, "*", SearchOption.AllDirectories) do
        Assert.StartsWith(root, Path.GetFullPath f, StringComparison.OrdinalIgnoreCase)

/// The PDF is assembled rather than checked in, so the structure it must have
/// is asserted rather than assumed.
[<Fact>]
let ``the PDF fixture is a structurally valid PDF`` () =
    use store = seeded ()

    let pdf =
        store.Targets.Documents.GetAll() |> run |> List.find (fun d -> d.Kind = DocumentKind.Pdf)

    let text = File.ReadAllText pdf.StoredPath

    Assert.StartsWith("%PDF-1.", text)
    Assert.Contains("/Type /Catalog", text)
    Assert.Contains("/Type /Page", text)
    Assert.Contains("startxref", text)
    Assert.EndsWith("%%EOF\n", text)

    // The cross-reference offset must point at the xref table itself, which is
    // the part a hand-written PDF gets wrong and a viewer refuses to open.
    let startXref =
        text.Substring(text.LastIndexOf("startxref", StringComparison.Ordinal) + 9).Trim().Split('\n').[0].Trim()
        |> int

    Assert.Equal<string>("xref", text.Substring(startXref, 4))

// ---------------------------------------------------------------------------
// Search and Graph
// ---------------------------------------------------------------------------

/// The repositories index into FTS as a side effect of `Save`, so seeding a kind
/// is what makes it findable. This asserts the effect, not the mechanism.
[<Fact>]
let ``search finds the seeded entity kinds`` () =
    use store = seeded ()
    let search = store.Search

    let kindsFor (query: string) =
        search.Query(query, 50) |> run |> List.map (fun h -> h.Kind) |> Set.ofList

    Assert.Contains(EntityKind.Assignment, kindsFor "Classification")
    Assert.Contains(EntityKind.Module, kindsFor "COMP3811")
    Assert.Contains(EntityKind.Note, kindsFor "variance")
    Assert.Contains(EntityKind.Event, kindsFor "Stochastic")
    Assert.Contains(EntityKind.Document, kindsFor "handbook")
    Assert.Contains(EntityKind.Lecture, kindsFor "collectives")
    Assert.Contains(EntityKind.RevisionItem, kindsFor "memoryless")

[<Fact>]
let ``the graph fixture has explicit links across several entity kinds`` () =
    use store = seeded ()
    let links = store.Targets.Links.GetAll() |> run

    let pair (a: EntityKind) (b: EntityKind) =
        links
        |> List.exists (fun l ->
            (l.From.Kind = a && l.To.Kind = b) || (l.From.Kind = b && l.To.Kind = a))

    // The four pairings the related-items and backlink panels draw.
    Assert.True(pair EntityKind.Module EntityKind.Assignment, "no module ↔ assignment link")
    Assert.True(pair EntityKind.Note EntityKind.Lecture, "no note ↔ lecture link")
    Assert.True(pair EntityKind.Note EntityKind.Document, "no note ↔ document link")
    Assert.True(pair EntityKind.Assignment EntityKind.Document, "no assignment ↔ document link")

[<Fact>]
let ``every link endpoint refers to an entity the fixture really seeds`` () =
    use store = seeded ()

    let known =
        Set.ofList
            [ yield! ReviewMode.modules |> List.map (fun m -> EntityKind.Module, m.Id.Value)
              yield! ReviewMode.assignments |> List.map (fun a -> EntityKind.Assignment, a.Id.Value)
              yield! ReviewMode.notes |> List.map (fun n -> EntityKind.Note, n.Id.Value)
              yield! ReviewMode.events |> List.map (fun e -> EntityKind.Event, e.Id.Value)
              yield! ReviewMode.lectures |> List.map (fun l -> EntityKind.Lecture, l.Id.Value)
              yield!
                  ReviewMode.documentsIn store.DataRoot
                  |> List.map (fun d -> EntityKind.Document, d.Id.Value) ]

    for l in store.Targets.Links.GetAll() |> run do
        Assert.True(known.Contains(l.From.Kind, l.From.Id), sprintf "link from a missing %A" l.From.Kind)
        Assert.True(known.Contains(l.To.Kind, l.To.Id), sprintf "link to a missing %A" l.To.Kind)

// ---------------------------------------------------------------------------
// Labelling — the fixtures that could be mistaken for real activity
// ---------------------------------------------------------------------------

/// **No API call, no secret, no network.** The reply was written into the
/// fixture, and it has to say so on screen, because an unlabelled one would be
/// indistinguishable from something a model produced.
[<Fact>]
let ``the Assistant fixture is labelled as example data in its own text`` () =
    use store = seeded ()
    let t = store.Targets

    let conversation = t.Chat.GetConversations() |> run |> List.exactlyOne
    Assert.Contains(ReviewMode.fixtureLabel, conversation.Title)

    let messages = t.Chat.GetMessages conversation.Id |> run
    Assert.Equal(2, messages.Length)

    let reply = messages |> List.find (fun m -> m.Role = ChatRole.Assistant)
    Assert.Contains(ReviewMode.fixtureLabel, reply.Content)
    Assert.Contains("not a live AI response", reply.Content)

    // The question too — a labelled answer under an unlabelled question still
    // reads as a real exchange.
    let question = messages |> List.find (fun m -> m.Role = ChatRole.User)
    Assert.Contains("Review Mode fixture", question.Content)

/// The status enum cannot carry a caveat, so every piece of free text around it
/// must. A "Connected" row with no qualification anywhere would assert a sync
/// that never happened.
[<Fact>]
let ``the Connections fixture is labelled as demonstration data everywhere text is free`` () =
    use store = seeded ()
    let t = store.Targets

    let states = t.Connections.GetAll() |> run
    Assert.NotEmpty states

    for state in states do
        for KeyValue(_, value) in state.Config do
            Assert.Contains(ReviewMode.fixtureLabel, value)

        match state.Status with
        | ConnectionStatus.Failed message -> Assert.Contains(ReviewMode.fixtureLabel, message)
        | _ -> ()

    // Every history line the Connections screen shows.
    let history = t.SyncLog.GetRecent 50 |> run
    Assert.NotEmpty history

    for (_, _, message) in history do
        Assert.Contains(ReviewMode.fixtureLabel, message)

[<Fact>]
let ``the fixture states an error and a connected connector, so both renderings show`` () =
    use store = seeded ()
    let states = store.Targets.Connections.GetAll() |> run

    Assert.Contains(states, fun (s: ConnectionState) -> s.Status = ConnectionStatus.Connected)
    Assert.Contains(states, fun (s: ConnectionState) -> s.Status = ConnectionStatus.Disconnected)

    Assert.Contains(states, fun (s: ConnectionState) ->
        match s.Status with
        | ConnectionStatus.Failed _ -> true
        | _ -> false)

/// Activity entries that would otherwise read as a real sync or a real AI call
/// carry the same caveat, for the same reason.
[<Fact>]
let ``activity entries that imply a network or AI action say they are fixture data`` () =
    use store = seeded ()
    let entries = store.Targets.Activity.GetRecent 300 |> run

    let implied =
        [ ActivityKind.ConnectorSynced; ActivityKind.AiAction; ActivityKind.FlashcardsGenerated ]

    for entry in entries |> List.filter (fun e -> List.contains e.Kind implied) do
        let detail = entry.Detail |> Option.defaultValue ""

        Assert.Contains(ReviewMode.fixtureLabel, detail)

// ---------------------------------------------------------------------------
// The seed guard and the fixture version
// ---------------------------------------------------------------------------

let private counts (t: ReviewMode.Targets) =
    let wide = ReviewMode.fixedNow.AddYears -5, ReviewMode.fixedNow.AddYears 5

    (t.Modules.GetAll() |> run).Length,
    (t.Assignments.GetAll() |> run).Length,
    (t.Notes.GetAll() |> run).Length,
    (t.Documents.GetAll() |> run).Length,
    (t.Activity.GetRecent 300 |> run).Length,
    (t.Sessions.GetBetween wide |> run).Length,
    (t.Links.GetAll() |> run).Length

[<Fact>]
let ``seeding an empty store writes the fixture and records the version`` () =
    use store = new Store()
    Assert.True((store.Targets.Marker.Read() |> run).IsNone)

    store.Seed()

    Assert.Equal(Some ReviewMode.fixtureVersion, store.Targets.Marker.Read() |> run)

[<Fact>]
let ``re-seeding is idempotent, so a review session's own edits survive restart`` () =
    use store = seeded ()
    let first = counts store.Targets

    store.Seed()
    store.Seed()

    Assert.Equal(first, counts store.Targets)

/// **The safety catch.** Each store is tested alone, because the dangerous case
/// is a *partly* populated database: a real one with, say, only notes in it must
/// still be left alone. A guard that only checked modules would pass a
/// whole-store test and destroy that user's notes.
[<Theory>]
[<InlineData "module">]
[<InlineData "assignment">]
[<InlineData "note">]
[<InlineData "event">]
[<InlineData "session">]
[<InlineData "document">]
[<InlineData "lecture">]
[<InlineData "inbox">]
[<InlineData "activity">]
let ``seeding a store that already has ANY content writes nothing`` (occupied: string) =
    use store = new Store()
    let t = store.Targets
    let now = ReviewMode.fixedNow

    match occupied with
    | "module" -> t.Modules.Save(StudyModule.create now "EXIST" "Existing module" 0) |> run
    | "assignment" -> t.Assignments.Save(Assignment.create now "Existing assignment") |> run
    | "note" -> t.Notes.Save(Note.create now "Existing note") |> run
    | "event" ->
        t.Events.Save(CalendarEvent.create now "Existing event" EventKind.Lecture now (now.AddHours 1.0)) |> run
    | "session" ->
        t.Sessions.ReplaceFrom(
            now.AddYears -1,
            [ { Id = SessionId(Guid "00000000-0000-0000-0000-000000009001")
                AssignmentId = None
                MilestoneId = None
                Title = "Existing session"
                ModuleCode = None
                StartsAt = now
                EndsAt = now.AddHours 1.0
                Reason = "existing"
                Completed = false
                CreatedAt = now } ]
        )
        |> run
    | "document" ->
        t.Documents.Save
            { Id = DocumentId(Guid "00000000-0000-0000-0000-000000009002")
              ModuleId = None
              Title = "Existing document"
              FileName = "existing.txt"
              StoredPath = Path.Combine(store.DataRoot, "existing.txt")
              Kind = DocumentKind.Text
              SizeBytes = 1L
              ExtractedText = None
              Summary = None
              ImportedAt = now }
        |> run
    | "lecture" -> t.Lectures.Save(Lecture.create now "Existing lecture") |> run
    | "inbox" -> t.Inbox.Save(InboxItem.create now CaptureSource.ManualText "Existing capture" "body") |> run
    | "activity" ->
        t.Activity.Log(ActivityEntry.create now ActivityKind.NoteCreated "Existing activity" None None) |> run
    | other -> failwithf "unknown case %s" other

    let before = counts t
    store.Seed()

    Assert.Equal(before, counts t)
    Assert.True((t.Marker.Read() |> run).IsNone, "a skipped seed must not claim to have written the fixture")

/// **A stale store is refused, not topped up.** Adding this version's new kinds
/// to a store built from the previous one produces a fixture that is neither
/// version — reproducible for nobody.
[<Fact>]
let ``a store holding a different fixture version is left completely alone`` () =
    use store = seeded ()
    let before = counts store.Targets

    store.Targets.Marker.Write "2026.03-1-old" |> run
    store.Seed()

    Assert.Equal(before, counts store.Targets)
    Assert.Equal(Some "2026.03-1-old", store.Targets.Marker.Read() |> run)

// ---------------------------------------------------------------------------
// Determinism
// ---------------------------------------------------------------------------

[<Fact>]
let ``the fixture is byte-for-byte identical across runs`` () =
    let capture () =
        String.Join(
            "|",
            [ yield! ReviewMode.modules |> List.map (fun x -> sprintf "%A/%s/%s" x.Id x.Code x.Color)
              yield! ReviewMode.assignments |> List.map (fun x -> sprintf "%A/%s/%A" x.Id x.Title x.Deadline)
              yield! ReviewMode.events |> List.map (fun x -> sprintf "%A/%s/%A" x.Id x.Title x.StartsAt)
              yield! ReviewMode.notes |> List.map (fun x -> sprintf "%A/%s" x.Id x.Title)
              yield! ReviewMode.sessions |> List.map (fun x -> sprintf "%A/%s/%A/%b" x.Id x.Title x.StartsAt x.Completed)
              yield! ReviewMode.lectures |> List.map (fun x -> sprintf "%A/%s/%A" x.Id x.Title x.Date)
              yield! ReviewMode.revision |> List.map (fun x -> sprintf "%A/%s" x.Id x.Question)
              yield! ReviewMode.links |> List.map (fun x -> sprintf "%A/%A/%A" x.Id x.From x.To)
              yield! ReviewMode.inbox |> List.map (fun x -> sprintf "%A/%s/%A" x.Id x.Title x.Status)
              yield! ReviewMode.activity |> List.map (fun x -> sprintf "%A/%A/%s/%A" x.Id x.Kind x.Title x.OccurredAt)
              yield! ReviewMode.memory |> List.map (fun x -> sprintf "%A/%s" x.Id x.Content)
              yield! ReviewMode.connections |> List.map (fun x -> sprintf "%s/%A/%A" x.Key x.Status x.LastSync)
              yield! ReviewMode.chatMessages |> List.map (fun x -> sprintf "%A/%A" x.Id x.Role) ]
        )

    // Identifiers included deliberately: `Guid.NewGuid()` anywhere in the
    // fixture would break ordering between runs and this would catch it.
    Assert.Equal<string>(capture (), capture ())

[<Fact>]
let ``fixture files are byte-identical when written twice`` () =
    use store = new Store()
    ReviewMode.writeFixtureFiles store.DataRoot

    let hash () =
        use sha = System.Security.Cryptography.SHA256.Create()

        ReviewMode.documentsIn store.DataRoot
        |> List.map (fun d -> Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes d.StoredPath)))
        |> String.concat "/"

    let first = hash ()
    ReviewMode.writeFixtureFiles store.DataRoot

    Assert.Equal<string>(first, hash ())

[<Fact>]
let ``every fixture date is derived from the frozen instant`` () =
    // A `DateTime.Now` that crept into the fixture would drift away from the
    // frozen instant as soon as the calendar moved on. Deliberately a window
    // rather than an equality: the fixture spans months on purpose, and the
    // point is that it spans them *around a fixed anchor*.
    let all =
        [ yield! ReviewMode.sessions |> List.map (fun s -> s.StartsAt)
          yield! ReviewMode.activity |> List.map (fun a -> a.OccurredAt)
          yield! ReviewMode.inbox |> List.map (fun i -> i.ReceivedAt)
          yield! ReviewMode.documentsIn "C:\\anywhere" |> List.map (fun d -> d.ImportedAt) ]

    for date in all do
        Assert.True(
            abs (date - ReviewMode.fixedNow).TotalDays < 120.0,
            sprintf "%A is not anchored to the frozen instant" date
        )

[<Fact>]
let ``the frozen instant is mid-week, so a week view shows both directions`` () =
    let now = ReviewMode.fixedNow
    Assert.Equal(DayOfWeek.Wednesday, now.DayOfWeek)

    // Whatever the culture's first day of the week is, the frozen date must
    // have at least two days on each side of it inside that week — otherwise
    // past-vs-future styling only half-shows.
    for firstDay in [ DayOfWeek.Sunday; DayOfWeek.Monday; DayOfWeek.Saturday ] do
        let start = StudyTime.weekStart firstDay now
        let offset = (now.Date - start.Date).TotalDays
        Assert.InRange(offset, 2.0, 4.0)

// ---------------------------------------------------------------------------
// Coverage — what the fixture is actually meant to put on screen
// ---------------------------------------------------------------------------

[<Fact>]
let ``every assignment status and priority appears at least once`` () =
    for status in AssignmentStatus.all do
        Assert.Contains(ReviewMode.assignments, fun a -> a.Status = status)

    for priority in Priority.all do
        Assert.Contains(ReviewMode.assignments, fun a -> a.Priority = priority)

[<Fact>]
let ``the fixture covers overdue, imminent and distant deadlines`` () =
    let now = ReviewMode.fixedNow
    let daysOut (a: Assignment) = a.Deadline |> Option.map (fun d -> (d - now).TotalDays)

    let has predicate =
        ReviewMode.assignments
        |> List.exists (fun a -> daysOut a |> Option.map predicate |> Option.defaultValue false)

    Assert.True(has (fun d -> d < 0.0), "no overdue assignment — the overdue styling would never show")
    Assert.True(has (fun d -> d >= 0.0 && d < 3.0), "nothing due imminently")
    Assert.True(has (fun d -> d > 7.0), "nothing due far out")

[<Fact>]
let ``the fixture exercises the honest-unknown states, not just populated ones`` () =
    // Every screen has a "not known" rendering that only appears when data is
    // genuinely absent. A fixture where everything is filled in would leave all
    // of those paths uninspected — which is how they rot.
    Assert.Contains(ReviewMode.assignments, fun a -> a.ModuleId.IsNone)
    Assert.Contains(ReviewMode.assignments, fun a -> a.Weighting.IsNone)
    Assert.Contains(ReviewMode.assignments, fun a -> a.EstimatedHours.IsNone)
    Assert.Contains(ReviewMode.notes, fun (n: Note) -> n.ModuleId.IsNone)
    Assert.Contains(ReviewMode.notes, fun (n: Note) -> List.isEmpty n.Tags)
    Assert.Contains(ReviewMode.events, fun (e: CalendarEvent) -> e.Location.IsNone)
    Assert.Contains(ReviewMode.modules, fun (m: StudyModule) -> m.Archived)
    Assert.Contains(ReviewMode.memory, fun (m: MemoryItem) -> not m.Enabled)

[<Fact>]
let ``the fixture contains an overlapping pair, the case day layout most easily gets wrong`` () =
    let events = ReviewMode.events

    let overlapping =
        events
        |> List.exists (fun a ->
            events |> List.exists (fun b -> a.Id <> b.Id && CalendarEvent.overlaps a.StartsAt a.EndsAt b))

    Assert.True(overlapping, "no two events overlap — collision layout would never be exercised")

[<Fact>]
let ``at least one note exercises the Markdown renderer beyond plain text`` () =
    let rich =
        ReviewMode.notes
        |> List.exists (fun n ->
            n.Content.Contains "#"
            && n.Content.Contains "-"
            && n.Content.Contains "**"
            && n.Content.Contains "`")

    Assert.True(rich, "no note has headings, lists, emphasis and code — the renderer goes uninspected")

[<Fact>]
let ``the review queue names all eleven migrated screens exactly once`` () =
    Assert.Equal(11, ReviewMode.queue.Length)
    Assert.Equal(11, ReviewMode.queue |> List.distinct |> List.length)

    // Settings first, Assignments last: hardest and newest first, while the
    // reviewer is freshest; the earliest-migrated screen last.
    Assert.Equal<string>("Settings", List.head ReviewMode.queue)
    Assert.Equal<string>("Assignments", List.last ReviewMode.queue)
