module UniversityOS.Tests.SearchFieldTests

open System
open Xunit
open Avalonia.Layout
open Avalonia.Media
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Features
open UniversityOS.Tests.AuditHarness
open UniversityOS.Tests.AuditFixtures

/// **The Search field must be one field, not a field with a field inside it.**
///
/// Reported from the first runtime review: typed text was not vertically
/// centred, and clicking revealed "a smaller textbox inside the larger search
/// box" with only the inner portion editable.
///
/// One trace explained all three symptoms:
///
/// 1. **The inner box.** The editor already set `Background = Transparent` and
///    `BorderThickness = 0`, so it looked right until focused. Fluent's TextBox
///    theme styles the *template part* — `^:focus /template/
///    Border#PART_BorderElement` — rather than the control, so a value set on the
///    TextBox cannot override it. This application maps
///    `TextControlBackgroundFocused` to the **opaque** `Selected` surface
///    (`FluentMaterial.resolve`), so focusing painted a filled rectangle inside
///    the custom field.
/// 2. **The text sat high.** `TextBox.VerticalContentAlignment` defaults to Top
///    and was never set.
/// 3. **The dead space.** The wrapper was `VerticalAlignment.Center`, so the
///    editor occupied only its own text height. Clicking above or below it hit
///    decoration and focused nothing — which is what made the field *look*
///    bigger than the control inside it.
///
/// The fix is `Ui.bareInputClass` (which neutralises the template part in every
/// state, only for controls that opt in) plus stretching the editor and centring
/// its content.
///
/// **What this cannot establish:** that the focused field now looks like one
/// surface. Template parts are applied by a live Avalonia theme, so the *effect*
/// of the class is not decidable headlessly — only that the opt-in is present.
/// That still needs the user's eyes.

let private wide = contentWidth 1440.0

let private searchField () =
    let tree = render "Search" wide

    ofType "TextBox" tree
    |> List.tryFind (fun t -> accessibleName t = Some "Search")
    |> Option.defaultWith (fun () -> failwith "the Search input was not found in the tree")

// ---------------------------------------------------------------------------
// 1. one surface, not two
// ---------------------------------------------------------------------------

/// The editor must ask for no chrome of its own on the control...
[<Fact>]
let ``the Search editor draws no background or border of its own`` () =
    let field = searchField ()

    match propOf "Background" field with
    | Some(:? ISolidColorBrush as b) -> Assert.Equal(0uy, b.Color.A)
    | Some other -> failwithf "the Search editor paints a background: %A" other
    | None -> ()

    match propOf "BorderThickness" field with
    | Some(:? Avalonia.Thickness as t) ->
        Assert.True(t.Top = 0.0 && t.Left = 0.0 && t.Right = 0.0 && t.Bottom = 0.0, "the Search editor draws its own border")
    | _ -> ()

/// ...**and** opt out of the theme's template-part chrome, which is the part the
/// control's own properties cannot reach. Without this the field is correct
/// until the moment it is focused, which is exactly how the defect presented.
[<Fact>]
let ``the Search editor opts out of the Fluent template-part chrome`` () =
    let field = searchField ()

    // FuncUI records `classes` as a plain string sequence, so read it as one
    // rather than as Avalonia's `Classes` collection.
    let classes =
        match propOf "Classes" field with
        | Some(:? System.Collections.IEnumerable as items) ->
            items |> Seq.cast<obj> |> Seq.map string |> List.ofSeq
        | _ -> []

    Assert.True(
        classes |> List.contains (Ui.bareInputClass ()),
        sprintf "the Search editor does not carry the bare-input class; classes = %A" classes
    )

/// The premise, pinned: the focused key really does resolve to an opaque
/// surface, so neutralising the part is necessary rather than defensive. If this
/// ever stopped being true the class could be reconsidered — and this test is
/// what would say so.
[<Theory>]
[<InlineData true>]
[<InlineData false>]
let ``the focused text-control background is opaque, which is why the opt-out is needed`` (dark: bool) =
    let mode = if dark then ThemeMode.Dark else ThemeMode.Light
    let surfaces = Surfaces.resolve mode (Palette.ofKey (fst Palette.accents.Head))

    let focused = FluentMaterial.resolve surfaces "TextControlBackgroundFocused"

    Assert.Equal(255uy, focused.A)

// ---------------------------------------------------------------------------
// 2. the whole field is the target, and the text is centred in it
// ---------------------------------------------------------------------------

[<Fact>]
let ``the Search editor stretches to the field, so the whole field is clickable`` () =
    let field = searchField ()

    match propOf "VerticalAlignment" field with
    | Some(:? VerticalAlignment as a) ->
        Assert.Equal(VerticalAlignment.Stretch, a)
    | _ -> failwith "the Search editor does not state a vertical alignment — it would size to its text and leave dead space"

[<Fact>]
let ``the Search text is vertically centred within the field`` () =
    let field = searchField ()

    match propOf "VerticalContentAlignment" field with
    | Some(:? VerticalAlignment as a) ->
        Assert.Equal(VerticalAlignment.Center, a)
    | _ ->
        failwith "the Search editor does not centre its content — TextBox defaults to Top, which is what put the text high"

// ---------------------------------------------------------------------------
// 3. nothing else moved
// ---------------------------------------------------------------------------

/// Idle and mid-query must have identical outer geometry. A field that resizes
/// as you type is worse than one that sits high.
[<Fact>]
let ``the field declares no fixed height that typing could change`` () =
    for query in [ ""; "ray"; "a very long query that would not fit in the visible field at all" ] do
        let model = { searchModel () with Query = query; Width = wide }
        let tree = SearchView.view tokens model ignore

        let field =
            ofType "TextBox" tree |> List.tryFind (fun t -> accessibleName t = Some "Search")

        Assert.True(field.IsSome, sprintf "the Search input vanished for query %A" query)
        Assert.True((floatProp "Height" field.Value).IsNone, "the Search editor declares a fixed height")

/// The accessible name survives the geometry work — it is what the whole field
/// is called, and losing it would trade one defect for a worse one.
[<Fact>]
let ``the Search editor keeps its accessible name`` () =
    Assert.Equal(Some "Search", accessibleName (searchField ()))
