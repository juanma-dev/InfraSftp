<#
.SYNOPSIS
    Builds, signs and packages an InfraSftp release with Velopack.

.DESCRIPTION
    End-to-end release pipeline:
      1. Reads the version from InfraSftp.csproj (single source of truth).
      2. Ensures a self-signed code-signing cert exists in the user's
         certificate store (creates one on first run, reuses afterwards).
      3. Exports the cert to a local .pfx so signtool can consume it.
      4. Publishes the app self-contained for win-x64 into ./publish.
      5. Calls `vpk pack` to produce the installer + delta nupkg into ./releases.
         Velopack invokes signtool with the parameters we hand in via
         --signParams, signing every file in the bundle.

    The .pfx is written to ./.signing/InfraSftp.pfx; both that path and
    ./publish, ./releases are gitignored.

    Note on encoding: this script avoids unicode glyphs in string literals
    because Windows PowerShell 5.1 reads .ps1 files as the system codepage
    (ANSI / Windows-1252) by default and mojibakes anything outside that
    range. ASCII-only stays portable across powershell.exe and pwsh.

.PARAMETER Version
    Override the version stamped into the release. Defaults to the
    csproj <Version> tag.

.PARAMETER NoSign
    Skip the signing step entirely. Useful for fast local smoke tests; never
    use this for an actual release.

.PARAMETER Channel
    Velopack channel name. Defaults to "win". Use "beta" / "preview" for
    parallel release tracks.

.EXAMPLE
    powershell -File ./scripts/build-release.ps1
    # Reads version from csproj, signs, packs.

.EXAMPLE
    powershell -File ./scripts/build-release.ps1 -Version 0.2.0
    # Override the version (does not write back to csproj).
#>
param(
    [string] $Version = '',
    [switch] $NoSign,
    [string] $Channel = 'win'
)

$ErrorActionPreference = 'Stop'

# 0. Locate repo root regardless of where the script is invoked from.
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $RepoRoot

$CsprojPath  = Join-Path $RepoRoot 'InfraSftp.csproj'
$PublishDir  = Join-Path $RepoRoot 'publish'
$ReleasesDir = Join-Path $RepoRoot 'releases'
$SigningDir  = Join-Path $RepoRoot '.signing'
$PfxPath     = Join-Path $SigningDir 'InfraSftp.pfx'

# 1. Resolve version.
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$proj = Get-Content $CsprojPath
    $Version = ($proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1).Trim()
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "No <Version> found in $CsprojPath. Set it or pass -Version."
    }
}

Write-Host ""
Write-Host "==> Releasing InfraSftp $Version on channel '$Channel'" -ForegroundColor Cyan
Write-Host "    (sign: $(if ($NoSign) { 'NO' } else { 'YES' }))"
Write-Host ""

# 2. Ensure self-signed code-signing cert exists & export PFX.
if (-not $NoSign) {
    $CertSubject = 'CN=InfraSftp Code Signing'
    $StoreLocation = 'Cert:\CurrentUser\My'

    $cert = Get-ChildItem -Path $StoreLocation |
            Where-Object { $_.Subject -eq $CertSubject -and $_.NotAfter -gt (Get-Date) } |
            Sort-Object NotAfter -Descending |
            Select-Object -First 1

    if ($null -eq $cert) {
        Write-Host "==> No code-signing cert found, generating one (valid 5 years)..." -ForegroundColor Yellow
        $cert = New-SelfSignedCertificate `
            -Type           CodeSigningCert `
            -Subject        $CertSubject `
            -CertStoreLocation $StoreLocation `
            -KeyUsage       DigitalSignature `
            -KeyExportPolicy Exportable `
            -KeyLength      2048 `
            -KeyAlgorithm   RSA `
            -HashAlgorithm  SHA256 `
            -NotAfter       (Get-Date).AddYears(5)
        Write-Host "    Created cert: $($cert.Thumbprint)" -ForegroundColor Green
        Write-Host ""
        Write-Host "    NOTE: this is a SELF-SIGNED cert. Windows SmartScreen will" -ForegroundColor Yellow
        Write-Host "          still warn end users on first run because the cert" -ForegroundColor Yellow
        Write-Host "          has no reputation. The signature does prove the" -ForegroundColor Yellow
        Write-Host "          package wasn't tampered with after build, and" -ForegroundColor Yellow
        Write-Host "          Velopack's auto-update path is far happier with" -ForegroundColor Yellow
        Write-Host "          signed bundles." -ForegroundColor Yellow
        Write-Host ""
    }
    else {
        Write-Host "==> Reusing existing code-signing cert: $($cert.Thumbprint)" -ForegroundColor DarkGray
    }

    if (-not (Test-Path $SigningDir)) {
        New-Item -ItemType Directory -Path $SigningDir | Out-Null
    }

    # The PFX password protects the on-disk file from random readers. It is
    # not sensitive in the cryptographic sense (the cert is self-signed and
    # only used to sign installers), so a constant local password is fine.
    # If a real CA-issued cert is added later, replace this with a value
    # pulled from environment / secret manager.
    $pfxPassword = ConvertTo-SecureString -String 'InfraSftp.local' -Force -AsPlainText
    Export-PfxCertificate -Cert $cert -FilePath $PfxPath -Password $pfxPassword | Out-Null

    # Locate signtool.exe (ships with Windows SDK).
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -eq $signtool) {
        $candidates = Get-ChildItem -Path 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
                      Where-Object { $_.FullName -match 'x64' } |
                      Sort-Object FullName -Descending
        if ($candidates) { $signtool = $candidates[0] }
    }
    if ($null -eq $signtool) {
        throw @"
signtool.exe not found. Install the Windows SDK (or the 'Visual Studio Build Tools' workload)
and re-run, or pass -NoSign for an unsigned smoke build.
Download: https://developer.microsoft.com/en-us/windows/downloads/windows-sdk/
"@
    }
    $SigntoolPath = if ($signtool -is [System.IO.FileInfo]) { $signtool.FullName } else { $signtool.Source }
    Write-Host "==> Using signtool: $SigntoolPath" -ForegroundColor DarkGray
}

# 3. Clean output dirs.
foreach ($d in @($PublishDir, $ReleasesDir)) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    New-Item -ItemType Directory -Path $d | Out-Null
}

# 4. Publish self-contained.
Write-Host "==> dotnet publish (Release, win-x64, self-contained)..." -ForegroundColor Cyan
dotnet publish $CsprojPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $PublishDir `
    /p:Version=$Version `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# 5. Pack with Velopack.
$PackArgs = @(
    'pack',
    '--packId',      'com.webjuanma.InfraSftp',
    '--packVersion', $Version,
    '--packDir',     $PublishDir,
    '--packTitle',   'InfraSftp',
    '--packAuthors', 'webjuanma',
    '--mainExe',     'InfraSftp.exe',
    '--outputDir',   $ReleasesDir,
    '--channel',     $Channel
)

if (-not $NoSign) {
    # signtool params: /fd sha256 (modern hash), /td sha256 (timestamp hash),
    # /tr ... (RFC 3161 timestamp server, free, run by DigiCert / Sectigo --
    # signed packages keep validating after the cert expires).
    # The {{file}} placeholder is substituted by Velopack for each artifact.
    $signParams = "/fd sha256 /td sha256 /tr http://timestamp.digicert.com /f `"$PfxPath`" /p InfraSftp.local"
    $PackArgs += @('--signParams', $signParams)
}

Write-Host "==> vpk pack..." -ForegroundColor Cyan
& vpk @PackArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed (exit $LASTEXITCODE)." }

Write-Host ""
Write-Host "DONE. Release $Version built." -ForegroundColor Green
Write-Host "     Artifacts in: $ReleasesDir"
Get-ChildItem $ReleasesDir | Format-Table -AutoSize Name, Length
