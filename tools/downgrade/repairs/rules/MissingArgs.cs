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
