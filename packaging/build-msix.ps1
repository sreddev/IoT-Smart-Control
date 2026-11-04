<#
.SYNOPSIS
  Builds a Microsoft Store-ready MSIX package for IoT Smart Control.

.DESCRIPTION
  Publishes the WPF app self-contained (no .NET runtime dependency on the target
  machine), stages it with the MSIX manifest and visual assets, and packs an .msix
  with makeappx.exe from the Windows SDK.

  By default the package is produced UNSIGNED, which is what you upload to Partner
  Center (the Store signs it). Pass -Sign to create/reuse a self-signed certificate
  and sign the package so you can sideload-install it locally for testing.

.PARAMETER Runtime     win-x64 (default) or win-arm64.
.PARAMETER Sign        Also sign with a self-signed cert (local testing only).
.PARAMETER Install     Implies -Sign; trusts the cert and installs the package.

.EXAMPLE
  pwsh packaging/build-msix.ps1                 # unsigned .msix for Store upload
  pwsh packaging/build-msix.ps1 -Sign           # signed .msix for local sideload
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')] [string]$Runtime = 'win-x64',
    [switch]$Sign,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
if ($Install) { $Sign = $true }

$pkgDir  = $PSScriptRoot
$root    = Split-Path -Parent $pkgDir
$proj    = Join-Path $root 'src/LedvanceControl.App/LedvanceControl.App.csproj'
$stage   = Join-Path $pkgDir 'obj/stage'
$outDir  = Join-Path $pkgDir 'out'
$arch    = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64' }

# Version + Publisher are read straight from the manifest so there is one source of truth.
[xml]$manifest = Get-Content (Join-Path $pkgDir 'AppxManifest.xml')
$version   = $manifest.Package.Identity.Version
$publisher = $manifest.Package.Identity.Publisher
$msixPath  = Join-Path $outDir "IoTSmartControl_${version}_${arch}.msix"

Write-Host "==> Publishing $Runtime (self-contained)…" -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage, $outDir | Out-Null
& dotnet publish $proj -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false `
    -o $stage
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Write-Host "==> Staging manifest + visual assets…" -ForegroundColor Cyan
Copy-Item (Join-Path $pkgDir 'AppxManifest.xml') $stage -Force
Copy-Item (Join-Path $pkgDir 'Images') $stage -Recurse -Force
# MSIX cannot contain a .pdb-only app; keep the folder lean.
Get-ChildItem $stage -Filter *.pdb -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue

# Locate the newest Windows SDK that has makeappx/signtool.
function Find-SdkTool([string]$name) {
    $bins = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    Get-ChildItem $bins -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^10\.' } | Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName "x64/$name" } |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
$makeappx = Find-SdkTool 'makeappx.exe'
if (-not $makeappx) { throw 'makeappx.exe not found. Install the Windows 10/11 SDK.' }

Write-Host "==> Packing $msixPath …" -ForegroundColor Cyan
& $makeappx pack /o /h SHA256 /d $stage /p $msixPath
if ($LASTEXITCODE -ne 0) { throw 'makeappx pack failed.' }
Write-Host "    Packed: $msixPath" -ForegroundColor Green

if ($Sign) {
    $signtool = Find-SdkTool 'signtool.exe'
    if (-not $signtool) { throw 'signtool.exe not found.' }
    $cerPath = Join-Path $outDir 'IoTSmartControl-Test.cer'

    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $publisher } | Select-Object -First 1
    if (-not $cert) {
        Write-Host "==> Creating self-signed test certificate ($publisher)…" -ForegroundColor Cyan
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $publisher `
            -KeyUsage DigitalSignature -FriendlyName 'IoT Smart Control (test)' `
            -CertStoreLocation Cert:\CurrentUser\My `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    }
    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

    Write-Host "==> Signing package…" -ForegroundColor Cyan
    & $signtool sign /fd SHA256 /a /sha1 $cert.Thumbprint $msixPath
    if ($LASTEXITCODE -ne 0) { throw 'signtool sign failed.' }
    Write-Host "    Signed. Public cert exported to $cerPath" -ForegroundColor Green

    if ($Install) {
        Write-Host "==> Trusting cert (Trusted People, LocalMachine) and installing…" -ForegroundColor Cyan
        Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
        Add-AppxPackage -Path $msixPath
        Write-Host '    Installed. Launch "IoT Smart Control" from Start.' -ForegroundColor Green
    } else {
        Write-Host "    To install: trust $cerPath into LocalMachine\TrustedPeople, then Add-AppxPackage $msixPath" -ForegroundColor Yellow
    }
}

Write-Host "`nDone." -ForegroundColor Green
