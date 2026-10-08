# Multiplayer compatibility test: a VANILLA dedicated server (shadow install, Mods = only the game's own Harmony,
# EAC off) and the modded client joining it. Checks the client can play parkour there and the server stays clean.
# Usage: powershell -File tools\mptest.ps1 [-Port 26910] [-BootTimeoutSec 300]
param([int]$Port = 26910, [int]$TelnetPort = 8095, [int]$BootTimeoutSec = 300)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$test = Join-Path $root '_test'
$game = 'V:\SteamLibrary\steamapps\common\7 Days To Die'
$srv = Join-Path $test 'game_vanilla'
$srvData = Join-Path $test 'userdata_server'
$log = Join-Path $test 'server_vanilla.log'
if (Get-Process -Name 7DaysToDie -ErrorAction SilentlyContinue) { throw '7DaysToDie.exe is already running' }

# shadow install without this mod
if (-not (Test-Path "$srv\7DaysToDie.exe")) {
    New-Item -ItemType Directory -Force $srv | Out-Null
    Get-ChildItem $game -File | Where-Object { $_.Extension -in '.exe', '.dll', '.txt', '.cfg', '.config' } | ForEach-Object { Copy-Item $_.FullName $srv -Force }
    foreach ($d in '7DaysToDie_Data', 'Data', 'MonoBleedingEdge', 'EasyAntiCheat', 'Licenses', 'Logos') {
        if (Test-Path "$game\$d") { New-Item -ItemType Junction -Path "$srv\$d" -Target "$game\$d" | Out-Null }
    }
    New-Item -ItemType Directory -Force "$srv\Mods" | Out-Null
    New-Item -ItemType Junction -Path "$srv\Mods\0_TFP_Harmony" -Target "$game\Mods\0_TFP_Harmony" | Out-Null
}
if (Test-Path "$srv\Mods\ParkourMovement") { throw 'vanilla server install must not contain ParkourMovement' }
New-Item -ItemType Directory -Force $srvData | Out-Null

$cfg = Join-Path $test 'serverconfig_vanilla.xml'
$x = Get-Content "$game\serverconfig.xml" -Raw
function SetProp([string]$name, [string]$value) {
    $script:x = [regex]::Replace($script:x, "(<property\s+name=`"$name`"\s+value=`")[^`"]*(`")", "`${1}$value`${2}")
}
SetProp 'ServerName' 'Parkour vanilla test'; SetProp 'ServerPort' "$Port"; SetProp 'ServerVisibility' '0'
SetProp 'EACEnabled' 'false'; SetProp 'GameWorld' 'Navezgane'; SetProp 'GameName' 'PKVanilla'
SetProp 'TelnetEnabled' 'true'; SetProp 'TelnetPort' "$TelnetPort"; SetProp 'TelnetPassword' ''; SetProp 'WebDashboardEnabled' 'false'
Set-Content $cfg $x -Encoding UTF8
if (Test-Path $log) { Remove-Item $log -Force }

$env:SteamAppId = '251570'; $env:SteamGameId = '251570'
$s = Start-Process -FilePath "$srv\7DaysToDie.exe" -WorkingDirectory $srv -PassThru -ArgumentList @(
    '-batchmode', '-nographics', '-dedicated', "`"-configfile=$cfg`"", "`"-UserDataFolder=$srvData`"", '-logfile', "`"$log`"")
$t = 0
while ($t -lt $BootTimeoutSec) {
    Start-Sleep 3; $t += 3
    if ($s.HasExited) { throw "server exited early (code $($s.ExitCode)); see $log" }
    if ((Test-Path $log) -and (Select-String -Path $log -Pattern 'StartGame done' -Quiet)) { break }
}
if ($t -ge $BootTimeoutSec) { $s.Kill(); throw 'server boot timeout' }
"vanilla server up in ~$t s (pid $($s.Id)), mods loaded:"
Select-String -Path $log -Pattern '\[MODS\].*Loaded Mod' | ForEach-Object { '  ' + $_.Line.Trim() }

try {
    & (Join-Path $PSScriptRoot 'clienttest.ps1') -Filter 'mp' -ConnectTo "127.0.0.1:$Port" -TimeoutMin 12
}
finally {
    try {
        $c = New-Object Net.Sockets.TcpClient('127.0.0.1', $TelnetPort); $st = $c.GetStream()
        $b = [Text.Encoding]::ASCII.GetBytes("shutdown`r`n"); $st.Write($b, 0, $b.Length); Start-Sleep 3; $c.Close()
    } catch { 'telnet shutdown failed: ' + $_.Exception.Message }
    $w = 0; while (-not $s.HasExited -and $w -lt 60) { Start-Sleep 2; $w += 2 }
    if (-not $s.HasExited) { $s.Kill(); 'server killed' }
}
"`n--- vanilla server log: player / errors ---"
Select-String -Path $log -Pattern 'PlayerSpawnedInWorld|Player connected|Player disconnected|kick|Kick' | Select-Object -First 10 | ForEach-Object { $_.Line }
$errs = Select-String -Path $log -Pattern ' (ERR|EXC) ' | Where-Object { $_.Line -notmatch 'EOS|Steam|Xbox|XBL' }
"server ERR/EXC lines: $($errs.Count)"
$errs | Select-Object -First 15 | ForEach-Object { $_.Line }
