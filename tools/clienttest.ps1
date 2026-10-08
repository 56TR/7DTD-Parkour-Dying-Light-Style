# Build -> isolated game client (shadow install: junctions to the real game, own Mods with only Harmony + this mod)
# -> new Navezgane world starts by itself -> in-game self test -> results file -> game quits.
# Usage: powershell -File tools\clienttest.ps1 [-Filter all|slide,wallrun,...] [-FullPack] [-KeepSave] [-TimeoutMin 20]
#   -FullPack : run the real install with the whole modpack (compat check) instead of the clean shadow install
param(
    [string]$Filter = 'all',
    [switch]$FullPack,
    [switch]$KeepSave,
    [switch]$NoPatch,   # control run: mod loads but patches nothing except the autotest helpers
    [string]$Language = '',   # e.g. polish: start the game in that language (-language=...)
    [string]$ConnectTo = '',   # ip:port -> join that server as a client instead of hosting a world
    [int]$TimeoutMin = 20
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$test = Join-Path $root '_test'
$game = 'V:\SteamLibrary\steamapps\common\7 Days To Die'
$env:DOTNET_CLI_HOME = 'X:\Tools\dotnet\scratch\_home'; $env:DOTNET_NOLOGO = '1'

if (-not $ConnectTo -and (Get-Process -Name 7DaysToDie -ErrorAction SilentlyContinue)) { throw '7DaysToDie.exe is already running (close the game / server first)' }

& 'X:\Tools\dotnet\dotnet.exe' build "$root\ParkourMovement.csproj" -c Release 2>&1 | Select-String 'error|Build succeeded' | ForEach-Object { $_.Line }
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
python -I "$root\tools\gen_config.py" "$root"   # Config/parkour.xml always mirrors the Tuning.cs defaults

function Install-Mod($modsDir) {
    $dst = Join-Path $modsDir 'ParkourMovement'
    New-Item -ItemType Directory -Force "$dst\Config" | Out-Null
    Copy-Item "$root\bin\ParkourMovement.dll", "$root\ModInfo.xml" $dst -Force
    Copy-Item "$root\Config\parkour.xml" "$dst\Config" -Force
    Copy-Item "$root\Config\Localization.csv" "$dst\Config" -Force
    Copy-Item "$root\Config\XUi_Menu" "$dst\Config" -Recurse -Force
    New-Item -ItemType Directory -Force "$dst\Anims" | Out-Null
    Copy-Item "$root\Anims\*.json" "$dst\Anims" -Force
}

$userdata = Join-Path $test ($(if ($FullPack) { 'userdata_pack' } else { 'userdata' }))
New-Item -ItemType Directory -Force "$userdata\Mods" | Out-Null

if ($FullPack) {
    $exeDir = $game
    Install-Mod "$userdata\Mods"
} else {
    $exeDir = Join-Path $test 'game'
    if (-not (Test-Path "$exeDir\7DaysToDie.exe")) {
        New-Item -ItemType Directory -Force $exeDir | Out-Null
        Get-ChildItem $game -File | Where-Object { $_.Extension -in '.exe', '.dll', '.txt', '.cfg', '.config' -and $_.Name -notlike '*.log' } |
            ForEach-Object { Copy-Item $_.FullName $exeDir -Force }
        foreach ($d in '7DaysToDie_Data', 'Data', 'MonoBleedingEdge', 'EasyAntiCheat', 'Launcher', 'Licenses', 'Logos') {
            if (Test-Path "$game\$d") { New-Item -ItemType Junction -Path "$exeDir\$d" -Target "$game\$d" | Out-Null }
        }
        New-Item -ItemType Directory -Force "$exeDir\Mods" | Out-Null
        New-Item -ItemType Junction -Path "$exeDir\Mods\0_TFP_Harmony" -Target "$game\Mods\0_TFP_Harmony" | Out-Null
        'shadow install created'
    }
    Install-Mod "$exeDir\Mods"
}

# a fresh UserDataFolder has no SDCS character profile -> invisible body in 3rd person; reuse the player's own
$sdcsProfile = "$env:APPDATA\7DaysToDie\Saves\sdcs_profiles.sdf"
if ((Test-Path $sdcsProfile) -and -not (Test-Path "$userdata\Saves\sdcs_profiles.sdf")) {
    New-Item -ItemType Directory -Force "$userdata\Saves" | Out-Null
    Copy-Item $sdcsProfile "$userdata\Saves\" -Force
}

$results = Join-Path $test 'results.txt'
$log = Join-Path $test ($(if ($FullPack) { 'client_pack.log' } else { 'client.log' }))
foreach ($f in $results, $log) { if (Test-Path $f) { Remove-Item $f -Force } }
Get-ChildItem $test -Filter 'pk_*.png' -ErrorAction SilentlyContinue | Remove-Item -Force
if (-not $KeepSave) {
    Get-ChildItem "$userdata\Saves" -Directory -Recurse -Filter 'PKAutoTest' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
}

$env:SteamAppId = '251570'; $env:SteamGameId = '251570'
$env:PARKOUR_AUTOTEST = $Filter
$env:PARKOUR_NOPATCH = $(if ($NoPatch) { '1' } else { '' })
$env:PARKOUR_AUTOTEST_CONNECT = $ConnectTo
$env:PARKOUR_AUTOTEST_OUT = $results
$gameArgs = @('-logfile', "`"$log`"", "`"-UserDataFolder=$userdata`"",
    '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720', '-nogs')
if ($Language) { $gameArgs += "-language=$Language" }
$p = Start-Process -FilePath "$exeDir\7DaysToDie.exe" -WorkingDirectory $exeDir -PassThru -ArgumentList $gameArgs
"client pid $($p.Id), waiting for results (timeout $TimeoutMin min)..."

$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalMinutes -lt $TimeoutMin) {
    Start-Sleep 5
    if (Test-Path $results) { Start-Sleep 3; break }
    if ($p.HasExited) { break }
}
"elapsed $([int]$sw.Elapsed.TotalSeconds) s, exited=$($p.HasExited)"
if (Test-Path $results) { Get-Content $results } else { 'NO RESULTS FILE' }

$w = 0; while (-not $p.HasExited -and $w -lt 40) { Start-Sleep 2; $w += 2 }
if (-not $p.HasExited) { $p.Kill(); 'client killed' }

"`n--- log: parkour lines / errors ---"
if (Test-Path $log) {
    Select-String -Path $log -Pattern '\[Parkour\]|Parkour.*(Exception|Error)' | Where-Object { $_.Line -notmatch '\[TEST\] (PASS|FAIL|results)' } |
        Select-Object -First 25 | ForEach-Object { $_.Line }
    Select-String -Path $log -Pattern ' (ERR|EXC) ' | Where-Object { $_.Line -notmatch 'EOS|Steam|Discord' } | Select-Object -First 15 | ForEach-Object { $_.Line }
}
Get-ChildItem $test -Filter 'pk_*.png' -ErrorAction SilentlyContinue | ForEach-Object { "screenshot: $($_.FullName)" }
