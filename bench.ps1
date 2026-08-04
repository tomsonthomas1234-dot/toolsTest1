# Banco di prova. Invocazione sempre identica:
#
#   & C:\precursori\tools\bench.ps1
#
# Misura lo stesso carico due volte, a snapshot interi e a differenziali, e
# mette i numeri uno accanto all'altro. Farlo in un colpo solo e' il punto: due
# misure prese in momenti diversi, con la macchina in stati diversi, non si
# possono confrontare, e finora e' stato proprio questo a rendere incerta la
# decisione sui differenziali.

$ErrorActionPreference = "Continue"
$root    = "C:\precursori"
$log     = "$root\tools\logs"
$clients = 200
$seconds = 120
New-Item -ItemType Directory -Force -Path $log | Out-Null

Write-Output "=== build banco di prova ==="
Push-Location "$root\tools\loadtest"
$buildOut = dotnet build -c Release -v minimal --nologo 2>&1
$buildOk  = $LASTEXITCODE -eq 0
Pop-Location
$buildOut | Select-String -Pattern "error|Errori:|Compilazione" | Select-Object -First 6 |
    ForEach-Object { "  " + $_.Line }

# Se la compilazione fallisce si esce, invece di misurare il binario vecchio.
# E' gia successo: il banco ha girato col codice precedente e ha prodotto numeri
# che sembravano validi. Misurare la cosa sbagliata senza saperlo e' peggio che
# non misurare.
if (-not $buildOk) {
    Write-Output ""
    Write-Output "BANCO NON COMPILATO: misura annullata. Nessun numero e' meglio di un numero falso."
    exit 1
}

$esiti = @{}

foreach ($delta in @($false, $true)) {
    $etichetta = if ($delta) { "differenziali" } else { "snapshot interi" }
    Write-Output ""
    Write-Output "===== $etichetta : $clients client per ${seconds}s ====="

    Get-Process -Name "Precursori.GameServer","Precursori.WebApi" -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.Id -Force }
    Start-Sleep -Milliseconds 1200

    $srvLog = "$log\bench-server-$($delta.ToString().ToLower()).log"
    Remove-Item $srvLog -ErrorAction SilentlyContinue

    Start-Process -FilePath "dotnet" -ArgumentList "run","--no-build" `
        -WorkingDirectory "$root\bootstrap\05_webapi\src\Precursori.WebApi" `
        -RedirectStandardOutput "$log\bench-webapi.log" `
        -RedirectStandardError  "$log\bench-webapi.err.log" -WindowStyle Hidden

    $env:Server__DeltaSnapshots = $delta.ToString().ToLower()
    Start-Process -FilePath "dotnet" -ArgumentList "run","--no-build" `
        -WorkingDirectory "$root\bootstrap\02_gameserver\src\Precursori.GameServer" `
        -RedirectStandardOutput $srvLog `
        -RedirectStandardError  "$log\bench-server.err.log" -WindowStyle Hidden
    Remove-Item Env:\Server__DeltaSnapshots

    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline -and
           ((Get-NetUDPEndpoint -LocalPort 27015 -ErrorAction SilentlyContinue | Measure-Object).Count -eq 0)) {
        Start-Sleep -Milliseconds 500
    }

    Push-Location "$root\tools\loadtest"
    $out = & dotnet run -c Release --no-build -- $clients $seconds 2>&1
    Pop-Location

    $banda = ($out | Select-String "traffico in ingresso").Line
    $snap  = ($out | Select-String "snapshot interi|differenziali +:|verifica ricostruzione").Line
    $conn  = ($out | Select-String "client collegati").Line

    # Solo le metriche prese a carico avviato: le prime valgono poco, i bot
    # stanno ancora entrando e sparpagliandosi.
    $metriche = Select-String -Path $srvLog -Pattern "\[METRICHE\]" |
                ForEach-Object { $_.Line } | Select-Object -Skip 3

    $esiti[$etichetta] = [PSCustomObject]@{
        Collegati = $conn; Snapshot = @($snap); Banda = $banda
        Metriche  = $metriche | Select-Object -Last 6
    }

    Write-Output "  $conn"
    $snap | ForEach-Object { "  " + $_ }
    Write-Output "  $banda"
    $metriche | Select-Object -Last 6 | ForEach-Object { "  " + $_ }
}

Write-Output ""
Write-Output "===== CONFRONTO ====="
foreach ($k in @("snapshot interi", "differenziali")) {
    Write-Output "--- $k"
    Write-Output "  $($esiti[$k].Banda)"
    $esiti[$k].Snapshot | ForEach-Object { "  " + $_ }
    $esiti[$k].Metriche | ForEach-Object { "  " + $_ }
}

Get-Process -Name "Precursori.GameServer","Precursori.WebApi" -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.Id -Force }
Write-Output ""
Write-Output "processi di prova fermati. Per tornare a giocare: & C:\precursori\tools\run.ps1"
