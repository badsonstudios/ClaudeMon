<#
.SYNOPSIS  Print a single value from the project's .env file.
.EXAMPLE   ./get-secret.ps1 GITHUB_TOKEN
.NOTES     Prints only the requested value — never the whole file.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][string]$Name,
    [string]$EnvFile
)
$ErrorActionPreference = 'Stop'
if (-not $EnvFile) { $EnvFile = Join-Path $PSScriptRoot '..\.env' }
if (-not (Test-Path $EnvFile)) { Write-Error ".env not found at: $EnvFile"; exit 1 }

foreach ($line in Get-Content $EnvFile) {
    $t = $line.Trim()
    if ($t -eq '' -or $t.StartsWith('#')) { continue }
    $idx = $t.IndexOf('=')
    if ($idx -lt 1) { continue }
    $k = $t.Substring(0, $idx).Trim()
    if ($k -ne $Name) { continue }
    $v = $t.Substring($idx + 1).Trim()
    if ($v.Length -ge 2) {
        $first = $v[0]; $last = $v[$v.Length - 1]
        if (($first -eq '"' -and $last -eq '"') -or ($first -eq "'" -and $last -eq "'")) {
            $v = $v.Substring(1, $v.Length - 2)
        }
    }
    Write-Output $v
    exit 0
}
Write-Error "Key '$Name' not found in $EnvFile"
exit 1
