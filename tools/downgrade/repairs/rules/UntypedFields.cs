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
    // ct, a ColorTransform, which V15 and V20 then hold for unityDraw.
    static void ButtonDrawLock(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.ui.Button::drawLock()", "425e6592");
        m.Site("__load8(__address(__reference_arithmetic((this.padlockDebris.__a[m#] + 312L))))", "c1f40de0")
         .With(il => { Debris(il); il.Ldfld("com.bitmapbureau.terminator.sprites.GameObject::y").DoubleToBits(); });
        m.Site("__load4(__address(__reference_arithmetic((this.padlockDebris.__a[m#] + 428L))))", "032054cf")
         .With(il => { Debris(il); il.Ldfld("com.bitmapbureau.terminator.sprites.GameObject::frame"); });
        m.Site("__load8(__address(__reference_arithmetic((this.padlockDebris.__a[m#] + 624L))))", "48bf9249")
         .With(il => { Debris(il); il.Ldfld("com.bitmapbureau.terminator.sprites.GameObject::ct"); });
        m.RetypeLocal(15, "com.twissgreen.engine.geom.ColorTransform");
        m.RetypeLocal(20, "com.twissgreen.engine.geom.ColorTransform");
    }

    static void Debris(IL il)
    {
        il.This().Ldfld("com.bitmapbureau.terminator.ui.Button::padlockDebris").Ldfld("haxe.root.Array::__a").Ldloc(0)
          .Op(OpCodes.Ldelem_Ref).Castclass("com.bitmapbureau.terminator.sprites.GameObject");
    }
}
