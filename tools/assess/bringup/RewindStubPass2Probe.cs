// compile2018/RewindStubPass2.cs's members for a runnable probe build: the same stand-ins, but each
// logs its first use with "REWIND_SILENT" (they keep the code running without restoring what the
// construct did, so they are where a silent error would come from).
internal static partial class __Rewind
{
    internal static System.RuntimeFieldHandle FieldToken(string field)
    {
        Log("REWIND_SILENT FieldToken " + field);
        return default(System.RuntimeFieldHandle);
    }

    // arrayfold.py's RuntimeHelpers.InitializeArray(array, FieldToken(hash)): the field's bytes, as
    // FieldData wrote them to <T2D_PROBE_FIELDDATA>/<hash>.bin from Rewind's assembly.
    internal static void InitArray(System.Array array, string field)
    {
        string dir = System.Environment.GetEnvironmentVariable("T2D_PROBE_FIELDDATA");
        string path = dir == null ? null : System.IO.Path.Combine(dir, field + ".bin");
        if (path == null || !System.IO.File.Exists(path))
            throw Gap("InitArray", field + " (no " + (path ?? "T2D_PROBE_FIELDDATA") + ")");
        byte[] data = System.IO.File.ReadAllBytes(path);
        System.Buffer.BlockCopy(data, 0, array, 0, System.Math.Min(data.Length, System.Buffer.ByteLength(array)));
    }

    internal static uint ComputeStringHash(string s)
    {
        uint hash = 2166136261u;
        if (s != null)
            for (int i = 0; i < s.Length; i++) hash = (s[i] ^ hash) * 16777619u;
        return hash;
    }

    internal static System.IntPtr Ftn(string method)
    {
        Log("REWIND_SILENT Ftn " + method);
        return System.IntPtr.Zero;
    }

    internal static void InlinedCtor(object obj, object[] args)
    {
        Log("REWIND_SILENT InlinedCtor " + (obj == null ? "null" : obj.GetType().FullName));
    }

    internal static long Ref(object o)
    {
        Log("REWIND_SILENT Ref " + (o == null ? "null" : o.GetType().FullName));
        return 0L;
    }

    internal static T As<T>(long value)
    {
        Log("REWIND_SILENT As<" + typeof(T).Name + ">");
        return default(T);
    }
}
