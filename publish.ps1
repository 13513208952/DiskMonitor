# DiskMonitor 便携包发布脚本
# 输出目录：publish\DiskMonitor\
# 使用方式：在项目根目录以管理员 PowerShell 运行 .\publish.ps1

$root = $PSScriptRoot
$out  = Join-Path $root "publish\DiskMonitor"
$svc  = Join-Path $out  "service"

Write-Host "=== DiskMonitor 便携包发布 ===" -ForegroundColor Cyan
Write-Host "输出目录: $out"

# 清理上次输出
if (Test-Path $out) {
    Write-Host "清理旧输出..." -ForegroundColor Yellow
    Remove-Item $out -Recurse -Force
}

# 发布前端（WPF，net9.0-windows，win-x64 self-contained）
Write-Host "`n[1/2] 发布前端..." -ForegroundColor Green
dotnet publish "$root\DiskMonitor.Frontend\DiskMonitor.Frontend.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $out

if ($LASTEXITCODE -ne 0) { Write-Host "前端发布失败！" -ForegroundColor Red; exit 1 }

# 发布服务（Worker，net9.0，win-x64 self-contained）到 service\ 子目录
Write-Host "`n[2/2] 发布服务..." -ForegroundColor Green
dotnet publish "$root\DiskMonitor.Service\DiskMonitor.Service.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $svc

if ($LASTEXITCODE -ne 0) { Write-Host "服务发布失败！" -ForegroundColor Red; exit 1 }

# 清理不需要的文件
Write-Host "`n清理冗余文件..." -ForegroundColor Yellow
Get-ChildItem $out  -File -Filter "*.xml"  -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $svc  -File -Filter "*.xml"  -ErrorAction SilentlyContinue | Remove-Item -Force
# appsettings.json 对 Worker 服务有用，保留

# 统计大小
$sizeMB = [math]::Round((Get-ChildItem $out -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)

Write-Host "`n=== 发布完成 ===" -ForegroundColor Cyan
Write-Host "便携包位置 : $out"
Write-Host "总大小     : ${sizeMB} MB"
Write-Host "运行前端   : $out\DiskMonitor.Frontend.exe"
Write-Host "服务二进制 : $svc\DiskMonitor.Service.exe"
