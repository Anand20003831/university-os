module UniversityOS.Tests.AuditHarness

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Automation
open Avalonia.FuncUI.Types

/// Shared tree-walking harness for the cross-screen audits.
///
/// The eleven per-screen test files each grew their own private copy of these
/// helpers, which was fine while each file only looked at one screen. The audits
/// look at *all* of them, along one dimension at a time, so the helpers belong in
/// one place — and, more importantly, so a claim like "this control has no
/// accessible name" means exactly the same thing on every screen.
///
/// A FuncUI `IView` is an immutable description, not a control: `ViewType` plus
/// `Attrs`. Nothing here instantiates Avalonia, so every audit runs headless.

// ---------------------------------------------------------------------------
// structure
// ---------------------------------------------------------------------------

let kids (view: IView) : IView list =
    view.Attrs
    |> List.collect (fun attr ->
        match attr.Content with
        | ValueSome content ->
            match content.Content with
            | ViewContent.Single(Some child) -> [ child ]
            | ViewContent.Single None -> []
            | ViewContent.Multiple children -> children
        | ValueNone -> [])

/// Every view in the tree, root first, depth-first.
let rec all (view: IView) : IView list = view :: (kids view |> List.collect all)

/// Every view paired with its ancestor chain, nearest ancestor first. Needed by
/// any audit whose question is "is this thing *inside* that thing" — clipping,
/// z-order and modal containment are all of that shape.
let allWithAncestors (root: IView) : (IView * IView list) list =
    let rec walk (ancestors: IView list) (view: IView) =
        (view, ancestors) :: (kids view |> List.collect (walk (view :: ancestors)))

    walk [] root

let typeName (view: IView) = view.ViewType.Name

let ofType (name: string) (root: IView) =
    all root |> List.filter (fun v -> typeName v = name)

// ---------------------------------------------------------------------------
// properties
// ---------------------------------------------------------------------------

let private accessorName (accessor: Accessor) =
    match accessor with
    | Accessor.InstanceProperty p -> p.Name
    | Accessor.AvaloniaProperty p -> p.Name

/// Looks a property up **by name**. Convenient, but ambiguous where two
/// properties share a short name — see `byAvaloniaProperty` for those.
let propOf (name: string) (view: IView) : obj option =
    view.Attrs
    |> List.tryPick (fun attr ->
        match attr.Property with
        | ValueSome p when accessorName p.Accessor = name -> Some p.Value
        | _ -> None)

/// Looks a property up by **identity**, which is the only safe way to read the
/// automation properties: `AutomationProperties.NameProperty` and
/// `StyledElement.NameProperty` are both called `"Name"`, and confusing an
/// accessible name with an element's internal id would make an accessibility
/// audit report the opposite of the truth.
let byAvaloniaProperty (target: AvaloniaProperty) (view: IView) : obj option =
    view.Attrs
    |> List.tryPick (fun attr ->
        match attr.Property with
        | ValueSome p ->
            match p.Accessor with
            | Accessor.AvaloniaProperty ap when Object.ReferenceEquals(ap, target) -> Some p.Value
            | _ -> None
        | _ -> None)

let stringProp name view =
    match propOf name view with
    | Some(:? string as s) -> Some s
    | _ -> None

let floatProp name view =
    match propOf name view with
    | Some(:? float as f) -> Some f
    | _ -> None

let boolProp name view =
    match propOf name view with
    | Some(:? bool as b) -> Some b
    | _ -> None

let intProp name view =
    match propOf name view with
    | Some(:? int as i) -> Some i
    | _ -> None

// ---------------------------------------------------------------------------
// accessibility
// ---------------------------------------------------------------------------

/// The name assistive technology would read, as set by `Ui.accessibleName`.
let accessibleName (view: IView) : string option =
    match byAvaloniaProperty AutomationProperties.NameProperty view with
    | Some(:? string as s) when not (String.IsNullOrWhiteSpace s) -> Some s
    | _ -> None

/// True when the view is hidden from the accessibility tree by `Ui.decorative`.
let isDecorative (view: IView) : bool =
    match byAvaloniaProperty AutomationProperties.AccessibilityViewProperty view with
    | Some(:? AccessibilityView as v) -> v = AccessibilityView.Raw
    | _ -> false

/// The text a sighted user reads on a control: its own `Text`/`Content` string,
/// or the first such string among its descendants. A button whose content is a
/// panel of glyphs has no readable text and therefore needs an accessible name.
let visibleText (view: IView) : string option =
    all view
    |> List.tryPick (fun v ->
        match stringProp "Text" v, stringProp "Content" v with
        | Some t, _ when not (String.IsNullOrWhiteSpace t) -> Some t
        | _, Some c when not (String.IsNullOrWhiteSpace c) -> Some c
        | _ -> None)

/// Every string anywhere in the tree that a user or screen reader could
/// encounter — including accessible names and watermarks, which is what makes
/// this the right net for a secret-leak check.
let everyString (root: IView) : string list =
    all root
    |> List.collect (fun v ->
        [ stringProp "Text" v
          stringProp "Content" v
          stringProp "Watermark" v
          stringProp "ToolTip" v
          accessibleName v ]
        |> List.choose id)

// ---------------------------------------------------------------------------
// interaction
// ---------------------------------------------------------------------------

/// Control types that are interactive by nature. `Border` is deliberately absent:
/// a Border with a click handler is a real target, but one without is scenery,
/// and only `isClickable` can tell them apart.
let interactiveTypes =
    set [ "Button"; "ToggleButton"; "RadioButton"; "CheckBox"; "ComboBox"; "TextBox"; "Slider"; "ListBox"; "TabControl" ]

let isInteractiveType (view: IView) = interactiveTypes.Contains(typeName view)

/// True when the view carries a click/tap subscription. FuncUI records these as
/// content attributes rather than properties, so they cannot be read as values —
/// only their presence can be detected, which is all an audit needs.
let hasHandler (namePart: string) (view: IView) =
    view.Attrs
    |> List.exists (fun attr ->
        match attr.Subscription with
        | ValueSome s -> s.Name.Contains(namePart, StringComparison.OrdinalIgnoreCase)
        | ValueNone -> false)

/// The names of every subscription on a view — the handler set, which is what an
/// audit needs when the question is "can this be reached by keyboard as well as
/// by pointer".
let handlerNames (view: IView) =
    view.Attrs
    |> List.choose (fun attr ->
        match attr.Subscription with
        | ValueSome s -> Some s.Name
        | ValueNone -> None)

let isClickable (view: IView) =
    hasHandler "Click" view || hasHandler "Tapped" view || hasHandler "PointerPressed" view

/// Every genuinely interactive element: a control of an interactive type, or
/// anything carrying a click handler.
let interactive (root: IView) =
    all root |> List.filter (fun v -> isInteractiveType v || isClickable v)

let isEnabled (view: IView) =
    boolProp "IsEnabled" view |> Option.defaultValue true

let isVisible (view: IView) =
    boolProp "IsVisible" view |> Option.defaultValue true

/// Views that are actually rendered — anything under an `IsVisible false`
/// ancestor is described but never shown, and must not be audited as if it were.
let visible (root: IView) =
    allWithAncestors root
    |> List.filter (fun (v, ancestors) -> isVisible v && ancestors |> List.forall isVisible)
    |> List.map fst

let visibleInteractive (root: IView) =
    visible root |> List.filter (fun v -> isInteractiveType v || isClickable v)

// ---------------------------------------------------------------------------
// geometry
// ---------------------------------------------------------------------------

/// The declared size of a target, where one is declared. `None` means the value
/// is left to layout, which a headless tree cannot resolve — an audit must say
/// so rather than assume a number.
type Declared =
    { Width: float option
      Height: float option
      MinWidth: float option
      MinHeight: float option
      Padding: Thickness option }

let declared (view: IView) : Declared =
    { Width = floatProp "Width" view
      Height = floatProp "Height" view
      MinWidth = floatProp "MinWidth" view
      MinHeight = floatProp "MinHeight" view
      Padding =
        match propOf "Padding" view with
        | Some(:? Thickness as t) -> Some t
        | _ -> None }

/// The smallest height this control can be, from whatever it declares. Padding
/// counts: a 12px glyph in 8px of vertical padding is a 28px target.
let effectiveMinHeight (view: IView) =
    let d = declared view

    let fromPadding =
        d.Padding |> Option.map (fun p -> p.Top + p.Bottom) |> Option.defaultValue 0.0

    [ d.Height; d.MinHeight ]
    |> List.choose id
    |> function
        | [] -> if fromPadding > 0.0 then Some fromPadding else None
        | xs -> Some(List.max xs)

let effectiveMinWidth (view: IView) =
    let d = declared view

    let fromPadding =
        d.Padding |> Option.map (fun p -> p.Left + p.Right) |> Option.defaultValue 0.0

    [ d.Width; d.MinWidth ]
    |> List.choose id
    |> function
        | [] -> if fromPadding > 0.0 then Some fromPadding else None
        | xs -> Some(List.max xs)

/// Any declared dimension that is negative or not a number. A `Grid` handed a
/// negative width throws at layout time, so this is a genuine crash check rather
/// than a style preference.
let impossibleDimensions (root: IView) =
    all root
    |> List.collect (fun v ->
        [ "Width"; "Height"; "MinWidth"; "MinHeight"; "MaxWidth"; "MaxHeight" ]
        |> List.choose (fun name ->
            match floatProp name v with
            | Some value when Double.IsNaN value && name.StartsWith "Min" -> Some(typeName v, name, value)
            | Some value when value < 0.0 -> Some(typeName v, name, value)
            | Some value when Double.IsInfinity value && not (name.StartsWith "Max") -> Some(typeName v, name, value)
            | _ -> None))

/// A control that declares a fixed width wider than the viewport forces the page
/// to scroll sideways. `MaxWidth` does not: it is a ceiling, not a demand.
let widerThan (viewport: float) (root: IView) =
    all root
    |> List.filter (fun v ->
        match floatProp "Width" v, floatProp "MinWidth" v with
        | Some w, _ when w > viewport -> true
        | _, Some mw when mw > viewport -> true
        | _ -> false)

// ---------------------------------------------------------------------------
// text
// ---------------------------------------------------------------------------

/// Text that is allowed to wrap, or is explicitly trimmed. Long content that can
/// do neither is the thing that pushes controls off-screen.
let wrapsOrTrims (view: IView) =
    let wraps =
        match propOf "TextWrapping" view with
        | Some(:? Avalonia.Media.TextWrapping as w) -> w <> Avalonia.Media.TextWrapping.NoWrap
        | _ -> false

    let trims =
        match propOf "TextTrimming" view with
        | Some(:? Avalonia.Media.TextTrimming as t) -> not (Object.ReferenceEquals(t, Avalonia.Media.TextTrimming.None))
        | _ -> false

    wraps || trims

/// Long single-line strings with no wrapping and no trimming, which is where
/// overflow comes from in practice.
let unwrappedLongText (threshold: int) (root: IView) =
    all root
    |> List.filter (fun v ->
        typeName v = "TextBlock"
        && not (wrapsOrTrims v)
        && (match stringProp "Text" v with
            | Some t -> t.Length > threshold && not (t.Contains '\n')
            | None -> false))

// ---------------------------------------------------------------------------
// the audited widths
// ---------------------------------------------------------------------------

/// The matrix, as the audit brief defines it. Heights are carried because two
/// entries share a width and differ only in height.
let viewports =
    [ 1920.0, 1080.0
      1600.0, 900.0
      1440.0, 900.0
      1366.0, 768.0
      1280.0, 720.0
      1024.0, 768.0
      768.0, 1024.0
      480.0, 800.0
      360.0, 800.0 ]

let widths = viewports |> List.map fst

/// The width a screen actually receives, given a window width.
///
/// **This is derived, not invented.** Every screen measures itself — each view's
/// root carries `onSizeChanged (dispatch (Resized args.NewSize.Width))` — so the
/// number reaching `Model.Width` is the screen's own rendered width, never the
/// window's. Two things come off first, both read from production source:
///
/// * `ShellLayout.reservedWidth`, which is `SidebarDensity.width` only while the
///   sidebar is **pinned**; floating, the panel hovers over content and reserves
///   nothing (`Shell.fs:1576`).
/// * The content border's 10px margin on each side (`Shell.fs:1586`).
///
/// Auditing a screen at the full window width would audit a layout that never
/// happens; auditing it with the sidebar always pinned would miss that the same
/// window is wider when the sidebar floats. Both cases are therefore modelled.
let private contentMargin = 10.0 + 10.0

let contentWidthWith (density: UniversityOS.Domain.SidebarDensity) (pinned: bool) (windowWidth: float) =
    let reserved =
        if pinned then
            UniversityOS.Domain.SidebarDensity.width density
        else
            0.0

    windowWidth - reserved - contentMargin

/// The narrowest case a given window width can produce: sidebar pinned and
/// expanded. If a screen survives this, it survives every other sidebar state at
/// that window width.
let contentWidth (windowWidth: float) =
    contentWidthWith UniversityOS.Domain.SidebarDensity.Expanded true windowWidth
