"""What the tools show while they work: a bar for each step that takes a while, a line for each thing done.

    with progress.step('Packaging the VPK', total=total, eta=True, count='MB') as step:  # pipeline.py
        for name, source in files:
            step.note(name)                       # shown after the step's name
            ...
            step.advance(size)
    for path in progress.track(files, 'Reading the game materials'):  # a step for a list
        ...
    with progress.following('Unity: building', log):  # a tool writing a log: its last line as the note
        subprocess.run(...)
    progress.run('Compiling the rewriter', [csc, ...], log)  # a tool whose output goes to log, followed

Each step shows a display of its own while it runs; steps inside `with progress.display():` share one,
so the modules don't need to know who called them. The bars only show on an interactive terminal and
are gone when the step ends; the lines, and anything printed meanwhile, stay above them, and are all
that a redirected output gets. Lines are never wrapped, so a
log keeps them whole. A tool run under a step must not write to the terminal itself (its output to a
log file or captured): it would print through the bars.
"""

import contextlib
import datetime
import os
import re
import subprocess
import sys
import threading
import time
from pathlib import Path

from rich.console import Console
from rich.markup import escape
from rich.progress import BarColumn, Progress, ProgressColumn, SpinnerColumn, TextColumn, TimeElapsedColumn
from rich.table import Column
from rich.text import Text

console = Console(highlight=False, soft_wrap=True)
errors = Console(stderr=True, highlight=False, soft_wrap=True)  # for messages outside a display
NOTE_WIDTH = 70  # characters of a log's last line following() shows
POLL_SECONDS = 0.5  # how often following() reads the log
TAIL_BYTES = 4096  # how much of the log's end last_line() reads

_progress = None  # the display shown, if any
NAME_WIDTH = 34  # written(): sharedassets59.assets.resS and Resources/unity_builtin_extra fit


class _Count(ProgressColumn):
    """What the step has done, as its `count` asks: 'done/total', the MB with the speed, or nothing."""

    def render(self, task):
        count = task.fields.get('count')
        if count == 'MB':
            done = f'{task.completed / 2**20:.0f}' + ('' if task.total is None else f'/{task.total / 2**20:.0f}')
            speed = f', {task.speed / 2**20:.1f} MB/s' if task.speed else ''
            return Text(f'{done} MB{speed}', 'progress.download')
        if count == 'items' and task.total is not None:
            return Text(f'{task.completed:.0f}/{task.total:.0f}', 'progress.download')
        return Text('')


class _Left(ProgressColumn):
    """The time left, for a step that asked for it once it has done enough to tell."""

    def render(self, task):
        left = task.time_remaining
        if not task.fields.get('eta') or left is None or not task.completed:
            return Text('')
        return Text(f'{datetime.timedelta(seconds=round(left))} left', 'progress.remaining')


def _columns():
    """One line per step: the name and note give way (cut with …), then the bar. The time left keeps its
    width, so the bars don't move when a step that shows it starts or ends."""
    fixed = Column(no_wrap=True)
    return (
        SpinnerColumn(table_column=fixed),
        TextColumn('{task.description}', table_column=Column(no_wrap=True, overflow='ellipsis', ratio=3)),
        BarColumn(bar_width=None, table_column=Column(min_width=10, max_width=40, ratio=1)),
        _Count(table_column=fixed),
        TimeElapsedColumn(table_column=fixed),
        _Left(table_column=Column(no_wrap=True, min_width=len('0:00:00 left'))),
    )


@contextlib.contextmanager
def display():
    """Show the steps started inside; nested calls share the outer one's display."""
    global _progress
    if _progress is not None:
        yield
        return
    p = Progress(*_columns(), console=console, transient=True, expand=True)
    # Not started without a terminal to draw it on (it would leave an empty line when it stops), or one
    # that can't show its spinner (a forced terminal on a cp1252 stream).
    drawn = console.is_interactive and console.encoding.lower().startswith('utf')
    with p if drawn else contextlib.nullcontext():
        _progress = p
        try:
            yield
        finally:
            _progress = None


class Step:
    def __init__(self, progress, task, name):
        self.progress, self.task, self.name = progress, task, name

    def advance(self, n=1):
        self.progress.advance(self.task, n)

    def more(self, n):
        """n more items to the total (no total yet: n), for a step whose parts each count their own."""
        total = next(t for t in self.progress.tasks if t.id == self.task).total
        self.progress.update(self.task, total=(total or 0) + n)

    def update(self, completed, total=None):
        self.progress.update(self.task, completed=completed, **({} if total is None else {'total': total}))

    def note(self, text):
        """What the step is on, after its name."""
        self.progress.update(self.task, description=f'{self.name} [dim]{escape(str(text))}[/]')


@contextlib.contextmanager
def step(name, total=None, eta=False, count='items', timed=False):
    """A bar for the block (a spinner without a total), removed when it ends. Yields a Step. eta:
    show the time left, for steps whose items take about as long as each other. count: what the bar
    shows it has done: 'items' (done/total), 'MB' (it counts bytes), or None (the total is a measure
    of the work, as the audio's frames). timed: leave a line with the step's name, count and time when
    it ends, for the steps a build is made of (not one per file). A step that fails says so, with its
    time, whether timed or not: the innermost one, the step the build stopped in."""
    with display():
        p = _progress
        task = p.add_task(escape(name), total=total, eta=eta, count=count)
        # A first sample at the start: rich's speed leaves out the first sample's work. (A step whose total
        # comes later, as following()'s with a pattern, starts its samples over then.)
        if eta:
            p.advance(task, 0)
        started = time.monotonic()
        try:
            yield Step(p, task, escape(name))
        except BaseException as error:
            if _failure(error) and not getattr(error, 'progress_step', None):
                error.progress_step = name  # the outer steps leave it to this one
                failed_in(name, time.monotonic() - started)
            raise
        else:
            if timed:
                finished(name, time.monotonic() - started, _done(next(t for t in p.tasks if t.id == task)))
        finally:
            p.remove_task(task)


def _failure(error):
    """Whether an exception leaving a step stops the build with an error (not Ctrl+C, nor sys.exit(0))."""
    if isinstance(error, (KeyboardInterrupt, GeneratorExit)):
        return False
    return not isinstance(error, SystemExit) or error.code not in (None, 0)


def _done(task):
    """What a finished step did, as its bar counted it: '161/161', '334 MB', or ''."""
    count = task.fields.get('count')
    if count == 'MB':
        return f'{task.completed / 2**20:.0f} MB'
    if count == 'items' and task.total is not None:
        return f'{task.completed:.0f}/{task.total:.0f}'
    return ''


def duration(seconds):
    """'4.2 s', '2 min 41 s'."""
    if seconds < 60:
        return f'{seconds:.1f} s'
    minutes, seconds = divmod(round(seconds), 60)
    return f'{minutes} min {seconds:02} s'


def finished(name, seconds, what=''):
    """The line a timed step leaves: its name, what it did, its time."""
    what = (f'  {what}' if what else '', 'progress.download')
    console.print(Text.assemble(name, what, (f'  {duration(seconds)}', 'dim')))


def cached(name, why='its inputs have not changed'):
    """The line for a step skipped because its output is there from the same inputs."""
    console.print(Text.assemble(name, (f'  skipped: {why}', 'dim')))


def failed_in(name, seconds):
    errors.print(Text(f'Failed in: {name} (after {duration(seconds)})', 'bold red'))


def track(items, name, timed=False):
    """The items (a list), with a step for them that advances as each is taken."""
    with step(name, total=len(items), timed=timed) as s:
        for item in items:
            yield item
            s.advance()


@contextlib.contextmanager
def following(name, log, start=0, pattern=None, status=None, timed=False):
    """A step called `name` for a block that waits on a tool writing `log`: its note is the last line
    the log has after byte `start`, read every POLL_SECONDS. pattern: a regular expression whose
    groups are the items done and their total, for a tool that logs them (AssetRipper's export); the
    step then has a bar and the time left, and the note is the rest of the line. status: a function
    of that line giving the note instead, for a tool that logs little while it works (IL2CPP: what
    it has written so far). timed: as step's."""
    with step(name, eta=pattern is not None, timed=timed) as s:
        stop = threading.Event()

        def poll():
            while not stop.wait(POLL_SECONDS):
                with contextlib.suppress(OSError):  # not created yet
                    line = last_line(log, start)
                    found = pattern and re.search(pattern, line)
                    if found:
                        s.update(int(found[1]), int(found[2]))
                        line = line[found.end() :].strip()
                    s.note(status(line) if status else line)

        thread = threading.Thread(target=poll, daemon=True)
        thread.start()
        try:
            yield s
        finally:
            stop.set()
            thread.join()
        if pattern:  # the count the tool got to, for the step's line: the last poll can miss its last items
            with contextlib.suppress(OSError):
                with open(log, 'rb') as f:
                    f.seek(max(start, f.seek(0, os.SEEK_END) - 16 * TAIL_BYTES))
                    lines = f.read().decode('utf-8', errors='replace').splitlines()
                found = next(filter(None, (re.search(pattern, x.strip()) for x in reversed(lines))), None)
                if found:
                    s.update(int(found[1]), int(found[2]))


def run(name, command, log, cwd=None, env=None, check=True, timed=False, status=None):
    """Run a tool under a step called name: its command line, then its output (stdout and stderr), go to
    log, whose last line is the step's note. check: stop on a failure, with the log's last lines.
    timed: as step's; status: as following()'s. Returns the exit code."""
    log = Path(log)
    log.parent.mkdir(parents=True, exist_ok=True)
    command = [str(c) for c in command]
    with open(log, 'w', encoding='utf-8', errors='replace') as out:
        out.write(subprocess.list2cmdline(command) + '\n\n')
        out.flush()
        started = time.monotonic()
        with following(name, log, start=out.tell(), status=status):
            code = subprocess.run(
                command, cwd=cwd, env=env, stdout=out, stderr=subprocess.STDOUT, check=False
            ).returncode
    if timed and not (code and check):
        finished(name, time.monotonic() - started, f'exit code {code}' if code else '')
    if code and check:
        tail = log.read_text(encoding='utf-8', errors='replace').splitlines()[-15:]
        fail(f'{name} failed (exit code {code}); the end of {log}:\n' + '\n'.join(tail))
    return code


def last_line(log, start=0):
    """The last line written to log after byte `start`, cut to NOTE_WIDTH."""
    with open(log, 'rb') as f:
        end = f.seek(0, os.SEEK_END)
        at = max(start, end - TAIL_BYTES)
        f.seek(max(at - 1, start))
        within = at > start and f.read(1) != b'\n'  # the read starts within a line
        lines = f.read().decode('utf-8', errors='replace').splitlines()
    if within and any(x.strip() for x in lines[1:]):
        lines = lines[1:]
    line = next((x.strip() for x in reversed(lines) if x.strip()), '')
    return line if len(line) <= NOTE_WIDTH else line[: NOTE_WIDTH - 3] + '...'


def done(text):
    console.print(Text(text, 'bold green'))


def fail(text):
    """Stop with text, in red, on stderr (as sys.exit(text) would, without the color)."""
    errors.print(Text(text, 'bold red'))
    sys.exit(1)


def written(name, size, seconds=None, note='', missing=None):
    """The line for a file the build wrote: its size, how long it took, the --compare result and
    the classes it lacks ({class ID: objects}) because they cannot be built yet."""
    line = Text(f'{name:<{NAME_WIDTH}} {size:>12,} bytes')
    if seconds is not None:
        line.append(f'  {seconds:6.1f} s', 'dim')
    elif note or missing:
        line.append(' ' * 10)  # where the time would be, so the notes line up
    if note:
        style = 'green' if note == 'identical' else 'yellow'
        line.append(f'  {note}', style)
    if missing:
        line.append(f'  not built yet: {dict(missing)}', 'red')
    console.print(line)
