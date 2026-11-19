using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Checks the platform layer's rules (compat/rewriter/PlatformRules.cs) against Rewind's assemblies and T2DCompat.dll,
// without the pipeline:
//   1. every game type, method, field and callee a rule names is in Rewind's Assembly-CSharp (or Steamworks), and every
//      T2DCompat method a rule names is in T2DCompat.dll, with the callee's parameters and result where it replaces one;
//   2. every InControl type and member Assembly-CSharp names has its T2DCompat.InControl_* counterpart, of the same
//      signature once InControl's types are renamed as the rewriter renames them;
//   3. the platform pass (PlatformPasses.cs) runs on a copy of Assembly-CSharp, with InControl's types retargeted as the
//      rewriter does: no problems, no rule without sites, every T2DCompat member the result names resolves, every
//      method the pass changed passes a stack check, and none of them has a Rewind marker left;
//   4. the calls into Steamworks left in the game's code, with the methods they're in.
// Writes the patched copy to <out dir>/Assembly-CSharp.dll (under work/, never committed).
//
//   PlatformCheck.exe rules <Rewind's assemblies dir> <T2DCompat.dll> <out dir>
// Exit code 0 when everything holds, 1 otherwise.
static partial class PlatformCheck
{
    static int failures;

    static void Fail(string message)
    {
        failures++;
        Console.WriteLine("FAIL " + message);
    }

    static int Rules(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: PlatformCheck.exe rules <Rewind's assemblies dir> <T2DCompat.dll> <out dir>");
            return 2;
        }
        string rewind = Path.GetFullPath(args[0]), compatPath = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(output);
        var resolver = new DefaultAssemblyResolver();
        foreach (string dir in resolver.GetSearchDirectories()) resolver.RemoveSearchDirectory(dir);
        resolver.AddSearchDirectory(Path.GetDirectoryName(compatPath));
        resolver.AddSearchDirectory(rewind);
        var read = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate };

        ModuleDefinition compatModule = ModuleDefinition.ReadModule(compatPath, read);
        foreach (TypeDefinition type in AllTypes(compatModule.Types)) DowngradeAssemblies.compat[type.FullName] = type;
        ModuleDefinition game = ModuleDefinition.ReadModule(Path.Combine(rewind, PlatformRules.GameAssembly + ".dll"), read);
        ModuleDefinition steamworks = ModuleDefinition.ReadModule(Path.Combine(rewind, "Steamworks.dll"), read);

        Console.WriteLine("== 1. rule targets");
        CheckTargets(game, steamworks);
        Console.WriteLine("== 2. InControl in T2DCompat");
        CheckInControl(game);
        Console.WriteLine("== 3. the platform pass");
        ApplyAndVerify(game, compatModule, output);
        Console.WriteLine("== 4. calls into Steamworks left in the game's code");
        ListSteamCalls(game);
        Console.WriteLine(failures == 0 ? "OK" : failures + " failures");
        return failures == 0 ? 0 : 1;
    }

    // ---- 1 ----

    static MethodDefinition GameMethod(ModuleDefinition game, string name)
    {
        int split = name.IndexOf("::", StringComparison.Ordinal);
        TypeDefinition type = game.GetType(name.Substring(0, split));
        if (type == null)
        {
            Fail("no type " + name.Substring(0, split));
            return null;
        }
        MethodDefinition[] found = type.Methods.Where(m => m.Name == name.Substring(split + 2)).ToArray();
        if (found.Length != 1) Fail(found.Length + " methods " + name);
        return found.Length == 1 ? found[0] : null;
    }

    static MethodDefinition CompatMethodDefinition(string type, string name)
    {
        TypeDefinition definition;
        if (!DowngradeAssemblies.compat.TryGetValue("T2DCompat." + type, out definition))
        {
            Fail("no T2DCompat." + type);
            return null;
        }
        MethodDefinition[] found = definition.Methods.Where(m => m.Name == name && m.IsStatic && m.IsPublic).ToArray();
        if (found.Length != 1) Fail(found.Length + " public static T2DCompat." + type + "." + name);
        return found.Length == 1 ? found[0] : null;
    }

    static string Signature(MethodReference method)
    {
        return method.ReturnType.FullName + "(" + string.Join(",", method.Parameters.Select(p => p.ParameterType.FullName)) + ")";
    }

    static IEnumerable<MethodReference> Callees(ModuleDefinition module, string fullName)
    {
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                foreach (Instruction instruction in method.Body.Instructions)
                {
                    var called = instruction.Operand as MethodReference;
                    if (called != null && called.FullName == fullName) yield return called;
                }
    }

    static void CheckTargets(ModuleDefinition game, ModuleDefinition steamworks)
    {
        foreach (string[] rule in PlatformRules.Stubbed)
        {
            TypeDefinition type = game.GetType(rule[0]);
            if (type == null) { Fail("stub: no type " + rule[0]); continue; }
            int methods = (rule[1] == "*" ? AllTypes(new[] { type }) : new[] { type })
                .SelectMany(t => t.Methods).Count(m => m.HasBody && !m.IsConstructor && (rule[1] == "*" || m.Name == rule[1]));
            if (methods == 0) Fail("stub: no method " + rule[0] + "::" + rule[1]);
            else Console.WriteLine("  stub " + rule[0] + "::" + rule[1] + ": " + methods + " methods");
        }
        foreach (string[] rule in PlatformRules.Calls)
        {
            MethodReference[] sites = Callees(game, rule[0]).ToArray();
            if (sites.Length == 0) { Fail("call: " + rule[0] + " is never called"); continue; }
            MethodDefinition callee = sites[0].Resolve();
            if (callee == null || callee.Module.Assembly.Name.Name != steamworks.Assembly.Name.Name) Fail("call: " + rule[0] + " doesn't resolve in Steamworks");
            MethodDefinition replacement = CompatMethodDefinition(rule[1], rule[2]);
            if (replacement != null && Signature(replacement) != Signature(sites[0]))
                Fail("call: T2DCompat." + rule[1] + "." + rule[2] + " is " + Signature(replacement) + ", the callee " + Signature(sites[0]));
            Console.WriteLine("  call " + rule[0] + ": " + sites.Length + " sites -> T2DCompat." + rule[1] + "." + rule[2]);
        }
        foreach (string[] rule in PlatformRules.Sequences)
        {
            MethodDefinition method = GameMethod(game, rule[0]);
            MethodDefinition replacement = CompatMethodDefinition(rule[1], rule[2]);
            if (method == null || replacement == null) continue;
            string[] wanted = rule.Skip(3).ToArray();
            int matches = 0;
            IList<Instruction> all = method.Body.Instructions;
            for (int i = 0; i + wanted.Length <= all.Count; i++)
                if (Enumerable.Range(0, wanted.Length).All(j => Show(all[i + j]) == wanted[j])) matches++;
            if (matches != 1) Fail("sequence in " + rule[0] + ": " + matches + " matches, not 1");
            // The run leaves one value: the last instruction's; the replacement must give the same type.
            var last = all.Select(Show).ToList().IndexOf(wanted[wanted.Length - 1]) >= 0 ? (MemberReference)all.First(i => Show(i) == wanted[wanted.Length - 1]).Operand : null;
            var field = last as FieldReference;
            string leaves = field != null ? field.FieldType.FullName : last is MethodReference ? ((MethodReference)last).ReturnType.FullName : null;
            if (leaves != replacement.ReturnType.FullName || replacement.Parameters.Count != 0)
                Fail("sequence in " + rule[0] + ": leaves " + leaves + ", T2DCompat." + rule[1] + "." + rule[2] + " is " + Signature(replacement));
            Console.WriteLine("  sequence in " + rule[0] + ": " + matches + " match -> T2DCompat." + rule[1] + "." + rule[2]);
        }
        foreach (string[] rule in PlatformRules.Forwards)
        {
            MethodDefinition method = GameMethod(game, rule[0]);
            if (method == null) continue;
            MethodDefinition target = method.DeclaringType.Methods.FirstOrDefault(m => m.Name == rule[1] && Signature(m) == Signature(method));
            if (target == null) Fail("forward " + rule[0] + ": no " + rule[1] + " " + Signature(method));
            else Console.WriteLine("  forward " + rule[0] + " -> " + rule[1] + (Markers(target) > 0 ? " (which has Rewind markers)" : ""));
        }
        foreach (string[] rule in PlatformRules.Switches)
        {
            MethodDefinition method = GameMethod(game, rule[0]);
            if (method == null) continue;
            for (int i = 2; i + 1 < rule.Length; i += 2)
            {
                FieldDefinition field = method.DeclaringType.Fields.FirstOrDefault(f => f.Name == rule[i]);
                if (field == null || !field.IsStatic || field.FieldType.FullName != "System.Boolean") Fail("switches " + rule[0] + ": no static bool " + rule[i]);
                MethodDefinition getter = CompatMethodDefinition(rule[1], rule[i + 1]);
                if (getter != null && Signature(getter) != "System.Boolean()") Fail("switches: T2DCompat." + rule[1] + "." + rule[i + 1] + " is " + Signature(getter));
            }
            Console.WriteLine("  switches " + rule[0] + ": " + (rule.Length - 2) / 2 + " fields");
        }
        foreach (string[] rule in PlatformRules.Cuts)
        {
            MethodDefinition method = GameMethod(game, rule[0]);
            if (method == null) continue;
            int calls = method.Body.Instructions.Count(i => i.Operand is MethodReference && ((MethodReference)i.Operand).FullName == rule[1] &&
                i.Next != null && i.Next.OpCode == OpCodes.Pop);
            if (calls != 1) Fail("cut " + rule[0] + ": " + calls + " calls of " + rule[1] + " then pop");
            else Console.WriteLine("  cut " + rule[0] + " after " + rule[1]);
        }
        {
            string[] rule = PlatformRules.SoundLoader;
            MethodDefinition method = GameMethod(game, rule[0]);
            if (method != null)
            {
                if (!method.DeclaringType.Methods.Any(m => m.FullName == rule[1])) Fail("sound loader: no " + rule[1]);
                if (!method.DeclaringType.Fields.Any(f => f.Name == rule[2] && f.FieldType.FullName == "System.String")) Fail("sound loader: no string " + rule[2]);
                MethodDefinition clip = CompatMethodDefinition(rule[3], rule[4]);
                if (clip != null && Signature(clip) != "UnityEngine.AudioClip(System.String)") Fail("sound loader: T2DCompat." + rule[3] + "." + rule[4] + " is " + Signature(clip));
                Console.WriteLine("  sound loader " + rule[0]);
            }
        }
    }

    static string Show(Instruction instruction)
    {
        var member = instruction.Operand as MemberReference;
        return instruction.OpCode.Name + (member != null ? " " + member.FullName : instruction.Operand != null ? " " + instruction.Operand : "");
    }

    static int Markers(MethodDefinition method)
    {
        if (!method.HasBody) return 0;
        return method.Body.Instructions.Count(i => i.Operand is MethodReference && ((MethodReference)i.Operand).DeclaringType.Name == "__Rewind" &&
                                                   ((MethodReference)i.Operand).Name != "Note");
    }

    // ---- 2 ----

    static bool FromInControl(TypeReference type)
    {
        while (type is TypeSpecification) type = ((TypeSpecification)type).ElementType;
        if (type.IsGenericParameter) return false;
        while (type.DeclaringType != null) type = type.DeclaringType;
        var scope = type.Scope as AssemblyNameReference;
        return scope != null && scope.Name == PlatformRules.ReplacedLibrary;
    }

    // A type's full name as the rewriter's retargeting leaves it: InControl.X is T2DCompat.InControl_X.
    static string Renamed(TypeReference type)
    {
        var generic = type as GenericInstanceType;
        if (generic != null) return Renamed(generic.ElementType) + "<" + string.Join(",", generic.GenericArguments.Select(Renamed)) + ">";
        if (type is ArrayType) return Renamed(((ArrayType)type).ElementType) + "[]";
        if (type is ByReferenceType) return Renamed(((ByReferenceType)type).ElementType) + "&";
        if (type.IsGenericParameter) return type.FullName;
        if (FromInControl(type) && type.DeclaringType == null) return "T2DCompat." + PlatformRules.ReplacementPrefix + type.Name;
        return type.FullName;
    }

    static void CheckInControl(ModuleDefinition game)
    {
        int types = 0, members = 0;
        foreach (TypeReference type in game.GetTypeReferences().Where(FromInControl))
        {
            types++;
            if (type.DeclaringType != null) { Fail("nested InControl type " + type.FullName + " (the retargeting skips nested types)"); continue; }
            if (!DowngradeAssemblies.compat.ContainsKey(Renamed(type))) Fail("no " + Renamed(type) + " for " + type.FullName);
        }
        foreach (MemberReference member in game.GetMemberReferences())
        {
            TypeReference owner = member.DeclaringType;
            if (owner == null || !FromInControl(owner)) continue;
            while (owner is TypeSpecification) owner = ((TypeSpecification)owner).ElementType;
            TypeDefinition replacement;
            if (!DowngradeAssemblies.compat.TryGetValue(Renamed(owner), out replacement)) { Fail("no " + Renamed(owner) + " for " + member.FullName); continue; }
            members++;
            var field = member as FieldReference;
            if (field != null)
            {
                FieldDefinition found = replacement.Fields.FirstOrDefault(f => f.Name == field.Name);
                if (found == null) Fail("no field " + replacement.FullName + "::" + field.Name);
                else if (found.FieldType.FullName != Renamed(field.FieldType)) Fail("field " + found.FullName + " is " + found.FieldType.FullName + ", the game's " + Renamed(field.FieldType));
                else if (found.IsPrivate) Fail("field " + found.FullName + " is private");
                continue;
            }
            var method = member as MethodReference;
            if (method == null) continue;
            string wanted = Renamed(method.ReturnType) + "(" + string.Join(",", method.Parameters.Select(p => Renamed(p.ParameterType))) + ")";
            bool ok = false;
            for (TypeDefinition t = replacement; t != null && !ok; t = t.BaseType == null ? null : t.BaseType.Resolve())
                ok = t.Methods.Any(m => m.Name == method.Name && !m.IsPrivate && m.HasThis == method.HasThis && Signature(m) == wanted);
            if (!ok) Fail("no " + replacement.FullName + "::" + method.Name + wanted + " for " + method.FullName);
        }
        Console.WriteLine("  " + types + " InControl types and " + members + " members named by the game");
    }

    // ---- 3 ----

    static string BodyKey(MethodDefinition method)
    {
        if (!method.HasBody) return "";
        return string.Join("\n", method.Body.Instructions.Select(Show));
    }

    static void ApplyAndVerify(ModuleDefinition game, ModuleDefinition compatModule, string output)
    {
        // The rewriter's retargeting, for InControl only: its types to T2DCompat's, by the naming convention.
        var compatReference = new AssemblyNameReference(compatModule.Assembly.Name.Name, compatModule.Assembly.Name.Version);
        game.AssemblyReferences.Add(compatReference);
        int retargeted = 0;
        foreach (TypeReference type in game.GetTypeReferences().ToArray())
        {
            if (type.DeclaringType != null || !FromInControl(type)) continue;
            type.Namespace = "T2DCompat";
            type.Name = PlatformRules.ReplacementPrefix + type.Name;
            type.Scope = compatReference;
            retargeted++;
        }
        Console.WriteLine("  retargeted " + retargeted + " InControl types");

        var before = new Dictionary<MethodDefinition, string>();
        foreach (TypeDefinition type in AllTypes(game.Types))
            foreach (MethodDefinition method in type.Methods) before[method] = BodyKey(method);
        int sites = DowngradeAssemblies.RunPlatformPatches(game);
        Console.WriteLine("  platform pass: " + sites + " sites");
        foreach (KeyValuePair<string, int> rule in DowngradeAssemblies.matches.OrderBy(m => m.Key))
        {
            Console.WriteLine("    " + rule.Key + ": " + rule.Value);
            if (rule.Value == 0) Fail("NO MATCH " + rule.Key);
        }
        foreach (string problem in DowngradeAssemblies.problems) Fail("UNRESOLVED " + problem);

        MethodDefinition[] changed = before.Where(p => BodyKey(p.Key) != p.Value).Select(p => p.Key).ToArray();
        Console.WriteLine("  " + changed.Length + " methods changed");
        foreach (MethodDefinition method in changed)
        {
            string stack = StackCheck(method);
            if (stack != null) Fail("stack: " + method.FullName + ": " + stack);
            int markers = Markers(method);
            if (markers > 0) Console.WriteLine("    " + method.FullName + ": " + markers + " Rewind markers left (other gaps of the method)");
        }

        // Every T2DCompat type and member the module names now resolves.
        int resolved = 0;
        foreach (TypeReference type in game.GetTypeReferences().Where(t => t.Scope == compatReference || (t.Scope is AssemblyNameReference && ((AssemblyNameReference)t.Scope).Name == compatReference.Name)))
        {
            if (type.Resolve() == null) Fail("doesn't resolve: " + type.FullName);
            resolved++;
        }
        foreach (MemberReference member in game.GetMemberReferences())
        {
            TypeReference owner = member.DeclaringType;
            while (owner is TypeSpecification) owner = ((TypeSpecification)owner).ElementType;
            var scope = owner.Scope as AssemblyNameReference;
            if (scope == null || scope.Name != compatReference.Name) continue;
            object definition = member is MethodReference ? (object)((MethodReference)member).Resolve() : member is FieldReference ? ((FieldReference)member).Resolve() : null;
            if (definition == null) Fail("doesn't resolve: " + member.FullName);
            resolved++;
        }
        Console.WriteLine("  " + resolved + " T2DCompat types and members named by the patched assembly resolve");
        game.Write(Path.Combine(output, PlatformRules.GameAssembly + ".dll"));
        Console.WriteLine("  wrote " + Path.Combine(output, PlatformRules.GameAssembly + ".dll"));
    }

    // ---- 4 ----

    static void ListSteamCalls(ModuleDefinition game)
    {
        var users = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (TypeDefinition type in AllTypes(game.Types))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                foreach (Instruction instruction in method.Body.Instructions)
                {
                    var member = instruction.Operand as MemberReference;
                    if (member == null || member.DeclaringType == null) continue;
                    TypeReference owner = member.DeclaringType;
                    while (owner is TypeSpecification) owner = ((TypeSpecification)owner).ElementType;
                    while (owner.DeclaringType != null) owner = owner.DeclaringType;
                    var scope = owner.Scope as AssemblyNameReference;
                    if (scope == null || scope.Name != "Steamworks") continue;
                    string key = type.FullName + "::" + method.Name;
                    int n;
                    users.TryGetValue(key, out n);
                    users[key] = n + 1;
                }
        foreach (KeyValuePair<string, int> user in users) Console.WriteLine("  " + user.Key + ": " + user.Value);
        if (users.Count == 0) Console.WriteLine("  none");
    }

    // ---- a stack check, as the rewriter's CheckStacks: depths agree where paths meet, no underflow, a ret finds its
    // value alone, and no path runs off the end ----

    static string StackCheck(MethodDefinition method)
    {
        MethodBody body = method.Body;
        IList<Instruction> all = body.Instructions;
        if (all.Count == 0) return "empty body";
        var index = new Dictionary<Instruction, int>();
        for (int i = 0; i < all.Count; i++) index[all[i]] = i;
        var depth = new int?[all.Count];
        var pending = new Stack<KeyValuePair<int, int>>();
        pending.Push(new KeyValuePair<int, int>(0, 0));
        foreach (ExceptionHandler handler in body.ExceptionHandlers)
        {
            pending.Push(new KeyValuePair<int, int>(index[handler.HandlerStart], handler.HandlerType == ExceptionHandlerType.Catch ? 1 : 0));
            if (handler.FilterStart != null) pending.Push(new KeyValuePair<int, int>(index[handler.FilterStart], 1));
        }
        int returned = method.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
        while (pending.Count > 0)
        {
            KeyValuePair<int, int> next = pending.Pop();
            int at = next.Key, d = next.Value;
            if (at >= all.Count) return "runs off the end";
            if (depth[at] != null)
            {
                if (depth[at] != d) return "depth " + depth[at] + " and " + d + " meet at " + all[at];
                continue;
            }
            depth[at] = d;
            Instruction instruction = all[at];
            int pops = Pops(instruction, method), pushes = Pushes(instruction);
            if (instruction.OpCode == OpCodes.Ret)
            {
                if (d != returned) return d + " values at " + instruction;
                continue;
            }
            if (d < pops) return "underflow at " + instruction;
            int after = d - pops + pushes;
            if (instruction.OpCode == OpCodes.Leave || instruction.OpCode == OpCodes.Leave_S) after = 0;
            var target = instruction.Operand as Instruction;
            if (target != null) pending.Push(new KeyValuePair<int, int>(index[target], after));
            var targets = instruction.Operand as Instruction[];
            if (targets != null) foreach (Instruction t in targets) pending.Push(new KeyValuePair<int, int>(index[t], after));
            FlowControl flow = instruction.OpCode.FlowControl;
            if (flow != FlowControl.Branch && flow != FlowControl.Throw && instruction.OpCode != OpCodes.Endfinally &&
                instruction.OpCode != OpCodes.Leave && instruction.OpCode != OpCodes.Leave_S && instruction.OpCode != OpCodes.Rethrow)
                pending.Push(new KeyValuePair<int, int>(at + 1, after));
        }
        return null;
    }

    static int Pops(Instruction instruction, MethodDefinition method)
    {
        OpCode code = instruction.OpCode;
        if (code.FlowControl == FlowControl.Call && code != OpCodes.Calli)
        {
            var called = (MethodReference)instruction.Operand;
            int n = called.Parameters.Count;
            if (called.HasThis && code != OpCodes.Newobj) n++;
            return n;
        }
        switch (code.StackBehaviourPop)
        {
            case StackBehaviour.Pop0: return 0;
            case StackBehaviour.Pop1: case StackBehaviour.Popi: case StackBehaviour.Popref: return 1;
            case StackBehaviour.Pop1_pop1: case StackBehaviour.Popi_pop1: case StackBehaviour.Popi_popi: case StackBehaviour.Popi_popi8:
            case StackBehaviour.Popi_popr4: case StackBehaviour.Popi_popr8: case StackBehaviour.Popref_pop1: case StackBehaviour.Popref_popi:
                return 2;
            case StackBehaviour.Popi_popi_popi: case StackBehaviour.Popref_popi_popi: case StackBehaviour.Popref_popi_popi8:
            case StackBehaviour.Popref_popi_popr4: case StackBehaviour.Popref_popi_popr8: case StackBehaviour.Popref_popi_popref:
                return 3;
            case StackBehaviour.PopAll: return 0;
            case StackBehaviour.Varpop: return code == OpCodes.Ret ? (method.ReturnType.MetadataType == MetadataType.Void ? 0 : 1) : 0;
            default: return 0;
        }
    }

    static int Pushes(Instruction instruction)
    {
        OpCode code = instruction.OpCode;
        if (code.FlowControl == FlowControl.Call && code != OpCodes.Calli)
        {
            var called = (MethodReference)instruction.Operand;
            return code == OpCodes.Newobj || called.ReturnType.MetadataType != MetadataType.Void ? 1 : 0;
        }
        switch (code.StackBehaviourPush)
        {
            case StackBehaviour.Push0: return 0;
            case StackBehaviour.Push1_push1: return 2;
            default: return 1;
        }
    }
}
