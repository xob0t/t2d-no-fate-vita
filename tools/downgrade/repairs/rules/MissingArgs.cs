using Repairs;

// Stack arguments of a call Rewind didn't capture (Rewind issue 21, which a Rewind fix is to take over; these rules
// then fail with "Rewind recovers it now" and go). Each value was read from the native call's stack stores.
static partial class Rules
{
    // BmpFont.drawScaled: a tail call to drawStringScaled through the vtable ([r9+0x228]), whose stack arguments are
    // drawScaled's own, left in place: the last, vscale, is _vscale.
    static void BmpFontDrawScaled(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.ui.BmpFont::drawScaled(BitmapData,Int32,Double,Double,Int32,ColorTransform,Double,Double)", "dd3bdfdc");
        m.Site("__missing_argument()", "595f77c5").With(il => il.Arg(7));
    }

    // TitleText.draw: drawScaled ([r9+0x218]) and drawStringScaled ([r9+0x228]) with vscale from [rsp+0x40], loaded
    // from [rbx+0x50], TitleText.vScale (the field table's offset 0x50).
    static void TitleTextDraw(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.ui.TitleText::draw()", "f60b4491");
        foreach (Site s in m.Sites("__missing_argument()", 2, "c3205652 1a9a7213"))
            s.With(il => il.This().Ldfld("com.bitmapbureau.terminator.ui.TitleText::vScale"));
    }
}
