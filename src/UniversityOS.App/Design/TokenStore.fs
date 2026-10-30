namespace UniversityOS.App.Design

/// The one piece of mutable design state in the application.
///
/// FuncUI views receive Tokens through the Elmish model, but custom-drawn
/// controls sit deep in the visual tree and are constructed by Avalonia, not by
/// a view function — they read the current tokens from here and copy them into
/// their immutable frame snapshot on the UI thread.
///
/// Tokens is a record (a reference type), so publishing is a single atomic
/// reference assignment: a reader can never observe a half-updated token set.
type private TokenStoreImpl() =
    let changed = Event<Tokens>()

    member val Current: Tokens = Tokens.defaults with get, set

    member _.Changed = changed.Publish

    member this.Publish(tokens: Tokens) =
        this.Current <- tokens
        changed.Trigger tokens

[<RequireQualifiedAccess>]
module TokenStore =

    let private store = TokenStoreImpl()

    /// The current tokens. Safe to read from any thread.
    let current () = store.Current

    /// Raised on the UI thread whenever appearance changes. Custom controls
    /// subscribe on attach and unsubscribe on detach.
    let changed = store.Changed

    /// Publishes a new token set. Call on the UI thread only.
    let publish (tokens: Tokens) = store.Publish tokens
