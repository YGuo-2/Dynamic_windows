# 一键构建 DynamicIsland 发行版(自包含 publish + WiX MSI)。
# 用法:pwsh -File scripts/build-release.ps1   (或在 PowerShell 里直接跑)
# 产物:dist/DynamicIsland-<版本>-win-x64.msi
$ErrorActionPreference = 'Stop'

$Version = '1.0.0'
$Rid     = 'win-x64'
$Root    = Split-Path -Parent $PSScriptRoot
$Proj    = Join-Path $Root 'src/DynamicIsland.App/DynamicIsland.App.csproj'
$Wxs     = Join-Path $Root 'setup/DynamicIsland.wxs'
$Dist    = Join-Path $Root 'dist'
$PubDir  = Join-Path $Dist 'publish'
$Msi     = Join-Path $Dist "DynamicIsland-$Version-$Rid.msi"

# 0) App 运行时锁住 exe —— 先杀进程
taskkill /F /IM DynamicIsland.App.exe 2>$null | Out-Null
Start-Sleep -Milliseconds 800

# 1) 清理并自包含发布(内嵌 .NET 运行时,对方免装;多文件,无 pdb)
Write-Host '==> 清理 dist/' -ForegroundColor Cyan
if (Test-Path $PubDir) { Remove-Item -Recurse -Force $PubDir }
if (Test-Path $Msi)    { Remove-Item -Force $Msi }

Write-Host '==> dotnet publish (self-contained)' -ForegroundColor Cyan
dotnet publish $Proj `
  -c Release -r $Rid --self-contained true `
  -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false `
  -o $PubDir -v minimal -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败 (exit $LASTEXITCODE)" }

# 2) WiX 打 MSI(wix 装在 ~/.dotnet/tools)
$env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
Write-Host '==> wix build' -ForegroundColor Cyan
wix build $Wxs -b "pub=$PubDir" -arch x64 -o $Msi
if ($LASTEXITCODE -ne 0) { throw "wix build 失败 (exit $LASTEXITCODE)" }

# 3) 汇报
$sizeMB = [math]::Round((Get-Item $Msi).Length / 1MB, 1)
Write-Host ''
Write-Host "✓ 发行版已生成:" -ForegroundColor Green
Write-Host "    $Msi  ($sizeMB MB)" -ForegroundColor Green
