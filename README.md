# Smooth Frames

A BepInEx 5 mod for **Apocalypter** that removes the constant micro-stutter many mod setups get, and smooths the movement of the player, the car and its trailers (trailer trains included).

## What it fixes

Many mods look for objects with `FindObjectsOfType` / `FindObjectsOfTypeAll` on timers (every 0.5, 1 or 2 seconds). In a loaded world each of those searches walks every script in the scene and takes around 10 ms, whatever it is looking for. With several mods the timers line up, and a single frame can pay for a dozen searches: that is the stutter you feel while walking, aiming or driving, even on a fast PC.

Smooth Frames redirects those calls, in **every mod in `BepInEx\plugins`**, to a shared cache:

- **One search answers every script type.** A single scene-wide search is shared by all mods and reused for a few seconds (`FindCacheRefresh`, 4 s by default). Searches that include templates and disabled objects (`FindObjectsOfTypeAll`) are kept three times as long.
- **At most one real search per frame.** Requests that arrive in the same frame get the previous answer instead of stacking up.
- **Never stale objects.** Destroyed objects are left out of every answer, and what a mod creates inside a searching method is added to the cache right away.
- **Left as they were:** code that saves or loads the game, and searches that ask for disabled objects.
- **No mod or save file is changed.** The redirection is applied in memory when the game starts.

It also sets rigidbody **interpolation** on the player, the car you drive and every trailer hitched to it (including trailers hitched to other trailers), so they move smoothly between physics steps. The original values come back when you turn it off or leave the vehicle.

Measured on a heavily modded game (25 mods), the result was going from about 45 FPS with stutter every few hundred milliseconds to 75-95 FPS on foot, with the periodic 50-70 ms hitches gone.

## Requirements

- Apocalypter
- BepInEx 5 (5.4.x)

## Installation

**With Apocasetter:** open the in-game Mods window, find Smooth Frames and click install. Updates show up there automatically.

**Manually:**
1. Download the release zip.
2. Put the `SmoothFrames` folder in `Apocalypter\BepInEx\plugins`.
3. Start the game. The config file is `BepInEx\config\com.gelatti.apocalypter.smoothframes.cfg`.

When the game starts, the mod prepares the other mods during the first seconds in the main menu. `LogOutput.log` shows `Find cache ready: N methods patched in M mods` when it is done. If you load a save before that, the rest finishes in the background without slowing the game.

## Settings

Everything is on by default. There are no keys; each option can be switched in **Apocasetter** or in the config file.

| Section | Option | Default | What it does |
|---|---|---|---|
| Fixes | `FindCache` | on | The shared search cache. Turning it off takes effect at once. |
| Fixes | `FindCacheRefresh` | 4 | Seconds a scene-wide search is reused. |
| Fixes | `FindCacheExclude` | empty | DLL names (without `.dll`, comma separated) the cache must leave alone. Takes effect after a restart. |
| Interpolation | `Enabled` | on | Interpolation on the player. |
| Interpolation | `IncludeVehicle` | on | Also on the vehicle you drive. |
| Interpolation | `IncludeTrailer` | on | Also on the trailers hitched to it, trailer trains included. |

## Compatibility

- Works with any BepInEx mod; nothing is listed by name, so mods you install later are covered too.
- If a mod behaves oddly with the cache, add its DLL name to `FindCacheExclude` and restart the game.
- The cache waits 20 s after each scene load, while the game and the mods are still spawning things.
- What this mod cannot fix: the freezes while saving (the game and the mods writing the save file) and the one-time hitch when a mod builds a large structure in a single frame.

## Building from source

Requires the .NET SDK (6 or newer).

```
dotnet build -c Release -p:GamePath="C:\path\to\Apocalypter"
```

Add `-p:CopyToGame=true` to copy the DLL and icon into `BepInEx\plugins\SmoothFrames` after building.

### Releasing

The Apocasetter index reads GitHub releases. For every release:
1. Bump `VERSION` and the assembly version in `src/Plugin.cs`, `<Version>` in the csproj, and the changelog.
2. Run `pwsh tools/release.ps1`. It builds, packs `SmoothFrames-X.Y.Z.zip` (a `SmoothFrames` folder with the DLL, icon, README, LICENSE and CHANGELOG), tags `vX.Y.Z` and publishes the GitHub release.

The game and BepInEx DLLs are referenced from your install and are not included in this repository.

## How it works (for modders)

- The game removes the BepInEx plugin object, so the mod runs from `Application.onBeforeRender`.
- At startup it walks every loaded assembly located in `BepInEx\plugins` (except itself and the loader/Harmony/MonoMod libraries), reads each method's raw IL and resolves the `call`/`callvirt` tokens once per module to find the methods that call `Object.FindObjectsOfType` or `Resources.FindObjectsOfTypeAll`. Only those methods are patched, with a Harmony transpiler that swaps the calls for `FindCache` equivalents with the same signatures. Types whose names contain `Persistence`, `Save` or `Load` are skipped. The work runs within a time budget per frame (8 ms in the main menu, 1 ms in game).
- `FindCache` keeps one entry per requested type. When a script-type entry expires, a single `FindObjectsOfType<MonoBehaviour>()` refills every script entry at once, through a cached map from concrete type to the requested types it satisfies. Other types are searched one by one. Every caller gets its own array, without destroyed objects.
- Inside the patched methods, the result of `Instantiate` / `AddComponent` is passed to `FindCache.Track`, which adds the new components to the matching entries.
- `FindObjectsOfType<T>(true)` (include inactive) is passed straight through.

## License

All rights reserved, see [LICENSE](LICENSE). You can use the mod and read the code, but please do not re-upload it or publish modified versions; link to the [releases page](https://github.com/bgelatti/SmoothFrames/releases) instead. Bug reports and ideas are welcome as [issues](https://github.com/bgelatti/SmoothFrames/issues); pull requests are not accepted.
