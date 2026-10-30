namespace UniversityOS.App

open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.FuncUI.Builder
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open UniversityOS.App.Design
open UniversityOS.App.Controls

/// **One floating window for the whole application.**
///
/// The review asks for a floating surface in six places — the Planner's full-day
/// view, assignment editing, lecture creation, document viewing, connection
/// details and expanded planner content. Six implementations would be six
/// slightly different answers to the same six questions, and the glass action
/// button has already shown what that costs: eleven copies of one control, ten
/// of them missing the one line that mattered.
///
/// So the questions are answered once, here.
[<RequireQualifiedAccess>]
module FloatingWindow =

    // -----------------------------------------------------------------------
    // Dismissal — the decisions, separated from the drawing
    // -----------------------------------------------------------------------

    /// How the user asked to close the window.
    type DismissSource =
        /// A press that landed on the scrim rather than on the window.
        | OutsideClick
        | EscapeKey
        /// The window's own ✕.
        | CloseButton
        /// An explicit Cancel inside an editor.
        | CancelButton

    [<RequireQualifiedAccess>]
    module DismissSource =
        let all = [ OutsideClick; EscapeKey; CloseButton; CancelButton ]

        let label =
            function
            | OutsideClick -> "clicking outside"
            | EscapeKey -> "Escape"
            | CloseButton -> "the close button"
            | CancelButton -> "Cancel"

    /// What the host should do about it.
    type DismissOutcome =
        /// Close now; nothing would be lost.
        | Close
        /// Ask before discarding. **Never close silently.**
        | ConfirmDiscard

    /// **The one rule, in one place.**
    ///
    /// Every route out of the window goes through this, so "click outside closes
    /// when safe" and "unsaved changes must not disappear silently" cannot
    /// disagree with each other, and a seventh caller cannot get it wrong by
    /// implementing only one of them.
    ///
    /// Deliberately independent of the source: a reader who has to remember that
    /// Escape confirms but the ✕ does not has been given two rules. Losing work
    /// to a stray Escape is the same loss as losing it to a stray click.
    let outcomeOf (unsavedChanges: bool) (_source: DismissSource) =
        if unsavedChanges then ConfirmDiscard else Close

    // -----------------------------------------------------------------------
    // Geometry
    // -----------------------------------------------------------------------

    [<RequireQualifiedAccess>]
    module Geometry =

        /// Matches the elevation-2 popover surface in the design system.
        [<Literal>]
        let radius = 18.0

        [<Literal>]
        let padding = 24.0

        [<Literal>]
        let headerSpacing = 16.0

        /// The window never fills the viewport: the scrim has to stay visibly
        /// clickable on every edge, or "click outside to close" is a gesture
        /// with nowhere to perform it.
        [<Literal>]
        let viewportFraction = 0.86

        /// Below this the fraction above leaves too little to read in, so the
        /// window takes what it can and its content scrolls instead.
        [<Literal>]
        let minWidth = 320.0

        [<Literal>]
        let minHeight = 240.0

        /// Width for a given viewport width, never less than `minWidth` and
        /// never more than `preferred`.
        let widthFor (preferred: float) (viewport: float) =
            let available = viewport * viewportFraction
            max minWidth (min preferred available)

        let heightFor (preferred: float) (viewport: float) =
            let available = viewport * viewportFraction
            max minHeight (min preferred available)

    // -----------------------------------------------------------------------
    // Rendering
    // -----------------------------------------------------------------------

    /// Focus behaviour a pure view cannot express, applied to the window's
    /// container as it is built.
    ///
    /// `TabNavigation = Cycle` is the focus trap: Tab from the last control
    /// returns to the first rather than walking out into the page behind, which
    /// a modal must never allow.
    let private trapFocus (attrs: IAttr<Border> list) : IAttr<Border> list =
        attrs
        @ [ AttrBuilder<Border>
                .CreateProperty<KeyboardNavigationMode>(
                    KeyboardNavigation.TabNavigationProperty,
                    KeyboardNavigationMode.Cycle,
                    ValueNone
                ) ]

    /// The scrim's own fill: the theme's overlay surface, held back so the page
    /// behind stays legible as context rather than being blanked out.
    let scrimAlphaHex = "9E"

    /// **The window.**
    ///
    /// `title` is both the visible heading and the accessible name, so assistive
    /// technology and the screen agree about what this is.
    ///
    /// `onDismiss` receives the `DismissSource`; the host decides using
    /// `outcomeOf`. This component never closes anything itself — a view that
    /// silently discarded an editor's contents is exactly the failure the rule
    /// above exists to prevent.
    let create
        (tokens: Tokens)
        (title: string)
        (width: float)
        (height: float)
        (onDismiss: DismissSource -> unit)
        (content: IView)
        : IView =

        let radius = Tokens.radius tokens Geometry.radius

        Panel.create
            [ Panel.horizontalAlignment HorizontalAlignment.Stretch
              Panel.verticalAlignment VerticalAlignment.Stretch
              Panel.children
                  [
                    // **The scrim is a SIBLING of the window, not its parent.**
                    //
                    // With the window nested inside it, every click on the window
                    // would bubble to this handler and the host would have to
                    // reason about the event's source to tell a real outside
                    // click from a press on a control inside — the same trap the
                    // Timeline row fell into twice. As siblings, a press on the
                    // window never reaches the scrim at all, so there is no
                    // ordering question to get wrong.
                    Border.create
                        [ Border.background (
                              Theme.brush (Theme.withAlphaHex scrimAlphaHex (Ui.hexOf (Theme.surfaces ()).Overlay))
                          )
                          Border.horizontalAlignment HorizontalAlignment.Stretch
                          Border.verticalAlignment VerticalAlignment.Stretch
                          // Deliberately unnamed. The backdrop is not a control a
                          // screen-reader user navigates to — the dialog carries
                          // the name and the ✕ carries the action. Announcing the
                          // scrim as well would be one more thing to walk past.
                          Border.onPointerPressed (
                              (fun args ->
                                  onDismiss OutsideClick
                                  args.Handled <- true),
                              SubPatchOptions.Always
                          ) ]

                    Border.create (
                        trapFocus
                            [ Border.horizontalAlignment HorizontalAlignment.Center
                              Border.verticalAlignment VerticalAlignment.Center
                              Border.width width
                              Border.height height
                              // Focusable so the trap has somewhere to start and
                              // so Escape is heard wherever focus lands inside.
                              Border.focusable true
                              Ui.accessibleName title
                              Border.onKeyDown (
                                  (fun args ->
                                      if args.Key = Key.Escape then
                                          onDismiss EscapeKey
                                          args.Handled <- true),
                                  SubPatchOptions.Always
                              )
                              Border.child (
                                  GlassPanel.create
                                      [ GlassPanel.baseRadius Geometry.radius
                                        // Elevation 2 is the design system's
                                        // popover surface — this floats above a
                                        // page that is itself glass, so it needs
                                        // to read as a further layer, not a card.
                                        GlassPanel.elevation 2
                                        GlassPanel.tint (Theme.surfaces ()).Overlay
                                        GlassPanel.child (
                                            Border.create
                                                [ Border.padding (Tokens.space tokens Geometry.padding)
                                                  Border.cornerRadius radius
                                                  Border.child (
                                                      DockPanel.create
                                                          [ DockPanel.children
                                                                [ DockPanel.create
                                                                      [ DockPanel.dock Dock.Top
                                                                        DockPanel.margin (
                                                                            0.0,
                                                                            0.0,
                                                                            0.0,
                                                                            Tokens.space tokens Geometry.headerSpacing
                                                                        )
                                                                        DockPanel.children
                                                                            [ Border.create
                                                                                  [ DockPanel.dock Dock.Right
                                                                                    Border.child (
                                                                                        Ui.namedSmallButton
                                                                                            (Typography.asText "✕")
                                                                                            (sprintf "Close %s" title)
                                                                                            (fun () ->
                                                                                                onDismiss CloseButton)
                                                                                    ) ]
                                                                              TextBlock.create
                                                                                  [ TextBlock.text title
                                                                                    TextBlock.fontSize Theme.fontLg
                                                                                    TextBlock.fontWeight
                                                                                        FontWeight.SemiBold
                                                                                    TextBlock.foreground (Theme.text ())
                                                                                    TextBlock.verticalAlignment
                                                                                        VerticalAlignment.Center
                                                                                    TextBlock.textTrimming
                                                                                        TextTrimming.CharacterEllipsis ] ] ]

                                                                  // The content scrolls, so a long
                                                                  // editor or a full day of sessions
                                                                  // cannot push its own actions off
                                                                  // the bottom of the window.
                                                                  Ui.scroll content ] ]
                                                  ) ] ) ] ) ] ) ] ]
        :> IView
