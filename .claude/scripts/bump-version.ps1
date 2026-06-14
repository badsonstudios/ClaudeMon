# Bump the project version.
#
# Single source of truth: src\ClaudeMon\ClaudeMon.csproj <Version>. The installer
# (installer\ClaudeMon.iss) derives its version from the built assembly, so this is
# the only file that needs changing — build.sh then picks it up automatically.
#
# Usage: .\bump-version.ps1 <X.Y.Z>
#   e.g. .\bump-version.ps1 0.2.0
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Write-Error "Version must be in the form X.Y.Z (e.g. 0.2.0)"
    exit 1
}

# .claude\scripts -> repo root is two levels up
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$csproj = Join-Path $repoRoot 'src\ClaudeMon\ClaudeMon.csproj'

if (-not (Test-Path $csproj)) {
    Write-Error "Cannot find $csproj"
    exit 1
}

$content = Get-Content $csproj -Raw
if ($content -match '<Version>([^<]+)</Version>') {
    $old = $Matches[1]
}
else {
    $old = '<none>'
}

$updated = [regex]::Replace($content, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
Set-Content -Path $csproj -Value $updated -NoNewline -Encoding utf8

Write-Host "Bumped version: $old -> $Version"
Write-Host "Updated: $csproj"
Write-Host "The installer derives its version from the built assembly - no .iss change needed."
