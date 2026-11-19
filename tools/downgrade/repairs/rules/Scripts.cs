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
}
