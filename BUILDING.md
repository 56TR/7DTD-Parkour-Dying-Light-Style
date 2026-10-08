# Building Parkour Movement

## Requirements

- 7 Days to Die V3.2 installed (the build references its `Assembly-CSharp.dll`, Unity modules and the bundled Harmony).
- .NET SDK 8 or newer (builds the net48 mod DLL and runs the net8 unit tests).
- Python 3.10+ for the generators and checks in `tools/`.

## Build

```
dotnet build ParkourMovement.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die"
```

Or set the `GAME_DIR` environment variable instead of `-p:GameDir`. The DLL lands in `bin/ParkourMovement.dll`.
Copy `bin/ParkourMovement.dll`, `ModInfo.xml`, `Config/` and `Anims/` into `<game>/Mods/ParkourMovement/`.

## Tests

```
dotnet test tests/ParkourMovement.Tests
```

The motor (`src/Core`) is plain C# with no game dependency; the tests run it in a box world (`tests/.../BoxSim.cs`)
for every move, the perk gating and the edge cases found in game.

`src/Game/AutoTest.cs` is an in-game self test: `pk test` in the console (own game only) builds a steel course
200 m up and runs every move with scripted input. `tools/clienttest.ps1` automates that with a separate game
install; it and the other `tools/*.ps1` scripts carry the author's paths (`X:\Tools`, `V:\SteamLibrary`) and need
adjusting before use.

## Layout

| Path | What |
|---|---|
| `src/Core` | The movement state machine (`ParkourMotor`), tuning, types. No Unity, no game code. |
| `src/Game` | Game side: runtime glue, probes, combat, body animation, first-person arm IK, settings, perk integration, options UI, self test. |
| `src/Patches` | Harmony patches: controller motor, input, camera, fall impact. |
| `Config/parkour.xml` | All tuning values; generated from `src/Core/Tuning.cs` by `tools/gen_config.py`. |
| `Config/Localization.csv`, `Config/XUi_Menu/windows.xml` | Texts and the options tab; generated from `tools/i18n/strings.json` by `tools/gen_options.py`. |
| `Anims/*.json` | Third-person clips, retargeted by `tools/retarget_ual.py` (Quaternius UAL, glTF) and `tools/retarget_bvh.py` (CMU BVH). |

## Translations

All player-facing texts are in `tools/i18n/strings.json` (13 languages). Game terms follow the game's own
translations:

```
python -I tools/i18n/extract_glossary.py . "<game dir>"   # the game's terms -> tools/i18n/vanilla_glossary.json
python -I tools/i18n/check.py .                           # placeholders, numbers, scripts, label length, terms, consistency
python -I tools/gen_options.py .                          # -> Config/Localization.csv + options tab XML
```

## Release

`tools/package.ps1` regenerates the config and texts, runs the translation checks, the build and the unit tests,
and writes `release/ParkourMovement-<version>.zip`. The version comes from `ModInfo.xml` and must match
`ModApi.Version`.
