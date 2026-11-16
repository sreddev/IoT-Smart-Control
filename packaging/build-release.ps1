<#
.SYNOPSIS
  Builds every Windows release artifact of IoT Smart Control in one go.

.DESCRIPTION
  1. Runs the unit tests (skip with -SkipTests).
  2. Publishes the app self-contained for win-x64 (no .NET install needed on the target).
  3. Bundles Node.js (node.exe + its LICENSE) as matter-bridge\node so Matter works out of the box.
  4. Compiles the Inno Setup installer  ->  out\IoTSmartControl-<ver>-Setup-x64.exe
  5. Packs + signs the sideload MSIX     ->  out\IoTSmartControl_<ver>.0_x64.msix (+ .cer)
  6. Packs the Microsoft Store upload     ->  out\IoTSmartControl_<store ver>_x64_store-upload.msix (unsigned)
  7. Writes SHA256SUMS-<ver>.txt for the GitHub release files (1, 5).

  The app version comes from src\LedvanceControl.App\LedvanceControl.App.csproj (<Version>). The Store
  numbers its packages on its own: packaging\AppxManifest.xml holds the Store version (1.0.0.0 was the
  first Store release) and is only used for the Store upload.

.PARAMETER NodeExe    node.exe to bundle (default: the one on PATH).
.PARAMETER NodeLicense Node.js LICENSE file for that version (default: packaging\obj\node\LICENSE).
.PARAMETER SkipTests  Don't run the test suite first.
.PARAMETER NoMsix     Only build the .exe installer.

.EXAMPLE
  pwsh packaging/build-release.ps1
#>
[CmdletBinding()]
param(
    [string]$NodeExe = (Get-Command node -ErrorAction SilentlyContinue).Source,
    [string]$NodeLicense = (Join-Path $PSScriptRoot 'obj/node/LICENSE'),
    [switch]$SkipTests,
    [switch]$NoMsix
)

$ErrorActionPreference = 'Stop'
$pkgDir  = $PSScriptRoot
$root    = Split-Path -Parent $pkgDir
$proj    = Join-Path $root 'src/LedvanceControl.App/LedvanceControl.App.csproj'
$publish = Join-Path $pkgDir 'obj/publish'
$nodeDir = Join-Path $pkgDir 'obj/node-bundle'
$outDir  = Join-Path $pkgDir 'out'

# ---- versions: the app's (csproj) and the Store package's (AppxManifest)
$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$storeVersion = ([xml](Get-Content (Join-Path $pkgDir 'AppxManifest.xml'))).Package.Identity.Version
Write-Host "==> IoT Smart Control $version (Microsoft Store package $storeVersion)" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

if (-not $SkipTests) {
    Write-Host '==> Running tests…' -ForegroundColor Cyan
    & dotnet test (Join-Path $root 'LedvanceControl.slnx') -c Release -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; nothing was packaged.' }
}

# ---- Node.js runtime for the Matter bridge
if (-not $NodeExe -or -not (Test-Path $NodeExe)) { throw 'node.exe not found: install Node.js LTS or pass -NodeExe.' }
if (-not (Test-Path $NodeLicense)) { throw "Node.js LICENSE not found at $NodeLicense (download it for $(& $NodeExe --version) from github.com/nodejs/node)." }
if (Test-Path $nodeDir) { Remove-Item $nodeDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $nodeDir | Out-Null
Copy-Item $NodeExe (Join-Path $nodeDir 'node.exe')
Copy-Item $NodeLicense (Join-Path $nodeDir 'LICENSE')
Write-Host "    bundling Node.js $(& $NodeExe --version)" -ForegroundColor DarkGray

# ---- self-contained publish
Write-Host '==> Publishing win-x64 (self-contained)…' -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
& dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false -o $publish -nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
if (-not (Test-Path (Join-Path $publish 'matter-bridge/bridge.mjs'))) { throw 'The Matter bridge bundle is missing from the publish output (is npm installed?).' }
Copy-Item $nodeDir (Join-Path $publish 'matter-bridge/node') -Recurse -Force

# ---- Inno Setup installer
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup).' }
Write-Host '==> Compiling the installer…' -ForegroundColor Cyan
& $iscc /Q "/DAppVersion=$version" "/DSourceDir=$publish" (Join-Path $pkgDir 'installer/IoTSmartControl.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed.' }
$setup = Join-Path $outDir "IoTSmartControl-$version-Setup-x64.exe"
Write-Host "    $setup" -ForegroundColor Green

# ---- MSIX
$files = @($setup)
if (-not $NoMsix) {
    Write-Host '==> Building the sideload MSIX…' -ForegroundColor Cyan
    & (Join-Path $pkgDir 'build-msix.ps1') -Sign -NodeDir $nodeDir -Version "$version.0"
    $files += Join-Path $outDir "IoTSmartControl_$version.0_x64.msix"
    $files += Join-Path $outDir 'IoTSmartControl-Test.cer'
    Write-Host '==> Building the Microsoft Store upload…' -ForegroundColor Cyan
    & (Join-Path $pkgDir 'build-msix.ps1') -NodeDir $nodeDir
    $store = Join-Path $outDir "IoTSmartControl_${storeVersion}_x64_store-upload.msix"
}

# ---- checksums
$sums = Join-Path $outDir "SHA256SUMS-$version.txt"
$files | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $_ -Leaf) } |
    Set-Content -Path $sums -Encoding ascii
Write-Host "==> Done. Release files:" -ForegroundColor Green
($files + $sums + @($store | Where-Object { $_ })) | ForEach-Object { '    {0,-48} {1,8:N1} MB' -f (Split-Path $_ -Leaf), ((Get-Item $_).Length / 1MB) }
