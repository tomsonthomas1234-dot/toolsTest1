# Compila tutto. Invocazione sempre identica, cosi il permesso si puo dare
# una volta sola invece che a ogni variante del comando.
#
#   & C:\precursori\tools\build.ps1
#
# Ferma il game server se sta girando (tiene i DLL occupati), compila shared,
# game server e web API, e se Unity e chiuso lancia anche BatchVerify sul client.

$ErrorActionPreference = "Continue"
$root = "C:\precursori"
$log  = "$root\tools\logs"
New-Item -ItemType Directory -Force -Path $log | Out-Null

Write-Output "=== server in esecuzione: fermo ==="
Get-Process -Name "Precursori.GameServer","Precursori.WebApi" -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Output "  stop PID $($_.Id) $($_.ProcessName)"; Stop-Process -Id $_.Id -Force }
Start-Sleep -Milliseconds 900

foreach ($p in @(
    @{ Name = "GameServer"; Path = "$root\bootstrap\02_gameserver\src\Precursori.GameServer" },
    @{ Name = "WebApi";     Path = "$root\bootstrap\05_webapi\src\Precursori.WebApi" }
)) {
    Write-Output "=== build $($p.Name) ==="
    Push-Location $p.Path
    dotnet build -v minimal --nologo 2>&1 |
        Select-String -Pattern "error|Errori:|Compilazione" | Select-Object -First 6 |
        ForEach-Object { "  " + $_.Line }
    Pop-Location
}

$unity = (Get-Process Unity -ErrorAction SilentlyContinue | Measure-Object).Count
if ($unity -gt 0) {
    Write-Output "=== client: Unity aperto ($unity), compila lui al focus ==="
} else {
    Write-Output "=== client: BatchVerify ==="
    $ulog = "$log\client.log"
    & "C:\Program Files\Unity\Hub\Editor\6000.3.2f1\Editor\Unity.exe" -batchmode -quit -nographics `
        -silent-crashes -projectPath "$root\client-unity\PersecutoriCLient" `
        -executeMethod BatchVerify.All -logFile $ulog
    Get-Process Unity -ErrorAction SilentlyContinue | Wait-Process -Timeout 900 -ErrorAction SilentlyContinue
    $errs = (Select-String -Path $ulog -Pattern "error CS" | Measure-Object).Count
    Write-Output "  errori CS: $errs"
    Select-String -Path $ulog -Pattern "error CS|PROBLEMA:|esito:" |
        ForEach-Object { "  " + $_.Line } | Select-Object -First 8
}
