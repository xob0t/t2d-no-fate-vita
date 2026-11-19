using Mono.Cecil.Cil;
using Repairs;

// The level script's pause and resume, and the end-of-level summary.
static partial class Rules
{
    // Script.pause: the level track's volume kept before muting it, Runtime.toDouble(levelTrackVolume[i]) inlined
    // (0x1804302CA..37E: the IConvertible test and the invoker 0x180003650, slot 12 ToDouble), the element in V2,
    // unboxed into V13 on the Double path.
    static void ScriptPause(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.scripts.Script::pause()", "c229591c");
        InlinedToDouble(m.Site("__InterfaceInvoker()", "f3eeb2f6"), m, 2, 13);
    }

    // Script.resume: the same, twice, for the volumes pause kept: prevLevelTrackVolume[i] in V2 (V13 on the Double
    // path), then prevLevelTrackVolume[0] in V5 (V17).
    static void ScriptResume(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.scripts.Script::resume()", "19606516");
        InlinedToDouble(m.Site("__InterfaceInvoker()", 0, "f3eeb2f6"), m, 2, 13);
        InlinedToDouble(m.Site("__InterfaceInvoker()", 0, "beaf668f"), m, 5, 17);
    }

    // Script.drawSummary: the font's vtable read for the virtual draw that follows (Rewind recovered the call), shifted,
    // narrowed and popped: the read goes with what consumes it.
    static void ScriptDrawSummary(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.scripts.Script::drawSummary()", "bb0aec82");
        m.Site("__vtable()", "bec808fc").Replace(0, 4, il => { });
    }

    // CyberdyneHQOutro.draw: a font's vtable read for a virtual call Rewind recovered, shifted, narrowed and popped: it
    // goes, as in Script.drawSummary.
    static void CyberdyneHQOutroDraw(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.cutscenes.CyberdyneHQOutro::draw()", "38ba54af");
        m.Site("__vtable()", "dcf66616").Replace(0, 4, il => { });
    }

    // Script.drawParallaxTiled (0x18042D320; Cyberdyne Factory, Future War 2 to 4, the attract demos): the inner loop's
    // j + 1 (ebx). MSVC increments it on both paths, before the draw (0x18042D4E1) and on the culled path
    // (0x18042D576, which falls into the loop test); Rewind keeps the first as V10 = V3 + 1 and loses the second, so
    // a culled tile tests a stale V10 and the loop never ends (the level's first frame froze for good). The culled
    // path's nop at IL_0106, the target of the four culling branches, gets V10 = V3 + 1 (rewind-issues.md, issue 31).
    static void DrawParallaxTiled(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.scripts.Script::drawParallaxTiled(BitmapData,Int32,ColorTransform)", "730069ec");
        m.Anchor(new[] { "nop" }, 11, "4d7e2a55").With(il => il.Ldloc(3).I4(1).Op(OpCodes.Add).Stloc(10));
    }
}
