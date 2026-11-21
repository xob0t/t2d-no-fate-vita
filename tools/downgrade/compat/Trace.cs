// The game methods entered last, for a build made with T2D_TRACE=1 (the rewriter's TraceMethods pass calls Enter at
// the start of every method of Recovered.T2D). Unity 2018.2's PS Vita player walks the stack with
// sceKernelBacktraceSelf, which a retail console doesn't answer, so an exception there has no frames; the log
// (Log.cs) writes this trail under each new error instead: the methods entered before it, the last one first. The
// last entered is usually the one that threw, or one it called that returned. Each thread keeps its own trail, and
// the log reads the one of the thread that logs the error. The probe's stall sampler (Probe.cs) reads the main
// thread's from its own thread (MarkMain, MainTrail): a racy read, good enough to say where a long frame is. A normal
// build has no calls to it.
// Part of T2DCompat; see Compat2018.cs.
namespace T2DCompat
{
    public static class Trace
    {
        const int Size = 64;

        sealed class Ring
        {
            public readonly string[] Names = new string[Size];
            public int Next;
            public long Count;
        }

        [System.ThreadStatic] static Ring ring;
        static Ring main;

        // True once a game method has recorded itself, so only in a T2D_TRACE=1 build: T2DCompat's own entry points
        // join the trail with `if (Trace.On) Trace.Enter(...)`, at the cost of a test otherwise.
        public static bool On;

        public static void Enter(string method)
        {
            On = true;
            Ring r = ring ?? (ring = new Ring());
            if (Allocs && r == main) Charge(method);
            r.Names[r.Next] = method;
            r.Next = (r.Next + 1) & (Size - 1);
            r.Count++;
        }

        // Called on the main thread: its trail becomes the one MainTrail reads.
        public static void MarkMain()
        {
            main = ring ?? (ring = new Ring());
        }

        // The last count methods entered on this thread, the last first, one a line; empty when nothing was traced.
        public static string Trail(int count)
        {
            return Lines(ring, count);
        }

        // The main thread's, from any thread.
        public static string MainTrail(int count)
        {
            return Lines(main, count);
        }

        // How many methods the main thread has entered so far (0 in a normal build).
        public static long MainCount
        {
            get { Ring r = main; return r == null ? 0 : r.Count; }
        }

        // The probe's -t2d-allocs: on the main thread, each growth of the managed heap is charged to the method entered
        // last before it. The heap grows a block (4 KB) at a time, whichever allocation takes it, so a method's share
        // over many frames is about what it allocates. Probe.cs logs the top of the list (AllocReport).
        public static bool Allocs;

        static readonly System.Collections.Generic.Dictionary<string, long[]> allocBy =
            new System.Collections.Generic.Dictionary<string, long[]>();
        static long lastUsed = -1;
        static string lastMethod;

        static void Charge(string method)
        {
            long used = System.GC.GetTotalMemory(false);
            if (lastMethod != null && lastUsed >= 0 && used > lastUsed)
            {
                long[] sum;
                if (!allocBy.TryGetValue(lastMethod, out sum)) allocBy[lastMethod] = sum = new long[1];
                sum[0] += used - lastUsed;
            }
            lastUsed = used;
            lastMethod = method;
        }

        // The methods charged most since the last report, in KB a second over the seconds given, and clears the list.
        public static string AllocReport(double seconds, int top)
        {
            var list = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, long>>();
            long total = 0;
            foreach (var e in allocBy)
            {
                list.Add(new System.Collections.Generic.KeyValuePair<string, long>(e.Key, e.Value[0]));
                total += e.Value[0];
                e.Value[0] = 0;
            }
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var text = new System.Text.StringBuilder();
            text.Append((total / 1024.0 / seconds).ToString("F1", inv)).Append(" KB/s:");
            for (int i = 0; i < list.Count && i < top && list[i].Value > 0; i++)
                text.Append(' ').Append(list[i].Key).Append('=').Append((list[i].Value / 1024.0 / seconds).ToString("F1", inv));
            return text.ToString();
        }

        static string Lines(Ring r, int count)
        {
            if (r == null) return "";
            int next = r.Next;
            var text = new System.Text.StringBuilder();
            for (int i = 1; i <= count && i <= Size; i++)
            {
                string method = r.Names[(next - i) & (Size - 1)];
                if (method == null) break;
                text.Append("  < ").Append(method).Append('\n');
            }
            return text.ToString();
        }
    }
}
