namespace UniversityOS.App.Rendering

open System.Collections.Generic
open SkiaSharp
open UniversityOS.App.Design

/// The Tier A shader programs and their lifecycle.
///
/// Shaders are compiled EXACTLY ONCE, during a deliberate pre-warm at startup,
/// and never recompiled while the application runs. A shader compile costs
/// hundreds of milliseconds; paying that lazily would stall the first frame of
/// whichever screen the user navigated to, which is precisely when they are
/// watching. Changing accent, quality, mode or appearance changes *uniforms*,
/// never source, so frame cost stays predictable.
///
/// Compilation failure is never fatal: the mode is marked unavailable and the
/// renderer falls back to Tier B (see Capabilities).
[<RequireQualifiedAccess>]
module ShaderLibrary =

    let private gate = obj ()
    let private effects = Dictionary<BackgroundMode, SKRuntimeEffect>()
    let private failed = HashSet<BackgroundMode>()
    let mutable private prewarmed = false

    /// Set when the programs were dropped to relieve memory pressure, rather
    /// than at shutdown. It is what licenses `tryGet` to recompile on demand.
    let mutable private releasedUnderPressure = false

    /// Shared SkSL helpers prepended to every program.
    let private common =
        """
uniform float2 uRes;
uniform float uTime;
uniform float3 uAccent;
uniform float3 uAccentLight;
uniform float3 uAccentDeep;
uniform float3 uAccentAlt;
uniform float uIntensity;
uniform float uAlpha;

float hash1(float n) { return fract(sin(n) * 43758.5453123); }

float2 rot2(float2 p, float a) {
    float s = sin(a);
    float c = cos(a);
    return float2(p.x * c - p.y * s, p.x * s + p.y * c);
}

// SkSL in SkiaSharp 2.88.9 does NOT provide step, smoothstep or mod — they
// compile as "unknown identifier". These portable equivalents keep the shaders
// working on this Skia build instead of silently dropping the whole mode to
// Tier B. Verified against the actual compiler, not assumed.
float stepf(float edge, float x) { return x < edge ? 0.0 : 1.0; }

float smoothstepf(float e0, float e1, float x) {
    float t = clamp((x - e0) / (e1 - e0), 0.0, 1.0);
    return t * t * (3.0 - 2.0 * t);
}

float wrapMod(float x, float y) { return x - y * floor(x / y); }
"""

    /// Aurora — layered curtains, not blobs.
    ///
    /// Each band is a flowing horizontal centreline with a NARROW luminous core
    /// and a much wider bloom that hangs upward like a curtain, broken by fine
    /// vertical striations. Bands are thin relative to the viewport, so the dark
    /// gaps between them survive — that negative space is what makes it read as
    /// an aurora rather than as fog. Radial gradients are deliberately absent:
    /// they were what produced a single central spotlight.
    let private auroraSource =
        common
        + """
// The scalar field. A domain-warped sum of low-frequency sinusoids: warping is
// what produces irregular organic curvature, large connected sweeping shapes,
// and the uneven spacing where contours bunch on steep slopes and spread on
// shallow ones. Every term drifts at its own slow rate, so the composition
// deforms continuously without a perceptible loop.
float fieldValue(float2 p, float t) {
    float2 q = p;
    q += 0.42 * float2(sin(p.y * 0.85 + t * 0.060), cos(p.x * 0.70 - t * 0.050));
    q += 0.20 * float2(sin(q.y * 1.35 - t * 0.043), cos(q.x * 1.10 + t * 0.037));

    float v = sin(q.x * 0.62 + q.y * 0.22 + t * 0.031);
    v += 0.70 * sin(q.y * 0.78 - q.x * 0.16 - t * 0.026);
    v += 0.34 * sin((q.x + q.y) * 0.50 + t * 0.019);
    return v;
}

half4 main(float2 fragCoord) {
    float2 uv = fragCoord / uRes;
    float aspect = uRes.x / max(uRes.y, 1.0);
    float2 p = float2(uv.x * aspect, uv.y) * 3.4;
    float t = uTime;

    // Contours are the level sets of the field. Their screen-space distance is
    // |f - level| / |grad f|, so lines come out at CONSTANT pixel width however
    // steep the surface is. SkSL runtime effects have no fwidth/dFdx, so the
    // gradient is taken by finite difference over a 1.5px step.
    float pe = 1.5 * 3.4 / uRes.y;
    float f0 = fieldValue(p, t);
    float fx = fieldValue(p + float2(pe, 0.0), t);
    float fy = fieldValue(p + float2(0.0, pe), t);

    float density = 13.0;
    float s = f0 * density;
    float gx = (fx * density - s) / 1.5;
    float gy = (fy * density - s) / 1.5;
    float g = max(sqrt(gx * gx + gy * gy), 0.00001);

    float idx = floor(s + 0.5);          // which contour
    float dpx = abs(s - idx) / g;        // distance to it, in pixels

    // Line classes. Most contours are dim; every 5th is a secondary tone, every
    // 12th a bright ridge, every 29th a rare pale highlight.
    float isSecondary = stepf(wrapMod(idx, 4.0), 0.5);
    float isRidge = stepf(wrapMod(idx, 7.0), 0.5);
    float isPale = stepf(wrapMod(idx, 17.0), 0.5);

    // Subtle per-line width and opacity variation, so the field never looks
    // mechanically uniform.
    float width = 0.85 + isSecondary * 0.22 + isRidge * 0.45;
    float line = 1.0 - smoothstepf(width - 0.45, width + 0.55, dpx);
    line *= 0.70 + 0.30 * sin(idx * 2.399 + 1.7);

    // Spatial falloff. Without it the field tiles the whole viewport uniformly
    // and reads as wood grain; the reference concentrates its linework in one
    // region and leaves a calmer area.
    //
    // The dense region SWEEPS: drift moves the BOUNDARY between dense and calm
    // across the frame, it does not raise and lower the whole mask. That
    // distinction is the whole point — an additive drift on the mask itself
    // could push every level of it below the smoothstep at once, and the
    // linework then faded out completely for minutes at a time. Here the
    // covered fraction is bounded below by construction, the sweep direction
    // rotates slowly so the composition never repeats, and a floor keeps the
    // calm region ghosted rather than blank.
    float2 dir = normalize(float2(0.52 + 0.20 * sin(t * 0.013),
                                  0.72 + 0.16 * cos(t * 0.011)));
    float dn = dot(uv, dir) / (dir.x + dir.y);
    dn += 0.11 * sin(uv.y * 3.1 + t * 0.037) + 0.08 * sin(uv.x * 2.4 - t * 0.029);
    float centre = 0.72 + 0.24 * sin(t * 0.021);
    float sweep = 1.0 - smoothstepf(centre - 0.42, centre + 0.34, dn);
    float mask = 0.38 + 0.62 * sweep;

    float3 dim = mix(uAccentDeep, uAccent, 0.34);
    float3 col = dim;
    col = mix(col, uAccentAlt, isSecondary * 0.55);
    col = mix(col, uAccentLight, isRidge * 0.72);
    col = mix(col, mix(uAccentLight, float3(1.0, 1.0, 1.0), 0.55), isPale * 0.75);

    // Very dark accent-derived base so the linework sits on tinted depth
    // rather than pure black.
    float3 base = uAccentDeep * 0.30;
    float baseA = (0.16 + 0.10 * (0.5 + 0.5 * sin(f0 * 1.3 + t * 0.02))) * (0.35 + 0.65 * mask);

    float la = clamp(line * mask, 0.0, 1.0);
    float3 outCol = base * (1.0 - la) + col * la;
    float a = baseA * (1.0 - la) + la;

    // Restrained glow, ridge lines only — never a blur over everything.
    float glow = isRidge * exp(-dpx * 0.30) * 0.22 * mask;
    outCol += uAccentLight * glow;
    a += glow;

    a = clamp(a, 0.0, 1.0) * uAlpha * (0.7 + 0.3 * uIntensity);
    return half4(half3(outCol * a), half(a));
}
"""

    /// Layered Waves — six silk bands from summed sine harmonics, sliding and
    /// bobbing. Evaluated per-pixel, so it is smooth at any resolution and loops
    /// seamlessly without the double-width slide the web version needs.
    let private wavesSource =
        common
        + """
float waveCover(float2 uv, float baseY, float a1, float p1, float ph1,
                float a2, float p2, float ph2, float t, float slide, float bob) {
    float x = uv.x + t * slide;
    float y = baseY
        + a1 * sin(x * p1 * 6.28318 + ph1)
        + a2 * sin(x * p2 * 6.28318 + ph2)
        + bob * sin(t * 0.25 + ph1);
    return smoothstepf(y - 0.035, y + 0.035, uv.y);
}

half4 main(float2 fragCoord) {
    float2 uv = fragCoord / uRes;
    float t = uTime;

    float3 col = float3(0.0);
    float alpha = 0.0;
    float cov;
    float w;

    cov = waveCover(uv, 0.18, 0.050, 4.0, 0.2, 0.020, 8.0, 1.1, t, 0.010, 0.012);
    w = cov * 0.30;
    col = col * (1.0 - w) + float3(0.470, 0.588, 0.659) * w;
    alpha = alpha * (1.0 - w) + w;

    cov = waveCover(uv, 0.32, 0.038, 6.0, 1.4, 0.022, 10.0, 0.3, t, 0.014, 0.014);
    w = cov * 0.16;
    col = col * (1.0 - w) + float3(0.949, 0.925, 0.886) * w;
    alpha = alpha * (1.0 - w) + w;

    cov = waveCover(uv, 0.45, 0.062, 4.0, 2.1, 0.017, 12.0, 0.9, t, 0.017, 0.010);
    w = cov * 0.24;
    col = col * (1.0 - w) + uAccent * w;
    alpha = alpha * (1.0 - w) + w;

    cov = waveCover(uv, 0.58, 0.042, 6.0, 0.6, 0.025, 8.0, 2.4, t, 0.011, 0.013);
    w = cov * 0.20;
    col = col * (1.0 - w) + float3(0.435, 0.627, 0.722) * w;
    alpha = alpha * (1.0 - w) + w;

    cov = waveCover(uv, 0.71, 0.055, 4.0, 1.2, 0.015, 10.0, 0.5, t, 0.015, 0.012);
    w = cov * 0.12;
    col = col * (1.0 - w) + float3(0.949, 0.925, 0.886) * w;
    alpha = alpha * (1.0 - w) + w;

    cov = waveCover(uv, 0.84, 0.038, 6.0, 2.8, 0.027, 8.0, 0.1, t, 0.010, 0.015);
    w = cov * 0.30;
    col = col * (1.0 - w) + float3(0.353, 0.486, 0.573) * w;
    alpha = alpha * (1.0 - w) + w;

    alpha = alpha * uAlpha * (0.7 + 0.3 * uIntensity);
    return half4(half3(col * alpha), half(alpha));
}
"""

    /// Liquid Glass — fourteen drifting frosted blobs, placed by a stable hash
    /// so the arrangement is identical every launch.
    let private liquidSource =
        common
        + """
half4 main(float2 fragCoord) {
    float2 uv = fragCoord / uRes;
    float t = uTime;
    float aspect = uRes.x / max(uRes.y, 1.0);

    float3 frost = float3(0.949, 0.925, 0.886);

    // Accumulate premultiplied colour so every blob contributes its OWN hue.
    // The previous version tracked a single "accent field" and tinted the whole
    // composition by its ratio, which is why only the few accent blobs (all of
    // which happened to hash into one corner) showed any colour at all.
    float3 colSum = float3(0.0);
    float field = 0.0;

    for (int i = 0; i < 14; i++) {
        float fi = float(i) + 1.0;

        // Jittered grid placement: 5 columns x 3 rows guarantees the blobs —
        // and therefore the accent — cover the whole viewport instead of
        // clustering wherever the hash happened to put them.
        float col = wrapMod(fi, 5.0);
        float row = floor(fi / 5.0);
        float2 cell = float2((col + 0.5) / 5.0, (row + 0.5) / 3.0);
        float2 jitter = float2(hash1(fi * 3.1) - 0.5, hash1(fi * 7.7) - 0.5) * float2(0.26, 0.30);

        float sr = 0.13 + 0.15 * hash1(fi * 11.3);
        float sp = 0.04 + 0.06 * hash1(fi * 5.9);

        float2 c = cell + jitter
                 + float2(sin(t * sp + fi) * 0.05, cos(t * sp * 0.9 + fi * 1.7) * 0.045);

        float2 d = uv - c;
        d.x *= aspect;
        float g = exp(-dot(d, d) / max(sr * sr, 0.0001) * 1.4);

        // Four colour classes cycled by index, so each is spread across the
        // grid rather than concentrated. Every class is accent-derived: even
        // the "neutral" one carries a subtle tint, so no blob stays grey.
        float cls = wrapMod(fi, 4.0);
        float3 tint = mix(frost, uAccent, 0.14);                 // cls 3: near-neutral
        tint = mix(tint, uAccentAlt, stepf(1.5, cls) * stepf(cls, 2.5) * 0.75);  // cls 2: analogous
        tint = mix(tint, mix(frost, uAccent, 0.55), stepf(0.5, cls) * stepf(cls, 1.5)); // cls 1: soft
        tint = mix(tint, uAccent, stepf(cls, 0.5));              // cls 0: strong

        colSum += tint * g;
        field += g;
    }

    float cover = clamp(field * 0.55, 0.0, 1.0);
    float3 col = colSum / max(field, 0.001);

    float alpha = cover * uAlpha * (0.7 + 0.3 * uIntensity);
    return half4(half3(col * alpha), half(alpha));
}
"""

    /// Folded Glass — faceted acrylic sheets with a slow light sweep. The facet
    /// grid is a skewed triangular lattice, which reads the same as the
    /// hand-authored triangle mesh in the design without enumerating vertices.
    let private foldedSource =
        common
        + """
half4 main(float2 fragCoord) {
    float2 uv = fragCoord / uRes;
    float t = uTime;

    // A folded sheet is a piecewise-planar height field: abs() of a linear
    // function IS a crease. Summing a few gives broad connected planes meeting
    // along ridges. Using sqrt(x*x + k*k) instead of abs() rounds the crease,
    // so boundaries are softened rather than hard polygon seams.
    //
    // This replaces a per-triangle hash-coloured lattice, which read as low-poly
    // wallpaper because each triangle was lit independently of its neighbours.
    // Here lighting is derived from the SURFACE, so adjacent planes relate.
    float k = 0.055;                       // crease softness

    float2 d1 = float2( 0.94,  0.34);
    float2 d2 = float2(-0.42,  0.91);
    float2 d3 = float2( 0.62, -0.78);

    float o1 = 0.62 + 0.035 * sin(t * 0.045);
    float o2 = 0.74 + 0.030 * sin(t * 0.037 + 1.9);
    float o3 = -0.34 + 0.028 * sin(t * 0.029 + 3.4);

    float e1 = dot(uv, d1) - o1;
    float e2 = dot(uv, d2) - o2;
    float e3 = dot(uv, d3) - o3;

    float s1 = sqrt(e1 * e1 + k * k);
    float s2 = sqrt(e2 * e2 + k * k);
    float s3 = sqrt(e3 * e3 + k * k);

    // Gradient of the height field -> the plane normal. Constant within a
    // plane, turning smoothly across each ridge.
    float2 grad = d1 * (0.85 * e1 / s1) + d2 * (0.62 * e2 / s2) + d3 * (0.48 * e3 / s3);

    float3 n = normalize(float3(-grad.x, -grad.y, 1.35));
    float3 lightDir = normalize(float3(0.38, -0.66, 0.65));

    float diffuse = clamp(dot(n, lightDir), 0.0, 1.0);
    float sheen = pow(clamp(dot(n, normalize(lightDir + float3(0.0, 0.0, 1.0))), 0.0, 1.0), 14.0);

    // Fine highlight exactly along each ridge, and darkening in the valleys.
    float ridge = exp(-(e1 * e1) / (k * k * 1.6))
                + exp(-(e2 * e2) / (k * k * 1.6))
                + exp(-(e3 * e3) / (k * k * 1.6));
    ridge = clamp(ridge, 0.0, 1.0);

    float valley = clamp((s1 + s2 + s3) * 0.42, 0.0, 1.0);

    // Broad gradient across each plane so the sheet has depth, not flat fill.
    float sweep = 0.5 + 0.5 * sin((uv.x * 0.9 + uv.y * 0.6) * 3.14159 - t * 0.05);

    float lum = 0.16 + 0.52 * diffuse + 0.34 * sheen + 0.30 * ridge;
    lum *= 0.72 + 0.28 * sweep;
    lum *= 1.0 - 0.30 * valley;

    // Restrained tint: a hint of refraction from the normal's horizontal tilt.
    float3 sheet = float3(0.949, 0.925, 0.886);
    float refract = clamp(n.x * 0.5 + 0.5, 0.0, 1.0);
    float3 col = mix(sheet, uAccent, 0.20 + 0.22 * refract);
    col = mix(col, uAccentAlt, 0.18 * (1.0 - refract));
    col = mix(col, uAccentLight, 0.35 * ridge);

    float alpha = clamp(lum, 0.0, 1.0) * uAlpha * (0.7 + 0.3 * uIntensity);
    return half4(half3(col * alpha), half(alpha));
}
"""

    let private sourceFor mode =
        match mode with
        | Aurora -> Some auroraSource
        | LayeredWaves -> Some wavesSource
        | LiquidGlass -> Some liquidSource
        | FoldedGlass -> Some foldedSource
        | NoBackground -> None

    let private compile mode =
        match sourceFor mode with
        | None -> None
        | Some src ->
            try
                let mutable errors = ""
                let effect = SKRuntimeEffect.Create(src, &errors)

                if isNull (box effect) || not (System.String.IsNullOrEmpty errors) then
                    eprintfn "[render] shader %A failed to compile: %s" mode errors
                    None
                else
                    Diagnostics.countAlways "shader.compiled"
                    Some effect
            with ex ->
                Capabilities.reportFailure (sprintf "shader-compile-%A" mode) ex
                None

    /// Compiles every background shader once. Call during startup, before the
    /// first paint. Safe to call more than once; only the first does work.
    let prewarmAll () =
        lock gate (fun () ->
            if not prewarmed then
                prewarmed <- true

                for mode in [ Aurora; LayeredWaves; LiquidGlass; FoldedGlass ] do
                    match compile mode with
                    | Some effect -> effects[mode] <- effect
                    | None -> failed.Add mode |> ignore

                // Nothing compiled at all -> this machine cannot do Tier A.
                if effects.Count = 0 then
                    Capabilities.demoteTo RenderTier.B "no background shader compiled")

    let hasPrewarmed () = lock gate (fun () -> prewarmed)

    let compiledCount () = lock gate (fun () -> effects.Count)

    /// The compiled program for a mode, or None when it is unavailable and the
    /// caller should fall back to Tier B.
    let tryGet (mode: BackgroundMode) =
        lock gate (fun () ->
            match effects.TryGetValue mode with
            | true, effect ->
                Diagnostics.count Diagnostics.Names.shaderHit
                Some effect
            | _ when releasedUnderPressure && not (failed.Contains mode) ->
                // Released to relieve memory pressure, and now needed again.
                // §11.2: shaders are the last thing released and the recompile
                // is deliberate and logged, not a silent per-frame retry —
                // `failed` still suppresses modes that genuinely cannot compile.
                match compile mode with
                | Some effect ->
                    effects[mode] <- effect
                    Diagnostics.countAlways "shader.recompiled"
                    printfn "[render] recompiled %A shader after a memory-pressure release" mode
                    Some effect
                | None ->
                    failed.Add mode |> ignore
                    Diagnostics.count Diagnostics.Names.shaderMiss
                    None
            | _ ->
                Diagnostics.count Diagnostics.Names.shaderMiss
                None)

    /// Marks a mode unusable after a runtime failure so it is not retried at
    /// frame rate. One-way, like tier demotion.
    let markFailed (mode: BackgroundMode) =
        lock gate (fun () ->
            if effects.Remove mode then
                failed.Add mode |> ignore)

    /// Releases every compiled program. Called at shutdown, and as the very last
    /// step of memory-pressure relief (see MemoryPressure) — in which case the
    /// next use triggers a deliberate, logged recompile.
    let dispose () =
        lock gate (fun () ->
            for kv in effects do
                kv.Value.Dispose()

            effects.Clear()
            prewarmed <- false
            releasedUnderPressure <- false)

    /// Releases the compiled programs to relieve severe memory pressure.
    ///
    /// Distinct from `dispose`, which is shutdown: this one arms the on-demand
    /// recompile in `tryGet`, so Tier A comes back by itself instead of quietly
    /// degrading the session to Tier B for as long as the process lives.
    let releaseUnderPressure () =
        lock gate (fun () ->
            if effects.Count > 0 then
                for kv in effects do
                    kv.Value.Dispose()

                effects.Clear()
                prewarmed <- false
                releasedUnderPressure <- true
                Diagnostics.countAlways "shader.released")
