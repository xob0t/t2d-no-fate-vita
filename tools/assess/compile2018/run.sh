#!/bin/sh
# Compile Rewind's C# for Steamworks, InControl and Assembly-CSharp against Unity 2018.2 and classify
# the errors. Run from the repository root (Git Bash).
#
#   tools/assess/compile2018/run.sh pass1 <src dir> <out dir>
#       Rewind's source as it is, with only the __Rewind stub.
#   tools/assess/compile2018/run.sh pass2 <src dir> <out dir>
#       mechfix.py's text rules into <out dir>/src, the Compat2018 shim, then its Roslyn-driven
#       rules (opaque values used as objects), compiled again.
#   tools/assess/compile2018/run.sh emit <pass2 out dir> <out dir>
#       RoslynDiag --stub-errors on pass 2's sources: members with errors left get a `throw null` body,
#       and the rest is emitted as DLLs (Steamworks, InControl, then Assembly-CSharp against them);
#       ILCompare then compares each with Rewind's assembly (REWIND_ASM, default
#       work/compile2018/rewind73/assemblies) method by method.
#
# Each compile writes <name>.rsp and, through RoslynDiag (every error, not just csc's first round),
# <name>.diag.tsv; classify.py writes classified.txt and classified.tsv.

MODE=$1; SRC=$2; OUT=$3
T=tools/assess/compile2018
DOTNET=tools/external/dotnet10/dotnet.exe
RD="$DOTNET work/bin/RoslynDiag/RoslynDiag.dll"
mkdir -p "$OUT"
[ -f work/bin/RoslynDiag/RoslynDiag.dll ] || $DOTNET build -c Release $T/RoslynDiag -o work/bin/RoslynDiag

compile_all() {  # $1 source root, extra args for compile.py follow
  S=$1; shift
  python -I $T/compile.py --name Steamworks --src "$S/Steamworks" --stub "$@" --out "$OUT"
  python -I $T/compile.py --name InControl --src "$S/InControl" --stub "$@" --out "$OUT"
  python -I $T/compile.py --name Assembly-CSharp --src "$S/Assembly-CSharp" --stub "$@" --out "$OUT"
  for a in Steamworks InControl Assembly-CSharp; do mv -f "$OUT/$a.log" "$OUT/$a.csc.log"; done
  $RD "$OUT/Steamworks.rsp" "$OUT/Steamworks.diag.tsv"
  $RD "$OUT/InControl.rsp" "$OUT/InControl.diag.tsv"
  $RD "$OUT/Assembly-CSharp.rsp" "$OUT/Assembly-CSharp.diag.tsv" --dep "$OUT/Steamworks.rsp" --dep "$OUT/InControl.rsp"
}

classify() {  # $1 source root
  python -I $T/classify.py --apigap work/apigap --libsrc "$1/InControl" --libsrc "$1/Steamworks" \
    --out "$OUT/classified.tsv" Steamworks="$OUT/Steamworks.diag.tsv" InControl="$OUT/InControl.diag.tsv" \
    Assembly-CSharp="$OUT/Assembly-CSharp.diag.tsv" > "$OUT/classified.txt"
}

case $MODE in
pass1)
  compile_all "$SRC"
  classify "$SRC"
  ;;
pass2)
  python -I $T/mechfix.py text "$SRC" "$OUT/src" --libs InControl,Steamworks > "$OUT/mechfix-text.txt"
  python -I $T/gen_injected.py "$OUT/Compat2018.Injected.cs" "$OUT/src" work/apigap/*.tsv
  python -I $T/compile.py --name Compat2018 --src $T/Compat2018 --src "$OUT/Compat2018.Injected.cs" --out "$OUT/bin"
  EXTRA="--src $T/RewindStubPass2.cs --ref $OUT/bin/Compat2018.dll"
  compile_all "$OUT/src" $EXTRA
  python -I $T/mechfix.py diag "$OUT/src" "$OUT"/*.diag.tsv > "$OUT/mechfix-diag.txt"
  mkdir -p "$OUT/before-diag"
  mv -f "$OUT"/*.diag.tsv "$OUT/before-diag/"
  compile_all "$OUT/src" $EXTRA
  classify "$OUT/src"
  ;;
emit)
  for a in Steamworks InControl; do
    $RD "$SRC/$a.rsp" "$OUT/$a.diag.tsv" --stub-errors "$OUT/$a.stubbed.tsv" --emit "$OUT/$a.dll"
  done
  $RD "$SRC/Assembly-CSharp.rsp" "$OUT/Assembly-CSharp.diag.tsv" --depdll "$OUT/Steamworks.dll" --depdll "$OUT/InControl.dll"     --stub-errors "$OUT/Assembly-CSharp.stubbed.tsv" --emit "$OUT/Assembly-CSharp.dll"
  [ -f work/bin/ILCompare/ILCompare.dll ] || $DOTNET build -c Release $T/ILCompare -o work/bin/ILCompare --source https://api.nuget.org/v3/index.json
  for a in Steamworks InControl Assembly-CSharp; do
    $DOTNET work/bin/ILCompare/ILCompare.dll "${REWIND_ASM:-work/compile2018/rewind73/assemblies}/$a.dll" "$OUT/$a.dll" "$OUT/$a.ilcompare.tsv" | tee "$OUT/$a.ilcompare.txt"
  done
  exit 0
  ;;
*) echo "usage: run.sh pass1|pass2|emit <src dir> <out dir>"; exit 2 ;;
esac
grep -E '^== ' "$OUT/classified.txt"
