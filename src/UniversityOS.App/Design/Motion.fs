namespace UniversityOS.App.Design

open System
open Avalonia.Animation.Easings

/// Every duration and curve in the application derives from here, so the
/// animation-speed preference and reduce-motion are correct by construction
/// rather than by remembering to honour them at each call site.
[<RequireQualifiedAccess>]
module Motion =

    /// The redesign's signature curve, cubic-bezier(.22,.9,.32,1). Defined once.
    let standardEase: Easing =
        SplineEasing(X1 = 0.22, Y1 = 0.9, X2 = 0.32, Y2 = 1.0) :> Easing

    /// Base transition duration (240ms unscaled).
    let durBase (t: Tokens) =
        TimeSpan.FromMilliseconds(240.0 * t.AnimScale)

    /// Short transition duration (140ms unscaled) — hovers and micro-feedback.
    let durFast (t: Tokens) =
        TimeSpan.FromMilliseconds(140.0 * t.AnimScale)

    /// Arbitrary design duration scaled by the animation-speed preference.
    let scaled (t: Tokens) (ms: float) =
        TimeSpan.FromMilliseconds(ms * t.AnimScale)

    /// Reduce Motion is ACCESSIBILITY state, not a speed.
    ///
    /// The operating system's preference is honoured automatically and the app
    /// setting can only ever add to it — there is deliberately no way for the
    /// application to switch reduced motion off for a user whose OS asked for
    /// it. That asymmetry is the whole point: the app-level toggle exists for
    /// people whose OS does not expose the setting, not as an override.
    let reduceMotionActive (userSetting: bool) (osSetting: bool) = userSetting || osSetting

    /// True when the OS is the reason motion is reduced, so the UI can say so
    /// instead of showing a toggle that appears stuck.
    let reduceMotionForcedByOs (osSetting: bool) = osSetting

    /// The single motion gate. False collapses transitions to zero and stops
    /// every frame-driven animation, satisfying both the app preference and the
    /// operating system's reduce-motion setting.
    let enabled (t: Tokens) (osReduceMotion: bool) =
        not (reduceMotionActive t.ReduceMotion osReduceMotion)

    /// Duration honouring the motion gate — zero when motion is disabled.
    let gated (t: Tokens) (osReduceMotion: bool) (duration: TimeSpan) =
        if enabled t osReduceMotion then duration else TimeSpan.Zero

    /// Whether the animated background may run. Reduce Motion freezes it; an
    /// inactive window stops it because off-screen animation is pure waste; the
    /// None style has nothing to animate.
    let backgroundAnimates
        (userReduceMotion: bool)
        (osReduceMotion: bool)
        (backgroundIsOff: bool)
        (windowActive: bool)
        =
        not (reduceMotionActive userReduceMotion osReduceMotion)
        && not backgroundIsOff
        && windowActive

    /// Whether pointer parallax may move the background. Strictly stronger than
    /// the animation gate: parallax is opt-in AND requires motion to be allowed,
    /// so enabling Reduce Motion disables it without touching the preference.
    let parallaxActive
        (parallaxPreference: bool)
        (userReduceMotion: bool)
        (osReduceMotion: bool)
        (backgroundIsOff: bool)
        (windowActive: bool)
        =
        parallaxPreference
        && backgroundAnimates userReduceMotion osReduceMotion backgroundIsOff windowActive
