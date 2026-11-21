"""Build Terminator 2D: NO FATE for the PS Vita from your copy of the game: set up, then run the pipeline's
stages in order.

    uv run build.py                      # the VPK: builds/t2d-2018.2.19f1-vita.vpk
    uv run build.py --game <folder>      # the game somewhere other than Steam's default folder
    uv run build.py --dry-run            # list the stages, run nothing

Runs tools/setup.py (it installs what is missing and skips what is there), then
tools/downgrade/pipeline.py: recover (skipped while Rewind's output matches the game, Rewind and its
signatures), rip (skipped while the export matches), project, build and package. The stages run under the
vita-build lock (tools/downgrade/lock.py), waiting for another job's pipeline to finish first.
docs/build.md describes the requirements, the times and the other options. From butcher-vita's build.py
(MIT, same author).

The port doesn't play yet: the recovered code still has gaps (docs/build.md, "Where it stands").
"""

import argparse
import os
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / 'tools' / 'downgrade'))
import progress  # noqa: E402
from common import TARGET, WORK, default_game, registry_env, root_too_long  # noqa: E402

PIPELINE = ROOT / 'tools' / 'downgrade' / 'pipeline.py'
LOCK = ROOT / 'tools' / 'downgrade' / 'lock.py'
# code is no stage of its own here: project runs it (pipeline.assembly_overlay); --from code starts at project.
STAGES = ('recover', 'rip', 'code', 'project', 'build', 'package')
# What each stage does, its steps in order (the progress lines name them as they run), for --dry-run.
STEPS = {
    'setup': 'installs what is missing, checks the rest',
    'recover': 'Rewind decompiles GameAssembly.dll',
    'rip': 'copies the game into the Mono layout; AssetRipper loads it and exports the project',
    'project': 'code (compiles the C# tools, rewrites the assemblies); the asset rules (the atlas repack); the sync',
    'build': 'reads the project; the data files (audio, textures, shaders); the code module (UnityLinker, IL2CPP: '
    'C++ conversion, psp2snc, psp2ld)',
    'package': 'the LiveArea art; the VPK',
}
RETRY = 'Fix what it says above and run uv run build.py again: it picks up where it stopped.'
# lock.py run's exit code when the lock stayed held.
LOCK_NOT_TAKEN = 75


def environment():
    """This process's variables, with what setup has just set for the user winning over an older value
    this shell started with."""
    env = dict(os.environ)
    for name in ('SCE_PSP2_SDK_DIR', 'T2D_GAME'):
        value = registry_env(name, 'user')
        if value:
            env[name] = value
    return env


def run(failed, command, env=None, times=None, stage=None):
    """Run a command; stop with failed (what to tell the user) when it fails. times: {stage: seconds, a note,
    or ('failed', seconds)}, the summary's, which gets the command's time under stage (and is shown when it
    fails)."""
    started = time.monotonic()
    code = subprocess.run([str(c) for c in command], env=env or environment(), check=False).returncode
    seconds = time.monotonic() - started
    if times is not None:
        times[stage] = seconds if not code else ('failed', seconds)
    if code:
        if times:
            summary(times)
        progress.fail(f'\nbuild.py: {failed} (exit code {code}, after {progress.duration(seconds)}).')


def summary(times):
    """The stages' times, the skipped ones with why."""
    progress.console.print('\nStages:')
    for stage in sorted(times, key=('setup', *STAGES).index):
        value = times[stage]
        if isinstance(value, tuple):
            value = f'failed after {progress.duration(value[1])}'
        shown = progress.duration(value) if isinstance(value, float) else value
        progress.console.print(f'  {stage:8} {shown}')
    total = sum(v[1] if isinstance(v, tuple) else v for v in times.values() if not isinstance(v, str))
    progress.console.print(f'  {"total":8} {progress.duration(total)}')


def plan(args):
    """[(stage, pipeline.py arguments, what it makes)] in order, from --from on."""
    game = ['--game', args.game] if args.game else []
    steps = []
    if not recover_current(args):
        steps.append(('recover', ['recover', *game], 'work/recover/assemblies'))
    if not rip_current(args):
        steps.append(('rip', ['rip', *game], f'work/rip-{TARGET}'))
    steps.append(('project', ['project', *game], f'work/t2d-{TARGET}-vita'))
    steps.append(('build', ['build', *game], f'builds/t2d-{TARGET}-vita'))
    steps.append(('package', ['package', *game], f'builds/t2d-{TARGET}-vita.vpk'))
    first = STAGES.index(args.start)
    return [step for step in steps if STAGES.index(step[0]) >= first]


def skipped(args):
    """{stage: why} for the stages plan() leaves out, in order, and code (which project runs)."""
    out = {}
    for stage in ('recover', 'rip'):
        if STAGES.index(stage) < STAGES.index(args.start):
            out[stage] = 'skipped: before --from ' + args.start
        elif stage == 'recover' and recover_current(args):
            out[stage] = "skipped: current (Rewind's output is from this game, Rewind and signatures)"
        elif stage == 'rip' and rip_current(args):
            out[stage] = 'skipped: current (the export is from this game, recovered code and AssetRipper)'
    if STAGES.index(args.start) <= STAGES.index('project'):
        out['code'] = 'runs in project'
    return out


def recover_current(args):
    """Whether work/recover is Rewind's output for this game, Rewind build and signature library; the
    recover stage itself always runs again."""
    if args.start != 'recover':
        return True
    import pipeline

    return pipeline.recover_is_current(Path(args.game) if args.game else default_game())


def rip_current(args):
    """Whether work/rip-<version> is the export of this game and recovered code with this AssetRipper and its
    settings (pipeline.rip_is_current); the rip stage itself always exports again."""
    if args.start not in ('recover', 'rip'):
        return True
    import pipeline

    if not (WORK / f'rip-{TARGET}' / pipeline.RIP_STAMP).exists():  # no export yet
        return False
    if not recover_current(args):  # the export would be of the old code
        return False
    game = Path(args.game) if args.game else default_game()
    return pipeline.rip_is_current(game, WORK / f'rip-{TARGET}', TARGET)


def build(args, times=None):
    """The pipeline's stages, in this process, under the build lock. times: the summary's so far (setup's)."""
    steps = plan(args)
    if not steps:
        progress.done('Nothing to run from that stage.')
        return
    times = {**(times or {}), **skipped(args)}
    for stage, why in times.items():
        if str(why).startswith('skipped: current'):
            progress.console.print(f'\nbuild.py: {stage} {why}')
    env = environment()
    for number, (stage, arguments, output) in enumerate(steps, 1):
        progress.console.print(
            f'\nbuild.py: pipeline.py {" ".join(map(str, arguments))}  ->  {output}  (stage {number} of {len(steps)})'
        )
        run(f'pipeline.py {stage} failed. {RETRY}', [sys.executable, PIPELINE, *arguments], env, times, stage)
    summary(times)
    made = [ROOT / output for _, _, output in steps if output.startswith('builds/')]
    progress.done('\nDone:\n' + '\n'.join(f'  {path}' for path in made))


def dry_run(args, setup):
    """Every stage with what it does, the skipped ones with why."""
    print('under the vita-build lock:')
    if not args.skip_setup:
        print('  setup    ' + ' '.join(map(str, ['uv run tools/setup.py', *setup[2:]])))
        print(f'           {STEPS["setup"]}')
    steps = {stage: (arguments, output) for stage, arguments, output in plan(args)}
    left_out = skipped(args)
    for stage in STAGES:
        if stage in steps:
            arguments, output = steps[stage]
            print(f'  {stage:8} uv run tools/downgrade/pipeline.py {" ".join(map(str, arguments))}  ->  {output}')
            print(f'           {STEPS[stage]}')
        elif stage in left_out:
            print(f'  {stage:8} {left_out[stage]}')


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--game', metavar='FOLDER', help="the game, if not in Steam's default folder (or T2D_GAME)")
    parser.add_argument('--vita-sdk', metavar='PATH', help='your own PS Vita SDK, an archive or a folder')
    parser.add_argument('--skip-setup', action='store_true', help='go straight to the pipeline')
    parser.add_argument(
        '--from',
        dest='start',
        choices=STAGES,
        default='recover',
        help='start at this stage (the earlier ones are done); recover and rip are skipped anyway while current',
    )
    parser.add_argument(
        '--wait',
        type=float,
        default=6 * 3600,
        metavar='SECONDS',
        help="how long to wait for another job's pipeline (the build lock); default 6 hours",
    )
    parser.add_argument('--dry-run', action='store_true', help='list the setup command and the stages, run nothing')
    parser.add_argument('--locked', action='store_true', help=argparse.SUPPRESS)  # build.py under lock.py run
    args = parser.parse_args()
    # First, before setup's downloads: from a deeper folder the build fails at IL2CPP, 20 minutes in.
    if problem := root_too_long():
        progress.fail(f'build.py: {problem}')
    setup = [sys.executable, ROOT / 'tools' / 'setup.py', '--then-build']
    setup += ['--game', args.game] if args.game else []
    setup += ['--vita-sdk', args.vita_sdk] if args.vita_sdk else []
    # First: a dry run runs nothing, also in a shell that holds the build lock (T2D_LOCK), where build() would run
    # the stages (before Nov 2026 it did).
    if args.dry_run:
        dry_run(args, setup)
        return
    if args.locked or os.environ.get('T2D_LOCK'):
        # Setup under the build lock too: two builds' setups both cloned or checked out Rewind's checkout, and a
        # setup changed it under another job's pipeline.
        times = {}
        if args.locked and not args.skip_setup:
            run(f'setup stopped. {RETRY}', setup, times=times, stage='setup')
        build(args, times)
        return

    # Setup and the stages under one build lock, so no other job's pipeline runs between them. lock.py run waits for
    # the lock and releases it however build.py ends.
    rerun = [sys.executable, ROOT / 'build.py', '--locked', *sys.argv[1:]]
    command = [sys.executable, LOCK, 'run', 'vita-build', 'build.py', '--wait', str(args.wait), '--', *rerun]
    code = subprocess.run([str(c) for c in command], env=environment(), check=False).returncode
    if code == LOCK_NOT_TAKEN:
        progress.fail(f'\nbuild.py: another job held the vita-build lock for {args.wait:.0f} s; try again later.')
    sys.exit(code)


if __name__ == '__main__':
    try:
        main()
    except KeyboardInterrupt:
        progress.fail('\nStopped. Run uv run build.py again to continue.')
