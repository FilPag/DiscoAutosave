# Disco Autosave 1.6.8

Slim builds are the default. Debug builds retain optional profiling; native comparison
dumps default to off (`NativeComparisonSaves=0`) because they cause extra hitches.
The autosave completion symbol appears silently in the top-right for two seconds,
with an "Autosave complete" caption. The icon is 200×200 at 1440p and scales with screen height.
Version 1.6.8 still needs runtime testing of the new icon.

## Install (Windows, Linux/Proton, Steam Deck)
Requires the Windows version of **Disco Elysium: The Final Cut**; native macOS is not supported.

1. Close the game and back up your saves. In Steam, use **Manage → Browse local files** to find the folder containing `disco.exe`.
2. Download **BepInEx Unity IL2CPP, Windows x64, build 755** from the [official builds page](https://builds.bepinex.dev/projects/bepinex_be). This is the verified loader version; newer builds are not yet verified. Extract it beside `disco.exe`, not into a subfolder. Back up an existing loader before replacing it.
3. **Linux/Steam Deck:** use Proton and set Steam launch options to `WINEDLLOVERRIDES="winhttp=n,b" %command%`. Install files in Desktop Mode on Steam Deck. Windows needs no launch option.
4. Launch the game once to initialize BepInEx (the first launch can take longer), then close it.
5. Copy the release `DiscoAutosave.dll` into `BepInEx/plugins/DiscoAutosave/`, creating the folder if needed. Use `artifacts/release/DiscoAutosave.dll` from a release build; the source checkout alone is not an installable mod. Disable the original Disco QoL mod.
6. Start the game. **F8** opens settings; on Steam Deck, map a controller button to F8. Configuration is saved in `BepInEx/config/local.disco.autosave.cfg`.

To update, close the game and replace only the mod DLL; keep your config. To uninstall the mod, remove its plugin folder.
The current `install.py` uses developer-machine paths; use these manual steps on other machines.

Per-save receiver/sequence discovery uses scene-load, clone and AddComponent
tracking. Inactive/disabled objects
remain registered; current activity is checked at save time. Destroyed objects are
pruned and receiver dispatch uses an ordered snapshot. Failed tracking retains the
previous discovery/safety scans. One bootstrap scan normally runs on scene load.

`python3 build.py release` builds the slim DLL with profiling and routine info logs
compiled out; warnings/errors remain. `build.py debug`
retains profiling/logs. Direct builds also default to slim mode.

GC collects at the loading-screen lifecycle boundary,
before its hide/fade-out coroutine starts. No background-image/alpha checks.
Its next matching transition-tail collection is suppressed; unknown coroutine paths,
automatic GC and the separate save-loading collection remain unchanged.
`[Loading] CollectBeforeReveal=false` restores native transition timing.
Conversation blocks are cached during sliced preparation. Final capture validates
native collection versions and mutable scalar fields before copying cached bytes,
instead of recursively exporting unchanged conversations. Unsupported version
semantics retain full traversal. Previous runtime logs confirm cache reuse and paired
GC relocation; broader lifecycle correctness remains unverified.

Disco Elysium: The Final Cut on Windows/Proton, using BepInEx 6 IL2CPP build 755.

## Controls
- **F8:** autosave interval (1–30 minutes), area-change triggers, camera/party
  smoothing, VSync override and integer FPS cap (0 = unlimited).
- FPS cap requires VSync off; external limits still apply.
- Autosave uses a small, silent top-right icon; the top-left menu control is hidden. Esc still works;
  quicksave/error notifications and controller hints are unchanged.

## Saving
Dialogue preparation runs across frames; state capture stays on the main thread.
Area-change saves start as soon as gameplay is safe, without an extra settling delay.
Preparation uses a soft 1–5 ms slice (20% of frame duration), targeting a save
within roughly 2 seconds under measured conditions, not a hard deadline.
A worker encodes detached data directly into a buffered ZIP. Native file handling
and rotation remain in use. Capture is blocked during dialogue/loading/scripted
movement. Errors stop autosaving; there is no native exporter fallback or runtime
format validation, only an archive existence/length check.

While this mod's autosaving is enabled and healthy, other native autosave requests
are suppressed; their callbacks still complete. Manual saves are unchanged.
Disabling mod autosaving or stopping on an error restores native autosave requests.
Safety scans inspect active scene objects instead of all loaded resources.

Exploration quicksaves use the same sliced capture/worker ZIP/replay pipeline,
retaining the original native quicksave iterator, name, mode, callbacks, rotation
and notifications. This does not depend on the autosave toggle. Unsupported save
formats, unsafe/menu requests and a stopped optimized pipeline retain native
quicksaving. Preparation failure also resumes the original native quicksave.

Forced save-tail GC is skipped for this mod's save iterator; automatic GC remains.
Set `DeferForcedGarbageCollection = false` to restore native save-tail GC.
Camera smoothing changes party animation timing during exploration, not movement speed.

Previous desktop measurements showed roughly 16–17 ms warm capture, with higher
cold-capture costs. Steam Deck performance and the new icon require runtime testing.
The snapshot buffer is preallocated/reused across threads, scalar copying uses fewer
capacity checks, and thumbnail encoding/final preparation run before the capture frame.
Record/JSON internals now measure broadcast/receiver costs, all 14 world persisters,
and preload/world JSON tree construction versus printing separately.
Final live-state capture is still synchronous; zero stalls are not guaranteed.
Debug profiling includes save origins/modes and GC caller contexts through existing hooks.
When profiling, wait 15 seconds after the last test save before requesting a log check.

## Build and check
```sh
dotnet build -c Release
dotnet run --project tests/ScheduleTests.csproj
# Optional byte-exact native fixture check:
dotnet run --project tests/ScheduleTests.csproj -- /path/to/save.ntwtf.zip
# Read the game log only with user permission:
python3 verify.py
```

One-command builds: `python3 build.py debug` (optimized diagnostic DLL) or
`python3 build.py release` (no diagnostic code). Outputs go to `artifacts/debug/`
and `artifacts/release/`; neither command installs anything. Diagnostic details:
[diagnostics/README.md](diagnostics/README.md).
With installation permission and the game closed: `python3 install.py debug`
(or `release`). This backs up the installed DLL/config/saves before replacement.

Install `bin/Release/net6.0/DiscoAutosave.dll` into game
`BepInEx/plugins/DiscoAutosave/` while the game is closed. Configuration:
`BepInEx/config/local.disco.autosave.cfg`. Old experimental keys are ignored.
Keep the original Disco QoL mod disabled.

Older implementations and development history are retained in git, not source copies.
Persistent rollback instructions: `/path/to/local-data`.
