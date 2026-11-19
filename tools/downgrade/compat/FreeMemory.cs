using System;
using System.Linq;
using System.Reflection;

// Free memory as the PS Vita player reports it: main memory (LPDDR), video memory (CDRAM) and physically contiguous
// memory, from UnityEngine.PSVita.Diagnostics, read by reflection since T2DCompat is built for Windows too
// (asset_rules.PLATFORM_LINK_PRESERVE keeps the class). The probe logs it; the atlas sizes its budget by it.
// Part of T2DCompat; see Compat2018.cs.
namespace T2DCompat
{
    public static class FreeMemory
    {
        public const int Lpddr = 0, Cdram = 1, Physcont = 2;
        public static readonly string[] Names = { "LPDDR", "CDRAM", "PHYSCONT" };
        static readonly MethodInfo[] getters = Find();

        static MethodInfo[] Find()
        {
            var found = new MethodInfo[Names.Length];
            try
            {
                Type diagnostics = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("UnityEngine.PSVita.Diagnostics", false)).FirstOrDefault(t => t != null);
                if (diagnostics != null)
                    for (int i = 0; i < Names.Length; i++)
                        found[i] = diagnostics.GetMethod("GetFreeMemory" + Names[i], BindingFlags.Static | BindingFlags.Public);
            }
            catch (Exception)
            {
            }
            return found;
        }

        // Whether this player reports the kind (only the PS Vita's does).
        public static bool Known(int kind)
        {
            return getters[kind] != null;
        }

        // Free bytes of the kind, or -1 when the player doesn't report it.
        public static long Bytes(int kind)
        {
            MethodInfo getter = getters[kind];
            if (getter == null) return -1;
            try
            {
                return Convert.ToInt64(getter.Invoke(null, null));
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }
}
