# Le prove sulle regole contro lo stack di produzione in Docker. Invocazione
# sempre identica:
#
#   & C:\precursori\tools\regole-docker.ps1
#
# Accende game server, web API, Postgres e MinIO da produzione\docker-compose.yml,
# come andrebbero su un server Linux, ci fa girare sopra le stesse prove di
# regole.ps1 e poi spegne e butta via tutto.
#
# Lo stack e un progetto Docker a parte (precursori-prove-docker), con i suoi
# volumi: non tocca i container di sviluppo ne il mondo vero, e a ogni giro
# riparte da un mondo vuoto. Password e segreto si generano qui a caso e
# finiscono in un file temporaneo che si cancella alla fine.
#
# Usa le stesse porte del server vero (27015, 5080): come regole.ps1, ferma il
# server vero se e acceso e non lo riaccende.

$ErrorActionPreference = "Continue"
$root = "C:\precursori"
$prod = "$root\tools\produzione"
$log  = "$root\tools\logs"
New-Item -ItemType Directory -Force -Path $log | Out-Null

# Stesso lucchetto di regole.ps1: run.ps1 non deve accendere il server vero
# mentre le sonde girano.
$lucchetto = "$root\tools\regole.lock"
$io = Get-Process -Id $PID
Set-Content -Path $lucchetto -Value @($PID, $io.StartTime.ToString("o")) -Encoding ascii

$progetto = "precursori-prove-docker"
$envFile  = Join-Path $env:TEMP "precursori-prove-docker.env"

function Hex([int]$byte) {
    $b = New-Object byte[] $byte
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
    ($b | ForEach-Object { $_.ToString("x2") }) -join ""
}

function Compose {
    & docker compose -p $progetto -f "$prod\docker-compose.yml" -f "$prod\docker-compose.prove.yml" `
        --env-file $envFile @args
}

function Esci([int]$codice) {
    Write-Output ""
    Write-Output "--- spengo e butto via lo stack di prova"
    Compose logs --no-color gameserver 2>&1 | Out-File "$log\regole-docker-server.log" -Encoding utf8
    Compose logs --no-color webapi     2>&1 | Out-File "$log\regole-docker-webapi.log" -Encoding utf8
    Compose down -v --remove-orphans 2>&1 | Out-Null
    Remove-Item $envFile -Force -ErrorAction SilentlyContinue
    Remove-Item $lucchetto -Force -ErrorAction SilentlyContinue
    Write-Output ""
    if ($codice -eq 0) { Write-Output "PROVE SUPERATE (stack in Docker)" } else { Write-Output "PROVE FALLITE (stack in Docker, codice $codice)" }
    Write-Output "Per tornare a giocare: & C:\precursori\tools\run.ps1"
    exit $codice
}

# Aspetta che il game server dica di essere in ascolto. Sul PC basta guardare
# la porta, qui no: Docker apre la porta appena parte il container, prima che
# il server dentro sia pronto.
function Aspetta-Server([int]$dopoRighe) {
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        $righe = @(Compose logs --no-color gameserver 2>&1 | Select-String "UDP started on port")
        if ($righe.Count -gt $dopoRighe) { return $true }
        Start-Sleep -Milliseconds 700
    }
    return $false
}

function Aspetta-WebApi {
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri "http://127.0.0.1:5080/health" -UseBasicParsing -TimeoutSec 2
            if ($r.StatusCode -eq 200) { return $true }
        } catch { }
        Start-Sleep -Milliseconds 700
    }
    return $false
}

# ---- prima di accendere ----------------------------------------------------
Write-Output "=== controllo: senza .env lo stack si rifiuta di partire"
$vuoto = Join-Path $env:TEMP "precursori-prove-docker-vuoto.env"
Set-Content -Path $vuoto -Value "" -Encoding ascii
$cfg = & docker compose -p $progetto -f "$prod\docker-compose.yml" --env-file $vuoto config 2>&1
$rifiutato = $LASTEXITCODE -ne 0
Remove-Item $vuoto -Force -ErrorAction SilentlyContinue
$esito = 0
if ($rifiutato) {
    $cfg | Select-String "manca" | Select-Object -First 1 | ForEach-Object { "  " + $_.Line.Trim() }
    Write-Output "  OK: senza valori non parte"
} else {
    Write-Output "  FALLITA: e partito senza password"
    $esito = 1
}

Set-Content -Path $envFile -Encoding ascii -Value @(
    "POSTGRES_PASSWORD=$(Hex 24)",
    "MINIO_USER=precursori",
    "MINIO_PASSWORD=$(Hex 24)",
    "AUTH_SECRET=$(Hex 32)",
    "ADMIN=Torak"
)

Write-Output "=== fermo il server vero, se e acceso"
Get-Process -Name "Precursori.GameServer","Precursori.WebApi" -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 900

# Un giro interrotto puo aver lasciato lo stack acceso.
Compose down -v --remove-orphans 2>&1 | Out-Null

Write-Output "=== build prove"
Push-Location "$root\tools\loadtest"
$buildOut = dotnet build -c Release -v minimal --nologo 2>&1
$buildOk  = $LASTEXITCODE -eq 0
Pop-Location
if (-not $buildOk) {
    $buildOut | Select-String "error" | Select-Object -First 4 | ForEach-Object { "  " + $_.Line }
    Write-Output "  PROVE NON COMPILATE: annullo."
    Esci 1
}

Write-Output "=== costruisco e accendo lo stack (puo volerci qualche minuto la prima volta)"
Compose up -d --build 2>&1 | Out-File "$log\regole-docker-up.log" -Encoding utf8
if ($LASTEXITCODE -ne 0) {
    Get-Content "$log\regole-docker-up.log" -Tail 8 | ForEach-Object { "  " + $_ }
    Write-Output "  LO STACK NON PARTE: annullo."
    Esci 1
}
if (-not (Aspetta-WebApi))   { Write-Output "  la web API non risponde su /health"; Esci 1 }
if (-not (Aspetta-Server 0)) { Write-Output "  il game server non si e messo in ascolto"; Esci 1 }
Write-Output "  web API e game server pronti"

# ---- il grosso delle prove -------------------------------------------------
Push-Location "$root\tools\loadtest"
& dotnet run -c Release --no-build -- regole
if ($LASTEXITCODE -ne 0) { $esito = 1 }
Pop-Location

# ---- persistenza attraverso un riavvio del container -----------------------
Write-Output ""
Write-Output "--- persistenza attraverso un riavvio del container del game server"
Push-Location "$root\tools\loadtest"
& dotnet run -c Release --no-build -- regole prepara
$preparato = $LASTEXITCODE
Pop-Location

if ($preparato -ne 0) {
    Write-Output "  FALLITA: preparazione non riuscita"
    $esito = 1
} else {
    # Come in regole.ps1: si aspetta almeno un salvataggio periodico.
    Start-Sleep -Seconds 9
    Compose restart gameserver 2>&1 | Out-Null
    if (-not (Aspetta-Server 1)) { Write-Output "  il game server non e ripartito"; Esci 1 }

    Push-Location "$root\tools\loadtest"
    & dotnet run -c Release --no-build -- regole verifica
    $verificato = $LASTEXITCODE
    Pop-Location

    if ($verificato -ne 0) { Write-Output "  FALLITA"; $esito = 1 }
    else { Write-Output "  OK: lo zaino, la posizione e l'orda hanno superato il riavvio del container" }
}

# ---- rifiuto di aprire col segreto di sviluppo -----------------------------
Write-Output ""
Write-Output "--- apertura al pubblico: col segreto di sviluppo il container non parte"
# Staccato e guardato dopo un po': se per errore partisse, un "run" in primo
# piano resterebbe appeso per sempre.
$nome = "$progetto-apertura"
Compose run -d --no-deps --name $nome -e "Auth__Secret=CAMBIAMI-segreto-di-sviluppo" gameserver 2>&1 | Out-Null
Start-Sleep -Seconds 15
$stato = (& docker inspect -f "{{.State.Running}} {{.State.ExitCode}}" $nome 2>&1) -join " "
& docker logs $nome 2>&1 | Select-String "ProductionChecks" | Select-Object -First 1 |
    ForEach-Object { "  " + $_.Line.Trim() }
& docker rm -f $nome 2>&1 | Out-Null
if ($stato -like "false *" -and $stato -notlike "false 0") {
    Write-Output "  OK: si e rifiutato (stato: $stato)"
} else {
    Write-Output "  FALLITA: stato del container '$stato'"
    $esito = 1
}

Esci $esito
