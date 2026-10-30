namespace UniversityOS.Application

open System
open System.Text.RegularExpressions
open UniversityOS.Domain

/// The offline capture classifier. When no AI provider is configured (or it fails),
/// this pure module still extracts dates, module codes and likely intents, so the
/// capture pipeline never depends on the network. Deterministic and unit-tested.
module CaptureHeuristics =

    let private months =
        [ "january", 1; "jan", 1
          "february", 2; "feb", 2
          "march", 3; "mar", 3
          "april", 4; "apr", 4
          "may", 5
          "june", 6; "jun", 6
          "july", 7; "jul", 7
          "august", 8; "aug", 8
          "september", 9; "sept", 9; "sep", 9
          "october", 10; "oct", 10
          "november", 11; "nov", 11
          "december", 12; "dec", 12 ]
        |> dict

    let private tryDate (y: int) (m: int) (d: int) =
        if m >= 1 && m <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m) then
            Some(DateTime(y, m, d))
        else
            None

    /// If the year was omitted, prefer the next occurrence rather than a past date.
    let private resolveYear (now: DateTime) (m: int) (d: int) =
        tryDate now.Year m d
        |> Option.map (fun date -> if date < now.Date.AddDays(-7.0) then date.AddYears 1 else date)

    let private timeRx =
        Regex(@"\b(?<h>[01]?\d|2[0-3]):(?<m>[0-5]\d)\b|\b(?<h12>\d{1,2})\s?(?<ap>am|pm)\b", RegexOptions.IgnoreCase)

    /// First explicit clock time mentioned in the text, if any.
    let findTime (text: string) : TimeSpan option =
        let m = timeRx.Match text

        if not m.Success then
            None
        elif m.Groups.["h"].Success then
            Some(TimeSpan(int m.Groups.["h"].Value, int m.Groups.["m"].Value, 0))
        else
            let h = int m.Groups.["h12"].Value

            if h >= 1 && h <= 12 then
                let h24 =
                    match m.Groups.["ap"].Value.ToLowerInvariant() with
                    | "pm" when h <> 12 -> h + 12
                    | "am" when h = 12 -> 0
                    | _ -> h

                Some(TimeSpan(h24, 0, 0))
            else
                None

    let private isoRx = Regex(@"\b(?<y>20\d{2})-(?<m>\d{1,2})-(?<d>\d{1,2})\b")

    let private dmyRx = Regex(@"\b(?<d>\d{1,2})/(?<m>\d{1,2})(?:/(?<y>\d{2,4}))?\b")

    let private dayMonthRx =
        Regex(
            @"\b(?<d>\d{1,2})(?:st|nd|rd|th)?\s+(?<mon>[A-Za-z]{3,9})(?:\s+(?<y>20\d{2}))?\b|\b(?<mon2>[A-Za-z]{3,9})\s+(?<d2>\d{1,2})(?:st|nd|rd|th)?(?:,?\s+(?<y2>20\d{2}))?\b",
            RegexOptions.IgnoreCase
        )

    /// Every calendar date mentioned in the text, in document order, deduplicated.
    let findDates (now: DateTime) (text: string) : DateTime list =
        let results = ResizeArray<int * DateTime>()

        for m in isoRx.Matches text do
            tryDate (int m.Groups.["y"].Value) (int m.Groups.["m"].Value) (int m.Groups.["d"].Value)
            |> Option.iter (fun d -> results.Add(m.Index, d))

        for m in dmyRx.Matches text do
            let year =
                if m.Groups.["y"].Success then
                    let y = int m.Groups.["y"].Value
                    Some(if y < 100 then 2000 + y else y)
                else
                    None

            match year with
            | Some y ->
                tryDate y (int m.Groups.["m"].Value) (int m.Groups.["d"].Value)
                |> Option.iter (fun d -> results.Add(m.Index, d))
            | None ->
                resolveYear now (int m.Groups.["m"].Value) (int m.Groups.["d"].Value)
                |> Option.iter (fun d -> results.Add(m.Index, d))

        for m in dayMonthRx.Matches text do
            let monGroup = if m.Groups.["mon"].Success then "mon" else "mon2"
            let dayGroup = if m.Groups.["mon"].Success then "d" else "d2"
            let yearGroup = if m.Groups.["mon"].Success then "y" else "y2"
            let monName = m.Groups.[monGroup].Value.ToLowerInvariant()

            if months.ContainsKey monName then
                let month = months.[monName]
                let day = int m.Groups.[dayGroup].Value

                let date =
                    if m.Groups.[yearGroup].Success then
                        tryDate (int m.Groups.[yearGroup].Value) month day
                    else
                        resolveYear now month day

                date |> Option.iter (fun d -> results.Add(m.Index, d))

        // Relative words — cheap and very common in captures.
        let lower = text.ToLowerInvariant()
        if lower.Contains "tomorrow" then
            results.Add(lower.IndexOf "tomorrow", now.Date.AddDays 1.0)
        if lower.Contains "tonight" || Regex.IsMatch(lower, @"\btoday\b") then
            results.Add(0, now.Date)

        results
        |> Seq.sortBy fst
        |> Seq.map snd
        |> Seq.distinct
        |> List.ofSeq

    let private genericModuleRx = Regex(@"\b([A-Z]{2,5})[ -]?(\d{3,4})\b")

    let normalizeCode (code: string) =
        code.Replace(" ", "").Replace("-", "").ToUpperInvariant()

    /// Finds a module code in the text, preferring codes of modules the user actually has.
    let findModuleCode (knownCodes: string list) (text: string) : string option =
        let known =
            knownCodes
            |> List.tryPick (fun code ->
                let m = Regex.Match(normalizeCode code, @"^([A-Z]+)(\d+)$")

                if m.Success then
                    let pattern =
                        sprintf @"\b%s[ -]?%s\b" (Regex.Escape m.Groups.[1].Value) (Regex.Escape m.Groups.[2].Value)

                    if Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase) then Some code else None
                elif text.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0 then
                    Some code
                else
                    None)

        match known with
        | Some _ -> known
        | None ->
            let m = genericModuleRx.Match text
            if m.Success then Some(normalizeCode m.Value) else None

    let private dueRx =
        Regex(@"\b(due|deadline|submit|submission|hand\s?-?\s?in)\b", RegexOptions.IgnoreCase)

    let private assignmentRx =
        Regex(@"\b(assignment|coursework|report|essay|project|lab|worksheet|problem\s+sheet|quiz)\b", RegexOptions.IgnoreCase)

    let private eventRx =
        Regex(@"\b(lecture|seminar|tutorial|meeting|workshop|exam|test|presentation|viva)\b", RegexOptions.IgnoreCase)

    let private examRx = Regex(@"\b(exam|test|viva)\b", RegexOptions.IgnoreCase)

    let private firstLine (s: string) =
        s.Split('\n') |> Array.tryHead |> Option.defaultValue "" |> fun l -> l.Trim()

    let private truncate (n: int) (s: string) =
        if s.Length <= n then s else s.Substring(0, n - 1).TrimEnd() + "…"

    /// The heuristic analyzer: same output shape as the AI classifier, so the rest of
    /// the pipeline cannot tell them apart.
    let analyze (now: DateTime) (knownCodes: string list) (title: string) (body: string) : CaptureAnalysis =
        let full = title + "\n" + body
        let moduleCode = findModuleCode knownCodes full
        let dates = findDates now full
        let time = findTime full
        let hasDue = dueRx.IsMatch full
        let isAssignment = assignmentRx.IsMatch full
        let isEvent = eventRx.IsMatch full

        let dueDate =
            if hasDue || isAssignment then
                dates
                |> List.tryFind (fun d -> d >= now.Date)
                |> Option.orElse (List.tryHead dates)
                |> Option.map (fun d ->
                    match time with
                    | Some t -> d.Date + t
                    | None -> d.Date.AddHours 17.0) // sensible default: 5pm hand-in
            else
                None

        let cleanTitle =
            let t = if String.IsNullOrWhiteSpace title then firstLine body else title
            truncate 80 (if String.IsNullOrWhiteSpace t then "Captured item" else t)

        let actions =
            [ if isAssignment && (hasDue || dueDate.IsSome) then
                  { Kind = ProposedActionKind.CreateAssignment
                    Title = cleanTitle
                    ModuleCode = moduleCode
                    Date = dueDate
                    DurationMinutes = None
                    Body = if String.IsNullOrWhiteSpace body then None else Some(truncate 2000 body)
                    TargetId = None }

              if isEvent && not dates.IsEmpty then
                  let start =
                      let d = dates |> List.tryFind (fun d -> d >= now.Date) |> Option.defaultValue dates.Head

                      match time with
                      | Some t -> d.Date + t
                      | None -> d.Date.AddHours 9.0

                  { Kind = ProposedActionKind.CreateEvent
                    Title = cleanTitle
                    ModuleCode = moduleCode
                    Date = Some start
                    DurationMinutes = Some(if examRx.IsMatch full then 120 else 60)
                    Body = None
                    TargetId = None }

              // Substantial prose with no other outcome becomes a note so nothing is lost.
              if not (isAssignment && (hasDue || dueDate.IsSome)) && not (isEvent && not dates.IsEmpty) then
                  if body.Trim().Length > 0 then
                      { Kind = ProposedActionKind.CreateNote
                        Title = cleanTitle
                        ModuleCode = moduleCode
                        Date = None
                        DurationMinutes = None
                        Body = Some body
                        TargetId = None } ]

        let actions =
            if actions.IsEmpty then
                [ { Kind = ProposedActionKind.NoAction
                    Title = cleanTitle
                    ModuleCode = moduleCode
                    Date = None
                    DurationMinutes = None
                    Body = None
                    TargetId = None } ]
            else
                actions

        { Summary = truncate 180 (if String.IsNullOrWhiteSpace body then cleanTitle else firstLine body)
          SuggestedModuleCode = moduleCode
          Actions = actions
          Analyzer = "heuristic" }
