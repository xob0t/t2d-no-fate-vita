using System;
using System.Runtime.InteropServices;
using UnityEngine;

// When the managed heap is collected. Unity 2018.2's IL2CPP has Boehm's collector with no incremental mode, so each
// collection stops the game for the whole mark: 110 to 170 ms with the game's 21 MB heap on the PS Vita. Boehm
// collects once the bytes allocated since the last collection pass the live heap's scan size over
// GC_free_space_divisor (3 by default), about 4 MB here; Future War 3's rain (Script.addRain, a new Rain per drop)
// allocates 100 to 130 KB a second, so that level stopped every 28 to 30 s, against every 3 to 10 minutes elsewhere.
// Two things move the collections out of play:
// - Divisor: GC_set_free_space_divisor(1), so a collection waits for three times as much allocation (about 12 MB;
//   the heap grows by as much). It's in the player's bdwgc.a, called through DllImport("__Internal").
// - CollectAtEnter: each App's Enter (Atlas.EnterApp, under the fade's covered screen) collects once the heap has
//   grown CollectMinBytes since the last collection, so a level starts with a fresh count.
// Part of T2DCompat; see Compat2018.cs.
namespace T2DCompat
{
    public static class Gc
    {
        // Boehm's free-space divisor on the PS Vita, set at the first Enter; 0 leaves it as it is (3).
        public static int Divisor = 1;
        public static bool CollectAtEnter = true;
        public static long CollectMinBytes = 1L << 20;

        static bool divisorSet;
        static long live;   // the heap after our last collection: about what is live, whichever collection came since

        [DllImport("__Internal")]
        static extern void GC_set_free_space_divisor(UIntPtr value);

        // From Atlas.EnterApp: the next App was made under the covered screen and hasn't drawn yet.
        public static void AtEnter(string app)
        {
            if (!divisorSet)
            {
                divisorSet = true;
                if (Divisor > 0 && Application.platform == RuntimePlatform.PSP2)
                {
                    try
                    {
                        GC_set_free_space_divisor((UIntPtr)(uint)Divisor);
                        Debug.Log("T2D gc: free space divisor " + Divisor);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("T2D gc: no GC_set_free_space_divisor (" + e.GetType().Name + ")");
                    }
                }
            }
            if (!CollectAtEnter) return;
            long used = GC.GetTotalMemory(false);
            if (used - live < CollectMinBytes) return;
            float start = Time.realtimeSinceStartup;
            GC.Collect();
            live = GC.GetTotalMemory(false);
            Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "T2D gc: collected at app:{0}, {1:0.0} -> {2:0.0} MB, {3:0} ms", app, used / 1048576.0, live / 1048576.0,
                (Time.realtimeSinceStartup - start) * 1000));
        }
    }
}
