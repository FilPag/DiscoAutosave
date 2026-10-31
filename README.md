# Disco Autosave 1.6.7

**Installed, SHA-256 verified:** v1.6.7-debug.1 adds same-frame native comparison dumps,
transition child/ready-handler timings and optimized exploration quicksaves.
The first three debug saves write full save data to
`BepInEx/DiscoAutosave-comparisons/<capture>/` (`native.zip`, `optimized.zip`,
`comparison.json`). `[Diagnostics] NativeComparisonSaves=0` disables this;
comparison captures intentionally cause additional hitches. Release contains none
of this diagnostic code. These new runtime paths remain unverified.

**Retained from v1.6.6:** removes normal per-save receiver/sequence discovery
scans using scene-load, clone and AddComponent tracking. Inactive/disabled objects
remain registered; current activity is checked at save time. Destroyed objects are
pruned and receiver dispatch uses an ordered snapshot. Failed tracking retains the
previous discovery/safety scans. One bootstrap scan normally runs on scene load.

`python3 build.py release` builds the slim DLL with profiling and routine info logs
compiled out; warnings/errors remain. `build.py debug`
retains profiling/logs and remains installed until runtime gains are confirmed.
Direct builds use `-p:SaveDiagnostics=false -p:SaveLogging=false` for the slim mode.

GC collects at the loading-screen lifecycle boundary,
before its hide/fade-out coroutine starts. No background-image/alpha checks.
Its next matching transition-tail collection is suppressed; unknown coroutine paths,
automatic GC and the separate save-loading collection remain unchanged.
`[Loading] CollectBeforeReveal=false` restores native transition timing.
Conversation blocks are cached during sliced preparation. Final capture validates
native collection versions and mutable scalar fields before copying cached bytes,
instead of recursively exporting unchanged conversations. Unsupported version
semantics retain full traversal. Previous runtime logs confirm cache reuse and paired
GC relocation; broader lifecycle correctness remains unverified. Full diagnostics
remain enabled.

Disco Elysium: The Final Cut on Windows/Proton, using BepInEx 6 IL2CPP build 755.

## Controls
- **F8:** autosave interval (1–30 minutes), area-change triggers, camera/party
  smoothing, VSync override and integer FPS cap (0 = unlimited).
- FPS cap requires VSync off; external limits still apply.
- Autosave icon/sound and the top-left menu control are hidden. Esc still works;
  quicksave/error notifications and controller hints are unchanged.

## Saving
Dialogue preparation runs across frames; state capture stays on the main thread.
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

**Installed:** diagnostic v1.6.7-debug.1; not launched after installation. Latest
authorized log check of v1.6.6-debug.1 found four
successful autosaves, capture 41.9 ms cold and 16.3–17.0 ms warm, and six paired
loading-screen GC collections/suppressions. The user reports a successful
save/reload smoke test; exhaustive restored-state equivalence remains untested.
The snapshot buffer is preallocated/reused across threads, scalar copying uses fewer
capacity checks, and thumbnail encoding/final preparation run before the capture frame.
Record/JSON internals now measure broadcast/receiver costs, all 14 world persisters,
and preload/world JSON tree construction versus printing separately.
Final live-state capture is still synchronous; zero stalls are not guaranteed.
Full profiling is back on, including save origins/modes and GC caller contexts.
Keep diagnostics enabled until the issue is resolved and the user approves removal.
Default builds now include diagnostics; explicit `python3 build.py release` omits them.
Wait 15 seconds after the last test save before requesting a log check.

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

Agent notes: [HANDOFF.md](HANDOFF.md). Archived diagnostics:
[reference/validation-v1.5/](reference/validation-v1.5/README.md).
Persistent rollback instructions: `/path/to/local-data`.
