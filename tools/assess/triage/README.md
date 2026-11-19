# Gap triage

Turns the Rewind gap methods left in the rewritten game assembly into a prioritized repair worklist: where each method is first reached in play (boot, main menu, each level in order, other menus, extras, endings), which repair pattern each marker site needs, and the hours by docs/repairs.md's rates.
Nothing here contains game code; `tiers.json` names game classes and methods, as the repair rules do.

| File | What it does |
| --- | --- |
| `GapGraph.cs` | Mono.Cecil: dumps an assembly's methods with their calls, `ldtoken`s, static-field types, `__Rewind` markers and notes to JSON. |
| `kinds.py` | Classifies a marker site by repair pattern from its text; the patterns, their scope (general or per method) and the rule hours. |
| `triage.py` | Rapid type analysis over the dump, tier by tier in play order; joins the atlas-usage map objects and the bring-up probe's stacks; writes the worklist. |
| `report.py` | `worklist.md`. |
| `tiers.json` | The play order, what each tier admits, the gates, the cut factories and the moot (replaced or stubbed) methods. |

```sh
tools/external/dotnet10/dotnet.exe build -c Release tools/assess/triage/GapGraph.csproj -o work/bin/GapGraph
tools/external/dotnet10/dotnet.exe work/bin/GapGraph/GapGraph.dll work/assemblies-2018.2.19f1-vita/Recovered.T2D.dll work/triage/graph.json
python -I tools/assess/triage/triage.py --graph work/triage/graph.json --config tools/assess/triage/tiers.json \
  --atlas work/atlas-usage --bringup work/bringup/logs --out work/triage --report artifacts/downgrade/code-report.txt
```

It writes `work/triage/worklist.md`, `worklist.tsv` (one row per gap method) and `reach.json`.
The first line it prints compares its gap list with the code stage's report; they should not differ.
Run it again after the code stage changes.
