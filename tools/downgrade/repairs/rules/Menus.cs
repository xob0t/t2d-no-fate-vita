using Repairs;

// The menu buttons' small gaps.
static partial class Rules
{
    // Menu.cursorMoved: for a button that selects when highlighted, a virtual call at [r9+0x188], Button's slot 5,
    // select(bool), with edx = 0, on (Button)buttons.__a[cursorPos] (the native class check against Button before it),
    // which Rewind keeps in V0.
    static void MenuCursorMoved(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.ui.Menu::cursorMoved(Int32)", "04b87978");
        m.Site("__call_indirect(__vtable(), t#, 0L, __vtable(), __objklass())", "9e00f684").With(il =>
            il.Ldloc(0).Castclass("com.bitmapbureau.terminator.ui.Button").I4(0)
              .Callvirt("com.bitmapbureau.terminator.ui.Button::select(Boolean)"));
    }

    // LevelSelect.draw: the font's vtable read for the virtual drawString that follows (recovered; the level's name at
    // 125, 210), shifted, narrowed and popped, as in Script.drawSummary: the read goes with what consumes it.
    static void LevelSelectDraw(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.apps.LevelSelect::draw()", "430dbd67");
        m.Site("__vtable()", "ffdf65f7").Replace(0, 4, il => { });
    }

    // Database.draw: two fonts' vtable reads for virtual calls Rewind recovered, shifted, narrowed and popped: they go.
    static void DatabaseDraw(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.apps.Database::draw()", "48b97539");
        m.Site("__vtable()", 0, "834a15f3").Replace(0, 4, il => { });
        m.Site("__vtable()", 0, "b1611e3c").Replace(0, 4, il => { });
    }

    // Credits.draw: a popped vtable read, and the inlined string compares (against "BITMAP BUREAU") by the family.
    static void CreditsDraw(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.apps.Credits::draw()", "7b299bc1");
        m.Site("__vtable()", "a4642ce3").Replace(0, 4, il => { });
        m.Families();
    }

    // MusicPlayer.update: Runtime.toDouble inlined twice as in HighscoreTable.update, the elements in V1 and V4,
    // unboxed into V10 and V13 on the Double path.
    static void MusicPlayerUpdate(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.apps.MusicPlayer::update()", "bcf8c0f9");
        InlinedToDouble(m.Site("__InterfaceInvoker()", 0, "0247193f"), m, 1, 10);
        InlinedToDouble(m.Site("__InterfaceInvoker()", 0, "6ee8d559"), m, 4, 13);
    }
}
