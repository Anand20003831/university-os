namespace UniversityOS.Domain

[<RequireQualifiedAccess>]
type ThemeMode =
    | Dark
    | Light

type UserSettings =
    { UserName: string
      /// RETIRED. AppearanceSettings.Theme is the authoritative theme choice —
      /// it can also express "follow the OS", which this cannot. Nothing reads
      /// this field; it is kept because UserSettings' serialized shape must stay
      /// stable or previously persisted JSON fails to load.
      /// Do not wire it back up.
      Theme: ThemeMode
      /// Hour of day (0–23) the planner may start scheduling work.
      WorkdayStartHour: int
      /// Hour of day (0–23) the planner must stop scheduling work.
      WorkdayEndHour: int
      /// Preferred length of a single focused study session.
      SessionMinutes: int
      BreakMinutes: int
      MaxDailyStudyHours: float
      /// Aim to finish work this many hours before the deadline.
      DeadlineBufferHours: float
      PlannerHorizonDays: int
      /// Model id used for AI requests.
      AiModel: string
      /// When enabled, capture analysis applies its proposed actions without asking.
      AiAutoApply: bool
      NotificationsEnabled: bool }

[<RequireQualifiedAccess>]
module UserSettings =
    let defaults =
        { UserName = ""
          Theme = ThemeMode.Dark
          WorkdayStartHour = 9
          WorkdayEndHour = 21
          SessionMinutes = 50
          BreakMinutes = 10
          MaxDailyStudyHours = 6.0
          DeadlineBufferHours = 12.0
          PlannerHorizonDays = 14
          AiModel = "claude-opus-4-8"
          AiAutoApply = false
          NotificationsEnabled = true }
