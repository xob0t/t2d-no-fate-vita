# Rewind

The pipeline recovers the game's code with this project's fork of the Rewind IL2CPP Decompiler, https://github.com/xob0t/Rewind-IL2CPP-Decompiler, a GitHub fork of https://github.com/ratman-bit/Rewind-IL2CPP-Decompiler (Copyright 2026 ratman-bit, Apache License 2.0).
tools/setup.py clones it at the commit pinned in `tools/downgrade/common.py` (`REWIND_COMMIT`) into tools/external/rewind and builds it with the pinned .NET 10 SDK.
`REWIND_REPOSITORY` (an environment variable) clones from another URL or a local clone instead, to test a fork commit before it is pushed.

The fork is upstream's commit `ea8e305` plus one commit per change; its FORK.md lists them, with the Rewind issues they fix (drafted in [docs/rewind-issues.md](../../docs/rewind-issues.md)), and it carries the game-free repros (`repros/`).
A new Rewind fix is a new commit on the fork's main; then `REWIND_COMMIT` moves to it here.
Until 2026-11-20 the changes were patches in tools/rewind/patches, applied by setup.py; the fork's first 33 commits are those patches, 0001 to 0033, in order.

## Notice of modification

The fork modifies Rewind under the Apache License 2.0.
Rewind's own LICENSE and NOTICE are left as they are and are in every clone setup.py makes.
Each source file the fork changes or adds carries a line saying so (fork commit 22, kept up to date by the later ones).

Changes made for t2d-no-fate-vita in 2026, one fork commit per change, in order:

| # | Change |
| --- | --- |
| 1 | Support functions reached through a wrapper that only widens its arguments are identified. |
| 2 | Signature libraries can name `Array::NewSpecific`, `Object::New` and `Object::NewAllocSpecific`. |
| 3 | The write barrier inlined after a store to a static field is removed. |
| 4 | The metadata initializer is found by how it is called when no library names it. |
| 5 | Floating-point arguments of unidentified native calls are kept. |
| 6 | Array element accessors are identified by their code. |
| 7 | A wide store over inherited fields is split into those fields. |
| 8 | A virtual call through a class pointer merged from several paths is recognized. |
| 9 | Invokers specialized for one vtable slot are recognized. |
| 10 | The address of a struct's first field is kept where the field is meant. |
| 11 | A string's characters read by index become the indexer. |
| 12 | Every unrecovered part is marked; a note goes with its markers. |
| 13 | Base constructor calls are put back. |
| 14 | Inlined constructors are folded back; calls IL does not accept get a note. |
| 15 | The runtime's inlined object hash becomes a call; arithmetic on a reference is marked. |
| 16 | Small structs computed with as numbers are rebuilt or marked. |
| 17 | One assembly reference per assembly. |
| 18 | Floating-point negation through a sign mask is folded. |
| 19 | Bits read as a float or double are reinterpreted without BitConverter. |
| 20 | 8-byte structs returned in a register and merged are rebuilt from their bits. |
| 21 | A 32-bit read of a 64-bit field is narrowed. |
| 22 | Each changed or added source file carries a line saying so (Apache-2.0 §4(b)). |
| 23 | The write barrier's card marking is removed in more shapes: a card table indexed from the image base, a flag read again after each marking, a marking that ends in a copy of the method's ending. |
| 24 | A long stack slot that held a reference and a number is split into two variables. |
| 25 | An object checked as a class is stored cast; a struct's address handed to a call is typed as its target. |
| 26 | A class's static data pointer merged from several paths is that class's statics. |
| 27 | An inlined compare-exchange on a reference field becomes `Interlocked.CompareExchange<T>`. |
| 28 | A call whose target isn't known yet takes as many stack arguments as the run of outgoing slots written before it. |
| 29 | An engine-method lookup merged from several paths (a tail-merged inlined internal call) collapses into the internal-call method. |
| 30 | A read past the header of an object a type test proved to be a boxed value is the unboxed value, or a field of it. |
| 31 | Virtual calls on `this` merged from several paths, through a merged vtable-entry address or a merge of merges, are recovered. |
| 32 | A switch through a byte table of case indexes and a table of targets is recovered; the register a jump table jumps through is dead after the jump. |
| 33 | A direct call changes only the volatile registers the callee's code writes. |
| 34 | A class check casts the object only where every path to the use passes the check; a check on a path the CFG no longer has proves nothing. |

Change 34 comes after the fork's repros and docs commits.
Most changes have a `REWIND_NO_*` environment switch that turns them off (2, 3, 17 and 23 have none).
The fork holds no game code or data.

## Signatures

[signatures/unity-6000.0.60f1-windows-x64.sig](signatures) is the signature library for the game's Unity version, built from the game-free test project in tools/assess/sigtest (see [docs/assessment.md](../../docs/assessment.md)); it stays here and isn't in the fork.
