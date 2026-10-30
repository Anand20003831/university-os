namespace UniversityOS.App.Rendering

open SkiaSharp

/// Small conversions between Avalonia's value types and Skia's. Kept in one
/// place so colour handling cannot drift between the background and glass.
[<RequireQualifiedAccess>]
module SkiaInterop =

    let toSkColor (c: Avalonia.Media.Color) = SKColor(c.R, c.G, c.B, c.A)

    /// Same colour with an explicit 0–1 alpha.
    let withAlphaF (alpha: float32) (c: SKColor) =
        let a = byte (max 0.0f (min 255.0f (alpha * 255.0f)))
        c.WithAlpha a

    /// Red/green/blue as 0–1 floats, for shader uniforms.
    let rgbFloats (c: SKColor) =
        [| float32 c.Red / 255.0f
           float32 c.Green / 255.0f
           float32 c.Blue / 255.0f |]
