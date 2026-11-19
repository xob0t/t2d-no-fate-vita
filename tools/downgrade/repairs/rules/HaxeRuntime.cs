using Repairs;

// Haxe's dynamic runtime (haxe.lang.Runtime).
static partial class Rules
{
    // Runtime.eq(a, b), two boxed numbers of which one is a float (Single or Double type code, or UInt64): the native
    // code (0x180511F6F) calls IConvertible.ToDouble(null) through the interface invoker 0x180003650 on a's and b's
    // IConvertible (V5, V6), then `ucomisd`, equal unless unordered. Rewind lost the two calls (two invoker effects,
    // two __undefined() results it reads back as doubles from int64 locals). The calls go back; each result is kept
    // as its bits, as Rewind's locals have them. The Decimal path (type code 15, VarDecCmp), which the game's Haxe
    // values never take, keeps its markers.
    static void HaxeRuntimeEq(Engine e)
    {
        MethodRepair m = e.Method("haxe.lang.Runtime::eq(Object,Object)", "d0aa8056");
        m.Partial();
        m.Site("__InterfaceInvoker()", 0, "b4e1137a").Delete();
        m.Site("__InterfaceInvoker()", 0, "855f32aa").Delete();
        m.Site("__undefined()", 0, "5a8483b1").With(il => ToDoubleBits(il, 5));
        m.Site("__undefined()", 0, "330cd6e2").With(il => ToDoubleBits(il, 6));
    }

    // ((IConvertible)local).ToDouble(null) as its int64 bits.
    static void ToDoubleBits(IL il, int local)
    {
        il.Ldloc(local).Null().Callvirt("System.IConvertible::ToDouble(IFormatProvider)").DoubleToBits();
    }
}
