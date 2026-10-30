namespace UniversityOS.Infrastructure

open System
open System.IO
open System.Text
open UglyToad.PdfPig
open DocumentFormat.OpenXml.Packaging
open UniversityOS.Domain

/// Plain-text extraction per document kind. Extraction failures are soft: the
/// import still succeeds, the document just has no searchable text.
[<RequireQualifiedAccess>]
module TextExtraction =

    let private fromPdf (path: string) =
        use doc = PdfDocument.Open path
        let sb = StringBuilder()

        for page in doc.GetPages() do
            sb.AppendLine(page.Text) |> ignore

        sb.ToString()

    let private fromDocx (path: string) =
        use doc = WordprocessingDocument.Open(path, false)

        match doc.MainDocumentPart with
        | null -> ""
        | part ->
            match part.Document with
            | null -> ""
            | d ->
                match d.Body with
                | null -> ""
                | body ->
                    // InnerText concatenates runs without separators; walk paragraphs
                    // so line breaks survive.
                    let sb = StringBuilder()

                    for para in body.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>() do
                        sb.AppendLine(para.InnerText) |> ignore

                    sb.ToString()

    let private fromPptx (path: string) =
        use doc = PresentationDocument.Open(path, false)

        match doc.PresentationPart with
        | null -> ""
        | presentation ->
            let sb = StringBuilder()

            for slidePart in presentation.SlideParts do
                match slidePart.Slide with
                | null -> ()
                | slide ->
                    for text in slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>() do
                        sb.AppendLine text.Text |> ignore

                sb.AppendLine() |> ignore

            sb.ToString()

    /// Returns extracted text, or None when the format has no supported text
    /// representation (images, audio) or the file could not be parsed.
    let extract (kind: DocumentKind) (path: string) : string option =
        try
            match kind with
            | DocumentKind.Pdf -> Some(fromPdf path)
            | DocumentKind.Word -> Some(fromDocx path)
            | DocumentKind.Slides -> Some(fromPptx path)
            | DocumentKind.Markdown
            | DocumentKind.Text -> Some(File.ReadAllText path)
            | DocumentKind.Image
            | DocumentKind.Audio
            | DocumentKind.Other -> None
        with _ ->
            None
