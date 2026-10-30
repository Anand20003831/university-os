namespace UniversityOS.App.Rendering

open System
open Avalonia.Controls

/// One tick of the master clock.
type FrameTick =
    { /// Monotonic seconds since the clock started. Never rewinds.
      Time: float
      /// Seconds since the previous tick, clamped to a sane maximum so a
      /// suspended window cannot produce an enormous jump on resume.
      Delta: float
      FrameId: int64 }

/// THE master animation clock. One `RequestAnimationFrame` registration for the
/// whole application, vsync-aligned, feeding every time-based visual.
///
/// Independent per-control clocks drift apart, so effects that should pulse
/// together gradually separate and the interface stops feeling like one object.
/// A single monotonic source also makes rendering deterministic for tests:
/// inject a fixed time and the frame is reproducible.
///
/// The clock STOPS rather than pauses when motion is not wanted. A stopped clock
/// costs nothing; a paused animation still wakes the compositor every frame.
type private FrameClockImpl() =

    let subscribers = ResizeArray<int * (FrameTick -> unit)>()
    let gate = obj ()

    let mutable topLevel: TopLevel option = None
    let mutable running = false
    let mutable scheduled = false
    let mutable nextId = 0
    let mutable frameId = 0L
    let mutable time = 0.0
    let mutable lastRaw = TimeSpan.Zero
    let mutable started = false

    /// A frame longer than this is treated as a discontinuity (window restored,
    /// machine resumed) and contributes a nominal delta instead of a huge one.
    let maxDelta = 0.1

    member _.IsRunning = running
    member _.Time = time
    member _.FrameId = frameId

    member this.Attach(tl: TopLevel) =
        topLevel <- Some tl
        // A caller may have asked us to run before any window existed, in which
        // case scheduling was a no-op. Pick it up now.
        if running then this.Schedule()

    member _.HasTopLevel = topLevel.IsSome

    member this.Subscribe(handler: FrameTick -> unit) =
        let id =
            lock gate (fun () ->
                nextId <- nextId + 1
                subscribers.Add(nextId, handler)
                nextId)

        { new IDisposable with
            member _.Dispose() =
                lock gate (fun () ->
                    subscribers.RemoveAll(fun (i, _) -> i = id) |> ignore)

                // Stop the loop once nothing is listening.
                if lock gate (fun () -> subscribers.Count = 0) then
                    this.SetRunning false }

    member this.SetRunning(value: bool) =
        if value <> running then
            running <- value

            // Reset the delta baseline so resuming does not report the whole
            // paused interval as one frame.
            if running then started <- false

        // Deliberately outside the change check. A control can set us running
        // before it is attached to a visual tree — Avalonia raises property
        // changes before OnAttachedToVisualTree — and that first Schedule is a
        // no-op because there is no TopLevel yet. Only re-scheduling when the
        // value *changes* would then leave the clock permanently idle.
        // Schedule() is already idempotent via its `scheduled` guard.
        if running then this.Schedule()

    member private this.Schedule() =
        if running && not scheduled then
            match topLevel with
            | Some tl ->
                scheduled <- true
                tl.RequestAnimationFrame(fun ts -> this.OnFrame ts)
            | None -> ()

    member private this.OnFrame(raw: TimeSpan) =
        scheduled <- false

        Diagnostics.count Diagnostics.Names.clockTick

        if running then
            let delta =
                if not started then
                    started <- true
                    lastRaw <- raw
                    0.0
                else
                    let d = (raw - lastRaw).TotalSeconds
                    lastRaw <- raw
                    if d < 0.0 || d > maxDelta then maxDelta else d

            time <- time + delta
            frameId <- frameId + 1L

            if delta > 0.0 then
                Diagnostics.recordFrame(delta * 1000.0)

            let tick =
                { Time = time
                  Delta = delta
                  FrameId = frameId }

            // Snapshot under the lock, invoke outside it: a handler may
            // unsubscribe (on detach) while we are iterating.
            let handlers = lock gate (fun () -> subscribers.ToArray())

            for (_, handler) in handlers do
                try
                    handler tick
                with ex ->
                    Capabilities.reportFailure "frame-clock-subscriber" ex

            this.Schedule()

[<RequireQualifiedAccess>]
module FrameClock =

    let private impl = FrameClockImpl()

    /// Called once when the main window exists. Without a TopLevel the clock
    /// cannot schedule frames and simply never runs.
    let attach (topLevel: TopLevel) = impl.Attach topLevel

    let hasTopLevel () = impl.HasTopLevel

    /// Subscribe to frames. Dispose on detach — an undisposed subscription keeps
    /// the control alive and the loop spinning.
    let subscribe (handler: FrameTick -> unit) = impl.Subscribe handler

    /// The single pause switch. False stops the loop entirely.
    let setRunning (value: bool) = impl.SetRunning value

    let isRunning () = impl.IsRunning

    /// Monotonic seconds since start.
    let time () = impl.Time

    let frameId () = impl.FrameId
