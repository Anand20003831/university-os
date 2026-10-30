namespace UniversityOS.Infrastructure

open System.Text.Json
open System.Text.Json.Serialization

/// One shared serializer configuration for everything the infrastructure persists
/// (capture analyses, settings, connector token sets). FSharp.SystemTextJson makes
/// records, options and unions serialize cleanly.
[<RequireQualifiedAccess>]
module Json =

    let options =
        let o = JsonSerializerOptions(WriteIndented = false)

        JsonFSharpOptions
            .Default()
            .WithUnionEncoding(JsonUnionEncoding.AdjacentTag ||| JsonUnionEncoding.UnwrapOption ||| JsonUnionEncoding.UnwrapSingleCaseUnions)
            .AddToJsonSerializerOptions o

        o

    let serialize (value: 'T) = JsonSerializer.Serialize(value, options)

    let deserialize<'T> (json: string) : 'T option =
        try
            Some(JsonSerializer.Deserialize<'T>(json, options))
        with _ ->
            None
