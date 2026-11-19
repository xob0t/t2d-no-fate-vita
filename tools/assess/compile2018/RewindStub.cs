// Stand-in for the internal __Rewind class Rewind puts in each rebuilt assembly and leaves out of the
// C# it writes. Only for measuring whether the source compiles: a marker evaluates to default(T) and
// does nothing, so code compiled with this stub is not a working build of the method.
internal static partial class __Rewind
{
    internal static T Opaque<T>(string what) { return default(T); }
    internal static void Effect(string what) { }
    internal static void Note(string what) { }
}
