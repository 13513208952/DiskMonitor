#Requires -Version 5.1
<#
.SYNOPSIS
  Build DiskMonitor installer packages: MSI + NSIS EXE.
  All artifacts go to dist\.

.NOTES
  Requires: dotnet, wix (global tool), NSIS at default path.
  Run from project root (C:\diskmonitor).
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root    = $PSScriptRoot
$distDir = Join-Path $root "dist"
$pubRoot = Join-Path $root "publish\DiskMonitor"
$svcPub  = Join-Path $pubRoot "service"
$nsisExe = "C:\Program Files (x86)\NSIS\makensis.exe"

# ════════════════════════════════════════════════════════════════════════════
# New-WixFragment
# Generates a WiX v4 fragment declaring directories + component group.
# All subdirectories are assumed to be at depth 1 (flat structure).
# ════════════════════════════════════════════════════════════════════════════
function New-WixFragment {
    param(
        [System.IO.FileInfo[]] $Files,         # files to include
        [string]               $BaseDir,        # root of the source tree (no trailing \)
        [string]               $RootDirId,      # WiX Directory Id for BaseDir
        [string]               $GroupId,        # ComponentGroup Id
        [string]               $Prefix,         # short prefix for IDs (e.g. FE, SV)
        [bool]                 $Permanent,      # Permanent="yes" on components
        [string]               $OutputPath
    )

    # Collect distinct subdirectory relative paths
    $subDirs = $Files |
        ForEach-Object { $relDir = Split-Path ($_.FullName.Substring($BaseDir.Length + 1)) -Parent; $relDir } |
        Where-Object   { $_ -ne '' } |
        Select-Object -Unique |
        Sort-Object

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
    [void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
    [void]$sb.AppendLine('  <Fragment>')

    # ── Directory declarations ─────────────────────────────────────────────
    if ($subDirs.Count -gt 0) {
        [void]$sb.AppendLine("    <DirectoryRef Id=""$RootDirId"">")
        foreach ($rel in $subDirs) {
            $name  = Split-Path $rel -Leaf
            $dirId = "${Prefix}_DIR_" + ($rel -replace '[^A-Za-z0-9]', '_')
            [void]$sb.AppendLine("      <Directory Id=""$dirId"" Name=""$name"" />")
        }
        [void]$sb.AppendLine("    </DirectoryRef>")
    }

    # ── Component group ────────────────────────────────────────────────────
    [void]$sb.AppendLine("    <ComponentGroup Id=""$GroupId"">")

    $permAttr = if ($Permanent) { ' Permanent="yes"' } else { '' }
    $counter = 0

    foreach ($file in ($Files | Sort-Object FullName)) {
        $counter++
        $relPath = $file.FullName.Substring($BaseDir.Length + 1)
        $relDir  = Split-Path $relPath -Parent

        $dirRef = if ($relDir -eq '') {
            $RootDirId
        } else {
            "${Prefix}_DIR_" + ($relDir -replace '[^A-Za-z0-9]', '_')
        }

        $compId = "${Prefix}_" + $counter.ToString("D5")
        $fileId = "${Prefix}_F" + $counter.ToString("D5")

        [void]$sb.AppendLine("      <Component Id=""$compId"" Directory=""$dirRef""$permAttr>")
        [void]$sb.AppendLine("        <File Id=""$fileId"" Source=""$($file.FullName)"" KeyPath=""yes"" />")
        [void]$sb.AppendLine("      </Component>")
    }

    [void]$sb.AppendLine("    </ComponentGroup>")
    [void]$sb.AppendLine("  </Fragment>")
    [void]$sb.AppendLine("</Wix>")

    $sb.ToString() | Set-Content $OutputPath -Encoding UTF8
    Write-Host ("  {0}: {1} files, {2} subdirs → {3}" -f $Prefix, $counter, $subDirs.Count, (Split-Path $OutputPath -Leaf)) -ForegroundColor DarkGray
}

# ════════════════════════════════════════════════════════════════════════════
# Main
# ════════════════════════════════════════════════════════════════════════════

if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir | Out-Null }

# ── [1/6] Publish ────────────────────────────────────────────────────────────
Write-Host "[1/6] Running publish.ps1..." -ForegroundColor Cyan
& "$root\publish.ps1"
if ($LASTEXITCODE -ne 0) { throw "publish.ps1 failed" }
if (-not (Test-Path "$pubRoot\DiskMonitor.Frontend.exe")) { throw "Frontend EXE missing after publish" }
if (-not (Test-Path "$svcPub\DiskMonitor.Service.exe"))   { throw "Service EXE missing after publish" }
Write-Host "  OK" -ForegroundColor Green

# ── [2/6] Build CA DLL ───────────────────────────────────────────────────────
Write-Host "[2/6] Building CA DLL..." -ForegroundColor Cyan
$actionsProj = "$root\DiskMonitor.InstallerActions\DiskMonitor.InstallerActions.csproj"
dotnet build $actionsProj -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "InstallerActions build failed" }
$caDll = "$root\DiskMonitor.InstallerActions\bin\Release\net48\DiskMonitor.InstallerActions.CA.dll"
if (-not (Test-Path $caDll)) { throw "CA DLL missing: $caDll" }
Write-Host "  OK: DiskMonitor.InstallerActions.CA.dll" -ForegroundColor Green

# ── [3/6] Generate WiX fragments ─────────────────────────────────────────────
Write-Host "[3/6] Generating WiX fragments..." -ForegroundColor Cyan

$feFiles = Get-ChildItem $pubRoot -Recurse -File |
           Where-Object { $_.FullName -notlike "$svcPub\*" }
$svFiles = Get-ChildItem $svcPub   -Recurse -File

New-WixFragment `
    -Files      $feFiles `
    -BaseDir    $pubRoot `
    -RootDirId  "INSTALLFOLDER" `
    -GroupId    "FrontendComponents" `
    -Prefix     "FE" `
    -Permanent  $false `
    -OutputPath "$root\DiskMonitor.Installer\FrontendFiles.wxs"

New-WixFragment `
    -Files      $svFiles `
    -BaseDir    $svcPub `
    -RootDirId  "SERVICE_DIR" `
    -GroupId    "ServiceComponents" `
    -Prefix     "SV" `
    -Permanent  $true `
    -OutputPath "$root\DiskMonitor.Installer\ServiceFiles.wxs"

Write-Host "  OK" -ForegroundColor Green

# ── [4/6] Build MSI ──────────────────────────────────────────────────────────
Write-Host "[4/6] Building MSI..." -ForegroundColor Cyan

$frontendWxs = "$root\DiskMonitor.Installer\FrontendFiles.wxs"
$serviceWxs  = "$root\DiskMonitor.Installer\ServiceFiles.wxs"
$productWxs  = "$root\DiskMonitor.Installer\Product.wxs"
$licenseRtf  = "$root\installer\License.rtf"
$msiOut      = "$distDir\DiskMonitor-Setup.msi"

# Read version from Frontend EXE
$exeVerInfo = (Get-Item "$pubRoot\DiskMonitor.Frontend.exe").VersionInfo
$prodVer    = if ($exeVerInfo.ProductVersion -match '^\d+\.\d+\.\d+') {
    $Matches[0]
} elseif ($exeVerInfo.FileVersion -match '^\d+\.\d+\.\d+') {
    $Matches[0]
} else {
    "1.3.0"
}
Write-Host "  Version: $prodVer"

wix build $productWxs $frontendWxs $serviceWxs `
    -ext WixToolset.UI.wixext `
    -ext WixToolset.Util.wixext `
    -arch x64 `
    -d "ProductVersion=$prodVer" `
    -d "CADll=$caDll" `
    -d "LicenseRtf=$licenseRtf" `
    -o $msiOut 2>&1

if ($LASTEXITCODE -ne 0) { throw "wix build failed (exit $LASTEXITCODE)" }
if (-not (Test-Path $msiOut)) { throw "MSI not found after wix build" }
$msiMB = [Math]::Round((Get-Item $msiOut).Length / 1MB, 1)
Write-Host "  OK: DiskMonitor-Setup.msi ($msiMB MB)" -ForegroundColor Green

# ── [5/6] Build NSIS-edition publish ─────────────────────────────────────────
Write-Host "[5/6] Building NSIS-edition frontend (NsisEdition=true)..." -ForegroundColor Cyan

$nsisPub = Join-Path $root "publish\DiskMonitor-Nsis"
$nsisTmp = Join-Path $root "publish\DiskMonitor-Nsis-tmp"

if (Test-Path $nsisPub) { Remove-Item $nsisPub -Recurse -Force }
Copy-Item $pubRoot $nsisPub -Recurse

dotnet publish "$root\DiskMonitor.Frontend\DiskMonitor.Frontend.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:NsisEdition=true `
    -p:PublishReadyToRun=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $nsisTmp --nologo -v quiet 2>&1
if ($LASTEXITCODE -ne 0) { throw "NSIS-edition frontend publish failed" }

Get-ChildItem $nsisTmp -File | Copy-Item -Destination $nsisPub -Force
Get-ChildItem $nsisPub -File -Filter "*.xml" -ErrorAction SilentlyContinue | Remove-Item -Force
Remove-Item $nsisTmp -Recurse -Force
if (-not (Test-Path "$nsisPub\DiskMonitor.Frontend.exe")) { throw "NSIS-edition EXE missing" }
Write-Host "  OK" -ForegroundColor Green

# ── [6/6] Build NSIS EXE ─────────────────────────────────────────────────────
Write-Host "[6/6] Building NSIS EXE..." -ForegroundColor Cyan
if (-not (Test-Path $nsisExe)) {
    Write-Warning "NSIS not found at '$nsisExe' — skipping"
} else {
    # Run makensis from project root so NSIS relative paths work
    Push-Location $root
    try {
        & $nsisExe "installer\DiskMonitor.nsi" 2>&1
        if ($LASTEXITCODE -ne 0) { throw "makensis failed (exit $LASTEXITCODE)" }
    } finally { Pop-Location }

    $nsisOut = "$distDir\DiskMonitor-Setup.exe"
    if (-not (Test-Path $nsisOut)) { throw "NSIS EXE not found after makensis" }
    $exeMB = [Math]::Round((Get-Item $nsisOut).Length / 1MB, 1)
    Write-Host "  OK: DiskMonitor-Setup.exe ($exeMB MB)" -ForegroundColor Green
}

# ── Summary ───────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Build complete. Artifacts in dist\:" -ForegroundColor Green
Get-ChildItem $distDir -File -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object {
    $mb = [Math]::Round($_.Length / 1MB, 1)
    Write-Host ("  {0,-40} {1,6} MB" -f $_.Name, $mb)
}
