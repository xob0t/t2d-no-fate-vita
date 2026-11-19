using Mono.Cecil.Cil;
using Repairs;

// The high score table: after game over in arcade mode, in the attract loop, and from the menu.
static partial class Rules
{
    const string HighscoreType = "com.bitmapbureau.terminator.apps.HighscoreTable";

    // HighscoreTable.update: the backdrop layers eased, Runtime.toDouble(layerPos[i]) inlined twice (0x1804DB89D and
    // 0x1804DB9AE: the IConvertible test and the invoker 0x180003650, slot 12 ToDouble); the elements in V1 and V4,
    // unboxed into V13 and V16 on the Double path.
    static void HighscoreTableUpdate(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.apps.HighscoreTable::update()", "0f57ea07");
        InlinedToDouble(m.Site("__InterfaceInvoker()", 0, "7535059b"), m, 1, 13);
        InlinedToDouble(m.Site("__InterfaceInvoker()", 0, "da39377b"), m, 4, 16);
    }

    // HighscoreTable.draw (0x1804DBF50). Arguments from the native stores before each call; the slot invokers as in
    // Invokers.cs (rdx the font, r8 the target, r9 the text or id, then x, y, alignment and ct on the stack).
    // - The fonts' vtable reads for the virtual drawString calls Rewind recovered (the MethodInfo they pass), shifted,
    //   narrowed and popped: each goes with what consumes it.
    // - The table's rows (mode != 2, not leaderboardRange), row V10, top V67 = 17 * row + 52:
    //     0x1804DCA43  bmpFontSmallWhite.draw(CANVAS_BD, row + 116, 83.0, langVOffset + top (V68), 17, textCt)  (rank)
    //     0x1804DCF99  V81.drawString(V82, "(" + rank text + ")" (V88), 231.0, langVOffset + top, 9, textCt (V89))
    //     0x1804DD12A  V93.drawString(V95, the name's letter V12 (V96), V12 * 12.0 + 301.0, V98, 17, textCt)
    // - Name entry (mode 2), letter V36: 0x1804DED24  bmpFontLargeWhite.drawString(CANVAS_BD, Text.PERIOD,
    //   48 * V36 + 151 (V188), 175.0, 17, textCt).
    // The other seven sites are the Steam leaderboard's rows (leaderboardRange), drawn only for a board
    // SteamManager has ready; the port's Steamworks is a stub that never readies one, so they are left: partial.
    static void HighscoreTableDraw(Engine e)
    {
        const string draw = FontType + "::draw(BitmapData,Int32,Double,Double,Int32,ColorTransform)";
        const string drawString = FontType + "::drawString(BitmapData,String,Double,Double,Int32,ColorTransform)";
        MethodRepair m = e.Method("com.bitmapbureau.terminator.apps.HighscoreTable::draw()", "1fb777ee");
        m.Site("__vtable()", 0, "e10b21fa").Replace(0, 4, il => { });
        m.Site("__native_0x180002F90(", 0, "b4bcd511").With(il =>
            il.Ldsfld("com.bitmapbureau.terminator.ui.Fonts::bmpFontSmallWhite").Ldsfld("com.bitmapbureau.terminator.Main::CANVAS_BD")
              .Ldloc(10).I4(116).Op(OpCodes.Add).R8(83.0).Ldloc(68).Op(OpCodes.Conv_R8).I4(17)
              .This().Ldfld(HighscoreType + "::textCt").Callvirt(draw));
        m.Site("__native_0x180002E30(", 0, "a19228b5").With(il =>
            il.Ldloc(81).Ldloc(82).Ldloc(88).R8(231.0)
              .Ldsfld("com.bitmapbureau.terminator.ui.Text::langVOffset").Ldloc(67).Op(OpCodes.Add).Op(OpCodes.Conv_R8)
              .I4(9).Ldloc(89).Callvirt(drawString));
        m.Site("__native_0x180002E30(", 0, "fae07c85").With(il =>
            il.Ldloc(93).Ldloc(95).Ldloc(96).Ldloc(12).Op(OpCodes.Conv_R8).R8(12.0).Op(OpCodes.Mul).R8(301.0).Op(OpCodes.Add)
              .Ldloc(98).Op(OpCodes.Conv_R8).I4(17).This().Ldfld(HighscoreType + "::textCt").Callvirt(drawString));
        m.Site("__vtable()", 0, "65af09ea").Replace(0, 4, il => { });
        m.Site("__native_0x180002E30(conv.u8(m#), Fonts.bmpFontLargeWhite,", 0, "72922e80").With(il =>
            il.Ldsfld("com.bitmapbureau.terminator.ui.Fonts::bmpFontLargeWhite").Ldsfld("com.bitmapbureau.terminator.Main::CANVAS_BD")
              .Ldsfld("com.bitmapbureau.terminator.ui.Text::PERIOD").Ldloc(188).Op(OpCodes.Conv_R8).R8(175.0).I4(17)
              .This().Ldfld(HighscoreType + "::textCt").Callvirt(drawString));
        m.Partial();
    }
}
