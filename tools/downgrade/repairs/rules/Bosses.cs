using Mono.Cecil.Cil;
using Repairs;

// The levels' bosses.
static partial class Rules
{
    const string DesertBossType = "com.bitmapbureau.terminator.sprites.enemies.DesertBoss";

    // DesertBoss.changeState: the jump point's x and y each from one of two fields the compiler merged into one load,
    // `movsd xmm0, [rdi + rax]` with the field's offset chosen per path into a register (Rewind's V0 to V3:
    // 976 jumpLeft, 984 jumpRight, 992 jumpTop, 1000 jumpBottom, the il2cpp dump's offsets). Each load becomes the
    // field its local names; the marker, its stloc, ldloca and ldobj go. The point it is stored into (V14 to V17) is
    // loaded before the marker; it is loaded again after the choice, so no value is on the stack across its branches
    // (IL2CPP 2018 mistypes those).
    static void DesertBossChangeState(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.enemies.DesertBoss::changeState(Int32)", "8ea43aaf");
        string[] sites = { "720908b6", "b40d090e", "7755739f", "7699a938" };
        VariableDefinition value = m.Method == null ? null : m.NewLocal("System.Double");
        for (int i = 0; i < 4; i++)
        {
            int local = i;
            m.Site("__load8(__address(__reference_arithmetic((conv.u8(m#) + this))))", 0, sites[i])
             .Replace(1, 3, il => { JumpField(il, local, value); il.Ldloc(14 + local).Ldloc(value); });
        }
    }

    // value = this.<the field at offset V<local>> among the jump bounds.
    static void JumpField(IL il, int local, VariableDefinition value)
    {
        string[] fields = { "jumpLeft", "jumpRight", "jumpTop", "jumpBottom" };
        Instruction done = il.NewLabel();
        for (int f = 0; f < 3; f++)
        {
            Instruction next = il.NewLabel();
            il.Ldloc(local).I4(976 + 8 * f).Op(OpCodes.Ceq).Brfalse(next)
              .This().Ldfld(DesertBossType + "::" + fields[f]).Stloc(value).Br(done).Place(next);
        }
        il.This().Ldfld(DesertBossType + "::" + fields[3]).Stloc(value).Place(done);
    }

    // Biker..ctor: NUM_FRAMES_PER_ANIM = new int[n] { ... } per biker (name 573 to 576), four initializers whose
    // InitializeArray calls the compiler merged (0x18032EEC1), as in ControlsExplanation.setupScene; the handle is V37,
    // the merged value V10. The FieldInfo usages each path loads (0x18032EEB1 FI_2505, 0x18032EF2E FI_800,
    // 0x18032EF60 FI_391, 0x18032EF95 FI_148) name <PrivateImplementationDetails>'s fields of 48, 52, 48 and 52 bytes,
    // the arrays' lengths. The inlined boxes are the box family's.
    static void BikerCtor(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.enemies.Biker::.ctor(Int32,Int32,Int32)", "55f1f68e");
        MergedInitializeArray(m, m.Sites("__fieldinfo()", 4, "29934929 17102f66 e706f197 93faf8f3"), new[] {
            "617DCB0A992A0741C2E55152380C2131AD78D1E12236C6B49895D11F590B3ACC", // int[12], name 573
            "1D601B68E2A684A57C70228F0AC66FEB4766FA0214F07B924A4469D4A7F9A85C", // int[13], name 574
            "0EB7131D1747379150A838A3DEC50C6DD0209003F706FF93847F17FBD1256D73", // int[12], name 575
            "053F0E76E9FD8C14EC4DDD77F05E528450DE0A7E2AC94F2C098D9698A772EC8B", // int[13], name 576
        }, 37, 10, "721a2af3");
        m.Families();
    }
}
