// Dumps an assembly's call graph and its Rewind gap markers as JSON, for triage.py.
//
//   GapGraph <assembly.dll> <out.json>
//
// For every type: full name, namespace, base type, interfaces, nested-in.
// For every method with a body: its key (Type::name(params)), virtual/static/ctor flags, the marker sites
// (__Rewind.Opaque / __Rewind.Effect with their text), Rewind's Note, and its references: calls (call,
// callvirt, newobj, ldftn, ldvirtftn), the types whose static fields it touches (for .cctor edges) and the types it
// takes a token of (Rewind's raw allocations: GetUninitializedObject(typeof(T)) then T's .ctor).
// Only references inside the assembly are kept. Reads nothing but the given file; writes only <out.json>.
// Contains no game content: it works on whatever assembly it is given.

using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class GapGraph
{
    static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("usage: GapGraph <assembly.dll> <out.json>"); return 2; }
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
        using var module = ModuleDefinition.ReadModule(args[0], new ReaderParameters { AssemblyResolver = resolver });
        string own = module.Assembly.Name.Name;

        var types = new List<object>();
        var methods = new List<object>();
        foreach (var t in AllTypes(module))
        {
            if (t.Name == "<Module>") continue;
            if (t.Name == "__Rewind" && t.DeclaringType == null) continue;
            types.Add(new Dictionary<string, object?>
            {
                ["name"] = t.FullName,
                ["short"] = t.Name,
                ["ns"] = Ns(t),
                ["base"] = t.BaseType?.FullName,
                ["interfaces"] = t.Interfaces.Select(i => i.InterfaceType.FullName).ToList(),
                ["abstract"] = t.IsAbstract,
                ["interface"] = t.IsInterface,
            });
            foreach (var md in t.Methods)
            {
                var calls = new List<object>();
                var statics = new HashSet<string>();
                var tokens = new HashSet<string>();
                var markers = new List<object>();
                string? note = null;
                if (md.HasBody)
                {
                    var ins = md.Body.Instructions;
                    for (int i = 0; i < ins.Count; i++)
                    {
                        var x = ins[i];
                        if (x.Operand is MethodReference mr)
                        {
                            if (mr.DeclaringType.Name == "__Rewind")
                            {
                                string text = Str(ins, i) ?? "";
                                if (mr.Name is "Opaque" or "Effect")
                                    markers.Add(new Dictionary<string, object> { ["kind"] = mr.Name, ["text"] = text, ["offset"] = x.Offset });
                                else if (mr.Name == "Note") note = text;
                                continue;
                            }
                            if (!InModule(mr.DeclaringType, own)) continue;
                            string op = x.OpCode.Code switch
                            {
                                Code.Call => "call", Code.Callvirt => "callvirt", Code.Newobj => "newobj",
                                Code.Ldftn => "ldftn", Code.Ldvirtftn => "ldvirtftn", _ => "other",
                            };
                            calls.Add(new object[] { op, Key(mr), Elem(mr.DeclaringType).FullName });
                        }
                        else if (x.Operand is TypeReference tr && x.OpCode.Code == Code.Ldtoken)
                        {
                            if (InModule(tr, own)) tokens.Add(Elem(tr).FullName);
                        }
                        else if (x.Operand is FieldReference fr && x.OpCode.Code is Code.Ldsfld or Code.Stsfld or Code.Ldsflda)
                        {
                            if (InModule(fr.DeclaringType, own)) statics.Add(Elem(fr.DeclaringType).FullName);
                        }
                    }
                }
                methods.Add(new Dictionary<string, object?>
                {
                    ["key"] = Key(md),
                    ["type"] = t.FullName,
                    ["name"] = md.Name,
                    ["sig"] = string.Join(",", md.Parameters.Select(p => p.ParameterType.Name)),
                    ["full"] = md.FullName,
                    ["virtual"] = md.IsVirtual,
                    ["newslot"] = md.IsNewSlot,
                    ["abstract"] = md.IsAbstract,
                    ["static"] = md.IsStatic,
                    ["body"] = md.HasBody,
                    ["il_size"] = md.HasBody ? md.Body.CodeSize : 0,
                    ["markers"] = markers,
                    ["note"] = note,
                    ["calls"] = calls,
                    ["statics"] = statics.OrderBy(s => s, StringComparer.Ordinal).ToList(),
                    ["tokens"] = tokens.OrderBy(s => s, StringComparer.Ordinal).ToList(),
                });
            }
        }
        var doc = new Dictionary<string, object> { ["assembly"] = own, ["file"] = Path.GetFullPath(args[0]), ["types"] = types, ["methods"] = methods };
        File.WriteAllText(args[1], JsonSerializer.Serialize(doc), new UTF8Encoding(false));
        int gaps = methods.Count(m => ((List<object>)((Dictionary<string, object?>)m)["markers"]!).Count > 0);
        Console.WriteLine($"{own}: {types.Count} types, {methods.Count} methods, {gaps} with markers -> {args[1]}");
        return 0;
    }

    static string? Str(Mono.Collections.Generic.Collection<Instruction> ins, int i)
    {
        for (int j = i - 1; j >= 0 && j >= i - 3; j--)
            if (ins[j].OpCode.Code == Code.Ldstr) return (string)ins[j].Operand;
        return null;
    }

    static TypeReference Elem(TypeReference t) => t is GenericInstanceType g ? g.ElementType : t;

    static bool InModule(TypeReference t, string own)
    {
        t = Elem(t);
        if (t is ArrayType) return false;
        var scope = t.Scope;
        return scope is ModuleDefinition || (scope is AssemblyNameReference a && a.Name == own);
    }

    // Type::name(paramTypeNames): the same key for a reference and its definition inside one module.
    static string Key(MethodReference m)
    {
        var e = m is GenericInstanceMethod gm ? gm.ElementMethod : m;
        return Elem(e.DeclaringType).FullName + "::" + e.Name + "(" + string.Join(",", e.Parameters.Select(p => p.ParameterType.Name)) + ")";
    }

    static string Ns(TypeDefinition t)
    {
        while (t.DeclaringType != null) t = t.DeclaringType;
        return t.Namespace;
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
}
