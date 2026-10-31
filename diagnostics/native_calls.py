#!/usr/bin/env python3
"""Read-only call map for named methods in the installed IL2CPP binary."""
import argparse
import bisect
import json
from pathlib import Path
import re
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('methods', nargs='+', help='Exact method name or namespace-qualified suffix from script.json.')
parser.add_argument('--symbols', type=Path, required=True)
parser.add_argument('--assembly', type=Path, required=True)
args = parser.parse_args()
symbols = json.loads(args.symbols.read_text())
names = {}
for method in symbols['ScriptMethod']:
    names.setdefault(method['Address'], []).append(method['Name'])
addresses = sorted(names)
for requested in args.methods:
    matches = [method for method in symbols['ScriptMethod'] if method['Name'].endswith(requested)]
    if not matches:
        raise SystemExit('No symbol found: ' + requested)
    for method in matches:
        start = method['Address']
        index = bisect.bisect_right(addresses, start)
        if index == len(addresses):
            raise SystemExit('Cannot determine method boundary: ' + method['Name'])
        stop = addresses[index]
        result = subprocess.run(['objdump', '-d', '-Mintel', f'--start-address={0x180000000 + start}',
            f'--stop-address={0x180000000 + stop}', str(args.assembly)], check=True, text=True, capture_output=True)
        print(f"{method['Name']} RVA={start:#x}")
        print(' ', method['Signature'])
        indirect = 0
        for line in result.stdout.splitlines():
            if not re.search(r'\b(?:call|jmp)\s', line):
                continue
            match = re.search(r'(call|jmp)\s+0x([0-9a-f]+)', line)
            if not match:
                if 'call' in line:
                    indirect += 1
                continue
            target = int(match[2], 16) - 0x180000000
            if target in names:
                labels = names[target]
                print(' ', line.strip().split(':', 1)[0], '->', ' | '.join(labels[:3]),
                      f'(+{len(labels) - 3} shared generic names)' if len(labels) > 3 else '')
        if indirect:
            print(f'  Indirect/virtual calls: {indirect}; not resolved by this static call map.')
