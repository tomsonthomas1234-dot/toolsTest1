# Committa e pusha i tre repo. Invocazione sempre identica:
#
#   & C:\precursori\tools\ship.ps1
#
# Il messaggio di commit non sta nel comando ma in un file per repo, scritto
# prima di chiamare lo script. Cosi la riga di comando non cambia mai e il
# permesso si da una volta sola.
#
#   C:\precursori\tools\msg\shared.txt
#   C:\precursori\tools\msg\server.txt
#   C:\precursori\tools\msg\client.txt
#
# Un repo senza file messaggio viene saltato anche se ha modifiche: serve a
# committare solo cio che si intende committare. Il file viene rimosso dopo un
# commit riuscito, cosi non si riusa un messaggio vecchio per sbaglio.

$ErrorActionPreference = "Continue"
$root = "C:\precursori"
$msgDir = "$root\tools\msg"
New-Item -ItemType Directory -Force -Path $msgDir | Out-Null

$repos = @(
    @{ Name = "shared"; Path = "$root\bootstrap\01_shared\src\Precursori.Shared" },
    @{ Name = "server"; Path = "$root\bootstrap\02_gameserver\src\Precursori.GameServer" },
    @{ Name = "client"; Path = "$root\client-unity\PersecutoriCLient" }
)

foreach ($r in $repos) {
    $msg = "$msgDir\$($r.Name).txt"
    if (-not (Test-Path $msg)) { continue }

    Push-Location $r.Path

    # Il controller del giocatore viene riscritto a ogni BatchVerify con soli
    # fileID rimescolati: rumore, mai contenuto.
    git checkout -- "Assets/Animators/Player/PlayerAnimator.controller" 2>$null

    $dirty = (git status --porcelain | Where-Object { $_ -notmatch "\b(bin|obj)/" } | Measure-Object).Count
    if ($dirty -eq 0) {
        Write-Output "=== $($r.Name): niente da committare ==="
        Pop-Location
        continue
    }

    Write-Output "=== $($r.Name): $dirty file ==="
    git add -A
    git commit -q -F $msg
    if ($LASTEXITCODE -eq 0) {
        git push origin main 2>&1 | Select-Object -Last 1 | ForEach-Object { "  " + $_ }
        git log --oneline -1 | ForEach-Object { "  " + $_ }
        Remove-Item $msg -Force
    } else {
        Write-Output "  commit fallito, messaggio conservato"
    }
    Pop-Location
}
