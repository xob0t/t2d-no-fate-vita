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
