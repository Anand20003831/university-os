namespace UniversityOS.App.Design

open Avalonia.Media

/// A generated accent palette: one chosen hue expanded into every tint, shade
/// and alpha the interface needs. Derived rather than enumerated so a new accent
/// is one hex value, and so the relationships between tints stay consistent.
type Palette =
    { Normal: Color
      Light: Color
      VLight: Color
      Strong: Color
      Dark: Color
      /// Accent at low alpha — chip and pill fills.
      A14: Color
      A1A: Color
      A1F: Color
      /// Strong accent at low alpha — emphasised fills.
      S14: Color
      S22: Color
      /// Soft accent wash and the focus ring.
      Soft: Color
      Ring: Color
      /// Accent alpha ladder used by borders, glows and dividers.
      B10: Color
      B18: Color
      B25: Color
      B30: Color
      B35: Color
      B40: Color
      B45: Color
      B55: Color
      StrongB30: Color }

/// Pure colour maths. No Avalonia service, no rendering — only the Color value
/// struct — so every derivation here is unit-testable without a running app.
[<RequireQualifiedAccess>]
module Palette =

    /// The six accent presets. Keys are what AppearanceSettings persists.
    let accents: (string * string) list =
        [ "amber", "#CB8A4E"
          "blue", "#6FA0B8"
          "purple", "#B98FD1"
          "teal", "#6FBFAE"
          "pink", "#D18FA0"
          "gold", "#D2A24C" ]

    let private clamp01 (v: float) = max 0.0 (min 1.0 v)

    let private toByte (v: float) =
        byte (max 0.0 (min 255.0 (System.Math.Round(v * 255.0))))

    /// Parses "#RRGGBB". Total: anything unrecognised returns None so callers
    /// can fall back rather than throw on a hand-edited settings value.
    let tryParseHex (hex: string) =
        let s = if isNull hex then "" else hex.Trim().TrimStart '#'

        if s.Length <> 6 then
            None
        else
            let ok, value = System.Int32.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture)

            if ok then
                Some(Color.FromRgb(byte ((value >>> 16) &&& 0xFF), byte ((value >>> 8) &&& 0xFF), byte (value &&& 0xFF)))
            else
                None

    let private rgbToHsl (c: Color) =
        let r = float c.R / 255.0
        let g = float c.G / 255.0
        let b = float c.B / 255.0
        let mx = max r (max g b)
        let mn = min r (min g b)
        let l = (mx + mn) / 2.0

        if mx = mn then
            0.0, 0.0, l
        else
            let d = mx - mn
            let s = if l > 0.5 then d / (2.0 - mx - mn) else d / (mx + mn)

            let h =
                if mx = r then (g - b) / d + (if g < b then 6.0 else 0.0)
                elif mx = g then (b - r) / d + 2.0
                else (r - g) / d + 4.0

            h / 6.0, s, l

    let private hslToRgb (h: float, s: float, l: float) =
        if s = 0.0 then
            let v = toByte l
            Color.FromRgb(v, v, v)
        else
            let q = if l < 0.5 then l * (1.0 + s) else l + s - l * s
            let p = 2.0 * l - q

            let channel (t0: float) =
                let t =
                    if t0 < 0.0 then t0 + 1.0
                    elif t0 > 1.0 then t0 - 1.0
                    else t0

                if t < 1.0 / 6.0 then p + (q - p) * 6.0 * t
                elif t < 1.0 / 2.0 then q
                elif t < 2.0 / 3.0 then p + (q - p) * (2.0 / 3.0 - t) * 6.0
                else p

            Color.FromRgb(toByte (channel (h + 1.0 / 3.0)), toByte (channel h), toByte (channel (h - 1.0 / 3.0)))

    /// Same colour, explicit alpha. Alpha is a 0–1 fraction.
    let withAlpha (alpha: float) (c: Color) =
        Color.FromArgb(toByte (clamp01 alpha), c.R, c.G, c.B)

    /// Linear blend between two colours. `amount` 0 keeps `a`, 1 keeps `b`, and
    /// out-of-range amounts clamp rather than wrapping the bytes.
    ///
    /// `a`'s alpha is preserved: the surfaces that carry alpha (borders,
    /// dividers, the modal scrim) are overlays whose transparency is the point,
    /// so a blend must not quietly make them opaque.
    let mix (a: Color) (b: Color) (amount: float) =
        let t = clamp01 amount
        let ch (x: byte) (y: byte) = byte (float x + (float y - float x) * t)
        Color.FromArgb(a.A, ch a.R b.R, ch a.G b.G, ch a.B b.B)

    // ---- readability ---------------------------------------------------------

    /// WCAG relative luminance.
    let relativeLuminance (c: Color) =
        let channel (v: byte) =
            let s = float v / 255.0
            if s <= 0.04045 then s / 12.92 else ((s + 0.055) / 1.055) ** 2.4

        0.2126 * channel c.R + 0.7152 * channel c.G + 0.0722 * channel c.B

    /// WCAG contrast ratio between two opaque colours, 1.0–21.0.
    let contrastRatio (a: Color) (b: Color) =
        let la, lb = relativeLuminance a, relativeLuminance b
        (max la lb + 0.05) / (min la lb + 0.05)

    /// The AA floor for body text.
    [<Literal>]
    let aaText = 4.5

    /// The WCAG 1.4.11 floor for **non-text** visual information: a control
    /// boundary, a state indicator, a meaningful graphic. Lower than `aaText`
    /// because an outline is not a word.
    [<Literal>]
    let aaNonText = 3.0

    /// Composites a colour carrying alpha over an opaque backdrop.
    ///
    /// Needed because almost nothing is drawn straight onto a card. A chip sits
    /// on its own low-alpha wash, a warning glyph sits on a banner, a selected
    /// filter's label sits on an accent veil — and the colour those actually
    /// render as is this, not the surface underneath. Measuring contrast against
    /// the surface instead of the composite is the mistake this exists to stop.
    let composite (backdrop: Color) (overlay: Color) =
        let a = float overlay.A / 255.0
        let ch (f: byte) (b: byte) = toByte ((float f * a + float b * (1.0 - a)) / 255.0)
        Color.FromRgb(ch overlay.R backdrop.R, ch overlay.G backdrop.G, ch overlay.B backdrop.B)

    /// The shared walk: move `c` toward `ink` in twentieths, stopping at the
    /// first step that clears `bar` against `surface`.
    ///
    /// Twenty steps is finer than the eye resolves at these lightnesses, and the
    /// walk is monotonic, so the first pass is the closest pass: the colour is
    /// moved as little as possible. A colour that already clears the bar is
    /// returned untouched, byte for byte.
    let private walkToward (bar: float) (surface: Color) (ink: Color) (c: Color) =
        if contrastRatio c surface >= bar then
            c
        else
            let rec walk step =
                if step >= 20 then
                    ink
                else
                    let candidate = mix c ink (float step / 20.0)

                    if contrastRatio candidate surface >= bar then candidate
                    else walk (step + 1)

            walk 1

    /// The same hue, moved toward `ink` only as far as it has to go to become
    /// readable as text on `surface`.
    ///
    /// **Why this exists.** The identity colours and the six accent presets are
    /// chosen against the dark card, where they measure 5–10:1. Against the
    /// light card the very same colours measure 1.7–3.1:1 — *every one of them
    /// fails*, because a mid-tone colour that glows on charcoal disappears on
    /// parchment. They are not decoration: they are the ink of the module-code
    /// pill, the session-status pill and the SELECTED badge.
    ///
    /// The alternative would be a second hand-tuned palette per theme, which
    /// drifts the moment either side is edited. This keeps ONE palette and walks
    /// the colour along a straight line toward the theme's own ink until it
    /// clears the threshold, so a module keeps its identity and gains its
    /// contrast.
    ///
    /// A colour that already passes is returned untouched — which is every case
    /// in the dark theme, so the approved dark screens render identically.
    let readableOn (surface: Color) (ink: Color) (c: Color) = walkToward aaText surface ink c

    /// The same walk at the **non-text** threshold, for a graphic rather than a
    /// word: a selection outline, a state indicator.
    ///
    /// **Why a selection outline needs this.** An accent against the dark page
    /// measures 6.5–8.7:1 — an unmistakable ring. The very same accent against
    /// the light page measures 1.75–2.33:1, so on Light the selected filter chip
    /// was outlined in something barely distinguishable from the background
    /// while Dark had a bold ring. Walking to 3:1 restores the signal and is a
    /// no-op on Dark, where every accent already clears it by a wide margin —
    /// so the approved dark screens render byte for byte as before.
    let visibleOn (surface: Color) (ink: Color) (c: Color) = walkToward aaNonText surface ink c

    /// Rotates the hue by `degrees`, keeping saturation and lightness.
    ///
    /// Backgrounds need *related* hues, not just tints of one colour — an aurora
    /// built from a single hue reads as a stain, whereas neighbouring hues read
    /// as light. Analogous colours (±20–40°) stay unmistakably "the accent
    /// family" while giving the composition somewhere to move.
    let shiftHue (degrees: float) (c: Color) =
        let h, s, l = rgbToHsl c
        let shifted = (h + degrees / 360.0 + 1.0) % 1.0
        hslToRgb (shifted, s, l)

    /// Expands one accent colour into the full palette. Ported from the
    /// redesign's buildPalette so generated tints match the specification.
    let generate (baseColor: Color) =
        let h, s, l = rgbToHsl baseColor
        let mk sat lum = hslToRgb (h, clamp01 sat, clamp01 lum)

        let normal = baseColor
        let light = mk (s * 0.98) (l + (1.0 - l) * 0.42)
        let vlight = mk (s * 0.88) (l + (1.0 - l) * 0.74)
        let strong = mk (s * 1.12) (l * 1.03)
        let dark = mk (s * 1.05) (l * 0.6)

        { Normal = normal
          Light = light
          VLight = vlight
          Strong = strong
          Dark = dark
          A14 = withAlpha 0.078 normal
          A1A = withAlpha 0.102 normal
          A1F = withAlpha 0.122 normal
          S14 = withAlpha 0.078 strong
          S22 = withAlpha 0.133 strong
          Soft = withAlpha 0.14 normal
          Ring = withAlpha 0.5 light
          B10 = withAlpha 0.10 normal
          B18 = withAlpha 0.18 normal
          B25 = withAlpha 0.25 normal
          B30 = withAlpha 0.30 normal
          B35 = withAlpha 0.35 normal
          B40 = withAlpha 0.40 normal
          B45 = withAlpha 0.45 normal
          B55 = withAlpha 0.55 normal
          StrongB30 = withAlpha 0.30 strong }

    /// Hex for a named preset; unknown keys fall back to the default accent.
    let hexForKey (key: string) =
        accents
        |> List.tryFind (fun (k, _) -> k = key)
        |> Option.map snd
        |> Option.defaultValue (accents |> List.head |> snd)

    /// Palette for a named preset. Total — an unknown key yields the default.
    let ofKey (key: string) =
        match tryParseHex (hexForKey key) with
        | Some c -> generate c
        | None ->
            // Unreachable with the built-in table, but keeps this total rather
            // than relying on the table being well-formed.
            generate (Color.FromRgb(0xCBuy, 0x8Auy, 0x4Euy))
