# Rewind issues (drafts)

Candidate issues for [Rewind](https://github.com/ratman-bit/Rewind-IL2CPP-Decompiler), first found on a commercial Unity 6000.0.60f1 game (Windows x64, IL2CPP Master build, metadata v31.1) with Rewind built from commit `ea8e305`.
None are filed yet.
The fixes are commits on this project's fork of Rewind, https://github.com/xob0t/Rewind-IL2CPP-Decompiler (`tools/setup.py` builds it; see [tools/rewind/README.md](../tools/rewind/README.md)), and a single upstream issue points at it.
"Patch NNNN" below is the fork's change commit NN, in the same order; the fork's FORK.md maps issues to commits, and its `docs/fork-issues` has these drafts without the game.
A new Rewind fix is a new commit on the fork, and `REWIND_COMMIT` in `tools/downgrade/common.py` moves to it.
Issues 1 to 21, 29 and 30 have a game-free reproduction: a method in [Repros.cs](../tools/assess/rewind-repros/Repros.cs), written from scratch, that makes the same native pattern; the fork's `repros/` has a standalone copy, with identifiers that echoed the game's renamed.
The game's names and addresses are kept only as "Seen in a real game" notes, which are not for filing.

## Reproducing

The repro project builds editor-free like the signature library's test project (`tools/assess/sigtest`): Roslyn, UnityLinker, then il2cpp with `--configuration=ReleasePlus` (the editor's "Master": `/GL`, `/LTCG`), incremental GC, and the C++ build replayed with MSVC 14.50.
Unity 6000.0.60f1 and MSVC 14.50 are expected in `tools/external` (see [assessment.md](assessment.md)); no editor, license or sign-in is needed.

    python -I tools/assess/rewind-repros/build_repros.py
    python -I tools/assess/rewind-repros/check_repros.py <rewind build folder> [--report <file.md>]

`build_repros.py` builds once into `work/rewind-repros/build/master/game` and reuses that while the sources don't change; a build takes 10 to 25 minutes.
`check_repros.py` decompiles it with the given Rewind (`rewind decompile <game> --signatures tools/rewind/signatures/unity-6000.0.60f1-windows-x64.sig --assembly Repros`) and prints PASS or FAIL per repro; PASS means fixed.
The report has, per repro, the expected C#, Rewind's C# and IL, and the native code (rizin, with the build's PDB symbols); the run on upstream main is in `work/rewind-repros/results.md`.
`disasm.py <symbol part>` disassembles any function of the build.

## Status

Upstream main is still `ea8e305` (checked 2026-11-19), so nothing has been fixed upstream since the assessment.
The fork's commits (until 2026-11-20 patches in `tools/rewind/patches`, applied by `tools/setup.py`) fix most of these issues; each issue below has a Status line.
The repro checks were run on upstream main and on the patched build, both with the rebuilt 6000.0.60f1 library.

- On upstream main only the controls and issue 1 pass (issue 1 through the library): 14 of 46 checks.
- With the fork (checked 2026-11-20 at the fork's head, `86258fc`, the same as with patches 0001 to 0033): 36 of 46 checks pass; 1, 3, 7, 8, 9, 11, 16, 17, 18, 21, 29 and 30 in full, and 6 and 10 in part.
- 2, 13 and 19 fail their checks only on form: the values are right and nothing is marked, but a merged double or struct is still built from its bits.
- 4, half of 6 and the local variant of the inlined constructors are open.
- Fixed on the game by the library alone, with or without the patches: 14, the metadata-init part of 1, and three items of 20.
- Not about native code, so not in the repro project: 5 (open) and 12 (fixed by patch 0017).

## 1. `Array::NewSpecific` called directly is not identified

Status: fixed by the rebuilt 6000.0.60f1 library, which names `Array::NewSpecific` and `Object::New` / `NewAllocSpecific`; both repros pass, with or without the patches.
Building that library needs patch 0002's helper rules; patch 0001 also lets a helper reached through a thunk that only widens its arguments keep the thunk's name.

`new T[n]` compiles to a call to `SZArrayNew(klass, n)`, a two-instruction thunk (`mov edx, edx; jmp Array::NewSpecific`).
With `/LTCG` nearly every call site skips the thunk and calls `il2cpp::vm::Array::NewSpecific` directly.
The signature library names the thunk `SzArrayNew`, but `NewSpecific` itself has no helper kind, so the direct calls stay unidentified.
`new T[n]` then comes out as `__Rewind.Opaque<long>("__native_0x…(__klass(), n)")`, with "Klass value used as data" in the note.
`NewSpecific` is easy to recognize: the length is checked against `0x7FFFFFFF`, the element size is read at `klass+0x104`, and then it allocates.
Two fixes would work: let a thunk's target inherit the thunk's identification (issue 15), or find array allocation by its use, as Rewind already finds the object allocator and the internal-call resolver: it is called with a metadata-slot class and an integer, and the result is used as an array.

Repro: `Issue1.Issue1_NewIntArray` and `Issue1.Issue1_NewStringArray`.
Expected: `return new int[n];`.
Observed: `return (int[])__Rewind.Opaque<long>("__native_0x180138F50(__klass(), conv.u8(n))");`, where the PDB names 0x180138F50 `?NewSpecific@Array@vm@il2cpp@@SAPEAUIl2CppArray@@PEAUIl2CppClass@@_K@Z`.
Reproduces on upstream main with the 6000.0.60f1 library.

The first draft also listed `MetadataInit`, `MetadataInitInline`, `GenericInterfaceInvoker`, `GenericVirtualInvoker`, `ObjectNew` and `InterfaceInvokeDataSlow` as unidentified.
That was with the 6000.0.85f2 library; the 6000.0.60f1 library identifies all of them, so only the array allocator is left.
The README says versions without their own library recover fewer bodies, and also that for Unity 6 the missing library "made no difference beyond one method", which didn't hold for this 6000.0 build.

Seen in a real game: the allocator at `0x1800E2FD0` (Steam build), 828 direct calls from Assembly-CSharp, gaps in 184 Assembly-CSharp and 428 InControl methods.

## 2. "bits reinterpreted as double" when `BitConverter.Int64BitsToDouble` was stripped

Status: the gap is gone with patch 0019, which reinterprets the bits with `ldloca` / `ldobj` instead of `Int64BitsToDouble` (so the C# needs `/unsafe`).
The double is still split into halves through the merge, so both repros still fail their check, which looks for the plain double; the value is right, and nothing is marked.
On the game `ContinueScreen.update` is now complete.

When a double flows through a merge (a `?:` or an `if`/`else` that assigns it), Rewind splits it into two 32-bit halves and rebuilds it with `Int64BitsToDouble`.
UnityLinker strips that method when the game doesn't call it, and Rewind then leaves the value opaque.
Keeping the double whole through the merge would avoid the split; failing that, emit the reinterpretation without the BCL method (a local and `ldloca` / `ldind.r8`, or a helper inside `__Rewind`).

Repro: `Fader.Issue2_ClampedAlpha` and `Issue2.Issue2_MergedArgument`.
Expected: `SetAlpha(a > 1.0 ? 1.0 : a);`.
Observed: the two branches assign `(int)BitConverter.DoubleToInt64Bits(num)` and its high half (or `0` and `1072693248`, the halves of 1.0) to locals, then `SetAlpha(__Rewind.Opaque<double>("__bits_to_double((conv.u8(conv.u4(m0)) | (conv.u8(conv.u4(m1)) << 32)))"))`.
Reproduces on upstream main with the 6000.0.60f1 library.
The repro keeps `DoubleToInt64Bits` and `SingleToInt32Bits` and lets UnityLinker strip `Int64BitsToDouble`, as the game's mscorlib does.

Seen in a real game: 256 Assembly-CSharp methods, for example `ContinueScreen.update` (`colorTransform.setAlphaMultiplier(...)`).

## 3. The incremental GC's write barrier after a static field store is left in (Master builds)

Status: patch 0003 removes the barrier after static field stores; on the game `EpicManager.getInstance` is complete.
With patch 0026 the statics pointer reused across the barrier is read as the class's statics again, and the repro passes; 10 `__statics()` sites in 3 methods are left on the game (issue 26).

In Master builds with incremental GC, the card-marking write barrier is inlined after reference stores.
After instance field stores Rewind removes it.
After static field stores it doesn't: the method keeps a `__load4(<gc flag>)` check, a `__statics() >>> 12` card index and an `__atomic_compare_exchange` loop.
The notes say "load: absolute address", "Statics value used as data", "address without a typed base" and "intrinsic atomic_compare_exchange".
The barrier could be dropped like the instance-field one.
It isn't always harmless: in the repro the compiler reuses the statics pointer across the barrier, and the field read after it, `return s_instance`, comes out as `__load8(__statics())` too.

Repro: `Registry.Issue3_GetInstance`, with `Registry.Issue3_SetInstanceField` as the control (its barrier is removed).
Expected: `if (s_instance == null) s_instance = new Registry(); return s_instance;`.
Observed: the store, then `if (__Rewind.Opaque<int>("__load4(6462374980L)") != 0) { … do { … __atomic_compare_exchange(…) } while (…); }`, and `return (Registry)__Rewind.Opaque<long>("__load8(m0)");` with `m0 = __statics()`.
Every static constructor of the repro that stores a reference has the same barrier.
Reproduces on upstream main with the 6000.0.60f1 library.

Seen in a real game: `EpicManager.getInstance` and `GalaxyManager.getInstance` (there the return survived).

## 4. Signed modulo by a power of two isn't folded back (cosmetic)

Status: open (cosmetic).

Repro: `Issue4.Issue4_Mod4`.
Expected: `return ticks % 4;`.
Observed: `int num = ticks & -2147483645; if (0 > num) return ((num + -1) | -4) + 1; return num;`.
It's correct, but harder to read.
Reproduces on upstream main.

## 5. `recovery-report.tsv` can't be joined to assemblies or overloads (minor)

Status: open.

Rows are keyed `Type::Method` without the assembly or the parameter types, so overloads and same-named types in different assemblies collide.
An assembly column and the full signature would help.
Visible in any output, including the repro's (`Repros.ULongId::Equals`); not a native pattern, so there is no check for it.

## 6. A gap noted but not marked: `typeof(T)` comes out as invalid IL

Status: the marking half is fixed by patch 0012: the address computation is marked, and the first repro passes.
Folding the inlined `GetTypeFromHandle` back into `typeof(T)` is open, and the second repro fails.

The README says every gap shows up as a marker.
`typeof(T)` compiles to an inlined `Type.GetTypeFromHandle`, and Rewind doesn't recognize it: it notes "address computation: no field at offset 0x20 of object", but puts no `Opaque` or `Effect` in the body.
The unrecovered part comes out as invalid IL instead: `ldtoken; …; Enum.InternalBoxEnum(...); conv.i8; ldc.i8 32; add; callvirt Object::GetType()`, which ILSpy shows with "Expected Obj, but got I8" comments.
Two fixes are needed: mark the gap, and fold the inlined `GetTypeFromHandle` back into `typeof(T)`.

Repro: `Issue6.Issue6_TypeofSizeOf` (no marker at all) and `Issue6.Issue6_TypeofList`.
Expected: `int a = Marshal.SizeOf(typeof(PackSentinel));`.
Observed: `Type type = ((long)Enum.InternalBoxEnum((System.RuntimeType)typeof(PackSentinel).TypeHandle, 1L) + 32L).GetType();`, under a note that says 2 parts weren't recovered, and no marker.
Reproduces on upstream main with the 6000.0.60f1 library.

Seen in a real game: 13 methods with a note and no marker; in 8 the unrecovered part was invalid IL, for example `Steamworks.Packsize::Test`, `Steamworks.Callback`1::GetCallbackType` and `InControl.XInputDeviceManager::Enable()`.
In the other 5 ILVerify found nothing, for example `Main::update(Boolean)`, whose note says 5 parts (class pointer used as data) weren't recovered; that variant isn't reproduced.

## 7. The address of an offset-0 field is replaced by the struct's own address (bug)

Status: fixed by patch 0010; both repros pass.

Calling an instance method of a field at offset 0 of a struct (`Value.ToString()` on a `ulong` field) takes the field's address, which equals the struct's address.
Rewind emits the struct's address with no `ldflda`, so the IL calls `UInt64::ToString()` on a pointer to the whole struct.
It happens to work at run time, but the IL is wrong and the C# shows a cast.

Repro: `ULongId.ToString` (on `this`) and `Issue7.Issue7_ParameterFieldToString` (on a by-value parameter).
Expected: `public override string ToString() => Value.ToString();` and `Last = callback.GameId.ToString();`.
Observed: IL `ldarg.0; call UInt64::ToString()` (C# `((ulong)this).ToString()`) and `ldarga.s callback; call UInt64::ToString()` (C# `((ulong*)(&callback))->ToString()`).
Reproduces on upstream main.

Seen in a real game: `Steamworks.CSteamID::ToString()` and `SteamManager::OnUserStatsReceived`.

## 8. No truncation after an inlined `UInt64.GetHashCode` (bug)

Status: fixed by patch 0021; both repros pass.

`UInt64.GetHashCode` is `(int)value ^ (int)(value >> 32)`; inlined, the XOR is done on 64 bits and the result used as an int.
Rewind keeps the 64-bit value, so a method declared to return int32 returns an int64, and an int32 is added to an int64.

Repro: `ULongId.GetHashCode` and `KeyChord.GetHashCode`.
Expected: `Value.GetHashCode()` and `Keys.GetHashCode() + Count`.
Observed: `return (int)((long)(int)(Value >> 32) ^ (long)Value);` with "Expected I4, but got I8"; the cast is ILSpy's, the IL returns the int64.
Reproduces on upstream main.

Seen in a real game: `Steamworks.CSteamID::GetHashCode()` and `InControl.KeyCombo::GetHashCode`.

## 9. An inlined object hash done as arithmetic on a reference (bug)

Status: fixed by patch 0015; both repros pass.

`RuntimeHelpers.GetHashCode(o)`, and so `Object.GetHashCode` (`base.GetHashCode()`), is inlined as `(uint)((nint)o >> 3) * 2654435761`.
Rewind applies the shift and multiply to the reference itself, with no marker: `ldarg.0; ldc.i4.3; shr.un; ldc.i4 -1640531535; mul`.

Repro: `Issue9.Issue9_IdentityHash` and `BindingLike.GetHashCode`.
Expected: `return RuntimeHelpers.GetHashCode(o);` and `return base.GetHashCode();`.
Observed: `return (o >> 3) * -1640531535;`, which ILSpy marks "Unknown result type (might be due to invalid IL…)".
Reproduces on upstream main.

Seen in a real game: `InControl.BindingSource::GetHashCode()`.

## 10. Small structs handled as integers without a marker

Status: patch 0016 rebuilds the struct where its fields are known and marks it where not; the parameter repro passes.
The return repro still fails its check, but only on form: Rewind now writes `new PipeHandle { Value = NextHandle() }`, which is correct.

A struct of 4 bytes or less travels in an integer register.
Rewind treats the register as the struct's bits without converting back, and the README says unresolved struct copies become gaps; these have no marker.

Repro: `Matcher.set_Issue10_VendorID` (a 4-byte `{ bool; ushort }` parameter stored to a field) and `Issue10.Issue10_IntAsStruct` (an int returned as a 4-byte struct).
Expected: `set => VendorField = value;` and `return new PipeHandle(NextHandle());`.
Observed: `VendorField = new OptionalU16 { HasValue = ((byte)(int)value != 0), Value = (ushort)((uint)(int)value >> 16) };` (IL `ldarg.1; conv.i4` on the struct) and `return (PipeHandle)NextHandle();` with "Expected VT, but got I4".
Reproduces on upstream main.
Issue 19 is the 8-byte case.

Seen in a real game: `InControl.InputDeviceMatcher::set_VendorID` / `set_ProductID` and `Steamworks.SteamAPI::GetHSteamPipe`.
Related: some marker-free methods carry int64 where the original had native int, an x64 artifact that matters for 32-bit targets.

## 11. Constructors with no base `.ctor` call

Status: fixed by patch 0013; both repros pass, and ILVerify's `ThisUninitReturn` and `CallCtor` errors are gone from the game's three assemblies.

When the base constructor is empty and inlined, the constructor comes out as `nop; ret`.
That runs on Mono and IL2CPP, but it is invalid per ECMA-335, and ILVerify flags it.
The README says trivial constructors are folded back; the base call should be put back too.

Repro: `EmptyBehaviour..ctor` (a MonoBehaviour) and `EmptyDerived..ctor` (a plain class over an empty base).
Expected: `ldarg.0; call MonoBehaviour::.ctor(); ret` (and `EmptyBase::.ctor()`).
Observed: `nop; ret` for both.
Reproduces on upstream main.

Seen in a real game: 125 constructors (Assembly-CSharp 74, InControl 46, Steamworks 5), for example `EpicManager::.ctor()`.

## 12. Two AssemblyRef rows per class library

Status: fixed by patch 0017.

Each game assembly references mscorlib twice, and System/System.Core where it uses them.
One row has the original signed identity (b77a5c561934e089 / 7cec85d7bea7798e) and no TypeRef uses it; the other is unsigned, and every TypeRef uses it.
The rebuilt mscorlib, System and System.Core themselves have `PublicKeyToken=null`.
Report this as "inconsistent identities", since we can't confirm from the metadata that the originals were signed.
Not a native pattern, so the repro project has no check for it.

## 13. Floating-point negation turned into a wrong integer value, unmarked (bug, high priority)

Status: the silent wrong value is fixed by patches 0018 and 0019: on the game `ExplosiveBarrel.hit` and `Player.shoot` are complete and negate the double.
The repros still fail their checks: where the negated double is merged from two branches, Rewind keeps it as two halves with the sign bit flipped on the high one and reinterprets the result, which is the right value but not `-x`.

Negation compiles to `xorps` with a sign-mask constant.
Rewind lifts it as an XOR on the double's high 32 bits.
When the negated value is a stack argument of a call, Rewind passes the int low half instead of the negated double, with no marker, in a method it reports complete.
The correct output is `-expr`.
When the negated value is a register argument, the same lifting becomes a "bits reinterpreted as double" opaque (issue 2), so it is at least flagged there.

Repro: `Issue13.Issue13_NegatedStackArgument` (silent), `Issue13.Issue13_NegatedRegisterArgument` and `Issue13.Issue13_NegatedFieldArgument` (opaque).
Expected: `double vy = Mode == 8 ? 0.0 : -(Rng.NextDouble() * 6.0 + 2.0); Spawned[i & 7] = new Particle(57, 0, true, vx, vy, 0.0, i);`.
Observed: `num3 = (int)BitConverter.DoubleToInt64Bits(value);` with `value = num4 * 6.0 + 2.0`, then `new Particle(57, 0, animated: true, vx, num3, 0.0, num)`, an `//IL_00b3: Expected F8, but got I4` comment, no note and no row in the recovery report.
The native code loads the mask out of the loop (`movsd xmm11, [0x181156680]`, the 8 bytes `0x8000000000000000`), negates with `xorps xmm6, xmm11`, and stores the result as the constructor's fifth argument.
For the register variants Rewind prints `Sink.TakeDouble(__Rewind.Opaque<double>("__bits_to_double((…conv.i4(BitConverter.DoubleToInt64Bits(t0))…) | (…(conv.i4((BitConverter.DoubleToInt64Bits(t0) >> 32)) ^ -2147483648)…) << 32)))"))`.
Reproduces on upstream main with the 6000.0.60f1 library.
Clues to find more in a real game: `Expected F8, but got I4` in the C# and `DoubleToInt64Bits` outside a gap.

Seen in a real game: `ExplosiveBarrel::hit`, `Furniture::hit` and `Player::shoot` (the spread shot's vertical speed is the low half of `Math.Sin`'s bits instead of `-Math.Sin(a)`).

## 14. The value argument of `double[]` element stores is dropped (fixed by the 6000.0.60f1 library)

Status: fixed by the 6000.0.60f1 library; patches 0005 and 0006 also fix it for games without a library of their own.

A bounds-checked store into a `double[]` that isn't inlined (`SetAt` of the array class, value in XMM2) was unidentified with the 6000.0.85f2 library, and its `Effect` string dropped the XMM argument: `__native_0x…(t1, 0L)`.
The 6000.0.60f1 library identifies it as `ArraySetAt`, and the same methods now come out as array initializers (`new double[5] { 4087.0 / 4096.0, … }`).
No repro: the pattern needs methods large enough that the store isn't inlined, which the signature library's test project already covers.

Seen in a real game: 2,438 sites, nearly all in `AssetsPack.staticInit*`.

## 15. Direct calls to `Array::NewSpecific` stay unidentified while its thunk is named

Status: fixed by the rebuilt library, which names the target itself, and by patch 0001, which passes the thunk's identification on to the function behind a wrapper that only widens its arguments.

This adds detail to issue 1, and the same repro shows it: `rewind-lab sig-apply` identifies the thunk `SZArrayNew` at 0x1801C1690 (`mov edx, edx; jmp 0x180138F50`), but not the 0x180138F50 it jumps to, which the repro's methods call directly.
A thunk's target could inherit the thunk's identification.

Seen in a real game: the thunk at 0x1800A0D60 (`mov edx, edx; jmp 0x1800E2FD0`) is named `SZArrayNew`.

## 16. A 2- or 4-byte store that spans inherited bool fields isn't split

Status: fixed by patch 0007; all three repros pass.

MSVC merges stores to adjacent bool fields into one wider store (`mov word [rcx+0x16], 0x01`, or `mov dword [rcx+0x14], 0x10100` for four).
Rewind splits it when the fields are declared in the method's own class, but not when they are inherited.
It notes "store: access of 2 bytes spans fields of <derived class>" and emits `__store2`.

Repro: `FlagsDerived.Issue16_SetInheritedBools`, `Issue16_ClearInheritedBools` (2 bytes) and `Issue16_SetFourInheritedBools` (4 bytes), with the fields in `FlagsBase`.
`Flags16.Issue16_SetTwoBools`, with the fields in its own class, is recovered correctly.
Expected: `Kind = kind; Visible = true; CanBeHit = false;`.
Observed: `__Rewind.Effect("__store2(&this.Visible, 1)")`, `__store2(&this.CanBeHit, 0)` and `__store4(&this.Important, 65792)`.
Reproduces on upstream main.

Seen in a real game: 54 Assembly-CSharp methods with this reason, for example `Player::changeState` (`visible = true; canBeHit = false;`) and `APCGrenadeThrower..ctor`.

## 17. A virtual call isn't recognized after tail-merging

Status: fixed by patch 0008 for the static receiver; the repro passes and `Player.moveUpdate` is complete on the game.
The this-form (virtual calls on `this` merged from several paths, through a merged vtable-entry address or a merge of merges) is fixed by patch 0031.

When two paths end in the same virtual call with different arguments, MSVC keeps one call site and jumps to it from both (tail merging).
Each path loads the receiver and its class, and the call block gets them, and the differing argument, as merged values.
Rewind doesn't follow the class pointer through the merge: the call comes out as `__call_indirect(__load8(klass + 408L), receiver, arg, 180L, __load8(klass + 416L))`, with an `__objklass()` opaque on each path ("ObjKlass value used as data", "indirect call through Val").
The slot follows from the offset ((0x198 − 0x138) / 16 here) once the merged class pointer is known to be the receiver's.

Repro: `Issue17b.Issue17_TailMergedNoCctor` (`Random.Next` on a static field's object, from two paths).
Expected: `if (state == 1) t = mode != 8 ? RandomHolder.Rng.Next(60, 180) : 120; else t = mode != 8 ? RandomHolder.Rng.Next(120, 180) : 150; return t + 1;`.
Observed: each path ends in `Random rng = RandomHolder.Rng; long num = __Rewind.Opaque<long>("__objklass()"); int num2 = 60;` (or 120), and the merged call is `__Rewind.Opaque<long>("__call_indirect(__load8((m0 + 408L)), m1, conv.u8(m2), 180L, __load8((m0 + 416L)))")`.
The native code has the paths set `rcx`, `rax = [rcx]` and `edx` (0x3C or 0x78), then share `mov r9, [rax+0x1A0]; mov r8d, 0xB4; call [rax+0x198]`.
Reproduces on upstream main with the 6000.0.60f1 library.

MSVC only merges when the paths are alike.
With a static constructor on the receiver's class, each path has its own class-init check and the calls stay apart (`Issue17.Issue17_TailMergedStaticReceiver`, recovered correctly).
The variant with the call on `this` that the real game showed wasn't reproduced.
In `Walker.Issue17_TailMergedThisCall`, `Issue17_MergedClassPointer` and `Issue17_MergedClassPointerTail` MSVC merges the call sites too, but reloads the class pointer (`mov r10, [rbx]`) in the shared block, and Rewind recovers them.
In `Actor.Issue17_MergedVirtualCall` the class load is hoisted above a branch and used on one side only, and Rewind recovers that as well.

Seen in a real game: `PoliceHelicopter::changeState` (`Utils.rand.Next(lo, 180)`, the same shape as the repro) and `Player::moveUpdate` (`changeAnim(row, 0, 4)` through `__load8(m0+552L)`).

## 18. A misleading marker for an unidentified invoker thunk

Status: patch 0009 recognizes invokers specialized for one slot; on the game `Gfx.draw` and `Player.changeState` are complete.
The 13-argument call's stack arguments, issue 21, are captured since patch 0028, and the repro passes.

In a large method MSVC stops inlining IL2CPP's invoker templates, so a call through `VirtualActionInvokerN<…>::Invoke(slot, obj, args…)` stays a call.
When every remaining call passes the same slot, LTCG specializes the invoker for it: the slot argument (RCX) goes dead and the invoker calls through `[klass+slot]`.
These invokers are generated per signature, so no signature library can name them.
Rewind's `Effect` for the call prints the dead RCX value as an argument, shows the receiver from RDX as the next argument, and drops the floating-point and stack arguments; arguments computed only for the call are left behind as dead code.
A gap marker should either show the real receiver and arguments or say they are unknown.

Repro: `Brawler.Issue18_SpecializedInvoker` (a 13-argument virtual method called from a large `switch`).
Expected: `SetupHitbox(X, Y, 48, 48, Knock, Damage * 3, Facing, Facing * 6.0, -6.0, Types, Sounds, true, true);` (case 4).
Observed: `int num4 = damage + damage * 2; … __Rewind.Effect("__native_0x1800E07E0(conv.u8(t39), this)");`, where the PDB names 0x1800E07E0 `VirtualActionInvoker13<double, double, int, int, bool, int, int, double, double, int, object[], bool, bool>::Invoke` and its body calls `[r8+0x178]` without reading RCX.
Reproduces on upstream main with the 6000.0.60f1 library.

Seen in a real game: the invoker at 0x180003170, calling through `[klass+0x5D8]` (`setupImpact`), from `Player::changeState`.

## 19. 8-byte struct returns in RAX aren't rebuilt as structs

Status: patch 0020 rebuilds merged 8-byte struct returns: in `UnityMain.BetterAnalog` on the game, two of the three return paths now build a `Vector2`.
The third, in the game and in the repro, still fills a field from `SingleToInt32Bits` bits through a reinterpreting load; that is valid IL and the right value, but the repro's check wants `new Vector2(...)`.

A `Vector2` return is packed into RAX from two floats.
Rewind returns the packed integer cast to the struct, built from `SingleToInt32Bits` bits; that doesn't compile.
The logic itself matches the native code.

Repro: `Issue19.Issue19_SnapVector2`.
Expected: `return new Vector2(Math.Sign(x), Math.Sign(y));` (and `(0, sign y)`, `(sign x, 0)`).
Observed: `return (Vector2)((uint)BitConverter.SingleToInt32Bits(num4) | ((ulong)(uint)BitConverter.SingleToInt32Bits(Math.Sign(y)) << 32));` with "Expected VT, but got I8", in a method with no note.
`Issue19.Issue19_OrderedPair`, which returns a struct of our own, is recovered correctly.
Reproduces on upstream main with the 6000.0.60f1 library.

Seen in a real game: `UnityMain::BetterAnalog`.

## 20. Smaller coverage gaps

Status: inlined constructors calling virtual methods are folded back by patch 0014 (the `InlinedCtor_NewVirtualTint` repro passes); where the object is kept in a local (`InlinedCtor_NewVirtualTintLocal`) it isn't, and that is left with a note.
Patch 0030 reads a value past the header of an object a type test proved to be a boxed value as the unboxed value (or a field of it), which covers the tested unboxing reads; the double-returning interface invoker and the covariance check are open.

- Inlined constructors aren't folded back when the constructor calls virtual methods: `new VirtualTint(1, 1, 1, 0)` comes out as `(VirtualTint)FormatterServices.GetUninitializedObject(typeof(VirtualTint))`, the field stores and the two virtual calls. Repro: `InlinedCtor.InlinedCtor_NewVirtualTint` and `InlinedCtor_NewVirtualTintLocal`; `InlinedCtor_NewTint`, whose class is sealed and calls non-virtual methods, is folded back correctly. Reproduces on upstream main. Seen in a real game: 286 sites, for example `new ColorTransform(1, 1, 1, 0)` in `BBSplash`.
- An interface invoker with a double return comes out typed `Opaque<long>`. With the 6000.0.60f1 library it is identified, but still "not translated", and the method still returns a long where a double is declared. Seen in a real game: 0x180003650, in `haxe.lang.Runtime::toDouble`.
- Fixed by the 6000.0.60f1 library on the game: the `IDisposable.Dispose` invoker (0x180002550) is identified, the array-type-mismatch exception getter (0x1800A1160, 546 sites) is identified, and metadata-init calls (0x1800A0BA0, 1,503 sites) no longer leave dead `if (1 == 0) {…}` blocks.
- The covariance check isn't folded back into a plain array store; not rechecked.
- Unboxing a struct in an `is` pattern isn't recovered: `obj is ULongId other && other.Value == Value` reads the boxed value as `__Rewind.Opaque<long>("__load8((obj + 16L))")` ("no field at offset 0x10 of object"). It is marked. Seen in the repro's `ULongId.Equals`, found while writing it; no check.

## 21. Stack arguments of a call with many arguments are not captured (new)

Status: fixed by patch 0028: a call whose target isn't known yet takes as many stack slots as the run of outgoing slots written before it, from the first and with no hole, on every way in. The repro passes.

A virtual call with 13 arguments, inlined into a small method, loses its last six arguments.
Rewind marks them (`__Rewind.Opaque<double>("__missing_argument()")`, "call argument was not captured"), so nothing is silent, but the call is otherwise fully known.
The native code stores the stack arguments out of order, interleaved with the register setup, and the call is `call [r10+0x178]`.

Repro: `Shooter.Issue18_InvokerCallA`, `Issue18_InvokerCallB` and `Issue18_InvokerCallC`.
Expected: `SetupImpact(X + 4.0, Y, 32, 32, KnockDown, damage, Facing, Facing * 2.0, Vy, Types, Sounds, true, KnockDown);`.
Observed: `SetupImpact(x, y, 32, 32, knockDown, damage2, Facing, __Rewind.Opaque<double>("__missing_argument()"), …)` with six such arguments.
Reproduces on upstream main.

## 22. Card markings of the write barrier left in where the address is in a local, an element or a static (new)

Status: fixed by patch 0023; on the game the `barrier` gaps go from 393 sites in 54 methods to 8 sites in 3, and 46 Assembly-CSharp methods become complete.

Issue 3 and the instance-field pass remove the card marking when it has their shape.
Three shapes on the game didn't have it:

- The card table indexed from the image base (`[__ImageBase + index*8 + 0x1118580]`): neither constant alone is in the image's data, so the marking's memory wasn't recognized as the runtime's.
- A marking after the first in a run of stores: the compiler reads the GC flag again after each marking and the next test takes it merged from both ways (a phi of reads of the same address), which isn't a plain read.
- The last marking of a path that ends in a copy of the method's epilogue: the marking returns by itself instead of rejoining.

The card table's address left in a register after the loop (a `lea r9, [table]` handed on as an unused argument of the next call) also kept a marking from being removed.
The C++ then had `address >> 12 & 0x1FFFFF` on a field's address: 126 psp2snc errors in 53 methods.
No repro yet; the game's `GameObject..ctor`, `FWElevator.awake` and `SarahVan.changeImage` have the three shapes.
Left: `Sound.createSoundFromFile`, `AssetsEmbed.loadBytes`, `SaveSettings.loadSettings` and `SarahVan.update`, where the marking sits in a larger region.

## 23. An object handed on from a long stack slot (new)

Status: fixed in part by patch 0024.

An outgoing stack-argument slot holds the bools of one call (merged in as bytes: `slot & -256`) and then the object argument of the next.
Rewind's stack splitting only splits slots it finds contradictory, and a long slot holding a reference wasn't, since an untyped slot is taken to be a long.
The object went to the call as an `int64`, which C++ rejects (`Cutscene.draw`, the `ColorTransform` of `unityDraw`).
With the patch a long slot given both a reference (or handed where a reference is taken) and a computed number is split.
Not fixed where the slot's address is also kept in a value (`HUD.draw`, 10 sites), or where the long is an inlined box (`new object[] { 0 }` in 38 enemy constructors: the `box` gaps).

## 24. A class check pasted in, the object stored uncast (new)

Status: fixed by patch 0025.

An inlined `castclass` (the type-hierarchy compare and the throw) is taken out as a check, and Rewind already casts the checked object when it is a call's argument or receiver.
Stored into a field it went in uncast, so a `GameObject` was stored into an `Elevator` field (`ElevatorDoors.awake`, `FutureWar3.setupScene`, in methods with no gap).

## 25. A struct slot reused for another struct, handed by reference (new)

Status: fixed by patch 0025.

`UnityMain.OnRenderImage` builds a `Color` for `GL.Clear` and later a `Vector3` for `Matrix4x4.TRS` in the same stack memory.
Rewind's check for a struct handed where another is taken read the type of the address it makes, which is already the parameter's, so the slot stayed one `Color` local passed as a `Vector3`.
The check now reads the local's own type; such slots are split, and loads that only go to inputs a call doesn't take don't join their lifetimes.

## 26. A class's statics pointer merged from several paths (new)

Status: fixed by patch 0026; `__statics()` sites on the game go from 85 to 10, and 22 more Assembly-CSharp methods are complete.

This is the main remainder of issue 3.
When several paths each read a class's static data pointer and the compiler merges them (tail merging), the field reads after the merge went through a variable holding the pointer, marked `__statics()`.
`Audio.setDuckingVolume`: three paths load `[Audio_klass + 0xB8]` and share `mulsd xmm0, [rax+0x58]` (`audioDuckingLevel`).

## 27. An inlined compare-exchange on a reference field is left opaque (new)

Status: fixed by patch 0027; 56 more bodies of the game are complete, among them every event accessor of InControl and `UnityMain.OnEnable` and `OnDisable`.

A field-like event's `add` and `remove` are a loop of `Delegate.Combine` (or `Remove`) and `Interlocked.CompareExchange` on the field.
Pasted in, the exchange is a `lock cmpxchg`, which Rewind kept as the intrinsic `__atomic_compare_exchange(&field, value, comparand)`, typed long.
The delegate compared with its result became a number, which psp2snc rejects (12 errors in `UnityMain.OnEnable` and `OnDisable`).
Rewind already turns the runtime's own compare-exchange helper into `Interlocked.CompareExchange<T>`; the patch does the same for the intrinsic on a reference field, and Rewind's delegate folding then turns the loops back into `add_`/`remove_` calls.

## 28. Found by the Windows bring-up probe

The probe (`tools/assess/bringup`) ran Rewind's C# as a Unity 2018.2 Windows player and listed what stopped it.
Checked again with all the patches:

- Fixed by patch 0029: internal calls through a cached function pointer. Unity 6 caches the resolved internal call in a static: `__load8(0x1810CFE90)`, resolved and stored when null (`__nativecode()`), then `__call_indirect`. Seen in `UnityMain.ScreenStuff` (`Camera.set_rect`) and `UnityMain.FixedUpdate`.
- Fixed by patch 0027: inlined field-like event accessors. `InputManager.OnDeviceAttached += …` came out as `Delegate.Combine` and an `__atomic_compare_exchange(&InputManager.OnDeviceAttached, m1, m0)` loop, in `UnityMain.OnEnable` and `OnDisable` (issue 27).
- Fixed by patch 0030: an unboxing read as `__load4(obj + 16)` in `haxe.lang.Runtime.toInt` and `toBool`, behind their type tests.
- Fixed: the element stores of `Vector3.zero`'s parts in `BitmapData..cctor` are whole `Vector3.zero` elements now.
- Fixed by patch 0025: the `Vector3` local typed as `Color` in `UnityMain.OnRenderImage` (issue 25).
- Fixed: `Main.main`'s inlined constructor, which came out as `__Rewind.Ref(new Sprite())`, is `new Main()` now.

## 29. A switch through a byte-indexed jump table loses its case bodies (new, found on the console)

Status: fixed by patch 0032; both repros (`Issue29_ByteTable`, `Issue29_ByteTableFrom400`) pass.
`X64Cfg.TryReadJumpTable` took the `cmp` bound (the number of case values, 129 in `Button.staticCallbacks`) as the length of the table of targets (65 entries), read past it, found the count wrong and gave up, so the jump became an indirect tail call.
The patch decodes the byte table and the table of targets and recovers a switch on the case value; the register the jump goes through is dead after it.
Checked against the native code: all 33 byte tables in `GameAssembly.dll` (22 in methods) and all 292 one-level tables in methods give the native targets case by case.

MSVC compiles a sparse `switch` (many cases sharing few bodies) as two tables: a byte per case value giving a body's index, then a table of 32-bit offsets from the image base:

```
movzx eax, byte ptr [rdx + rax + <index table>]
mov   ecx, dword ptr [rdx + rax*4 + <offset table>]
add   rcx, rdx          ; rdx = image base (lea rdx, [rip - ...])
jmp   rcx
```

Rewind keeps the two loads as `__Rewind.Opaque<int>("__load4(((conv.u8(__load1((t + <index>))) * 4L) + <offsets>))")` and the jump as `__call_indirect(t, t, __klass(), 6442450944L, ...)`, and the bodies reached only through the table are missing from the IL: the method ends at the marker.
A one-level table (`mov ecx, [rdx + rax*4 + table]`) is recovered as a `switch`.
No repair rule can stand in for it, since the bodies are game code.

Seen in a real game, 8 sites in 6 methods, all in play: `Button.staticCallbacks` (129 cases, every menu button's action), `Sprites.addFromJSON` (3 tables, the map objects' factory; the first gap the console run reached, as the attract demo loaded its map), `GameMap.solid`, `GameObject.move`, `Player.setGroundTile` and `Game.start`.

## 30. The write-barrier helper is taken to clobber r9 (new)

Status: fixed by patch 0033 (a direct call changes only the volatile registers its callee's code writes); the repro (`Issue30_*`, branch `rewind/issue30-repro`) passes.

MSVC with LTCG knows which registers a function of the same image leaves alone, and keeps a value in r9 across a call to the write-barrier helper, which doesn't touch it.
Rewind treats every call as clobbering the volatile registers (rax, rcx, rdx, r8 to r11), so a store of r9 after the call reads an undefined value:

```
xor   r9d, r9d
...
call  <write barrier>       ; preserves r9
mov   dword ptr [rbx + 0x..], r9d
```

comes out as `obj.field = __undefined();`.
The register save sets of a callee in the same image are known from its code (or, for IL2CPP's helpers, from the signature library), so Rewind could keep r9 (and whatever else the callee doesn't write) live across such calls.

The same holds for xmm1, a volatile register the helper doesn't touch either.

Seen in a real game: `Sprites.addFromJSON`, the stores of `fW3BossLift.type` and `fWElevator.type` after a barrier at 0x1800a0270 (r9d, 0); `Platform.doPlatformCheck`, `other.y = top - 1.0` with top in xmm1 across the barrier.

## 31. An instruction MSVC duplicated into a block that falls into a join is lost there (new, found on the console)

Status: open; worked around by a repair rule (`rules/Scripts.cs`, `DrawParallaxTiled`). No repro yet.

MSVC sinks a loop's `j++` into both paths of an `if` inside the loop body: the taken path increments before its call, and the other path is one instruction that falls through into the loop test.

```
  comisd xmm6, xmm8
  jae    culled            ; four culling tests branch here
  ...
  inc    ebx               ; j + 1 on the drawing path
  ...
  call   Gfx::draw
  jmp    test
culled:
  inc    ebx               ; j + 1 on the culled path
test:
  cmp    ebx, 4
  jl     loop
```

Rewind gives the drawing path's `inc ebx` its own local (`V10 = V3 + 1`) and tests that local at `test:`, but leaves the culled block empty (a `nop`), so on the culled path the test reads the V10 of the last drawn tile, or its initial 0, and the loop never ends.
The value at a join should be a phi of both paths' definitions of ebx; Rewind gives the culled block none.
It isn't every block of this shape: `tools/downgrade/repairs/native/joins.py` lists the game's blocks of one update that a branch targets and that fall into the join right after a `jmp` to it (391, nearly all `mov` and `xor`), and of the register updates checked, `Script.getRank`'s `dec ebx` and `Player.update`'s `dec eax` (a store of `shotTimer - 1` on two paths) come out right.
What sets this one apart is that it is the loop's induction variable, tested at the join and carried round the back edge.

Seen in a real game: `Script.drawParallaxTiled` (0x18042D320), which Cyberdyne Factory, Future War 2 to 4 and the attract demos call: on the console the first frame with a tile off the screen never ended.

## 32. A class check on one path casts the object on every path (new, found on the console)

Status: fixed by the fork's change 34 (`a45bb78`, `REWIND_NO_CAST_DOMINANCE` turns it off). No repro yet.

An inlined `castclass` of `this` on one path of a method, before a field of the subclass, is taken out as a check and kept as a fact about `this` for the whole method.
Every member access through `this` was then cast to the subclass, also the reads of the method's own class's fields on paths that never made the check, so the method threw `InvalidCastException` whenever `this` wasn't the subclass, with no gap to show for it.
A check in a block a later simplification took out still held everywhere, and a check's place was lost when its block was merged with the next.
The fork casts only where every path from the entry passes a check, or the use is past the check in its block (or just before it, with no store or call between: the compiler loads fields ahead of the class test); a field the static type lacks, read where nothing proves the class, stays unresolved.

Seen in a real game: `Script.getRank` (0x18042AAB0) reads `levelID` (`[rdi+0x10]`) unchecked and checks `this` against `CyberdyneHQ` only on the `levelID == 7` path, before `numCopsKilled`; every other level's end threw. A repair rule (`ScriptGetRank`) dropped the two casts until the fork's fix.

## 33. A class check taken to hold at uses it doesn't reach (new)

Status: fixed by the fork's change 35 (`3130179`, `REWIND_NO_CAST_DOMINANCE` turns it off with change 34). No repro yet.

Change 34 asked whether a check holds only for member reads. A call's receiver and arguments, a store and a merge's incoming value still read the method-wide fact, so an object was cast to a class checked on another path, or later on the same one.
Three smaller faults went with it: a use just before its check counted as safe to reorder when no store or call was strictly between, though the use could be the store itself, or a load or division between could fault first; a check under a null test was kept from either arm, though only the arm taken when the object isn't null passes it; and a merged block's check lost its place when a later pass took out the node it was recorded before.
With the fix, on the game: the same counts of complete and gap bodies; in Assembly-CSharp one method changes, `PoliceHelicopter.start`; in mscorlib 20 bodies change, and UnityEngine.CoreModule and InControl are unchanged.

Seen in a real game: `PoliceHelicopter.start` chains the helicopter's targets, `targets[i].nextTarget = targets[i + 1]`. Rewind merged `targets[i + 1]` (or null past the end) and typed the merge `HeliTarget`, from the native class test that comes after the store. The fork now types it `object`, and the repair rule `PoliceHelicopterStart` (`rules/Bosses.cs`) casts it to `HeliTarget` at the store, where the native code tests it.

## Not Rewind's

ILVerify 10.0.15 crashes with an InvalidCastException on `callvirt Object::MemberwiseClone()` over arrays (an inlined `Array.Clone`).
That crash is ILVerify's bug.
