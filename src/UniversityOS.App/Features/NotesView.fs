module UniversityOS.App.Features.NotesView

open System
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

/// Notes: the knowledge workspace.
///
/// The redesign's page is a top bar carrying inline metrics, then a three-column
/// grid — an Explorer rail, a reading-width workspace, and a context rail. Both
/// rails collapse independently, and focus mode hides both. That is the
/// structure here. Every decision lives in `NotesLogic`.
///
/// This **replaces** the legacy screen, which was a `1*,16,3*` two-pane CRUD
/// split. The pane structure survives only because the mockup genuinely has one;
/// everything else — the metric strip, the Explorer's sections, the centred
/// workspace, the context rail — is new.
///
/// Every real behaviour the legacy screen had is preserved: `[[wiki link]]`
/// navigation that creates the target and records a real edge, automatic wiki
/// linking on save, clickable task checkboxes that rewrite the Markdown source,
/// AI summarise (which appends into the body, because `Note` has no summary
/// field) and AI flashcards. Added: a delete confirmation, an unsaved-changes
/// guard, an error banner with retry, backlinks and linked resources.

// ---------------------------------------------------------------------------
// model
// ---------------------------------------------------------------------------

type Model =
    { Facts: NotesLogic.Facts list
      Modules: StudyModule list
      Selected: NoteId option
      // ---- the draft for the selected note ----
      TitleText: string
      ContentText: string
      TagsText: string
      ModuleIndex: int
      Pinned: bool
      Dirty: bool
      /// Reading mode renders the Markdown; editing mode shows the source.
      Reading: bool
      /// Focus mode hides both rails, as the mockup's focus toggle does.
      Focus: bool
      ExplorerOpen: bool
      ContextOpen: bool
      Filters: NotesLogic.Selection
      ModuleFilter: Set<Guid>
      Query: string
      PendingDelete: bool
      /// A selection waiting on the unsaved-changes guard.
      PendingSwitch: NoteId option
      Saving: bool
      AiBusy: NotesLogic.AiAction option
      Notice: string option
      Loading: bool
      Error: string option
      Width: float }

type External = Goto of Page

type Msg =
    | Load
    | Loaded of NotesLogic.Facts list * StudyModule list
    | Failed of exn
    | Select of NoteId
    | ConfirmSwitch
    | CancelSwitch
    | NewNote
    | SetTitle of string
    | SetContent of string
    | SetTags of string
    | SetModuleIndex of int
    | TogglePinned
    | ToggleReading
    | ToggleFocus
    | ToggleExplorer
    | ToggleContext
    | ToggleFilter of NotesLogic.Filter
    | ToggleModule of ModuleId
    | SetQuery of string
    | ToggleTask of int
    | OpenWikiLink of string
    | WikiOpened of Note
    | Save
    | Saved of Note
    | RequestDelete
    | CancelDelete
    | ConfirmDelete
    | Deleted
    | RunAi of NotesLogic.AiAction
    | AiDone of Result<string, string>
    | GotoPage of Page
    | Resized of float
    | ClearNotice

// ---------------------------------------------------------------------------
// loading
// ---------------------------------------------------------------------------

/// One command, one snapshot.
///
/// The link table is fetched **once** and filtered per note rather than queried
/// per row, and its direction is preserved, because the right rail distinguishes
/// what a note links to from what links back to it.
let private loadCmd (services: AppServices) =
    Elm.ofAsync
        (async {
            let! notes = services.Notes.GetAll()
            let! modules = services.Modules.GetAll()
            let! links = services.Links.GetAll()
            let! revision = services.Revision.GetAll()
            let! entries = services.Activity.GetRecent 300

            let facts =
                notes
                |> List.map (fun n ->
                    let self = { Kind = EntityKind.Note; Id = n.Id.Value }

                    { NotesLogic.Note = n
                      NotesLogic.Module =
                        n.ModuleId |> Option.bind (fun id -> modules |> List.tryFind (fun m -> m.Id = id))
                      NotesLogic.LinksOut =
                        links |> List.filter (fun l -> l.From = self) |> List.map (fun l -> l.To) |> List.distinct
                      NotesLogic.LinksIn =
                        links |> List.filter (fun l -> l.To = self) |> List.map (fun l -> l.From) |> List.distinct
                      NotesLogic.Flashcards =
                        revision |> List.filter (fun r -> r.Source = Some self) |> List.length
                      NotesLogic.Activity = entries |> List.filter (fun e -> e.Entity = Some self) })

            return facts, modules
        })
        Loaded
        Failed

let init (services: AppServices) : Model * Cmd<Msg> =
    { Facts = []
      Modules = []
      Selected = None
      TitleText = ""
      ContentText = ""
      TagsText = ""
      ModuleIndex = 0
      Pinned = false
      Dirty = false
      Reading = true
      Focus = false
      ExplorerOpen = true
      ContextOpen = true
      Filters = NotesLogic.Selection.empty
      ModuleFilter = Set.empty
      Query = ""
      PendingDelete = false
      PendingSwitch = None
      Saving = false
      AiBusy = None
      Notice = None
      Loading = true
      Error = None
      Width = 1600.0 },
    loadCmd services

// ---------------------------------------------------------------------------
// editor plumbing — behaviour preserved from the legacy screen
// ---------------------------------------------------------------------------

let private loadDraft (modules: StudyModule list) (note: Note) (model: Model) : Model =
    { model with
        Selected = Some note.Id
        TitleText = note.Title
        ContentText = note.Content
        TagsText = String.concat ", " note.Tags
        ModuleIndex =
            (match note.ModuleId with
             | Some mid ->
                 match modules |> List.tryFindIndex (fun m -> m.Id = mid) with
                 | Some i -> i + 1
                 | None -> 0
             | None -> 0)
        Pinned = note.Pinned
        Dirty = false
        PendingSwitch = None
        PendingDelete = false
        Notice = None }

let private blankDraft (model: Model) : Model =
    { model with
        Selected = None
        TitleText = ""
        ContentText = ""
        TagsText = ""
        ModuleIndex = 0
        Pinned = false
        Dirty = false
        PendingSwitch = None
        PendingDelete = false }

let private selectedNote (model: Model) =
    model.Selected
    |> Option.bind (fun id -> model.Facts |> List.tryFind (fun f -> f.Note.Id = id))
    |> Option.map (fun f -> f.Note)

let private buildNote (now: DateTime) (model: Model) : Note =
    let title =
        if String.IsNullOrWhiteSpace model.TitleText then "Untitled note" else model.TitleText.Trim()

    let baseNote =
        match selectedNote model with
        | Some n -> n
        | None -> Note.create now title

    { baseNote with
        Title = title
        Content = model.ContentText
        Tags =
            model.TagsText.Split(',')
            |> Array.map (fun t -> t.Trim())
            |> Array.filter (fun t -> t.Length > 0)
            |> List.ofArray
        ModuleId =
            (if model.ModuleIndex > 0 && model.ModuleIndex <= model.Modules.Length then
                 Some model.Modules.[model.ModuleIndex - 1].Id
             else
                 None)
        Pinned = model.Pinned
        UpdatedAt = now }

/// Every `[[wiki link]]` that resolves to an existing note becomes a real
/// knowledge-graph edge. Preserved exactly from the legacy screen.
let private syncWikiLinks (services: AppServices) (note: Note) =
    async {
        let targets = Markdown.wikiLinks note.Content

        if not targets.IsEmpty then
            let! all = services.Notes.GetAll()
            let noteRef = { Kind = EntityKind.Note; Id = note.Id.Value }
            let! existing = services.Links.GetFor noteRef

            for title in targets do
                match
                    all |> List.tryFind (fun n -> String.Equals(n.Title, title, StringComparison.OrdinalIgnoreCase))
                with
                | Some target when target.Id <> note.Id ->
                    let targetRef = { Kind = EntityKind.Note; Id = target.Id.Value }

                    if not (existing |> List.exists (fun l -> Link.otherEnd noteRef l = Some targetRef)) then
                        do! services.Links.Save(Link.create services.Clock.Now noteRef targetRef (Some "wiki"))
                | _ -> ()
    }

// ---------------------------------------------------------------------------
// update
// ---------------------------------------------------------------------------

let update (services: AppServices) (msg: Msg) (model: Model) : Model * Cmd<Msg> * External option =
    let plain m = m, Cmd.none, None
    let dirty m = plain { m with Dirty = true }

    let selectById (id: NoteId) (m: Model) =
        match m.Facts |> List.tryFind (fun f -> f.Note.Id = id) with
        | Some f -> loadDraft m.Modules f.Note m
        | None -> m

    match msg with
    | Load -> { model with Loading = true }, loadCmd services, None

    | Loaded(facts, modules) ->
        // A selection whose note has gone is dropped rather than left dangling,
        // and so is a module chip whose module has gone.
        let selected =
            model.Selected |> Option.filter (fun id -> facts |> List.exists (fun f -> f.Note.Id = id))

        let moduleFilter =
            model.ModuleFilter |> Set.filter (fun id -> modules |> List.exists (fun m -> m.Id.Value = id))

        plain
            { model with
                Facts = facts
                Modules = modules
                Selected = selected
                ModuleFilter = moduleFilter
                Loading = false
                Error = None }

    | Failed ex ->
        plain
            { model with
                Loading = false
                Saving = false
                AiBusy = None
                PendingDelete = false
                Error = Some ex.Message }

    // The unsaved-changes guard: switching away from a dirty draft asks first,
    // rather than silently discarding what was typed.
    | Select id when model.Dirty && model.Selected <> Some id ->
        plain { model with PendingSwitch = Some id }

    | Select id -> plain (selectById id model)

    | ConfirmSwitch ->
        match model.PendingSwitch with
        | Some id -> plain (selectById id { model with Dirty = false; PendingSwitch = None })
        | None -> plain model

    | CancelSwitch -> plain { model with PendingSwitch = None }

    | NewNote -> plain { (blankDraft model) with Dirty = true }

    | SetTitle v -> dirty { model with TitleText = v }
    | SetContent v -> dirty { model with ContentText = v }
    | SetTags v -> dirty { model with TagsText = v }
    | SetModuleIndex i -> dirty { model with ModuleIndex = i }
    | TogglePinned -> dirty { model with Pinned = not model.Pinned }

    | ToggleReading -> plain { model with Reading = not model.Reading }
    | ToggleFocus -> plain { model with Focus = not model.Focus }
    | ToggleExplorer -> plain { model with ExplorerOpen = not model.ExplorerOpen }
    | ToggleContext -> plain { model with ContextOpen = not model.ContextOpen }

    | ToggleFilter filter ->
        plain { model with Filters = NotesLogic.Selection.toggle filter model.Filters }

    | ToggleModule id ->
        let next =
            if model.ModuleFilter.Contains id.Value then model.ModuleFilter.Remove id.Value
            else model.ModuleFilter.Add id.Value

        plain { model with ModuleFilter = next }

    | SetQuery q -> plain { model with Query = q }

    // Clicking a task checkbox in the preview flips it in the source text.
    | ToggleTask index ->
        dirty { model with ContentText = Markdown.toggleTask index model.ContentText }

    // Wiki navigation: save the current note, then open (or create) the target
    // and record a knowledge link between them.
    | OpenWikiLink _ when model.Saving -> plain model
    | OpenWikiLink title ->
        let current = buildNote services.Clock.Now model

        { model with Saving = true },
        Elm.ofAsync
            (async {
                do! services.Notes.Save current
                let! all = services.Notes.GetAll()

                let! target =
                    match
                        all
                        |> List.tryFind (fun n -> String.Equals(n.Title, title, StringComparison.OrdinalIgnoreCase))
                    with
                    | Some existing -> async.Return existing
                    | None ->
                        async {
                            let created =
                                { Note.create services.Clock.Now title with ModuleId = current.ModuleId }

                            do! services.Notes.Save created
                            return created
                        }

                let currentRef = { Kind = EntityKind.Note; Id = current.Id.Value }
                let targetRef = { Kind = EntityKind.Note; Id = target.Id.Value }
                let! existing = services.Links.GetFor currentRef

                if
                    current.Id <> target.Id
                    && not (existing |> List.exists (fun l -> Link.otherEnd currentRef l = Some targetRef))
                then
                    do! services.Links.Save(Link.create services.Clock.Now currentRef targetRef (Some "wiki"))

                return target
            })
            WikiOpened
            Failed,
        None

    | WikiOpened target ->
        loadDraft model.Modules target { model with Saving = false }, loadCmd services, None

    // The re-entry guard: a second Save while one is running is dropped.
    | Save when model.Saving -> plain model
    | Save ->
        let note = buildNote services.Clock.Now model
        let isNew = model.Selected.IsNone

        { model with Saving = true },
        Elm.ofAsync
            (async {
                do! services.Notes.Save note

                do! services.Activity.Log(
                        ActivityEntry.create
                            services.Clock.Now
                            (if isNew then ActivityKind.NoteCreated else ActivityKind.NoteUpdated)
                            (sprintf "\"%s\"" note.Title)
                            None
                            (Some { Kind = EntityKind.Note; Id = note.Id.Value })
                    )

                do! syncWikiLinks services note
                return note
            })
            Saved
            Failed,
        None

    // A failed save must not discard the draft, so only success clears Dirty.
    | Saved note ->
        { model with Saving = false; Dirty = false; Selected = Some note.Id }, loadCmd services, None

    | RequestDelete -> plain { model with PendingDelete = true }
    | CancelDelete -> plain { model with PendingDelete = false }
    | ConfirmDelete when model.Saving -> plain model
    | ConfirmDelete ->
        match model.Selected with
        | Some id ->
            { model with Saving = true; PendingDelete = false },
            Elm.ofAsync (services.Notes.Delete id) (fun _ -> Deleted) Failed,
            None
        | None -> plain { model with PendingDelete = false }

    | Deleted -> { (blankDraft model) with Saving = false }, loadCmd services, None

    | RunAi _ when model.AiBusy.IsSome -> plain model
    | RunAi NotesLogic.AiAction.AskAssistant -> model, Cmd.none, Some(Goto Page.Chat)
    | RunAi action ->
        // The buttons are disabled without a body; this is the second line of
        // defence, because a model asked to summarise nothing invents.
        match NotesLogic.aiDisabledReason model.ContentText with
        | Some _ -> plain model
        | None ->
            let note = buildNote services.Clock.Now model

            { model with AiBusy = Some action },
            Elm.ofAsync
                (async {
                    match action with
                    | NotesLogic.AiAction.Summarise ->
                        let! result = services.Ai.Summarize(note.Title, note.Content)

                        match result with
                        | Ok summary ->
                            // `Note` has no summary field, so the summary is
                            // appended into the body — which is why it persists
                            // at all, and why the right rail can detect it.
                            let updated =
                                { note with
                                    Content =
                                        note.Content.TrimEnd()
                                        + "\n\n"
                                        + NotesLogic.Threshold.summaryHeading
                                        + "\n\n"
                                        + summary
                                    UpdatedAt = services.Clock.Now }

                            do! services.Notes.Save updated
                            return Ok "Summary written into the note."
                        | Error e -> return Error(AiError.describe e)

                    | NotesLogic.AiAction.GenerateFlashcards ->
                        let! result =
                            services.Ai.GenerateFlashcards(
                                note.Title,
                                note.Content,
                                note.ModuleId,
                                Some { Kind = EntityKind.Note; Id = note.Id.Value }
                            )

                        match result with
                        | Ok cards when cards.IsEmpty -> return Error "The model returned no cards for this note."
                        | Ok cards ->
                            do! services.Revision.SaveMany cards

                            do! services.Activity.Log(
                                    ActivityEntry.create
                                        services.Clock.Now
                                        ActivityKind.FlashcardsGenerated
                                        (sprintf "%d flashcards from \"%s\"" cards.Length note.Title)
                                        None
                                        (Some { Kind = EntityKind.Note; Id = note.Id.Value })
                                )

                            return Ok(sprintf "%d flashcards generated." cards.Length)
                        | Error e -> return Error(AiError.describe e)

                    | NotesLogic.AiAction.AskAssistant -> return Ok ""
                })
                AiDone
                (fun ex -> AiDone(Error ex.Message)),
            None

    | AiDone(Ok notice) ->
        { model with AiBusy = None; Dirty = false; Notice = Some notice },
        Cmd.batch [ loadCmd services; Elm.delayed 6000 ClearNotice ],
        None
    | AiDone(Error message) ->
        { model with AiBusy = None; Notice = Some message }, Elm.delayed 8000 ClearNotice, None

    | GotoPage page -> model, Cmd.none, Some(Goto page)
    | Resized width -> plain { model with Width = width }
    | ClearNotice -> plain { model with Notice = None }

// ---------------------------------------------------------------------------
// view — shared treatments
// ---------------------------------------------------------------------------

let private contentHighlightScale = NotesLogic.Geometry.contentHighlight

let private weight (w: int) = enum<FontWeight> w

/// **The one card material on this screen.**
let private cardWith
    (tokens: Tokens)
    (padding: float)
    (radius: float)
    (tint: Color)
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
                              // Never Left: a StackPanel that is not stretched
                              // sizes to its widest child.
                              StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                              StackPanel.children children ]
                    ) ] ) ]
    :> IView

let private plainCard (tokens: Tokens) (padding: float) (children: IView list) =
    cardWith tokens padding DashboardLogic.Card.radius (Theme.surfaces ()).Card children

let private sectionCaption (text: string) : IView =
    TextBlock.create
        [ TextBlock.text (text.ToUpperInvariant())
          TextBlock.fontSize 10.0
          TextBlock.fontWeight (weight 600)
          TextBlock.foreground (Theme.label ())
          TextBlock.textWrapping TextWrapping.Wrap ]
    :> IView

let private bulletRow (glyph: string) (glyphBrush: IBrush) (text: string) (textBrush: IBrush) : IView =
    Grid.create
        [ Grid.columnDefinitions NotesLogic.Geometry.bulletColumns
          Grid.margin (0.0, 2.0)
          Grid.children
              [ TextBlock.create
                    [ Grid.column 0
                      TextBlock.text glyph
                      TextBlock.fontSize Theme.Type.metricLabel
                      TextBlock.foreground glyphBrush
                      TextBlock.verticalAlignment VerticalAlignment.Top
                      Ui.decorative () ]
                TextBlock.create
                    [ Grid.column 2
                      TextBlock.text text
                      TextBlock.fontSize Theme.Type.metricLabel
                      TextBlock.foreground textBrush
                      TextBlock.textWrapping TextWrapping.Wrap ] ] ]
    :> IView

let private chipWash (surface: Color) (hex: string) =
    Theme.compositeOver surface (Theme.withAlphaHex NotesLogic.Geometry.chipFillAlpha hex)

/// A tag / concept chip. The alpha used to PAINT is the one used to MEASURE.
let private tagChip (tokens: Tokens) (surface: Color) (text: string) (hex: string) : IView =
    Border.create
        [ Border.background (Theme.brush (Theme.withAlphaHex NotesLogic.Geometry.chipFillAlpha hex))
          Border.cornerRadius (Tokens.radius tokens 999.0)
          Border.padding (10.0, 3.0)
          Border.margin (0.0, 0.0, 6.0, 6.0)
          Border.isHitTestVisible false
          Border.child (
              TextBlock.create
                  [ TextBlock.text text
                    TextBlock.fontSize Theme.Type.metricLabel
                    TextBlock.fontWeight FontWeight.SemiBold
                    TextBlock.textWrapping TextWrapping.Wrap
                    TextBlock.foreground (Theme.brush (Theme.readableOnColor (chipWash surface hex) hex)) ] ) ]
    :> IView

/// Notes' action button — geometry only.
///
/// The glass, the veil, the accent floor and the ink rule live in
/// `Ui.glassAction`, because three screens each had their own copy of them and
/// only one copy carried `Glass.primaryTintAlpha`. Padding and margin stay here
/// because they genuinely differ between screens; nothing else does.
let private glassAction
    (tokens: Tokens)
    (label: string)
    (primary: bool)
    (enabled: bool)
    (accessibleName: string)
    (onClick: unit -> unit)
    : IView =
    Ui.glassAction
        tokens
        { Ui.GlassActionMetrics.Padding = (14.0, 8.0)
          Ui.GlassActionMetrics.Margin = (0.0, 0.0, 8.0, 8.0) }
        label
        primary
        enabled
        accessibleName
        onClick

/// A rail's collapse toggle. It stays on screen when the rail is collapsed, so
/// the control that brings the panel back is never itself hidden.
let private railToggle (glyph: string) (label: string) (onClick: unit -> unit) : IView =
    Button.create
        [ Button.content glyph
          Button.width NotesLogic.Geometry.iconActionSize
          Button.height NotesLogic.Geometry.iconActionSize
          Button.background Theme.transparent
          Button.borderThickness 0.0
          Button.padding 0.0
          // Without these the glyph's TextBlock stretches to fill the fixed
          // square and draws its text at the top-left of it. Reported from the
          // runtime review as off-centre disclosure glyphs.
          Button.horizontalContentAlignment HorizontalAlignment.Center
          Button.verticalContentAlignment VerticalAlignment.Center
          Button.fontSize Theme.Type.rowMeta
          Button.foreground (Theme.muted ())
          Button.cursor Ui.hand
          ToolTip.tip label
          Ui.accessibleName label
          Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always) ]
    :> IView

let private filterChip
    (tokens: Tokens)
    (label: string)
    (isOn: bool)
    (hex: string)
    (accessibleName: string)
    (onClick: unit -> unit)
    : IView =
    let page = (Theme.surfaces ()).Window
    let wash = Theme.compositeOver page (Theme.withAlphaHex NotesLogic.Geometry.chipFillAlpha hex)

    Button.create
        [ Button.background (
              if isOn then Theme.brush (Theme.withAlphaHex NotesLogic.Geometry.chipFillAlpha hex)
              else Theme.transparent
          )
          Button.borderThickness NotesLogic.Geometry.filterBorderThickness
          Button.borderBrush (if isOn then Theme.brush (Theme.visibleOnColor page hex) else Theme.border ())
          Button.foreground (if isOn then Theme.brush (Theme.readableOnColor wash hex) else Theme.muted ())
          Button.cornerRadius (Tokens.radius tokens NotesLogic.Geometry.filterRadius)
          Button.minHeight NotesLogic.Geometry.filterMinHeight
          Button.padding (10.0, 2.0)
          Button.fontSize 10.5
          Button.fontWeight FontWeight.SemiBold
          Button.margin (0.0, 0.0, 6.0, 6.0)
          Button.cursor Ui.hand
          Button.content label
          Ui.accessibleName accessibleName
          Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always) ]
    :> IView

/// A quiet, chromeless row button — the context rail's links.
let private quietLink (label: string) (accessibleName: string) (onClick: unit -> unit) : IView =
    Button.create
        [ Button.background Theme.transparent
          Button.borderThickness 0.0
          Button.padding (0.0, 2.0)
          Button.horizontalContentAlignment HorizontalAlignment.Left
          Button.fontSize Theme.Type.metricLabel
          Button.foreground (Theme.textSecondary ())
          Button.cursor Ui.hand
          Button.content label
          Ui.accessibleName accessibleName
          Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always) ]
    :> IView

// ---------------------------------------------------------------------------
// top bar
// ---------------------------------------------------------------------------

let private topBar (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let metric (m: NotesLogic.Metric) : IView =
        StackPanel.create
            [ StackPanel.orientation Orientation.Horizontal
              StackPanel.spacing 5.0
              StackPanel.margin (0.0, 0.0, 16.0, 0.0)
              StackPanel.verticalAlignment VerticalAlignment.Center
              StackPanel.children
                  [ TextBlock.create
                        [ TextBlock.text (m.Value |> Option.defaultValue "—")
                          TextBlock.fontSize 13.0
                          TextBlock.fontWeight (weight 600)
                          TextBlock.foreground (Theme.text ()) ]
                    TextBlock.create
                        [ TextBlock.text m.Label
                          TextBlock.fontSize 10.0
                          TextBlock.foreground (Theme.faint ())
                          TextBlock.verticalAlignment VerticalAlignment.Bottom ] ] ]
        :> IView

    let titleAndMetrics: IView list =
        (TextBlock.create
            [ TextBlock.text "Notes"
              TextBlock.fontSize 19.0
              TextBlock.fontWeight FontWeight.SemiBold
              TextBlock.foreground (Theme.text ())
              TextBlock.margin (0.0, 0.0, 22.0, 0.0)
              TextBlock.verticalAlignment VerticalAlignment.Center ]
         :> IView)
        :: (NotesLogic.metrics model.Facts |> List.map metric)

    Border.create
        [ Border.borderThickness (0.0, 0.0, 0.0, 1.0)
          Border.borderBrush (Theme.divider ())
          Border.padding (24.0, 12.0)
          Border.child (
              DockPanel.create
                  [ DockPanel.children
                        [ Border.create
                              [ DockPanel.dock Dock.Right
                                Border.margin (16.0, 0.0, 0.0, 0.0)
                                Border.verticalAlignment VerticalAlignment.Center
                                Border.child (
                                    glassAction tokens "＋ New note" true (not model.Saving) "Create a new note" (fun () ->
                                        dispatch NewNote)
                                ) ]

                          // The mockup runs the metrics as an inline strip beside
                          // the title, not as the metric cards other screens use.
                          WrapPanel.create [ WrapPanel.children titleAndMetrics ] ] ]
          ) ]
    :> IView

// ---------------------------------------------------------------------------
// the Explorer rail
// ---------------------------------------------------------------------------

let private noteRow (tokens: Tokens) (now: DateTime) (model: Model) (dispatch: Msg -> unit) (f: NotesLogic.Facts) : IView =
    let isSelected = model.Selected = Some f.Note.Id
    let hex = f.Module |> Option.map (fun m -> m.Color) |> Option.defaultValue Theme.Identity.neutral
    let excerpt = NotesLogic.excerpt f.Note.Content

    let meta =
        sprintf
            "%s · %s · %d links"
            (Format.relativeTime now f.Note.UpdatedAt)
            (NotesLogic.readingLabel f.Note.Content)
            (NotesLogic.linkCount f)

    Button.create
        [ Button.classes [ Ui.glassActionClass () ]
          Button.background (
              if isSelected then Theme.brush (Theme.withAlphaHex NotesLogic.Geometry.selectedRowAlpha hex)
              else Theme.transparent
          )
          // Reserved in BOTH states, so selecting a row recolours it and never
          // resizes it.
          Button.borderThickness NotesLogic.Geometry.noteRowBorderThickness
          Button.borderBrush (if isSelected then Theme.brush hex else Theme.transparent)
          Button.cornerRadius (Tokens.radius tokens NotesLogic.Geometry.noteRowRadius)
          Button.padding (10.0, 9.0)
          Button.margin (0.0, 0.0, 0.0, 6.0)
          Button.horizontalAlignment HorizontalAlignment.Stretch
          Button.horizontalContentAlignment HorizontalAlignment.Stretch
          Button.cursor Ui.hand
          Ui.accessibleName (sprintf "Open note %s" f.Note.Title)
          Button.onClick ((fun _ -> dispatch (Select f.Note.Id)), SubPatchOptions.Always)
          Button.content (
              StackPanel.create
                  [ StackPanel.spacing 3.0
                    StackPanel.children
                        [ Grid.create
                              [ Grid.columnDefinitions "Auto,6,*,6,Auto"
                                Grid.children
                                    [ Border.create
                                          [ Grid.column 0
                                            Border.width 7.0
                                            Border.height 7.0
                                            Border.cornerRadius 4.0
                                            Border.background (Theme.brush hex)
                                            Border.verticalAlignment VerticalAlignment.Center
                                            Ui.decorative () ]
                                      TextBlock.create
                                          [ Grid.column 2
                                            TextBlock.text f.Note.Title
                                            TextBlock.fontSize 12.5
                                            TextBlock.fontWeight FontWeight.SemiBold
                                            TextBlock.foreground (Theme.text ())
                                            TextBlock.textTrimming TextTrimming.CharacterEllipsis ]
                                      TextBlock.create
                                          [ Grid.column 4
                                            TextBlock.text (if f.Note.Pinned then "◆" else "")
                                            TextBlock.fontSize 10.0
                                            TextBlock.foreground (Theme.accent ())
                                            TextBlock.verticalAlignment VerticalAlignment.Center
                                            Ui.decorative () ] ] ]

                          TextBlock.create
                              [ TextBlock.text meta
                                TextBlock.fontSize 10.5
                                TextBlock.foreground (Theme.faint ())
                                TextBlock.textTrimming TextTrimming.CharacterEllipsis ]

                          if excerpt.Length > 0 then
                              TextBlock.create
                                  [ TextBlock.text excerpt
                                    TextBlock.fontSize 10.5
                                    TextBlock.foreground (Theme.muted ())
                                    TextBlock.textWrapping TextWrapping.Wrap ] ] ]
          ) ]
    :> IView

let private explorerPanel (tokens: Tokens) (now: DateTime) (model: Model) (dispatch: Msg -> unit) : IView =
    let rows = NotesLogic.visible now model.Filters model.ModuleFilter model.Query model.Facts
    let pinnedRows = NotesLogic.pinned rows
    let otherRows = NotesLogic.unpinned rows

    let region (r: NotesLogic.ExplorerRegion) : IView list =
        match r with
        | NotesLogic.ExplorerRegion.Search ->
            [ Ui.textBox model.Query "Search titles, tags and text" (SetQuery >> dispatch) ]

        | NotesLogic.ExplorerRegion.Filters ->
            [ WrapPanel.create
                  [ WrapPanel.children
                        (NotesLogic.Filter.all
                         |> List.map (fun f ->
                             let label = NotesLogic.Filter.label f
                             let isOn = model.Filters.Contains f

                             filterChip
                                 tokens
                                 label
                                 isOn
                                 (Theme.accentHex ())
                                 (if isOn then sprintf "%s filter, on" label else sprintf "Turn on %s" label)
                                 (fun () -> dispatch (ToggleFilter f)))) ] ]

        | NotesLogic.ExplorerRegion.ModuleChips ->
            let chips = NotesLogic.moduleChips model.Facts

            if chips.IsEmpty then
                []
            else
                [ WrapPanel.create
                      [ WrapPanel.children
                            (chips
                             |> List.map (fun (m, count) ->
                                 let isOn = model.ModuleFilter.Contains m.Id.Value

                                 filterChip
                                     tokens
                                     (sprintf "%s (%d)" m.Code count)
                                     isOn
                                     m.Color
                                     (if isOn then sprintf "%s filter, on" (StudyModule.displayName m)
                                      else sprintf "Show only %s" (StudyModule.displayName m))
                                     (fun () -> dispatch (ToggleModule m.Id)))) ] ]

        | NotesLogic.ExplorerRegion.Pinned ->
            // Omitted entirely when nothing is pinned, as the mockup does.
            if pinnedRows.IsEmpty then
                []
            else
                [ sectionCaption "Pinned"
                  StackPanel.create
                      [ StackPanel.children (pinnedRows |> List.map (noteRow tokens now model dispatch)) ] ]

        | NotesLogic.ExplorerRegion.All ->
            if rows.IsEmpty then
                [ Border.create
                      [ Border.padding (0.0, 18.0)
                        Border.child (
                            bulletRow
                                "•"
                                (Theme.faint ())
                                (NotesLogic.emptyMessage model.Filters model.ModuleFilter model.Query model.Facts.Length)
                                (Theme.muted ())
                        ) ] ]
            elif otherRows.IsEmpty then
                []
            else
                [ sectionCaption "All notes"
                  StackPanel.create
                      [ StackPanel.children (otherRows |> List.map (noteRow tokens now model dispatch)) ] ]

    DockPanel.create
        [ DockPanel.children
              [ DockPanel.create
                    [ DockPanel.dock Dock.Top
                      DockPanel.margin (10.0, 8.0, 6.0, 4.0)
                      DockPanel.children
                          [ Border.create
                                [ DockPanel.dock Dock.Right
                                  Border.child (railToggle "‹" "Collapse the Explorer" (fun () -> dispatch ToggleExplorer)) ]
                            Border.create
                                [ Border.verticalAlignment VerticalAlignment.Center
                                  Border.child (sectionCaption "Explorer") ] ] ]

                Ui.scrollNoChrome (
                    StackPanel.create
                        [ StackPanel.spacing 12.0
                          StackPanel.margin (12.0, 0.0, 12.0, 16.0)
                          StackPanel.children (NotesLogic.explorerOrder |> List.collect region) ]
                ) ] ]
    :> IView

// ---------------------------------------------------------------------------
// the workspace
// ---------------------------------------------------------------------------

let private workspace (tokens: Tokens) (now: DateTime) (model: Model) (dispatch: Msg -> unit) : IView =
    let current = model.Facts |> List.tryFind (fun f -> Some f.Note.Id = model.Selected)
    let hasDraft = model.Selected.IsSome || model.Dirty

    let hex =
        current
        |> Option.bind (fun f -> f.Module)
        |> Option.map (fun m -> m.Color)
        |> Option.defaultValue Theme.Identity.neutral

    let surface = (Theme.surfaces ()).Card

    let headerBar: IView =
        Border.create
            [ Border.borderThickness (0.0, 0.0, 0.0, 1.0)
              Border.borderBrush (Theme.divider ())
              Border.padding (28.0, 12.0)
              Border.child (
                  DockPanel.create
                      [ DockPanel.children
                            [ WrapPanel.create
                                  [ DockPanel.dock Dock.Right
                                    WrapPanel.margin (12.0, 0.0, 0.0, 0.0)
                                    WrapPanel.children
                                        [ if hasDraft then
                                              glassAction
                                                  tokens
                                                  (if model.Reading then "✎ Edit" else "▤ Read")
                                                  false
                                                  true
                                                  (if model.Reading then "Switch to editing" else "Switch to reading")
                                                  (fun () -> dispatch ToggleReading)

                                          glassAction
                                              tokens
                                              (if model.Focus then "⤢ Exit focus" else "⤡ Focus")
                                              false
                                              true
                                              (if model.Focus then "Show the side panels" else "Hide the side panels")
                                              (fun () -> dispatch ToggleFocus)

                                          if hasDraft then
                                              glassAction
                                                  tokens
                                                  (if model.Saving then "Saving…" elif model.Dirty then "Save •" else "Saved")
                                                  model.Dirty
                                                  (model.Dirty && not model.Saving)
                                                  "Save this note"
                                                  (fun () -> dispatch Save)

                                          if model.Selected.IsSome then
                                              glassAction tokens "Delete" false (not model.Saving) "Delete this note" (fun () ->
                                                  dispatch RequestDelete) ] ]

                              WrapPanel.create
                                  [ WrapPanel.children
                                        [ match current with
                                          | Some f ->
                                              Border.create
                                                  [ Border.width 9.0
                                                    Border.height 9.0
                                                    Border.cornerRadius 5.0
                                                    Border.margin (0.0, 0.0, 10.0, 0.0)
                                                    Border.background (Theme.brush hex)
                                                    Border.verticalAlignment VerticalAlignment.Center
                                                    Ui.decorative () ]

                                              Border.create
                                                  [ Border.margin (0.0, 0.0, 10.0, 0.0)
                                                    Border.verticalAlignment VerticalAlignment.Center
                                                    Border.child (
                                                        match f.Module with
                                                        | Some m -> tagChip tokens surface m.Code m.Color
                                                        | None -> tagChip tokens surface "Unfiled" Theme.Identity.neutral
                                                    ) ]

                                              TextBlock.create
                                                  [ TextBlock.text (
                                                        sprintf
                                                            "%s · %s · %d links"
                                                            (Format.relativeTime now f.Note.UpdatedAt)
                                                            (NotesLogic.readingLabel f.Note.Content)
                                                            (NotesLogic.linkCount f)
                                                    )
                                                    TextBlock.fontSize Theme.Type.metricLabel
                                                    TextBlock.foreground (Theme.faint ())
                                                    TextBlock.verticalAlignment VerticalAlignment.Center ]
                                          | None ->
                                              TextBlock.create
                                                  [ TextBlock.text (
                                                        if model.Dirty then "New note — unsaved" else "No note selected"
                                                    )
                                                    TextBlock.fontSize Theme.Type.metricLabel
                                                    TextBlock.foreground (Theme.faint ())
                                                    TextBlock.verticalAlignment VerticalAlignment.Center ] ] ] ] ]
              ) ]
        :> IView

    let aiRow: IView =
        let reason = NotesLogic.aiDisabledReason model.ContentText

        StackPanel.create
            [ StackPanel.spacing 6.0
              StackPanel.children
                  [ sectionCaption "AI actions"

                    WrapPanel.create
                        [ WrapPanel.children
                              (NotesLogic.AiAction.all
                               |> List.map (fun action ->
                                   let busy = model.AiBusy = Some action

                                   let enabled =
                                       model.AiBusy.IsNone
                                       && (not (NotesLogic.AiAction.needsBody action) || reason.IsNone)

                                   glassAction
                                       tokens
                                       (if busy then NotesLogic.AiAction.busyLabel action
                                        else NotesLogic.AiAction.label action)
                                       false
                                       enabled
                                       (NotesLogic.AiAction.label action)
                                       (fun () -> dispatch (RunAi action)))) ]

                    match reason with
                    | Some text ->
                        TextBlock.create
                            [ TextBlock.text text
                              TextBlock.fontSize Theme.Type.metricLabel
                              TextBlock.foreground (Theme.faint ())
                              TextBlock.textWrapping TextWrapping.Wrap ]
                    | None -> () ] ]
        :> IView

    let wikiRow: IView list =
        match Markdown.wikiLinks model.ContentText with
        | [] -> []
        | targets ->
            [ StackPanel.create
                  [ StackPanel.spacing 6.0
                    StackPanel.children
                        [ // Deterministic, so it is labelled as such — these are
                          // parsed out of the text, not suggested by a model.
                          sectionCaption "Extracted links"
                          WrapPanel.create
                              [ WrapPanel.children
                                    (targets
                                     |> List.map (fun t ->
                                         filterChip tokens (sprintf "⛓ %s" t) false (Theme.accentHex ()) (sprintf "Open %s" t) (fun () ->
                                             dispatch (OpenWikiLink t)))) ] ] ] ]

    let body: IView list =
        if not hasDraft then
            [ plainCard
                  tokens
                  20.0
                  [ bulletRow
                        "•"
                        (Theme.faint ())
                        (if model.Facts.IsEmpty then
                             "No notes yet. Create one with ＋ New note — everything else on this screen hangs off a note."
                         else
                             "Pick a note from the Explorer, or create a new one.")
                        (Theme.muted ()) ] ]
        else
            [ if model.Reading then
                  TextBlock.create
                      [ TextBlock.text (
                            if String.IsNullOrWhiteSpace model.TitleText then "Untitled note" else model.TitleText
                        )
                        TextBlock.fontSize 30.0
                        TextBlock.fontWeight FontWeight.SemiBold
                        TextBlock.foreground (Theme.text ())
                        TextBlock.textWrapping TextWrapping.Wrap ]
              else
                  Ui.labeled "Title" (Ui.textBox model.TitleText "A title" (SetTitle >> dispatch))

              // The mockup's "key concepts". No concept extraction exists; tags
              // are real and occupy the same slot.
              match current with
              | Some f when not f.Note.Tags.IsEmpty && model.Reading ->
                  WrapPanel.create
                      [ WrapPanel.children (f.Note.Tags |> List.map (fun t -> tagChip tokens surface ("⛓ " + t) hex)) ]
              | _ -> ()

              if not model.Reading then
                  Grid.create
                      [ Grid.columnDefinitions "2*,12,1*"
                        Grid.children
                            [ StackPanel.create
                                  [ Grid.column 0
                                    StackPanel.children
                                        [ Ui.labeled "Tags" (Ui.textBox model.TagsText "tags, comma, separated" (SetTags >> dispatch)) ] ]
                              StackPanel.create
                                  [ Grid.column 2
                                    StackPanel.children
                                        [ Ui.labeled
                                              "Module"
                                              (Ui.combo
                                                  ("(none)" :: (model.Modules |> List.map StudyModule.displayName))
                                                  model.ModuleIndex
                                                  (SetModuleIndex >> dispatch)) ] ] ] ]

                  StackPanel.create
                      [ StackPanel.orientation Orientation.Horizontal
                        StackPanel.spacing 8.0
                        StackPanel.children
                            [ Ui.checkGlyph model.Pinned (fun () -> dispatch TogglePinned)
                              TextBlock.create
                                  [ TextBlock.text "Pinned to the top of the Explorer"
                                    TextBlock.fontSize Theme.Type.rowMeta
                                    TextBlock.foreground (Theme.muted ())
                                    TextBlock.verticalAlignment VerticalAlignment.Center ] ] ]

              aiRow

              if model.Reading then
                  // The renderer genuinely handles the mockup's callout, task
                  // list, code block and table — they are note content, not
                  // separate regions.
                  if String.IsNullOrWhiteSpace model.ContentText then
                      bulletRow "•" (Theme.faint ()) "This note has no body yet. Switch to Edit and write something." (Theme.muted ())
                  else
                      Ui.markdownWith (Some(fun i -> dispatch (ToggleTask i))) model.ContentText
              else
                  StackPanel.create
                      [ StackPanel.spacing 4.0
                        StackPanel.children
                            [ sectionCaption "Markdown"
                              Ui.multilineBox model.ContentText "Write in Markdown. [[Wiki links]] become real edges." 320.0 (SetContent >> dispatch) ] ]

              yield! wikiRow ]

    DockPanel.create
        [ DockPanel.children
              [ Border.create [ DockPanel.dock Dock.Top; Border.child headerBar ]

                Ui.scrollNoChrome (
                    Border.create
                        [ Border.padding (24.0, 28.0, 24.0, 64.0)
                          Border.child (
                              StackPanel.create
                                  [ StackPanel.spacing (Tokens.space tokens 18.0)
                                    // The WORKSPACE is reading-width. The page
                                    // is not — that distinction is the point.
                                    StackPanel.maxWidth NotesLogic.Geometry.editorMaxWidth
                                    StackPanel.horizontalAlignment HorizontalAlignment.Center
                                    StackPanel.children body ]
                          ) ]
                ) ] ]
    :> IView

// ---------------------------------------------------------------------------
// the context rail
// ---------------------------------------------------------------------------

/// The mockup calls this "AI Context". Most of it is deterministic — links,
/// counts, timestamps and rules — so it is titled **CONTEXT**, and only the one
/// region a provider genuinely wrote is labelled as AI.
let private contextPanel (tokens: Tokens) (now: DateTime) (model: Model) (dispatch: Msg -> unit) : IView =
    let current = model.Facts |> List.tryFind (fun f -> Some f.Note.Id = model.Selected)
    let surface = (Theme.surfaces ()).Card

    let hex =
        current
        |> Option.bind (fun f -> f.Module)
        |> Option.map (fun m -> m.Color)
        |> Option.defaultValue Theme.Identity.neutral

    let titleOf (e: EntityRef) =
        match e.Kind with
        | EntityKind.Note ->
            model.Facts
            |> List.tryFind (fun f -> f.Note.Id.Value = e.Id)
            |> Option.map (fun f -> f.Note.Title)
        | _ -> None

    let labelled (caption: string) (children: IView list) : IView list =
        if children.IsEmpty then
            []
        else
            [ StackPanel.create
                  [ StackPanel.spacing 5.0
                    StackPanel.children (sectionCaption caption :: children) ] ]

    let valueRow (label: string) (value: string) : IView =
        Grid.create
            [ Grid.columnDefinitions NotesLogic.Geometry.detailColumns
              Grid.margin (0.0, 1.0)
              Grid.children
                  [ TextBlock.create
                        [ Grid.column 0
                          TextBlock.text label
                          TextBlock.fontSize 11.0
                          TextBlock.foreground (Theme.faint ()) ]
                    TextBlock.create
                        [ Grid.column 2
                          TextBlock.text value
                          TextBlock.fontSize 11.5
                          TextBlock.foreground (Theme.text ())
                          TextBlock.textAlignment TextAlignment.Right
                          TextBlock.textTrimming TextTrimming.CharacterEllipsis ] ] ]
        :> IView

    /// An edge, as a button. A note opens in place; anything else navigates to
    /// the screen that owns it.
    let edgeButton (e: EntityRef) : IView =
        match titleOf e, e.Kind with
        | Some title, EntityKind.Note ->
            quietLink (Format.truncate 34 title) (sprintf "Open note %s" title) (fun () ->
                dispatch (Select(NoteId e.Id)))
        | _ ->
            match NotesLogic.RelatedKind.ofEntity e.Kind with
            | Some kind ->
                let label = NotesLogic.RelatedKind.label kind

                quietLink
                    (sprintf "a linked %s" (label.TrimEnd 's' |> fun s -> s.ToLowerInvariant()))
                    (sprintf "Open %s" label)
                    (fun () -> dispatch (GotoPage(NotesLogic.RelatedKind.page kind)))
            | None ->
                TextBlock.create
                    [ TextBlock.text "a linked item"
                      TextBlock.fontSize Theme.Type.metricLabel
                      TextBlock.foreground (Theme.faint ()) ]
                :> IView

    let region (f: NotesLogic.Facts) (r: NotesLogic.ContextRegion) : IView list =
        match r with
        | NotesLogic.ContextRegion.AiSummary ->
            // `Note` has no summary field, so a summary exists only as a heading
            // inside the body. Shown only when one genuinely does.
            if not (NotesLogic.hasSummary f) then
                []
            else
                labelled
                    "AI summary"
                    [ TextBlock.create
                          [ TextBlock.text "Written by the AI into this note's body."
                            TextBlock.fontSize Theme.Type.metricLabel
                            TextBlock.foreground (Theme.textSecondary ())
                            TextBlock.textWrapping TextWrapping.Wrap ] ]

        | NotesLogic.ContextRegion.Concepts ->
            labelled
                "Key concepts"
                (if f.Note.Tags.IsEmpty then
                     []
                 else
                     [ WrapPanel.create
                           [ WrapPanel.children (f.Note.Tags |> List.map (fun t -> tagChip tokens surface t hex)) ] ])

        | NotesLogic.ContextRegion.Related ->
            // Zero-count categories are omitted, not drawn as decorative rows.
            labelled
                "Related"
                (NotesLogic.related f
                 |> List.map (fun (kind, count) -> valueRow (NotesLogic.RelatedKind.label kind) (string count)))

        | NotesLogic.ContextRegion.History ->
            labelled
                "Timeline history"
                (f.Activity
                 |> List.sortByDescending (fun e -> e.OccurredAt)
                 |> List.truncate 5
                 |> List.map (fun e -> valueRow (Format.truncate 22 e.Title) (Format.relativeTime now e.OccurredAt)))

        | NotesLogic.ContextRegion.GraphNodes ->
            // The mockup's dashed canvas is omitted; the real edges are listed.
            let edges = (f.LinksOut @ f.LinksIn) |> List.distinct

            labelled
                (sprintf "Knowledge graph — %d linked" edges.Length)
                (edges |> List.truncate 8 |> List.map edgeButton)

        | NotesLogic.ContextRegion.Backlinks ->
            // Two categories, not the mockup's three: "Linked from" and
            // "Referenced by" are the same relation, so showing both would
            // count the same edge twice.
            labelled "Links to" (f.LinksOut |> List.truncate 5 |> List.map edgeButton)
            @ labelled "Linked from" (f.LinksIn |> List.truncate 5 |> List.map edgeButton)

        | NotesLogic.ContextRegion.Timestamps ->
            // Replaces the mockup's "Version History". Nothing stores versions —
            // only these two timestamps exist.
            labelled
                "History"
                [ valueRow "Created" (Format.shortDate f.Note.CreatedAt)
                  valueRow "Last edited" (Format.relativeTime now f.Note.UpdatedAt)
                  valueRow "Words" (string (NotesLogic.wordCount f.Note.Content)) ]

        | NotesLogic.ContextRegion.Suggestions ->
            labelled
                "Rule-based suggestions"
                (NotesLogic.suggestions f
                 |> List.map (fun (glyph, text) -> bulletRow glyph (Theme.brush hex) text (Theme.textSecondary ())))

    let body: IView list =
        match current with
        | Some f -> NotesLogic.contextOrder |> List.collect (region f)
        | None ->
            [ bulletRow
                  "•"
                  (Theme.faint ())
                  "Select a note to see its links, tags, history and suggestions."
                  (Theme.muted ()) ]

    DockPanel.create
        [ DockPanel.children
              [ DockPanel.create
                    [ DockPanel.dock Dock.Top
                      DockPanel.margin (6.0, 8.0, 10.0, 4.0)
                      DockPanel.children
                          [ Border.create
                                [ DockPanel.dock Dock.Left
                                  Border.child (railToggle "›" "Collapse the context panel" (fun () -> dispatch ToggleContext)) ]
                            Border.create
                                [ Border.verticalAlignment VerticalAlignment.Center
                                  Border.horizontalAlignment HorizontalAlignment.Right
                                  Border.child (sectionCaption "Context") ] ] ]

                Ui.scrollNoChrome (
                    StackPanel.create
                        [ StackPanel.spacing 14.0
                          StackPanel.margin (14.0, 0.0, 14.0, 20.0)
                          StackPanel.children body ]
                ) ] ]
    :> IView

// ---------------------------------------------------------------------------
// page
// ---------------------------------------------------------------------------

let view (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let now = DateTime.Now
    let panes = NotesLogic.panesFor model.Width model.Focus model.ExplorerOpen model.ContextOpen

    /// A collapsed rail is still a rail: it keeps the toggle that reopens it.
    let collapsedRail (glyph: string) (label: string) (onClick: unit -> unit) : IView =
        Border.create
            [ Border.width NotesLogic.Geometry.railWidth
              Border.padding (0.0, 8.0)
              Border.child (
                  StackPanel.create
                      [ StackPanel.verticalAlignment VerticalAlignment.Top
                        StackPanel.horizontalAlignment HorizontalAlignment.Center
                        StackPanel.children [ railToggle glyph label onClick ] ]
              ) ]
        :> IView

    let leftColumn: IView =
        if panes.Explorer then
            Border.create
                [ Border.borderThickness (0.0, 0.0, 1.0, 0.0)
                  Border.borderBrush (Theme.divider ())
                  Border.child (explorerPanel tokens now model dispatch) ]
            :> IView
        else
            collapsedRail "›" "Open the Explorer" (fun () -> dispatch ToggleExplorer)

    let rightColumn: IView =
        if panes.Context then
            Border.create
                [ Border.borderThickness (1.0, 0.0, 0.0, 0.0)
                  Border.borderBrush (Theme.divider ())
                  Border.child (contextPanel tokens now model dispatch) ]
            :> IView
        else
            collapsedRail "‹" "Open the context panel" (fun () -> dispatch ToggleContext)

    /// A confirmation the legacy screen never had, and an unsaved-changes guard
    /// it never had either. Both are drawn above the page rather than as modal
    /// windows, so nothing can be lost behind a dialog.
    let banners: IView list =
        [ match model.Error with
          | Some error ->
              let wash =
                  Theme.compositeOver (Theme.surfaces ()).Window (Theme.withAlphaHex "22" (Theme.dangerHex ()))

              Border.create
                  [ Border.background (Theme.brush (Theme.withAlphaHex "22" (Theme.dangerHex ())))
                    Border.cornerRadius (Tokens.radius tokens 12.0)
                    Border.padding (16.0, 12.0)
                    Border.margin (24.0, 12.0, 24.0, 0.0)
                    Border.child (
                        DockPanel.create
                            [ DockPanel.children
                                  [ Border.create
                                        [ DockPanel.dock Dock.Right
                                          Border.margin (12.0, 0.0, 0.0, 0.0)
                                          Border.verticalAlignment VerticalAlignment.Center
                                          Border.child (Ui.smallButton "Try again" (fun () -> dispatch Load)) ]
                                    TextBlock.create
                                        [ TextBlock.text error
                                          TextBlock.fontSize Theme.Type.rowMeta
                                          TextBlock.foreground (
                                              Theme.brush (Theme.readableOnColor wash (Theme.dangerHex ()))
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
                    Border.margin (24.0, 12.0, 24.0, 0.0)
                    Border.child (
                        TextBlock.create
                            [ TextBlock.text notice
                              TextBlock.fontSize Theme.Type.rowMeta
                              TextBlock.foreground (Theme.textSecondary ())
                              TextBlock.textWrapping TextWrapping.Wrap ] ) ]
          | None -> ()

          if model.PendingSwitch.IsSome then
              Border.create
                  [ Border.background (Theme.brush (Theme.withAlphaHex "1F" (Theme.warningHex ())))
                    Border.borderThickness 1.0
                    Border.borderBrush (Theme.brush (Theme.overlayEdgeHex "55" (Theme.warningHex ())))
                    Border.cornerRadius (Tokens.radius tokens 12.0)
                    Border.padding (16.0, 12.0)
                    Border.margin (24.0, 12.0, 24.0, 0.0)
                    Border.child (
                        DockPanel.create
                            [ DockPanel.children
                                  [ StackPanel.create
                                        [ DockPanel.dock Dock.Right
                                          StackPanel.orientation Orientation.Horizontal
                                          StackPanel.spacing 10.0
                                          StackPanel.margin (12.0, 0.0, 0.0, 0.0)
                                          StackPanel.children
                                              [ Ui.smallButton "Discard and switch" (fun () -> dispatch ConfirmSwitch)
                                                Ui.ghostButton "Stay here" (fun () -> dispatch CancelSwitch) ] ]
                                    TextBlock.create
                                        [ TextBlock.text
                                              "This note has unsaved changes. Switching now discards them."
                                          TextBlock.fontSize Theme.Type.rowMeta
                                          TextBlock.foreground (Theme.text ())
                                          TextBlock.verticalAlignment VerticalAlignment.Center
                                          TextBlock.textWrapping TextWrapping.Wrap ] ] ]
                    ) ]

          if model.PendingDelete then
              Border.create
                  [ Border.background (Theme.brush (Theme.withAlphaHex "1F" (Theme.dangerHex ())))
                    Border.borderThickness 1.0
                    Border.borderBrush (Theme.brush (Theme.overlayEdgeHex "55" (Theme.dangerHex ())))
                    Border.cornerRadius (Tokens.radius tokens 12.0)
                    Border.padding (16.0, 12.0)
                    Border.margin (24.0, 12.0, 24.0, 0.0)
                    Border.child (
                        DockPanel.create
                            [ DockPanel.children
                                  [ StackPanel.create
                                        [ DockPanel.dock Dock.Right
                                          StackPanel.orientation Orientation.Horizontal
                                          StackPanel.spacing 10.0
                                          StackPanel.margin (12.0, 0.0, 0.0, 0.0)
                                          StackPanel.children
                                              [ Ui.dangerButton "Yes, delete it" (fun () -> dispatch ConfirmDelete)
                                                Ui.ghostButton "Cancel" (fun () -> dispatch CancelDelete) ] ]
                                    TextBlock.create
                                        [ TextBlock.text
                                              "Delete this note permanently? Its links go with it, and nothing here keeps a prior state, so this cannot be undone."
                                          TextBlock.fontSize Theme.Type.rowMeta
                                          TextBlock.foreground (Theme.text ())
                                          TextBlock.verticalAlignment VerticalAlignment.Center
                                          TextBlock.textWrapping TextWrapping.Wrap ] ] ]
                    ) ] ]

    // Explicit Grid columns, because alignment between the rails and the centre
    // matters: a stack would let the centre drift as the rails resize.
    let columns =
        sprintf
            "%f,*,%f"
            (NotesLogic.explorerColumnWidth panes)
            (NotesLogic.contextColumnWidth panes)

    Border.create
        [ Border.onSizeChanged (
              (fun args ->
                  let decisions (w: float) = NotesLogic.panesFor w model.Focus model.ExplorerOpen model.ContextOpen

                  if decisions model.Width <> decisions args.NewSize.Width then
                      dispatch (Resized args.NewSize.Width)),
              SubPatchOptions.Always
          )
          Border.child (
              DockPanel.create
                  [ DockPanel.children
                        [ StackPanel.create
                              [ DockPanel.dock Dock.Top
                                StackPanel.children (topBar tokens model dispatch :: banners) ]

                          Grid.create
                              [ Grid.columnDefinitions columns
                                Grid.children
                                    [ Border.create [ Grid.column 0; Border.child leftColumn ]
                                      Border.create
                                          [ Grid.column 1
                                            Border.minWidth NotesLogic.Geometry.centreMinWidth
                                            Border.child (workspace tokens now model dispatch) ]
                                      Border.create [ Grid.column 2; Border.child rightColumn ] ] ] ] ]
          ) ]
    :> IView
