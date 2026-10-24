# Kill stuck AnyCAM.Milling process(es).
# Usage: powershell -ExecutionPolicy Bypass -File .\kill.ps1
#        or right-click -> "Run with PowerShell"

$names = @("AnyCAM.Milling", "AnyCAM.Mill")
$found = $false

foreach ($name in $names) {
    $procs = Get-Process -Name $name -ErrorAction SilentlyContinue
    foreach ($p in $procs) {
        $found = $true
        Write-Host "Found $($p.ProcessName) (PID $($p.Id)), trying graceful close..." -ForegroundColor Yellow
        $p.CloseMainWindow() | Out-Null
        if (-not $p.WaitForExit(2000)) {
            Write-Host "Not responding, force killing..." -ForegroundColor Yellow
            Stop-Process -Id $p.Id -Force
        }
        Write-Host "Killed PID $($p.Id)" -ForegroundColor Green
    }
}

if (-not $found) {
    Write-Host "No running AnyCAM process found." -ForegroundColor Green
}
