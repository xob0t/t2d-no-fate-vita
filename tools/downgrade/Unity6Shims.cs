using System;
using System.Linq;
using UnityEngine;

// Public API of Unity 6 and .NET Standard 2.1 that the game's assemblies use and Unity 2018.2 lacks,
// written with 2018.2's (docs/assessment.md, "API gaps"). The rewriter sends missing types and members
// here by the naming convention in Compat2018.cs: a missing type Namespace.Name is T2DCompat.Namespace_Name,
// a missing member Namespace.Type::M a static T2DCompat.Namespace_Type.M with the instance first (by ref
// for structs), constructors as static methods named "ctor".
//
// Only the easy items are here. Left for the code repair, and reported by pipeline.py code as UNRESOLVED:
// the engine's inlined binding calls (*_Injected, GetCachedPtr), RenderTextureDescriptor's graphics
// formats (UnityMain.OnPreRender), UnityWebRequest.Result (a nested type), and
// Convert.ToBase64String(ReadOnlySpan<byte>) (InControl).
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
}
