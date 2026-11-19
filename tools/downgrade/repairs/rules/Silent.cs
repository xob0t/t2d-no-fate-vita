using Mono.Cecil.Cil;
using Repairs;

// Silent errors: code Rewind reports complete that differs from the native code. No marker, so the
// rule anchors on an instruction pattern, checked by the same context hash.
static partial class Rules
{
    // ExplosiveBarrel.hit (0x1802D4090): Debris's vy is negated by xorps with -0.0 at 0x1802D44D8, on
    // both paths: 5.0 (xmm10) when Game.mode == 8, else rand * 6 + 2. Rewind passes the int low half
    // of the double's bits (V3) instead. A double local takes V3's place.
    static void ExplosiveBarrelHit(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.objects.ExplosiveBarrel::hit(GameObject)", null);
        VariableDefinition vy = m.NewLocal("System.Double");
        Site zero = m.Anchor(new[] { "ldc.i4.0", "stloc.3", "br" }, 0, null);
        zero.Replace(0, 0, il => il.R8(-5.0).Stloc(vy).Br((Instruction)zero.Last.Operand));
        m.Anchor(new[] { "call System.BitConverter::DoubleToInt64Bits", "conv.i4", "stloc.3" }, 0, null).Replace(0, 0, il => il.Op(OpCodes.Neg).Stloc(vy));
        m.Anchor(new[] { "ldloc.1", "ldloc.3", "ldc.r8" }, 0, null).Replace(0, 0, il => il.Ldloc(1).Ldloc(vy).R8(0));
    }
}
