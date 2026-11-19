# Notice of modification

The patches in this folder modify the Rewind IL2CPP Decompiler (https://github.com/ratman-bit/Rewind-IL2CPP-Decompiler), Copyright 2026 ratman-bit, licensed under the Apache License 2.0.
They apply to its commit ea8e305 in name order; tools/setup.py applies them with `git apply` and builds the result.
Rewind's own LICENSE and NOTICE are left as they are and stay in every copy setup.py makes.

Changes made for t2d-no-fate-vita in 2026, one patch per change (0023 to 0027 also update the line 0022 adds to the files they change):

| Patch | Change |
| --- | --- |
| 0001 | Support functions reached through a wrapper that only widens its arguments are identified. |
| 0002 | Signature libraries can name `Array::NewSpecific`, `Object::New` and `Object::NewAllocSpecific`. |
| 0003 | The write barrier inlined after a store to a static field is removed. |
| 0004 | The metadata initializer is found by how it is called when no library names it. |
| 0005 | Floating-point arguments of unidentified native calls are kept. |
| 0006 | Array element accessors are identified by their code. |
| 0007 | A wide store over inherited fields is split into those fields. |
| 0008 | A virtual call through a class pointer merged from several paths is recognized. |
| 0009 | Invokers specialized for one vtable slot are recognized. |
| 0010 | The address of a struct's first field is kept where the field is meant. |
| 0011 | A string's characters read by index become the indexer. |
| 0012 | Every unrecovered part is marked; a note goes with its markers. |
| 0013 | Base constructor calls are put back. |
| 0014 | Inlined constructors are folded back; calls IL does not accept get a note. |
| 0015 | The runtime's inlined object hash becomes a call; arithmetic on a reference is marked. |
| 0016 | Small structs computed with as numbers are rebuilt or marked. |
| 0017 | One assembly reference per assembly. |
| 0018 | Floating-point negation through a sign mask is folded. |
| 0019 | Bits read as a float or double are reinterpreted without BitConverter. |
| 0020 | 8-byte structs returned in a register and merged are rebuilt from their bits. |
| 0021 | A 32-bit read of a 64-bit field is narrowed. |
| 0022 | Each changed or added source file carries a line saying so (Apache-2.0 §4(b)). |
| 0023 | The write barrier's card marking is removed in more shapes: a card table indexed from the image base, a flag read again after each marking, a marking that ends in a copy of the method's ending. |
| 0024 | A long stack slot that held a reference and a number is split into two variables. |
| 0025 | An object checked as a class is stored cast; a struct's address handed to a call is typed as its target. |
| 0026 | A class's static data pointer merged from several paths is that class's statics. |
| 0027 | An inlined compare-exchange on a reference field becomes `Interlocked.CompareExchange<T>`. |
| 0028 | A call whose target isn't known yet takes as many stack arguments as the run of outgoing slots written before it. |
| 0029 | An engine-method lookup merged from several paths (a tail-merged inlined internal call) collapses into the internal-call method. |
| 0030 | A read past the header of an object a type test proved to be a boxed value is the unboxed value, or a field of it. |
| 0031 | Virtual calls on `this` merged from several paths, through a merged vtable-entry address or a merge of merges, are recovered. |

Every change has a `REWIND_NO_*` environment switch that turns it off.
The patches hold no game code or data.
