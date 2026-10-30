namespace UniversityOS.App.Design

open System
open Avalonia.Media

/// The application's typefaces — one place, so no screen hardcodes a font.
///
/// TYPEFACE DECISION (Stage 1, final):
/// The redesign mock-up specified **General Sans** (Indian Type Foundry, served
/// as a webfont by Fontshare). It is NOT adopted. No General Sans file or
/// licence exists in this repository, and a native desktop application must
/// embed and redistribute its font rather than fetch one at runtime — which
/// requires a licence we do not hold and must not assume.
///
/// **Inter** is the chosen application typeface. It ships with Avalonia via the
/// `Avalonia.Fonts.Inter` package (registered by `.WithInterFont()` in
/// Program.fs), is licensed under the **SIL Open Font License 1.1**, and is
/// therefore redistributable inside this application without further permission.
/// Inter is a close match for General Sans: both are geometric-humanist sans
/// faces designed for screen UI at small sizes.
///
/// Attribution: Inter is © The Inter Project Authors, licensed under SIL OFL
/// 1.1. The OFL requires the licence to accompany redistribution; it travels
/// with the NuGet package. It imposes no attribution notice inside the running
/// UI and permits use for any purpose, including commercial.
///
/// To adopt General Sans later: obtain the licence from Fontshare, commit the
/// font file plus its licence text to the repository, register it as an
/// Avalonia embedded font, and change `uiFamilyName` here — nothing else.
[<RequireQualifiedAccess>]
module Typography =

    /// The interface typeface, with platform fallbacks.
    [<Literal>]
    let uiFamilyName = "Inter, Segoe UI, sans-serif"

    /// Fixed-width face for code, extracted text and diagnostics.
    [<Literal>]
    let monoFamilyName = "Cascadia Mono, Consolas, Menlo, monospace"

    let ui = FontFamily uiFamilyName
    let mono = FontFamily monoFamilyName

    /// **Forces a glyph to render as text rather than as a colour emoji.**
    ///
    /// Several of the symbols this application uses as icons — `☑`, `▶`, `◈` and
    /// their neighbours — carry *emoji presentation by default* in Unicode. On
    /// Windows the UI font stack above contains no text glyph for them, so the
    /// system falls back to **Segoe UI Emoji** and paints a full-colour pictograph
    /// instead: a filled rounded square, its own palette, ignoring the foreground
    /// brush entirely.
    ///
    /// That is precisely what was reported in the first runtime review as a
    /// "filled purple assignment glyph". The character was always `☑`; what was
    /// wrong was which font drew it. Appending VARIATION SELECTOR-15 (U+FE0E)
    /// requests the text presentation, so the glyph is drawn monochrome by the UI
    /// face and inherits whatever foreground the caller set.
    ///
    /// It is a zero-width selector: it changes nothing about layout, and it is
    /// ignored by anything reading the string as data.
    [<Literal>]
    let textPresentation = '︎'

    /// A glyph, pinned to its monochrome text form. Idempotent, so it is safe to
    /// apply to a glyph that already carries the selector.
    /// **`StringComparison.Ordinal` is load-bearing here.** `EndsWith(string)`
    /// defaults to a culture-sensitive comparison, and U+FE0E is a zero-width
    /// *ignorable* character under those rules — so `"☑".EndsWith "︎"`
    /// returns **true**, and the naive version of this function silently returned
    /// every glyph unchanged while looking correct.
    let asText (glyph: string) =
        if String.IsNullOrEmpty glyph
           || glyph.EndsWith(string textPresentation, StringComparison.Ordinal) then
            glyph
        else
            glyph + string textPresentation
