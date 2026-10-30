module UniversityOS.App.Features.LecturesView

open System
open System.Diagnostics
open Elmish
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Controls

/// Lectures: what learning resources do I have, and what should I learn from
/// them?
///
/// The redesign's page is a drop banner, a row of analytics, the AI processing
/// pipeline, two rows of chips, and a grid of large lecture cards. That is the
/// structure here. Every decision lives in `LecturesLogic`; this file draws what
/// that module resolves.
///
/// This **replaces** the legacy screen, which was a `2*,16,3*` master-detail
/// split with a list on the left and a permanent detail pane on the right. The
/// full CRUD it had is preserved — as a focused editing mode that takes the
/// page, plus a delete confirmation the legacy screen never had.

// ---------------------------------------------------------------------------
// model
// ---------------------------------------------------------------------------

type Draft =
    { Original: Lecture option
      Title: string
      DateText: string
      /// 0 = no module, else Modules.[i-1].
      ModuleIndex: int
      Transcript: string
      FormError: string option }

type Model =
    { Facts: LecturesLogic.Facts list
      Modules: StudyModule list
      /// Chips are toggles: several can be on at once.
      Filters: LecturesLogic.Selection
      /// Module chips, ORed among themselves and ANDed with the chips above.
      ModuleFilter: Set<Guid>
      /// The one expanded card. Only ever one.
      Expanded: LectureId option
      /// The chosen tab per lecture, keyed by its id.
      Tabs: Map<Guid, LecturesLogic.Tab>
      Draft: Draft option
      PendingDelete: bool
      Attaching: LectureId option
      /// Which AI action is in flight, and on which lecture.
      AiBusy: (LectureId * LecturesLogic.AiAction) option
      Notice: string option
      Saving: bool
      Loading: bool
      Error: string option
      Width: float }

type External = Goto of Page

type Msg =
    | Load
    | Loaded of LecturesLogic.Facts list * StudyModule list
    | Failed of exn
    | ToggleFilter of LecturesLogic.Filter
    | ToggleModule of ModuleId
    | ToggleExpanded of LectureId
    | SetTab of LectureId * LecturesLogic.Tab
    | GotoPage of Page
    | Resized of float
    | ClearNotice
    | OpenDocument of DocumentId
    // ---- attachments ----
    | AttachSlides of Lecture
    | AttachRecording of Lecture
    | AttachDone of Result<unit, string>
    // ---- ai ----
    | RunAi of LecturesLogic.AiAction * LecturesLogic.Facts
    | AiDone of Result<string, string>
    // ---- editor ----
    | NewLecture
    | EditLecture of Lecture
    | CloseDraft
    | SetTitle of string
    | SetDateText of string
    | SetModuleIndex of int
    | SetTranscript of string
    | Save
    | Saved
    | RequestDelete
    | CancelDelete
    | ConfirmDelete
    | Deleted

// ---------------------------------------------------------------------------
// loading
// ---------------------------------------------------------------------------

/// One command, one snapshot.
///
/// Every panel on this page derives from the same moment, so the reads are
/// batched. The link table is fetched **once** and filtered per lecture rather
/// than queried per card, because a page of twenty lectures would otherwise be
/// twenty round trips for the same answer.
let private loadCmd (services: AppServices) =
    Elm.ofAsync
        (async {
            let now = services.Clock.Now
            let! lectures = services.Lectures.GetAll()
            let! modules = services.Modules.GetAll()
            let! documents = services.Documents.GetAll()
            let! notes = services.Notes.GetAll()
            let! revision = services.Revision.GetAll()
            let! links = services.Links.GetAll()
            let! assignments = services.Assignments.GetAll()
            let! entries = services.Activity.GetRecent 300
            let! settings = services.Settings.Load()

            let horizonDays = float (Planning.PlannerPreferences.ofSettings settings).HorizonDays
            let! sessions = services.Sessions.GetBetween(now, now.AddDays(horizonDays + 1.0))

            let documentById = documents |> List.map (fun d -> d.Id, d) |> Map.ofList
            let noteById = notes |> List.map (fun n -> n.Id.Value, n) |> Map.ofList

            let facts =
                lectures
                |> List.map (fun l ->
                    let self = { Kind = EntityKind.Lecture; Id = l.Id.Value }

                    let otherEnds =
                        links |> List.choose (Link.otherEnd self) |> List.distinct

                    let linkedNotes =
                        otherEnds
                        |> List.filter (fun e -> e.Kind = EntityKind.Note)
                        |> List.choose (fun e -> noteById.TryFind e.Id)

                    let linkedDocuments =
                        otherEnds
                        |> List.filter (fun e -> e.Kind = EntityKind.Document)
                        |> List.choose (fun e ->
                            documents |> List.tryFind (fun d -> d.Id.Value = e.Id))
                        // The slides and the recording are already shown as
                        // attachments; listing them again as "linked" would
                        // double-count them in the asset chip.
                        |> List.filter (fun d ->
                            Some d.Id <> l.SlidesDocumentId && Some d.Id <> l.RecordingDocumentId)

                    let owningModule =
                        l.ModuleId |> Option.bind (fun id -> modules |> List.tryFind (fun m -> m.Id = id))

                    { LecturesLogic.Lecture = l
                      LecturesLogic.Module = owningModule
                      LecturesLogic.Slides = l.SlidesDocumentId |> Option.bind documentById.TryFind
                      LecturesLogic.Recording = l.RecordingDocumentId |> Option.bind documentById.TryFind
                      LecturesLogic.Flashcards =
                        revision |> List.filter (fun r -> r.Source = Some self)
                      LecturesLogic.LinkedNotes = linkedNotes
                      LecturesLogic.LinkedDocuments = linkedDocuments
                      LecturesLogic.ModuleAssignments =
                        match l.ModuleId with
                        | Some id -> assignments |> List.filter (fun a -> a.ModuleId = Some id)
                        | None -> []
                      LecturesLogic.Sessions =
                        match owningModule with
                        | Some m -> sessions |> List.filter (fun s -> s.ModuleCode = Some m.Code && not s.Completed)
                        | None -> []
                      LecturesLogic.Activity =
                        entries |> List.filter (fun e -> e.Entity = Some self) })

            return facts, modules
        })
        Loaded
        Failed

let init (services: AppServices) : Model * Cmd<Msg> =
    { Facts = []
      Modules = []
      Filters = LecturesLogic.Selection.empty
      ModuleFilter = Set.empty
      Expanded = None
      Tabs = Map.empty
      Draft = None
      PendingDelete = false
      Attaching = None
      AiBusy = None
      Notice = None
      Saving = false
      Loading = true
      Error = None
      Width = 1400.0 },
    loadCmd services

// ---------------------------------------------------------------------------
// editor plumbing — behaviour preserved from the legacy screen
// ---------------------------------------------------------------------------

let private draftOf (modules: StudyModule list) (l: Lecture) : Draft =
    { Original = Some l
      Title = l.Title
      DateText = l.Date |> Option.map Format.dateTimeInput |> Option.defaultValue ""
      ModuleIndex =
        match l.ModuleId with
        | Some mid ->
            match modules |> List.tryFindIndex (fun m -> m.Id = mid) with
            | Some i -> i + 1
            | None -> 0
        | None -> 0
      Transcript = l.Transcript |> Option.defaultValue ""
      FormError = None }

let private emptyDraft: Draft =
    { Original = None
      Title = ""
      DateText = ""
      ModuleIndex = 0
      Transcript = ""
      FormError = None }

let private buildLecture (now: DateTime) (modules: StudyModule list) (draft: Draft) : Result<Lecture, string> =
    if String.IsNullOrWhiteSpace draft.Title then
        Error "A lecture needs a title."
    else
        let dateResult =
            let text = draft.DateText.Trim()

            if text.Length = 0 then
                Ok None
            else
                match Format.parseDateTimeInput text with
                | Some d -> Ok(Some d)
                | None -> Error "Couldn't read the date — try 2026-03-14 or 2026-03-14 09:00."

        match dateResult with
        | Error message -> Error message
        | Ok date ->
            let baseRecord =
                match draft.Original with
                | Some original -> original
                | None -> Lecture.create now draft.Title

            Ok
                { baseRecord with
                    Title = draft.Title.Trim()
                    ModuleId =
                        (if draft.ModuleIndex > 0 && draft.ModuleIndex <= modules.Length then
                             Some modules.[draft.ModuleIndex - 1].Id
                         else
                             None)
                    Date = date
                    Transcript = (if String.IsNullOrWhiteSpace draft.Transcript then None else Some draft.Transcript)
                    UpdatedAt = now }

// ---------------------------------------------------------------------------
// update
// ---------------------------------------------------------------------------

let update (services: AppServices) (msg: Msg) (model: Model) : Model * Cmd<Msg> * External option =
    let withDraft f =
        match model.Draft with
        | Some d -> { model with Draft = Some(f d) }
        | None -> model

    let plain m = m, Cmd.none, None

    /// Pick one file, import it into the managed store filed under the
    /// lecture's module, and record the resulting document id on the lecture.
    let attach (lecture: Lecture) (setDocument: Lecture -> DocumentId -> Lecture) =
        { model with Attaching = Some lecture.Id },
        Elm.ofAsync
            (async {
                let! paths = services.PickFiles()

                match paths with
                | [] -> return Ok()
                | path :: _ ->
                    let! result = services.Import.ImportFile(path, lecture.ModuleId)

                    match result with
                    | Ok document ->
                        let updated =
                            { setDocument lecture document.Id with UpdatedAt = services.Clock.Now }

                        do! services.Lectures.Save updated
                        return Ok()
                    | Error message -> return Error message
            })
            AttachDone
            (fun ex -> AttachDone(Error ex.Message)),
        None

    match msg with
    | Load -> { model with Loading = true }, loadCmd services, None

    | Loaded(facts, modules) ->
        // An expansion whose lecture has gone is dropped rather than left
        // dangling, and so is a module chip whose module has gone.
        let expanded =
            model.Expanded
            |> Option.filter (fun id -> facts |> List.exists (fun f -> f.Lecture.Id = id))

        let moduleFilter =
            model.ModuleFilter |> Set.filter (fun id -> modules |> List.exists (fun m -> m.Id.Value = id))

        plain
            { model with
                Facts = facts
                Modules = modules
                Expanded = expanded
                ModuleFilter = moduleFilter
                Loading = false
                Error = None }

    | Failed ex ->
        plain
            { model with
                Loading = false
                Saving = false
                PendingDelete = false
                Attaching = None
                AiBusy = None
                Error = Some ex.Message }

    | ToggleFilter filter ->
        plain { model with Filters = LecturesLogic.Selection.toggle filter model.Filters }

    | ToggleModule id ->
        let next =
            if model.ModuleFilter.Contains id.Value then model.ModuleFilter.Remove id.Value
            else model.ModuleFilter.Add id.Value

        plain { model with ModuleFilter = next }

    | ToggleExpanded id ->
        plain { model with Expanded = (if model.Expanded = Some id then None else Some id) }

    | SetTab(id, tab) -> plain { model with Tabs = model.Tabs.Add(id.Value, tab) }

    | GotoPage page -> model, Cmd.none, Some(Goto page)
    | Resized width -> plain { model with Width = width }
    | ClearNotice -> plain { model with Notice = None }

    | OpenDocument id ->
        let document =
            model.Facts
            |> List.tryPick (fun f ->
                [ f.Slides; f.Recording ] @ (f.LinkedDocuments |> List.map Some)
                |> List.tryPick (Option.filter (fun d -> d.Id = id)))

        match document with
        | Some d ->
            try
                Process.Start(ProcessStartInfo(FileName = d.StoredPath, UseShellExecute = true)) |> ignore
                plain model
            with ex ->
                { model with Notice = Some("Could not open the file: " + ex.Message) },
                Elm.delayed 8000 ClearNotice,
                None
        | None -> plain model

    // ---- attachments ----
    | AttachSlides _ when model.Attaching.IsSome -> plain model
    | AttachSlides lecture -> attach lecture (fun l id -> { l with SlidesDocumentId = Some id })

    | AttachRecording _ when model.Attaching.IsSome -> plain model
    | AttachRecording lecture -> attach lecture (fun l id -> { l with RecordingDocumentId = Some id })

    | AttachDone(Ok()) -> { model with Attaching = None }, loadCmd services, None
    | AttachDone(Error message) ->
        { model with Attaching = None; Notice = Some("Import failed: " + message) },
        Elm.delayed 8000 ClearNotice,
        None

    // ---- ai ----
    | RunAi _ when model.AiBusy.IsSome -> plain model
    | RunAi(action, f) ->
        // The button is disabled without usable text, so this guard is the
        // second line of defence rather than the first — but a model asked to
        // summarise nothing invents, so it is worth having twice.
        match LecturesLogic.sourceText f, LecturesLogic.hasUsableSource f with
        | Some text, true ->
            let lecture = f.Lecture

            { model with AiBusy = Some(lecture.Id, action) },
            Elm.ofAsync
                (async {
                    match action with
                    | LecturesLogic.AiAction.Summarise ->
                        let! result = services.Ai.Summarize(lecture.Title, text)

                        match result with
                        | Ok summary ->
                            do! services.Lectures.Save
                                    { lecture with Summary = Some summary; UpdatedAt = services.Clock.Now }

                            return Ok "Summary written."
                        | Error e -> return Error(AiError.describe e)

                    | LecturesLogic.AiAction.GenerateFlashcards ->
                        let! result =
                            services.Ai.GenerateFlashcards(
                                lecture.Title,
                                text,
                                lecture.ModuleId,
                                Some { Kind = EntityKind.Lecture; Id = lecture.Id.Value }
                            )

                        match result with
                        | Ok cards when cards.IsEmpty ->
                            return Error "The model returned no cards for this lecture."
                        | Ok cards ->
                            do! services.Revision.SaveMany cards

                            do! services.Activity.Log(
                                    ActivityEntry.create
                                        services.Clock.Now
                                        ActivityKind.FlashcardsGenerated
                                        lecture.Title
                                        (Some(sprintf "%d cards" cards.Length))
                                        (Some { Kind = EntityKind.Lecture; Id = lecture.Id.Value })
                                )

                            return Ok(sprintf "%d flashcards generated." cards.Length)
                        | Error e -> return Error(AiError.describe e)
                    })
                AiDone
                (fun ex -> AiDone(Error ex.Message)),
            None
        | _ -> plain model

    | AiDone(Ok notice) ->
        { model with AiBusy = None; Notice = Some notice },
        Cmd.batch [ loadCmd services; Elm.delayed 6000 ClearNotice ],
        None
    | AiDone(Error message) ->
        { model with AiBusy = None; Notice = Some message }, Elm.delayed 8000 ClearNotice, None

    // ---- editor ----
    | NewLecture -> plain { model with Draft = Some emptyDraft; PendingDelete = false }
    | EditLecture l -> plain { model with Draft = Some(draftOf model.Modules l); PendingDelete = false }
    | CloseDraft -> plain { model with Draft = None; PendingDelete = false }

    | SetTitle v -> plain (withDraft (fun d -> { d with Title = v }))
    | SetDateText v -> plain (withDraft (fun d -> { d with DateText = v }))
    | SetModuleIndex i -> plain (withDraft (fun d -> { d with ModuleIndex = i }))
    | SetTranscript v -> plain (withDraft (fun d -> { d with Transcript = v }))

    // The re-entry guard: a second Save while one is running is dropped.
    | Save when model.Saving -> plain model
    | Save ->
        match model.Draft with
        | None -> plain model
        | Some draft ->
            match buildLecture services.Clock.Now model.Modules draft with
            | Error message -> plain { model with Draft = Some { draft with FormError = Some message } }
            | Ok lecture ->
                let isNew = draft.Original.IsNone

                { model with Saving = true; Draft = None },
                Elm.ofAsync
                    (async {
                        do! services.Lectures.Save lecture

                        if isNew then
                            do! services.Activity.Log(
                                    ActivityEntry.create
                                        services.Clock.Now
                                        ActivityKind.LectureAdded
                                        lecture.Title
                                        None
                                        (Some { Kind = EntityKind.Lecture; Id = lecture.Id.Value })
                                )
                    })
                    (fun _ -> Saved)
                    Failed,
                None

    | Saved -> { model with Saving = false }, loadCmd services, None

    // Deleting a lecture is irreversible, so it takes an explicit confirmation.
    | RequestDelete -> plain { model with PendingDelete = true }
    | CancelDelete -> plain { model with PendingDelete = false }
    | ConfirmDelete when model.Saving -> plain model
    | ConfirmDelete ->
        match model.Draft |> Option.bind (fun d -> d.Original) with
        | Some original ->
            { model with Saving = true; PendingDelete = false; Draft = None },
            Elm.ofAsync (services.Lectures.Delete original.Id) (fun _ -> Deleted) Failed,
            None
        | None -> plain { model with Draft = None; PendingDelete = false }

    | Deleted -> { model with Saving = false }, loadCmd services, None

// ---------------------------------------------------------------------------
// view — shared treatments
// ---------------------------------------------------------------------------

let private contentHighlightScale = LecturesLogic.Geometry.contentHighlight

let private weight (w: int) = enum<FontWeight> w

/// **The one card material on this screen.** Lecture cards, analytics cards, the
/// pipeline panel and the editor all draw through this: one surface, one
/// highlight behaviour, one border.
let private cardWith
    (tokens: Tokens)
    (padding: float)
    (radius: float)
    (tint: Color)
    (align: VerticalAlignment)
    (children: IView list)
    : IView =
    GlassPanel.create
        [ GlassPanel.baseRadius radius
          GlassPanel.elevation 1
          GlassPanel.highlightScale contentHighlightScale
          GlassPanel.tint tint
          GlassPanel.child (
              Border.create
                  [ Border.padding (Tokens.space tokens padding)
                    Border.verticalAlignment VerticalAlignment.Stretch
                    Border.child (
                        StackPanel.create
                            [ StackPanel.spacing (Tokens.space tokens 10.0)
                              StackPanel.verticalAlignment align
                              // Never Left: a StackPanel that is not stretched
                              // sizes to its widest child, which silently
                              // shrinks every divider and right-aligned value
                              // inside it.
                              StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                              StackPanel.children children ]
                    ) ] ) ]
    :> IView

let private plainCard (tokens: Tokens) (padding: float) (children: IView list) =
    cardWith tokens padding DashboardLogic.Card.radius (Theme.surfaces ()).Card VerticalAlignment.Top children

// ---- text roles ------------------------------------------------------------

let private sectionCaption (text: string) : IView =
    TextBlock.create
        [ TextBlock.text (text.ToUpperInvariant())
          TextBlock.fontSize Theme.fontXs
          TextBlock.fontWeight (weight 600)
          TextBlock.foreground (Theme.label ())
          TextBlock.margin (2.0, 0.0, 0.0, 0.0) ]
    :> IView

let private bulletRow (glyph: string) (glyphBrush: IBrush) (text: string) (textBrush: IBrush) : IView =
    Grid.create
        [ Grid.columnDefinitions LecturesLogic.Geometry.bulletColumns
          Grid.children
              [ TextBlock.create
                    [ Grid.column 0
                      TextBlock.text glyph
                      TextBlock.fontSize Theme.Type.rowMeta
                      TextBlock.foreground glyphBrush
                      TextBlock.verticalAlignment VerticalAlignment.Top
                      Ui.decorative () ]
                TextBlock.create
                    [ Grid.column 2
                      TextBlock.text text
                      TextBlock.fontSize Theme.Type.rowMeta
                      TextBlock.foreground textBrush
                      TextBlock.textWrapping TextWrapping.Wrap ] ] ]
    :> IView

let private chipWash (surface: Color) (hex: string) =
    Theme.compositeOver surface (Theme.withAlphaHex LecturesLogic.Geometry.chipFillAlpha hex)

/// A pill carrying an identity colour. The alpha used to PAINT the wash is the
/// same one used to MEASURE the contrast against it.
let private pill (tokens: Tokens) (surface: Color) (text: string) (hex: string) : IView =
    Border.create
        [ Border.background (Theme.brush (Theme.withAlphaHex LecturesLogic.Geometry.chipFillAlpha hex))
          Border.cornerRadius (Tokens.radius tokens 999.0)
          Border.padding (9.0, 3.0)
          Border.isHitTestVisible false
          Border.child (
              TextBlock.create
                  [ TextBlock.text text
                    TextBlock.fontSize Theme.Type.metricLabel
                    TextBlock.fontWeight FontWeight.SemiBold
                    TextBlock.foreground (Theme.brush (Theme.readableOnColor (chipWash surface hex) hex)) ]
          ) ]
    :> IView

/// An asset chip. `present` decides whether it reads as a real holding or as a
/// gap — an absent asset stays on screen, because its absence is the point.
let private assetChip (tokens: Tokens) (surface: Color) (glyph: string) (label: string) (present: bool) (hex: string) : IView =
    let fill = if present then Theme.brush (Theme.withAlphaHex LecturesLogic.Geometry.chipFillAlpha hex) else Theme.transparent

    let ink =
        if present then Theme.brush (Theme.readableOnColor (chipWash surface hex) hex) else Theme.faint ()

    Border.create
        [ Border.background fill
          Border.borderThickness LecturesLogic.Geometry.filterBorderThickness
          Border.borderBrush (if present then Theme.transparent else Theme.border ())
          Border.cornerRadius (Tokens.radius tokens LecturesLogic.Geometry.chipRadius)
          Border.padding (9.0, 4.0)
          Border.margin (0.0, 0.0, 6.0, 6.0)
          Border.isHitTestVisible false
          Border.child (
              StackPanel.create
                  [ StackPanel.orientation Orientation.Horizontal
                    StackPanel.spacing 5.0
                    StackPanel.children
                        [ TextBlock.create
                              [ TextBlock.text glyph
                                TextBlock.fontSize Theme.Type.metricLabel
                                TextBlock.foreground ink
                                Ui.decorative () ]
                          TextBlock.create
                              [ TextBlock.text label
                                TextBlock.fontSize Theme.Type.metricLabel
                                TextBlock.fontWeight FontWeight.SemiBold
                                TextBlock.foreground ink ] ] ]
          ) ]
    :> IView

/// A real Button whose visible surface is a GlassPanel, as on every migrated
/// screen. Identical geometry in every state, including disabled.
let private glassAction
    (tokens: Tokens)
    (label: string)
    (primary: bool)
    (enabled: bool)
    (accessibleName: string)
    (onClick: unit -> unit)
    : IView =
    // Geometry only. The glass, the veil, the accent floor and the ink rule all
    // live in `Ui.glassAction` — see the note there on why eleven copies of
    // this button existed and only one of them kept its accent.
    Ui.glassAction
        tokens
        { Ui.GlassActionMetrics.Padding = (14.0, 8.0)
          Ui.GlassActionMetrics.Margin = (0.0, 0.0, 8.0, 8.0) }
        label
        primary
        enabled
        accessibleName
        onClick

/// One of the redesign's small square glyph buttons.
///
/// The label is carried twice — as the accessible name and as a tooltip —
/// because a glyph on its own is invisible to a screen reader and ambiguous to
/// everyone else.
let private iconAction
    (tokens: Tokens)
    (glyph: string)
    (label: string)
    (hex: string)
    (enabled: bool)
    (onClick: unit -> unit)
    : IView =
    Button.create
        [ Button.classes [ Ui.glassActionClass () ]
          Button.isEnabled enabled
          Button.background Theme.transparent
          Button.borderThickness 0.0
          Button.padding 0.0
          Button.width LecturesLogic.Geometry.iconActionSize
          Button.height LecturesLogic.Geometry.iconActionSize
          Button.cornerRadius (Tokens.radius tokens LecturesLogic.Geometry.iconActionRadius)
          Button.clipToBounds false
          Button.margin (0.0, 0.0, 6.0, 0.0)
          Button.cursor Ui.hand
          ToolTip.tip label
          Ui.accessibleName label
          Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always)
          Button.content (
              GlassPanel.create
                  [ GlassPanel.baseRadius LecturesLogic.Geometry.iconActionRadius
                    GlassPanel.elevation 0
                    GlassPanel.borderInset LecturesLogic.Geometry.borderInset
                    GlassPanel.highlightScale contentHighlightScale
                    GlassPanel.tint (Theme.surfaces ()).Action
                    GlassPanel.child (
                        Panel.create
                            [ Panel.children
                                  [ Border.create
                                        [ Border.classes [ Ui.glassVeilClass () ]
                                          Border.background (
                                              Theme.brush (Theme.withAlphaHex "2E" (Ui.hexOf (Theme.surfaces ()).Text))
                                          )
                                          Border.cornerRadius (
                                              Tokens.radius tokens LecturesLogic.Geometry.iconActionRadius
                                          ) ]
                                    Border.create
                                        [ Border.isHitTestVisible false
                                          Border.child (
                                              TextBlock.create
                                                  [ TextBlock.text glyph
                                                    TextBlock.fontSize Theme.Type.rowMeta
                                                    TextBlock.horizontalAlignment HorizontalAlignment.Center
                                                    TextBlock.verticalAlignment VerticalAlignment.Center
                                                    Ui.decorative ()
                                                    TextBlock.foreground (
                                                        Theme.brush (
                                                            Theme.readableOnColor (Theme.surfaces ()).Action hex
                                                        )
                                                    ) ] ) ] ] ] ) ] ) ]
    :> IView

/// One logical row of cards, as ONE Grid, so a row's cards share a height.
let private cardRow (spacing: float) (minHeight: float) (columns: int) (cards: IView list) : IView =
    StackPanel.create
        [ StackPanel.spacing spacing
          StackPanel.children
              (cards
               |> List.chunkBySize (max 1 columns)
               |> List.map (fun row ->
                   Grid.create
                       [ Grid.minHeight minHeight
                         Grid.columnDefinitions (
                             List.replicate row.Length "1*" |> String.concat (sprintf ",%f," spacing)
                         )
                         Grid.children
                             (row
                              |> List.mapi (fun i c ->
                                  Border.create
                                      [ Grid.column (i * 2)
                                        Border.verticalAlignment VerticalAlignment.Stretch
                                        Border.horizontalAlignment HorizontalAlignment.Stretch
                                        Border.child c ]
                                  :> IView)) ]
                   :> IView)) ]
    :> IView

// ---------------------------------------------------------------------------
// the processing pipeline
// ---------------------------------------------------------------------------

/// One stage of the stepper: a dot, a fixed-width wrapping label beneath it, and
/// a connector to the next one.
///
/// The label gets an **explicit width** rather than relying on the panel to
/// constrain it, because a horizontal StackPanel measures its children with
/// infinite width and wrapping would silently never engage.
let private pipelineStep (label: string) (isDone: bool) (isLast: bool) (hex: string) : IView =
    let ring = if isDone then hex else Ui.hexOf (Theme.surfaces ()).Divider

    let dot: IView =
        Border.create
            [ Border.width LecturesLogic.Geometry.pipelineDotSize
              Border.height LecturesLogic.Geometry.pipelineDotSize
              Border.cornerRadius (LecturesLogic.Geometry.pipelineDotSize / 2.0)
              Border.horizontalAlignment HorizontalAlignment.Center
              Border.borderThickness 1.0
              Border.borderBrush (Theme.brush ring)
              Border.background (
                  if isDone then
                      Theme.brush (Theme.withAlphaHex LecturesLogic.Geometry.stageDotAlpha hex)
                  else
                      Theme.transparent
              )
              Ui.accessibleName (sprintf "%s: %s" label (if isDone then "done" else "not yet"))
              Border.child (
                  TextBlock.create
                      [ TextBlock.text (if isDone then "✓" else "")
                        TextBlock.fontSize 10.0
                        TextBlock.horizontalAlignment HorizontalAlignment.Center
                        TextBlock.verticalAlignment VerticalAlignment.Center
                        // Measured against the wash the tick actually sits on —
                        // the card composited with the SAME alpha the dot is
                        // painted with, not the lighter chip alpha.
                        TextBlock.foreground (
                            Theme.brush (
                                Theme.readableOnColor
                                    (Theme.compositeOver
                                        (Theme.surfaces ()).Card
                                        (Theme.withAlphaHex LecturesLogic.Geometry.stageDotAlpha hex))
                                    hex
                            )
                        )
                        Ui.decorative () ]
              ) ]
        :> IView

    let caption: IView =
        TextBlock.create
            [ TextBlock.text label
              // An explicit width, because the horizontal StackPanel below
              // measures its children with infinite width and wrapping would
              // otherwise never engage.
              TextBlock.width LecturesLogic.Geometry.pipelineStepWidth
              TextBlock.fontSize 10.0
              TextBlock.textAlignment TextAlignment.Center
              TextBlock.textWrapping TextWrapping.Wrap
              TextBlock.foreground (if isDone then Theme.muted () else Theme.faint ()) ]
        :> IView

    let connector: IView =
        Border.create
            [ Border.width LecturesLogic.Geometry.pipelineConnectorWidth
              Border.height 1.0
              Border.margin (0.0, LecturesLogic.Geometry.pipelineDotSize / 2.0, 0.0, 0.0)
              Border.verticalAlignment VerticalAlignment.Top
              Border.background (Theme.divider ())
              Ui.decorative () ]
        :> IView

    let column: IView =
        StackPanel.create
            [ StackPanel.spacing 6.0
              StackPanel.width LecturesLogic.Geometry.pipelineStepWidth
              StackPanel.children [ dot; caption ] ]
        :> IView

    StackPanel.create
        [ StackPanel.orientation Orientation.Horizontal
          StackPanel.verticalAlignment VerticalAlignment.Top
          StackPanel.children (if isLast then [ column ] else [ column; connector ]) ]
    :> IView

/// The same nine stages again, shrunk onto a card: dots only, no captions.
let private miniPipeline (f: LecturesLogic.Facts) (hex: string) : IView =
    WrapPanel.create
        [ WrapPanel.children
              (LecturesLogic.Stage.all
               |> List.map (fun stage ->
                   let isDone = LecturesLogic.stageDone f stage

                   Border.create
                       [ Border.width LecturesLogic.Geometry.miniDotSize
                         Border.height LecturesLogic.Geometry.miniDotSize
                         Border.cornerRadius (LecturesLogic.Geometry.miniDotSize / 2.0)
                         Border.margin (0.0, 0.0, 6.0, 0.0)
                         Border.borderThickness 1.0
                         Border.borderBrush (
                             Theme.brush (if isDone then hex else Ui.hexOf (Theme.surfaces ()).Divider)
                         )
                         Border.background (
                             if isDone then Theme.brush (Theme.withAlphaHex "5A" hex) else Theme.transparent
                         )
                         ToolTip.tip (
                             sprintf
                                 "%s — %s"
                                 (LecturesLogic.Stage.label stage)
                                 (if isDone then "done" else "not yet")
                         )
                         Ui.accessibleName (
                             sprintf
                                 "%s: %s"
                                 (LecturesLogic.Stage.label stage)
                                 (if isDone then "done" else "not yet")
                         ) ]
                   :> IView)) ]
    :> IView

// ---------------------------------------------------------------------------
// sections
// ---------------------------------------------------------------------------

let private header (tokens: Tokens) : IView =
    StackPanel.create
        [ StackPanel.spacing 6.0
          StackPanel.horizontalAlignment HorizontalAlignment.Left
          StackPanel.maxWidth tokens.ReadingWidth
          StackPanel.children
              [ TextBlock.create
                    [ TextBlock.text "Lectures"
                      TextBlock.fontSize Theme.Type.pageTitle
                      TextBlock.fontWeight FontWeight.Bold
                      TextBlock.foreground (Theme.text ()) ]
                TextBlock.create
                    [ TextBlock.text "What learning resources do I have, and what should I learn from them?"
                      TextBlock.fontSize Theme.Type.pageMeta
                      TextBlock.foreground (Theme.muted ())
                      TextBlock.textWrapping TextWrapping.Wrap ] ] ]
    :> IView

/// The redesign's drop banner.
///
/// Two divergences, both deliberate. The **wording** is corrected: dropped files
/// become Inbox captures, not auto-filed lectures — see
/// `LecturesLogic.dropZoneMessage`. The **border is solid, not dashed**: a
/// dashed stroke needs a Shape, and Avalonia's `Border` has no dash support, so
/// a hairline in the accent colour stands in for it.
let private dropZone (tokens: Tokens) : IView =
    Border.create
        [ Border.background (Theme.brush (Theme.accentSoftHex ()))
          Border.borderThickness 1.0
          Border.borderBrush (Theme.brush (Theme.overlayEdgeHex "55" (Theme.accentHex ())))
          Border.cornerRadius (Tokens.radius tokens LecturesLogic.Geometry.dropZoneRadius)
          Border.padding (18.0, 14.0)
          Border.child (
              Grid.create
                  [ Grid.columnDefinitions LecturesLogic.Geometry.bulletColumns
                    Grid.children
                        [ TextBlock.create
                              [ Grid.column 0
                                TextBlock.text "↓"
                                TextBlock.fontSize Theme.Type.rowTitle
                                TextBlock.verticalAlignment VerticalAlignment.Center
                                TextBlock.foreground (
                                    Theme.brush (
                                        Theme.readableOnColor
                                            (Theme.compositeOver (Theme.surfaces ()).Window (Theme.accentSoftHex ()))
                                            (Theme.accentHex ())
                                    )
                                )
                                Ui.decorative () ]
                          TextBlock.create
                              [ Grid.column 2
                                TextBlock.text LecturesLogic.dropZoneMessage
                                TextBlock.fontSize Theme.Type.rowMeta
                                TextBlock.verticalAlignment VerticalAlignment.Center
                                TextBlock.foreground (Theme.textSecondary ())
                                TextBlock.textWrapping TextWrapping.Wrap ] ] ]
          ) ]
    :> IView

let private metricCard (tokens: Tokens) (metric: LecturesLogic.Metric) : IView =
    cardWith
        tokens
        14.0
        14.0
        (Theme.surfaces ()).Card
        VerticalAlignment.Center
        [ StackPanel.create
              [ StackPanel.spacing 2.0
                StackPanel.children
                    [ TextBlock.create
                          [ TextBlock.text (metric.Value |> Option.defaultValue "—")
                            TextBlock.fontSize 17.0
                            TextBlock.fontWeight (weight 600)
                            TextBlock.foreground (Theme.text ())
                            TextBlock.textTrimming TextTrimming.CharacterEllipsis ]
                      TextBlock.create
                          [ TextBlock.text metric.Label
                            TextBlock.fontSize Theme.fontXs
                            TextBlock.foreground (Theme.muted ())
                            TextBlock.textWrapping TextWrapping.Wrap ]
                      match metric.Note with
                      | Some note ->
                          TextBlock.create
                              [ TextBlock.text note
                                TextBlock.fontSize Theme.fontXs
                                TextBlock.foreground (Theme.faint ())
                                TextBlock.textWrapping TextWrapping.Wrap ]
                      | None -> () ] ] ]

/// The redesign's nine-step stepper, over one real lecture.
///
/// On an empty screen it still renders, with every step pending and the example
/// label a dash — the page keeps its shape, and the panel doubles as an honest
/// statement of what happens to a lecture once one exists.
let private pipelinePanel (tokens: Tokens) (model: Model) : IView =
    let example = LecturesLogic.pipelineExample model.Facts

    let hex =
        example
        |> Option.bind (fun f -> f.Module)
        |> Option.map (fun m -> m.Color)
        |> Option.defaultValue (Theme.accentHex ())

    let steps =
        LecturesLogic.Stage.all
        |> List.mapi (fun i stage ->
            let isDone =
                match example with
                | Some f -> LecturesLogic.stageDone f stage
                | None -> false

            pipelineStep (LecturesLogic.Stage.label stage) isDone (i = LecturesLogic.Stage.all.Length - 1) hex)

    plainCard
        tokens
        20.0
        [ DockPanel.create
              [ DockPanel.children
                    [ TextBlock.create
                          [ DockPanel.dock Dock.Right
                            TextBlock.text (
                                match example with
                                | Some f -> sprintf "Example: %s" (Format.truncate 40 f.Lecture.Title)
                                | None -> "Example: —"
                            )
                            TextBlock.fontSize Theme.Type.metricLabel
                            TextBlock.foreground (Theme.faint ())
                            TextBlock.verticalAlignment VerticalAlignment.Center
                            TextBlock.margin (12.0, 0.0, 0.0, 0.0)
                            TextBlock.textTrimming TextTrimming.CharacterEllipsis ]
                      sectionCaption "AI processing pipeline" ] ]

          // A WrapPanel rather than the mockup's horizontal scroll: the project
          // forbids a horizontal scrollbar, so nine steps reflow onto a second
          // line instead of running off the edge.
          WrapPanel.create [ WrapPanel.children steps ] ]

let private filtersSection (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let page = (Theme.surfaces ()).Window
    let selectedFill = Theme.compositeOver page (Theme.accentSoftHex ())
    let selectedRing = Theme.brush (Theme.visibleOnColor page (Theme.accentHex ()))

    let chipFor (filter: LecturesLogic.Filter) : IView =
        let isOn = model.Filters.Contains filter
        let label = LecturesLogic.Filter.label filter

        Button.create
            [ Button.background (if isOn then Theme.accentSoft () else Theme.transparent)
              Button.borderThickness LecturesLogic.Geometry.filterBorderThickness
              Button.borderBrush (if isOn then selectedRing else Theme.border ())
              Button.foreground (
                  if isOn then Theme.brush (Theme.readableOnColor selectedFill (Theme.accentHex ()))
                  else Theme.muted ()
              )
              Button.cornerRadius (Tokens.radius tokens LecturesLogic.Geometry.filterRadius)
              Button.minHeight LecturesLogic.Geometry.filterMinHeight
              Button.padding (13.0, 4.0)
              Button.fontSize Theme.Type.metricLabel
              Button.fontWeight FontWeight.SemiBold
              Button.margin (0.0, 0.0, 8.0, 8.0)
              Button.cursor Ui.hand
              Button.content label
              Ui.accessibleName (if isOn then sprintf "%s filter, on" label else sprintf "Turn on %s" label)
              Button.onClick ((fun _ -> dispatch (ToggleFilter filter)), SubPatchOptions.Always) ]
        :> IView

    DockPanel.create
        [ DockPanel.children
              [ Border.create
                    [ DockPanel.dock Dock.Right
                      Border.margin (12.0, 0.0, 0.0, 8.0)
                      Border.verticalAlignment VerticalAlignment.Top
                      Border.child (
                          glassAction tokens "＋ New lecture" false (not model.Saving) "Create a new lecture" (fun () ->
                              dispatch NewLecture)
                      ) ]
                WrapPanel.create [ WrapPanel.children (LecturesLogic.Filter.all |> List.map chipFor) ] ] ]
    :> IView

/// The redesign's "By Module" row. Only modules that genuinely have lectures
/// appear — a chip that could never match anything would be a dead end.
let private moduleFiltersSection (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let chips = LecturesLogic.moduleChips model.Facts
    let page = (Theme.surfaces ()).Window

    let chipFor (m: StudyModule, count: int) : IView =
        let isOn = model.ModuleFilter.Contains m.Id.Value
        let wash = Theme.compositeOver page (Theme.withAlphaHex LecturesLogic.Geometry.chipFillAlpha m.Color)

        Button.create
            [ Button.background (if isOn then Theme.brush (Theme.withAlphaHex LecturesLogic.Geometry.chipFillAlpha m.Color) else Theme.transparent)
              Button.borderThickness LecturesLogic.Geometry.filterBorderThickness
              Button.borderBrush (if isOn then Theme.brush (Theme.visibleOnColor page m.Color) else Theme.border ())
              Button.foreground (if isOn then Theme.brush (Theme.readableOnColor wash m.Color) else Theme.muted ())
              Button.cornerRadius (Tokens.radius tokens LecturesLogic.Geometry.filterRadius)
              Button.minHeight LecturesLogic.Geometry.filterMinHeight
              Button.padding (13.0, 3.0)
              Button.fontSize Theme.Type.metricLabel
              Button.fontWeight FontWeight.SemiBold
              Button.margin (0.0, 0.0, 8.0, 8.0)
              Button.cursor Ui.hand
              Button.content (sprintf "%s (%d)" m.Code count)
              Ui.accessibleName (
                  if isOn then sprintf "%s filter, on" (StudyModule.displayName m)
                  else sprintf "Show only %s" (StudyModule.displayName m)
              )
              Button.onClick ((fun _ -> dispatch (ToggleModule m.Id)), SubPatchOptions.Always) ]
        :> IView

    StackPanel.create
        [ StackPanel.spacing 6.0
          StackPanel.children
              [ sectionCaption "By module"

                if chips.IsEmpty then
                    TextBlock.create
                        [ TextBlock.text "No lecture is filed under a module yet."
                          TextBlock.fontSize Theme.Type.metricLabel
                          TextBlock.foreground (Theme.faint ())
                          TextBlock.textWrapping TextWrapping.Wrap ]
                else
                    WrapPanel.create [ WrapPanel.children (chips |> List.map chipFor) ] ] ]
    :> IView

// ---------------------------------------------------------------------------
// the lecture card
// ---------------------------------------------------------------------------

let private tabContent
    (tokens: Tokens)
    (now: DateTime)
    (f: LecturesLogic.Facts)
    (tab: LecturesLogic.Tab)
    (dispatch: Msg -> unit)
    : IView =
    let row (title: string) (meta: string) : IView =
        Grid.create
            [ Grid.columnDefinitions "*,10,Auto"
              Grid.margin (0.0, 2.0)
              Grid.children
                  [ TextBlock.create
                        [ Grid.column 0
                          TextBlock.text title
                          TextBlock.fontSize Theme.Type.rowMeta
                          TextBlock.foreground (Theme.text ())
                          TextBlock.textTrimming TextTrimming.CharacterEllipsis ]
                    TextBlock.create
                        [ Grid.column 2
                          TextBlock.text meta
                          TextBlock.fontSize Theme.Type.metricLabel
                          TextBlock.foreground (Theme.faint ()) ] ] ]
        :> IView

    let paragraph (text: string) (italic: bool) : IView =
        TextBlock.create
            [ TextBlock.text text
              TextBlock.fontSize Theme.Type.rowMeta
              TextBlock.foreground (Theme.textSecondary ())
              TextBlock.textWrapping TextWrapping.Wrap
              TextBlock.fontStyle (if italic then FontStyle.Italic else FontStyle.Normal) ]
        :> IView

    let body: IView list =
        match tab with
        | LecturesLogic.Tab.Summary ->
            match f.Lecture.Summary with
            | Some summary -> [ Ui.markdown summary ]
            | None -> []

        | LecturesLogic.Tab.Transcript ->
            match f.Lecture.Transcript with
            | Some transcript -> [ paragraph (Format.truncate 4000 transcript) true ]
            | None -> []

        | LecturesLogic.Tab.Notes ->
            f.LinkedNotes |> List.map (fun n -> row n.Title (Format.relativeTime now n.UpdatedAt))

        | LecturesLogic.Tab.Assignments ->
            f.ModuleAssignments
            |> List.map (fun a ->
                row
                    a.Title
                    (match a.Deadline with
                     | Some d -> Format.shortDate d
                     | None -> "no deadline"))

        | LecturesLogic.Tab.Documents ->
            let attached =
                [ f.Slides |> Option.map (fun d -> d, "slides")
                  f.Recording |> Option.map (fun d -> d, "recording") ]
                |> List.choose id

            (attached
             |> List.map (fun (d, role) ->
                 Border.create
                     [ Border.margin (0.0, 2.0)
                       Border.child (
                           DockPanel.create
                               [ DockPanel.children
                                     [ Border.create
                                           [ DockPanel.dock Dock.Right
                                             Border.margin (10.0, 0.0, 0.0, 0.0)
                                             Border.child (
                                                 Ui.smallButton "Open" (fun () -> dispatch (OpenDocument d.Id))
                                             ) ]
                                       TextBlock.create
                                           [ TextBlock.text (sprintf "%s · %s" d.Title role)
                                             TextBlock.fontSize Theme.Type.rowMeta
                                             TextBlock.foreground (Theme.text ())
                                             TextBlock.verticalAlignment VerticalAlignment.Center
                                             TextBlock.textTrimming TextTrimming.CharacterEllipsis ] ] ]
                       ) ]
                 :> IView))
            @ (f.LinkedDocuments |> List.map (fun d -> row d.Title (DocumentKind.label d.Kind)))

        | LecturesLogic.Tab.Flashcards ->
            if f.Flashcards.IsEmpty then
                []
            else
                [ StackPanel.create
                      [ StackPanel.spacing 2.0
                        StackPanel.children
                            [ TextBlock.create
                                  [ TextBlock.text (string f.Flashcards.Length)
                                    TextBlock.fontSize 16.0
                                    TextBlock.fontWeight FontWeight.SemiBold
                                    TextBlock.foreground (Theme.text ()) ]
                              TextBlock.create
                                  [ TextBlock.text "cards generated from this lecture"
                                    TextBlock.fontSize Theme.Type.metricLabel
                                    TextBlock.foreground (Theme.faint ()) ]
                              // The redesign's second figure is "Due for
                              // review". Nothing schedules a review, so saying
                              // how many are due would be an invention.
                              TextBlock.create
                                  [ TextBlock.text
                                        "No review schedule is stored, so how many are due cannot be worked out."
                                    TextBlock.fontSize Theme.Type.metricLabel
                                    TextBlock.foreground (Theme.faint ())
                                    TextBlock.textWrapping TextWrapping.Wrap
                                    TextBlock.margin (0.0, 6.0, 0.0, 0.0) ] ] ]
                  :> IView ]

        | LecturesLogic.Tab.Planner ->
            f.Sessions
            |> List.sortBy (fun s -> s.StartsAt)
            |> List.truncate 8
            |> List.map (fun s -> row s.Title (Format.shortDate s.StartsAt + " " + Format.time s.StartsAt))

        | LecturesLogic.Tab.Timeline ->
            f.Activity
            |> List.sortByDescending (fun e -> e.OccurredAt)
            |> List.truncate LecturesLogic.Threshold.timelineRows
            |> List.map (fun e -> row e.Title (Format.relativeTime now e.OccurredAt))

        | LecturesLogic.Tab.Links ->
            (f.LinkedNotes |> List.map (fun n -> row n.Title "note"))
            @ (f.LinkedDocuments |> List.map (fun d -> row d.Title "document"))

    Border.create
        [ Border.background (Theme.cardAlt ())
          Border.cornerRadius (Tokens.radius tokens 14.0)
          Border.padding (16.0, 14.0)
          Border.minHeight LecturesLogic.Geometry.tabPanelMinHeight
          Border.child (
              if body.IsEmpty then
                  bulletRow "•" (Theme.faint ()) (LecturesLogic.Tab.emptyMessage tab) (Theme.muted ())
              else
                  StackPanel.create [ StackPanel.spacing 4.0; StackPanel.children body ] :> IView
          ) ]
    :> IView

let private lectureCard
    (tokens: Tokens)
    (now: DateTime)
    (model: Model)
    (dispatch: Msg -> unit)
    (f: LecturesLogic.Facts)
    : IView =
    let l = f.Lecture
    let isExpanded = model.Expanded = Some l.Id
    let surface = if isExpanded then (Theme.surfaces ()).Selected else (Theme.surfaces ()).Card
    let hex = f.Module |> Option.map (fun m -> m.Color) |> Option.defaultValue Theme.Identity.neutral
    let activeTab = model.Tabs.TryFind l.Id.Value |> Option.defaultValue LecturesLogic.Tab.Summary

    /// lecturer · date · time. **No week number and no duration** — neither is
    /// stored anywhere, and the lecturer is the module's, not the lecture's.
    let metaLine =
        [ f.Module |> Option.bind (fun m -> m.Lecturer)
          l.Date |> Option.map Format.shortDate
          l.Date |> Option.map Format.time ]
        |> List.choose id
        |> function
            | [] -> "No date, no module"
            | parts -> String.Join(" · ", parts)

    let headRow: IView =
        Grid.create
            [ Grid.columnDefinitions "*,10,Auto"
              Grid.children
                  [ StackPanel.create
                        [ Grid.column 0
                          StackPanel.spacing 4.0
                          StackPanel.children
                              [ Grid.create
                                    [ Grid.columnDefinitions "*,8,Auto"
                                      Grid.children
                                          [ TextBlock.create
                                                [ Grid.column 0
                                                  TextBlock.text l.Title
                                                  TextBlock.fontSize 17.0
                                                  TextBlock.fontWeight FontWeight.SemiBold
                                                  TextBlock.foreground (Theme.text ())
                                                  // The mockup ellipsises; a
                                                  // long title must stay
                                                  // readable, so it wraps.
                                                  TextBlock.textWrapping TextWrapping.Wrap
                                                  TextBlock.verticalAlignment VerticalAlignment.Center ]
                                            Border.create
                                                [ Grid.column 2
                                                  Border.verticalAlignment VerticalAlignment.Center
                                                  Border.child (
                                                      match f.Module with
                                                      | Some m -> pill tokens surface m.Code m.Color
                                                      | None -> pill tokens surface "Unfiled" Theme.Identity.neutral
                                                  ) ] ] ]

                                TextBlock.create
                                    [ TextBlock.text metaLine
                                      TextBlock.fontSize Theme.Type.rowMeta
                                      TextBlock.foreground (Theme.muted ())
                                      TextBlock.textWrapping TextWrapping.Wrap ] ] ]

                    Button.create
                        [ Grid.column 2
                          Button.content (if isExpanded then "▾" else "▸")
                          Button.width 18.0
                          Button.background Theme.transparent
                          Button.borderThickness 0.0
                          Button.padding 0.0
                          Button.fontSize Theme.Type.metricLabel
                          Button.foreground (Theme.muted ())
                          Button.verticalAlignment VerticalAlignment.Top
                          Button.cursor Ui.hand
                          Ui.accessibleName (
                              if isExpanded then sprintf "Collapse %s" l.Title else sprintf "Expand %s" l.Title
                          )
                          Button.onClick ((fun _ -> dispatch (ToggleExpanded l.Id)), SubPatchOptions.Always) ] ] ]
        :> IView

    /// The redesign's status row. Its attendance chip is **absent**: nothing
    /// records whether a lecture was attended or watched.
    let statusRow: IView =
        WrapPanel.create
            [ WrapPanel.children
                  [ Border.create
                        [ Border.margin (0.0, 0.0, 10.0, 4.0)
                          Border.child (pill tokens surface (LecturesLogic.processingLabel f) hex) ]
                    TextBlock.create
                        [ TextBlock.text (sprintf "Summary: %s" (LecturesLogic.summaryStatus f))
                          TextBlock.fontSize Theme.Type.metricLabel
                          TextBlock.foreground (Theme.faint ())
                          TextBlock.verticalAlignment VerticalAlignment.Center
                          TextBlock.margin (0.0, 0.0, 10.0, 4.0) ] ] ]
        :> IView

    let assetsBlock: IView =
        StackPanel.create
            [ StackPanel.spacing 6.0
              StackPanel.children
                  [ sectionCaption "Learning assets"
                    WrapPanel.create
                        [ WrapPanel.children
                              (LecturesLogic.assets f
                               |> List.map (fun (glyph, label, present) ->
                                   assetChip tokens surface glyph label present hex)) ] ] ]
        :> IView

    /// The redesign's "AI learning hub" offers seven actions. Two of them exist.
    /// The other five are omitted rather than drawn as dead buttons, and the
    /// caption says "AI actions" so the section does not promise more than it
    /// delivers. Both are disabled, visibly and with a stated reason, when there
    /// is nothing for the model to read.
    let aiBlock: IView =
        let reason = LecturesLogic.aiDisabledReason f
        let enabled = reason.IsNone && model.AiBusy.IsNone

        StackPanel.create
            [ StackPanel.spacing 6.0
              StackPanel.children
                  [ sectionCaption "AI actions"

                    WrapPanel.create
                        [ WrapPanel.children
                              (LecturesLogic.AiAction.all
                               |> List.map (fun action ->
                                   let busy = model.AiBusy = Some(l.Id, action)

                                   glassAction
                                       tokens
                                       (if busy then LecturesLogic.AiAction.busyLabel action
                                        else LecturesLogic.AiAction.label action)
                                       false
                                       (enabled && not busy)
                                       (sprintf "%s for %s" (LecturesLogic.AiAction.label action) l.Title)
                                       (fun () -> dispatch (RunAi(action, f))))) ]

                    match reason with
                    | Some text ->
                        TextBlock.create
                            [ TextBlock.text text
                              TextBlock.fontSize Theme.Type.metricLabel
                              TextBlock.foreground (Theme.faint ())
                              TextBlock.textWrapping TextWrapping.Wrap ]
                    | None -> () ] ]
        :> IView

    let primaryRow: IView =
        StackPanel.create
            [ StackPanel.orientation Orientation.Horizontal
              StackPanel.children
                  (LecturesLogic.CardAction.all
                   |> List.map (fun action ->
                       let label = LecturesLogic.CardAction.label f action

                       let enabled =
                           LecturesLogic.CardAction.enabled f action
                           && (match action with
                               | LecturesLogic.CardAction.AttachSlides
                               | LecturesLogic.CardAction.AttachRecording -> model.Attaching.IsNone
                               | _ -> true)

                       iconAction tokens (LecturesLogic.CardAction.glyph action) label hex enabled (fun () ->
                           match action with
                           | LecturesLogic.CardAction.Edit -> dispatch (EditLecture l)
                           | LecturesLogic.CardAction.AttachSlides -> dispatch (AttachSlides l)
                           | LecturesLogic.CardAction.AttachRecording -> dispatch (AttachRecording l)
                           | LecturesLogic.CardAction.OpenMaterial ->
                               match f.Slides, f.Recording with
                               | Some d, _ -> dispatch (OpenDocument d.Id)
                               | None, Some d -> dispatch (OpenDocument d.Id)
                               | None, None -> ()))) ]
        :> IView

    let expandedActions: IView =
        WrapPanel.create
            [ WrapPanel.children
                  (LecturesLogic.expandedActions
                   |> List.map (fun (label, page) ->
                       let enabled = page <> Page.Modules || f.Module.IsSome

                       glassAction
                           tokens
                           label
                           false
                           enabled
                           (if enabled then sprintf "Open %s" label else "This lecture is not filed under a module")
                           (fun () -> dispatch (GotoPage page)))) ]
        :> IView

    let tabsRow: IView =
        WrapPanel.create
            [ WrapPanel.children
                  (LecturesLogic.Tab.all
                   |> List.map (fun tab ->
                       let isOn = tab = activeTab
                       let count = LecturesLogic.tabCount f tab

                       Button.create
                           [ Button.background (
                                 if isOn then Theme.brush (Theme.withAlphaHex LecturesLogic.Geometry.chipFillAlpha hex) else Theme.transparent
                             )
                             Button.borderThickness 0.0
                             Button.cornerRadius (Tokens.radius tokens 999.0)
                             Button.padding (12.0, 5.0)
                             Button.margin (0.0, 0.0, 6.0, 6.0)
                             Button.fontSize Theme.Type.metricLabel
                             Button.fontWeight FontWeight.SemiBold
                             Button.foreground (
                                 if isOn then Theme.brush (Theme.readableOnColor (chipWash surface hex) hex)
                                 else Theme.muted ()
                             )
                             Button.cursor Ui.hand
                             Button.content (
                                 if count > 0 then sprintf "%s (%d)" (LecturesLogic.Tab.label tab) count
                                 else LecturesLogic.Tab.label tab
                             )
                             Ui.accessibleName (sprintf "%s tab for %s" (LecturesLogic.Tab.label tab) l.Title)
                             Button.onClick ((fun _ -> dispatch (SetTab(l.Id, tab))), SubPatchOptions.Always) ]
                       :> IView)) ]
        :> IView

    let expansionDivider: IView =
        Border.create
            [ Border.height 1.0
              Border.margin (0.0, 2.0)
              Border.background (Theme.divider ())
              Ui.decorative () ]
        :> IView

    let expandedRegion (region: LecturesLogic.ExpandedRegion) : IView =
        match region with
        | LecturesLogic.ExpandedRegion.Divider -> expansionDivider
        | LecturesLogic.ExpandedRegion.Actions -> expandedActions
        | LecturesLogic.ExpandedRegion.Tabs -> tabsRow
        | LecturesLogic.ExpandedRegion.TabPanel -> tabContent tokens now f activeTab dispatch

    let cardBody: IView list =
        [ headRow
          statusRow
          assetsBlock
          aiBlock
          bulletRow "✦" (Theme.brush hex) (LecturesLogic.insight f) (Theme.textSecondary ())
          miniPipeline f hex
          primaryRow ]
        @ (if isExpanded then LecturesLogic.expandedOrder |> List.map expandedRegion else [])

    cardWith
        tokens
        22.0
        LecturesLogic.Geometry.cardRadius
        surface
        VerticalAlignment.Top
        [ Border.create
              [ // The expansion outline, drawn in BOTH states at one thickness,
                // so expanding recolours a card and never resizes it.
                Border.borderThickness LecturesLogic.Geometry.cardBorderThickness
                Border.borderBrush (if isExpanded then Theme.brush hex else Theme.transparent)
                Border.cornerRadius (Tokens.radius tokens LecturesLogic.Geometry.cardRadius)
                Border.child (
                    StackPanel.create
                        [ StackPanel.spacing (Tokens.space tokens 12.0)
                          StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                          StackPanel.children cardBody ]
                ) ] ]

// ---------------------------------------------------------------------------
// the editor — a focused mode, not a permanent panel
// ---------------------------------------------------------------------------

let private editor (tokens: Tokens) (model: Model) (draft: Draft) (dispatch: Msg -> unit) : IView =
    plainCard
        tokens
        20.0
        [ DockPanel.create
              [ DockPanel.children
                    [ StackPanel.create
                          [ DockPanel.dock Dock.Right
                            StackPanel.orientation Orientation.Horizontal
                            StackPanel.spacing 8.0
                            StackPanel.children
                                [ if draft.Original.IsSome then
                                      Ui.dangerButton "Delete" (fun () -> dispatch RequestDelete)
                                  Ui.ghostButton "Close" (fun () -> dispatch CloseDraft)
                                  Ui.primaryButton (if model.Saving then "Saving…" else "Save") (fun () ->
                                      if not model.Saving then dispatch Save) ] ]
                      TextBlock.create
                          [ TextBlock.text (if draft.Original.IsSome then "Edit lecture" else "New lecture")
                            TextBlock.fontSize Theme.Type.cardTitle
                            TextBlock.fontWeight FontWeight.SemiBold
                            TextBlock.foreground (Theme.text ()) ] ] ]

          if model.PendingDelete then
              Border.create
                  [ Border.background (Theme.brush (Theme.withAlphaHex "1F" (Theme.dangerHex ())))
                    Border.borderThickness 1.0
                    Border.borderBrush (Theme.brush (Theme.overlayEdgeHex "55" (Theme.dangerHex ())))
                    Border.cornerRadius (Tokens.radius tokens 12.0)
                    Border.padding (16.0, 12.0)
                    Border.child (
                        StackPanel.create
                            [ StackPanel.spacing 10.0
                              StackPanel.children
                                  [ TextBlock.create
                                        [ TextBlock.text
                                              "Delete this lecture permanently? Its transcript and summary go with it. Imported slides and recordings stay in Documents, and nothing here keeps a prior state, so this cannot be undone."
                                          TextBlock.fontSize Theme.Type.rowMeta
                                          TextBlock.foreground (Theme.text ())
                                          TextBlock.textWrapping TextWrapping.Wrap ]
                                    StackPanel.create
                                        [ StackPanel.orientation Orientation.Horizontal
                                          StackPanel.spacing 10.0
                                          StackPanel.children
                                              [ Ui.dangerButton "Yes, delete it" (fun () -> dispatch ConfirmDelete)
                                                Ui.ghostButton "Cancel" (fun () -> dispatch CancelDelete) ] ] ] ]
                    ) ]

          match draft.FormError with
          | Some error ->
              TextBlock.create
                  [ TextBlock.text error
                    TextBlock.foreground (Theme.danger ())
                    TextBlock.textWrapping TextWrapping.Wrap
                    TextBlock.fontSize Theme.Type.rowMeta ]
          | None -> ()

          Ui.labeled "Title" (Ui.textBox draft.Title "Week 4 — Shading and rasterization" (SetTitle >> dispatch))

          Grid.create
              [ Grid.columnDefinitions "1*,12,1*"
                Grid.children
                    [ StackPanel.create
                          [ Grid.column 0
                            StackPanel.children
                                [ Ui.labeled
                                      "Date"
                                      (Ui.textBox draft.DateText "2026-03-14 09:00" (SetDateText >> dispatch)) ] ]
                      StackPanel.create
                          [ Grid.column 2
                            StackPanel.children
                                [ Ui.labeled
                                      "Module"
                                      (Ui.combo
                                          ("(none)" :: (model.Modules |> List.map StudyModule.displayName))
                                          draft.ModuleIndex
                                          (SetModuleIndex >> dispatch)) ] ] ] ]

          Ui.labeled
              "Transcript"
              (Ui.multilineBox draft.Transcript "Paste the lecture transcript here…" 180.0 (SetTranscript >> dispatch))

          TextBlock.create
              [ TextBlock.text
                    "Slides and recordings are attached from the lecture's own card, so the file lands in the document library filed under the right module."
                TextBlock.fontSize Theme.Type.metricLabel
                TextBlock.foreground (Theme.faint ())
                TextBlock.textWrapping TextWrapping.Wrap ] ]

// ---------------------------------------------------------------------------
// page
// ---------------------------------------------------------------------------

let view (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let spacing = Tokens.space tokens 22.0
    let now = DateTime.Now
    let shown = LecturesLogic.visible model.Filters model.ModuleFilter model.Facts

    let grid: IView =
        if model.Loading && model.Facts.IsEmpty then
            cardRow spacing 0.0 (LecturesLogic.cardColumns model.Width) [ Ui.skeletonCard 5; Ui.skeletonCard 5 ]
        elif shown.IsEmpty then
            plainCard
                tokens
                20.0
                [ bulletRow
                      "•"
                      (Theme.faint ())
                      (LecturesLogic.emptyMessage model.Filters model.ModuleFilter model.Facts.Length)
                      (Theme.muted ()) ]
        else
            cardRow
                spacing
                0.0
                (LecturesLogic.cardColumns model.Width)
                (shown |> List.map (lectureCard tokens now model dispatch))

    let renderSection (section: LecturesLogic.Section) : IView =
        match section with
        | LecturesLogic.Section.Header -> header tokens
        | LecturesLogic.Section.DropZone -> dropZone tokens
        | LecturesLogic.Section.Analytics ->
            cardRow
                spacing
                LecturesLogic.Geometry.metricRowMinHeight
                (LecturesLogic.metricColumns model.Width)
                (LecturesLogic.metrics model.Facts |> List.map (metricCard tokens))
        | LecturesLogic.Section.Pipeline -> pipelinePanel tokens model
        | LecturesLogic.Section.Filters -> filtersSection tokens model dispatch
        | LecturesLogic.Section.ModuleFilters -> moduleFiltersSection tokens model dispatch
        | LecturesLogic.Section.Grid -> grid

    let body: IView list =
        match model.Draft with
        | Some draft -> [ header tokens; editor tokens model draft dispatch ]
        | None -> LecturesLogic.sectionOrder |> List.map renderSection

    Border.create
        [ Border.onSizeChanged (
              (fun args ->
                  let decisions (width: float) =
                      LecturesLogic.cardColumns width, LecturesLogic.metricColumns width

                  if decisions model.Width <> decisions args.NewSize.Width then
                      dispatch (Resized args.NewSize.Width)),
              SubPatchOptions.Always
          )
          Border.child (
              Ui.scrollNoChrome (
                  StackPanel.create
                      [ StackPanel.spacing spacing
                        StackPanel.children
                            [ match model.Error with
                              | Some error ->
                                  let wash =
                                      Theme.compositeOver
                                          (Theme.surfaces ()).Window
                                          (Theme.withAlphaHex "22" (Theme.dangerHex ()))

                                  Border.create
                                      [ Border.background (
                                            Theme.brush (Theme.withAlphaHex "22" (Theme.dangerHex ()))
                                        )
                                        Border.cornerRadius (Tokens.radius tokens 12.0)
                                        Border.padding (16.0, 12.0)
                                        Border.child (
                                            DockPanel.create
                                                [ DockPanel.children
                                                      [ Border.create
                                                            [ DockPanel.dock Dock.Right
                                                              Border.margin (12.0, 0.0, 0.0, 0.0)
                                                              Border.verticalAlignment VerticalAlignment.Center
                                                              Border.child (
                                                                  Ui.smallButton "Try again" (fun () -> dispatch Load)
                                                              ) ]
                                                        TextBlock.create
                                                            [ TextBlock.text error
                                                              TextBlock.fontSize Theme.Type.rowMeta
                                                              TextBlock.foreground (
                                                                  Theme.brush (
                                                                      Theme.readableOnColor wash (Theme.dangerHex ())
                                                                  )
                                                              )
                                                              TextBlock.verticalAlignment VerticalAlignment.Center
                                                              TextBlock.textWrapping TextWrapping.Wrap ] ] ]
                                        ) ]
                              | None -> ()

                              match model.Notice with
                              | Some notice ->
                                  Border.create
                                      [ Border.background (Theme.brush (Theme.accentSoftHex ()))
                                        Border.cornerRadius (Tokens.radius tokens 12.0)
                                        Border.padding (16.0, 10.0)
                                        Border.child (
                                            TextBlock.create
                                                [ TextBlock.text notice
                                                  TextBlock.fontSize Theme.Type.rowMeta
                                                  TextBlock.foreground (Theme.textSecondary ())
                                                  TextBlock.textWrapping TextWrapping.Wrap ]
                                        ) ]
                              | None -> ()

                              yield! body ] ] ) ) ]
    :> IView
