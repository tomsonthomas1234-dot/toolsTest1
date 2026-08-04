# Guarda dentro il mondo vero. Invocazione sempre identica:
#
#   & C:\precursori\tools\ispeziona.ps1
#
# Elenca le strutture costruite nel mondo di gioco. Serve a rispondere alla
# domanda piu' ovvia di un amministratore — chi ha costruito cosa e dove — e a
# scoprire le tracce lasciate dalle prove prima che avessero un mondo separato.
#
# NON modifica niente: elenca soltanto. Per togliere qualcosa serve il comando
# 'remove <id>', che va dato apposta e sapendo cosa si toglie.

$ErrorActionPreference = "Continue"
$root = "C:\precursori"
$log  = "$root\tools\logs"

Push-Location "$root\tools\loadtest"
$buildOut = dotnet build -c Release -v minimal --nologo 2>&1
$buildOk  = $LASTEXITCODE -eq 0
Pop-Location
if (-not $buildOk) {
    $buildOut | Select-String -Pattern "error" | Select-Object -First 4 | ForEach-Object { "  " + $_.Line }
    Write-Output "SONDA NON COMPILATA: annullo."
    exit 1
}

# Il server viene riavviato con la sonda fra gli amministratori. Possibile solo
# da quando le variabili d'ambiente hanno la precedenza sul file: prima il file
# vinceva e l'elenco amministratori non era sovrascrivibile.
Get-Process -Name "Precursori.GameServer","Precursori.WebApi" -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.Id -Force }
Start-Sleep -Milliseconds 1200

Start-Process -FilePath "dotnet" -ArgumentList "run","--no-build" `
    -WorkingDirectory "$root\bootstrap\05_webapi\src\Precursori.WebApi" `
    -RedirectStandardOutput "$log\webapi.log" -RedirectStandardError "$log\webapi.err.log" `
    -WindowStyle Hidden

$env:Server__Admins__0 = "Torak"
$env:Server__Admins__1 = "sonda0000"
Start-Process -FilePath "dotnet" -ArgumentList "run","--no-build" `
    -WorkingDirectory "$root\bootstrap\02_gameserver\src\Precursori.GameServer" `
    -RedirectStandardOutput "$log\server.log" -RedirectStandardError "$log\server.err.log" `
    -WindowStyle Hidden
Remove-Item Env:\Server__Admins__0
Remove-Item Env:\Server__Admins__1

$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline -and
       ((Get-NetUDPEndpoint -LocalPort 27015 -ErrorAction SilentlyContinue | Measure-Object).Count -eq 0)) {
    Start-Sleep -Milliseconds 500
}

Push-Location "$root\tools\loadtest"
& dotnet run -c Release --no-build -- admin "structures" "sectors"
Pop-Location

Write-Output ""
Write-Output "=== strutture nel mondo ==="
Select-String -Path "$log\server.log" -Pattern "\[ADMIN\] struttura|nessuna struttura" |
    ForEach-Object { "  " + ($_.Line -replace '.*\[ADMIN\] ', '') }

Write-Output ""
Write-Output "=== settori ==="
Select-String -Path "$log\server.log" -Pattern "\[ADMIN\] settore|nessun settore" |
    ForEach-Object { "  " + ($_.Line -replace '.*\[ADMIN\] ', '') }
