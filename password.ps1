# Imposta la password di un account. Invocazione:
#
#   & C:\precursori\tools\password.ps1            (Torak)
#   & C:\precursori\tools\password.ps1 AltroNome
#
# Va lanciato DA TE in un terminale, perche' la password si digita e basta: non
# passa da argomenti (finirebbe nella cronologia della shell), non compare a
# schermo, non finisce in nessun log. Resta nella memoria del processo il tempo
# di calcolarne l'hash PBKDF2, e nel database c'e' solo quello.
#
# L'hash lo calcola la stessa Passwords che usa il gioco, non una copia.

param([string]$Nome = "Torak")

$ErrorActionPreference = "Continue"
$root = "C:\precursori"

Push-Location "$root\tools\password"
$buildOut = dotnet build -c Release -v minimal --nologo 2>&1
$buildOk  = $LASTEXITCODE -eq 0
Pop-Location
if (-not $buildOk) {
    $buildOut | Select-String -Pattern "error" | Select-Object -First 4 | ForEach-Object { "  " + $_.Line }
    Write-Output "Strumento non compilato: annullo."
    exit 1
}

& "$root\tools\password\bin\Release\net8.0\Password.exe" $Nome
