# Scarica le novita da tutti e cinque i repo. Invocazione sempre identica:
#
#   & C:\precursori\tools\pull.ps1
#
# Non fonde a forza e non butta via niente: se un repo ha modifiche locali che
# si scontrano con quelle remote, lo dice e passa oltre. Un pull che risolve da
# solo i conflitti e' un pull che ogni tanto perde lavoro.

$ErrorActionPreference = "Continue"
$root = "C:\precursori"

$repos = @(
    @{ Name = "shared"; Path = "$root\bootstrap\01_shared\src\Precursori.Shared" },
    @{ Name = "server"; Path = "$root\bootstrap\02_gameserver\src\Precursori.GameServer" },
    @{ Name = "client"; Path = "$root\client-unity\PersecutoriCLient" },
    @{ Name = "webapi"; Path = "$root\bootstrap\05_webapi\src\Precursori.WebApi" },
    @{ Name = "tools";  Path = "$root\tools" }
)

foreach ($r in $repos) {
    Write-Output "=== $($r.Name) ==="
    if (-not (Test-Path "$($r.Path)\.git")) { Write-Output "  non e un repo, salto"; continue }

    Push-Location $r.Path

    if ((git remote | Measure-Object).Count -eq 0) {
        Write-Output "  nessun remoto configurato"
        Pop-Location
        continue
    }

    # Il controller del giocatore viene riscritto a ogni BatchVerify con soli
    # fileID rimescolati: rumore, mai contenuto, e blocca il pull per niente.
    git checkout -- "Assets/Animators/Player/PlayerAnimator.controller" 2>$null

    $sporco = (git status --porcelain | Measure-Object).Count
    if ($sporco -gt 0) {
        Write-Output "  $sporco file modificati in locale: committa prima, non tocco niente"
        git status --short | Select-Object -First 5 | ForEach-Object { "    " + $_ }
        Pop-Location
        continue
    }

    $prima = git rev-parse --short HEAD
    git pull --ff-only 2>&1 | Select-Object -Last 2 | ForEach-Object { "  " + $_ }
    $dopo = git rev-parse --short HEAD

    if ($prima -eq $dopo) { Write-Output "  gia aggiornato ($dopo)" }
    else {
        Write-Output "  $prima -> $dopo"
        git log --oneline "$prima..$dopo" | Select-Object -First 8 | ForEach-Object { "    " + $_ }
    }
    Pop-Location
}
