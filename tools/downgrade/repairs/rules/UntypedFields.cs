using Mono.Cecil.Cil;
using Repairs;

// Field reads off an array element Rewind didn't type (`__loadN(__address(__reference_arithmetic((a.__a[i] + off))))`):
// the element's class is the one the same method casts that element to elsewhere, and the offset names its field in
// the il2cpp field table.
static partial class Rules
{
    // Menu.moveCursor: the button under the cursor, (Button)Menu.buttons.__a[Menu.cursorPos]; +192 (0xC0) is
    // Button.width, +196 (0xC4) Button.height.
    static void MenuMoveCursor(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.ui.Menu::moveCursor(Int32,Int32)", "6e571956");
        m.Site("__load4(__address(__reference_arithmetic((Menu.buttons.__a[Menu.cursorPos] + 192L))))", "34df16f4")
         .With(il => { CursorButton(il); il.Ldfld("com.bitmapbureau.terminator.ui.Button::width"); });
        m.Site("__load4(__address(__reference_arithmetic((Menu.buttons.__a[Menu.cursorPos] + 196L))))", "ccc49d85")
         .With(il => { CursorButton(il); il.Ldfld("com.bitmapbureau.terminator.ui.Button::height"); });
    }

    static void CursorButton(IL il)
    {
        il.Ldsfld("com.bitmapbureau.terminator.ui.Menu::buttons").Ldfld("haxe.root.Array::__a")
          .Ldsfld("com.bitmapbureau.terminator.ui.Menu::cursorPos").Op(OpCodes.Ldelem_Ref)
          .Castclass("com.bitmapbureau.terminator.ui.Button");
    }

    // Button.drawLock: each padlock debris, (GameObject)this.padlockDebris.__a[V0] as the method casts it for its x;
    // +312 (0x138) GameObject.y, kept as its bits as Rewind's int64 local V13 has it; +428 (0x1AC) frame; +624 (0x270)
    // ct, a ColorTransform, which V15 holds for unityDraw.
    static void ButtonDrawLock(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.ui.Button::drawLock()", "425e6592");
        m.Site("__load8(__address(__reference_arithmetic((this.padlockDebris.__a[m#] + 312L))))", "c1f40de0")
         .With(il => { Debris(il); il.Ldfld("com.bitmapbureau.terminator.sprites.GameObject::y").DoubleToBits(); });
        m.Site("__load4(__address(__reference_arithmetic((this.padlockDebris.__a[m#] + 428L))))", "032054cf")
         .With(il => { Debris(il); il.Ldfld("com.bitmapbureau.terminator.sprites.GameObject::frame"); });
        m.Site("__load8(__address(__reference_arithmetic((this.padlockDebris.__a[m#] + 624L))))", "48bf9249")
         .With(il => { Debris(il); il.Ldfld("com.bitmapbureau.terminator.sprites.GameObject::ct"); });
        // V20 is a long stack slot that later holds the bools of the next draw (`V20 & -256`, then conv.i4): it keeps
        // its 0, and the first unityDraw takes the ct from V15 instead of through it.
        m.Anchor(new[] { "ldloc.s V_15", "stloc.s V_20" }, 0, "602ee0eb").With(il => { });
        m.Anchor(new[] { "ldloc.s V_20", "ldc.i4.0", "ldc.i4.0", "ldc.i4.m1" }, 0, "f6896bb6")
         .With(il => il.Ldloc(15).I4(0).I4(0).I4(-1));
        m.RetypeLocal(15, "com.twissgreen.engine.geom.ColorTransform");
    }

    static void Debris(IL il)
    {
        il.This().Ldfld("com.bitmapbureau.terminator.ui.Button::padlockDebris").Ldfld("haxe.root.Array::__a").Ldloc(0)
          .Op(OpCodes.Ldelem_Ref).Castclass("com.bitmapbureau.terminator.sprites.GameObject");
    }
}

// Field reads off a local Rewind typed `object` (`__loadN(__address(__reference_arithmetic((mK + off))))`): the native
// code checks the object's class first (the klass's type hierarchy at +0xC8 against the class, an invalid cast
// otherwise), so the read is `((C)local).field`, the field at that offset in the il2cpp field table. A double read
// into an int64 local is kept as its bits.
static partial class Rules
{
    const string GameObjectType = "com.bitmapbureau.terminator.sprites.GameObject";

    static void FieldOf(Site s, int local, string type, string field)
    {
        s.With(il =>
        {
            il.Ldloc(local).Castclass(type).Ldfld(field);
            if (s.Marker.Text.StartsWith("__load8(") && s.Marker.ValueType.FullName == "System.Int64") il.DoubleToBits();
        });
    }

    // Centurion.shoot: the turret, turrets.__a[i] in V12, cast to CenturionTurret as the same method does for its x;
    // +312 (0x138) y.
    static void CenturionShoot(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.enemies.Centurion::shoot()", "c5659040");
        FieldOf(m.Site("__load8(__address(__reference_arithmetic((t# + 312L))))", "e21e1ace"), 12, "com.bitmapbureau.terminator.sprites.enemies.CenturionTurret", GameObjectType + "::y");
    }

    // SarahFuture's findBestBob callback: the sprite in V0, checked against GameObject at 0x18030FAD8 and 0x18030FB19;
    // +232 (0xE8) image, +312 (0x138) y.
    static void SarahFutureFindBestBob(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.heroes.SarahFuture_findBestBob_676__Fun::__hx_invoke2_o(Double,Object,Double,Object)", "24d47515");
        FieldOf(m.Site("__load4(__address(__reference_arithmetic((m# + 232L))))", "55e9cee9"), 0, GameObjectType, GameObjectType + "::image");
        FieldOf(m.Site("__load8(__address(__reference_arithmetic((m# + 312L))))", "0aa0fe64"), 0, GameObjectType, GameObjectType + "::y");
    }

    // PlayerProjectile's target search callback: the sprite in V0, checked against GameObject at 0x1802FCF4B and
    // 0x1802FCF89; +236 (0xEC) type, +299 (0x12B) canBeHit, +148 (0x94) hp, +312 y, +304 (0x130) x, +524 (0x20C) height,
    // +124 (0x7C) proximity.
    static void PlayerProjectileTargetSearch(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.objects.PlayerProjectile_update_400__Fun::__hx_invoke2_o(Double,Object,Double,Object)", "2423e55d");
        FieldOf(m.Site("__load4(__address(__reference_arithmetic((m# + 236L))))", "3ca439e4"), 0, GameObjectType, GameObjectType + "::type");
        FieldOf(m.Site("__load1(__address(__reference_arithmetic((m# + 299L))))", "4353dc93"), 0, GameObjectType, GameObjectType + "::canBeHit");
        FieldOf(m.Site("__load4(__address(__reference_arithmetic((m# + 148L))))", "e5cd457c"), 0, GameObjectType, GameObjectType + "::hp");
        FieldOf(m.Site("__load8(__address(__reference_arithmetic((m# + 312L))))", 0, "cd77ff9f"), 0, GameObjectType, GameObjectType + "::y");
        FieldOf(m.Site("__load8(__address(__reference_arithmetic((m# + 304L))))", 0, "85951a9c"), 0, GameObjectType, GameObjectType + "::x");
        FieldOf(m.Site("__load8(__address(__reference_arithmetic((m# + 304L))))", 0, "ac986bd9"), 0, GameObjectType, GameObjectType + "::x");
        FieldOf(m.Site("__load8(__address(__reference_arithmetic((m# + 312L))))", 0, "868cabca"), 0, GameObjectType, GameObjectType + "::y");
        FieldOf(m.Site("__load4(__address(__reference_arithmetic((m# + 524L))))", "c4ed9be4"), 0, GameObjectType, GameObjectType + "::height");
        FieldOf(m.Site("__load4(__address(__reference_arithmetic((m# + 124L))))", "ce42cc5f"), 0, GameObjectType, GameObjectType + "::proximity");
    }

    // Script.inLowJumpZone: each zone, (GameObject)lowJumpZones.__a[V0] as the method casts it for its x; +312 y (bits,
    // into the int64 V5), +520 (0x208) width, +524 (0x20C) height.
    static void ScriptInLowJumpZone(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.scripts.Script::inLowJumpZone()", "a17469bf");
        m.Site("__load8(__address(__reference_arithmetic((this.lowJumpZones.__a[m#] + 312L))))", "d368ce64")
         .With(il => { Zone(il); il.Ldfld(GameObjectType + "::y").DoubleToBits(); });
        m.Site("__load4(__address(__reference_arithmetic((this.lowJumpZones.__a[m#] + 520L))))", "26c1d8d7")
         .With(il => { Zone(il); il.Ldfld(GameObjectType + "::width"); });
        m.Site("__load4(__address(__reference_arithmetic((this.lowJumpZones.__a[m#] + 524L))))", "76ce75bd")
         .With(il => { Zone(il); il.Ldfld(GameObjectType + "::height"); });
    }

    static void Zone(IL il)
    {
        il.This().Ldfld("com.bitmapbureau.terminator.scripts.Script::lowJumpZones").Ldfld("haxe.root.Array::__a").Ldloc(0)
          .Op(OpCodes.Ldelem_Ref).Castclass(GameObjectType);
    }

    // Trigger.update: YoungJohn turning round, `youngJohn.runDirection = -youngJohn.runDirection` (0x1802C689F..8E9:
    // Sprites.youngJohn checked against YoungJohn, +0x3F0 read, negated, stored through Sprites.youngJohn again).
    // Rewind keeps youngJohn in V65.
    static void TriggerUpdate(Engine e)
    {
        const string john = "com.bitmapbureau.terminator.sprites.heroes.YoungJohn";
        MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.other.Trigger::update()", "5eb9ec8d");
        m.Site("__store4(__address(__reference_arithmetic((Sprites.youngJohn + 1008L))),", "7bbf09b5").With(il =>
            il.Ldsfld("com.bitmapbureau.terminator.game.Sprites::youngJohn").Castclass(john)
              .Ldloc(65).Castclass(john).Ldfld(john + "::runDirection").Op(OpCodes.Neg).Stfld(john + "::runDirection"));
    }
}
