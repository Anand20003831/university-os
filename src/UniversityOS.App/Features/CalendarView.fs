module UniversityOS.App.Features.CalendarView

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

/// The Calendar.
///
/// Four real modes — Day, Week, Month, Agenda — over two genuinely different
/// sources: stored `CalendarEvent` rows and planner-placed `PlannedSession`s.
/// Every decision and every pixel of geometry lives in `CalendarLogic`.
///
/// **The redesign's Week and Month areas are a Gantt of module projects** —
/// progress percentages, milestone diamonds, projected states, AI tails. None of
/// that exists in this domain, so none of it is drawn. What is drawn instead is
/// a real event calendar over real stored times.

// ---------------------------------------------------------------------------
// model
// ---------------------------------------------------------------------------

type Model =
    { Anchor: DateTime
      Mode: CalendarLogic.ViewMode
      Events: CalendarEvent list
      Sessions: PlannedSession list
      Modules: StudyModule list
      Filter: CalendarLogic.Filter
      Selected: CalendarLogic.Entry option
      Draft: CalendarLogic.Draft option
      PendingDelete: bool
      Saving: bool
      Loading: bool
      /// The clock, sampled on load and on each tick, so the current-time marker
      /// can move without anything else being re-read.
      Now: DateTime
      Width: float
      Error: string option }

type External = Goto of Page

type Msg =
    | Load
    | Loaded of CalendarEvent list * PlannedSession list * StudyModule list * DateTime
    | Failed of exn
    | Tick of DateTime
    | Prev
    | Next
    | Today
    | SetMode of CalendarLogic.ViewMode
    | SelectDay of DateTime
    | ToggleSource of CalendarLogic.Source
    | ToggleKind of EventKind
    | ToggleModule of Guid
    | ClearFilters
    | Select of CalendarLogic.Entry
    | ClearSelection
    | NewEvent
    | EditSelected
    | CloseDraft
    | SetTitle of string
    | SetKindIndex of int
    | SetAllDay of bool
    | SetStart of string
    | SetEnd of string
    | SetLocation of string
    | SetModuleIndex of int
    | SetNotes of string
    | SaveEvent
    | SavedEvent
    | RequestDelete
    | CancelDelete
    | ConfirmDelete
    | DeletedEvent
    | Resized of float
    | GotoPage of Page

// ---------------------------------------------------------------------------
// loading
// ---------------------------------------------------------------------------

/// One window wide enough for every mode around the anchor, so stepping within a
/// month never needs a fresh read.
let private loadCmd (services: AppServices) (anchor: DateTime) =
    let rangeStart = anchor.Date.AddDays -45.0
    let rangeEnd = anchor.Date.AddDays 75.0

    Elm.ofAsync
        (async {
            let! events = services.Events.GetBetween(rangeStart, rangeEnd)
            let! sessions = services.Sessions.GetBetween(rangeStart, rangeEnd)
            let! modules = services.Modules.GetAll()
            return events, sessions, modules, services.Clock.Now
        })
        Loaded
        Failed

let init (services: AppServices) : Model * Cmd<Msg> =
    let now = services.Clock.Now

    { Anchor = now.Date
      Mode = CalendarLogic.ViewMode.Week
      Events = []
      Sessions = []
      Modules = []
      Filter = CalendarLogic.Filter.none
      Selected = None
      Draft = None
      PendingDelete = false
      Saving = false
      Loading = true
      Now = now
      Width = 1600.0
      Error = None },
    loadCmd services now

// ---------------------------------------------------------------------------
// derived
// ---------------------------------------------------------------------------

/// Everything loaded, as entries, before filtering.
let allEntries (model: Model) : CalendarLogic.Entry list =
    (model.Events |> List.map CalendarLogic.Ev) @ (model.Sessions |> List.map CalendarLogic.Sess)

/// Everything the filters allow.
let visibleEntries (model: Model) =
    allEntries model |> CalendarLogic.applyFilter model.Modules model.Filter

let private firstDay () =
    Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek

// ---------------------------------------------------------------------------
// update
// ---------------------------------------------------------------------------

let update (services: AppServices) (msg: Msg) (model: Model) : Model * Cmd<Msg> * External option =
    let plain m = m, Cmd.none, None

    /// A selection the filters have removed is cleared, never moved.
    let reconcile (after: Model) =
        { after with Selected = CalendarLogic.reconcileSelection after.Selected (visibleEntries after) }

    let withDraft f =
        match model.Draft with
        | Some d -> plain { model with Draft = Some(f d) }
        | None -> plain model

    match msg with
    | Load -> { model with Loading = true }, loadCmd services model.Anchor, None

    | Loaded(events, sessions, modules, now) ->
        plain (
            reconcile
                { model with
                    Events = events
                    Sessions = sessions
                    Modules = modules
                    Now = now
                    Loading = false
                    Error = None }
        )

    | Failed ex -> plain { model with Loading = false; Saving = false; Error = Some ex.Message }

    // Only the clock moves. Nothing is re-read.
    | Tick now -> plain { model with Now = now }

    | Prev ->
        let anchor = CalendarLogic.step model.Mode -1 model.Anchor
        { model with Anchor = anchor }, loadCmd services anchor, None

    | Next ->
        let anchor = CalendarLogic.step model.Mode 1 model.Anchor
        { model with Anchor = anchor }, loadCmd services anchor, None

    // Today is the clock's today, never a remembered anchor.
    | Today ->
        let now = services.Clock.Now
        { model with Anchor = CalendarLogic.todayAnchor now; Now = now }, loadCmd services now, None

    | SetMode mode -> plain { model with Mode = mode }

    // Opening a day from the month grid stays inside the loaded window.
    | SelectDay day -> plain { model with Anchor = day.Date; Mode = CalendarLogic.ViewMode.Day }

    | ToggleSource source ->
        plain (reconcile { model with Filter = CalendarLogic.Filter.toggleSource source model.Filter })

    | ToggleKind kind -> plain (reconcile { model with Filter = CalendarLogic.Filter.toggleKind kind model.Filter })

    | ToggleModule id -> plain (reconcile { model with Filter = CalendarLogic.Filter.toggleModule id model.Filter })

    | ClearFilters -> plain (reconcile { model with Filter = CalendarLogic.Filter.none })

    | Select entry -> plain { model with Selected = Some entry; PendingDelete = false }
    | ClearSelection -> plain { model with Selected = None; PendingDelete = false }

    | NewEvent ->
        let start = model.Anchor.Date.AddHours 9.0

        plain
            { model with
                Draft =
                    Some
                        { CalendarLogic.Original = None
                          CalendarLogic.Title = ""
                          CalendarLogic.KindIndex = EventKind.toInt EventKind.Personal
                          CalendarLogic.AllDay = false
                          CalendarLogic.StartText = Format.dateTimeInput start
                          CalendarLogic.EndText = Format.dateTimeInput (start.AddHours 1.0)
                          CalendarLogic.Location = ""
                          CalendarLogic.ModuleIndex = 0
                          CalendarLogic.Notes = "" } }

    // **Only a stored event can be edited here.** A planner session belongs to
    // the Planner, and this screen does not pretend otherwise.
    | EditSelected ->
        match model.Selected with
        | Some(CalendarLogic.Ev e) ->
            plain
                { model with
                    Draft =
                        Some
                            { CalendarLogic.Original = Some e
                              CalendarLogic.Title = e.Title
                              CalendarLogic.KindIndex = EventKind.toInt e.Kind
                              CalendarLogic.AllDay = e.AllDay
                              CalendarLogic.StartText = Format.dateTimeInput e.StartsAt
                              CalendarLogic.EndText = Format.dateTimeInput e.EndsAt
                              CalendarLogic.Location = e.Location |> Option.defaultValue ""
                              CalendarLogic.ModuleIndex =
                                (match e.ModuleId with
                                 | Some mid ->
                                     match model.Modules |> List.tryFindIndex (fun m -> m.Id = mid) with
                                     | Some i -> i + 1
                                     | None -> 0
                                 | None -> 0)
                              CalendarLogic.Notes = e.Notes |> Option.defaultValue "" } }
        | _ -> plain model

    | CloseDraft -> plain { model with Draft = None }
    | SetTitle v -> withDraft (fun d -> { d with Title = v })
    | SetKindIndex i -> withDraft (fun d -> { d with KindIndex = i })
    | SetAllDay v -> withDraft (fun d -> { d with AllDay = v })
    | SetStart v -> withDraft (fun d -> { d with StartText = v })
    | SetEnd v -> withDraft (fun d -> { d with EndText = v })
    | SetLocation v -> withDraft (fun d -> { d with Location = v })
    | SetModuleIndex i -> withDraft (fun d -> { d with ModuleIndex = i })
    | SetNotes v -> withDraft (fun d -> { d with Notes = v })

    | SaveEvent ->
        match model.Draft with
        | None -> plain model
        | Some draft ->
            // One guard covers validation AND a second click while saving.
            if not (CalendarLogic.canSave model.Saving draft) then
                plain model
            else
                match CalendarLogic.resolveTimes draft with
                | None -> plain model
                | Some(start, finish) ->
                    let now = services.Clock.Now

                    let baseEvent =
                        match draft.Original with
                        | Some original -> original
                        | None -> CalendarEvent.create now draft.Title (EventKind.ofInt draft.KindIndex) start finish

                    let event =
                        { baseEvent with
                            Title = draft.Title.Trim()
                            Kind = EventKind.ofInt draft.KindIndex
                            AllDay = draft.AllDay
                            StartsAt = start
                            EndsAt = finish
                            Location =
                                (if String.IsNullOrWhiteSpace draft.Location then
                                     None
                                 else
                                     Some(draft.Location.Trim()))
                            ModuleId =
                                (if draft.ModuleIndex > 0 && draft.ModuleIndex <= model.Modules.Length then
                                     Some model.Modules.[draft.ModuleIndex - 1].Id
                                 else
                                     None)
                            Notes = (if String.IsNullOrWhiteSpace draft.Notes then None else Some draft.Notes)
                            UpdatedAt = now }

                    let isNew = draft.Original.IsNone

                    { model with Saving = true },
                    Elm.ofAsync
                        (async {
                            do! services.Events.Save event

                            // Preserved from the previous screen: a new event is
                            // an activity, and the Timeline reads it.
                            if isNew then
                                do!
                                    services.Activity.Log(
                                        ActivityEntry.create
                                            services.Clock.Now
                                            ActivityKind.EventAdded
                                            (sprintf
                                                "\"%s\" on %s"
                                                event.Title
                                                (event.StartsAt.ToString "ddd d MMM HH:mm"))
                                            None
                                            (Some { Kind = EntityKind.Event; Id = event.Id.Value })
                                    )
                        })
                        (fun _ -> SavedEvent)
                        Failed,
                    None

    | SavedEvent ->
        { model with Saving = false; Draft = None; Selected = None }, loadCmd services model.Anchor, None

    | RequestDelete -> plain { model with PendingDelete = true }
    | CancelDelete -> plain { model with PendingDelete = false }

    | ConfirmDelete ->
        match model.Selected with
        | Some(CalendarLogic.Ev e) ->
            { model with PendingDelete = false; Saving = true },
            Elm.ofAsync (async { return! services.Events.Delete e.Id }) (fun _ -> DeletedEvent) Failed,
            None
        | _ -> plain { model with PendingDelete = false }

    | DeletedEvent ->
        { model with Saving = false; Selected = None; Draft = None }, loadCmd services model.Anchor, None

    | Resized width -> plain { model with Width = width }
    | GotoPage page -> model, Cmd.none, Some(Goto page)

// ---------------------------------------------------------------------------
// view — shared treatments
// ---------------------------------------------------------------------------

let private weight (w: int) = enum<FontWeight> w

let private sectionCaption (text: string) : IView =
    TextBlock.create
        [ TextBlock.text (text.ToUpperInvariant())
          TextBlock.fontSize 10.5
          TextBlock.fontWeight (weight 600)
          TextBlock.foreground (Theme.label ())
          TextBlock.textWrapping TextWrapping.Wrap ]
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

/// A pill. **Identical geometry selected and unselected** — only the fill and
/// border colour change, so a filter row never reflows as it is used.
let private chip
    (tokens: Tokens)
    (label: string)
    (count: int option)
    (hex: string)
    (selected: bool)
    (onClick: unit -> unit)
    : IView =
    let surface = (Theme.surfaces ()).Card

    let wash =
        Theme.compositeOver surface (Theme.withAlphaHex CalendarLogic.Geometry.chipFillAlpha hex)

    let ink = Theme.brush (Theme.readableOnColor wash hex)

    Button.create
        [ Button.background (
              if selected then
                  Theme.brush (Theme.withAlphaHex CalendarLogic.Geometry.chipFillAlpha hex)
              else
                  Theme.transparent
          )
          Button.borderThickness 1.0
          Button.borderBrush (if selected then Theme.brush hex else Theme.border ())
          Button.foreground (if selected then ink else Theme.textSecondary ())
          Button.cornerRadius (Tokens.radius tokens 999.0)
          Button.minHeight 28.0
          Button.padding (12.0, 5.0)
          Button.fontSize 11.5
          Button.fontWeight FontWeight.SemiBold
          Button.margin (0.0, 0.0, 7.0, 7.0)
          Button.cursor Ui.hand
          Button.content (
              match count with
              | Some n -> sprintf "%s  %d" label n
              | None -> label
          )
          Ui.accessibleName (if selected then sprintf "%s filter, on" label else sprintf "%s filter, off" label)
          Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always) ]
    :> IView

let private navButton (glyph: string) (name: string) (onClick: unit -> unit) : IView =
    Button.create
        [ Button.content glyph
          Button.width 30.0
          Button.height 30.0
          Button.background Theme.transparent
          Button.borderThickness 1.0
          Button.borderBrush (Theme.border ())
          Button.cornerRadius 8.0
          Button.padding 0.0
          // A fixed 30x30 box with zero padding centres nothing on its own: the
          // content alignment defaults to Stretch, so the glyph's TextBlock fills
          // the button and draws its text at the top-left of that stretched box.
          // Reported from the runtime review as off-centre `‹` and `›`.
          Button.horizontalContentAlignment HorizontalAlignment.Center
          Button.verticalContentAlignment VerticalAlignment.Center
          Button.fontSize 13.0
          Button.foreground (Theme.text ())
          Button.cursor Ui.hand
          Ui.accessibleName name
          Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always) ]
    :> IView

let private detailRow (label: string) (value: string) : IView =
    Grid.create
        [ Grid.columnDefinitions CalendarLogic.Geometry.detailColumns
          Grid.margin (0.0, 1.0)
          Grid.children
              [ TextBlock.create
                    [ Grid.column 0
                      TextBlock.text label
                      TextBlock.fontSize 11.0
                      TextBlock.fontWeight FontWeight.SemiBold
                      TextBlock.foreground (Theme.textSecondary ()) ]
                TextBlock.create
                    [ Grid.column 2
                      TextBlock.text value
                      TextBlock.fontSize 10.5
                      TextBlock.foreground (Theme.faint ())
                      TextBlock.textWrapping TextWrapping.Wrap ] ] ]
    :> IView

// ---------------------------------------------------------------------------
// entry presentation
// ---------------------------------------------------------------------------

/// The ink that stays readable on an entry's own wash.
///
/// **The module/kind colour is an accent, never the whole surface** — a
/// full-strength identity colour behind body text is unreadable at most hues.
let private entryInk (entry: CalendarLogic.Entry) (selected: bool) =
    let hex = CalendarLogic.Entry.hex entry

    let alpha =
        if selected then
            CalendarLogic.Geometry.selectedFillAlpha
        else
            CalendarLogic.Geometry.eventFillAlpha

    let wash = Theme.compositeOver (Theme.surfaces ()).Card (Theme.withAlphaHex alpha hex)
    hex, alpha, Theme.brush (Theme.readableOnColor wash hex)

let private isSelected (model: Model) (entry: CalendarLogic.Entry) =
    model.Selected |> Option.map (CalendarLogic.Entry.sameAs entry) |> Option.defaultValue false

/// One entry, as a block in the timed grid.
let private timedCard (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) (placed: CalendarLogic.Placed) : IView =
    let entry = placed.Entry
    let selected = isSelected model entry
    let hex, alpha, ink = entryInk entry selected

    let edge =
        if selected then
            CalendarLogic.Geometry.selectedEdgeAlpha
        else
            CalendarLogic.Geometry.eventEdgeAlpha

    Border.create
        [ Border.background (Theme.brush (Theme.withAlphaHex alpha hex))
          // Reserved in BOTH states, so selecting recolours and never resizes.
          Border.borderThickness 1.0
          Border.borderBrush (Theme.brush (Theme.overlayEdgeHex edge hex))
          Border.cornerRadius (Tokens.radius tokens CalendarLogic.Geometry.cardRadius)
          Border.padding (8.0, 4.0)
          Border.margin (1.0, 0.0)
          Border.cursor Ui.hand
          Border.clipToBounds true
          Ui.accessibleName (
              sprintf
                  "%s, %s, %s"
                  (CalendarLogic.Entry.title entry)
                  (CalendarLogic.Entry.kindLabel entry)
                  (Format.timeRange (CalendarLogic.Entry.start entry) (CalendarLogic.Entry.finish entry))
          )
          Border.onPointerPressed ((fun _ -> dispatch (Select entry)), SubPatchOptions.Always)
          Border.child (
              StackPanel.create
                  [ StackPanel.spacing 1.0
                    StackPanel.children
                        [ StackPanel.create
                              [ StackPanel.orientation Orientation.Horizontal
                                StackPanel.spacing 5.0
                                StackPanel.children
                                    [ TextBlock.create
                                          [ TextBlock.text (
                                                CalendarLogic.Source.glyph (CalendarLogic.Entry.source entry)
                                            )
                                            TextBlock.fontSize 9.0
                                            TextBlock.foreground ink
                                            TextBlock.verticalAlignment VerticalAlignment.Center
                                            Ui.decorative () ]
                                      TextBlock.create
                                          [ TextBlock.text (CalendarLogic.Entry.title entry)
                                            TextBlock.fontSize 11.5
                                            TextBlock.fontWeight FontWeight.SemiBold
                                            TextBlock.foreground (Theme.text ())
                                            TextBlock.textTrimming TextTrimming.CharacterEllipsis ] ] ]

                          if placed.Height > 34.0 then
                              TextBlock.create
                                  [ TextBlock.text (
                                        Format.timeRange
                                            (CalendarLogic.Entry.start entry)
                                            (CalendarLogic.Entry.finish entry)
                                    )
                                    TextBlock.fontSize 9.5
                                    TextBlock.foreground (Theme.faint ())
                                    TextBlock.textTrimming TextTrimming.CharacterEllipsis ] ] ]
          ) ]
    :> IView

/// One entry, as a chip in a month cell, the all-day band or the side panel.
let private entryChip (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) (entry: CalendarLogic.Entry) : IView =
    let selected = isSelected model entry
    let hex, alpha, ink = entryInk entry selected

    Border.create
        [ Border.background (Theme.brush (Theme.withAlphaHex alpha hex))
          Border.borderThickness 1.0
          Border.borderBrush (
              if selected then
                  Theme.brush (Theme.overlayEdgeHex CalendarLogic.Geometry.selectedEdgeAlpha hex)
              else
                  Theme.transparent
          )
          Border.cornerRadius (Tokens.radius tokens CalendarLogic.Geometry.chipRadius)
          Border.padding (6.0, 2.0)
          Border.margin (0.0, 0.0, 0.0, 2.0)
          Border.cursor Ui.hand
          Ui.accessibleName (sprintf "%s, %s" (CalendarLogic.Entry.title entry) (CalendarLogic.Entry.kindLabel entry))
          Border.onPointerPressed ((fun _ -> dispatch (Select entry)), SubPatchOptions.Always)
          Border.child (
              StackPanel.create
                  [ StackPanel.orientation Orientation.Horizontal
                    StackPanel.spacing 5.0
                    StackPanel.children
                        [ TextBlock.create
                              [ TextBlock.text (CalendarLogic.Source.glyph (CalendarLogic.Entry.source entry))
                                TextBlock.fontSize 8.5
                                TextBlock.foreground ink
                                TextBlock.verticalAlignment VerticalAlignment.Center
                                Ui.decorative () ]

                          // An all-day entry never shows a time, because it has
                          // none that means anything.
                          if not (CalendarLogic.Entry.isAllDay entry) then
                              TextBlock.create
                                  [ TextBlock.text ((CalendarLogic.Entry.start entry).ToString "HH:mm")
                                    TextBlock.fontSize 9.5
                                    TextBlock.foreground (Theme.faint ())
                                    TextBlock.verticalAlignment VerticalAlignment.Center ]

                          TextBlock.create
                              [ TextBlock.text (CalendarLogic.Entry.title entry)
                                TextBlock.fontSize 10.5
                                TextBlock.foreground (Theme.text ())
                                TextBlock.textTrimming TextTrimming.CharacterEllipsis ] ] ]
          ) ]
    :> IView

// ---------------------------------------------------------------------------
// the timed grid, shared by Day and Week
// ---------------------------------------------------------------------------

let private hourGutter () : IView =
    Panel.create
        [ Panel.width CalendarLogic.Geometry.timeGutter
          Panel.height CalendarLogic.dayHeight
          Panel.children
              [ for hour in 0..23 do
                    TextBlock.create
                        [ TextBlock.text (DateTime.Today.AddHours(float hour).ToString "HH:mm")
                          TextBlock.fontSize 10.0
                          TextBlock.foreground (Theme.faint ())
                          TextBlock.horizontalAlignment HorizontalAlignment.Right
                          TextBlock.verticalAlignment VerticalAlignment.Top
                          TextBlock.margin (0.0, float hour * CalendarLogic.Geometry.hourHeight - 6.0, 8.0, 0.0)
                          Ui.decorative () ] ] ]
    :> IView

/// One day's column: hour lines, laned entries, and the current-time marker when
/// this column is today.
let private dayColumn
    (tokens: Tokens)
    (model: Model)
    (dispatch: Msg -> unit)
    (day: DateTime)
    (entries: CalendarLogic.Entry list)
    : IView =
    let isToday = day.Date = model.Now.Date
    let placed = CalendarLogic.layoutDay day (CalendarLogic.timedOf entries)

    Panel.create
        [ Panel.height CalendarLogic.dayHeight
          Panel.background (
              if isToday then
                  Theme.brush (Theme.withAlphaHex CalendarLogic.Geometry.todayFillAlpha (Theme.accentHex ()))
              else
                  Theme.transparent
          )
          Panel.children
              [ for hour in 1..23 do
                    Border.create
                        [ Border.height 1.0
                          Border.background (
                              Theme.brush (
                                  Theme.withAlphaHex
                                      CalendarLogic.Geometry.gridLineAlpha
                                      (Ui.hexOf (Theme.surfaces ()).Text)
                              )
                          )
                          Border.verticalAlignment VerticalAlignment.Top
                          Border.horizontalAlignment HorizontalAlignment.Stretch
                          Border.margin (0.0, float hour * CalendarLogic.Geometry.hourHeight, 0.0, 0.0)
                          Border.isHitTestVisible false ]

                // Laned entries. Nothing is ever hidden behind anything else.
                for p in placed do
                    Border.create
                        [ Border.verticalAlignment VerticalAlignment.Top
                          Border.horizontalAlignment HorizontalAlignment.Stretch
                          Border.height p.Height
                          Border.margin (0.0, p.Top, 0.0, 0.0)
                          Border.child (
                              Grid.create
                                  [ Grid.columnDefinitions (String.concat "," (List.replicate p.Lanes "1*"))
                                    Grid.children
                                        [ Border.create
                                              [ Grid.column p.Lane
                                                Border.child (timedCard tokens model dispatch p) ] ] ]
                          ) ]

                if isToday then
                    Border.create
                        [ Border.height 1.5
                          Border.background (Theme.brush (Theme.accentHex ()))
                          Border.verticalAlignment VerticalAlignment.Top
                          Border.horizontalAlignment HorizontalAlignment.Stretch
                          Border.margin (0.0, CalendarLogic.topOf day model.Now, 0.0, 0.0)
                          Border.isHitTestVisible false ] ] ]
    :> IView

/// The all-day band. **Real, and previously ignored entirely.**
let private allDayBand
    (tokens: Tokens)
    (model: Model)
    (dispatch: Msg -> unit)
    (days: DateTime list)
    (entries: CalendarLogic.Entry list)
    : IView list =
    let anyAllDay =
        days
        |> List.exists (fun d -> CalendarLogic.onDay d entries |> CalendarLogic.allDayOf |> List.isEmpty |> not)

    if not anyAllDay then
        []
    else
        [ Grid.create
              [ Grid.columnDefinitions (
                    sprintf
                        "%f,%s"
                        CalendarLogic.Geometry.timeGutter
                        (String.concat "," (List.replicate days.Length "1*"))
                )
                Grid.margin (0.0, 0.0, 0.0, 6.0)
                Grid.children
                    [ TextBlock.create
                          [ Grid.column 0
                            TextBlock.text "All day"
                            TextBlock.fontSize 9.5
                            TextBlock.foreground (Theme.faint ())
                            TextBlock.horizontalAlignment HorizontalAlignment.Right
                            TextBlock.margin (0.0, 3.0, 8.0, 0.0) ]

                      for i, day in List.indexed days do
                          StackPanel.create
                              [ Grid.column (i + 1)
                                StackPanel.margin (1.0, 0.0)
                                StackPanel.minHeight CalendarLogic.Geometry.allDayRowHeight
                                StackPanel.children
                                    (CalendarLogic.onDay day entries
                                     |> CalendarLogic.allDayOf
                                     |> List.map (entryChip tokens model dispatch)) ] ] ]
          :> IView ]

// ---------------------------------------------------------------------------
// the four modes
// ---------------------------------------------------------------------------

let private dayView (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) (entries: CalendarLogic.Entry list) : IView =
    let day = model.Anchor.Date
    let onThisDay = CalendarLogic.onDay day entries

    StackPanel.create
        [ StackPanel.horizontalAlignment HorizontalAlignment.Stretch
          StackPanel.children
              [ if onThisDay.IsEmpty then
                    TextBlock.create
                        [ TextBlock.text "No scheduled items for this day."
                          TextBlock.fontSize 13.0
                          TextBlock.foreground (Theme.muted ())
                          TextBlock.margin (CalendarLogic.Geometry.timeGutter, 16.0, 0.0, 14.0)
                          TextBlock.textWrapping TextWrapping.Wrap ]

                yield! allDayBand tokens model dispatch [ day ] entries

                Ui.scrollNoChrome (
                    Grid.create
                        [ Grid.columnDefinitions (sprintf "%f,*" CalendarLogic.Geometry.timeGutter)
                          Grid.children
                              [ Border.create [ Grid.column 0; Border.child (hourGutter ()) ]
                                Border.create
                                    [ Grid.column 1
                                      Border.borderThickness (1.0, 0.0, 0.0, 0.0)
                                      Border.borderBrush (Theme.divider ())
                                      Border.child (dayColumn tokens model dispatch day onThisDay) ] ] ]
                ) ] ]
    :> IView

let private weekView (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) (entries: CalendarLogic.Entry list) : IView =
    let start = CalendarLogic.weekStart (firstDay ()) model.Anchor
    let days = [ for i in 0..6 -> start.AddDays(float i) ]

    StackPanel.create
        [ StackPanel.horizontalAlignment HorizontalAlignment.Stretch
          StackPanel.children
              [ Grid.create
                    [ Grid.columnDefinitions (
                          sprintf "%f,%s" CalendarLogic.Geometry.timeGutter (String.concat "," (List.replicate 7 "1*"))
                      )
                      Grid.margin (0.0, 0.0, 0.0, 6.0)
                      Grid.children
                          [ for i, day in List.indexed days do
                                StackPanel.create
                                    [ Grid.column (i + 1)
                                      StackPanel.spacing 1.0
                                      StackPanel.margin (1.0, 0.0)
                                      StackPanel.children
                                          [ TextBlock.create
                                                [ TextBlock.text (day.ToString "ddd")
                                                  TextBlock.fontSize 10.5
                                                  TextBlock.fontWeight FontWeight.SemiBold
                                                  TextBlock.foreground (
                                                      if day.Date = model.Now.Date then
                                                          Theme.accent ()
                                                      else
                                                          Theme.label ()
                                                  ) ]
                                            TextBlock.create
                                                [ TextBlock.text (string day.Day)
                                                  TextBlock.fontSize 13.0
                                                  TextBlock.fontWeight FontWeight.SemiBold
                                                  TextBlock.foreground (
                                                      if day.Date = model.Now.Date then
                                                          Theme.accent ()
                                                      else
                                                          Theme.text ()
                                                  ) ] ] ] ] ]

                yield! allDayBand tokens model dispatch days entries

                Ui.scrollNoChrome (
                    Grid.create
                        [ Grid.columnDefinitions (
                              sprintf
                                  "%f,%s"
                                  CalendarLogic.Geometry.timeGutter
                                  (String.concat "," (List.replicate 7 "1*"))
                          )
                          Grid.children
                              [ Border.create [ Grid.column 0; Border.child (hourGutter ()) ]

                                for i, day in List.indexed days do
                                    Border.create
                                        [ Grid.column (i + 1)
                                          Border.borderThickness (1.0, 0.0, 0.0, 0.0)
                                          Border.borderBrush (Theme.divider ())
                                          Border.child (
                                              dayColumn tokens model dispatch day (CalendarLogic.onDay day entries)
                                          ) ] ] ]
                ) ] ]
    :> IView

let private monthView (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) (entries: CalendarLogic.Entry list) : IView =
    let cells = CalendarLogic.monthCells (firstDay ()) model.Anchor

    let cell (c: CalendarLogic.MonthCell) : IView =
        let dayEntries = CalendarLogic.onDay c.Date entries |> List.sortBy CalendarLogic.Entry.start
        let isToday = c.Date.Date = model.Now.Date

        Border.create
            [ Border.borderThickness (0.0, 0.0, 1.0, 1.0)
              Border.borderBrush (Theme.divider ())
              Border.background (
                  if isToday then
                      Theme.brush (Theme.withAlphaHex CalendarLogic.Geometry.todayFillAlpha (Theme.accentHex ()))
                  else
                      Theme.transparent
              )
              Border.padding (6.0, 4.0)
              Border.minHeight 92.0
              Border.child (
                  StackPanel.create
                      [ StackPanel.spacing 3.0
                        StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                        StackPanel.children
                            [ Button.create
                                  [ Button.content (string c.Date.Day)
                                    Button.background Theme.transparent
                                    Button.borderThickness 0.0
                                    Button.padding (2.0, 0.0)
                                    Button.fontSize 11.5
                                    Button.fontWeight (if isToday then FontWeight.Bold else FontWeight.SemiBold)
                                    // An out-of-month day is subordinate, never
                                    // invisible — it is still a real date.
                                    Button.foreground (
                                        if isToday then Theme.accent ()
                                        elif c.InMonth then Theme.text ()
                                        else Theme.faint ()
                                    )
                                    Button.horizontalAlignment HorizontalAlignment.Left
                                    Button.cursor Ui.hand
                                    Ui.accessibleName (sprintf "Open %s" (c.Date.ToString "d MMMM yyyy"))
                                    Button.onClick ((fun _ -> dispatch (SelectDay c.Date)), SubPatchOptions.Always) ]

                              for entry in dayEntries |> List.truncate CalendarLogic.monthChipsPerCell do
                                  entryChip tokens model dispatch entry

                              match CalendarLogic.monthOverflow dayEntries with
                              | Some hidden ->
                                  Button.create
                                      [ Button.content (sprintf "+%d more" hidden)
                                        Button.background Theme.transparent
                                        Button.borderThickness 0.0
                                        Button.padding (2.0, 0.0)
                                        Button.fontSize 10.0
                                        Button.foreground (Theme.muted ())
                                        Button.horizontalAlignment HorizontalAlignment.Left
                                        Button.cursor Ui.hand
                                        Ui.accessibleName (
                                            sprintf "Open %s to see %d more" (c.Date.ToString "d MMMM") hidden
                                        )
                                        Button.onClick ((fun _ -> dispatch (SelectDay c.Date)), SubPatchOptions.Always) ]
                              | None -> () ] ]
              ) ]
        :> IView

    StackPanel.create
        [ StackPanel.horizontalAlignment HorizontalAlignment.Stretch
          StackPanel.children
              [ Grid.create
                    [ Grid.columnDefinitions (String.concat "," (List.replicate 7 "1*"))
                      Grid.margin (0.0, 0.0, 0.0, 4.0)
                      Grid.children
                          [ for i, heading in List.indexed (CalendarLogic.weekdayHeadings (firstDay ())) do
                                TextBlock.create
                                    [ Grid.column i
                                      TextBlock.text (heading.ToUpperInvariant())
                                      TextBlock.fontSize 10.0
                                      TextBlock.fontWeight FontWeight.SemiBold
                                      TextBlock.foreground (Theme.label ())
                                      TextBlock.horizontalAlignment HorizontalAlignment.Center ] ] ]

                Ui.scrollNoChrome (
                    Grid.create
                        [ Grid.columnDefinitions (String.concat "," (List.replicate 7 "1*"))
                          Grid.rowDefinitions (String.concat "," (List.replicate CalendarLogic.monthRows "Auto"))
                          Grid.children
                              [ for i, c in List.indexed cells do
                                    Border.create [ Grid.column (i % 7); Grid.row (i / 7); Border.child (cell c) ] ] ]
                ) ] ]
    :> IView

let private agendaCard (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) (entry: CalendarLogic.Entry) : IView =
    let selected = isSelected model entry
    let hex, _, ink = entryInk entry selected

    Border.create
        [ Border.background (
              if selected then
                  Theme.brush (Theme.withAlphaHex CalendarLogic.Geometry.selectedFillAlpha hex)
              else
                  Theme.brush (Ui.hexOf (Theme.surfaces ()).Card)
          )
          Border.borderThickness 1.0
          Border.borderBrush (
              if selected then
                  Theme.brush (Theme.overlayEdgeHex CalendarLogic.Geometry.selectedEdgeAlpha hex)
              else
                  Theme.border ()
          )
          Border.cornerRadius (Tokens.radius tokens CalendarLogic.Geometry.cardRadius)
          Border.padding (14.0, 10.0)
          Border.margin (0.0, 0.0, 0.0, 6.0)
          Border.cursor Ui.hand
          Ui.accessibleName (sprintf "%s, %s" (CalendarLogic.Entry.title entry) (CalendarLogic.Entry.kindLabel entry))
          Border.onPointerPressed ((fun _ -> dispatch (Select entry)), SubPatchOptions.Always)
          Border.child (
              StackPanel.create
                  [ StackPanel.spacing 3.0
                    StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                    StackPanel.children
                        [ TextBlock.create
                              [ TextBlock.text (CalendarLogic.Entry.title entry)
                                TextBlock.fontSize 13.0
                                TextBlock.fontWeight FontWeight.SemiBold
                                TextBlock.foreground (Theme.text ())
                                TextBlock.textWrapping TextWrapping.Wrap ]

                          StackPanel.create
                              [ StackPanel.orientation Orientation.Horizontal
                                StackPanel.spacing 8.0
                                StackPanel.children
                                    [ TextBlock.create
                                          [ TextBlock.text (
                                                sprintf
                                                    "%s %s"
                                                    (CalendarLogic.Source.glyph (CalendarLogic.Entry.source entry))
                                                    (CalendarLogic.Entry.kindLabel entry)
                                            )
                                            TextBlock.fontSize 10.5
                                            TextBlock.foreground ink ]

                                      TextBlock.create
                                          [ TextBlock.text (
                                                if CalendarLogic.Entry.isAllDay entry then
                                                    (CalendarLogic.Entry.start entry).ToString "ddd d MMM"
                                                    + " · All day"
                                                else
                                                    sprintf
                                                        "%s · %s"
                                                        ((CalendarLogic.Entry.start entry).ToString "ddd d MMM")
                                                        (Format.timeRange
                                                            (CalendarLogic.Entry.start entry)
                                                            (CalendarLogic.Entry.finish entry))
                                            )
                                            TextBlock.fontSize 10.5
                                            TextBlock.foreground (Theme.faint ()) ] ] ] ] ]
          ) ]
    :> IView

let private agendaView (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) (entries: CalendarLogic.Entry list) : IView =
    let rangeStart, rangeEnd =
        CalendarLogic.visibleRange CalendarLogic.ViewMode.Agenda (firstDay ()) model.Anchor

    let inRange =
        entries
        |> List.filter (fun e -> CalendarLogic.Entry.start e < rangeEnd && CalendarLogic.Entry.finish e > rangeStart)

    let groups = CalendarLogic.agendaGroups model.Now (firstDay ()) inRange

    Ui.scrollNoChrome (
        StackPanel.create
            [ StackPanel.spacing 16.0
              StackPanel.horizontalAlignment HorizontalAlignment.Stretch
              StackPanel.children
                  [ if groups.IsEmpty then
                        TextBlock.create
                            [ TextBlock.text "Nothing scheduled in the next four weeks."
                              TextBlock.fontSize 13.0
                              TextBlock.foreground (Theme.muted ())
                              TextBlock.textWrapping TextWrapping.Wrap ]

                    for label, groupEntries in groups do
                        StackPanel.create
                            [ StackPanel.spacing 6.0
                              StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                              StackPanel.children
                                  [ sectionCaption label

                                    for entry in groupEntries do
                                        agendaCard tokens model dispatch entry ] ] ] ]
    )

// ---------------------------------------------------------------------------
// the side panel
// ---------------------------------------------------------------------------

let private selectedPanel (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) (entry: CalendarLogic.Entry) : IView =
    let hex = CalendarLogic.Entry.hex entry

    let moduleName =
        match entry with
        | CalendarLogic.Ev e ->
            e.ModuleId
            |> Option.bind (fun mid -> model.Modules |> List.tryFind (fun m -> m.Id = mid))
            |> Option.map StudyModule.displayName
        | CalendarLogic.Sess s -> s.ModuleCode

    StackPanel.create
        [ StackPanel.spacing 12.0
          StackPanel.horizontalAlignment HorizontalAlignment.Stretch
          StackPanel.children
              [ DockPanel.create
                    [ DockPanel.children
                          [ Border.create
                                [ DockPanel.dock Dock.Right
                                  Border.child (Ui.smallButton "Clear" (fun () -> dispatch ClearSelection)) ]
                            Border.create
                                [ Border.verticalAlignment VerticalAlignment.Center
                                  Border.child (sectionCaption "Selected") ] ] ]

                StackPanel.create
                    [ StackPanel.spacing 4.0
                      StackPanel.children
                          [ TextBlock.create
                                [ TextBlock.text (
                                      sprintf
                                          "%s %s"
                                          (CalendarLogic.Source.glyph (CalendarLogic.Entry.source entry))
                                          (CalendarLogic.Entry.kindLabel entry)
                                  )
                                  TextBlock.fontSize 11.0
                                  TextBlock.fontWeight FontWeight.SemiBold
                                  TextBlock.foreground (
                                      Theme.brush (Theme.readableOnColor (Theme.surfaces ()).Card hex)
                                  ) ]

                            TextBlock.create
                                [ TextBlock.text (CalendarLogic.Entry.title entry)
                                  TextBlock.fontSize 15.0
                                  TextBlock.fontWeight FontWeight.SemiBold
                                  TextBlock.foreground (Theme.text ())
                                  TextBlock.textWrapping TextWrapping.Wrap ] ] ]

                StackPanel.create
                    [ StackPanel.spacing 4.0
                      StackPanel.children
                          [ sectionCaption "Details"

                            detailRow "Date" ((CalendarLogic.Entry.start entry).ToString "dddd d MMMM yyyy")

                            if CalendarLogic.Entry.isAllDay entry then
                                detailRow "Time" "All day"
                            else
                                detailRow
                                    "Time"
                                    (Format.timeRange
                                        (CalendarLogic.Entry.start entry)
                                        (CalendarLogic.Entry.finish entry))

                            match moduleName with
                            | Some name -> detailRow "Module" name
                            | None -> ()

                            match entry with
                            | CalendarLogic.Ev e ->
                                match e.Location with
                                | Some location -> detailRow "Location" location
                                | None -> ()

                                match e.Notes with
                                | Some notes -> detailRow "Notes" notes
                                | None -> ()

                                match e.Source with
                                | Some source -> detailRow "Imported from" source
                                | None -> ()
                            | CalendarLogic.Sess s ->
                                detailRow "Reason" s.Reason
                                detailRow "Status" (if s.Completed then "Completed" else "Planned") ] ]

                // Import is a one-way copy. Nothing here syncs.
                match entry with
                | CalendarLogic.Ev e when e.Source.IsSome ->
                    TextBlock.create
                        [ TextBlock.text CalendarLogic.importNotice
                          TextBlock.fontSize 10.5
                          TextBlock.foreground (Theme.faint ())
                          TextBlock.textWrapping TextWrapping.Wrap ]
                | _ -> ()

                StackPanel.create
                    [ StackPanel.spacing 5.0
                      StackPanel.children
                          [ sectionCaption "Actions"

                            match entry with
                            | CalendarLogic.Ev _ ->
                                WrapPanel.create
                                    [ WrapPanel.children
                                          [ glassAction tokens "Edit" true true "Edit this event" (fun () ->
                                                dispatch EditSelected)

                                            glassAction tokens "Delete" false true "Delete this event" (fun () ->
                                                dispatch RequestDelete) ] ]

                                TextBlock.create
                                    [ TextBlock.text CalendarLogic.rescheduleNotice
                                      TextBlock.fontSize 10.5
                                      TextBlock.foreground (Theme.faint ())
                                      TextBlock.textWrapping TextWrapping.Wrap ]

                            // A planner session is the Planner's, so this screen
                            // does not offer to edit or delete it.
                            | CalendarLogic.Sess _ ->
                                WrapPanel.create
                                    [ WrapPanel.children
                                          [ glassAction tokens "Open Planner" true true "Open the Planner" (fun () ->
                                                dispatch (GotoPage Page.Planner)) ] ]

                                TextBlock.create
                                    [ TextBlock.text CalendarLogic.sessionNotice
                                      TextBlock.fontSize 10.5
                                      TextBlock.foreground (Theme.faint ())
                                      TextBlock.textWrapping TextWrapping.Wrap ] ] ]

                if model.PendingDelete then
                    Border.create
                        [ Border.background (Theme.brush (Theme.withAlphaHex "1F" (Theme.dangerHex ())))
                          Border.borderThickness 1.0
                          Border.borderBrush (Theme.brush (Theme.overlayEdgeHex "55" (Theme.dangerHex ())))
                          Border.cornerRadius (Tokens.radius tokens 10.0)
                          Border.padding (12.0, 10.0)
                          Border.child (
                              StackPanel.create
                                  [ StackPanel.spacing 8.0
                                    StackPanel.children
                                        [ TextBlock.create
                                              [ TextBlock.text "Delete this event permanently?"
                                                TextBlock.fontSize Theme.Type.rowMeta
                                                TextBlock.foreground (Theme.text ())
                                                TextBlock.textWrapping TextWrapping.Wrap ]
                                          WrapPanel.create
                                              [ WrapPanel.children
                                                    [ Ui.dangerButton "Yes, delete it" (fun () -> dispatch ConfirmDelete)
                                                      Ui.ghostButton "Cancel" (fun () -> dispatch CancelDelete) ] ] ] ]
                          ) ] ] ]
    :> IView

let private sidePanel (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) (entries: CalendarLogic.Entry list) : IView =
    GlassPanel.create
        [ GlassPanel.baseRadius DashboardLogic.Card.radius
          GlassPanel.elevation 1
          GlassPanel.highlightScale CalendarLogic.Geometry.contentHighlight
          GlassPanel.tint (Theme.surfaces ()).Card
          GlassPanel.child (
              Border.create
                  [ Border.padding (Tokens.space tokens 18.0)
                    Border.child (
                        StackPanel.create
                            [ StackPanel.spacing 14.0
                              StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                              StackPanel.children
                                  [ match model.Selected with
                                    | Some entry -> selectedPanel tokens model dispatch entry
                                    | None ->
                                        StackPanel.create
                                            [ StackPanel.spacing 6.0
                                              StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                                              StackPanel.children
                                                  [ sectionCaption "Next up"

                                                    let upcoming =
                                                        entries
                                                        |> List.filter (fun e ->
                                                            CalendarLogic.Entry.finish e >= model.Now)
                                                        |> List.sortBy CalendarLogic.Entry.start
                                                        |> List.truncate 6

                                                    if upcoming.IsEmpty then
                                                        TextBlock.create
                                                            [ TextBlock.text "Nothing coming up."
                                                              TextBlock.fontSize 11.5
                                                              TextBlock.foreground (Theme.faint ())
                                                              TextBlock.textWrapping TextWrapping.Wrap ]
                                                    else
                                                        for entry in upcoming do
                                                            entryChip tokens model dispatch entry

                                                    TextBlock.create
                                                        [ TextBlock.text
                                                              "Select anything on the calendar to see its details here."
                                                          TextBlock.fontSize 10.5
                                                          TextBlock.foreground (Theme.faint ())
                                                          TextBlock.margin (0.0, 6.0, 0.0, 0.0)
                                                          TextBlock.textWrapping TextWrapping.Wrap ] ] ] ] ]
                    ) ] ) ]
    :> IView

// ---------------------------------------------------------------------------
// the editor
// ---------------------------------------------------------------------------

let private editor (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) (draft: CalendarLogic.Draft) : IView =
    let error = CalendarLogic.draftError draft

    Border.create
        [ Border.background (Theme.brush (Ui.hexOf (Theme.surfaces ()).Overlay))
          Border.borderThickness 1.0
          Border.borderBrush (Theme.border ())
          Border.cornerRadius (Tokens.radius tokens 16.0)
          Border.padding (20.0, 18.0)
          Border.child (
              StackPanel.create
                  [ StackPanel.spacing 10.0
                    StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                    StackPanel.children
                        [ sectionCaption (if draft.Original.IsSome then "Edit event" else "New event")

                          Ui.textBox draft.Title "Title" (SetTitle >> dispatch)

                          Ui.combo (EventKind.all |> List.map EventKind.label) draft.KindIndex (SetKindIndex >> dispatch)

                          CheckBox.create
                              [ CheckBox.content "All day"
                                CheckBox.isChecked draft.AllDay
                                CheckBox.foreground (Theme.text ())
                                CheckBox.fontSize 12.0
                                Ui.accessibleName "All day"
                                CheckBox.onChecked ((fun _ -> dispatch (SetAllDay true)), SubPatchOptions.Always)
                                CheckBox.onUnchecked ((fun _ -> dispatch (SetAllDay false)), SubPatchOptions.Always) ]

                          // An all-day event has no end time to ask for, so it
                          // is not asked for.
                          if draft.AllDay then
                              Ui.textBox draft.StartText "Date — yyyy-MM-dd" (SetStart >> dispatch)
                          else
                              Ui.textBox draft.StartText "Starts — yyyy-MM-dd HH:mm" (SetStart >> dispatch)
                              Ui.textBox draft.EndText "Ends — yyyy-MM-dd HH:mm" (SetEnd >> dispatch)

                          Ui.textBox draft.Location "Location" (SetLocation >> dispatch)

                          Ui.combo
                              ("No module" :: (model.Modules |> List.map StudyModule.displayName))
                              draft.ModuleIndex
                              (SetModuleIndex >> dispatch)

                          Ui.multilineBox draft.Notes "Notes" 70.0 (SetNotes >> dispatch)

                          match error with
                          | Some message ->
                              TextBlock.create
                                  [ TextBlock.text message
                                    TextBlock.fontSize 11.5
                                    TextBlock.foreground (Theme.danger ())
                                    TextBlock.textWrapping TextWrapping.Wrap ]
                          | None -> ()

                          WrapPanel.create
                              [ WrapPanel.children
                                    [ glassAction
                                          tokens
                                          (if model.Saving then "Saving…" else "Save")
                                          true
                                          (CalendarLogic.canSave model.Saving draft)
                                          (match error with
                                           | Some message -> message
                                           | None -> "Save this event")
                                          (fun () -> dispatch SaveEvent)

                                      glassAction tokens "Cancel" false true "Discard this draft" (fun () ->
                                          dispatch CloseDraft) ] ] ] ]
          ) ]
    :> IView

// ---------------------------------------------------------------------------
// controls
// ---------------------------------------------------------------------------

let private controls (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let all = allEntries model
    let placement = CalendarLogic.placementFor model.Width
    let shownMode = CalendarLogic.effectiveMode placement model.Mode

    StackPanel.create
        [ StackPanel.spacing 10.0
          StackPanel.horizontalAlignment HorizontalAlignment.Stretch
          StackPanel.children
              [ DockPanel.create
                    [ DockPanel.children
                          [ WrapPanel.create
                                [ DockPanel.dock Dock.Right
                                  WrapPanel.children
                                      [ glassAction tokens "＋ New event" true true "Create an event" (fun () ->
                                            dispatch NewEvent) ] ]

                            WrapPanel.create
                                [ WrapPanel.children
                                      [ TextBlock.create
                                            [ TextBlock.text "Calendar"
                                              TextBlock.fontSize 19.0
                                              TextBlock.fontWeight FontWeight.SemiBold
                                              TextBlock.foreground (Theme.text ())
                                              TextBlock.margin (0.0, 0.0, 14.0, 0.0)
                                              TextBlock.verticalAlignment VerticalAlignment.Center ]

                                        TextBlock.create
                                            [ TextBlock.text (
                                                  CalendarLogic.rangeTitle shownMode (firstDay ()) model.Anchor
                                              )
                                              TextBlock.fontSize 13.5
                                              TextBlock.foreground (Theme.textSecondary ())
                                              TextBlock.verticalAlignment VerticalAlignment.Center
                                              TextBlock.textWrapping TextWrapping.Wrap ] ] ] ] ]

                WrapPanel.create
                    [ WrapPanel.children
                          [ navButton "‹" "Previous" (fun () -> dispatch Prev)
                            Border.create
                                [ Border.margin (6.0, 0.0)
                                  Border.child (Ui.smallButton "Today" (fun () -> dispatch Today)) ]
                            navButton "›" "Next" (fun () -> dispatch Next)

                            Border.create [ Border.width 16.0 ]

                            for mode in CalendarLogic.ViewMode.all do
                                chip
                                    tokens
                                    (CalendarLogic.ViewMode.label mode)
                                    None
                                    (Theme.accentHex ())
                                    (model.Mode = mode)
                                    (fun () -> dispatch (SetMode mode)) ] ]

                // Filters: only sources and kinds that genuinely appear.
                WrapPanel.create
                    [ WrapPanel.children
                          [ chip
                                tokens
                                "All"
                                (Some all.Length)
                                (Theme.accentHex ())
                                (CalendarLogic.Filter.isAll model.Filter)
                                (fun () -> dispatch ClearFilters)

                            for source in CalendarLogic.sourcesPresent all do
                                chip
                                    tokens
                                    (CalendarLogic.Source.label source)
                                    (Some(CalendarLogic.countFor (fun e -> CalendarLogic.Entry.source e = source) all))
                                    (Theme.eventKindHex (
                                        match source with
                                        | CalendarLogic.Source.Sessions -> EventKind.StudySession
                                        | CalendarLogic.Source.Events -> EventKind.Other
                                    ))
                                    (Set.contains source model.Filter.Sources)
                                    (fun () -> dispatch (ToggleSource source))

                            for kind in CalendarLogic.kindsPresent all do
                                chip
                                    tokens
                                    (EventKind.label kind)
                                    (Some(
                                        CalendarLogic.countFor
                                            (function
                                            | CalendarLogic.Ev e -> e.Kind = kind
                                            | CalendarLogic.Sess _ -> false)
                                            all
                                    ))
                                    (Theme.eventKindHex kind)
                                    (Set.contains kind model.Filter.Kinds)
                                    (fun () -> dispatch (ToggleKind kind))

                            for m in model.Modules |> List.filter (fun m -> not m.Archived) do
                                chip
                                    tokens
                                    m.Code
                                    None
                                    m.Color
                                    (Set.contains m.Id.Value model.Filter.Modules)
                                    (fun () -> dispatch (ToggleModule m.Id.Value)) ] ] ] ]
    :> IView

// ---------------------------------------------------------------------------
// page
// ---------------------------------------------------------------------------

let view (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let placement = CalendarLogic.placementFor model.Width
    let shownMode = CalendarLogic.effectiveMode placement model.Mode
    let entries = visibleEntries model

    let workspace: IView =
        StackPanel.create
            [ StackPanel.spacing 8.0
              StackPanel.horizontalAlignment HorizontalAlignment.Stretch
              StackPanel.children
                  [ // The stored mode is untouched; only the presentation fell
                    // back, and it says so.
                    if shownMode <> model.Mode then
                        TextBlock.create
                            [ TextBlock.text (CalendarLogic.narrowNotice model.Mode)
                              TextBlock.fontSize 11.0
                              TextBlock.foreground (Theme.muted ())
                              TextBlock.textWrapping TextWrapping.Wrap ]

                    match model.Error with
                    | Some message ->
                        let wash =
                            Theme.compositeOver (Theme.surfaces ()).Card (Theme.withAlphaHex "22" (Theme.dangerHex ()))

                        Border.create
                            [ Border.background (Theme.brush (Theme.withAlphaHex "22" (Theme.dangerHex ())))
                              Border.borderThickness 1.0
                              Border.borderBrush (Theme.brush (Theme.overlayEdgeHex "55" (Theme.dangerHex ())))
                              Border.cornerRadius (Tokens.radius tokens 12.0)
                              Border.padding (16.0, 12.0)
                              Border.child (
                                  StackPanel.create
                                      [ StackPanel.spacing 8.0
                                        StackPanel.children
                                            [ TextBlock.create
                                                  [ TextBlock.text ("The calendar could not be loaded: " + message)
                                                    TextBlock.fontSize Theme.Type.rowMeta
                                                    TextBlock.foreground (
                                                        Theme.brush (Theme.readableOnColor wash (Theme.dangerHex ()))
                                                    )
                                                    TextBlock.textWrapping TextWrapping.Wrap ]
                                              Border.create
                                                  [ Border.horizontalAlignment HorizontalAlignment.Left
                                                    Border.child (Ui.smallButton "Try again" (fun () -> dispatch Load)) ] ] ]
                              ) ]
                    | None -> ()

                    if entries.IsEmpty && not (CalendarLogic.Filter.isAll model.Filter) then
                        TextBlock.create
                            [ TextBlock.text "Every item is hidden by the filters above. Choose All to see them."
                              TextBlock.fontSize 12.5
                              TextBlock.foreground (Theme.muted ())
                              TextBlock.textWrapping TextWrapping.Wrap ]

                    match shownMode with
                    | CalendarLogic.ViewMode.Day -> dayView tokens model dispatch entries
                    | CalendarLogic.ViewMode.Week -> weekView tokens model dispatch entries
                    | CalendarLogic.ViewMode.Month -> monthView tokens model dispatch entries
                    | CalendarLogic.ViewMode.Agenda -> agendaView tokens model dispatch entries ] ]
        :> IView

    let side: IView list =
        [ match model.Draft with
          | Some draft -> editor tokens model dispatch draft
          | None -> ()

          sidePanel tokens model dispatch entries ]

    let body: IView =
        match placement.Side with
        | CalendarLogic.SidePlacement.Rail ->
            Grid.create
                [ Grid.columnDefinitions (sprintf "*,%f" CalendarLogic.sideWidth)
                  Grid.children
                      [ Border.create [ Grid.column 0; Border.child workspace ]
                        Border.create
                            [ Grid.column 1
                              Border.margin (18.0, 0.0, 0.0, 0.0)
                              Border.child (
                                  StackPanel.create
                                      [ StackPanel.spacing 12.0
                                        StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                                        StackPanel.children side ]
                              ) ] ] ]
            :> IView
        | CalendarLogic.SidePlacement.Below ->
            StackPanel.create
                [ StackPanel.spacing 16.0
                  StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                  StackPanel.children [ workspace; yield! side ] ]
            :> IView

    Border.create
        [ Border.onSizeChanged (
              (fun args ->
                  if CalendarLogic.placementFor model.Width <> CalendarLogic.placementFor args.NewSize.Width then
                      dispatch (Resized args.NewSize.Width)),
              SubPatchOptions.Always
          )
          Border.padding (24.0, 20.0)
          Border.child (
              DockPanel.create
                  [ DockPanel.children
                        [ Border.create
                              [ DockPanel.dock Dock.Top
                                Border.margin (0.0, 0.0, 0.0, 14.0)
                                Border.child (controls tokens model dispatch) ]

                          body ] ]
          ) ]
    :> IView
