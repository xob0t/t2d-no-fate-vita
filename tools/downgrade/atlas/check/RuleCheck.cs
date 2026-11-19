using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Runs the texture rules (atlas/rewriter/AtlasPatches.cs) on Rewind's Assembly-CSharp alone, outside the pipeline,
// and checks what they did: each rule's sites, the stack of every method they changed (rewriter/Stack.cs, the
// pipeline's own check), that nothing in the game still loads or reads a texture pack, and that the written assembly
// reads back. Prints the changed IL to <out>.il.txt.
//
// Usage: RuleCheck <Assembly-CSharp.dll> <T2DCompat or Atlas.dll> <out.dll>
//
// The helpers below stand in for the rewriter's own (DowngradeAssemblies.cs, rewriter/Redirect.cs; from
// butcher-vita's rewriter, MIT, same author) with the same names and behaviour, so AtlasPatches.cs compiles
// unchanged in both.
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

    static readonly Pass[] AtlasPasses =
    {
        new Pass("atlas", Scope.Game, AtlasTables),
        new Pass("atlas", Scope.Game, AtlasSplash),
        new Pass("atlas", Scope.Game, AtlasPackLoads),
        new Pass("atlas", Scope.Game, AtlasDraws),
        new Pass("atlas", Scope.Game, AtlasSceneHooks),
    };

    static readonly string[] Watched =
    {
        "com.bitmapbureau.terminator.AssetsPack::.cctor", "com.bitmapbureau.terminator.AssetsPack::staticInit5",
        "com.bitmapbureau.terminator.AssetsPack::staticInit9", "com.bitmapbureau.terminator.apps.BBSplash::start",
        "com.bitmapbureau.terminator.apps.BBSplash::update", "com.bitmapbureau.terminator.AssetsEmbed::staticInit",
        "com.twissgreen.engine.display.BitmapData::loadTexturePacks", "com.twissgreen.engine.display.BitmapData::unityDraw",
        "com.twissgreen.engine.display.BitmapData::unityDrawClippedWidth", "com.bitmapbureau.terminator.Transition::update",
        "com.bitmapbureau.terminator.game.GameMap::loadFile", "com.bitmapbureau.terminator.game.Game::loadScriptByLevelID",
    };

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
        var stackBefore = watched.ToDictionary(m => m, m => StackProblem(m));
        int failures = 0;

        foreach (Pass pass in AtlasPasses)
        {
            try
            {
                int sites = pass.Run(module);
                Count("pass " + pass.Name, sites);
                Console.WriteLine("pass " + pass.Name + ": " + sites + " sites");
            }
            catch (InvalidOperationException e)
            {
                Console.WriteLine("FAIL pass " + pass.Name + ": " + e.Message);
                failures++;
            }
        }
        foreach (KeyValuePair<string, int> m in matches.OrderBy(m => m.Key))
            if (m.Value == 0) { Console.WriteLine("NO MATCH " + m.Key); failures++; }

        // Nothing loads a pack or reads BitmapData.texpack any more, but initBitmapData's allocation of it.
        foreach (MethodDefinition m in methods)
            foreach (Instruction i in m.Body.Instructions)
            {
                var called = i.Operand as MethodReference;
                if (called != null && called.Name == "loadTexturePacks" && called.DeclaringType.Name == "BitmapData")
                {
                    Console.WriteLine("FAIL still calls loadTexturePacks: " + Key(m) + " " + i);
                    failures++;
                }
                var field = i.Operand as FieldReference;
                if (field != null && field.Name == "texpack" && field.DeclaringType.Name == "BitmapData" &&
                    m.Name != "initBitmapData" && !(m.Name == "loadTexturePacks"))
                {
                    Console.WriteLine("FAIL still uses BitmapData.texpack: " + Key(m) + " " + i);
                    failures++;
                }
                if (field != null && field.Name.StartsWith("imagetexframes_uv") && i.OpCode == OpCodes.Stsfld &&
                    !(m.Body.Instructions.Count == 3 && m.Body.Instructions[0].OpCode == OpCodes.Call))
                {
                    Console.WriteLine("FAIL another store of " + field.Name + ": " + Key(m));
                    failures++;
                }
            }

        foreach (MethodDefinition m in watched)
        {
            string after = StackProblem(m);
            if (after != null && stackBefore[m] == null)
            {
                Console.WriteLine("FAIL stack after the rules in " + Key(m) + ": " + after);
                failures++;
            }
            else if (after != null)
                Console.WriteLine("note: stack check fails before and after the rules in " + Key(m) + " (" + after + ")");
            else
                Console.WriteLine("stack ok: " + Key(m));
        }

        using (var il = new StreamWriter(output + ".il.txt"))
            foreach (MethodDefinition m in watched)
            {
                il.WriteLine("== " + m.FullName);
                List<Instruction> ins = m.Body.Instructions.ToList();
                var show = new HashSet<int>();
                for (int k = 0; k < ins.Count; k++)
                {
                    bool atlas = ins[k].Operand is MethodReference && ((MethodReference)ins[k].Operand).DeclaringType.FullName == "T2DCompat.Atlas";
                    bool forget = ins[k].Operand is FieldReference && ((FieldReference)ins[k].Operand).Name == "texused" && ins[k].OpCode == OpCodes.Stsfld;
                    if (atlas || forget || ins.Count <= 8 || ins[k].OpCode == OpCodes.Ldc_I4 && (int)ins[k].Operand == 12 && k + 1 < ins.Count && ins[k + 1].OpCode == OpCodes.Sub)
                        for (int d = -3; d <= 3; d++) show.Add(k + d);
                }
                int last = -2;
                foreach (int k in show.Where(k => k >= 0 && k < ins.Count).OrderBy(k => k))
                {
                    if (k != last + 1) il.WriteLine("  ...");
                    il.WriteLine("  " + ins[k]);
                    last = k;
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
