# Crossbow Balance (for Infernax co-op)

A BepInEx mod that tones down the co-op sidekick's crossbow (Cervul's), which otherwise tends to clear the screen before player 1 can do anything.

- **Lower damage:** bolts deal 50% damage at every crossbow level.
- **Ammo:** 9 shots, refilled at save shrines (and on death).
- **Pierce cap:** once upgraded to piercing, a bolt goes through at most 2 enemies instead of the whole row. (It still feels strong, and Cervul has other tools Alcedor doesn't get.)
- **HUD:** the tool icon's `xN` counter shows remaining ammo and fades while no bolt is loaded.

Tested alongside the Runecedor mod (Infernax ClassMod 0.8.59).

## Requirements

- Infernax (Steam, current version)
- [BepInEx 6 for IL2CPP](https://builds.bepinex.dev/projects/bepinex_be) (tested on 6.0.0-be.791).
  Run the game once with BepInEx before installing, so it can generate its files.

## Install

1. Install BepInEx to the Infernax game folder. Download [Here](https://builds.bepinex.dev/projects/bepinex_be/791/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.791%2Bb782da4.zip) - or find on [their site](https://builds.bepinex.dev/projects/bepinex_be) (I used artifact #791.)
2. Download `CrossbowBalance-x.y.z.zip` from the [Releases](https://github.com/Aeroxia/Infernax-Crossbow-Balance-Mod/releases) page
3. Extract it into the Infernax game folder, so the dll ends up in `Infernax\BepInEx\plugins\`.

## Settings

Launch the game once and the config is created at `BepInEx\config\crossbowbalance.cfg`.
Edit it with the game closed; changes apply on next launch.

| Section | Setting | Default | |
|---|---|---|---|
| Damage | `DamageMultiplier` | 0.5 | Bolt damage multiplier (1 = vanilla) |
| Damage | `PierceCap` | 2 | Max enemies a piercing bolt can hit (0 = vanilla, unlimited) |
| Ammo | `Enabled` | true | Turn the ammo system off entirely |
| Ammo | `MaxAmmo` | 9 | Shots per shrine refill (the HUD shows 2+ digits vertically, so keep it at 9 or below) |
| Ammo | `RefillOnDeath` | true | Also refill on respawn |
| Ammo | `ShowAmmoInHud` | true | Show ammo in the HUD counter instead of loaded bolts |
| HUD | `DimWhenUnloaded` | true | Fade the HUD counter while no bolt is loaded |
| HUD | `DimAlpha` | 0.35 | How faded (0 = invisible, 1 = no fade) |

## Known limitations

- Upgrade descriptions in the shrine menu still describe the vanilla crossbow.
- With `ShowAmmoInHud` on (default), the counter shows total ammo instead of the one loaded shot. The loaded shot is shown by not being dimmed text.

## Building from source

Requires the .NET SDK (6.0 or newer). Put this folder at `<Infernax>\!Etc\CrossbowBalance`, or pass the game path explicitly:

```
dotnet build -c Release -p:GameDir="C:\path\to\Infernax\"
```

The built dll is copied to `BepInEx\plugins` automatically.

## License

MIT, see `LICENSE`.
