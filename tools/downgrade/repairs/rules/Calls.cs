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
    }
}

// GameObject.overCover: on finding the cover sprite it collides with, `sprite.collider = this; this.cover = sprite`
// (0x1802BFAAF, 0x1802BFAC2, each with rt_WriteBarrier): the sprite is Sprites.sprite[i], Rewind's V5; the second
// store's value was lost with its barrier call.
static partial class Rules
{
    static void GameObjectOverCover(Engine e)
    {
        MethodRepair m = e.Method(GameObjectType + "::overCover()", "4ce01f9f");
        m.Site("__undefined()", "40540507").With(il => il.Ldloc(5));
    }
}
