namespace UniversityOS.App.Design

open Avalonia.Media
open UniversityOS.Domain

/// The application's MATERIAL palette: the surfaces the interface is built out
/// of, and the ink written on them.
///
/// This is deliberately a *separate* palette from `Design.Palette`, and the
/// distinction is the whole point of this module:
///
///   * **material** (here) — the redesign's own identity. Deep warm charcoal,
///     brown-charcoal cards, restrained warm borders. Stable: it is very nearly
///     the same whichever accent the user picks, so the application still looks
///     like itself under amber, blue, purple, teal, pink or gold.
///
///   * **accent** (`Design.Palette`) — the user's chosen emphasis colour. It
///     drives details that communicate state: the active navigation indicator,
///     primary actions, progress arcs, focus rings, selected controls.
///
/// The previous Dashboard correction blended every card 34% toward the accent's
/// dark shade. That fixed a blue-grey cast by making the surface itself take the
/// accent's hue, so amber turned the whole page orange. A surface is not a state
/// indicator; it must not carry the accent as its fill.
///
/// Every value below is taken from the redesign, which is the source of truth
/// for the interface — see the comment on each field for the markup it comes
/// from. Nothing here is derived from the accent.
type Surfaces =
    { /// The window base, behind the animated background.
      Window: Color
      /// Shell chrome: the content base and inset control tracks.
      Shell: Color
      /// The docked sidebar's opaque fill.
      SidebarPinned: Color
      /// The floating sidebar's tint. Same family as the pinned fill — the
      /// GlassPanel supplies the transparency, so this stays an opaque RGB.
      SidebarFloating: Color
      /// A primary content card.
      Card: Color
      /// A secondary / recessed content card.
      CardAlt: Color
      /// A quick-action glass card.
      Action: Color
      /// A subtle warm lift under the pointer.
      Hover: Color
      /// A neutral selected surface (accent-derived selection is separate).
      Selected: Color
      /// An elevated popover, menu or modal panel.
      Overlay: Color
      /// The hairline around a surface. Carries alpha.
      Border: Color
      /// A quieter divider inside a surface. Carries alpha.
      Divider: Color
      /// The dimming layer behind a modal. Carries alpha.
      Scrim: Color
      /// Primary ink.
      Text: Color
      /// Ink one step down: values in dense rows.
      TextSecondary: Color
      /// Supporting copy.
      Muted: Color
      /// Uppercase section labels.
      Label: Color
      /// The smallest meta text.
      Faint: Color
      /// Ink placed ON an accent fill — never white, which vibrates on a
      /// mid-tone accent.
      OnAccent: Color
      Success: Color
      Warning: Color
      Danger: Color
      Info: Color }

[<RequireQualifiedAccess>]
module Surfaces =

    let private c (hex: string) = Color.Parse hex

    /// Dark: the redesign as drawn.
    ///
    /// Sources, from `University OS Redesign.dc.html`:
    ///   Window          `background:#100D09` on the 100vw/100vh root
    ///   Shell           `background:#17140F` segmented-control tracks / base
    ///   SidebarPinned   `panelBg: s.pinned ? '#1B1711' : …`
    ///   SidebarFloating `… : 'rgba(27,23,17,0.74)'` — same RGB, glass alpha
    ///   Card            `background:rgba(34,29,22,0.55)` + blur + hairline
    ///   CardAlt         `linear-gradient(…,rgba(27,23,16,0.5))` card gradient end
    ///   Action          quick-action `background:#1B1710`
    ///   Hover           `background:#2A2318` chips and progress tracks
    ///   Overlay         popover `min-width:220px;background:#241E17`
    ///   Border/Divider  `1px solid rgba(255,255,255,0.08 / 0.06)`
    ///   Scrim           modal backdrop `rgba(10,8,5,0.6)`
    ///   Text ladder     `#F2ECE2 → #D8CFC1 → #A79A87 → #8a7d6b → #6E6353`
    ///   OnAccent        `background:var(--accent,#CB8A4E);color:#1B1710`
    ///
    /// Two deliberate departures from the literals, both documented rather than
    /// silent: `Selected` has no counterpart in the redesign (its selection is
    /// accent-only) so it is one restrained step above `Hover`; and the bottom
    /// two ink steps are lifted from `#8a7d6b`/`#6E6353`, which measure 4.1:1
    /// and 2.9:1 on the card and would fail WCAG AA at the sizes they are used.
    let dark =
        { Window = c "#100D09"
          Shell = c "#17140F"
          SidebarPinned = c "#1B1711"
          SidebarFloating = c "#1B1711"
          Card = c "#221D16"
          CardAlt = c "#1D1912"
          Action = c "#1B1710"
          Hover = c "#2A2318"
          Selected = c "#322A1E"
          Overlay = c "#241E17"
          Border = c "#14FFFFFF"
          Divider = c "#0FFFFFFF"
          Scrim = c "#99100D09"
          Text = c "#F2ECE2"
          TextSecondary = c "#D8CFC1"
          Muted = c "#B0A491"
          Label = c "#A79A87"
          Faint = c "#9C8F7B"
          OnAccent = c "#1B1710"
          Success = c "#7FAE82"
          Warning = c "#D2A24C"
          Danger = c "#D3765F"
          Info = c "#8FB9CC" }

    /// Light: the same design system, inverted through warm parchment rather
    /// than cold white.
    ///
    /// The redesign offers a Light option in Settings but never renders one, so
    /// these are derived rather than transcribed. The rule followed is that
    /// every light surface keeps the dark palette's hue (a warm brown around
    /// 33°) and only trades lightness, so the two themes are recognisably one
    /// family. Saturation is kept low so nothing reads as beige or yellow.
    let light =
        { Window = c "#EDE7DC"
          Shell = c "#F3EEE4"
          SidebarPinned = c "#F7F2E9"
          SidebarFloating = c "#FBF7F0"
          Card = c "#FDFAF4"
          CardAlt = c "#F5EFE5"
          Action = c "#FBF6EE"
          Hover = c "#EFE8DC"
          Selected = c "#E7DFD1"
          Overlay = c "#FFFDF8"
          Border = c "#1C594B36"
          Divider = c "#12594B36"
          Scrim = c "#66332A1E"
          Text = c "#241E17"
          TextSecondary = c "#3E3527"
          Muted = c "#5C513F"
          Label = c "#6E6250"
          Faint = c "#786C5A"
          OnAccent = c "#1B1710"
          // The status inks are darker than a naive lightening of the Dark
          // family would give, because they are read as BODY TEXT on the light
          // card — a failed constraint, a risk level, a warning row — and the
          // 4.5:1 floor applies to all of them.
          //
          // Measured on `Card #FDFAF4`, the values these replace were:
          //   Success #4E7F53 4.49:1, Warning #9A7526 4.08:1, Info #4C7B90 4.44:1
          // all of which fail AA. Each keeps its hue to within a degree and only
          // trades lightness, so the family is unchanged and only the contrast
          // moves. Danger was already 5.11:1 and is untouched.
          Success = c "#47734B" // 5.27:1
          Warning = c "#8A6821" // 4.94:1
          Danger = c "#A8523E" // 5.11:1, unchanged
          Info = c "#456F82" } // 5.24:1

    /// How far a material surface is allowed to move toward the active accent.
    ///
    /// Not zero: at exactly zero the cards sit inertly against strongly coloured
    /// linework under the cool accents. Six per cent is enough to harmonise and
    /// far too little to recolour — on the card it moves each channel by at most
    /// a handful of 0–255 steps, which `SurfaceTests` pins numerically.
    ///
    /// This is the ONLY accent influence on any surface. It replaced a 0.34
    /// blend, which is where the orange wash came from.
    [<Literal>]
    let accentHarmony = 0.06

    /// Per-theme weight for a **translucent coloured edge** — the hairline round
    /// a conflict banner or a confirmation prompt.
    ///
    /// The same asymmetry `Glass.hairlineAlphaScale` exists for, in a different
    /// place: a light ink at low alpha over a dark surface separates further
    /// than a dark ink at the same alpha over a light one. Measured on the
    /// approved Dark theme against its Light counterpart at identical alpha:
    ///
    /// | edge | dark | light @1.0 | light @1.5 |
    /// |---|---|---|---|
    /// | conflict warning (`4D` on the card) | 1.82:1 | 1.49:1 | 1.85:1 |
    /// | conflict danger (`4D` on the card) | 1.60:1 | 1.52:1 | 1.92:1 |
    /// | confirmation prompt (`55` on the page) | 1.93:1 | 1.51:1 | 1.88:1 |
    ///
    /// 1.5 lands Light on the weight Dark was approved with, so the two themes
    /// read as one design system rather than one having crisp banner edges and
    /// the other having smudges. Dark is 1.0, so nothing there moves at all.
    let overlayEdgeScale (mode: ThemeMode) =
        match mode with
        | ThemeMode.Light -> 1.5
        | ThemeMode.Dark -> 1.0

    /// Which end of the accent a surface harmonises toward. Dark surfaces move
    /// toward the accent's dark shade and light surfaces toward its palest, so
    /// harmonisation never fights the theme's own lightness.
    let harmonyTarget (mode: ThemeMode) (accent: Palette) =
        match mode with
        | ThemeMode.Light -> accent.VLight
        | ThemeMode.Dark -> accent.Dark

    /// Applies the harmonisation to the material roles only.
    ///
    /// Ink is never harmonised — text tinted toward the accent is a legibility
    /// problem, not a design one — and neither are the border, divider or scrim,
    /// which are alpha overlays whose colour comes from what is behind them.
    let harmonise (mode: ThemeMode) (accent: Palette) (s: Surfaces) =
        let t = harmonyTarget mode accent
        let h (col: Color) = Palette.mix col t accentHarmony

        { s with
            Window = h s.Window
            Shell = h s.Shell
            SidebarPinned = h s.SidebarPinned
            SidebarFloating = h s.SidebarFloating
            Card = h s.Card
            CardAlt = h s.CardAlt
            Action = h s.Action
            Hover = h s.Hover
            Selected = h s.Selected
            Overlay = h s.Overlay }

    /// The unharmonised base family for a theme.
    let baseFor (mode: ThemeMode) =
        match mode with
        | ThemeMode.Light -> light
        | ThemeMode.Dark -> dark

    /// The material palette as the interface actually consumes it: the stable
    /// base family for the theme, harmonised to the active accent by the very
    /// small amount above. One function, one answer — the shell, the sidebar
    /// and every migrated screen read the result rather than mixing their own.
    let resolve (mode: ThemeMode) (accent: Palette) =
        baseFor mode |> harmonise mode accent
