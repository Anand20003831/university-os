namespace UniversityOS.Domain

/// How much of each navigation entry the sidebar shows.
///
/// Replaces the old three-state Expanded → Compact → Hidden cycle. Hidden is no
/// longer a density: in the redesign the sidebar is a floating panel that hides
/// itself when unpinned and returns on hover, so "is it showing" and "how wide
/// is it" are two independent things and cycling one control through both made
/// them impossible to set independently.
[<RequireQualifiedAccess>]
type SidebarDensity =
    | Expanded
    | Compact

[<RequireQualifiedAccess>]
module SidebarDensity =
    let toggle =
        function
        | SidebarDensity.Expanded -> SidebarDensity.Compact
        | SidebarDensity.Compact -> SidebarDensity.Expanded

    /// Panel width in px. From the redesign.
    let width =
        function
        | SidebarDensity.Expanded -> 252.0
        | SidebarDensity.Compact -> 80.0

    let label =
        function
        | SidebarDensity.Expanded -> "Expanded"
        | SidebarDensity.Compact -> "Compact"

/// Persisted UI/app state that is NOT a user preference (those live in
/// UserSettings). Kept as a separate record + storage row so UserSettings'
/// serialized shape never changes (old JSON must keep deserializing).
type UiState =
    { /// How wide the sidebar's entries are drawn.
      SidebarDensity: SidebarDensity
      /// Pinned: docked, always visible, and it reserves layout width.
      /// Unpinned: a floating glass panel that hides until the pointer reaches
      /// the left edge, and content runs full width behind it.
      SidebarPinned: bool
      /// "yyyy-MM-dd" of the last day the Morning Briefing was shown.
      LastBriefingDate: string option
      LastReviewDate: string option
      AiMemoryEnabled: bool }

[<RequireQualifiedAccess>]
module UiState =
    let defaults =
        { SidebarDensity = SidebarDensity.Expanded
          SidebarPinned = true
          LastBriefingDate = None
          LastReviewDate = None
          AiMemoryEnabled = true }
