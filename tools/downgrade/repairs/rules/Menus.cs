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
}
