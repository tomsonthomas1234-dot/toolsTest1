# Prove sulle regole. Invocazione sempre identica:
#
#   & C:\precursori\tools\regole.ps1
#
# Verifica che il server dica di no quando deve, e che ricordi quando deve. Il
# banco di carico misura le prestazioni; questo misura le regole, che e' una
# domanda diversa e piu' facile da dimenticare: un rifiuto mancato non rallenta
# niente, si nota mesi dopo.
#
# Gira su un mondo separato, azzerato a ogni giro.

$ErrorActionPreference = "Continue"
$root = "C:\precursori"
$log  = "$root\tools\logs"
New-Item -ItemType Directory -Force -Path $log | Out-Null

function Avvia-ServerDiProva([string]$logFile) {
    # Tetto basso ma non nullo: una prova lo riempie apposta, le altre devono
    # restare libere di costruire.
    $env:Server__MaxStructuresPerFaction = "3"
    # Le sonde sono amministratrici solo qui dentro, per poter preparare gli
    # scenari: piantare un muro, erigere uno stendardo, morire su richiesta.
    # Sono variabili d'ambiente di questo processo e non toccano la
    # configurazione vera.
    $env:Server__Admins__0 = "sonda0000"
    $env:Server__Admins__1 = "sonda0001"
    # sonda0002 resta fuori apposta: serve a verificare che senza il titolo i
    # comandi non funzionino. sonda0003 e' dedicata alla persistenza, cosi' il
    # suo zaino non dipende da cosa hanno fatto le altre prove.
    $env:Server__Admins__2 = "sonda0003"
    # Mondo separato. Le prove piantano muri e stendardi: senza questo finiscono
    # nel mondo vero, ed e' gia' successo — uno stendardo di prova aveva
    # rivendicato un settore nella partita vera.
    $env:Persistence__MinioBucket = "precursori-prove"

    Start-Process -FilePath "dotnet" -ArgumentList "run","--no-build" `
        -WorkingDirectory "$root\bootstrap\02_gameserver\src\Precursori.GameServer" `
        -RedirectStandardOutput $logFile `
        -RedirectStandardError  "$log\regole-server.err.log" -WindowStyle Hidden

    Remove-Item Env:\Server__MaxStructuresPerFaction
    Remove-Item Env:\Server__Admins__0
    Remove-Item Env:\Server__Admins__1
    Remove-Item Env:\Server__Admins__2
    Remove-Item Env:\Persistence__MinioBucket

    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline -and
           ((Get-NetUDPEndpoint -LocalPort 27015 -ErrorAction SilentlyContinue | Measure-Object).Count -eq 0)) {
        Start-Sleep -Milliseconds 500
    }
}

function Ferma-Tutto {
    Get-Process -Name "Precursori.GameServer","Precursori.WebApi" -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 1200
}

function Ferma-SoloServer {
    Get-Process -Name "Precursori.GameServer" -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 1200
}

# ---- compilazione ----------------------------------------------------------
Write-Output "=== build prove ==="
Push-Location "$root\tools\loadtest"
$buildOut = dotnet build -c Release -v minimal --nologo 2>&1
$buildOk  = $LASTEXITCODE -eq 0
Pop-Location
$buildOut | Select-String -Pattern "error|Errori:|Compilazione" | Select-Object -First 6 |
    ForEach-Object { "  " + $_.Line }
if (-not $buildOk) { Write-Output "  PROVE NON COMPILATE: annullo."; exit 1 }

Ferma-Tutto

# Mondo di prova azzerato a ogni giro.
#
# Senza, le prove ereditano le strutture di quelle precedenti e smettono di
# essere ripetibili: e' gia' successo che una fazione arrivasse al tetto non per
# quello che faceva la prova, ma per quante volte l'avevamo lanciata. Un esito
# che dipende dalla storia non e' un esito.
#
# La cancellazione tocca SOLO le righe etichettate come mondo di prova. Quelle
# del mondo vero non sono raggiungibili da questa condizione.
docker exec precursori-postgres psql -U postgres -d precursori `
    -c "DELETE FROM world_snapshots WHERE bucket='precursori-prove';" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Output "  attenzione: non ho potuto azzerare il mondo di prova" }

Start-Process -FilePath "dotnet" -ArgumentList "run","--no-build" `
    -WorkingDirectory "$root\bootstrap\05_webapi\src\Precursori.WebApi" `
    -RedirectStandardOutput "$log\regole-webapi.log" `
    -RedirectStandardError  "$log\regole-webapi.err.log" -WindowStyle Hidden

Avvia-ServerDiProva "$log\regole-server.log"

# ---- il grosso delle prove -------------------------------------------------
Push-Location "$root\tools\loadtest"
& dotnet run -c Release --no-build -- regole
$esito = $LASTEXITCODE
Pop-Location

Select-String -Path "$log\regole-server.log" -Pattern "\[LIMITI\]|\[ADMIN\] rifiutato" |
    Select-Object -Last 4 | ForEach-Object { "  log server: " + $_.Line }

# ---- persistenza attraverso un riavvio -------------------------------------
#
# Il rientro sullo stesso server prova che il giocatore viene messo da parte
# alla disconnessione. Questa prova e' un'altra cosa: che quel deposito arrivi
# fino al disco e torni indietro dopo che il processo e' morto. E' il percorso
# dove i difetti si sono gia' visti in partita, e l'unico modo di verificarlo e'
# spegnere davvero.
Write-Output ""
Write-Output "--- persistenza attraverso un riavvio del server"

Push-Location "$root\tools\loadtest"
& dotnet run -c Release --no-build -- regole prepara
$preparato = $LASTEXITCODE
Pop-Location

if ($preparato -ne 0) {
    Write-Output "  FALLITA: preparazione non riuscita"
    $esito = 1
} else {
    # Il giocatore viene messo da parte alla disconnessione, ma finisce su disco
    # col salvataggio periodico del mondo: si aspetta che ne passi almeno uno.
    Start-Sleep -Seconds 9
    Ferma-SoloServer
    Avvia-ServerDiProva "$log\regole-server2.log"

    Push-Location "$root\tools\loadtest"
    & dotnet run -c Release --no-build -- regole verifica
    $verificato = $LASTEXITCODE
    Pop-Location

    if ($verificato -ne 0) { Write-Output "  FALLITA"; $esito = 1 }
    else { Write-Output "  OK: lo zaino e la posizione hanno superato il riavvio" }
}

Ferma-Tutto

# ---- prova a parte: rifiuto di aprire con valori da sviluppo ----------------
#
# Non si puo' verificare da dentro il gioco: la prova e' che il gioco NON parta.
Write-Output ""
Write-Output "--- apertura al pubblico: rifiuta di partire con i valori di sviluppo"

$apLog = "$log\regole-apertura.log"
Remove-Item $apLog -ErrorAction SilentlyContinue
$env:Server__ProductionChecks = "true"
$p = Start-Process -FilePath "dotnet" -ArgumentList "run","--no-build" `
    -WorkingDirectory "$root\bootstrap\02_gameserver\src\Precursori.GameServer" `
    -RedirectStandardOutput $apLog -RedirectStandardError "$log\regole-apertura.err.log" `
    -WindowStyle Hidden -PassThru
Remove-Item Env:\Server__ProductionChecks

Start-Sleep -Seconds 12
$inAscolto = (Get-NetUDPEndpoint -LocalPort 27015 -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

Select-String -Path "$log\regole-apertura.err.log","$apLog" -Pattern "APERTURA|ProductionChecks" -ErrorAction SilentlyContinue |
    Select-Object -First 3 | ForEach-Object { "  " + $_.Line.Trim() }

if ($inAscolto) {
    Write-Output "  FALLITA: il server e' partito lo stesso, con il segreto di sviluppo"
    $esito = 1
} else {
    Write-Output "  OK: non si e' aperto"
}

Write-Output ""
if ($esito -eq 0) { Write-Output "PROVE SUPERATE" } else { Write-Output "PROVE FALLITE (codice $esito)" }
Write-Output "Per tornare a giocare: & C:\precursori\tools\run.ps1"
exit $esito
