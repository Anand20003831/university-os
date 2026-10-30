module UniversityOS.App.Features.ModulesView

open System
open Elmish
open Avalonia
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Controls

/// Modules: how am I progressing in each module?
///
/// The redesign's page is a health row, a row of toggle chips, a captioned grid
/// of large module cards, and a captioned grid of small archived ones. That is
/// the structure here. Every decision lives in `ModulesLogic`; this file draws
/// what that module resolves.
///
/// The full-CRUD editor the screen has always had is preserved, but as a focused
/// editing mode that takes the page — not as a permanent half of a master/detail
/// split, which is what the legacy screen was.

// ---------------------------------------------------------------------------
// model
// ---------------------------------------------------------------------------

type Draft =
    { Original: StudyModule option
      Code: string
      Name: string
      Lecturer: string
      CreditsText: string
      Term: string
      Description: string
      ColorIndex: int
      Archived: bool
      FormError: string option }

type Model =
    { Facts: ModulesLogic.Facts list
      /// Chips are toggles: several can be on at once.
      Filters: ModulesLogic.Selection
      /// The one expanded card. Only ever one.
      Expanded: ModuleId option
      /// The chosen tab per module, keyed by its id.
      Tabs: Map<Guid, ModulesLogic.Tab>
      Draft: Draft option
      PendingDelete: bool
      /// From `StudyTime.minutesStudiedThisWeek` — the shared definition.
      StudiedThisWeek: int
      Saving: bool
      Loading: bool
      Error: string option
      Width: float }

type External = Goto of Page

type Msg =
    | Load
    | Loaded of ModulesLogic.Facts list * int
    | Failed of exn
    | ToggleFilter of ModulesLogic.Filter
    | ToggleExpanded of ModuleId
    | SetTab of ModuleId * ModulesLogic.Tab
    | GotoPage of Page
    | Resized of float
    // ---- editor ----
    | NewModule
    | EditModule of StudyModule
    | CloseDraft
    | SetCode of string
    | SetName of string
    | SetLecturer of string
    | SetCredits of string
    | SetTerm of string
    | SetDescription of string
    | SetColorIndex of int
    | ToggleArchivedFlag
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
/// batched: the modules, their assignments (as the shared `AssignmentsLogic`
/// facts, so risk cannot disagree with the Assignments screen), their notes,
/// documents, lectures and flashcards, the calendar events filed under them, the
/// planner's own sessions, and the activity log.
let private loadCmd (services: AppServices) =
    Elm.ofAsync
        (async {
            let now = services.Clock.Now
            let! modules = services.Modules.GetAll()
            let! settings = services.Settings.Load()
            let horizonDays = float (Planning.PlannerPreferences.ofSettings settings).HorizonDays

            let! sessions = services.Sessions.GetBetween(now, now.AddDays(horizonDays + 1.0))
            let! weekSessions = services.Sessions.GetBetween(now.AddDays -14.0, now.AddDays 1.0)
            let! events = services.Events.GetBetween(now, now.AddDays 400.0)
            let! entries = services.Activity.GetRecent 300
            let! preview = services.Planner.PreviewWith id

            let unplacedReason (a: Assignment) =
                preview.Unplanned
                |> List.tryFind (fun u -> u.Task.AssignmentId = a.Id)
                |> Option.map (fun u -> u.Reason)

            let mutable facts = []

            for m in modules do
                let! assignments = services.Assignments.GetByModule m.Id
                let! notes = services.Notes.GetByModule m.Id
                let! documents = services.Documents.GetByModule m.Id
                let! lectures = services.Lectures.GetByModule m.Id
                let! flashcards = services.Revision.GetByModule m.Id

                let assignmentFacts =
                    assignments
                    |> List.map (fun a ->
                        let scheduled =
                            sessions |> List.filter (fun s -> s.AssignmentId = Some a.Id && not s.Completed)

                        { AssignmentsLogic.Assignment = a
                          AssignmentsLogic.Module = Some m
                          AssignmentsLogic.ScheduledMinutes =
                            scheduled |> List.sumBy (fun s -> max 0 (int (s.EndsAt - s.StartsAt).TotalMinutes))
                          AssignmentsLogic.SessionCount = scheduled.Length
                          AssignmentsLogic.UnplacedReason = unplacedReason a
                          AssignmentsLogic.Linked = []
                          AssignmentsLogic.ActivityCount = 0 })

                facts <-
                    facts
                    @ [ { ModulesLogic.Module = m
                          ModulesLogic.Assignments = assignmentFacts
                          ModulesLogic.Notes = notes
                          ModulesLogic.Documents = documents
                          ModulesLogic.Lectures = lectures
                          ModulesLogic.Flashcards = flashcards
                          ModulesLogic.Events = events |> List.filter (fun e -> e.ModuleId = Some m.Id)
                          ModulesLogic.Sessions =
                            sessions |> List.filter (fun s -> s.ModuleCode = Some m.Code && not s.Completed)
                          ModulesLogic.ActivityCount =
                            entries
                            |> List.filter (fun e -> e.Entity = Some { Kind = EntityKind.Module; Id = m.Id.Value })
                            |> List.length } ]

            let studied =
                StudyTime.minutesStudiedThisWeek
                    Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek
                    now
                    weekSessions

            return facts, studied
        })
        Loaded
        Failed

let init (services: AppServices) : Model * Cmd<Msg> =
    { Facts = []
      Filters = ModulesLogic.Selection.empty
      Expanded = None
      Tabs = Map.empty
      Draft = None
      PendingDelete = false
      StudiedThisWeek = 0
      Saving = false
      Loading = true
      Error = None
      Width = 1400.0 },
    loadCmd services

// ---------------------------------------------------------------------------
// editor plumbing — behaviour preserved exactly
// ---------------------------------------------------------------------------

let private draftOf (m: StudyModule) : Draft =
    { Original = Some m
      Code = m.Code
      Name = m.Name
      Lecturer = m.Lecturer |> Option.defaultValue ""
      CreditsText = m.Credits |> Option.map string |> Option.defaultValue ""
      Term = m.Term |> Option.defaultValue ""
      Description = m.Description |> Option.defaultValue ""
      ColorIndex =
        StudyModule.palette
        |> List.tryFindIndex (fun c -> String.Equals(c, m.Color, StringComparison.OrdinalIgnoreCase))
        |> Option.defaultValue 0
      Archived = m.Archived
      FormError = None }

let private emptyDraft (colorIndex: int) : Draft =
    { Original = None
      Code = ""
      Name = ""
      Lecturer = ""
      CreditsText = ""
      Term = ""
      Description = ""
      ColorIndex = colorIndex
      Archived = false
      FormError = None }

let private buildModule (now: DateTime) (draft: Draft) : Result<StudyModule, string> =
    if String.IsNullOrWhiteSpace draft.Code && String.IsNullOrWhiteSpace draft.Name then
        Error "A module needs at least a code or a name."
    else
        let baseRecord =
            match draft.Original with
            | Some original -> original
            | None -> StudyModule.create now draft.Code draft.Name draft.ColorIndex

        Ok
            { baseRecord with
                Code = draft.Code.Trim().ToUpperInvariant()
                Name = draft.Name.Trim()
                Lecturer = (if String.IsNullOrWhiteSpace draft.Lecturer then None else Some(draft.Lecturer.Trim()))
                Credits = Format.parseIntOpt draft.CreditsText
                Term = (if String.IsNullOrWhiteSpace draft.Term then None else Some(draft.Term.Trim()))
                Description = (if String.IsNullOrWhiteSpace draft.Description then None else Some draft.Description)
                Color = StudyModule.palette.[draft.ColorIndex % StudyModule.palette.Length]
                Archived = draft.Archived
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

    match msg with
    | Load -> { model with Loading = true }, loadCmd services, None

    | Loaded(facts, studied) ->
        // An expansion whose module has gone is dropped rather than left dangling.
        let expanded =
            model.Expanded
            |> Option.filter (fun id -> facts |> List.exists (fun f -> f.Module.Id = id))

        plain
            { model with
                Facts = facts
                StudiedThisWeek = studied
                Expanded = expanded
                Loading = false
                Error = None }

    | Failed ex ->
        plain
            { model with
                Loading = false
                Saving = false
                PendingDelete = false
                Error = Some ex.Message }

    | ToggleFilter filter ->
        plain { model with Filters = ModulesLogic.Selection.toggle filter model.Filters }

    | ToggleExpanded id ->
        plain { model with Expanded = (if model.Expanded = Some id then None else Some id) }

    | SetTab(id, tab) -> plain { model with Tabs = model.Tabs.Add(id.Value, tab) }

    | GotoPage page -> model, Cmd.none, Some(Goto page)
    | Resized width -> plain { model with Width = width }

    // ---- editor ----
    | NewModule ->
        plain
            { model with
                Draft = Some(emptyDraft (model.Facts.Length % StudyModule.palette.Length))
                PendingDelete = false }

    | EditModule m -> plain { model with Draft = Some(draftOf m); PendingDelete = false }
    | CloseDraft -> plain { model with Draft = None; PendingDelete = false }

    | SetCode v -> plain (withDraft (fun d -> { d with Code = v }))
    | SetName v -> plain (withDraft (fun d -> { d with Name = v }))
    | SetLecturer v -> plain (withDraft (fun d -> { d with Lecturer = v }))
    | SetCredits v -> plain (withDraft (fun d -> { d with CreditsText = v }))
    | SetTerm v -> plain (withDraft (fun d -> { d with Term = v }))
    | SetDescription v -> plain (withDraft (fun d -> { d with Description = v }))
    | SetColorIndex i -> plain (withDraft (fun d -> { d with ColorIndex = i }))
    | ToggleArchivedFlag -> plain (withDraft (fun d -> { d with Archived = not d.Archived }))

    // The re-entry guard: a second Save while one is running is dropped.
    | Save when model.Saving -> plain model
    | Save ->
        match model.Draft with
        | None -> plain model
        | Some draft ->
            match buildModule services.Clock.Now draft with
            | Error message -> plain { model with Draft = Some { draft with FormError = Some message } }
            | Ok m ->
                { model with Saving = true; Draft = None },
                Elm.ofAsync
                    (async {
                        do! services.Modules.Save m

                        do! services.Activity.Log(
                                ActivityEntry.create
                                    services.Clock.Now
                                    ActivityKind.ModuleUpdated
                                    (StudyModule.displayName m)
                                    None
                                    (Some { Kind = EntityKind.Module; Id = m.Id.Value })
                            )
                    })
                    (fun _ -> Saved)
                    Failed,
                None

    | Saved -> { model with Saving = false }, loadCmd services, None

    // Deleting a module is irreversible and orphans everything filed under it,
    // so it takes an explicit confirmation step.
    | RequestDelete -> plain { model with PendingDelete = true }
    | CancelDelete -> plain { model with PendingDelete = false }
    | ConfirmDelete when model.Saving -> plain model
    | ConfirmDelete ->
        match model.Draft |> Option.bind (fun d -> d.Original) with
        | Some original ->
            { model with Saving = true; PendingDelete = false; Draft = None },
            Elm.ofAsync (async { return! services.Modules.Delete original.Id }) (fun _ -> Deleted) Failed,
            None
        | None -> plain { model with Draft = None; PendingDelete = false }

    | Deleted -> { model with Saving = false }, loadCmd services, None

// ---------------------------------------------------------------------------
// view — shared treatments
// ---------------------------------------------------------------------------

let private contentHighlightScale = ModulesLogic.Geometry.contentHighlight

/// **The one card material on this screen.** Large module cards, compact
/// archived cards, metric cards and the editor all draw through this: one
/// surface, one highlight behaviour, one border. Only padding, span, minimum
/// height and content differ.
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
                              StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                              StackPanel.children children ]
                    ) ] ) ]
    :> IView

let private plainCard (tokens: Tokens) (padding: float) (children: IView list) =
    cardWith tokens padding DashboardLogic.Card.radius (Theme.surfaces ()).Card VerticalAlignment.Top children

// ---- text roles ------------------------------------------------------------

let private weight (w: int) = enum<FontWeight> w

/// A page-level caption: ACTIVE MODULES, ARCHIVED MODULES.
let private sectionCaption (text: string) : IView =
    TextBlock.create
        [ TextBlock.text (text.ToUpperInvariant())
          TextBlock.fontSize Theme.fontXs
          TextBlock.fontWeight (weight 600)
          TextBlock.foreground (Theme.label ())
          TextBlock.margin (2.0, 0.0, 0.0, 0.0) ]
    :> IView

/// A small uppercase label above a figure.
let private figureLabel (text: string) : IView =
    TextBlock.create
        [ TextBlock.text (text.ToUpperInvariant())
          TextBlock.fontSize 10.0
          TextBlock.fontWeight (weight 400)
          TextBlock.foreground (Theme.faint ()) ]
    :> IView

let private bulletRow (glyph: string) (glyphBrush: IBrush) (text: string) (textBrush: IBrush) : IView =
    Grid.create
        [ Grid.columnDefinitions ModulesLogic.Geometry.bulletColumns
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
    Theme.compositeOver surface (Theme.withAlphaHex "1F" hex)

let private chip (tokens: Tokens) (surface: Color) (text: string) (hex: string) : IView =
    Border.create
        [ Border.background (Theme.brush (Theme.withAlphaHex "1F" hex))
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

/// A real Button whose visible surface is a GlassPanel, as on every migrated
/// screen. Identical geometry in every state.
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

/// One of the redesign's small square glyph buttons, always visible on a card.
///
/// A glyph on its own is invisible to a screen reader and ambiguous to everyone
/// else, so the label is carried **twice**: as the accessible name, and as a
/// tooltip. That is the same answer the collapsed sidebar rail already uses, and
/// it keeps the redesign's compact geometry instead of trading it for four
/// full-width labelled buttons on every card.
let private iconAction
    (tokens: Tokens)
    (glyph: string)
    (label: string)
    (hex: string)
    (enabled: bool)
    (onClick: unit -> unit)
    : IView =
    // Hover is the same veil the labelled glass actions use — a topmost
    // hit-testable Border with only inert content above it — rather than the
    // Fluent presenter fill, because a local `Button.background` value outranks
    // the style setter in *every* state and would kill the hover outright.
    Button.create
        [ Button.classes [ Ui.glassActionClass () ]
          Button.isEnabled enabled
          Button.background Theme.transparent
          Button.borderThickness 0.0
          Button.padding 0.0
          Button.width ModulesLogic.Geometry.iconActionSize
          Button.height ModulesLogic.Geometry.iconActionSize
          Button.cornerRadius (Tokens.radius tokens ModulesLogic.Geometry.iconActionRadius)
          Button.clipToBounds false
          Button.margin (0.0, 0.0, 6.0, 0.0)
          Button.cursor Ui.hand
          ToolTip.tip label
          Ui.accessibleName label
          Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always)
          Button.content (
              GlassPanel.create
                  [ GlassPanel.baseRadius ModulesLogic.Geometry.iconActionRadius
                    GlassPanel.elevation 0
                    GlassPanel.borderInset ModulesLogic.Geometry.borderInset
                    GlassPanel.highlightScale contentHighlightScale
                    GlassPanel.tint (Theme.surfaces ()).Action
                    GlassPanel.child (
                        Panel.create
                            [ Panel.children
                                  [ Border.create
                                        [ Border.classes [ Ui.glassVeilClass () ]
                                          Border.background (
                                              Theme.brush (
                                                  Theme.withAlphaHex "2E" (Ui.hexOf (Theme.surfaces ()).Text)
                                              )
                                          )
                                          Border.cornerRadius (
                                              Tokens.radius tokens ModulesLogic.Geometry.iconActionRadius
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
                                                            Theme.readableOnColor
                                                                (Theme.surfaces ()).Action
                                                                hex
                                                        )
                                                    ) ] ) ] ] ] ) ] ) ]
    :> IView

/// The redesign's archived-card links: quiet, chromeless text. No fill and no
/// outline in either state, so the row reads as a list of destinations rather
/// than a bank of buttons — but it is a real `Button`, so it keeps focus, keyed
/// activation and an accessible name.
let private quietLink (label: string) (accessibleName: string) (onClick: unit -> unit) : IView =
    Button.create
        [ Button.background Theme.transparent
          Button.borderThickness 0.0
          Button.padding (0.0, 2.0)
          Button.margin (0.0, 0.0, 12.0, 0.0)
          Button.fontSize Theme.Type.metricLabel
          Button.foreground (Theme.faint ())
          Button.cursor Ui.hand
          Button.content label
          Ui.accessibleName accessibleName
          Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always) ]
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
// sections
// ---------------------------------------------------------------------------

let private header (tokens: Tokens) : IView =
    StackPanel.create
        [ StackPanel.spacing 6.0
          StackPanel.horizontalAlignment HorizontalAlignment.Left
          StackPanel.maxWidth tokens.ReadingWidth
          StackPanel.children
              [ TextBlock.create
                    [ TextBlock.text "Modules"
                      TextBlock.fontSize Theme.Type.pageTitle
                      TextBlock.fontWeight FontWeight.Bold
                      TextBlock.foreground (Theme.text ()) ]
                TextBlock.create
                    [ TextBlock.text "How am I progressing in each module?"
                      TextBlock.fontSize Theme.Type.pageMeta
                      TextBlock.foreground (Theme.muted ())
                      TextBlock.textWrapping TextWrapping.Wrap ] ] ]
    :> IView

let private metricCard (tokens: Tokens) (metric: ModulesLogic.Metric) : IView =
    cardWith
        tokens
        14.0
        DashboardLogic.Card.radius
        (Theme.surfaces ()).Card
        VerticalAlignment.Center
        [ StackPanel.create
              [ StackPanel.spacing 2.0
                StackPanel.children
                    [ TextBlock.create
                          [ TextBlock.text (metric.Value |> Option.defaultValue "—")
                            TextBlock.fontSize 22.0
                            TextBlock.fontWeight (weight 700)
                            TextBlock.foreground (Theme.text ())
                            TextBlock.textTrimming TextTrimming.CharacterEllipsis ]
                      TextBlock.create
                          [ TextBlock.text metric.Label
                            TextBlock.fontSize 12.0
                            TextBlock.fontWeight (weight 500)
                            TextBlock.foreground (Theme.muted ())
                            TextBlock.textWrapping TextWrapping.Wrap ]
                      match metric.Note with
                      | Some note ->
                          TextBlock.create
                              [ TextBlock.text note
                                TextBlock.fontSize 12.0
                                TextBlock.foreground (Theme.faint ())
                                TextBlock.textWrapping TextWrapping.Wrap ]
                      | None -> () ] ] ]

let private filtersSection (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let page = (Theme.surfaces ()).Window
    let selectedFill = Theme.compositeOver page (Theme.accentSoftHex ())
    let selectedRing = Theme.brush (Theme.visibleOnColor page (Theme.accentHex ()))

    let chipFor (filter: ModulesLogic.Filter) : IView =
        let isOn = model.Filters.Contains filter
        let label = ModulesLogic.Filter.label filter

        Button.create
            [ Button.background (if isOn then Theme.accentSoft () else Theme.transparent)
              Button.borderThickness ModulesLogic.Geometry.filterBorderThickness
              Button.borderBrush (if isOn then selectedRing else Theme.border ())
              Button.foreground (
                  if isOn then Theme.brush (Theme.readableOnColor selectedFill (Theme.accentHex ()))
                  else Theme.muted ()
              )
              Button.cornerRadius (Tokens.radius tokens ModulesLogic.Geometry.filterRadius)
              Button.minHeight ModulesLogic.Geometry.filterMinHeight
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
                          glassAction tokens "＋ New module" false (not model.Saving) "Create a new module" (fun () ->
                              dispatch NewModule)
                      ) ]
                WrapPanel.create [ WrapPanel.children (ModulesLogic.Filter.all |> List.map chipFor) ] ] ]
    :> IView

// ---------------------------------------------------------------------------
// the large module card
// ---------------------------------------------------------------------------

/// A ring showing coursework progress. The redesign's conic gradient has no
/// FuncUI equivalent (no shape DSL), so this is the project's established
/// answer: a bordered ring carrying the colour, with the figure in the middle.
let private progressRing (size: float) (percent: int option) (hex: string) : IView =
    let p = percent |> Option.defaultValue 0 |> max 0 |> min 100

    Panel.create
        [ Panel.width size
          Panel.height size
          Panel.children
              [ Border.create
                    [ Border.width size
                      Border.height size
                      Border.cornerRadius (size / 2.0)
                      Border.borderThickness 3.0
                      Border.borderBrush (Theme.brush (Theme.withAlphaHex "26" hex))
                      Border.background (Theme.brush (Theme.withAlphaHex "10" hex))
                      Ui.decorative () ]
                Border.create
                    [ Border.width size
                      Border.height size
                      Border.cornerRadius (size / 2.0)
                      Border.borderThickness (if p > 0 then 3.0 else 0.0)
                      Border.borderBrush (Theme.brush (Theme.withAlphaHex (if p >= 100 then "FF" else "9A") hex))
                      Ui.decorative () ]
                TextBlock.create
                    [ TextBlock.text (
                          match percent with
                          | Some v -> string v
                          | None -> "—"
                      )
                      TextBlock.fontSize (size / 4.2)
                      TextBlock.fontWeight FontWeight.SemiBold
                      TextBlock.foreground (Theme.text ())
                      TextBlock.horizontalAlignment HorizontalAlignment.Center
                      TextBlock.verticalAlignment VerticalAlignment.Center ] ] ]
    :> IView

let private tabContent (tokens: Tokens) (now: DateTime) (f: ModulesLogic.Facts) (tab: ModulesLogic.Tab) : IView =
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

    let rows: IView list =
        match tab with
        | ModulesLogic.Tab.Assignments ->
            f.Assignments
            |> List.map (fun a ->
                row
                    a.Assignment.Title
                    (sprintf
                        "%d%% · %s"
                        (AssignmentsLogic.progress a)
                        (match a.Assignment.Deadline with
                         | Some d -> Format.shortDate d
                         | None -> "no deadline")))
        | ModulesLogic.Tab.Lectures ->
            f.Lectures
            |> List.map (fun l ->
                row l.Title (l.Date |> Option.map Format.shortDate |> Option.defaultValue "undated"))
        | ModulesLogic.Tab.Notes ->
            f.Notes |> List.map (fun n -> row n.Title (Format.relativeTime now n.UpdatedAt))
        | ModulesLogic.Tab.Documents ->
            f.Documents |> List.map (fun d -> row d.Title (DocumentKind.label d.Kind))
        | ModulesLogic.Tab.Flashcards ->
            f.Flashcards |> List.truncate 8 |> List.map (fun r -> row (Format.truncate 70 r.Question) "card")
        | ModulesLogic.Tab.Calendar ->
            f.Events
            |> List.sortBy (fun e -> e.StartsAt)
            |> List.truncate 8
            |> List.map (fun e -> row e.Title (Format.shortDate e.StartsAt + " · " + EventKind.label e.Kind))
        | ModulesLogic.Tab.Planner ->
            f.Sessions
            |> List.sortBy (fun s -> s.StartsAt)
            |> List.truncate 8
            |> List.map (fun s -> row s.Title (Format.shortDate s.StartsAt + " " + Format.time s.StartsAt))
        | ModulesLogic.Tab.Timeline ->
            [ if f.ActivityCount > 0 then
                  row (sprintf "%d entries recorded against this module" f.ActivityCount) "" ]

    Border.create
        [ Border.background (Theme.cardAlt ())
          Border.cornerRadius (Tokens.radius tokens 12.0)
          Border.padding (14.0, 12.0)
          Border.minHeight 96.0
          Border.child (
              if rows.IsEmpty then
                  bulletRow "•" (Theme.faint ()) (ModulesLogic.Tab.emptyMessage tab) (Theme.muted ())
              else
                  StackPanel.create [ StackPanel.spacing 2.0; StackPanel.children rows ] :> IView
          ) ]
    :> IView

let private moduleCard
    (tokens: Tokens)
    (now: DateTime)
    (model: Model)
    (dispatch: Msg -> unit)
    (f: ModulesLogic.Facts)
    : IView =
    let m = f.Module
    let isExpanded = model.Expanded = Some m.Id
    let surface = if isExpanded then (Theme.surfaces ()).Selected else (Theme.surfaces ()).Card
    let hex = m.Color
    let attention = ModulesLogic.needsAttention now f
    let activeTab = model.Tabs.TryFind m.Id.Value |> Option.defaultValue ModulesLogic.Tab.Assignments

    let headerRow: IView =
        Grid.create
            [ Grid.columnDefinitions "Auto,10,*,10,Auto,8,Auto"
              Grid.children
                  [ Border.create
                        [ Grid.column 0
                          Border.width ModulesLogic.Geometry.moduleDotSize
                          Border.height ModulesLogic.Geometry.moduleDotSize
                          Border.cornerRadius (ModulesLogic.Geometry.moduleDotSize / 2.0)
                          Border.background (Theme.brush hex)
                          Border.verticalAlignment VerticalAlignment.Center
                          Ui.decorative () ]

                    TextBlock.create
                        [ Grid.column 2
                          TextBlock.text (if String.IsNullOrWhiteSpace m.Name then m.Code else m.Name)
                          TextBlock.fontSize 18.0
                          TextBlock.fontWeight FontWeight.SemiBold
                          TextBlock.foreground (Theme.text ())
                          TextBlock.textWrapping TextWrapping.Wrap
                          TextBlock.verticalAlignment VerticalAlignment.Center ]

                    Border.create
                        [ Grid.column 4
                          Border.verticalAlignment VerticalAlignment.Center
                          Border.child (chip tokens surface m.Code hex) ]

                    Border.create
                        [ Grid.column 6
                          Border.verticalAlignment VerticalAlignment.Center
                          Border.child (
                              StackPanel.create
                                  [ StackPanel.orientation Orientation.Horizontal
                                    StackPanel.spacing 8.0
                                    StackPanel.children
                                        [ progressRing
                                              ModulesLogic.Geometry.dialSize
                                              (ModulesLogic.courseworkProgress f)
                                              hex
                                          Button.create
                                              [ Button.content (if isExpanded then "▾" else "▸")
                                                Button.width ModulesLogic.Geometry.chevronWidth
                                                Button.background Theme.transparent
                                                Button.borderThickness 0.0
                                                Button.padding 0.0
                                                Button.fontSize Theme.Type.metricLabel
                                                Button.foreground (Theme.muted ())
                                                Button.verticalAlignment VerticalAlignment.Center
                                                Button.cursor Ui.hand
                                                Ui.accessibleName (
                                                    if isExpanded then sprintf "Collapse %s" m.Code
                                                    else sprintf "Expand %s" m.Code
                                                )
                                                Button.onClick (
                                                    (fun _ -> dispatch (ToggleExpanded m.Id)),
                                                    SubPatchOptions.Always
                                                ) ] ] ]
                          ) ] ] ]
        :> IView

    let figuresRow: IView =
        Grid.create
            [ Grid.columnDefinitions "1*,10,1*,10,1*,10,1*"
              Grid.children
                  (ModulesLogic.cardFigures now f
                   |> List.mapi (fun i fig ->
                       StackPanel.create
                           [ Grid.column (i * 2)
                             StackPanel.spacing 1.0
                             StackPanel.children
                                 [ figureLabel fig.Label
                                   TextBlock.create
                                       [ TextBlock.text (fig.Value |> Option.defaultValue "—")
                                         TextBlock.fontSize 15.0
                                         TextBlock.fontWeight FontWeight.SemiBold
                                         TextBlock.foreground (Theme.text ()) ] ] ]
                       :> IView)) ]
        :> IView

    let countsRow: IView =
        WrapPanel.create
            [ WrapPanel.children
                  (ModulesLogic.cardCounts f
                   |> List.map (fun (label, count) ->
                       Border.create
                           [ Border.margin (0.0, 0.0, 16.0, 4.0)
                             Border.child (
                                 StackPanel.create
                                     [ StackPanel.orientation Orientation.Horizontal
                                       StackPanel.spacing 6.0
                                       StackPanel.children
                                           [ TextBlock.create
                                                 [ TextBlock.text (string count)
                                                   TextBlock.fontSize Theme.Type.rowMeta
                                                   TextBlock.fontWeight FontWeight.SemiBold
                                                   TextBlock.foreground (
                                                       if count > 0 then Theme.text () else Theme.faint ()
                                                   ) ]
                                             TextBlock.create
                                                 [ TextBlock.text label
                                                   TextBlock.fontSize Theme.Type.metricLabel
                                                   TextBlock.foreground (Theme.faint ())
                                                   TextBlock.verticalAlignment VerticalAlignment.Center ] ] ]
                             ) ]
                       :> IView)) ]
        :> IView

    let metaRow: IView =
        WrapPanel.create
            [ WrapPanel.children
                  [ match ModulesLogic.nextDeadline now f with
                    | Some(d, title) ->
                        Border.create
                            [ Border.margin (0.0, 0.0, 14.0, 4.0)
                              Border.child (
                                  chip
                                      tokens
                                      surface
                                      (sprintf "%s · %s" (Format.shortDate d) (Format.truncate 26 title))
                                      (if attention then Theme.warningHex () else hex)
                              ) ]
                    | None -> ()

                    TextBlock.create
                        [ TextBlock.text (
                              sprintf "%s remaining" (Format.hours (float (ModulesLogic.remainingMinutes f) / 60.0))
                          )
                          TextBlock.fontSize Theme.Type.rowMeta
                          TextBlock.foreground (Theme.muted ())
                          TextBlock.margin (0.0, 0.0, 14.0, 4.0) ]

                    TextBlock.create
                        [ TextBlock.text (
                              match ModulesLogic.nextSession now f with
                              | Some s -> sprintf "Next: %s · %s" (Format.truncate 24 s.Title) (Format.shortDate s.StartsAt)
                              | None -> "No study session scheduled"
                          )
                          TextBlock.fontSize Theme.Type.rowMeta
                          TextBlock.foreground (Theme.faint ())
                          TextBlock.margin (0.0, 0.0, 14.0, 4.0)
                          TextBlock.textTrimming TextTrimming.CharacterEllipsis ] ] ]
        :> IView

    let upcoming: IView =
        let cell (label: string) (value: string) : IView =
            StackPanel.create
                [ StackPanel.spacing 2.0
                  StackPanel.margin (0.0, 0.0, 18.0, 8.0)
                  StackPanel.width 150.0
                  StackPanel.children
                      [ figureLabel label
                        TextBlock.create
                            [ TextBlock.text value
                              TextBlock.fontSize Theme.Type.rowMeta
                              TextBlock.foreground (Theme.text ())
                              TextBlock.textWrapping TextWrapping.Wrap ] ] ]
            :> IView

        // Order and count come from `ModulesLogic.upcomingCells`, not from this
        // list, so a cell cannot go missing without a test noticing.
        StackPanel.create
            [ StackPanel.spacing 6.0
              StackPanel.children
                  [ sectionCaption "Upcoming work"
                    WrapPanel.create
                        [ WrapPanel.children
                              (ModulesLogic.upcomingCells now f
                               |> List.map (fun (label, value) ->
                                   cell label (value |> Option.defaultValue "—"))) ] ] ]
        :> IView

    /// The redesign's always-visible glyph actions, on the collapsed card too.
    let primaryRow: IView =
        StackPanel.create
            [ StackPanel.orientation Orientation.Horizontal
              StackPanel.children
                  (ModulesLogic.primaryActions
                   |> List.map (fun (glyph, label, target) ->
                       match target with
                       | Some page ->
                           iconAction tokens glyph label hex true (fun () -> dispatch (GotoPage page))
                       | None ->
                           iconAction tokens glyph label hex (not model.Saving) (fun () ->
                               dispatch (EditModule m)))) ]
        :> IView

    let tabsRow: IView =
        WrapPanel.create
            [ WrapPanel.children
                  (ModulesLogic.Tab.all
                   |> List.map (fun tab ->
                       let isOn = tab = activeTab
                       let count = ModulesLogic.tabCount f tab

                       Button.create
                           [ Button.background (
                                 if isOn then Theme.brush (Theme.withAlphaHex "1F" hex) else Theme.transparent
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
                                 if count > 0 then sprintf "%s (%d)" (ModulesLogic.Tab.label tab) count
                                 else ModulesLogic.Tab.label tab
                             )
                             Ui.accessibleName (sprintf "%s tab for %s" (ModulesLogic.Tab.label tab) m.Code)
                             Button.onClick ((fun _ -> dispatch (SetTab(m.Id, tab))), SubPatchOptions.Always) ]
                       :> IView)) ]
        :> IView

    let quickActions: IView =
        WrapPanel.create
            [ WrapPanel.children
                  [ glassAction tokens "Edit module" false (not model.Saving) (sprintf "Edit %s" m.Code) (fun () ->
                        dispatch (EditModule m))
                    glassAction tokens "Assignments" false true "Go to Assignments" (fun () ->
                        dispatch (GotoPage Page.Assignments))
                    glassAction tokens "Planner" false true "Go to the Planner" (fun () ->
                        dispatch (GotoPage Page.Planner))
                    glassAction tokens "Notes" false true "Go to Notes" (fun () -> dispatch (GotoPage Page.Notes))
                    glassAction tokens "Documents" false true "Go to Documents" (fun () ->
                        dispatch (GotoPage Page.Documents))
                    glassAction tokens "Timeline" false true "Go to the Timeline" (fun () ->
                        dispatch (GotoPage Page.Timeline)) ] ]
        :> IView

    /// The redesign carries one-to-two ✦ bullets here, not a single line.
    let insightRows: IView list =
        ModulesLogic.insights now f
        |> List.map (fun line -> bulletRow "✦" (Theme.brush hex) line (Theme.textSecondary ()))

    let expansionDivider: IView =
        Border.create
            [ Border.height 1.0
              Border.margin (0.0, 2.0)
              Border.background (Theme.divider ())
              Ui.decorative () ]
        :> IView

    /// The expanded regions are drawn by mapping `ModulesLogic.expandedOrder`
    /// rather than by writing the sequence out here, so the redesign's order —
    /// upcoming work, actions, tabs, panel — is a fact a test can pin rather
    /// than an accident of how this list happens to be typed.
    let expandedRegion (region: ModulesLogic.ExpandedRegion) : IView =
        match region with
        | ModulesLogic.ExpandedRegion.Divider -> expansionDivider
        | ModulesLogic.ExpandedRegion.UpcomingWork -> upcoming
        | ModulesLogic.ExpandedRegion.Actions -> quickActions
        | ModulesLogic.ExpandedRegion.Tabs -> tabsRow
        | ModulesLogic.ExpandedRegion.TabPanel -> tabContent tokens now f activeTab

    let cardBody: IView list =
        [ headerRow ]
        @ insightRows
        @ [ figuresRow; countsRow; metaRow; primaryRow ]
        @ (if isExpanded then ModulesLogic.expandedOrder |> List.map expandedRegion else [])

    cardWith
        tokens
        20.0
        ModulesLogic.Geometry.cardRadius
        surface
        VerticalAlignment.Top
        [ Border.create
              [ // **No expansion outline** — the same correction as Assignments.
                // This painted the module colour around the whole of the card's
                // content when expanded, reported in the first runtime review as
                // an unintended line. The module's identity is already carried by
                // its colour elsewhere on the card, and expansion by the surface
                // change, so the outline added nothing but the line.
                //
                // The thickness stays in both states, so removing the colour
                // cannot move anything. Keyboard focus is drawn by Fluent's
                // adorner on the header button, not here.
                Border.borderThickness ModulesLogic.Geometry.cardBorderThickness
                Border.borderBrush Theme.transparent
                Border.cornerRadius (Tokens.radius tokens ModulesLogic.Geometry.cardRadius)
                Border.child (
                    StackPanel.create
                        [ StackPanel.spacing (Tokens.space tokens 12.0)
                          StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                          StackPanel.children cardBody ]
                ) ] ]

/// A compact archived card — the redesign's lower four-across grid.
///
/// Its "final grade" is **absent**: no mark is stored anywhere. The real
/// contents of the module take that place.
let private archivedCard (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) (f: ModulesLogic.Facts) : IView =
    let m = f.Module

    cardWith
        tokens
        16.0
        14.0
        (Theme.surfaces ()).Action
        VerticalAlignment.Top
        [ Grid.create
              [ Grid.columnDefinitions "*,8,Auto"
                Grid.children
                    [ TextBlock.create
                          [ Grid.column 0
                            TextBlock.text (if String.IsNullOrWhiteSpace m.Name then m.Code else m.Name)
                            TextBlock.fontSize Theme.Type.rowTitle
                            TextBlock.fontWeight FontWeight.SemiBold
                            TextBlock.foreground (Theme.muted ())
                            TextBlock.textTrimming TextTrimming.CharacterEllipsis ]
                      TextBlock.create
                          [ Grid.column 2
                            TextBlock.text m.Code
                            TextBlock.fontSize Theme.Type.metricLabel
                            TextBlock.foreground (Theme.faint ())
                            TextBlock.verticalAlignment VerticalAlignment.Center ] ] ]

          TextBlock.create
              [ TextBlock.text (m.Term |> Option.defaultValue "No term recorded")
                TextBlock.fontSize Theme.Type.metricLabel
                TextBlock.foreground (Theme.faint ()) ]

          TextBlock.create
              [ TextBlock.text (
                    sprintf
                        "%d assignments · %d notes · %d documents"
                        f.Assignments.Length
                        f.Notes.Length
                        f.Documents.Length
                )
                TextBlock.fontSize Theme.Type.metricLabel
                TextBlock.foreground (Theme.faint ())
                TextBlock.textWrapping TextWrapping.Wrap ]

          // The redesign's link row. It lists five; the fifth is Grades, which
          // nothing stores, so four real destinations remain — plus Edit, which
          // is the one action an archived module still genuinely has.
          WrapPanel.create
              [ WrapPanel.children
                    (quietLink "Edit" (sprintf "Edit %s" m.Code) (fun () -> dispatch (EditModule m))
                     :: (ModulesLogic.archivedLinks
                         |> List.map (fun (label, page) ->
                             quietLink label (sprintf "Open %s" label) (fun () -> dispatch (GotoPage page))))) ] ]

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
                          [ TextBlock.text (if draft.Original.IsSome then "Edit module" else "New module")
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
                                              "Delete this module permanently? Everything filed under it loses its module, and nothing here keeps a prior state, so this cannot be undone."
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

          Grid.create
              [ Grid.columnDefinitions "1*,12,2*"
                Grid.children
                    [ StackPanel.create
                          [ Grid.column 0
                            StackPanel.children [ Ui.labeled "Code" (Ui.textBox draft.Code "CS2004" (SetCode >> dispatch)) ] ]
                      StackPanel.create
                          [ Grid.column 2
                            StackPanel.children
                                [ Ui.labeled "Name" (Ui.textBox draft.Name "Computer Graphics" (SetName >> dispatch)) ] ] ] ]

          Grid.create
              [ Grid.columnDefinitions "2*,12,1*,12,1*"
                Grid.children
                    [ StackPanel.create
                          [ Grid.column 0
                            StackPanel.children
                                [ Ui.labeled "Lecturer" (Ui.textBox draft.Lecturer "Dr …" (SetLecturer >> dispatch)) ] ]
                      StackPanel.create
                          [ Grid.column 2
                            StackPanel.children
                                [ Ui.labeled "Credits" (Ui.textBox draft.CreditsText "20" (SetCredits >> dispatch)) ] ]
                      StackPanel.create
                          [ Grid.column 4
                            StackPanel.children
                                [ Ui.labeled "Term" (Ui.textBox draft.Term "Autumn 2026" (SetTerm >> dispatch)) ] ] ] ]

          Ui.labeled
              "Colour"
              (WrapPanel.create
                  [ WrapPanel.children
                        (StudyModule.palette
                         |> List.mapi (fun i hex ->
                             Button.create
                                 [ Button.width 26.0
                                   Button.height 26.0
                                   Button.margin (0.0, 0.0, 6.0, 6.0)
                                   Button.cornerRadius 13.0
                                   Button.background (Theme.brush hex)
                                   Button.borderThickness (if i = draft.ColorIndex then 3.0 else 0.0)
                                   Button.borderBrush (Theme.text ())
                                   Button.cursor Ui.hand
                                   Ui.accessibleName (sprintf "Colour %d" (i + 1))
                                   Button.onClick ((fun _ -> dispatch (SetColorIndex i)), SubPatchOptions.Always) ]
                             :> IView)) ])

          Ui.labeled
              "Description"
              (Ui.multilineBox draft.Description "What this module covers…" 70.0 (SetDescription >> dispatch))

          StackPanel.create
              [ StackPanel.orientation Orientation.Horizontal
                StackPanel.spacing 8.0
                StackPanel.children
                    [ Ui.checkGlyph draft.Archived (fun () -> dispatch ToggleArchivedFlag)
                      TextBlock.create
                          [ TextBlock.text "Archived (hidden from planning and AI context)"
                            TextBlock.fontSize Theme.Type.rowMeta
                            TextBlock.foreground (Theme.muted ())
                            TextBlock.verticalAlignment VerticalAlignment.Center ] ] ] ]

// ---------------------------------------------------------------------------
// page
// ---------------------------------------------------------------------------

let view (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let spacing = Tokens.space tokens 22.0
    let now = DateTime.Now

    let active = ModulesLogic.activeGrid now model.Filters model.Facts
    let archived = ModulesLogic.archivedGrid model.Facts

    let activeSection: IView =
        StackPanel.create
            [ StackPanel.spacing (Tokens.space tokens 12.0)
              StackPanel.children
                  [ sectionCaption "Active modules"

                    if model.Loading && model.Facts.IsEmpty then
                        cardRow spacing 0.0 (ModulesLogic.cardColumns model.Width) [ Ui.skeletonCard 5; Ui.skeletonCard 5 ]
                    elif active.IsEmpty then
                        plainCard
                            tokens
                            20.0
                            [ bulletRow
                                  "•"
                                  (Theme.faint ())
                                  (ModulesLogic.emptyActiveMessage model.Filters)
                                  (Theme.muted ()) ]
                    else
                        cardRow
                            spacing
                            0.0
                            (ModulesLogic.cardColumns model.Width)
                            (active |> List.map (moduleCard tokens now model dispatch)) ] ]
        :> IView

    let archivedSection: IView =
        StackPanel.create
            [ StackPanel.spacing (Tokens.space tokens 12.0)
              StackPanel.children
                  [ sectionCaption "Archived modules"

                    if archived.IsEmpty then
                        plainCard
                            tokens
                            16.0
                            [ bulletRow
                                  "•"
                                  (Theme.faint ())
                                  "No modules have been archived. Archiving hides a module from planning and AI context without deleting anything."
                                  (Theme.muted ()) ]
                    else
                        cardRow
                            spacing
                            0.0
                            (ModulesLogic.archivedColumns model.Width)
                            (archived |> List.map (archivedCard tokens model dispatch)) ] ]
        :> IView

    let renderSection (section: ModulesLogic.Section) : IView list =
        match section with
        | ModulesLogic.Section.Header -> [ header tokens ]
        | ModulesLogic.Section.Metrics ->
            [ cardRow
                  spacing
                  ModulesLogic.Geometry.metricRowMinHeight
                  (ModulesLogic.metricColumns model.Width)
                  (ModulesLogic.metrics now model.StudiedThisWeek model.Facts |> List.map (metricCard tokens)) ]
        | ModulesLogic.Section.Filters -> [ filtersSection tokens model dispatch ]
        | ModulesLogic.Section.ActiveSection ->
            if ModulesLogic.Selection.showsActive model.Filters then [ activeSection ] else []
        | ModulesLogic.Section.ArchivedSection ->
            if ModulesLogic.Selection.showsArchived model.Filters then [ archivedSection ] else []

    let body: IView list =
        match model.Draft with
        | Some draft -> [ header tokens; editor tokens model draft dispatch ]
        | None -> ModulesLogic.sectionOrder |> List.collect renderSection

    Border.create
        [ Border.onSizeChanged (
              (fun args ->
                  let decisions (width: float) =
                      ModulesLogic.cardColumns width,
                      ModulesLogic.metricColumns width,
                      ModulesLogic.archivedColumns width

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
                                                                  Ui.smallButton "Try again" (fun () ->
                                                                      dispatch Load)
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

                              yield! body ] ] ) ) ]
    :> IView
