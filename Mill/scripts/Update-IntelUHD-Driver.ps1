# Intel UHD / Iris Xe — 11～14 代核显通用驱动 32.0.101.7092
# 右键 PowerShell「以管理员身份运行」后执行本脚本

$ErrorActionPreference = 'Stop'
$url = 'https://downloadmirror.intel.com/929468/gfx_win_101.7092.exe'
$dest = Join-Path $env:TEMP 'gfx_win_101.7092.exe'

Write-Host "下载 Intel 显卡驱动 (约 745 MB) ..." -ForegroundColor Cyan
if (-not (Test-Path $dest) -or (Get-Item $dest).Length -lt 700MB) {
    Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
}

Write-Host "开始安装（需 UAC 管理员确认，安装后请重启）..." -ForegroundColor Cyan
Start-Process -FilePath $dest -ArgumentList '--overwrite', '-s' -Verb RunAs -Wait
Write-Host "完成。请重启电脑。" -ForegroundColor Green
