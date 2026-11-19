using System;
using Mono.Cecil.Cil;
using Repairs;

// A one-character string made in place: IL2CPP inlined the allocation of a string of length 1 (Haxe's
// String.fromCharCode, a char's ToString). Each is nine markers in a row: the allocation of 28 bytes through the
// unidentified allocator 0x18010BEF0 (an Opaque<Int64> kept in a local), the class and monitor words, the
// allocation counter's atomic increment, the profiler flag's load and its callback 0x1800BA540, the length 1 at +16,
// the terminating 0 at +22, and the character at +20, whose expression is the marker's. The local holding the
// "pointer" is then handed on through conv.i4 where a string is taken.
// The repair: the allocation becomes new string(c, 1) with c the character the +20 store writes, the other eight
// markers go, the local becomes a string and the conv.i4 after its read goes.
static partial class Rules
{
    // StringExt.charAt(s, i): s[i] as a string (V0 the char, V1 the string).
    static void StringExtCharAt(Engine e)
    {
        MethodRepair m = e.Method("haxe.lang.StringExt::charAt(String,Int32)", "3501c0fd");
        Site[] alloc = m.Sites("__native_0x18010BEF0(28L, 0L,", 1, "39bebcc9");
        foreach (Site s in m.Sites("__store8(t#, __klass())", 1, "43832bcc")) s.Delete();
        foreach (Site s in m.Sites("__store8((t# + 8L), 0L)", 1, "828ab862")) s.Delete();
        foreach (Site s in m.Sites("__atomic_inc(6460065928L)", 1, "85c811a5")) s.Delete();
        foreach (Site s in m.Sites("__load1(6460062216L)", 1, "65644d5a")) s.With(il => il.I4(0));
        foreach (Site s in m.Sites("__store4((t# + 16L), 1)", 1, "bf6d6292")) s.Delete();
        foreach (Site s in m.Sites("__store2((t# + 22L), 0)", 1, "b276e977")) s.Delete();
        foreach (Site s in m.Sites("__native_0x1800BA540(t#, __klass())", 1, "b885470b")) s.Delete();
        foreach (Site s in m.Sites("__store2((t# + 20L),", 1, "6e6b39ba")) s.Delete();
        alloc[0].With(il => OneChar(il, x => x.Ldloc(0)));
        m.Anchor(new[] { "ldloc.1", "conv.i4", "ret" }, 0, "680a1b9c").With(il => il.Ldloc(1).Op(OpCodes.Ret));
        m.RetypeLocal(1, "System.String");
    }

    // StringExt.split(s, ""): each character of s as a string (V15 the char, V16 the string); the
    // ThrowIndexOutOfRangeException's unreachable after it goes too.
    static void StringExtSplit(Engine e)
    {
        MethodRepair m = e.Method("haxe.lang.StringExt::split(String,String)", "e2757482");
        Site[] alloc = m.Sites("__native_0x18010BEF0(28L, 0L,", 1, "af0fc769");
        foreach (Site s in m.Sites("__store8(t#, __klass())", 1, "b421c1b9")) s.Delete();
        foreach (Site s in m.Sites("__store8((t# + 8L), 0L)", 1, "5e3dff42")) s.Delete();
        foreach (Site s in m.Sites("__atomic_inc(6460065928L)", 1, "3628c93a")) s.Delete();
        foreach (Site s in m.Sites("__load1(6460062216L)", 1, "2974ebbc")) s.With(il => il.I4(0));
        foreach (Site s in m.Sites("__store4((t# + 16L), 1)", 1, "ef31a99f")) s.Delete();
        foreach (Site s in m.Sites("__store2((t# + 22L), 0)", 1, "96a0affb")) s.Delete();
        foreach (Site s in m.Sites("__native_0x1800BA540(t#, __klass())", 1, "bdcbe6bd")) s.Delete();
        foreach (Site s in m.Sites("__store2((t# + 20L),", 1, "ce5d098b")) s.Delete();
        alloc[0].With(il => OneChar(il, x => x.Ldloc(15)));
        m.Anchor(new[] { "ldloc.s V_16", "conv.i4", "stelem.any System.String" }, 0, "548d465b")
         .With(il => il.Ldloc(16).Emit(Instruction.Create(OpCodes.Stelem_Ref)));
        m.Site("__unreachable()", "0b9f2b21").Delete();
        m.RetypeLocal(16, "System.String");
    }

    // Text.processTextFile: the character map's digits, capitals and small letters, txtMap[n] = the string of
    // (char)(k + 48), (k + 65), (k + 97) for the loop counters V10, V14, V18 (the +20 stores' conv.u2(m) + 48, + 65,
    // + 97), into V26, V31, V36, each then stored by Array.__set(int, object).
    static void TextProcessTextFile(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.ui.Text::processTextFile(ByteArray,Int32)", "afdf4a08");
        Site[] alloc = m.Sites("__native_0x18010BEF0(28L, 0L,", 3, "373366a1 430ea6de 5f703dbd");
        foreach (Site s in m.Sites("__store8(t#, __klass())", 3, "4cc52831 e434caab cc3eae09")) s.Delete();
        foreach (Site s in m.Sites("__store8((t# + 8L), 0L)", 3, "1ce678ed 96dd1f17 2cdf07ec")) s.Delete();
        foreach (Site s in m.Sites("__atomic_inc(6460065928L)", 3, "6dcd505f 8b362dc6 ec1ead43")) s.Delete();
        foreach (Site s in m.Sites("__load1(6460062216L)", 3, "4357b07d 76a3feea c109fc44")) s.With(il => il.I4(0));
        foreach (Site s in m.Sites("__store4((t# + 16L), 1)", 3, "d8d40c97 8933eb5b 14dc1503")) s.Delete();
        foreach (Site s in m.Sites("__store2((t# + 22L), 0)", 3, "16a0eb75 7065f80f 830ea5cb")) s.Delete();
        foreach (Site s in m.Sites("__native_0x1800BA540(t#, __klass())", 3, "c1a93d7a 9079aafa e18d1d9e")) s.Delete();
        foreach (Site s in m.Sites("__store2((t# + 20L),", 3, "0b63d942 59d4e6ff a3a06517")) s.Delete();
        int[] counters = { 10, 14, 18 }, bases = { 48, 65, 97 }, strings = { 26, 31, 36 };
        for (int k = 0; k < 3; k++)
        {
            int counter = counters[k], at = bases[k];
            alloc[k].With(il => OneChar(il, x => x.Ldloc(counter).I4(at).Op(OpCodes.Add)));
        }
        Site[] reads =
        {
            m.Anchor(new[] { "ldloc.s V_26", "conv.i4", "callvirt haxe.root.Array::__set" }, 0, "8ba369b1"),
            m.Anchor(new[] { "ldloc.s V_31", "conv.i4", "callvirt haxe.root.Array::__set" }, 0, "3e7ba906"),
            m.Anchor(new[] { "ldloc.s V_36", "conv.i4", "callvirt haxe.root.Array::__set" }, 0, "e5032a97"),
        };
        for (int k = 0; k < 3; k++)
        {
            int local = strings[k];
            reads[k].With(il => il.Ldloc(local).Callvirt("haxe.root.Array::__set(Int32,Object)"));
        }
        foreach (int local in strings) m.RetypeLocal(local, "System.String");
    }

    // new string((char)c, 1), c pushed by `character`.
    static void OneChar(IL il, Action<IL> character)
    {
        character(il);
        il.Op(OpCodes.Conv_U2).I4(1).Newobj("System.String::.ctor(Char,Int32)");
    }
}
