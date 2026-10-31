#!/usr/bin/env python3
"""Build an optimized diagnostic or slim release DLL without installing it."""
import argparse
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('mode', choices=['debug', 'release'])
args = parser.parse_args()
root = Path(__file__).resolve().parent
output = root / 'artifacts' / args.mode

def run(command):
    result = subprocess.run(command, cwd=root, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if result.returncode:
        print(result.stdout)
        raise SystemExit(result.returncode)

run(['dotnet', 'build', 'DiscoAutosave.csproj', '-c', 'Release',
     f'-p:SaveDiagnostics={str(args.mode == "debug").lower()}',
     f'-p:SaveLogging={str(args.mode == "debug").lower()}', '-o', str(output)])
dll = (output / 'DiscoAutosave.dll').read_bytes()
if any((name in dll) != (args.mode == 'debug') for name in [b'SaveDiagnostics', b'HitchTrace', b'RuntimeGcEvents', b'SaveInternals', b'NativeSaveDump', b'SavePayloadComparison', b'TransitionTimings']):
    raise SystemExit('FAIL: diagnostic code inclusion does not match the requested build mode.')
if ('Autosave timings:'.encode('utf-16-le') in dll) != (args.mode == 'debug'):
    raise SystemExit('FAIL: routine logging was not compiled as requested.')
run(['dotnet', 'run', '--project', 'tests/ScheduleTests.csproj'])
if args.mode == 'debug':
    run(['dotnet', 'run', '--project', 'tests/DiagnosticsTests.csproj'])
print(f'PASS: {args.mode} build and offline tests. DLL: {output / "DiscoAutosave.dll"}')
print('Not installed. Close the game and obtain permission before replacing the installed DLL.')
