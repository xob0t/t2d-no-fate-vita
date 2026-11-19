# The fold-back passes

IL2CPP inlined small members of Unity 6's engine and of its class libraries (.NET's) into the game's methods, and Rewind recovered them as they were: binding calls (`Camera.get_pixelWidth_Injected(IntPtr)`), native-pointer plumbing (`Object.GetCachedPtr`), private fields (`String._firstChar`), internal helpers (`ThrowHelper`, `Number.FormatInt32`).
Unity 2018.2 has none of them, so UnityLinker stopped on the first.
These passes turn them back into the API 2018.2 has.

They run in the rewriter (`DowngradeAssemblies.Rewrite`): after the gap repairs and the platform layer, before the type retargeting and the other passes.
They read the Unity 6 names the game's references still have and look the members up in the target's (2018.2's) assemblies by full name.
They don't touch what the atlas passes match (`BitmapData`'s `texpack` reads), so those see the same code.

| File | What it folds |
| --- | --- |
| `Foldback.cs` | The entry (`FoldBack`), the stack flow of a body, putting what the passes imported back on the module's own assembly references |
| `Bindings.cs` | `GetCachedPtr`/`m_Ptr` handles, `*_Injected` calls, `Unmarshal.UnmarshalUnityObject` |
| `ClassLibrary.cs` | `String._firstChar`, missing fields, locals of missing types, `Number.Format*` with a `ReadOnlySpan` format |

Shims and stand-ins the passes call are in T2DCompat, `Unity6Shims.cs`.

## How general

The rules are shapes, not methods: no rule names a game method or type.

- A binding call `T::X_Injected(IntPtr self, ...)` maps to 2018.2's member from its name and signature alone: the instance member `X` or a static `X` with the object first; `XImpl` is also `X`; struct arguments by `ref`, struct results through a last `out`, engine objects as native pointers. One table, `BindingNames`, holds names that differ (`GL.GLClear` is `GL.Clear`).
- The handle analysis follows the value through the stack (`Stack.cs`) and through IntPtr locals and their copies, so the null checks and the calls can be anywhere in the body.
- Missing fields go to T2DCompat by the existing naming convention, extended to fields (`get_<Field>`, `set_<Field>`); `FieldRenames` holds fields 2018.2 has under another name, checked against its assemblies.
- A shape the rules don't know fails the method, all of it, with an `UNRESOLVED ... foldback:` line naming what was unexpected. A half-folded body never reaches the build. `T2D_FOLDBACK=0` skips the passes.

## The items and their rules

On the Steam release (build 24040403), master Rewind (ea8e305, no patches), with the platform layer and the texture pipeline merged; 61 unresolved references before, 0 after (with `T2D_REPAIRS=0`; with the repairs on, the fold-back leaves 0 too).
Sites are folds in `Recovered.T2D`; the rewriter's log lists them (`FOLDED` lines).
Categories are the assessment's (`docs/assessment.md`, "API gaps against Unity 2018.2").

### Engine bindings (U1a, U1b)

| Item | Sites | Rule |
| --- | --- | --- |
| `Object.GetCachedPtr()`, `MaterialPropertyBlock.m_Ptr` (handles) | 53 | The object itself; `== 0` becomes Unity's `Object == null` (a `MaterialPropertyBlock`, no engine object, a reference test) |
| `Camera.get_pixelWidth_Injected`, `get_pixelHeight_Injected` | 4, 4 | `Camera.pixelWidth`, `pixelHeight` |
| `Camera.get_depth_Injected` | 1 | `Camera.depth` |
| `Camera.get_rect_Injected(IntPtr, out Rect)` | 2 | `Camera.rect`, stored to the `out` local |
| `Component.get_gameObject_Injected` + `Unmarshal.UnmarshalUnityObject<GameObject>` | 1 | `Component.gameObject`; the Unmarshal goes |
| `AudioClip.get_length_Injected` | 1 | `AudioClip.length` |
| `AudioSource.get_isPlaying_Injected`, `set_panStereo_Injected`, `set_time_Injected`, `set_volume_Injected` | 1 each | The properties |
| `AudioSource.Play_Injected(IntPtr, double)` | 1 | T2DCompat `UnityEngine_AudioSource.Play(AudioSource, double)`: 2018.2 has no `Play(double)`; 0 is `Play()`, > 0 `PlayScheduled`, < 0 `PlayDelayed` |
| `AudioSource.PlayHelper_Injected(IntPtr, ulong)` | 1 | T2DCompat `PlayHelper`: `Play(ulong)` |
| `AudioSource.Stop_Injected(IntPtr, bool)` | 1 | T2DCompat `Stop`: `Stop()` (2018.2 can't keep one-shots playing) |
| `Material.SetColorImpl_Injected(IntPtr, int, ref Color)` | 2 | `Material.SetColor(int, Color)` |
| `MaterialPropertyBlock.SetColorImpl_Injected`, `SetVectorImpl_Injected` | 4, 4 | `SetColor(int, Color)`, `SetVector(int, Vector4)` |
| `MaterialPropertyBlock.Clear_Injected(IntPtr, bool)` | 5 | T2DCompat `UnityEngine_MaterialPropertyBlock.Clear`: `Clear()` (2018.2's `Clear(bool)` is private) |
| `RenderTexture.set_useMipMap_Injected` | 1 | `RenderTexture.useMipMap` |
| `Quaternion.AngleAxis_Injected(float, ref Vector3, out Quaternion)` | 4 | `Quaternion.AngleAxis` (resolved before, privately) |
| `GL.GLClear_Injected(bool, bool, ref Color, float)` | 2 | `GL.Clear` (resolved before, privately) |
| `UnityEngine.Bindings.ThrowHelper` (the null checks' throw paths) | | T2DCompat stand-in `UnityEngine_Bindings_ThrowHelper` (throws `NullReferenceException`, `ArgumentNullException`) |
| `UnityEngine.Bindings.Unmarshal` | | Gone with the binding calls; no stand-in (an IntPtr can't become an object in 2018.2) |

### Class library internals (B1)

| Item | Sites | Rule |
| --- | --- | --- |
| `&a._firstChar == &b._firstChar` (string equality's "same string" test) | 28 | The references compared: the two `ldflda` go |
| `String.FastAllocateString(1)` + `stfld _firstChar` (`char.ToString`) | 1 | `new string(c, 1)` |
| `String._firstChar`, address taken (character reads by pointer, inside Rewind's gaps) | 10 | 2018.2's `m_firstChar` (`FieldRenames`) |
| `CompareInfo.Invariant` (static field) | 1 | T2DCompat `System_Globalization_CompareInfo.get_Invariant()` |
| `CompareInfo.IndexOfOrdinal(string, string, int, int, bool)` | 1 | T2DCompat shim (`RedirectMembers`): `string.IndexOf(..., Ordinal or OrdinalIgnoreCase)` |
| `Decimal.ulomidLE` (store) | 2 | T2DCompat `System_Decimal.set_ulomidLE(ref decimal, ulong)` |
| `Random.GenerateSeed()` | 1 | T2DCompat shim (`RedirectMembers`) |
| `Number.FormatInt32(int, ReadOnlySpan<char>, IFormatProvider)` | 1 | A helper the pass writes into the game's assembly (`__Foldback.FormatInt32`): C# 5 rejects ref structs, so T2DCompat can't take a `ReadOnlySpan` |
| `System.ThrowHelper.ThrowIndexOutOfRangeException()` | | T2DCompat stand-in `System_ThrowHelper` (the retargeting sends the type there) |
| `SpanHelpers.SequenceEqual(ref byte, ref byte, ulong)` | | T2DCompat stand-in `System_SpanHelpers` |
| `OrdinalCaseSensitiveComparer` (a local's type) | 1 | The nearest base 2018.2 has, `StringComparer` |
| `System.RuntimeType` | | Nothing uses it in the written assembly: the retargeting now reports a missing type only if the written module still references it |

U1c, U1d and U2 items had no sites left after the platform layer (it replaces `UnityMain.OnPreRender`'s `RenderTextureDescriptor` code and the `UnityWebRequest` sound loading); InControl and Steamworks are no longer rewritten.

### Left, non-public but resolving (B2)

UnityLinker and IL2CPP resolve these; huntdown's Mono loadcheck would reject them.
All are in the class libraries; no engine internal is left.

| Member | Where it comes from |
| --- | --- |
| `String.m_firstChar` | The 10 address-taken reads above, inside Rewind gaps |
| `Decimal.flags`, `Decimal.hi` | The decimal built in place beside `ulomidLE` (`haxe.lang.Runtime.eq`) |
| `Array.IndexOfImpl<T>` | Inlined `Array.IndexOf<T>` |
| `RuntimeFieldHandle.value` | Rewind's array initializers |
| `Exception.Init()` | An inlined exception constructor |
| `CultureInfo..ctor(string, bool, bool)` | `cs.Lib.applyCultureChanges` |

## The build after the fold-back

- The code stage: 0 unresolved references.
- UnityLinker: passes.
- IL2CPP's C++ conversion: passes (157 C++ files).
- The C++ compile (psp2snc): stops in `Recovered.T2D`'s C++, on Rewind's typing rather than on API. Every one of the 20 `Bulk_Recovered.T2D_*.cpp` files fails, with at least 800 errors in all (psp2snc stops a file at 50 at `-O0`): integer arithmetic on a static field's address, the GC write barrier's card index (at least 395; at least 278 with the repair engine's barrier pattern), `RuntimeHelpers.InitializeArray` given an array held in an `int64` local (at least 250, the static initializers), and other object references held in `int64` locals.
- In a checkout whose path is longer than about 73 characters (this worktree's is 75), IL2CPP's compile step fails first with `PathTooLongException`: the PS Vita player's longest libil2cpp header path is 186 characters under the repository.
