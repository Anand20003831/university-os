namespace UniversityOS.App

open Avalonia.Media
open UniversityOS.Domain
open UniversityOS.App.Design

/// The design system as views consume it: named brushes over the resolved
/// material palette. Views re-render fully on appearance change (MVU), so
/// module-level current-palette state is safe here.
///
/// **This module owns no colours.** Every value below is a projection of
/// `Design.Surfaces` (the stable warm-neutral material) and `Design.Palette`
/// (the user's accent). The two are kept apart on purpose:
///
///   * a *surface* is material — it stays the same warm charcoal whichever
///     accent is selected, which is what keeps the redesign's identity;
///   * an *accent* is emphasis — the active nav indicator, primary actions,
///     progress, focus rings, selection.
///
/// Binding surfaces to the accent is exactly what turned the Dashboard orange
/// under amber, so the two never mix here beyond `Surfaces.accentHarmony`.
[<RequireQualifiedAccess>]
module Theme =

    /// Legacy view-facing shape, kept so the unmigrated screens keep reading
    /// named roles rather than literals. Every field is filled from `Surfaces`.
    ///
    /// The names are historical; the mapping is documented in `paletteOf` below
    /// because two of them are not the obvious one-to-one.
    type Palette =
        { Window: string
          Sidebar: string
          Content: string
          Surface: string
          SurfaceAlt: string
          Hover: string
          Border: string
          Text: string
          Muted: string
          Faint: string
          Accent: string
          AccentSoft: string
          Success: string
          Warning: string
          Danger: string
          Info: string }

    /// "#RRGGBB", or "#AARRGGBB" when the colour carries transparency.
    ///
    /// Avalonia parses eight hex digits as #AARRGGBB, so alpha leads. Emitting
    /// six digits for opaque colours keeps every existing string comparison and
    /// every `withAlphaHex` caller working unchanged.
    let hexOfColor (c: Color) =
        if c.A = 255uy then sprintf "#%02X%02X%02X" c.R c.G c.B
        else sprintf "#%02X%02X%02X%02X" c.A c.R c.G c.B

    /// Projects the material palette and the active accent into the view-facing
    /// roles.
    ///
    /// Two mappings are deliberately not literal:
    ///   `SurfaceAlt` → `Hover`, and `Hover` → `Selected`. `SurfaceAlt` means
    ///   "one visible step off the card" at every call site (skeletons, progress
    ///   tracks, code blocks, table headers, the toast), and the material's
    ///   `CardAlt` is a *recessed* card, which would make those invisible.
    let paletteOf (s: Surfaces) (accent: Design.Palette) =
        { Window = hexOfColor s.Window
          Sidebar = hexOfColor s.SidebarPinned
          Content = hexOfColor s.Shell
          Surface = hexOfColor s.Card
          SurfaceAlt = hexOfColor s.Hover
          Hover = hexOfColor s.Selected
          Border = hexOfColor s.Border
          Text = hexOfColor s.Text
          Muted = hexOfColor s.Muted
          Faint = hexOfColor s.Faint
          Accent = hexOfColor accent.Normal
          AccentSoft = hexOfColor accent.Soft
          Success = hexOfColor s.Success
          Warning = hexOfColor s.Warning
          Danger = hexOfColor s.Danger
          Info = hexOfColor s.Info }

    /// The default accent, used before appearance has loaded.
    let private defaultAccent = Design.Palette.ofKey "amber"

    let dark = paletteOf Surfaces.dark defaultAccent
    let light = paletteOf Surfaces.light defaultAccent

    let mutable private mode = ThemeMode.Dark
    let mutable private activeAccent = defaultAccent
    let mutable private material = Surfaces.resolve ThemeMode.Dark defaultAccent
    let mutable private current = paletteOf material defaultAccent

    /// True when the OS composites real transparency behind the window (Mica /
    /// acrylic). When on, the outer layers carry alpha so the desktop tint bleeds
    /// through; when the OS declines, the same alpha composites over an opaque
    /// fallback surface — slightly darker, never broken.
    let mutable private glass = false

    let private rebuild () =
        material <- Surfaces.resolve mode activeAccent
        current <- paletteOf material activeAccent

    let setMode (newMode: ThemeMode) =
        mode <- newMode
        rebuild ()

    /// The active accent. Set alongside the theme by the shell's single
    /// appearance writer, so `accentHex` below is the swatch the user picked
    /// rather than a fixed brand colour.
    let setAccent (newAccent: Design.Palette) =
        activeAccent <- newAccent
        rebuild ()

    /// The resolved material palette. Migrated screens read semantic roles from
    /// here instead of mixing surface colours of their own.
    let surfaces () = material

    let setGlass (enabled: bool) = glass <- enabled

    let brush (hex: string) : IBrush = SolidColorBrush(Color.Parse hex) :> IBrush

    /// Applies an alpha to a "#RRGGBB" colour.
    ///
    /// Avalonia parses eight hex digits as **#AARRGGBB**, so the alpha goes at
    /// the FRONT. Appending it instead — `hex + "14"` — silently reinterprets
    /// the whole colour: `#7C6FF0` + `14` becomes `#7C6FF014`, which is a bright
    /// green at 48% alpha, not a faint purple. That is exactly how green and
    /// purple ended up in the Dashboard's progress dials.
    let withAlphaHex (alphaHex: string) (hex: string) =
        "#" + alphaHex + hex.TrimStart('#')

    let private withAlpha = withAlphaHex

    let window () =
        if glass then brush (withAlpha "E0" current.Window) else brush current.Window

    let sidebar () =
        if glass then brush (withAlpha "C8" current.Sidebar) else brush current.Sidebar

    let content () = brush current.Content

    /// Legacy alias: the general page background is the content surface.
    let bg () = content ()

    let surface () = brush current.Surface
    let surfaceAlt () = brush current.SurfaceAlt
    let hover () = brush current.Hover
    let border () = brush current.Border
    let text () = brush current.Text
    let muted () = brush current.Muted
    let faint () = brush current.Faint
    let accent () = brush current.Accent
    let accentSoft () = brush current.AccentSoft
    let success () = brush current.Success
    let warning () = brush current.Warning
    let danger () = brush current.Danger
    let info () = brush current.Info
    let transparent = Brushes.Transparent :> IBrush

    // ---- material roles the legacy Palette shape has no field for -----------
    //
    // Exposed as brushes so the shell reads a semantic role rather than a
    // literal, without widening the legacy record every unmigrated screen
    // still destructures.

    let cardAlt () = brush (hexOfColor material.CardAlt)
    let action () = brush (hexOfColor material.Action)
    let selected () = brush (hexOfColor material.Selected)
    let divider () = brush (hexOfColor material.Divider)
    /// An elevated popover, menu or modal panel.
    let overlay () = brush (hexOfColor material.Overlay)
    /// The dimming layer behind a modal.
    let scrim () = brush (hexOfColor material.Scrim)
    /// Ink on an accent fill.
    let onAccent () = brush (hexOfColor material.OnAccent)
    let textSecondary () = brush (hexOfColor material.TextSecondary)
    /// Uppercase section labels — one step above `faint`.
    let label () = brush (hexOfColor material.Label)

    /// Hex accessors for places that need the raw color (e.g. mixing, or a
    /// Skia tint that has to be a Color rather than a Brush).
    /// The accent the USER selected, not a fixed brand colour. Every screen
    /// that emphasises with `accentHex` therefore follows the swatch.
    /// A detail colour, made readable as TEXT on the current theme's card.
    ///
    /// The identity colours and the accent presets are tuned against the dark
    /// card and measure 1.7–3.1:1 on the light one — see `Palette.readableOn`.
    /// Anything that writes a *word* in one of those colours goes through here
    /// first: the module-code pill, the session-status pill, the SELECTED badge.
    ///
    /// In the dark theme every one of them already clears the threshold, so this
    /// returns them unchanged and the approved dark screens are untouched.
    let readableHex (hex: string) =
        Design.Palette.readableOn material.Card material.Text (Color.Parse hex)
        |> hexOfColor

    /// `readableHex`, but measured against the surface the text is **actually**
    /// drawn on rather than against the card.
    ///
    /// **Why this is a different function.** `readableHex` walks a colour until
    /// it clears AA against the CARD. Almost nothing is drawn straight onto the
    /// card: a chip sits on its own wash — the same hue at 12% over the card —
    /// a warning glyph sits on a banner, a selected filter's label sits on an
    /// accent veil. Each of those is darker than the card on a light theme, so
    /// the walk stopped one or two steps early and every chip on the screen
    /// landed at 4.15–4.44:1. On a SELECTED card, a further step darker again,
    /// the same inks measured 3.4–3.6:1.
    ///
    /// Passing the real background fixes all of it. On Dark it is almost always
    /// a no-op — the identity colours and every accent clear AA on their own
    /// washes there already — so it moves only the handful of dark cases that
    /// were themselves below the floor.
    ///
    /// `readableHex` is deliberately left exactly as it was, so `Ui.pill` and
    /// every screen already approved against it are untouched.
    let readableOnColor (background: Color) (hex: string) =
        Design.Palette.readableOn background material.Text (Color.Parse hex)
        |> hexOfColor

    /// A non-text graphic — a selection outline, a state ring — made visible on
    /// `background` at the WCAG 1.4.11 threshold. A no-op wherever it already
    /// clears 3:1, which on Dark is everywhere.
    let visibleOnColor (background: Color) (hex: string) =
        Design.Palette.visibleOn background material.Text (Color.Parse hex)
        |> hexOfColor

    /// The opaque colour an "#AARRGGBB" overlay will actually render as over
    /// `backdrop`, so contrast can be measured against what the eye sees.
    let compositeOver (backdrop: Color) (overlayHex: string) =
        Design.Palette.composite backdrop (Color.Parse overlayHex)

    /// Scales a translucent edge's alpha by the theme's own edge weight, so a
    /// coloured hairline reads with the same presence in both themes. See
    /// `Surfaces.overlayEdgeScale` for the measurements behind the constant.
    let overlayEdgeHex (alphaHex: string) (hex: string) =
        let raw =
            System.Int32.Parse(alphaHex, System.Globalization.NumberStyles.HexNumber)

        let scaled =
            int (System.Math.Round(float raw * Surfaces.overlayEdgeScale mode)) |> min 255 |> max 0

        withAlphaHex (sprintf "%02X" scaled) hex

    let accentHex () = current.Accent

    /// The accent's soft wash, as the "#AARRGGBB" it is drawn with — so a caller
    /// can composite it over its own surface and measure a label against the
    /// result rather than against the surface underneath.
    let accentSoftHex () = current.AccentSoft
    let dangerHex () = current.Danger
    let successHex () = current.Success
    let warningHex () = current.Warning
    let infoHex () = current.Info

    // Typography scale.
    let fontXs = 11.0
    let fontSm = 12.5
    let fontBase = 14.0
    let fontMd = 15.5
    let fontLg = 18.0
    let fontXl = 24.0
    let fontTitle = 30.0

    /// Semantic roles for full-page screens.
    ///
    /// The base scale above was built for dense list screens and tops out at
    /// 30px, which reproduced the redesign at roughly two-thirds size — every
    /// heading, metric and label came out cramped. These are the roles a
    /// migrated screen composes with, so sizes are shared and named rather than
    /// picked per control.
    [<RequireQualifiedAccess>]
    module Type =
        /// The one page heading. "Good morning, Maya".
        let pageTitle = 34.0
        /// Date / academic context directly under the heading.
        let pageMeta = 15.0
        /// The accent-coloured insight line under that.
        let pageLead = 15.5
        /// A card's uppercase section label.
        let cardLabel = 11.5
        /// The headline inside a card — a recommended task, a card title.
        let cardTitle = 21.0
        /// Body copy inside a card.
        let cardBody = 14.5
        /// A large metric value.
        let metric = 22.0
        /// The label under a metric.
        let metricLabel = 12.0
        /// A timeline row's event title.
        let rowTitle = 15.0
        /// A timeline row's supporting detail and timestamps.
        let rowMeta = 12.5
        /// Quick-action card label.
        let actionLabel = 14.0

    /// Priority → semantic color.
    let priorityBrush (p: Priority) =
        match p with
        | Priority.Low -> muted ()
        | Priority.Medium -> info ()
        | Priority.High -> warning ()
        | Priority.Critical -> danger ()

    /// Identity hues: the redesign's own accent family, at the muted, slightly
    /// desaturated pitch it uses throughout.
    ///
    /// These are neither material nor the user's accent — they are a third,
    /// fixed set, because an entity must look the same everywhere it appears and
    /// must not change when the user picks a different swatch. They were
    /// previously the cool primaries (`#4FA8E8`, `#7C6FF0`, `#4FC98F`), which is
    /// the last place the blue-grey character was still visible; every value
    /// below appears verbatim in the redesign.
    [<RequireQualifiedAccess>]
    module Identity =
        let violet = "#B98FD1"
        let amber = "#CB8A4E"
        let sky = "#8FB9CC"
        let green = "#7FAE82"
        let orchid = "#CFA8E0"
        let teal = "#6FD6C9"
        let terracotta = "#D3765F"
        let gold = "#D2A24C"
        let rose = "#D18FA0"
        /// The neutral for kinds with no identity of their own.
        let neutral = "#A79A87"

    /// Entity kind → identity color shared by search results and knowledge-graph
    /// nodes so an entity looks the same everywhere it appears.
    let entityKindHex (kind: EntityKind) =
        match kind with
        | EntityKind.Module -> Identity.violet
        | EntityKind.Assignment -> Identity.amber
        | EntityKind.Event -> Identity.sky
        | EntityKind.Note -> Identity.green
        | EntityKind.Document -> Identity.orchid
        | EntityKind.Lecture -> Identity.teal
        | EntityKind.InboxItem -> Identity.terracotta
        | EntityKind.Session -> Identity.gold
        | EntityKind.Conversation -> Identity.rose
        | EntityKind.RevisionItem -> Identity.neutral

    /// Event kind → identity color used across calendar, dashboard and planner.
    let eventKindHex (kind: EventKind) =
        match kind with
        | EventKind.Lecture -> Identity.sky
        | EventKind.Deadline -> Identity.terracotta
        | EventKind.Meeting -> Identity.orchid
        | EventKind.Personal -> Identity.green
        | EventKind.StudySession -> Identity.violet
        | EventKind.Exam -> Identity.gold
        | EventKind.Other -> Identity.neutral
