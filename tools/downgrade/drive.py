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
the game is quit at the end (only when it is the running app: another app is never quit), also after a launch whose
reply was lost. Each step of the end runs whatever the others did, and the session exits non-zero when one failed,
when the launch wasn't confirmed or when the launch wrote no log of its own.
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


class Failed(Exception):
    """A vita.py command that exited non-zero, with its last words."""


def vita(*args, quiet=False, check=True):
    """vita.py's output. check: a non-zero exit raises Failed (a sync, flags or a press that didn't happen must
    stop the session, not go on as if it had)."""
    out = subprocess.run(
        [*VITA, *args], cwd=ROOT, capture_output=True, text=True, encoding='utf-8', errors='replace', check=False
    )
    last = (out.stdout.strip().splitlines() or [out.stderr.strip()[-200:] or '?'])[-1]
    if not quiet or out.returncode:
        code = f' (exit code {out.returncode})' if out.returncode else ''
        print('vita', ' '.join(args), '->', last + code, flush=True)
    if check and out.returncode:
        raise Failed(f'vita.py {args[0]} failed (exit code {out.returncode}): {out.stderr.strip()[-300:] or last}')
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


def release(button):
    """Release a button, tried three times; whether vita.py did it."""
    for _ in range(3):
        try:
            vita('do', f'release {button}', quiet=True)
        except Failed as error:
            last = error
            time.sleep(1)
        else:
            print(f'drive: released {button}', flush=True)
            return True
    print(f'drive: WARNING {button} may still be held: {last}', flush=True)
    return False


def cleanup(name, failures, action):
    """One step of the session's end: run on its own, so a failed step (a log file that can't be written, a
    console that doesn't answer) doesn't skip the ones after it; a failure is recorded in failures."""
    try:
        if action() is False:
            failures.append(name)
    except (Failed, OSError, EOFError, ftplib.Error) as error:
        print(f'drive: {name} failed: {error}', flush=True)
        failures.append(name)


def main():
    tag, flags, steps = sys.argv[1], sys.argv[2], sys.argv[3]
    out = ROOT / 'work' / 'runs' / tag
    out.mkdir(parents=True, exist_ok=True)
    for old in ('log.txt', 'log.stale.txt'):  # a tag run again: no log of the last session's left to read as this one's
        (out / old).unlink(missing_ok=True)
    try:
        if '--sync' in sys.argv:
            vita('sync')  # a failed sync would run the build before
        vita('args', '--', *flags.split())
        # The run before's whole log: a new run's differs. None on this console yet (a first run) is ''.
        before = vita('log', '--missing-ok', quiet=True)
    except Failed as error:
        vita('args', '--', quiet=True, check=False)  # flags set before the failure aren't left for the next launch
        sys.exit(f'drive: {error}; nothing was launched')
    held = None
    # Set before the launch is asked for: a launch whose "Launched" reply was lost (a timeout) may still have
    # started the game, which the session's end then quits (only if it is the running app: vita.py quit).
    tried = False
    stale = False
    failures = []
    try:
        tried = True
        if 'Launched' not in vita('launch', check=False):
            failures.append('launch')
            sys.exit('drive: the launch was not confirmed (is the console busy with another app?)')
        started = time.time()
        for step in [s.strip() for s in steps.split(';') if s.strip()]:
            word, *rest = step.split()
            if word == 'wait':
                # The display kept on: when it goes off on its idle timer the system pauses the game.
                end = time.time() + float(rest[0])
                while time.time() < end:
                    time.sleep(min(5.0, max(0.0, end - time.time())))
                    vita('do', 'nosleep on; screen on', quiet=True, check=False)  # the next one tries again
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
                    text = vita('log', quiet=True, check=False)
                    if text.strip() and text != before and pattern.search(text):
                        print(f'{time.time() - started:6.1f} until {rest[0]}: matched', flush=True)
                        break
                    time.sleep(5)
                else:
                    print(f'{time.time() - started:6.1f} until {rest[0]}: TIMED OUT', flush=True)
            else:
                sys.exit(f'unknown step {step!r}')
        time.sleep(2)
    except Failed as error:
        failures.append('session')
        print(f'drive: {error}; the session stopped there', flush=True)
    finally:
        if held is not None:
            cleanup(f'release {held}', failures, lambda: release(held))
        if tried:

            def collect():
                nonlocal stale
                text = vita('log', quiet=True)
                # The same text as before the launch: the player wrote no log of its own (it rotates log.txt as
                # it starts), so this is the previous run's, kept apart rather than recorded as this one's.
                stale = text == before
                (out / ('log.stale.txt' if stale else 'log.txt')).write_text(text, encoding='utf-8')
                if stale:
                    print("drive: WARNING the log is the previous run's; this launch wrote none (log.stale.txt)", flush=True)

            cleanup('reading the log', failures, collect)
            # Only this game: vita.py quit asks perf which app runs first and never quits another one.
            cleanup('quit', failures, lambda: vita('quit') and None)

        def clear_flags():  # the flags are one launch's: if the launch never read them, they go now
            if vita('args', '--show', quiet=True).strip() not in ('', 'args.txt: (empty)'):
                vita('args', '--')

        cleanup('clearing the flags', failures, clear_flags)
        if tried:
            cleanup('fetching the shots', failures, lambda: fetch_shots(flags, out))
    if failures or stale:
        sys.exit(f'drive: {", ".join(failures + (["no log of its own"] if stale else []))} failed')


main()
