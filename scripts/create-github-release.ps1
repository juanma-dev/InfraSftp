<#
.SYNOPSIS
    Creates a GitHub release from a tag and uploads release assets.

.DESCRIPTION
    Reads the GitHub credential from the local Git Credential Manager
    (no separate gh CLI required), creates a release via the REST API
    against the configured tag, then uploads each asset as a binary
    multipart blob.

    Re-running against an existing release is idempotent: assets that
    already exist are deleted before re-upload, so you can iterate on
    the artifact list without manually pruning the release.

.PARAMETER Owner
    GitHub owner (user or org). Defaults to "juanma-dev".

.PARAMETER Repo
    GitHub repo name. Defaults to "InfraSftp".

.PARAMETER Tag
    Git tag the release is anchored to. Must already be pushed.

.PARAMETER Name
    Display title of the release.

.PARAMETER BodyFile
    Path to a markdown file used as the release body.

.PARAMETER AssetPaths
    One or more files to attach to the release.

.EXAMPLE
    powershell -File ./scripts/create-github-release.ps1 `
      -Tag v0.2.0 -Name 'InfraSftp 0.2.0' `
      -BodyFile ./scripts/release-notes-0.2.0.md `
      -AssetPaths ./releases/com.webjuanma.InfraSftp-win-Setup.exe, ./releases-linux/infrasftp-0.2.0-1.fc44.x86_64.rpm
#>
param(
    [string] $Owner = 'juanma-dev',
    [string] $Repo  = 'InfraSftp',
    [Parameter(Mandatory)] [string] $Tag,
    [Parameter(Mandatory)] [string] $Name,
    [Parameter(Mandatory)] [string] $BodyFile,
    [Parameter(Mandatory)] [string[]] $AssetPaths
)

$ErrorActionPreference = 'Stop'

# 1. Pull the github.com credential from Git Credential Manager. The
#    helper expects key=value lines on stdin terminated by a blank
#    line. PowerShell's pipeline mangles binary newlines into CRLF and
#    re-encodes the stream, which trips git ("missing protocol field").
#    cmd.exe `<` redirects raw bytes — much more reliable here.
function Get-GitHubToken {
    $tmp = [System.IO.Path]::GetTempFileName()
    # Write LF-only, no BOM. ASCII encoding via .NET avoids the UTF-8
    # BOM that Set-Content -Encoding ascii would prepend on PS 5.1.
    $bytes = [System.Text.Encoding]::ASCII.GetBytes("protocol=https`nhost=github.com`n`n")
    [System.IO.File]::WriteAllBytes($tmp, $bytes)
    try {
        $out = (& cmd.exe /c "git credential fill < `"$tmp`"" 2>$null) -join "`n"
    } finally {
        Remove-Item $tmp -Force
    }
    if ($out -notmatch '(?m)^password=(.+)$') {
        throw "git credential fill did not return a password -- make sure GitHub is logged in via the Credential Manager."
    }
    return $matches[1]
}

$Token = Get-GitHubToken
$Headers = @{
    Authorization = "Bearer $Token"
    Accept        = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent'  = "$Owner/$Repo-release-script"
}

$ApiBase    = "https://api.github.com/repos/$Owner/$Repo"
$UploadBase = "https://uploads.github.com/repos/$Owner/$Repo"

# 2. Look up the release; create if missing.
$existing = $null
try {
    $existing = Invoke-RestMethod -Method Get `
        -Uri "$ApiBase/releases/tags/$Tag" -Headers $Headers
    Write-Host "==> Release for tag $Tag already exists (id $($existing.id)) -- will reuse." -ForegroundColor DarkGray
} catch {
    if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
    Write-Host "==> Creating release for tag $Tag..." -ForegroundColor Cyan
    # Force UTF-8: PS 5.1's Get-Content -Raw without BOM falls back to
    # the system codepage and corrupts non-ASCII chars in the body.
    $bodyText = [System.IO.File]::ReadAllText(
        (Resolve-Path $BodyFile),
        [System.Text.Encoding]::UTF8)
    $payload = @{
        tag_name   = $Tag
        name       = $Name
        body       = $bodyText
        draft      = $false
        prerelease = $false
    } | ConvertTo-Json -Depth 5

    # Send the body as UTF-8 bytes; -Body string defaults to ISO-8859-1
    # which would re-mangle the very chars we just preserved.
    $payloadBytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
    $existing = Invoke-RestMethod -Method Post `
        -Uri "$ApiBase/releases" -Headers $Headers `
        -Body $payloadBytes -ContentType 'application/json; charset=utf-8'
    Write-Host "    Created release id $($existing.id)" -ForegroundColor Green
}

$ReleaseId = $existing.id

# 3. For each asset: delete the old one if present, then upload fresh.
foreach ($path in $AssetPaths) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Asset not found: $path"
    }
    $file = Get-Item -LiteralPath $path
    $assetName = $file.Name

    $stale = $existing.assets | Where-Object { $_.name -eq $assetName }
    if ($stale) {
        Write-Host "==> Deleting existing asset '$assetName' (id $($stale.id))..." -ForegroundColor Yellow
        Invoke-RestMethod -Method Delete `
            -Uri "$ApiBase/releases/assets/$($stale.id)" -Headers $Headers | Out-Null
    }

    Write-Host "==> Uploading $assetName ($([math]::Round($file.Length / 1MB, 1)) MB)..." -ForegroundColor Cyan

    # Pick a content type that GitHub will pass through verbatim. The
    # actual value doesn't gate downloads -- Octet-stream is the safe
    # default for binary; the .asc public key gets text/plain so it
    # previews in-browser.
    $contentType = switch -wildcard ($assetName) {
        '*.asc'  { 'application/pgp-keys' }
        '*.json' { 'application/json' }
        default  { 'application/octet-stream' }
    }

    $uploadHeaders = $Headers.Clone()
    $uploadHeaders['Content-Type'] = $contentType

    $uploadUri = "$UploadBase/releases/$ReleaseId/assets?name=$([uri]::EscapeDataString($assetName))"

    # -InFile streams the file rather than buffering -- required for
    # the 50+ MB Windows installer that would otherwise eat memory.
    Invoke-RestMethod -Method Post -Uri $uploadUri `
        -Headers $uploadHeaders -InFile $file.FullName | Out-Null

    Write-Host "    OK" -ForegroundColor Green
}

Write-Host ""
Write-Host "DONE. Release: https://github.com/$Owner/$Repo/releases/tag/$Tag" -ForegroundColor Green
