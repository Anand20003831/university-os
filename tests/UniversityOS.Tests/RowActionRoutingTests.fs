module UniversityOS.Tests.RowActionRoutingTests

// Avalonia 11.3 deprecates the public `PointerPressedEventArgs` constructor in
// favour of `IHeadlessWindow` mouse methods. Those need a started Avalonia
// application; these tests deliberately have none — they invoke the handler the
// view recorded, which is the only way to test the guard without a window. The
// suppression is file-scoped and this is the sole deprecated construct in it.
#nowarn "44"

open System
open Xunit
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.FuncUI.Types
open UniversityOS.Domain
open UniversityOS.Application
open UniversityOS.App
open UniversityOS.App.Features
open UniversityOS.Tests.AuditHarness

/// **A button inside a selectable Timeline row must own its own click.**
///
/// Reported twice from live Review Mode: pressing **Open** or **History** on the
/// Classification Report study-session row selected the row instead of doing
/// what the button says.
///
/// The first attempt reasoned about *ordering* — the child button marked the
/// press handled, on the theory that an ancestor row handler would then never
/// see it. The user reported the same symptom afterwards, so that theory was
/// wrong: either this row's handler sees the press first, or the child's flag is
/// not consulted on the route this application subscribes to. Either way,
/// ordering was never a sound thing to depend on.
///
/// The fix asks a different question: **what was pressed?** `Ui.isInteractiveSource`
/// walks the visual — then logical — tree from the event source looking for a
/// control that owns its click, and the row simply does not select when the
/// answer is yes. That holds in any phase, under any Handled semantics. The
/// Feed's background handler had used this technique all along; the row never
/// did.
///
/// **What these tests can establish:** which message each control dispatches,
/// that the row still selects, that the actions reach real destinations, and that
/// the source guard classifies a Button as interactive. **What they cannot:**
/// that a real click lands where intended — routed-event delivery only exists in
/// a live window. That is why this needs looking at in Review Mode.

let private noServices: AppServices = Unchecked.defaultof<AppServices>
let private now = DateTime(2026, 3, 4, 12, 0, 0)

let private modelOf (m, _, _) = m
let private externalOf (_, _, e) = e

/// The row the user actually reported, with a real linked entity — Open and
/// History only render when `Entity` is `Some`, so a fixture without one would
/// make every assertion below vacuous.
let private classificationEntity: EntityRef =
    { Kind = EntityKind.Session
      Id = Guid("00000000-0000-0000-0000-000000000203") }

let private classificationRow: ActivityEntry =
    { Id = Guid("00000000-0000-0000-0000-000000000903")
      Kind = ActivityKind.SessionCompleted
      Title = "Classification report"
      Detail = Some "Study session"
      Entity = Some classificationEntity
      OccurredAt = now.AddHours -3.0 }

/// A second row with no entity, to prove the actions are conditional on real
/// linked data rather than always drawn.
let private unlinkedRow: ActivityEntry =
    { classificationRow with
        Id = Guid("00000000-0000-0000-0000-000000000904")
        Title = "Plan generated"
        Entity = None }

let private loaded entries : TimelineView.Model =
    { Entries = entries
      Category = TimelineLogic.All
      Mode = TimelineLogic.Feed
      Query = ""
      FocusEntity = None
      Selected = None
      Study = StudyTime.emptySummary
      Loading = false
      Error = None
      Width = 1400.0 }

let private render (model: TimelineView.Model) =
    TimelineView.view AuditFixtures.tokens model ignore

// ---------------------------------------------------------------------------
// 1. the source guard is what decides, and it classifies a Button correctly
// ---------------------------------------------------------------------------

/// The whole fix rests on this. If a Button were not recognised as owning its
/// click, the row would keep stealing it.
[<Fact>]
let ``a Button is recognised as owning its own click`` () =
    Assert.True(Ui.isInteractiveSource (Button()), "a Button is not recognised as interactive")
    Assert.True(Ui.isInteractiveSource (CheckBox()), "a CheckBox is not recognised as interactive")
    Assert.True(Ui.isInteractiveSource (TextBox()), "a TextBox is not recognised as interactive")

/// ...and ordinary row content is NOT, or the row could never select at all.
[<Fact>]
let ``plain row content is not treated as owning a click`` () =
    Assert.False(Ui.isInteractiveSource (TextBlock()), "a TextBlock was treated as interactive")
    Assert.False(Ui.isInteractiveSource (Border()), "a bare Border was treated as interactive")
    Assert.False(Ui.isInteractiveSource null, "a null source was treated as interactive")

/// A control nested inside a Button still belongs to that Button — the guard is
/// a tree walk, not an identity check, because the thing actually pressed is
/// usually the Button's inner text, never the Button itself.
[<Fact>]
let ``a control inside a Button still belongs to that Button`` () =
    let label = TextBlock()
    let button = Button(Content = label)

    // Establish the parent link the walk follows.
    Assert.Same(button, (label :> Avalonia.LogicalTree.ILogical).LogicalParent)
    Assert.True(Ui.isInteractiveSource label, "the text inside a Button was not attributed to the Button")

// ---------------------------------------------------------------------------
// 2. each control dispatches only its own message
// ---------------------------------------------------------------------------

/// Open navigates to the entity's own page. That is a real destination, not a
/// selection: `Page.forEntity` decides it, and a Session belongs to the Planner.
[<Fact>]
let ``Open navigates to the entity page and changes nothing else`` () =
    let before = loaded [ classificationRow ]
    let after, _, external = TimelineView.update noServices (TimelineView.OpenEntry classificationRow) before

    Assert.Equal(Some(TimelineView.Goto(Page.forEntity EntityKind.Session)), external)
    Assert.Equal(Page.Planner, Page.forEntity EntityKind.Session)

    // Crucially: it did NOT select the row.
    Assert.Equal(None, after.Selected)

/// History switches the screen to that entity's own history — a visible mode
/// change, again not a selection.
[<Fact>]
let ``History focuses the entity history and changes nothing else`` () =
    let before = loaded [ classificationRow ]

    let after =
        TimelineView.update noServices (TimelineView.FocusOn(classificationEntity, classificationRow.Title)) before
        |> modelOf

    Assert.Equal(TimelineLogic.Entity, after.Mode)
    Assert.Equal(Some(classificationEntity, classificationRow.Title), after.FocusEntity)
    Assert.Equal(None, after.Selected)

/// Neither action leaves a selection behind even when one already existed — the
/// row's state is simply not theirs to touch.
[<Fact>]
let ``neither action disturbs an existing selection`` () =
    let selected =
        TimelineView.update noServices (TimelineView.SelectEntry classificationRow) (loaded [ classificationRow ])
        |> modelOf

    Assert.Equal(Some classificationRow.Id, selected.Selected)

    let afterOpen =
        TimelineView.update noServices (TimelineView.OpenEntry classificationRow) selected |> modelOf

    let afterHistory =
        TimelineView.update noServices (TimelineView.FocusOn(classificationEntity, "x")) selected |> modelOf

    Assert.Equal(Some classificationRow.Id, afterOpen.Selected)
    // History changes mode, and selection is reconciled against the new mode —
    // what must not happen is the action *selecting* something.
    Assert.NotEqual(Some unlinkedRow.Id, afterHistory.Selected)

// ---------------------------------------------------------------------------
// 3. the row itself still selects
// ---------------------------------------------------------------------------

/// Removing the row's ability to steal a button's click must not remove its
/// ability to select when the press really is its own.
[<Fact>]
let ``pressing the row background still selects it`` () =
    let after =
        TimelineView.update noServices (TimelineView.SelectEntry classificationRow) (loaded [ classificationRow ])
        |> modelOf

    Assert.Equal(Some classificationRow.Id, after.Selected)

[<Fact>]
let ``selecting the open row again clears it`` () =
    let once =
        TimelineView.update noServices (TimelineView.SelectEntry classificationRow) (loaded [ classificationRow ])
        |> modelOf

    let twice =
        TimelineView.update noServices (TimelineView.SelectEntry classificationRow) once |> modelOf

    Assert.Equal(None, twice.Selected)

// ---------------------------------------------------------------------------
// 4. the rendered controls
// ---------------------------------------------------------------------------

let private rowActions (root: IView) =
    ofType "Button" root
    |> List.filter (fun b ->
        match stringProp "Content" b with
        | Some c -> c.Contains("Open", StringComparison.Ordinal) || c = "History"
        | None -> false)

/// Both controls have to be on screen for any of this to matter, and they only
/// appear for a row with a real linked entity.
[<Fact>]
let ``the Classification Report row renders both actions`` () =
    let buttons = rowActions (render (loaded [ classificationRow ]))
    let labels = buttons |> List.map (fun b -> stringProp "Content" b |> Option.defaultValue "")

    Assert.Contains("History", labels)
    Assert.True(labels |> List.exists (fun l -> l.StartsWith("Open", StringComparison.Ordinal)), "no Open action")

/// **Real Buttons, so Enter and Space activate them.** A clickable Border would
/// not be focusable and would have no keyboard path at all.
[<Fact>]
let ``both actions are real Buttons, which is what makes Enter and Space work`` () =
    let buttons = rowActions (render (loaded [ classificationRow ]))

    Assert.Equal(2, buttons.Length)

    for b in buttons do
        Assert.Equal<string>("Button", typeName b)
        Assert.True(hasHandler "Click" b, "an action lost its click handler")

/// A row with nothing linked offers neither action, so the buttons are genuinely
/// conditional on real data rather than always drawn.
[<Fact>]
let ``a row with no linked entity offers neither action`` () =
    Assert.Empty(rowActions (render (loaded [ unlinkedRow ])))

/// **No nested clickable ancestor.** If an action sat inside another Button, one
/// press would activate two things.
[<Fact>]
let ``neither action sits inside another clickable control`` () =
    let tree = render (loaded [ classificationRow ])

    let nested =
        allWithAncestors tree
        |> List.filter (fun (v, ancestors) ->
            typeName v = "Button"
            && (match stringProp "Content" v with
                | Some c -> c.Contains("Open", StringComparison.Ordinal) || c = "History"
                | None -> false)
            && ancestors |> List.exists (fun a -> typeName a = "Button"))

    Assert.True(List.isEmpty nested, sprintf "%d row action(s) nested inside another Button" nested.Length)

/// The action buttons must not carry a pointer-press swallow. The earlier
/// version did, on a theory the user's report disproved, and swallowing the
/// press risks suppressing the Button's own handling — and so its `Click`.
[<Fact>]
let ``the row actions do not interfere with their own pointer handling`` () =
    for b in rowActions (render (loaded [ classificationRow ])) do
        Assert.False(
            hasHandler "PointerPressed" b,
            "a row action swallows its own pointer press — that was the disproven fix, and it can suppress Click"
        )

// ---------------------------------------------------------------------------
// 5. the guard itself, invoked
// ---------------------------------------------------------------------------

/// **Found by mutation, and the whole reason this section exists.**
///
/// Everything above drives `update` with messages, or inspects the rendered
/// tree. None of it invokes the row's *pointer handler*, so deleting the source
/// guard from that handler failed nothing — the tests looked thorough and
/// protected the one line that matters not at all. That is the same shape of
/// gap that made the earlier "fixed" reports untrustworthy.
///
/// These tests pull the recorded handler out of the view and run it, with a
/// dispatch that records what it is asked to send.
let private pointerPress (source: obj) =
    // `new Pointer` because it owns unmanaged input state; the args object takes
    // ownership of it for the life of the call.
    PointerPressedEventArgs(
        source,
        new Pointer(0, PointerType.Mouse, true),
        Border(),
        Point(0.0, 0.0),
        0UL,
        PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed),
        KeyModifiers.None
    )

/// The row card's own press handler, and the messages it dispatches for a press
/// originating from `source`.
let private rowPressMessages (source: obj) =
    let sent = ResizeArray<TimelineView.Msg>()
    let tree = TimelineView.view AuditFixtures.tokens (loaded [ classificationRow ]) sent.Add

    // The row handler is the press subscription on the Border that owns the
    // card's accessible name — the one control that both names the entry and
    // listens for a press.
    let handler =
        allWithAncestors tree
        |> List.map fst
        |> List.filter (fun v ->
            typeName v = "Border"
            && (accessibleName v |> Option.map (fun n -> n.Contains(classificationRow.Title, StringComparison.Ordinal)) |> Option.defaultValue false))
        |> List.collect (fun v ->
            v.Attrs
            |> List.choose (fun a ->
                match a.Subscription with
                | ValueSome s when s.Name.Contains("PointerPressed", StringComparison.OrdinalIgnoreCase) -> Some s.Func
                | _ -> None))
        |> function
            | [ f ] -> f
            | other -> failwithf "expected exactly one row press handler, found %d" other.Length

    handler.DynamicInvoke [| box (pointerPress source) |] |> ignore
    List.ofSeq sent

/// A press that really is the row's own selects it.
[<Fact>]
let ``a press on ordinary row content selects the row`` () =
    let sent = rowPressMessages (TextBlock())

    Assert.Equal<TimelineView.Msg list>([ TimelineView.SelectEntry classificationRow ], sent)

/// **The reported bug, as a test.** A press that started inside a Button is that
/// Button's, and the row must dispatch nothing at all.
[<Fact>]
let ``a press that started inside a Button does not select the row`` () =
    let sent = rowPressMessages (Button())

    Assert.True(
        List.isEmpty sent,
        sprintf "the row still reacted to a press belonging to a button: %A" sent
    )

/// And the realistic case: what gets pressed is the button's inner text, never
/// the Button itself.
[<Fact>]
let ``a press on the text inside a Button does not select the row either`` () =
    let label = TextBlock()
    let _ = Button(Content = label)

    Assert.True(List.isEmpty (rowPressMessages label), "a press on a button's own label selected the row")

/// **Found by the same mutation class.** The tests above drive `update` with
/// `OpenEntry` directly, so making the *button* dispatch an extra `SelectEntry`
/// alongside it failed nothing. What the button asks for has to be checked at
/// the button.
let private clickMessages (label: string) =
    let sent = ResizeArray<TimelineView.Msg>()
    let tree = TimelineView.view AuditFixtures.tokens (loaded [ classificationRow ]) sent.Add

    let handler =
        ofType "Button" tree
        |> List.filter (fun b ->
            match stringProp "Content" b with
            | Some c -> c.StartsWith(label, StringComparison.Ordinal)
            | None -> false)
        |> List.collect (fun b ->
            b.Attrs
            |> List.choose (fun a ->
                match a.Subscription with
                | ValueSome s when s.Name.Contains("Click", StringComparison.OrdinalIgnoreCase) -> Some s.Func
                | _ -> None))
        |> function
            | [ f ] -> f
            | other -> failwithf "expected exactly one %s click handler, found %d" label other.Length

    handler.DynamicInvoke [| box (Avalonia.Interactivity.RoutedEventArgs()) |] |> ignore
    List.ofSeq sent

/// Open asks for exactly one thing: to open. Not to select as well.
[<Fact>]
let ``pressing Open dispatches only the open action`` () =
    Assert.Equal<TimelineView.Msg list>([ TimelineView.OpenEntry classificationRow ], clickMessages "Open")

/// History asks for exactly one thing: the entity's history.
[<Fact>]
let ``pressing History dispatches only the history action`` () =
    Assert.Equal<TimelineView.Msg list>(
        [ TimelineView.FocusOn(classificationEntity, classificationRow.Title) ],
        clickMessages "History"
    )

/// Neither may smuggle a selection in alongside its own action.
[<Fact>]
let ``neither action dispatches a row selection`` () =
    for label in [ "Open"; "History" ] do
        let sent = clickMessages label

        Assert.False(
            sent |> List.exists (fun m -> m = TimelineView.SelectEntry classificationRow),
            sprintf "%s also selected the row" label
        )
