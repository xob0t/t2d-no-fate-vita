#!/bin/sh
# One probe iteration: emit the DLLs from the probe sources, assemble the project, build the player,
# run it on the hidden desktop and summarize its log. Run from the repository root.
#   tools/assess/bringup/cycle.sh <run name> [seconds, default 40] [extra player args...]
# Expects work/bringup/{pass2,probe-src,empty2018,fielddata} (see the bring-up notes).
set -e
NAME=$1; SECS=${2:-40}; shift; [ $# -gt 0 ] && shift
P=work/bringup
B=tools/assess/bringup
sh $B/emit.sh $P/pass2 $P/probe-src $P/emit > $P/logs/emit-$NAME.log 2>&1 || { tail -20 $P/logs/emit-$NAME.log; exit 1; }
grep -E "members stubbed|error" $P/logs/emit-$NAME.log | head
python -I $B/assemble.py work/assetripper/export-rewind/ExportedProject $P/emit $P/empty2018 $P/project --probe-scripts $B/probe
sh $B/build.sh $P/project $P/player/T2DProbe.exe $P/logs/build-$NAME.log | grep -E "PROBE_BUILD |error CS|unity exit"
export T2D_PROBE_FIELDDATA="$(cygpath -aw $P/fielddata)"
python -I $B/run_player.py $P/player/T2DProbe.exe $P/logs/run-$NAME.log --timeout $((SECS + 20)) -- \
  -probe-quit $SECS -probe-shots "$(cygpath -aw $P/shots/$NAME)" "$@" || true
python -I $B/summarize.py $P/logs/run-$NAME.log
