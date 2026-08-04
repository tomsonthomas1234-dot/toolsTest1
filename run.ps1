# Avvia web API e game server. Invocazione sempre identica:
#
#   & C:\precursori\tools\run.ps1
#
# Ferma prima le istanze gia attive: piu copie sulla stessa porta hanno gia
# prodotto un bind fallito in silenzio e mezz'ora persa a capire perche il
# server sembrava girare sul codice vecchio.

$ErrorActionPreference = "Continue"
$root = "C:\precursori"
$log  = "$root\tools\logs"
New-Item -ItemType Directory -Force -Path $log | Out-Null

Get-Process -Name "Precursori.GameServer","Precursori.WebApi" -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Output "stop PID $($_.Id) $($_.ProcessName)"; Stop-Process -Id $_.Id -Force }
Start-Sleep -Milliseconds 900

Start-Process -FilePath "dotnet" -ArgumentList "run","--no-build" `
    -WorkingDirectory "$root\bootstrap\05_webapi\src\Precursori.WebApi" `
    -RedirectStandardOutput "$log\webapi.log" -RedirectStandardError "$log\webapi.err.log" `
    -WindowStyle Hidden

Start-Process -FilePath "dotnet" -ArgumentList "run","--no-build" `
    -WorkingDirectory "$root\bootstrap\02_gameserver\src\Precursori.GameServer" `
    -RedirectStandardOutput "$log\server.log" -RedirectStandardError "$log\server.err.log" `
    -WindowStyle Hidden

$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline -and
       ((Get-NetUDPEndpoint -LocalPort 27015 -ErrorAction SilentlyContinue | Measure-Object).Count -eq 0)) {
    Start-Sleep -Milliseconds 500
}

Write-Output "game server in ascolto: $((Get-NetUDPEndpoint -LocalPort 27015 -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0)"
try { Write-Output "web api: $((Invoke-RestMethod 'http://127.0.0.1:5080/health' -TimeoutSec 3).status)" }
catch { Write-Output "web api: non risponde" }

Get-Content "$log\server.log" -ErrorAction SilentlyContinue |
    Where-Object { $_ -notmatch "^\s+at |^\s+--->" } | Select-Object -Last 6
