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
             .Replace(1, 3, il => { JumpField(il, DesertBossType, local, value); il.Ldloc(14 + local).Ldloc(value); });
        }
    }

    // value = this.<the field at offset V<local>> among the jump bounds.
    static void JumpField(IL il, string type, int local, VariableDefinition value)
    {
        string[] fields = { "jumpLeft", "jumpRight", "jumpTop", "jumpBottom" };
        Instruction done = il.NewLabel();
        for (int f = 0; f < 3; f++)
        {
            Instruction next = il.NewLabel();
            il.Ldloc(local).I4(976 + 8 * f).Op(OpCodes.Ceq).Brfalse(next)
              .This().Ldfld(type + "::" + fields[f]).Stloc(value).Br(done).Place(next);
        }
        il.This().Ldfld(type + "::" + fields[3]).Stloc(value).Place(done);
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

    // BikerBarPatron..ctor: NUM_FRAMES_PER_ANIM per patron, four initializers merged as in Biker..ctor (0x1802C9968),
    // the handle V16, the merged value V2. Each native path's FieldInfo usage, matched to the IL's paths by the fields
    // each stores first: 0x1802C9627 FI_759 (int[3]), 0x1802C9655 FI_2463 (int[5]), 0x1802C9769 FI_4205 (int[3], vo 33),
    // 0x1802C9958 FI_4386 (int[3], vo 32, layer 4).
    static void BikerBarPatronCtor(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.objects.BikerBarPatron::.ctor(Int32)", "f463171c");
        MergedInitializeArray(m, m.Sites("__fieldinfo()", 4, "f6dd7bc7 9f3a9ed9 6f0be422 70216a34"), new[] {
            "1BCFD1727A92E832B31F6ED4E4CF5B6251CD3418F5DA45CAA562FA735F4F531E", // int[3]
            "5FAB0E079E0FE2D2EBFF33D2F71C88E32CFBCE14E4D7E12690073B13BDA322E6", // int[5]
            "A8998E92D1256D83DBF33404053858F2E1D92D06AE2662767397308EE493B47C", // int[3], vo 33
            "AF7B923C5FFB0F9F02CAB6317346322D51FC24FEE404C7782776523D7A5D1666", // int[3], vo 32
        }, 16, 2, "2884273c");
    }

    // TerminatorBob.changeState: the jump point's x and y from the jump bounds as in DesertBoss.changeState, the same
    // fields at the same offsets (976 jumpLeft to 1000 jumpBottom); the offset per site in V3, V1, V4, V5. The point
    // is this.jumpPoint, loaded just before each marker; it is loaded again after the choice.
    static void TerminatorBobChangeState(Engine e)
    {
        const string bob = "com.bitmapbureau.terminator.sprites.enemies.TerminatorBob";
        MethodRepair m = e.Method(bob + "::changeState(Int32)", "7af1153e");
        VariableDefinition value = m.Method == null ? null : m.NewLocal("System.Double");
        int[] locals = { 3, 1, 4, 5 };
        string[] sites = { "2a8f34d5", "63ef13e0", "b532fc28", "50cf9dc1" };
        for (int i = 0; i < 4; i++)
        {
            int local = locals[i];
            m.Site("__load8(__address(__reference_arithmetic((conv.u8(m#) + this))))", 0, sites[i])
             .Replace(2, 3, il => { JumpField(il, bob, local, value); il.This().Ldfld(bob + "::jumpPoint").Ldloc(value); });
        }
    }

    // FW3Boss.update: the front leg's hp, ((GameObject)this.legs.__a[0]).hp (+148, 0x94), twice; the method casts the
    // legs to GameObject for the same test just before.
    static void FW3BossUpdate(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.enemies.FW3Boss::update()", "1706dd3d");
        foreach (Site s in m.Sites("__load4(__address(__reference_arithmetic((this.legs.__a[0] + 148L))))", 2, "ab37ba1f c9b99af9"))
            s.With(il => il.This().Ldfld("com.bitmapbureau.terminator.sprites.enemies.FW3Boss::legs").Ldfld("haxe.root.Array::__a")
                           .I4(0).Op(OpCodes.Ldelem_Ref).Castclass(GameObjectType).Ldfld(GameObjectType + "::hp"));
    }

    // FW3Boss2ndPhase.update: a platform's y, ((GameObject)this.platforms.__a[V6]).y (+312, 0x138) as its bits, the
    // element cast as the method casts it for its x just before.
    static void FW3Boss2ndPhaseUpdate(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.enemies.FW3Boss2ndPhase::update()", "c4fe21cc");
        m.Site("__load8(__address(__reference_arithmetic((this.platforms.__a[m#] + 312L))))", "f0ad66bf").With(il =>
            il.This().Ldfld("com.bitmapbureau.terminator.sprites.enemies.FW3Boss2ndPhase::platforms").Ldfld("haxe.root.Array::__a")
              .Ldloc(6).Op(OpCodes.Ldelem_Ref).Castclass(GameObjectType).Ldfld(GameObjectType + "::y").DoubleToBits());
    }

    // PoliceHelicopter.start: the targets chained, ((HeliTarget)targets[i] (V4)).nextTarget (+968, 0x3C8) =
    // targets[i + 1] (V6, which Rewind typed HeliTarget).
    static void PoliceHelicopterStart(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.enemies.PoliceHelicopter::start()", "8c289455");
        m.Site("__store8(__address(__reference_arithmetic((m# + 968L))), m#)", "b2215064").With(il =>
            il.Ldloc(4).Castclass("com.bitmapbureau.terminator.sprites.enemies.HeliTarget").Ldloc(6)
              .Stfld("com.bitmapbureau.terminator.sprites.enemies.HeliTarget::nextTarget"));
    }

    // T1000SteelMill.update: two virtual calls on this through the generic virtual invoker 0x180002B50 (slot in cx,
    // the receiver in rdx, one int argument): slot 8 changeImage(41), slot 14 changeState(59) (the il2cpp dump's slots).
    static void T1000SteelMillUpdate(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.enemies.T1000SteelMill::update()", "39378706");
        m.Site("__native_0x180002B50(8L, this,", "b4731e98").With(il => il.This().I4(41).Callvirt(GameObjectType + "::changeImage(Int32)"));
        m.Site("__native_0x180002B50(14L, this,", "97408cb6").With(il => il.This().I4(59).Callvirt(GameObjectType + "::changeState(Int32)"));
    }
}
