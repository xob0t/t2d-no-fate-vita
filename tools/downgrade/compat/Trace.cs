// The game methods entered last, for a build made with T2D_TRACE=1 (the rewriter's TraceMethods pass calls Enter at
// the start of every method of Recovered.T2D). Unity 2018.2's PS Vita player walks the stack with
// sceKernelBacktraceSelf, which a retail console doesn't answer, so an exception there has no frames; the log
// (Log.cs) writes this trail under each new error instead: the methods entered before it, the last one first. The
// last entered is usually the one that threw, or one it called that returned. Each thread keeps its own trail, and
// the log reads the one of the thread that logs the error. A normal build has no calls to it.
// Part of T2DCompat; see Compat2018.cs.
namespace T2DCompat
{
    public static class Trace
    {
        const int Size = 64;
        [System.ThreadStatic] static string[] entered;
        [System.ThreadStatic] static int next;

        // True once a game method has recorded itself, so only in a T2D_TRACE=1 build: T2DCompat's own entry points
        // join the trail with `if (Trace.On) Trace.Enter(...)`, at the cost of a test otherwise.
        public static bool On;

        public static void Enter(string method)
        {
            On = true;
            string[] trail = entered ?? (entered = new string[Size]);
            trail[next] = method;
            next = (next + 1) & (Size - 1);
        }

        // The last count methods entered, the last first, one a line; empty when nothing was traced.
        public static string Trail(int count)
        {
            string[] trail = entered;
            if (trail == null) return "";
            var text = new System.Text.StringBuilder();
            for (int i = 1; i <= count && i <= Size; i++)
            {
                string method = trail[(next - i) & (Size - 1)];
                if (method == null) break;
                text.Append("  < ").Append(method).Append('\n');
            }
            return text.ToString();
        }
    }
}
