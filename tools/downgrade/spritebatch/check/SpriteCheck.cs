using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Runs the sprite batch's rules (spritebatch/rewriter/SpriteBatchPatches.cs) and then the atlas rules
// (atlas/rewriter/AtlasPatches.cs), in the pipeline's order, on Rewind's Assembly-CSharp alone, and checks what they
// did: each rule's sites; that the atlas rules still apply, AtlasDraws skipping the two methods R8 took; the stack of
// every changed method (rewriter/Stack.cs, the pipeline's own check); that nothing builds a sprite frame's Meshes,
// reads BitmapData.meshes or texpack, or calls the old draw paths any more; that the new bodies call only SpriteBatch
// and hold no Rewind marker; and that the written assembly reads back. Prints the new bodies to <out>.il.txt.
//
// Usage: SpriteCheck <Assembly-CSharp.dll> <T2DCompat, or a dll of Atlas.cs and SpriteBatch.cs> <out.dll>
//
// The helpers below stand in for the rewriter's own (DowngradeAssemblies.cs, rewriter/Redirect.cs) with the same
// names and behaviour, as atlas/check/RuleCheck.cs's do, so both rule files compile unchanged here and there.
partial class DowngradeAssemblies
{
    enum Scope { All, Game }

    class Pass
    {
        public readonly string Group;
        public readonly Scope Scope;
        public readonly Func<ModuleDefinition, int> Run;
        public Pass(string group, Scope scope, Func<ModuleDefinition, int> run) { Group = group; Scope = scope; Run = run; }
        public string Name { get { return Run.Method.Name; } }
    }

    static readonly Dictionary<string, TypeDefinition> compat = new Dictionary<string, TypeDefinition>();
    static readonly Dictionary<string, int> matches = new Dictionary<string, int>();

    static void Count(string rule, int sites)
    {
        int sum;
        matches.TryGetValue(rule, out sum);
        matches[rule] = sum + sites;
    }

    static TypeDefinition GameType(ModuleDefinition module, string name)
    {
        TypeDefinition type = module.GetType(name);
        Count("type " + name, type == null ? 0 : 1);
        return type;
    }

    static MethodReference CompatMethod(ModuleDefinition module, string type, string name)
    {
        return module.ImportReference(compat["T2DCompat." + type].Methods.Single(m => m.Name == name));
    }

    static void Prepend(MethodDefinition method, params Instruction[] added)
    {
        InsertBefore(method.Body.GetILProcessor(), method.Body.Instructions[0], added);
    }

    static void InsertBefore(ILProcessor il, Instruction anchor, params Instruction[] added)
    {
        foreach (Instruction instruction in added) il.InsertBefore(anchor, instruction);
    }

    static void InsertAfter(ILProcessor il, Instruction anchor, params Instruction[] added)
    {
        foreach (Instruction instruction in added)
        {
            il.InsertAfter(anchor, instruction);
            anchor = instruction;
        }
    }

    static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (TypeDefinition type in types)
        {
            yield return type;
            foreach (TypeDefinition nested in AllTypes(type.NestedTypes)) yield return nested;
        }
    }

    // As DowngradeAssemblies.Passes lists them.
    static readonly Pass[] CheckedPasses =
    {
        new Pass("spritebatch", Scope.Game, SpriteBatchDraws),
        new Pass("spritebatch", Scope.Game, SpriteBatchMeshes),
        new Pass("spritebatch", Scope.Game, SpriteBatchFlush),
        new Pass("spritebatch", Scope.Game, SpriteBatchFilters),
        new Pass("atlas", Scope.Game, AtlasTables),
        new Pass("atlas", Scope.Game, AtlasSplash),
        new Pass("atlas", Scope.Game, AtlasPackLoads),
        new Pass("atlas", Scope.Game, AtlasDraws),
        new Pass("atlas", Scope.Game, AtlasSceneHooks),
    };

    // The sites each pass changes in the Steam release (build 24040403) as Rewind recovers it.
    static readonly Dictionary<string, int> ExpectedSites = new Dictionary<string, int>
    {
        { "SpriteBatchDraws", 2 }, { "SpriteBatchMeshes", 1 }, { "SpriteBatchFlush", 1 }, { "SpriteBatchFilters", 1 },
        { "AtlasTables", 6 }, { "AtlasSplash", 3 }, { "AtlasPackLoads", 1 }, { "AtlasDraws", 0 }, { "AtlasSceneHooks", 4 },
    };

    const string BD = "com.twissgreen.engine.display.BitmapData";

    static readonly string[] Replaced =
        { BD + "::unityDraw", BD + "::unityDrawClippedWidth", BD + "::CreateMultiMeshFrames", "com.bitmapbureau.terminator.Gfx::saturateImage" };
    static readonly string[] Watched = Replaced.Concat(new[] { "com.twissgreen.unity.UnityMain::OnRenderObject" }).ToArray();

    static string Key(MethodDefinition m) { return m.DeclaringType.FullName + "::" + m.Name; }

    static string StackProblem(MethodDefinition method)
    {
        try
        {
            Dictionary<Instruction, Instruction[]> before = StackProducers(method, null);
            int returned = method.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
            foreach (KeyValuePair<Instruction, Instruction[]> reached in before)
                if (reached.Key.OpCode.Code == Code.Ret && reached.Value.Length != returned)
                    return reached.Value.Length + " values at " + reached.Key;
            return null;
        }
        catch (InvalidOperationException e) { return e.Message; }
    }

    static int Markers(MethodDefinition m)
    {
        return m.Body.Instructions.Count(i => i.Operand is MethodReference && ((MethodReference)i.Operand).DeclaringType.Name == "__Rewind" &&
                                              ((MethodReference)i.Operand).Name != "Note");
    }

    static int Main(string[] args)
    {
        string input = args[0], compatPath = args[1], output = args[2];
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input)));
        ModuleDefinition compatModule = ModuleDefinition.ReadModule(compatPath);
        foreach (TypeDefinition type in compatModule.Types) compat[type.FullName] = type;
        ModuleDefinition module = ModuleDefinition.ReadModule(input, new ReaderParameters { AssemblyResolver = resolver });

        var methods = AllTypes(module.Types).SelectMany(t => t.Methods).Where(m => m.HasBody).ToList();
        var watched = methods.Where(m => Watched.Contains(Key(m))).ToList();
        int failures = 0;
        if (watched.Count != Watched.Length) { Console.WriteLine("FAIL found " + watched.Count + " of the " + Watched.Length + " watched methods"); failures++; }
        var stackBefore = watched.ToDictionary(m => m, m => StackProblem(m));
        foreach (MethodDefinition m in watched)
            Console.WriteLine("before: " + Key(m) + " " + m.Body.Instructions.Count + " instructions, " + Markers(m) + " Rewind markers" +
                              (stackBefore[m] != null ? ", stack check fails (" + stackBefore[m] + ")" : ""));

        foreach (Pass pass in CheckedPasses)
        {
            try
            {
                int sites = pass.Run(module);
                Count("pass " + pass.Name, sites);
                bool ok = sites == ExpectedSites[pass.Name];
                Console.WriteLine((ok ? "pass " : "FAIL pass ") + pass.Name + ": " + sites + " sites" + (ok ? "" : ", expected " + ExpectedSites[pass.Name]));
                if (!ok) failures++;
            }
            catch (InvalidOperationException e)
            {
                Console.WriteLine("FAIL pass " + pass.Name + ": " + e.Message);
                failures++;
            }
        }
        foreach (KeyValuePair<string, int> m in matches.OrderBy(m => m.Key))
            if (m.Value == 0 && m.Key != "pass AtlasDraws") { Console.WriteLine("NO MATCH " + m.Key); failures++; }

        // Running a rule twice must fail, not apply again.
        try { SpriteBatchDraws(module); Console.WriteLine("FAIL R8 applied twice"); failures++; }
        catch (InvalidOperationException e) { Console.WriteLine("ok: R8 refuses a second run (" + e.Message + ")"); }

        foreach (MethodDefinition m in methods)
            foreach (Instruction i in m.Body.Instructions)
            {
                var called = i.Operand as MethodReference;
                var field = i.Operand as FieldReference;
                string where = Key(m) + " " + i;
                if (called != null && called.Name == "CreateMultiMeshFrames" && !(m.IsConstructor && m.DeclaringType.FullName == BD))
                { Console.WriteLine("FAIL another caller of CreateMultiMeshFrames: " + where); failures++; }
                if (field != null && field.Name == "meshes" && field.DeclaringType.FullName == BD && i.OpCode != OpCodes.Stfld)
                { Console.WriteLine("FAIL still reads BitmapData.meshes: " + where); failures++; }
                if (field != null && field.Name == "texpack" && field.DeclaringType.FullName == BD && m.Name != "initBitmapData" && m.Name != "loadTexturePacks")
                { Console.WriteLine("FAIL still uses BitmapData.texpack: " + where); failures++; }
                if (called != null && called.Name == "loadTexturePacks" && called.DeclaringType.FullName == BD)
                { Console.WriteLine("FAIL still calls loadTexturePacks: " + where); failures++; }
            }

        foreach (MethodDefinition m in watched)
        {
            string after = StackProblem(m);
            if (after != null && stackBefore[m] == null) { Console.WriteLine("FAIL stack after the rules in " + Key(m) + ": " + after); failures++; }
            else if (after != null) { Console.WriteLine("FAIL stack check fails after the rules in " + Key(m) + " (" + after + ")"); failures++; }
            else Console.WriteLine("stack ok: " + Key(m));
        }
        // R9's handler: Main.draw() in a try whose catch calls SpriteBatch.Discard() and rethrows.
        foreach (MethodDefinition m in watched.Where(m => m.Name == "OnRenderObject"))
        {
            List<Instruction> ins = m.Body.Instructions.ToList();
            ExceptionHandler h = m.Body.ExceptionHandlers.SingleOrDefault();
            Instruction draw = ins.SingleOrDefault(i => i.Operand is MethodReference && ((MethodReference)i.Operand).Name == "draw");
            bool ok = h != null && draw != null && h.HandlerType == ExceptionHandlerType.Catch && h.CatchType.FullName == "System.Exception" &&
                      ins.IndexOf(h.TryStart) <= ins.IndexOf(draw) && ins.IndexOf(draw) < ins.IndexOf(h.TryEnd) &&
                      h.HandlerStart.Operand is MethodReference && ((MethodReference)h.HandlerStart.Operand).Name == "Discard" &&
                      h.HandlerStart.Next.OpCode == OpCodes.Rethrow && h.HandlerEnd == h.HandlerStart.Next.Next &&
                      h.TryEnd.Previous.OpCode == OpCodes.Leave && h.TryEnd.Previous.Operand == h.HandlerEnd &&
                      h.HandlerEnd.Next.Operand is MethodReference && ((MethodReference)h.HandlerEnd.Next.Operand).Name == "Flush";
            Console.WriteLine(ok ? "handler ok: " + Key(m) + " discards the queue when Main.draw() throws" : "FAIL " + Key(m) + ": no R9 handler around Main.draw()");
            if (!ok) failures++;
        }
        foreach (MethodDefinition m in watched.Where(m => Replaced.Contains(Key(m))))
        {
            int left = Markers(m);
            var calls = m.Body.Instructions.Select(i => i.Operand as MethodReference).Where(c => c != null)
                         .Select(c => c.DeclaringType.FullName + "::" + c.Name).Distinct().ToArray();
            var foreign = calls.Where(c => !c.StartsWith("T2DCompat.SpriteBatch::")).ToArray();
            if (left > 0) { Console.WriteLine("FAIL " + Key(m) + " keeps " + left + " Rewind markers"); failures++; }
            if (m.Body.Variables.Count > 0 || m.Body.ExceptionHandlers.Count > 0) { Console.WriteLine("FAIL " + Key(m) + " keeps locals or handlers"); failures++; }
            if (foreign.Length > 0) { Console.WriteLine("FAIL " + Key(m) + " calls more than SpriteBatch: " + string.Join(", ", foreign)); failures++; }
            else Console.WriteLine("body ok: " + Key(m) + " " + m.Body.Instructions.Count + " instructions, calls " + (calls.Length == 0 ? "nothing" : string.Join(", ", calls)));
        }

        using (var il = new StreamWriter(output + ".il.txt"))
            foreach (MethodDefinition m in watched)
            {
                il.WriteLine("== " + m.FullName);
                List<Instruction> ins = m.Body.Instructions.ToList();
                bool all = Replaced.Contains(Key(m));
                for (int k = 0; k < ins.Count; k++)
                {
                    bool near = Enumerable.Range(Math.Max(0, k - 3), Math.Min(ins.Count, k + 4) - Math.Max(0, k - 3))
                        .Any(j => ins[j].Operand is MethodReference && ((MethodReference)ins[j].Operand).DeclaringType.FullName == "T2DCompat.SpriteBatch");
                    if (all || near) il.WriteLine("  " + ins[k]);
                }
            }

        module.Write(output);
        ModuleDefinition back = ModuleDefinition.ReadModule(output, new ReaderParameters { AssemblyResolver = resolver });
        int reread = AllTypes(back.Types).SelectMany(t => t.Methods).Count(m => m.HasBody && m.Body.Instructions.Count > 0);
        Console.WriteLine("written and read back: " + output + " (" + reread + " method bodies)");
        Console.WriteLine(failures == 0 ? "ALL RULES OK" : failures + " FAILURES");
        return failures == 0 ? 0 : 1;
    }
}
