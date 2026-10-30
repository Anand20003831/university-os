# User data — location, backup and restore

**Your data is not in this repository.** Copying, zipping, cloning or moving the
source tree moves **none** of your notes, sessions, documents or settings.

---

## 1. Where the data lives

Everything University OS creates lives in one folder:

```
%APPDATA%\UniversityOS\
```

which expands to `C:\Users\<your-username>\AppData\Roaming\UniversityOS`.

| Item | Path | What it holds |
|---|---|---|
| Database | `universityos.db` | modules, assignments, events, study sessions, notes, documents metadata, lectures, revision items, links, inbox, chat, connections, sync log, settings, appearance, UI state, AI memory |
| WAL / shared memory | `universityos.db-wal`, `universityos.db-shm` | in-flight transactions — **transient, but must be copied with the database** (see §3) |
| Imported documents | `files\` | the managed copy of every PDF/DOCX/etc. you imported |
| Secrets | `secrets\` | API keys and OAuth tokens, DPAPI-encrypted |

The path is resolved at runtime from
`Environment.SpecialFolder.ApplicationData` (`src/UniversityOS.App/Program.fs`),
never relative to the executable — so the app behaves identically whether it
runs from `publish\`, from `Program Files`, or from a USB stick.

**Paths containing your username:** every path above. The repository itself
contains no absolute user paths (verified).

---

## 2. What must never be committed

`.gitignore` already blocks all of it, but to be explicit — never copy into the
repository:

- `universityos.db` and its `-wal` / `-shm` companions
- anything from `files\` (your actual documents)
- anything from `secrets\` (your API keys)
- any backup archive produced by `scripts\backup-user-data.ps1`

---

## 3. Close the app before copying

**SQLite in WAL mode keeps recent writes in `universityos.db-wal`.** Copying only
`universityos.db` from a running application gives you a database missing your
most recent work, and can produce a file SQLite refuses to open.

Before backing up or moving data:

1. **Close University OS completely.** Check Task Manager for
   `UniversityOS.exe` if unsure.
2. Copy the **whole `UniversityOS` folder**, not selected files.

The backup script in §4 enforces both of these.

---

## 4. Backing up

```powershell
# From the repository root. Writes to Documents\UniversityOS-Backups by default.
.\scripts\backup-user-data.ps1

# Or choose a destination
.\scripts\backup-user-data.ps1 -Destination "D:\Backups"

# See what it would do without writing anything
.\scripts\backup-user-data.ps1 -WhatIf
```

It produces `UniversityOS-userdata-YYYY-MM-DD-HHmmss.zip` containing the whole
`%APPDATA%\UniversityOS` tree.

The script **never uploads anything, never deletes anything, and never prints
secret contents** — it reports file counts and sizes only. It refuses to run if
`UniversityOS.exe` is running.

---

## 5. Restoring on another Windows account or machine

Restore is deliberately **manual**. No script overwrites a database
automatically.

1. **Install and run University OS once** on the target machine, then close it.
   This creates `%APPDATA%\UniversityOS\` with a migrated, empty database.
2. **Close the application completely.**
3. Back up whatever is already there, in case you are restoring over real data:
   ```powershell
   Rename-Item "$env:APPDATA\UniversityOS" "UniversityOS.before-restore"
   ```
4. Extract the backup archive so that `universityos.db`, `files\` and `secrets\`
   sit directly inside `%APPDATA%\UniversityOS\`:
   ```powershell
   Expand-Archive -Path "D:\Backups\UniversityOS-userdata-2026-07-27-120000.zip" `
                  -DestinationPath "$env:APPDATA"
   ```
   The archive contains a top-level `UniversityOS` folder, so extracting to
   `%APPDATA%` puts everything in the right place.
5. Start University OS. Startup migrations run automatically and are additive
   and idempotent, so a database from an older build is upgraded in place.

### ⚠️ Secrets do not survive the move

`secrets\` is encrypted with **Windows DPAPI in `CurrentUser` scope**. Only the
original Windows user on the original machine can decrypt it. After restoring to
a different Windows account or machine the files are present but undecryptable,
and the app will behave as though no key were set.

**You must re-enter your API keys** on the new account:

> **Connections** → **Claude** → paste your Anthropic API key.

Nothing else is affected — no notes, sessions or documents depend on DPAPI.

You may safely delete the stale `secrets\` folder after restoring.

### If you are moving to a different username

Nothing needs editing. Every path is resolved from `%APPDATA%` at runtime, so
the new username is picked up automatically.

---

## 6. Privacy

- The database contains your real coursework, notes and calendar. Treat a backup
  archive as personal data: keep it off shared drives and out of public
  repositories.
- Archives are **not encrypted**. Put them somewhere already protected, or
  encrypt them yourself (BitLocker, 7-Zip AES, an encrypted external drive).
- The application requires no internet connection for core functionality.
  Network access happens only when you explicitly connect a service under
  **Connections**.
- The AI memory store is deliberately transparent — everything it remembers is
  visible and deletable in the UI.

---

## 7. Quick reference

| Task | Command |
|---|---|
| Open the data folder | `explorer "$env:APPDATA\UniversityOS"` |
| Check the folder's size | `"{0:N1} MB" -f ((Get-ChildItem "$env:APPDATA\UniversityOS" -Recurse -File \| Measure-Object Length -Sum).Sum / 1MB)` |
| Back up | `.\scripts\backup-user-data.ps1` |
| Confirm the app is closed | `Get-Process UniversityOS -ErrorAction SilentlyContinue` |
| Start fresh (destructive) | close the app, then `Remove-Item "$env:APPDATA\UniversityOS" -Recurse` |
