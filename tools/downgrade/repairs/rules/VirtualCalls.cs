using Repairs;

// Virtual calls whose vtable load the compiler hoisted above a merge point ("ObjKlass value used as
// data"): Rewind keeps `__objklass()` loads and a `__call_indirect(__load8(m + off))`. The method is
// the vtable slot (off - 0x138) / 16 of the receiver's class; the MethodInfo load (off + 8) goes with it.
static partial class Rules
{
    // Player.moveUpdate: 0x1802EB5A8..5C0, call [klass+0x228] (slot 15, changeAnim) with
    // edx = the row each path sets (V1 here), r8d = 0, r9d = 4.
    static void PlayerMoveUpdate(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.heroes.Player::moveUpdate()", "2690909d");
        foreach (Site s in m.Sites("__objklass()", 3, "7749df70 c31a1fe3 b140682c")) s.Delete();
        m.Site("__load8((m0 + 560L))", "175005d6").Delete();
        m.Site("__call_indirect(__load8((m0 + 552L)), this,", "6f4b883e")
         .With(il => il.This().Ldloc(1).I4(0).I4(4).Callvirt("com.bitmapbureau.terminator.sprites.GameObject::changeAnim(Int32,Int32,Int32)"));
    }

    // Gfx.draw (0x180282E60): the invoker thunk 0x180002D50 calls [klass+0x178] (BitmapData slot 4,
    // unityDraw) on rdx = _target. Arguments, from the thunk's and the caller's stack shuffle:
    // _source, (int)floor(_x) (Rewind's V1), (int)floor(_y) (whose floor Rewind computes and drops),
    // _frame, _columns, _rows, _align, _ct, _hflip, _vflip, then -1, -1, null (blend mode),
    // _blendAlpha, 0.0 (xorps), 1.0, 1.0 (the 16-byte constant at 0x180F6E410).
    // The same thunk is the unidentified call in 92 methods (Gfx's draw* family and their callers).
    static void GfxDraw(Engine e)
    {
        const string Bitmap = "com.twissgreen.engine.display.BitmapData";
        MethodRepair m = e.Method("com.bitmapbureau.terminator.Gfx::draw(BitmapData,BitmapData,Double,Double,Int32,Int32,Int32,Int32,ColorTransform,Boolean,Boolean,Boolean)", "c08cb122");
        m.Site("__native_0x180002D50(", "7bf9f32d").With(il => il
            .Arg(0).Arg(1)
            .Ldloc(1)
            .Arg(3).Call("System.Math::Floor(Double)").Op(Mono.Cecil.Cil.OpCodes.Conv_I4).Op(Mono.Cecil.Cil.OpCodes.Conv_R8)
            .Arg(4).Arg(5).Arg(6).Arg(7).Arg(8).Arg(9).Arg(10)
            .I4(-1).I4(-1).Null()
            .Arg(11)
            .R8(0).R8(1).R8(1)
            .Callvirt(Bitmap + "::unityDraw(BitmapData,Double,Double,Int32,Int32,Int32,Int32,ColorTransform,Boolean,Boolean,Int32,Int32,BlendMode,Boolean,Double,Double,Double)"));
    }
}
