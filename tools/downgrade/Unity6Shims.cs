using System;
using System.Linq;
using UnityEngine;

// Public API of Unity 6 and .NET Standard 2.1 that the game's assemblies use and Unity 2018.2 lacks,
// written with 2018.2's (docs/assessment.md, "API gaps"). The rewriter sends missing types and members
// here by the naming convention in Compat2018.cs: a missing type Namespace.Name is T2DCompat.Namespace_Name,
// a missing member Namespace.Type::M a static T2DCompat.Namespace_Type.M with the instance first (by ref
// for structs), constructors as static methods named "ctor".
//
// Also the shims and stand-ins of the fold-back passes (foldback/README.md): the engine's binding calls whose
// member 2018.2 lacks or keeps private, and the class library internals IL2CPP inlined into the game (stand-ins of
// internal types under their own names, accessors of missing fields, get_<Field>/set_<Field>).
//
// Part of T2DCompat (pipeline.COMPAT_SOURCES). The platform layer (input, saves, store stubs) is in
// compat/*.cs; keep the type names here out of there.

namespace T2DCompat
{
    // Mesh.RecalculateNormals(MeshUpdateFlags): 2018.2 has only the overload without flags, which
    // validates and notifies as Default does.
    [Flags]
    public enum UnityEngine_Rendering_MeshUpdateFlags
    {
        Default = 0,
        DontValidateIndices = 1,
        DontResetBoneBounds = 2,
        DontNotifyMeshUsers = 4,
        DontRecalculateBounds = 8,
    }

    public static class UnityEngine_Mesh
    {
        public static void RecalculateNormals(Mesh mesh, UnityEngine_Rendering_MeshUpdateFlags flags)
        {
            mesh.RecalculateNormals();
        }
    }

    // Object.FindObjectsByType (InControl): FindObjectsOfType, which leaves inactive objects out; with
    // Include, the scene objects among Resources.FindObjectsOfTypeAll. Sorted by instance ID when asked.
    public enum UnityEngine_FindObjectsInactive
    {
        Exclude = 0,
        Include = 1,
    }

    public enum UnityEngine_FindObjectsSortMode
    {
        None = 0,
        InstanceID = 1,
    }

    public static class UnityEngine_Object
    {
        public static UnityEngine.Object[] FindObjectsByType(
            Type type, UnityEngine_FindObjectsInactive inactive, UnityEngine_FindObjectsSortMode sort)
        {
            UnityEngine.Object[] found = inactive == UnityEngine_FindObjectsInactive.Include
                ? Resources.FindObjectsOfTypeAll(type).Where(InScene).ToArray()
                : UnityEngine.Object.FindObjectsOfType(type);
            if (sort == UnityEngine_FindObjectsSortMode.InstanceID)
                Array.Sort(found, (a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
            return found;
        }

        public static UnityEngine.Object[] FindObjectsByType(Type type, UnityEngine_FindObjectsSortMode sort)
        {
            return FindObjectsByType(type, UnityEngine_FindObjectsInactive.Exclude, sort);
        }

        static bool InScene(UnityEngine.Object found)
        {
            var component = found as Component;
            GameObject owner = component != null ? component.gameObject : found as GameObject;
            return owner != null && owner.scene.IsValid();
        }
    }

    // Resolution.refreshRateRatio: 2018.2's whole-number refreshRate.
    public struct UnityEngine_RefreshRate
    {
        public uint numerator;
        public uint denominator;

        public double value
        {
            get { return denominator == 0 ? 0 : (double)numerator / denominator; }
        }
    }

    public static class UnityEngine_Resolution
    {
        public static UnityEngine_RefreshRate get_refreshRateRatio(ref Resolution resolution)
        {
            return new UnityEngine_RefreshRate { numerator = (uint)resolution.refreshRate, denominator = 1 };
        }
    }

    // Texture2D's constructor with a mipmap limit (BitmapData): 2018.2 has no mipmap limits, and no
    // constructor that takes a mip count, so any count but 1 means a full chain.
    public struct UnityEngine_MipmapLimitDescriptor
    {
        public bool useMipmapLimit;
        public string groupName;
    }

    public static class UnityEngine_Texture2D
    {
        public static Texture2D ctor(
            int width, int height, TextureFormat format, int mipCount, bool linear, IntPtr nativeTexture,
            bool createUninitialized, UnityEngine_MipmapLimitDescriptor mipmapLimit)
        {
            return new Texture2D(width, height, format, mipCount != 1, linear);
        }
    }

    // Unity 6's Input.GetKey(string) and Input.GetAxisRaw(string) call InputUnsafeUtility, an internal class
    // of InputLegacyModule, and IL2CPP inlined them into InControl: back to 2018.2's Input.
    public static class UnityEngine_Internal_InputUnsafeUtility
    {
        public static bool GetKeyString(string name)
        {
            return Input.GetKey(name);
        }

        public static float GetAxisRaw(string axisName)
        {
            return Input.GetAxisRaw(axisName);
        }
    }

    // BitConverter.SingleToInt32Bits and Int32BitsToSingle (.NET Standard 2.1). Rewind packs 8-byte struct
    // returns with the first (docs/rewind-issues.md, issue 19), so most of their sites go when that is fixed.
    public static class System_BitConverter
    {
        public static int SingleToInt32Bits(float value)
        {
            return BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
        }

        public static float Int32BitsToSingle(int value)
        {
            return BitConverter.ToSingle(BitConverter.GetBytes(value), 0);
        }
    }

    // ---- The fold-back passes' shims (foldback/Bindings.cs): binding calls whose member 2018.2 lacks or keeps private.

    public static class UnityEngine_AudioSource
    {
        // Unity 6's private AudioSource.Play(double): 0 plays now, a positive value at that DSP time (PlayScheduled),
        // a negative one after that many seconds (PlayDelayed).
        public static void Play(AudioSource source, double delay)
        {
            if (delay == 0.0) source.Play();
            else if (delay > 0.0) source.PlayScheduled(delay);
            else source.PlayDelayed((float)-delay);
        }

        // Unity 6's static PlayHelper(source, delay) behind Play(ulong delay), which 2018.2 has public.
        public static void PlayHelper(AudioSource source, ulong delay)
        {
            source.Play(delay);
        }

        // Unity 6's Stop(bool stopOneShots): Stop() is Stop(true). 2018.2's Stop() also ends one-shots; it has no way
        // to keep them, so false stops them too.
        public static void Stop(AudioSource source, bool stopOneShots)
        {
            source.Stop();
        }
    }

    public static class UnityEngine_MaterialPropertyBlock
    {
        // Unity 6's private Clear(bool keepMemory), behind Clear() (keepMemory true); 2018.2 keeps its own private.
        public static void Clear(MaterialPropertyBlock block, bool keepMemory)
        {
            block.Clear();
        }
    }

    // UnityEngine.Bindings.ThrowHelper: the binding wrappers' null checks, where an engine object's native pointer
    // is zero (destroyed, or null).
    public static class UnityEngine_Bindings_ThrowHelper
    {
        public static void ThrowNullReferenceException(object obj)
        {
            throw new NullReferenceException();
        }

        public static void ThrowArgumentNullException(object obj, string parameterName)
        {
            throw new ArgumentNullException(parameterName);
        }
    }

    // ---- The fold-back passes' class library stand-ins (foldback/ClassLibrary.cs): internal types of .NET's class
    // libraries under their own names, missing members of public ones, missing fields as accessors.

    public static class System_ThrowHelper
    {
        public static void ThrowIndexOutOfRangeException()
        {
            throw new IndexOutOfRangeException();
        }

        public static void ThrowArgumentOutOfRangeException()
        {
            throw new ArgumentOutOfRangeException();
        }

        public static void ThrowArgumentOutOfRange_IndexException()
        {
            throw new ArgumentOutOfRangeException("index");
        }

        public static void ThrowInvalidOperationException_InvalidOperation_NoValue()
        {
            throw new InvalidOperationException("Nullable object must have a value.");
        }
    }

    public static class System_SpanHelpers
    {
        // Byte by byte, as .NET's does in effect: two runs of length bytes.
        public static unsafe bool SequenceEqual(ref byte first, ref byte second, ulong length)
        {
            fixed (byte* a = &first)
            fixed (byte* b = &second)
            {
                if (a == b) return true;
                for (ulong i = 0; i < length; i++)
                    if (a[i] != b[i]) return false;
            }
            return true;
        }
    }

    public static class System_Random
    {
        static int seeds = Environment.TickCount;

        // .NET's Random() seeds from GenerateSeed, a fresh value per instance.
        public static int GenerateSeed()
        {
            return System.Threading.Interlocked.Increment(ref seeds) * 1103515245;
        }
    }

    public static class System_Globalization_CompareInfo
    {
        // The static field CompareInfo.Invariant.
        public static System.Globalization.CompareInfo get_Invariant()
        {
            return System.Globalization.CultureInfo.InvariantCulture.CompareInfo;
        }

        public static int IndexOfOrdinal(System.Globalization.CompareInfo info, string source, string value, int startIndex, int count, bool ignoreCase)
        {
            return source.IndexOf(value, startIndex, count, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
    }

    public static class System_Decimal
    {
        // .NET's decimal keeps the low and middle 32 bits as one ulong, ulomid; Mono's as lo and mid.
        public static void set_ulomidLE(ref decimal value, ulong bits)
        {
            int[] parts = decimal.GetBits(value);
            value = new decimal((int)bits, (int)(bits >> 32), parts[2], parts[3] < 0, (byte)((parts[3] >> 16) & 0xFF));
        }
    }
}
