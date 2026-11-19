using Repairs;

// Player.changeState (0x1802F3C40): every marker kind in one method.
static partial class Rules
{
    const string GameObject = "com.bitmapbureau.terminator.sprites.GameObject";

    static void PlayerChangeState(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.heroes.Player::changeState(Int32)", "36295859");

        // mov word [rbx+0x12A], 1 or 0x101: visible (0x12A) and canBeHit (0x12B).
        foreach (Site s in m.Sites("__store2(&this.visible,", 6, "5c39efdb 747a56dc 753650b3 e46cdd74 f00ab05a f504abca")) s.SplitStore2(GameObject, "canBeHit");

        // The tail-merged call [klass+0x228] at 0x1802F3FA0: slot 15, changeAnim(row, 0, rate); each path
        // sets row and rate in V6 and V7 and loads the class pointer (the __objklass markers).
        foreach (Site s in m.Sites("__objklass()", 3, "f6269351 1260a992 8c289718")) s.Delete();
        m.Site("__call_indirect(__load8((m25 + 552L)), this,", "2f1a6a23")
         .With(il => il.This().Ldloc(6).I4(0).Ldloc(7).Callvirt(GameObject + "::changeAnim(Int32,Int32,Int32)"));

        // vx = +-MAX_SPEED by vx's sign (0x1802F4560..): the low half of MAX_SPEED (V38) and the high half
        // with the sign flipped or not (V4).
        m.Site("__bits_to_double(", 0, "1ac6314f").With(il => il.Halves(
            lo => lo.Ldloc(38).Call("System.BitConverter::DoubleToInt64Bits(Double)"),
            hi => hi.Ldloc(4)));
        // vy = -4.0 or -6.0 by vaultDistance: a zero low half and the high half in V3.
        m.Site("__bits_to_double(", 0, "4d161721").With(il => il.Halves(lo => lo.I4(0), hi => hi.Ldloc(3)));

        // 0x1802F4F6D..0x1802F5029: the invoker 0x180003170 calls [klass+0x5D8] (slot 74, setupImpact)
        // on rdx = this, with xmm2 = facing * 8 + x, xmm3 = y - 16, then from the stack 32, 32, false,
        // image == 230 ? 0 : 25, facing, facing * 2.0, vy, impact.hitTypes, impact.hitSounds, false, true.
        m.Site("__native_0x180003170(", "60723e10").With(il => il
            .This()
            .This().Ldfld(GameObject + "::facing").Op(Mono.Cecil.Cil.OpCodes.Conv_R8).R8(8).Op(Mono.Cecil.Cil.OpCodes.Mul)
            .This().Ldfld(GameObject + "::x").Op(Mono.Cecil.Cil.OpCodes.Add)
            .This().Ldfld(GameObject + "::y").R8(16).Op(Mono.Cecil.Cil.OpCodes.Sub)
            .I4(32).I4(32).I4(0)
            .This().Ldfld(GameObject + "::image").I4(230).Op(Mono.Cecil.Cil.OpCodes.Ceq).I4(0).Op(Mono.Cecil.Cil.OpCodes.Ceq).I4(25).Op(Mono.Cecil.Cil.OpCodes.Mul)
            .This().Ldfld(GameObject + "::facing")
            .This().Ldfld(GameObject + "::facing").Op(Mono.Cecil.Cil.OpCodes.Conv_R8).Op(Mono.Cecil.Cil.OpCodes.Dup).Op(Mono.Cecil.Cil.OpCodes.Add)
            .This().Ldfld(GameObject + "::vy")
            .This().Ldfld(GameObject + "::impact").Ldfld(GameObject + "::hitTypes")
            .This().Ldfld(GameObject + "::impact").Ldfld(GameObject + "::hitSounds")
            .I4(0).I4(1)
            .Callvirt(GameObject + "::setupImpact(Double,Double,Int32,Int32,Boolean,Int32,Int32,Double,Double,Int32,Sound[],Boolean,Boolean)"));
    }
}
