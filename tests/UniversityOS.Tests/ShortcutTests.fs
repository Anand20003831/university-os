module UniversityOS.Tests.ShortcutTests

open Xunit
open Avalonia.Input
open UniversityOS.App

/// **The global shortcut policy.**
///
/// Reported from the first runtime review: **Ctrl+N did not open Capture from
/// normal application state.**
///
/// The binding was never wrong. `handleKeys` had always mapped Ctrl+N to
/// `OpenCapture`, and `update` had always handled it. What was wrong was *where
/// the handler was attached*: `Panel.onKeyDown` on the shell root. Key events
/// bubble **from the focused element**, and a `Panel` is neither focusable nor
/// ever focused — so on a fresh window, before anything had been clicked, there
/// was no focused descendant for the event to bubble from and the handler never
/// ran. Click any page control first and the same shortcut worked, which is
/// exactly the "from normal application state" qualifier in the report.
///
/// The fix adds a handler on the window itself, which is the fallback focus
/// target. The Panel handler stays: it runs first when focus *is* inside the
/// page and marks the event handled, and the window handler is registered
/// without `handledEventsToo`, so the two can never both act on one press —
/// which is what stops a second Capture opening on top of the first.
///
/// **What these tests cover:** the decision — every binding, and the modal rules
/// Escape depends on. That is now a pure function precisely so it can be tested.
///
/// **What they cannot cover:** the attachment. Routed-event delivery only exists
/// in a live Avalonia window, so *that the window handler fires when nothing is
/// focused* is not decidable headlessly and needs confirming in the running app.

let private noOverlays: Shell.ShortcutContext =
    { CaptureOpen = false
      PaletteOpen = false
      BriefingOpen = false
      ReviewOpen = false }

let private resolve ctrl shift key = Shell.resolveShortcut noOverlays ctrl shift key

// ---------------------------------------------------------------------------
// the reported bug
// ---------------------------------------------------------------------------

[<Fact>]
let ``Ctrl+N opens Capture`` () =
    let messages, handled = resolve true false Key.N

    Assert.Equal<Shell.Msg list>([ Shell.OpenCapture ], messages)
    Assert.True(handled, "Ctrl+N must consume the press, or it reaches the page underneath as well")

/// A press that resolves to Capture must resolve to *one* Capture. The window
/// and Panel handlers both exist now, and if the policy ever returned the
/// message twice they would compound rather than cancel.
[<Fact>]
let ``Ctrl+N resolves to exactly one message`` () =
    let messages, _ = resolve true false Key.N
    Assert.Single messages |> ignore

/// Capture must open regardless of which overlays happen to be open — the
/// context only governs Escape.
[<Theory>]
[<InlineData(true, false, false, false)>]
[<InlineData(false, true, false, false)>]
[<InlineData(false, false, true, false)>]
[<InlineData(false, false, false, true)>]
let ``Ctrl+N opens Capture whatever else is open`` (capture, palette, briefing, review) =
    let ctx: Shell.ShortcutContext =
        { CaptureOpen = capture
          PaletteOpen = palette
          BriefingOpen = briefing
          ReviewOpen = review }

    let messages, handled = Shell.resolveShortcut ctx true false Key.N
    Assert.Equal<Shell.Msg list>([ Shell.OpenCapture ], messages)
    Assert.True handled

// ---------------------------------------------------------------------------
// the rest of the policy, pinned unchanged
// ---------------------------------------------------------------------------

/// **Ctrl+F and Ctrl+K stay separate.** They are not merged until the command
/// centre is designed, and this is what stops that happening by accident.
[<Fact>]
let ``Ctrl+F and Ctrl+K remain distinct and unmerged`` () =
    let f, fHandled = resolve true false Key.F
    let k, kHandled = resolve true false Key.K

    Assert.Equal<Shell.Msg list>([ Shell.Navigate Page.Search ], f)
    Assert.Equal<Shell.Msg list>([ Shell.OpenPalette ], k)
    Assert.NotEqual<Shell.Msg list>(f, k)
    Assert.True(fHandled && kHandled)

[<Fact>]
let ``Ctrl+B toggles density and Ctrl+Shift+B is the sidebar reveal aid`` () =
    // Order matters: Ctrl+Shift+B must be tested before Ctrl+B can swallow it.
    Assert.Equal<Shell.Msg list>([ Shell.DevToggleSidebarReveal ], fst (resolve true true Key.B))
    Assert.Equal<Shell.Msg list>([ Shell.ToggleSidebarDensity ], fst (resolve true false Key.B))

[<Fact>]
let ``Ctrl+1 through Ctrl+9 navigate to the first nine sections`` () =
    let digits = [ Key.D1; Key.D2; Key.D3; Key.D4; Key.D5; Key.D6; Key.D7; Key.D8; Key.D9 ]

    digits
    |> List.iteri (fun index key ->
        let messages, handled = resolve true false key

        if index < Page.all.Length then
            let page, _, _ = Page.all.[index]
            Assert.Equal<Shell.Msg list>([ Shell.Navigate page ], messages)
            Assert.True handled
        else
            Assert.Empty messages)

/// An unbound Ctrl combination must do nothing **and** not consume the press —
/// swallowing it would silently break whatever the focused control does with it.
[<Fact>]
let ``an unbound Ctrl combination is neither acted on nor consumed`` () =
    let messages, handled = resolve true false Key.Q
    Assert.Empty messages
    Assert.False(handled, "an unbound shortcut must not consume the key")

/// A plain letter is not a shortcut. If this ever returned a message, typing
/// into any text field would trigger global actions.
[<Theory>]
[<InlineData(Key.N)>]
[<InlineData(Key.F)>]
[<InlineData(Key.K)>]
[<InlineData(Key.B)>]
[<InlineData(Key.D1)>]
let ``a letter without Ctrl is never a shortcut`` (key: Key) =
    let messages, handled = resolve false false key
    Assert.Empty messages
    Assert.False handled

// ---------------------------------------------------------------------------
// Escape
// ---------------------------------------------------------------------------

[<Fact>]
let ``Escape closes only what is actually open`` () =
    Assert.Empty(fst (resolve false false Key.Escape))

    let withCapture = { noOverlays with CaptureOpen = true }
    Assert.Equal<Shell.Msg list>([ Shell.CloseCapture ], fst (Shell.resolveShortcut withCapture false false Key.Escape))

    let withPalette = { noOverlays with PaletteOpen = true }
    Assert.Equal<Shell.Msg list>([ Shell.ClosePalette ], fst (Shell.resolveShortcut withPalette false false Key.Escape))

/// Every open overlay closes on one Escape, in a fixed order, so the behaviour
/// cannot depend on which happened to be opened first.
[<Fact>]
let ``Escape closes every open overlay at once`` () =
    let all: Shell.ShortcutContext =
        { CaptureOpen = true
          PaletteOpen = true
          BriefingOpen = true
          ReviewOpen = true }

    let messages, handled = Shell.resolveShortcut all false false Key.Escape

    Assert.Equal<Shell.Msg list>(
        [ Shell.CloseCapture; Shell.ClosePalette; Shell.CloseBriefing; Shell.CloseReview ],
        messages
    )

    // Escape does not consume: other things may legitimately care about it.
    Assert.False handled
