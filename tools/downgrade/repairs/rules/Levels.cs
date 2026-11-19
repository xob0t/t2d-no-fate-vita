using Mono.Cecil.Cil;
using Repairs;

// The levels' scripts.
static partial class Rules
{
    // TruckChase.drawForeground: the foreground strips side by side, `this + (i & 1) * 8 + 128` as Rewind's note says,
    // i being V1: Script.foreground0 (0x80) or foreground1 (0x88), into V2, which unityDraw takes as its source (Rewind
    // typed it int64), and that BitmapData's width (+20, 0x14) twice.
    static void TruckChaseDrawForeground(Engine e)
    {
        const string script = "com.bitmapbureau.terminator.scripts.Script::";
        MethodRepair m = e.Method("com.bitmapbureau.terminator.scripts.TruckChase::drawForeground()", "df161344");
        m.Site("__load8(__address((__address(__reference_arithmetic((this +", "91bd9788").Replace(0, 1, il =>
        {
            Instruction odd = il.NewLabel(), done = il.NewLabel();
            il.Ldloc(1).I4(1).Op(OpCodes.And).Brtrue(odd)
              .This().Ldfld(script + "foreground0").Stloc(2).Br(done)
              .Place(odd).This().Ldfld(script + "foreground1").Stloc(2).Place(done);
        });
        foreach (Site s in m.Sites("__load4((t# + 20L))", 2, "0fb76074 33944e52"))
            s.With(il => il.Ldloc(2).Ldfld("com.twissgreen.engine.display.BitmapData::width"));
        m.RetypeLocal(2, "com.twissgreen.engine.display.BitmapData");
    }

    // Truck.update: the nearest hazard ahead, `if (best == null || hazard.x < best.x) best = hazard` over
    // script.hazards, best in V6 (an object; null before the loop, carried round it), then V8 after it: best.x (+304,
    // 0x130, kept as its bits, as Rewind's int64 V83 has it) and, twice, best.lane (+804, 0x324), each read behind the
    // test that the local isn't null, as GameObject, the class the method casts the hazards to.
    static void TruckUpdate(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.enemies.Truck::update()", "0340a192");
        m.Site("__load8(__address(__reference_arithmetic((m# + 304L))))", "957410b3").With(il =>
            il.Ldloc(6).Castclass(GameObjectType).Ldfld(GameObjectType + "::x").DoubleToBits());
        foreach (Site s in m.Sites("__load4(__address(__reference_arithmetic((m# + 804L))))", 2, "5452ba18 f165bcd2"))
            s.With(il => il.Ldloc(8).Castclass(GameObjectType).Ldfld(GameObjectType + "::lane"));
    }

    // TruckChase.update:
    // - a lane's y for the hazard it spawns, `this + V3` with V3 the field's offset per path (164 Script.SCROLL_TOP,
    //   172 SCROLL_BOTTOM, the il2cpp dump's offsets): the field V3 names, through a local, so no value is on the
    //   stack across the choice.
    // - hazards[V33]'s half width as it scrolls off: ((GameObject)this.hazards.__a[V33]).width (+520, 0x208), the
    //   element cast as the method casts it for its x just before.
    static void TruckChaseUpdate(Engine e)
    {
        const string script = "com.bitmapbureau.terminator.scripts.Script::";
        MethodRepair m = e.Method("com.bitmapbureau.terminator.scripts.TruckChase::update()", "3520cc02");
        VariableDefinition lane = m.Method == null ? null : m.NewLocal("System.Int32");
        m.Site("__load4(__address(__reference_arithmetic((conv.u8(m#) + this))))", "aae2aeda").With(il =>
        {
            Instruction bottom = il.NewLabel(), done = il.NewLabel();
            il.Ldloc(3).I4(164).Op(OpCodes.Ceq).Brfalse(bottom)
              .This().Ldfld(script + "SCROLL_TOP").Stloc(lane).Br(done)
              .Place(bottom).This().Ldfld(script + "SCROLL_BOTTOM").Stloc(lane).Place(done).Ldloc(lane);
        });
        m.Site("__load4(__address(__reference_arithmetic((this.hazards.__a[m#] + 520L))))", "62d744b7").With(il =>
            il.This().Ldfld(script + "hazards").Ldfld("haxe.root.Array::__a").Ldloc(33).Op(OpCodes.Ldelem_Ref)
              .Castclass(GameObjectType).Ldfld(GameObjectType + "::width"));
    }

    // Orderly.update: the next timer, through the generic virtual invoker 0x180003090 (slot in cx, the receiver in
    // rdx, two int arguments): Utils.rand.Next(60, 240), System.Random's slot 6 in the il2cpp dump, widened to the int64
    // Rewind keeps it in.
    static void OrderlyUpdate(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.enemies.Orderly::update()", "22ec120c");
        m.Site("__native_0x180003090(6L, Utils.rand,", "bfdd26ab").With(il =>
            il.Ldsfld("com.bitmapbureau.terminator.Utils::rand").I4(60).I4(240)
              .Callvirt("System.Random::Next(Int32,Int32)").Op(OpCodes.Conv_I8));
    }

    // FreewayChase.update: a hazard's half width as it scrolls off, as in TruckChase.update: ((GameObject)
    // this.hazards.__a[V16]).width (+520, 0x208).
    static void FreewayChaseUpdate(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.scripts.FreewayChase::update()", "1802d144");
        m.Site("__load4(__address(__reference_arithmetic((this.hazards.__a[m#] + 520L))))", "7da822f0").With(il =>
            il.This().Ldfld("com.bitmapbureau.terminator.scripts.Script::hazards").Ldfld("haxe.root.Array::__a").Ldloc(16)
              .Op(OpCodes.Ldelem_Ref).Castclass(GameObjectType).Ldfld(GameObjectType + "::width"));
    }

    // TankerChase.setupScene: `Sprites.sprite[i] = truck`, with IL2CPP's array-store type check inlined (the object's
    // class assignable to the array's element class: IsAssignableFrom, then the class's flags and kinds at +280 and +42,
    // its interfaces at +112 and the slow helper 0x1800A2580); the store follows when it passes. truck is the
    // GameObject field stored into a GameObject[], so it always passes: the first test branches to the store, and the
    // rest of the check, with the throw past it, is reached no more.
    static void TankerChaseSetupScene(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.scripts.TankerChase::setupScene(Int32)", "f72ae2a1");
        Site check = m.Site("__IsAssignableFrom(__objklass())", "8081aa87");
        Instruction store = m.Method == null ? null : FirstBranchTarget(check);
        check.Replace(0, 1, il => il.Br(store));
        m.MayRemove("__load1((__undefined()", "__load8((__undefined()", "__load8((__load8((__undefined()", "__native_0x1800A2580(");
    }

    // The target of the first brtrue after a site's marker.
    static Instruction FirstBranchTarget(Site s)
    {
        for (Instruction i = s.Marker.Call; i != null; i = i.Next)
            if (i.OpCode == OpCodes.Brtrue || i.OpCode == OpCodes.Brtrue_S) return (Instruction)i.Operand;
        throw new RepairException("no brtrue after " + s.Marker.Text);
    }
}
