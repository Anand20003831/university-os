namespace UniversityOS.App.Rendering

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Threading

/// A smoothed timing channel. Diagnostics values are advisory: they are written
/// from the render thread and read from the UI thread without synchronisation,
/// because a torn read of a display statistic is harmless and locking the frame
/// path to protect one would not be.
type TimingChannel() =
    let mutable ewma = 0.0
    let mutable worst = 0.0
    let mutable samples = 0L

    member _.Ewma = ewma
    member _.Worst = worst
    member _.Samples = samples

    member _.Add(ms: float) =
        ewma <- (if ewma = 0.0 then ms else ewma * 0.9 + ms * 0.1)
        worst <- max worst ms
        samples <- samples + 1L

    member _.Reset() =
        ewma <- 0.0
        worst <- 0.0
        samples <- 0L

/// Named cumulative counters.
///
/// Deliberately cumulative rather than "per frame". The previous design reset a
/// single global draw counter from the UI thread on each clock tick while the
/// render thread incremented it, so the number was both racy and unattributed —
/// it could not distinguish one control from another, and it read the same on a
/// page with four glass panels as on a page with none. Cumulative counters with
/// explicit names cannot be misread that way.
type Counters() =
    let map = ConcurrentDictionary<string, int64 ref>()

    member _.Add(name: string, amount: int64) =
        let cell = map.GetOrAdd(name, (fun _ -> ref 0L))
        Interlocked.Add(&cell.contents, amount) |> ignore

    member this.Increment(name: string) = this.Add(name, 1L)

    member _.Value(name: string) =
        match map.TryGetValue name with
        | true, cell -> cell.Value
        | _ -> 0L

    member _.Snapshot() =
        map |> Seq.map (fun kv -> kv.Key, kv.Value.Value) |> Seq.sortBy fst |> List.ofSeq

    member _.Reset() =
        for kv in map do
            Interlocked.Exchange(&kv.Value.contents, 0L) |> ignore

/// Developer diagnostics for the rendering system.
///
/// Every collection point is guarded by `enabled`, a flag set once at startup.
/// When it is false the guarded body is skipped entirely: no stopwatch is
/// started, no counter incremented and nothing is allocated. The residual cost
/// is a single, perfectly-predicted boolean test per call site.
[<RequireQualifiedAccess>]
module Diagnostics =

    let mutable private enabled = false

    let setEnabled (value: bool) = enabled <- value
    let isEnabled () = enabled

    // ---- counter names (one place, so reports and tests agree) -------------

    module Names =
        /// One per custom draw operation actually rendered, per operation name.
        /// "draw.background-host" doubles as the render-pass count on any page
        /// hosting the background, since it draws exactly once per pass.
        let drawPrefix = "draw."
        let backgroundDraw = "draw.background-host"
        let glassDraw = "draw.glass-panel"

        /// Backdrop offscreen generated (one per background paint).
        let backdropGenerated = "backdrop.generated"
        /// Shared blur passes executed (one per backdrop generation).
        let backdropBlur = "backdrop.blur"
        /// Background painted but no backdrop published (mode off / surface failed).
        let backdropSkipped = "backdrop.skipped"

        /// A glass panel sampled the shared backdrop.
        let glassSampleHit = "glass.sample.hit"
        /// A glass panel found no backdrop and fell back to tint only.
        let glassSampleMiss = "glass.sample.miss"

        let shaderHit = "shader.cache.hit"
        let shaderMiss = "shader.cache.miss"
        let painterCreated = "painter.created"
        let clockTick = "clock.tick"
        let fallbackPaint = "paint.fallback"

    // ---- channels ----------------------------------------------------------

    let frame = TimingChannel()
    let layout = TimingChannel()
    let render = TimingChannel()
    let shader = TimingChannel()
    let blur = TimingChannel()

    let counters = Counters()

    let mutable private gpuBytes = 0L
    let mutable private gpuResources = 0
    let mutable private gpuLimitBytes = 0L

    let gpuMemoryBytes () = gpuBytes
    let gpuResourceCount () = gpuResources
    let gpuMemoryLimitBytes () = gpuLimitBytes

    // ---- collection points -------------------------------------------------

    /// Returns a timestamp, or 0 when diagnostics are off. Deliberately not a
    /// higher-order "measure" helper: that would allocate a closure per frame.
    let startTimer () =
        if enabled then Stopwatch.GetTimestamp() else 0L

    let stopTimer (channel: TimingChannel) (started: int64) =
        if enabled && started <> 0L then
            let elapsed = Stopwatch.GetTimestamp() - started
            channel.Add(float elapsed * 1000.0 / float Stopwatch.Frequency)

    /// Counts one rendered draw operation, attributed to its operation name.
    let countDraw (operationName: string) =
        if enabled then
            counters.Increment(Names.drawPrefix + operationName)

    let count (name: string) = if enabled then counters.Increment name

    /// Pressure events are always counted: they are rare and explain later
    /// cache misses even when the overlay is off.
    let countAlways (name: string) = counters.Increment name

    let reportGpuMemory (bytes: int64) (resources: int) (limit: int64) =
        if enabled then
            gpuBytes <- bytes
            gpuResources <- resources
            gpuLimitBytes <- limit

    /// The surface currently on screen. Set by the shell on navigation so a
    /// report can be attributed to a page rather than guessed at.
    let mutable private surface = "(unset)"

    let setSurface (name: string) = surface <- name
    let currentSurface () = surface

    let recordFrame (deltaMs: float) = if enabled then frame.Add deltaMs
    let recordLayout (ms: float) = if enabled then layout.Add ms

    /// Frames per second implied by the smoothed frame time.
    let fps () =
        if frame.Ewma <= 0.0 then 0.0 else 1000.0 / frame.Ewma

    // ---- managed allocation / GC ------------------------------------------

    type GcSnapshot =
        { AllocatedBytes: int64
          Gen0: int
          Gen1: int
          Gen2: int
          Timestamp: int64 }

    let gcSnapshot () =
        { AllocatedBytes = GC.GetTotalAllocatedBytes false
          Gen0 = GC.CollectionCount 0
          Gen1 = GC.CollectionCount 1
          Gen2 = GC.CollectionCount 2
          Timestamp = Stopwatch.GetTimestamp() }

    /// Allocation rate and collection deltas between two snapshots.
    let gcDelta (before: GcSnapshot) (after: GcSnapshot) =
        let seconds =
            float (after.Timestamp - before.Timestamp) / float Stopwatch.Frequency

        let bytes = after.AllocatedBytes - before.AllocatedBytes

        {| Seconds = seconds
           Bytes = bytes
           BytesPerSecond = (if seconds > 0.0 then float bytes / seconds else 0.0)
           Gen0 = after.Gen0 - before.Gen0
           Gen1 = after.Gen1 - before.Gen1
           Gen2 = after.Gen2 - before.Gen2 |}

    /// Clears every counter and timing channel. Used to start a clean
    /// measurement window after warm-up.
    let resetMeasurements () =
        counters.Reset()
        frame.Reset()
        layout.Reset()
        render.Reset()
        shader.Reset()
        blur.Reset()
