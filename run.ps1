# Avvia web API e game server. Invocazione sempre identica:
#
#   & C:\precursori\tools\run.ps1
#
# Ferma prima le istanze gia attive: piu copie sulla stessa porta hanno gia
# prodotto un bind fallito in silenzio e mezz'ora persa a capire perche il
# server sembrava girare sul codice vecchio.
#
# Ma solo quelle vere. Il 4 novembre un run.ps1 lanciato a meta di regole.ps1
# ha fermato per nome anche il server delle prove, e le sonde sono finite sul
# mondo vero. Per questo:
#   - se regole.ps1 o bench.ps1 stanno girando (lo dicono regole.lock e
#     bench.lock) non si fa niente: usano le stesse porte, e il server vero
#     ripartirebbe sotto i loro piedi;
#   - si fermano solo i processi senza il segno --Prove nella riga di comando,
#     che le prove mettono ai loro.

$ErrorActionPreference = "Continue"
$root = "C:\precursori"
$log  = "$root\tools\logs"
New-Item -ItemType Directory -Force -Path $log | Out-Null

# ---- prove in corso? ---------------------------------------------------------
#
# Il lucchetto contiene il PID dello script che lo ha scritto e l'ora in cui
# quel processo e' nato. Si guarda anche l'ora perche' un PID morto viene
# riusato da Windows: un lucchetto rimasto da un giro interrotto non deve
# bloccare per sempre.
foreach ($nome in @("regole", "bench")) {
    $lock = "$root\tools\$nome.lock"
    if (-not (Test-Path $lock)) { continue }
    $righe = @(Get-Content $lock -ErrorAction SilentlyContinue)
    $vivo  = $false
    if ($righe.Count -ge 2) {
        $p = Get-Process -Id ([int]$righe[0]) -ErrorAction SilentlyContinue
        $vivo = $p -and $p.StartTime.ToString("o") -eq $righe[1]
    }
    if ($vivo) {
        Write-Output "$nome.ps1 sta girando (PID $($righe[0])): non avvio niente."
        Write-Output "Rilancia run.ps1 quando ha finito."
        exit 0
    }
    Write-Output "$nome.lock rimasto da un giro interrotto: lo tolgo."
    Remove-Item $lock -Force -ErrorAction SilentlyContinue
}

Get-CimInstance Win32_Process -Filter "Name='Precursori.GameServer.exe' or Name='Precursori.WebApi.exe'" |
    Where-Object { $_.CommandLine -notmatch "--Prove" } |
    ForEach-Object {
        Write-Output "stop PID $($_.ProcessId) $($_.Name)"
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }
Start-Sleep -Milliseconds 900

# Con Start-Process, come sempre. Il server vero lo accende Tommaso per
# giocare; gli agenti non lo accendono e non lo lasciano acceso (decisione
# del 4 novembre, notte): un processo avviato da un agente muore con lui, ed
# e' giusto cosi'. Vedi tools/README.md.
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
