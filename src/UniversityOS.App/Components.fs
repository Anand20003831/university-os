namespace UniversityOS.App

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Layout
open Avalonia.Media
open Avalonia.FuncUI
open Avalonia.FuncUI.Builder
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.VisualTree
open UniversityOS.App.Design
open UniversityOS.App.Controls

/// The reusable UI toolkit. Every interactive helper subscribes events with
/// SubPatchOptions.Always so closures over list-item data never go stale when
/// FuncUI reuses virtual nodes.
[<RequireQualifiedAccess>]
module Ui =

    // ---- pointer routing ----------------------------------------------------

    /// Does this control own a click of its own?
    ///
    /// `Button` covers `ToggleButton`, `CheckBox` and `RadioButton`, which all
    /// derive from it — listed anyway so the intent is readable rather than
    /// inherited by accident.
    let private ownsItsClick (visual: Visual) =
        match visual with
        | :? Button
        | :? Primitives.ToggleButton
        | :? CheckBox
        | :? RadioButton
        | :? TextBox
        | :? ComboBox
        | :? Slider
        | :? Primitives.ScrollBar
        | :? MenuItem -> true
        | _ -> false

    /// Whether a pointer event started inside a control that has its own
    /// action, walking up the visual tree from the event's source.
    ///
    /// A page-level "click the background to dismiss" handler needs this
    /// because the source of a press is usually a leaf — the `TextBlock` inside
    /// a Button, or the Border inside a TextBox's template — not the control
    /// the user thinks they clicked. Testing the source's own type alone would
    /// treat every button press as a background click.
    ///
    /// Deliberately a walk, never a coordinate or distance test: "is this click
    /// inside a control" is a tree question, and hit rectangles drift the moment
    /// padding or a transform changes.
    ///
    /// Both trees are walked, because Avalonia puts the two kinds of child in
    /// different places: a Button's *content* is a logical child, while the
    /// parts a TextBox builds from its control template are visual children with
    /// no logical parent. Following only one would miss half the controls on the
    /// page — and the visual tree is not built at all until a control has been
    /// templated, which is why the logical fallback also makes this testable
    /// without a running window.
    let rec private walkForOwner (visual: Visual) =
        if ownsItsClick visual then
            true
        else
            let next =
                match visual.GetVisualParent() with
                | null ->
                    match (visual :> Avalonia.LogicalTree.ILogical).LogicalParent with
                    | :? Visual as logical -> Some logical
                    | _ -> None
                | parent -> Some parent

            match next with
            | Some parent -> walkForOwner parent
            | None -> false

    let isInteractiveSource (source: obj) =
        match source with
        | :? Visual as visual -> walkForOwner visual
        | _ -> false

    /// One shared hand cursor instance for every clickable element.
    ///
    /// The cursor factory is a **platform service**, so this needs a live
    /// Avalonia platform behind it. View trees are also built by headless tests,
    /// where no platform exists — and a missing cursor must not be the reason a
    /// screen's composition cannot be asserted. So the construction degrades to
    /// `null`, which Avalonia reads as "inherit the parent's cursor". In the
    /// running application the platform is always up before the first view is
    /// built, so the fallback never applies there.
    let hand: Cursor =
        try
            new Cursor(StandardCursorType.Hand)
        with _ ->
            null

    /// Marks a Button whose visible surface is a `GlassPanel` in its content
    /// rather than the Fluent template's own fill.
    ///
    /// Fluent paints a Button's background on `PART_ContentPresenter` inside the
    /// control template, and its `:pointerover`, `:pressed` and `:disabled`
    /// styles set that background **directly on the presenter** — which beats
    /// the Button's own `Background` property, local value or not. A button that
    /// asked for a transparent fill therefore still renders Fluent's cool grey
    /// block the moment it is disabled or hovered, right through the glass.
    ///
    /// `App.Initialize` neutralises that presenter fill for this class only, so
    /// every other Button in the application keeps Fluent's behaviour untouched.
    /// A function, not a `let` value, so a headless test can read it without
    /// forcing this module's static constructor (which builds a `Cursor`).
    let glassActionClass () = "glass-action"

    /// The control states whose Fluent presenter fill is neutralised for that
    /// class. Exactly one: `:disabled`.
    ///
    /// **An unconditional entry here would blanket every state.** A selector
    /// carrying no pseudo-class matches whenever the control has the class —
    /// hovered, pressed and disabled included — and because these styles are
    /// registered after the Fluent theme, it outranks Fluent's own
    /// `:pointerover` fill. An earlier version listed `""` alongside
    /// `:disabled`, which is why these buttons had no hover at all: removing
    /// `:pointerover` from the list did nothing, because `""` was already
    /// covering it.
    ///
    /// The resting state needs no entry regardless. Fluent's template binds the
    /// presenter's background to the Button's own `Background`, which these
    /// buttons already set to transparent — so the glass is the surface without
    /// any style having to say so.
    ///
    /// `:disabled` is the one state that genuinely needs overriding, because
    /// `ButtonBackgroundDisabled` is the single Fluent state resource
    /// `FluentMaterial` does not remap, so it paints its own cool grey.
    let glassActionNeutralisedStates () = [ ":disabled" ]

    /// A translucent sheet sitting inside a glass control, carrying its hover
    /// and pressed feedback.
    ///
    /// **Why the glass cannot use opacity for this.** A `GlassPanel` paints
    /// through a Skia custom draw operation, and a visual's `Opacity` does not
    /// reach one — measured directly: at 0.35 the button's label all but
    /// disappears while the glass fill is unchanged. So the application-wide
    /// `Button:pointerover → 0.85` convention does nothing to any glass surface;
    /// it only fades the text sitting on it.
    ///
    /// The one channel that did work was the Fluent presenter fill showing
    /// through the glass's 36% transparency — but that is a small step behind a
    /// saturated accent, so the primary action still read as dead.
    ///
    /// This veil is an ordinary `Border`, so styles and transitions drive it
    /// normally. It is tinted with the theme's own ink, which lifts a dark
    /// surface and deepens a light one without needing a second colour.
    let glassVeilClass () = "glass-veil"

    /// A text input that draws **no chrome of its own**, for use inside a surface
    /// that already looks like a field.
    ///
    /// Setting `TextBox.background` to transparent is not enough, and the reason
    /// is worth stating because it is invisible until the control is focused.
    /// Fluent's TextBox theme styles the template part directly —
    /// `^:focus /template/ Border#PART_BorderElement` — rather than the TextBox,
    /// so it wins over any value set on the control itself. This application then
    /// maps `TextControlBackgroundFocused` to the **opaque** `Selected` surface
    /// (`FluentMaterial.resolve`), so focusing such a box paints a filled
    /// rectangle inside whatever custom field surrounds it.
    ///
    /// That is exactly the "smaller textbox inside the larger search box" from
    /// the first runtime review. This class neutralises the part in every state,
    /// and only for controls that opt in — every ordinary input keeps its chrome.
    let bareInputClass () = "bare-input"

    /// "#RRGGBB" for a palette colour, so accent-derived brushes can go through
    /// the same hex helpers as the theme's own literals.
    let hexOf (c: Color) = sprintf "#%02X%02X%02X" c.R c.G c.B

    /// Linear blend between two colours. `amount` 0 keeps `a`, 1 keeps `b`.
    ///
    /// One implementation, in the design layer, so the material palette and the
    /// views cannot drift apart on what "blend" means. Written out as a function
    /// rather than `let mix = Palette.mix`: a value binding compiles to a
    /// property, and reading it forces this module's static constructor, which
    /// builds a `Cursor` and needs a running Avalonia platform — enough to fail
    /// every headless test that only wanted to blend two colours.
    let mix (a: Color) (b: Color) (amount: float) =
        UniversityOS.App.Design.Palette.mix a b amount

    // ---- accessibility ------------------------------------------------------
    //
    // FuncUI 1.5.1 ships no AutomationProperties DSL — there is no
    // `Button.automationName`. But the automation properties are ordinary
    // Avalonia attached properties, so they can be set through the generic
    // attribute builder. That is the strongest supported native option: real
    // automation metadata, no invented API, no reflection.

    /// The name assistive technology reads for a control. Needed wherever a
    /// control's content is a glyph or a panel rather than plain text, because
    /// Avalonia can only infer a name from textual content.
    let accessibleName<'t when 't :> Control> (name: string) : IAttr<'t> =
        AttrBuilder<'t>
            .CreateProperty<string>(Avalonia.Automation.AutomationProperties.NameProperty, name, ValueNone)

    /// Hides a purely decorative element from the accessibility tree, so a
    /// screen reader is not read a list of coloured dots.
    let decorative<'t when 't :> Control> () : IAttr<'t> =
        AttrBuilder<'t>
            .CreateProperty<Avalonia.Automation.AccessibilityView>(
                Avalonia.Automation.AutomationProperties.AccessibilityViewProperty,
                Avalonia.Automation.AccessibilityView.Raw,
                ValueNone
            )

    // ---- text ---------------------------------------------------------------

    let title (t: string) : IView =
        TextBlock.create
            [ TextBlock.text t
              TextBlock.fontSize Theme.fontTitle
              TextBlock.fontWeight FontWeight.Bold
              TextBlock.foreground (Theme.text ()) ]

    let pageHeader (t: string) (sub: string) : IView =
        StackPanel.create
            [ StackPanel.spacing 4.0
              StackPanel.children
                  [ TextBlock.create
                        [ TextBlock.text t
                          TextBlock.fontSize Theme.fontXl
                          TextBlock.fontWeight FontWeight.Bold
                          TextBlock.foreground (Theme.text ()) ]
                    TextBlock.create
                        [ TextBlock.text sub
                          TextBlock.fontSize Theme.fontSm
                          TextBlock.foreground (Theme.muted ())
                          TextBlock.textWrapping TextWrapping.Wrap ] ] ]

    let sectionTitle (t: string) : IView =
        TextBlock.create
            [ TextBlock.text (t.ToUpperInvariant())
              TextBlock.fontSize Theme.fontXs
              TextBlock.fontWeight FontWeight.SemiBold
              TextBlock.foreground (Theme.label ()) ]

    let body (t: string) : IView =
        TextBlock.create
            [ TextBlock.text t
              TextBlock.fontSize Theme.fontBase
              TextBlock.foreground (Theme.text ())
              TextBlock.textWrapping TextWrapping.Wrap ]

    let subtle (t: string) : IView =
        TextBlock.create
            [ TextBlock.text t
              TextBlock.fontSize Theme.fontSm
              TextBlock.foreground (Theme.muted ())
              TextBlock.textWrapping TextWrapping.Wrap ]

    let faintText (t: string) : IView =
        TextBlock.create
            [ TextBlock.text t
              TextBlock.fontSize Theme.fontXs
              TextBlock.foreground (Theme.faint ()) ]

    // ---- containers ---------------------------------------------------------

    /// Gentle elevation shared by every card-like surface.
    let cardShadow = BoxShadow.Parse "0 2 10 0 #33000000"

    let card (children: IView list) : IView =
        Border.create
            [ Border.background (Theme.surface ())
              Border.cornerRadius 12.0
              Border.borderThickness 1.0
              Border.borderBrush (Theme.border ())
              Border.padding 16.0
              Border.boxShadow cardShadow
              Border.child (
                  StackPanel.create [ StackPanel.spacing 10.0; StackPanel.children children ]
              ) ]

    let cardTight (children: IView list) : IView =
        Border.create
            [ Border.background (Theme.surface ())
              Border.cornerRadius 10.0
              Border.borderThickness 1.0
              Border.borderBrush (Theme.border ())
              Border.padding 12.0
              Border.boxShadow cardShadow
              Border.child (StackPanel.create [ StackPanel.spacing 6.0; StackPanel.children children ]) ]

    let divider () : IView =
        Border.create [ Border.height 1.0; Border.background (Theme.divider ()); Border.margin (0.0, 4.0) ]

    let scroll (content: IView) : IView =
        ScrollViewer.create
            [ ScrollViewer.horizontalScrollBarVisibility Primitives.ScrollBarVisibility.Disabled
              ScrollViewer.content content ]

    /// Scrolls normally but draws no scrollbar.
    ///
    /// `Hidden` is not `Disabled`: the viewer still scrolls by wheel, trackpad,
    /// keyboard, focus navigation and touch — only the chrome is suppressed, and
    /// because the bar is an overlay in Avalonia's Fluent theme there is no
    /// gutter to reclaim, so content does not shift when the page grows past
    /// one viewport.
    /// Hidden — deliberately NOT Disabled. The two look identical in a
    /// screenshot and are opposites in behaviour: Disabled stops the wheel
    /// working. Named so the distinction is assertable.
    let pageScrollBarVisibility = Primitives.ScrollBarVisibility.Hidden

    let scrollNoChrome (content: IView) : IView =
        ScrollViewer.create
            [ ScrollViewer.horizontalScrollBarVisibility Primitives.ScrollBarVisibility.Disabled
              ScrollViewer.verticalScrollBarVisibility pageScrollBarVisibility
              ScrollViewer.content content ]

    let emptyState (icon: string) (heading: string) (sub: string) : IView =
        StackPanel.create
            [ StackPanel.spacing 8.0
              StackPanel.horizontalAlignment HorizontalAlignment.Center
              StackPanel.verticalAlignment VerticalAlignment.Center
              StackPanel.margin (0.0, 40.0)
              StackPanel.children
                  [ TextBlock.create
                        [ TextBlock.text icon
                          TextBlock.fontSize 34.0
                          TextBlock.horizontalAlignment HorizontalAlignment.Center ]
                    TextBlock.create
                        [ TextBlock.text heading
                          TextBlock.fontSize Theme.fontMd
                          TextBlock.fontWeight FontWeight.SemiBold
                          TextBlock.foreground (Theme.text ())
                          TextBlock.horizontalAlignment HorizontalAlignment.Center ]
                    TextBlock.create
                        [ TextBlock.text sub
                          TextBlock.fontSize Theme.fontSm
                          TextBlock.foreground (Theme.muted ())
                          TextBlock.textAlignment TextAlignment.Center
                          TextBlock.textWrapping TextWrapping.Wrap
                          TextBlock.maxWidth 360.0 ] ] ]

    // ---- badges & chips -----------------------------------------------------

    /// A small labelled badge in an identity or accent colour.
    ///
    /// The FILL keeps the raw colour at low alpha — a wash reads as the colour
    /// whatever the theme. The TEXT goes through `Theme.readableHex`, because a
    /// mid-tone colour that glows on charcoal measures under 3:1 on parchment,
    /// and this pill carries words: module codes, session status, SELECTED.
    /// In the dark theme that call is a no-op, so dark output is unchanged.
    let pill (label: string) (hex: string) : IView =
        Border.create
            [ Border.background (Theme.brush (Theme.withAlphaHex "22" hex))
              Border.cornerRadius 999.0
              Border.padding (8.0, 2.0)
              // **A badge is the size of its label.**
              //
              // `HorizontalAlignment` defaults to Stretch, so this Border filled
              // whatever cell it was dropped into. Inside a horizontal StackPanel
              // it got its desired width and looked right; in a Grid cell or a
              // vertical StackPanel the background spanned the whole column —
              // which is the "background does not hug the text" from the first
              // runtime review. Nothing ever asked it to stretch; it simply never
              // said not to.
              //
              // A caller that wants the badge on the right of a title row aligns
              // its *container*, which still works because the badge now hugs.
              Border.horizontalAlignment HorizontalAlignment.Left
              Border.verticalAlignment VerticalAlignment.Center
              Border.child (
                  TextBlock.create
                      [ TextBlock.text label
                        TextBlock.fontSize Theme.fontXs
                        TextBlock.fontWeight FontWeight.SemiBold
                        TextBlock.foreground (Theme.brush (Theme.readableHex hex)) ]
              ) ]

    /// Purely decorative: every place this is used also states the same thing
    /// in words, so it is kept out of the accessibility tree rather than read
    /// out as an unnamed element.
    let colorDot (hex: string) : IView =
        Border.create
            [ Border.width 8.0
              Border.height 8.0
              Border.cornerRadius 4.0
              Border.background (Theme.brush hex)
              Border.verticalAlignment VerticalAlignment.Center
              decorative () ]

    // ---- buttons ------------------------------------------------------------

    let private buttonBase
        (label: string)
        (bg: IBrush)
        (fg: IBrush)
        (borderBrush: IBrush)
        (onClick: unit -> unit)
        : IView =
        Button.create
            [ Button.content label
              Button.background bg
              Button.foreground fg
              Button.borderBrush borderBrush
              Button.borderThickness 1.0
              Button.cornerRadius 6.0
              Button.padding (14.0, 7.0)
              Button.fontSize Theme.fontSm
              Button.fontWeight FontWeight.SemiBold
              Button.cursor hand
              Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always) ]

    /// Accent fill, material ink. White on a mid-tone accent vibrates and reads
    /// as a different brand; the redesign always sets `color:#1B1710` on an
    /// accent-filled control.
    let primaryButton (label: string) (onClick: unit -> unit) : IView =
        buttonBase label (Theme.accent ()) (Theme.onAccent ()) Theme.transparent onClick

    let ghostButton (label: string) (onClick: unit -> unit) : IView =
        buttonBase label Theme.transparent (Theme.text ()) (Theme.border ()) onClick

    let dangerButton (label: string) (onClick: unit -> unit) : IView =
        buttonBase label Theme.transparent (Theme.danger ()) (Theme.border ()) onClick

    let smallButton (label: string) (onClick: unit -> unit) : IView =
        Button.create
            [ Button.content label
              Button.background Theme.transparent
              Button.foreground (Theme.muted ())
              Button.borderBrush (Theme.border ())
              Button.borderThickness 1.0
              Button.cornerRadius 5.0
              Button.padding (8.0, 3.0)
              Button.fontSize Theme.fontXs
              Button.cursor hand
              Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always) ]

    /// A `smallButton` that lives **inside a selectable row**.
    ///
    /// It is an ordinary Button and deliberately does nothing clever with pointer
    /// events. An earlier version marked the press handled here, on the theory
    /// that doing so would stop an ancestor row handler from also selecting the
    /// row. **That did not work** — a user reported Open still selecting the row —
    /// and the swallow was removed rather than left in place looking protective.
    ///
    /// Keeping a control from stealing a click is the *row's* responsibility, not
    /// the button's: the row asks `Ui.isInteractiveSource` what was pressed. See
    /// the selection handler in `TimelineView`. Swallowing the press here also
    /// risked suppressing the Button's own press handling, and so its `Click`.
    ///
    /// The name is kept because the call sites read better for it, and because it
    /// marks which buttons sit inside a selectable row.
    let rowActionButton (label: string) (onClick: unit -> unit) : IView =
        Button.create
            [ Button.content label
              Button.background Theme.transparent
              Button.foreground (Theme.muted ())
              Button.borderBrush (Theme.border ())
              Button.borderThickness 1.0
              Button.cornerRadius 5.0
              Button.padding (8.0, 3.0)
              Button.fontSize Theme.fontXs
              Button.cursor hand
              Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always) ]

    // ---- the glass action button --------------------------------------------

    /// The only thing about a glass action button that ever differed between
    /// screens.
    ///
    /// Radius, minimum height, border inset and highlight were already the same
    /// three constants copied into three `Geometry` modules — 11.0, 38.0, 0.5
    /// and 0.0 — so `glassAction` owns those outright. Padding and margin really
    /// did differ, and changing them would be a redesign nobody asked for, so
    /// they stay with the caller.
    type GlassActionMetrics =
        { Padding: float * float
          Margin: float * float * float * float }

    [<RequireQualifiedAccess>]
    module GlassActionMetrics =
        let standard =
            { Padding = (15.0, 8.0); Margin = (0.0, 0.0, 10.0, 10.0) }

    /// **The primary action button, in one place.**
    ///
    /// There were three copies of this — Planner, Inbox and Notes — built from
    /// the same constants, with the same veil, the same ink rule and the same
    /// accent tint. They looked like three implementations of one component
    /// because that is what they were.
    ///
    /// **What that cost.** A primary action has to keep looking like the accent
    /// at every glass setting: the shared tint curve bottoms out at 0.40, where
    /// the panel is 60% blurred backdrop and a saturated accent desaturates
    /// toward the surface behind it. `Glass.primaryTintAlpha` is the derived
    /// floor that fixes it — and it was applied in exactly ONE of the three
    /// copies. The other two are Regenerate Planner, Accept and Accept All: the
    /// buttons reported as looking greyed out. One cause, three screens, and no
    /// amount of per-button patching would have found it.
    ///
    /// With one copy the floor cannot be applied to only some of them.
    ///
    /// Geometry is identical in every state — same height, padding, radius and
    /// inset whether the action is primary, secondary, hovered, pressed, focused
    /// or disabled. Only the tint and the ink move, so nothing in a row of
    /// actions can shift when a state changes.
    let glassAction
        (tokens: Tokens)
        (metrics: GlassActionMetrics)
        (label: string)
        (primary: bool)
        (enabled: bool)
        (name: string)
        (onClick: unit -> unit)
        : IView =
        let radius = 11.0
        let minHeight = 38.0
        let borderInset = 0.5

        let cornerRadius = Tokens.radius tokens radius

        // Built explicitly rather than passed as tuples: F#'s implicit
        // tuple-to-Thickness conversion only fires for a literal at the call
        // site, not for a value carried in a record.
        let padding =
            let x, y = metrics.Padding
            Thickness(x, y)

        let margin =
            let l, t, r, b = metrics.Margin
            Thickness(l, t, r, b)

        Button.create
            [ Button.classes [ glassActionClass () ]
              Button.isEnabled enabled
              Button.background Theme.transparent
              Button.borderThickness 0.0
              Button.padding 0.0
              // The Button's own radius must agree with the glass inside it.
              // Left at Fluent's default it is a much tighter curve, so anything
              // the template rounds — a focus ring, a state fill — cuts across
              // the glass corner instead of following it.
              Button.cornerRadius cornerRadius
              Button.clipToBounds false
              Button.minHeight minHeight
              Button.margin margin
              Button.cursor hand
              accessibleName name
              Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always)
              Button.content (
                  GlassPanel.create
                      [ GlassPanel.baseRadius radius
                        GlassPanel.elevation 0
                        // The whole hairline inside the panel, so the corners are
                        // not half-clipped by the draw operation's bounds.
                        GlassPanel.borderInset borderInset
                        // No broad white top sheen on a control.
                        GlassPanel.highlightScale 0.0
                        GlassPanel.tint (
                            if primary then
                                tokens.Accent.Normal
                            else
                                (Theme.surfaces ()).Action
                        )
                        // The floor described above. Secondary actions keep the
                        // shared curve untouched.
                        GlassPanel.minTintAlpha (
                            if primary then
                                Glass.primaryTintAlpha
                            else
                                0.0
                        )
                        GlassPanel.child (
                            Panel.create
                                [ Panel.children
                                      [ // Hover feedback, FIRST so it sits UNDER
                                        // the label — the surface lifts, the text
                                        // stays crisp. A GlassPanel paints through
                                        // a Skia draw operation and Opacity does
                                        // not reach one, so this ordinary Border
                                        // is the only channel that works.
                                        //
                                        // Its resting opacity is a STYLE setter,
                                        // never a local value: a local value
                                        // outranks a style and would make the
                                        // hover unreachable. See `veilBase`.
                                        //
                                        // It is also the topmost hit-testable
                                        // child, because it must receive
                                        // `:pointerover` itself — a selector
                                        // hanging off the Button's pseudo-class
                                        // does not fire for a descendant.
                                        Border.create
                                            [ Border.classes [ glassVeilClass () ]
                                              Border.background (
                                                  Theme.brush (Theme.withAlphaHex "2E" (hexOf (Theme.surfaces ()).Text))
                                              )
                                              Border.cornerRadius cornerRadius ]

                                        Border.create
                                            [ Border.padding padding
                                              Border.verticalAlignment VerticalAlignment.Stretch
                                              // Transparent to the pointer, so
                                              // every event reaches the veil
                                              // beneath — including over the text,
                                              // which would otherwise be the one
                                              // place hover died.
                                              Border.isHitTestVisible false
                                              Border.child (
                                                  TextBlock.create
                                                      [ TextBlock.text label
                                                        TextBlock.fontSize Theme.Type.rowMeta
                                                        TextBlock.fontWeight FontWeight.SemiBold
                                                        TextBlock.verticalAlignment VerticalAlignment.Center
                                                        TextBlock.foreground (
                                                            if primary then Theme.onAccent () else Theme.text ()
                                                        ) ] ) ] ] ] ) ] ) ]
        :> IView

    /// `smallButton` whose label is a glyph, so it carries a spoken name too.
    ///
    /// `smallButton` names itself from its content, which is correct while the
    /// content is words. Where the content is `▾` or `✕` the control is silent to
    /// assistive technology, and a caller cannot fix that from outside — hence a
    /// variant that takes the name rather than a rule that guesses it.
    let namedSmallButton (label: string) (name: string) (onClick: unit -> unit) : IView =
        Button.create
            [ Button.content label
              accessibleName name
              Button.background Theme.transparent
              Button.foreground (Theme.muted ())
              Button.borderBrush (Theme.border ())
              Button.borderThickness 1.0
              Button.cornerRadius 5.0
              Button.padding (8.0, 3.0)
              Button.fontSize Theme.fontXs
              Button.cursor hand
              Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always) ]

    let accentSmallButton (label: string) (onClick: unit -> unit) : IView =
        Button.create
            [ Button.content label
              Button.background (Theme.accentSoft ())
              Button.foreground (Theme.brush (Theme.accentHex ()))
              Button.cornerRadius 5.0
              Button.padding (8.0, 3.0)
              Button.fontSize Theme.fontXs
              Button.fontWeight FontWeight.SemiBold
              Button.cursor hand
              Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always) ]

    /// A custom checkbox glyph (avoids toggle-state fights with MVU): state
    /// always comes from the model, clicking dispatches.
    /// A tick control. A real Button, so it is focusable and keyboard
    /// activatable; the glyph alone would be nameless, so it carries an explicit
    /// accessible name that also states the current state.
    let checkGlyph (isChecked: bool) (onToggle: unit -> unit) : IView =
        Button.create
            [ Button.content (if isChecked then "☑" else "☐")
              Button.fontSize 16.0
              Button.background Theme.transparent
              Button.foreground (if isChecked then Theme.success () else Theme.muted ())
              Button.padding (2.0, 0.0)
              Button.cursor hand
              accessibleName (if isChecked then "Completed. Mark as not done" else "Not done. Mark as completed")
              Button.onClick ((fun _ -> onToggle ()), SubPatchOptions.Always) ]

    // ---- inputs -------------------------------------------------------------

    // Every input names itself from its watermark.
    //
    // A watermark is only visible while the box is EMPTY — the moment the user
    // types, the one cue identifying the field disappears. Sighted users keep a
    // nearby label to fall back on; assistive technology had nothing, because
    // Avalonia infers a name from content and a TextBox's content is the user's
    // own text. Every input in the app went through these three helpers, so
    // naming them here fixes Settings, Notes, Documents, Assistant, Graph,
    // Connections and Search in one place — and any input added later inherits it.
    //
    // The watermark is the right source: it is already a truthful description of
    // the field ("Your name", "Search titles, tags and text"), it is already
    // localised with the rest of the UI, and it can never be the value — which
    // matters most for `passwordBox`, where the name must describe the field and
    // never the credential inside it.

    let textBox (value: string) (watermark: string) (onChange: string -> unit) : IView =
        TextBox.create
            [ TextBox.text value
              TextBox.watermark watermark
              accessibleName watermark
              TextBox.fontSize Theme.fontBase
              TextBox.cornerRadius 6.0
              TextBox.onTextChanged ((fun t -> onChange t), SubPatchOptions.Always) ]

    let passwordBox (value: string) (watermark: string) (onChange: string -> unit) : IView =
        TextBox.create
            [ TextBox.text value
              TextBox.watermark watermark
              accessibleName watermark
              TextBox.passwordChar '•'
              TextBox.fontSize Theme.fontBase
              TextBox.cornerRadius 6.0
              TextBox.onTextChanged ((fun t -> onChange t), SubPatchOptions.Always) ]

    let multilineBox (value: string) (watermark: string) (minHeight: float) (onChange: string -> unit) : IView =
        TextBox.create
            [ TextBox.text value
              TextBox.watermark watermark
              accessibleName watermark
              TextBox.acceptsReturn true
              TextBox.textWrapping TextWrapping.Wrap
              TextBox.minHeight minHeight
              TextBox.fontSize Theme.fontBase
              TextBox.cornerRadius 6.0
              TextBox.verticalContentAlignment VerticalAlignment.Top
              TextBox.onTextChanged ((fun t -> onChange t), SubPatchOptions.Always) ]

    let labeled (label: string) (control: IView) : IView =
        StackPanel.create
            [ StackPanel.spacing 4.0
              StackPanel.children
                  [ TextBlock.create
                        [ TextBlock.text label
                          TextBlock.fontSize Theme.fontXs
                          TextBlock.fontWeight FontWeight.SemiBold
                          TextBlock.foreground (Theme.muted ()) ]
                    control ] ]

    /// Index-based combo of string options — reliable across FuncUI patching.
    let combo (options: string list) (selectedIndex: int) (onSelect: int -> unit) : IView =
        ComboBox.create
            [ ComboBox.dataItems options
              ComboBox.selectedIndex selectedIndex
              ComboBox.fontSize Theme.fontSm
              ComboBox.onSelectedIndexChanged ((fun i -> if i >= 0 then onSelect i), SubPatchOptions.Always) ]

    // ---- progress -----------------------------------------------------------

    /// Star-ratio grid progress bar — no measurement code, always proportional.
    let progressBar (percent: int) (hex: string) : IView =
        let p = max 0 (min 100 percent)

        Border.create
            [ Border.height 6.0
              Border.cornerRadius 3.0
              Border.background (Theme.surfaceAlt ())
              Border.child (
                  Grid.create
                      [ Grid.columnDefinitions (sprintf "%d*,%d*" (max p 1) (max (100 - p) 1))
                        Grid.children
                            [ Border.create
                                  [ Grid.column 0
                                    Border.cornerRadius 3.0
                                    Border.background (if p > 0 then Theme.brush hex else Theme.transparent) ] ] ]
              ) ]

    // ---- skeleton loading ---------------------------------------------------

    /// A static placeholder block shown while content loads. Deliberately not
    /// animated — calm beats shimmer, and loads here are near-instant anyway.
    /// A circular progress badge: an accent ring with the value in the middle,
    /// over a linear bar that carries the actual proportion.
    ///
    /// The redesign draws a conic arc here. FuncUI 1.5.1 exposes no shape DSL
    /// (`Path`, `Ellipse`) at all, so an arc would need a new Skia control with
    /// its own Tier C fallback — more rendering surface than one gauge is worth.
    /// The bar carries the proportion, the numeral carries the exact figure, and
    /// the ring carries the shape: the information is complete without
    /// inventing a control. `caption` is rendered as visible text, which is
    /// better for a screen reader than an invisible automation name would be.
    /// Every colour comes from the ACTIVE ACCENT PALETTE, never a literal.
    ///
    /// The previous version took a hex and was handed `Theme.accentHex()` — the
    /// fixed theme brand colour, not the accent the user picked — so the dials
    /// stayed purple whatever the swatch said. `accent` here is the live
    /// `Palette`, so changing the preset recolours both dials immediately.
    let ringGauge (size: float) (percent: int) (accent: UniversityOS.App.Design.Palette) (centre: string) (caption: string) : IView =
        let p = max 0 (min 100 percent)
        let hex = hexOf accent.Normal
        // Thin and refined, as in the redesign — not a heavy donut.
        let thickness = max 2.5 (size / 16.0)

        StackPanel.create
            [ StackPanel.spacing 8.0
              StackPanel.horizontalAlignment HorizontalAlignment.Center
              StackPanel.children
                  [ Panel.create
                        [ Panel.width size
                          Panel.height size
                          Panel.horizontalAlignment HorizontalAlignment.Center
                          Panel.children
                              [ // Remaining track: the same accent at low opacity.
                                Border.create
                                    [ Border.width size
                                      Border.height size
                                      Border.cornerRadius (size / 2.0)
                                      Border.borderThickness thickness
                                      Border.borderBrush (Theme.brush (Theme.withAlphaHex "26" hex))
                                      // Centre fill is the card surface, so the
                                      // value sits on the card, not on a disc.
                                      Border.background (Theme.brush (Theme.withAlphaHex "10" hex))
                                      decorative () ]

                                // Progress. FuncUI 1.5.1 has no shape DSL, so a
                                // true arc would need a bespoke Skia control;
                                // the ring reads as filled in proportion via the
                                // bar below, and the numeral carries the figure.
                                Border.create
                                    [ Border.width size
                                      Border.height size
                                      Border.cornerRadius (size / 2.0)
                                      Border.borderThickness (if p > 0 then thickness else 0.0)
                                      Border.borderBrush (Theme.brush (Theme.withAlphaHex (if p >= 100 then "FF" else "8A") hex))
                                      Border.opacity (if p > 0 then 1.0 else 0.0)
                                      decorative () ]

                                TextBlock.create
                                    [ TextBlock.text centre
                                      TextBlock.fontSize (size / 4.4)
                                      TextBlock.fontWeight FontWeight.SemiBold
                                      TextBlock.foreground (Theme.text ())
                                      TextBlock.horizontalAlignment HorizontalAlignment.Center
                                      TextBlock.verticalAlignment VerticalAlignment.Center ] ] ]

                    TextBlock.create
                        [ TextBlock.text caption
                          TextBlock.fontSize Theme.Type.metricLabel
                          TextBlock.foreground (Theme.muted ())
                          TextBlock.textAlignment TextAlignment.Center
                          TextBlock.textWrapping TextWrapping.Wrap
                          TextBlock.maxWidth (size * 2.2)
                          TextBlock.horizontalAlignment HorizontalAlignment.Center ] ] ]
        :> IView

    /// A large value with a small label under it.
    let statTile (value: string) (label: string) : IView =
        StackPanel.create
            [ StackPanel.spacing 2.0
              StackPanel.children
                  [ TextBlock.create
                        [ TextBlock.text value
                          TextBlock.fontSize Theme.fontLg
                          TextBlock.fontWeight FontWeight.SemiBold
                          TextBlock.foreground (Theme.text ())
                          TextBlock.textTrimming TextTrimming.CharacterEllipsis ]
                    TextBlock.create
                        [ TextBlock.text label
                          TextBlock.fontSize Theme.fontXs
                          TextBlock.foreground (Theme.muted ())
                          TextBlock.textWrapping TextWrapping.Wrap ] ] ]
        :> IView

    /// A quick-action tile: a real Button, so it is focusable, keyboard
    /// activatable and named — not a clickable decorative container.
    let quickAction (accentHex: string) (icon: string) (label: string) (onClick: unit -> unit) : IView =
        Button.create
            [ Button.horizontalAlignment HorizontalAlignment.Stretch
              Button.horizontalContentAlignment HorizontalAlignment.Left
              Button.background (Theme.action ())
              Button.borderBrush (Theme.border ())
              Button.borderThickness 1.0
              Button.cornerRadius 14.0
              Button.padding (16.0, 18.0)
              Button.minHeight 78.0
              Button.horizontalContentAlignment HorizontalAlignment.Center
              Button.cursor hand
              // The content is a panel, not text, so Avalonia cannot infer a
              // name from it.
              accessibleName label
              Button.onClick ((fun _ -> onClick ()), SubPatchOptions.Always)
              Button.content (
                  // Icon above label, centred — the redesign's broad action
                  // card, not a toolbar chip.
                  StackPanel.create
                      [ StackPanel.spacing 8.0
                        StackPanel.horizontalAlignment HorizontalAlignment.Center
                        StackPanel.children
                            [ TextBlock.create
                                  [ TextBlock.text icon
                                    TextBlock.fontSize 17.0
                                    TextBlock.foreground (Theme.brush accentHex)
                                    TextBlock.horizontalAlignment HorizontalAlignment.Center ]
                              TextBlock.create
                                  [ TextBlock.text label
                                    TextBlock.fontSize Theme.Type.actionLabel
                                    TextBlock.foreground (Theme.text ())
                                    TextBlock.textAlignment TextAlignment.Center
                                    TextBlock.textTrimming TextTrimming.CharacterEllipsis
                                    TextBlock.horizontalAlignment HorizontalAlignment.Center ] ] ]
              ) ]
        :> IView

    let skeletonBlock (width: float) (height: float) : IView =
        Border.create
            [ Border.width width
              Border.height height
              Border.cornerRadius 6.0
              Border.horizontalAlignment HorizontalAlignment.Left
              Border.background (Theme.surfaceAlt ()) ]

    /// A card-shaped skeleton: a section-title stub plus content line stubs.
    let skeletonCard (lines: int) : IView =
        card
            [ yield skeletonBlock 90.0 10.0
              for i in 0 .. lines - 1 do
                  yield skeletonBlock (if i % 2 = 0 then 230.0 else 170.0) 12.0 ]

    // ---- interactive rows ---------------------------------------------------

    /// A clickable row/card. The "hoverable" class picks up the app-level
    /// pointer-over style (opacity dip) — theme-agnostic micro-interaction.
    let clickable (isSelected: bool) (onClick: unit -> unit) (content: IView) : IView =
        Border.create
            [ Border.classes [ "hoverable" ]
              Border.background (if isSelected then Theme.surfaceAlt () else Theme.surface ())
              Border.cornerRadius 8.0
              Border.borderThickness 1.0
              Border.borderBrush (if isSelected then Theme.accent () else Theme.border ())
              Border.padding 12.0
              Border.cursor hand
              Border.onPointerPressed ((fun _ -> onClick ()), SubPatchOptions.Always)
              Border.child content ]

    let toolbar (children: IView list) : IView =
        StackPanel.create
            [ StackPanel.orientation Orientation.Horizontal
              StackPanel.spacing 8.0
              StackPanel.children children ]

    // ---- markdown rendering (v1.1) ------------------------------------------

    let private mdText (size: float) (fg: IBrush) (t: string) : IView =
        TextBlock.create
            [ TextBlock.text t
              TextBlock.fontSize size
              TextBlock.foreground fg
              TextBlock.textWrapping TextWrapping.Wrap
              TextBlock.lineHeight (size * 1.55) ]

    let private calloutStyle (kind: string) =
        match kind with
        | "TIP"
        | "SUCCESS" -> "✔", Theme.successHex ()
        | "WARN"
        | "WARNING"
        | "CAUTION" -> "⚠", Theme.warningHex ()
        | "IMPORTANT"
        | "DANGER" -> "‼", Theme.dangerHex ()
        | _ -> "ℹ", Theme.infoHex () // NOTE / INFO / anything else

    /// Rich structural Markdown: headings, bullets, numbered + task lists,
    /// quotes, callouts, code fences and tables. `onToggleTask` makes task
    /// checkboxes interactive (the notes editor passes it; read-only surfaces
    /// pass None). Inline emphasis is intentionally rendered as-is.
    let markdownWith (onToggleTask: (int -> unit) option) (source: string) : IView =
        let render (block: Markdown.Block) : IView option =
            match block with
            | Markdown.Blank -> Some(Border.create [ Border.height 4.0 ])
            | Markdown.Heading(1, t) ->
                Some(
                    TextBlock.create
                        [ TextBlock.text t
                          TextBlock.fontSize Theme.fontXl
                          TextBlock.fontWeight FontWeight.Bold
                          TextBlock.foreground (Theme.text ())
                          TextBlock.margin (0.0, 10.0, 0.0, 2.0)
                          TextBlock.textWrapping TextWrapping.Wrap ]
                )
            | Markdown.Heading(2, t) ->
                Some(
                    TextBlock.create
                        [ TextBlock.text t
                          TextBlock.fontSize Theme.fontLg
                          TextBlock.fontWeight FontWeight.SemiBold
                          TextBlock.foreground (Theme.text ())
                          TextBlock.margin (0.0, 8.0, 0.0, 1.0)
                          TextBlock.textWrapping TextWrapping.Wrap ]
                )
            | Markdown.Heading(_, t) ->
                Some(
                    TextBlock.create
                        [ TextBlock.text t
                          TextBlock.fontSize Theme.fontMd
                          TextBlock.fontWeight FontWeight.SemiBold
                          TextBlock.foreground (Theme.text ())
                          TextBlock.margin (0.0, 6.0, 0.0, 0.0)
                          TextBlock.textWrapping TextWrapping.Wrap ]
                )
            | Markdown.Paragraph t -> Some(mdText Theme.fontBase (Theme.text ()) t)
            | Markdown.Bullet t ->
                Some(
                    StackPanel.create
                        [ StackPanel.orientation Orientation.Horizontal
                          StackPanel.spacing 8.0
                          StackPanel.children
                              [ TextBlock.create
                                    [ TextBlock.text "•"
                                      TextBlock.foreground (Theme.brush (Theme.accentHex ()))
                                      TextBlock.fontSize Theme.fontBase ]
                                Border.create
                                    [ Border.maxWidth 640.0
                                      Border.child (mdText Theme.fontBase (Theme.text ()) t) ] ] ]
                )
            | Markdown.Numbered(ordinal, t) ->
                Some(
                    StackPanel.create
                        [ StackPanel.orientation Orientation.Horizontal
                          StackPanel.spacing 8.0
                          StackPanel.children
                              [ TextBlock.create
                                    [ TextBlock.text (ordinal + ".")
                                      TextBlock.foreground (Theme.muted ())
                                      TextBlock.fontSize Theme.fontBase
                                      TextBlock.width 22.0 ]
                                Border.create
                                    [ Border.maxWidth 640.0
                                      Border.child (mdText Theme.fontBase (Theme.text ()) t) ] ] ]
                )
            | Markdown.TaskItem(index, isDone, t) ->
                let check: IView =
                    match onToggleTask with
                    | Some toggle -> checkGlyph isDone (fun () -> toggle index)
                    | None ->
                        TextBlock.create
                            [ TextBlock.text (if isDone then "☑" else "☐")
                              TextBlock.fontSize 15.0
                              TextBlock.foreground (if isDone then Theme.success () else Theme.muted ()) ]

                Some(
                    StackPanel.create
                        [ StackPanel.orientation Orientation.Horizontal
                          StackPanel.spacing 8.0
                          StackPanel.children
                              [ check
                                TextBlock.create
                                    [ TextBlock.text t
                                      TextBlock.fontSize Theme.fontBase
                                      TextBlock.verticalAlignment VerticalAlignment.Center
                                      TextBlock.textWrapping TextWrapping.Wrap
                                      TextBlock.foreground (if isDone then Theme.faint () else Theme.text ())
                                      TextBlock.textDecorations (
                                          if isDone then TextDecorations.Strikethrough else null
                                      ) ] ] ]
                )
            | Markdown.Quote t ->
                Some(
                    Border.create
                        [ Border.borderThickness (3.0, 0.0, 0.0, 0.0)
                          Border.borderBrush (Theme.brush (Theme.accentHex ()))
                          Border.padding (10.0, 2.0, 0.0, 2.0)
                          Border.child (
                              TextBlock.create
                                  [ TextBlock.text t
                                    TextBlock.fontSize Theme.fontBase
                                    TextBlock.fontStyle FontStyle.Italic
                                    TextBlock.foreground (Theme.muted ())
                                    TextBlock.textWrapping TextWrapping.Wrap ]
                          ) ]
                )
            | Markdown.Callout(kind, title, body) ->
                let icon, hex = calloutStyle kind

                Some(
                    Border.create
                        [ Border.background (Theme.brush (Theme.withAlphaHex "1A" hex))
                          Border.borderThickness (3.0, 0.0, 0.0, 0.0)
                          Border.borderBrush (Theme.brush hex)
                          Border.cornerRadius 6.0
                          Border.padding 12.0
                          Border.child (
                              StackPanel.create
                                  [ StackPanel.spacing 4.0
                                    StackPanel.children
                                        [ StackPanel.create
                                              [ StackPanel.orientation Orientation.Horizontal
                                                StackPanel.spacing 8.0
                                                StackPanel.children
                                                    [ TextBlock.create
                                                          [ TextBlock.text icon
                                                            TextBlock.fontSize Theme.fontSm
                                                            TextBlock.foreground (Theme.brush hex) ]
                                                      TextBlock.create
                                                          [ TextBlock.text (if title.Trim().Length > 0 then title else kind)
                                                            TextBlock.fontSize Theme.fontSm
                                                            TextBlock.fontWeight FontWeight.SemiBold
                                                            TextBlock.foreground (Theme.brush hex) ] ] ]
                                          for line in body do
                                              mdText Theme.fontSm (Theme.text ()) line ] ]
                          ) ]
                )
            | Markdown.CodeBlock lines ->
                Some(
                    Border.create
                        [ Border.background (Theme.surfaceAlt ())
                          Border.cornerRadius 6.0
                          Border.padding 10.0
                          Border.child (
                              TextBlock.create
                                  [ TextBlock.text (String.concat "\n" lines)
                                    TextBlock.fontFamily UniversityOS.App.Design.Typography.mono
                                    TextBlock.fontSize Theme.fontSm
                                    TextBlock.foreground (Theme.text ())
                                    TextBlock.textWrapping TextWrapping.Wrap ]
                          ) ]
                )
            | Markdown.Table(header, rows) ->
                let columnCount = max 1 header.Length
                let columnDefs = List.replicate columnCount "1*" |> String.concat ","

                let cell (isHeader: bool) (column: int) (text: string) : IView =
                    Border.create
                        [ Grid.column column
                          Border.padding (8.0, 5.0)
                          Border.borderThickness (0.0, 0.0, 0.0, 1.0)
                          Border.borderBrush (Theme.border ())
                          Border.background (if isHeader then Theme.surfaceAlt () else Theme.transparent)
                          Border.child (
                              TextBlock.create
                                  [ TextBlock.text text
                                    TextBlock.fontSize Theme.fontSm
                                    TextBlock.fontWeight (if isHeader then FontWeight.SemiBold else FontWeight.Normal)
                                    TextBlock.foreground (Theme.text ())
                                    TextBlock.textWrapping TextWrapping.Wrap ]
                          ) ]

                Some(
                    Border.create
                        [ Border.borderThickness 1.0
                          Border.borderBrush (Theme.border ())
                          Border.cornerRadius 6.0
                          Border.clipToBounds true
                          Border.child (
                              StackPanel.create
                                  [ StackPanel.children
                                        [ Grid.create
                                              [ Grid.columnDefinitions columnDefs
                                                Grid.children [ for i, h in List.indexed header -> cell true i h ] ]
                                          for row in rows do
                                              Grid.create
                                                  [ Grid.columnDefinitions columnDefs
                                                    Grid.children
                                                        [ for i, value in List.indexed row do
                                                              if i < columnCount then
                                                                  cell false i value ] ] ] ]
                          ) ]
                )

        StackPanel.create
            [ StackPanel.spacing 4.0
              StackPanel.children (Markdown.parse source |> List.choose render) ]
        :> IView

    let markdown (source: string) : IView = markdownWith None source
