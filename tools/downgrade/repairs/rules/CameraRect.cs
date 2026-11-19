using Mono.Cecil;
using Mono.Cecil.Cil;
using Repairs;

// Camera.rect's setter through a cached internal-call pointer (Rewind issue 28, open): Unity 6 resolves
// Camera::set_rect_Injected once, keeps the function in a static (0x1810CFE90), and calls it with the camera's native
// pointer and the address of a Rect on the stack. Rewind keeps the Rect as two int64 stack slots, x|y<<32 and
// width|height<<32, and loses the call. Read in the native code of both methods: the rect at [rsp+0x20] is filled
// as x, y, width, height (movss into +0x20, +0x24, +0x28, +0x2c) from UnityMain.m_MainCamera's aspect, then
// `lea rdx, [rsp+0x20]; mov rcx, <camera's cached ptr>; call rax`, and the camera is m_MainCamera on both paths.
// The repair: the cached pointer counts as resolved (its resolve path goes), and the call becomes
// m_MainCamera.rect = new Rect(x, y, width, height), each float the bits of its half of the two slots.
// Until the Rewind fix lands; then these rules fail with "Rewind recovers it now" and go.
static partial class Rules
{
    // UnityMain.ScreenStuff: letterboxes the main camera to 16:9 (from Start); the rect is V16 (x, y), V17 (w, h).
    static void UnityMainScreenStuff(Engine e)
    {
        MethodRepair m = e.Method("com.twissgreen.unity.UnityMain::ScreenStuff()", "b62dc80c");
        m.Site("__load8(6460079760L)", 0, "8166cf8a").With(il => il.I8(1));
        m.Site("__load8(6460079760L)", 0, "dd323579").With(il => il.I8(1));
        m.Site("__store8(6460079760L, __nativecode())", "87d831e8").Delete();
        m.Site("__nativecode()", "637ab4ac").Delete();
        VariableDefinition bits = m.NewLocal("System.Int32");
        m.Site("__call_indirect(m#, m#, __frame(), conv.u8(m#), conv.u8(m#))", "e734431c").With(il => SetMainCameraRect(il, 16, 17, bits));
        foreach (Site s in m.Sites("__unreachable()", 3, "cef3bdb4 77a3e9e3 8cddb9b2")) s.Delete();
    }

    // UnityMain.FixedUpdate: the same after a change of screen size; the rect is V30 (x, y), V31 (w, h).
    static void UnityMainFixedUpdate(Engine e)
    {
        MethodRepair m = e.Method("com.twissgreen.unity.UnityMain::FixedUpdate()", "bc1a9f00");
        m.Site("__load8(6460079760L)", 0, "24568c99").With(il => il.I8(1));
        m.Site("__load8(6460079760L)", 0, "c619063b").With(il => il.I8(1));
        m.Site("__store8(6460079760L, __nativecode())", "b2f1263f").Delete();
        m.Site("__nativecode()", "637ab4ac").Delete();
        VariableDefinition bits = m.NewLocal("System.Int32");
        m.Site("__call_indirect(m#, m#, __frame(), conv.u8(m#), conv.u8(m#))", "349da4d3").With(il => SetMainCameraRect(il, 30, 31, bits));
        foreach (Site s in m.Sites("__unreachable()", 3, "a54b3225 e352107c d5ba3d85")) s.Delete();
    }

    // UnityMain.m_MainCamera.rect = new Rect(x, y, width, height) from the two slots.
    static void SetMainCameraRect(IL il, int xy, int wh, VariableDefinition bits)
    {
        il.Ldsfld("com.twissgreen.unity.UnityMain::m_MainCamera");
        Half(il, xy, false, bits);
        Half(il, xy, true, bits);
        Half(il, wh, false, bits);
        Half(il, wh, true, bits);
        il.Newobj("UnityEngine.Rect::.ctor(Single,Single,Single,Single)");
        il.Callvirt("UnityEngine.Camera::set_rect(Rect)");
    }

    // Pushes the float whose bits are the low (or high) half of int64 local `slot`, through an int32 local.
    static void Half(IL il, int slot, bool high, VariableDefinition bits)
    {
        il.Ldloc(slot);
        if (high) il.I4(32).Op(OpCodes.Shr_Un);
        il.Op(OpCodes.Conv_U4).Stloc(bits);
        il.Emit(Instruction.Create(OpCodes.Ldloca, bits));
        il.Op(OpCodes.Ldind_R4);
    }
}
