module UniversityOS.App.Features.DesignPreview

open Elmish
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Rendering
open UniversityOS.App.Controls

/// Developer surface for the design system.
///
/// It drives the REAL production systems — the same AppearanceSettings record
/// the app persists, the same TokenStore every control reads, the same
/// BackgroundHost the shell mounts and the same GlassPanel screens will use.
/// Nothing here is a mock-up that gets thrown away later; changing a control on
/// this page changes the whole application.

/// Identifies a live instance, so the diagnostics refresh loop from a previous
/// visit stops rather than accumulating a timer per navigation.
let mutable private liveInstance = 0

type Model =
    { Appearance: AppearanceSettings
      /// Developer tier override (None = automatically detected).
      TierOverride: RenderTier option
      QualityOverride: QualityLevel option
      /// Snapshot of the diagnostics counters, refreshed on a timer.
      Stats: string list
      /// Developer inspection: hide the panels to see the whole background.
      ChromeVisible: bool
      /// Developer inspection: hold the current animation frame.
      Frozen: bool
      InstanceId: int }

type Msg =
    | SetAccent of string
    | SetTheme of ThemePreference
    | SetGlass of int
    | SetRadius of int
    | SetAnimationSpeed of Speed
    | SetDensity of Density
    | SetReadingWidth of ReadingWidth
    | SetBackgroundStyle of BackgroundStyle
    | SetBackgroundSpeed of Speed
    | SetOpacity of int
    | SetBlur of int
    | SetIntensity of int
    | ToggleParallax
    | ToggleReduceMotion
    | SetTier of RenderTier option
    | SetQuality of QualityLevel option
    | ToggleChrome
    | ToggleFrozen
    | Tick of int

/// Appearance changes bubble to the shell, which persists them and republishes
/// the token set — exactly how the Settings screen will behave.
type External = AppearanceChanged of AppearanceSettings

/// Both this panel and the console logger read the same definition.
let private collectStats () = RenderReport.lines ()

let init (services: AppServices) : Model * Cmd<Msg> =
    liveInstance <- liveInstance + 1
    let instance = liveInstance

    // Diagnostics are NOT enabled here. Shell.init constructs every feature at
    // startup, so doing it here would leave a normal launch permanently
    // instrumented. The shell switches them on when this page is navigated to.
    { Appearance = AppearanceSettings.defaults
      TierOverride = Capabilities.forcedTier ()
      QualityOverride = QualityController.forcedLevel ()
      Stats = collectStats ()
      ChromeVisible = true
      Frozen = false
      InstanceId = instance },
    Elm.delayed 400 (Tick instance)

let update (services: AppServices) (msg: Msg) (model: Model) : Model * Cmd<Msg> * External option =
    let changed (appearance: AppearanceSettings) =
        { model with Appearance = appearance }, Cmd.none, Some(AppearanceChanged appearance)

    match msg with
    | SetAccent key -> changed { model.Appearance with AccentKey = key }
    | SetTheme theme -> changed { model.Appearance with Theme = theme }
    | SetGlass value -> changed { model.Appearance with GlassIntensity = value }
    | SetRadius value -> changed { model.Appearance with CornerRadius = value }
    | SetAnimationSpeed speed -> changed { model.Appearance with AnimationSpeed = speed }
    | SetDensity density -> changed { model.Appearance with Density = density }
    | SetReadingWidth width -> changed { model.Appearance with ReadingWidth = width }
    | SetBackgroundStyle style -> changed { model.Appearance with BackgroundStyle = style }
    | SetBackgroundSpeed speed -> changed { model.Appearance with BackgroundSpeed = speed }
    | SetOpacity value -> changed { model.Appearance with BackgroundOpacity = value }
    | SetBlur value -> changed { model.Appearance with BackgroundBlur = value }
    | SetIntensity value -> changed { model.Appearance with BackgroundIntensity = value }
    | ToggleParallax -> changed { model.Appearance with BackgroundParallax = not model.Appearance.BackgroundParallax }
    | ToggleReduceMotion -> changed { model.Appearance with ReduceMotion = not model.Appearance.ReduceMotion }

    | SetTier tier ->
        Capabilities.force tier
        { model with TierOverride = tier }, Cmd.none, None

    | SetQuality quality ->
        QualityController.force quality
        { model with QualityOverride = quality }, Cmd.none, None

    | ToggleChrome -> { model with ChromeVisible = not model.ChromeVisible }, Cmd.none, None
    | ToggleFrozen -> { model with Frozen = not model.Frozen }, Cmd.none, None

    | Tick instance ->
        // Only the current instance keeps ticking; a stale loop from a previous
        // visit ends here rather than running for the life of the process.
        if instance <> liveInstance then
            model, Cmd.none, None
        else
            { model with Stats = collectStats () }, Elm.delayed 400 (Tick instance), None

// ---------------------------------------------------------------------------
// view
// ---------------------------------------------------------------------------

let private optionRow (label: string) (options: (string * bool * (unit -> unit)) list) : IView =
    StackPanel.create
        [ StackPanel.orientation Orientation.Horizontal
          StackPanel.spacing 6.0
          StackPanel.children
              [ yield
                    TextBlock.create
                        [ TextBlock.text label
                          TextBlock.width 108.0
                          TextBlock.fontSize Theme.fontSm
                          TextBlock.foreground (Theme.muted ())
                          TextBlock.verticalAlignment VerticalAlignment.Center ]
                for (text, selected, onClick) in options do
                    yield
                        if selected then
                            Ui.accentSmallButton text onClick
                        else
                            Ui.smallButton text onClick ] ]
    :> IView

/// A slider bound to an int setting. The guard matters: without it the
/// value-changed handler fires on every re-render and dispatches in a loop.
let private slider (label: string) (value: int) (onChange: int -> unit) : IView =
    StackPanel.create
        [ StackPanel.orientation Orientation.Horizontal
          StackPanel.spacing 6.0
          StackPanel.children
              [ TextBlock.create
                    [ TextBlock.text label
                      TextBlock.width 108.0
                      TextBlock.fontSize Theme.fontSm
                      TextBlock.foreground (Theme.muted ())
                      TextBlock.verticalAlignment VerticalAlignment.Center ]
                Slider.create
                    [ Slider.minimum 0.0
                      Slider.maximum 100.0
                      Slider.width 190.0
                      Slider.value (float value)
                      Slider.onValueChanged (
                          (fun v ->
                              let rounded = int (round v)
                              if rounded <> value then onChange rounded),
                          SubPatchOptions.Always
                      ) ]
                TextBlock.create
                    [ TextBlock.text (string value)
                      TextBlock.width 34.0
                      TextBlock.fontSize Theme.fontSm
                      TextBlock.foreground (Theme.text ())
                      TextBlock.verticalAlignment VerticalAlignment.Center ] ] ]
    :> IView

let private swatches (model: Model) (dispatch: Msg -> unit) : IView =
    StackPanel.create
        [ StackPanel.orientation Orientation.Horizontal
          StackPanel.spacing 6.0
          StackPanel.children
              [ yield
                    TextBlock.create
                        [ TextBlock.text "Accent"
                          TextBlock.width 108.0
                          TextBlock.fontSize Theme.fontSm
                          TextBlock.foreground (Theme.muted ())
                          TextBlock.verticalAlignment VerticalAlignment.Center ]
                for (key, hex) in Palette.accents do
                    let isSelected = key = model.Appearance.AccentKey

                    yield
                        Border.create
                            [ Border.width 26.0
                              Border.height 26.0
                              Border.cornerRadius 13.0
                              Border.background (SolidColorBrush(Color.Parse hex) :> IBrush)
                              Border.borderThickness (if isSelected then 2.0 else 0.0)
                              Border.borderBrush (Theme.text ())
                              Border.cursor Ui.hand
                              Border.onPointerPressed ((fun _ -> dispatch (SetAccent key)), SubPatchOptions.Always) ]
                        :> IView ] ]
    :> IView

/// Sample glass surfaces over the live background. These use the production
/// GlassPanel, so what is on screen here is what screens will get.
let private glassSamples (tokens: Tokens) : IView =
    let sample (title: string) (bodyText: string) (elevation: int) (radius: float) : IView =
        GlassPanel.create
            [ GlassPanel.baseRadius radius
              GlassPanel.elevation elevation
              GlassPanel.width 250.0
              GlassPanel.height 132.0
              GlassPanel.child (
                  StackPanel.create
                      [ StackPanel.margin 16.0
                        StackPanel.spacing 8.0
                        StackPanel.children
                            [ TextBlock.create
                                  [ TextBlock.text title
                                    TextBlock.fontSize Theme.fontMd
                                    TextBlock.fontWeight FontWeight.SemiBold
                                    TextBlock.foreground (Theme.text ()) ]
                              TextBlock.create
                                  [ TextBlock.text bodyText
                                    TextBlock.fontSize Theme.fontSm
                                    TextBlock.textWrapping TextWrapping.Wrap
                                    TextBlock.foreground (Theme.muted ()) ]
                              Ui.progressBar 64 (Theme.accentHex ()) ] ]
              ) ]
        :> IView

    WrapPanel.create
        [ WrapPanel.orientation Orientation.Horizontal
          WrapPanel.children
              [ sample "Glass · elevation 0" "Flat surface, no shadow. Radius follows the corner-radius token." 0 (Tokens.radius tokens 14.0)
                sample "Glass · elevation 1" "The default card surface used across the application." 1 (Tokens.radius tokens 14.0)
                sample "Glass · elevation 2" "Raised surface for popovers and overlays." 2 (Tokens.radius tokens 18.0)
                sample "Glass · pill radius" "Same material, much larger corner radius." 1 (Tokens.radius tokens 28.0) ] ]
    :> IView

let view (tokens: Tokens) (model: Model) (dispatch: Msg -> unit) : IView =
    let a = model.Appearance

    // The OS preference is honoured whatever the app setting says, so the panel
    // has to be able to explain that rather than showing a toggle that looks
    // stuck.
    let osReduceMotion = SystemPreferences.prefersReducedMotion ()

    let tierOptions =
        [ "Auto", model.TierOverride.IsNone, (fun () -> dispatch (SetTier None))
          "A shader", model.TierOverride = Some RenderTier.A, (fun () -> dispatch (SetTier(Some RenderTier.A)))
          "B skia", model.TierOverride = Some RenderTier.B, (fun () -> dispatch (SetTier(Some RenderTier.B)))
          "C avalonia", model.TierOverride = Some RenderTier.C, (fun () -> dispatch (SetTier(Some RenderTier.C))) ]

    let qualityOptions =
        ("Auto", model.QualityOverride.IsNone, (fun () -> dispatch (SetQuality None)))
        :: (QualityLevel.all
            |> List.map (fun level ->
                QualityLevel.label level, model.QualityOverride = Some level, (fun () -> dispatch (SetQuality(Some level)))))

    let controls =
        Ui.card
            [ Ui.sectionTitle "Appearance"
              swatches model dispatch
              optionRow
                  "Theme"
                  (ThemePreference.all
                   |> List.map (fun t -> ThemePreference.label t, a.Theme = t, (fun () -> dispatch (SetTheme t))))
              slider "Glass" a.GlassIntensity (SetGlass >> dispatch)
              slider "Corner radius" a.CornerRadius (fun v -> dispatch (SetRadius(min 28 v)))
              optionRow
                  "Animation"
                  (Speed.all
                   |> List.map (fun s -> Speed.label s, a.AnimationSpeed = s, (fun () -> dispatch (SetAnimationSpeed s))))
              optionRow
                  "Density"
                  (Density.all
                   |> List.map (fun d -> Density.label d, a.Density = d, (fun () -> dispatch (SetDensity d))))
              optionRow
                  "Reading width"
                  (ReadingWidth.all
                   |> List.map (fun w -> ReadingWidth.label w, a.ReadingWidth = w, (fun () -> dispatch (SetReadingWidth w))))
              Ui.divider ()
              Ui.sectionTitle "Background"
              optionRow
                  "Mode"
                  (BackgroundStyle.all
                   |> List.map (fun s ->
                       BackgroundStyle.label s, a.BackgroundStyle = s, (fun () -> dispatch (SetBackgroundStyle s))))
              optionRow
                  "Speed"
                  (Speed.all
                   |> List.map (fun s -> Speed.label s, a.BackgroundSpeed = s, (fun () -> dispatch (SetBackgroundSpeed s))))
              slider "Opacity" a.BackgroundOpacity (SetOpacity >> dispatch)
              slider "Blur" a.BackgroundBlur (SetBlur >> dispatch)
              slider "Intensity" a.BackgroundIntensity (SetIntensity >> dispatch)
              optionRow
                  "Parallax"
                  [ "On", a.BackgroundParallax, (fun () -> dispatch ToggleParallax)
                    "Off", not a.BackgroundParallax, (fun () -> dispatch ToggleParallax) ]

              // Reduce Motion is deliberately NOT in the Speed row above. It is
              // not a fourth speed — Slow still moves continuously, this stops
              // movement — and presenting it as a peer taught exactly the wrong
              // model. It gets its own accessibility section.
              Ui.divider ()
              Ui.sectionTitle "Accessibility"
              optionRow
                  "Reduce motion"
                  [ "On", a.ReduceMotion, (fun () -> dispatch ToggleReduceMotion)
                    "Off", not a.ReduceMotion, (fun () -> dispatch ToggleReduceMotion) ]
              Ui.subtle (
                  if osReduceMotion then
                      "Windows is asking for reduced motion, so backgrounds are frozen and parallax is off regardless of this setting. Your speed choice is remembered."
                  else
                      "Freezes animated backgrounds and turns off pointer parallax. Your Slow / Normal / Fast choice is kept and returns when you switch this off. Windows' own reduced-motion setting is followed automatically."
              )

              Ui.divider ()
              Ui.sectionTitle "Rendering (developer)"
              optionRow "Tier" tierOptions
              optionRow "Quality" qualityOptions ]

    let diagnostics =
        Ui.card
            [ Ui.sectionTitle "Diagnostics"
              TextBlock.create
                  [ TextBlock.text (String.concat "\n" model.Stats)
                    TextBlock.fontFamily Typography.mono
                    TextBlock.fontSize 11.5
                    TextBlock.foreground (Theme.muted ()) ] ]

    // Always-visible inspection row. Developer-only: it lives on this page and
    // affects nothing in production screens.
    let inspection =
        StackPanel.create
            [ StackPanel.orientation Orientation.Horizontal
              StackPanel.spacing 8.0
              StackPanel.children
                  [ (if model.ChromeVisible then
                         Ui.smallButton "▤ Hide panels" (fun () -> dispatch ToggleChrome)
                     else
                         Ui.accentSmallButton "▤ Show panels" (fun () -> dispatch ToggleChrome))
                    (if model.Frozen then
                         Ui.accentSmallButton "❚❚ Frame frozen" (fun () -> dispatch ToggleFrozen)
                     else
                         Ui.smallButton "❚❚ Freeze frame" (fun () -> dispatch ToggleFrozen)) ] ]
        :> IView

    Ui.scroll (
        StackPanel.create
            [ StackPanel.spacing (Tokens.space tokens 16.0)
              StackPanel.maxWidth tokens.ReadingWidth
              StackPanel.horizontalAlignment HorizontalAlignment.Left
              StackPanel.children
                  [ if model.ChromeVisible then
                        Ui.pageHeader
                            "Design Preview"
                            "Every background mode, glass over the live background, and the tokens driving both."

                    inspection

                    if model.ChromeVisible then
                        glassSamples tokens

                        Grid.create
                            [ Grid.columnDefinitions "*,16,320"
                              Grid.children
                                  [ Grid.create [ Grid.column 0; Grid.children [ controls ] ]
                                    Grid.create [ Grid.column 2; Grid.children [ diagnostics ] ] ] ] ] ]
    )
