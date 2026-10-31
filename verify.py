#!/usr/bin/env python3
"""Read-only summary of the mod's current session."""
import re
import argparse
import json
import statistics
from pathlib import Path, PureWindowsPath

def trace_summary(path):
    data = json.loads(path.read_text())
    frames = data['Frames']
    spans = data['Spans']
    pauses = data['ManagedGcPauses']
    if not frames:
        print('Trace has no recorded frames:', path)
        return
    median = statistics.median(frame['WallMs'] for frame in frames)
    kind = data.get('Kind', 'save')
    print(f"Trace #{data['Save']} ({kind}): {data['Status']}; median frame={median:.1f} ms; "
          f"CLR pauses={len(pauses)}; tail={'truncated' if data['TailTruncated'] else '12 s'}")
    if not data['RuntimeGcProviderEnabled'] or not data['RuntimeGcEventsSeen']:
        print('GC event stream unavailable/unconfirmed; collection snapshots are not exact frame attribution.')
    for frame in sorted(frames, key=lambda frame: frame['WallMs'], reverse=True)[:3]:
        start, wall = frame['StartMs'], frame['WallMs']
        end = start + wall
        overlap = lambda event: max(0, min(end, event['StartMs'] + event['DurationMs']) - max(start, event['StartMs']))
        gc_ms = sum(overlap(pause) for pause in pauses)
        measured = sorted(((span, overlap(span)) for span in spans if span['Thread'] == data['MainThread']),
                          key=lambda item: item[1], reverse=True)
        measured = [(span, ms) for span, ms in measured if ms >= 1]
        cpu = frame['MainCpuMs']
        cpu_label = 'unavailable' if cpu is None else f'{cpu:.1f} ms'
        completed = data['CompletedMs']
        completion = 'transition completion' if kind == 'transition' else 'callback'
        when = f'before {completion}' if completed is None or end <= completed else f'{max(0, start - completed):.0f} ms after {completion} (or straddling)'
        print(f"Frame {frame['Number']}: wall={wall:.1f} ms, main CPU={cpu_label}, "
              f"process CPU={frame['ProcessCpuMs']:.1f} ms, CLR suspension overlap={gc_ms:.1f} ms; {when}")
        if measured:
            print('  Inclusive main-thread spans:', '; '.join(f"{span['Name']}={ms:.1f} ms" for span, ms in measured[:4]))
        native_gc = max((ms for span, ms in measured if '.GC.Collect' in span['Name'] or '.GC.WaitForPendingFinalizers' in span['Name']), default=0)
        io = max((ms for span, ms in measured if span['Name'].startswith(('FileEx.', 'CreateFileOp.', 'FileOpCommandBuffer.', 'Il2CppSystem.IO.FileStream.'))), default=0)
        if gc_ms >= max(5, wall * 0.2):
            print('  Evidence: timestamped CLR suspension explains part/all of this stall.')
        elif native_gc >= max(5, wall * 0.2):
            print('  Evidence: a native explicit GC/finalizer call overlaps this stall.')
        elif io >= max(5, wall * 0.2):
            print('  Evidence: backend file execution overlaps this stall; spans include nested work.')
        elif cpu is not None and cpu < wall * 0.25:
            print('  Evidence: main thread mostly blocked/descheduled; driver/GPU/OS waits remain unresolved.')
        else:
            print('  Evidence: CPU work/mixed stall; inspect spans. Automatic IL2CPP GC is not directly measured.')
        gc_delta = [frame[f'Gen{i}'] for i in range(3)]
        print(f"  Allocation: main={frame['MainAllocatedBytes']} B, process={frame['TotalAllocatedBytes']} B; GC count deltas={gc_delta}")
    workers = sorted((span for span in spans if span['Name'].startswith('worker-')), key=lambda span: span['DurationMs'], reverse=True)
    if workers:
        print('Largest worker spans:', '; '.join(f"{span['Name']}={span['DurationMs']:.1f} ms/{span['AllocatedBytes']} B" for span in workers[:4]))
    if data['DroppedSpans'] or data['DroppedFrames']:
        print(f"Trace truncated: {data['DroppedSpans']} spans and {data['DroppedFrames']} frames dropped.")

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--log', type=Path, help='Path to BepInEx/LogOutput.log.')
parser.add_argument('--profiles', type=int, choices=range(1, 21), default=1, metavar='1..20')
parser.add_argument('--trace', type=Path, help='Analyze one detailed JSON trace without reading the game log.')
args = parser.parse_args()
if args.trace:
    trace_summary(args.trace)
    raise SystemExit(0)
log = args.log
if log is None:
    parser.error('--log is required unless --trace is supplied')
text = log.read_text(errors='replace')
versions = re.findall(r'Loading \[Disco Autosave ([^\]]+)\]', text)
print('Loaded version:', versions[-1] if versions else 'not found')
print('Completed autosaves:', text.count('Autosave written.'))
print('Completed optimized quicksaves:', text.count('Quicksave written through optimized pipeline.'))
for comparison in re.findall(r'Native save comparison: [^\n]+', text)[-3:]:
    print(comparison)
for warning in re.findall(r'Native save comparison unavailable[^\n]*', text)[-3:]:
    print(warning)
print('Suppressed built-in autosaves:', text.count('Built-in autosave suppressed;'))
for origin in re.findall(r'Save origin: [^\n]+', text)[-5:]:
    print(origin)
errors = [line.strip() for line in text.splitlines() if re.search(r'\[(Error|Fatal)|timed out|stopped:', line, re.I)]
print('Problems:', len(errors))
for error in errors[-3:]:
    print(error[:240])
timings = [line.split('Autosave timings:', 1)[1].strip() for line in text.splitlines() if 'Autosave timings:' in line]
for timing in timings[-3:]:
    print('Timing:', timing)
profiles = list(re.finditer(r'Save profile #[^\n]+', text))
for index in range(max(0, len(profiles) - args.profiles), len(profiles)):
    profile = profiles[index]
    latest = text[profile.start():profiles[index + 1].start() if index + 1 < len(profiles) else len(text)]
    print(profile.group())
    stages = re.search(r'Save stages: ([^\n]+)', latest)
    if stages:
        measured = re.findall(r'([\w-]+)=([\d.]+) ms/\d+ B@\d+', stages.group(1))
        slowest = sorted(measured, key=lambda item: float(item[1]), reverse=True)[:5]
        print('Largest main-thread stages:', '; '.join(f'{name}={ms} ms' for name, ms in slowest))
    for label in ['Save native:', 'Save prep:']:
        match = re.search(re.escape(label) + r' [^\n]+', latest)
        if match:
            metrics = re.findall(r'([^;:]+?) calls=\d+, total=[\d.]+ ms, max=([\d.]+) ms', match.group())
            largest = sorted(metrics, key=lambda item: float(item[1]), reverse=True)[:5]
            print(label, '; '.join(f'{name.strip()} max={ms} ms' for name, ms in largest))
    for cause in re.findall(r'Save cause: [^\n]+', latest):
        print(cause)
    for copy in re.findall(r'Save dialogue: [^\n]+', latest):
        print(copy)
    for label in ['Save Record internals:', 'Save JSON persisters:']:
        match = re.search(re.escape(label) + r' ([^\n]+)', latest)
        if match:
            entries = match.group(1).split('; ')
            print(label, '; '.join(entries[:6]))
    for split in re.findall(r'Save JSON split: [^\n]+', latest):
        print(split)
traces = list(re.finditer(r'Save trace #\d+: (.+?); CLR pauses=', text))
for trace in traces[-args.profiles:]:
    path = Path(trace.group(1))
    if not path.exists() and 'DiscoAutosave-profiles' in PureWindowsPath(trace.group(1)).parts:
        path = log.parent / 'DiscoAutosave-profiles' / PureWindowsPath(trace.group(1)).name
    if path.exists():
        trace_summary(path)
    else:
        print('Trace file unavailable:', path)
raise SystemExit(1 if errors else 0)
