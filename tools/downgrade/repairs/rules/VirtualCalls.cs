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
}
