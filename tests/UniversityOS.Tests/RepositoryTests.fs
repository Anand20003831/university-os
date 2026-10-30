module UniversityOS.Tests.RepositoryTests

open System
open System.IO
open Microsoft.Data.Sqlite
open Xunit
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.Infrastructure
open UniversityOS.Infrastructure.Repositories

/// Round-trip tests against a real (temp-file) SQLite database — the same code
/// path the app uses, migrations included.

type TempDb() =
    let path =
        Path.Combine(Path.GetTempPath(), sprintf "universityos-test-%s.db" (Guid.NewGuid().ToString "N"))

    let db = Database(path)
    do Migrations.apply db |> Async.RunSynchronously

    member _.Db = db

    interface IDisposable with
        member _.Dispose() =
            // Pooled connections keep the file locked until pools are cleared.
            SqliteConnection.ClearAllPools()

            try
                if File.Exists path then File.Delete path
            with _ ->
                ()

let private run = Async.RunSynchronously

let private now = DateTime(2026, 7, 20, 12, 0, 0)

[<Fact>]
let ``modules round-trip and delete`` () =
    use fixture = new TempDb()
    let repo = ModuleRepository(fixture.Db) :> IModuleRepository

    let m = { StudyModule.create now "cs2004" "Computer Graphics" 0 with Lecturer = Some "Dr Smith" }
    run (repo.Save m)

    let loaded = run (repo.GetById m.Id)
    Assert.Equal(Some m.Code, loaded |> Option.map (fun x -> x.Code))
    Assert.Equal(Some(Some "Dr Smith"), loaded |> Option.map (fun x -> x.Lecturer))
    Assert.Equal("CS2004", (Option.get loaded).Code) // normalized to upper case

    run (repo.Delete m.Id)
    Assert.Equal(None, run (repo.GetById m.Id))

[<Fact>]
let ``assignments round-trip with checklist`` () =
    use fixture = new TempDb()
    let repo = AssignmentRepository(fixture.Db) :> IAssignmentRepository

    let assignment =
        { Assignment.create now "Coursework 2" with
            Deadline = Some(now.AddDays 10.0)
            Priority = Priority.High
            EstimatedHours = Some 12.0
            Progress = 25
            Checklist =
                [ { Id = ChecklistItemId.New(); Text = "Read brief"; Done = true; Position = 0 }
                  { Id = ChecklistItemId.New(); Text = "Write report"; Done = false; Position = 1 } ] }

    run (repo.Save assignment)

    let loaded = Option.get (run (repo.GetById assignment.Id))
    Assert.Equal(2, loaded.Checklist.Length)
    Assert.Equal("Read brief", loaded.Checklist.[0].Text)
    Assert.True loaded.Checklist.[0].Done
    Assert.Equal(Priority.High, loaded.Priority)
    Assert.Equal(Some(now.AddDays 10.0), loaded.Deadline)

    // Saving again with a shorter checklist replaces, not duplicates.
    run (repo.Save { loaded with Checklist = loaded.Checklist |> List.take 1 })
    let reloaded = Option.get (run (repo.GetById assignment.Id))
    Assert.Equal(1, reloaded.Checklist.Length)

[<Fact>]
let ``open assignments exclude submitted and graded`` () =
    use fixture = new TempDb()
    let repo = AssignmentRepository(fixture.Db) :> IAssignmentRepository

    run (repo.Save(Assignment.create now "Open one"))
    run (repo.Save { Assignment.create now "Done one" with Status = AssignmentStatus.Submitted })

    let openOnes = run (repo.GetOpen())
    Assert.Single openOnes |> ignore
    Assert.Equal("Open one", openOnes.Head.Title)

[<Fact>]
let ``events can be found by connector external id`` () =
    use fixture = new TempDb()
    let repo = EventRepository(fixture.Db) :> IEventRepository

    let event =
        { CalendarEvent.create now "Moodle deadline" EventKind.Deadline (now.AddDays 3.0) (now.AddDays 3.0) with
            Source = Some "moodle"
            ExternalId = Some "12345" }

    run (repo.Save event)

    let found = run (repo.GetByExternalId("moodle", "12345"))
    Assert.Equal(Some event.Id, found |> Option.map (fun e -> e.Id))
    Assert.Equal(None, run (repo.GetByExternalId("moodle", "99999")))

[<Fact>]
let ``planned sessions ReplaceFrom keeps completed history`` () =
    use fixture = new TempDb()
    let repo = SessionRepository(fixture.Db) :> ISessionRepository

    let session start completed =
        { Id = SessionId.New()
          AssignmentId = None
          MilestoneId = None
          Title = "Study"
          ModuleCode = None
          StartsAt = start
          EndsAt = start + TimeSpan.FromMinutes 50.0
          Reason = "test"
          Completed = completed
          CreatedAt = now }

    let doneSession = session (now.AddHours 1.0) true
    let pendingSession = session (now.AddHours 3.0) false
    run (repo.ReplaceFrom(now, [ doneSession; pendingSession ]))

    // Mark one complete, then replace the future — the completed one must survive.
    run (repo.SetCompleted(doneSession.Id, true))
    let replacement = session (now.AddHours 5.0) false
    run (repo.ReplaceFrom(now, [ replacement ]))

    let all = run (repo.GetBetween(now.AddDays -1.0, now.AddDays 1.0))
    let ids = all |> List.map (fun s -> s.Id) |> Set.ofList

    Assert.Contains(doneSession.Id, ids)
    Assert.Contains(replacement.Id, ids)
    Assert.DoesNotContain(pendingSession.Id, ids)

[<Fact>]
let ``full-text search finds notes by content`` () =
    use fixture = new TempDb()
    let notes = NoteRepository(fixture.Db) :> INoteRepository
    let search = SearchRepository(fixture.Db) :> ISearchRepository

    run (notes.Save { Note.create now "Graphics revision" with Content = "Rasterization converts vectors to pixels." })
    run (notes.Save { Note.create now "Unrelated" with Content = "Shopping list: milk, eggs." })

    let hits = run (search.Query("rasterization", 10))
    Assert.Single hits |> ignore
    Assert.Equal(EntityKind.Note, hits.Head.Kind)

    // Deleting the note removes it from the index.
    let all = run (notes.GetAll())
    let target = all |> List.find (fun n -> n.Title = "Graphics revision")
    run (notes.Delete target.Id)
    Assert.Empty(run (search.Query("rasterization", 10)))

[<Fact>]
let ``prefix search works for search-as-you-type`` () =
    use fixture = new TempDb()
    let notes = NoteRepository(fixture.Db) :> INoteRepository
    let search = SearchRepository(fixture.Db) :> ISearchRepository

    run (notes.Save { Note.create now "Databases" with Content = "Normalization and indexing strategies." })

    Assert.NotEmpty(run (search.Query("normali", 10)))

[<Fact>]
let ``settings persist as typed records`` () =
    use fixture = new TempDb()
    let store = SettingsStore(fixture.Db) :> ISettingsStore

    // Defaults come back when nothing is saved.
    Assert.Equal(UserSettings.defaults, run (store.Load()))

    let custom =
        { UserSettings.defaults with
            UserName = "Alex"
            WorkdayStartHour = 8
            AiAutoApply = true
            Theme = ThemeMode.Light }

    run (store.Save custom)
    Assert.Equal(custom, run (store.Load()))

[<Fact>]
let ``activity log round-trips and honors range queries`` () =
    use fixture = new TempDb()
    let log = ActivityRepository(fixture.Db) :> IActivityLog

    let entry =
        ActivityEntry.create
            now
            ActivityKind.NoteCreated
            "Wrote a note"
            (Some "detail")
            (Some { Kind = EntityKind.Note; Id = Guid.NewGuid() })

    run (log.Log entry)
    run (log.Log(ActivityEntry.create (now.AddDays -3.0) ActivityKind.PlanGenerated "Old plan" None None))

    let recent = run (log.GetRecent 10)
    Assert.Equal(2, recent.Length)
    Assert.Equal("Wrote a note", recent.Head.Title) // newest first
    Assert.Equal(ActivityKind.NoteCreated, recent.Head.Kind)
    Assert.Equal(Some "detail", recent.Head.Detail)
    Assert.Equal(entry.Entity, recent.Head.Entity)

    let lastDay = run (log.GetBetween(now.AddDays -1.0, now.AddDays 1.0))
    Assert.Single lastDay |> ignore
    Assert.Equal("Wrote a note", lastDay.Head.Title)

[<Fact>]
let ``ui state round-trips separately from user settings`` () =
    use fixture = new TempDb()
    let store = UiStateStore(fixture.Db) :> IUiStateStore

    // Defaults come back when nothing is saved.
    Assert.Equal(UiState.defaults, run (store.Load()))

    let custom =
        { SidebarDensity = SidebarDensity.Compact
          SidebarPinned = false
          LastBriefingDate = Some "2026-07-20"
          LastReviewDate = None
          AiMemoryEnabled = false }

    run (store.Save custom)
    Assert.Equal(custom, run (store.Load()))

    // The user_settings row is untouched by UiState writes (§9.2-B rule).
    let settings = SettingsStore(fixture.Db) :> ISettingsStore
    Assert.Equal(UserSettings.defaults, run (settings.Load()))

[<Fact>]
let ``ui state written by an older build keeps the fields it does carry`` () =
    // The whole-record deserializer throws on a missing field and the caller
    // then falls back to defaults — which silently discarded the user's last
    // briefing date and made the morning briefing reappear. Field-level reading
    // keeps everything an older row does carry.
    let legacy =
        """{"SidebarMode":{"Case":"Compact"},"CollapsedGroups":["System"],"LastBriefingDate":"2026-07-20","AiMemoryEnabled":false}"""

    let loaded = UiStateJson.decode legacy

    Assert.Equal(Some "2026-07-20", loaded.LastBriefingDate)
    Assert.False loaded.AiMemoryEnabled

    // Fields the old shape never had take their documented defaults.
    Assert.Equal(UiState.defaults.SidebarDensity, loaded.SidebarDensity)
    Assert.Equal(UiState.defaults.SidebarPinned, loaded.SidebarPinned)

[<Fact>]
let ``ui state survives corrupt or foreign json without throwing`` () =
    for payload in [ ""; "not json"; "[]"; "null"; "42"; """{"SidebarDensity":7}""" ] do
        let loaded = UiStateJson.decode payload
        Assert.Equal(UiState.defaults.SidebarDensity, loaded.SidebarDensity)
        Assert.Equal(UiState.defaults.AiMemoryEnabled, loaded.AiMemoryEnabled)

[<Fact>]
let ``every sidebar density round-trips`` () =
    for density in [ SidebarDensity.Expanded; SidebarDensity.Compact ] do
        for pinned in [ true; false ] do
            let state =
                { UiState.defaults with SidebarDensity = density; SidebarPinned = pinned }

            let loaded = UiStateJson.decode (UiStateJson.encode state)
            Assert.Equal(density, loaded.SidebarDensity)
            Assert.Equal(pinned, loaded.SidebarPinned)

[<Fact>]
let ``inbox external-id dedup check works`` () =
    use fixture = new TempDb()
    let inbox = InboxRepository(fixture.Db) :> IInboxRepository

    let item =
        { InboxItem.create now (CaptureSource.Connector "gmail") "Email subject" "body" with
            ExternalId = Some "msg-1" }

    run (inbox.Save item)

    Assert.True(run (inbox.ExistsExternal("connector:gmail", "msg-1")))
    Assert.False(run (inbox.ExistsExternal("connector:gmail", "msg-2")))
