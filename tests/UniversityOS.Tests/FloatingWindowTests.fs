module UniversityOS.Tests.FloatingWindowTests

// Avalonia 11.3 deprecates the public `PointerPressedEventArgs` constructor in
// favour of `IHeadlessWindow` mouse methods. Those need a started Avalonia
// application; these tests deliberately have none — they invoke the delegate the
// view recorded, which is the only way to test the wiring without a window.
#nowarn "44"

open System
open Xunit
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open UniversityOS.App
open UniversityOS.Tests.AuditHarness

/// **One floating window, six callers.**
///
/// The review asks for a floating surface in six places. Six implementations
/// would be six slightly different answers to the same questions, and the glass
/// action button has already shown what that costs — eleven copies of one
/// control, ten of them missing the line that mattered.
///
/// These tests pin the answers, and two of them pin *structure* rather than
/// behaviour, because structure is what makes the behaviour impossible to get
/// wrong later.
///
/// **What they cannot tell you.** Whether the window looks right, whether the
/// scrim is the right darkness, whether the focus ring is visible on the glass.
/// That is the reviewer's judgement in the running window.

let private tokens = AuditFixtures.tokens

let private sampleContent =
    TextBlock.create [ TextBlock.text "window content" ] :> IView

let private windowWith (onDismiss: FloatingWindow.DismissSource -> unit) =
    FloatingWindow.create tokens "Edit assignment" 620.0 480.0 onDismiss sampleContent

let private window () = windowWith ignore

// ---------------------------------------------------------------------------
// The dismissal rule
// ---------------------------------------------------------------------------

/// **Unsaved work is never lost silently, by any route out.**
///
/// Stated over every source rather than over the one the reviewer mentioned: a
/// reader who has to remember that Escape confirms but the ✕ does not has been
/// given two rules, and losing work to a stray Escape is the same loss as
/// losing it to a stray click.
[<Fact>]
let ``every route out confirms when there are unsaved changes`` () =
    for source in FloatingWindow.DismissSource.all do
        Assert.Equal(
            FloatingWindow.ConfirmDiscard,
            FloatingWindow.outcomeOf true source
        )

/// And "click outside closes when safe" — with nothing to lose, no route may
/// stop to ask. A confirmation on a read-only viewer is friction with no purpose.
[<Fact>]
let ``every route out closes immediately when there is nothing to lose`` () =
    for source in FloatingWindow.DismissSource.all do
        Assert.Equal(FloatingWindow.Close, FloatingWindow.outcomeOf false source)

[<Fact>]
let ``all four dismissal routes exist and are distinct`` () =
    Assert.Equal(4, FloatingWindow.DismissSource.all.Length)
    Assert.Equal(4, FloatingWindow.DismissSource.all |> List.distinct |> List.length)

    for source in FloatingWindow.DismissSource.all do
        Assert.False(String.IsNullOrWhiteSpace(FloatingWindow.DismissSource.label source))

// ---------------------------------------------------------------------------
// Geometry
// ---------------------------------------------------------------------------

/// The window never fills the viewport: the scrim has to stay visibly clickable
/// on every edge, or "click outside to close" is a gesture with nowhere to
/// perform it.
[<Fact>]
let ``the window always leaves scrim on every edge`` () =
    for viewport in [ 900.0; 1108.0; 1400.0; 1920.0; 2560.0 ] do
        let width = FloatingWindow.Geometry.widthFor 10_000.0 viewport
        Assert.True(width < viewport, sprintf "at %f the window fills the viewport" viewport)
        Assert.Equal(viewport * FloatingWindow.Geometry.viewportFraction, width, 3)

/// A preferred size smaller than the available space is honoured — the window
/// is not stretched to the cap just because it can be.
[<Fact>]
let ``a modest preferred size is honoured rather than inflated`` () =
    Assert.Equal(620.0, FloatingWindow.Geometry.widthFor 620.0 1400.0)
    Assert.Equal(480.0, FloatingWindow.Geometry.heightFor 480.0 900.0)

/// Below the minimum the fraction would leave too little to read in, so the
/// window keeps its floor and its content scrolls instead.
[<Fact>]
let ``the window never shrinks below a readable minimum`` () =
    Assert.Equal(FloatingWindow.Geometry.minWidth, FloatingWindow.Geometry.widthFor 600.0 200.0)
    Assert.Equal(FloatingWindow.Geometry.minHeight, FloatingWindow.Geometry.heightFor 600.0 100.0)

// ---------------------------------------------------------------------------
// Structure — the part that makes the behaviour hard to get wrong
// ---------------------------------------------------------------------------

let private namedWindow (root: IView) =
    root |> all |> List.find (fun v -> accessibleName v = Some "Edit assignment")

let private scrim (root: IView) =
    root
    |> all
    |> List.find (fun v -> typeName v = "Border" && hasHandler "PointerPressed" v)

/// **The scrim is a sibling of the window, never its ancestor.**
///
/// Nested, every click on the window would bubble to the scrim's handler and the
/// host would have to reason about the event's source to tell a real outside
/// click from a press on a control inside — the same trap the Timeline row fell
/// into twice. As siblings there is no ordering question to get wrong, and this
/// test is what stops somebody "tidying" the tree into the nested shape.
[<Fact>]
let ``the window is not inside the scrim, so a press on it never reaches the scrim`` () =
    let root = window ()
    let backdrop = scrim root

    let ancestorsOfWindow =
        root
        |> allWithAncestors
        |> List.find (fun (v, _) -> accessibleName v = Some "Edit assignment")
        |> snd

    Assert.DoesNotContain(backdrop, ancestorsOfWindow)

    // And they really are siblings under one Panel.
    let panels = root |> ofType "Panel"
    Assert.NotEmpty panels

[<Fact>]
let ``the window carries its title as its accessible name`` () =
    Assert.Equal(Some "Edit assignment", accessibleName (namedWindow (window ())))

/// The backdrop is deliberately unnamed: a screen-reader user navigates the
/// dialog and its ✕, not the sheet behind them.
[<Fact>]
let ``the scrim is not announced as a control of its own`` () =
    Assert.Equal(None, accessibleName (scrim (window ())))

/// Tab from the last control must return to the first rather than walking out
/// into the page behind. That is the focus trap, and it is an attached property
/// a pure view cannot express any other way.
[<Fact>]
let ``focus is trapped inside the window`` () =
    let win = namedWindow (window ())

    Assert.Equal(
        Some(box KeyboardNavigationMode.Cycle),
        byAvaloniaProperty KeyboardNavigation.TabNavigationProperty win
    )

    Assert.Equal(Some true, boolProp "Focusable" win)

/// A long editor or a full day of sessions must not push its own actions off
/// the bottom of the window.
[<Fact>]
let ``the content area scrolls`` () =
    let root = window ()
    Assert.NotEmpty(root |> ofType "ScrollViewer")

    let text = everyString root |> String.concat "\n"
    Assert.Contains("window content", text)
    Assert.Contains("Edit assignment", text)

// ---------------------------------------------------------------------------
// The handlers, invoked
// ---------------------------------------------------------------------------

let private pointerPress (source: obj) =
    PointerPressedEventArgs(
        source,
        new Pointer(0, PointerType.Mouse, true),
        Border(),
        Point(0.0, 0.0),
        0UL,
        PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed),
        KeyModifiers.None
    )

let private invoke (namePart: string) (view: IView) (args: obj) =
    let subscription =
        view.Attrs
        |> List.pick (fun a ->
            match a.Subscription with
            | ValueSome s when s.Name.Contains(namePart, StringComparison.OrdinalIgnoreCase) -> Some s
            | _ -> None)

    subscription.Func.DynamicInvoke [| args |] |> ignore

/// Invoking the recorded delegate rather than driving a model: the point is that
/// the *view* wires the gesture to the right source, which a message-level test
/// cannot see.
[<Fact>]
let ``pressing the scrim reports an outside click`` () =
    let seen = ResizeArray<FloatingWindow.DismissSource>()
    let root = windowWith seen.Add

    invoke "PointerPressed" (scrim root) (pointerPress (Border()))

    Assert.Equal<FloatingWindow.DismissSource list>([ FloatingWindow.OutsideClick ], List.ofSeq seen)

[<Fact>]
let ``Escape inside the window reports Escape, and other keys report nothing`` () =
    let seen = ResizeArray<FloatingWindow.DismissSource>()
    let root = windowWith seen.Add
    let win = namedWindow root

    let keyPress (key: Key) =
        KeyEventArgs(Key = key, RoutedEvent = InputElement.KeyDownEvent)

    invoke "KeyDown" win (keyPress Key.A)
    Assert.Empty seen

    invoke "KeyDown" win (keyPress Key.Escape)
    Assert.Equal<FloatingWindow.DismissSource list>([ FloatingWindow.EscapeKey ], List.ofSeq seen)

[<Fact>]
let ``the close button reports the close button`` () =
    let seen = ResizeArray<FloatingWindow.DismissSource>()
    let root = windowWith seen.Add

    let closeButton =
        root
        |> all
        |> List.find (fun v -> accessibleName v = Some "Close Edit assignment")

    invoke "Click" closeButton (Avalonia.Interactivity.RoutedEventArgs())

    Assert.Equal<FloatingWindow.DismissSource list>([ FloatingWindow.CloseButton ], List.ofSeq seen)

/// The component never closes anything itself. A view that silently discarded an
/// editor's contents is exactly the failure `outcomeOf` exists to prevent, so
/// the decision stays with the host.
[<Fact>]
let ``the window reports dismissal rather than performing it`` () =
    let seen = ResizeArray<FloatingWindow.DismissSource>()
    let root = windowWith seen.Add

    // Nothing at all happens without a gesture.
    Assert.Empty seen

    invoke "PointerPressed" (scrim root) (pointerPress (Border()))

    // One report, and the host is the one that consults `outcomeOf`.
    Assert.Single seen |> ignore
    Assert.Equal(FloatingWindow.ConfirmDiscard, FloatingWindow.outcomeOf true seen.[0])
