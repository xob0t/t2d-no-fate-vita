using Repairs;

// Virtual calls Rewind lost, and values it read from a vtable or a class only as plumbing.
static partial class Rules
{
    // Centurion.draw: each turret drawn after its ct is set: [r9+0x308], slot 29, GameObject.draw(), on
    // turrets.__a[i] (V3), checked against CenturionTurret just before (0x18035656B..594).
    static void CenturionDraw(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.enemies.Centurion::draw()", "dbe73014");
        m.Site("__call_indirect(__vtable(), t#, __vtable(), __klass(), __objklass())", "b15ee40b").With(il =>
            il.Ldloc(3).Castclass("com.bitmapbureau.terminator.sprites.enemies.CenturionTurret")
              .Callvirt(GameObjectType + "::draw()"));
    }

    // Game.draw:
    // - the boss rush countdown's drawNumber (two languages' layouts): the font's VirtualInvokeData method pointer
    //   ([r9+0x200], slot 12's MethodInfo) stored at [rsp+0x58], the call's hidden argument. Rewind keeps that stack
    //   slot as the int64 V30, which later carries a bool argument of Gfx.draw in its low byte (`V30 & -256` first,
    //   as the native code stores only the byte). The slot starts at 0 and keeps it.
    // - Game.state 5: `cmp [Game klass+0xe4], 0` (the class's static constructor finished), then rt_ClassInit if not;
    //   both paths join (0x180487238, 0x180487256 both go on to Game.shutdownFrame). The class is initialized here.
    static void GameDraw(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.game.Game::draw()", "87f3e4d8");
        m.Site("__vtable()", 0, "d42bb3ad").With(il => il.I8(0));
        m.Site("__vtable()", 0, "7544e5ec").With(il => il.I8(0));
        m.Site("__undefined()", "45b728b4").With(il => il.I4(1));
        // drawNumber's last argument (Object) is V31, a long stack slot that holds 0 there, narrowed: null.
        m.Anchor(new[] { "ldloc.s V_31", "conv.i4", "callvirt com.bitmapbureau.terminator.ui.BmpFont::drawNumber" }, 0, "300586d8")
         .With(il => il.Null().Callvirt(FontType + "::drawNumber(BitmapData,Int32,Double,Double,Int32,ColorTransform,Boolean,Double,Double,Object)"));
    }
}

// Game.start: player.elevator = cast(Sprites.findByName(220, -1, -1), Elevator), the cast taken out with the native
// class check (rewind-issues.md, issue 24, into a local here): V6 is typed Elevator and gets the GameObject V31.
static partial class Rules
{
    static void GameStart(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.game.Game::start()", "f582855f");
        m.Anchor(new[] { "ldloc.s V_31", "stloc.s V_6" }, 0, "601ade75")
         .With(il => il.Ldloc(31).Castclass("com.bitmapbureau.terminator.sprites.objects.Elevator").Stloc(6));
    }
}
