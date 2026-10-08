# Build Release and install the mod where the player's launchers load it:
#   pack\Mods (Play_Modpack.bat, source of truth for releases) and the game's own Mods folder (56 Days to Die launcher).
# Config/parkour.xml is replaced only when the installed copy still matches the previously deployed one (user edits are kept).
param([string[]]$Targets = @('X:\AI Coding Projects\7DTD\pack\Mods\ParkourMovement',
                             'V:\SteamLibrary\steamapps\common\7 Days To Die\Mods\ParkourMovement'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME = 'X:\Tools\dotnet\scratch\_home'; $env:DOTNET_NOLOGO = '1'
if (Get-Process -Name 7DaysToDie -ErrorAction SilentlyContinue) { throw 'game/server running: DLL would be locked' }
& 'X:\Tools\dotnet\dotnet.exe' build "$root\ParkourMovement.csproj" -c Release | Select-String 'error|Build succeeded'
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
python -I "$root\tools\gen_config.py" "$root" | Out-Null

foreach ($dest in $Targets) {
    New-Item -ItemType Directory -Force "$dest\Config", "$dest\Anims" | Out-Null
    Copy-Item "$root\bin\ParkourMovement.dll", "$root\ModInfo.xml", "$root\README.md" $dest -Force
    Copy-Item "$root\Anims\*.json" "$dest\Anims" -Force
    Copy-Item "$root\Config\Localization.csv" "$dest\Config" -Force
    Copy-Item "$root\Config\XUi_Menu" "$dest\Config" -Recurse -Force
    $cfg = "$dest\Config\parkour.xml"
    $stamp = "$dest\Config\.deployed.sha256"
    $userEdited = (Test-Path $cfg) -and (Test-Path $stamp) -and ((Get-FileHash $cfg).Hash -ne (Get-Content $stamp -Raw).Trim())
    if ($userEdited) { "$dest : config edited by the player, kept" }
    else {
        Copy-Item "$root\Config\parkour.xml" $cfg -Force
        (Get-FileHash $cfg).Hash | Set-Content $stamp
        "$dest : installed"
    }
}
