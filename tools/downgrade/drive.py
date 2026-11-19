"""One console session under the shared vita-device lock (the caller wraps it in lock.py run). From butcher-vita's
tools/downgrade/drive.py (same author, MIT), for this port's title and folder.
Sets the probe flags, launches, plays a scripted sequence of waits and button presses, then fetches the log and the
-t2d-shot(s) screenshots into work/runs/<tag>/ and quits the game.

    uv run tools/downgrade/lock.py run vita-device "t2d: boot" --wait 1800 -- \\
        uv run tools/downgrade/drive.py <tag> "<flags>" "<steps>" [--sync]

    drive.py boot1 "-t2d-heartbeat 5 -t2d-shots 10 ux0:data/T2DNoFate/shot 5" "wait 40" --sync

steps: ';'-separated: `wait <s>`, `press <button> [ms]` (pressed, then released), `until <regex> <timeout s>` (polls
the log). There is no step that leaves a button held.

Safety, as on butcher-vita's: the flags are one launch's (the player renames args.txt as it reads it, and the session
clears them when it ends before a launch reads them); every press is released, also when the session stops part way;
the game is quit at the end; a launch that wrote no log of its own is reported.
"""

import ftplib
import re
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from common import ROOT, vita_host  # noqa: E402

VITA = ['uv', 'run', str(ROOT / 'tools' / 'downgrade' / 'vita.py')]
FOLDER = 'ux0:/data/T2DNoFate'


def vita(*args, quiet=False):
    out = subprocess.run([*VITA, *args], cwd=ROOT, capture_output=True, text=True, encoding='utf-8', errors='replace')
    if not quiet:
        print('vita', ' '.join(args), '->', (out.stdout.strip().splitlines() or [out.stderr.strip()[-200:] or '?'])[-1], flush=True)
    return out.stdout


def ftp():
    f = ftplib.FTP()
    f.connect(vita_host(), 1337, timeout=30)
    f.login()
    return f


def fetch_shots(flags, out: Path):
    """The screenshots the flags asked for, fetched into out and deleted from the console."""
    words = flags.split()
    prefixes = []
    if '-t2d-shots' in words and words.index('-t2d-shots') + 2 < len(words):
        prefixes.append(words[words.index('-t2d-shots') + 2])
    if '-t2d-shot' in words and words.index('-t2d-shot') + 2 < len(words):
        prefixes.append(words[words.index('-t2d-shot') + 2].removesuffix('.png'))
    if not prefixes:
        return
    f = ftp()
    count = 0
    try:
        for prefix in prefixes:
            folder, stem = prefix.replace('ux0:data', 'ux0:/data').rsplit('/', 1)
            if not folder.lower().startswith(FOLDER.lower()):
                print(f'drive: shots outside {FOLDER} are not fetched: {prefix}', flush=True)
                continue
            names = [n.rsplit('/', 1)[-1] for n in f.nlst(folder + '/')]
            for leaf in names:
                if not leaf.startswith(stem) or not leaf.endswith('.png'):
                    continue
                part = out / (leaf + '.part')
                with open(part, 'wb') as file:
                    f.retrbinary(f'RETR {folder}/{leaf}', file.write)
                part.replace(out / leaf)
                f.delete(f'{folder}/{leaf}')
                count += 1
    finally:
        f.quit()
    print(f'fetched {count} shots into {out}', flush=True)


def main():
    tag, flags, steps = sys.argv[1], sys.argv[2], sys.argv[3]
    out = ROOT / 'work' / 'runs' / tag
    out.mkdir(parents=True, exist_ok=True)
    if '--sync' in sys.argv:
        vita('sync')
    vita('args', '--', *flags.split())
    before = (vita('log', quiet=True).splitlines() or [''])[0]
    held = None
    launched = False
    try:
        if 'Launched' not in vita('launch'):
            sys.exit('drive: the launch failed (is the console busy with another app?)')
        launched = True
        started = time.time()
        for step in [s.strip() for s in steps.split(';') if s.strip()]:
            word, *rest = step.split()
            if word == 'wait':
                # The display kept on: when it goes off on its idle timer the system pauses the game.
                end = time.time() + float(rest[0])
                while time.time() < end:
                    time.sleep(min(5.0, max(0.0, end - time.time())))
                    vita('do', 'nosleep on; screen on', quiet=True)
            elif word == 'press':
                hold = rest[1] if len(rest) > 1 else '150'
                held = rest[0]
                vita('do', f'press {held}; wait {hold}ms; release {held}', quiet=True)
                held = None
                print(f'{time.time() - started:6.1f} press {rest[0]}', flush=True)
            elif word == 'until':
                pattern, limit = re.compile(rest[0]), float(rest[1])
                end = time.time() + limit
                while time.time() < end:
                    text = vita('log', quiet=True)
                    first = (text.splitlines() or [''])[0]
                    if first and first != before and pattern.search(text):
                        print(f'{time.time() - started:6.1f} until {rest[0]}: matched', flush=True)
                        break
                    time.sleep(5)
                else:
                    print(f'{time.time() - started:6.1f} until {rest[0]}: TIMED OUT', flush=True)
            else:
                sys.exit(f'unknown step {step!r}')
        time.sleep(2)
    finally:
        if held is not None:
            vita('do', f'release {held}', quiet=True)
            print(f'drive: released {held}', flush=True)
        text = vita('log', quiet=True)
        (out / 'log.txt').write_text(text, encoding='utf-8')
        if launched and (text.splitlines() or [''])[0] == before:
            print("drive: WARNING the log is the previous run's; this launch wrote no log", flush=True)
        vita('quit')
        # The flags are one launch's: if the launch never read them, they go now.
        if vita('args', '--show', quiet=True).strip() not in ('', 'args.txt: (empty)'):
            vita('args', '--')
        try:
            fetch_shots(flags, out)
        except (OSError, EOFError, ftplib.Error) as error:
            print(f'drive: shots not fetched: {error}', flush=True)


main()
