namespace UniversityOS.App.Rendering

open System
open System.Runtime.InteropServices
open Avalonia.Controls
open Avalonia.Platform

/// Operating-system accessibility and theme preferences.
///
/// Reduce-motion is honoured independently of the in-app setting: a user who has
/// asked Windows to minimise animation should not have to ask again here.
[<RequireQualifiedAccess>]
module SystemPreferences =

    [<Literal>]
    let private SPI_GETCLIENTAREAANIMATION = 0x1042u

    [<DllImport("user32.dll", SetLastError = true)>]
    extern bool private SystemParametersInfoW(uint32 uiAction, uint32 uiParam, int& pvParam, uint32 fWinIni)

    /// True when the OS asks for reduced motion. Non-Windows and any failure
    /// answer false — the in-app setting still applies.
    let prefersReducedMotion () =
        if OperatingSystem.IsWindows() then
            try
                // SPI_GETCLIENTAREAANIMATION reports whether animation is
                // ENABLED, so reduced motion is its negation.
                let mutable animationsEnabled = 1

                if SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0u, &animationsEnabled, 0u) then
                    animationsEnabled = 0
                else
                    false
            with _ ->
                false
        else
            false

    /// The OS light/dark preference, used when the user chose "System".
    /// Defaults to dark, matching the application's own default.
    let prefersDarkTheme (topLevel: TopLevel) =
        try
            match topLevel with
            | null -> true
            | tl ->
                match tl.PlatformSettings with
                | null -> true
                | settings -> settings.GetColorValues().ThemeVariant = PlatformThemeVariant.Dark
        with _ ->
            true

    /// The OS light/dark preference without needing a window — used where only
    /// the application is in scope, such as token derivation in the shell.
    let osPrefersDark () =
        try
            match Avalonia.Application.Current with
            | null -> true
            | app ->
                match app.PlatformSettings with
                | null -> true
                | settings -> settings.GetColorValues().ThemeVariant = PlatformThemeVariant.Dark
        with _ ->
            true
