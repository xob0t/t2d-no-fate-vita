#!/bin/sh
# Builds the repair engine with its rules as huntdown builds its rewriter: the .NET Framework's csc
# (C# 5) and Mono.Cecil 0.10.0-beta6. Run from the repository root (Git Bash).
#   tools/downgrade/repairs/build.sh <Mono.Cecil.dll> [<out dir>]
# The .NET Framework's csc reads a relative path with forward slashes as options, so every path
# goes to it absolute, in Windows form.
set -e
R=tools/downgrade/repairs
CECIL=$1
OUT=${2:-work/bin/Repairs}
mkdir -p "$OUT"
cp "$CECIL" "$OUT/"
SRC=$(for f in $R/Engine.cs $R/Edit.cs $R/IL.cs $R/Support.cs $R/RepairsMain.cs $R/rules/*.cs; do cygpath -wa "$f"; done)
/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe -nologo -langversion:5 -debug:pdbonly \
  -out:"$(cygpath -wa "$OUT/Repairs.exe")" -r:"$(cygpath -wa "$CECIL")" $SRC
