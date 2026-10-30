<#
.SYNOPSIS
    Backs up University OS user data to a timestamped zip archive.

.DESCRIPTION
    University OS stores everything the user creates in %APPDATA%\UniversityOS,
    outside the repository. This archives that folder whole.

    Safety properties, all deliberate:
      * Refuses to run while UniversityOS.exe is running. SQLite is in WAL mode,
        so copying a live database yields one that is missing recent writes and
        may not open at all.
      * Never deletes or modifies the source data.
      * Never uploads anything.
      * Never prints the contents of any file, so API keys under secrets\ cannot
        leak into a console log or a screenshot.

    Restore is deliberately NOT automated - see DATA_MIGRATION.md section 5.

.PARAMETER Destination
    Folder to write the archive into.
    Defaults to Documents\UniversityOS-Backups, created if missing.

.PARAMETER WhatIf
    Report what would be archived without writing anything.

.EXAMPLE
    .\scripts\backup-user-data.ps1

.EXAMPLE
    .\scripts\backup-user-data.ps1 -Destination "D:\Backups"

.EXAMPLE
    .\scripts\backup-user-data.ps1 -WhatIf
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Destination = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'UniversityOS-Backups')
)

$ErrorActionPreference = 'Stop'

$dataPath = Join-Path $env:APPDATA 'UniversityOS'

Write-Host ''
Write-Host 'University OS - user data backup' -ForegroundColor Cyan
Write-Host ''

# ---- 1. The data must exist ------------------------------------------------

if (-not (Test-Path $dataPath)) {
    Write-Host "No user data found at:" -ForegroundColor Yellow
    Write-Host "  $dataPath"
    Write-Host ''
    Write-Host 'This is normal if University OS has never been run on this account.'
    Write-Host 'Nothing to back up.'
    exit 0
}

# ---- 2. The app must be closed ---------------------------------------------
#
# WAL mode keeps recent writes in universityos.db-wal. A copy taken while the
# app holds the database open can be missing work or be unopenable.

$running = Get-Process -Name 'UniversityOS' -ErrorAction SilentlyContinue

if ($running) {
    Write-Host 'ABORTED: University OS appears to be running.' -ForegroundColor Red
    Write-Host ''
    Write-Host ("  Process ID(s): {0}" -f ($running.Id -join ', '))
    Write-Host ''
    Write-Host 'The database is in WAL mode, so a backup taken now could be missing'
    Write-Host 'your most recent work or fail to open at all.'
    Write-Host ''
    Write-Host 'Close University OS completely, then run this script again.'
    exit 1
}

# ---- 3. Survey, without reading contents -----------------------------------

$files = Get-ChildItem -Path $dataPath -Recurse -File -ErrorAction SilentlyContinue
$totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
if ($null -eq $totalBytes) { $totalBytes = 0 }

$dbPath = Join-Path $dataPath 'universityos.db'
$hasDatabase = Test-Path $dbPath
$documentCount = @(Get-ChildItem -Path (Join-Path $dataPath 'files') -Recurse -File -ErrorAction SilentlyContinue).Count
$secretCount = @(Get-ChildItem -Path (Join-Path $dataPath 'secrets') -File -ErrorAction SilentlyContinue).Count

Write-Host "Source:    $dataPath"
Write-Host ("Files:     {0}" -f $files.Count)
Write-Host ("Size:      {0:N1} MB" -f ($totalBytes / 1MB))
Write-Host ("Database:  {0}" -f $(if ($hasDatabase) { 'present' } else { 'MISSING' }))
Write-Host ("Documents: {0}" -f $documentCount)
# Count only. Contents are never read, printed or logged.
Write-Host ("Secrets:   {0} encrypted file(s) - contents never read" -f $secretCount)
Write-Host ''

if (-not $hasDatabase) {
    Write-Host 'WARNING: universityos.db was not found. Backing up anyway.' -ForegroundColor Yellow
    Write-Host ''
}

# ---- 4. Archive -------------------------------------------------------------

$stamp = Get-Date -Format 'yyyy-MM-dd-HHmmss'
$archiveName = "UniversityOS-userdata-$stamp.zip"
$archivePath = Join-Path $Destination $archiveName

if ($PSCmdlet.ShouldProcess($archivePath, 'Create backup archive')) {

    if (-not (Test-Path $Destination)) {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    }

    # Compress the folder itself, so the archive contains a top-level
    # "UniversityOS" directory and extracting to %APPDATA% restores in place.
    Compress-Archive -Path $dataPath -DestinationPath $archivePath -CompressionLevel Optimal

    $archive = Get-Item $archivePath

    Write-Host 'Backup complete.' -ForegroundColor Green
    Write-Host ''
    Write-Host "  $($archive.FullName)"
    Write-Host ("  {0:N1} MB" -f ($archive.Length / 1MB))
    Write-Host ''
    Write-Host 'Notes:' -ForegroundColor Cyan
    Write-Host '  * The archive is NOT encrypted. Keep it somewhere already protected.'
    Write-Host '  * Do not commit it - .gitignore excludes it, but do not move it into the repo.'
    Write-Host '  * secrets\ is DPAPI-encrypted for THIS Windows user on THIS machine.'
    Write-Host '    After restoring elsewhere you must re-enter API keys under Connections.'
    Write-Host '  * To restore, follow DATA_MIGRATION.md section 5. This script does not restore.'
    Write-Host ''
}
else {
    Write-Host 'WhatIf: nothing was written.' -ForegroundColor Yellow
    Write-Host "Would have created: $archivePath"
    Write-Host ''
}
