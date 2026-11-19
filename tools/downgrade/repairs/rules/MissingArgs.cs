using Repairs;

// Stack arguments of a call Rewind didn't capture (Rewind issue 21, which a Rewind fix is to take over; these rules
// then fail with "Rewind recovers it now" and go; patch 0028 took TitleText.draw's). Each value was read from the native call's stack stores.
static partial class Rules
{
    // BmpFont.drawScaled: a tail call to drawStringScaled through the vtable ([r9+0x228]), whose stack arguments are
    // drawScaled's own, left in place: the last, vscale, is _vscale.
    static void BmpFontDrawScaled(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.ui.BmpFont::drawScaled(BitmapData,Int32,Double,Double,Int32,ColorTransform,Double,Double)", "dd3bdfdc");
        m.Site("__missing_argument()", "595f77c5").With(il => il.Arg(7));
    }
}

static partial class Rules
{
    // Gfx.drawClippedWidth: a tail jump to unityDrawClippedWidth through the vtable ([rbx+0x188]) whose stack arguments
    // are drawClippedWidth's own, left in place: _ct at [rsp+0xe0] as it came, then the clip width, (double)_width,
    // stored over _width's slot (0x180283E5A, 0x180283EA1); _height's slot takes the MethodInfo.
    static void GfxDrawClippedWidth(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.Gfx::drawClippedWidth(BitmapData,BitmapData,Double,Double,Int32,Int32,Int32,Int32,ColorTransform,Int32,Int32)", "f7598190");
        m.Site("__missing_argument()", 0, "4073275c").With(il => il.Arg(8));
        m.Site("__missing_argument()", 0, "d4cafa2c").With(il => il.Arg(9).Op(Mono.Cecil.Cil.OpCodes.Conv_R8));
    }
}
