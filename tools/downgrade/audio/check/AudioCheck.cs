using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Checks the sound loading rules (audio/rewriter/AudioPatches.cs, A1 to A3) with Mono.Cecil, in two parts.
//
// On Rewind's Assembly-CSharp, outside the pipeline:
//   - what the game does before the rules: createSoundFromFile's File.Exists and UnityWebRequest, staticInit's
//     loadSound loop;
//   - A1 with the texture rules (atlas/rewriter/AtlasPatches.cs, R1 to R6, R3 in the same method) in both orders:
//     staticInit comes out the same, and its stack and the new method's balance;
//   - that the rules fail loudly on another shape: A1 run twice, A1 with staticInit's loop call gone, A2 without the
//     platform pass's Sounds.Clip, A3 without A2.
// On the pipeline's written Recovered.T2D (pipeline.py code), where every pass has run:
//   - staticInit queues the sounds (A1) and enters the splash's pages (R3), and loads neither a sound nor a pack;
//   - createSoundFromFile takes its clip from SoundLoads.Clip (A2), and has no File.Exists, UnityWebRequest or
//     Sounds.Clip left;
//   - createSoundFromFile starts with SoundLoads.Defer, and makes a Sound without its clip there (A3); nothing in the
//     game reads Sound.snd or Sound.length but the constructor and the rule's t2dClip and t2dLength, which take the
//     clip from SoundLoads.Clip; A3 run again fails;
//   - every call into T2DCompat.SoundLoads resolves against T2DCompat.dll, and the stacks of AssetsEmbed's and
//     Sound's methods, and of every method that calls t2dClip or t2dLength, balance (rewriter/Stack.cs, the
//     pipeline's own check).
// Prints the changed IL to <out>/audio-rules.il.txt.
//
// Usage: AudioCheck <Rewind's Assembly-CSharp.dll> <written Recovered.T2D.dll> <T2DCompat.dll> <out dir>
//
// The helpers below stand in for the rewriter's own (DowngradeAssemblies.cs, rewriter/Redirect.cs), as in
// atlas/check/RuleCheck.cs, so the rule files compile unchanged here and in the rewriter.
partial class DowngradeAssemblies
{
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

    const string Embed = "com.bitmapbureau.terminator.AssetsEmbed";
    const string SoundType = "com.twissgreen.engine.media.Sound";

    static readonly Func<ModuleDefinition, int>[] AtlasRules = { AtlasTables, AtlasSplash, AtlasPackLoads, AtlasDraws, AtlasSceneHooks };

    static int failures;
    static StreamWriter il;

    static void Fail(string what)
    {
        Console.WriteLine("FAIL " + what);
        failures++;
    }

    static void Ok(string what)
    {
        Console.WriteLine("ok   " + what);
    }

    static void Expect(bool good, string what)
    {
        if (good) Ok(what);
        else Fail(what);
    }

    static MethodDefinition Method(ModuleDefinition module, string type, string name)
    {
        TypeDefinition t = module.GetType(type);
        return t == null ? null : t.Methods.SingleOrDefault(m => m.Name == name && m.HasBody);
    }

    static IEnumerable<MethodReference> Calls(MethodDefinition method)
    {
        return method.Body.Instructions.Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Newobj)
            .Select(i => i.Operand as MethodReference).Where(m => m != null);
    }

    static int CallsTo(MethodDefinition method, string type, string name)
    {
        return Calls(method).Count(m => m.DeclaringType.FullName == type && m.Name == name);
    }

    static int Mentions(MethodDefinition method, string text)
    {
        return method.Body.Instructions.Count(i => i.Operand != null && (i.Operand is MemberReference
            ? ((MemberReference)i.Operand).FullName.Contains(text) : i.Operand.ToString().Contains(text)));
    }

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

    static string Text(MethodDefinition method)
    {
        return string.Join("\n", method.Body.Instructions.Select(i => i.ToString()));
    }

    static void Dump(string title, MethodDefinition method)
    {
        il.WriteLine("== " + title + ": " + method.FullName);
        foreach (Instruction i in method.Body.Instructions) il.WriteLine("  " + i);
    }

    static ModuleDefinition Read(string path)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        return ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = resolver });
    }

    static string Throws(Func<ModuleDefinition, int> rule, ModuleDefinition module)
    {
        try { rule(module); return null; }
        catch (InvalidOperationException e) { return e.Message; }
    }

    // Part 1: the rules on Rewind's assembly.
    static void Rewind(string path)
    {
        ModuleDefinition raw = Read(path);
        MethodDefinition create = Method(raw, SoundType, "createSoundFromFile");
        MethodDefinition init = Method(raw, Embed, "staticInit");
        Console.WriteLine("Rewind's Assembly-CSharp, before the rules:");
        Console.WriteLine("  Sound.createSoundFromFile: " + CallsTo(create, "System.IO.File", "Exists") + " File.Exists call(s), " +
                          Mentions(create, "UnityWebRequest") + " UnityWebRequest reference(s); one File.Exists per clip at start");
        Console.WriteLine("  AssetsEmbed.staticInit: " + CallsTo(init, Embed, "loadSound") + " loadSound call(s), " +
                          CallsTo(init, "com.twissgreen.engine.display.BitmapData", "loadTexturePacks") + " loadTexturePacks call(s)");
        string stackBefore = StackProblem(init);

        // Texture rules then A1, and A1 then the texture rules.
        string[] orders = { "atlas, then audio", "audio, then atlas" };
        var results = new List<string>();
        foreach (string order in orders)
        {
            ModuleDefinition module = Read(path);
            var rules = new List<Func<ModuleDefinition, int>>();
            if (order.StartsWith("atlas")) { rules.AddRange(AtlasRules); rules.Add(AudioStartupLoads); }
            else { rules.Add(AudioStartupLoads); rules.AddRange(AtlasRules); }
            bool ran = true;
            foreach (Func<ModuleDefinition, int> rule in rules)
            {
                string error = Throws(rule, module);
                if (error != null) { Fail(order + ": " + rule.Method.Name + " threw: " + error); ran = false; }
            }
            if (!ran) continue;
            MethodDefinition after = Method(module, Embed, "staticInit");
            MethodDefinition queue = Method(module, Embed, AudioQueueMethod);
            results.Add(Text(after) + "\n--\n" + Text(queue));
            Expect(CallsTo(after, Embed, "loadSound") == 0 && CallsTo(after, Embed, AudioQueueMethod) == 1,
                order + ": staticInit queues the sounds instead of loading them");
            Expect(CallsTo(after, "T2DCompat.Atlas", "Enter") == 1 && CallsTo(after, "com.twissgreen.engine.display.BitmapData", "loadTexturePacks") == 0,
                order + ": staticInit enters the splash's pages (R3) instead of loading the pack");
            Instruction call = after.Body.Instructions.Single(i => i.Operand is MethodReference && ((MethodReference)i.Operand).Name == AudioQueueMethod);
            Expect(call.Next.OpCode == OpCodes.Nop, order + ": the loop call's pop is a nop");
            string stack = StackProblem(after);
            if (stack == null) Ok(order + ": staticInit's stack balances");
            else if (stackBefore != null) Console.WriteLine("note: " + order + ": staticInit's stack check fails before and after the rules (" + stack + ")");
            else Fail(order + ": staticInit's stack after the rules: " + stack);
            Expect(StackProblem(queue) == null && queue.IsStatic && queue.ReturnType.MetadataType == MetadataType.Void &&
                   CallsTo(queue, "T2DCompat.SoundLoads", "Queue") == 1, order + ": " + AudioQueueMethod + " is static void(int), balanced, and calls SoundLoads.Queue");
            if (order.StartsWith("atlas"))
            {
                Dump("Rewind + atlas + A1", after);
                Dump("Rewind + atlas + A1", queue);
            }
        }
        if (results.Count == 2) Expect(results[0] == results[1], "staticInit and " + AudioQueueMethod + " are the same in both orders");

        // Loud failures on other shapes.
        ModuleDefinition twice = Read(path);
        AudioStartupLoads(twice);
        string second = Throws(AudioStartupLoads, twice);
        Expect(second != null, "A1 run twice fails: " + second);
        ModuleDefinition noLoop = Read(path);
        MethodDefinition noLoopInit = Method(noLoop, Embed, "staticInit");
        foreach (Instruction i in noLoopInit.Body.Instructions.Where(i => i.Operand is MethodReference && ((MethodReference)i.Operand).Name == "loadSound").ToArray())
            i.Operand = Method(noLoop, Embed, "loadText");
        string missing = Throws(AudioStartupLoads, noLoop);
        Expect(missing != null, "A1 without staticInit's loadSound call fails: " + missing);
        string a2 = Throws(AudioClipLoads, Read(path));
        Expect(a2 != null, "A2 without the platform pass's Sounds.Clip fails: " + a2);
        string a3 = Throws(AudioDeferredClips, Read(path));
        Expect(a3 != null, "A3 without A2 fails: " + a3);
    }

    // Part 2: the pipeline's written assembly.
    static void Written(string path, string compatPath)
    {
        Console.WriteLine("The pipeline's " + Path.GetFileName(path) + ":");
        ModuleDefinition module = Read(path);
        MethodDefinition init = Method(module, Embed, "staticInit");
        MethodDefinition queue = Method(module, Embed, AudioQueueMethod);
        MethodDefinition load = Method(module, Embed, "loadSound");
        MethodDefinition create = Method(module, SoundType, "createSoundFromFile");
        if (init == null || queue == null || load == null || create == null)
        {
            Fail("staticInit, " + AudioQueueMethod + ", loadSound or createSoundFromFile is missing");
            return;
        }
        Expect(CallsTo(init, Embed, "loadSound") == 0 && CallsTo(init, Embed, AudioQueueMethod) == 1, "staticInit queues the sounds (A1)");
        Instruction enter = init.Body.Instructions.SingleOrDefault(i => i.Operand is MethodReference &&
            ((MethodReference)i.Operand).DeclaringType.FullName == "T2DCompat.Atlas" && ((MethodReference)i.Operand).Name == "Enter");
        Expect(enter != null && enter.Previous.OpCode == OpCodes.Ldstr && (string)enter.Previous.Operand == "app:BBSplash" &&
               CallsTo(init, "com.twissgreen.engine.display.BitmapData", "loadTexturePacks") == 0, "staticInit enters app:BBSplash's pages (R3)");
        Expect(CallsTo(queue, "T2DCompat.SoundLoads", "Queue") == 1, AudioQueueMethod + " calls SoundLoads.Queue");
        Expect(CallsTo(load, SoundType, "createSoundFromFile") == 1, "loadSound still makes the Sound with createSoundFromFile");
        Expect(CallsTo(create, "T2DCompat.SoundLoads", "Clip") == 1 && CallsTo(create, "T2DCompat.Sounds", "Clip") == 0,
            "createSoundFromFile takes its clip from SoundLoads.Clip (A2)");
        Expect(CallsTo(create, "System.IO.File", "Exists") == 0 && Mentions(create, "UnityWebRequest") == 0,
            "createSoundFromFile has no File.Exists and no UnityWebRequest (the platform layer's loader): no File.Exists per clip");
        int soundsClip = 0, exists = 0;
        var existsIn = new List<string>();
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
            {
                soundsClip += CallsTo(method, "T2DCompat.Sounds", "Clip");
                int n = CallsTo(method, "System.IO.File", "Exists");
                if (n > 0) { exists += n; existsIn.Add(type.Name + "." + method.Name); }
            }
        Expect(soundsClip == 0, "no call of T2DCompat.Sounds.Clip is left");
        Console.WriteLine("  File.Exists left in the game: " + exists + " site(s)" + (existsIn.Count > 0 ? " (" + string.Join(", ", existsIn.Distinct()) + ")" : ""));

        // The calls into SoundLoads resolve against T2DCompat.dll.
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(compatPath)));
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        ModuleDefinition resolving = ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = resolver });
        var soundLoads = AllTypes(resolving.Types).SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(Calls)
            .Where(m => m.DeclaringType.FullName == "T2DCompat.SoundLoads").ToList();
        int resolved = soundLoads.Count(m => { try { return m.Resolve() != null; } catch (AssemblyResolutionException) { return false; } });
        Expect(soundLoads.Count == 4 && resolved == 4 && soundLoads.Count(m => m.Name == "Defer") == 1,
            "the " + soundLoads.Count + " calls into T2DCompat.SoundLoads (Queue, Clip twice, Defer) resolve (" + resolved + ")");

        // A3.
        MethodDefinition clipOf = Method(module, SoundType, AudioClipMethod);
        MethodDefinition lengthOf = Method(module, SoundType, AudioLengthMethod);
        MethodDefinition constructor = module.GetType(SoundType).Methods.Single(m => m.IsConstructor && !m.IsStatic);
        if (clipOf == null || lengthOf == null)
        {
            Fail("Sound has no " + AudioClipMethod + " or " + AudioLengthMethod + " (A3)");
            return;
        }
        Expect(create.Body.Instructions[0].OpCode == OpCodes.Ldarg_0 && CallsTo(create, "T2DCompat.SoundLoads", "Defer") == 1 &&
               ((MethodReference)create.Body.Instructions[1].Operand).Name == "Defer" &&
               create.Body.Instructions.Count(i => i.OpCode == OpCodes.Newobj && i.Operand == constructor) == 2,
            "createSoundFromFile starts with SoundLoads.Defer and makes the Sound without its clip there (A3)");
        Expect(clipOf.IsStatic && clipOf.ReturnType.FullName == "UnityEngine.AudioClip" && CallsTo(clipOf, "T2DCompat.SoundLoads", "Clip") == 1,
            AudioClipMethod + " is static AudioClip(Sound) and takes the clip from SoundLoads.Clip");
        Expect(lengthOf.IsStatic && lengthOf.ReturnType.MetadataType == MetadataType.Double && CallsTo(lengthOf, SoundType, AudioClipMethod) == 1,
            AudioLengthMethod + " is static double(Sound) and calls " + AudioClipMethod);
        var direct = new List<string>();
        var through = new List<MethodDefinition>();
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
            {
                bool own = method == constructor || method == clipOf || method == lengthOf;
                foreach (Instruction i in method.Body.Instructions)
                {
                    var field = i.Operand as FieldReference;
                    if (!own && field != null && field.DeclaringType.FullName == SoundType && (field.Name == "snd" || field.Name == "length") &&
                        i.OpCode != OpCodes.Stfld)
                        direct.Add(method.FullName + ": " + i);
                }
                if (!own && (CallsTo(method, SoundType, AudioClipMethod) > 0 || CallsTo(method, SoundType, AudioLengthMethod) > 0))
                    through.Add(method);
            }
        Expect(direct.Count == 0, "nothing reads Sound.snd or Sound.length but the constructor, " + AudioClipMethod + " and " + AudioLengthMethod +
            (direct.Count > 0 ? ": " + string.Join("; ", direct) : ""));
        foreach (MethodDefinition method in through)
            Console.WriteLine("  " + method.DeclaringType.Name + "." + method.Name + ": " + CallsTo(method, SoundType, AudioClipMethod) + " " +
                              AudioClipMethod + ", " + CallsTo(method, SoundType, AudioLengthMethod) + " " + AudioLengthMethod);
        Expect(through.Sum(m => CallsTo(m, SoundType, AudioClipMethod)) == 4 && through.Sum(m => CallsTo(m, SoundType, AudioLengthMethod)) == 2,
            "the game's 4 reads of Sound.snd and 2 of Sound.length go through " + AudioClipMethod + " and " + AudioLengthMethod);
        string again = Throws(AudioDeferredClips, Read(path));
        Expect(again != null, "A3 run again fails: " + again);

        int checkedMethods = 0;
        var stacked = new List<MethodDefinition>();
        foreach (string type in new[] { Embed, SoundType })
            stacked.AddRange(module.GetType(type).Methods.Where(m => m.HasBody));
        stacked.AddRange(through.Where(m => !stacked.Contains(m)));
        foreach (MethodDefinition method in stacked)
        {
            string stack = StackProblem(method);
            if (stack != null) Fail("stack of " + method.FullName + ": " + stack);
            checkedMethods++;
        }
        Ok("stacks of AssetsEmbed's and Sound's methods and the callers of " + AudioClipMethod + " and " + AudioLengthMethod + ", " +
           checkedMethods + " in all, checked");
        Dump("written", init);
        Dump("written", queue);
        Dump("written", create);
        Dump("written", clipOf);
        Dump("written", lengthOf);
        foreach (MethodDefinition method in through) Dump("written", method);
    }

    static int Main(string[] args)
    {
        if (args.Length != 4)
        {
            Console.WriteLine("usage: AudioCheck <Rewind's Assembly-CSharp.dll> <written Recovered.T2D.dll> <T2DCompat.dll> <out dir>");
            return 2;
        }
        ModuleDefinition compatModule = ModuleDefinition.ReadModule(args[2]);
        foreach (TypeDefinition type in compatModule.Types) compat[type.FullName] = type;
        Directory.CreateDirectory(args[3]);
        using (il = new StreamWriter(Path.Combine(args[3], "audio-rules.il.txt")))
        {
            Rewind(args[0]);
            Written(args[1], args[2]);
        }
        Console.WriteLine(failures == 0 ? "ALL AUDIO RULES OK" : failures + " FAILURES");
        return failures == 0 ? 0 : 1;
    }
}
