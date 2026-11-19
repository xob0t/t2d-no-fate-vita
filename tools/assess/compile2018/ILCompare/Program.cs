// ILCompare: compare the IL of Rewind's rebuilt assembly with the IL Roslyn compiled from Rewind's C#
// (after mechfix.py), method by method, to see whether the round trip through C# keeps the meaning.
//
//   ILCompare <rewind.dll> <recompiled.dll> <out.tsv> [<rewind dir for resolving>]
//
// Methods compared: those with a body on both sides, no __Rewind.Opaque/Effect in Rewind's IL (complete),
// and not stubbed in the recompiled one (`throw null`). For each, the "effects" sequence on both sides:
// calls and newobj (declaring type, name, parameter count), field loads and stores (with the kind of
// access), string literals, and the integer and float constants. Compiler-generated names are
// normalized as mechfix.py renames them; __Rewind and Compat2018 calls are dropped on the recompiled
// side, and calls to members mechfix redirects are dropped on Rewind's side.
//
// Verdicts: same (identical sequences); reordered (same multiset); differs. The TSV lists every compared
// method with its verdict and, for differing ones, the first difference.
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: ILCompare <rewind.dll> <recompiled.dll> <out.tsv>");
    return 2;
}

var rewind = ModuleDefinition.ReadModule(args[0]);
var recompiled = ModuleDefinition.ReadModule(args[1]);

static string Norm(string s)
{
    s = Regex.Replace(s, @"CS\$<>(\d+)__locals(\d+)", "CS__$1__locals$2");
    s = Regex.Replace(s, @"(?<![\w>])<>(\w+)", "__$1");
    s = Regex.Replace(s, @"(?<![\w>])<(\w+)>([a-zA-Z])__(\w*)", "_$1_$2__$3");
    return s;
}

static string TypeName(TypeReference t) => Norm(t.FullName.Replace('/', '.'));

static string MethodKey(MethodDefinition m) =>
    TypeName(m.DeclaringType) + "::" + Norm(m.Name) + "(" + string.Join(",", m.Parameters.Select(p => Norm(p.ParameterType.Name))) + ")";

static bool IsMarkerCall(Instruction i) =>
    (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference mr &&
    (mr.DeclaringType.Name == "__Rewind");

static bool Stubbed(MethodDefinition m) =>
    m.Body.Instructions.Count(i => i.OpCode != OpCodes.Nop) == 2 &&
    m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldnull) && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Throw);

// Redirected by mechfix (their Rewind-side calls have a Compat2018 counterpart, dropped on both sides).
var redirected = new Regex(@"(_Injected$|^GetCachedPtr$|^SingleToInt32Bits$|^AsPointer$|^AreSame$|^SequenceEqual$|^Throw\w+$|^UnmarshalUnityObject$|^MemberwiseClone$|^InitializeArray$|^GetUninitializedObject$|^GetTypeFromHandle$)");

static string? Effect(Instruction i, Regex redirected, bool recompiledSide)
{
    switch (i.Operand)
    {
        case MethodReference mr when i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Newobj:
        {
            string dt = mr.DeclaringType.Name;
            if (dt == "__Rewind" || mr.DeclaringType.Namespace == "Compat2018" || dt is "BCL" or "Unsafe" or "Engine" or "Injected") return null;
            if (redirected.IsMatch(mr.Name)) return null;
            // base constructor calls: Rewind's are call .ctor, Roslyn's too; keep
            string kind = i.OpCode == OpCodes.Newobj ? "new" : "call";
            return $"{kind} {TypeName(mr.DeclaringType)}::{Norm(mr.Name)}/{mr.Parameters.Count}";
        }
        case FieldReference fr:
        {
            if (fr.Name == "_firstChar" || fr.Name.StartsWith("method") || fr.Name == "m_Ptr" || fr.Name == "m_target") return null;
            string op = i.OpCode.Code switch
            {
                Code.Ldfld or Code.Ldsfld => "ld",
                Code.Stfld or Code.Stsfld => "st",
                _ => "addr",
            };
            return $"{op} {TypeName(fr.DeclaringType)}::{Norm(fr.Name)}";
        }
        case string s when i.OpCode == OpCodes.Ldstr:
            return "str " + s;
        case double d:
            return "r8 " + d.ToString("R");
        case float f:
            return "r4 " + f.ToString("R");
    }
    return null;
}

static List<string> Effects(MethodDefinition m, Regex redirected, bool rec) =>
    m.Body.Instructions.Select(i => Effect(i, redirected, rec)).Where(e => e != null).Select(e => e!).ToList();

var recMethods = new Dictionary<string, MethodDefinition>();
foreach (var t in recompiled.GetTypes())
    foreach (var m in t.Methods.Where(m => m.HasBody))
        recMethods.TryAdd(MethodKey(m), m);

int compared = 0, same = 0, reordered = 0, differs = 0, gap = 0, stubbed = 0, missing = 0;
var diffKinds = new Dictionary<string, int>();
using var w = new StreamWriter(args[2]);
w.WriteLine("method\tverdict\trewind_effects\trecompiled_effects\tfirst_difference");
foreach (var t in rewind.GetTypes())
{
    if (t.Name == "__Rewind") continue;
    foreach (var m in t.Methods.Where(m => m.HasBody))
    {
        if (m.Body.Instructions.Any(i => IsMarkerCall(i) && ((MethodReference)i.Operand).Name is "Opaque" or "Effect")) { gap++; continue; }
        if (!recMethods.TryGetValue(MethodKey(m), out var r)) { missing++; w.WriteLine($"{MethodKey(m)}\tmissing\t\t\t"); continue; }
        if (Stubbed(r)) { stubbed++; continue; }
        compared++;
        var a = Effects(m, redirected, false);
        var b = Effects(r, redirected, true);
        string verdict;
        string first = "";
        if (a.SequenceEqual(b)) { verdict = "same"; same++; }
        else if (a.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(b.OrderBy(x => x, StringComparer.Ordinal))) { verdict = "reordered"; reordered++; }
        else
        {
            verdict = "differs"; differs++;
            var onlyA = a.GroupBy(x => x).SelectMany(g => g.Skip(b.Count(y => y == g.Key))).ToList();
            var onlyB = b.GroupBy(x => x).SelectMany(g => g.Skip(a.Count(y => y == g.Key))).ToList();
            first = "rewind-only: " + string.Join(" | ", onlyA.Take(4)) + "  recompiled-only: " + string.Join(" | ", onlyB.Take(4));
            foreach (var x in onlyA) { var k = "rewind-only " + Regex.Replace(x, @"\s.*$", "") + " " + Regex.Replace(x, @"^.*::", ""); diffKinds[k] = diffKinds.GetValueOrDefault(k) + 1; }
            foreach (var x in onlyB) { var k = "recompiled-only " + Regex.Replace(x, @"\s.*$", "") + " " + Regex.Replace(x, @"^.*::", ""); diffKinds[k] = diffKinds.GetValueOrDefault(k) + 1; }
        }
        w.WriteLine($"{MethodKey(m)}\t{verdict}\t{a.Count}\t{b.Count}\t{first.Replace('\t', ' ').Replace('\n', ' ')}");
    }
}
Console.WriteLine($"{rewind.Name}: gap methods skipped {gap}, stubbed in recompiled {stubbed}, missing in recompiled {missing}; compared {compared}: same {same}, reordered {reordered}, differs {differs}");
foreach (var kv in diffKinds.OrderByDescending(kv => kv.Value).Take(25)) Console.WriteLine($"  {kv.Value,6}  {kv.Key}");
return 0;
