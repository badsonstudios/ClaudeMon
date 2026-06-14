<#
.SYNOPSIS  Branch (if needed), commit, push, and open a GitHub PR via gh.
.DESCRIPTION
    Convenience executor for the commit/push/PR steps. APPROVAL FIRST:
    the project rule is to confirm with the user before committing or pushing.
.EXAMPLE
    ./new-pr.ps1 -Title "Add login form" -All
.EXAMPLE
    ./new-pr.ps1 -Title "Fix #42: null check" -Body "Guards against empty input" -Base develop
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Title,
    [string]$Body = '',
    [string]$Base = 'main',
    [string]$Branch,
    [switch]$All
)
$ErrorActionPreference = 'Stop'

# Must be inside a git work tree
git rev-parse --is-inside-work-tree | Out-Null

$current = (git branch --show-current).Trim()

# Decide branch
if ($current -eq $Base -or [string]::IsNullOrWhiteSpace($current)) {
    if (-not $Branch) {
        $slug = ($Title.ToLower() -replace '[^a-z0-9]+', '-').Trim('-')
        if ($slug.Length -gt 50) { $slug = $slug.Substring(0, 50).Trim('-') }
        $Branch = "feature/$slug"
    }
    Write-Host "Creating branch: $Branch" -ForegroundColor Cyan
    git checkout -b $Branch
}
elseif ($Branch -and $Branch -ne $current) {
    Write-Host "Creating branch: $Branch" -ForegroundColor Cyan
    git checkout -b $Branch
}
else {
    $Branch = $current
    Write-Host "Using current branch: $Branch" -ForegroundColor Cyan
}

if ($All) { git add -A }

# Commit if anything is staged
$staged = git diff --cached --name-only
if ($staged) {
    if ($Body) { git commit -m $Title -m $Body } else { git commit -m $Title }
}
else {
    Write-Host "No staged changes — skipping commit, proceeding to push/PR." -ForegroundColor Yellow
}

git push -u origin $Branch

if ($Body) { gh pr create --base $Base --title $Title --body $Body }
else { gh pr create --base $Base --title $Title --fill }
