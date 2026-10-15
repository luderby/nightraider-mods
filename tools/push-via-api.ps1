#!/usr/bin/env pwsh
# Pushes a local git working tree to GitHub through the REST API.
#
# Why: on this machine github.com:443 is unreachable, so git-over-HTTPS cannot push, while
# api.github.com:443 works fine. The Git Data API (blobs -> tree -> commit -> ref) is served
# from api.github.com, so it is used instead of `git push`.
#
# Usage: pwsh tools/push-via-api.ps1 [-Repo owner/name] [-Branch main] [-Message "..."]

param(
    [string]$Repo = 'luderby/nightraider-mods',
    [string]$Branch = 'main',
    [string]$Message = ''
)

$ErrorActionPreference = 'Stop'
$root = (git rev-parse --show-toplevel).Trim()
Set-Location $root

# Files git would track, so .gitignore is honoured.
#
# core.quotepath=false stops git from octal-escaping non-ASCII names, and -z uses NUL
# separators so paths containing spaces survive intact. Both matter here because the repo
# has Chinese file names.
$files = @(git -c core.quotepath=false ls-files -z) -join '' -split "`0" | Where-Object { $_ -ne '' }
if (-not $files) { throw 'no tracked files to push' }
Write-Host "files to push: $($files.Count)"

function Api {
    # Note: not named $Args, which is a reserved automatic variable in PowerShell.
    # A native command writing to stderr becomes a terminating error while
    # ErrorActionPreference is Stop, so it is relaxed around the call and the exit code is
    # checked instead.
    param([string[]]$ApiArgs)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & gh api @ApiArgs 2>&1
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prev
    }
    if ($code -ne 0) { throw "gh api failed ($code): $($ApiArgs -join ' ')`n$out" }
    return (($out | ForEach-Object { "$_" }) -join "`n")
}

# Parent commit, when the branch already exists.
$parent = $null
try {
    $refJson = Api @("repos/$Repo/git/ref/heads/$Branch")
    $parent = ($refJson | ConvertFrom-Json).object.sha
    Write-Host "parent commit: $parent"
} catch {
    Write-Host 'branch does not exist yet, creating the first commit'
}

$entries = New-Object System.Collections.Generic.List[object]
$i = 0
foreach ($f in $files) {
    $i++
    $full = Join-Path $root $f
    # git ls-files prints forward slashes; normalise for the filesystem read.
    $full = $full -replace '/', [System.IO.Path]::DirectorySeparatorChar
    $bytes = [System.IO.File]::ReadAllBytes($full)
    $b64 = [Convert]::ToBase64String($bytes)

    $payload = @{ content = $b64; encoding = 'base64' } | ConvertTo-Json -Compress
    $tmp = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllText($tmp, $payload, [System.Text.UTF8Encoding]::new($false))
        $blobJson = Api @("repos/$Repo/git/blobs", '--input', $tmp)
    } finally {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    }
    $sha = ($blobJson | ConvertFrom-Json).sha

    $entries.Add(@{
        path = ($f -replace '\\', '/')
        mode = '100644'
        type = 'blob'
        sha  = $sha
    })
    if ($i % 10 -eq 0 -or $i -eq $files.Count) { Write-Host "  blob $i/$($files.Count)" }
}

# One tree with full paths; the API creates intermediate trees itself.
$treeBody = @{ tree = $entries } | ConvertTo-Json -Depth 6 -Compress
$tmpTree = [System.IO.Path]::GetTempFileName()
try {
    [System.IO.File]::WriteAllText($tmpTree, $treeBody, [System.Text.UTF8Encoding]::new($false))
    $treeJson = Api @("repos/$Repo/git/trees", '--input', $tmpTree)
} finally {
    Remove-Item $tmpTree -Force -ErrorAction SilentlyContinue
}
$treeSha = ($treeJson | ConvertFrom-Json).sha
Write-Host "tree: $treeSha"

if (-not $Message) {
    $Message = (git log -1 --pretty=%B).Trim()
    if (-not $Message) { $Message = 'sync from local' }
}

$commit = @{ message = $Message; tree = $treeSha }
if ($parent) { $commit.parents = @($parent) }
$tmpCommit = [System.IO.Path]::GetTempFileName()
try {
    [System.IO.File]::WriteAllText($tmpCommit, ($commit | ConvertTo-Json -Depth 4 -Compress), [System.Text.UTF8Encoding]::new($false))
    $commitJson = Api @("repos/$Repo/git/commits", '--input', $tmpCommit)
} finally {
    Remove-Item $tmpCommit -Force -ErrorAction SilentlyContinue
}
$commitSha = ($commitJson | ConvertFrom-Json).sha
Write-Host "commit: $commitSha"

if ($parent) {
    $refBody = @{ sha = $commitSha; force = $true } | ConvertTo-Json -Compress
    $tmpRef = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllText($tmpRef, $refBody, [System.Text.UTF8Encoding]::new($false))
        Api @("repos/$Repo/git/refs/heads/$Branch", '--method', 'PATCH', '--input', $tmpRef) | Out-Null
    } finally {
        Remove-Item $tmpRef -Force -ErrorAction SilentlyContinue
    }
    Write-Host "updated refs/heads/$Branch"
} else {
    $refBody = @{ ref = "refs/heads/$Branch"; sha = $commitSha } | ConvertTo-Json -Compress
    $tmpRef = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllText($tmpRef, $refBody, [System.Text.UTF8Encoding]::new($false))
        Api @("repos/$Repo/git/refs", '--input', $tmpRef) | Out-Null
    } finally {
        Remove-Item $tmpRef -Force -ErrorAction SilentlyContinue
    }
    Write-Host "created refs/heads/$Branch"
}

Write-Host ''
Write-Host "pushed: https://github.com/$Repo/tree/$Branch"
