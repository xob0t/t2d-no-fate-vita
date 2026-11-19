using Mono.Cecil.Cil;
using Repairs;

// Virtual calls through IL2CPP's per-slot invoker thunks, which Rewind leaves as calls to unidentified native
// functions. Each thunk loads the receiver's vtable entry at a fixed offset (VirtualInvokeData: the method pointer, then
// its MethodInfo, 16 bytes a slot from klass+0x138) and passes the caller's register and stack arguments on: so the
// thunk names the slot, and the call site's stores name the arguments. Slots are read from the il2cpp dump's
// "Slot:" lines, fields from its field offsets.
//   0x180003020: [r10+0x1b0], slot 7    0x180002F90: [r10+0x1e0], slot 10    0x180002E30: [r10+0x1f0], slot 11
static partial class Rules
{
    const string ButtonType = "com.bitmapbureau.terminator.ui.Button";
    const string FontType = "com.bitmapbureau.terminator.ui.BmpFont";

    // TextButton.draw(target, x, y): the button's text on BmpFont, slot 7 drawStringLines, 10 draw (a text id),
    // 11 drawString. Native (0x1802B5B40):
    //   0x1802B5D02  bmpFont.drawStringLines(target, textLines, x, y, alignment, ct, 16)      (not a slider, textLines)
    //   0x1802B5D5F  bmpFont.drawString(target, text, x, y, alignment, ct)                    (a slider)
    //   0x1802B5FCD  bmpFont.draw(target, option != 0 ? 533 : 534, left + optionSpacing, bottom, 6, ct)  (an on/off setting)
    //   0x1802B602D  bmpFont.draw(target, option, left + optionSpacing, bottom, 6, ct)       (another setting)
    //   0x1802B60A8  bmpFont.drawString(target, s, ...): merged from two paths, Rewind's V3 the font and V4 the string:
    //                not a setting, no textLines: (text; x, y, alignment); a setting with settingStrings
    //                (settingStrings[option]; left + optionSpacing, bottom, 6); ct on both. `setting` (0x8C) tells them.
    // The statics pointer Rewind reads into V1 is used only as a stray value and goes.
    static void TextButtonDraw(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.ui.TextButton::draw(BitmapData,Double,Double)", "df9968d6");
        m.Site("__native_0x180003020(", "1f3a8d31").With(il =>
        {
            il.Ldloc(6).Arg(0).This().Ldfld("com.bitmapbureau.terminator.ui.TextButton::textLines");
            AtXY(il);
            il.I4(16).Callvirt(FontType + "::drawStringLines(BitmapData,String[],Double,Double,Int32,ColorTransform,Int32)");
        });
        m.Site("__native_0x180002E30(", 0, "b99cbaf7").With(il =>
        {
            il.Ldloc(6).Arg(0).This().Ldfld(ButtonType + "::text");
            AtXY(il);
            il.Callvirt(FontType + "::drawString(BitmapData,String,Double,Double,Int32,ColorTransform)");
        });
        // The id is chosen before the call's arguments go on the stack: IL2CPP 2018 mistypes values held on the stack
        // across a branch.
        VariableDefinition chosen = m.NewLocal("System.Int32");
        m.Site("__native_0x180002F90(", 0, "a34e7895").With(il =>
        {
            Instruction on = il.NewLabel(), id = il.NewLabel();
            il.This().Ldfld(ButtonType + "::option").Brtrue(on).I4(534).Stloc(chosen).Br(id).Place(on).I4(533).Stloc(chosen).Place(id);
            il.This().Ldfld(ButtonType + "::bmpFont").Arg(0).Ldloc(chosen);
            AtOption(il);
            il.Callvirt(FontType + "::draw(BitmapData,Int32,Double,Double,Int32,ColorTransform)");
        });
        m.Site("__native_0x180002F90(", 0, "dd9a97f2").With(il =>
        {
            il.This().Ldfld(ButtonType + "::bmpFont").Arg(0).This().Ldfld(ButtonType + "::option");
            AtOption(il);
            il.Callvirt(FontType + "::draw(BitmapData,Int32,Double,Double,Int32,ColorTransform)");
        });
        m.Site("__native_0x180002E30(", 0, "a84a2180").With(il =>
        {
            Instruction setting = il.NewLabel(), done = il.NewLabel();
            il.This().Ldfld(ButtonType + "::setting").Brtrue(setting);
            il.Ldloc(3).Arg(0).Ldloc(4);
            AtXY(il);
            il.Callvirt(FontType + "::drawString(BitmapData,String,Double,Double,Int32,ColorTransform)").Br(done).Place(setting);
            il.Ldloc(3).Arg(0).Ldloc(4);
            AtOption(il);
            il.Callvirt(FontType + "::drawString(BitmapData,String,Double,Double,Int32,ColorTransform)").Place(done);
        });
        m.Site("__statics()", "f978d700").Delete();
    }

    // x, y, alignment, ct.
    static void AtXY(IL il)
    {
        il.This().Ldfld(ButtonType + "::x").This().Ldfld(ButtonType + "::y").This().Ldfld(ButtonType + "::alignment")
          .This().Ldfld(ButtonType + "::ct");
    }

    // left + optionSpacing, bottom, 6, ct.
    static void AtOption(IL il)
    {
        il.This().Ldfld(ButtonType + "::optionSpacing").Op(OpCodes.Conv_R8).This().Ldfld(ButtonType + "::left").Op(OpCodes.Add);
        il.This().Ldfld(ButtonType + "::bottom").I4(6).This().Ldfld(ButtonType + "::ct");
    }
}

// More of BmpFont's slot invokers. 0x1800030D0 is [r10+0x1f8], slot 12, drawNumber(target, number, x, y, align, ct,
// commaSeparated, hscale, vscale, ordinal): the caller's stack arguments at +0x20..+0x58 are x, y, align, ct, the bool,
// the two scales and the ordinal.
static partial class Rules
{
    // Message.draw: a message sprite with no bitmap and no image draws a number (value >= 0) or a text (textId != -1)
    // at its position on screen. Native (0x1802C3640):
    //   0x1802C37A2  bmpFont.drawNumber(Main.CANVAS_BD, value, x - Cam.left, y - Cam.top, alignment, ct, false, 1.0, 1.0, null)
    //   0x1802C3853  bmpFont.draw(Main.CANVAS_BD, textId, x - Cam.left, y - Cam.top, alignment, ct)
    // (x +0x130, y +0x138, alignment +0x164, ct +0x270; Cam's statics +0 left, +8 top; the scales (1.0, 1.0) one
    // 16-byte constant.)
    static void MessageDraw(Engine e)
    {
        const string message = "com.bitmapbureau.terminator.sprites.other.Message";
        MethodRepair m = e.Method(message + "::draw()", "22561e17");
        m.Site("__native_0x1800030D0(", "44ff6061").With(il =>
        {
            il.This().Ldfld(message + "::bmpFont").Ldsfld("com.bitmapbureau.terminator.Main::CANVAS_BD").This().Ldfld(message + "::value");
            OnScreen(il);
            il.I4(0).R8(1.0).R8(1.0).Null();
            il.Callvirt(FontType + "::drawNumber(BitmapData,Int32,Double,Double,Int32,ColorTransform,Boolean,Double,Double,Object)");
        });
        m.Site("__native_0x180002F90(", "1a4802ec").With(il =>
        {
            il.This().Ldfld(message + "::bmpFont").Ldsfld("com.bitmapbureau.terminator.Main::CANVAS_BD").This().Ldfld(message + "::textId");
            OnScreen(il);
            il.Callvirt(FontType + "::draw(BitmapData,Int32,Double,Double,Int32,ColorTransform)");
        });
    }

    // x - Cam.left, y - Cam.top, alignment, ct of the sprite.
    static void OnScreen(IL il)
    {
        il.This().Ldfld(GameObjectType + "::x").Ldsfld("com.bitmapbureau.terminator.game.Cam::left").Op(OpCodes.Sub);
        il.This().Ldfld(GameObjectType + "::y").Ldsfld("com.bitmapbureau.terminator.game.Cam::top").Op(OpCodes.Sub);
        il.This().Ldfld(GameObjectType + "::alignment").This().Ldfld(GameObjectType + "::ct");
    }
}

// Cutscene.drawText: the dialogue lines, through 0x180002E30 (slot 11, drawString). Four call paths in the native code
// (0x1804B6680), which share two call instructions:
//   0x1804B6A4F  the line being typed (strings[line] up to currentCharacter, or ""), ct currentCharacterCt:
//                left-aligned at the text's x, or with centreText at 199 - (width >> 1)
//   0x1804B6E49  an earlier line, whole, ct null (r12, zero there), at the same two x's
// in both: font Fonts.bmpFontSmallWhite, target Main.CANVAS_BD, y the line's (_top, then 16 more a line; Rewind's V0),
// align 9. Rewind keeps the first call's font, target and text in V11, V10, V12, the second's font and strings in V14,
// V15, the line in V1.
// The text's x (r13d, kept at [rsp+0x118]) was computed before the loop and lost with the call: with no dialogPortrait,
// 199 - (widestStringWidth >> 1); with one, its width + 8 + Rewind's V19 (199 - ((widestStringWidth + 8 + width) >> 1),
// the portrait's x). The width of a centred line is getStringWidth(strings[line]), which Rewind calls and pops
// (0x1804B6BCC, kept in r13d): the pop becomes a store.
static partial class Rules
{
    static void CutsceneDrawText(Engine e)
    {
        const string cutscene = "com.bitmapbureau.terminator.cutscenes.Cutscene";
        MethodRepair m = e.Method(cutscene + "::drawText(Int32)", "67b68242");
        Mono.Cecil.Cil.VariableDefinition width = m.NewLocal("System.Int32"), x = m.NewLocal("System.Int32");
        m.Anchor(new[] { "callvirt BmpFont::getStringWidth", "pop" }, 0, "a8927ac0")
         .With(il => il.Callvirt(FontType + "::getStringWidth(String)").Stloc(width));
        // The x, into a local before the call's arguments go on the stack (IL2CPP 2018 and values on the stack across
        // a branch).
        System.Action<IL> lineX = il =>
        {
            Instruction centred = il.NewLabel(), portrait = il.NewLabel(), done = il.NewLabel();
            il.This().Ldfld(cutscene + "::centreText").Brtrue(centred);
            il.This().Ldfld(cutscene + "::dialogPortrait").Brtrue(portrait);
            il.I4(199).This().Ldfld(cutscene + "::widestStringWidth").I4(1).Op(OpCodes.Shr).Op(OpCodes.Sub).Stloc(x).Br(done);
            il.Place(portrait).This().Ldfld(cutscene + "::dialogPortrait").Ldfld("com.twissgreen.engine.display.BitmapData::width")
              .I4(8).Op(OpCodes.Add).Ldloc(19).Op(OpCodes.Add).Stloc(x).Br(done);
            il.Place(centred).I4(199).Ldloc(width).I4(1).Op(OpCodes.Shr).Op(OpCodes.Sub).Stloc(x);
            il.Place(done);
        };
        m.Site("__native_0x180002E30(", 0, "8df7b58e").With(il =>
        {
            lineX(il);
            il.Ldloc(11).Ldloc(10).Ldloc(12).Ldloc(x).Op(OpCodes.Conv_R8).Ldloc(0).Op(OpCodes.Conv_R8).I4(9)
              .This().Ldfld(cutscene + "::currentCharacterCt")
              .Callvirt(FontType + "::drawString(BitmapData,String,Double,Double,Int32,ColorTransform)");
        });
        m.Site("__native_0x180002E30(", 0, "45ec129a").With(il =>
        {
            lineX(il);
            il.Ldloc(14).Ldsfld("com.bitmapbureau.terminator.Main::CANVAS_BD").Ldloc(15).Ldloc(1).Op(OpCodes.Ldelem_Ref)
              .Ldloc(x).Op(OpCodes.Conv_R8).Ldloc(0).Op(OpCodes.Conv_R8).I4(9).Null()
              .Callvirt(FontType + "::drawString(BitmapData,String,Double,Double,Int32,ColorTransform)");
        });
    }
}

// HUD.draw (0x18048E680): the HUD's texts and numbers through the slot invokers, all at the HUD's top, xmm13 in the
// native code, 8 - offset (Rewind's V68), and with ct null (r13, zero throughout):
//   0x18048EAA1, F627, F85B, FA4E  Fonts.bmpFontSmallWhite.draw(Main.CANVAS_BD, 535, 199.0, top, 17, null)
//   0x18048EFE7  Fonts.bmpFontSmallWhite.drawString(Main.CANVAS_BD, continues, offset + 390.0,
//                top + (Game.difficulty > 0 ? 0.0 : -2.0), 3, null), the text in Rewind's V2 (Text.INFINITY or
//                Std.string(continuesDisplayCount)), font and target in V90, V91
//   drawNumber(Main.CANVAS_BD, n, x, y, 3, null, false, 1.0, 1.0, null) for the score, n = score % 100000000 as the
//   markers' own text has it (their temporaries are Rewind's V-locals that hold Game.score and its quotient's high
//   part), the font a static the marker names:
//     0x18048FD68  x 102, y top + 19        0x180491114  x 46, y top - 1 + 18 - 1   0x180491788  x 46, y top - 1 + 18 - 1
//     0x180491E55  x 102, y top + 19        0x180492A0C  x 63, y top - 1 + 18
//   0x1804912F7  V218.drawNumber(V219, Game.getScoreMultiplier(), 53.0, top + 18.0, 9, null, false, 1.0, 1.0, null)
// (the scales are xmm8, the 16-byte constant (1.0, 1.0); the - 1 is its low half). The six string compares go to the
// string family.
static partial class Rules
{
    const string GfxDraw = "com.bitmapbureau.terminator.Gfx::draw(BitmapData,BitmapData,Double,Double,Int32,Int32,Int32,Int32,ColorTransform,Boolean,Boolean,Boolean)";
    const string DrawNumber = FontType + "::drawNumber(BitmapData,Int32,Double,Double,Int32,ColorTransform,Boolean,Double,Double,Object)";

    static void HUDDraw(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.game.HUD::draw()", "e67ba160");
        // Gfx.draw's ct is Rewind's V66, a long stack slot that holds 0 before each of these calls (r13, null) and,
        // elsewhere, drawTime's bool in its low byte: psp2snc rejects the long where a ColorTransform is taken.
        string[] gfxDraw = { "ldloc.s V_66", "ldc.i4.0", "ldc.i4.0", "ldc.i4.0", "call com.bitmapbureau.terminator.Gfx::draw" };
        string[] ctHashes = { "771bb4af", "2dc7ac8c", "36956b55", "cb64db93", "de0ac886", "5c456b63", "a44966ea" };
        var cts = new Site[7];
        for (int i = 0; i < 7; i++) cts[i] = m.Anchor(gfxDraw, i, ctHashes[i]);
        foreach (Site s in cts) s.With(il => il.Null().I4(0).I4(0).I4(0).Call(GfxDraw));
        System.Action<IL> skynet = il => il.Ldsfld("com.bitmapbureau.terminator.ui.Fonts::bmpFontSmallWhite")
            .Ldsfld("com.bitmapbureau.terminator.Main::CANVAS_BD").I4(535).R8(199.0).Ldloc(68).I4(17).Null()
            .Callvirt(FontType + "::draw(BitmapData,Int32,Double,Double,Int32,ColorTransform)");
        m.Site("__native_0x180002F90(", 0, "9e8c8600").With(skynet);
        VariableDefinition y = m.NewLocal("System.Double");
        m.Site("__native_0x180002E30(", "33118c72").With(il =>
        {
            Instruction easy = il.NewLabel(), done = il.NewLabel();
            il.Ldsfld("com.bitmapbureau.terminator.game.Game::difficulty").I4(0).Op(OpCodes.Cgt).Brtrue(easy);
            il.Ldloc(68).R8(-2.0).Op(OpCodes.Add).Stloc(y).Br(done);
            il.Place(easy).Ldloc(68).R8(0.0).Op(OpCodes.Add).Stloc(y).Place(done);
            il.Ldloc(90).Ldloc(91).Ldloc(2).This().Ldfld("com.bitmapbureau.terminator.game.HUD::offset").Op(OpCodes.Conv_R8).R8(390.0)
              .Op(OpCodes.Add).Ldloc(y).I4(3).Null().Callvirt(FontType + "::drawString(BitmapData,String,Double,Double,Int32,ColorTransform)");
        });
        m.Site("__native_0x180002F90(", 0, "92ebd3dd").With(skynet);
        m.Site("__native_0x180002F90(", 0, "7a8aac30").With(skynet);
        m.Site("__native_0x180002F90(", 0, "63b2d0ac").With(skynet);
        m.Bind("t385", 115).Bind("t386", 116).Bind("t487", 205).Bind("t488", 206).Bind("t517", 230).Bind("t519", 231)
         .Bind("t551", 262).Bind("t552", 263).Bind("t612", 317).Bind("t614", 318);
        Score(m, m.Site("__native_0x1800030D0(", 0, "59822fea"), 102.0, il => il.Ldloc(68).R8(19.0).Op(OpCodes.Add));
        Score(m, m.Site("__native_0x1800030D0(", 0, "b207d3c3"), 46.0, TopLess1Plus18Less1);
        m.Site("__native_0x1800030D0(", 0, "e3615386").With(il =>
            il.Ldloc(218).Ldloc(219).Call("com.bitmapbureau.terminator.game.Game::getScoreMultiplier()").R8(53.0)
              .Ldloc(68).R8(18.0).Op(OpCodes.Add).I4(9).Null().I4(0).R8(1.0).R8(1.0).Null().Callvirt(DrawNumber));
        Score(m, m.Site("__native_0x1800030D0(", 0, "4231200e"), 46.0, TopLess1Plus18Less1);
        Score(m, m.Site("__native_0x1800030D0(", 0, "c16ba7f4"), 102.0, il => il.Ldloc(68).R8(19.0).Op(OpCodes.Add));
        Score(m, m.Site("__native_0x1800030D0(", 0, "43c24cd8"), 63.0, il => il.Ldloc(68).R8(1.0).Op(OpCodes.Sub).R8(18.0).Op(OpCodes.Add));
        m.Families();
    }

    static void TopLess1Plus18Less1(IL il)
    {
        il.Ldloc(68).R8(1.0).Op(OpCodes.Sub).R8(18.0).Op(OpCodes.Add).R8(1.0).Op(OpCodes.Sub);
    }

    // font.drawNumber(target, n, x, y, 3, null, false, 1.0, 1.0, null), font, target and n the marker's own arguments.
    static void Score(MethodRepair m, Site s, double x, System.Action<IL> y)
    {
        if (s.Marker == null) return;
        s.With(il =>
        {
            for (int i = 1; i <= 3; i++) ExprCompiler.CompileArg(m, s.Marker, i, il, i == 3 ? Kind.I4 : Kind.Ref);
            il.R8(x);
            y(il);
            il.I4(3).Null().I4(0).R8(1.0).R8(1.0).Null().Callvirt(DrawNumber);
        });
    }
}
