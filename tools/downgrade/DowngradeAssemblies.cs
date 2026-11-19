using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Rewrites game assemblies built against a newer Unity so they load against an
// older engine's assemblies: calls to an API missing from the target version go to the
// compat assembly (T2DCompat). The other passes (Passes lists them) send game
// calls through T2DCompat rules.
//
// From butcher-vita's rewriter (MIT, same author). Here its input is Rewind's recovered
// assemblies (pipeline.py recover), not a Mono game's Managed folder.
//
// Usage: DowngradeAssemblies <input dir> <output dir> <compat.dll> <roots> <reference dir>...
// <roots> is a comma-separated list of assembly names; only they and the input
// assemblies they reference are written. A name with a leading '-' is never written: its types
// go to T2DCompat like any the target lacks.
//
// The rewriter is this file and rewriter/*.cs, compiled together in the order of
// pipeline.REWRITER_SOURCES; DowngradeAssemblies is one partial class across them:
//   DowngradeAssemblies.cs  Main, Rewrite, Verify, Passes (every pass in the order they run), body sizes
//   Redirect.cs             missing types and members to T2DCompat, attributes, references,
//                           the IL helpers the passes share
//   Runtime.cs              type layouts the target's Mono rejects (interfaces)
//   RewindGaps.cs           lists the methods Rewind could not recover completely (its markers)
//   Stack.cs                the stack before each instruction, for the stack check
partial class DowngradeAssemblies
{
    // Types that exist in the target under another name.
    static readonly Dictionary<string, string> renamedTypes = new Dictionary<string, string>
    {
        { "UnityEngine.ExecuteAlways", "UnityEngine.ExecuteInEditMode" },
        { "UnityEngine.Rendering.RenderPipelineAsset", "UnityEngine.Experimental.Rendering.RenderPipelineAsset" },
    };

    // Nested types that exist in the target under another name, by full name (outer/inner).
    // InputField's end-edit event is a UnityEvent<string> in both, named SubmitEvent in 2018.
    static readonly Dictionary<string, string> renamedNestedTypes = new Dictionary<string, string>
    {
        { "UnityEngine.UI.InputField/EndEditEvent", "SubmitEvent" },
    };

    // Attributes stripped although the target has their type: its constructors differ, and they
    // only tell the editor which API version the assembly was compiled against.
    static readonly HashSet<string> strippedAttributes = new HashSet<string>
    {
        "UnityEngine.UnityAPICompatibilityVersionAttribute",
    };

    // Unity reserves these names for assemblies compiled from project scripts. pipeline.ASSEMBLY_NAMES and
    // Compat2018.cs name the new ones too: keep them in step.
    static readonly Dictionary<string, string> renamedAssemblies = new Dictionary<string, string>
    {
        { "Assembly-CSharp", "Recovered.T2D" },
        { "Assembly-CSharp-firstpass", "Recovered.Firstpass" },
    };

    static readonly Dictionary<string, TypeDefinition> target = new Dictionary<string, TypeDefinition>();
    static readonly Dictionary<string, TypeDefinition> compat = new Dictionary<string, TypeDefinition>();
    static readonly HashSet<string> shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // Input assemblies never written (the roots' "-" names): T2DCompat replaces them.
    static readonly HashSet<string> excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static readonly List<string> problems = new List<string>();
    // Type references the target and T2DCompat lack, by module name: reported as problems only if the written module
    // still references them. Rewrite's retargeting reads the file's own type references, which keep types nothing uses
    // any more (System.RuntimeType in Rewind's output; what a pass took out, such as Bindings.ThrowHelper's callers).
    static readonly Dictionary<string, List<string>> missingTypes = new Dictionary<string, List<string>>();
    // Methods and types a pass could not inspect because a type or method they name failed to
    // resolve (printed as SKIPPED; the run still succeeds): the pass left them as they were, so its
    // rule may be missing there. For MarkNativeCallbacks that means a native callback IL2CPP would
    // reject at run time; for ReimplementedInterfaces a listing Mono may reject (TypeLoadException).
    static readonly List<string> skipped = new List<string>();
    static readonly HashSet<string> strippedTypes = new HashSet<string>();
    static ModuleDefinition compatModule;

    // Which assemblies a pass runs on: all, or the two game assemblies.
    enum Scope { All, Game }

    static bool IsGameAssembly(ModuleDefinition module)
    {
        return renamedAssemblies.ContainsValue(module.Assembly.Name.Name);
    }

    class Pass
    {
        public readonly string Group;
        public readonly Scope Scope;
        public readonly Func<ModuleDefinition, int> Run;
        // A build switch (common.BUILD_SWITCHES) the pass runs under, only when it is 1; null: always.
        public readonly string Switch;

        public Pass(string group, Scope scope, Func<ModuleDefinition, int> run, string buildSwitch = null)
        {
            Group = group;
            Scope = scope;
            Run = run;
            Switch = buildSwitch;
        }

        public string Name { get { return Run.Method.Name; } }
    }

    // The passes after the type retargeting, in the order they run: later ones build on earlier
    // ones (their comments say which). Each returns the number of sites it changed; the stats
    // show them by group, and a pass that changed nothing in any assembly fails the run.
    // Passes that may change nothing in every assembly: rules for native callbacks and reimplemented
    // interfaces cost nothing to keep, and ListRewindGaps finds nothing once every gap is repaired.
    // AtlasDraws (R5) finds nothing once the sprite batch's R8 has taken both draw methods (docs/drawing.md).
    static readonly HashSet<string> OptionalPasses = new HashSet<string> { "MarkNativeCallbacks", "ReimplementedInterfaces", "ListRewindGaps", "TypeGapValues", "AtlasDraws", "SplitLongMethods" };

    static readonly Pass[] Passes =
    {
        new Pass("redirect", Scope.All, RedirectMembers),
        new Pass("redirect", Scope.All, MarkNativeCallbacks),
        // Runtime.cs
        new Pass("runtime", Scope.All, ReimplementedInterfaces),
        // spritebatch/rewriter/SpriteBatchPatches.cs: the batched sprite renderer (docs/drawing.md), R8, R7, R9, R10.
        // Before the gap passes, as R7 and R8 replace bodies that have gaps (TypeGapValues needn't type what R8 throws
        // away, and R8 checks the old body's draws), and before the atlas passes (AtlasDraws skips R8's).
        new Pass("spritebatch", Scope.Game, SpriteBatchDraws),
        new Pass("spritebatch", Scope.Game, SpriteBatchMeshes),
        new Pass("spritebatch", Scope.Game, SpriteBatchFlush),
        new Pass("spritebatch", Scope.Game, SpriteBatchFilters),
        // GapTyping.cs: well-typed stand-ins for values of the wrong kind left in gap methods, before they are counted.
        new Pass("rewind", Scope.Game, TypeGapValues),
        // RewindGaps.cs: counts, changes nothing.
        new Pass("rewind", Scope.All, ListRewindGaps),
        // atlas/rewriter/AtlasPatches.cs: the texture pages (docs/textures.md), R1 to R6.
        new Pass("atlas", Scope.Game, AtlasTables),
        new Pass("atlas", Scope.Game, AtlasSplash),
        new Pass("atlas", Scope.Game, AtlasPackLoads),
        new Pass("atlas", Scope.Game, AtlasDraws),
        new Pass("atlas", Scope.Game, AtlasSceneHooks),
        // audio/rewriter/AudioPatches.cs: the sounds load in the background (docs/audio.md), A1 to A3.
        new Pass("audio", Scope.Game, AudioStartupLoads),
        new Pass("audio", Scope.Game, AudioClipLoads),
        new Pass("audio", Scope.Game, AudioDeferredClips),
        // rewriter/MenuCursor.cs: Menu.moveCursor's grid search stops at its first hit (docs/drawing.md), M1. Before Trace,
        // which changes the bodies M1 pins by hash.
        new Pass("menus", Scope.Game, MenuCursorSearch),
        // rewriter/MenuStrings.cs: Utils.getTimeString keeps its strings (the high score table), M2.
        new Pass("menus", Scope.Game, MenuTimeStrings),
        // rewriter/Trace.cs: a diagnostic build's method trail (T2D_TRACE=1), after the passes that change bodies.
        new Pass("diagnostics", Scope.Game, TraceMethods, "T2D_TRACE"),
        // SplitMethods.cs: last, so every pass above sees whole methods; cuts what psp2snc -O3 can't compile.
        new Pass("size", Scope.Game, SplitLongMethods),
    };

    // Sites changed per rule (a pass, a row of a rule table, a game type a pass looks for),
    // summed over the assemblies: a rule with none in any assembly fails the run, as the game
    // no longer has what it expects. Passes run per assembly, so none in one is normal.
    static readonly Dictionary<string, int> matches = new Dictionary<string, int>();

    static void Count(string rule, int sites)
    {
        int sum;
        matches.TryGetValue(rule, out sum);
        matches[rule] = sum + sites;
    }

    // module.GetType(name), counted: a game type a pass looks for must be in some assembly.
    static TypeDefinition GameType(ModuleDefinition module, string name)
    {
        TypeDefinition type = module.GetType(name);
        Count("type " + name, type == null ? 0 : 1);
        return type;
    }

    // References in the game assemblies name each other by their new names (Recovered.*);
    // their files keep the original names.
    class RenamedAssemblyResolver : DefaultAssemblyResolver
    {
        public override AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            string original = renamedAssemblies.Where(p => p.Value == name.Name).Select(p => p.Key).FirstOrDefault();
            return base.Resolve(original == null ? name : new AssemblyNameReference(original, name.Version));
        }
    }

    static int Main(string[] args)
    {
        string input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
        compatModule = ModuleDefinition.ReadModule(args[2]);
        foreach (TypeDefinition type in compatModule.Types) compat[type.FullName] = type;
        foreach (string dir in args.Skip(4))
            foreach (string file in Directory.GetFiles(dir, "*.dll").OrderBy(Priority))
                Index(file);
        // What the target provides (its class libraries, with netstandard in Facades, and engine
        // modules) comes from the reference folders, never from the input: a game built by a newer
        // Unity ships its own copies.
        var provided = new HashSet<string>(args.Skip(4).SelectMany(dir => Directory.GetFiles(dir, "*.dll")
            .Concat(Directory.Exists(Path.Combine(dir, "Facades")) ? Directory.GetFiles(Path.Combine(dir, "Facades"), "*.dll") : new string[0]))
            .Select(Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase);
        var available = Directory.GetFiles(input, "*.dll")
            .Where(f => !Path.GetFileName(f).StartsWith("UnityEngine.") && !provided.Contains(Path.GetFileNameWithoutExtension(f)))
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase);
        string[] names = args[3].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        string[] roots = names.Where(n => !n.StartsWith("-")).ToArray();
        foreach (string name in names.Where(n => n.StartsWith("-")))
        {
            available.Remove(name.Substring(1));
            excluded.Add(name.Substring(1));
        }
        string[] absent = roots.Where(root => !available.ContainsKey(root)).ToArray();
        if (absent.Length > 0)
        {
            Console.WriteLine("MISSING root assemblies, not in " + input + ": " + string.Join(", ", absent));
            return 2;
        }
        // The roots, then what they reference from the input folder (the rest, such as
        // mscorlib, comes from the reference folders).
        var pending = new Queue<string>(roots);
        while (pending.Count > 0)
        {
            string name = pending.Dequeue();
            if (!available.ContainsKey(name) || !shipped.Add(name)) continue;
            using (var module = ModuleDefinition.ReadModule(available[name]))
                foreach (AssemblyNameReference reference in module.AssemblyReferences) pending.Enqueue(reference.Name);
        }
        Directory.CreateDirectory(output);
        var resolver = new RenamedAssemblyResolver();
        resolver.AddSearchDirectory(input);
        foreach (string dir in args.Skip(4)) resolver.AddSearchDirectory(dir);
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2])));
        foreach (string file in shipped.Select(n => available[n]).ToArray())
        {
            using (var assembly = AssemblyDefinition.ReadAssembly(file, new ReaderParameters { AssemblyResolver = resolver }))
            {
                string stats = Rewrite(assembly);
                string destination = Path.Combine(output, assembly.Name.Name + ".dll");
                assembly.Write(destination);
                ReportMissingTypes(assembly.MainModule.Name, destination);
                Console.WriteLine(Path.GetFileName(file) + " -> " + Path.GetFileName(destination) + " " + stats);
            }
        }
        Verify(output, args.Skip(4).Concat(new[] { Path.GetDirectoryName(Path.GetFullPath(args[2])) }));
        foreach (string problem in problems.Distinct()) Console.WriteLine("UNRESOLVED " + problem);
        foreach (string site in skipped.Distinct()) Console.WriteLine("SKIPPED " + site);
        string[] unmatched = matches.Where(m => m.Value == 0).Select(m => m.Key).ToArray();
        foreach (string rule in unmatched) Console.WriteLine("NO MATCH " + rule + " (in none of the assemblies; the game no longer has what it expects)");
        return problems.Count > 0 ? 3 : unmatched.Length > 0 ? 4 : 0;
    }

    // Every type and member the written assemblies reference must resolve against each other, the
    // target's engine and T2DCompat, as Unity's linker resolves them (it stops at the first that
    // does not, after a build of the whole player). The input folder is not searched: it holds the
    // source version's assemblies. A failure is reported as UNRESOLVED.
    static void Verify(string output, IEnumerable<string> references)
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (string dir in resolver.GetSearchDirectories()) resolver.RemoveSearchDirectory(dir);
        resolver.AddSearchDirectory(output);
        foreach (string dir in references) resolver.AddSearchDirectory(dir);
        foreach (string file in Directory.GetFiles(output, "*.dll"))
        {
            using (var module = ModuleDefinition.ReadModule(file, new ReaderParameters { AssemblyResolver = resolver }))
            {
                foreach (TypeReference type in module.GetTypeReferences())
                {
                    try { if (type.Resolve() == null) problems.Add(module.Name + ": verify type " + type.FullName); }
                    catch (AssemblyResolutionException e) { problems.Add(module.Name + ": verify type " + type.FullName + " (" + e.AssemblyReference.Name + ")"); }
                }
                if (module.Assembly.HasSecurityDeclarations || AllTypes(module.Types).Any(t => t.HasSecurityDeclarations || t.Methods.Any(m => m.HasSecurityDeclarations)))
                    problems.Add(module.Name + ": verify security declarations left");
                foreach (TypeReference type in AttributeTypes(module))
                {
                    try { if (type.Resolve() == null) problems.Add(module.Name + ": verify attribute type " + type.FullName); }
                    catch (AssemblyResolutionException e) { problems.Add(module.Name + ": verify attribute type " + type.FullName + " (" + e.AssemblyReference.Name + ")"); }
                }
                foreach (MemberReference member in module.GetMemberReferences())
                {
                    // Multidimensional arrays' Get, Set, Address and constructor come from the runtime.
                    if (member.DeclaringType is ArrayType) continue;
                    try
                    {
                        var method = member as MethodReference;
                        var field = member as FieldReference;
                        if ((method != null && method.Resolve() == null) || (field != null && field.Resolve() == null))
                            problems.Add(module.Name + ": verify member " + member.FullName);
                    }
                    catch (AssemblyResolutionException e) { problems.Add(module.Name + ": verify member " + member.FullName + " (" + e.AssemblyReference.Name + ")"); }
                }
            }
        }
    }

    // The types named in a module's attribute arguments.
    static IEnumerable<TypeReference> AttributeTypes(ModuleDefinition module)
    {
        var found = new List<TypeReference>();
        foreach (ICustomAttributeProvider provider in AttributeProviders(module))
        {
            if (!provider.HasCustomAttributes) continue;
            foreach (CustomAttribute attribute in provider.CustomAttributes)
            {
                try
                {
                    foreach (CustomAttributeArgument argument in attribute.ConstructorArguments
                        .Concat(attribute.Fields.Select(f => f.Argument)).Concat(attribute.Properties.Select(p => p.Argument)))
                        CollectTypes(argument, found);
                }
                catch (AssemblyResolutionException) { }
            }
        }
        return found;
    }

    static void CollectTypes(CustomAttributeArgument argument, List<TypeReference> found)
    {
        var type = argument.Value as TypeReference;
        if (type != null && !(type is TypeSpecification) && !type.IsGenericParameter) found.Add(type);
        var array = argument.Value as CustomAttributeArgument[];
        if (array != null) foreach (CustomAttributeArgument item in array) CollectTypes(item, found);
        if (argument.Value is CustomAttributeArgument) CollectTypes((CustomAttributeArgument)argument.Value, found);
    }

    static int Priority(string file)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        return name == "mscorlib" ? 0 : name == "System" ? 1 : name == "System.Core" ? 2 : 3;
    }

    // Namespaces of the game's own assemblies that T2DCompat replaces where the other assemblies use them
    // (butcher-vita's FMOD Studio API); none here yet.
    static readonly Dictionary<string, string[]> ReplacedGameNamespaces = new Dictionary<string, string[]>();

    static bool IsReplacedGameType(TypeReference type)
    {
        var scope = type.Scope as AssemblyNameReference;
        string[] namespaces;
        return type.DeclaringType == null && scope != null && ReplacedGameNamespaces.TryGetValue(scope.Name, out namespaces) &&
               namespaces.Contains(type.Namespace);
    }

    // The public types of a reference assembly. The module stays open (it is never disposed):
    // Cecil reads the indexed types' members from it later, on demand.
    static void Index(string file)
    {
        ModuleDefinition module;
        try { module = ModuleDefinition.ReadModule(file); }
        catch (BadImageFormatException) { return; }
        foreach (TypeDefinition type in AllTypes(module.Types))
        {
            if (type.IsPublic || type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamilyOrAssembly)
                if (!target.ContainsKey(type.FullName)) target[type.FullName] = type;
        }
    }

    static string Rewrite(AssemblyDefinition assembly)
    {
        ModuleDefinition module = assembly.MainModule;
        // The repair engine (repairs/RewriterHook.cs), first: its rules key Rewind's own names and hash the IL as
        // Rewind wrote it, before the rename and every other pass.
        string repaired = RepairRewindGaps(assembly);
        string newName;
        if (renamedAssemblies.TryGetValue(assembly.Name.Name, out newName))
        {
            assembly.Name.Name = newName;
            module.Name = newName + ".dll";
        }
        foreach (AssemblyNameReference reference in module.AssemblyReferences)
            if (renamedAssemblies.TryGetValue(reference.Name, out newName)) reference.Name = newName;

        // Each body's size before the passes (see WidenChangedBodies).
        var sizes = new Dictionary<MethodDefinition, long>();
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody)) sizes[method] = BodySize(method.Body);
        // The platform layer (compat/rewriter/PlatformPasses.cs), before the retargeting: its rules name the game's
        // calls into Steamworks by their own names, and take them out before Steamworks' types go to T2DCompat.
        int platform = IsGameAssembly(module) ? PlatformPatches(module) : 0;
        Count("pass PlatformPatches", platform);
        // The fold-back passes (foldback/), before the retargeting too: they read the Unity 6 names of the engine's and
        // class libraries' internals that IL2CPP inlined, and bring them back to 2018.2's API. They don't touch what
        // the atlas passes match, so those (in Passes, after the retargeting) see the same code.
        string folded = FoldBack(module);
        // Before the retargeting: it fills strippedTypes, whose references the loop leaves alone.
        int stripped = StripAttributes(module) + StripSecurityDeclarations(module);
        int retargeted = 0, shimmedTypes = 0, replacedGameTypes = 0;
        foreach (TypeReference type in module.GetTypeReferences().ToArray())
        {
            string inner;
            if (type.DeclaringType != null && renamedNestedTypes.TryGetValue(type.FullName, out inner))
            {
                type.Name = inner;
                retargeted++;
            }
            bool replaced = IsReplacedGameType(type);
            if (!replaced && (type.DeclaringType != null || !IsExternal(type) || strippedTypes.Contains(type.FullName))) continue;
            string name;
            if (renamedTypes.TryGetValue(type.FullName, out name))
            {
                int dot = name.LastIndexOf('.');
                type.Namespace = name.Substring(0, dot);
                type.Name = name.Substring(dot + 1);
            }
            TypeDefinition definition;
            if (!replaced && target.TryGetValue(type.FullName, out definition))
            {
                if (SetScope(module, type, definition.Module.Assembly.Name)) retargeted++;
            }
            else if (compat.TryGetValue(CompatName(type.FullName), out definition))
            {
                type.Namespace = definition.Namespace;
                type.Name = definition.Name;
                SetScope(module, type, compatModule.Assembly.Name);
                shimmedTypes++;
                if (replaced) replacedGameTypes++;
            }
            else
            {
                List<string> missing;
                if (!missingTypes.TryGetValue(module.Name, out missing)) missingTypes[module.Name] = missing = new List<string>();
                missing.Add(type.FullName);
            }
        }
        if (ReplacedGameNamespaces.Count > 0) Count("rule ReplacedGameNamespaces", replacedGameTypes);
        int attributeTypes = RetargetAttributeTypes(module);
        // Before the passes: references unused after the retargeting are read from the file's own
        // type references, which do not list what a pass imports.
        int removed = RemoveUnusedReferences(module);
        // After it: the reference to the attribute's module may be new.
        MarkApiVersion(assembly);
        // Each group's line of the stats, "  <group>: <pass>=<sites> ...", for groups that changed something.
        var groups = new List<string>();
        var lines = new Dictionary<string, string>();
        var changed = new HashSet<string>();
        foreach (Pass pass in Passes)
        {
            if (pass.Scope == Scope.Game && !IsGameAssembly(module)) continue;
            if (pass.Switch != null && Environment.GetEnvironmentVariable(pass.Switch) != "1") continue;
            int sites = pass.Run(module);
            Count("pass " + pass.Name, sites);
            // The engine-wide passes find nothing to do in a game that has no such sites.
            if (OptionalPasses.Contains(pass.Name)) Count("pass " + pass.Name, 1);
            if (!lines.ContainsKey(pass.Group))
            {
                groups.Add(pass.Group);
                lines[pass.Group] = "\n  " + pass.Group + ":";
            }
            lines[pass.Group] += " " + pass.Name + "=" + sites;
            if (sites > 0) changed.Add(pass.Group);
        }
        int outOfRange;
        int widened = WidenChangedBodies(module, sizes, out outOfRange);
        CheckStacks(module);
        return "platform=" + platform + " retargeted=" + retargeted + " shimTypes=" + shimmedTypes + " attributeTypes=" + attributeTypes +
               " strippedAttributes=" + stripped +
               " widenedBodies=" + widened + " (short branches out of range in " + outOfRange + ")" +
               " removedReferences=" + removed + repaired + folded + string.Concat(groups.Where(changed.Contains).Select(g => lines[g]));
    }

    static void ReportMissingTypes(string moduleName, string written)
    {
        List<string> missing;
        if (!missingTypes.TryGetValue(moduleName, out missing)) return;
        HashSet<string> referenced;
        using (ModuleDefinition module = ModuleDefinition.ReadModule(written))
            referenced = new HashSet<string>(module.GetTypeReferences().Select(t => t.FullName));
        foreach (string type in missing.Where(referenced.Contains)) problems.Add(moduleName + ": type " + type);
    }

    // [assembly: UnityAPICompatibilityVersion("<target>")] (T2D_API_VERSION, set by pipeline.py): the
    // editor's API updater then takes the assembly as up to date and does not open it. Without it,
    // the updater's check failed on five of the rewritten assemblies: it reads attribute arguments
    // and could not resolve the engine modules their enums come from.
    static void MarkApiVersion(AssemblyDefinition assembly)
    {
        string version = Environment.GetEnvironmentVariable("T2D_API_VERSION");
        if (string.IsNullOrEmpty(version)) return;
        ModuleDefinition module = assembly.MainModule;
        MethodDefinition constructor = target["UnityEngine.UnityAPICompatibilityVersionAttribute"].Methods
            .Single(m => m.IsConstructor && m.Parameters.Count == 1);
        var attribute = new CustomAttribute(module.ImportReference(constructor));
        attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, version));
        assembly.CustomAttributes.Add(attribute);
    }

    // Compat's method T2DCompat.<type>.<name> (the only one of that name), imported, and
    // instantiated with the type arguments given.
    static MethodReference CompatMethod(ModuleDefinition module, string type, string name, params TypeReference[] typeArguments)
    {
        MethodReference method = module.ImportReference(compat["T2DCompat." + type].Methods.Single(m => m.Name == name));
        if (typeArguments.Length == 0) return method;
        var instance = new GenericInstanceMethod(method);
        foreach (TypeReference argument in typeArguments) instance.GenericArguments.Add(argument);
        return instance;
    }

    // Instruction count and encoded size of a body.
    static long BodySize(MethodBody body)
    {
        long size = 0;
        foreach (Instruction instruction in body.Instructions) size += instruction.GetSize();
        return ((long)body.Instructions.Count << 32) | size;
    }

    // Cecil writes a short branch whose target ended up out of its range without complaint
    // (the offset wraps; it reads back with no target). After the passes, every body whose
    // instructions changed (inserted, removed or resized), every new body, and any body with
    // a short branch out of range gets the long branch forms.
    static int WidenChangedBodies(ModuleDefinition module, Dictionary<MethodDefinition, long> sizes, out int outOfRange)
    {
        int count = 0;
        outOfRange = 0;
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
            {
                long size;
                bool changed = !sizes.TryGetValue(method, out size) || size != BodySize(method.Body);
                bool wrong = ShortBranchOutOfRange(method.Body);
                if (wrong) outOfRange++;
                if (!changed && !wrong) continue;
                if (!method.Body.Instructions.Any(i => i.OpCode.OperandType == OperandType.ShortInlineBrTarget)) continue;
                WidenBranches(method.Body);
                count++;
            }
        return count;
    }

    // IL2CPP compiles IL without verifying it, and Mono's load check (LoadCheck.cs) is a stage of its
    // own: a pass that leaves a stack unbalanced would show only as a crash or garbage in the player.
    // After the passes, every body (the ones a pass changed, and the others: an operand changed to a
    // method of another shape leaves the size as it was; 43,000 bodies in 0.4 s) is followed with
    // Stack.cs' StackProducers, which stops on a depth that differs where paths meet and on an
    // underflow; a ret must find its value alone on the stack, and no path may run off the body's
    // end. A body that fails is reported (UNRESOLVED, exit 3).
    static void CheckStacks(ModuleDefinition module)
    {
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
            {
                if (method.Body.Instructions.Count == 0)
                {
                    problems.Add(module.Name + ": stack: empty body " + method.FullName);
                    continue;
                }
                Dictionary<Instruction, Instruction[]> before;
                try { before = StackProducers(method, null); }
                catch (InvalidOperationException e)
                {
                    problems.Add(module.Name + ": stack: " + e.Message + " in " + method.FullName);
                    continue;
                }
                int returned = method.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
                foreach (KeyValuePair<Instruction, Instruction[]> reached in before)
                {
                    Instruction at = reached.Key;
                    FlowControl flow = at.OpCode.FlowControl;
                    if (at.OpCode.Code == Code.Ret && reached.Value.Length != returned)
                        problems.Add(module.Name + ": stack: " + reached.Value.Length + " values at " + at + " in " + method.FullName);
                    else if (at.Next == null && flow != FlowControl.Branch && flow != FlowControl.Return && flow != FlowControl.Throw &&
                             at.OpCode.Code != Code.Endfinally && at.OpCode.Code != Code.Endfilter)
                        problems.Add(module.Name + ": stack: runs off the end after " + at + " in " + method.FullName);
                }
            }
    }

    static bool ShortBranchOutOfRange(MethodBody body)
    {
        var offsets = new Dictionary<Instruction, int>();
        int offset = 0;
        foreach (Instruction instruction in body.Instructions)
        {
            offsets[instruction] = offset;
            offset += instruction.GetSize();
        }
        foreach (Instruction instruction in body.Instructions)
        {
            if (instruction.OpCode.OperandType != OperandType.ShortInlineBrTarget) continue;
            var target = instruction.Operand as Instruction;
            int at;
            if (target == null || !offsets.TryGetValue(target, out at)) return true;
            int delta = at - (offsets[instruction] + instruction.GetSize());
            if (delta < sbyte.MinValue || delta > sbyte.MaxValue) return true;
        }
        return false;
    }
}
