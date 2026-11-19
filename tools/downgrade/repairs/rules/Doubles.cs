using Repairs;

// Doubles Rewind moved through integer registers and can't turn back without
// BitConverter.Int64BitsToDouble ("bits reinterpreted as double"). The marker names the halves'
// SSA values (m28, m29); the rule says which locals hold them, from the IL before the marker.
static partial class Rules
{
    // ContinueScreen.update: colorTransform.setAlphaMultiplier(min(alpha + 0.01, 1)) at 0x1804D1A50..6E,
    // whose halves Rewind keeps in V8/V9 (m28, m29); and SaveStats.setStoryBossSpawnPointY's argument,
    // the halves of bossSpawnPoint.y or zeros on the other path (V10/V11, m30, m31). The markers' own
    // text, __bits_to_double((conv.u8(conv.u4(m28)) | (conv.u8(conv.u4(m29)) << 32))), is the repair.
    static void ContinueScreenUpdate(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.apps.ContinueScreen::update()", "da5efe50");
        m.Bind("m28", 8).Bind("m29", 9).Bind("m30", 10).Bind("m31", 11);
        m.CompileAll("__bits_to_double(", 2, "17f98874 7e0421f9");
    }
}
