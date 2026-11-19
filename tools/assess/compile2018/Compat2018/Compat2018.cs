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

    // COMPILE-ONLY: System.Runtime.CompilerServices.Unsafe is internal in 2018.2's mscorlib. Its real
    // bodies are IL that C# can't express; the IL-only System.Runtime.CompilerServices.Unsafe.dll
    // package would supply them.
    public static unsafe class Unsafe
    {
        public static void* AsPointer<T>(ref T value) { throw new NotSupportedException(); }
        public static bool AreSame<T>(ref T left, ref T right) { throw new NotSupportedException(); }
        public static ref TTo As<TFrom, TTo>(ref TFrom source) { throw new NotSupportedException(); }
        public static T As<T>(object o) where T : class { return (T)o; }
        public static ref T Add<T>(ref T source, int elementOffset) { throw new NotSupportedException(); }
        public static ref T AddByteOffset<T>(ref T source, IntPtr byteOffset) { throw new NotSupportedException(); }
    }

    public static class Engine
    {
        public static void ThrowNullReferenceException(object obj) { throw new NullReferenceException(); }
        public static void ThrowArgumentNullException(object obj, string parameterName) { throw new ArgumentNullException(parameterName); }

        // COMPILE-ONLY: native pointers of engine objects (Object.m_CachedPtr, m_Ptr fields).
        static IntPtr s_ptr;
        public static IntPtr GetCachedPtr(this UnityEngine.Object o) { throw new NotSupportedException(); }
        public static ref IntPtr Ptr(object o) { return ref s_ptr; }
        public static T UnmarshalUnityObject<T>(IntPtr ptr) where T : UnityEngine.Object { throw new NotSupportedException(); }
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
