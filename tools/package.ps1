# Builds the release archive for Nexus: <repo>\release\ParkourMovement-<version>.zip containing one folder
# "ParkourMovement" (DLL, ModInfo, README, CHANGELOG, Config, Anims). Stops if the build, the unit tests or the
# translation checks fail.
#   powershell -File tools\package.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path (Split-Path -Parent (Split-Path -Parent $root)) 'release'
$dotnet = 'dotnet'
if (Test-Path 'X:\Tools\dotnet\dotnet.exe') {   # the author's machine; anyone else: dotnet from PATH
    $dotnet = 'X:\Tools\dotnet\dotnet.exe'
    $env:DOTNET_CLI_HOME = 'X:\Tools\dotnet\scratch\_home'
}
$env:DOTNET_NOLOGO = '1'

[xml]$info = Get-Content "$root\ModInfo.xml"
$version = $info.ModInfo.Version.value
$code = Select-String -Path "$root\src\Game\ModApi.cs" -Pattern 'Version = "([^"]+)"'
if ($code.Matches[0].Groups[1].Value -ne $version) { throw "ModInfo version $version differs from ModApi.Version" }

python -I "$root\tools\gen_config.py" "$root" | Out-Null
python -I "$root\tools\gen_options.py" "$root"
python -I "$root\tools\i18n\check.py" "$root" | Select-Object -Last 1
if ($LASTEXITCODE -ne 0) { throw 'translation checks failed (python -I tools\i18n\check.py)' }

& $dotnet build "$root\ParkourMovement.csproj" -c Release | Select-String 'error|Build succeeded' | Select-Object -First 5
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
& $dotnet test "$root\tests\ParkourMovement.Tests" | Select-String 'Passed!|Failed!'
if ($LASTEXITCODE -ne 0) { throw 'unit tests failed' }

$stage = Join-Path $out "stage_$version"
$mod = Join-Path $stage 'ParkourMovement'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$mod\Config\XUi_Menu", "$mod\Anims" | Out-Null
Copy-Item "$root\bin\ParkourMovement.dll", "$root\ModInfo.xml", "$root\README.md", "$root\CHANGELOG.md", "$root\LICENSE" $mod
Copy-Item "$root\Config\parkour.xml", "$root\Config\Localization.csv" "$mod\Config"
Copy-Item "$root\Config\XUi_Menu\windows.xml" "$mod\Config\XUi_Menu"
Copy-Item "$root\Anims\*.json" "$mod\Anims"

$zip = Join-Path $out "ParkourMovement-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# Python's zipfile writes standard '/' paths (PowerShell 5.1 Compress-Archive writes backslash paths, which some mod managers mis-extract)
python -I -c "import os,sys,zipfile; src,dst=sys.argv[1],sys.argv[2]; z=zipfile.ZipFile(dst,'w',zipfile.ZIP_DEFLATED); [z.write(os.path.join(d,f), os.path.relpath(os.path.join(d,f), os.path.dirname(src)).replace(os.sep,'/')) for d,_,fs in os.walk(src) for f in sorted(fs)]; z.close()" $mod $zip
if ($LASTEXITCODE -ne 0) { throw 'zip failed' }
Remove-Item $stage -Recurse -Force
$size = [math]::Round((Get-Item $zip).Length / 1KB)
"release: $zip ($size KB)"
