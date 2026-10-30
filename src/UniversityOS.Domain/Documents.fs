namespace UniversityOS.Domain

open System

[<RequireQualifiedAccess>]
type DocumentKind =
    | Pdf
    | Slides
    | Word
    | Markdown
    | Text
    | Image
    | Audio
    | Other

[<RequireQualifiedAccess>]
module DocumentKind =
    let ofExtension (ext: string) =
        match ext.TrimStart('.').ToLowerInvariant() with
        | "pdf" -> DocumentKind.Pdf
        | "ppt" | "pptx" | "potx" -> DocumentKind.Slides
        | "doc" | "docx" -> DocumentKind.Word
        | "md" | "markdown" -> DocumentKind.Markdown
        | "txt" | "log" | "csv" -> DocumentKind.Text
        | "png" | "jpg" | "jpeg" | "gif" | "bmp" | "webp" -> DocumentKind.Image
        | "mp3" | "wav" | "m4a" | "ogg" | "flac" | "mp4" | "webm" -> DocumentKind.Audio
        | _ -> DocumentKind.Other

    let label =
        function
        | DocumentKind.Pdf -> "PDF"
        | DocumentKind.Slides -> "Slides"
        | DocumentKind.Word -> "Word"
        | DocumentKind.Markdown -> "Markdown"
        | DocumentKind.Text -> "Text"
        | DocumentKind.Image -> "Image"
        | DocumentKind.Audio -> "Audio"
        | DocumentKind.Other -> "File"

    let toInt =
        function
        | DocumentKind.Pdf -> 0
        | DocumentKind.Slides -> 1
        | DocumentKind.Word -> 2
        | DocumentKind.Markdown -> 3
        | DocumentKind.Text -> 4
        | DocumentKind.Image -> 5
        | DocumentKind.Audio -> 6
        | DocumentKind.Other -> 7

    let ofInt =
        function
        | 0 -> DocumentKind.Pdf
        | 1 -> DocumentKind.Slides
        | 2 -> DocumentKind.Word
        | 3 -> DocumentKind.Markdown
        | 4 -> DocumentKind.Text
        | 5 -> DocumentKind.Image
        | 6 -> DocumentKind.Audio
        | _ -> DocumentKind.Other

/// A file imported into the managed store. The original is copied, never referenced
/// in place, so the library survives the source file moving or being deleted.
type Document =
    { Id: DocumentId
      ModuleId: ModuleId option
      Title: string
      FileName: string
      /// Path inside the managed file store.
      StoredPath: string
      Kind: DocumentKind
      SizeBytes: int64
      /// Plain text extracted at import time (PDF/Word/Slides/Markdown/Text) — feeds search and AI.
      ExtractedText: string option
      Summary: string option
      ImportedAt: DateTime }
