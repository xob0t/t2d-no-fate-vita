"""Build the PS Vita player from the translated project without Unity, in one go.

    uv run tools/builder/make.py [--out dir] [--vpk]

Lays out the build folder (package.py), writes the serialized data and streams (build.py), builds
the game code module (il2cpp.py), does what the pipeline does to the editor's build (sorts
globalgamemanagers' lists and sets its build GUID, adds the pipeline's native plugins (none yet) and boot.config's
graphics jobs), and with --vpk packages it like the pipeline's `package` stage.
"""

import argparse
import sys
import time
from pathlib import Path

import il2cpp
import package
import paths
from paths import progress

import build as data


def make(out):
    start = time.time()
    marker = paths.common.build_incomplete(out)
    marker.parent.mkdir(parents=True, exist_ok=True)
    marker.write_text(f'make.py started {time.strftime("%Y-%m-%d %H:%M:%S")}', encoding='utf-8')
    with progress.display():
        with progress.step('Preparing the build folder', timed=True):
            paths.builtin_shaders()
            package.vita_folder(out)
        with progress.step('Reading the project', timed=True):  # its asset index: about 3 minutes the first time
            build = data.Build(out, platform='vita')
        data.write_all(build)
        with progress.step('Code module', timed=True):
            il2cpp.build(out)  # its steps leave their times, or a line saying it reused the module
        pipeline = downgrade_pipeline()
        with progress.step('Making the player data reproducible', timed=True):
            pipeline.reproducible_player_data(out / 'Media')
        pipeline.native_plugins(out)
        pipeline.boot_config(out)
    marker.unlink()  # whole: package and the console tools take it now
    progress.done(f'Built {out} in {time.time() - start:.0f} s')


def downgrade_pipeline():
    sys.path.insert(0, str(paths.ROOT / 'tools' / 'downgrade'))
    import pipeline

    return pipeline


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--out', type=Path, default=paths.OUT)
    parser.add_argument('--vpk', action='store_true', help='also package it as a VPK beside the folder')
    args = parser.parse_args()
    # Under vita-build, as pipeline.py build and package are: no other writer between the .incomplete marker
    # and its removal, and none while the VPK is packed (build_lock).
    with paths.build_lock('builder: make.py'):
        make(args.out)
        if args.vpk:
            downgrade_pipeline().package_vita(args.out)
    return 0


if __name__ == '__main__':
    sys.exit(main())
