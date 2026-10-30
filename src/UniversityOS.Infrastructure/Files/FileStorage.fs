namespace UniversityOS.Infrastructure

open System
open System.IO

/// The managed document store: imported files are copied under the app data
/// directory so the library never depends on the original file's location.
type FileStorage(rootDirectory: string) =

    do Directory.CreateDirectory rootDirectory |> ignore

    member _.Root = rootDirectory

    /// Copies a file into the store under a collision-proof name, returns the
    /// stored absolute path.
    member _.Import(sourcePath: string) : Result<string, string> =
        try
            if not (File.Exists sourcePath) then
                Error(sprintf "File not found: %s" sourcePath)
            else
                let fileName = Path.GetFileName sourcePath
                let subDir = DateTime.Now.ToString "yyyy-MM"
                let targetDir = Path.Combine(rootDirectory, subDir)
                Directory.CreateDirectory targetDir |> ignore

                let uniqueName =
                    sprintf "%s_%s" (Guid.NewGuid().ToString("N").Substring(0, 8)) fileName

                let targetPath = Path.Combine(targetDir, uniqueName)
                File.Copy(sourcePath, targetPath)
                Ok targetPath
        with ex ->
            Error ex.Message

    member _.Delete(storedPath: string) =
        try
            if File.Exists storedPath then
                File.Delete storedPath

            Ok()
        with ex ->
            Error ex.Message
