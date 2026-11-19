using Repairs;

// new T[n] that Rewind leaves as a call to Array::NewSpecific (0x1800E2FD0): the element type comes
// from the class the native code passes (its metadata usage), the length from the marker.
static partial class Rules
{
    // T800Pistol..ctor: rcx = TI_int[] at 0x1803E0FC0, edx = 8.
    static void T800PistolCtor(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.anims.T800Pistol::.ctor()", null);
        m.Site("__native_0x1800E2FD0(", null).NewArray("System.Int32");
        m.RetypeLocal(0, "System.Int32[]");
    }

    // Gfx.saturateImage (0x180283320): rcx = TI_double[], edx = 20 at 0x180283378; the stores and loads
    // of the color matrix are the markers' own text, compiled with t0 = that array (V0) and the int64
    // temporaries t2..t19 = V2..V19.
    static void GfxSaturateImage(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.Gfx::saturateImage(BitmapData,Double)", null);
        m.Site("__native_0x1800E2FD0(", null).NewArray("System.Double");
        m.RetypeLocal(0, "System.Double[]");
        m.BindSame(0, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16);
        m.CompileAll("__store8(", 10, null);
        m.CompileAll("__load8(", 15, null);
        m.CompileAll("__bits_to_double(", 20, null);
    }
}
