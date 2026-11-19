using Mono.Cecil.Cil;
using Repairs;

// The end screen after a level: the summary and the levels' thumbnails with their ranks.
static partial class Rules
{
    // EndScreen.draw (0x1804D85F0):
    // - the font's vtable read for a virtual call Rewind recovered, shifted, narrowed and popped: it goes.
    // - `thumbnails.Length` (System.Array's) inlined as in MissionBriefing.draw (Inlined.cs): the rank test, the
    //   dimensions' product, a throw for rank 0. The array is V91; both exits store V20, V21 (the length) and V22 and
    //   branch on. The first test becomes the rank-1 exit with ldlen for the product, and the rest is reached no more;
    //   the throw's three markers go.
    // - 0x1804D973D, the slot invoker 0x180002F90 (draw, a text id): bmpFontMediumWhite (V107).draw(CANVAS_BD (V108),
    //   Game.getRankTextLabel(Game.rank[level V10]), half the thumbnail's width (V84) + its left (V101) - 1.0,
    //   (double)(its top (V102) + half its height (V87)) - 1.0, 6, null): xmm9 is 1.0, r15 the top, [rsp+0x198] and
    //   [rsp+0xa0] the halves.
    // - The inlined string compares, by the string family.
    static void EndScreenDraw(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.apps.EndScreen::draw()", "4b945907");
        m.Site("__vtable()", "f6a4caf1").Replace(0, 4, il => { });
        Instruction after = m.Method == null ? null : StoreThenBranch(m, 22);
        m.Site("__undefined()", 0, "96423ae0").Replace(0, 5, il =>
            il.Ldloc(8).Stloc(20).Ldloc(91).Op(OpCodes.Ldlen).Op(OpCodes.Conv_I4).Stloc(21).Ldloc(12).Stloc(22).Br(after));
        m.MayRemove("__undefined()", "__load4(", "__load8((__undefined()");
        // The throw for rank 0, dead now too; taken here, as the unreachable family refuses a __noreturn() after an
        // unrecovered call.
        m.Site("__native_0x1800B8380(", "ee26ddbe").Delete();
        m.Site("__native_0x1800B73D0(", "790895db").Delete();
        m.Site("__noreturn()", "d2c132a1").Delete();
        m.Site("__native_0x180002F90(", "2daa626e").With(il =>
            il.Ldloc(107).Ldloc(108)
              .Ldsfld("com.bitmapbureau.terminator.game.Game::rank").Ldloc(10).Op(OpCodes.Ldelem_I4)
              .Call("com.bitmapbureau.terminator.game.Game::getRankTextLabel(Int32)")
              .Ldloc(84).Op(OpCodes.Conv_R8).Ldloc(101).Op(OpCodes.Add).R8(1.0).Op(OpCodes.Sub)
              .Ldloc(102).Ldloc(87).Op(OpCodes.Add).Op(OpCodes.Conv_R8).R8(1.0).Op(OpCodes.Sub)
              .I4(6).Null().Callvirt(FontType + "::draw(BitmapData,Int32,Double,Double,Int32,ColorTransform)"));
        m.Families();
    }
}
