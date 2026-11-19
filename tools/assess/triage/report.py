"""worklist.md for triage.py: summary by tier, pattern groups, the ordered worklist, tiers, caveats."""
import datetime

from kinds import PATTERNS, RULE_HOURS


def h(x):
    return f'{x:.1f}'


def write_md(rows, items, stats, pulled, rule_id, tiers, tier_info, report_check, graph_file, path):
    live = [r for r in rows if not r['moot']]
    moot = [r for r in rows if r['moot']]
    L = []
    w = L.append
    w('# Repair worklist: the gap methods left in Recovered.T2D')
    w('')
    import hashlib, os
    dll = hashlib.sha256(open(graph_file, 'rb').read()).hexdigest() if os.path.exists(graph_file) else '?'
    stamp = datetime.datetime.fromtimestamp(os.path.getmtime(graph_file)).strftime('%Y-%m-%d %H:%M') if os.path.exists(graph_file) else '?'
    w(f'Generated {datetime.datetime.now().strftime("%Y-%m-%d %H:%M")} by `tools/assess/triage` from `{graph_file}`, written {stamp}, SHA-256 `{dll[:16]}...`.')
    w(f'{report_check}.')
    w('Another agent is clearing the C++ compile errors at the same time, so a few of these methods may change. Re-run the triage once its changes land.')
    w('')
    w('How it was made:')
    w('')
    w("- `GapGraph` (Mono.Cecil) dumps the rewritten assembly: each method's calls, `ldtoken`s and static-field types, its `__Rewind` markers, and Rewind's note.")
    w("- `triage.py` classifies every marker site by repair pattern (`kinds.py`). It then walks the call graph tier by tier, in play order, with rapid type analysis: a virtual call dispatches only to classes that a reached `newobj` creates, or a raw allocation's `ldtoken`.")
    w("- Each tier admits its scene classes (the level script, its cutscenes, the menu). Until then, a `newobj` of one, or a dispatch into one, waits. A level's map objects come from `work/atlas-usage` (maps.json, class_refs.json).")
    w("- `Game`'s methods wait for the first level and `MainMenu`'s for the menu tier; `xbox*` methods go to a console-only tier.")
    w("- The map spawners whose classes depend on a parameter (`Trigger.update`, `HidingPlace.update` and two doors) don't instantiate, and `Sprites.addFromJSON` is cut. The map data stands in for them.")
    w("- A method belongs to the first tier that reaches it. The reachability is static, so a method in a tier *can* run there, behind whatever branch.")
    w("- The bring-up probe's runs confirm the start order (`work/bringup/logs`, on an older Rewind output without the patches): `UnityMain.Start` > `Main.main` > `Main.init` > the static initializers, as far as `FontsPack.staticInit`. Methods seen in the probe's stacks are marked *probe*.")
    w("- Per-method hours use docs/repairs.md's measured rates by marker sites: 1-5 sites 10 min, 6-20 30 min, 21-50 1 h, over 50 2 h. A rule's hours are my estimate (`kinds.RULE_HOURS`), and the sites it clears count no per-method time.")
    w('')
    w('## Summary')
    w('')
    w('| Tier | Methods | Sites | Hours, method by method | Left after the rules: methods | sites | hours |')
    w('| --- | ---: | ---: | ---: | ---: | ---: | ---: |')
    tot = [0, 0, 0.0, 0, 0, 0.0]
    for t in tiers:
        rs = [r for r in live if r['tier'] == t]
        if not rs:
            continue
        left = [r for r in rs if r['residual']]
        v = [len(rs), sum(r['sites'] for r in rs), sum(r['hours_alone'] for r in rs), len(left),
             sum(r['residual'] for r in left), sum(r['hours_left'] for r in left)]
        tot = [x + y for x, y in zip(tot, v)]
        w(f'| {t} | {v[0]} | {v[1]} | {h(v[2])} | {v[3]} | {v[4]} | {h(v[5])} |')
    rule_h = sum(RULE_HOURS.get(p, 2.0) for p in pulled)
    w(f'| **All live** | **{tot[0]}** | **{tot[1]}** | **{h(tot[2])}** | **{tot[3]}** | **{tot[4]}** | '
      f'**{h(tot[5])}**, plus {h(rule_h)} for the {len(pulled)} rules |')
    w(f'| Moot (replaced or stubbed) | {len(moot)} | {sum(r["sites"] for r in moot)} | 0 | | | |')
    w('')
    w('Moot: ' + ', '.join(f'`{r["cls"]}.{r["method"]}` ({r["moot"]})' for r in moot) + '.')
    w("No other method of the stubbed or replaced code has a marker left in the code stage's output: SteamManager, EpicManager, GalaxyManager, PlayerPrefsX, `Sound.createSoundFromFile`, `AssetsPack.staticInit5..9`, `BitmapData.loadTexturePacks` and `CreateMultiMeshFrames`. None of the 253 calls into InControl or Steamworks either.")
    w("The atlas rules change only one call or one field read in `BBSplash.start`, `BBSplash.update` and `AssetsEmbed.staticInit`, and none of those is a gap, so their gaps stay live. The same goes for what the platform layer keeps of `UnityMain`, `Utils`, `Platform` and `Achievements.unlock`.")
    w('')
    w('## Pattern groups')
    w('')
    w('Marker sites of the live methods, by the repair pattern they need.')
    w('*Only* counts methods that have that pattern and nothing else.')
    w('*Finished* counts methods that the pulled-forward rules (R1...) clear between them.')
    w('')
    w('| Rule | Pattern | Scope | Methods | Sites | Only | Finished | Boot sites | Boot + menu + FW1 sites | Rule hours | Fix |')
    w('| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |')
    for p in sorted(PATTERNS, key=lambda q: (q not in pulled, pulled.index(q) if q in pulled else 0, -stats[q]['sites'])):
        s = stats[p]
        if not s['methods']:
            continue
        title, scope, fix = PATTERNS[p]
        w(f'| {rule_id.get(p, "")} | `{p}`: {title} | {scope} | {s["methods"]} | {s["sites"]} | {s["only"]} | '
          f'{s.get("finished", "")} | {s["boot_sites"]} | {s["early_sites"]} | '
          f'{h(RULE_HOURS[p]) if p in pulled else ""} | {fix} |')
    w('')
    w('## Worklist')
    w('')
    w('In priority order:')
    w('')
    w('1. The pulled-forward rules, ordered by how many boot sites they clear, then boot + menu + first-level sites.')
    w('2. Every method with work left after the rules, in tier order. Within boot: startup before the first frames, probe-seen first. Then cheapest first.')
    w('')
    w("Methods the rules finish are listed under their rule, and in the TSV as `by R...`.")
    w('')
    w('| # | Item | Tier | Method | Sites | Left | Left by kind | Hours | Reached via |')
    w('| ---: | --- | --- | --- | ---: | ---: | --- | ---: | --- |')
    for i, it in enumerate(items, 1):
        if it['kind'] == 'rule':
            p = it['what']
            fin = [r for r in live if p in r['kinds'] and all(q in pulled for q in r['kinds'])]
            names = ', '.join(f'{r["cls"]}.{r["method"]}' for r in fin[:6]) + (' ...' if len(fin) > 6 else '')
            done = (f'With the other rules it finishes {len(fin)} methods: {names}' if fin else
                    'It finishes no method by itself: every method with it has other kinds too')
            w(f'| {i} | **{it["item"]}** | all | rule `{p}`: {PATTERNS[p][0]}. {done} | '
              f'{it["sites"]} | | in {it["methods"]} methods | {h(it["hours"])} | {PATTERNS[p][2]} |')
        else:
            r = it['row']
            tier = r['tier'] + (f' ({r["phase"]})' if r['phase'] else '') + (', probe' if r['probe'] else '')
            via = ' > '.join(r['path'][-4:]) if r['path'] else r['note']
            w(f'| {i} | {it["item"]} | {tier} | `{r["cls"]}.{r["method"]}({r["sig"]})` | {r["sites"]} | {r["residual"]} | '
              f'{r["residual_kinds"]} | {h(it["hours"])} | {via} |')
    w('')
    w('## Tiers')
    w('')
    w('| Tier | Scene classes admitted | Map object classes | Methods first reached |')
    w('| --- | --- | ---: | ---: |')
    for t in tier_info:
        w(f'| {t["name"]} | {", ".join(t["roots"])} | {t["map_classes"]} | {t["reached"]} |')
    w('')
    w('## Caveats')
    w('')
    w("- The reachability is static. Branches on game state aren't followed, so a method can land in an earlier tier than the one it first runs in.")
    w("  `FreewayChase..cctor` lands in Future War 1 because `HUD.draw` reads FreewayChase's static fields; IL2CPP runs the static constructor only if that branch runs.")
    w("  `Achievements.update` and `Audio.update` run every frame, but their gaps may sit behind conditions.")
    w("- Calls hidden inside gaps (unidentified invokers, `__call_indirect`) aren't edges; calls named in a marker's text are added by name.")
    w("  The unreached methods are the likeliest targets of those hidden calls, `Gfx.drawClippedWidth` from the HUD's invoker sites for example.")
    w("- Haxe's dynamic calls (`Reflect`, `Runtime.callField`) aren't followed.")
    w("- Sites are classified by the marker's text. A `box` block's value, or a barrier's field, may still need a fact per site, and the rule's hours don't count that.")
    w("- The rates come from docs/repairs.md's sample of nine methods. The rule hours are guesses.")
    with open(path, 'w', encoding='utf-8') as f:
        f.write('\n'.join(L) + '\n')
