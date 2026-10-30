namespace UniversityOS.App

open System
open System.Text.RegularExpressions

/// Pure Markdown structure parser (v1.1 notes experience). Kept free of any UI
/// types so it is directly unit-testable; Components.fs renders the blocks.
/// Supported: headings, paragraphs, bullets, numbered lists, task lists,
/// quotes, callouts (> [!NOTE] …), fenced code, tables, wiki links.
[<RequireQualifiedAccess>]
module Markdown =

    type Block =
        | Heading of level: int * text: string
        | Paragraph of text: string
        | Bullet of text: string
        | Numbered of ordinal: string * text: string
        /// index = nth task item in the document (used for click-to-toggle).
        | TaskItem of index: int * isDone: bool * text: string
        | Quote of text: string
        | Callout of kind: string * title: string * body: string list
        | CodeBlock of lines: string list
        | Table of header: string list * rows: string list list
        | Blank

    let private taskRx = Regex(@"^- \[( |x|X)\] (.*)$")
    let private numberedRx = Regex(@"^(\d+)\. (.*)$")
    let private calloutRx = Regex(@"^> \[!(\w+)\]\s*(.*)$")
    let private wikiRx = Regex(@"\[\[([^\[\]]+)\]\]")

    let private isTableRow (line: string) = line.TrimStart().StartsWith "|"

    let private splitCells (line: string) =
        line.Trim().Trim('|').Split('|') |> Array.map (fun c -> c.Trim()) |> List.ofArray

    let private isSeparatorRow (line: string) =
        isTableRow line
        && splitCells line
           |> List.forall (fun c -> c.Length > 0 && c |> Seq.forall (fun ch -> ch = '-' || ch = ':'))

    /// Every distinct [[wiki link]] target in the document, in order.
    let wikiLinks (source: string) : string list =
        wikiRx.Matches source
        |> Seq.map (fun m -> m.Groups.[1].Value.Trim())
        |> Seq.filter (fun t -> t.Length > 0)
        |> Seq.distinct
        |> List.ofSeq

    /// Flip the checkbox of the nth task item (0-based) in the source text.
    let toggleTask (index: int) (source: string) : string =
        let lines = source.Replace("\r\n", "\n").Split '\n'
        let mutable seen = -1

        let updated =
            lines
            |> Array.map (fun line ->
                let m = taskRx.Match(line.TrimEnd())

                if m.Success then
                    seen <- seen + 1

                    if seen = index then
                        let isDone = m.Groups.[1].Value <> " "
                        sprintf "- [%s] %s" (if isDone then " " else "x") m.Groups.[2].Value
                    else
                        line
                else
                    line)

        String.Join("\n", updated)

    let parse (source: string) : Block list =
        let lines = source.Replace("\r\n", "\n").Split '\n'
        let blocks = ResizeArray<Block>()
        let mutable taskIndex = -1
        let mutable i = 0

        while i < lines.Length do
            let raw = lines.[i]
            let line = raw.TrimEnd()

            if line.StartsWith "```" then
                // Fenced code: consume to closing fence (or EOF).
                let code = ResizeArray<string>()
                i <- i + 1

                while i < lines.Length && not (lines.[i].TrimEnd().StartsWith "```") do
                    code.Add lines.[i]
                    i <- i + 1

                blocks.Add(CodeBlock(List.ofSeq code))
                i <- i + 1 // skip closing fence
            elif isTableRow line && i + 1 < lines.Length && isSeparatorRow lines.[i + 1] then
                let header = splitCells line
                let rows = ResizeArray<string list>()
                i <- i + 2 // skip header + separator

                while i < lines.Length && isTableRow (lines.[i].TrimEnd()) do
                    rows.Add(splitCells lines.[i])
                    i <- i + 1

                blocks.Add(Table(header, List.ofSeq rows))
            elif calloutRx.IsMatch line then
                let m = calloutRx.Match line
                let body = ResizeArray<string>()
                i <- i + 1

                while i < lines.Length
                      && lines.[i].TrimEnd().StartsWith "> "
                      && not (calloutRx.IsMatch(lines.[i].TrimEnd())) do
                    body.Add(lines.[i].TrimEnd().Substring 2)
                    i <- i + 1

                blocks.Add(Callout(m.Groups.[1].Value.ToUpperInvariant(), m.Groups.[2].Value, List.ofSeq body))
            else
                let trimmed = line.TrimStart()

                if trimmed.StartsWith "### " then blocks.Add(Heading(3, trimmed.Substring 4))
                elif trimmed.StartsWith "## " then blocks.Add(Heading(2, trimmed.Substring 3))
                elif trimmed.StartsWith "# " then blocks.Add(Heading(1, trimmed.Substring 2))
                elif taskRx.IsMatch trimmed then
                    let m = taskRx.Match trimmed
                    taskIndex <- taskIndex + 1
                    blocks.Add(TaskItem(taskIndex, m.Groups.[1].Value <> " ", m.Groups.[2].Value))
                elif trimmed.StartsWith "- " || trimmed.StartsWith "* " then
                    blocks.Add(Bullet(trimmed.Substring 2))
                elif numberedRx.IsMatch trimmed then
                    let m = numberedRx.Match trimmed
                    blocks.Add(Numbered(m.Groups.[1].Value, m.Groups.[2].Value))
                elif trimmed.StartsWith "> " then
                    blocks.Add(Quote(trimmed.Substring 2))
                elif trimmed.Length = 0 then
                    blocks.Add Blank
                else
                    blocks.Add(Paragraph trimmed)

                i <- i + 1

        List.ofSeq blocks
