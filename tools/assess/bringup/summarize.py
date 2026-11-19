"""Summarize a probe player's log: the first occurrence of each distinct exception (PROBE_EXC first),
each REWIND_GAP / REWIND_SILENT / REWIND_PASS line with the game frame that hit it, the last PROBE
status lines and the exception totals.

    python -I tools/assess/bringup/summarize.py <run log> [--stack N]
"""
import argparse
import re
import sys
from pathlib import Path

FRAME_SKIP = ('__Rewind.', 'System.Environment', 'Compat2018.', 'UnityEngine.Debug', 'System.Object.__icall')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('log', type=Path)
    ap.add_argument('--stack', type=int, default=3)
    a = ap.parse_args()
    lines = a.log.read_text(encoding='utf-8', errors='replace').splitlines()
    status = [l for l in lines if l.startswith('PROBE t=')]
    for i, line in enumerate(lines):
        if line.startswith('PROBE_EXC first'):
            # the first game frames of the exception's stack, from the Unity log that follows
            frames = []
            for l in lines[i + 1:i + 60]:
                m = re.match(r'\s*(?:at )?([\w\.`<>\[\]]+[\.:][\w\.`<>\[\]]+) ?\(', l)
                if m and not l.lstrip().startswith(('PROBE', 'Rethrow')) and not m.group(1).startswith(FRAME_SKIP):
                    frames.append(m.group(1))
                if len(frames) >= a.stack or (frames and not l.strip()):
                    break
            print(line[:260] + ('\n      at ' + ' <- '.join(frames) if frames else ''))
        elif line.startswith(('REWIND_GAP', 'REWIND_SILENT', 'REWIND_PASS')):
            frames = []
            for l in lines[i + 1:i + 20]:
                m = re.match(r'\s+at ([^\s(]+)', l)
                if m and not m.group(1).startswith(FRAME_SKIP):
                    frames.append(m.group(1))
                if len(frames) >= a.stack:
                    break
            print(line[:220] + ('\n      at ' + ' <- '.join(frames) if frames else ''))
    for line in status[-3:]:
        print(line)
    for line in lines:
        if line.startswith(('PROBE_EXC_SUMMARY', 'PROBE quit')):
            print(line[:260])
    if not status:
        print('no PROBE status lines (the player did not reach the scene, or crashed)', file=sys.stderr)


if __name__ == '__main__':
    main()
