// Stand-in for Rewind's internal __Rewind class for a runnable probe build (replaces
// compile2018/RewindStub.cs). A gap marker that runs throws NotSupportedException with the marker's
// text and logs it once with "REWIND_GAP", so a test player's log names every gap it hit.
// One pattern is known plumbing and is let through: the load of IL2CPP's incremental-GC flag
// (the write barrier's "is marking on?" test), which reads as 0 so the card-marking block is skipped.
// The probe's own allow list (T2D_PROBE_PASS, separated by '|') lets more markers through by
// prefix, each logged once with "REWIND_PASS".
internal static partial class __Rewind
{
    // 0x1810CD090, IL2CPP's GC "incremental marking" flag in this build's GameAssembly.dll.
    const string GcFlagLoad = "__load4(6460067984L)";

    static readonly System.Collections.Generic.HashSet<string> seen = new System.Collections.Generic.HashSet<string>();
    static string[] pass;

    static bool Passes(string what)
    {
        if (what == GcFlagLoad) return true;
        if (pass == null)
        {
            string env = System.Environment.GetEnvironmentVariable("T2D_PROBE_PASS");
            pass = string.IsNullOrEmpty(env) ? new string[0] : env.Split('|');
        }
        foreach (string p in pass)
            if (p.Length > 0 && what.StartsWith(p, System.StringComparison.Ordinal))
            {
                Log("REWIND_PASS " + what);
                return true;
            }
        return false;
    }

    static void Log(string line)
    {
        lock (seen)
        {
            if (!seen.Add(line)) return;
        }
        UnityEngine.Debug.Log(line + "\n" + System.Environment.StackTrace);
    }

    static System.Exception Gap(string kind, string what)
    {
        Log("REWIND_GAP " + kind + " " + what);
        return new System.NotSupportedException("REWIND_GAP " + kind + ": " + what);
    }

    internal static T Opaque<T>(string what)
    {
        if (Passes(what)) return default(T);
        throw Gap("Opaque", what);
    }

    internal static void Effect(string what)
    {
        if (Passes(what)) return;
        throw Gap("Effect", what);
    }

    internal static void Note(string what) { }
}
