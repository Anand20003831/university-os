namespace UniversityOS.App.Controls

open Avalonia.Controls
open Avalonia.Media
open Avalonia.FuncUI
open Avalonia.FuncUI.Builder
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.App.Design

/// FuncUI bindings for the custom controls, so views compose them exactly like
/// any built-in control. Kept beside the controls themselves rather than in the
/// view layer — one source of truth for how a control is configured.
[<AutoOpen>]
module CustomControlDsl =

    type BackgroundHost with

        static member create(attrs: IAttr<BackgroundHost> list) : IView<BackgroundHost> =
            View.createGeneric<BackgroundHost> attrs

        static member appearance(value: AppearanceSettings) =
            AttrBuilder<BackgroundHost>
                .CreateProperty<AppearanceSettings>(BackgroundHost.AppearanceProperty, value, ValueNone)

        static member section(value: BackgroundSection) =
            AttrBuilder<BackgroundHost>
                .CreateProperty<BackgroundSection>(BackgroundHost.SectionProperty, value, ValueNone)

        static member isGraphScreen(value: bool) =
            AttrBuilder<BackgroundHost>
                .CreateProperty<bool>(BackgroundHost.IsGraphScreenProperty, value, ValueNone)

        static member accent(value: Color) =
            AttrBuilder<BackgroundHost>.CreateProperty<Color>(BackgroundHost.AccentProperty, value, ValueNone)

        static member generation(value: int) =
            AttrBuilder<BackgroundHost>.CreateProperty<int>(BackgroundHost.GenerationProperty, value, ValueNone)

        static member isFrozen(value: bool) =
            AttrBuilder<BackgroundHost>.CreateProperty<bool>(BackgroundHost.IsFrozenProperty, value, ValueNone)

    type GlassPanel with

        static member create(attrs: IAttr<GlassPanel> list) : IView<GlassPanel> =
            View.createGeneric<GlassPanel> attrs

        static member baseRadius(value: float) =
            AttrBuilder<GlassPanel>.CreateProperty<float>(GlassPanel.BaseRadiusProperty, value, ValueNone)

        static member tint(value: Color) =
            AttrBuilder<GlassPanel>.CreateProperty<Color>(GlassPanel.TintProperty, value, ValueNone)

        /// A floor under the shared tint curve — see `Glass.primaryTintAlpha`.
        static member minTintAlpha(value: float) =
            AttrBuilder<GlassPanel>.CreateProperty<float>(GlassPanel.MinTintAlphaProperty, value, ValueNone)

        static member elevation(value: int) =
            AttrBuilder<GlassPanel>.CreateProperty<int>(GlassPanel.ElevationProperty, value, ValueNone)

        static member presentation(value: float) =
            AttrBuilder<GlassPanel>.CreateProperty<float>(GlassPanel.PresentationProperty, value, ValueNone)

        static member highlightScale(value: float) =
            AttrBuilder<GlassPanel>.CreateProperty<float>(GlassPanel.HighlightScaleProperty, value, ValueNone)

        static member borderInset(value: float) =
            AttrBuilder<GlassPanel>.CreateProperty<float>(GlassPanel.BorderInsetProperty, value, ValueNone)

        static member child(value: IView) =
            AttrBuilder<GlassPanel>.CreateContentSingle(Decorator.ChildProperty, Some value)
