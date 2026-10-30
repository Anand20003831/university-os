namespace UniversityOS.App

open System

/// All user-facing date/number formatting lives here so the app speaks with one
/// voice. Views never call ToString on dates directly.
[<RequireQualifiedAccess>]
module Format =

    let greeting (now: DateTime) (name: string) =
        let salutation =
            if now.Hour < 5 then "Burning the midnight oil"
            elif now.Hour < 12 then "Good morning"
            elif now.Hour < 18 then "Good afternoon"
            else "Good evening"

        if String.IsNullOrWhiteSpace name then salutation
        else sprintf "%s, %s" salutation (name.Split(' ').[0])

    let fullDate (d: DateTime) = d.ToString "dddd d MMMM yyyy"

    let shortDate (d: DateTime) = d.ToString "ddd d MMM"

    let time (d: DateTime) = d.ToString "HH:mm"

    let timeRange (s: DateTime) (e: DateTime) = sprintf "%s–%s" (time s) (time e)

    let dayLabel (now: DateTime) (d: DateTime) =
        if d.Date = now.Date then "Today"
        elif d.Date = now.Date.AddDays 1.0 then "Tomorrow"
        elif d.Date = now.Date.AddDays -1.0 then "Yesterday"
        elif d.Date < now.Date.AddDays 7.0 && d.Date > now.Date then d.ToString "dddd"
        else shortDate d

    let duration (minutes: int) =
        if minutes < 60 then sprintf "%dm" minutes
        elif minutes % 60 = 0 then sprintf "%dh" (minutes / 60)
        else sprintf "%dh %02dm" (minutes / 60) (minutes % 60)

    let hours (h: float) =
        if h = Math.Floor h then sprintf "%gh" h else sprintf "%.1fh" h

    /// "due in 3h" / "due Fri 17:00" / "OVERDUE by 2 days" — the deadline voice.
    let deadline (now: DateTime) (d: DateTime) =
        let span = d - now

        if span.TotalMinutes < 0.0 then
            let overdue = now - d

            if overdue.TotalHours < 24.0 then sprintf "overdue by %dh" (max 1 (int overdue.TotalHours))
            else sprintf "overdue by %d days" (int overdue.TotalDays)
        elif span.TotalHours < 1.0 then sprintf "due in %dm" (int span.TotalMinutes)
        elif span.TotalHours < 24.0 then sprintf "due in %dh" (int span.TotalHours)
        elif span.TotalDays < 7.0 then sprintf "due %s %s" (d.ToString "ddd") (time d)
        else sprintf "due %s" (shortDate d)

    let relativeTime (now: DateTime) (d: DateTime) =
        let span = now - d

        if span.TotalMinutes < 1.0 then "just now"
        elif span.TotalMinutes < 60.0 then sprintf "%dm ago" (int span.TotalMinutes)
        elif span.TotalHours < 24.0 then sprintf "%dh ago" (int span.TotalHours)
        elif span.TotalDays < 7.0 then sprintf "%dd ago" (int span.TotalDays)
        else shortDate d

    let truncate (n: int) (s: string) =
        if s.Length <= n then s else s.Substring(0, n - 1).TrimEnd() + "…"

    let firstLine (s: string) =
        s.Split('\n') |> Array.tryHead |> Option.defaultValue "" |> fun l -> l.Trim()

    let parseDateTimeInput (s: string) : DateTime option =
        let formats =
            [| "yyyy-MM-dd HH:mm"; "yyyy-MM-dd H:mm"; "yyyy-MM-dd"; "dd/MM/yyyy HH:mm"; "dd/MM/yyyy" |]

        match DateTime.TryParseExact(s.Trim(), formats, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.None) with
        | true, d -> Some d
        | _ ->
            match DateTime.TryParse(s.Trim()) with
            | true, d -> Some d
            | _ -> None

    let dateTimeInput (d: DateTime) = d.ToString "yyyy-MM-dd HH:mm"

    let parseFloatOpt (s: string) : float option =
        match Double.TryParse(s.Trim().Replace(",", "."), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | _ -> None

    let parseIntOpt (s: string) : int option =
        match Int32.TryParse(s.Trim()) with
        | true, v -> Some v
        | _ -> None
