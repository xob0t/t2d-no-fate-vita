using Repairs;

// new T[n] that Rewind leaves as a call to Array::NewSpecific (0x1800E2FD0): the element type comes
// from the class the native code passes (its metadata usage), the length from the marker.
static partial class Rules
{
    // T800Pistol..ctor: rcx = TI_int[] at 0x1803E0FC0, edx = 8.
    static void T800PistolCtor(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.anims.T800Pistol::.ctor()", "f1466012");
        m.Site("__native_0x1800E2FD0(", "5b0ca50c").NewArray("System.Int32");
        m.RetypeLocal(0, "System.Int32[]");
    }

    // Gfx.saturateImage (0x180283320): rcx = TI_double[], edx = 20 at 0x180283378; the stores and loads
    // of the color matrix are the markers' own text, compiled with t0 = that array (V0) and the int64
    // temporaries t2..t19 = V2..V19.
    static void GfxSaturateImage(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.Gfx::saturateImage(BitmapData,Double)", "ac93eb03");
        m.Site("__native_0x1800E2FD0(", "5f2ce643").NewArray("System.Double");
        m.RetypeLocal(0, "System.Double[]");
        m.BindSame(0, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16);
        m.CompileAll("__store8(", 10, "e30ef99c 0f46cf76 81928cc0 333ceebf 749d71cd 1af1290d 22999ed7 42607997 9746e176 54b67256");
        m.CompileAll("__load8(", 15, "e85cf64b d3cbb0be b2ea6540 7ce9cc36 7319d403 c8d7579d 85766fa7 314e3861 dbcd0c4c cc17e689 3afd7918 b01e0baf 866e5429 0d321944 a98ec705");
        m.CompileAll("__bits_to_double(", 20, "4391ea55 bd47bd43 d54cd8f9 6796de33 65aee676 ea1e118d cd7702f6 88b37335 3b7794b4 b8114aa3 76d7f82b 1ec92b6e 0099569d 910907ce 7a10748a 2b805ca7 c26ed77e 28db2359 f7fcc075 7688fca3");
    }
}
