# AnyCAM Mill 运行环境：VC++ 2015-2022 x64 + Intel 显卡驱动助手
# 请在 PowerShell 中右键「使用管理员身份运行」

$ErrorActionPreference = 'Stop'

Write-Host "=== 1/2 Microsoft VC++ 2015-2022 x64 ===" -ForegroundColor Cyan
$vcUrl = 'https://aka.ms/vs/17/release/vc_redist.x64.exe'
$vcExe = Join-Path $env:TEMP 'VC_redist.x64.exe'
if (-not (Test-Path $vcExe)) {
    Write-Host "下载 $vcUrl ..."
    Invoke-WebRequest -Uri $vcUrl -OutFile $vcExe -UseBasicParsing
}
Write-Host "安装 VC++（静默）..."
Start-Process -FilePath $vcExe -ArgumentList '/install', '/quiet', '/norestart' -Wait
Write-Host "VC++ 安装完成。" -ForegroundColor Green

Write-Host ""
Write-Host "=== 2/2 Intel 显卡驱动（Driver & Support Assistant）===" -ForegroundColor Cyan
if (Get-Command winget -ErrorAction SilentlyContinue) {
    winget install --id Intel.IntelDriverAndSupportAssistant `
        --accept-package-agreements --accept-source-agreements
    Write-Host "安装完成后，从开始菜单打开「Intel Driver & Support Assistant」扫描并安装推荐的显卡驱动。" -ForegroundColor Yellow
} else {
    Write-Host "未找到 winget。请手动打开：" -ForegroundColor Yellow
    Write-Host "https://www.intel.com/content/www/us/en/support/detect.html"
}

Write-Host ""
Write-Host "=== 若仍报 D3DImageHost 错误 ===" -ForegroundColor Cyan
Write-Host "设备管理器中如有「OrayIddDriver Device」（向日葵虚拟显示器），请禁用后再试 AnyCAM。"
Write-Host "完成 VC++/驱动 后请重启电脑，再运行 AnyCAM.Milling.exe。"
