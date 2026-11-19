using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// M2, Utils.getTimeString(double), the time a high score took as text (docs/drawing.md, "The high score table").
// HighscoreTable.draw calls it for every row every frame, and each call makes about 13 objects: three boxed ints, their
// strings and the Concats that pad and join them. The table is part of the menu's idle cycle (menu, attract demo, high
// scores), where it made the managed heap grow by about 100 KB/s and the GC run every minute or so.
// The method is a function of its argument: it writes no field, reads only static strings its class's initializer
// sets and nothing else changes (Text.ZERO, BLANK, COLON), and calls only Math.Floor, Runtime.toString and
// String.Concat, which the rule checks. M2 keeps what it returned for each argument (T2DCompat.TextMemo):
//
//   string s = TextMemo.Time(ms); if (s != null) return s;
//   ... the body, each `return x;` becoming `return TextMemo.KeepTime(x, ms);`
//
// so a time the table shows again is the same string, and nothing is made.
//
// M3, the rest of HighscoreTable.draw's strings. Each frame it draws every name a character at a time, each character
// StringExt.substr(name, j, (object)1), a boxed int and a new one-character string; and every rank as
// "(" + label + ")", two Concats. M3 changes, in HighscoreTable.draw only:
//
//   substr(s, j, box(len)) with s, j and len plain loads  ->  TextMemo.Char(s, j, len) ?? substr(s, j, box(len))
//   String.Concat(a, b)                                   ->  TextMemo.Concat(a, b)
//
// TextMemo.Char answers only a one-character substring inside the string (a kept string per character) and leaves
// every other case to substr; TextMemo.Concat keeps its results by the two strings' values, at most MaxConcats.
partial class DowngradeAssemblies
{
    const string UtilsType = "com.bitmapbureau.terminator.Utils";

    static Exception MenuStringsRule(string what)
    {
        return new InvalidOperationException("menu rule M2/M3: " + what);
    }

    // A static string field that only its own class's static initializer stores.
    static bool ConstantString(ModuleDefinition module, FieldReference field)
    {
        if (field.FieldType.FullName != "System.String") return false;
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition m in type.Methods.Where(m => m.HasBody))
            {
                if (m.Name == ".cctor" && type.FullName == field.DeclaringType.FullName) continue;
                if (m.Body.Instructions.Any(i => (i.OpCode == OpCodes.Stsfld || i.OpCode == OpCodes.Ldsflda) &&
                                                  ((FieldReference)i.Operand).FullName == field.FullName))
                    return false;
            }
        return true;
    }

    static int MenuTimeStrings(ModuleDefinition module)
    {
        TypeDefinition utils = GameType(module, UtilsType);
        if (utils == null) return 0;
        MethodDefinition[] found = utils.Methods.Where(m => m.Name == "getTimeString" && m.HasBody && m.IsStatic &&
            m.ReturnType.FullName == "System.String" && m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "System.Double").ToArray();
        if (found.Length != 1) throw MenuStringsRule("Utils.getTimeString(double): " + found.Length + " methods, not 1");
        MethodDefinition time = found[0];
        MethodBody body = time.Body;
        // A function of its argument: no stores but to its locals, no calls but these, no exception handlers.
        string[] allowed = { "System.Math::Floor", "haxe.lang.Runtime::toString", "System.String::Concat" };
        foreach (Instruction i in body.Instructions)
        {
            OpCode op = i.OpCode;
            if (op == OpCodes.Ldsfld && ConstantString(module, (FieldReference)i.Operand)) continue;
            if (op == OpCodes.Stfld || op == OpCodes.Stsfld || op == OpCodes.Ldsfld || op == OpCodes.Ldfld || op == OpCodes.Ldsflda ||
                op == OpCodes.Ldflda || op == OpCodes.Starg || op == OpCodes.Starg_S || op == OpCodes.Stind_Ref || op == OpCodes.Stobj ||
                op == OpCodes.Newobj || op == OpCodes.Newarr || op == OpCodes.Calli)
                throw MenuStringsRule("Utils.getTimeString has " + i + ", so it may not be a function of its argument");
            var called = i.Operand as MethodReference;
            if ((op == OpCodes.Call || op == OpCodes.Callvirt) &&
                !allowed.Contains(called.DeclaringType.FullName + "::" + called.Name))
                throw MenuStringsRule("Utils.getTimeString calls " + called.FullName);
        }
        if (body.HasExceptionHandlers) throw MenuStringsRule("Utils.getTimeString has exception handlers");
        List<Instruction> rets = body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
        if (rets.Count == 0) throw MenuStringsRule("Utils.getTimeString has no ret");

        MethodReference get = CompatMethod(module, "TextMemo", "Time");
        MethodReference keep = CompatMethod(module, "TextMemo", "KeepTime");
        ILProcessor il = body.GetILProcessor();
        // Each `ret` becomes `ldarg.0; call KeepTime; ret` in place, so a branch to the ret now goes through KeepTime.
        foreach (Instruction ret in rets)
        {
            ret.OpCode = OpCodes.Ldarg_0;
            ret.Operand = null;
            Instruction call = il.Create(OpCodes.Call, keep);
            il.InsertAfter(ret, call);
            il.InsertAfter(call, il.Create(OpCodes.Ret));
        }
        // The prologue, before the first instruction (nothing branches to it: checked).
        Instruction first = body.Instructions[0];
        if (body.Instructions.Any(i => i.Operand == first || (i.Operand is Instruction[] && ((Instruction[])i.Operand).Contains(first))))
            throw MenuStringsRule("a branch goes to Utils.getTimeString's first instruction");
        Instruction compute = il.Create(OpCodes.Pop);
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Call, get));
        il.InsertBefore(first, il.Create(OpCodes.Dup));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse, compute));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
        il.InsertBefore(first, compute);
        return 1;
    }

    static bool PlainLoad(Instruction i)
    {
        Code c = i.OpCode.Code;
        return (c >= Code.Ldloc_0 && c <= Code.Ldloc_3) || c == Code.Ldloc_S || c == Code.Ldloc || c == Code.Ldarg_0 ||
               c == Code.Ldarg_1 || c == Code.Ldarg_S || c == Code.Ldarg || (c >= Code.Ldc_I4_M1 && c <= Code.Ldc_I4) ;
    }

    static int MenuHighscoreStrings(ModuleDefinition module)
    {
        TypeDefinition table = GameType(module, "com.bitmapbureau.terminator.apps.HighscoreTable");
        if (table == null) return 0;
        MethodDefinition[] found = table.Methods.Where(m => m.Name == "draw" && m.HasBody && m.Parameters.Count == 0).ToArray();
        if (found.Length != 1) throw MenuStringsRule("HighscoreTable.draw(): " + found.Length + " methods, not 1");
        MethodBody body = found[0].Body;
        ILProcessor il = body.GetILProcessor();
        MethodReference chr = CompatMethod(module, "TextMemo", "Char");
        MethodReference concat = CompatMethod(module, "TextMemo", "Concat");
        int substrs = 0, concats = 0;
        foreach (Instruction call in body.Instructions.ToList())
        {
            var m = call.Operand as MethodReference;
            if (call.OpCode != OpCodes.Call || m == null) continue;
            if (m.DeclaringType.FullName == "System.String" && m.Name == "Concat" && m.Parameters.Count == 2 &&
                m.Parameters.All(p => p.ParameterType.FullName == "System.String"))
            {
                call.Operand = concat;
                concats++;
                continue;
            }
            if (m.DeclaringType.FullName != "haxe.lang.StringExt" || m.Name != "substr") continue;
            // ldX s; ldX j; ldX len; box Int32; call substr
            Instruction box = call.Previous, len = box == null ? null : box.Previous, pos = len == null ? null : len.Previous,
                        str = pos == null ? null : pos.Previous;
            if (box == null || box.OpCode != OpCodes.Box || ((TypeReference)box.Operand).FullName != "System.Int32" ||
                !PlainLoad(len) || !PlainLoad(pos) || !PlainLoad(str))
                throw MenuStringsRule("HighscoreTable.draw: substr at " + call.Offset.ToString("X4") + " is not (load, load, box(load))");
            if (body.Instructions.Any(i => i.Operand == pos || i.Operand == len || i.Operand == box || i.Operand == call ||
                                           (i.Operand is Instruction[] && ((Instruction[])i.Operand).Any(t => t == pos || t == len || t == box || t == call))))
                throw MenuStringsRule("HighscoreTable.draw: a branch goes into the substr call's arguments");
            // s; j; len; call Char; dup; brtrue done; pop; s; j; len; box; call substr; done:
            Instruction done = il.Create(OpCodes.Nop);
            Instruction after = call.Next;
            var code = new List<Instruction>
            {
                il.Create(OpCodes.Call, chr), il.Create(OpCodes.Dup), il.Create(OpCodes.Brtrue, done), il.Create(OpCodes.Pop),
                Copy(str), Copy(pos), Copy(len), il.Create(OpCodes.Box, (TypeReference)box.Operand), il.Create(OpCodes.Call, m), done,
            };
            il.Remove(box);
            il.Remove(call);
            foreach (Instruction i in code) il.InsertBefore(after, i);
            substrs++;
        }
        if (substrs != 1 || concats == 0)
            throw MenuStringsRule("HighscoreTable.draw: " + substrs + " substr calls (1 expected), " + concats + " Concats");
        Count("M3 substr", substrs);
        Count("M3 Concat", concats);
        return substrs + concats;
    }

    static Instruction Copy(Instruction i)
    {
        Instruction c = Instruction.Create(OpCodes.Nop);
        c.OpCode = i.OpCode;
        c.Operand = i.Operand;
        return c;
    }
}
