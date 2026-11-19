"""Re-run an il2cpp Bee build's C++ compile and link steps with another MSVC toolset.

    python -I tools/assess/sigtest/replay_msvc.py <cache dir> <old MSVC dir> <new MSVC dir> [--jobs N]

Bee picks the newest MSVC toolset of the Visual Studio installs that vswhere reports, and has no setting to
point it elsewhere. To build with a toolset that isn't installed (a portable copy, e.g. MSVC 14.50 from
Microsoft's VS package manifest), run il2cpp once with the installed one, keep its --cachedirectory, then run
this: it reads <cache>/buildstate/bee.dag.json, replaces the old toolset folder with the new one in every
compile and link command, in their PATH and in the link response files, and runs the compile steps (precompiled
headers first) and then the link, in the dag's dependency order. The generated C++, the lumps and the response
files Bee already wrote are reused. Outputs overwrite the cache's objects and GameAssembly.dll/.pdb in place.

The toolset folders are the ones holding bin/, include/ and lib/ (…/VC/Tools/MSVC/<version>).
"""
import argparse
import concurrent.futures
import json
import os
import re
import subprocess
import sys
from pathlib import Path


def variants(path):
    p = str(Path(path))
    return [p, p.replace('\\', '/')]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('cache')
    parser.add_argument('old')
    parser.add_argument('new')
    parser.add_argument('--jobs', type=int, default=os.cpu_count())
    args = parser.parse_args()
    cache = Path(args.cache)
    dag = json.load(open(cache / 'buildstate/bee.dag.json', encoding='utf-8'))
    nodes = dag['Nodes']
    pairs = list(zip(variants(args.old), variants(args.new)))

    def swap(text):
        for old, new in pairs:
            text = re.sub(re.escape(old), new.replace('\\', '\\\\'), text, flags=re.IGNORECASE)
        return text

    wanted = {i for i, n in enumerate(nodes) if n.get('Annotation', '').split(' ')[0].startswith(('C_Win', 'Link_Win'))}
    if not wanted:
        sys.exit('no compile or link actions in the dag')
    for i in wanted:
        for m in re.finditer(r'@"([^"]+\.rsp)"', nodes[i]['Action']):
            rsp = Path(m.group(1))
            raw = rsp.read_bytes()
            encoding = 'utf-8-sig' if raw.startswith(b'\xef\xbb\xbf') else 'utf-8'
            text = raw.decode(encoding)
            rsp.write_text(swap(text), encoding=encoding, newline='')
    print(f'{len(wanted)} actions to run', flush=True)

    base_env = {k: v for k, v in os.environ.items()}

    def run(i):
        node = nodes[i]
        env = dict(base_env)
        for item in node.get('Env', []):
            env[item['Key']] = swap(item['Value'])
        command = swap(node['Action'])
        result = subprocess.run(command, env=env, capture_output=True, text=True, errors='replace')
        if result.returncode != 0:
            return i, f'{node["DisplayName"]}: exit {result.returncode}\n{result.stdout[-3000:]}{result.stderr[-2000:]}'
        return i, None

    done, failed = set(), []
    pending = set(wanted)
    with concurrent.futures.ThreadPoolExecutor(args.jobs) as pool:
        running = {}
        while pending or running:
            for i in sorted(pending):
                deps = set(nodes[i].get('ToBuildDependencies', [])) & wanted
                if deps <= done and len(running) < args.jobs:
                    running[pool.submit(run, i)] = i
                    pending.discard(i)
            if not running:
                sys.exit(f'stuck: {len(pending)} actions wait on failed ones')
            finished, _ = concurrent.futures.wait(running, return_when=concurrent.futures.FIRST_COMPLETED)
            for future in finished:
                i, error = future.result()
                del running[future]
                if error:
                    failed.append(error)
                    print(error, flush=True)
                    pending.clear()
                else:
                    done.add(i)
                    if len(done) % 50 == 0 or nodes[i]['Annotation'].startswith('Link'):
                        print(f'{len(done)}/{len(wanted)} {nodes[i]["DisplayName"]}', flush=True)
    if failed:
        sys.exit(f'{len(failed)} actions failed')
    print('done', flush=True)


if __name__ == '__main__':
    main()
