# Repairing Rewind's gaps without shipping the game's code

Rewind recovers the game's IL at build time from the user's own `GameAssembly.dll`, and leaves gaps where it couldn't: calls to `__Rewind.Opaque<T>("...")` for a lost value and `__Rewind.Effect("...")` for a lost statement.
This document describes how the port fills those gaps.
The repository may hold rules that name game methods and emit small IL edits, as huntdown's and butcher's rewriters do.
It may not hold the game's code, recovered or rewritten, and that includes whole method bodies written from scratch.

The short version.
A repair is a rule in `tools/downgrade/repairs/rules/*.cs` that names one game method, keys each marker by its kind and a short prefix of its text, checks a hash of the IL around it, and replaces only the marker with IL from a small builder.
Where the marker's own text already says what was lost, the rule gives only facts ("t0 is the `double[]` in local 0") and the engine compiles Rewind's text at build time.
Gaps of one known shape are fixed by patterns that name no game method.
The engine runs inside the rewriter, first, before anything renames or retargets the assembly.
Any drift between a rule and Rewind's output fails the build.

Everything here was measured on two Rewind outputs of the Steam build (24040403):
`work/rewind-steam-60f1`, made with the 6000.0.60f1 signature library, and `work/rewind-fix/runtime/fixed60`, the same with Rewind's runtime-helper fixes.
The prototypes now target fixed60, which is what the pipeline will use.
The pipeline now runs Rewind with this repository's 22 patches (`tools/rewind/patches`), and the rules were checked against that output; see [After the patch series](#after-the-patch-series).

## The four candidates

### (a) Cecil IL edits per marker

A rule finds a marker and replaces it with IL built by a builder API, as huntdown's `Patches.cs` passes do.
What the repository holds is the method's name, the marker's kind and prefix, a hash, and the IL that stands for the one value or statement the marker hides.
Filling `new int[8]` into a marker is not the game's code.
A 13-argument `setupImpact` call rebuilt from the native argument shuffle is closer to it, but it is still one statement the marker hid, and the rest of the method stays the user's.
Drift is easy to detect, because the rule addresses the marker and the IL around it directly.
Authoring is slower than C#, but the builder and the macros (below) bring most sites down to one line.
It fits butcher's rewriter, which is Cecil 0.10.0-beta6 and C# 5, and huntdown's Mono loadcheck sees ordinary IL.

### (b) Replacement bodies in C#, spliced in by Cecil

A person writes the whole method in C# from the native code, compiles it against the recovered assemblies, and the build splices the body in.
I rejected it.
A body written from the native code restates the game's method in substance, whoever typed it, and for a method with one gap it restates fifty lines to fix one.
It also hides drift: a replacement body keeps working when Rewind later recovers the method correctly, or recovers it differently, and nobody is told.

### (c) The C# route

Regenerate Rewind's C# at build time, apply Roslyn rewrites keyed by method and marker, compile with Roslyn.
The text a rule holds is the same as in (a), a fragment per marker, so it is no better or worse on the code question.
It is worse everywhere else.
The 2018.2 compile test (`tools/assess/compile2018`) still had 213 errors in Assembly-CSharp after the mechanical fixes, and 223 of the 2,100 compared methods came out of Roslyn with different IL from Rewind's.
So the route adds a second translation with its own errors to an IL that is faithful where Rewind recovered it.
Syntax-keyed edits also drift with every change in how Rewind prints C#, not only with changes in what it recovers.
And the rewriter and loadcheck are IL tools anyway.

### (d) Hybrid

What I recommend, and what is prototyped, is (a) with three additions that cut the text a rule holds and the time it takes to write:

1. Marker compilation. Rewind writes what it couldn't express as an expression in its own notation, such as `__store8((t0 + 40L), ((1d - _value) * 0.6094d))`. The rule binds Rewind's temporaries to locals, and `Expr.cs` compiles the marker's text to IL at build time. The game's arithmetic then comes from the user's Rewind output, and the repository holds only "t0 is local 0, a `double[]`".
2. Patterns. A gap of a known shape gets the same edit everywhere, with no method named. The write-barrier pattern is the one built.
3. Generators. Where thousands of sites share a shape and the lost data is in the native code, a reader extracts it from the user's `GameAssembly.dll` at build time and a recipe applies it. This was built for the static initializers and then dropped when Rewind's fixes made it unnecessary, see below.

Option (b) stays rejected even inside (d).
A rule may produce only what its marker stands for.

## The engine

`tools/downgrade/repairs/`:

| File | What it does |
| --- | --- |
| `Engine.cs` | Markers, selectors, hashes, `MethodRepair` and `Site`, the run and its checks, pattern snapshots |
| `Edit.cs` | Replacing a run of instructions in place, unreachable-code removal, the stack-depth check |
| `IL.cs` | The builder: `This`, `Arg`, `Ldloc`, `I4`, `R8`, `Call`, `Callvirt`, `Ldfld`, `Newarr`, `Halves`, `BitsToDouble`... |
| `Expr.cs` | Compiles a marker's own text with the rule's bindings |
| `Support.cs` | `__Repairs.Bits.ToDouble(long)`, added to the repaired assembly, because the game's stripped mscorlib has no `BitConverter.Int64BitsToDouble` |
| `Patterns.cs` | Shape-based repairs, run after the rules on methods no rule took |
| `RewriterHook.cs` | The engine as the rewriter's first step |
| `RepairsMain.cs` | A standalone driver for authoring: `apply`, `markers`, `gaps` |
| `rules/*.cs` | The rules, as static methods of `partial class Rules` |
| `native/listing.py` | A method's native code with its markers beside it |
| `verify.py` | Markers and ILVerify errors per method, before and after |
| `pin.py` | Fills in the hashes a new rule leaves as `null` |
| `build.sh` | Builds the standalone driver with the .NET Framework's csc and Cecil 0.10.0-beta6 |

The engine is C# 5 on Mono.Cecil 0.10.0-beta6, so it compiles into the rewriter unchanged.

### A rule

```csharp
// Player.moveUpdate: 0x1802EB5A8..5C0, call [klass+0x228] (slot 15, changeAnim) with
// edx = the row each path sets (V1 here), r8d = 0, r9d = 4.
static void PlayerMoveUpdate(Engine e)
{
    MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.heroes.Player::moveUpdate()", "2690909d");
    foreach (Site s in m.Sites("__objklass()", 3, "7749df70 c31a1fe3 b140682c")) s.Delete();
    m.Site("__load8((m0 + 560L))", "175005d6").Delete();
    m.Site("__call_indirect(__load8((m0 + 552L)), this,", "6f4b883e")
     .With(il => il.This().Ldloc(1).I4(0).I4(4).Callvirt("com.bitmapbureau.terminator.sprites.GameObject::changeAnim(Int32,Int32,Int32)"));
}
```

`e.Method(key, hash)` names the method as `Namespace.Type::name(ParamTypes)`, with the body hash it was written against.
`m.Site(prefix, nth, hash)` takes the nth marker not yet taken whose text starts with the prefix.
Rewind's temporary names (`t12`, `m3`, `%45`, `o7`) are numbered away before matching, so `m0` and `m25` both match `m#`.
A prefix names the marker's kind and the part that is Rewind's, such as a helper's address, a vtable offset or a field, and never an expression of the game's.
`m.Sites(prefix, count, hashes)` takes exactly `count` of them, with one hash per site.

What a site can do:

- `With(il => ...)`: for `Opaque<T>`, IL that pushes the value; for `Effect`, IL with the effect.
- `Delete()`: an `Effect` goes; an `Opaque` goes with the `stloc` or `pop` that consumes it, and anything else is an error.
- `Replace(before, after, il => ...)`: also takes up to 6 instructions on either side, which is the hashed window and no further.
- `Compile()`: the marker's own text, compiled with the rule's bindings (`m.Bind("t2", 2)`, `m.BindSame(...)`).
- Macros for shapes that recur: `SkipBarrier()`, `SplitStore2(type, nextField)`, `NewArray(elementType)`.

For a silent error, which has no marker, `m.Anchor(pattern, nth, hash)` finds an instruction run by opcode and operand substring, under the same hash check.
`m.RetypeLocal`, `m.NewLocal`, `m.MayRemove` and `m.Partial()` cover the rest.

### What a run checks

After every rule the engine removes code no path reaches, which is how a skipped write barrier disappears.
It then fails the run if:

- a marker in a repaired method is left, unless the rule says `Partial()`;
- unreachable-code removal took a marker that no rule accounted for (`MayRemove`);
- the stack depth is wrong anywhere, by a simulation over every path;
- a rule has no hash yet, outside `--pin`.

When nothing is left, it drops Rewind's `__Rewind.Note` too.
Short branches are widened before any edit, because Cecil 0.10 writes an operand as given and inserted IL could overflow a `br.s`.
ILVerify runs afterwards, in `verify.py`, as a separate check.

## Drift policy

Each rule pins two kinds of hash, both over normalized IL: opcodes, operands by full name, branch targets as relative distances, local slots with their types, and marker text with temporaries numbered away.
The site hash covers 6 instructions on each side of the marker, computed before any edit.
The method hash covers the whole body.

| What changed in Rewind's output | What the run does |
| --- | --- |
| The IL around a marker | Error: "context hash ..., the rule was written against ... (re-check the repair)" |
| Something else in the method | Warning only; the site hashes decide |
| A new marker in a repaired method | Error: markers left |
| A marker the rule names is gone | Error: stale rule, "Rewind may recover it now" |
| Every marker in the method is gone | One error: "Rewind recovers it now; check it against the native code and delete the rule" |
| An anchored silent error is gone | Error: anchor not found |
| A local renumbered | Error, as the slot is in the hash |

A build always runs strict.
To update rules after a new Rewind, run `Repairs apply ... --pin`, read each failing rule against the native code, fix it, and paste the new hashes with `pin.py`.
A pin records what the IL is now, not that the repair is right, so the re-check is the person's job and the hash only forces it.

This was tested on real changes, not only in theory.

- The rules written against the 60f1 output, run on the older output made with the 6000.0.85f2 library, failed on `Sound.createSoundFromFile`: there the `Dispose` in its `finally` is still an unidentified invoker, a marker the rule didn't know.
- The same rules run on their own output failed every method with the one "Rewind recovers it now" error.
- Run on fixed60, they failed `T800Pistol..ctor`, `EpicManager.getInstance` and `Gfx.saturateImage` as recovered, and the initializer table on its strict method hashes. `Player.changeState` failed on three site hashes: Rewind's fix now computes `setupImpact`'s first argument into V27 before the call, and the marker gained an argument. The re-check took about 5 minutes, and the rule now loads V27. The other rules applied unchanged.

## In the rewriter

The pipeline's rewriter is butcher's, merged into master at 4f5a137 (`tools/downgrade/DowngradeAssemblies.cs`, `rewriter/*.cs`).
Rules key Rewind's own names and hash Rewind's own IL, so the engine has to run before `Rewrite()` renames Assembly-CSharp to `Recovered.T2D` and retargets its type references.
`RewriterHook.cs` adds `RepairRewindGaps(AssemblyDefinition)` to `partial class DowngradeAssemblies`.
These files are owned by the pipeline, so the hookup is theirs to make:

1. In `Rewrite()`, first line after `ModuleDefinition module = assembly.MainModule;`, add `string repaired = RepairRewindGaps(assembly);`, and append `repaired` to the stats string it returns.
2. In `pipeline.py`, add to `REWRITER_SOURCES` the files `repairs/Engine.cs`, `Edit.cs`, `IL.cs`, `Support.cs`, `Expr.cs`, `Patterns.cs`, `RewriterHook.cs` and `repairs/rules/*.cs`, but not `RepairsMain.cs`, which has its own `Main`.
3. Make `REPAIR ERROR` lines fail the code stage. The hook adds each error to the rewriter's `problems`, which print as `UNRESOLVED` and exit 3, and the pipeline tolerates exit 3 while the game's references don't resolve yet. A repair error should not hide among those.

`RewindGaps.cs` stays as it is and counts what the repairs left.
`T2D_REPAIRS=0` skips the engine, and `T2D_REPAIR_PATTERNS=0` skips the patterns.
I compiled the merged rewriter with the hook and the engine in a scratch copy (`work/repairs/rwtest`): no clashes, no errors.
I did not run the rewriter end to end, since that needs T2DCompat and the 2018.2 references the pipeline builds.

## The static-initializer generator

Assessed on the 60f1 output, the 30 asset-table initializers, plus the other `staticInit` methods, held 3,019 marker sites in 41 methods.
The plan was a build-time reader of the native code.
It turned out the lost data was mostly element types, not constants: the 60f1 library already names the `double[]` store helper, so `AssetsPack.staticInit6` to `9` and `AssetsStaticMap.staticInit1` came out complete.
What was left was `Array::NewSpecific` with the class lost, element stores (`__ArraySetAt`, `__store4`) and write barriers.

So I built `native/array_facts.py`, which reads each `Array::NewSpecific` call's array class (the `TI_int[]` metadata slot loaded into rcx) and length from the user's DLL.
On top of it came a recipe that checked the facts against the markers in count, order and length, emitted `new T[n]`, retyped Rewind's int64 locals, skipped the barriers and compiled the stores from the markers' text.
One table in `rules/Initializers.cs` held the 38 methods it completed, each with a strict body hash and nothing else.
It repaired 3,008 of the 3,019 sites, and ILVerify's errors in those methods went from 66 to 7, all of them in the three methods it left out.
That is in commit cf99d96.

On fixed60, Rewind's runtime fixes leave two initializers with markers: `AssetsStaticMap.staticInitPrep` (4, objects from the unidentified helper 0x1800E3F30) and `Controls.staticInit` (3).
`AssetsPack.cs` has none.
So the generator is gone from the tree (commit 5222d7c); those two methods are per-method rules.
If a future Rewind regresses, cf99d96 has the reader and the recipe.
The parts that stay are general: `SkipBarrier` for a flag kept in a local, and `Expr.cs` reading string literals, static fields and a static array's `get_Length()`.

## After the patch series

Run on the patched Rewind's output (`pipeline.py recover`), five of the six rules failed with "Rewind recovers it now", or, for `ExplosiveBarrel.hit`, with its anchors gone.
Each method's new IL was compared with what its rule produced on fixed60:

- `Player.changeState`: the same `visible` and `canBeHit` stores (7 and 14), the same 23 `changeAnim` calls, and `setupImpact(facing * 8 + x, y - 16, 32, 32, false, image == 230 ? 0 : 25, facing, facing * 2.0, vy, impact.hitTypes, impact.hitSounds, false, true)`, now on a local holding `impact`.
- `Player.moveUpdate`: `changeAnim(row, 0, 4)` through slot 15, as the rule had it.
- `Gfx.draw`: the same `unityDraw` call; `Math.Floor(y)` is computed once instead of twice.
- `ContinueScreen.update`: the same `setAlphaMultiplier` argument, reinterpreted with `ldloca`/`ldobj` instead of `__Repairs.Bits.ToDouble`.
- `ExplosiveBarrel.hit`: Debris's vy is `-5.0` when `Game.mode == 8` and `-(rand * 6 + 2)` otherwise, the value the rule wrote; Rewind now computes `5.0` or `rand * 6 + 2` and negates it.

So those rules are retired: `rules/Player.cs`, `rules/VirtualCalls.cs`, `rules/Doubles.cs` and `rules/Silent.cs` are deleted.
`Sound.createSoundFromFile` (`rules/Barriers.cs`) still applies with its hashes unchanged, so nothing needed re-pinning.
With that rule and the write-barrier pattern (5 methods), Assembly-CSharp goes from 271 gap methods to 265, and the code stage passes with no `REPAIR ERROR`.
The fixed60 tables below are kept as the record of how the rules were written.

Patches 0023 to 0027 (2026-11-19) take out most of what the barrier pattern was for: the card markings on addresses in locals, elements and statics, which the pattern's shape didn't match.
With them the code stage reports 188 gap methods with 1,241 marker sites (from 253 and 1,640); `rules/Barriers.cs` still applies unchanged.

### Well-typed stand-ins for what is left (`rewriter/GapTyping.cs`)

Some gap methods hold, next to a marker, a value of the wrong kind that Mono would run but psp2snc rejects in IL2CPP's C++: the bits of an inlined box handed on as an object, a `long` stack slot read as an object, a field's address shifted as a card index, an `Object` stored where a `GameObject` is.
A method with a marker throws when it reaches one, so these only have to compile, not to run.
The rewriter's `TypeGapValues` pass (after the retargeting, before the gaps are counted) handles them, in methods that still have a marker and nowhere else:

- A number or an address where an object (or, for an address, arithmetic) is taken: the value is popped and `__Rewind.Opaque<T>("type: ...")` of the type taken stands for it. It is one more marker, counted with the others, and throws like them.
- An object where a class derived from its type is taken: `castclass` to that class, the check the native code made.

A value with several consumers, or whose type isn't known, is left and logged as `GAP TYPING SKIPPED`; each change is logged as `GAP TYPED`.
On the Steam release it types 122 sites in 64 methods, most of them the inlined boxes of the enemies' constructors (`new object[] { 0 }`), which the `box` repair pattern will turn into real values.
Since it touches only what is still a gap, a repair that completes a method takes the method out of its reach.

## Prototype results

### On fixed60, the current rules

Each repair was written from the native code, read with `native/listing.py` and, for the invoker thunks, from their disassembly.
Times are approximate wall-clock times from this session, including reading the listing, writing the rule and verifying it.
The first rule of each kind also cost the macro or builder piece it needed, listed separately.

| Method | Gap kind | Sites | Time | ILVerify errors before, after | The repair |
| --- | --- | --- | --- | --- | --- |
| `Player.moveUpdate` | tail-merged virtual call | 5 | 5 min | 1, 1 (kept) | three vtable loads deleted; `changeAnim(row, 0, 4)` via slot 15 |
| `Player.changeState` | invoker thunk, 2-byte bool stores, tail-merged call, doubles | 13 | 30 min, re-check for fixed60 5 min | 1, 1 (kept) | six `visible`/`canBeHit` pairs; `changeAnim(V6, 0, V7)`; two doubles from their halves; `this.setupImpact(V27, y - 16, 32, 32, false, image == 230 ? 0 : 25, facing, facing * 2.0, vy, impact.hitTypes, impact.hitSounds, false, true)` |
| `ContinueScreen.update` | bits reinterpreted as double | 2 | 5 min | 3, 3 (kept) | the markers' own text, with m28..m31 bound to V8..V11 |
| `Sound.createSoundFromFile` | write barrier inside a try/finally | 3 | 2 min | 2, 2 (kept) | `SkipBarrier`; handler bounds kept |
| `ExplosiveBarrel.hit` | silent FP negation, no marker | 3 anchors | 15 min | 1, 0 | a double local takes the int low half's place: -5.0, or -(rand * 6 + 2) |
| `Gfx.draw` | invoker thunk 0x180002D50 | 1 | 15 min | 0, 0 | `_target.unityDraw(_source, (int)floor(_x), (int)floor(_y), _frame, _columns, _rows, _align, _ct, _hflip, _vflip, -1, -1, null, _blendAlpha, 0.0, 1.0, 1.0)` |

All six end with no markers and no new ILVerify errors.
The kept errors aren't the repairs'.
In `Sound` and `EpicManager` they are the inlined engine plumbing (`Object.GetCachedPtr`, an `IntPtr` compared with an int64) that the binding fold-back pass is for.
The `[found Byte]` ones are `ldelem.any System.Boolean` followed by a branch, which ILVerify 10 flags all over Rewind's output, and which needs checking under huntdown's Mono loadcheck.

Two corrections to the assessment came out of reading the native code.
`ExplosiveBarrel.hit` negates on both paths, so the vy for `Game.mode == 8` is -5.0, which Rewind keeps as 0.
And the invoker in `Player.changeState` calls `setupImpact` on `this`, not on `this.impact`; the impact's fields are arguments.

The write-barrier pattern completes 5 methods on fixed60 that no rule names.
It made 99 complete on the 60f1 output, where the static-field barriers were still there.
Across the assembly ILVerify then reported 5 errors fewer and 31 newly visible: in `SteamManager.OnDestroy` the barrier's `ldflda ... shr.un` had stopped ILVerify early, and behind it are private engine-field accesses of the kind the API rules cover.
None of the new errors are in IL the engine emitted.

With the six rules and the pattern, fixed60 goes from 595 gap methods to 585.

### On the 60f1 output, now obsolete

These were the first round, and the engine reported each as recovered on fixed60:

| Method | Gap kind | Sites | Time |
| --- | --- | --- | --- |
| `T800Pistol..ctor` | `new T[n]` | 1 | 4 min |
| `EpicManager.getInstance` | static write barrier | 4 | 3 min, plus 10 min for `SkipBarrier` |
| `Gfx.saturateImage` | `new double[20]` and a color matrix in `__store8`/`__load8`/`__bits_to_double` | 46 | 5 min, plus about 40 min for `Expr.cs` |
| 38 static initializers | arrays, stores, barriers | 3,008 | about 40 min for the reader and recipe, none per method |

`Gfx.saturateImage` is the clearest case for marker compilation: the rule was six lines of bindings and counts, and the 46 sites' arithmetic never entered the repository.

## Estimate

The assessment assumed 20 minutes per gap method with 1 to 5 sites, 1 hour for 6 to 20, 2 hours for 21 to 50 and 4 hours above 50.
The prototypes, now that the tools exist, ran faster: about 2 to 5 minutes for a mechanical site (array, barrier, tail-merged call, double halves, bool pair), about 15 minutes for an invoker thunk's call or a silent error, and 30 minutes for a 13-site method with one of each.
I propose 10 minutes, 30 minutes, 1 hour and 2 hours for the four buckets.

| Gap methods on fixed60, initializers aside | Methods | At the assessment's rates | At the measured rates |
| --- | --- | --- | --- |
| 1 to 5 sites | 437 | 146 h | 73 h |
| 6 to 20 | 138 | 138 h | 69 h |
| 21 to 50 | 12 | 24 h | 12 h |
| over 50 | 6 | 24 h | 12 h |
| Total | 593 | 332 h | 166 h |

So about 4 person-weeks, against the 6 to 16 the assessment gave for this line after stage 1.
Read that with care.
Nine methods is a small sample, and I chose them with native listings already made for most.
Re-checks after each Rewind update add to it; the fixed60 one cost 5 minutes for one rule out of eight.
Finding silent errors is outside these numbers: the assessment's play-testing line covers it.

The clusters say where more tooling would pay most:

| Kind on fixed60 | Methods with it | Methods with only it |
| --- | --- | --- |
| `__bits_to_double` | 276 | 172 |
| invoker thunk 0x180002D50 (`unityDraw`) | 92 | 52 |
| write barrier on an untyped object | 91 | 2 |
| `__store2` over two bool fields | 79 | 42 |
| tail-merged virtual call | 52 | 19 |
| `__undefined` | 49 | 1 |
| `__Memcpy` | 40 | 0 |

A Rewind fix for issue 2 (bits to double) or a pattern for it would take 172 methods out of the first bucket.
Of the remaining 1-to-5-site methods, that is roughly 30 hours.

## Tooling that would speed authoring

Built:

- `native/listing.py` prints a method's native code with each marker placed before the instruction it most likely comes from (a call to the address it names, a vtable offset it names, the GC flag's load), labels metadata usages, runtime helpers, double constants and vtable slots of the method's own class.
- `Repairs markers <dll> <filter>` lists markers with index, kind, IL offset and hashes; `Repairs gaps` lists methods with markers.
- `verify.py` splits ILVerify's errors per method into gone, kept and new, and counts markers before and after.
- `pin.py` fills hashes into new rules.

Worth building, in the order I would build them:

1. An invoker decoder. Given an invoker thunk and a call site, map the caller's registers and stack slots through the thunk to the target's parameters, and print each argument's native source. That is what took 15 to 20 minutes by hand for `setupImpact` and `unityDraw`, and 0x180002D50 alone has 92 methods.
2. A `__store2` pattern from the field-offset table: the field at offset + 1 is in the user's metadata, so no rule needs to name it.
3. A `__bits_to_double` pattern or the Rewind fix: when both halves come from `DoubleToInt64Bits` of one double on every path, or from constants, the value is known without a person.
4. Binding suggestions in `listing.py`: for each temporary a marker names, the locals stored just before it, so a rule's `Bind` lines write themselves.
5. A rules lint that caps a site's emitted IL, to keep a rule from growing into a method body.

## Open questions

- Should `REPAIR ERROR` fail the code stage outright, as recommended above? That is the pipeline's call.
- Should the write-barrier pattern live in Rewind instead? It drops out of the engine on its own once Rewind stops emitting the shape, but every pattern here is a Rewind fix in waiting.
- The `[found Byte]` errors on `ldelem.any System.Boolean` look like ILVerify's, not Rewind's. huntdown's Mono loadcheck would settle it.
- fixed60 has marker kinds the 60f1 output didn't: `__undefined` (49 methods, mostly in `SteamManager`, which gets stubbed) and `__missing_argument` (14), where Rewind found the call but lost arguments. `Gfx.drawClippedWidth` is an example; its repair looks like a minute's work.
- The IL was compared with the native logic by reading it. No tool checks the equivalence, and play-testing remains the last check.
- Repaired methods keep Rewind's now-unused locals. That is harmless, but the rewriter's body-size widening sees them.
- `native/listing.py` reads the tables `tools/assess/ghidra` writes. If a generator comes back, the pipeline needs its own metadata-usage decoder.
