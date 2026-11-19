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
partial class DowngradeAssemblies
{
    const string UtilsType = "com.bitmapbureau.terminator.Utils";

    static Exception MenuStringsRule(string what)
    {
        return new InvalidOperationException("menu rule M2: " + what);
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
}
