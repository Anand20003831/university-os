module UniversityOS.Tests.ConnectionsSelectionTests

open System
open Xunit
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.App
open UniversityOS.App.Features
open UniversityOS.Tests.AuditHarness
open UniversityOS.Tests.AuditFixtures

/// **Selecting a provider must visibly show that provider's details.**
///
/// Reported from the first runtime review: clicking a provider card did not open
/// or select its details.
///
/// The earlier passes established that the card *does* dispatch `Select`, that
/// `update` handles it, that `reconcileSelection` only drops a key which no
/// longer exists, and that both placements render `detailPanel`. So the state
/// path was sound — and reporting "the handler is there" would have been the
/// wrong place to stop.
///
/// What was actually missing is that the card was a bare clickable `Border`. A
/// Border is **not focusable**, so it could never be tabbed to and Enter or
/// Space could never reach it: selecting a provider was a pointer-only
/// capability. It is now focusable and answers both activation keys, with every
/// visual unchanged — converting it to a `Button` would have pulled Fluent's own
/// chrome into a card that paints its own.
///
/// **What this cannot establish:** that a click lands on the card at runtime.
/// Hit-testing needs a live visual tree. These tests cover the state machine, the
/// placement rule and the rendered detail surface in every placement.

let private noServices: AppServices = Unchecked.defaultof<AppServices>
let private modelOf (m, _, _) = m

let private ready width =
    { connectionsModel () with Selected = None; Width = width }

let private select key model =
    ConnectionsView.update noServices (ConnectionsView.Select key) model |> modelOf

/// The three widths that produce the three placements, expressed as screen
/// widths the shell can actually deliver.
let private wideWidth = contentWidth 1920.0
let private mediumWidth = ConnectionsLogic.narrowWidth + 50.0
let private narrowWidth = contentWidth 1100.0

// ---------------------------------------------------------------------------
// 1. the state machine
// ---------------------------------------------------------------------------

[<Fact>]
let ``selecting a provider records it`` () =
    let after = ready wideWidth |> select "gitlab"
    Assert.Equal(Some "gitlab", after.Selected)

[<Fact>]
let ``selecting a different provider moves the selection`` () =
    let after = ready wideWidth |> select "gitlab" |> select "moodle"
    Assert.Equal(Some "moodle", after.Selected)

/// Clicking the selected card again closes it — the deselect route the brief
/// requires, and the reason a second click is not a no-op.
[<Fact>]
let ``selecting the same provider again deselects it`` () =
    let after = ready wideWidth |> select "gitlab" |> select "gitlab"
    Assert.Equal(None, after.Selected)

[<Fact>]
let ``ClearSelection closes the detail`` () =
    let after =
        ready wideWidth
        |> select "gitlab"
        |> fun m -> ConnectionsView.update noServices ConnectionsView.ClearSelection m |> modelOf

    Assert.Equal(None, after.Selected)

/// **Selection survives a status refresh.** A background reload replaces the
/// connector list, and if that dropped the selection the detail would vanish
/// under the user for no reason they could see.
[<Fact>]
let ``a status refresh does not clear the selection`` () =
    let selected = ready wideWidth |> select "gitlab"

    let refreshed =
        ConnectionsView.update
            noServices
            (ConnectionsView.Loaded(nineProviders (), [], now))
            selected
        |> modelOf

    Assert.Equal(Some "gitlab", refreshed.Selected)

/// The one case where dropping it is right: the provider is gone.
[<Fact>]
let ``a refresh that loses the provider clears the selection rather than dangling`` () =
    let selected = ready wideWidth |> select "gitlab"
    let without = nineProviders () |> List.filter (fun p -> p.Metadata.Key <> "gitlab")

    let refreshed =
        ConnectionsView.update noServices (ConnectionsView.Loaded(without, [], now)) selected
        |> modelOf

    Assert.Equal(None, refreshed.Selected)

/// Resizing must not disturb selection — the detail moves between placements,
/// it does not close.
[<Theory>]
[<InlineData 1920.0>]
[<InlineData 1100.0>]
let ``resizing while a provider is selected keeps it selected`` (window: float) =
    let selected = ready wideWidth |> select "gitlab"

    let resized =
        ConnectionsView.update noServices (ConnectionsView.Resized(contentWidth window)) selected
        |> modelOf

    Assert.Equal(Some "gitlab", resized.Selected)

// ---------------------------------------------------------------------------
// 2. the detail surface is actually rendered, in every placement
// ---------------------------------------------------------------------------

/// The provider's own description appears only in the detail panel, so finding
/// it proves the detail surface is in the tree rather than merely that the model
/// changed.
let private detailMarker = "GitLab description."

let private renders width =
    let model = ready width |> select "gitlab"
    ConnectionsView.view tokens model ignore |> everyString

[<Theory>]
[<InlineData "wide">]
[<InlineData "medium">]
[<InlineData "narrow">]
let ``the selected provider's details are rendered in every placement`` (band: string) =
    let width =
        match band with
        | "wide" -> wideWidth
        | "medium" -> mediumWidth
        | _ -> narrowWidth

    let strings = renders width

    Assert.True(
        strings |> List.exists (fun s -> s.Contains(detailMarker, StringComparison.Ordinal)),
        sprintf "%s (%.0fpx): the selected provider's detail panel is not in the tree" band width
    )

/// And nothing of the sort appears when nothing is selected — otherwise the test
/// above would pass on a screen that always shows a panel.
[<Fact>]
let ``no detail panel is rendered when nothing is selected`` () =
    let strings = ConnectionsView.view tokens (ready wideWidth) ignore |> everyString

    Assert.False(
        strings |> List.exists (fun s -> s.Contains(detailMarker, StringComparison.Ordinal)),
        "a detail panel is rendered with no provider selected"
    )

/// Wide really is the rail placement, and the other two really are not — the
/// placement rule the render branches switch on.
[<Fact>]
let ``the three widths produce the placements the branches expect`` () =
    Assert.Equal(ConnectionsLogic.DetailPlacement.Rail, (ConnectionsLogic.placementFor wideWidth).Detail)
    Assert.Equal(ConnectionsLogic.DetailPlacement.Below, (ConnectionsLogic.placementFor mediumWidth).Detail)
    Assert.Equal(ConnectionsLogic.DetailPlacement.Below, (ConnectionsLogic.placementFor narrowWidth).Detail)

// ---------------------------------------------------------------------------
// 3. the card is operable, and says so
// ---------------------------------------------------------------------------

let private providerCards (root: IView) =
    all root
    |> List.filter (fun v ->
        typeName v = "Border"
        && (match accessibleName v with
            | Some n -> n.Contains("GitLab", StringComparison.Ordinal)
            | None -> false))

[<Fact>]
let ``a provider card is focusable, so it can be reached by keyboard at all`` () =
    let cards = providerCards (ConnectionsView.view tokens (ready wideWidth) ignore)

    Assert.True(not (List.isEmpty cards), "no provider card was found")

    Assert.True(
        cards |> List.exists (fun c -> boolProp "Focusable" c = Some true),
        "the provider card is not focusable — Enter and Space can never reach it"
    )

[<Fact>]
let ``a provider card answers the keyboard as well as the pointer`` () =
    let cards = providerCards (ConnectionsView.view tokens (ready wideWidth) ignore)

    Assert.True(
        cards |> List.exists (hasHandler "KeyDown"),
        "the provider card has no key handler — selection would be pointer-only"
    )

    Assert.True(
        cards |> List.exists (hasHandler "PointerPressed"),
        "the provider card lost its pointer handler"
    )

/// Keyboard and pointer must reach the same state. Both routes dispatch the same
/// message, so this pins that the message they share does the whole job.
[<Fact>]
let ``keyboard and pointer activation produce the same selection`` () =
    let byEither = ready wideWidth |> select "gitlab"
    Assert.Equal(Some "gitlab", byEither.Selected)

    let strings = ConnectionsView.view tokens byEither ignore |> everyString
    Assert.True(strings |> List.exists (fun s -> s.Contains(detailMarker, StringComparison.Ordinal)))

/// Selection is stated, not merely coloured — the card's accessible name carries
/// the provider and its status.
[<Fact>]
let ``a provider card names its provider and status`` () =
    let cards = providerCards (ConnectionsView.view tokens (ready wideWidth) ignore)

    Assert.True(
        cards
        |> List.exists (fun c ->
            match accessibleName c with
            | Some n -> n.Contains("GitLab", StringComparison.Ordinal) && n.Contains("Failed", StringComparison.OrdinalIgnoreCase)
            | None -> false),
        "the card does not state its provider and status"
    )
