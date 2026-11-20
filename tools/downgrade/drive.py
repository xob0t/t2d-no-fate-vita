"""One console session under the shared vita-device lock (the caller wraps it in lock.py run). From butcher-vita's
tools/downgrade/drive.py (same author, MIT), for this port's title and folder.
Sets the probe flags, launches, plays a scripted sequence of waits and button presses, then fetches the log and the
-t2d-shot(s) screenshots (ux0:data/T2DNoFate/shots/<name>[-<s>].png) into work/runs/<tag>/ and quits the game.

    uv run tools/downgrade/lock.py run vita-device "t2d: boot" --wait 1800 -- \\
        uv run tools/downgrade/drive.py <tag> "<flags>" "<steps>" [--sync]

    drive.py boot1 "-t2d-heartbeat 5 -t2d-shots 10 shot 5" "wait 40" --sync

steps: ';'-separated: `wait <s>`, `press <button> [ms]` (pressed, then released), `until <regex> <timeout s>` (polls
the log). There is no step that leaves a button held.

Safety, as on butcher-vita's: the flags are one launch's (the player renames args.txt as it reads it, and the session
clears them when it ends before a launch reads them); every press is released, also when the session stops part way;
the game is quit at the end (only when it is the running app: another app is never quit), also after a launch whose
reply was lost. Each step of the end runs whatever the others did, and the session exits non-zero when one failed,
when the launch wasn't confirmed or when the launch wrote no log of its own.

The session holds the console once for all its steps: inside lock.py run (any port's), or, run alone, it takes the
vita-device lock itself (and stops if someone holds it) and hands it to its vita.py commands (T2D_HOLDER).
"""

import ftplib
import os
import re
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import lock  # noqa: E402
from common import ROOT, vita_host  # noqa: E402

VITA = ['uv', 'run', str(ROOT / 'tools' / 'downgrade' / 'vita.py')]
FOLDER = 'ux0:/data/T2DNoFate'
SHOTS = FOLDER + '/shots'  # where the player saves screenshots (Probe.Shot), and only there


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


SHOT_NAME = re.compile(r'[A-Za-z0-9_-]{1,48}')


def shot_names(flags):
    """The -t2d-shot and -t2d-shots names in flags, as the player takes them (Probe.ShotName: letters, digits, '-'
    and '_', a '.png' dropped): (single, series), each None when not asked for. A name the player would refuse (a
    path, say) raises ValueError, before anything is launched."""
    words = flags.split()
    found = []
    for flag in ('-t2d-shot', '-t2d-shots'):
        if flag in words and words.index(flag) + 2 < len(words):
            name = words[words.index(flag) + 2]
            stem = name[:-4] if name.lower().endswith('.png') else name
            if not SHOT_NAME.fullmatch(stem):
                raise ValueError(f'{flag} {name}: a screenshot is a name (letters, digits, - and _), saved in {SHOTS}')
            found.append(stem)
        else:
            found.append(None)
    return found[0], found[1]


def fetch_shots(flags, out: Path):
    """The screenshots the flags asked for, fetched from SHOTS into out and deleted from the console: the -t2d-shot
    name's file and the -t2d-shots name's <name>-<seconds>.png, nothing else."""
    single, series = shot_names(flags)
    if single is None and series is None:
        return
    wanted = re.compile(
        '|'.join(
            ([re.escape(single) + r'\.png'] if single else []) + ([re.escape(series) + r'-\d+\.png'] if series else [])
        )
    )
    f = ftp()
    count = 0
    try:
        for leaf in [n.rsplit('/', 1)[-1] for n in f.nlst(SHOTS + '/')]:
            if not wanted.fullmatch(leaf):
                continue
            part = out / (leaf + '.part')
            with open(part, 'wb') as file:
                f.retrbinary(f'RETR {SHOTS}/{leaf}', file.write)
            part.replace(out / leaf)
            f.delete(f'{SHOTS}/{leaf}')
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
    try:
        shot_names(flags)
    except ValueError as error:
        sys.exit(f'drive: {error}; nothing was launched')
    # One session, one holder: every vita.py this session runs acts under the same hold of the console. Run alone
    # (not in lock.py run), each vita.py took the lock for its own command, so another job could take the console
    # between the flags' upload and the launch, or between a press and its release.
    holder = None
    if not lock.ours('vita-device'):
        holder = f'drive.py {os.getpid()}: {tag}'
        if lock.take('vita-device', holder, 0, process=True) != 0:
            sys.exit(f'drive: the console is held by "{lock.holder_of("vita-device")}"; nothing was launched')
        os.environ['T2D_HOLDER'] = holder  # the vita.py children's lock.ours
    try:
        session(tag, flags, steps)
    finally:
        if holder is not None:
            lock.release('vita-device', holder)


def session(tag, flags, steps):
    out = ROOT / 'work' / 'runs' / tag
    out.mkdir(parents=True, exist_ok=True)
    for old in ('log.txt', 'log.stale.txt'):  # a tag run again: no log of the last session's left to read as this one's
        (out / old).unlink(missing_ok=True)
    if '--sync' in sys.argv:
        try:
            vita('sync')  # a failed sync would run the build before
        except Failed as error:
            sys.exit(f'drive: {error}; nothing was launched')
    held = None
    # Set before the launch is asked for: a launch whose "Launched" reply was lost (a timeout) may still have
    # started the game, which the session's end then quits (only if it is the running app: vita.py quit).
    tried = False
    stale = False
    failures = []
    # From the flags' upload on, whatever stops the session (a failure, Ctrl+C) clears flags no launch read: the
    # upload may have landed even when its reply was lost. Before (Nov 2026) an interrupt between the upload and
    # the session's try left them for the next launch.
    try:
        vita('args', '--', *flags.split())
        # The run before's whole log: a new run's differs. None on this console yet (a first run) is ''.
        before = vita('log', '--missing-ok', quiet=True)
    except BaseException as error:
        vita('args', '--', quiet=True, check=False)
        if isinstance(error, Failed):
            sys.exit(f'drive: {error}; nothing was launched')
        raise
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


if __name__ == '__main__':
    main()
