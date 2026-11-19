#!/bin/sh
# Builds PlatformCheck.exe (check/*.cs with the platform pass, compat/rewriter/*.cs) as butcher-vita builds its
# rewriter: the .NET Framework's csc (C# 5) and Mono.Cecil 0.10.0-beta6, which it copies beside the exe. Then, with
# T2DCompat compiled by compile.sh, runs the rule check against Rewind's assemblies. Run from the repository root:
#   tools/downgrade/compat/check/build.sh <out dir> [<Rewind's assemblies dir>]
# The defaults are the main checkout's work/platform, and the recovered assemblies of this checkout's pipeline
# (work/recover/assemblies, pipeline.py recover), else the assessment's (the main checkout's work/rewind-steam).
set -e
ROOT=$(cd "$(dirname "$0")/../../../.." && pwd)
MAIN=${T2D_MAIN:-$(git -C "$ROOT" worktree list --porcelain | sed -n '1s/^worktree //p')}
OUT=${1:-$MAIN/work/platform}
REWIND=${2:-$ROOT/work/recover/assemblies}
[ -f "$REWIND/Assembly-CSharp.dll" ] || REWIND=$MAIN/work/rewind-steam/out/assemblies
CECIL=${CECIL:-$MAIN/../huntdown-vita/tools/external/mono.cecil-0.10.0-beta6/Mono.Cecil.dll}
CSC=/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe
w() { cygpath -w "$1"; }
mkdir -p "$OUT/bin"
cp "$CECIL" "$OUT/bin/Mono.Cecil.dll"
"$CSC" -nologo -langversion:5 -out:"$(w "$OUT/bin/PlatformCheck.exe")" -r:"$(w "$OUT/bin/Mono.Cecil.dll")" \
  $(for f in "$ROOT"/tools/downgrade/compat/check/*.cs "$ROOT"/tools/downgrade/compat/rewriter/*.cs; do w "$f"; done)
"$ROOT/tools/downgrade/compat/check/compile.sh" "$OUT/compat"
for platform in vita windows; do
  echo "=== rules against T2DCompat for $platform"
  "$OUT/bin/PlatformCheck.exe" rules "$(w "$REWIND")" "$(w "$OUT/compat/$platform/T2DCompat.dll")" "$(w "$OUT/patched-$platform")"
done
