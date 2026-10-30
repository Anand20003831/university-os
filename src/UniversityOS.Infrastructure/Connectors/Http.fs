namespace UniversityOS.Infrastructure.Connectors

open System
open System.Net.Http
open System.Text.Json

/// Minimal JSON-over-HTTP helpers shared by every connector. All failures are
/// values — connectors never throw across the interface boundary.
[<RequireQualifiedAccess>]
module Http =

    let getJson (http: HttpClient) (headers: (string * string) list) (url: string) : Async<Result<JsonDocument, string>> =
        async {
            try
                use msg = new HttpRequestMessage(HttpMethod.Get, url)

                for name, value in headers do
                    msg.Headers.TryAddWithoutValidation(name, value) |> ignore

                let! response = http.SendAsync msg |> Async.AwaitTask
                let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                if response.IsSuccessStatusCode then
                    try
                        return Ok(JsonDocument.Parse body)
                    with _ ->
                        return Error "The service returned an invalid response."
                else
                    let snippet = if body.Length > 300 then body.Substring(0, 300) else body
                    return Error(sprintf "HTTP %d: %s" (int response.StatusCode) snippet)
            with ex ->
                return Error ex.Message
        }

    let postForm
        (http: HttpClient)
        (url: string)
        (values: (string * string) list)
        : Async<Result<JsonDocument, string>> =
        async {
            try
                use content = new FormUrlEncodedContent(values |> List.map (fun (k, v) -> Collections.Generic.KeyValuePair(k, v)))
                let! response = http.PostAsync(url, content) |> Async.AwaitTask
                let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                if response.IsSuccessStatusCode then
                    try
                        return Ok(JsonDocument.Parse body)
                    with _ ->
                        return Error "The service returned an invalid response."
                else
                    let snippet = if body.Length > 300 then body.Substring(0, 300) else body
                    return Error(sprintf "HTTP %d: %s" (int response.StatusCode) snippet)
            with ex ->
                return Error ex.Message
        }

    // -- defensive JsonElement readers ---------------------------------------

    let prop (name: string) (el: JsonElement) : JsonElement option =
        match el.TryGetProperty name with
        | true, v when v.ValueKind <> JsonValueKind.Null -> Some v
        | _ -> None

    let propStr (name: string) (el: JsonElement) : string option =
        prop name el
        |> Option.bind (fun v -> if v.ValueKind = JsonValueKind.String then Some(v.GetString()) else None)

    let propInt64 (name: string) (el: JsonElement) : int64 option =
        prop name el
        |> Option.bind (fun v -> if v.ValueKind = JsonValueKind.Number then Some(v.GetInt64()) else None)

    let propArray (name: string) (el: JsonElement) : JsonElement list =
        match prop name el with
        | Some v when v.ValueKind = JsonValueKind.Array -> v.EnumerateArray() |> List.ofSeq
        | _ -> []

    let parseIsoDate (s: string) : DateTime option =
        match DateTime.TryParse(s, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AdjustToUniversal ||| Globalization.DateTimeStyles.AssumeUniversal) with
        | true, d -> Some(d.ToLocalTime())
        | _ -> None
