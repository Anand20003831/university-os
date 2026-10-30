module UniversityOS.Tests.MarkdownTests

open Xunit
open UniversityOS.App

/// The pure Markdown structure parser behind Notes v2 (Components.fs renders
/// the blocks; nothing UI-typed is touched here).

[<Fact>]
let ``parses headings, bullets, numbered lists and paragraphs`` () =
    let blocks = Markdown.parse "# Title\n\nSome text\n- point one\n1. first"

    let expected =
        [ Markdown.Heading(1, "Title")
          Markdown.Blank
          Markdown.Paragraph "Some text"
          Markdown.Bullet "point one"
          Markdown.Numbered("1", "first") ]

    Assert.True((expected = blocks), sprintf "unexpected: %A" blocks)

[<Fact>]
let ``task items are indexed in document order`` () =
    let blocks = Markdown.parse "- [ ] first\ntext\n- [x] second"

    match blocks with
    | [ Markdown.TaskItem(0, false, "first"); Markdown.Paragraph "text"; Markdown.TaskItem(1, true, "second") ] -> ()
    | other -> failwithf "unexpected: %A" other

[<Fact>]
let ``toggleTask flips only the nth checkbox`` () =
    let source = "- [ ] one\n- [x] two\n- [ ] three"

    Assert.Equal("- [x] one\n- [x] two\n- [ ] three", Markdown.toggleTask 0 source)
    Assert.Equal("- [ ] one\n- [ ] two\n- [ ] three", Markdown.toggleTask 1 source)
    // Out-of-range index leaves the text untouched.
    Assert.Equal(source, Markdown.toggleTask 9 source)

[<Fact>]
let ``wiki links are distinct, trimmed and in order`` () =
    let links = Markdown.wikiLinks "See [[Alpha]] and [[ Beta ]] and [[Alpha]] again."
    Assert.True(([ "Alpha"; "Beta" ] = links), sprintf "unexpected: %A" links)

[<Fact>]
let ``parses callouts, code fences and tables`` () =
    let source =
        "> [!NOTE] Heads up\n> line one\n> line two\n```\nlet x = 1\n```\n| A | B |\n| --- | --- |\n| 1 | 2 |"

    let blocks = Markdown.parse source |> List.filter (fun b -> b <> Markdown.Blank)

    match blocks with
    | [ Markdown.Callout("NOTE", "Heads up", [ "line one"; "line two" ])
        Markdown.CodeBlock [ "let x = 1" ]
        Markdown.Table([ "A"; "B" ], [ [ "1"; "2" ] ]) ] -> ()
    | other -> failwithf "unexpected: %A" other

[<Fact>]
let ``unclosed code fence consumes to end of document without looping`` () =
    match Markdown.parse "```\nlet x = 1" with
    | [ Markdown.CodeBlock [ "let x = 1" ] ] -> ()
    | other -> failwithf "unexpected: %A" other
