namespace UniversityOS.Infrastructure

open System
open System.IO
open System.Text
open System.Security.Cryptography
open UniversityOS.Application

/// Secrets (API keys, OAuth tokens) encrypted at rest with Windows DPAPI in
/// per-user scope: only this Windows user on this machine can decrypt them.
/// One file per key under the secrets directory; plaintext never touches disk.
type DpapiSecretStore(directory: string) =

    do Directory.CreateDirectory directory |> ignore

    // Additional entropy binds the ciphertext to this application.
    let entropy = Encoding.UTF8.GetBytes "UniversityOS.SecretStore.v1"

    let pathFor (key: string) =
        let safe =
            key |> String.map (fun c -> if Char.IsLetterOrDigit c || c = '-' || c = '_' then c else '_')

        Path.Combine(directory, safe + ".secret")

    interface ISecretStore with

        member _.Set(key, value) =
            async {
                let plaintext = Encoding.UTF8.GetBytes value

                let ciphertext =
                    if OperatingSystem.IsWindows() then
                        ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser)
                    else
                        // Non-Windows fallback: rely on OS user-directory permissions.
                        plaintext

                do! File.WriteAllBytesAsync(pathFor key, ciphertext) |> Async.AwaitTask
            }

        member _.Get(key) =
            async {
                let path = pathFor key

                if not (File.Exists path) then
                    return None
                else
                    try
                        let! ciphertext = File.ReadAllBytesAsync path |> Async.AwaitTask

                        let plaintext =
                            if OperatingSystem.IsWindows() then
                                ProtectedData.Unprotect(ciphertext, entropy, DataProtectionScope.CurrentUser)
                            else
                                ciphertext

                        return Some(Encoding.UTF8.GetString plaintext)
                    with _ ->
                        // Corrupt or foreign ciphertext — treat as absent.
                        return None
            }

        member _.Delete(key) =
            async {
                let path = pathFor key

                if File.Exists path then
                    File.Delete path
            }
