module UniversityOS.Tests.SettingsLogicTests

open System
open Xunit
open UniversityOS.Domain
open UniversityOS.App
open UniversityOS.App.Features

/// The Settings decision layer.
///
/// Settings owns `UserSettings` and one field of `UiState`. Most of what these
/// tests protect is what it must NOT do: never touch the retired theme field,
/// never claim a credential exists, never describe a cosmetic setting as though
/// it changed scheduling.

let private defaults = UserSettings.defaults

// ---------------------------------------------------------------------------
// 1. categories
// ---------------------------------------------------------------------------

[<Fact>]
let ``the eight categories are the redesign's, in order`` () =
    Assert.Equal<SettingsLogic.Category list>(
        [ SettingsLogic.Category.Profile
          SettingsLogic.Category.Planner
          SettingsLogic.Category.Ai
          SettingsLogic.Category.Appearance
          SettingsLogic.Category.Notifications
          SettingsLogic.Category.Integrations
          SettingsLogic.Category.Privacy
          SettingsLogic.Category.Advanced ],
        SettingsLogic.Category.all
    )

    Assert.Equal(8, SettingsLogic.Category.all.Length)

[<Fact>]
let ``every category has a distinct label, glyph and caption`` () =
    let labels = SettingsLogic.Category.all |> List.map SettingsLogic.Category.label
    let glyphs = SettingsLogic.Category.all |> List.map SettingsLogic.Category.glyph

    Assert.Equal(8, (List.distinct labels).Length)
    Assert.Equal(8, (List.distinct glyphs).Length)

    for c in SettingsLogic.Category.all do
        Assert.False(String.IsNullOrWhiteSpace(SettingsLogic.categoryCaption c))

[<Fact>]
let ``only three categories edit anything`` () =
    // The rest are handoffs or plain statements of fact, and must not be dressed
    // up with controls.
    let editable = SettingsLogic.Category.all |> List.filter SettingsLogic.isEditable

    Assert.Equal<SettingsLogic.Category list>(
        [ SettingsLogic.Category.Profile; SettingsLogic.Category.Planner; SettingsLogic.Category.Ai ],
        editable
    )

    for c in
        [ SettingsLogic.Category.Appearance
          SettingsLogic.Category.Notifications
          SettingsLogic.Category.Integrations
          SettingsLogic.Category.Privacy
          SettingsLogic.Category.Advanced ] do
        Assert.False(SettingsLogic.isEditable c, string c)

// ---------------------------------------------------------------------------
// 2. the retired theme field
// ---------------------------------------------------------------------------

[<Fact>]
let ``the retired theme field is excluded from every comparison`` () =
    // Changing ONLY the retired field must not make the form dirty. Including it
    // would let a stale legacy value mark the draft dirty forever.
    let a = defaults
    let b = { defaults with Theme = ThemeMode.Light }

    Assert.NotEqual<ThemeMode>(a.Theme, b.Theme)
    Assert.True(SettingsLogic.sameSettings a b)
    Assert.False(SettingsLogic.isDirty a b)

[<Fact>]
let ``every other field does count toward dirty state`` () =
    let changes =
        [ { defaults with UserName = "Maya" }
          { defaults with WorkdayStartHour = 8 }
          { defaults with WorkdayEndHour = 22 }
          { defaults with SessionMinutes = 45 }
          { defaults with BreakMinutes = 15 }
          { defaults with MaxDailyStudyHours = 8.0 }
          { defaults with DeadlineBufferHours = 24.0 }
          { defaults with PlannerHorizonDays = 21 }
          { defaults with AiModel = "claude-opus-5" }
          { defaults with AiAutoApply = true } ]

    for changed in changes do
        Assert.True(SettingsLogic.isDirty defaults changed, sprintf "%A" changed)

// ---------------------------------------------------------------------------
// 3. validation and save eligibility
// ---------------------------------------------------------------------------

[<Fact>]
let ``the defaults are valid`` () = Assert.Equal(None, SettingsLogic.validationError defaults)

[<Fact>]
let ``a study day must end after it starts`` () =
    let inverted = { defaults with WorkdayStartHour = 21; WorkdayEndHour = 9 }
    Assert.Equal(Some "The study day must end after it starts.", SettingsLogic.validationError inverted)

    // Equal is not a range either.
    let equal = { defaults with WorkdayStartHour = 12; WorkdayEndHour = 12 }
    Assert.True((SettingsLogic.validationError equal).IsSome)

[<Fact>]
let ``each numeric rule is enforced with its own message`` () =
    let cases =
        [ { defaults with SessionMinutes = 1 }, "session"
          { defaults with BreakMinutes = -1 }, "break"
          { defaults with MaxDailyStudyHours = 0.0 }, "daily limit"
          { defaults with PlannerHorizonDays = 0 }, "planner"
          { defaults with AiModel = "  " }, "model" ]

    for draft, word in cases do
        match SettingsLogic.validationError draft with
        | Some message -> Assert.Contains(word, message.ToLowerInvariant())
        | None -> failwithf "%s should have been refused" word

[<Fact>]
let ``Save needs a change, validity and a settled write`` () =
    let dirty = { defaults with UserName = "Maya" }

    Assert.True(SettingsLogic.canSave false defaults dirty)

    // Clean.
    Assert.False(SettingsLogic.canSave false defaults defaults)
    Assert.Equal(Some "No changes to save.", SettingsLogic.saveBlockedReason false defaults defaults)

    // Already saving — this is what stops a duplicate write.
    Assert.False(SettingsLogic.canSave true defaults dirty)
    Assert.Equal(Some "Saving…", SettingsLogic.saveBlockedReason true defaults dirty)

    // Dirty but invalid.
    let invalid = { defaults with UserName = "Maya"; WorkdayEndHour = 1 }
    Assert.False(SettingsLogic.canSave false defaults invalid)
    Assert.True((SettingsLogic.saveBlockedReason false defaults invalid).IsSome)

[<Fact>]
let ``save states each have their own words, and idle is silent`` () =
    Assert.Equal(None, SettingsLogic.saveStateText SettingsLogic.SaveState.Idle)
    Assert.Equal(Some "Saving…", SettingsLogic.saveStateText SettingsLogic.SaveState.Saving)
    Assert.Equal(Some "Saved.", SettingsLogic.saveStateText SettingsLogic.SaveState.Saved)

    match SettingsLogic.saveStateText (SettingsLogic.SaveState.Failed "the disk is full") with
    | Some message -> Assert.Contains("the disk is full", message)
    | None -> failwith "a failure must be reported"

// ---------------------------------------------------------------------------
// 4. the deadline buffer is cosmetic, and says so
// ---------------------------------------------------------------------------

[<Fact>]
let ``the deadline buffer is described as wording, never as scheduling`` () =
    let notice = SettingsLogic.deadlineBufferNotice.ToLowerInvariant()

    // What it really does.
    Assert.Contains("tight against deadline", notice)

    // What it must never claim.
    for absent in [ "scheduling order"; "capacity"; "feasibility"; "when work is placed" ] do
        Assert.Contains(absent, notice)

    Assert.Contains("does not change", notice)
    Assert.DoesNotContain("warning threshold", SettingsLogic.deadlineBufferNotice)
    Assert.Contains("threshold", SettingsLogic.deadlineBufferLabel.ToLowerInvariant())

[<Fact>]
let ``the planner effect table never mentions the deadline buffer`` () =
    // It is kept out of the main controls precisely because its effect is not
    // what its name suggests.
    for name, effect in SettingsLogic.plannerEffects do
        Assert.DoesNotContain("deadline buffer", (name + " " + effect).ToLowerInvariant())

[<Fact>]
let ``every planner control states what it actually changes`` () =
    Assert.Equal(5, SettingsLogic.plannerEffects.Length)

    for name, effect in SettingsLogic.plannerEffects do
        Assert.False(String.IsNullOrWhiteSpace name)
        Assert.False(String.IsNullOrWhiteSpace effect)

// ---------------------------------------------------------------------------
// 5. secrets — none, and no claim about them
// ---------------------------------------------------------------------------

[<Fact>]
let ``Settings says plainly that it cannot see the credential`` () =
    let notice = SettingsLogic.credentialHandoffNotice

    Assert.Contains("not here", notice)
    Assert.Contains("cannot read it", notice)
    Assert.Contains("does not know whether one is saved", notice)

[<Fact>]
let ``no wording claims a credential is present or absent`` () =
    let all =
        SettingsLogic.credentialHandoffNotice
        + " "
        + SettingsLogic.aiCredentialHandoff.Body
        + " "
        + SettingsLogic.autoApplyNotice
        |> fun s -> s.ToLowerInvariant()

    for absent in [ "key is set"; "key is configured"; "no key"; "credential stored"; "verified" ] do
        Assert.DoesNotContain(absent, all)

// ---------------------------------------------------------------------------
// 6. handoffs
// ---------------------------------------------------------------------------

[<Fact>]
let ``appearance hands off to Design Preview and never claims to edit it`` () =
    let h = SettingsLogic.appearanceHandoff

    Assert.Equal(Page.DesignPreview, h.Target)
    Assert.Contains("Design Preview", h.Action)
    Assert.Contains("Settings does not edit them", h.Body)

[<Fact>]
let ``integrations and AI credentials both hand off to Connections`` () =
    Assert.Equal(Page.Connections, SettingsLogic.integrationsHandoff.Target)
    Assert.Equal(Page.Connections, SettingsLogic.aiCredentialHandoff.Target)

    for h in [ SettingsLogic.integrationsHandoff; SettingsLogic.aiCredentialHandoff ] do
        Assert.Contains("Connections", h.Action)

[<Fact>]
let ``every handoff action names what opens`` () =
    for h in
        [ SettingsLogic.appearanceHandoff
          SettingsLogic.integrationsHandoff
          SettingsLogic.aiCredentialHandoff ] do
        Assert.StartsWith("Open ", h.Action)
        Assert.False(String.IsNullOrWhiteSpace h.Title)
        Assert.False(String.IsNullOrWhiteSpace h.Body)

// ---------------------------------------------------------------------------
// 7. notifications — orphaned, said plainly
// ---------------------------------------------------------------------------

[<Fact>]
let ``notifications state that nothing is delivered`` () =
    let notice = SettingsLogic.notificationsNotice

    Assert.Contains("does not currently deliver notifications", notice)
    Assert.Contains("no reminder scheduler", notice.ToLowerInvariant())

    // And the legacy flag is explained rather than shown as a setting.
    Assert.Contains("Nothing reads it", SettingsLogic.notificationsLegacyNotice)

[<Fact>]
let ``nothing implies notifications work`` () =
    let all =
        (SettingsLogic.notificationsNotice + " " + SettingsLogic.notificationsLegacyNotice).ToLowerInvariant()

    for absent in [ "reminders are on"; "you will be notified"; "permission granted"; "scheduled for" ] do
        Assert.DoesNotContain(absent, all)

// ---------------------------------------------------------------------------
// 8. privacy — evidence-backed only
// ---------------------------------------------------------------------------

[<Fact>]
let ``every privacy line is a fact this codebase can support`` () =
    Assert.Equal(5, SettingsLogic.privacyFacts.Length)

    let all = String.concat " " SettingsLogic.privacyFacts

    Assert.Contains("local SQLite database", all)
    Assert.Contains("Windows DPAPI", all)
    Assert.Contains("does not revoke access at the provider", all)
    Assert.Contains("stays in University OS", all)

[<Fact>]
let ``no forbidden privacy claim appears`` () =
    let all = (String.concat " " SettingsLogic.privacyFacts).ToLowerInvariant()

    for claim in SettingsLogic.forbiddenPrivacyClaims do
        Assert.DoesNotContain(claim.ToLowerInvariant(), all)

// ---------------------------------------------------------------------------
// 9. UiState classification
// ---------------------------------------------------------------------------

[<Fact>]
let ``every UiState field is classified`` () =
    Assert.Equal(5, SettingsLogic.uiStateFields.Length)

    let names = SettingsLogic.uiStateFields |> List.map (fun (n, _, _) -> n)

    Assert.Equal<string list>(
        [ "SidebarDensity"; "SidebarPinned"; "LastBriefingDate"; "LastReviewDate"; "AiMemoryEnabled" ],
        names
    )

    for _, _, reason in SettingsLogic.uiStateFields do
        Assert.False(String.IsNullOrWhiteSpace reason)

[<Fact>]
let ``only the AI memory flag is exposed as a setting`` () =
    // The two sidebar fields are real preferences the shell already owns; the
    // two dates are bookkeeping. Turning any of them into a Settings control
    // would create a second writer or expose transient state.
    Assert.Equal<string list>([ "AiMemoryEnabled" ], SettingsLogic.exposedUiStateFields)

[<Fact>]
let ``transient bookkeeping is never exposed`` () =
    let transient =
        SettingsLogic.uiStateFields
        |> List.filter (fun (_, c, _) -> c = SettingsLogic.UiStateClass.Transient)
        |> List.map (fun (n, _, _) -> n)

    Assert.Equal<string list>([ "LastBriefingDate"; "LastReviewDate" ], transient)

    for name in transient do
        Assert.DoesNotContain(name, SettingsLogic.exposedUiStateFields)

// ---------------------------------------------------------------------------
// 10. the orphaned appearance setting
// ---------------------------------------------------------------------------

[<Fact>]
let ``AnimationSpeed is recorded as having no runtime consumer`` () =
    Assert.Contains("no runtime consumer", SettingsLogic.animationSpeedFinding)
    Assert.Contains("not offered as a setting", SettingsLogic.animationSpeedFinding)

// ---------------------------------------------------------------------------
// 11. shortcuts — fixed
// ---------------------------------------------------------------------------

[<Fact>]
let ``the shortcut list matches the real handler`` () =
    let chords = SettingsLogic.shortcuts |> List.map fst

    // Every one of these is a literal in Shell.handleKeys.
    Assert.Contains("Ctrl + K", chords)
    Assert.Contains("Ctrl + N", chords)
    Assert.Contains("Ctrl + F", chords)
    Assert.Contains("Ctrl + B", chords)

    for _, description in SettingsLogic.shortcuts do
        Assert.False(String.IsNullOrWhiteSpace description)

[<Fact>]
let ``shortcuts are declared unreassignable`` () =
    Assert.Contains("fixed", SettingsLogic.shortcutsNotice.ToLowerInvariant())
    Assert.Contains("does not support reassigning", SettingsLogic.shortcutsNotice)

// ---------------------------------------------------------------------------
// 12. responsive
// ---------------------------------------------------------------------------

[<Fact>]
let ``the category rail never disappears`` () =
    Assert.Equal(SettingsLogic.RailPlacement.Side, SettingsLogic.placementFor 1600.0)
    Assert.Equal(SettingsLogic.RailPlacement.Side, SettingsLogic.placementFor 1000.0)

    // Below the narrow breakpoint it moves above the content rather than going.
    Assert.Equal(SettingsLogic.RailPlacement.Top, SettingsLogic.placementFor 700.0)

[<Fact>]
let ``the rail narrows before it moves`` () =
    Assert.Equal(SettingsLogic.Geometry.railWidth, SettingsLogic.railColumnWidth 1600.0, 6)
    Assert.True(SettingsLogic.railColumnWidth 1000.0 < SettingsLogic.railColumnWidth 1600.0)
    Assert.Equal(220.0, SettingsLogic.Geometry.railWidth, 6)

// ---------------------------------------------------------------------------
// 13. option lists
// ---------------------------------------------------------------------------

[<Fact>]
let ``every option list contains its own default`` () =
    Assert.Contains(defaults.WorkdayStartHour, SettingsLogic.hourOptions)
    Assert.Contains(defaults.WorkdayEndHour, SettingsLogic.hourOptions)
    Assert.Contains(defaults.SessionMinutes, SettingsLogic.sessionOptions)
    Assert.Contains(defaults.BreakMinutes, SettingsLogic.breakOptions)
    Assert.Contains(defaults.MaxDailyStudyHours, SettingsLogic.maxDailyOptions)
    Assert.Contains(defaults.DeadlineBufferHours, SettingsLogic.bufferOptions)
    Assert.Contains(defaults.PlannerHorizonDays, SettingsLogic.horizonOptions)
    Assert.Contains(defaults.AiModel, SettingsLogic.modelOptions)

[<Fact>]
let ``a zero deadline buffer is offered and named for what it does`` () =
    Assert.Contains(0.0, SettingsLogic.bufferOptions)
    Assert.Equal("No warning", SettingsLogic.bufferLabel 0.0)
    Assert.Equal("12 h before", SettingsLogic.bufferLabel 12.0)

[<Fact>]
let ``hours are labelled as times of day`` () =
    Assert.Equal("09:00", SettingsLogic.hourLabel 9)
    Assert.Equal("21:00", SettingsLogic.hourLabel 21)
