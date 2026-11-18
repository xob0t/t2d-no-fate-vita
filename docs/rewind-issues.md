# Rewind issues (drafts)

Candidate issues for [Rewind](https://github.com/ratman-bit/Rewind-IL2CPP-Decompiler), found while assessing Terminator 2D: NO FATE (Unity 6000.0.60f1, Windows x64, IL2CPP Master build, metadata v31.1) with Rewind built from commit `ea8e305`.
None are filed yet.
Before filing, recheck each one on the newest commit, and give evidence from the Steam release (build 24040403), never from game files.

## 1. `Array::NewSpecific` and metadata init not identified on 6000.0.60f1

With `unity-6000.0.85f2-windows-x64.sig` (picked as the nearest library), `sig-apply` leaves `MetadataInit` and `MetadataInitInline` unidentified.
On the Steam build it also misses `GenericInterfaceInvoker`, `GenericVirtualInvoker`, `ObjectNew` and `InterfaceInvokeDataSlow`.
It does report an `SzArrayNew` there, but the allocator most game methods call (`0x1800E2FD0` on Steam, `0x1800E8960` on the repack) stays unidentified.
`new T[n]` then comes out as `__Rewind.Opaque<long>("__native_0x…(__klass(), n)")`, with "Klass value used as data" in the note.
The array allocator alone accounts for 184 Assembly-CSharp methods and 428 InControl methods.
The function is plainly `Array::NewSpecific`: the length is checked against `0x7FFFFFFF`, the element size is read at `klass+0x104`, and then it allocates.
Rewind already finds the object allocator and the internal-call resolver by their use. Array allocation could be found the same way: it is called with a metadata-slot class and an integer, and the result is used as an array.
Known limitation? Partly: the README says versions without their own library recover fewer bodies. But it also says that for Unity 6 the missing library "made no difference beyond one method", which doesn't hold for this 6000.0 build.

## 2. "bits reinterpreted as double (no BitConverter in this build)"

256 Assembly-CSharp methods (Steam build) leave a double opaque because the game's stripped mscorlib has `BitConverter.DoubleToInt64Bits` but no `Int64BitsToDouble`.
Example: `ContinueScreen.update`, `colorTransform.setAlphaMultiplier(__Rewind.Opaque<double>("__bits_to_double(...)"))`.
Suggestion: when the BCL method is missing, emit the reinterpretation without it (a local and `ldloca` / `ldind.r8`, or a helper inside `__Rewind`).

## 3. Inlined write barrier on static field stores is flagged as a gap (Master builds)

In Master builds, the incremental GC's card-marking barrier is inlined after field stores.
For instance fields Rewind removes it, as in `LeaderboardState..ctor`.
For static fields it doesn't: `EpicManager.getInstance` and `GalaxyManager.getInstance` keep a `__load4(<gc flag>)` check, a `__statics() >>> 12` card index and an `__atomic_compare_exchange` loop.
The notes say "load: absolute address", "Statics value used as data", "address without a typed base" and "intrinsic atomic_compare_exchange".
The method's actual logic is complete, so the barrier could be dropped like the instance-field one.

## 4. Signed modulo by a power of two isn't folded back (cosmetic)

`ticks % 4` comes out as `ticks & -2147483645` followed by a negative fix-up.
It's correct, but harder to read.

## 5. `recovery-report.tsv` can't be joined to assemblies or overloads (minor)

Rows are keyed `Type::Method` without the assembly or the parameter types, so overloads and same-named types in different assemblies collide.
An assembly column and the full signature would help.

## 6. Gap noted but not marked: invalid IL instead of a marker (likely bug)

13 methods start with a `__Rewind.Note` that says parts weren't recovered, but contain no `Opaque`/`Effect` call.
In 8 of them the unrecovered part comes out as invalid IL; in the other 5 ILVerify finds nothing, so the IL doesn't show what was lost.
For example, the note in `Steamworks.Packsize::Test` and `Steamworks.Callback`1::GetCallbackType` says "no field at offset 0x20 of object", and the IL is `conv.i8; ldc.i8 32; add; callvirt Object::GetType()`.
Others with invalid IL: `InControl.XInputDeviceManager::Enable()`, `com.bitmapbureau.terminator.sprites.objects.SteelMillPress::update()`.
Without: `com.bitmapbureau.terminator.Main::update(Boolean)`, whose note says 5 parts (class pointer used as data) weren't recovered.
The README says every gap shows up as a marker.

## 7. Address of an offset-0 field replaced by the struct's own address (likely bug)

`Steamworks.CSteamID::ToString()` is `ldarg.0; call UInt64::ToString()`, with no `ldflda m_SteamID`.
`com.twissgreen.unity.SteamManager::OnUserStatsReceived` does the same at IL 0x1F (`ldarga pCallback; call UInt64::ToString()`).

## 8. Missing truncation after an inlined `UInt64.GetHashCode` (likely bug)

`Steamworks.CSteamID::GetHashCode()` returns an int64 from a method declared as int32.
`InControl.KeyCombo::GetHashCode` adds an int32 to an int64.
The similar `InControl.KeyBindingSource::GetHashCode` comes out correct.

## 9. An inlined object hash done as arithmetic on a reference (likely bug)

`InControl.BindingSource::GetHashCode()` is `ldarg.0; ldc.i4.3; shr.un; ldc.i4 -1640531535; mul`.
That is the runtime's inlined `RuntimeHelpers.GetHashCode`, applied to the reference as if it were an integer, with no marker.

## 10. Small structs handled as integers without a marker

- `InControl.InputDeviceMatcher::set_VendorID` / `set_ProductID`: `ldarg.1; conv.i4` on an `OptionalUInt16`.
- `com.twissgreen.unity.UnityMain::BetterAnalog`: a `Vector2` built as an int64 from `SingleToInt32Bits`.
- `Steamworks.SteamAPI::GetHSteamPipe`: an int32 returned as `HSteamPipe`.

The README says unresolved struct copies become gaps; these have no marker.
Related: some marker-free methods carry int64 where the original had native int, an x64 artifact that matters for 32-bit targets.

## 11. Constructors with no base `.ctor` call

125 constructors (Assembly-CSharp 74, InControl 46, Steamworks 5).
For example, `com.twissgreen.unity.EpicManager::.ctor()`, a MonoBehaviour's, is just `nop; ret`.
That runs on Mono and IL2CPP, but it is invalid per ECMA-335, and ILVerify flags it.
The README says trivial constructors are folded back.

## 12. Two AssemblyRef rows per class library

Each game assembly references mscorlib twice, and System/System.Core where it uses them.
One row has the original signed identity (b77a5c561934e089 / 7cec85d7bea7798e) and no TypeRef uses it; the other is unsigned, and every TypeRef uses it.
The rebuilt mscorlib, System and System.Core themselves have `PublicKeyToken=null`.
Report this as "inconsistent identities", since we can't confirm from the metadata that the originals were signed.

## Not Rewind's

ILVerify 10.0.15 crashes with an InvalidCastException on `callvirt Object::MemberwiseClone()` over arrays (an inlined `Array.Clone`), in `SaveStats.loadStats`, `SaveStats.saveStats` and `TiledData.copy2DInt` / `getGeometry`.
That crash is ILVerify's bug.

## 13. Floating-point negation turned into a wrong integer value, unmarked (bug, high priority)

Negation by `xorps` with a sign mask is lifted as an XOR on the double's high 32 bits.
When the result flows into a call argument, Rewind passes the int low half instead of the negated double, with no marker, in methods reported Complete.
Confirmed against the native code in `ExplosiveBarrel::hit` (0x1802D4090, Steam build), `Furniture::hit` and `Player::shoot` (`xorps` at 0x1802F6C16; the spread shot's `vy` is the low half of `Math.Sin`'s bits instead of `-Math.Sin(a)`).
The correct output is `-expr`.
Elsewhere the same pattern becomes a "bits reinterpreted as double" opaque (issue 2), so it is at least flagged there.
Clues to find more: `Expected F8, but got I4` (14 hits in Assembly-CSharp's C#) and `DoubleToInt64Bits` outside a gap.

## 14. The value argument of `double[]` element stores is dropped

0x1800026F0 is a bounds-checked store into a `double[]` (the value is in XMM2).
Rewind doesn't identify it, and its `Effect` string drops the XMM argument: `__native_0x1800026F0(t1, 0L)`.
That is 2,438 sites, nearly all in `AssetsPack.staticInit*`; for example the call at 0x18043375E in `staticInit6` (0x1804319E0) stores the constant at 0x180F6CED0.
The correct output is `arr[i] = <const>`.

## 15. Direct calls to `Array::NewSpecific` stay unidentified while its thunk is named

This adds detail to issue 1.
On the Steam build Rewind names the thunk at 0x1800A0D60 (`mov edx, edx; jmp 0x1800E2FD0`) `SZArrayNew`, but 828 Assembly-CSharp calls go straight to 0x1800E2FD0 and stay unidentified.
A thunk's target could inherit the thunk's identification.

## 16. A 2-byte store that spans two bool fields isn't split

`mov word [rbx+0x12A], 1` becomes `__store2(&this.visible, 1)`; the correct output is `visible = true; canBeHit = false;`.
Example: `Player::changeState`, 0x1802F3DF4.
54 Assembly-CSharp methods have this reason ("store: access of 2 bytes spans fields of …").

## 17. A virtual call isn't recognized after tail-merging

When the compiler hoists the class/vtable load above a merge point, the call comes out as `__call_indirect(__load8(m0+552L), …)` ("ObjKlass value used as data").
The slot follows from the offset: (0x228 − 0x138) / 16.
Examples: `Player::moveUpdate` (`changeAnim(row, 0, 4)`), `PoliceHelicopter::changeState` (`Utils.rand.Next(lo, 180)`).

## 18. A misleading marker for an unidentified invoker thunk

0x180003170 is an invoker that calls through `[klass+0x5D8]`, which is slot 74, `setupImpact`.
The `Effect` prints `(this.impact, this, t65)`: the dead RCX value appears as an argument, the real receiver is `this` (in RDX), and 11 floating-point and stack arguments are dropped.
The computed damage argument is left as an empty `if (image != 230) {}`.
Call site: `Player::changeState`, 0x1802F5029.
A gap marker should either show the real receiver and arguments or say they are unknown.

## 19. 8-byte struct returns in RAX aren't rebuilt as structs

`UnityMain::BetterAnalog` is reported Complete, but returns `(Vector2)(ulong)…` built from `SingleToInt32Bits` bits.
That doesn't compile; it should be `new Vector2(…)`.
The logic itself matches the native code.

## 20. Smaller coverage gaps (fit the README's signature-library limits)

- An interface invoker with a double return (0x180003650, in `haxe.lang.Runtime::toDouble`) comes out typed `Opaque<long>`.
- The `IDisposable.Dispose` invoker at 0x180002550 is unidentified, so `request.Dispose()` in a `finally` is hidden.
- 0x1800A1160 (546 sites) looks like the array-type-mismatch exception getter.
- The covariance check isn't folded back into a plain array store.
- Metadata init at 0x1800A0BA0 is left as dead `if (1 == 0) {…}` blocks (1,503 sites, harmless).
- Inlined constructors aren't folded back (`GetUninitializedObject(typeof(Sound))` followed by a `.ctor` call); the README mentions this one.
