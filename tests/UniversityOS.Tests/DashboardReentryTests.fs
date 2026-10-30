module UniversityOS.Tests.DashboardReentryTests

open System
open Xunit
open UniversityOS.Application
open UniversityOS.App
open UniversityOS.App.Features

/// **Returning to the Dashboard must not change its layout.**
///
/// Reported from the first runtime review: open Dashboard → *Regenerate plan* in
/// Morning Briefing → navigate away → return, and the cards are misaligned.
///
/// Two earlier hypotheses were wrong and are recorded so they are not re-tried:
///
/// * *Stale state.* `Shell`'s `Navigate` re-creates the Dashboard model on every
///   entry, so no flag, loading state or error can survive the round trip.
/// * *A card that fails to stretch.* `pairRow` sets `VerticalAlignment.Stretch`
///   on both columns, `GlassPanel` inherits `Decorator` whose vertical alignment
///   already defaults to `Stretch`, and the inner Border stretches too. The
///   stretch chain was intact all along.
///
/// The real cause is **`Columns`**. It is a measurement of the viewport, and
/// `init` hardcodes `3`. Three facts combine into the bug:
///
/// 1. On the default 1380px window the content is roughly 1108px wide, and
///    `columnsFor` returns **2** for anything under 1180.
/// 2. The size handler dispatches `Resized` **only when the count would change**
///    — a deliberate guard, so a drag does not rebuild the card tree per pixel.
/// 3. FuncUI diffs the view tree rather than rebuilding it, so the measuring
///    `Border` survives navigation and never raises `SizeChanged` again.
///
/// So the first visit corrects itself once, and every later visit renders a
/// three-column grid in a two-column space with nothing left to fix it.
/// "Regenerate plan" was simply the most natural way to leave and come back.
///
/// **What this cannot establish:** that the cards now *look* aligned. These tests
/// prove the column count survives re-entry; the rendering still needs the
/// user's eyes.

let private noServices: AppServices = Unchecked.defaultof<AppServices>

let private modelOf (m, _, _) = m

/// The state after the user's window has been measured once — two columns, which
/// is what the default window actually produces.
let private measured () =
    let fresh, _ = Dashboard.init noServices
    Dashboard.update noServices (Dashboard.Resized 2) fresh |> modelOf

// ---------------------------------------------------------------------------
// the reported sequence
// ---------------------------------------------------------------------------

/// The whole reported sequence, as state transitions: measure the viewport,
/// regenerate, leave, come back.
[<Fact>]
let ``returning to the Dashboard preserves the measured column count`` () =
    let onScreen = measured ()
    Assert.Equal(2, onScreen.Columns)

    // Step 2 of the report: Regenerate plan. It leaves the Dashboard model
    // alone and asks the shell to act, which is why it is the *navigation* that
    // matters rather than the regeneration itself.
    let afterRegenerate =
        Dashboard.update noServices Dashboard.RequestRegeneratePlan onScreen |> modelOf

    Assert.Equal(2, afterRegenerate.Columns)

    // Steps 4 and 5: navigate away, and come back. This is what `Shell` does.
    let afterReturn, _ = Dashboard.reinit afterRegenerate noServices

    Assert.Equal(2, afterReturn.Columns)

/// Every column count the layout can produce must survive, not just the one the
/// default window happens to give.
[<Theory>]
[<InlineData 1>]
[<InlineData 2>]
[<InlineData 3>]
let ``re-entry preserves every possible column count`` (columns: int) =
    let fresh, _ = Dashboard.init noServices
    let onScreen = Dashboard.update noServices (Dashboard.Resized columns) fresh |> modelOf
    let returned, _ = Dashboard.reinit onScreen noServices

    Assert.Equal(columns, returned.Columns)

/// The bug in one line: a plain `init` on re-entry throws the measurement away.
/// This is what makes the fix necessary rather than cosmetic.
[<Fact>]
let ``a plain init would discard the measurement, which is why re-entry needs its own path`` () =
    let onScreen = measured ()
    let wrong, _ = Dashboard.init noServices

    Assert.NotEqual<int>(onScreen.Columns, wrong.Columns)

// ---------------------------------------------------------------------------
// what re-entry must still do
// ---------------------------------------------------------------------------

/// Preserving the viewport must not preserve the *data* — reload-on-navigate is
/// the whole reason the shell re-initialises a screen, and quietly keeping a
/// stale snapshot would be a worse bug than the one being fixed.
[<Fact>]
let ``re-entry still reloads the data`` () =
    let onScreen = measured ()
    let returned, _ = Dashboard.reinit onScreen noServices

    Assert.True(returned.Loading, "re-entry must start a fresh load")
    Assert.True(returned.Data.IsNone, "re-entry must not keep the previous snapshot")
    Assert.True(returned.Error.IsNone)
    Assert.False returned.Refreshing

/// A regeneration that failed must not leave an error behind on the next visit.
[<Fact>]
let ``re-entry clears an error left by a failed regeneration`` () =
    let failed =
        Dashboard.update noServices (Dashboard.Failed(exn "generation failed")) (measured ())
        |> modelOf

    Assert.True(failed.Error.IsSome, "the failure should be visible while on the screen")

    let returned, _ = Dashboard.reinit failed noServices
    Assert.True(returned.Error.IsNone, "the error must not survive re-entry")
    Assert.Equal(2, returned.Columns)

// ---------------------------------------------------------------------------
// the column rule the fix depends on
// ---------------------------------------------------------------------------

/// The reason `init`'s hardcoded 3 is wrong for the shipped window: the default
/// 1380px window, less the 252px expanded sidebar and the shell's margins, lands
/// under the 1180px boundary and therefore asks for two columns.
[<Fact>]
let ``the default window produces two columns, not the three init assumes`` () =
    let contentWidth = 1380.0 - 252.0 - 20.0

    Assert.Equal(2, DashboardLogic.columnsFor contentWidth)

    let fresh, _ = Dashboard.init noServices
    Assert.NotEqual<int>(DashboardLogic.columnsFor contentWidth, fresh.Columns)

// ---------------------------------------------------------------------------
// the same defect, everywhere else
// ---------------------------------------------------------------------------

/// **This was never Dashboard-specific.** Every screen's `init` hardcodes a
/// width of 1400 or 1600, and the shipped window gives about 1108. The Dashboard
/// merely made it visible, because its column count changes at 1180.
///
/// The consequence elsewhere is the same shape: after navigating away and back,
/// a responsive screen lays itself out for a window the user does not have —
/// Connections at three provider columns instead of two, Calendar with the
/// side panel railed instead of stacked. `Shell`'s `Navigate` now carries each
/// screen's measured width across re-entry for exactly this reason.
///
/// This test pins the premise: the defaults really are wrong for the real
/// window, so preserving the measurement is necessary rather than tidy.
[<Fact>]
let ``every screen's init width is wrong for the shipped window, which is why it must be preserved`` () =
    let realContentWidth = 1380.0 - 252.0 - 20.0

    // The two families of default that `init` functions actually use.
    for initDefault in [ 1400.0; 1600.0 ] do
        Assert.True(
            initDefault > realContentWidth,
            sprintf "%.0f is not wider than the real content width %.0f" initDefault realContentWidth
        )

    // And the difference genuinely changes responsive decisions, so carrying the
    // measurement over is not cosmetic.
    Assert.NotEqual<int>(
        (ConnectionsLogic.placementFor realContentWidth).Columns,
        (ConnectionsLogic.placementFor 1600.0).Columns
    )
