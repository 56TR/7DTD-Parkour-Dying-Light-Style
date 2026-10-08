# Parkour Movement 1.0.0 (7 Days to Die V3.2)

Dying Light style parkour for 7 Days to Die: slide, wall run, wall climb, vault, ledge hang with shimmy and
corners, landing roll, slide tackles, dropkicks and stomps on zombies. Every move can be switched off in
Options > General > Parkour, and the moves can unlock with the vanilla Parkour perk.

## Requirements

- 7 Days to Die V3.2.
- Easy Anti-Cheat off (start the game without EAC). This mod contains a DLL, like every code mod.
- Nothing else: Harmony ships with the game (`Mods/0_TFP_Harmony`).

## Install

1. Extract the archive into the game's `Mods` folder, so that you have `7 Days To Die/Mods/ParkourMovement/ModInfo.xml`.
2. Start the game with EAC off.
3. Options > General > **Parkour** to see and change the settings.

Update: replace the folder. Your settings are kept (they are stored in the game's user data folder as
`ParkourMovement_settings.txt`, not in the mod folder).

## Multiplayer

- Client-side and optional. You can join servers without the mod (tested on a vanilla dedicated server);
  the server and the other players need nothing.
- Your parkour animations are sent through the game's own animation messages in a form vanilla ignores:
  players who have the mod see them, players without it see the normal animations.
- Hits on zombies use the game's own damage and ragdoll messages, so they work on any server.

## Moves (your normal keys)

| Move | How |
|---|---|
| Mantle | Jump at a ledge up to about 1.6 m above your feet while holding Forward: you pull yourself up. |
| Slide | Sprint, then press Crouch (hold it to keep sliding). Downhill slides speed up. |
| Low slide | A slide fits under 1-block openings. Let go of Crouch underneath and you crawl until there is room to stand. |
| Slide jump | Jump while sliding: the slide speed carries into the air. |
| Landing roll | Press Crouch shortly before landing from a big fall: 75% less fall damage and you keep running. |
| Vault | Sprint into a waist-high obstacle (0.45 - 1.35 m): you hop over it without losing speed. Release Forward over the obstacle to stay on top of it. |
| Ledge hang | Ledges up to about 2.4 m above your feet are grabbed. Forward or Jump: climb up. Back: drop to a lower ledge below (or let go if there is none). Crouch: let go. Back + Jump: kick off. Left / Right: shimmy, also around outer and inner block corners. |
| Wall climb | Sprint at a wall and press Jump (or hold Jump + Forward when you hit it in the air): you run up it. |
| Wall run | Sprint along a wall, jump towards it and keep holding Forward. |
| Wall jump | Press Jump during a wall run, wall climb or hang (with Back). Look away from the wall to aim. |
| Coyote jump | A jump a moment after running off an edge still works. |
| Air momentum | Speed from slides and wall jumps is kept in the air. |

### Zombies

| Move | How | Effect |
|---|---|---|
| Slide tackle | Slide into zombies | 15 damage to each zombie in the way, knocked down about 2 s and thrown back about 3 m; you keep about half your speed |
| Dropkick | Running jump into a zombie | 30 damage, knocked down, you bounce back |
| Stomp | Fall onto a zombie | 25 damage plus more the faster you fall, knocked down; you bounce up and the fall does not hurt |
| Zombie vault | Sprint at a zombie and press Jump within about 2.4 m | You hop over its head |

Hits count as your attacks (aggro, kill credit). A zombie can be hit at most every 0.8 s.

### Stamina and injuries

- Moves cost stamina. Hanging drains it slowly, shimmying faster; at zero you let go.
- Below 10 stamina you are too tired to pull up, climb out of a hang or wall climb (grabbing an edge still works).
- A sprained or broken leg disables slide, wall moves, vaults and kicks. Mantle, hang and landing roll still work.
- While hanging or climbing the body keeps facing the wall: you can look about 80 degrees to each side.

## Settings and the Parkour perk

Options > General > **Parkour** has an On/Off switch for every move and effect. With **Parkour Perk
Integration** on (default), the moves unlock with the levels of the vanilla Parkour perk (Agility):

| Level | Vanilla (kept) | Parkour Movement |
|---|---|---|
| no perk | | Mantle |
| 1 Tumbler | jump stamina -10%, safe fall +1 m | Slide, Low Slide, Landing Roll |
| 2 Stuntman | -20%, +2 m, jump +1 m (unless replaced) | Vault, Ledge Hang, Slide Jump |
| 3 Adrenaline Junkie | -30%, +3 m, no broken legs | Wall Climb, Wall Run, Wall Jump |
| 4 Urban Acrobat | -40%, +4 m, jump +2 m (unless replaced), no leg injuries | Slide Tackle, Dropkick, Stomp, Zombie Vault |
| 5 Parkour Master | -50%, no fall damage (unless switched off) | parkour moves cost half the stamina, hanging costs none |
| Agility Mastery 4 (Specter's Grace) | first attack deflected | Auto-Roll: big landings roll by themselves |

- **Replace Perk Jump Height** (default on): the perk's vanilla jump bonus is removed; the moves take you up instead.
- **No Fall Immunity at Level 5** (default off): level 5 gives 5 m of safe fall instead of no fall damage.
- The perk descriptions in the skill window list exactly what your settings give.
- With the integration off, every move that is switched on is available from the start, including the bonuses.
- Body in First Person, Camera Effects and Body Animations can be switched off separately.

## Languages

English, German, Spanish, French, Italian, Japanese, Korean, Polish, Brazilian Portuguese, Russian, Turkish,
Simplified and Traditional Chinese. The texts use the game's own terms for perks, keys and stats.

## Console (F1)

- `pk`: status. `pk on` / `pk off`: switch the mod.
- `pk opt`: list the settings. `pk opt <key> on|off`: change one.
- `pk perk`: the perk levels in use.
- `pk set <name> <value>`: change a tuning value live. `pk dump`: all values. `pk reload`: re-read `Config/parkour.xml`.
- `pk stamina`: infinite stamina for testing (no god mode).

## Tuning

All movement numbers are in `Config/parkour.xml` (speeds in m/s, times in s, heights in m). Edit, then `pk reload`.

## Known limits

- Wall run, wall climb, mantle and ledge hang use blocks and terrain only: not zombies, players, trees or vehicles.
- Very steep terrain counts as a wall.

## Source code

Open source under the MIT license: https://github.com/56TR/7DTD-Parkour-from-Dying-Light-2
Build instructions: `BUILDING.md`. Bug reports and pull requests are welcome.

## Credits

- Third-person animations: Quaternius Universal Animation Library 1 and 2 (CC0) and the CMU Graphics Lab Motion
  Capture Database, retargeted onto the 7 Days to Die skeleton.
  The data used in this project was obtained from mocap.cs.cmu.edu.
  The database was created with funding from NSF EIA-0196217.
- Movement feel tuned against Dying Light 2's parkour values; no game assets are included.
