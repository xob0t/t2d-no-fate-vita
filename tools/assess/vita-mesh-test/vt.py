"""Drive the PS Vita mesh test app (title TDMS00001) on the shared console.

    python -I tools/assess/vita-mesh-test/vt.py <command> ...

The texture test's vt.py (tools/assess/vita-texture-test) with this app's title, folder (ux0:data/t2dmesh), VPK and
runs folder (work/vita-mesh-test/runs). Same commands: state, install [vpk], run <tag> "<steps>", fetch <tag>, clean,
crashes. Run every command that touches the console inside butcher-vita's lock:

    python <butcher-vita>/tools/downgrade/lock.py run vita-device "t2d: mesh test" --wait 1800 -- \\
        python -I tools/assess/vita-mesh-test/vt.py run c1 "create game 10000,50000,100000,150000 touch"
"""

import importlib.util
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]

spec = importlib.util.spec_from_file_location('vt_texture', HERE.parent / 'vita-texture-test' / 'vt.py')
vt = importlib.util.module_from_spec(spec)
spec.loader.exec_module(vt)
vt.TITLE = 'TDMS00001'
vt.REMOTE = 'ux0:/data/t2dmesh'
vt.RUNS = ROOT / 'work' / 'vita-mesh-test' / 'runs'
vt.DEFAULT_VPK = ROOT / 'work' / 'vita-mesh-test' / 't2d-mesh-test.vpk'
vt.__doc__ = __doc__

if __name__ == '__main__':
    vt.main(sys.argv[1:])
