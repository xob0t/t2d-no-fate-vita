"""Drive the PS Vita audio test app (title TDAU00001) on the shared console.

    python -I tools/downgrade/audio/vita-audio-test/va.py <command> ...

Run every command that touches the console inside butcher-vita's lock, which all ports share:

    python <butcher-vita>/tools/downgrade/lock.py run vita-device "t2d: audio" --wait 1800 -- \
        python -I tools/downgrade/audio/vita-audio-test/va.py run <tag> "load; play 2 8 6"

Commands:
  state                       perf's answer: the running app, clocks, fps
  install [vpk]               stream the VPK to vitacompanion's installer
                              (default work/vita-audio-test/t2d-audio-test.vpk)
  run <tag> "<steps>"         write args.txt, launch, wait for the log's DONE (or the app to stop), fetch the log
                              into work/vita-audio-test/runs/<tag>
  fetch <tag>                 fetch the log only
  crashes                     list crash dumps in ux0:data

The console's address: BUTCHER_VITA_HOST, HUNT_VITA_HOST, CROW_VITA_HOST or DRIFTER_VITA_HOST, as the ports read
them (process, then user and machine environment). It never quits an app other than TDAU00001: when another app
runs, it stops. Ctrl+C lets a running FTP transfer finish (a transfer cut part way can hang the FTP server).
"""

import contextlib
import ftplib
import io
import os
import re
import signal
import socket
import sys
import time
import winreg
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import audio_rules

WORK = audio_rules.default_export()[1].parent / 'vita-audio-test'
RUNS = WORK / 'runs'
DEFAULT_VPK = WORK / 't2d-audio-test.vpk'
TITLE = 'TDAU00001'
REMOTE = 'ux0:/data/t2daudio'
KEEP_AWAKE = 'nosleep on; screen on'


def setting(name):
    if os.environ.get(name):
        return os.environ[name]
    for key, path in (
        (winreg.HKEY_CURRENT_USER, 'Environment'),
        (winreg.HKEY_LOCAL_MACHINE, r'SYSTEM\CurrentControlSet\Control\Session Manager\Environment'),
    ):
        with contextlib.suppress(OSError), winreg.OpenKey(key, path) as handle:
            return winreg.QueryValueEx(handle, name)[0]
    return ''


HOST = next(
    (v for v in map(setting, ('BUTCHER_VITA_HOST', 'HUNT_VITA_HOST', 'CROW_VITA_HOST', 'DRIFTER_VITA_HOST')) if v), ''
)


def log(message):
    print(time.strftime('[%H:%M:%S] ') + message, flush=True)


def check_lock():
    """Stops unless this session holds the shared vita-device lock: a lock.py run (any port's) this process runs
    under, or T2D_HOLDER naming the holder (lock.ours checks who, not what the holder's text says). Before (Nov 2026)
    any lock whose text held "t2d:" passed, another job's included."""
    sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
    import lock

    lock.require_ours('vita-device', 'va.py')


def ftp():
    connection = ftplib.FTP()
    connection.connect(HOST, 1337, timeout=30)
    connection.login()
    return connection


def command(chain, timeout=10):
    with socket.create_connection((HOST, 1338), timeout=10) as connection:
        connection.sendall((chain + '\n').encode())
        connection.settimeout(timeout)
        out = b''
        try:
            while chunk := connection.recv(4096):
                out += chunk
        except TimeoutError:
            pass
    return out.decode(errors='replace').strip()


@contextlib.contextmanager
def transferring():
    caught = []

    def held(number, _frame):
        caught.append(number)
        if len(caught) > 1:
            raise KeyboardInterrupt
        print('\nva.py: finishing the transfer first (again to cut it short)', file=sys.stderr, flush=True)

    kinds = [signal.SIGINT, *([signal.SIGBREAK] if hasattr(signal, 'SIGBREAK') else [])]
    before = {kind: signal.signal(kind, held) for kind in kinds}
    try:
        yield
    finally:
        for kind, handler in before.items():
            signal.signal(kind, handler)
    if caught:
        raise KeyboardInterrupt


def running_app():
    """perf's "app:" value ('none' when nothing runs), or None when perf has no answer."""
    with contextlib.suppress(OSError):
        command(KEEP_AWAKE, timeout=2)
    out = command('perf', timeout=4)
    found = re.search(r'app:\s*(\S+)', out)
    return found.group(1) if found else None


def makedirs(connection, directory):
    parts = directory.split('/')
    for i in range(2, len(parts) + 1):
        with contextlib.suppress(ftplib.error_perm):
            connection.mkd('/'.join(parts[:i]))


def write(path, data: bytes):
    with ftp() as connection, transferring():
        makedirs(connection, path.rsplit('/', 1)[0])
        connection.storbinary(f'STOR {path}.part', io.BytesIO(data))
        with contextlib.suppress(ftplib.error_perm):
            connection.delete(path)
        connection.rename(f'{path}.part', path)


def listing(connection, folder):
    entries = []
    with contextlib.suppress(ftplib.error_perm):
        connection.retrlines(f'LIST {folder}', entries.append)
    return [line.split(None, 8)[-1] for line in entries if line.strip()]


def fetch(tag):
    out = RUNS / tag
    out.mkdir(parents=True, exist_ok=True)
    with ftp() as connection:
        for name in listing(connection, REMOTE):
            if name.endswith('.txt'):
                buffer = io.BytesIO()
                with transferring():
                    connection.retrbinary(f'RETR {REMOTE}/{name}', buffer.write)
                (out / name).write_bytes(buffer.getvalue())
    log(f'fetched into {out}')
    return out


def read_log():
    buffer = io.BytesIO()
    with ftp() as connection, transferring():
        try:
            connection.retrbinary(f'RETR {REMOTE}/log.txt', buffer.write)
        except ftplib.error_perm:
            return ''
    return buffer.getvalue().decode(errors='replace')


def run(tag, steps, timeout=900):
    app = running_app()
    if app not in (None, 'none', TITLE) and TITLE not in (app or ''):
        sys.exit(f'another app runs on the console ({app}); not touching it')
    if app and TITLE in app:
        log('our app still runs; quitting it')
        command(f'{KEEP_AWAKE}; quit all; wait 2s')
    write(f'{REMOTE}/args.txt', steps.encode())
    write(f'{REMOTE}/log.txt', b'')  # so a launch that writes nothing is not mistaken for the last run's
    log(f'args: {steps}')
    log(command(f'{KEEP_AWAKE}; launch {TITLE}', timeout=6))
    started, seen, last_change, last = time.time(), 0, time.time(), ''
    gone = 0
    while time.time() - started < timeout:
        time.sleep(5)
        with contextlib.suppress(OSError):
            command(KEEP_AWAKE, timeout=2)
        text = read_log()
        lines = text.splitlines()
        for line in lines[seen:]:
            print('   ' + line, flush=True)
        seen = max(seen, len(lines))
        if text != last:
            last, last_change = text, time.time()
        if 'T2D' in text and ' DONE ' in text:
            log('done')
            break
        app = running_app()
        if app is not None and TITLE not in app and time.time() - started > 20:
            gone += 1
            if gone >= 2:
                log(f'the app stopped without DONE (app: {app}); a crash? see va.py crashes')
                break
        if time.time() - last_change > 240:
            log('no log progress for 4 minutes; quitting our app')
            if TITLE in (running_app() or ''):
                command(f'{KEEP_AWAKE}; quit all; wait 2s')
            break
    time.sleep(3)
    app = running_app()
    if app and TITLE in app:
        log('our app still runs after the test; quitting it')
        command(f'{KEEP_AWAKE}; quit all; wait 2s')
    return fetch(tag)


def install(vpk: Path):
    size = vpk.stat().st_size
    names = {line.split()[0] for line in command('help', timeout=4).splitlines() if line.strip()}
    if 'install' not in names:
        sys.exit('vitacompanion on the console has no install command')
    app = running_app()
    if app not in (None, 'none') and TITLE not in app:
        sys.exit(f'another app runs on the console ({app}); not installing over it')
    with socket.create_connection((HOST, 1338), timeout=30) as connection, vpk.open('rb') as handle, transferring():
        connection.sendall(f'install - {size}\n'.encode())
        connection.settimeout(300)
        sent, t0 = 0, time.time()
        while chunk := handle.read(1 << 20):
            connection.sendall(chunk)
            sent += len(chunk)
        log(f'sent {sent >> 20} MB in {time.time() - t0:.0f}s')
        connection.settimeout(1800)
        reply = b''
        while not reply.endswith(b'\n') and (chunk := connection.recv(4096)):
            reply += chunk
    reply = reply.decode(errors='replace').strip()
    log(reply)
    if not reply.startswith('Installed'):
        sys.exit('installation failed')


def main(argv):
    if not HOST:
        sys.exit('no console address (BUTCHER_VITA_HOST, HUNT_VITA_HOST, CROW_VITA_HOST, DRIFTER_VITA_HOST)')
    if not argv:
        sys.exit(__doc__)
    what = argv[0]
    if what != 'crashes':
        check_lock()
    if what == 'state':
        print(command('perf', timeout=4))
    elif what == 'install':
        install(Path(argv[1]) if len(argv) > 1 else DEFAULT_VPK)
    elif what == 'run':
        run(argv[1], argv[2])
    elif what == 'fetch':
        fetch(argv[1])
    elif what == 'crashes':
        with ftp() as connection:
            entries = []
            connection.retrlines('LIST ux0:/data', entries.append)
            for line in entries:
                if line.endswith('.psp2dmp'):
                    print(line)
    else:
        sys.exit(__doc__)


if __name__ == '__main__':
    main(sys.argv[1:])
