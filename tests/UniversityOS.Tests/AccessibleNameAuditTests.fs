module UniversityOS.Tests.AccessibleNameAuditTests

open System
open Xunit
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Features
open UniversityOS.Tests.AuditHarness
open UniversityOS.Tests.AuditFixtures

/// **The accessible-name audit**, across all eleven migrated screens.
///
/// The question is coverage, not existence: `Ui.accessibleName` and
/// `Ui.decorative` already exist and are already used on every screen. What has
/// never been checked is whether they are used *everywhere they are needed* —
/// and a glyph button that was missed is invisible to a screen reader no matter
/// how many of its neighbours are named.
///
/// Accessible names are read by **property identity**, never by name.
/// `AutomationProperties.NameProperty` and `StyledElement.NameProperty` are both
/// called `"Name"`; matching on the string would make this audit report the
/// opposite of the truth on any control that sets an element name.
///
/// What this cannot establish: whether a name *reads well* aloud, whether the
/// reading order makes sense, or how any real screen reader behaves. Those need
/// a person and a screen reader.

// ---------------------------------------------------------------------------
// what counts as "icon-only"
// ---------------------------------------------------------------------------

/// A control is icon-only when a sighted user identifies it by a glyph rather
/// than by words. Three or fewer non-space characters is the practical test: it
/// covers emoji, arrows, chevrons, plus/minus, and the multi-codepoint emoji that
/// arrive as surrogate pairs.
let private looksLikeGlyph (text: string) =
    let trimmed = text.Trim()

    trimmed.Length <= 3
    || trimmed |> Seq.forall (fun c -> Char.IsSymbol c || Char.IsPunctuation c || Char.IsSurrogate c || Char.IsWhiteSpace c)

/// Every interactive control that a sighted user reads as a glyph, and that
/// therefore needs a name for everyone else.
///
/// "Icon-only" means **no readable text anywhere inside it**, not merely that the
/// first string happens to be a glyph. The looser reading flagged Graph's entity
/// nodes, which draw a kind glyph *beside* the node's label — a sighted user
/// reads the label, so the control is not icon-only and demanding a name for it
/// would have been a false positive dressed up as a defect.
///
/// Controls hidden from the accessibility tree are excluded; a decorative control
/// that should not be decorative is a separate problem with its own test.
let private readableTexts (view: IView) =
    all view
    |> List.collect (fun v -> [ stringProp "Text" v; stringProp "Content" v ] |> List.choose id)
    |> List.filter (fun t -> not (String.IsNullOrWhiteSpace t) && not (looksLikeGlyph t))

let private iconOnlyControls (root: IView) =
    visibleInteractive root
    |> List.filter (fun v -> not (isDecorative v) && List.isEmpty (readableTexts v))

let private unnamedIconControls (root: IView) =
    iconOnlyControls root
    |> List.filter (fun v -> (accessibleName v).IsNone)
    |> List.map (fun v -> typeName v, (visibleText v |> Option.defaultValue "<no text>"))

let private wide = contentWidth 1440.0

// ---------------------------------------------------------------------------
// 1. icon-only controls are named
// ---------------------------------------------------------------------------

[<Theory>]
[<InlineData "Settings">]
[<InlineData "Connections">]
[<InlineData "Calendar">]
[<InlineData "Search">]
[<InlineData "Assistant">]
[<InlineData "Graph">]
[<InlineData "Documents">]
[<InlineData "Notes">]
[<InlineData "Lectures">]
[<InlineData "Modules">]
[<InlineData "Assignments">]
let ``every icon-only control has an accessible name`` (name: string) =
    let offenders = unnamedIconControls (render name wide)

    Assert.True(
        List.isEmpty offenders,
        sprintf "%s has %d icon-only control(s) with no accessible name: %A" name offenders.Length offenders
    )

/// A name has to say something. `"+"` as an accessible name is no better than
/// the glyph it came from, and a single character is never a description.
[<Theory>]
[<InlineData "Settings">]
[<InlineData "Connections">]
[<InlineData "Calendar">]
[<InlineData "Search">]
[<InlineData "Assistant">]
[<InlineData "Graph">]
[<InlineData "Documents">]
[<InlineData "Notes">]
[<InlineData "Lectures">]
[<InlineData "Modules">]
[<InlineData "Assignments">]
let ``no accessible name is merely the glyph it replaces`` (name: string) =
    let offenders =
        all (render name wide)
        |> List.choose (fun v ->
            match accessibleName v with
            | Some n when looksLikeGlyph n -> Some(typeName v, n)
            | _ -> None)

    Assert.True(
        List.isEmpty offenders,
        sprintf "%s has accessible name(s) that are just a glyph: %A" name offenders
    )

// ---------------------------------------------------------------------------
// 2. decorative glyphs are not announced
// ---------------------------------------------------------------------------

/// Screens draw coloured dots, module swatches and separator glyphs. Announced,
/// they become a list of meaningless single characters between the things that
/// matter. This asserts the screens that carry such decoration mark it.
[<Theory>]
[<InlineData "Connections">]
[<InlineData "Calendar">]
[<InlineData "Graph">]
[<InlineData "Documents">]
[<InlineData "Lectures">]
[<InlineData "Modules">]
[<InlineData "Assignments">]
[<InlineData "Notes">]
let ``screens with decorative glyphs mark them as decorative`` (name: string) =
    let decorated = all (render name wide) |> List.filter isDecorative

    Assert.True(
        not (List.isEmpty decorated),
        sprintf "%s marks nothing as decorative — its status dots and swatches would all be announced" name
    )

// ---------------------------------------------------------------------------
// 3. Connections: identity comes from the provider, never the emoji
// ---------------------------------------------------------------------------

/// The nine providers are drawn with a glyph. Their accessible identity must
/// come from the provider **name** — "Google Drive" — because the glyph carries
/// no meaning to anyone who cannot see it, and "◆" is not a provider.
[<Fact>]
let ``every provider is identifiable by its real name in the accessibility tree`` () =
    let tree = render "Connections" wide
    let strings = everyString tree

    for provider in nineProviders () do
        let providerName = provider.Metadata.Name

        Assert.True(
            strings |> List.exists (fun s -> s.Contains(providerName, StringComparison.Ordinal)),
            sprintf "provider %s is not identifiable by name anywhere in the tree" providerName
        )

/// The complement: the glyph must never be somebody's *whole* identity. If a
/// provider control's accessible name were the icon character, the audit above
/// could still pass on a nearby label while the control itself said "◆".
[<Fact>]
let ``no provider control is named after its icon character`` () =
    let icons = nineProviders () |> List.map (fun p -> p.Metadata.Icon) |> List.distinct

    let offenders =
        all (render "Connections" wide)
        |> List.choose (fun v ->
            match accessibleName v with
            | Some n when icons |> List.exists (fun i -> n.Trim() = i) -> Some n
            | _ -> None)

    Assert.True(List.isEmpty offenders, sprintf "provider control(s) named after an icon glyph: %A" offenders)

// ---------------------------------------------------------------------------
// 4. status is never colour alone
// ---------------------------------------------------------------------------

/// Connection status is the clearest case in the app: four states, each with its
/// own colour. If the colour were the only difference, the screen would be
/// unreadable to anyone who cannot distinguish them — so every status must also
/// appear as text.
[<Fact>]
let ``every connection status is stated in words, not only in colour`` () =
    let strings = everyString (render "Connections" wide)

    let statuses =
        [ ConnectionStatus.NotConfigured; ConnectionStatus.Disconnected; ConnectionStatus.Connected ]

    for status in statuses do
        let label = ConnectionStatus.label status

        Assert.True(
            strings |> List.exists (fun s -> s.Contains(label, StringComparison.OrdinalIgnoreCase)),
            sprintf "status %A has no textual label on the screen — it would be conveyed by colour alone" status
        )

/// A failed connector must say so in words too, and the *reason* must reach the
/// screen — an error the user cannot read is an error they cannot act on.
[<Fact>]
let ``a failed connector states the failure in words`` () =
    let strings = everyString (render "Connections" wide)
    let combined = String.Join(" ", strings)

    Assert.Contains("Failed", combined, StringComparison.OrdinalIgnoreCase)
    Assert.Contains("401", combined, StringComparison.Ordinal)

// ---------------------------------------------------------------------------
// 5. secrets
// ---------------------------------------------------------------------------

/// The invariant is **"nowhere else"**, not "nowhere".
///
/// A credential being typed has to live in the masked box the user is typing it
/// into — that is what an editable password field *is*. What must never happen is
/// the value reaching a label, a watermark, a tooltip, an accessible name, an
/// error message, or any other control.
let private secret = "sk-ant-REVIEWONLY-0123456789abcdef"

let private everyStringOutsideMaskedBoxes (root: IView) =
    all root
    |> List.collect (fun v ->
        let masked = boolProp "PasswordChar" v |> Option.isSome || typeName v = "TextBox" && (propOf "PasswordChar" v).IsSome

        [ (if masked then None else stringProp "Text" v)
          stringProp "Content" v
          stringProp "Watermark" v
          stringProp "ToolTip" v
          accessibleName v ]
        |> List.choose id)

let private typingSecretInto (fieldKey: string) (connectorKey: string) =
    { connectionsModel () with
        Selected = Some connectorKey
        FieldValues = Map.ofList [ fieldKey, secret ]
        Width = wide }

/// **The scenario has to be real before "it did not leak" means anything.**
///
/// Mutation testing caught this: a leak test that renders a tree where the
/// credential never appears at all passes for the wrong reason, and would keep
/// passing while somebody piped the secret straight into a label. So the first
/// assertion is that the credential IS present — in its masked box, where a
/// value being typed belongs — and only then that it is nowhere else.
[<Fact>]
let ``the credential really does reach its masked box, so the leak tests are not vacuous`` () =
    let tree = ConnectionsView.view tokens (typingSecretInto "api_key" "claude") ignore

    let inMaskedBox =
        all tree
        |> List.exists (fun v ->
            (propOf "PasswordChar" v).IsSome
            && (stringProp "Text" v |> Option.map (fun t -> t.Contains(secret, StringComparison.Ordinal)) |> Option.defaultValue false))

    Assert.True(inMaskedBox, "the credential never reaches the masked box — every leak test below would be vacuous")

[<Fact>]
let ``a credential being typed never appears outside its own masked box`` () =
    let leaks =
        ConnectionsView.view tokens (typingSecretInto "api_key" "claude") ignore
        |> everyStringOutsideMaskedBoxes
        |> List.filter (fun s -> s.Contains(secret, StringComparison.Ordinal))

    Assert.True(List.isEmpty leaks, sprintf "the credential leaked outside its masked box into: %A" leaks)

/// Even a fragment is a leak. A "masked" summary that showed the last four
/// characters would pass a whole-value check and still expose the secret.
[<Fact>]
let ``no fragment of a credential reaches the tree`` () =
    let model =
        { connectionsModel () with
            Selected = Some "claude"
            FieldValues = Map.ofList [ "api_key", secret ]
            Width = wide }

    let tail = secret.Substring(secret.Length - 8)
    let head = secret.Substring(0, 12)

    let leaks =
        ConnectionsView.view tokens model ignore
        |> everyStringOutsideMaskedBoxes
        |> List.filter (fun s -> s.Contains(tail, StringComparison.Ordinal) || s.Contains(head, StringComparison.Ordinal))

    Assert.True(List.isEmpty leaks, sprintf "a credential fragment reached: %A" leaks)

/// A provider error is generated by the provider, not by this codebase, so it is
/// the one string that could carry a credential in from outside. The fixture's
/// error is long and realistic; this asserts the screen renders it without any
/// secret riding along.
[<Fact>]
let ``a long provider error is shown in full without carrying a credential`` () =
    let model =
        { connectionsModel () with
            Selected = Some "gitlab"
            FieldValues = Map.ofList [ "token", secret ]
            Width = wide }

    let strings = ConnectionsView.view tokens model ignore |> everyStringOutsideMaskedBoxes

    Assert.True(
        strings |> List.exists (fun s -> s.Contains("401", StringComparison.Ordinal)),
        "the provider error is not shown at all"
    )

    Assert.True(
        strings |> List.forall (fun s -> not (s.Contains(secret, StringComparison.Ordinal))),
        "a credential appears alongside the provider error"
    )

// ---------------------------------------------------------------------------
// 6. form fields
// ---------------------------------------------------------------------------

/// A text box with nothing but a watermark is identifiable only while it is
/// empty — the moment the user types, the cue disappears. Every input therefore
/// needs a name or a real label, not a placeholder standing in for one.
[<Theory>]
[<InlineData "Settings">]
[<InlineData "Connections">]
[<InlineData "Search">]
[<InlineData "Notes">]
let ``every text input is identifiable by something other than its watermark`` (name: string) =
    let offenders =
        ofType "TextBox" (render name wide)
        |> List.filter (fun v -> (accessibleName v).IsNone)
        |> List.map (fun v -> stringProp "Watermark" v |> Option.defaultValue "<no watermark>")

    Assert.True(
        List.isEmpty offenders,
        sprintf "%s has %d text input(s) with no accessible name: %A" name offenders.Length offenders
    )
