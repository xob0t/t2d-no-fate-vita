using Mono.Cecil.Cil;
using Repairs;

// IL2CPP's runtime code inlined into game methods, which Rewind leaves as markers.
static partial class Rules
{
    // MissionBriefing.draw:
    // - the font's vtable read for the virtual draw that follows (recovered), shifted, narrowed and popped: it goes with
    //   what consumes it, as in Script.drawSummary.
    // - `text.Length` (System.Array's, text being a string[]) inlined at 0x18050B930..99A: the klass's rank (byte
    //   +0x132), a throw (0x1800B8380, 0x1800B73D0) when it is 0, then the product of the dimensions' lengths, each from
    //   the bounds (+0x10) when there are any, else the vector's length (+0x18). Rewind keeps the array in V71 and stores
    //   the product into V17 on the two exits (`br IL_05f1`); a vector has rank 1 and no bounds, so it is `ldlen`. The
    //   first marker's test becomes the whole computation and a branch to the code after it; the rest of the block,
    //   with the throw, is reached no more.
    static void MissionBriefingDraw(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.apps.MissionBriefing::draw()", "a465917b");
        m.Site("__vtable()", "c2ac9fe4").Replace(0, 4, il => { });
        Instruction after = m.Method == null ? null : StoreThenBranch(m, 17);
        m.Site("__undefined()", 0, "45c5aaee").Replace(0, 5, il => il.Ldloc(71).Op(OpCodes.Ldlen).Op(OpCodes.Conv_I4).Stloc(17).Br(after));
        m.MayRemove("__undefined()", "__load4(", "__load8(", "__native_0x1800B8380(", "__native_0x1800B73D0(", "__noreturn()");
    }

    // The target of the first `stloc V<local>; br <target>` in the method.
    static Instruction StoreThenBranch(MethodRepair m, int local)
    {
        foreach (Instruction i in m.Method.Body.Instructions)
            if (i.OpCode.Name.StartsWith("stloc") && Fingerprint.Local(i, m.Method.Body) == m.Method.Body.Variables[local]
                && i.Next != null && i.Next.OpCode == OpCodes.Br)
                return (Instruction)i.Next.Operand;
        throw new RepairException(m.Key + ": no store to V" + local + " followed by a branch");
    }
}
