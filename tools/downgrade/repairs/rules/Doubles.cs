using Repairs;

// Doubles Rewind moved through integer registers and can't turn back without
// BitConverter.Int64BitsToDouble ("bits reinterpreted as double"). The marker names the halves'
// SSA values (m28, m29); the rule says which locals hold them, from the IL before the marker.
static partial class Rules
{
    // ContinueScreen.update: colorTransform.setAlphaMultiplier(min(alpha + 0.01, 1)) at 0x1804D1A50..6E,
    // whose halves Rewind keeps in V8/V9; and SaveStats.setStoryBossSpawnPointY's argument, the halves
    // of bossSpawnPoint.y or zeros on the other path (V10/V11).
    static void ContinueScreenUpdate(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.apps.ContinueScreen::update()", "da5efe50");
        m.Site("__bits_to_double(", 0, "17f98874").With(il => il.HalvesToDouble(8, 9));
        m.Site("__bits_to_double(", 0, "7e0421f9").With(il => il.HalvesToDouble(10, 11));
    }
}
