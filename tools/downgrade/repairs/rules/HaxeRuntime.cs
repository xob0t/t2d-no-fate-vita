using Mono.Cecil.Cil;
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

    // Runtime.toDouble(obj), not a boxed Double: the native code (0x180512200) takes `IsInst(obj, IConvertible)`
    // (a null reference exception when it isn't one) and tail-jumps to the interface invoker 0x180003650, which
    // looks IConvertible up in the object's interface offsets and calls slot 12 of it, ToDouble, with r9 = null.
    // Rewind keeps the call as an Opaque<Int64> returned as the method's double.
    static void HaxeRuntimeToDouble(Engine e)
    {
        MethodRepair m = e.Method("haxe.lang.Runtime::toDouble(Object)", "8a84cee1");
        m.Site("__InterfaceInvoker()", "832daf1d").With(il =>
            il.Arg(0).Isinst("System.IConvertible").Null().Callvirt("System.IConvertible::ToDouble(IFormatProvider)"));
    }

    // HaxeException.wrap(obj), obj a HaxeException: the native code (0x180511930) checks the class again (an invalid
    // cast otherwise) and calls [klass+0x1e8], slot 11, getObject(), storing the result in the new exception's obj
    // (+0x90). Rewind keeps the call as an int64 in V2, then `conv.i4` into the object local V0; the value and the
    // two instructions that narrow it become the call.
    static void HaxeExceptionWrap(Engine e)
    {
        MethodRepair m = e.Method("haxe.lang.HaxeException::wrap(Object)", "eb2ca8cf");
        m.Site("__call_indirect(__vtable(), obj, __vtable(), __objklass())", "fb0a8901").Replace(0, 3, il =>
            il.Arg(0).Castclass("haxe.lang.HaxeException").Callvirt("haxe.lang.HaxeException::getObject()"));
    }

    // Runtime.toDouble inlined, `x is Double ? (double)x : ((IConvertible)x).ToDouble(null)`, with the interface call
    // lost as an Effect on the second path (the invoker 0x180003650, slot 12, as in Runtime.toDouble). The first path
    // unboxes x into a double local and splits it into the halves Rewind's locals carry on: the call stores its
    // result in the same double local and joins that path right after the unbox, so the halves are Rewind's own.
    // obj: the local x is in; value: the double local the unbox stores to.
    static void InlinedToDouble(Site s, MethodRepair m, int obj, int value)
    {
        if (s.Marker == null) return;
        Mono.Collections.Generic.Collection<Mono.Cecil.Cil.Instruction> all = m.Method.Body.Instructions;
        Mono.Cecil.Cil.Instruction after = null;
        for (int i = 0; i + 2 < all.Count; i++)
        {
            if (Fingerprint.Local(all[i], m.Method.Body) == m.Method.Body.Variables[obj] && all[i].OpCode.Name.StartsWith("ldloc")
                && all[i + 1].OpCode == Mono.Cecil.Cil.OpCodes.Unbox_Any && ((Mono.Cecil.TypeReference)all[i + 1].Operand).FullName == "System.Double"
                && all[i + 2].OpCode.Name.StartsWith("stloc") && Fingerprint.Local(all[i + 2], m.Method.Body) == m.Method.Body.Variables[value])
            {
                if (after != null) throw new RepairException(m.Key + ": two unboxes of V" + obj + " into V" + value);
                after = all[i + 3];
            }
        }
        if (after == null) throw new RepairException(m.Key + ": no unbox of V" + obj + " into V" + value + " to join");
        s.With(il => il.Ldloc(obj).Isinst("System.IConvertible").Null().Callvirt("System.IConvertible::ToDouble(IFormatProvider)")
                       .Stloc(value).Br(after));
    }

    // Std.parseInt: the "0x" test reads the string's second character, `x + 22` (the first char is at 0x14), behind the
    // length test: x[1], narrowed to the int16 Rewind keeps it as. The marker after IL2CPP's IndexOutOfRange throw
    // (which doesn't return; int3 follows it in the native code) goes.
    static void StdParseInt(Engine e)
    {
        MethodRepair m = e.Method("haxe.root.Std::parseInt(String)", "425a1e34");
        m.Site("__load2(__address(__reference_arithmetic((x + 22L))))", "8d70579e").With(il =>
            il.Arg(0).I4(1).Callvirt("System.String::get_Chars(Int32)").Op(OpCodes.Conv_I2));
        m.Site("__unreachable()", "69d19492").Delete();
    }
}
