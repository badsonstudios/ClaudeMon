# Publish a GitHub release for the current version, if one doesn't already exist.
#
#   - Version comes from src\ClaudeMon\ClaudeMon.csproj <Version> (single source of truth).
#   - Release notes are extracted from CHANGELOG.md for that version.
#   - If older CHANGELOG.md versions were never published as GitHub releases (a release got
#     skipped), their sections are rolled into this release's notes automatically, with a
#     notice - so no shipped work is ever invisible on the releases page.
#   - Attaches the built installer dist\ClaudeMon-Setup-<version>.exe when present, plus its
#     SHA-256 checksum (<installer>.sha256) - required for in-app auto-updates to verify.
#   - No-op if a release tagged v<version> already exists.
#
# APPROVAL FIRST: this publishes a public GitHub release - confirm with the user.
#
# Usage: .\publish-release.ps1 [-Target <branch-or-sha>] [-Draft]
param(
    [string]$Target = "",
    [switch]$Draft
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$csproj = Join-Path $repoRoot 'src\ClaudeMon\ClaudeMon.csproj'
$changelog = Join-Path $repoRoot 'CHANGELOG.md'

if (-not (Test-Path $csproj)) { Write-Error "Cannot find $csproj"; exit 1 }

$csprojText = Get-Content $csproj -Raw
if ($csprojText -notmatch '<Version>([^<]+)</Version>') {
    Write-Error "Could not read <Version> from $csproj"; exit 1
}
$version = $Matches[1]
$tag = "v$version"

# Idempotent: skip if the release already exists.
& gh release view $tag *> $null
if ($LASTEXITCODE -eq 0) {
    Write-Host "Release $tag already exists - nothing to publish."
    exit 0
}

# Extract this version's notes from CHANGELOG.md.
$notesLines = @()
$inBlock = $false
foreach ($line in (Get-Content $changelog)) {
    if ($line.StartsWith("## [$version]")) { $inBlock = $true; continue }
    if ($inBlock -and $line.StartsWith("## ")) { break }
    if ($inBlock) { $notesLines += $line }
}
$notes = ($notesLines -join "`n").Trim()
if ([string]::IsNullOrWhiteSpace($notes)) {
    Write-Error ("CHANGELOG.md has no section for $version - add one before publishing. " +
        "(Publishing without notes creates an empty release the auto-updater will offer.)")
    exit 1
}

# Safety net: roll up older changelog versions that were never published as releases.
# Walk CHANGELOG.md versions older than this one (the file is newest-first) and collect
# their sections until we hit the first version that already has a GitHub release.
$changelogLines = Get-Content $changelog
$clVersions = @()
foreach ($line in $changelogLines) {
    if ($line -match '^## \[([0-9]+\.[0-9]+\.[0-9]+)\]') { $clVersions += $Matches[1] }
}
$rollup = @()
$seenCurrent = $false
foreach ($v in $clVersions) {
    if ($v -eq $version) { $seenCurrent = $true; continue }
    if (-not $seenCurrent) { continue }
    & gh release view "v$v" *> $null
    if ($LASTEXITCODE -eq 0) { break }
    Write-Warning "Changelog version $v was never published - rolling its notes into $tag."
    $sectionLines = @()
    $inBlock = $false
    foreach ($line in $changelogLines) {
        if ($line.StartsWith("## [$v]")) { $inBlock = $true; continue }
        if ($inBlock -and $line.StartsWith("## ")) { break }
        if ($inBlock) { $sectionLines += $line }
    }
    $rollup += "## $v (previously unpublished)`n`n" + ($sectionLines -join "`n").Trim()
}
if ($rollup.Count -gt 0) {
    $notes = "This release also rolls up previously unpublished changelog versions - their GitHub releases were never created; their notes are included below.`n`n$notes`n`n---`n`n" + ($rollup -join "`n`n")
}

$asset = Join-Path $repoRoot "dist\ClaudeMon-Setup-$version.exe"

$ghArgs = @('release', 'create', $tag, '--title', $tag, '--notes', $notes)
if ($Draft) { $ghArgs += '--draft' }
if ($Target) { $ghArgs += @('--target', $Target) }
if (Test-Path $asset) {
    # (Re)generate the checksum beside the installer, in sha256sum "<hash>  <filename>" format
    # (bare filename) so both script variants produce identical assets.
    $hash = (Get-FileHash $asset -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumFile = "$asset.sha256"
    $fileName = Split-Path $asset -Leaf
    Set-Content -Path $checksumFile -Value "$hash  $fileName" -Encoding ascii -NoNewline
    $ghArgs += @($asset, $checksumFile)
}
else {
    Write-Warning "Installer not found at $asset - publishing notes without an asset."
    Write-Warning "Run 'bash installer/build.sh' first to attach the installer."
}

Write-Host "Publishing GitHub release $tag..."
& gh @ghArgs
Write-Host "Done: $tag"
