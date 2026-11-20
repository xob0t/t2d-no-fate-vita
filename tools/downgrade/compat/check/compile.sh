#!/bin/sh
# Compiles T2DCompat's sources (tools/downgrade/compat/*.cs) as the pipeline does (pipeline.engine_library: C# 5,
# -nostdlib, unityaot's mscorlib, System and System.Core, UnityEngine's CoreModule and the modules T2DCompat uses), with
# the .NET 10 SDK's Roslyn at -langversion:5, once per player (with and without T2D_VITA). With extra .cs files after
# the output folder (the pipeline's Compat2018.cs and Unity6Shims.cs), it compiles them in too, to catch name clashes.
# Run from the repository root (Git Bash):
#   tools/downgrade/compat/check/compile.sh <out dir> [extra.cs ...]
set -e
OUT=${1:?usage: compile.sh <out dir> [extra.cs ...]}; shift
ROOT=$(cd "$(dirname "$0")/../../../.." && pwd)
# The main checkout holds tools/external (a git worktree has none): the first entry of `git worktree list`.
MAIN=${T2D_MAIN:-$(git -C "$ROOT" worktree list --porcelain | sed -n '1s/^worktree //p')}
DOTNET=${DOTNET:-$MAIN/tools/external/dotnet10/dotnet.exe}
# The Unity 2018.2.19f1 editor's Data folder, where this repository's setup puts it (pipeline.editor_data).
U2018=${U2018:-$MAIN/tools/external/unity-2018.2.19f1/Editor/Data}
[ -f "$U2018/Managed/UnityEngine/UnityEngine.CoreModule.dll" ] || { echo "compile.sh: no Unity 2018.2.19f1 editor at $U2018 (uv run tools/setup.py, or set U2018)" >&2; exit 1; }
[ -f "$DOTNET" ] || { echo "compile.sh: no .NET 10 SDK at $DOTNET (uv run tools/setup.py, or set DOTNET)" >&2; exit 1; }
CSC=$(ls -d "$(dirname "$DOTNET")"/sdk/*/Roslyn/bincore/csc.dll | sort | tail -1)
AOT=$U2018/MonoBleedingEdge/lib/mono/unityaot
ENGINE=$U2018/Managed/UnityEngine
mkdir -p "$OUT"
w() { cygpath -w "$1"; }
for platform in vita windows; do
  define=""; [ $platform = vita ] && define="-define:T2D_VITA"
  {
    echo "-nologo -target:library -nostdlib+ -unsafe -langversion:5 -deterministic $define"
    for lib in mscorlib System System.Core; do echo "-r:\"$(w "$AOT/$lib.dll")\""; done
    for m in CoreModule AudioModule ImageConversionModule IMGUIModule TextRenderingModule UIModule UnityWebRequestModule UnityWebRequestAudioModule JSONSerializeModule; do
      echo "-r:\"$(w "$ENGINE/UnityEngine.$m.dll")\""
    done
    echo "-out:\"$(w "$OUT/$platform/T2DCompat.dll")\""
    for f in "$ROOT"/tools/downgrade/compat/*.cs "$@"; do echo "\"$(w "$f")\""; done
  } > "$OUT/$platform.rsp"
  mkdir -p "$OUT/$platform"
  "$DOTNET" "$CSC" "@$(w "$OUT/$platform.rsp")"
  echo "compiled $OUT/$platform/T2DCompat.dll"
done
