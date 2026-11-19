// Extra __Rewind members for the constructs mechfix.py rewrites (pass 2). Measuring only: these keep
// the source compiling, they don't restore what the rewritten construct did.
internal static partial class __Rewind
{
    // __ldtoken(<PrivateImplementationDetails>.HASH): the array-initializer data isn't in the source.
    internal static System.RuntimeFieldHandle FieldToken(string field) { return default(System.RuntimeFieldHandle); }

    internal static uint ComputeStringHash(string s)
    {
        uint hash = 2166136261u;
        if (s != null)
            for (int i = 0; i < s.Length; i++) hash = (s[i] ^ hash) * 16777619u;
        return hash;
    }

    // __ldftn / __ldvirtftn / &Method: a function pointer.
    internal static System.IntPtr Ftn(string method) { return System.IntPtr.Zero; }

    // x = (T)GetUninitializedObject(typeof(T)); x..ctor(args): a base constructor run on an object whose own
    // constructor IL2CPP inlined. C# has no way to say it.
    internal static void InlinedCtor(object obj, object[] args) { }

    // An opaque value (a long) given an object (objctor), or used as one (marker-cast, marker-throw).
    internal static long Ref(object o) { return 0L; }
    internal static T As<T>(long value) { return default(T); }
}
