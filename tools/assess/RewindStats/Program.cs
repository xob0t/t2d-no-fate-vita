// Measures Rewind's output for the port assessment (docs/assessment.md).
//
//   stats  <assemblies dir> <tsv out> [assembly ...]   complete / gaps / placeholder per assembly, gap reasons
//   load   <assemblies dir> <assembly>                 read every body eagerly and resolve every reference
//   roundtrip <assemblies dir> <assembly> <out dir>     write the module back with Cecil and re-read it
//   il <assemblies dir> <assembly> <filter>             print the IL of methods whose Type::Name contains <filter>
//   xcheck <assemblies dir> <assembly> <2018 Data dir> [engine dir]
//                                                      apigap's cross-check: resolve every TypeRef/MemberRef row
//                                                      with Cecil's own resolver against the target assemblies
//   members <dll> <type full name> [name filter]        list a type's members with access and signature (hand checks)
//   compare <a.dll> <b.dll>                             structural diff of two builds of a module (types, members,
//                                                      attributes, bodies with operands by name), not bytes
//   apigap <assemblies dir> <assembly> <2018 Data dir> <tsv out> [engine dir]
//                                                      references that Unity 2018.2's engine and unityaot
//                                                      class libraries don't have, or don't make public
//                                                      (engine dir: default Data/Managed/UnityEngine; e.g. a
//                                                      player's PlaybackEngines/<player>/Managed)
//
// The assemblies dir is Rewind's `assemblies/` output. Nothing here writes next to its inputs.

using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2) return Usage();
        return args[0] switch
        {
            "stats" when args.Length >= 3 => Stats(args[1], args[2], args.Skip(3).ToArray()),
            "load" when args.Length == 3 => Load(args[1], args[2]),
            "apigap" when args.Length is 5 or 6 => ApiGap(args[1], args[2], args[3], args[4], args.Length == 6 ? args[5] : null),
            "roundtrip" when args.Length == 4 => RoundTrip(args[1], args[2], args[3]),
            "compare" when args.Length == 3 => Compare(args[1], args[2]),
            "il" when args.Length == 4 => DumpIl(args[1], args[2], args[3]),
            "xcheck" when args.Length is 4 or 5 => XCheck(args[1], args[2], args[3], args.Length == 5 ? args[4] : null),
            "members" when args.Length >= 3 => Members(args[1], args[2], args.Skip(3).ToArray()),
            _ => Usage(),
        };
    }

    static int Usage()
    {
        Console.Error.WriteLine("usage: stats <dir> <tsv out> [asm...] | load <dir> <asm> | roundtrip <dir> <asm> <out dir> | apigap <dir> <asm> <2018 Editor/Data> <tsv out> [engine dir] | il <dir> <asm> <filter> | compare <a.dll> <b.dll>");
        return 2;
    }

    static ModuleDefinition Open(string dir, string name, bool immediate = false)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(dir);
        return ModuleDefinition.ReadModule(Path.Combine(dir, name + ".dll"), new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadingMode = immediate ? ReadingMode.Immediate : ReadingMode.Deferred,
        });
    }

    static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition m)
    {
        foreach (var t in m.Types)
            foreach (var n in Walk(t)) yield return n;
        static IEnumerable<TypeDefinition> Walk(TypeDefinition t)
        {
            yield return t;
            foreach (var n in t.NestedTypes)
                foreach (var x in Walk(n)) yield return x;
        }
    }

    // ---- stats ---------------------------------------------------------------------------------------------------

    enum Kind { Complete, Gaps, Placeholder }

    static readonly Regex NoteHead = new(@"^\d+ part\(s\) of this method were not recovered: ", RegexOptions.Compiled);
    static readonly Regex SsaVar = new(@"%\d+", RegexOptions.Compiled);
    static readonly Regex Addr = new(@"0x[0-9A-Fa-f]{6,}", RegexOptions.Compiled);

    static int Stats(string dir, string tsvOut, string[] only)
    {
        var names = only.Length > 0
            ? only
            : Directory.GetFiles(dir, "*.dll").Select(Path.GetFileNameWithoutExtension).ToArray()!;
        using var tsv = new StreamWriter(tsvOut, false, new UTF8Encoding(false));
        tsv.WriteLine("assembly\tmethod\tkind\tgeneric\tcompiler_generated\til_size\tmarker_sites\treasons");
        var whole = new int[3];
        foreach (var name in names.OrderBy(n => n, StringComparer.Ordinal))
        {
            using var m = Open(dir, name!);
            var counts = new int[3];
            var generic = new int[3];
            var reasonMethods = new Dictionary<string, int>();
            var nativeFns = new Dictionary<string, int>();
            var bucketAny = new Dictionary<string, int>();
            var bucketCombo = new Dictionary<string, int>();
            var sizes = new List<(int sites, string method)>();
            int sites = 0, notedComplete = 0;
            foreach (var t in AllTypes(m))
            {
                if (t.Name is "__Rewind" or "<Module>") continue;
                foreach (var md in t.Methods)
                {
                    if (!md.HasBody) continue;
                    var ins = md.Body.Instructions;
                    Kind k;
                    int s = 0;
                    string? note = null;
                    if (ins.Count == 2 && ins[0].OpCode == OpCodes.Ldnull && ins[1].OpCode == OpCodes.Throw) k = Kind.Placeholder;
                    else
                    {
                        for (int i = 0; i < ins.Count; i++)
                        {
                            if (ins[i].Operand is MethodReference mr && mr.DeclaringType.Name == "__Rewind")
                            {
                                if (mr.Name is "Opaque" or "Effect") s++;
                                else if (mr.Name == "Note" && i > 0 && ins[i - 1].Operand is string str) note = str;
                            }
                        }
                        k = s > 0 ? Kind.Gaps : Kind.Complete;
                    }
                    bool isGeneric = md.HasGenericParameters || IsInGenericType(t);
                    bool cg = IsCompilerGenerated(t) || md.Name.Contains('<');
                    counts[(int)k]++;
                    whole[(int)k]++;
                    if (isGeneric) generic[(int)k]++;
                    sites += s;
                    var reasons = new List<string>();
                    if (k == Kind.Gaps && note != null)
                    {
                        foreach (var r in NoteHead.Replace(note, "").Split("; ", StringSplitOptions.RemoveEmptyEntries))
                        {
                            foreach (Match a in Addr.Matches(r))
                                if (r.StartsWith("call to unidentified native function"))
                                    nativeFns[a.Value] = nativeFns.GetValueOrDefault(a.Value) + 1;
                            reasons.Add(Normalize(r));
                        }
                        foreach (var r in reasons.Distinct())
                            reasonMethods[r] = reasonMethods.GetValueOrDefault(r) + 1;
                        var bs = reasons.Select(Bucket).Distinct().OrderBy(b => b, StringComparer.Ordinal).ToList();
                        foreach (var b in bs) bucketAny[b] = bucketAny.GetValueOrDefault(b) + 1;
                        string combo = string.Join("+", bs);
                        bucketCombo[combo] = bucketCombo.GetValueOrDefault(combo) + 1;
                    }
                    else if (k == Kind.Gaps)
                    {
                        reasonMethods["(no note)"] = reasonMethods.GetValueOrDefault("(no note)") + 1;
                    }
                    if (k == Kind.Gaps) sizes.Add((s, $"{t.FullName}::{md.Name}"));
                    // A note with no Opaque/Effect site: Rewind says parts weren't recovered but marked none of them.
                    if (k == Kind.Complete && note != null) notedComplete++;
                    tsv.WriteLine(string.Join('\t', name, $"{t.FullName}::{md.Name}({string.Join(",", md.Parameters.Select(p => p.ParameterType.Name))})",
                        k, isGeneric ? 1 : 0, cg ? 1 : 0, md.Body.CodeSize, s, string.Join(" | ", reasons.Distinct()), note != null ? 1 : 0));
                }
            }
            int total = counts.Sum();
            if (total == 0) continue;
            bool detail = only.Length > 0;
            Console.WriteLine($"{name}: {total} bodies: {counts[0]} complete ({Pct(counts[0], total)}), {counts[1]} with gaps ({Pct(counts[1], total)}), {counts[2]} placeholder" +
                              $"  [generic: {generic[0]}/{generic[1]}/{generic[2]}; marker sites {sites}; complete bodies with a Note but no marker: {notedComplete}]");
            if (!detail) continue;
            Console.WriteLine("  gap reasons, by number of methods that have each (a method can have several):");
            foreach (var (r, c) in reasonMethods.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
                Console.WriteLine($"    {c,6}  {r}");
            Console.WriteLine("  causes (runtime / double / layout / other), methods with each:");
            foreach (var (b, c) in bucketAny.OrderByDescending(p => p.Value))
                Console.WriteLine($"    {c,6}  {b}");
            Console.WriteLine("  methods by exact set of causes:");
            foreach (var (b, c) in bucketCombo.OrderByDescending(p => p.Value))
                Console.WriteLine($"    {c,6}  {b}");
            sizes.Sort((a, b) => b.sites.CompareTo(a.sites));
            int all = sizes.Sum(x => x.sites);
            Console.WriteLine($"  marker sites in gap methods: {all}; largest methods:");
            foreach (var (st, mname) in sizes.Take(12))
                Console.WriteLine($"    {st,6}  {mname}");
            foreach (int cut in new[] { 1, 2, 5, 10, 50 })
                Console.WriteLine($"    gap methods with <= {cut} sites: {sizes.Count(x => x.sites <= cut)}");
            Console.WriteLine("  unidentified native functions, by number of methods calling them:");
            foreach (var (a, c) in nativeFns.OrderByDescending(p => p.Value).Take(25))
                Console.WriteLine($"    {c,6}  {a}");
        }
        int wt = whole.Sum();
        Console.WriteLine($"ALL: {wt} bodies: {whole[0]} complete ({Pct(whole[0], wt)}), {whole[1]} with gaps, {whole[2]} placeholder");
        return 0;
    }

    static string Normalize(string r)
    {
        r = SsaVar.Replace(r, "%n");
        r = Addr.Replace(r, "0xADDR");
        r = Regex.Replace(r, @"offset 0x[0-9A-F]+", "offset 0xN");
        r = Regex.Replace(r, @"spans fields of \S+", "spans fields of <type>");
        r = Regex.Replace(r, @"virtual slot \d+", "virtual slot N");
        return r;
    }

    // Coarse causes, for sizing the hand work. See docs/assessment.md.
    //   runtime: IL2CPP runtime code the signature library didn't name, or that Master's link-time
    //            optimization inlined into the method (metadata init, write barriers, array allocation)
    //   double:  a double moved through an integer register; the stripped mscorlib has no BitConverter
    //   layout:  a memory access Rewind couldn't map to a field or element
    //   other:   indirect calls, lost arguments, virtual slots, delegates, exceptions ...
    static string Bucket(string r)
    {
        if (r.StartsWith("bits reinterpreted as")) return "double";
        if (r.StartsWith("call to unidentified native function") || r.StartsWith("intrinsic atomic")
            || r.Contains("absolute address") || r.Contains("value used as data") || r.StartsWith("runtime helper")
            || r.Contains("address without a typed base")) return "runtime";
        if (r.Contains("no field at offset") || r.Contains("spans fields") || r.Contains("indexed access")
            || r.Contains("several index terms") || r.Contains("memory access through")) return "layout";
        return "other";
    }

    static string Pct(int a, int b) => b == 0 ? "-" : $"{100.0 * a / b:F1}%";

    static bool IsInGenericType(TypeDefinition t)
    {
        for (var x = t; x != null; x = x.DeclaringType)
            if (x.HasGenericParameters) return true;
        return false;
    }

    static bool IsCompilerGenerated(TypeDefinition t)
    {
        for (var x = t; x != null; x = x.DeclaringType)
            if (x.Name.Contains('<')) return true;
        return false;
    }

    // ---- load ----------------------------------------------------------------------------------------------------

    static int Load(string dir, string name)
    {
        using var m = Open(dir, name, immediate: true);
        int types = 0, methods = 0, bodies = 0, instrs = 0, unresolved = 0, refs = 0, readErrors = 0;
        var bad = new List<string>();
        void Bad(string s) { unresolved++; if (bad.Count < 40) bad.Add(s); }
        foreach (var t in AllTypes(m))
        {
            types++;
            if (!ResolvesType(t.BaseType)) Bad($"{t.FullName}: base {t.BaseType}");
            foreach (var i in t.Interfaces) if (!ResolvesType(i.InterfaceType)) Bad($"{t.FullName}: interface {i.InterfaceType}");
            foreach (var f in t.Fields) if (!ResolvesType(f.FieldType)) Bad($"{f.FullName}: field type");
            foreach (var md in t.Methods)
            {
                methods++;
                if (!ResolvesType(md.ReturnType) || md.Parameters.Any(p => !ResolvesType(p.ParameterType))) Bad($"{md.FullName}: signature");
                foreach (var ov in md.Overrides) if (!ResolvesMember(ov)) Bad($"{md.FullName}: override {ov}");
                if (!md.HasBody) continue;
                Mono.Cecil.Cil.MethodBody body;
                try { body = md.Body; bodies++; }
                catch (Exception e) { readErrors++; if (bad.Count < 40) bad.Add($"{md.FullName}: body read error {e.GetType().Name}: {e.Message}"); continue; }
                foreach (var v in body.Variables) if (!ResolvesType(v.VariableType)) Bad($"{md.FullName}: local {v.VariableType}");
                foreach (var i in body.Instructions)
                {
                    instrs++;
                    bool ok;
                    switch (i.Operand)
                    {
                        case MemberReference mr when mr is MethodReference or FieldReference: refs++; ok = ResolvesMember(mr); break;
                        case TypeReference tr: refs++; ok = ResolvesType(tr); break;
                        case CallSite cs: refs++; ok = ResolvesType(cs.ReturnType) && cs.Parameters.All(p => ResolvesType(p.ParameterType)); break;
                        default: continue;
                    }
                    if (!ok) Bad($"{md.FullName}: {i.OpCode} {i.Operand}");
                }
            }
        }
        foreach (var tr in m.GetTypeReferences())
            if (!ResolvesType(tr)) Bad($"typeref {tr.FullName} [{tr.Scope}]");
        foreach (var mr in m.GetMemberReferences())
            if (!ResolvesMember(mr)) Bad($"memberref {mr.FullName}");
        Console.WriteLine($"{name}: runtime {m.RuntimeVersion}, target {m.Assembly.Name}; {types} types, {methods} methods, {bodies} bodies, {instrs} instructions read; " +
                          $"{refs} operand references; {m.GetTypeReferences().Count()} typerefs, {m.GetMemberReferences().Count()} memberrefs; {unresolved} unresolved, {readErrors} body read errors");
        Console.WriteLine("  assembly references: " + string.Join(", ", m.AssemblyReferences.Select(a => $"{a.Name} {a.Version} pk={Convert.ToHexString(a.PublicKeyToken ?? [])}")));
        var dups = m.AssemblyReferences.GroupBy(a => a.Name).Where(g => g.Count() > 1).ToList();
        foreach (var g in dups)
        {
            var users = g.Select(a => m.GetTypeReferences().Count(tr => tr.Scope == a)).ToList();
            Console.WriteLine($"  duplicate AssemblyRef rows for {g.Key}: {g.Count()} (typerefs scoped to each: {string.Join("/", users)})");
        }
        foreach (var b in bad) Console.WriteLine("  unresolved: " + b);
        return unresolved == 0 && readErrors == 0 ? 0 : 1;
    }

    // Resolve a type the way a loader would: generic parameters resolve against their owner, type
    // specifications (arrays, by-refs, pointers, modifiers, generic instances) resolve through their parts.
    static bool ResolvesType(TypeReference? t)
    {
        switch (t)
        {
            case null: return true;
            case GenericParameter: return true;
            case GenericInstanceType git: return ResolvesType(git.ElementType) && git.GenericArguments.All(ResolvesType);
            case RequiredModifierType rm: return ResolvesType(rm.ElementType) && ResolvesType(rm.ModifierType);
            case OptionalModifierType om: return ResolvesType(om.ElementType) && ResolvesType(om.ModifierType);
            case FunctionPointerType fp: return ResolvesType(fp.ReturnType) && fp.Parameters.All(p => ResolvesType(p.ParameterType));
            case TypeSpecification ts: return ResolvesType(ts.ElementType);
        }
        return Try(() => t.Resolve()) != null;
    }

    static bool ResolvesMember(MemberReference mr)
    {
        var decl = mr.DeclaringType;
        if (!ResolvesType(decl)) return false;
        if (mr is GenericInstanceMethod gim && !gim.GenericArguments.All(ResolvesType)) return false;
        // Multi-dimensional array accessors (Get/Set/Address/.ctor) are runtime-provided: no definition to find.
        if (decl is ArrayType) return true;
        return mr switch
        {
            MethodReference m => Try(() => m.Resolve()) != null,
            FieldReference f => Try(() => f.Resolve()) != null,
            _ => true,
        };
    }

    // Write the module back with Cecil, read the copy and compare counts.
    static int RoundTrip(string dir, string name, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, name + ".dll");
        int a, b;
        using (var m = Open(dir, name, immediate: true))
        {
            a = Count(m);
            try { m.Write(outPath); }
            catch (Exception e) { Console.WriteLine($"{name}: write failed: {e}"); return 1; }
        }
        using (var m2 = ModuleDefinition.ReadModule(outPath, new ReaderParameters { AssemblyResolver = Resolver(dir), ReadingMode = ReadingMode.Immediate }))
        {
            b = Count(m2);
            Console.WriteLine($"{name}: wrote {outPath} ({new FileInfo(outPath).Length} bytes, input {new FileInfo(Path.Combine(dir, name + ".dll")).Length}); instructions before {a}, after {b}; " +
                              $"assembly references after: {m2.AssemblyReferences.Count}");
        }
        return a == b ? 0 : 1;
        static int Count(ModuleDefinition m) => AllTypes(m).SelectMany(t => t.Methods).Where(x => x.HasBody).Sum(x => x.Body.Instructions.Count);
    }

    static int Compare(string a, string b)
    {
        var da = Describe(a);
        var db = Describe(b);
        int diffs = 0;
        for (int i = 0; i < Math.Max(da.Count, db.Count); i++)
        {
            string? x = i < da.Count ? da[i] : null, y = i < db.Count ? db[i] : null;
            if (x == y) continue;
            if (++diffs <= 10) Console.WriteLine($"  line {i}:\n    a: {x}\n    b: {y}");
        }
        Console.WriteLine($"{Path.GetFileName(a)}: {da.Count} vs {db.Count} description lines, {diffs} differ");
        return diffs == 0 ? 0 : 1;

        static List<string> Describe(string path)
        {
            using var m = ModuleDefinition.ReadModule(path, new ReaderParameters { ReadingMode = ReadingMode.Immediate });
            var o = new List<string>
            {
                $"module {m.Name} {m.Assembly.FullName} {m.RuntimeVersion} {m.Kind} {m.Architecture} {m.Attributes}",
                "refs " + string.Join(", ", m.AssemblyReferences.Select(r => r.FullName)),
                "resources " + string.Join(", ", m.Resources.Select(r => r.Name)),
            };
            o.Add("assembly attrs" + Attrs(m.Assembly.CustomAttributes) + " module attrs" + Attrs(m.CustomAttributes));
            foreach (var t in AllTypes(m))
            {
                o.Add($"type {t.FullName} {t.Attributes} : {t.BaseType?.FullName} pack={t.PackingSize} size={t.ClassSize} gp={string.Join(",", t.GenericParameters.Select(g => g.Name + ":" + string.Join("+", g.Constraints.Select(c => c.ConstraintType.FullName))))}");
                o.AddRange(t.Interfaces.Select(i => "  impl " + i.InterfaceType.FullName));
                o.Add("  attrs" + Attrs(t.CustomAttributes));
                foreach (var f in t.Fields)
                    o.Add($"  field {f.FullName} {f.Attributes} off={f.Offset} const={f.Constant} rva={(f.InitialValue is { Length: > 0 } iv ? Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(iv)) : "")}" +
                          Attrs(f.CustomAttributes));
                foreach (var p in t.Properties) o.Add($"  prop {p.FullName} get={p.GetMethod?.Name} set={p.SetMethod?.Name}" + Attrs(p.CustomAttributes));
                foreach (var e in t.Events) o.Add($"  event {e.FullName} add={e.AddMethod?.Name} remove={e.RemoveMethod?.Name}");
                foreach (var md in t.Methods)
                {
                    o.Add($"  method {md.FullName} {md.Attributes} {md.ImplAttributes} gp={md.GenericParameters.Count} pinv={md.PInvokeInfo?.EntryPoint}/{md.PInvokeInfo?.Module?.Name}" +
                          Attrs(md.CustomAttributes) +
                          string.Concat(md.Parameters.Select(p => $" [{p.Name} {p.Attributes} {p.Constant}]")) +
                          string.Concat(md.Overrides.Select(x => " override " + x.FullName)));
                    if (!md.HasBody) continue;
                    var body = md.Body;
                    o.Add($"    maxstack={body.MaxStackSize} init={body.InitLocals} locals={string.Join(",", body.Variables.Select(v => v.VariableType.FullName))}");
                    foreach (var h in body.ExceptionHandlers)
                        o.Add($"    eh {h.HandlerType} {h.TryStart?.Offset}-{h.TryEnd?.Offset} {h.HandlerStart?.Offset}-{h.HandlerEnd?.Offset} {h.FilterStart?.Offset} {h.CatchType?.FullName}");
                    foreach (var i in body.Instructions)
                        o.Add($"    {i.Offset:X4} {i.OpCode} {Operand(i.Operand)}");
                }
            }
            return o;
        }

        // Attribute order on one owner carries no meaning (writers sort the table only by owner).
        static string Attrs(IEnumerable<CustomAttribute> cs) => string.Concat(cs.Select(Attr).OrderBy(s => s, StringComparer.Ordinal).Select(s => " " + s));

        static string Attr(CustomAttribute c) =>
            $"[{c.Constructor.FullName}({string.Join(",", c.ConstructorArguments.Select(a => a.Value?.ToString()))}){string.Join(",", c.Properties.Select(p => p.Name + "=" + p.Argument.Value)) + string.Join(",", c.Fields.Select(p => p.Name + "=" + p.Argument.Value))}]";

        static string Operand(object? op) => op switch
        {
            null => "",
            Instruction t => "->" + t.Offset.ToString("X4"),
            Instruction[] ts => string.Join(",", ts.Select(t => t.Offset.ToString("X4"))),
            MemberReference mr => mr.FullName + " @" + (mr.DeclaringType != null ? ScopeName(mr.DeclaringType) : mr is TypeReference tr ? ScopeName(tr) : ""),
            VariableDefinition v => "V" + v.Index,
            ParameterDefinition p => "A" + p.Index,
            CallSite cs => cs.FullName,
            _ => op.ToString() ?? "",
        };
    }

    // Print the IL of the methods whose "Type::Name" contains <filter> (a substring match).
    static int DumpIl(string dir, string name, string filter)
    {
        using var m = Open(dir, name);
        foreach (var t in AllTypes(m))
            foreach (var md in t.Methods.Where(x => x.HasBody && $"{t.FullName}::{x.Name}".Contains(filter, StringComparison.Ordinal)))
            {
                Console.WriteLine($"{md.FullName}  maxstack {md.Body.MaxStackSize}, locals {string.Join(", ", md.Body.Variables.Select(v => $"V{v.Index}:{v.VariableType}"))}");
                foreach (var h in md.Body.ExceptionHandlers)
                    Console.WriteLine($"  .{h.HandlerType} try {h.TryStart?.Offset:X4}-{h.TryEnd?.Offset:X4} handler {h.HandlerStart?.Offset:X4}-{h.HandlerEnd?.Offset:X4} {h.CatchType}");
                foreach (var i in md.Body.Instructions) Console.WriteLine("  " + i);
            }
        return 0;
    }

    // Cecil's resolver, given the target's assemblies for platform names and the assemblies dir for the rest.
    sealed class TargetResolver(string dir, string[] targetDirs) : IAssemblyResolver
    {
        readonly Dictionary<string, AssemblyDefinition?> cache = new(StringComparer.OrdinalIgnoreCase);
        public AssemblyDefinition? Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());
        public AssemblyDefinition? Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            if (cache.TryGetValue(name.Name, out var a)) return a;
            var dirs = IsPlatformAssembly(name.Name) ? targetDirs : [dir];
            var file = dirs.Select(d => Path.Combine(d, name.Name + ".dll")).FirstOrDefault(File.Exists);
            a = file == null ? null : AssemblyDefinition.ReadAssembly(file, new ReaderParameters { AssemblyResolver = this });
            return cache[name.Name] = a;
        }
        public void Dispose() { }
    }

    static int XCheck(string dir, string name, string data, string? engineDir)
    {
        var aot = Path.Combine(data, "MonoBleedingEdge", "lib", "mono", "unityaot");
        var resolver = new TargetResolver(dir, [engineDir ?? Path.Combine(data, "Managed", "UnityEngine"), aot, Path.Combine(aot, "Facades")]);
        using var m = ModuleDefinition.ReadModule(Path.Combine(dir, name + ".dll"), new ReaderParameters { AssemblyResolver = resolver });
        var types = new SortedSet<string>(StringComparer.Ordinal);
        var members = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var tr in m.GetTypeReferences())
        {
            var asm = ScopeName(tr);
            if (asm != null && IsPlatformAssembly(asm) && Try(() => tr.Resolve()) == null) types.Add($"{asm}	{tr.FullName}");
        }
        foreach (var mr in m.GetMemberReferences())
        {
            var decl = mr.DeclaringType is GenericInstanceType g ? g.ElementType : mr.DeclaringType;
            if (decl is ArrayType || ResolvesNull(decl)) continue; // its type is the missing thing
            if (Try(() => mr.Resolve()) == null) members.Add($"{ScopeName(decl)}	{MemberKey(mr, decl)}");
        }
        foreach (var t in types) Console.WriteLine("type	" + t);
        foreach (var x in members) Console.WriteLine("member	" + x);
        Console.Error.WriteLine($"{name}: Cecil can't resolve {types.Count} platform typerefs and {members.Count} memberrefs against the target");
        return 0;
        static bool ResolvesNull(TypeReference t) => Try(() => t.Resolve()) == null;
    }

    static int Members(string dll, string type, string[] filter)
    {
        using var m = ModuleDefinition.ReadModule(dll);
        var t = AllTypes(m).FirstOrDefault(x => x.FullName == type);
        if (t == null) { Console.WriteLine($"{type}: not defined in {m.Assembly.Name.Name}" + (m.ExportedTypes.Any(e => e.FullName == type) ? " (forwarded)" : "")); return 1; }
        Console.WriteLine($"{t.FullName} in {m.Assembly.Name.Name}: {t.Attributes} : {t.BaseType}");
        bool Want(string n) => filter.Length == 0 || filter.Any(f => n.Contains(f, StringComparison.Ordinal));
        foreach (var f in t.Fields.Where(f => Want(f.Name))) Console.WriteLine($"  field  {f.Attributes & FieldAttributes.FieldAccessMask,-12} {Sig(f.FieldType)} {f.Name}");
        foreach (var md in t.Methods.Where(x => Want(x.Name)))
            Console.WriteLine($"  method {md.Attributes & MethodAttributes.MemberAccessMask,-12} {(md.IsStatic ? "static " : "")}{Sig(md.ReturnType)} {md.Name}" +
                              $"{(md.HasGenericParameters ? $"<{md.GenericParameters.Count}>" : "")}({string.Join(", ", md.Parameters.Select(p => Sig(p.ParameterType)))})");
        return 0;
    }

    static DefaultAssemblyResolver Resolver(string dir)
    {
        var r = new DefaultAssemblyResolver();
        r.AddSearchDirectory(dir);
        return r;
    }

    static object? Try(Func<object?> f)
    {
        try { return f(); } catch { return null; }
    }

    // ---- apigap --------------------------------------------------------------------------------------------------
    //
    // Every reference the assembly makes to the platform (the BCL and UnityEngine), checked against the target's:
    // the 2018.2 engine modules (or another engine dir, e.g. a player's Managed folder) and the unityaot class
    // libraries. Game code (any other assembly: the game's, plugins, packages) is ported along and not checked.
    // Kinds:
    //   type-missing      no type of that full name in the target
    //   type-nonpublic    the target has it, but not visible to user code
    //   type-moved        the target has it in another assembly (a retarget; informational)
    //   member-missing    the target type (with its bases and interfaces) has no member of that name and signature
    //   member-nonpublic  it has the member, but not visible to user code
    // The unity6 column says whether the reference is visible in the game's own (Unity 6) libraries: a non-public
    // one is code IL2CPP inlined into the game, not an API the game's source called.

    static bool IsPlatformAssembly(string name) =>
        name is "mscorlib" or "netstandard" or "System" or "UnityEngine" || name.StartsWith("System.", StringComparison.Ordinal)
        || name.StartsWith("Mono.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal)
        || (name.StartsWith("UnityEngine.", StringComparison.Ordinal) && name.EndsWith("Module", StringComparison.Ordinal));

    sealed class Target
    {
        public readonly Dictionary<string, List<TypeDefinition>> Types = new(StringComparer.Ordinal);

        /// The definition a reference to <paramref name="fullName"/> from <paramref name="asm"/> would bind to after a
        /// retarget: the same assembly's if it has one, else a visible one, else any.
        public TypeDefinition? Find(string fullName, string? asm = null)
        {
            if (!Types.TryGetValue(fullName, out var list)) return null;
            return list.FirstOrDefault(t => t.Module.Assembly.Name.Name == asm)
                   ?? list.FirstOrDefault(t => IsVisible(t))
                   ?? list[0];
        }
    }

    static Target LoadTarget(string data, string? engineDir)
    {
        var files = new List<string>();
        var engine = engineDir ?? Path.Combine(data, "Managed", "UnityEngine");
        files.AddRange(Directory.GetFiles(engine, "UnityEngine*.dll"));
        var aot = Path.Combine(data, "MonoBleedingEdge", "lib", "mono", "unityaot");
        files.AddRange(Directory.GetFiles(aot, "*.dll"));
        var facades = Path.Combine(aot, "Facades");
        if (Directory.Exists(facades)) files.AddRange(Directory.GetFiles(facades, "*.dll"));
        var target = new Target();
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(aot);
        resolver.AddSearchDirectory(engine);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            ModuleDefinition m;
            try { m = ModuleDefinition.ReadModule(f, new ReaderParameters { AssemblyResolver = resolver }); }
            catch (BadImageFormatException) { continue; } // native
            if (!seen.Add(m.Assembly.Name.Name)) continue;
            // Facades (UnityEngine.dll, netstandard.dll, the unityaot Facades) only forward; the definitions they
            // forward to are indexed from their own assemblies.
            foreach (var t in AllTypes(m))
            {
                if (t.Name == "<Module>") continue;
                if (!target.Types.TryGetValue(t.FullName, out var list)) target.Types[t.FullName] = list = [];
                list.Add(t);
            }
        }
        return target;
    }

    // Visible to user code in another assembly. <bases>: full names of the referencing type's base types (and
    // those of the types it is nested in); protected members and nested types are visible only through them.
    static bool IsVisible(TypeDefinition t, ISet<string>? bases = null)
    {
        for (var x = t; x != null; x = x.DeclaringType)
        {
            if (x.DeclaringType == null) return x.IsPublic;
            if (x.IsNestedPublic) continue;
            if ((x.IsNestedFamily || x.IsNestedFamilyOrAssembly) && bases != null && bases.Contains(x.DeclaringType.FullName)) continue;
            return false;
        }
        return true;
    }

    static bool IsVisible(IMemberDefinition m, ISet<string>? bases = null)
    {
        if (m is TypeDefinition td) return IsVisible(td, bases);
        var (pub, fam) = m switch
        {
            MethodDefinition md => (md.IsPublic, md.IsFamily || md.IsFamilyOrAssembly),
            FieldDefinition fd => (fd.IsPublic, fd.IsFamily || fd.IsFamilyOrAssembly),
            _ => (true, false),
        };
        if (!pub && !(fam && bases != null && bases.Contains(m.DeclaringType.FullName))) return false;
        return IsVisible(m.DeclaringType, bases);
    }

    static HashSet<string> BaseNames(TypeDefinition t)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var outer = t; outer != null; outer = outer.DeclaringType)
            for (TypeReference? b = outer.BaseType; b != null;)
            {
                var cur = b;
                var bd = Try(() => cur.Resolve()) as TypeDefinition;
                var e = cur is GenericInstanceType g ? g.ElementType : cur;
                set.Add(bd?.FullName ?? e.FullName);
                b = bd?.BaseType;
            }
        return set;
    }

    sealed class Gap(string kind, string asm6, string api)
    {
        public readonly string Kind = kind, Asm6 = asm6, Api = api;
        public string TargetAsm = "", Why = "", Unity6 = "", TargetHas = "";
        public readonly HashSet<string> Users = new(StringComparer.Ordinal);
        public int Sites;
    }

    static int ApiGap(string dir, string name, string data, string tsvOut, string? engineDir)
    {
        var target = LoadTarget(data, engineDir);
        using var m = Open(dir, name);
        var gaps = new Dictionary<string, Gap>(StringComparer.Ordinal);
        var typeCache = new Dictionary<string, TypeDefinition?>(StringComparer.Ordinal);
        var memberCache = new Dictionary<string, (IMemberDefinition? found, string why, string has)>(StringComparer.Ordinal);
        ISet<string> bases = new HashSet<string>(); // of the game type being walked

        Gap Add(string kind, string asm6, string api, string user)
        {
            if (!gaps.TryGetValue(kind + "\t" + api, out var g)) gaps[kind + "\t" + api] = g = new Gap(kind, asm6, api);
            g.Users.Add(user);
            g.Sites++;
            return g;
        }

        void CheckType(TypeReference? tr, string user)
        {
            switch (tr)
            {
                case null or GenericParameter: return;
                case GenericInstanceType git:
                    CheckType(git.ElementType, user);
                    foreach (var a in git.GenericArguments) CheckType(a, user);
                    return;
                case RequiredModifierType rm: CheckType(rm.ElementType, user); CheckType(rm.ModifierType, user); return;
                case OptionalModifierType om: CheckType(om.ElementType, user); CheckType(om.ModifierType, user); return;
                case FunctionPointerType fp:
                    CheckType(fp.ReturnType, user);
                    foreach (var p in fp.Parameters) CheckType(p.ParameterType, user);
                    return;
                case TypeSpecification ts: CheckType(ts.ElementType, user); return;
            }
            string? asm = ScopeName(tr);
            if (asm == null || !IsPlatformAssembly(asm)) return;
            if (!typeCache.TryGetValue(tr.FullName + "\t" + asm, out var def))
                typeCache[tr.FullName + "\t" + asm] = def = target.Find(tr.FullName, asm);
            string? kind = def == null ? "type-missing"
                : !IsVisible(def, bases) ? "type-nonpublic"
                : def.Module.Assembly.Name.Name != asm ? "type-moved"
                : null;
            if (kind == null) return;
            var g = Add(kind, asm, tr.FullName, user);
            if (g.Sites == 1)
            {
                g.TargetAsm = def?.Module.Assembly.Name.Name ?? "";
                var u6 = Try(() => tr.Resolve()) as TypeDefinition;
                g.Unity6 = u6 == null ? "unresolved" : IsVisible(u6, bases) ? "public" : "nonpublic";
            }
        }

        void CheckMember(MemberReference mr, string user)
        {
            var decl = mr.DeclaringType;
            CheckType(decl, user);
            if (mr is MethodReference mref)
            {
                CheckType(mref.ReturnType, user);
                foreach (var p in mref.Parameters) CheckType(p.ParameterType, user);
                if (mref is GenericInstanceMethod gim) foreach (var a in gim.GenericArguments) CheckType(a, user);
            }
            else if (mr is FieldReference fref) CheckType(fref.FieldType, user);

            if (decl is ArrayType) return; // runtime-provided accessors of multi-dimensional arrays
            var elem = decl is GenericInstanceType g ? g.ElementType : decl;
            string? asm = ScopeName(elem);
            if (asm == null) return;
            IMemberDefinition? u6 = null;
            if (!IsPlatformAssembly(asm))
            {
                // A reference through a game type to a member it inherits from a platform type.
                if (mr is IMemberDefinition) return;
                u6 = Try(() => mr.Resolve()) as IMemberDefinition;
                if (u6?.DeclaringType == null || !IsPlatformAssembly(u6.DeclaringType.Module.Assembly.Name.Name)) return;
                elem = u6.DeclaringType;
                asm = elem.Module.Assembly.Name.Name;
            }
            var oldType = target.Find(elem.FullName, asm);
            if (oldType == null) return; // counted as a missing type
            string key = MemberKey(mr, elem);
            if (!memberCache.TryGetValue(key, out var r))
            {
                var found = FindMember(oldType, mr, target);
                memberCache[key] = r = found == null ? (null, Why(oldType, mr, target), Overloads(oldType, mr, target)) : (found, Access(found), "");
            }
            string? kind = r.found == null ? "member-missing" : !IsVisible(r.found, bases) ? "member-nonpublic" : null;
            if (kind == null) return;
            var gap = Add(kind, asm, key, user);
            if (gap.Sites == 1)
            {
                gap.Why = r.why;
                gap.TargetHas = r.has;
                gap.TargetAsm = oldType.Module.Assembly.Name.Name;
                u6 ??= Try(() => mr.Resolve()) as IMemberDefinition;
                gap.Unity6 = u6 == null ? "unresolved" : IsVisible(u6, bases) ? "public" : "nonpublic";
            }
        }

        void CheckAttributes(ICustomAttributeProvider p, string user)
        {
            if (!p.HasCustomAttributes) return;
            foreach (var ca in p.CustomAttributes)
            {
                CheckMember(ca.Constructor, user);
                foreach (var a in ca.ConstructorArguments) CheckArgument(a, user);
                foreach (var n in ca.Properties.Concat(ca.Fields)) CheckArgument(n.Argument, user);
                // Named arguments name a property or field of the attribute type (or a base).
                var at = ca.AttributeType;
                string? asm = ScopeName(at);
                if (asm == null || !IsPlatformAssembly(asm) || (ca.Properties.Count == 0 && ca.Fields.Count == 0)) continue;
                var old = target.Find(at.FullName, asm);
                if (old == null) continue;
                foreach (var n in ca.Properties)
                    if (!Chain(old, target).Any(t => t.Properties.Any(x => x.Name == n.Name)))
                        Add("member-missing", asm, $"{at.FullName}::{n.Name} (attribute property)", user).Why = "no such property";
                foreach (var n in ca.Fields)
                    if (!Chain(old, target).Any(t => t.Fields.Any(x => x.Name == n.Name)))
                        Add("member-missing", asm, $"{at.FullName}::{n.Name} (attribute field)", user).Why = "no such field";
            }
        }

        void CheckArgument(CustomAttributeArgument a, string user)
        {
            CheckType(a.Type, user);
            switch (a.Value)
            {
                case TypeReference t: CheckType(t, user); break;
                case CustomAttributeArgument inner: CheckArgument(inner, user); break;
                case CustomAttributeArgument[] arr: foreach (var x in arr) CheckArgument(x, user); break;
            }
        }

        void CheckGenericParameters(IGenericParameterProvider p, string user)
        {
            foreach (var gp in p.GenericParameters)
            {
                CheckAttributes(gp, user);
                foreach (var c in gp.Constraints) { CheckType(c.ConstraintType, user); CheckAttributes(c, user); }
            }
        }

        bases = new HashSet<string>();
        CheckAttributes(m.Assembly, "<assembly>");
        CheckAttributes(m, "<module>");
        foreach (var t in AllTypes(m))
        {
            if (t.Name is "__Rewind" or "<Module>") continue;
            string tu = t.FullName;
            bases = BaseNames(t);
            CheckType(t.BaseType, tu);
            CheckAttributes(t, tu);
            CheckGenericParameters(t, tu);
            foreach (var i in t.Interfaces) { CheckType(i.InterfaceType, tu); CheckAttributes(i, tu); }
            foreach (var f in t.Fields) { CheckType(f.FieldType, tu); CheckAttributes(f, tu); }
            foreach (var p in t.Properties) { CheckType(p.PropertyType, tu); CheckAttributes(p, tu); }
            foreach (var e in t.Events) { CheckType(e.EventType, tu); CheckAttributes(e, tu); }
            foreach (var md in t.Methods)
            {
                string u = $"{t.FullName}::{md.Name}";
                CheckType(md.ReturnType, u);
                CheckAttributes(md, u);
                CheckAttributes(md.MethodReturnType, u);
                CheckGenericParameters(md, u);
                foreach (var p in md.Parameters) { CheckType(p.ParameterType, u); CheckAttributes(p, u); }
                foreach (var ov in md.Overrides) CheckMember(ov, u);
                if (!md.HasBody) continue;
                foreach (var v in md.Body.Variables) CheckType(v.VariableType, u);
                foreach (var h in md.Body.ExceptionHandlers) CheckType(h.CatchType, u);
                foreach (var ins in md.Body.Instructions)
                {
                    switch (ins.Operand)
                    {
                        case MethodReference r when r.DeclaringType.Name == "__Rewind": break;
                        case MethodReference r: CheckMember(r, u); break;
                        case FieldReference r: CheckMember(r, u); break;
                        case TypeReference r: CheckType(r, u); break;
                        case CallSite cs:
                            CheckType(cs.ReturnType, u);
                            foreach (var p in cs.Parameters) CheckType(p.ParameterType, u);
                            break;
                    }
                }
            }
        }

        string[] order = ["type-missing", "type-nonpublic", "member-missing", "member-nonpublic", "type-moved"];
        using var tsv = new StreamWriter(tsvOut, false, new UTF8Encoding(false));
        tsv.WriteLine("kind\tunity6_assembly\ttarget_assembly\tapi\twhy\tunity6_visibility\tusers\tsites\texample_user\ttarget_overloads\tall_users");
        Console.WriteLine($"{name}: references to the platform that the target (Unity 2018.2 {(engineDir == null ? "editor engine modules" : engineDir)} + unityaot) lacks");
        foreach (var kind in order)
        {
            var list = gaps.Values.Where(g => g.Kind == kind).OrderByDescending(g => g.Sites).ThenBy(g => g.Api, StringComparer.Ordinal).ToList();
            Console.WriteLine($"  {kind}: {list.Count} (sites {list.Sum(g => g.Sites)}, game methods {list.SelectMany(g => g.Users).Distinct().Count()})");
            foreach (var g in list)
            {
                Console.WriteLine($"    {g.Sites,5} {g.Users.Count,4}  [{g.Asm6}{(g.TargetAsm != "" && g.TargetAsm != g.Asm6 ? " -> " + g.TargetAsm : "")}] {g.Api}" +
                                  $"{(g.Why != "" ? "  (" + g.Why + ")" : "")}{(g.Unity6 == "nonpublic" ? "  [non-public in Unity 6: inlined]" : "")}");
                tsv.WriteLine(string.Join('\t', g.Kind, g.Asm6, g.TargetAsm, g.Api, g.Why, g.Unity6, g.Users.Count, g.Sites,
                    g.Users.OrderBy(x => x, StringComparer.Ordinal).First(), g.TargetHas, string.Join(";", g.Users.OrderBy(x => x, StringComparer.Ordinal))));
            }
        }
        var users = gaps.Values.Where(g => g.Kind != "type-moved").SelectMany(v => v.Users).Distinct().Count();
        Console.WriteLine($"  game methods touching any missing or non-public item: {users}");
        return 0;
    }

    static string? ScopeName(TypeReference tr)
    {
        while (tr.DeclaringType != null) tr = tr.DeclaringType;
        return tr.Scope switch
        {
            AssemblyNameReference a => a.Name,
            ModuleDefinition md => md.Assembly.Name.Name,
            _ => tr.Scope?.Name,
        };
    }

    static string MemberKey(MemberReference mr, TypeReference decl) => mr switch
    {
        MethodReference m => $"{decl.FullName}::{m.Name}{(ElementMethod(m).HasGenericParameters ? $"<{ElementMethod(m).GenericParameters.Count}>" : "")}" +
                             $"({string.Join(", ", ElementMethod(m).Parameters.Select(p => Sig(p.ParameterType)))}) : {Sig(ElementMethod(m).ReturnType)}",
        FieldReference f => $"{decl.FullName}::{f.Name} : {Sig(f.FieldType)}",
        _ => $"{decl.FullName}::{mr.Name}",
    };

    static MethodReference ElementMethod(MethodReference m) => m is GenericInstanceMethod gim ? gim.ElementMethod : m;

    /// A signature string that is the same for a reference and for the definition it names. Custom modifiers
    /// (modreq(InAttribute) of `in` parameters, modopt(IsConst) ...) are dropped: they don't select an overload
    /// in C#, and a rewriter maps the reference by name and shape.
    static string Sig(TypeReference t) => t switch
    {
        GenericParameter gp => (gp.Type == GenericParameterType.Method ? "!!" : "!") + gp.Position,
        ByReferenceType br => Sig(br.ElementType) + "&",
        ArrayType at => Sig(at.ElementType) + "[" + new string(',', at.Rank - 1) + "]",
        PointerType pt => Sig(pt.ElementType) + "*",
        PinnedType pn => Sig(pn.ElementType),
        GenericInstanceType git => git.ElementType.FullName + "<" + string.Join(",", git.GenericArguments.Select(Sig)) + ">",
        RequiredModifierType rm => Sig(rm.ElementType),
        OptionalModifierType om => Sig(om.ElementType),
        FunctionPointerType fp => "method " + Sig(fp.ReturnType) + "*(" + string.Join(",", fp.Parameters.Select(p => Sig(p.ParameterType))) + ")",
        _ => t.FullName,
    };

    /// The type, its base types and every interface any of them implements, as the target defines them.
    static IEnumerable<TypeDefinition> Chain(TypeDefinition start, Target target)
    {
        var seen = new HashSet<TypeDefinition>();
        var queue = new Queue<TypeDefinition>([start]);
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            if (!seen.Add(t)) continue;
            yield return t;
            if (t.BaseType != null && target.Find(Element(t.BaseType).FullName, ScopeName(t.BaseType)) is { } b) queue.Enqueue(b);
            foreach (var i in t.Interfaces)
                if (target.Find(Element(i.InterfaceType).FullName, ScopeName(i.InterfaceType)) is { } it) queue.Enqueue(it);
        }
        static TypeReference Element(TypeReference r) => r is GenericInstanceType g ? g.ElementType : r;
    }

    static IMemberDefinition? FindMember(TypeDefinition type, MemberReference mr, Target target)
    {
        if (mr is FieldReference f)
        {
            string want = Sig(f.FieldType);
            return Chain(type, target).SelectMany(t => t.Fields).FirstOrDefault(x => x.Name == f.Name && Sig(x.FieldType) == want);
        }
        if (mr is MethodReference m)
        {
            var em = ElementMethod(m);
            var want = em.Parameters.Select(p => Sig(p.ParameterType)).ToList();
            string ret = Sig(em.ReturnType);
            int gp = em.GenericParameters.Count;
            if (gp == 0 && m is GenericInstanceMethod gim) gp = gim.GenericArguments.Count;
            // Constructors and static members aren't inherited; .ctor/.cctor only on the type itself.
            IEnumerable<TypeDefinition> types = m.Name is ".ctor" or ".cctor" ? [type] : Chain(type, target);
            return types.SelectMany(t => t.Methods).FirstOrDefault(x => x.Name == m.Name && x.GenericParameters.Count == gp
                && x.HasThis == m.HasThis && Sig(x.ReturnType) == ret && x.Parameters.Select(p => Sig(p.ParameterType)).SequenceEqual(want));
        }
        return null;
    }

    static string Access(IMemberDefinition d) => d switch
    {
        MethodDefinition md when !IsVisible(md.DeclaringType) => "declaring type not public",
        FieldDefinition fd when !IsVisible(fd.DeclaringType) => "declaring type not public",
        MethodDefinition md => (md.Attributes & MethodAttributes.MemberAccessMask).ToString().ToLowerInvariant(),
        FieldDefinition fd => (fd.Attributes & FieldAttributes.FieldAccessMask).ToString().ToLowerInvariant(),
        _ => "",
    };

    static string Why(TypeDefinition type, MemberReference mr, Target target)
    {
        var chain = Chain(type, target).ToList();
        if (mr is FieldReference f)
            return chain.Any(t => t.Fields.Any(x => x.Name == f.Name)) ? "field type differs" : "no such field";
        int same = chain.Sum(t => t.Methods.Count(x => x.Name == mr.Name));
        if (same == 0) return "no member of that name";
        return $"no overload with this signature (2018.2 has {same})";
    }

    static string Overloads(TypeDefinition type, MemberReference mr, Target target)
    {
        var chain = Chain(type, target).ToList();
        if (mr is FieldReference f)
            return string.Join(" | ", chain.SelectMany(t => t.Fields).Where(x => x.Name == f.Name).Select(x => Sig(x.FieldType)));
        return string.Join(" | ", chain.SelectMany(t => t.Methods).Where(x => x.Name == mr.Name).Take(8)
            .Select(x => $"({string.Join(", ", x.Parameters.Select(p => Sig(p.ParameterType)))}) : {Sig(x.ReturnType)}"));
    }
}
