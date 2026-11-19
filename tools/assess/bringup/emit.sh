#!/bin/sh
# Emit runnable probe DLLs (Steamworks, InControl, Assembly-CSharp, Compat2018) from compile2018's
# pass 2. Run from the repository root (Git Bash).
#
#   tools/assess/bringup/emit.sh <pass2 dir> <probe src dir> <out dir> [extra .cs for Assembly-CSharp...]
#
# <probe src dir> is a copy of <pass2 dir>/src (made on the first run) where the probe's local hacks
# go. Compared with compile2018's emit:
#  - the __Rewind stub is RewindStubThrow.cs + RewindStubPass2Probe.cs: a gap marker that runs throws
#    with its text and logs REWIND_GAP once; the pass-2 stand-ins log REWIND_SILENT once;
#  - the members still not compiling are stubbed with `throw new NotSupportedException("COMPILE_STUB
#    <type>.<member>")` (RoslynDiag --stub-marker 1);
#  - Compat2018 is Compat2018Runtime/ (working engine plumbing and *_Injected bodies).
set -e
PASS2=$1; PSRC=$2; OUT=$3; shift 3
B=tools/assess/bringup
T=tools/assess/compile2018
DOTNET=tools/external/dotnet10/dotnet.exe
RD="$DOTNET work/bin/RoslynDiag/RoslynDiag.dll"
mkdir -p "$OUT/rsp"
if [ ! -d "$PSRC" ]; then cp -r "$PASS2/src" "$PSRC"; echo "copied $PASS2/src to $PSRC"; fi

python -I $T/compile.py --name Compat2018 --src $B/Compat2018Runtime --out "$OUT"

win() { cygpath -w "$(realpath "$1")"; }
W_PASS2_SRC="$(win "$PASS2/src")\\"
W_PSRC="$(win "$PSRC")\\"
W_OUT="$(win "$OUT")"
W_COMPAT2="$(win "$PASS2/bin/Compat2018.dll")"
W_STUB="$(win $T/RewindStub.cs)"
W_STUB2="$(win $T/RewindStubPass2.cs)"
W_THROW="$(win $B/RewindStubThrow.cs)"
W_THROW2="$(win $B/RewindStubPass2Probe.cs)"

for a in Steamworks InControl Assembly-CSharp; do
  python -I - "$PASS2/$a.rsp" "$OUT/rsp/$a.rsp" "$W_PASS2_SRC" "$W_PSRC" "$W_COMPAT2" "$W_OUT\\Compat2018.dll" \
      "$W_STUB" "$W_THROW" "$W_STUB2" "$W_THROW2" "$W_OUT\\$a.dll" "$@" <<'EOF'
import os, subprocess, sys
src, dst, old_src, new_src, old_compat, new_compat, stub, throw, stub2, throw2, out = sys.argv[1:12]
extra = sys.argv[12:] if os.path.basename(dst) == 'Assembly-CSharp.rsp' else []
lines = []
for line in open(src, encoding='utf-8').read().splitlines():
    if line.startswith('-out:'):
        line = '-out:' + out
    line = line.replace(old_src, new_src).replace(old_compat, new_compat)
    line = line.replace(stub2, throw2).replace(stub, throw)
    lines.append(line)
for e in extra:
    lines.append('"%s"' % subprocess.check_output(['cygpath', '-w', os.path.abspath(e)], text=True).strip())
open(dst, 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
EOF
done

for a in Steamworks InControl; do
  $RD "$OUT/rsp/$a.rsp" "$OUT/$a.diag.tsv" --stub-errors "$OUT/$a.stubbed.tsv" --stub-marker 1 --emit "$OUT/$a.dll"
done
$RD "$OUT/rsp/Assembly-CSharp.rsp" "$OUT/Assembly-CSharp.diag.tsv" --depdll "$OUT/Steamworks.dll" --depdll "$OUT/InControl.dll" \
  --stub-errors "$OUT/Assembly-CSharp.stubbed.tsv" --stub-marker 1 --emit "$OUT/Assembly-CSharp.dll"
for a in Steamworks InControl Assembly-CSharp; do
  echo "$a: $(($(wc -l < "$OUT/$a.stubbed.tsv") - 1)) members stubbed"
done
