namespace UniversityOS.Infrastructure

open System
open System.IO
open UniversityOS.Domain
open UniversityOS.Application

/// Full document import: copy into the managed store, extract text, persist,
/// index for search. Everything a dropped file needs to become part of the
/// knowledge system.
type DocumentImportService
    (storage: FileStorage, documents: IDocumentRepository, activity: IActivityLog, clock: IClock) =

    interface IDocumentImportService with

        member _.ImportFile(sourcePath, moduleId) =
            async {
                match storage.Import sourcePath with
                | Error e -> return Error e
                | Ok storedPath ->
                    let fileName = Path.GetFileName sourcePath
                    let kind = DocumentKind.ofExtension (Path.GetExtension fileName)
                    let extracted = TextExtraction.extract kind storedPath

                    let document =
                        { Id = DocumentId.New()
                          ModuleId = moduleId
                          Title = Path.GetFileNameWithoutExtension fileName
                          FileName = fileName
                          StoredPath = storedPath
                          Kind = kind
                          SizeBytes = FileInfo(storedPath).Length
                          ExtractedText = extracted |> Option.filter (fun t -> t.Trim().Length > 0)
                          Summary = None
                          ImportedAt = clock.Now }

                    do! documents.Save document

                    do! activity.Log(
                            ActivityEntry.create
                                clock.Now
                                ActivityKind.DocumentImported
                                (sprintf "Imported \"%s\"" document.Title)
                                (Some(sprintf "%s · %s" (DocumentKind.label kind) fileName))
                                (Some { Kind = EntityKind.Document; Id = document.Id.Value })
                        )

                    return Ok document
            }
