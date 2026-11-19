using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// M1, Menu.moveCursor's grid search (docs/drawing.md, "The achievements list"). On a menu laid out as a grid (the
// options menu's achievements list), moveCursor looks for the next button by moving an 8x8 probe 8 pixels at a time
// from the cursor's button towards the screen's edge (398x224), testing it against every button with
// Rectangle.intersectionWrap at each step. It keeps the first button it hits but goes on to the edge, and does the
// whole sweep 4 times; nothing differs between the 4 (the native code agrees: a counter at [rsp+0xd8], 0 to 4, and
// each pass starts again from the cursor's button). On the PS Vita that is 43 ms a move left or right and 88 ms up or
// down on the achievements list. M1 makes two edits that leave the button it picks the same:
//
//   on the first hit, after `found = i`, it jumps to the end of the sweep instead of taking the next step;
//   the sweep runs once (`tries + 1 < 4` becomes `tries + 1 < 1`).
//
// After the first hit the rest of a sweep can change nothing: the hit is kept (`if (found == -1)`), and what runs
// after it is Array.__get, reads of the buttons' fields and intersectionWrap, which writes only the right and bottom
// of the two Rectangles it is given (moveCursor's own probe and scratch rectangle) and makes the one it returns.
// Both methods' bodies are pinned with the repair engine's method hash (repairs/Engine.cs, Fingerprint.Of: opcodes,
// operands by name, branch distances, locals with their types), so any change to either fails the build.
partial class DowngradeAssemblies
{
    const string MenuType = "com.bitmapbureau.terminator.ui.Menu";
    const string RectangleType = "com.twissgreen.engine.geom.Rectangle";
    // Fingerprint.Of at this pass (after the repair rules, which retype moveCursor's untyped element reads), with the
    // Steam release, build 24040403, as the fork of Rewind at 56ccf43 recovers it.
    const string MoveCursorHash = "ed36d676";
    const string IntersectionWrapHash = "49bbf57b";

    static Exception MenuRule(string what)
    {
        return new InvalidOperationException("menu rule M1: " + what);
    }

    static void MenuPin(MethodDefinition method, string pinned)
    {
        string now = Repairs.Fingerprint.Of(method);
        if (now != pinned)
            throw MenuRule(method.DeclaringType.Name + "." + method.Name + " has method hash " + now + ", the rule was written against " +
                           pinned + ": re-check the rule against the method and pin the new hash");
    }

    static MethodDefinition MenuMethod(TypeDefinition type, string name, string[] parameters, string returns)
    {
        MethodDefinition[] found = type.Methods.Where(m => m.Name == name && m.HasBody &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters) && m.ReturnType.FullName == returns).ToArray();
        if (found.Length != 1) throw MenuRule(type.Name + "." + name + ": " + found.Length + " methods, not 1");
        return found[0];
    }

    static VariableDefinition MenuLocal(Instruction i, MethodBody body)
    {
        return Repairs.Fingerprint.Local(i, body);
    }

    static int MenuCursorSearch(ModuleDefinition module)
    {
        TypeDefinition menu = GameType(module, MenuType);
        TypeDefinition rectangle = GameType(module, RectangleType);
        if (menu == null || rectangle == null) return 0;
        MethodDefinition move = MenuMethod(menu, "moveCursor", new[] { "System.Int32", "System.Int32" }, "System.Void");
        MethodDefinition wrap = MenuMethod(rectangle, "intersectionWrap", new[] { RectangleType }, RectangleType);
        MenuPin(move, MoveCursorHash);
        MenuPin(wrap, IntersectionWrapHash);
        MethodBody body = move.Body;
        Repairs.Cleanup.LongBranches(body);
        // Matched on the instructions without Rewind's nops, which sit between some of them.
        List<Instruction> ins = body.Instructions.Where(i => i.OpCode != OpCodes.Nop).ToList();
        Func<Instruction, int, Instruction> after = (i, n) => ins[ins.IndexOf(i) + n];

        // The end of a sweep: where the probe's bounds checks go, `ldc.r8 398; ldloc; clt; brtrue <end>`.
        Instruction[] edge = ins.Where(i => i.OpCode == OpCodes.Ldc_R8 && (double)i.Operand == 398.0).ToArray();
        if (edge.Length != 1) throw MenuRule("moveCursor has " + edge.Length + " loads of 398.0, not 1");
        Instruction bound = after(edge[0], 3);
        if (bound.OpCode != OpCodes.Brtrue) throw MenuRule("398.0 is not tested by a brtrue two instructions on");
        var end = (Instruction)bound.Operand;

        // The first hit: `ldloc found; ldc.i4.m1; ceq; ldc.i4.0; ceq; brtrue <next button>; ldloc i; stloc hit`, then the
        // shared tail `ldloc hit; stloc found; br <next step>` that the end of the buttons' loop also takes.
        var hits = new List<Instruction>();
        for (int k = 0; k + 7 < ins.Count; k++)
            if (ins[k].OpCode.Name.StartsWith("ldloc") && ins[k + 1].OpCode == OpCodes.Ldc_I4_M1 && ins[k + 2].OpCode == OpCodes.Ceq &&
                ins[k + 3].OpCode == OpCodes.Ldc_I4_0 && ins[k + 4].OpCode == OpCodes.Ceq && ins[k + 5].OpCode == OpCodes.Brtrue &&
                ins[k + 6].OpCode.Name.StartsWith("ldloc") && ins[k + 7].OpCode.Name.StartsWith("stloc"))
                hits.Add(ins[k]);
        if (hits.Count != 1) throw MenuRule("moveCursor has " + hits.Count + " first-hit tests, not 1");
        VariableDefinition found = MenuLocal(hits[0], body);
        Instruction storeHit = after(hits[0], 7);
        VariableDefinition hit = MenuLocal(storeHit, body);
        if (found == null || hit == null || found.VariableType.FullName != "System.Int32" || hit.VariableType.FullName != "System.Int32")
            throw MenuRule("the first hit's locals are not ints");

        // The retry: `ldloc tries1; ldc.i4.4; clt; brtrue <again>`.
        Instruction[] four = ins.Where(i => i.OpCode == OpCodes.Ldc_I4_4 && ins.IndexOf(i) + 2 < ins.Count && after(i, 1).OpCode == OpCodes.Clt && after(i, 2).OpCode == OpCodes.Brtrue).ToArray();
        if (four.Length != 1) throw MenuRule("moveCursor has " + four.Length + " `< 4` loop tests, not 1");

        ILProcessor il = body.GetILProcessor();
        Instruction[] added =
        {
            il.Create(OpCodes.Ldloc, hit),
            il.Create(OpCodes.Stloc, found),
            il.Create(OpCodes.Br, end),
        };
        Instruction at = storeHit;
        foreach (Instruction a in added) { il.InsertAfter(at, a); at = a; }
        four[0].OpCode = OpCodes.Ldc_I4_1;
        return 2;
    }
}
