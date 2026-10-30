module UniversityOS.Tests.AssignmentsPresentationTests

open System
open Xunit
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Design
open UniversityOS.App.Features

/// Assignments presentation: the default expansion, and the entity glyph.

let private now = DateTime(2026, 3, 4, 12, 0, 0)
let private weekStart = StudyTime.weekStart DayOfWeek.Monday now

let private assignmentId (n: int) =
    AssignmentId(Guid(sprintf "00000000-0000-0000-0000-%012d" n))

let private facts (n: int) (title: string) : AssignmentsLogic.Facts =
    let a =
        { Assignment.create now title with
            Id = assignmentId n
            Deadline = Some(now.AddDays(float n))
            Status = AssignmentStatus.InProgress }

    AssignmentsLogic.Facts.ofAssignment a

let private three = [ facts 1 "First"; facts 2 "Second"; facts 3 "Third" ]

// ---------------------------------------------------------------------------
// the default expansion
// ---------------------------------------------------------------------------

/// The screen opens its first visible assignment, so the page does not arrive
/// as a list of closed rows.
[<Fact>]
let ``the first visible assignment opens by default`` () =
    Assert.Equal(Some(assignmentId 1), AssignmentsLogic.expansionFor false None three)

[<Fact>]
let ``a single assignment opens`` () =
    Assert.Equal(Some(assignmentId 2), AssignmentsLogic.expansionFor false None [ facts 2 "Only" ])

[<Fact>]
let ``an empty list opens nothing`` () =
    Assert.Equal(None, AssignmentsLogic.expansionFor false None [])
    Assert.Equal(None, AssignmentsLogic.expansionFor true None [])

/// **An explicit selection always wins.** The default may never override what
/// the user chose.
[<Fact>]
let ``an explicit selection is kept, not replaced by the first card`` () =
    Assert.Equal(Some(assignmentId 3), AssignmentsLogic.expansionFor true (Some(assignmentId 3)) three)
    Assert.Equal(Some(assignmentId 3), AssignmentsLogic.expansionFor false (Some(assignmentId 3)) three)

/// **The reason the `chosen` flag exists.** Without it the model cannot tell
/// "nothing opened yet" from "the user closed the open one", so collapsing the
/// first card would make it spring straight back open on the next render.
[<Fact>]
let ``a deliberate collapse stays collapsed`` () =
    Assert.Equal(None, AssignmentsLogic.expansionFor true None three)

/// A choice that has been filtered away or deleted falls back deterministically
/// rather than leaving the screen with nothing open.
[<Fact>]
let ``a selection that is no longer visible falls back to the first card`` () =
    let filtered = [ facts 2 "Second"; facts 3 "Third" ]
    Assert.Equal(Some(assignmentId 2), AssignmentsLogic.expansionFor true (Some(assignmentId 1)) filtered)

[<Fact>]
let ``a selection whose assignment was deleted falls back safely`` () =
    Assert.Equal(None, AssignmentsLogic.expansionFor true (Some(assignmentId 1)) [])

/// Only ever one card open — the rule the whole screen is built on.
[<Fact>]
let ``the default opens exactly one card`` () =
    let opened = AssignmentsLogic.expansionFor false None three
    Assert.True opened.IsSome
    Assert.Equal(Some(assignmentId 1), opened)

// ---------------------------------------------------------------------------
// the entity glyph
// ---------------------------------------------------------------------------

/// **The character was already right; the FONT was not.**
///
/// `☑` (U+2611) carries emoji presentation by default, and the UI font stack
/// contains no text glyph for it — so Windows fell back to Segoe UI Emoji and
/// painted a full-colour pictograph that ignored the foreground brush entirely.
/// That is the "filled purple assignment glyph" from the runtime review.
///
/// Appending VARIATION SELECTOR-15 requests the monochrome text form, so the
/// glyph inherits whatever colour the caller sets.
let private selector = string Typography.textPresentation

[<Fact>]
let ``the Assignment entity glyph is the unfilled checkbox in text presentation`` () =
    let glyph = GraphLogic.kindGlyph EntityKind.Assignment

    Assert.StartsWith("☑", glyph, StringComparison.Ordinal)

    Assert.True(
        glyph.EndsWith(selector, StringComparison.Ordinal),
        "the glyph carries no text-presentation selector, so Windows will draw it as a colour emoji"
    )

[<Fact>]
let ``the Assignments navigation entry uses the same glyph`` () =
    let _, glyph, label =
        Page.all |> List.find (fun (page, _, _) -> page = Page.Assignments)

    Assert.Equal<string>(GraphLogic.kindGlyph EntityKind.Assignment, glyph)
    Assert.Equal<string>("Assignments", label)

/// The glyph is not the identity. Accessible text says the word, so a screen
/// reader never announces a symbol on its own.
[<Fact>]
let ``the accessible identity of an Assignment is the word, not the glyph`` () =
    Assert.Equal<string>("Assignment", GraphLogic.kindLabel EntityKind.Assignment)
    Assert.NotEqual<string>(GraphLogic.kindGlyph EntityKind.Assignment, GraphLogic.kindLabel EntityKind.Assignment)

/// No other entity may share the Assignment glyph, or the two would be
/// indistinguishable wherever entities are listed together.
[<Fact>]
let ``no other entity kind uses the Assignment glyph`` () =
    let others =
        [ EntityKind.Module
          EntityKind.Note
          EntityKind.Document
          EntityKind.Lecture
          EntityKind.RevisionItem
          EntityKind.Event
          EntityKind.InboxItem
          EntityKind.Session ]

    for kind in others do
        Assert.NotEqual<string>(GraphLogic.kindGlyph EntityKind.Assignment, GraphLogic.kindGlyph kind)

/// **The trap this nearly shipped through.** `String.EndsWith(string)` is
/// culture-sensitive by default, and U+FE0E is a zero-width *ignorable*
/// character under those rules — so a naive `EndsWith` returns true for a glyph
/// that does not carry the selector at all, and the helper silently returns
/// every glyph unchanged while looking correct. The ordinal comparison is
/// load-bearing, and this is what holds it in place.
[<Fact>]
let ``asText actually appends the selector rather than believing it is present`` () =
    let bare = "\u2611"
    let converted = Typography.asText bare

    Assert.Equal(2, converted.Length)
    Assert.Equal<string>(bare + selector, converted)

/// Idempotent: applying it twice must not stack selectors.
[<Fact>]
let ``asText is idempotent`` () =
    let once = Typography.asText "\u2611"
    Assert.Equal<string>(once, Typography.asText once)

[<Fact>]
let ``asText leaves an empty glyph alone`` () =
    Assert.Equal<string>("", Typography.asText "")
