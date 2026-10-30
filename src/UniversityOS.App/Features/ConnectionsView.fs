module UniversityOS.App.Features.ConnectionsView

open System
open Elmish
open Avalonia
open Avalonia.Controls
open Avalonia.Input
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

/// Connections.
///
/// Nine real connectors as a card grid, with a detail panel for the selected
/// one. Every decision lives in `ConnectionsLogic`; this file draws what that
/// module resolves.
///
/// **Three things this screen refuses to do.** It does not claim a connection is
/// healthy — only that the last real operation succeeded. It does not read a
/// stored secret in order to decorate a placeholder. And it does not describe
/// disconnecting as revoking access, because every connector's `Disconnect()`
/// deletes a local file and contacts nobody.

// ---------------------------------------------------------------------------
// model
// ---------------------------------------------------------------------------

type Model =
    { Connectors: ConnectorView list
      History: (string * DateTime * string) list
      /// Key of the provider whose detail panel is open.
      Selected: string option
      /// Field values being edited for the selected provider. **Secret fields
      /// are only ever written here by the user** — nothing seeds them.
      FieldValues: Map<string, string>
      /// (connector key, operation) currently running.
      Busy: (string * string) option
      /// Last operation result per connector key.
      Messages: Map<string, Result<string, string>>
      PendingDisconnect: string option
      Now: DateTime
      Width: float
      Loading: bool
      Error: string option }

type External = Goto of Page

type Msg =
    | Load
    | Loaded of ConnectorView list * (string * DateTime * string) list * DateTime
    | Failed of exn
    | Select of string
    | ClearSelection
    | SetField of string * string
    | Configure of string
    | Test of string
    | SyncNow of string
    | RequestDisconnect of string
    | CancelDisconnect
    | ConfirmDisconnect of string
    | OperationDone of string * Result<string, string>
    | SetFrequency of string * int
    | Resized of float
    | GotoPage of Page

// ---------------------------------------------------------------------------
// loading
// ---------------------------------------------------------------------------

let private loadCmd (services: AppServices) =
    Elm.ofAsync
        (async {
            let! connectors = services.Registry.List()
            let! history = services.SyncLog.GetRecent 12
            return connectors, history, services.Clock.Now
        })
        Loaded
        Failed

let init (services: AppServices) : Model * Cmd<Msg> =
    { Connectors = []
      History = []
      Selected = None
      FieldValues = Map.empty
      Busy = None
      Messages = Map.empty
      PendingDisconnect = None
      Now = services.Clock.Now
      Width = 1600.0
      Loading = true
      Error = None },
    loadCmd services

// ---------------------------------------------------------------------------
// update
// ---------------------------------------------------------------------------

let update (services: AppServices) (msg: Msg) (model: Model) : Model * Cmd<Msg> * External option =
    let plain m = m, Cmd.none, None

    match msg with
    | Load -> { model with Loading = true }, loadCmd services, None

    | Loaded(connectors, history, now) ->
        plain
            { model with
                Connectors = connectors
                History = history
                Now = now
                Selected = ConnectionsLogic.reconcileSelection model.Selected connectors
                Loading = false
                Error = None }

    | Failed ex -> plain { model with Loading = false; Busy = None; Error = Some ex.Message }

    | Select key ->
        if model.Selected = Some key then
            plain { model with Selected = None; FieldValues = Map.empty; PendingDisconnect = None }
        else
            // Seed the form with saved NON-SECRET config so the user edits
            // rather than retypes. A secret is never seeded, because reading one
            // to prefill a box would mean reading it at all.
            let seeded =
                model.Connectors
                |> List.tryFind (fun c -> c.Metadata.Key = key)
                |> Option.map (fun c ->
                    c.Metadata.Fields
                    |> List.choose (fun f ->
                        if f.Secret then
                            None
                        else
                            c.State.Config.TryFind f.Key |> Option.map (fun v -> f.Key, v))
                    |> Map.ofList)
                |> Option.defaultValue Map.empty

            plain { model with Selected = Some key; FieldValues = seeded; PendingDisconnect = None }

    | ClearSelection -> plain { model with Selected = None; FieldValues = Map.empty; PendingDisconnect = None }

    | SetField(key, value) -> plain { model with FieldValues = model.FieldValues.Add(key, value) }

    | Configure key ->
        if model.Busy.IsSome then
            plain model
        else
            { model with Busy = Some(key, "configure") },
            Elm.ofAsync
                (async { return! services.Registry.Configure(key, model.FieldValues) })
                (fun result -> OperationDone(key, result))
                Failed,
            None

    | Test key ->
        if model.Busy.IsSome then
            plain model
        else
            { model with Busy = Some(key, "test") },
            Elm.ofAsync
                (async { return! services.Registry.Test key })
                (fun result -> OperationDone(key, result))
                Failed,
            None

    | SyncNow key ->
        if model.Busy.IsSome then
            plain model
        else
            { model with Busy = Some(key, "sync") },
            Elm.ofAsync
                (async {
                    let! result = services.Registry.Sync key
                    return result |> Result.map SyncOutcome.describe
                })
                (fun result -> OperationDone(key, result))
                Failed,
            None

    | RequestDisconnect key -> plain { model with PendingDisconnect = Some key }
    | CancelDisconnect -> plain { model with PendingDisconnect = None }

    | ConfirmDisconnect key ->
        if model.Busy.IsSome then
            plain { model with PendingDisconnect = None }
        else
            { model with Busy = Some(key, "disconnect"); PendingDisconnect = None },
            Elm.ofAsync
                (async {
                    do! services.Registry.Disconnect key
                    // Worded as what it is: a local deletion.
                    return Ok "The credentials stored on this computer were removed."
                })
                (fun result -> OperationDone(key, result))
                Failed,
            None

    | OperationDone(key, result) ->
        // The secret the user typed is cleared as soon as the operation ends,
        // successfully or not, so plaintext does not linger in the model.
        let cleared =
            match model.Connectors |> List.tryFind (fun c -> c.Metadata.Key = key) with
            | Some connector ->
                connector.Metadata.Fields
                |> List.filter (fun f -> f.Secret)
                |> List.fold (fun (values: Map<string, string>) f -> values.Remove f.Key) model.FieldValues
            | None -> model.FieldValues

        { model with Busy = None; Messages = model.Messages.Add(key, result); FieldValues = cleared },
        loadCmd services,
        None

    | SetFrequency(key, minutes) ->
        model,
        Elm.ofAsync (async { return! services.Registry.SetSyncFrequency(key, minutes) }) (fun _ -> Load) Failed,
        None

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

let private bodyText (text: string) (size: float) (brush: IBrush) : IView =
    TextBlock.create
        [ TextBlock.text text
          TextBlock.fontSize size
          TextBlock.foreground brush
          TextBlock.textWrapping TextWrapping.Wrap ]
    :> IView

let private detailRow (label: string) (value: string) : IView =
    Grid.create
        [ Grid.columnDefinitions ConnectionsLogic.Geometry.detailColumns
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

/// The status chip: **glyph and text as well as colour.**
let private statusChip (tokens: Tokens) (status: ConnectionStatus) : IView =
    let hex = ConnectionsLogic.statusHex status
    let surface = (Theme.surfaces ()).Card

    let wash =
        Theme.compositeOver surface (Theme.withAlphaHex ConnectionsLogic.Geometry.chipFillAlpha hex)

    Border.create
        [ Border.background (Theme.brush (Theme.withAlphaHex ConnectionsLogic.Geometry.chipFillAlpha hex))
          Border.cornerRadius (Tokens.radius tokens 999.0)
          Border.padding (10.0, 3.0)
          Border.isHitTestVisible false
          Border.child (
              StackPanel.create
                  [ StackPanel.orientation Orientation.Horizontal
                    StackPanel.spacing 6.0
                    StackPanel.children
                        [ TextBlock.create
                              [ TextBlock.text (ConnectionsLogic.statusGlyph status)
                                TextBlock.fontSize 10.0
                                TextBlock.foreground (Theme.brush (Theme.readableOnColor wash hex))
                                TextBlock.verticalAlignment VerticalAlignment.Center
                                Ui.decorative () ]
                          TextBlock.create
                              [ TextBlock.text (ConnectionsLogic.statusLabel status)
                                TextBlock.fontSize 10.5
                                TextBlock.fontWeight FontWeight.SemiBold
                                TextBlock.foreground (Theme.brush (Theme.readableOnColor wash hex)) ] ] ]
          ) ]
    :> IView

let private noteStrip (tokens: Tokens) (hex: string) (glyph: string) (text: string) : IView =
    let wash =
        Theme.compositeOver
            (Theme.surfaces ()).Card
            (Theme.withAlphaHex ConnectionsLogic.Geometry.noteFillAlpha hex)

    Border.create
        [ Border.background (Theme.brush (Theme.withAlphaHex ConnectionsLogic.Geometry.noteFillAlpha hex))
          Border.borderThickness 1.0
          Border.borderBrush (Theme.brush (Theme.overlayEdgeHex "40" hex))
          Border.cornerRadius (Tokens.radius tokens 10.0)
          Border.padding (11.0, 8.0)
          Border.child (
              Grid.create
                  [ Grid.columnDefinitions "Auto,8,*"
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
                                TextBlock.textWrapping TextWrapping.Wrap ] ] ]
          ) ]
    :> IView

// ---------------------------------------------------------------------------
// provider cards
// ---------------------------------------------------------------------------

let private providerCard (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) (view: ConnectorView) : IView =
    let selected = model.Selected = Some view.Metadata.Key
    let status = view.State.Status
    let hex = ConnectionsLogic.statusHex status
    let accent = Theme.accentHex ()

    Border.create
        [ Border.background (
              if selected then
                  Theme.brush (Theme.withAlphaHex ConnectionsLogic.Geometry.selectedFillAlpha accent)
              else
                  Theme.brush (Ui.hexOf (Theme.surfaces ()).Card)
          )
          // Reserved in EVERY state, so selecting or failing recolours and never
          // resizes.
          Border.borderThickness 1.0
          Border.borderBrush (
              if selected then
                  Theme.brush (Theme.overlayEdgeHex ConnectionsLogic.Geometry.selectedEdgeAlpha accent)
              else
                  Theme.border ()
          )
          Border.cornerRadius (Tokens.radius tokens ConnectionsLogic.Geometry.cardRadius)
          Border.padding (20.0, 18.0)
          Border.margin (0.0, 0.0, 14.0, 14.0)
          Border.cursor Ui.hand
          Ui.accessibleName (
              sprintf "%s, %s" view.Metadata.Name (ConnectionsLogic.statusLabel status)
          )
          // **The card is operable by keyboard, not only by pointer.**
          //
          // It was a bare clickable `Border`. A Border is not focusable, so it
          // could never be tabbed to and Enter/Space could never reach it —
          // selecting a provider was a pointer-only capability. Making it
          // focusable and handling the two activation keys keeps every visual
          // exactly as it was, where converting it to a `Button` would have
          // brought Fluent's own chrome into a card that paints its own.
          Border.focusable true
          Border.onPointerPressed ((fun _ -> dispatch (Select view.Metadata.Key)), SubPatchOptions.Always)
          Border.onKeyDown (
              (fun e ->
                  // Space and Enter are the two activation keys Avalonia's own
                  // controls answer to; matching them keeps the card consistent
                  // with every real Button on the screen.
                  if e.Key = Key.Enter || e.Key = Key.Space then
                      dispatch (Select view.Metadata.Key)
                      e.Handled <- true),
              SubPatchOptions.Always
          )
          Border.child (
              StackPanel.create
                  [ StackPanel.spacing 12.0
                    StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                    StackPanel.children
                        [ DockPanel.create
                              [ DockPanel.children
                                    [ // The provider tile. Its colour is an
                                      // accent, never the card's own surface.
                                      Border.create
                                          [ DockPanel.dock Dock.Left
                                            Border.width ConnectionsLogic.Geometry.iconTile
                                            Border.height ConnectionsLogic.Geometry.iconTile
                                            Border.background (
                                                Theme.brush (
                                                    Theme.withAlphaHex ConnectionsLogic.Geometry.tileAlpha hex
                                                )
                                            )
                                            Border.cornerRadius (
                                                Tokens.radius tokens ConnectionsLogic.Geometry.iconTileRadius
                                            )
                                            Border.margin (0.0, 0.0, 12.0, 0.0)
                                            Border.verticalAlignment VerticalAlignment.Top
                                            Border.child (
                                                TextBlock.create
                                                    [ TextBlock.text view.Metadata.Icon
                                                      TextBlock.fontSize 17.0
                                                      TextBlock.horizontalAlignment HorizontalAlignment.Center
                                                      TextBlock.verticalAlignment VerticalAlignment.Center ]
                                            ) ]

                                      StackPanel.create
                                          [ StackPanel.spacing 2.0
                                            StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                                            StackPanel.children
                                                [ TextBlock.create
                                                      [ TextBlock.text view.Metadata.Name
                                                        TextBlock.fontSize 15.0
                                                        TextBlock.fontWeight FontWeight.SemiBold
                                                        TextBlock.foreground (Theme.text ())
                                                        TextBlock.textTrimming TextTrimming.CharacterEllipsis ]
                                                  TextBlock.create
                                                      [ TextBlock.text (
                                                            ConnectorCategory.label view.Metadata.Category
                                                        )
                                                        TextBlock.fontSize 11.5
                                                        TextBlock.foreground (Theme.muted ()) ] ] ] ] ]

                          WrapPanel.create
                              [ WrapPanel.children
                                    [ Border.create
                                          [ Border.margin (0.0, 0.0, 10.0, 0.0)
                                            Border.child (statusChip tokens status) ]

                                      TextBlock.create
                                          [ TextBlock.text (ConnectionsLogic.lastSyncText model.Now view)
                                            TextBlock.fontSize 11.0
                                            TextBlock.foreground (Theme.faint ())
                                            TextBlock.verticalAlignment VerticalAlignment.Center ] ] ]

                          // The stored provider message, shown verbatim.
                          match ConnectionsLogic.errorOf status with
                          | Some message -> noteStrip tokens (Theme.dangerHex ()) "⚠" message
                          | None -> ()

                          // Real facts only: what it authenticates with, and how
                          // often it imports. No item counts — none are stored.
                          WrapPanel.create
                              [ WrapPanel.children
                                    [ TextBlock.create
                                          [ TextBlock.text (ConnectionsLogic.authLabel view.Metadata)
                                            TextBlock.fontSize 10.5
                                            TextBlock.foreground (Theme.faint ())
                                            TextBlock.margin (0.0, 0.0, 12.0, 0.0) ]

                                      if view.Metadata.SupportsSync then
                                          TextBlock.create
                                              [ TextBlock.text (
                                                    ConnectionsLogic.frequencyLabel view.State.SyncFrequencyMinutes
                                                )
                                                TextBlock.fontSize 10.5
                                                TextBlock.foreground (Theme.faint ())
                                                TextBlock.margin (0.0, 0.0, 12.0, 0.0) ]
                                      else
                                          TextBlock.create
                                              [ TextBlock.text "AI provider"
                                                TextBlock.fontSize 10.5
                                                TextBlock.foreground (Theme.faint ())
                                                TextBlock.margin (0.0, 0.0, 12.0, 0.0) ] ] ] ] ]
          ) ]
    :> IView

// ---------------------------------------------------------------------------
// the detail panel
// ---------------------------------------------------------------------------

let private detailPanel (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) (view: ConnectorView) : IView =
    let key = view.Metadata.Key
    let status = view.State.Status
    let busyHere = model.Busy |> Option.map fst |> (=) (Some key)

    let region (r: ConnectionsLogic.DetailRegion) : IView list =
        match r with
        | ConnectionsLogic.DetailRegion.Identity ->
            [ DockPanel.create
                  [ DockPanel.children
                        [ Border.create
                              [ DockPanel.dock Dock.Right
                                Border.child (Ui.smallButton "Close" (fun () -> dispatch ClearSelection)) ]
                          StackPanel.create
                              [ StackPanel.spacing 2.0
                                StackPanel.children
                                    [ TextBlock.create
                                          [ TextBlock.text view.Metadata.Name
                                            TextBlock.fontSize 16.0
                                            TextBlock.fontWeight FontWeight.SemiBold
                                            TextBlock.foreground (Theme.text ())
                                            TextBlock.textWrapping TextWrapping.Wrap ]
                                      TextBlock.create
                                          [ TextBlock.text (ConnectorCategory.label view.Metadata.Category)
                                            TextBlock.fontSize 11.0
                                            TextBlock.foreground (Theme.muted ()) ] ] ] ] ]

              bodyText view.Metadata.Description 11.5 (Theme.textSecondary ()) ]

        | ConnectionsLogic.DetailRegion.Status ->
            [ sectionCaption "Status"
              statusChip tokens status
              bodyText (ConnectionsLogic.statusDescription status) 11.0 (Theme.faint ())
              detailRow "Sign-in" (ConnectionsLogic.authLabel view.Metadata)

              match ConnectionsLogic.errorOf status with
              | Some message -> noteStrip tokens (Theme.dangerHex ()) "⚠" message
              | None -> ()

              // The result of the last operation run from this screen.
              match model.Messages.TryFind key with
              | Some(Ok message) -> noteStrip tokens (Theme.successHex ()) "✓" message
              | Some(Error message) -> noteStrip tokens (Theme.dangerHex ()) "✕" message
              | None -> () ]

        | ConnectionsLogic.DetailRegion.Access ->
            [ sectionCaption "What this connection does"

              for line in ConnectionsLogic.accessLines view.Metadata do
                  bodyText ("· " + line) 11.5 (Theme.textSecondary ())

              if view.Metadata.SupportsSync then
                  bodyText ConnectionsLogic.deduplicationNotice 10.5 (Theme.faint ()) ]

        | ConnectionsLogic.DetailRegion.Configuration ->
            [ sectionCaption "Configuration"

              match ConnectionsLogic.oauthNotice view.Metadata with
              | Some notice -> noteStrip tokens (Theme.accentHex ()) "↗" notice
              | None -> ()

              for field in view.Metadata.Fields do
                  StackPanel.create
                      [ StackPanel.spacing 3.0
                        StackPanel.margin (0.0, 0.0, 0.0, 6.0)
                        StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                        StackPanel.children
                            [ TextBlock.create
                                  [ TextBlock.text field.Label
                                    TextBlock.fontSize 11.0
                                    TextBlock.fontWeight FontWeight.SemiBold
                                    TextBlock.foreground (Theme.textSecondary ()) ]

                              // A secret renders as a masked box that is ALWAYS
                              // blank. `ISecretStore` has no existence check, so
                              // the only way to show "configured" would be to
                              // read the value — which this screen will not do.
                              if field.Secret then
                                  TextBox.create
                                      [ TextBox.text (model.FieldValues.TryFind field.Key |> Option.defaultValue "")
                                        TextBox.passwordChar '•'
                                        TextBox.watermark field.Placeholder
                                        TextBox.fontSize 12.0
                                        Ui.accessibleName field.Label
                                        TextBox.onTextChanged (
                                            (fun t -> dispatch (SetField(field.Key, t))),
                                            SubPatchOptions.OnChangeOf(key, field.Key)
                                        ) ]

                                  bodyText ConnectionsLogic.secretFieldNotice 10.0 (Theme.faint ())
                              else
                                  Ui.textBox
                                      (model.FieldValues.TryFind field.Key |> Option.defaultValue "")
                                      field.Placeholder
                                      (fun t -> dispatch (SetField(field.Key, t)))

                              if field.Help.Trim().Length > 0 then
                                  bodyText field.Help 10.0 (Theme.faint ()) ] ]

              // What is genuinely saved, secrets structurally excluded.
              match ConnectionsLogic.visibleConfig view with
              | [] -> ()
              | saved ->
                  sectionCaption "Saved settings"

                  for label, value in saved do
                      detailRow label value ]

        | ConnectionsLogic.DetailRegion.Sync ->
            if not view.Metadata.SupportsSync then
                []
            else
                [ sectionCaption "Importing"
                  detailRow "Last import" (ConnectionsLogic.lastSyncText model.Now view)
                  bodyText ConnectionsLogic.syncCadenceNotice 10.5 (Theme.faint ())

                  WrapPanel.create
                      [ WrapPanel.children
                            (ConnectionsLogic.frequencyOptions
                             |> List.map (fun minutes ->
                                 let active = view.State.SyncFrequencyMinutes = minutes

                                 Button.create
                                     [ Button.content (ConnectionsLogic.frequencyLabel minutes)
                                       Button.background (
                                           if active then
                                               Theme.brush (
                                                   Theme.withAlphaHex
                                                       ConnectionsLogic.Geometry.chipFillAlpha
                                                       (Theme.accentHex ())
                                               )
                                           else
                                               Theme.transparent
                                       )
                                       Button.borderThickness 1.0
                                       Button.borderBrush (
                                           if active then Theme.brush (Theme.accentHex ()) else Theme.border ()
                                       )
                                       Button.foreground (
                                           if active then Theme.accent () else Theme.textSecondary ()
                                       )
                                       Button.cornerRadius (Tokens.radius tokens 999.0)
                                       Button.minHeight 26.0
                                       Button.padding (11.0, 4.0)
                                       Button.fontSize 11.0
                                       Button.margin (0.0, 0.0, 6.0, 6.0)
                                       Button.cursor Ui.hand
                                       Ui.accessibleName (
                                           sprintf "Import %s" ((ConnectionsLogic.frequencyLabel minutes).ToLowerInvariant())
                                       )
                                       Button.onClick (
                                           (fun _ -> dispatch (SetFrequency(key, minutes))),
                                           SubPatchOptions.Always
                                       ) ]
                                 :> IView)) ] ]

        | ConnectionsLogic.DetailRegion.Actions ->
            [ sectionCaption "Actions"

              WrapPanel.create
                  [ WrapPanel.children
                        (ConnectionsLogic.availableActions view
                         |> List.map (fun action ->
                             let blocked = ConnectionsLogic.actionBlockedReason model.Busy view action

                             let label =
                                 match blocked with
                                 | Some reason when busyHere -> reason
                                 | _ -> ConnectionsLogic.actionLabel status action

                             glassAction
                                 tokens
                                 label
                                 (action = ConnectionsLogic.Action.Configure)
                                 blocked.IsNone
                                 (match blocked with
                                  | Some reason -> reason
                                  | None -> ConnectionsLogic.actionLabel status action)
                                 (fun () ->
                                     match action with
                                     | ConnectionsLogic.Action.Configure -> dispatch (Configure key)
                                     | ConnectionsLogic.Action.Test -> dispatch (Test key)
                                     | ConnectionsLogic.Action.Sync -> dispatch (SyncNow key)
                                     | ConnectionsLogic.Action.Disconnect -> dispatch (RequestDisconnect key)))) ]

              // Any blocked action states its reason in text, not by dimming
              // alone.
              for action in ConnectionsLogic.availableActions view do
                  match ConnectionsLogic.actionBlockedReason model.Busy view action with
                  | Some reason when not busyHere ->
                      bodyText
                          (sprintf "%s — %s" (ConnectionsLogic.actionLabel status action) reason)
                          10.0
                          (Theme.faint ())
                  | _ -> ()

              if model.PendingDisconnect = Some key then
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
                                      [ bodyText
                                            (ConnectionsLogic.disconnectConfirm view.Metadata.Name)
                                            Theme.Type.rowMeta
                                            (Theme.text ())

                                        bodyText ConnectionsLogic.disconnectNotice 10.5 (Theme.faint ())

                                        WrapPanel.create
                                            [ WrapPanel.children
                                                  [ Ui.dangerButton "Remove them" (fun () ->
                                                        dispatch (ConfirmDisconnect key))
                                                    Ui.ghostButton "Cancel" (fun () -> dispatch CancelDisconnect) ] ] ] ]
                        ) ] ]

        | ConnectionsLogic.DetailRegion.Privacy ->
            [ sectionCaption "Credentials"
              bodyText ConnectionsLogic.credentialNotice 10.5 (Theme.faint ())
              bodyText ConnectionsLogic.requestNotice 10.5 (Theme.faint ()) ]

    GlassPanel.create
        [ GlassPanel.baseRadius DashboardLogic.Card.radius
          GlassPanel.elevation 1
          GlassPanel.highlightScale ConnectionsLogic.Geometry.contentHighlight
          GlassPanel.tint (Theme.surfaces ()).Card
          GlassPanel.child (
              Border.create
                  [ Border.padding (Tokens.space tokens 18.0)
                    Border.child (
                        StackPanel.create
                            [ StackPanel.spacing 14.0
                              StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                              StackPanel.children
                                  (ConnectionsLogic.detailOrder
                                   |> List.collect (fun r ->
                                       match region r with
                                       | [] -> []
                                       | children ->
                                           [ StackPanel.create
                                                 [ StackPanel.spacing 5.0
                                                   StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                                                   StackPanel.children children ]
                                             :> IView ])) ] ) ] ) ]
    :> IView

// ---------------------------------------------------------------------------
// page
// ---------------------------------------------------------------------------

let view (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let placement = ConnectionsLogic.placementFor model.Width
    let summary = ConnectionsLogic.summarise model.Connectors
    let selected = ConnectionsLogic.selectedView model.Connectors model.Selected

    let summaryCard (label: string) (value: string) : IView =
        Border.create
            [ Border.background (Theme.brush (Ui.hexOf (Theme.surfaces ()).Card))
              Border.borderThickness 1.0
              Border.borderBrush (Theme.border ())
              Border.cornerRadius (Tokens.radius tokens 16.0)
              Border.padding (18.0, 16.0)
              Border.margin (0.0, 0.0, 14.0, 14.0)
              Border.minWidth 150.0
              Border.child (
                  StackPanel.create
                      [ StackPanel.spacing 6.0
                        StackPanel.children
                            [ sectionCaption label
                              TextBlock.create
                                  [ TextBlock.text value
                                    TextBlock.fontSize 24.0
                                    TextBlock.fontWeight FontWeight.SemiBold
                                    TextBlock.foreground (Theme.text ())
                                    TextBlock.textWrapping TextWrapping.Wrap ] ] ]
              ) ]
        :> IView

    let providerGrid: IView =
        StackPanel.create
            [ StackPanel.spacing 18.0
              StackPanel.horizontalAlignment HorizontalAlignment.Stretch
              StackPanel.children
                  [ for group, views in ConnectionsLogic.grouped model.Connectors do
                        StackPanel.create
                            [ StackPanel.spacing 8.0
                              StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                              StackPanel.children
                                  [ StackPanel.create
                                        [ StackPanel.spacing 2.0
                                          StackPanel.children
                                              [ StackPanel.create
                                                    [ StackPanel.orientation Orientation.Horizontal
                                                      StackPanel.spacing 8.0
                                                      StackPanel.children
                                                          [ sectionCaption (ConnectionsLogic.Group.label group)
                                                            TextBlock.create
                                                                [ TextBlock.text (string views.Length)
                                                                  TextBlock.fontSize 10.5
                                                                  TextBlock.foreground (Theme.faint ()) ] ] ]
                                                bodyText
                                                    (ConnectionsLogic.Group.caption group)
                                                    10.5
                                                    (Theme.faint ()) ] ]

                                    Grid.create
                                        [ Grid.columnDefinitions (
                                              String.concat "," (List.replicate placement.Columns "1*")
                                          )
                                          Grid.rowDefinitions (
                                              String.concat
                                                  ","
                                                  (List.replicate
                                                      ((views.Length + placement.Columns - 1) / placement.Columns)
                                                      "Auto")
                                          )
                                          Grid.children
                                              [ for i, v in List.indexed views do
                                                    Border.create
                                                        [ Grid.column (i % placement.Columns)
                                                          Grid.row (i / placement.Columns)
                                                          Border.child (providerCard tokens model dispatch v) ] ] ] ] ] ] ]
        :> IView

    let history: IView list =
        match ConnectionsLogic.historyRows model.Connectors model.History with
        | [] -> []
        | rows ->
            [ StackPanel.create
                  [ StackPanel.spacing 6.0
                    StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                    StackPanel.children
                        [ sectionCaption ConnectionsLogic.historyHeading

                          for row in rows do
                              Grid.create
                                  [ Grid.columnDefinitions "Auto,12,*,12,Auto"
                                    Grid.margin (0.0, 2.0)
                                    Grid.children
                                        [ TextBlock.create
                                              [ Grid.column 0
                                                TextBlock.text row.Provider
                                                TextBlock.fontSize 11.0
                                                TextBlock.fontWeight FontWeight.SemiBold
                                                TextBlock.foreground (Theme.textSecondary ()) ]
                                          TextBlock.create
                                              [ Grid.column 2
                                                TextBlock.text row.Outcome
                                                TextBlock.fontSize 10.5
                                                TextBlock.foreground (Theme.faint ())
                                                TextBlock.textWrapping TextWrapping.Wrap ]
                                          TextBlock.create
                                              [ Grid.column 4
                                                TextBlock.text (Format.relativeTime model.Now row.At)
                                                TextBlock.fontSize 10.0
                                                TextBlock.foreground (Theme.faint ()) ] ] ] ] ] ]

    let body: IView =
        StackPanel.create
            [ StackPanel.spacing 22.0
              StackPanel.horizontalAlignment HorizontalAlignment.Stretch
              StackPanel.children
                  [ StackPanel.create
                        [ StackPanel.spacing 4.0
                          StackPanel.children
                              [ TextBlock.create
                                    [ TextBlock.text "Integration Hub"
                                      TextBlock.fontSize 30.0
                                      TextBlock.fontWeight FontWeight.SemiBold
                                      TextBlock.foreground (Theme.text ())
                                      TextBlock.textWrapping TextWrapping.Wrap ]
                                bodyText
                                    "Connect services to bring your academic world into University OS."
                                    14.0
                                    (Theme.muted ()) ] ]

                    match model.Error with
                    | Some message ->
                        noteStrip tokens (Theme.dangerHex ()) "⚠" ("The connectors could not be loaded: " + message)
                    | None -> ()

                    WrapPanel.create
                        [ WrapPanel.children
                              [ summaryCard "Connected" (string summary.Connected)
                                summaryCard "Needs attention" (string summary.NeedsAttention)
                                summaryCard "Available" (string summary.Available)
                                summaryCard
                                    "Last import"
                                    (match summary.LastSync with
                                     | Some at -> Format.relativeTime model.Now at
                                     | None -> ConnectionsLogic.unavailable) ] ]

                    if model.Connectors.IsEmpty && not model.Loading then
                        bodyText "No connectors are registered in this build." 13.0 (Theme.muted ())
                    else
                        providerGrid

                    yield! history ] ]
        :> IView

    let content: IView =
        match placement.Detail, selected with
        | ConnectionsLogic.DetailPlacement.Rail, Some view ->
            Grid.create
                [ Grid.columnDefinitions (sprintf "*,%f" ConnectionsLogic.Geometry.detailWidth)
                  Grid.children
                      [ Border.create [ Grid.column 0; Border.child (Ui.scrollNoChrome body) ]
                        Border.create
                            [ Grid.column 1
                              Border.margin (18.0, 0.0, 0.0, 0.0)
                              Border.child (Ui.scrollNoChrome (detailPanel tokens model dispatch view)) ] ] ]
            :> IView
        | _, Some view ->
            Ui.scrollNoChrome (
                StackPanel.create
                    [ StackPanel.spacing 18.0
                      StackPanel.horizontalAlignment HorizontalAlignment.Stretch
                      StackPanel.children [ body; detailPanel tokens model dispatch view ] ]
            )
        | _, None -> Ui.scrollNoChrome body

    Border.create
        [ Border.onSizeChanged (
              (fun args ->
                  if ConnectionsLogic.placementFor model.Width <> ConnectionsLogic.placementFor args.NewSize.Width then
                      dispatch (Resized args.NewSize.Width)),
              SubPatchOptions.Always
          )
          Border.padding (24.0, 20.0)
          Border.child content ]
    :> IView
