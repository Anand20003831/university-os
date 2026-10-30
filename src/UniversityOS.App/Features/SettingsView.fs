module UniversityOS.App.Features.SettingsView

open System
open System.Diagnostics
open System.IO
open Elmish
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

/// Settings.
///
/// Eight categories behind a 220px rail. Every decision lives in
/// `SettingsLogic`; this file draws what that module resolves.
///
/// **Settings owns `UserSettings` and one field of `UiState`, and nothing
/// else.** Appearance belongs to Design Preview; credentials belong to
/// Connections. Both are reached by navigation, never duplicated — a second
/// writer for one store is how two screens end up disagreeing.

// ---------------------------------------------------------------------------
// model
// ---------------------------------------------------------------------------

type Model =
    { /// The last values read from disk — the baseline dirty state is measured
      /// against.
      Stored: UserSettings
      /// What the user is editing.
      Draft: UserSettings
      Category: SettingsLogic.Category
      SaveState: SettingsLogic.SaveState
      Saving: bool
      Loading: bool
      Error: string option
      // AI memory: real, transparent, and preserved from the previous screen.
      Memories: MemoryItem list
      MemoryEnabled: bool
      NewMemoryText: string
      MemoryBusy: string option
      MemoryNotice: string option
      PendingDeleteMemory: Guid option
      Width: float }

type External = Goto of Page

type Msg =
    | Load
    | Loaded of UserSettings
    | Failed of exn
    | SelectCategory of SettingsLogic.Category
    | SetName of string
    | SetWorkStart of int
    | SetWorkEnd of int
    | SetSession of int
    | SetBreak of int
    | SetMaxDaily of float
    | SetBuffer of float
    | SetHorizon of int
    | SetModel of string
    | ToggleAutoApply
    | Save
    | SavedOk
    | SaveFailed of string
    | ClearSaveState
    | MemoryStateLoaded of MemoryItem list * bool
    | ToggleMemoryEnabled
    | SetNewMemory of string
    | AddMemory
    | AddMemoryFailed of string
    | RequestDeleteMemory of Guid
    | CancelDeleteMemory
    | ConfirmDeleteMemory of Guid
    | ToggleMemoryItem of MemoryItem
    | MemoryChanged
    | RecomputeStats
    | StatsDone of int
    | ReflectNow
    | ReflectDone of Result<int, string>
    | ClearMemoryNotice
    | OpenDataFolder
    | Resized of float
    | GotoPage of Page

// ---------------------------------------------------------------------------
// loading
// ---------------------------------------------------------------------------

let private loadMemoryCmd (services: AppServices) =
    Elm.ofAsync
        (async {
            let! memories = services.Memory.GetAll()
            let! state = services.UiState.Load()
            return memories, state.AiMemoryEnabled
        })
        MemoryStateLoaded
        Failed

let init (services: AppServices) : Model * Cmd<Msg> =
    { Stored = UserSettings.defaults
      Draft = UserSettings.defaults
      Category = SettingsLogic.Category.Profile
      SaveState = SettingsLogic.SaveState.Idle
      Saving = false
      Loading = true
      Error = None
      Memories = []
      MemoryEnabled = true
      NewMemoryText = ""
      MemoryBusy = None
      MemoryNotice = None
      PendingDeleteMemory = None
      Width = 1600.0 },
    Cmd.batch
        [ Elm.ofAsync (async { return! services.Settings.Load() }) Loaded Failed
          loadMemoryCmd services ]

// ---------------------------------------------------------------------------
// update
// ---------------------------------------------------------------------------

let update (services: AppServices) (msg: Msg) (model: Model) : Model * Cmd<Msg> * External option =
    let plain m = m, Cmd.none, None

    /// Editing clears a stale "Saved." note, so the feedback belongs to the
    /// write it describes.
    let edit f =
        plain { model with Draft = f model.Draft; SaveState = SettingsLogic.SaveState.Idle }

    match msg with
    | Load -> { model with Loading = true }, Elm.ofAsync (async { return! services.Settings.Load() }) Loaded Failed, None

    // The loaded values become BOTH baseline and draft, so a fresh load is never
    // dirty.
    | Loaded settings -> plain { model with Stored = settings; Draft = settings; Loading = false; Error = None }

    | Failed ex -> plain { model with Loading = false; Saving = false; MemoryBusy = None; Error = Some ex.Message }

    // Switching category keeps the draft. Nothing is discarded silently.
    | SelectCategory category -> plain { model with Category = category; PendingDeleteMemory = None }

    | SetName v -> edit (fun s -> { s with UserName = v })
    | SetWorkStart v -> edit (fun s -> { s with WorkdayStartHour = v })
    | SetWorkEnd v -> edit (fun s -> { s with WorkdayEndHour = v })
    | SetSession v -> edit (fun s -> { s with SessionMinutes = v })
    | SetBreak v -> edit (fun s -> { s with BreakMinutes = v })
    | SetMaxDaily v -> edit (fun s -> { s with MaxDailyStudyHours = v })
    | SetBuffer v -> edit (fun s -> { s with DeadlineBufferHours = v })
    | SetHorizon v -> edit (fun s -> { s with PlannerHorizonDays = v })
    | SetModel v -> edit (fun s -> { s with AiModel = v })
    | ToggleAutoApply -> edit (fun s -> { s with AiAutoApply = not s.AiAutoApply })

    | Save ->
        // One guard covers clean, invalid AND a second click while saving.
        if not (SettingsLogic.canSave model.Saving model.Stored model.Draft) then
            plain model
        else
            let draft = model.Draft

            { model with Saving = true; SaveState = SettingsLogic.SaveState.Saving },
            Elm.ofAsync
                (async {
                    // `UserSettings.Theme` is deliberately NOT applied. It is
                    // retired; the appearance system owns the theme end to end,
                    // and re-applying a stale legacy value here would silently
                    // overwrite the user's real Dark/Light/System choice.
                    do! services.Settings.Save draft
                })
                (fun _ -> SavedOk)
                (fun ex -> SaveFailed ex.Message),
            None

    // The saved draft becomes the new baseline, which is what makes the form
    // clean again.
    | SavedOk ->
        { model with Saving = false; Stored = model.Draft; SaveState = SettingsLogic.SaveState.Saved },
        Elm.delayed 3000 ClearSaveState,
        None

    // **The draft survives a failure.** Losing a form's contents because a write
    // failed is the least forgivable thing a settings screen can do.
    | SaveFailed message ->
        plain { model with Saving = false; SaveState = SettingsLogic.SaveState.Failed message }

    | ClearSaveState ->
        plain
            { model with
                SaveState =
                    (match model.SaveState with
                     | SettingsLogic.SaveState.Saved -> SettingsLogic.SaveState.Idle
                     | other -> other) }

    // ---- AI memory ---------------------------------------------------------

    | MemoryStateLoaded(memories, enabled) -> plain { model with Memories = memories; MemoryEnabled = enabled }

    // Disabling stops the AI USING them. It never deletes one.
    | ToggleMemoryEnabled ->
        let enabled = not model.MemoryEnabled

        { model with MemoryEnabled = enabled },
        Elm.ofAsync
            (async {
                let! state = services.UiState.Load()
                do! services.UiState.Save { state with AiMemoryEnabled = enabled }
            })
            (fun _ -> MemoryChanged)
            Failed,
        None

    | SetNewMemory text -> plain { model with NewMemoryText = text }

    | AddMemory ->
        if model.NewMemoryText.Trim().Length = 0 || model.MemoryBusy.IsSome then
            plain model
        else
            let item =
                MemoryItem.create services.Clock.Now MemorySource.User "user" (model.NewMemoryText.Trim())

            { model with MemoryBusy = Some "add" },
            Elm.ofAsync
                (async { return! services.Memory.Save item })
                (fun _ -> MemoryChanged)
                (fun ex -> AddMemoryFailed ex.Message),
            None

    // The text stays in the box, so a failed write costs nothing typed.
    | AddMemoryFailed message ->
        plain { model with MemoryBusy = None; MemoryNotice = Some("Could not add that memory: " + message) }

    | RequestDeleteMemory id -> plain { model with PendingDeleteMemory = Some id }
    | CancelDeleteMemory -> plain { model with PendingDeleteMemory = None }

    | ConfirmDeleteMemory id ->
        if model.MemoryBusy.IsSome then
            plain { model with PendingDeleteMemory = None }
        else
            { model with MemoryBusy = Some "delete"; PendingDeleteMemory = None },
            Elm.ofAsync (async { return! services.Memory.Delete id }) (fun _ -> MemoryChanged) Failed,
            None

    | ToggleMemoryItem item ->
        model,
        Elm.ofAsync
            (async {
                return! services.Memory.Save { item with Enabled = not item.Enabled; UpdatedAt = services.Clock.Now }
            })
            (fun _ -> MemoryChanged)
            Failed,
        None

    // A confirmed round-trip is the only thing that clears the compose box, so a
    // memory that was never stored never vanishes from it.
    | MemoryChanged -> { model with MemoryBusy = None; NewMemoryText = "" }, loadMemoryCmd services, None

    | RecomputeStats ->
        { model with MemoryBusy = Some "stats" },
        Elm.ofAsync (async { return! services.MemoryService.ComputeStats() }) (fun items -> StatsDone items.Length) Failed,
        None

    | StatsDone count ->
        { model with
            MemoryBusy = None
            MemoryNotice = Some(sprintf "Recomputed %d habit statistic(s) from your history." count) },
        Cmd.batch [ loadMemoryCmd services; Elm.delayed 5000 ClearMemoryNotice ],
        None

    | ReflectNow ->
        { model with MemoryBusy = Some "reflect" },
        Elm.ofAsync (async { return! services.MemoryService.Reflect() }) ReflectDone Failed,
        None

    | ReflectDone(Ok count) ->
        { model with MemoryBusy = None; MemoryNotice = Some(sprintf "AI reflection updated %d observation(s)." count) },
        Cmd.batch [ loadMemoryCmd services; Elm.delayed 5000 ClearMemoryNotice ],
        None

    | ReflectDone(Error message) ->
        { model with MemoryBusy = None; MemoryNotice = Some message }, Elm.delayed 6000 ClearMemoryNotice, None

    | ClearMemoryNotice -> plain { model with MemoryNotice = None }

    | OpenDataFolder ->
        let path =
            Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.ApplicationData, "UniversityOS")

        (try
            Process.Start(ProcessStartInfo(FileName = path, UseShellExecute = true)) |> ignore
         with _ ->
             ())

        plain model

    | Resized width -> plain { model with Width = width }
    | GotoPage page -> model, Cmd.none, Some(Goto page)

// ---------------------------------------------------------------------------
// view — shared treatments
// ---------------------------------------------------------------------------

let private weight (w: int) = enum<FontWeight> w

let private caption (text: string) : IView =
    TextBlock.create
        [ TextBlock.text (text.ToUpperInvariant())
          TextBlock.fontSize 10.5
          TextBlock.fontWeight (weight 600)
          TextBlock.foreground (Theme.label ())
          TextBlock.textWrapping TextWrapping.Wrap ]
    :> IView

let private bodyText (text: string) (size: float) (brush: IBrush) : IView =
    TextBlock.create
        [ TextBlock.text text
          TextBlock.fontSize size
          TextBlock.foreground brush
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

let private card (tokens: Tokens) (children: IView list) : IView =
    GlassPanel.create
        [ GlassPanel.baseRadius SettingsLogic.Geometry.cardRadius
          GlassPanel.elevation 1
          GlassPanel.highlightScale SettingsLogic.Geometry.contentHighlight
          GlassPanel.tint (Theme.surfaces ()).Card
          GlassPanel.child (
              Border.create
                  [ Border.padding (Tokens.space tokens 20.0)
                    Border.child (
                        StackPanel.create
                            [ StackPanel.spacing 12.0
                              StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                              StackPanel.children children ] ) ] ) ]
    :> IView

/// A labelled row that states what it actually changes.
let private settingRow (label: string) (help: string) (control: IView) : IView =
    StackPanel.create
        [ StackPanel.spacing 4.0
          StackPanel.margin (0.0, 0.0, 0.0, 6.0)
          StackPanel.horizontalAlignment HorizontalAlignment.Stretch
          StackPanel.children
              [ TextBlock.create
                    [ TextBlock.text label
                      TextBlock.fontSize 12.5
                      TextBlock.fontWeight FontWeight.SemiBold
                      TextBlock.foreground (Theme.text ()) ]
                control
                if help.Trim().Length > 0 then
                    bodyText help 10.5 (Theme.faint ()) ] ]
    :> IView

/// A segmented control: identical geometry in both states, only fill and border
/// change, so a row never reflows as it is used.
let private options
    (tokens: Tokens)
    (name: string)
    (items: ('a * string) list)
    (current: 'a)
    (onPick: 'a -> unit)
    : IView =
    WrapPanel.create
        [ WrapPanel.children
              (items
               |> List.map (fun (value, label) ->
                   let selected = value = current

                   Button.create
                       [ Button.content label
                         Button.background (
                             if selected then
                                 Theme.brush (
                                     Theme.withAlphaHex SettingsLogic.Geometry.chipFillAlpha (Theme.accentHex ())
                                 )
                             else
                                 Theme.transparent
                         )
                         Button.borderThickness 1.0
                         Button.borderBrush (if selected then Theme.brush (Theme.accentHex ()) else Theme.border ())
                         Button.foreground (if selected then Theme.accent () else Theme.textSecondary ())
                         Button.cornerRadius (Tokens.radius tokens 999.0)
                         Button.minHeight 28.0
                         Button.padding (12.0, 4.0)
                         Button.fontSize 11.5
                         Button.fontWeight FontWeight.SemiBold
                         Button.margin (0.0, 0.0, 6.0, 6.0)
                         Button.cursor Ui.hand
                         Ui.accessibleName (sprintf "%s: %s" name label)
                         Button.onClick ((fun _ -> onPick value), SubPatchOptions.Always) ]
                   :> IView)) ]
    :> IView

let private toggleRow (label: string) (help: string) (value: bool) (onToggle: unit -> unit) : IView =
    StackPanel.create
        [ StackPanel.spacing 4.0
          StackPanel.margin (0.0, 0.0, 0.0, 6.0)
          StackPanel.horizontalAlignment HorizontalAlignment.Stretch
          StackPanel.children
              [ CheckBox.create
                    [ CheckBox.content label
                      CheckBox.isChecked value
                      CheckBox.fontSize 12.5
                      CheckBox.foreground (Theme.text ())
                      Ui.accessibleName label
                      CheckBox.onChecked ((fun _ -> onToggle ()), SubPatchOptions.Always)
                      CheckBox.onUnchecked ((fun _ -> onToggle ()), SubPatchOptions.Always) ]
                if help.Trim().Length > 0 then
                    bodyText help 10.5 (Theme.faint ()) ] ]
    :> IView

let private notice (tokens: Tokens) (hex: string) (glyph: string) (text: string) : IView =
    let wash =
        Theme.compositeOver (Theme.surfaces ()).Card (Theme.withAlphaHex SettingsLogic.Geometry.noticeFillAlpha hex)

    Border.create
        [ Border.background (Theme.brush (Theme.withAlphaHex SettingsLogic.Geometry.noticeFillAlpha hex))
          Border.borderThickness 1.0
          Border.borderBrush (Theme.brush (Theme.overlayEdgeHex "40" hex))
          Border.cornerRadius (Tokens.radius tokens 10.0)
          Border.padding (12.0, 9.0)
          Border.child (
              Grid.create
                  [ Grid.columnDefinitions "Auto,9,*"
                    Grid.children
                        [ TextBlock.create
                              [ Grid.column 0
                                TextBlock.text glyph
                                TextBlock.fontSize 11.0
                                TextBlock.foreground (Theme.brush (Theme.readableOnColor wash hex))
                                TextBlock.verticalAlignment VerticalAlignment.Top
                                Ui.decorative () ]
                          TextBlock.create
                              [ Grid.column 2
                                TextBlock.text text
                                TextBlock.fontSize 11.5
                                TextBlock.foreground (Theme.textSecondary ())
                                TextBlock.textWrapping TextWrapping.Wrap ] ] ] ) ]
    :> IView

/// A navigation card. The action always names what opens.
let private handoffCard (tokens: Tokens) (dispatch: Msg -> unit) (h: SettingsLogic.Handoff) : IView =
    card
        tokens
        [ TextBlock.create
              [ TextBlock.text h.Title
                TextBlock.fontSize 14.0
                TextBlock.fontWeight FontWeight.SemiBold
                TextBlock.foreground (Theme.text ())
                TextBlock.textWrapping TextWrapping.Wrap ]
          bodyText h.Body 12.0 (Theme.textSecondary ())
          Border.create
              [ Border.horizontalAlignment HorizontalAlignment.Left
                Border.child (glassAction tokens h.Action true true h.Action (fun () -> dispatch (GotoPage h.Target))) ] ]

// ---------------------------------------------------------------------------
// the save bar
// ---------------------------------------------------------------------------

let private saveBar (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let blocked = SettingsLogic.saveBlockedReason model.Saving model.Stored model.Draft

    StackPanel.create
        [ StackPanel.spacing 6.0
          StackPanel.margin (0.0, 4.0, 0.0, 0.0)
          StackPanel.horizontalAlignment HorizontalAlignment.Stretch
          StackPanel.children
              [ WrapPanel.create
                    [ WrapPanel.children
                          [ glassAction
                                tokens
                                (if model.Saving then "Saving…" else "Save changes")
                                true
                                (SettingsLogic.canSave model.Saving model.Stored model.Draft)
                                (match blocked with
                                 | Some reason -> reason
                                 | None -> "Save these settings")
                                (fun () -> dispatch Save)

                            match blocked with
                            | Some reason ->
                                TextBlock.create
                                    [ TextBlock.text reason
                                      TextBlock.fontSize 11.0
                                      TextBlock.foreground (Theme.faint ())
                                      TextBlock.verticalAlignment VerticalAlignment.Center
                                      TextBlock.textWrapping TextWrapping.Wrap ]
                            | None -> () ] ]

                match model.SaveState with
                | SettingsLogic.SaveState.Failed message ->
                    notice tokens (Theme.dangerHex ()) "✕" ("Could not save: " + message)
                | SettingsLogic.SaveState.Saved -> notice tokens (Theme.successHex ()) "✓" "Saved."
                | _ -> () ] ]
    :> IView

// ---------------------------------------------------------------------------
// the eight panels
// ---------------------------------------------------------------------------

let private profilePanel (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView list =
    [ card
          tokens
          [ caption "Profile"
            settingRow
                "Your name"
                "The Assistant sends this as context, and the Dashboard greets you with it. Nothing else uses it."
                (Ui.textBox model.Draft.UserName "Your name" (SetName >> dispatch))
            saveBar tokens model dispatch ] ]

let private plannerPanel (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView list =
    let d = model.Draft

    [ card
          tokens
          [ caption "Planner"

            settingRow
                "Study day starts"
                "The earliest the planner may place work."
                (options
                    tokens
                    "Study day starts"
                    (SettingsLogic.hourOptions |> List.map (fun h -> h, SettingsLogic.hourLabel h))
                    d.WorkdayStartHour
                    (SetWorkStart >> dispatch))

            settingRow
                "Study day ends"
                "The latest the planner may place work."
                (options
                    tokens
                    "Study day ends"
                    (SettingsLogic.hourOptions |> List.map (fun h -> h, SettingsLogic.hourLabel h))
                    d.WorkdayEndHour
                    (SetWorkEnd >> dispatch))

            settingRow
                "Session length"
                "How long one focused block is."
                (options
                    tokens
                    "Session length"
                    (SettingsLogic.sessionOptions |> List.map (fun m -> m, sprintf "%d min" m))
                    d.SessionMinutes
                    (SetSession >> dispatch))

            settingRow
                "Break length"
                "The gap the planner leaves between blocks."
                (options
                    tokens
                    "Break length"
                    (SettingsLogic.breakOptions |> List.map (fun m -> m, sprintf "%d min" m))
                    d.BreakMinutes
                    (SetBreak >> dispatch))

            settingRow
                "Daily limit"
                "The most work the planner will schedule in one day, and the Dashboard's daily target."
                (options
                    tokens
                    "Daily limit"
                    (SettingsLogic.maxDailyOptions |> List.map (fun h -> h, sprintf "%g h" h))
                    d.MaxDailyStudyHours
                    (SetMaxDaily >> dispatch))

            settingRow
                "Planning horizon"
                "How far ahead the planner schedules."
                (options
                    tokens
                    "Planning horizon"
                    (SettingsLogic.horizonOptions |> List.map (fun days -> days, sprintf "%d days" days))
                    d.PlannerHorizonDays
                    (SetHorizon >> dispatch))

            saveBar tokens model dispatch ]

      // Kept apart deliberately: the name suggests scheduling, and it does not.
      card
          tokens
          [ caption "Advanced"
            settingRow
                SettingsLogic.deadlineBufferLabel
                SettingsLogic.deadlineBufferNotice
                (options
                    tokens
                    SettingsLogic.deadlineBufferLabel
                    (SettingsLogic.bufferOptions |> List.map (fun h -> h, SettingsLogic.bufferLabel h))
                    d.DeadlineBufferHours
                    (SetBuffer >> dispatch)) ] ]

let private memoryPanel (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let busy = model.MemoryBusy.IsSome

    let memoryRow (item: MemoryItem) : IView =
        Border.create
            [ Border.background (Theme.cardAlt ())
              Border.borderThickness 1.0
              Border.borderBrush (Theme.border ())
              Border.cornerRadius (Tokens.radius tokens 10.0)
              Border.padding (12.0, 9.0)
              Border.margin (0.0, 0.0, 0.0, 6.0)
              Border.child (
                  StackPanel.create
                      [ StackPanel.spacing 5.0
                        StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                        StackPanel.children
                            [ bodyText item.Content 12.0 (if item.Enabled then Theme.text () else Theme.faint ())

                              WrapPanel.create
                                  [ WrapPanel.children
                                        [ TextBlock.create
                                              [ TextBlock.text (
                                                    sprintf
                                                        "%s · %s"
                                                        (MemorySource.toString item.Source)
                                                        (Format.shortDate item.UpdatedAt)
                                                )
                                                TextBlock.fontSize 10.0
                                                TextBlock.foreground (Theme.faint ())
                                                TextBlock.margin (0.0, 0.0, 10.0, 0.0)
                                                TextBlock.verticalAlignment VerticalAlignment.Center ]

                                          Ui.smallButton (if item.Enabled then "Mute" else "Unmute") (fun () ->
                                              dispatch (ToggleMemoryItem item))

                                          Ui.smallButton "Delete" (fun () -> dispatch (RequestDeleteMemory item.Id)) ] ]

                              if model.PendingDeleteMemory = Some item.Id then
                                  StackPanel.create
                                      [ StackPanel.spacing 6.0
                                        StackPanel.children
                                            [ bodyText "Delete this memory permanently?" 11.5 (Theme.text ())
                                              WrapPanel.create
                                                  [ WrapPanel.children
                                                        [ Ui.dangerButton "Yes, delete it" (fun () ->
                                                              dispatch (ConfirmDeleteMemory item.Id))
                                                          Ui.ghostButton "Cancel" (fun () ->
                                                              dispatch CancelDeleteMemory) ] ] ] ] ] ]
              ) ]
        :> IView

    card
        tokens
        [ caption "What the Assistant remembers"

          toggleRow "Use long-term memory" SettingsLogic.memoryNotice model.MemoryEnabled (fun () ->
              dispatch ToggleMemoryEnabled)

          // Disabling stops the AI using them. It never deletes one, and the
          // screen says so rather than leaving it to be discovered.
          if not model.MemoryEnabled then
              notice
                  tokens
                  (Theme.warningHex ())
                  "○"
                  "Memory is off, so the Assistant will not use anything below. Nothing has been deleted — turning it back on restores all of it."

          match model.MemoryNotice with
          | Some message -> notice tokens (Theme.accentHex ()) "•" message
          | None -> ()

          Ui.textBox model.NewMemoryText "Something the Assistant should remember" (SetNewMemory >> dispatch)

          WrapPanel.create
              [ WrapPanel.children
                    [ glassAction
                          tokens
                          (if model.MemoryBusy = Some "add" then "Adding…" else "Add memory")
                          true
                          (model.NewMemoryText.Trim().Length > 0 && not busy)
                          (if model.NewMemoryText.Trim().Length = 0 then
                               "Write something to remember first"
                           else
                               "Add this memory")
                          (fun () -> dispatch AddMemory)

                      glassAction
                          tokens
                          (if model.MemoryBusy = Some "stats" then "Working…" else "Recompute habits")
                          false
                          (not busy)
                          "Recompute habit statistics from your history"
                          (fun () -> dispatch RecomputeStats)

                      glassAction
                          tokens
                          (if model.MemoryBusy = Some "reflect" then "Reflecting…" else "Reflect now")
                          false
                          (not busy)
                          "Ask the Assistant to reflect on your recent work"
                          (fun () -> dispatch ReflectNow) ] ]

          if model.Memories.IsEmpty then
              bodyText "Nothing is remembered yet." 11.5 (Theme.faint ())
          else
              StackPanel.create
                  [ StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                    StackPanel.children (model.Memories |> List.map memoryRow) ] ]

let private aiPanel (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView list =
    [ card
          tokens
          [ caption "Assistant"

            settingRow
                "Model"
                "The model identifier sent with every request."
                (options
                    tokens
                    "Model"
                    (SettingsLogic.modelOptions |> List.map (fun m -> m, m))
                    model.Draft.AiModel
                    (SetModel >> dispatch))

            toggleRow "Apply captured changes automatically" SettingsLogic.autoApplyNotice model.Draft.AiAutoApply (fun () ->
                dispatch ToggleAutoApply)

            saveBar tokens model dispatch ]

      memoryPanel tokens model dispatch

      handoffCard tokens dispatch SettingsLogic.aiCredentialHandoff ]

let private appearancePanel (tokens: Tokens) (dispatch: Msg -> unit) : IView list =
    [ handoffCard tokens dispatch SettingsLogic.appearanceHandoff ]

let private notificationsPanel (tokens: Tokens) : IView list =
    [ card
          tokens
          [ caption "Notifications"
            bodyText SettingsLogic.notificationsNotice 12.0 (Theme.textSecondary ())
            bodyText SettingsLogic.notificationsLegacyNotice 10.5 (Theme.faint ()) ] ]

let private integrationsPanel (tokens: Tokens) (dispatch: Msg -> unit) : IView list =
    [ handoffCard tokens dispatch SettingsLogic.integrationsHandoff ]

let private privacyPanel (tokens: Tokens) : IView list =
    [ card
          tokens
          [ caption "Where your data lives"

            for fact in SettingsLogic.privacyFacts do
                Grid.create
                    [ Grid.columnDefinitions "Auto,9,*"
                      Grid.margin (0.0, 1.0)
                      Grid.children
                          [ TextBlock.create
                                [ Grid.column 0
                                  TextBlock.text "·"
                                  TextBlock.fontSize 11.5
                                  TextBlock.foreground (Theme.faint ())
                                  TextBlock.verticalAlignment VerticalAlignment.Top
                                  Ui.decorative () ]
                            TextBlock.create
                                [ Grid.column 2
                                  TextBlock.text fact
                                  TextBlock.fontSize 11.5
                                  TextBlock.foreground (Theme.textSecondary ())
                                  TextBlock.textWrapping TextWrapping.Wrap ] ] ] ] ]

let private advancedPanel (tokens: Tokens) (dispatch: Msg -> unit) : IView list =
    [ card
          tokens
          [ caption "Keyboard shortcuts"
            bodyText SettingsLogic.shortcutsNotice 10.5 (Theme.faint ())

            for chord, description in SettingsLogic.shortcuts do
                Grid.create
                    [ Grid.columnDefinitions SettingsLogic.Geometry.detailColumns
                      Grid.margin (0.0, 2.0)
                      Grid.children
                          [ TextBlock.create
                                [ Grid.column 0
                                  TextBlock.text chord
                                  TextBlock.fontSize 11.0
                                  TextBlock.fontWeight FontWeight.SemiBold
                                  TextBlock.foreground (Theme.textSecondary ()) ]
                            TextBlock.create
                                [ Grid.column 2
                                  TextBlock.text description
                                  TextBlock.fontSize 11.0
                                  TextBlock.foreground (Theme.faint ())
                                  TextBlock.textWrapping TextWrapping.Wrap ] ] ] ]

      card
          tokens
          [ caption "Storage"
            bodyText
                "Your data is kept in a local SQLite database in the University OS application data folder."
                11.5
                (Theme.textSecondary ())
            Border.create
                [ Border.horizontalAlignment HorizontalAlignment.Left
                  Border.child (
                      glassAction tokens "Open data folder" false true "Open the data folder" (fun () ->
                          dispatch OpenDataFolder)
                  ) ] ] ]

// ---------------------------------------------------------------------------
// page
// ---------------------------------------------------------------------------

let view (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let placement = SettingsLogic.placementFor model.Width

    let panel: IView list =
        match model.Category with
        | SettingsLogic.Category.Profile -> profilePanel tokens model dispatch
        | SettingsLogic.Category.Planner -> plannerPanel tokens model dispatch
        | SettingsLogic.Category.Ai -> aiPanel tokens model dispatch
        | SettingsLogic.Category.Appearance -> appearancePanel tokens dispatch
        | SettingsLogic.Category.Notifications -> notificationsPanel tokens
        | SettingsLogic.Category.Integrations -> integrationsPanel tokens dispatch
        | SettingsLogic.Category.Privacy -> privacyPanel tokens
        | SettingsLogic.Category.Advanced -> advancedPanel tokens dispatch

    let railRow (c: SettingsLogic.Category) : IView =
        let selected = model.Category = c
        let accent = Theme.accentHex ()

        Button.create
            [ Button.background (
                  if selected then
                      Theme.brush (Theme.withAlphaHex SettingsLogic.Geometry.selectedFillAlpha accent)
                  else
                      Theme.transparent
              )
              // Reserved in BOTH states, so selecting recolours and never resizes.
              Button.borderThickness 1.0
              Button.borderBrush (
                  if selected then
                      Theme.brush (Theme.overlayEdgeHex SettingsLogic.Geometry.selectedEdgeAlpha accent)
                  else
                      Theme.transparent
              )
              Button.cornerRadius (Tokens.radius tokens 11.0)
              Button.minHeight SettingsLogic.Geometry.rowHeight
              Button.padding (12.0, 0.0)
              Button.margin (0.0, 0.0, 6.0, 4.0)
              Button.horizontalAlignment (
                  if placement = SettingsLogic.RailPlacement.Side then
                      HorizontalAlignment.Stretch
                  else
                      HorizontalAlignment.Left
              )
              Button.horizontalContentAlignment HorizontalAlignment.Left
              Button.cursor Ui.hand
              Ui.accessibleName (sprintf "%s settings" (SettingsLogic.Category.label c))
              Button.onClick ((fun _ -> dispatch (SelectCategory c)), SubPatchOptions.Always)
              Button.content (
                  StackPanel.create
                      [ StackPanel.orientation Orientation.Horizontal
                        StackPanel.spacing 10.0
                        StackPanel.children
                            [ TextBlock.create
                                  [ TextBlock.text (SettingsLogic.Category.glyph c)
                                    TextBlock.fontSize 13.0
                                    TextBlock.width 16.0
                                    TextBlock.foreground (if selected then Theme.accent () else Theme.muted ())
                                    TextBlock.verticalAlignment VerticalAlignment.Center
                                    Ui.decorative () ]
                              TextBlock.create
                                  [ TextBlock.text (SettingsLogic.Category.label c)
                                    TextBlock.fontSize 13.0
                                    TextBlock.fontWeight (if selected then FontWeight.SemiBold else FontWeight.Normal)
                                    TextBlock.foreground (if selected then Theme.accent () else Theme.textSecondary ())
                                    TextBlock.verticalAlignment VerticalAlignment.Center ] ] ]
              ) ]
        :> IView

    let rail: IView =
        if placement = SettingsLogic.RailPlacement.Side then
            StackPanel.create
                [ StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                  StackPanel.children (SettingsLogic.Category.all |> List.map railRow) ]
            :> IView
        else
            // Narrow: the rail becomes a wrapped row above the content. It never
            // disappears.
            WrapPanel.create [ WrapPanel.children (SettingsLogic.Category.all |> List.map railRow) ] :> IView

    let content: IView =
        Ui.scrollNoChrome (
            StackPanel.create
                [ StackPanel.spacing 14.0
                  StackPanel.maxWidth SettingsLogic.Geometry.contentWidth
                  StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                  StackPanel.children
                      [ StackPanel.create
                            [ StackPanel.spacing 2.0
                              StackPanel.children
                                  [ TextBlock.create
                                        [ TextBlock.text (SettingsLogic.Category.label model.Category)
                                          TextBlock.fontSize 19.0
                                          TextBlock.fontWeight FontWeight.SemiBold
                                          TextBlock.foreground (Theme.text ()) ]
                                    bodyText (SettingsLogic.categoryCaption model.Category) 12.0 (Theme.muted ()) ] ]

                        match model.Error with
                        | Some message -> notice tokens (Theme.dangerHex ()) "⚠" ("Could not load settings: " + message)
                        | None -> ()

                        yield! panel ] ]
        )

    let split: IView =
        if placement = SettingsLogic.RailPlacement.Side then
            Grid.create
                [ Grid.columnDefinitions (sprintf "%f,28,*" (SettingsLogic.railColumnWidth model.Width))
                  Grid.children
                      [ Border.create [ Grid.column 0; Border.child rail ]
                        Border.create [ Grid.column 2; Border.child content ] ] ]
            :> IView
        else
            DockPanel.create
                [ DockPanel.children
                      [ Border.create
                            [ DockPanel.dock Dock.Top
                              Border.margin (0.0, 0.0, 0.0, 14.0)
                              Border.child rail ]
                        content ] ]
            :> IView

    Border.create
        [ Border.onSizeChanged (
              (fun args ->
                  if SettingsLogic.placementFor model.Width <> SettingsLogic.placementFor args.NewSize.Width then
                      dispatch (Resized args.NewSize.Width)),
              SubPatchOptions.Always
          )
          Border.padding (24.0, 20.0)
          Border.child (
              DockPanel.create
                  [ DockPanel.children
                        [ StackPanel.create
                              [ DockPanel.dock Dock.Top
                                StackPanel.spacing 3.0
                                StackPanel.margin (0.0, 0.0, 0.0, 18.0)
                                StackPanel.children
                                    [ TextBlock.create
                                          [ TextBlock.text "Control Center"
                                            TextBlock.fontSize 28.0
                                            TextBlock.fontWeight FontWeight.SemiBold
                                            TextBlock.foreground (Theme.text ())
                                            TextBlock.textWrapping TextWrapping.Wrap ]
                                      bodyText "Configure University OS to work the way you do." 13.5 (Theme.muted ()) ] ]

                          split ] ]
          ) ]
    :> IView
