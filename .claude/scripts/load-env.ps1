<#
.SYNOPSIS  Load .env into the current PowerShell session.
.DESCRIPTION
    DOT-SOURCE this script so the variables persist in your shell:
        . .\.claude\scripts\load-env.ps1
    After loading, child processes (e.g. gh) inherit the variables.
#>
[CmdletBinding()]
param([string]$EnvFile)

if (-not $EnvFile) { $EnvFile = Join-Path $PSScriptRoot '..\.env' }
if (-not (Test-Path $EnvFile)) { Write-Warning ".env not found at: $EnvFile"; return }

$count = 0
foreach ($line in Get-Content $EnvFile) {
    $t = $line.Trim()
    if ($t -eq '' -or $t.StartsWith('#')) { continue }
    $idx = $t.IndexOf('=')
    if ($idx -lt 1) { continue }
    $k = $t.Substring(0, $idx).Trim()
    $v = $t.Substring($idx + 1).Trim()
    if ($v.Length -ge 2) {
        $first = $v[0]; $last = $v[$v.Length - 1]
        if (($first -eq '"' -and $last -eq '"') -or ($first -eq "'" -and $last -eq "'")) {
            $v = $v.Substring(1, $v.Length - 2)
        }
    }
    if ($v -eq '') { continue }   # skip empty placeholders
    [Environment]::SetEnvironmentVariable($k, $v, 'Process')
    Set-Item -Path "env:$k" -Value $v
    $count++
}
Write-Host "Loaded $count variable(s) from $EnvFile" -ForegroundColor Green
