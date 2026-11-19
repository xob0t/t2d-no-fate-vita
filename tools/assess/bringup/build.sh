#!/bin/sh
# Build the probe project's Windows x64 Mono player with Unity 2018.2.19f1 in batch mode.
#   tools/assess/bringup/build.sh <project> <player exe> <log>
# Prints the PROBE_BUILD lines and errors from the editor log; exits with the editor's code.
PROJECT=$1; EXE=$2; LOG=$3
UNITY="$LOCALAPPDATA/Unity/Editors/2018.2.19f1/Editor/Unity.exe"
mkdir -p "$(dirname "$EXE")" "$(dirname "$LOG")"
rm -rf "$PROJECT/Temp"
timeout 3600 "$UNITY" -batchmode -nographics -projectPath "$(cygpath -aw "$PROJECT")" -executeMethod ProbeBuild.Build \
  -buildOutput "$(cygpath -aw "$EXE")" -buildTarget Win64 -logFile "$(cygpath -aw "$LOG")"
code=$?
grep -E "PROBE_BUILD|error CS|Aborting batchmode|Scripts have compiler errors" "$LOG" | head -40
echo "unity exit $code"
exit $code
