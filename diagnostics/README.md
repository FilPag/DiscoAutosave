# Save diagnostics

```sh
python3 build.py debug    # artifacts/debug/DiscoAutosave.dll
python3 build.py release  # artifacts/release/DiscoAutosave.dll, no diagnostic code
```

Both use optimized Release compilation and run offline tests. No automatic
installation or game launch. Close the game and obtain permission before installing
the diagnostic DLL in place of the existing one; never load both. Current built version:
`1.6.7-debug.1` (installed, SHA-256 verified; not launched). Cross-thread buffer reuse, scalar writes and capture scheduling
have been optimized; no Lua assignment replacement was implemented.
Install while closed, with permission: `python3 install.py debug`.

Debug.3 logs `Save cause:` with elapsed time and the active hooked caller chain for
slow native GC calls, localization bundle unload and scene/save/load coroutines.
These are observed nested method contexts, not guessed native stack traces; an
uncovered caller is explicitly labeled `untracked`. The summarizer prints them.

v1.6.2 adds `Save dialogue:` per-root traversal counts, characters, scanned slots,
flat-buffer bytes/capacity and copy time. Thumbnail timing is now
`thumbnail-encode-early`; preparation completion yields before coherent capture.

`SaveInternals.cs` now reports the selected Record dispatch mode, broadcast and
receiver callback costs, all 14 world persisters, and preload/world fsData tree
construction versus PrettyJson formatting. Derived tree time is the scoped
JsonUtil total minus scoped printer time, including timing-hook overhead.
These nested timings are not additive. They do not change saving behavior.

Read-only static call maps, using the existing Il2CppDumper `script.json`:
`python3 diagnostics/native_calls.py 'PersistentDataManager$$Record' 'SunshinePersistenceJsonSerializer$$Serialize'`.

`DISCO_DIAGNOSTICS` enables this directory and guarded measurement calls in the
production files. Default/debug builds include both; explicit release builds omit
both. `build.py` checks DLL metadata for
accidental diagnostic inclusion/exclusion.

## Native comparisons (v1.6.7)

Debug defaults to comparing the first three optimized captures per session.
`[Diagnostics] NativeComparisonSaves=0` disables this; the limit is 20. Native
dialogue values/extra data and live native JsonUtil output are captured in the same
main-thread capture, without running Record/preparation again. ZIP compression,
payload comparison and artifact writes run on the worker. This intentionally adds
capture/worker time: do not treat these saves as normal performance measurements.

`BepInEx/DiscoAutosave-comparisons/<capture>/` contains `native.zip`,
`optimized.zip` and `comparison.json` with entry equality, lengths, hashes and first
difference offsets. These files contain full save data; do not publish them without
permission. JSON is compared byte-exactly, including formatting/order. Lua states
use the shared existing native export. This checks codec/cache/detachment output,
not receiver discovery completeness, interrupted writes or reload equivalence.
Comparison mismatch/write failures warn without changing the save pipeline.

## Transition children (v1.6.7)

Additional timings cover loading-screen/unload steps, live-agent enable/disable,
map/shadow operations, Amplify texture warmup children (assets/runtimes/page tables/
state/initialization), and resolvable native ready-list callbacks. A startup native
method inventory rejects shared code addresses; generic, ambiguous and unmappable
targets are skipped. Callback discovery is separately timed; lists, callback
identity and native dispatch remain unchanged. No generic/delegate Invoke or
shared Dispose hook is introduced.

Transitions can start their own trace (`Kind=transition`) when no save trace is
active. Existing trace tails remain open through transitions; standalone transition
completion starts the same 12-second observation tail. GPU and automatic IL2CPP GC
remain unmeasured. Runtime coverage and hook safety still require confirmation.

## Measurement
- Main-thread stages: screenshot request, safety scan, Record, each flat table,
  extra data, Lua/JSON/FOW serialization, text encoding, thumbnail encoding,
  ZIP transfer into IL2CPP memory and archive length check. Stages include adjacent
  setup/disposal work; allocated bytes refer to managed allocations on that thread.
- Preparation hooks: conversation calls and Lua.Run calls inside them, total/max.
- Native tail: total/max coroutine step time and file creation, save-list refresh,
  rotation method timings. Hook totals are inclusive/nested: **do not add them**.
- Worst frame duration and its last measured work, plus managed GC counts.
  Frame attribution is a clue, not proof; other game/rendering work shares the frame.
  Managed GC counts do not measure the game's IL2CPP GC or GPU stalls.

Debug.2 continues recording for **12 seconds after the callback** to catch delayed
hitches. Wait 15 seconds after the last save before checking. A new save truncates
the previous tail and labels it accordingly. JSON reports are written off-thread
under game `BepInEx/DiscoAutosave-profiles/`; `verify.py` reads their paths from the
log automatically. Offline analysis: `python3 verify.py --trace /path/to/report.json`.

Reports include timestamped CLR suspension/resume intervals, per-frame main/process
CPU, allocation/collection deltas and last-collection pause durations. Native hooks
cover explicit IL2CPP GC calls, command-buffer execution, actual file operations,
FileEx writes, FileStream writes/flush/disposal, thumbnail release and nested save
coroutines. GC hooks may see calls suppressed by the existing save-tail policy.
Automatic IL2CPP GC and GPU timing are unavailable in the stripped Unity profiler
bindings; missing runtime GC events are explicitly reported. CLR event tests run
forced GC only in a standalone test process, never in the game.

Results are logged after the observation window, outside capture. Timing hooks add overhead;
especially per-conversation/Lua-call measurements are not pristine release timings.
No table contents, dialogue text or save payloads are logged.

With log-read permission, `python3 verify.py` shows the latest profile and slowest
stages. `python3 verify.py --log /path/to/copied.log` analyzes an offline copy.
Compare several warm saves in the same area. Long capture stages suggest the
main-thread bottleneck; large native steps/file calls suggest the final write/rotation
tail. Lua.Run time is a subset of preparation: it estimates the opportunity for
bypassing parsing, not the speedup of an unimplemented replacement.
