# University OS — Presentation Layer Technical Design

**Status:** APPROVED — authoritative specification for the presentation layer.
**Revision:** 2 (incorporates the retirement policy, rendering principles, three-tier fallback,
master clock, adaptive quality, diagnostics, memory-pressure handling, settings versioning,
tolerance-based golden images, error recovery, the native-desktop mandate, and the required
repository end-state).
**Date:** 2026-07-24
**Framework:** Avalonia 11.3.2 + Avalonia.FuncUI 1.5.1 (Elmish MVU), SkiaSharp 2.88.9
**Companion documents:** `docs/ARCHITECTURE.md` (backend)

This document is detailed enough to be implemented by an engineer who has not seen the redesign.
Where it states a rule, that rule is binding. Where a decision was contested, the rejected
alternative is recorded so it is not silently revisited.

---

## 0. Verified platform capabilities

Every capability below was verified against the packages resolved on this machine, not assumed.

| API | Assembly | Purpose here |
|---|---|---|
| `ICustomDrawOperation`, `ImmediateDrawingContext` | Avalonia.Base | Inject custom rendering into the scene graph |
| `ISkiaSharpApiLeaseFeature` → `ISkiaSharpApiLease` | Avalonia.Skia | Obtain the raw `SKCanvas` / `GRContext` |
| `SKRuntimeEffect` (SKSL), `SKShader`, `SKImageFilter`, `SKSurface`, `SKPicture` | SkiaSharp 2.88.9 | Shaders, blur, offscreen targets, recorded drawing |
| `GRContext.GetResourceCacheUsage` / `SetResourceCacheLimit` / `PurgeResources` / `PurgeUnusedResources` | SkiaSharp | GPU memory diagnostics and pressure release |
| `TopLevel.RequestAnimationFrame(Action<TimeSpan>)` | Avalonia.Controls | Vsync-aligned frame callback — the master clock |
| `Compositor`, `ElementComposition`, `ImplicitAnimation`, `ExpressionAnimation` | Avalonia.Base | Composition-level animation |
| `Animation`, `Transitions`, `Easing` | Avalonia.Base | Declarative property animation |
| `ConicGradientBrush`, `RadialGradientBrush`, `BoxShadows`, `PathGeometry`, `OpacityMask` | Avalonia.Base | Rings, aurora, glass edges, facet clipping |
| `BlurEffect`, `DropShadowEffect` (`Visual.Effect`) | Avalonia.Base | Per-element effects |
| `ExperimentalAcrylicBorder` / `ExperimentalAcrylicMaterial` | Avalonia.Controls | Tier C glass fallback |
| `RenderTargetBitmap` | Avalonia.Base | Golden-image tests |
| `Avalonia.Headless.XUnit` 12.1.0 | (to add) | Headless UI tests on the existing xUnit 2.9 setup |

**Not available:** any backdrop API that blurs arbitrary content behind an element. §10 addresses
this directly; it is the single genuine gap and the design works around it rather than pretending
otherwise.

---

## 1. The native desktop mandate (binding constraint)

University OS is and will remain a **true native desktop application**. The presentation layer is
entirely compiled .NET rendering through Avalonia's scene graph to Skia to the GPU.

The following are **prohibited in the presentation layer, permanently and without exception**:

- **No WebView** of any kind — WebView2, WKWebView, WebKitGTK.
- **No embedded browser engine** — Chromium, CEF, Gecko, Blink, WebKit.
- **No HTML, CSS or JavaScript runtime** as any part of rendering, layout or styling.
- **No Electron-, Tauri-, Photino- or CEF-style architecture.**
- **No HTML/CSS/JS assets** shipped as presentation resources.
- **No remote-rendered or server-rendered UI.**

**If a redesign feature cannot be reproduced exactly, it is recreated with native rendering
techniques — custom Skia drawing, shaders, geometry, composition — or it is adapted. Introducing
web technology to close a fidelity gap is never an acceptable answer.**

This constraint outranks visual fidelity. A feature that can only be achieved with web technology
is redesigned natively or dropped, and the decision is recorded in this document.

**Rationale.** The value of this application is a single, coherent, in-process .NET system: one
language, one toolchain, one debugger, one deployment artifact, no serialisation boundary, no
second runtime, no browser security surface, and direct typed access to the F# backend. That
property is worth more than the last few percent of visual fidelity, and once a web runtime is
admitted it is effectively permanent.

---

## 2. Rendering Principles

These are the project's rendering philosophy. Every implementation decision, code review and future
extension is measured against them. They are listed in priority order where they conflict.

1. **Never allocate on the render thread.** No `new`, no LINQ, no closures, no boxing inside
   `RenderSkia`. Paints, shaders and buffers come from caches built on the UI thread. Allocation on
   the frame path causes GC pauses that present as stutter.
   *One measured, bounded exception exists on the Tier A shader path — see §11.3.*
2. **Never animate layout when a transform or opacity achieves the same effect.** Animating
   `Width`, `Height`, `Margin` or `Padding` invalidates measure/arrange for the subtree every frame.
   Animate `RenderTransform` and `Opacity`, which are composited.
3. **Never duplicate a rendering path.** One way to draw glass. One way to draw the background. One
   way to draw a progress track. If a second way appears, one of them is wrong and must be deleted,
   not kept alongside.
4. **Prefer one GPU draw over many UI elements.** Forty animated visuals that could be one shader
   pass is a defect, not a style choice.
5. **Decorative rendering belongs in custom rendering.** Backgrounds, glass, gauges, sparklines,
   graph surfaces — anything non-interactive and visually rich — is custom-drawn.
6. **Interactive elements remain native Avalonia controls** unless there is a compelling, written
   technical justification. Buttons, inputs, list rows and navigation items are real controls, so
   focus, keyboard, IME, text selection and screen readers work by default. §15 defines the two
   sanctioned exceptions.
7. **Maintain one source of truth for every visual component.** One `GlassPanel`. One token record.
   One accent-palette generator. One easing curve definition. Duplication of visual truth is how
   design systems rot.
8. **Prefer composition over rebuilding.** Compose existing controls and painters before writing a
   new one. A new custom control requires justifying why composition is insufficient.
9. **Remove obsolete code immediately after replacement.** Deletion is part of the change that
   replaced it, not a follow-up task (§26).
10. **Rendering degrades gracefully; it never fails.** Every advanced path has a fallback, every
    fallback is tested, and no rendering failure may crash, blank, or hang the application (§22).

---

## 3. Overall presentation architecture

### 3.1 Shape

Elmish MVU is retained. Each screen is a module exposing `Model` / `Msg` / `init` / `update` /
`view`, composed by `Shell.fs`, as today. The project separates *pure design logic* from
*rendering* from *composition*:

```
src/UniversityOS.App/
  Design/
    Palette.fs        Accent palette generation (pure HSL math)
    Tokens.fs         The token record + derived values (pure)
    Motion.fs         Duration/easing derivation from tokens (pure)
    Theme.fs          Semantic colour roles; entity/event/priority identity colours (pure)
    BackgroundParams.fs  Background maths: easing curve, caps, adaptive mapping (pure)
    TokenStore.fs     Observable current-token holder (the only mutable design state)
  Rendering/
    Capabilities.fs   Startup probe; resolved render tier; demotion on failure
    FrameClock.fs     THE master animation clock                         §7
    SkiaControl.fs    Base control: ICustomDrawOperation plumbing + Skia lease
    ShaderLibrary.fs  SKSL sources; pre-warmed compiled-effect cache     §11
    BackdropCache.fs  Blurred-background texture used by glass           §10
    QualityController.fs  Adaptive quality level from measured frame time §9
    Painters.fs       Reusable SKPaint/SKShader caches keyed by token generation
    Diagnostics.fs    Counters + overlay, dev-mode gated                 §23
    MemoryPressure.fs Pressure monitor + tiered release                  §21.2
  Controls/
    BackgroundHost.fs Animated background (5 modes)
    GlassPanel.fs     Glass surface (Decorator)
    RingGauge.fs, Sparkline.fs, ProgressTrack.fs, MilestoneTrack.fs
    GraphCanvas.fs    Knowledge-graph rendering surface
    CalendarSurface.fs
  Components/         FuncUI composite components (buttons, pills, cards, inputs, skeletons)
  Features/           The 15 screens
  Shell.fs            Navigation, overlays, shortcuts, capture
  Program.fs          Composition root (backend wiring preserved)
```

**Why this shape.** The redesign's value is concentrated in a *design system*, not in individual
screens. Isolating `Design/` as pure, dependency-free F# makes the system testable and gives one
place to change a colour rule. Isolating `Rendering/` means only a handful of files know Skia
exists, containing the custom-rendering risk rather than smearing it across 15 screens.

**Alternatives rejected.**
- *Keep the flat current layout.* It has no home for rendering or shaders, and today's `Theme.fs`
  mixes pure palette data with Avalonia `IBrush` plumbing and mutable global state — the exact
  coupling this migration removes.
- *Move to XAML + ReactiveUI.* Abandons an F#-idiomatic paradigm working across 15 features,
  discards the MVU discipline the tests rely on, and buys little: XAML's advantage is tooling for
  stock controls, and much of this UI is custom-drawn.

**Maintainability.** Pure design logic is unit-testable with no framework dependency. Screens depend
on `Components/` and `Design/`, never on Skia.

**Native preservation.** Every layer is compiled .NET rendering through Skia to the GPU.

### 3.2 Migration coexistence (strictly time-boxed)

New code lands beside old **only while a replacement is incomplete**. The moment a screen reaches
parity, the old implementation is deleted in the same stage. See §26 — this is a binding policy,
not a preference.

---

## 4. Rendering pipeline

### 4.1 How a frame is produced

```
UI thread                                  Render thread
─────────                                  ─────────────
Elmish update → new Model
  ↓
FuncUI diff → Avalonia visual tree
  ↓
Layout (measure/arrange)
  ↓
Visual.Render(DrawingContext)
   └─ custom controls push an
      ICustomDrawOperation carrying an
      IMMUTABLE parameter snapshot  ─────►  Draw operation list
                                              ↓
                                            ICustomDrawOperation.Render(
                                              ImmediateDrawingContext)
                                              ↓
                                            lease ISkiaSharpApiLeaseFeature
                                              ↓
                                            SKCanvas / GRContext → GPU
```

### 4.2 The threading rule (most important constraint in this document)

`ICustomDrawOperation.Render` executes on the **render thread**. It must never read mutable UI
state, touch the Elmish model, or call a backend service.

**Every draw operation is therefore constructed with an immutable snapshot record** containing
everything it needs: bounds, token values, animation time, mode, colours, quality level. F# records
are immutable by default, so the language enforces this rather than convention.

**Why.** Reading shared mutable state from the render thread produces tearing and intermittent
crashes that are extremely hard to reproduce. Immutability removes the failure class rather than
guarding it.

**Alternative rejected.** *Locking shared state.* Adds contention on the frame path, risks priority
inversion against the UI thread, and still permits a torn read across two fields.

### 4.3 Invalidation strategy

Only the control that changed calls `InvalidateVisual()`. The background host invalidates itself per
frame while animating and never invalidates ancestors. Screens re-render through normal FuncUI
diffing on model change.

**Why.** Whole-tree invalidation would re-render 15 screens' worth of visuals at 60 Hz because a
background ribbon moved.

---

## 5. Custom rendering system

### 5.1 `SkiaControl` base

```fsharp
[<AbstractClass>]
type SkiaControl() =
    inherit Control()

    /// Immutable snapshot handed to the render thread. Built on the UI thread
    /// whenever inputs change; never mutated afterwards.
    abstract BuildFrame : Rect -> IFrameSnapshot

    override this.Render(ctx: DrawingContext) =
        ctx.Custom(SkiaDrawOperation(this.Bounds, this.BuildFrame this.Bounds, this.RenderSkia))

    /// Runs on the RENDER thread. Pure with respect to application state.
    /// MUST NOT allocate. MUST NOT throw.
    abstract RenderSkia : SKCanvas * SKRect * IFrameSnapshot -> unit
```

`SkiaDrawOperation` implements `ICustomDrawOperation`:
- `Bounds` — the control rect.
- `HitTest` — `false` for decorative surfaces; delegated for interactive ones.
- `Equals(other)` — structural comparison of the snapshot, letting Avalonia skip redundant work. A
  paused background therefore costs nothing.
- `Render(ImmediateDrawingContext)` — acquires `ISkiaSharpApiLeaseFeature`; on absence or failure,
  demotes to the next tier (§6) and invokes that tier's painter. **Wraps the call so no exception
  escapes to the render loop** (§22).
- `Dispose` — releases nothing by default; native resources live in caches with explicit lifetimes
  (§20, §21).

**Why a single base class.** Leasing, tier selection, failure handling and snapshot discipline are
identical for every custom control. Centralising them makes a new visualisation one `RenderSkia`
override and confines a leasing bug to one file.

**Alternatives rejected.**
- *`WriteableBitmap` + manual pixel work.* CPU-bound, no GPU acceleration, poor text and
  antialiasing.
- *Compose everything from stock visuals.* Caps fidelity near 60–70% and needs ~40 animated visuals
  for the background alone — violating Principle 4.
- *Per-control ad-hoc Skia leasing.* Duplicates fallback and threading logic 8+ times — violating
  Principle 3.

---

## 6. Rendering tiers and fallback strategy

Three tiers. **The application is fully functional at every tier.** Visual fidelity decreases;
functionality never does. No feature, screen, control, interaction or piece of information is
available at Tier A but missing at Tier C.

| Tier | Technique | Background | Glass | When selected |
|---|---|---|---|---|
| **A** | GPU shader (SKSL via `SKRuntimeEffect`) | Single full-screen shader pass per mode | Backdrop cache: offscreen render + `SKImageFilter` blur | GPU + `GRContext` + shader compilation all succeed |
| **B** | Skia draw calls | Gradients, `SKPath` geometry, `SKImageFilter` blur | Backdrop cache via Skia draws | Skia lease available, shaders unavailable or failed |
| **C** | Plain Avalonia | Static/simple gradient `Border` layers, optionally slow-animated | `ExperimentalAcrylicMaterial` tint + noise, or flat opaque surface | No Skia lease (software rendering, unsupported platform) or repeated Tier-B failure |

### 6.1 Tier resolution

- **At startup**, `Rendering/Capabilities.fs` probes: is a Skia lease obtainable, is a `GRContext`
  present, do the shaders compile (§11). The result is a `RenderTier` recorded in diagnostics and
  logged once.
- **At runtime**, any failure in an advanced path demotes the tier permanently for the session
  (§22). Demotion is one-way within a session — a path that failed is not retried every frame.
- **Manual override** via a developer setting, so all three tiers can be exercised and tested on any
  machine. This is also how Tier B and C are covered by the test suite.

### 6.2 Why three tiers rather than two

Two tiers left the case of "no GPU at all" undefined — a software-rendered VM, a remote desktop
session, or a driver failure. Tier C makes that case explicit and testable rather than accidental.
It is also the honest floor: it guarantees that even with every advanced feature unavailable, the
user gets a complete, usable, correct University OS.

**Maintainability.** Each control implements its rendering once per tier it supports, behind one
dispatch point. There is exactly one place that decides which tier is active.

---

## 7. Master animation clock

**There is one animation clock for the entire application.** `Rendering/FrameClock.fs` owns it.

```fsharp
type FrameClock =
    /// Seconds since application start, advanced only by the vsync callback.
    member Time : float32
    /// Monotonic frame counter, used as a cache key.
    member FrameId : int64
    /// Frame-to-frame delta, for the quality controller.
    member LastFrameDelta : TimeSpan
    member Subscribe : (FrameTick -> unit) -> IDisposable
    member IsRunning : bool
```

- Driven by a single `TopLevel.RequestAnimationFrame(Action<TimeSpan>)` registration — **one
  registration for the whole application**, not one per control.
- Every time-based visual derives its phase from `FrameClock.Time`: the background engine,
  graph physics, sparkline sweeps, shimmer effects, and any custom transition.
- Avalonia's declarative `Transitions`, keyframe `Animation`s and composition animations run on the
  same vsync-driven compositor tick, so they remain coherent with the master clock without drifting
  apart. Their *durations* come from the same `Motion` derivation (§12), so a speed change moves
  everything together.
- Where exact phase alignment matters (e.g. a card entrance timed against a background pulse), the
  control reads `FrameClock.Time` directly rather than starting an independent animation.

**Why one clock.** Independent clocks drift. With per-control `DispatcherTimer`s or separate
`RequestAnimationFrame` registrations, effects that should pulse together gradually separate, and
the interface stops feeling like one object. A single monotonic source also makes rendering
deterministic for golden-image tests (§25) — inject a fixed `Time`, get a byte-stable frame.

**Pause semantics.** The clock **stops** — it does not merely gate consumers — when the window is
hidden or deactivated, when `bgDisableAll` is set, or when app or OS reduce-motion is active. On
stop, the last frame remains rendered and `Equals` makes redraws free. A stopped clock costs zero
CPU and GPU; a "paused" animation still costs compositor work.

**Alternatives rejected.**
- *Per-control `DispatcherTimer`s.* Off-vsync, drift, N wakeups per frame, no coherent pause.
- *Letting each control register its own `RequestAnimationFrame`.* Coherent in phase but multiplies
  callback overhead and makes global pause a scattered concern.

---

## 8. Background engine architecture

### 8.1 Structure

```
BackgroundHost (SkiaControl, z-bottom of the shell, IsHitTestVisible = false,
                excluded from the automation tree)
  ├── FrameClock subscription   (the master clock, §7)
  ├── BackgroundSnapshot        immutable per-frame record
  ├── ShaderLibrary             pre-warmed SKSL program per mode (§11)
  ├── QualityController         current quality level (§9)
  └── Tier B / Tier C painters  (§6)
```

```fsharp
type BackgroundSnapshot =
    { Mode: BackgroundMode          // Aurora | Waves | Liquid | Folded | None
      TimeSeconds: float32          // from FrameClock, already speed-scaled
      Accent: SKColor
      OpacityFraction: float32
      BlurPx: float32
      Parallax: struct (float32 * float32)
      Overscan: struct (float32 * float32)  // §8.4
      Size: SKSize
      GlassScale: float32
      Quality: QualityLevel }       // §9
```

### 8.1a Overscan — why the layer is rendered larger than the viewport

Drawing a viewport-sized image at a parallax offset of *n* px necessarily uncovers
an *n*-px strip on the opposite edge, and the layer's own blur fades the image out
over roughly 3σ before that, so the boundary appears well before the strip does.
Stage 1 shipped exactly that bug.

The background is therefore rendered over an **extent** larger than the viewport
and cropped back to it by the control's `ClipToBounds`:

```
overscan = maxParallaxDisplacement + 3 × blurPx + safety
```

`Design/Parallax.fs` owns both halves of the contract — how far the pointer may
push the layer, and how much extra must be rendered — so "displacement can never
exceed overscan" is a unit test rather than a convention. It is deliberately not
capped as a fraction of the viewport: the blur cap already bounds the total to
about 156 px, and a fractional cap only ever binds on windows small enough for
the absolute cost to be irrelevant, while breaking coverage exactly there.

Painters take two rectangles. `content` is the viewport and fixes the scale of
the composition — every mode is authored against it. `extent` is `content` grown
by the overscan and is the area actually filled. Tier A keeps `uRes` at the
content size, so the shader's field simply continues past `uv` 0..1; Tier B
painters keep viewport-space geometry and only extend their spans and clips.
That is what lets the approved compositions be rendered larger without changing
size. Aurora's cached low-resolution bitmap additionally snaps its overscan to
whole bitmap pixels, so its sampling grid is identical with and without overscan.

### 8.2 Parameter maths (ported exactly, pure, unit-tested)

Lives in `Design/BackgroundParams.fs`, independent of rendering:

- Slider easing: `bgScale v = 1 - cos(π · clamp01(v/100))`
- Opacity: `cap × adaptiveMult × opF × (0.62 + 0.38 × inF)`
- Per-mode opacity caps — Aurora 0.20, Waves 0.13, Liquid 0.18, Folded 0.12
  (Aurora was raised from 0.11 when it became contour linework: thin lines cover
  a fraction of the pixels the earlier fog did.)
- Per-mode blur caps — 45 / 28 / 35 / 26 px
- Speed multipliers — these are **rates**: animation seconds accumulated per real
  second, so a bigger number is faster. Slow **0.6**, normal 1.0, fast **1.6**.
  They are `BackgroundParams.slowRate` / `normalRate` / `fastRate` and must not be
  confused with `Tokens.animScale`, which runs the other way because it scales
  transition **durations** (slow 1.55, normal 1.0, fast 0.6). Conflating the two
  is how the labels were inverted through Stage 1: "Slow" ran the background at
  1.6× until it was caught in review.
- Advancing the clock is `advanceTime current delta speed`, which accumulates
  speed-scaled deltas. Time already banked is never rescaled, so changing speed
  changes the rate from that point on and cannot make the composition jump.
- Adaptive section map — overview → Aurora; work → Waves; knowledge → Liquid; system → Folded
- Graph screen in adaptive mode — opacity × 0.4

### 8.3 Tier A shader implementations

Each mode is one `SKRuntimeEffect` drawing a single full-screen quad, with uniforms for time,
accent, intensity, size and quality.

- **Aurora** — six rotated radial gradients summed in-shader; per-band drift and breathe from
  `sin(t·ω + φ)`.
- **Layered Waves** — the harmonic sum `y = base + Σ ampᵢ·sin(t·periodsᵢ·2π + phaseᵢ)` evaluated as
  a signed-distance test: smooth at any resolution, seamless looping without the 200%-width slide.
- **Liquid Glass** — 14 metaball-style blobs using the same `sin`-hash placement, morphed by a
  low-frequency noise term.
- **Folded Glass** — two triangle meshes as barycentric tests; each facet's lightness modulated by a
  sweep phase offset by facet centroid, matching the negative-delay sheen.

**Why shaders.** The web original animates ~40 composited DOM layers. One shader replaces all of
them with a single GPU draw — higher fidelity (continuous, resolution-independent, no layer seams)
and dramatically cheaper. Shader source is data, so tuning a background needs no control changes.

**Alternatives rejected.**
- *One Avalonia visual per ribbon/blob/facet.* Faithful to the DOM but pays layout, scene-graph and
  compositing cost per layer per frame; `BlurEffect` over 40 large overlapping visuals will not hold
  60 fps. Violates Principle 4.
- *Pre-rendered video/APNG loop.* Cannot respond to accent, intensity, opacity or speed, and cannot
  pause coherently.
- *CPU-generated bitmaps per frame.* Bandwidth-bound; stalls the render thread.

---

## 9. Adaptive background quality

Quality is **measured and self-adjusting**, not fixed.

| Level | Backdrop resolution | Background detail | Notes |
|---|---|---|---|
| **Ultra** | 1:1 | Full layer/iteration count, highest-quality blur | High-end GPU with headroom |
| **High** | 1/2 linear | Full detail | |
| **Medium** | 1/4 linear | Full detail | **Default starting level** |
| **Low** | 1/8 linear | Reduced layer/iteration count, cheaper blur | |
| **Static** | 1/8, rendered once | No animation; single frame reused | Also the target for reduce-motion and pause |

### 9.1 The controller

`Rendering/QualityController.fs` consumes `FrameClock.LastFrameDelta` and the background's own
measured render time:

- **Budget:** 16.6 ms total frame; **4 ms** allocated to the background.
- **Downgrade:** if the background exceeds its budget for **30 consecutive frames** (~0.5 s), drop
  one level immediately. Responsiveness matters more than stability when the app is janking.
- **Upgrade:** only after **600 consecutive frames** (~10 s) using **< 60%** of budget, and never
  within **5 s** of a downgrade. Upgrading is slow and conservative.
- **Hysteresis and cooldown are mandatory.** Without them the controller oscillates between levels,
  which is far more visually objectionable than simply staying at the lower level.
- The level is carried in `BackgroundSnapshot`, so the render thread never queries the controller.
- Manual override in developer settings pins a level for testing.

**Why adaptive rather than fixed.** A fixed quarter-resolution backdrop is wrong in both directions:
wasteful on a high-end GPU that could render it at full resolution, and still too expensive on a
low-power integrated GPU. Measuring the actual frame cost and responding is the only approach that
is correct across the hardware range this app will run on.

**Why quarter-resolution remains the default start.** For blur radii of 18–75 px, downsampling
before blurring is standard practice and visually near-indistinguishable, at ~1/16 the pixel cost.
Starting at Medium means the common case is right immediately and the controller only adjusts at the
extremes.

---

## 10. Glass rendering system

### 10.1 The problem

CSS `backdrop-filter: blur()` blurs *whatever is behind* an element. Avalonia has no equivalent
(verified: no `Backdrop*` API). `Visual.Effect`'s `BlurEffect` blurs the element's **own content** —
the opposite of what is needed.

### 10.2 The design: an explicit backdrop cache

The insight that makes this tractable: **we draw the background ourselves, so we know exactly what
is behind a glass panel.**

```
Per frame (Tier A/B):
  1. BackgroundHost renders the background into an offscreen SKSurface at the
     current quality level's resolution (§9).
  2. That surface is blurred ONCE with SKImageFilter.CreateBlur(σ, σ),
     σ derived from the glass token.
  3. The blurred image is published to BackdropCache — a render-thread-owned,
     GPU-resident SKImage tagged with (size, tokenGeneration, frameId).

Per glass panel:
  4. GlassPanel samples BackdropCache for its own rect (scaled to cache resolution),
  5. draws it clipped to a rounded rect (radius from --radius-scale),
  6. composites the tint layer (surface colour at token alpha),
  7. draws the 1px inner border and outer BoxShadow.
```

One blur pass serves every glass panel on screen. N panels cost one blur, not N.

### 10.3 The honest limitation and its mitigation

For panels overlapping **scrolling content** rather than the background there is no cheap correct
answer. Policy, in priority order:

1. **Prefer designs where glass sits over the background.** In the redesign this is overwhelmingly
   true — sidebar, cards, calendar chips and popovers all float over the background layer.
2. **Content-snapshot blur** for genuine cases (a popover over a list): render the underlying region
   to a cached surface, blur, invalidate on scroll. One extra pass; used sparingly.
3. **Acrylic/flat fallback** (`ExperimentalAcrylicMaterial` tint + noise, or opaque surface). Always
   used at Tier C.

This is the main reason overall fidelity is ~90–95% rather than 100%.

### 10.4 `GlassPanel`

Derives from `Decorator` so it hosts a child: it draws glass behind, then the base renders the child
normally. **All content inside a glass panel remains real Avalonia controls** — essential for text
rendering, hit-testing, focus and accessibility (Principle 6, §24).

```fsharp
type GlassPanel() =
    inherit Decorator()
    member val Tint : GlassTint = GlassTint.Surface with get, set
    member val CornerRadius : float = 14.0 with get, set   // scaled by --radius-scale
    member val Elevation : int = 1 with get, set           // shadow preset
```

**Alternatives rejected.**
- *`ExperimentalAcrylicBorder` everywhere.* Simplest, but tint+noise with no real blur of our
  animated background; the signature look is lost. Retained only as the Tier C path.
- *Blurring each panel's backdrop independently.* N overlapping blur passes where one shared cache
  suffices. Violates Principle 4.
- *Rendering the background twice (sharp + blurred).* Doubles background cost every frame; the cache
  derives the blur from the frame already drawn.

---

## 11. Shader lifecycle

**Shaders are compiled exactly once per process, during a deliberate pre-warm phase, and are never
recompiled during normal runtime.**

### 11.1 Lifecycle

1. **Pre-warm at startup.** After the first `TopLevel` exists and a `GRContext` is obtainable, but
   before the shell's first paint, `ShaderLibrary.PrewarmAll()` compiles every SKSL program for
   every background mode and any shader-based control. This happens on a background thread against
   the render context, behind the existing splash/first-paint window.
2. **Cached for process lifetime.** Compiled `SKRuntimeEffect` instances live in a dictionary keyed
   by shader id. Lookups on the frame path are dictionary reads — never compilations.
3. **Never recompiled at runtime.** Changing accent, quality, mode or appearance changes *uniforms*,
   never shader source. This is a hard rule: a shader compile on the frame path is a multi-hundred-
   millisecond stall and would present as a freeze.
4. **Compilation failure is not fatal.** If any program fails to compile, the failure is logged once
   with the shader id and Skia's error text, that mode is marked Tier-B-only, and — if no program
   compiles — the whole session resolves to Tier B. **The application never crashes because a shader
   failed to compile.**
5. **Disposed at shutdown.** `ShaderLibrary` implements `IDisposable`; all effects are disposed on
   application exit, along with the backdrop cache, painters and any live surfaces.

**Why pre-warm rather than compile lazily.** Lazy compilation puts a several-hundred-millisecond
stall at the exact moment a user first navigates to a section — a visible hitch precisely when they
are watching. Paying it once at startup, hidden behind first paint, is strictly better and makes the
running app's frame cost predictable.

**Why never recompile.** Predictability. If shader source can change at runtime, frame cost becomes
unbounded and no performance guarantee holds.

### 11.3 The Tier A allocation exception (measured, not assumed)

`SKRuntimeEffect.ToShader` captures uniform values at construction, so a shader object must be
created per frame; SkiaSharp 2.88.9 offers no mutate-in-place shader. Verified against the
installed API by reflection: `SKRuntimeEffectUniform` has **no public constructor** (values enter
through `op_Implicit`) and the only `ToShader` overloads are
`(bool)`, `(bool, Uniforms)`, `(bool, Uniforms, Children)`, `(bool, Uniforms, Children, SKMatrix)`.

**A partial reuse path does exist and is now used.** `SKRuntimeEffectUniforms` *can* be created
once per effect and re-populated each frame. Measured over 10,000 iterations:

| Strategy | Bytes/iteration |
|---|---|
| Fresh uniforms + shader every frame | **841.9** |
| Reused uniforms, shader only (implemented) | **208.1** |

That is a **75% reduction**; the residual ~208 B/frame is the unavoidable `SKShader`.

**Measured impact in the running application** (10s windows, 144 Hz, Design Preview with four
glass panels — see §25.4 for method):

| Configuration | Alloc/s | Alloc/render pass | Gen0 | Gen1 | Gen2 |
|---|---|---|---|---|---|
| Tier A Ultra (shaders) | 5,686 KB/s | 40,462 B | 1 | 0 | 0 |
| Tier B (no shaders) | 5,891 KB/s | 41,921 B | 1 | 0 | 0 |
| Tier C (no Skia at all) | 5,498 KB/s | 39,115 B | 1 | 0 | 0 |

**Conclusion — the exception is not merely bounded, it is negligible.** Tier C runs no shaders and
still allocates ~39 KB per pass, so ~99.5% of per-frame allocation comes from the UI layer
(Elmish/FuncUI re-render and Avalonia's own per-frame work), not from rendering. The shader object
is ~208 B of ~40,000 B per pass. Across a 20-second soak: **Gen2 never ran**, Gen1 ran at most once,
Gen0 ran 2–3 times, and working set stayed flat (239.9 MB → 245.2 MB over ~100 s). No stutter is
attributable to it: worst-frame times are 8–14 ms against a 6.94 ms vsync interval, i.e. at most an
occasional single dropped frame, and identical across all three tiers.

**Standing rule:** this exception applies *only* to the per-frame `SKShader`. Any new allocation on
the render path must be justified and measured the same way.

### 11.2 Reconciling with memory pressure

Under severe memory pressure (§21.2) shader programs are the **last** resource released, because
they are small (kilobytes) and expensive to rebuild. If they are released, the next use triggers a
**controlled, explicitly logged recompilation** — this is a deliberate recovery path, not normal
runtime, and it is the single sanctioned exception to rule 3 above. Releasing shaders is only
reached after every larger cache has already been dropped, so in practice it should never occur.

---

## 12. Animation system

### 12.1 Three tiers, one clock, one duration source

| Tier | Mechanism | Used for |
|---|---|---|
| **1. Property transitions** | Avalonia `Transitions` | Hover, colour, opacity, size — the ~200 `style-hover` cases |
| **2. Keyframe animations** | Avalonia `Animation` + `Easing` | Entrance/exit, page transitions (`calFadeIn`), panel fades |
| **3. Frame-driven** | `FrameClock` subscription (§7) | Background engine, graph physics, continuous visualisations |

All durations derive from tokens through `Design/Motion.fs`:

```fsharp
let durBase (t: Tokens) = TimeSpan.FromMilliseconds(240.0 * t.AnimScale)   // slow 1.55 / normal 1 / fast 0.6
let durFast (t: Tokens) = TimeSpan.FromMilliseconds(140.0 * t.AnimScale)
let standardEase = CubicBezierEasing(0.22, 0.9, 0.32, 1.0)   // the redesign's signature curve
```

`cubic-bezier(.22,.9,.32,1)` appears on essentially every transition in the redesign; it is defined
once as `Motion.standardEase` and referenced everywhere (Principle 7).

### 12.2 Reduce-motion

A single predicate `Motion.enabled tokens` gates tiers 2 and 3 and collapses tier 1 durations to
zero; the background moves to `Static` quality and the master clock stops. One switch satisfies both
the app setting and the OS setting.

**Why centralise.** Durations scattered as literals across 15 screens means the animation-speed
setting silently fails for whichever screens were missed, and reduce-motion becomes unenforceable.

**Alternatives rejected.**
- *Composition animations as the primary mechanism.* Powerful and low-overhead, but a smaller,
  less-documented API, harder to unit-test, and overkill for property transitions. Reserved for
  cases where independence from the UI thread pays off.
- *A hand-rolled tween engine on a `DispatcherTimer`.* Reimplements Avalonia, runs off-vsync, drifts.

---

## 13. Design token system

```fsharp
type Palette =
    { Normal: Color; Light: Color; VLight: Color; Strong: Color; Dark: Color
      A14: Color; A1A: Color; A1F: Color
      S14: Color; S22: Color; Soft: Color; Ring: Color
      B10: Color; B18: Color; B25: Color; B30: Color
      B35: Color; B40: Color; B45: Color; B55: Color }

type Tokens =
    { Accent: Palette
      GlassScale: float      // settingsGlass / 50 -> 0.0 | 1.0 | 2.0; see §13.1a
      RadiusScale: float     // settingsRadius / 14
      AnimScale: float       // DURATION factor: slow 1.55 | normal 1.0 | fast 0.6
      Density: float         // compact 0.74 | comfortable 1.0 | spacious 1.32
      ReadingWidth: float    // 640 | 820 | 1040
      Theme: ThemeMode
      Generation: int }      // bumped on every change; the universal cache key
```

### 13.1a Glass, and where the theme is applied

`GlassScale` is never consumed raw. `Design/Glass.fs` turns it into six values —
tint opacity, backdrop compositing, border weight, the specular highlight, the
shared blur sigma and shadow softness — piecewise-linear through three defined
points: slider **0** (opaque reduce-transparency escape hatch, docs §24), **50**
(balanced default) and **100** (strongest approved, never below a readable tint
alpha). All three are asserted in tests.

Two failure modes this closes:

- *Panels that never repaint.* `GlassPanel` reads `TokenStore` at Render time,
  but Avalonia only calls Render when something invalidates the visual, and
  moving the Glass slider changes no property **of the panel**. It now subscribes
  to `TokenStore.changed`, so every panel anywhere is correct without its call
  site threading a generation through.
- *Two theme authorities.* `Tokens.themeModeFor` is the ONE derivation of the
  applied theme, and `Shell.publishAppearance` is the ONE place it is applied
  (tokens → `TokenStore` → `Theme.setMode` + `RequestedThemeVariant`).
  `UserSettings.Theme` is retired in place: kept so previously persisted JSON
  still deserializes, read by nothing. It used to be
  applied at startup and again on every Settings save, which is why choosing a
  theme in the appearance panel had no effect that survived.

`Palette.generate : Color -> Palette` ports the redesign's HSL derivation exactly (light
`L + (1-L)·0.42`, vlight `·0.74`, strong `L·1.03`/`S·1.12`, dark `L·0.60`/`S·1.05`, plus the alpha
ladder). It is **pure and unit-tested** — an advantage the CSS original never had.

**Distribution.** FuncUI views read `Tokens` from the Elmish model, so changes re-render naturally.
Custom controls read `TokenStore` and copy values into their frame snapshot. Changing tokens bumps
`Generation`, invalidating every painter cache (§20).

**Why one record with a generation counter.** Single source of truth, trivially correct cache key,
and "appearance updates the entire application live" falls out of the design rather than requiring
per-screen wiring.

**Alternatives rejected.**
- *Avalonia `DynamicResource` / `ResourceDictionary`.* The idiomatic XAML answer, but these values
  are *derived* (20 palette entries from one hex; durations from a scale). Derivation in resource
  dictionaries is awkward and untestable.
- *Mutable globals (today's `Theme.fs`).* Already a known weak spot: order-dependent tests, no
  invalidation signal.

---

## 14. Appearance / theme engine

### 14.1 Versioned settings model

```fsharp
/// Persisted appearance preferences. VERSIONED — see §14.2 before changing.
type AppearanceSettings =
    { Version: int                 // schema version; CURRENT = 1
      AccentKey: string            // amber | blue | purple | teal | pink | gold
      Theme: ThemePreference       // Dark | Light | System — THE authoritative
                                   // theme choice; UserSettings.Theme is retired
      GlassIntensity: int          // 0-100, default 50 (the balanced point)
      CornerRadius: int            // px, default 14
      AnimationSpeed: Speed        // Slow | Normal | Fast
      Density: Density
      ReadingWidth: ReadingWidth
      BackgroundStyle: BackgroundStyle
      BackgroundSpeed: Speed
      BackgroundOpacity: int       // 0-100
      BackgroundBlur: int          // 0-100
      BackgroundIntensity: int     // 0-100
      BackgroundParallax: bool
      ReduceMotion: bool }         // ACCESSIBILITY, not a fourth speed — §14.1a
```

Persisted under its own settings key via a new `IAppearanceStore` port, defaults returned when
absent — the pattern `IUiStateStore` already uses.

### 14.1a Reduce Motion is accessibility state

Slow is not reduced motion: Slow still moves continuously, Reduce Motion stops.
Presenting the two as peers in one option row taught the wrong model, so the
Design Preview now lists Slow / Normal / Fast as the background speed and puts
Reduce Motion under its own **Accessibility** heading with an explanation.

The decision itself lives in `Design/Motion.fs` so it is testable without a
window, and it is deliberately asymmetric:

```fsharp
reduceMotionActive userSetting osSetting = userSetting || osSetting
```

The OS preference is honoured automatically and the app setting can only ever
**add** to it. There is no way for the application to switch reduced motion off
for a user whose operating system asked for it; the app-level toggle exists for
people whose OS does not expose the setting, not as an override. When the OS is
the reason, the panel says so rather than showing a toggle that looks stuck.

When active: the frame clock stops (so the background freezes on a stable
composition rather than animating slowly), quality drops to `Static`, and pointer
parallax is gated off — `parallaxActive` is strictly stronger than the animation
gate. `BackgroundSpeed` is a separate field and is never written by any of this,
so the user's Slow/Normal/Fast choice is preserved throughout and resumes exactly
when Reduce Motion is switched off.

**Why not extend `UserSettings`.** The project's rule is that `UserSettings`' serialised
shape must remain stable or previously persisted JSON fails to deserialise. Adding 14 fields would
break every existing installation.

### 14.2 Schema versioning and migration

`AppearanceSettings` is persisted as JSON with an explicit `Version`. Migration is **forward-only and
total** — it always produces a valid record and never throws:

```fsharp
/// CURRENT_VERSION = 1
/// Applies migrations in sequence: v1 -> v2 -> ... -> CURRENT.
val migrate : json: JsonElement -> AppearanceSettings
```

Binding rules for every future schema change:

1. **Bump `CURRENT_VERSION` and add one migration step** `vN -> vN+1`. Steps are pure, individually
   unit-tested, and composed in sequence, so a v1 record stored years ago upgrades through every
   intermediate step.
2. **Never reuse or repurpose a field name.** Add new fields; retire old ones by ignoring them.
3. **Missing field → documented default.** A record written by an older version must always load.
4. **Unknown enum value → nearest sensible default**, logged once. A `BackgroundStyle` written by a
   newer version loads on an older build as `Adaptive` rather than failing.
5. **Version newer than `CURRENT_VERSION`** (user downgraded the app): load defaults, log a warning,
   and **do not overwrite the stored settings** until the user changes something — so downgrading and
   re-upgrading does not silently destroy preferences.
6. **A migration failure is never fatal.** Any exception yields defaults plus a logged warning.

**Why version from day one.** Retrofitting versioning onto already-persisted user data is the
expensive path — you cannot distinguish "old schema" from "corrupt" after the fact. Writing
`Version = 1` now costs one field and makes every future change safe.

### 14.3 Live application

```
Settings screen → Msg → update → services.Appearance.Save
                              → TokenStore.Publish (Generation + 1)
                              → Shell model updated
                                  ├── FuncUI re-renders (views read new tokens)
                                  └── custom controls invalidate; painter caches rebuild
```

`Theme = System` resolves against Avalonia's platform theme variant and subscribes to OS changes.

**Why push-based.** The redesign applies appearance by writing CSS variables on
`document.documentElement`, with every consumer reading through `var()`. `TokenStore` +
`Generation` is the direct native analogue: one write, global effect, no per-screen plumbing.

---

## 15. Custom control hierarchy

```
Avalonia.Controls.Control
├── SkiaControl (abstract)                        §5.1
│   ├── BackgroundHost         decorative; not hit-testable; excluded from automation
│   ├── RingGauge              conic progress (planner score)
│   ├── Sparkline              trend micro-charts
│   ├── ProgressTrack          gradient track + fill
│   ├── MilestoneTrack         diamond markers, 4 states (done/current/upcoming/projected)
│   ├── GraphCanvas            nodes + edges; interactive (sanctioned exception)
│   └── CalendarSurface        time grid, now-line, lane-packed cards (sanctioned exception)
└── Avalonia.Controls.Decorator
    └── GlassPanel             glass surface hosting real controls   §10.4
```

**The accessibility firewall (Principle 6).** Custom drawing is for decorative and
data-visualisation layers; everything interactive stays a real Avalonia control. Buttons, list rows,
text inputs and navigation items are stock controls styled with tokens, wrapped in `GlassPanel`
where the design calls for glass.

**`GraphCanvas` and `CalendarSurface` are the only two sanctioned exceptions**, because each renders
hundreds of interactive items where one control per item would be prohibitive. Each must therefore
ship explicit `AutomationPeer`s and full keyboard navigation (§24). Any future third exception
requires the same written justification and the same accessibility obligations.

**Why.** Hand-drawn interactive controls lose focus handling, keyboard access, screen-reader
exposure, IME and text selection — and correctly re-implementing those is large, thankless and
bug-prone. Restricting custom drawing to non-interactive layers keeps ~95% of the accessibility
surface working by default.

**Alternatives rejected.**
- *Draw entire screens in Skia (immediate-mode).* Maximum control, catastrophic for accessibility,
  text input and maintainability.
- *Use only stock controls.* Cannot produce the background, glass, rings or graph.

---

## 16. Screen structure

Every screen keeps the existing contract, so `Shell.fs` composition is unchanged:

```fsharp
module Features.Dashboard
type Model = { … ; Loading: bool; Error: string option }
type Msg = …
type External = Goto of Page | OpenCapture          // only where needed
val init   : AppServices -> Model * Cmd<Msg>
val update : AppServices -> Msg -> Model -> Model * Cmd<Msg> * External option
val view   : Tokens -> Model -> (Msg -> unit) -> IView
```

`view` gains a `Tokens` parameter — the one deliberate signature change, and the mechanism by which
appearance reaches every screen.

Standard visual skeleton:

```
PageRoot            max-width 1680px, padding = base × Density
  ├── PageHeader    title + subtitle + actions
  ├── Section*      GlassPanel cards on a responsive grid
  └── Overlay*      popovers/dialogs, auto-flipped near viewport edges
```

Responsive behaviour uses `Grid`/`WrapPanel` with breakpoint-driven column counts. The redesign's
popover auto-flip (`getBoundingClientRect` → above/below, left/center/right) is ported as a
`PopoverPlacement` helper computing the same decision from control bounds and window size. Loading
states reuse the skeleton components built in Phase J.

**Maintainability.** Fifteen screens with an identical shape means a new screen is a known exercise
and cross-cutting changes are mechanical.

---

## 17. Backend communication

**Unchanged — and this is the design's biggest structural win.**

```fsharp
let init (services: AppServices) =
    { …; Loading = true }, Elm.ofAsync (services.Dashboard.GetData()) Loaded Failed
```

- Direct **in-process async .NET calls**. No serialisation, no RPC, no IPC, no localhost port, no
  JSON schema to keep in sync, no bridge to version.
- `Composition.build` in `Program.fs` is preserved verbatim, including
  `registry.StartBackgroundSync()`.
- `Elm.ofAsync` continues to marshal results onto the Avalonia UI thread.
- Domain types cross the boundary as themselves — `Assignment`, `PlannedSession`, `SearchHit` — with
  full type safety and no DTO layer.

**Threading contract.** The UI thread calls services; services do their own async work; results are
marshalled back by `Elm.ofAsync`. **The render thread never calls a service** — it sees only
immutable snapshots (§4.2).

**Why this matters.** A WebView design would have required a typed RPC surface, a serialisation
format and a permanent frontend/backend versioning contract. Staying in-process deletes that entire
subsystem.

---

## 18. Performance considerations

Target: **60 fps (16.6 ms/frame)**, background budgeted at **≤4 ms**.

| Concern | Approach |
|---|---|
| Background cost | One shader draw; backdrop blurred once at adaptive resolution; clock stopped when not visible |
| Redundant work | `ICustomDrawOperation.Equals` short-circuits identical frames |
| Allocation on the frame path | **Zero** (Principle 1): paints/shaders from caches, snapshots built on the UI thread |
| Long lists | Avalonia virtualising panels for notes, documents, timeline, search results |
| Layout thrash | Animate transforms/opacity, never `Width`/`Margin` (Principle 2) |
| Startup | Shaders pre-warmed behind first paint (§11); screens load asynchronously behind skeletons |
| Graph | `GraphLayout.layout` (pure, existing, tested) runs off the UI thread; only positions reach `GraphCanvas` |

**Measurement, not guesswork.** The diagnostics overlay (§23) is checked on the lowest-spec target
before each stage is signed off.

---

## 19. GPU usage

- Avalonia's Skia backend renders through the GPU by default (Direct3D/ANGLE on Windows).
- `GRContext` is obtained from the Skia lease; offscreen surfaces are created **GPU-resident**
  (`SKSurface.Create(grContext, …)`), never CPU raster surfaces.
- **No GPU→CPU readbacks on the frame path.** Readback is the classic stall. The only sanctioned
  readback is golden-image testing (§25), which is off the hot path by construction.
- SKSL programs are compiled once at pre-warm (§11); compilation never happens mid-animation.
- GPU resource cache is bounded explicitly via `GRContext.SetResourceCacheLimit`, and reported via
  `GetResourceCacheUsage` (§23).
- If GPU acceleration is unavailable, `Capabilities` resolves to Tier B or C and the app degrades
  quietly (§6).

---

## 20. Caching strategy

| Cache | Key | Invalidated by | Owner |
|---|---|---|---|
| Compiled SKSL effects | shader id | process shutdown (or severe memory pressure, §11.2) | `ShaderLibrary` |
| `SKPaint` / `SKShader` | `Tokens.Generation` | appearance change | `Painters` |
| Blurred backdrop | `(size, generation, frameId, quality)` | every animated frame; once when stopped | `BackdropCache` |
| Static vector art | content hash | content change | `SKPicture` cache |
| Token-derived Avalonia brushes | `Tokens.Generation` | appearance change | `Design/Theme.fs` |
| Graph layout | `(nodes, edges)` hash | graph data change | screen model |

Every cache is **bounded and explicitly invalidated by a generation counter**. No time-based
evictions, no unbounded dictionaries.

**Why generation counters.** Invalidation becomes a single integer comparison, and it becomes
impossible to serve a stale paint after an appearance change — the bug class that otherwise appears
as "the accent changed everywhere except this one card."

---

## 21. Memory management

### 21.1 Ownership rules

Skia objects (`SKPaint`, `SKShader`, `SKSurface`, `SKImage`, `SKPicture`) wrap **native** memory and
are `IDisposable`. GC pressure is not the risk; native leaks are.

1. Every cache owns its entries and disposes them on invalidation. Nothing is handed out without a
   defined owner.
2. Controls dispose per-control resources in `OnDetachedFromVisualTree`.
3. **Frame-clock subscriptions and event handlers are released on detach** — the most common
   Avalonia leak (a control kept alive by a `TopLevel` handler). Registration and release are
   symmetric.
4. Resize disposes and recreates surfaces rather than growing them; resize is debounced so a drag
   does not allocate a surface per pixel.
5. Background mode change disposes the previous mode's transient resources.
6. Long-lived caches are bounded by construction (one entry per mode, one backdrop, one paint set
   per generation).
7. `ShaderLibrary`, `BackdropCache` and `Painters` are disposed at application shutdown.

### 21.2 Memory pressure handling

`Rendering/MemoryPressure.fs` monitors `GC.GetGCMemoryInfo()` (memory load against the high-load
threshold) on a low-frequency timer, and responds to Windows memory-pressure signals where
available. On pressure, recreatable resources are released in **least-valuable-first** order:

| Order | Resource | Rebuild cost |
|---|---|---|
| 1 | Temporary render targets, content-snapshot surfaces | Trivial — next frame |
| 2 | Blurred backdrop cache | One frame |
| 3 | `SKPicture` static-art cache | Cheap re-record |
| 4 | GPU resource cache (`GRContext.PurgeUnusedResources`, then `PurgeResources`) | Skia re-uploads on demand |
| 5 | `SKPaint` / `SKShader` painter caches | Cheap rebuild from tokens |
| 6 | **Compiled shader programs** — last resort only | Expensive; triggers a logged, controlled recompile (§11.2) |

Rules:
- **All released resources rebuild automatically on next use.** Release must never leave a control
  unable to render — it renders the next frame having rebuilt what it needs.
- Release is idempotent and safe to call from the UI thread; render-thread-owned resources are
  released at a frame boundary, never mid-draw.
- Under sustained pressure the quality controller (§9) is also pinned lower, reducing the size of
  what gets rebuilt.
- Every pressure event is logged once with the level reached, and surfaced in diagnostics.

**Why tiered rather than "drop everything".** Dropping all caches at once causes a visible multi-
frame hitch and immediately re-allocates most of what was freed. Releasing cheapest-to-rebuild
first usually relieves pressure before anything expensive is touched.

**Implementation (Stage 2).** `Rendering/MemoryPressure.fs`. Two choices worth recording:

- **A registry, not direct calls.** The things to release live at every level of the compile order —
  surfaces in the pipeline, paints in the painters, programs in the shader library, glass paints up
  in `Controls`. Calling them directly would force the module to the bottom of the dependency graph,
  where the pipeline could no longer ask it to run a pending release at a frame boundary. Startup
  registers one action per tier; the policy is then testable with fakes.
- **Shaders release differently from shutdown.** `ShaderLibrary.releaseUnderPressure()` arms an
  on-demand recompile in `tryGet`, where `dispose()` does not. Without that distinction a single
  pressure event would pin the whole session to Tier B until the app was restarted — a release that
  permanently degrades the renderer is not a release, it is a downgrade.

Spec tier 3 (`SKPicture` static-art cache) has no implementation: no such cache exists yet. It is
deliberately not stubbed — an empty tier reads as covered.

**Verification.** `MemoryPressureTests` covers the thresholds, the least-valuable-first ordering
(each level is a prefix-extension of the one below), idempotence, escalation when two requests
arrive between frames, a throwing release not aborting the remaining tiers, and — the rule that
makes tiered release safe — that a real frame rendered after releasing every renderer-owned resource
is byte-identical to the frame before it.

---

## 21.3 The application shell (Stage 2)

`ShellLayout.fs` owns the geometry; `Shell.fs` renders it. The redesign replaces
the docked column with a panel that has **two independent controls**:

| Control | States | Effect |
|---|---|---|
| Pin | pinned / floating | Pinned: docked, opaque, reserves layout width. Floating: inset 14px, 22px radius, drawn on `GlassPanel`, and content runs full width underneath it. |
| Density | Expanded 252px / Compact 80px | Whether entries show their label. Compact moves the label into a tooltip. |

**Why two controls and not the old cycle.** The previous sidebar cycled
Expanded → Compact → Hidden through one toggle, which made "how wide is it" and
"is it showing" the same setting — so neither could be chosen independently, and
"compact and hidden" was unreachable. Splitting them makes all four combinations
reachable, which is a unit test (`density and pinning are independent`).

### 21.3a The reveal state machine

The first implementation flickered at the left edge, revealing and hiding at
frame rate. The cause was structural, and worth recording because it is easy to
rebuild by accident:

- The edge strip and the panel were **separate siblings, each conditionally
  mounted on the state they controlled**. Revealing removed the element the
  pointer was standing on.
- FuncUI patches `Panel.children` **positionally**. Removing the strip shifted
  the panel from index 4 to index 3, so the strip's control was *mutated into*
  the panel underneath the cursor rather than a new control being created.
- The panel's `PointerExited` then fired immediately, because the pointer was in
  the **14px inset gap** — outside the panel, and no longer inside anything.
  That hid it, which remounted the strip under the pointer, which revealed it.

The rewrite removes all three preconditions:

- **One always-mounted control** at a fixed sibling index owns every sidebar
  hover event. Its width changes with the state; the edge strip, the inset gap
  and the panel are regions *inside* it, so the gap is a bridge rather than a
  dead zone.
- Revealing only ever **grows** the region (20px → 280px), so the pointer that
  triggered the reveal is necessarily still inside it and cannot produce an exit.
  This is asserted directly.
- Visibility is an explicit machine — `Pinned | Hidden | Revealed |
  HidePending of token` — with every transition a pure function in
  `SidebarInteraction`, testable without a window.

Leaving schedules a hide **200ms** later rather than hiding at once. Elmish
commands cannot be unscheduled, so cancellation is by **token**: each pending
hide carries the token it was scheduled with, and acts only if the state is
still `HidePending` with that same token. Re-entering, pinning, or a newer
schedule all leave the in-flight message inert. A second exit while one is
already pending keeps the original token and schedules nothing, so timers cannot
accumulate.

`Revealed` is transient and never persisted — an app closed with the pointer at
the left edge must not reopen showing a sidebar that nothing will ever hide. A
test asserts `UiState` carries pin and density only.

Unpinning takes a `pointerOverSidebar` flag from the caller, which always knows:
the pin control lives inside the panel (`true`, stay revealed), the command
palette does not (`false`, hide immediately — no exit event would ever arrive).

**Ctrl+Shift+B** toggles reveal from the keyboard. It is a developer aid for
reviewing the floating panel, and drives the same state machine the pointer
does rather than being a second path that could diverge.

### 21.3b The pin ↔ unpin transition

**One visual tree.** The docked and floating treatments are not two branches —
they are one `GlassPanel` at two ends of a blend. `GlassPanel.Presentation`
(0–1) drives corner radius, tint opacity, backdrop sampling, the specular
highlight and the elevation shadow together; the shell animates the surrounding
inset and the content's left offset from the same scalar. Branching on `pinned`
would remount the panel mid-gesture and leave nothing to interpolate — the same
class of mistake as the reveal flicker.

| Property | Docked (phase 0) | Floating (phase 1) |
|---|---|---|
| Inset around the panel | 0 | 14px |
| Corner radius | 0 | 22px × radius scale |
| Tint alpha | 1.0 (opaque) | `Glass.tintAlpha` |
| Backdrop sampled | no | yes |
| Highlight / shadow | none | `Glass.*`, elevation 2 |
| Content left offset | full density width | 0 |
| Region width | density width | density width + 2 × inset |

The panel keeps its density width at every phase, so only the inset moves and
"width stays consistent with the density" is structural rather than maintained.

**Timing.** 260ms, cubic ease-out (`1 − (1−t)³`) — monotonic, so it cannot
overshoot or bounce. Advanced by *real elapsed seconds* from a `DispatcherTimer`
subscription, so a 60Hz and a 144Hz display take the same wall-clock time and a
dropped frame costs smoothness rather than duration. Deliberately **not** scaled
by the animation-speed preference (Slow would put it at 400ms, outside the
specified band) and entirely independent of background speed.

**Retargeting, not queueing.** There is one phase and one destination. A click
mid-flight changes the destination; the running loop continues from wherever the
phase is. The tick subscription is keyed on "phase ≠ target", so Elmish keeps
exactly one loop alive across any number of reversals and disposes it the moment
the phase arrives.

**Hover during the transition.** Pinning sets the state to `Pinned` before the
geometry starts moving, and `Pinned` ignores all pointer traffic — so the region
shrinking under the pointer cannot start a hide. Unpinning from the panel's own
control sets `Revealed`, and the region only grows. The reveal strip is sized
from `Hidden`, which no transition passes through, so it cannot become active
mid-animation.

**Reduce Motion** settles the phase straight to its destination. The state
machine, the persisted result and the final geometry are identical; only the
interpolation is skipped, and no tick loop starts.

**Navigation** comes from `Page.groups`, whose group names — Overview, Work,
Knowledge, System — are the same four families `Page.backgroundSection` uses. An
Adaptive background therefore changes exactly when the sidebar heading does, and
a test asserts the two stay in step.

**Removed with the old shell** (§26 — deletion is part of the work): collapsible
sidebar groups, along with `SidebarMode`, `UiState.CollapsedGroups`, and the
`ToggleSidebar`/`ToggleGroup` messages. The redesign has static group headings,
and keeping an affordance it does not have would mean two sources of truth for
the interface.

`UiState` is now decoded field by field (`UiStateJson`), matching `AppearanceJson`:
the whole-record deserializer throws on a missing field and the caller then falls
back to defaults, so changing the record silently discarded the user's last
briefing date and made the morning briefing reappear.

---

## 22. Error recovery

**No rendering failure may crash, blank, hang or degrade the functionality of the application.**

| Failure | Detection | Response |
|---|---|---|
| Shader compilation fails | Pre-warm (§11) | Log once with shader id + Skia error; mark mode Tier-B; if all fail, session → Tier B |
| GPU acceleration unavailable | Startup probe: no `GRContext` | Session → Tier B (or C if no lease at all); logged once |
| Skia lease unavailable | `GetFeature<ISkiaSharpApiLeaseFeature>()` returns null | Session → Tier C; logged once |
| Blur creation fails | `SKImageFilter.CreateBlur` returns null / throws | Glass renders tint-only for that frame; on repeat, glass → Tier C path |
| Render-target creation fails | `SKSurface.Create` returns null | Skip backdrop cache this frame; glass falls back to tint; reduce quality one level; retry next frame |
| Unexpected exception in `RenderSkia` | try/catch inside the draw operation | Swallow for that frame, log once per control per session, demote tier after 3 occurrences |

Binding rules:

1. **Exceptions never escape `Render`.** An exception propagating into Avalonia's render loop can
   tear down the renderer for the whole window. Every custom draw operation catches at its boundary.
2. **Log once, not per frame.** A failing path at 60 fps would produce 3,600 log lines per minute.
   Failures are logged on first occurrence with a counter, and demotion is logged as a state change.
3. **Demotion is one-way within a session.** A path that failed is not retried every frame; retrying
   a broken GPU path 60 times a second is itself a performance bug.
4. **Every tier is reachable and tested.** The developer override (§6.1) forces each tier so the
   fallback paths are covered by tests rather than only exercised in the field.
5. **Fallbacks preserve function, not appearance.** Losing a blur is acceptable; losing a button,
   a data point, or the ability to complete a task is not.

---

## 23. Diagnostics

A developer-mode overlay and log, gated so **release builds pay nothing**.

| Metric | Source |
|---|---|
| FPS / frame time (current, avg, 1% low) | `FrameClock.LastFrameDelta` |
| Layout time | Avalonia layout instrumentation |
| Render time (total) | Render-thread stopwatch |
| **Shader time** | Timed around the background shader pass |
| **Blur time** | Timed around the backdrop blur pass |
| Draw calls | Counter incremented per custom draw operation |
| **GPU memory** | `GRContext.GetResourceCacheUsage` (bytes + resource count) vs `GetResourceCacheLimit` |
| **Cache hits / misses** | Per-cache counters (backdrop, painters, shaders, pictures, graph layout) |
| Active render tier (A/B/C) | `Capabilities` |
| Current quality level + last change reason | `QualityController` |
| Memory-pressure events | `MemoryPressure` |

**Zero release cost.** All instrumentation is behind a `static readonly bool` set from a developer
setting or environment variable at startup. When false, the JIT eliminates the guarded blocks; no
`Stopwatch` is started, no counter incremented, no allocation occurs. Timing uses raw
`Stopwatch.GetTimestamp()` ticks converted only when the overlay renders. The overlay itself is a
control that only exists in the visual tree when enabled.

**Why measure shader and blur separately.** They are the two most expensive and most variable
operations, and the two the quality controller acts on. Aggregate "render time" would not tell you
*which* to reduce.

---

## 24. Accessibility

Accessibility is a design constraint, not a later pass — custom rendering is where it is usually
lost.

| Area | Approach |
|---|---|
| **Structural** | The Principle 6 firewall: interactive elements are real controls, so focus, keyboard, IME and screen readers work by default |
| **Screen readers** | `GraphCanvas` and `CalendarSurface` expose `AutomationPeer`s describing nodes/events; `BackgroundHost` is excluded from the automation tree and is not hit-testable |
| **Reduce motion** | App setting **and** OS `prefers-reduced-motion`: master clock stops, background → Static, transitions collapse to zero (§12.2) |
| **Reduce transparency** | Glass intensity 0 yields opaque surfaces — the accessibility escape hatch for the entire glass system |
| **Contrast** | Glass lowers text contrast. Text colours are chosen against the *composited* surface, and a contrast check runs in tests across token combinations |
| **Keyboard** | Existing Ctrl+K/N/F/B and Ctrl+1..9 preserved; every screen fully keyboard-navigable; visible focus ring uses `--accent-ring` |
| **Density / reading width** | Existing tokens double as accessibility affordances |
| **Colour independence** | Status is never conveyed by colour alone — the redesign's diamond markers already vary shape/fill/border by state, which is preserved |
| **Tier independence** | Accessibility is identical at Tier A, B and C. Fallbacks change appearance only |

---

## 25. Testing strategy

Four layers, extending the existing 46-test xUnit suite (which must keep passing throughout).

**1. Pure logic (fast, the bulk of new tests).** No framework, no rendering:
- `Palette.generate` — exact expected values for all six accents, including the alpha ladder
- Token derivation — glass/radius/anim/density/reading-width formulas at boundaries
- `BackgroundParams` — eased slider curve, opacity composition, per-mode caps, adaptive section
  mapping, the Graph 0.4 multiplier
- `Motion` durations under each speed and under reduce-motion
- `QualityController` — downgrade/upgrade thresholds, hysteresis, cooldown, and specifically that it
  **cannot oscillate** under an adversarial frame-time sequence
- `AppearanceSettings.migrate` — every version step, missing fields, unknown enums, newer-than-current
  version, and corrupt JSON
- Popover placement at viewport edges; calendar lane packing, date stepping, `entriesOn` overlap
- Everything salvaged, extracted **with tests before** the old views are
  deleted

**2. Golden-image rendering — tolerance-based, never exact.** `RenderTargetBitmap` renders a control
at a **fixed `FrameClock.Time`** (the snapshot record makes this deterministic) and compares against
a committed baseline.

Comparison is configurable per test:

```fsharp
type ImageTolerance =
    { MaxChannelDelta: int      // per-channel, default 2 of 255
      MaxDifferingPixelRatio: float  // default 0.001 (0.1% of pixels)
      IgnoreEdgePixels: int }   // default 1, absorbs antialiasing seams
```

- **Why tolerance rather than exact match.** GPU vendors, driver versions and Skia builds differ in
  antialiasing, gradient dithering and blur kernels by a bit or two per channel. Exact matching
  produces tests that fail on a different machine for no real reason, and teams respond by deleting
  them. A tolerance detects genuine regressions (a wrong colour, a missing layer, a shifted layout)
  while ignoring insignificant noise.
- Tolerances are per-test: a flat-colour panel gets a tight tolerance; a blurred background gets a
  looser one.
- Baselines are generated on a documented reference configuration and regenerated deliberately, never
  silently.
- On failure the test writes `expected.png`, `actual.png` and a `diff.png` heat-map.
- Coverage: each background mode at each quality level, `GlassPanel` across token settings,
  `RingGauge`, `MilestoneTrack` in all four states.

### 25.2a Why there are no Tier A baselines

Tier A is a GPU shader path. On this SkiaSharp build, drawing any SkSL runtime
effect onto a CPU raster surface kills the process natively (0xC000001D), so
there is no honest way to capture it in a headless test — and rendering the
Tier B fallback and filing it under "Tier A" would produce a baseline that
passes forever while proving nothing about the shaders. That is worse than
having no baseline, because it looks like coverage.

Tier A is covered instead by the compile-and-prewarm test (every shader is
compiled, and a failure demotes the tier rather than silently disappearing) and
by manual review of the published build. When a GPU-capable test host exists,
Tier A baselines are recorded there and committed alongside the rest.

The whole test assembly runs with `DisableTestParallelization` for a related
reason: the rendering suites deliberately drive process-global state — the
compiled shader library, the capability probe, the shared backdrop, the
pipeline's cached surfaces, the memory-pressure registry — and freeing a native
Skia object on one thread while another draws takes the test host down rather
than failing an assertion.

**3. Headless interaction.** `Avalonia.Headless.XUnit` (12.1.0, verified, matches xUnit 2.9) drives
real input against real controls: navigation, keyboard shortcuts, focus order, glass panels hosting
focusable children, screen init/update cycles, and automation-peer exposure for the two custom
interactive surfaces.

**4. Soak / resource / resilience.**
- Repeated navigation across all 15 screens with animation running; assert bounded native memory and
  surface counts (§21) and a background frame budget within target.
- Synthesised memory pressure; assert tiered release and correct automatic rebuild.
- **Forced tier tests:** run the full headless suite at Tier B and Tier C to prove no functionality
  is lost when advanced rendering is unavailable (§6, §22).

**Gate per stage:** 0 warnings / 0 errors and all tests green — the project's existing standard.

---

## 26. Legacy Code Retirement Policy

**Binding policy. Deletion is part of the work that replaces something, never a follow-up task.**

1. **Coexistence is temporary and conditional.** Legacy presentation code may exist alongside the new
   implementation **only while the replacement is incomplete**.
2. **Parity triggers immediate deletion.** As soon as a screen or subsystem reaches feature parity,
   the previous implementation is deleted **within the same migration stage** — not the next stage,
   not at the end of the project.
3. **Deletion is total.** It covers the retired view *and* everything that existed only to serve it:
   unused components, helper functions, styles, themes, brushes, converters, assets, resources,
   rendering paths, compatibility shims, adapter layers, dead `Msg` cases, orphaned model fields, and
   now-unreferenced package dependencies.
4. **No duplicate implementations survive a stage boundary.** At the end of every stage there is
   exactly one implementation of every migrated feature. If two exist, the stage is not done.
5. **One implementation per feature at completion.** No exceptions.
6. **Full retirement of the Avalonia legacy layer.** Once the new presentation layer fully replaces
   the old one, every obsolete presentation file is removed from the repository — not commented out,
   not moved to an `old/` folder, not excluded from the build. Removed.
7. **Nothing is retained "just in case."** Version control is the safety net. A file kept because it
   might be useful later is dead code with a story attached.
8. **Every commit leaves the repository cleaner.** Each change reduces technical debt rather than
   accumulating it. A commit that adds a replacement without removing what it replaced is incomplete.

**Enforcement.** Each migration stage's definition-of-done includes: the replaced files are deleted,
the solution builds at 0 warnings / 0 errors, all tests pass, and no unreferenced presentation file
or unused package reference remains. A stage that leaves duplication is not merged.

**Why this is a policy rather than a preference.** Migrations rot precisely here. The replacement
ships, the old code is left "briefly," and it is still there two years later — building, confusing
newcomers, and occasionally being edited by mistake. Tying deletion to the same stage as the
replacement is the only reliable way to prevent that, and it is why the end-state in §27 is
achievable rather than aspirational.

---

## 27. Final repository state (migration success criterion)

The migration is complete only when **all** of the following are true. This is a success criterion,
not a wish list.

- **One presentation layer.** No parallel or partial second UI.
- **One rendering pipeline.** No duplicated or competing rendering systems.
- **No obsolete files.** Every Avalonia legacy view, component and helper superseded by the new layer
  is gone from the repository.
- **No redundant resources.** No unused styles, themes, brushes, icons, fonts or assets.
- **No temporary migration code.** No feature flags, routing shims, adapters or bridges that existed
  only to let old and new coexist.
- **No duplicate controls.** Exactly one `GlassPanel`, one background engine, one token system, one
  progress track, one of everything.
- **No unused dependencies.** Package references not required by the final layer are removed.
- **No dead code.** No unreferenced functions, types, `Msg` cases or model fields.
- **Every remaining file has a clear purpose**, discoverable from its name and location within the
  §3.1 structure.
- **Documentation matches reality.** `docs/ARCHITECTURE.md` and this document
  describe the code as it actually is.
- **Quality bar held:** 0 warnings, 0 errors, all tests passing, self-contained publish verified.
- **The result is production-quality software, not an upgraded prototype** — coherent, complete, and
  maintainable by someone who did not build it.

---

## 28. Risks

| Risk | Mitigation |
|---|---|
| Backdrop blur over scrolling content | §10.3 three-tier policy; prefer glass-over-background layouts |
| SKSL unsupported on some GPU | Tier B/C fallback selected at startup; forced-tier tests (§25) |
| Custom rendering harms accessibility | Principle 6 firewall + automation peers + headless tests |
| Native memory leaks | Explicit ownership, symmetric detach, pressure handling, soak test |
| Quality controller oscillation | Mandatory hysteresis + cooldown, with an adversarial unit test |
| Golden-image tests flaking across GPUs | Tolerance-based comparison with per-test thresholds (§25) |
| Font licensing (General Sans via Fontshare) | Verify licence before embedding; substitute if redistribution is not permitted — **resolved before Stage 1 ships** |
| Fidelity shortfall discovered late | Background engine + `GlassPanel` built as the first vertical slice, judged before the full migration proceeds |
| Legacy code lingering | §26 policy enforced at every stage boundary |

---

## 29. Open item requiring a decision (not blocking implementation)

**General Sans licensing.** The redesign loads General Sans from Fontshare as a webfont; a native app
must embed the font file. I will confirm the licence permits desktop embedding and redistribution
before Stage 1 ships. If it does not, I will propose a visually close alternative for approval rather
than substituting silently.

---

## 30. Summary

| Requirement | How it is met |
|---|---|
| True native desktop | Compiled .NET → Avalonia scene graph → Skia → GPU. No browser, no HTML/CSS/JS, no IPC. Prohibitions are binding (§1) |
| Highest achievable fidelity | Custom Skia/SKSL rendering for background, glass and visualisations (~90–95%) |
| Modern animation/glass/backgrounds | Shader background engine, backdrop-cache glass, one master clock, adaptive quality, full token system |
| Never fails | Three rendering tiers, graceful degradation, total error recovery — functionality preserved at every tier |
| Long-term maintainability | Pure testable design core; Skia confined to `Rendering/`; uniform screen contract; ten binding rendering principles; enforced retirement policy |
| Performance | 60 fps target, one-draw background, zero frame-path allocation, bounded caches, stopped-when-hidden clock, self-tuning quality |
| Clean backend integration | `AppServices` unchanged, in-process async calls, zero serialisation |
| Clean final repository | §26 retirement policy + §27 end-state as an explicit success criterion |

**This document is the authoritative specification for the presentation layer. Implementation may
proceed against it.**

---

## 31. Stage 1 acceptance corrections (verified against the running application)

This section records what the Stage 1 acceptance pass changed. Where it conflicts with an earlier
section, **this section wins** — earlier text described intent, this describes measured reality.

### 31.1 Tier C is now genuinely reachable (§6)

`SkiaControl` originally selected the fallback painter *only* when no Skia lease existed, so forcing
Tier C changed the reported label while still rendering through Skia. It now checks
`Capabilities.effective () = RenderTier.C` first. Verified live: at Tier C, background time is
**0.000 ms**, backdrop generations **0** and blur passes **0**, while GlassPanel draws continue —
the Skia path is genuinely bypassed and no functionality is lost.

### 31.2 Diagnostics are named cumulative counters, never "per frame" (§23)

The original single draw counter was incremented on the render thread and reset from the UI thread
on each clock tick. It was racy *and* unattributed: it read "5 per frame" both on the Design Preview
(four glass panels) and on the Dashboard (none), and was mistakenly cited as proof of a pipeline.

Counters are now cumulative and attributed by name. `draw.background-host` doubles as the
render-pass count, so relationships are expressed as ratios the reader can check:

| Counter | Meaning | Verified steady state (10 s @ 144 Hz) |
|---|---|---|
| `draw.background-host` | BackgroundHost custom draw operations = render passes | 1,439–1,440 (= 144/s) |
| `draw.glass-panel` | GlassPanel custom draw operations | 5,756–5,760 = **4.00 × passes** |
| `backdrop.generated` | Offscreen background renders | 1,439 = **1.00 per pass** |
| `backdrop.blur` | Shared blur passes | 1,439 = **1.00 per generation** |
| `glass.sample.hit` / `.miss` | Panels sampling the shared backdrop | 4 per pass / 0 |
| `shader.cache.hit` / `.miss` | Compiled-effect lookups | hits at Tier A, misses drive Tier B fallback |
| `painter.created` | Reusable SKPaint objects built (process lifetime) | 2 |
| `uniforms.created` | Reused uniform blocks built (process lifetime) | 1 per shader mode |
| `paint.fallback` | Tier C paints | 0 unless Tier C is active |

**This is the point:** four glass panels cause **one** backdrop generation and **one** blur, then
**four** samples of that shared result — not four generations and not four blurs.

### 31.3 The quality budget follows the real frame interval (§9)

A fixed 4 ms budget silently assumed a 60 Hz frame. On this 144 Hz display the whole frame is
6.94 ms, so 4 ms was 58% of it. Measured consequence: Tier B's wave painter ran at **3.149 ms**,
counted as "within budget", and still dropped the application to **104.3 FPS**.

`QualityController.budgetFor` now derives the budget from the observed frame interval
(30% of it, capped at the documented 4 ms, floored at 0.8 ms). Measured effect on the same case:

| | Before | After |
|---|---|---|
| Tier B Layered Waves | 3.149 ms @ **104.3 FPS**, quality stuck at Ultra | 0.526–0.960 ms @ **144.2 FPS**, quality auto-drops to High/Medium |

### 31.4 Typography: Inter is final (§29 closed)

**General Sans is not adopted.** It exists nowhere in this repository — no font file, no licence —
and a native application must embed and redistribute its typeface rather than fetch a webfont. We
hold no licence for it and will not assume one.

**Inter is the production typeface.** It arrives through the existing `Avalonia.Fonts.Inter`
dependency (registered by `.WithInterFont()`), is licensed **SIL OFL 1.1**, and is therefore
redistributable inside this application. The OFL requires the licence to accompany redistribution —
it travels with the package — and imposes no in-UI attribution.

All font selection now lives in `Design/Typography.fs` (`Typography.ui`, `Typography.mono`). No
screen declares a font family. Adopting General Sans later means obtaining the licence, committing
the file and licence text, and changing one value.

### 31.5 Deferred requirements — scheduled, not open-ended

| Requirement | Status | Scheduled |
|---|---|---|
| **Memory-pressure release** (§21.2) | **DONE — Stage 2**, `Rendering/MemoryPressure.fs` | Delivered ahead of the Graph and Documents screens as planned. Registry-based so the policy can be tested with fake registrations; releases run at a frame boundary from `BackdropPipeline.render`. Spec tier 3 (`SKPicture` static-art cache) has **no implementation** because no such cache exists yet — it is not stubbed, and gets added with the first screen that needs one. |
| **Committed golden-image baselines** (§25.2) | **DONE — Stage 2**, `tests/UniversityOS.Tests/baselines/` | Recorded immediately after Stage 1 visual approval, as planned: 12 background baselines (4 modes × 3 quality levels, through the real pipeline so quality and overscan are actually covered) plus 3 GlassPanel baselines at slider 0/50/100. Recording requires `UNIVERSITYOS_UPDATE_BASELINES=1`, so a regression can never quietly rewrite the file it should have failed against. **No Tier A baselines** — see §25.2a. |

### 31.6 The acceptance harness is retained

`Rendering/AcceptanceSweep.fs` is kept as a **performance regression harness**, not as leftover
scaffolding. It drives the real production entry points (`Capabilities.force`, `QualityController.force`
and the shell's ordinary `ApplyAppearance` message), so it measures what users get. It is inert
unless `UNIVERSITYOS_ACCEPTANCE` is set, and gives later stages a directly comparable baseline.

```
UNIVERSITYOS_ACCEPTANCE=clean   6 reference configurations   20s settle, 8s warm-up, 10s measure
UNIVERSITYOS_ACCEPTANCE=gc      4 allocation soaks            8s settle,  3s warm-up, 20s measure
UNIVERSITYOS_ACCEPTANCE=1       full 38-step matrix           5s settle, 1.5s warm-up, 2.5s measure
UNIVERSITYOS_RENDER_LOG=1       periodic console report, no sweep
```

**Measurement conditions** for every figure quoted above: NVIDIA GeForce RTX 5070 Ti, 3440×1440 @
**144 Hz** (frame interval 6.944 ms), Windows 11, .NET 10.0.302, window active and untouched.
~144 FPS is **vsync-locked, not uncapped** — the goal is stable delivery of exactly one frame per
refresh, which is what `passes = 1440 per 10 s` demonstrates.

**Rows measured while the window is inactive are not performance results.** An inactive window
correctly stops the master clock (FPS 0, ~2.5 passes/s from the diagnostics timer alone). Such rows
verify pause-on-deactivate and are reported as that, never as throughput.
