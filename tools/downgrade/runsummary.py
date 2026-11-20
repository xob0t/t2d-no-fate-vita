"""Summarise a console run's log (work/runs/<tag>/log.txt) for the level table.

    uv run tools/downgrade/runsummary.py work/runs/lv1 [--from <s>] [--to <s>]

Prints the scene changes, errors and stalls, then the heartbeat windows from
`--from` on (default: the last atlas switch into a level): p50/p90 ranges, fps,
sprites, runs, page switches and free memory. Windows in which the probe took a
screenshot (a T2D_PROBE screenshot line) are counted apart, since each stalls the
game ~0.6 s; a slow frame without one is a real hitch and stays in. The worst
frame of all the windows is printed too.
"""
import re
import sys
from pathlib import Path


def main(argv):
    run = Path(argv[0])
    log = run / "log.txt" if run.is_dir() else run
    start = end = None
    if "--from" in argv:
        start = float(argv[argv.index("--from") + 1])
    if "--to" in argv:
        end = float(argv[argv.index("--to") + 1])
    lines = log.read_text(encoding="utf-8", errors="replace").splitlines()
    events = []
    beats = []
    shots = []  # log times of the probe's screenshots
    for line in lines:
        m = re.match(r"\s*([\d.]+) (\S+)(.*)", line)
        if not m:
            continue
        t, tag, rest = float(m.group(1)), m.group(2), m.group(3)
        if tag == "T2D_HEARTBEAT":
            kv = {}
            for k, v, mx in re.findall(r"(\w+)=([\d.]+)(?:/max([\d.]+))?", rest):
                kv[k] = v
                if mx:
                    kv[k + "Max"] = mx
            kv["t"] = t
            beats.append(kv)
        elif tag == "T2D" and "atlas:" in rest and "->" in rest and "prefetch" not in rest:
            events.append((t, "atlas" + rest.split("atlas:")[1][:110]))
            if re.search(r"-> L\d+|-> level|-> \w+ (?!MissionBriefing|MainMenu)", rest) and " menu " not in rest and "cutscene" not in rest:
                if "--from" not in argv:
                    start = t
        elif tag == "T2D_PROBE" and rest.startswith(" screenshot "):
            shots.append(t)
        elif tag == "T2D_STALL" and "ended" in rest:
            events.append((t, "stall" + rest[:60]))
        elif "Exception" in rest or "Error" in rest or "gap" in rest.lower():
            events.append((t, (tag + rest)[:150]))
    seen = {}
    for t, e in events:
        key = e[:80]
        seen[key] = seen.get(key, 0) + 1
        if seen[key] <= 2:
            print(f"{t:8.2f} {e}")
    rep = {k: n for k, n in seen.items() if n > 2}
    for k, n in rep.items():
        print(f"         x{n} {k}")
    # A beat covers the frames since the beat before, so it lies in the window when the beat before came at or
    # after the start (whatever the heartbeat's period).
    sel = [b for i, b in enumerate(beats)
           if (start is None or (i > 0 and beats[i - 1]["t"] >= start)) and (end is None or b["t"] <= end)]
    if not sel:
        print("no heartbeats in the window")
        return
    shot_beats = {id(b) for b in shot_windows(beats, shots)}
    clean = [b for b in sel if id(b) not in shot_beats]
    use = clean or sel

    def rng(key, fmt="{:.1f}"):
        vals = [float(b[key]) for b in use if key in b]
        if not vals:
            return "-"
        lo, hi = min(vals), max(vals)
        return fmt.format(lo) if lo == hi else fmt.format(lo) + "-" + fmt.format(hi)

    print(f"window {sel[0]['t']:.0f}-{sel[-1]['t']:.0f} s: {len(sel)} beats, {len(clean)} without a screenshot")
    print(f"  fps {rng('fps')}  p50 {rng('p50')}  p90 {rng('p90')}  worst {rng('worst')}")
    worst = max(sel, key=lambda b: float(b.get("worst", 0)))
    where = " (a screenshot window)" if id(worst) in shot_beats else ""
    print(f"  worst frame of all the windows: {float(worst.get('worst', 0)):.1f} ms at {worst['t']:.0f} s{where}")
    print(f"  sprites {rng('sprites', '{:.0f}')}  runs {rng('runs')}  pageSwitches {rng('pageSwitches')}  offscreen {rng('offscreen')}")
    print(f"  freeLPDDR {rng('freeLPDDRMB', '{:.0f}')}  freeCDRAM {rng('freeCDRAMMB')}  pages {rng('pages', '{:.0f}')}  pagesMB {rng('pagesMB', '{:.0f}')}  budget {rng('budgetMB')}  heap {rng('heapMB')}")
    print(f"  most in a frame: sprites {rng('spritesMax', '{:.0f}')}  runs {rng('runsMax', '{:.0f}')}")


def shot_windows(beats, shots, near=0.1):
    """The beats whose frames hold a screenshot's stall, one beat per screenshot. A beat covers the frames since the
    beat before. A screenshot's line comes after its stall, and the stalled frame's time is counted the frame after,
    so a shot within near seconds before a beat's end may be counted in that beat or the next: of the two, the one
    with the longer worst frame (the stall). Before (Nov 2026) both counted as screenshot windows, and the other
    one's own hitch went unseen."""
    marked = []
    for s in shots:
        i = next((i for i, b in enumerate(beats) if s <= b["t"]), None)
        if i is None:
            continue  # after the last beat: in none
        if beats[i]["t"] - s < near and i + 1 < len(beats):
            i = max(i, i + 1, key=lambda j: float(beats[j].get("worst", 0)))
        marked.append(beats[i])
    return marked


if __name__ == "__main__":
    main(sys.argv[1:])
