// Compat2018 for a runnable probe build (tools/assess/bringup): compile2018/Compat2018/Compat2018.cs
// with working bodies for the engine plumbing that was COMPILE-ONLY there. The game DLLs are compiled
// against this file, so the signatures must stay those of the compile-only version.
//  - Engine.GetCachedPtr returns a fake native pointer: a key in Registry (the object's instance ID),
//    0 for a null or destroyed object, as m_CachedPtr is; Engine.Ptr does the same for engine classes
//    that aren't UnityEngine.Object (MaterialPropertyBlock, AnimationCurve, UnityWebRequest).
//  - Injected.cs maps each *_Injected binding call back to 2018.2's public API through Registry.
// Unsafe is made of DynamicMethods; the Delegate field accessors still share one static slot
// (Steamworks' hooks only).
// Compat2018: the members Unity 2018.2's libraries lack that Rewind's C# names, for measuring how close
// the source gets to compiling (mechfix.py redirects the call sites here). C# 7.3, built against
// 2018.2's unityaot and engine modules.
//
// Two kinds of member:
//  - real: a working equivalent (BitConverter bits, ThrowHelper, SpanHelpers.SequenceEqual, the enums
//    and attribute types);
//  - compile-only (marked COMPILE-ONLY): inlined engine and runtime internals (native pointers,
//    *_Injected binding calls, Delegate fields, Unsafe). They have no meaning outside Unity 6's
//    runtime; a port folds the call sites back into public API, by hand or by a rewriter pass.
using System;
using System.Runtime.CompilerServices;

namespace Compat2018
{
    public static class BCL
    {
        public static unsafe int SingleToInt32Bits(float value) { return *(int*)&value; }
        public static unsafe float Int32BitsToSingle(int value) { return *(float*)&value; }

        // String._firstChar: the string's first char, by reference. 2018's GC doesn't move objects.
        public static unsafe ref char FirstChar(string s)
        {
            fixed (char* p = s) { return ref *p; }
        }

        public static unsafe bool SequenceEqual(ref byte first, ref byte second, ulong length)
        {
            fixed (byte* a = &first)
            fixed (byte* b = &second)
            {
                for (ulong i = 0; i < length; i++)
                    if (a[i] != b[i]) return false;
            }
            return true;
        }

        public static void ThrowArgumentNullException(object argument) { throw new ArgumentNullException(argument == null ? null : argument.ToString()); }
        public static void ThrowArgumentOutOfRange_IndexException() { throw new ArgumentOutOfRangeException("index"); }
        public static void ThrowIndexOutOfRangeException() { throw new IndexOutOfRangeException(); }
        public static void ThrowInvalidOperationException_InvalidOperation_NoValue() { throw new InvalidOperationException("Nullable object must have a value."); }

        // object.MemberwiseClone on another object (IL2CPP inlined a protected call): through reflection.
        static readonly System.Reflection.MethodInfo s_clone = typeof(object).GetMethod("MemberwiseClone",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        public static object MemberwiseClone(object o) { return s_clone.Invoke(o, null); }

        // COMPILE-ONLY: Mono's private Delegate fields.
        static IntPtr s_ptr;
        static object s_obj;
        public static ref IntPtr Delegate_method_ptr(Delegate d) { return ref s_ptr; }
        public static ref IntPtr Delegate_method_code(Delegate d) { return ref s_ptr; }
        public static ref IntPtr Delegate_method(Delegate d) { return ref s_ptr; }
        public static ref IntPtr Delegate_invoke_impl(Delegate d) { return ref s_ptr; }
        public static ref IntPtr Delegate_extra_arg(Delegate d) { return ref s_ptr; }
        public static ref object Delegate_m_target(Delegate d) { return ref s_obj; }
    }

    // System.Runtime.CompilerServices.Unsafe is internal in 2018.2's mscorlib and its bodies are IL that
    // C# can't express, so each method is a DynamicMethod made once per type argument (the Mono player
    // JITs them; an IL2CPP or PS Vita build would need the IL-only package instead).
    public static unsafe class Unsafe
    {
        public static void* AsPointer<T>(ref T value) { return (void*)UnsafeIL<T>.AsPointer(ref value); }
        public static bool AreSame<T>(ref T left, ref T right) { return UnsafeIL<T>.AreSame(ref left, ref right); }
        public static ref TTo As<TFrom, TTo>(ref TFrom source) { return ref UnsafeAs<TFrom, TTo>.As(ref source); }
        public static T As<T>(object o) where T : class { return (T)o; }
        public static ref T Add<T>(ref T source, int elementOffset) { return ref UnsafeIL<T>.Add(ref source, elementOffset); }
        public static ref T AddByteOffset<T>(ref T source, IntPtr byteOffset) { return ref UnsafeIL<T>.AddByteOffset(ref source, byteOffset); }
    }

    static class UnsafeIL<T>
    {
        public delegate IntPtr AsPointerFn(ref T value);
        public delegate bool AreSameFn(ref T left, ref T right);
        public delegate ref T AddFn(ref T source, int elementOffset);
        public delegate ref T AddByteOffsetFn(ref T source, IntPtr byteOffset);
        public static readonly AsPointerFn AsPointer = Make<AsPointerFn>(typeof(IntPtr), new[] { typeof(T).MakeByRefType() },
            il => { il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Conv_U); });
        public static readonly AreSameFn AreSame = Make<AreSameFn>(typeof(bool), new[] { typeof(T).MakeByRefType(), typeof(T).MakeByRefType() },
            il => { il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1); il.Emit(System.Reflection.Emit.OpCodes.Ceq); });
        public static readonly AddFn Add = Make<AddFn>(typeof(T).MakeByRefType(), new[] { typeof(T).MakeByRefType(), typeof(int) },
            il =>
            {
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
                il.Emit(System.Reflection.Emit.OpCodes.Sizeof, typeof(T));
                il.Emit(System.Reflection.Emit.OpCodes.Conv_I);
                il.Emit(System.Reflection.Emit.OpCodes.Mul);
                il.Emit(System.Reflection.Emit.OpCodes.Add);
            });
        public static readonly AddByteOffsetFn AddByteOffset = Make<AddByteOffsetFn>(typeof(T).MakeByRefType(), new[] { typeof(T).MakeByRefType(), typeof(IntPtr) },
            il => { il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1); il.Emit(System.Reflection.Emit.OpCodes.Add); });

        internal static D Make<D>(Type ret, Type[] args, Action<System.Reflection.Emit.ILGenerator> body) where D : class
        {
            // Through reflection: the unityaot profile compiled against has DynamicMethod only as an abstract
            // stand-in; the Mono player's unityjit mscorlib has the real one.
            Type dm = typeof(System.Reflection.Emit.OpCodes).Assembly.GetType("System.Reflection.Emit.DynamicMethod", true);
            object m = Activator.CreateInstance(dm, "Unsafe_" + typeof(D).Name, ret, args, typeof(UnsafeIL<T>).Module, true);
            var il = (System.Reflection.Emit.ILGenerator)dm.GetMethod("GetILGenerator", Type.EmptyTypes).Invoke(m, null);
            body(il);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            return (D)dm.GetMethod("CreateDelegate", new[] { typeof(Type) }).Invoke(m, new object[] { typeof(D) });
        }
    }

    static class UnsafeAs<TFrom, TTo>
    {
        public delegate ref TTo AsFn(ref TFrom source);
        public static readonly AsFn As = UnsafeIL<TFrom>.Make<AsFn>(typeof(TTo).MakeByRefType(), new[] { typeof(TFrom).MakeByRefType() },
            il => il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0));
    }

    public static class Engine
    {
        public static void ThrowNullReferenceException(object obj) { throw new NullReferenceException(); }
        public static void ThrowArgumentNullException(object obj, string parameterName) { throw new ArgumentNullException(parameterName); }

        public static IntPtr GetCachedPtr(this UnityEngine.Object o) { return Registry.Of(o); }

        sealed class Cell { public IntPtr Value; }
        static readonly ConditionalWeakTable<object, Cell> s_cells = new ConditionalWeakTable<object, Cell>();
        public static ref IntPtr Ptr(object o)
        {
            Cell cell = s_cells.GetOrCreateValue(o);
            cell.Value = Registry.Of(o);
            return ref cell.Value;
        }

        public static T UnmarshalUnityObject<T>(IntPtr ptr) where T : UnityEngine.Object { return Registry.Get<T>(ptr); }
    }

    // Fake native pointers: instance IDs for UnityEngine.Object (never 0 for a live object), a counter
    // from 2^40 up for other engine objects.
    public static class Registry
    {
        static readonly System.Collections.Generic.Dictionary<long, object> s_objects = new System.Collections.Generic.Dictionary<long, object>();
        static readonly ConditionalWeakTable<object, object> s_ids = new ConditionalWeakTable<object, object>();
        static long s_next = 1L << 40;

        public static IntPtr Of(object o)
        {
            if (o == null) return IntPtr.Zero;
            var u = o as UnityEngine.Object;
            if ((object)u != null)
            {
                if (u == null) return IntPtr.Zero;  // destroyed
                long id = u.GetInstanceID();
                s_objects[id] = u;
                return (IntPtr)id;
            }
            object boxed;
            if (!s_ids.TryGetValue(o, out boxed))
            {
                boxed = s_next++;
                s_ids.Add(o, boxed);
                s_objects[(long)boxed] = o;
            }
            return (IntPtr)(long)boxed;
        }

        public static T Get<T>(IntPtr ptr) where T : class
        {
            if (ptr == IntPtr.Zero) return null;
            object o;
            if (!s_objects.TryGetValue((long)ptr, out o)) throw new InvalidOperationException("Compat2018.Registry: unknown pointer " + (long)ptr);
            return (T)o;
        }
    }

    // Public Unity 6 API that 2018.2 lacks.
    [Flags]
    public enum MeshUpdateFlags
    {
        Default = 0,
        DontValidateIndices = 1,
        DontResetBoneBounds = 2,
        DontNotifyMeshUsers = 4,
        DontRecalculateBounds = 8,
    }

    public struct MipmapLimitDescriptor
    {
        public bool useMipmapLimit;
        public string groupName;
        public MipmapLimitDescriptor(bool useMipmapLimit, string groupName) { this.useMipmapLimit = useMipmapLimit; this.groupName = groupName; }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class InspectorNameAttribute : Attribute
    {
        public readonly string displayName;
        public InspectorNameAttribute(string displayName) { this.displayName = displayName; }
    }

    public static class MeshExtensions
    {
        public static void RecalculateNormals(this UnityEngine.Mesh mesh, MeshUpdateFlags flags) { mesh.RecalculateNormals(); }
    }
}
