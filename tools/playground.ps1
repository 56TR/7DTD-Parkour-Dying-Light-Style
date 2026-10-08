# Start the clean test client (shadow install: only Harmony + this mod) into a fresh Navezgane world, build the
# sky course and hand it to the player. No scripted input, the game stays open.
# Usage: powershell -File tools\playground.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$test = Join-Path $root '_test'
$exeDir = Join-Path $test 'game'
$userdata = Join-Path $test 'userdata'
$env:DOTNET_CLI_HOME = 'X:\Tools\dotnet\scratch\_home'; $env:DOTNET_NOLOGO = '1'
if (Get-Process -Name 7DaysToDie -ErrorAction SilentlyContinue) { throw '7DaysToDie.exe is already running (close the game first)' }
if (-not (Test-Path "$exeDir\7DaysToDie.exe")) { throw 'run tools\clienttest.ps1 once first (it creates the shadow install)' }

& 'X:\Tools\dotnet\dotnet.exe' build "$root\ParkourMovement.csproj" -c Release 2>&1 | Select-String 'error|Build succeeded' | ForEach-Object { $_.Line }
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
python -I "$root\tools\gen_config.py" "$root" | Out-Null
$dst = "$exeDir\Mods\ParkourMovement"
New-Item -ItemType Directory -Force "$dst\Config", "$dst\Anims" | Out-Null
Copy-Item "$root\bin\ParkourMovement.dll", "$root\ModInfo.xml" $dst -Force
Copy-Item "$root\Config\parkour.xml" "$dst\Config" -Force
Copy-Item "$root\Config\Localization.csv" "$dst\Config" -Force
Copy-Item "$root\Config\XUi_Menu" "$dst\Config" -Recurse -Force
Copy-Item "$root\Anims\*.json" "$dst\Anims" -Force

$sdcsProfile = "$env:APPDATA\7DaysToDie\Saves\sdcs_profiles.sdf"
if ((Test-Path $sdcsProfile) -and -not (Test-Path "$userdata\Saves\sdcs_profiles.sdf")) {
    New-Item -ItemType Directory -Force "$userdata\Saves" | Out-Null
    Copy-Item $sdcsProfile "$userdata\Saves\" -Force
}
Get-ChildItem "$userdata\Saves" -Directory -Recurse -Filter 'PKAutoTest' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force

$env:SteamAppId = '251570'; $env:SteamGameId = '251570'
$env:PARKOUR_AUTOTEST = 'playground'
$env:PARKOUR_AUTOTEST_OUT = Join-Path $test 'results.txt'
$env:PARKOUR_AUTOTEST_CONNECT = ''
$env:PARKOUR_NOPATCH = ''
$p = Start-Process -FilePath "$exeDir\7DaysToDie.exe" -WorkingDirectory $exeDir -PassThru -ArgumentList @(
    '-logfile', "`"$test\playground.log`"", "`"-UserDataFolder=$userdata`"")
"playground starting (pid $($p.Id)) - the world loads by itself, the course is built 200 m up, then it's yours"
