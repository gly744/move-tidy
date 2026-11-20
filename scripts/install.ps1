# 建置並安裝「移動整理」到本機，然後安裝（更新）右鍵選單。
# 用法：在項目資料夾執行  powershell -ExecutionPolicy Bypass -File scripts\install.ps1
# 設定檔在 %APPDATA%\MoveTidy\settings.json，重新安裝不會動到它。
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA "Programs\MoveTidy"

Get-Process MoveTidy -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dest*" } | Stop-Process -Force

dotnet publish (Join-Path $root "src\MoveTidy\MoveTidy.csproj") -c Release -o $dest --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "建置失敗" }

$exe = Join-Path $dest "MoveTidy.exe"
& $exe --install-menu | Out-Null
Write-Host "已安裝到 $exe"
Write-Host "右鍵選單已更新。打開設定：$exe"
