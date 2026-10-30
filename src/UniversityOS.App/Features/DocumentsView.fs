module UniversityOS.App.Features.DocumentsView

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

/// Documents: what academic resources do I have, and how are they connected?
///
/// The redesign's page is a header, an import banner, six analytics cards, a
/// rule-based insight panel, a search-and-view row, three chip rows, and a
/// document grid beside a sticky detail panel. That is the structure here. Every
/// decision lives in `DocumentsLogic`.
///
/// This **replaces** the legacy screen, which was a `2*,16,3*` master-detail
/// split with a file list on the left and a detail form on the right.
///
/// Real behaviour preserved: import through `CaptureService.CaptureFile` (which
/// imports **and** files an Inbox capture), open with the OS handler, delete
/// including best-effort physical cleanup, assign a module, AI summarise from
/// extracted text, and display extracted text. Added: a **missing-file state**
/// built on `File.Exists`, a delete confirmation, an error banner with retry,
/// linked knowledge, the Grid/List toggle and the filter chips.

// ---------------------------------------------------------------------------
// model
// ---------------------------------------------------------------------------

type Model =
    { Facts: DocumentsLogic.Facts list
      Modules: StudyModule list
      Selected: DocumentId option
      View: DocumentsLogic.ViewMode
      Filters: DocumentsLogic.Selection
      ModuleFilter: Set<Guid>
      Query: string
      /// 0 = unfiled, else Modules.[i-1] — the detail panel's module picker.
      ModuleIndex: int
      Importing: bool
      Summarizing: bool
      PendingDelete: bool
      Notice: string option
      Loading: bool
      Error: string option
      Width: float }

type External = Goto of Page

type Msg =
    | Load
    | Loaded of DocumentsLogic.Facts list * StudyModule list
    | Failed of exn
    | Select of DocumentId
    | ClearSelection
    | SetView of DocumentsLogic.ViewMode
    | ToggleFilter of DocumentsLogic.Filter
    | ToggleModule of ModuleId
    | SetQuery of string
    | Import
    | Imported of int
    | OpenFile
    | OpenFailed of string
    | Summarize
    | Summarized of Result<string, AiError>
    | AssignModule of int
    | ModuleAssigned
    | RequestDelete
    | CancelDelete
    | ConfirmDelete
    | Deleted
    | GotoPage of Page
    | Resized of float
    | ClearNotice

// ---------------------------------------------------------------------------
// loading
// ---------------------------------------------------------------------------

/// One command, one snapshot.
///
/// **`File.Exists` is resolved here, once per load**, rather than in the view: a
/// view function runs on every patch, and hitting the filesystem from one would
/// put disk I/O on the render path.
let private loadCmd (services: AppServices) =
    Elm.ofAsync
        (async {
            let! documents = services.Documents.GetAll()
            let! modules = services.Modules.GetAll()
            let! links = services.Links.GetAll()
            let! revision = services.Revision.GetAll()
            let! lectures = services.Lectures.GetAll()
            let! inbox = services.Inbox.GetActive()
            let! entries = services.Activity.GetRecent 300

            let facts =
                documents
                |> List.map (fun d ->
                    let self = { Kind = EntityKind.Document; Id = d.Id.Value }

                    { DocumentsLogic.Document = d
                      DocumentsLogic.Module =
                        d.ModuleId |> Option.bind (fun id -> modules |> List.tryFind (fun m -> m.Id = id))
                      DocumentsLogic.FileExists =
                        not (String.IsNullOrWhiteSpace d.StoredPath)
                        && (try
                                IO.File.Exists d.StoredPath
                            with _ ->
                                false)
                      DocumentsLogic.LinksOut =
                        links |> List.filter (fun l -> l.From = self) |> List.map (fun l -> l.To) |> List.distinct
                      DocumentsLogic.LinksIn =
                        links |> List.filter (fun l -> l.To = self) |> List.map (fun l -> l.From) |> List.distinct
                      DocumentsLogic.LectureCount =
                        lectures
                        |> List.filter (fun l ->
                            l.SlidesDocumentId = Some d.Id || l.RecordingDocumentId = Some d.Id)
                        |> List.length
                      DocumentsLogic.Flashcards =
                        revision |> List.filter (fun r -> r.Source = Some self) |> List.length
                      DocumentsLogic.InboxPending = inbox |> List.exists (fun i -> i.DocumentId = Some d.Id)
                      DocumentsLogic.Activity = entries |> List.filter (fun e -> e.Entity = Some self) })

            return facts, modules
        })
        Loaded
        Failed

let init (services: AppServices) : Model * Cmd<Msg> =
    { Facts = []
      Modules = []
      Selected = None
      View = DocumentsLogic.ViewMode.Grid
      Filters = DocumentsLogic.Selection.empty
      ModuleFilter = Set.empty
      Query = ""
      ModuleIndex = 0
      Importing = false
      Summarizing = false
      PendingDelete = false
      Notice = None
      Loading = true
      Error = None
      Width = 1600.0 },
    loadCmd services

let private selectedFacts (model: Model) =
    model.Selected |> Option.bind (fun id -> model.Facts |> List.tryFind (fun f -> f.Document.Id = id))

let private moduleIndexFor (modules: StudyModule list) (d: Document) =
    match d.ModuleId with
    | Some mid ->
        match modules |> List.tryFindIndex (fun m -> m.Id = mid) with
        | Some i -> i + 1
        | None -> 0
    | None -> 0

// ---------------------------------------------------------------------------
// update
// ---------------------------------------------------------------------------

let update (services: AppServices) (msg: Msg) (model: Model) : Model * Cmd<Msg> * External option =
    let plain m = m, Cmd.none, None

    match msg with
    | Load -> { model with Loading = true }, loadCmd services, None

    | Loaded(facts, modules) ->
        // A selection whose document has gone is dropped rather than left
        // dangling, and so is a module chip whose module has gone.
        let selected =
            model.Selected |> Option.filter (fun id -> facts |> List.exists (fun f -> f.Document.Id = id))

        let moduleFilter =
            model.ModuleFilter |> Set.filter (fun id -> modules |> List.exists (fun m -> m.Id.Value = id))

        let moduleIndex =
            match selected |> Option.bind (fun id -> facts |> List.tryFind (fun f -> f.Document.Id = id)) with
            | Some f -> moduleIndexFor modules f.Document
            | None -> 0

        plain
            { model with
                Facts = facts
                Modules = modules
                Selected = selected
                ModuleFilter = moduleFilter
                ModuleIndex = moduleIndex
                Loading = false
                Error = None }

    | Failed ex ->
        plain
            { model with
                Loading = false
                Importing = false
                Summarizing = false
                PendingDelete = false
                Error = Some ex.Message }

    | Select id ->
        let index =
            match model.Facts |> List.tryFind (fun f -> f.Document.Id = id) with
            | Some f -> moduleIndexFor model.Modules f.Document
            | None -> 0

        plain { model with Selected = Some id; ModuleIndex = index; PendingDelete = false; Notice = None }

    | ClearSelection -> plain { model with Selected = None; PendingDelete = false }

    | SetView mode -> plain { model with View = mode }
    | ToggleFilter filter ->
        plain { model with Filters = DocumentsLogic.Selection.toggle filter model.Filters }

    | ToggleModule id ->
        let next =
            if model.ModuleFilter.Contains id.Value then model.ModuleFilter.Remove id.Value
            else model.ModuleFilter.Add id.Value

        plain { model with ModuleFilter = next }

    | SetQuery q -> plain { model with Query = q }

    // Import goes through CaptureService.CaptureFile, which imports the file
    // AND files an Inbox capture. Both halves are stated in the notice.
    | Import when model.Importing -> plain model
    | Import ->
        { model with Importing = true },
        Elm.ofAsync
            (async {
                let! paths = services.PickFiles()
                let mutable count = 0

                for path in paths do
                    let! result = services.Capture.CaptureFile path

                    match result with
                    | Ok _ -> count <- count + 1
                    | Error _ -> ()

                return count
            })
            Imported
            Failed,
        None

    | Imported count ->
        { model with
            Importing = false
            Notice = (if count = 0 then None else Some(DocumentsLogic.importedNotice count)) },
        Cmd.batch [ loadCmd services; Elm.delayed 7000 ClearNotice ],
        None

    | OpenFile ->
        match selectedFacts model with
        | Some f when f.FileExists ->
            (try
                Process.Start(ProcessStartInfo(FileName = f.Document.StoredPath, UseShellExecute = true))
                |> ignore

                plain model
             with ex ->
                 { model with Notice = Some("Could not open the file: " + ex.Message) },
                 Elm.delayed 8000 ClearNotice,
                 None)
        // The button is disabled when the file is gone; this is the second line
        // of defence.
        | Some _ ->
            { model with Notice = Some "That file is no longer in the store, so it cannot be opened." },
            Elm.delayed 8000 ClearNotice,
            None
        | None -> plain model

    | OpenFailed message ->
        { model with Notice = Some message }, Elm.delayed 8000 ClearNotice, None

    | Summarize when model.Summarizing -> plain model
    | Summarize ->
        match selectedFacts model with
        | Some f when (DocumentsLogic.summariseDisabledReason f).IsNone ->
            let text = DocumentsLogic.extractedText f |> Option.defaultValue ""

            { model with Summarizing = true },
            Elm.ofAsync (services.Ai.Summarize(f.Document.Title, text)) Summarized Failed,
            None
        | _ -> plain model

    | Summarized(Ok summary) ->
        match selectedFacts model with
        | Some f ->
            { model with Summarizing = false },
            Elm.ofAsync (services.Documents.Save { f.Document with Summary = Some summary }) (fun _ -> Load) Failed,
            None
        | None -> plain { model with Summarizing = false }

    | Summarized(Error e) ->
        { model with Summarizing = false; Notice = Some(AiError.describe e) }, Elm.delayed 8000 ClearNotice, None

    | AssignModule index ->
        match selectedFacts model with
        | Some f ->
            let moduleId =
                if index > 0 && index <= model.Modules.Length then Some model.Modules.[index - 1].Id else None

            { model with ModuleIndex = index },
            Elm.ofAsync
                (services.Documents.Save { f.Document with ModuleId = moduleId })
                (fun _ -> ModuleAssigned)
                Failed,
            None
        | None -> plain model

    | ModuleAssigned -> model, loadCmd services, None

    | RequestDelete -> plain { model with PendingDelete = true }
    | CancelDelete -> plain { model with PendingDelete = false }
    | ConfirmDelete ->
        match selectedFacts model with
        | Some f ->
            { model with PendingDelete = false; Selected = None },
            Elm.ofAsync
                (async {
                    do! services.Documents.Delete f.Document.Id

                    // Physical cleanup is best-effort: the library stays
                    // consistent even if the OS blocks the delete, and a file
                    // that was already missing is not an error.
                    try
                        if IO.File.Exists f.Document.StoredPath then
                            IO.File.Delete f.Document.StoredPath
                    with _ ->
                        ()
                })
                (fun _ -> Deleted)
                Failed,
            None
        | None -> plain { model with PendingDelete = false }

    | Deleted -> model, loadCmd services, None

    | GotoPage page -> model, Cmd.none, Some(Goto page)
    | Resized width -> plain { model with Width = width }
    | ClearNotice -> plain { model with Notice = None }

// ---------------------------------------------------------------------------
// view — shared treatments
// ---------------------------------------------------------------------------

let private contentHighlightScale = DocumentsLogic.Geometry.contentHighlight
let private weight (w: int) = enum<FontWeight> w

/// **The one card material on this screen.** Document cards, metric cards, the
/// insight panel and the detail panel all draw through this.
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
                              // sizes to its widest child.
                              StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                              StackPanel.children children ]
                    ) ] ) ]
    :> IView

let private plainCard (tokens: Tokens) (padding: float) (children: IView list) =
    cardWith tokens padding DashboardLogic.Card.radius (Theme.surfaces ()).Card VerticalAlignment.Top children

let private sectionCaption (text: string) : IView =
    TextBlock.create
        [ TextBlock.text (text.ToUpperInvariant())
          TextBlock.fontSize 10.5
          TextBlock.fontWeight (weight 600)
          TextBlock.foreground (Theme.label ())
          TextBlock.textWrapping TextWrapping.Wrap ]
    :> IView

let private bulletRow (glyph: string) (glyphBrush: IBrush) (text: string) (textBrush: IBrush) : IView =
    Grid.create
        [ Grid.columnDefinitions DocumentsLogic.Geometry.bulletColumns
          Grid.margin (0.0, 2.0)
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
    Theme.compositeOver surface (Theme.withAlphaHex DocumentsLogic.Geometry.chipFillAlpha hex)

/// A pill carrying an identity colour. The alpha used to PAINT the wash is the
/// same one used to MEASURE the contrast of the text on it.
let private pill (tokens: Tokens) (surface: Color) (text: string) (hex: string) : IView =
    Border.create
        [ Border.background (Theme.brush (Theme.withAlphaHex DocumentsLogic.Geometry.chipFillAlpha hex))
          Border.cornerRadius (Tokens.radius tokens 999.0)
          Border.padding (9.0, 2.0)
          Border.margin (0.0, 0.0, 6.0, 4.0)
          Border.isHitTestVisible false
          Border.child (
              TextBlock.create
                  [ TextBlock.text text
                    TextBlock.fontSize 10.0
                    TextBlock.fontWeight FontWeight.SemiBold
                    TextBlock.foreground (Theme.brush (Theme.readableOnColor (chipWash surface hex) hex)) ] ) ]
    :> IView

/// The status pill is filled more strongly than a chip, so its contrast is
/// measured against **that** wash, not the chip's.
let private statusPill (tokens: Tokens) (surface: Color) (text: string) (hex: string) : IView =
    let wash =
        Theme.compositeOver surface (Theme.withAlphaHex DocumentsLogic.Geometry.statusAlpha hex)

    Border.create
        [ Border.background (Theme.brush (Theme.withAlphaHex DocumentsLogic.Geometry.statusAlpha hex))
          Border.cornerRadius (Tokens.radius tokens 999.0)
          Border.padding (9.0, 2.0)
          Border.margin (0.0, 0.0, 6.0, 4.0)
          Border.isHitTestVisible false
          Border.child (
              TextBlock.create
                  [ TextBlock.text text
                    TextBlock.fontSize 10.0
                    TextBlock.fontWeight FontWeight.SemiBold
                    TextBlock.foreground (Theme.brush (Theme.readableOnColor wash hex)) ] ) ]
    :> IView

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

/// One of the redesign's small in-card glyph buttons. The label is carried twice
/// — accessible name and tooltip — because a glyph alone is unreadable.
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
          Button.width DocumentsLogic.Geometry.iconActionSize
          Button.height DocumentsLogic.Geometry.iconActionSize
          Button.cornerRadius (Tokens.radius tokens DocumentsLogic.Geometry.iconActionRadius)
          Button.clipToBounds false
          Button.margin (0.0, 0.0, 6.0, 0.0)
          Button.cursor Ui.hand
          ToolTip.tip label
          Ui.accessibleName label
          Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always)
          Button.content (
              GlassPanel.create
                  [ GlassPanel.baseRadius DocumentsLogic.Geometry.iconActionRadius
                    GlassPanel.elevation 0
                    GlassPanel.borderInset DocumentsLogic.Geometry.borderInset
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
                                              Tokens.radius tokens DocumentsLogic.Geometry.iconActionRadius
                                          ) ]
                                    Border.create
                                        [ Border.isHitTestVisible false
                                          Border.child (
                                              TextBlock.create
                                                  [ TextBlock.text glyph
                                                    TextBlock.fontSize Theme.Type.metricLabel
                                                    TextBlock.horizontalAlignment HorizontalAlignment.Center
                                                    TextBlock.verticalAlignment VerticalAlignment.Center
                                                    Ui.decorative ()
                                                    TextBlock.foreground (
                                                        Theme.brush (
                                                            Theme.readableOnColor (Theme.surfaces ()).Action hex
                                                        )
                                                    ) ] ) ] ] ] ) ] ) ]
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
    let wash = Theme.compositeOver page (Theme.withAlphaHex DocumentsLogic.Geometry.chipFillAlpha hex)

    Button.create
        [ Button.background (
              if isOn then Theme.brush (Theme.withAlphaHex DocumentsLogic.Geometry.chipFillAlpha hex)
              else Theme.transparent
          )
          Button.borderThickness DocumentsLogic.Geometry.filterBorderThickness
          Button.borderBrush (if isOn then Theme.brush (Theme.visibleOnColor page hex) else Theme.border ())
          // An unselected chip is muted, never the disabled colour.
          Button.foreground (if isOn then Theme.brush (Theme.readableOnColor wash hex) else Theme.muted ())
          Button.cornerRadius (Tokens.radius tokens DocumentsLogic.Geometry.filterRadius)
          Button.minHeight DocumentsLogic.Geometry.filterMinHeight
          Button.padding (13.0, 3.0)
          Button.fontSize Theme.Type.metricLabel
          Button.fontWeight FontWeight.SemiBold
          Button.margin (0.0, 0.0, 8.0, 8.0)
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

/// The state marks carry text as well as colour, so a stage's state never
/// depends on telling two dots apart.
let private stageHex (state: DocumentsLogic.StageState) =
    match state with
    | DocumentsLogic.StageState.Done -> Theme.successHex ()
    | DocumentsLogic.StageState.Pending -> Theme.Identity.neutral
    | DocumentsLogic.StageState.Unavailable -> Theme.Identity.neutral
    | DocumentsLogic.StageState.Failed -> Theme.dangerHex ()

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
                    [ TextBlock.text "Documents"
                      TextBlock.fontSize Theme.Type.pageTitle
                      TextBlock.fontWeight FontWeight.Bold
                      TextBlock.foreground (Theme.text ()) ]
                TextBlock.create
                    [ TextBlock.text "What academic resources do I have, and how are they connected?"
                      TextBlock.fontSize Theme.Type.pageMeta
                      TextBlock.foreground (Theme.muted ())
                      TextBlock.textWrapping TextWrapping.Wrap ] ] ]
    :> IView

/// The redesign's import banner.
///
/// Its wording carries **both halves** of the real flow, and the keyboard-
/// reachable Import button beside it performs the same operation a drop does, so
/// the capability is not mouse-only.
///
/// The mockup's drag-active overlay is **omitted**: the shell exposes a drop
/// handler but no drag-enter state, so there is nothing truthful to light up.
let private dropZone (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    Border.create
        [ Border.background (Theme.brush (Theme.accentSoftHex ()))
          Border.borderThickness 1.0
          Border.borderBrush (Theme.brush (Theme.overlayEdgeHex "55" (Theme.accentHex ())))
          Border.cornerRadius (Tokens.radius tokens 16.0)
          Border.padding (20.0, 14.0)
          Border.child (
              DockPanel.create
                  [ DockPanel.children
                        [ Border.create
                              [ DockPanel.dock Dock.Right
                                Border.margin (16.0, 0.0, 0.0, 0.0)
                                Border.verticalAlignment VerticalAlignment.Center
                                Border.child (
                                    glassAction
                                        tokens
                                        (if model.Importing then "Importing…" else "＋ Import files")
                                        true
                                        (not model.Importing)
                                        "Import files into the document library"
                                        (fun () -> dispatch Import)
                                ) ]

                          Grid.create
                              [ Grid.columnDefinitions DocumentsLogic.Geometry.bulletColumns
                                Grid.children
                                    [ TextBlock.create
                                          [ Grid.column 0
                                            TextBlock.text "↓"
                                            TextBlock.fontSize Theme.Type.rowTitle
                                            TextBlock.verticalAlignment VerticalAlignment.Center
                                            TextBlock.foreground (
                                                Theme.brush (
                                                    Theme.readableOnColor
                                                        (Theme.compositeOver
                                                            (Theme.surfaces ()).Window
                                                            (Theme.accentSoftHex ()))
                                                        (Theme.accentHex ())
                                                )
                                            )
                                            Ui.decorative () ]
                                      TextBlock.create
                                          [ Grid.column 2
                                            TextBlock.text DocumentsLogic.dropZoneMessage
                                            TextBlock.fontSize Theme.Type.rowMeta
                                            TextBlock.verticalAlignment VerticalAlignment.Center
                                            TextBlock.foreground (Theme.textSecondary ())
                                            TextBlock.textWrapping TextWrapping.Wrap ] ] ] ] ]
          ) ]
    :> IView

let private metricCard (tokens: Tokens) (metric: DocumentsLogic.Metric) : IView =
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
                            TextBlock.fontSize 10.5
                            TextBlock.foreground (Theme.faint ())
                            TextBlock.textWrapping TextWrapping.Wrap ] ] ] ]

/// The redesign calls this "AI Insights". Nothing here comes from a model.
let private insightsPanel (tokens: Tokens) (now: DateTime) (model: Model) : IView list =
    match DocumentsLogic.insights now model.Facts with
    | [] -> []
    | lines ->
        [ plainCard
              tokens
              18.0
              [ DockPanel.create
                    [ DockPanel.children
                          [ TextBlock.create
                                [ DockPanel.dock Dock.Right
                                  TextBlock.text (DocumentsLogic.totalSize model.Facts + " stored")
                                  TextBlock.fontSize Theme.Type.metricLabel
                                  TextBlock.foreground (Theme.faint ())
                                  TextBlock.margin (12.0, 0.0, 0.0, 0.0) ]
                            sectionCaption "Rule-based insights" ] ]

                StackPanel.create
                    [ StackPanel.children
                          (lines
                           |> List.map (fun (glyph, text) ->
                               let hex =
                                   if glyph = "!" then Theme.dangerHex () else Theme.accentHex ()

                               bulletRow glyph (Theme.brush hex) text (Theme.textSecondary ()))) ] ] ]

let private controlsRow (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let modeButton (mode: DocumentsLogic.ViewMode) : IView =
        let isOn = model.View = mode
        let label = DocumentsLogic.ViewMode.label mode

        filterChip
            tokens
            label
            isOn
            (Theme.accentHex ())
            (if isOn then sprintf "%s view, on" label else sprintf "Switch to %s view" label)
            (fun () -> dispatch (SetView mode))

    DockPanel.create
        [ DockPanel.children
              [ WrapPanel.create
                    [ DockPanel.dock Dock.Right
                      WrapPanel.margin (12.0, 0.0, 0.0, 0.0)
                      WrapPanel.verticalAlignment VerticalAlignment.Center
                      WrapPanel.children [ modeButton DocumentsLogic.ViewMode.Grid; modeButton DocumentsLogic.ViewMode.List ] ]

                Border.create
                    [ Border.verticalAlignment VerticalAlignment.Center
                      Border.child (
                          Ui.textBox model.Query "Search filenames, titles and extracted text" (SetQuery >> dispatch)
                      ) ] ] ]
    :> IView

let private filtersRow (tokens: Tokens) (now: DateTime) (model: Model) (dispatch: Msg -> unit) : IView =
    WrapPanel.create
        [ WrapPanel.children
              (DocumentsLogic.Filter.all
               |> List.map (fun f ->
                   let label = DocumentsLogic.Filter.label f
                   let isOn = model.Filters.Contains f

                   let hex =
                       if f = DocumentsLogic.Filter.MissingFile then Theme.dangerHex () else Theme.accentHex ()

                   filterChip
                       tokens
                       label
                       isOn
                       hex
                       (if isOn then sprintf "%s filter, on" label else sprintf "Turn on %s" label)
                       (fun () -> dispatch (ToggleFilter f)))) ]
    :> IView

let private moduleFiltersRow (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let chips = DocumentsLogic.moduleChips model.Facts

    StackPanel.create
        [ StackPanel.spacing 6.0
          StackPanel.children
              [ sectionCaption "By module"

                if chips.IsEmpty then
                    TextBlock.create
                        [ TextBlock.text "No document is filed under a module yet."
                          TextBlock.fontSize Theme.Type.metricLabel
                          TextBlock.foreground (Theme.faint ())
                          TextBlock.textWrapping TextWrapping.Wrap ]
                else
                    WrapPanel.create
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
                                       (fun () -> dispatch (ToggleModule m.Id)))) ] ] ]
    :> IView

// ---------------------------------------------------------------------------
// document cards
// ---------------------------------------------------------------------------

let private typeTile (tokens: Tokens) (size: float) (f: DocumentsLogic.Facts) : IView =
    let hex = DocumentsLogic.kindHex f.Document.Kind

    Border.create
        [ Border.width size
          Border.height size
          Border.cornerRadius (Tokens.radius tokens DocumentsLogic.Geometry.typeTileRadius)
          Border.background (Theme.brush (Theme.withAlphaHex DocumentsLogic.Geometry.chipFillAlpha hex))
          Border.verticalAlignment VerticalAlignment.Center
          Ui.decorative ()
          Border.child (
              TextBlock.create
                  [ TextBlock.text (DocumentsLogic.kindGlyph f.Document.Kind)
                    TextBlock.fontSize (size / 2.4)
                    TextBlock.horizontalAlignment HorizontalAlignment.Center
                    TextBlock.verticalAlignment VerticalAlignment.Center
                    TextBlock.foreground (
                        Theme.brush (Theme.readableOnColor (chipWash (Theme.surfaces ()).Card hex) hex)
                    ) ] ) ]
    :> IView

/// The card's four glyph actions. Geometry is identical whether or not each is
/// available; a missing file disables Open rather than hiding it.
let private cardActions (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) (f: DocumentsLogic.Facts) : IView =
    let hex = DocumentsLogic.kindHex f.Document.Kind
    let summariseOff = DocumentsLogic.summariseDisabledReason f

    StackPanel.create
        [ StackPanel.orientation Orientation.Horizontal
          StackPanel.children
              [ iconAction
                    tokens
                    "↗"
                    (if f.FileExists then "Open the file" else "The file is missing from the store")
                    hex
                    f.FileExists
                    (fun () -> dispatch (Select f.Document.Id); dispatch OpenFile)

                iconAction
                    tokens
                    "✦"
                    (match summariseOff with
                     | Some reason -> reason
                     | None -> "Summarise with AI")
                    hex
                    (summariseOff.IsNone && not model.Summarizing)
                    (fun () -> dispatch (Select f.Document.Id); dispatch Summarize)

                iconAction tokens "▣" "Open this document's details" hex true (fun () ->
                    dispatch (Select f.Document.Id))

                iconAction tokens "✕" "Delete this document" hex true (fun () ->
                    dispatch (Select f.Document.Id)
                    dispatch RequestDelete) ] ]
    :> IView

let private documentCard
    (tokens: Tokens)
    (now: DateTime)
    (model: Model)
    (dispatch: Msg -> unit)
    (f: DocumentsLogic.Facts)
    : IView =
    let isSelected = model.Selected = Some f.Document.Id
    let hex = DocumentsLogic.kindHex f.Document.Kind
    let surface = if isSelected then (Theme.surfaces ()).Selected else (Theme.surfaces ()).Card
    let source, text = DocumentsLogic.blurb f

    cardWith
        tokens
        18.0
        DocumentsLogic.Geometry.cardRadius
        surface
        VerticalAlignment.Top
        [ Border.create
              [ // Reserved in BOTH states, so selecting recolours and never resizes.
                Border.borderThickness DocumentsLogic.Geometry.cardBorderThickness
                Border.borderBrush (if isSelected then Theme.brush hex else Theme.transparent)
                Border.cornerRadius (Tokens.radius tokens DocumentsLogic.Geometry.cardRadius)
                Border.child (
                    StackPanel.create
                        [ StackPanel.spacing (Tokens.space tokens 10.0)
                          StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                          StackPanel.children
                              [ Grid.create
                                    [ Grid.columnDefinitions "Auto,10,*"
                                      Grid.children
                                          [ Border.create
                                                [ Grid.column 0
                                                  Border.child (
                                                      typeTile tokens DocumentsLogic.Geometry.typeTileSize f
                                                  ) ]
                                            StackPanel.create
                                                [ Grid.column 2
                                                  StackPanel.spacing 2.0
                                                  StackPanel.verticalAlignment VerticalAlignment.Center
                                                  StackPanel.children
                                                      [ TextBlock.create
                                                            [ TextBlock.text f.Document.Title
                                                              TextBlock.fontSize 13.0
                                                              TextBlock.fontWeight FontWeight.SemiBold
                                                              TextBlock.foreground (Theme.text ())
                                                              // A long filename wraps rather than
                                                              // vanishing behind an ellipsis.
                                                              TextBlock.textWrapping TextWrapping.Wrap ]
                                                        TextBlock.create
                                                            [ TextBlock.text (
                                                                  sprintf
                                                                      "%s · %s"
                                                                      (DocumentKind.label f.Document.Kind)
                                                                      (DocumentsLogic.sizeLabel f.Document.SizeBytes)
                                                              )
                                                              TextBlock.fontSize 10.5
                                                              TextBlock.foreground (Theme.faint ()) ] ] ] ] ]

                                WrapPanel.create
                                    [ WrapPanel.children
                                          [ match f.Module with
                                            | Some m -> pill tokens surface m.Code m.Color
                                            | None -> pill tokens surface "Unfiled" Theme.Identity.neutral

                                            statusPill
                                                tokens
                                                surface
                                                (DocumentsLogic.statusLabel f)
                                                (if DocumentsLogic.isMissing f then Theme.dangerHex ()
                                                 else Theme.successHex ())

                                            if f.InboxPending then
                                                pill tokens surface "Inbox review" (Theme.warningHex ()) ] ]

                                if text.Length > 0 then
                                    StackPanel.create
                                        [ StackPanel.spacing 2.0
                                          StackPanel.children
                                              [ sectionCaption (DocumentsLogic.BlurbSource.label source)
                                                TextBlock.create
                                                    [ TextBlock.text text
                                                      TextBlock.fontSize 11.5
                                                      TextBlock.foreground (Theme.textSecondary ())
                                                      TextBlock.textWrapping TextWrapping.Wrap ] ] ]

                                TextBlock.create
                                    [ TextBlock.text (
                                          sprintf
                                              "Imported %s · %d linked"
                                              (Format.relativeTime now f.Document.ImportedAt)
                                              (DocumentsLogic.linkCount f)
                                      )
                                      TextBlock.fontSize 10.5
                                      TextBlock.foreground (Theme.faint ())
                                      TextBlock.textWrapping TextWrapping.Wrap ]

                                cardActions tokens model dispatch f ] ]
                ) ] ]

let private documentRow
    (tokens: Tokens)
    (now: DateTime)
    (model: Model)
    (dispatch: Msg -> unit)
    (f: DocumentsLogic.Facts)
    : IView =
    let isSelected = model.Selected = Some f.Document.Id
    let hex = DocumentsLogic.kindHex f.Document.Kind
    let surface = if isSelected then (Theme.surfaces ()).Selected else (Theme.surfaces ()).Card
    let _, text = DocumentsLogic.blurb f

    Button.create
        [ Button.classes [ Ui.glassActionClass () ]
          Button.background (
              if isSelected then Theme.brush (Theme.withAlphaHex DocumentsLogic.Geometry.statusAlpha hex)
              else Theme.transparent
          )
          Button.borderThickness DocumentsLogic.Geometry.cardBorderThickness
          Button.borderBrush (if isSelected then Theme.brush hex else Theme.border ())
          Button.cornerRadius (Tokens.radius tokens 14.0)
          Button.padding (16.0, 10.0)
          Button.margin (0.0, 0.0, 0.0, 8.0)
          Button.horizontalAlignment HorizontalAlignment.Stretch
          Button.horizontalContentAlignment HorizontalAlignment.Stretch
          Button.cursor Ui.hand
          Ui.accessibleName (sprintf "Open %s" f.Document.Title)
          Button.onClick ((fun _ -> dispatch (Select f.Document.Id)), SubPatchOptions.Always)
          Button.content (
              Grid.create
                  [ Grid.columnDefinitions "Auto,12,2*,10,Auto,10,3*"
                    Grid.children
                        [ Border.create [ Grid.column 0; Border.child (typeTile tokens 30.0 f) ]

                          TextBlock.create
                              [ Grid.column 2
                                TextBlock.text f.Document.Title
                                TextBlock.fontSize 13.0
                                TextBlock.fontWeight FontWeight.SemiBold
                                TextBlock.foreground (Theme.text ())
                                TextBlock.verticalAlignment VerticalAlignment.Center
                                TextBlock.textTrimming TextTrimming.CharacterEllipsis ]

                          Border.create
                              [ Grid.column 4
                                Border.verticalAlignment VerticalAlignment.Center
                                Border.child (
                                    statusPill
                                        tokens
                                        surface
                                        (DocumentsLogic.statusLabel f)
                                        (if DocumentsLogic.isMissing f then Theme.dangerHex ()
                                         else Theme.successHex ())
                                ) ]

                          TextBlock.create
                              [ Grid.column 6
                                TextBlock.text (
                                    if text.Length > 0 then text
                                    else sprintf "%s · imported %s" (DocumentsLogic.sizeLabel f.Document.SizeBytes) (Format.relativeTime now f.Document.ImportedAt)
                                )
                                TextBlock.fontSize 11.5
                                TextBlock.foreground (Theme.faint ())
                                TextBlock.verticalAlignment VerticalAlignment.Center
                                TextBlock.textTrimming TextTrimming.CharacterEllipsis ] ] ]
          ) ]
    :> IView

// ---------------------------------------------------------------------------
// the selected-document detail panel
// ---------------------------------------------------------------------------

let private detailPanel
    (tokens: Tokens)
    (now: DateTime)
    (model: Model)
    (dispatch: Msg -> unit)
    (f: DocumentsLogic.Facts)
    : IView =
    let hex = DocumentsLogic.kindHex f.Document.Kind
    let surface = (Theme.surfaces ()).Card
    let summariseOff = DocumentsLogic.summariseDisabledReason f

    let labelled (caption: string) (children: IView list) : IView list =
        if children.IsEmpty then
            []
        else
            [ StackPanel.create
                  [ StackPanel.spacing 5.0
                    StackPanel.children (sectionCaption caption :: children) ] ]

    let valueRow (label: string) (value: string) : IView =
        Grid.create
            [ Grid.columnDefinitions DocumentsLogic.Geometry.detailColumns
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
                          TextBlock.textWrapping TextWrapping.Wrap ] ] ]
        :> IView

    let region (r: DocumentsLogic.DetailRegion) : IView list =
        match r with
        | DocumentsLogic.DetailRegion.Preview ->
            // **Not a preview of the contents.** Nothing in this application can
            // render a PDF, a slide deck or a Word file, so the box shows what is
            // genuinely known — the type and the filename — and the real Open
            // action sits in the action row below.
            [ Border.create
                  [ Border.height DocumentsLogic.Geometry.previewBoxHeight
                    Border.cornerRadius (Tokens.radius tokens 14.0)
                    Border.borderThickness 1.0
                    Border.borderBrush (Theme.border ())
                    Border.background (Theme.cardAlt ())
                    Border.child (
                        StackPanel.create
                            [ StackPanel.verticalAlignment VerticalAlignment.Center
                              StackPanel.horizontalAlignment HorizontalAlignment.Center
                              StackPanel.spacing 8.0
                              StackPanel.children
                                  [ Border.create
                                        [ Border.horizontalAlignment HorizontalAlignment.Center
                                          Border.child (typeTile tokens 44.0 f) ]
                                    TextBlock.create
                                        [ TextBlock.text f.Document.FileName
                                          TextBlock.fontSize 11.0
                                          TextBlock.foreground (Theme.faint ())
                                          TextBlock.textAlignment TextAlignment.Center
                                          TextBlock.textWrapping TextWrapping.Wrap
                                          TextBlock.maxWidth 260.0 ]
                                    TextBlock.create
                                        [ TextBlock.text "No in-app preview for this format — open it to read it."
                                          TextBlock.fontSize 10.5
                                          TextBlock.foreground (Theme.faint ())
                                          TextBlock.textAlignment TextAlignment.Center
                                          TextBlock.textWrapping TextWrapping.Wrap
                                          TextBlock.maxWidth 260.0 ] ] ]
                    ) ] ]

        | DocumentsLogic.DetailRegion.MissingWarning ->
            if f.FileExists then
                []
            else
                let wash =
                    Theme.compositeOver surface (Theme.withAlphaHex "22" (Theme.dangerHex ()))

                [ Border.create
                      [ Border.background (Theme.brush (Theme.withAlphaHex "22" (Theme.dangerHex ())))
                        Border.borderThickness 1.0
                        Border.borderBrush (Theme.brush (Theme.overlayEdgeHex "55" (Theme.dangerHex ())))
                        Border.cornerRadius (Tokens.radius tokens 12.0)
                        Border.padding (14.0, 10.0)
                        Border.child (
                            StackPanel.create
                                [ StackPanel.spacing 4.0
                                  StackPanel.children
                                      [ TextBlock.create
                                            [ TextBlock.text "The file is missing from the store"
                                              TextBlock.fontSize Theme.Type.rowMeta
                                              TextBlock.fontWeight FontWeight.SemiBold
                                              TextBlock.foreground (
                                                  Theme.brush (Theme.readableOnColor wash (Theme.dangerHex ()))
                                              )
                                              TextBlock.textWrapping TextWrapping.Wrap ]
                                        TextBlock.create
                                            [ TextBlock.text
                                                  "Its record, extracted text and links are all still here — only the file itself is gone. Opening it is disabled. Re-import the file to restore it."
                                              TextBlock.fontSize Theme.Type.metricLabel
                                              TextBlock.foreground (Theme.textSecondary ())
                                              TextBlock.textWrapping TextWrapping.Wrap ] ] ]
                        ) ] ]

        | DocumentsLogic.DetailRegion.Metadata ->
            labelled
                "File metadata"
                [ valueRow "Type" (DocumentKind.label f.Document.Kind)
                  valueRow "Size" (DocumentsLogic.sizeLabel f.Document.SizeBytes)
                  valueRow "Imported" (Format.shortDate f.Document.ImportedAt)
                  valueRow "File name" f.Document.FileName
                  valueRow "Location" (if f.FileExists then f.Document.StoredPath else "missing") ]

        | DocumentsLogic.DetailRegion.AiSummary ->
            // `Document.Summary` is written only by `Ai.Summarize`; the importer
            // sets it to None. That is why this may be labelled AI SUMMARY.
            match DocumentsLogic.aiSummary f with
            | Some summary ->
                labelled
                    "AI summary"
                    [ TextBlock.create
                          [ TextBlock.text summary
                            TextBlock.fontSize 12.0
                            TextBlock.foreground (Theme.textSecondary ())
                            TextBlock.textWrapping TextWrapping.Wrap ] ]
            | None ->
                labelled
                    "AI summary"
                    [ TextBlock.create
                          [ TextBlock.text (
                                match summariseOff with
                                | Some reason -> reason
                                | None -> "Not written yet — use Summarise below."
                            )
                            TextBlock.fontSize Theme.Type.metricLabel
                            TextBlock.foreground (Theme.faint ())
                            TextBlock.textWrapping TextWrapping.Wrap ] ]

        | DocumentsLogic.DetailRegion.ExtractedText ->
            match DocumentsLogic.extractedText f with
            | Some text ->
                labelled
                    "Extracted text"
                    [ Border.create
                          [ Border.background (Theme.cardAlt ())
                            Border.cornerRadius (Tokens.radius tokens 12.0)
                            Border.padding (12.0, 10.0)
                            Border.child (
                                TextBlock.create
                                    [ TextBlock.text (Format.truncate DocumentsLogic.Threshold.extractedTextChars text)
                                      TextBlock.fontSize 11.5
                                      TextBlock.foreground (Theme.muted ())
                                      TextBlock.textWrapping TextWrapping.Wrap ]
                            ) ] ]
            | None ->
                labelled
                    "Extracted text"
                    [ TextBlock.create
                          [ TextBlock.text (
                                if DocumentsLogic.canExtractText f.Document.Kind then
                                    "No text was extracted from this file."
                                else
                                    sprintf
                                        "%s files carry no text to extract."
                                        (DocumentKind.label f.Document.Kind)
                            )
                            TextBlock.fontSize Theme.Type.metricLabel
                            TextBlock.foreground (Theme.faint ())
                            TextBlock.textWrapping TextWrapping.Wrap ] ]

        | DocumentsLogic.DetailRegion.Pipeline ->
            labelled
                "Processing status"
                (DocumentsLogic.Stage.all
                 |> List.map (fun stage ->
                     let state = DocumentsLogic.stageState f stage
                     let stateHexValue = stageHex state
                     let wash = chipWash surface stateHexValue

                     Grid.create
                         [ Grid.columnDefinitions "Auto,8,*,8,Auto"
                           Grid.margin (0.0, 1.0)
                           Grid.children
                               [ TextBlock.create
                                     [ Grid.column 0
                                       TextBlock.text (DocumentsLogic.StageState.mark state)
                                       TextBlock.fontSize 11.0
                                       TextBlock.fontWeight FontWeight.SemiBold
                                       TextBlock.foreground (
                                           Theme.brush (Theme.readableOnColor wash stateHexValue)
                                       )
                                       Ui.decorative () ]
                                 TextBlock.create
                                     [ Grid.column 2
                                       TextBlock.text (DocumentsLogic.Stage.label stage)
                                       TextBlock.fontSize 11.5
                                       TextBlock.foreground (Theme.text ())
                                       TextBlock.textWrapping TextWrapping.Wrap ]
                                 TextBlock.create
                                     [ Grid.column 4
                                       TextBlock.text (DocumentsLogic.StageState.label state)
                                       TextBlock.fontSize 10.5
                                       TextBlock.foreground (Theme.faint ()) ] ] ]
                     :> IView))

        | DocumentsLogic.DetailRegion.Related ->
            // Zero-count categories are omitted, not drawn as decorative rows.
            labelled
                "Related content"
                (DocumentsLogic.related f
                 |> List.map (fun (kind, count) ->
                     let label = DocumentsLogic.RelatedKind.label kind

                     Button.create
                         [ Button.background Theme.transparent
                           Button.borderThickness 0.0
                           Button.padding (0.0, 2.0)
                           Button.horizontalAlignment HorizontalAlignment.Stretch
                           Button.horizontalContentAlignment HorizontalAlignment.Stretch
                           Button.cursor Ui.hand
                           // Focused navigation is unsupported: no screen takes
                           // an entity id on entry, so this opens the owner.
                           Ui.accessibleName (sprintf "Open %s" label)
                           Button.onClick (
                               (fun _ -> dispatch (GotoPage(DocumentsLogic.RelatedKind.page kind))),
                               SubPatchOptions.Always
                           )
                           Button.content (valueRow label (string count)) ]
                     :> IView))

        | DocumentsLogic.DetailRegion.GraphNodes ->
            let edges = (f.LinksOut @ f.LinksIn) |> List.distinct

            labelled
                (sprintf "Knowledge graph — %d linked" edges.Length)
                (if edges.IsEmpty then
                     []
                 else
                     [ glassAction tokens "Open the Graph" false true "Open the knowledge graph" (fun () ->
                           dispatch (GotoPage Page.Graph)) ])

        | DocumentsLogic.DetailRegion.History ->
            labelled
                "Timeline activity"
                (f.Activity
                 |> List.sortByDescending (fun e -> e.OccurredAt)
                 |> List.truncate DocumentsLogic.Threshold.historyRows
                 |> List.map (fun e -> valueRow (Format.truncate 22 e.Title) (Format.relativeTime now e.OccurredAt)))

        | DocumentsLogic.DetailRegion.Actions ->
            [ Ui.labeled
                  "Module"
                  (Ui.combo
                      ("(unfiled)" :: (model.Modules |> List.map StudyModule.displayName))
                      model.ModuleIndex
                      (AssignModule >> dispatch))

              WrapPanel.create
                  [ WrapPanel.children
                        [ glassAction
                              tokens
                              "Open file"
                              false
                              f.FileExists
                              (if f.FileExists then "Open the file" else "The file is missing from the store")
                              (fun () -> dispatch OpenFile)

                          glassAction
                              tokens
                              (if model.Summarizing then "Summarising…" else "✦ Summarise")
                              false
                              (summariseOff.IsNone && not model.Summarizing)
                              (match summariseOff with
                               | Some reason -> reason
                               | None -> "Summarise this document with AI")
                              (fun () -> dispatch Summarize)

                          if f.InboxPending then
                              glassAction tokens "Review in Inbox" false true "Open the Inbox to review the suggested filing" (fun () ->
                                  dispatch (GotoPage Page.Inbox))

                          glassAction tokens "Timeline" false true "Open the Timeline" (fun () ->
                              dispatch (GotoPage Page.Timeline))

                          glassAction tokens "Delete" false true "Delete this document" (fun () ->
                              dispatch RequestDelete) ] ]

              match summariseOff with
              | Some reason ->
                  TextBlock.create
                      [ TextBlock.text reason
                        TextBlock.fontSize Theme.Type.metricLabel
                        TextBlock.foreground (Theme.faint ())
                        TextBlock.textWrapping TextWrapping.Wrap ]
              | None -> ()

              if model.PendingDelete then
                  Border.create
                      [ Border.background (Theme.brush (Theme.withAlphaHex "1F" (Theme.dangerHex ())))
                        Border.borderThickness 1.0
                        Border.borderBrush (Theme.brush (Theme.overlayEdgeHex "55" (Theme.dangerHex ())))
                        Border.cornerRadius (Tokens.radius tokens 12.0)
                        Border.padding (14.0, 10.0)
                        Border.child (
                            StackPanel.create
                                [ StackPanel.spacing 10.0
                                  StackPanel.children
                                      [ TextBlock.create
                                            [ TextBlock.text
                                                  "Delete this document permanently? The record, its extracted text and its links go too, and the file itself is removed from the store where possible. This cannot be undone."
                                              TextBlock.fontSize Theme.Type.rowMeta
                                              TextBlock.foreground (Theme.text ())
                                              TextBlock.textWrapping TextWrapping.Wrap ]
                                        WrapPanel.create
                                            [ WrapPanel.children
                                                  [ Ui.dangerButton "Yes, delete it" (fun () -> dispatch ConfirmDelete)
                                                    Ui.ghostButton "Cancel" (fun () -> dispatch CancelDelete) ] ] ] ]
                        ) ] ]

    cardWith
        tokens
        20.0
        20.0
        (Theme.surfaces ()).Card
        VerticalAlignment.Top
        [ DockPanel.create
              [ DockPanel.children
                    [ Border.create
                          [ DockPanel.dock Dock.Right
                            Border.child (
                                iconAction tokens "✕" "Close the details panel" hex true (fun () ->
                                    dispatch ClearSelection)
                            ) ]
                      Border.create
                          [ Border.verticalAlignment VerticalAlignment.Center
                            Border.child (sectionCaption "Document details") ] ] ]

          StackPanel.create
              [ StackPanel.spacing (Tokens.space tokens 14.0)
                StackPanel.children (DocumentsLogic.detailOrder |> List.collect region) ] ]

// ---------------------------------------------------------------------------
// page
// ---------------------------------------------------------------------------

let view (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let spacing = Tokens.space tokens 20.0
    let now = DateTime.Now
    let shown = DocumentsLogic.visible now model.Filters model.ModuleFilter model.Query model.Facts
    let selected = selectedFacts model
    let beside = DocumentsLogic.detailBeside model.Width

    let grid: IView =
        if model.Loading && model.Facts.IsEmpty then
            cardRow spacing 0.0 (DocumentsLogic.cardColumns model.Width) [ Ui.skeletonCard 4; Ui.skeletonCard 4 ]
        elif shown.IsEmpty then
            plainCard
                tokens
                24.0
                [ bulletRow
                      "•"
                      (Theme.faint ())
                      (DocumentsLogic.emptyMessage model.Filters model.ModuleFilter model.Query model.Facts.Length)
                      (Theme.muted ()) ]
        else
            match model.View with
            | DocumentsLogic.ViewMode.Grid ->
                let columns =
                    // The detail panel takes its width out of the content column,
                    // so the card count has to account for it.
                    let contentWidth =
                        if beside && selected.IsSome then
                            model.Width - DocumentsLogic.Geometry.detailPanelWidth - spacing
                        else
                            model.Width

                    DocumentsLogic.cardColumns contentWidth

                cardRow spacing 0.0 columns (shown |> List.map (documentCard tokens now model dispatch))
            | DocumentsLogic.ViewMode.List ->
                StackPanel.create
                    [ StackPanel.children (shown |> List.map (documentRow tokens now model dispatch)) ] :> IView

    let content: IView =
        match selected with
        | Some f when beside ->
            // Explicit Grid columns, because the detail panel must align with the
            // top of the grid rather than drifting beside it.
            Grid.create
                [ Grid.columnDefinitions (sprintf "*,%f,%f" spacing DocumentsLogic.Geometry.detailPanelWidth)
                  Grid.children
                      [ Border.create [ Grid.column 0; Border.child grid ]
                        Border.create
                            [ Grid.column 2
                              Border.verticalAlignment VerticalAlignment.Top
                              Border.child (detailPanel tokens now model dispatch f) ] ] ]
            :> IView
        | Some f ->
            // Below the threshold the panel follows the selection instead of
            // sitting beside it, which keeps the reading order intact.
            StackPanel.create
                [ StackPanel.spacing spacing
                  StackPanel.children [ detailPanel tokens now model dispatch f; grid ] ]
            :> IView
        | None -> grid

    let renderSection (section: DocumentsLogic.Section) : IView list =
        match section with
        | DocumentsLogic.Section.Header -> [ header tokens ]
        | DocumentsLogic.Section.DropZone -> [ dropZone tokens model dispatch ]
        | DocumentsLogic.Section.Metrics ->
            [ cardRow
                  spacing
                  DocumentsLogic.Geometry.metricRowMinHeight
                  (DocumentsLogic.metricColumns model.Width)
                  (DocumentsLogic.metrics now model.Facts |> List.map (metricCard tokens)) ]
        | DocumentsLogic.Section.Insights -> insightsPanel tokens now model
        | DocumentsLogic.Section.Controls -> [ controlsRow tokens model dispatch ]
        | DocumentsLogic.Section.Filters -> [ filtersRow tokens now model dispatch ]
        | DocumentsLogic.Section.ModuleFilters -> [ moduleFiltersRow tokens model dispatch ]
        | DocumentsLogic.Section.Content -> [ content ]

    Border.create
        [ Border.onSizeChanged (
              (fun args ->
                  let decisions (w: float) =
                      DocumentsLogic.metricColumns w, DocumentsLogic.cardColumns w, DocumentsLogic.detailBeside w

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
                                                  TextBlock.textWrapping TextWrapping.Wrap ] ) ]
                              | None -> ()

                              yield! (DocumentsLogic.sectionOrder |> List.collect renderSection) ] ] ) ) ]
    :> IView
