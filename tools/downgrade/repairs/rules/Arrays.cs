using Repairs;

// new T[n] that Rewind leaves as a call to Array::NewSpecific (0x1800E2FD0): the element type comes
// from the class the native code passes (its metadata usage), the length from the marker.
static partial class Rules
{
    // T800Pistol..ctor: rcx = TI_int[] at 0x1803E0FC0, edx = 8.
    static void T800PistolCtor(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.anims.T800Pistol::.ctor()", null);
        Site s = m.Site("__native_0x1800E2FD0(", null);
        s.With(il => il.I4((int)s.Marker.IntArg(1)).Newarr("System.Int32"));
        m.RetypeLocal(0, "System.Int32[]");
    }
}
