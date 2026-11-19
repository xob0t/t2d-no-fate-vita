using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The repair engine: applies the rules in rules/*.cs to Rewind's Assembly-CSharp before the rewriter's
// other passes. A rule names one game method and, per gap marker, says what IL stands in its place.
// Rules never restate a method: they key a marker by its kind and a short prefix of its text, check a
// hash of the IL around it, and replace only the marker (or a few instructions next to it, inside
// the hashed window) with IL built by IL.cs's builder. See docs/repairs.md.
//
// C# 5 and Mono.Cecil 0.10.0-beta6, like huntdown's rewriter, so it compiles into it unchanged.
namespace Repairs
{
    // A __Rewind.Opaque<T>("...") or __Rewind.Effect("...") call: an ldstr and the call.
    public sealed class Marker
    {
        public int Index;               // among the method's markers, in IL order
        public bool IsValue;            // Opaque<T> (pushes a T) or Effect (pushes nothing)
        public TypeReference ValueType; // T, for Opaque
        public string Text;
        public Instruction Ldstr, Call;

        public string Kind { get { return IsValue ? "Opaque<" + ValueType.Name + ">" : "Effect"; } }

        // The arguments of the marker's outermost call, split at top-level commas:
        // "__native_0x1800E2FD0(__klass(), 8L)" gives "__klass()", "8L".
        public string[] Args
        {
            get
            {
                int open = Text.IndexOf('(');
                if (open < 0 || !Text.EndsWith(")")) return new string[0];
                var args = new List<string>();
                int depth = 0, start = open + 1;
                for (int i = open + 1; i < Text.Length - 1; i++)
                {
                    char c = Text[i];
                    if (c == '(') depth++;
                    else if (c == ')') depth--;
                    else if (c == ',' && depth == 0)
                    {
                        args.Add(Text.Substring(start, i - start).Trim());
                        start = i + 1;
                    }
                }
                args.Add(Text.Substring(start, Text.Length - 1 - start).Trim());
                return args.ToArray();
            }
        }

        // An integer literal argument ("8L"), or an error naming the marker.
        public long IntArg(int i)
        {
            string a = Args.Length > i ? Args[i] : "";
            long v;
            if (!long.TryParse(a.TrimEnd('L'), out v))
                throw new RepairException("marker " + Index + " (" + Text + "): argument " + i + " is not a literal: " + a);
            return v;
        }
    }

    public class RepairException : Exception
    {
        public RepairException(string message) : base(message) { }
    }

    public static class Markers
    {
        public static bool IsMarkerCall(Instruction ins)
        {
            var m = ins.Operand as MethodReference;
            return ins.OpCode == OpCodes.Call && m != null && m.DeclaringType.FullName == "__Rewind" && (m.Name == "Opaque" || m.Name == "Effect");
        }

        // The value an ldc.i4 (any form) pushes, or null.
        public static int? ConstI4(Instruction ins)
        {
            switch (ins.OpCode.Code)
            {
                case Code.Ldc_I4_M1: return -1;
                case Code.Ldc_I4_0: return 0;
                case Code.Ldc_I4_1: return 1;
                case Code.Ldc_I4_2: return 2;
                case Code.Ldc_I4_3: return 3;
                case Code.Ldc_I4_4: return 4;
                case Code.Ldc_I4_5: return 5;
                case Code.Ldc_I4_6: return 6;
                case Code.Ldc_I4_7: return 7;
                case Code.Ldc_I4_8: return 8;
                case Code.Ldc_I4_S: return (sbyte)ins.Operand;
                case Code.Ldc_I4: return (int)ins.Operand;
            }
            return null;
        }

        public static bool IsNoteCall(Instruction ins)
        {
            var m = ins.Operand as MethodReference;
            return ins.OpCode == OpCodes.Call && m != null && m.DeclaringType.FullName == "__Rewind" && m.Name == "Note";
        }

        public static List<Marker> Find(MethodDefinition method)
        {
            var list = new List<Marker>();
            if (!method.HasBody) return list;
            foreach (Instruction ins in method.Body.Instructions)
            {
                if (!IsMarkerCall(ins)) continue;
                var m = (MethodReference)ins.Operand;
                Instruction ldstr = ins.Previous;
                if (ldstr == null || ldstr.OpCode != OpCodes.Ldstr)
                    throw new RepairException(Engine.Name(method) + ": marker call at IL_" + ins.Offset.ToString("x4") + " without its ldstr");
                var g = m as GenericInstanceMethod;
                list.Add(new Marker
                {
                    Index = list.Count, IsValue = m.Name == "Opaque", ValueType = g != null ? g.GenericArguments[0] : null,
                    Text = (string)ldstr.Operand, Ldstr = ldstr, Call = ins,
                });
            }
            return list;
        }
    }

    // Hashes of normalized IL: a marker's context (the instructions around it) and a whole body.
    // Offsets and Rewind's temporary names (t12, m3, %45, o7) don't count, so a
    // marker keeps its hash when Rewind renumbers; any change to the code around it changes it.
    public static class Fingerprint
    {
        public const int Window = 6;
        static readonly Regex temps = new Regex(@"\b([tmo])\d+\b|%\d+");

        // A marker's text with Rewind's temporary names numbered away: "__load8((m0 + 560L))" is "__load8((m# + 560L))".
        public static string Text(string text)
        {
            return temps.Replace(text, "$1#");
        }

        public static string Norm(Instruction ins, IList<Instruction> all, MethodBody body)
        {
            object op = ins.Operand;
            string s;
            if (op == null) s = "";
            else if (op is Instruction) s = "@" + (all.IndexOf((Instruction)op) - all.IndexOf(ins));
            else if (op is Instruction[]) s = string.Join(",", ((Instruction[])op).Select(t => (all.IndexOf(t) - all.IndexOf(ins)).ToString()));
            else if (op is string) s = "\"" + temps.Replace((string)op, "$1#") + "\"";
            else if (op is VariableDefinition) s = "V" + ((VariableDefinition)op).Index + ":" + ((VariableDefinition)op).VariableType.FullName;
            else if (op is ParameterDefinition) s = "A:" + ((ParameterDefinition)op).Index;
            else if (op is MemberReference) s = ((MemberReference)op).FullName;
            else if (op is double) s = ((double)op).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            else if (op is float) s = ((float)op).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            else s = Convert.ToString(op, System.Globalization.CultureInfo.InvariantCulture);
            string name = ins.OpCode.Name;
            // ldloc.1 / ldloc.s / ldloc are one thing once the slot is dropped (it is in the operand for the long forms)
            if (name.StartsWith("ldloc") || name.StartsWith("stloc") || name.StartsWith("ldloca"))
            {
                VariableDefinition v = Local(ins, body);
                // the slot counts: a rule that loads V23 must notice when Rewind renumbers its locals
                if (v != null) s = "V" + v.Index + ":" + v.VariableType.FullName;
                name = name.Split('.')[0];
            }
            return name + " " + s;
        }

        // The local an ldloc/stloc/ldloca refers to (short forms carry no operand).
        public static VariableDefinition Local(Instruction ins, MethodBody body)
        {
            var v = ins.Operand as VariableDefinition;
            if (v != null) return v;
            string n = ins.OpCode.Name;
            if (body != null && (n.StartsWith("ldloc.") || n.StartsWith("stloc.")) && char.IsDigit(n[n.Length - 1]))
                return body.Variables[n[n.Length - 1] - '0'];
            return null;
        }

        static string Hash(IEnumerable<string> lines)
        {
            using (var sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
                return BitConverter.ToString(h, 0, 4).Replace("-", "").ToLowerInvariant();
            }
        }

        public static string Of(Marker marker, MethodBody body)
        {
            IList<Instruction> all = body.Instructions;
            int at = all.IndexOf(marker.Ldstr);
            int from = Math.Max(0, at - Window), to = Math.Min(all.Count - 1, at + 1 + Window);
            var lines = new List<string>();
            for (int i = from; i <= to; i++) lines.Add(Norm(all[i], all, body));
            return Hash(lines);
        }

        public static string Of(MethodDefinition method)
        {
            IList<Instruction> all = method.Body.Instructions;
            return Hash(all.Select(i => Norm(i, all, method.Body)).Concat(method.Body.Variables.Select(v => v.VariableType.FullName)));
        }
    }

    // How a run treats a rule whose marker is gone or whose hash differs. Strict is the build's
    // default: any drift fails the run. Pin prints the hashes rules are missing (authoring).
    public enum Mode { Strict, Pin }

    public sealed class Engine
    {
        public readonly ModuleDefinition Module;
        public readonly Mode Mode;
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Pins = new List<string>();
        public readonly List<string> Log = new List<string>();
        internal readonly Dictionary<MethodDefinition, MethodRepair> repairs = new Dictionary<MethodDefinition, MethodRepair>();
        public int RuleCount, SiteCount;

        public Engine(ModuleDefinition module, Mode mode)
        {
            Module = module;
            Mode = mode;
        }

        public static string Name(MethodReference m)
        {
            return m.DeclaringType.FullName + "::" + m.Name + "(" + string.Join(",", m.Parameters.Select(p => p.ParameterType.Name)) + ")";
        }

        // "Namespace.Type::name(Int32,Double)": parameter types by simple name, as Name prints them.
        public MethodDefinition FindMethod(string key)
        {
            int sep = key.IndexOf("::");
            TypeDefinition type = Module.GetType(key.Substring(0, sep).Replace('+', '/'));
            if (type == null) return null;
            return type.Methods.FirstOrDefault(m => Name(m) == key);
        }

        // Starts a method's repair. hash is the method's body hash when the rule was written: a
        // different one is a warning (the method changed elsewhere; the markers' own hashes decide).
        public MethodRepair Method(string key, string hash)
        {
            MethodDefinition method = FindMethod(key);
            if (method == null)
            {
                Errors.Add(key + ": no such method");
                return new MethodRepair(this, null, key);
            }
            if (repairs.ContainsKey(method))
            {
                Errors.Add(key + ": two rules for one method");
                return new MethodRepair(this, null, key);
            }
            Cleanup.LongBranches(method.Body);
            string now = Fingerprint.Of(method);
            if (hash == null) Pins.Add(key + ": method hash " + now);
            else if (hash != now) Warnings.Add(key + ": method hash " + now + ", the rule was written against " + hash);
            var repair = new MethodRepair(this, method, key);
            repairs[method] = repair;
            return repair;
        }

        // Runs every rule (the static methods of Rules taking an Engine), then Finish.
        public static Engine Run(ModuleDefinition module, Mode mode, Func<string, bool> only)
        {
            var engine = new Engine(module, mode);
            foreach (System.Reflection.MethodInfo rule in typeof(Rules).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                         .Where(m => m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Engine)).OrderBy(m => m.Name))
            {
                if (only != null && !only(rule.Name)) continue;
                engine.RuleCount++;
                try
                {
                    rule.Invoke(null, new object[] { engine });
                }
                catch (System.Reflection.TargetInvocationException e)
                {
                    engine.Errors.Add(rule.Name + ": " + (e.InnerException is RepairException ? e.InnerException.Message : e.InnerException.ToString()));
                }
            }
            engine.Finish();
            return engine;
        }

        internal void Fail(string key, string message)
        {
            Errors.Add(key + ": " + message);
        }

        // After every rule: no marker left in a repaired method (unless its rule says Partial),
        // unreachable code removed, the Note dropped, and the stack checked.
        public void Finish()
        {
            foreach (KeyValuePair<MethodDefinition, MethodRepair> pair in repairs)
            {
                MethodDefinition method = pair.Key;
                MethodRepair r = pair.Value;
                List<string> dead = Cleanup.RemoveUnreachable(method);
                foreach (string text in dead)
                {
                    if (!r.MayDie(text)) Fail(Name(method), "unreachable code removed with marker \"" + text + "\" that no rule accounted for");
                }
                List<Marker> left = Markers.Find(method);
                if (left.Count > 0 && !r.IsPartial)
                    Fail(Name(method), left.Count + " marker(s) left: " + string.Join("; ", left.Select(m => m.Index + " " + m.Text).Take(5)));
                if (left.Count == 0) Cleanup.RemoveNotes(method);
                string stack = StackCheck.Check(method);
                if (stack != null) Fail(Name(method), "stack: " + stack);
            }
        }
    }

    // One method's repair: sites by marker, local retyping, markerless anchors.
    public sealed class MethodRepair
    {
        readonly Engine engine;
        public readonly MethodDefinition Method;
        readonly string key;
        readonly List<Marker> markers;
        readonly Dictionary<int, string> hashes = new Dictionary<int, string>();  // before any edit
        readonly HashSet<int> used = new HashSet<int>();
        readonly List<Regex> mayDie = new List<Regex>();
        public bool IsPartial;

        internal MethodRepair(Engine engine, MethodDefinition method, string key)
        {
            this.engine = engine;
            Method = method;
            this.key = key;
            markers = method == null ? new List<Marker>() : Markers.Find(method);
            foreach (Marker m in markers) hashes[m.Index] = Fingerprint.Of(m, method.Body);
        }

        public Engine Engine { get { return engine; } }
        public string Key { get { return key; } }

        // The nth marker (0-based) whose text starts with prefix, among those not taken yet by
        // this rule, checked against hash. A prefix names the marker's kind and the part of it that
        // is Rewind's (a helper's address, a vtable offset, a field): never an expression of the game's.
        public Site Site(string prefix, string hash)
        {
            return Site(prefix, 0, hash);
        }

        public Site Site(string prefix, int nth, string hash)
        {
            if (Method == null) return Repairs.Site.Null(this);
            var found = markers.Where(m => Fingerprint.Text(m.Text).StartsWith(Fingerprint.Text(prefix)) && !used.Contains(m.Index)).ToList();
            if (found.Count <= nth)
            {
                engine.Fail(key, "no marker \"" + prefix + "\" #" + nth + " (stale rule: Rewind may recover it now; check and delete the site)");
                return Repairs.Site.Null(this);
            }
            Marker marker = found[nth];
            used.Add(marker.Index);
            string now = hashes[marker.Index];
            if (hash == null) engine.Pins.Add(key + ": site \"" + prefix + "\" #" + nth + " (marker " + marker.Index + ") " + now);
            else if (hash != now)
            {
                engine.Fail(key, "marker " + marker.Index + " \"" + prefix + "\": context hash " + now + ", the rule was written against " + hash + " (the IL around it changed: re-check the repair)");
                return Repairs.Site.Null(this);
            }
            return new Site(this, marker, now);
        }

        // Every remaining marker whose text starts with prefix: for repeated plumbing (a vtable
        // load used only by a tail-merged call). count is how many the rule expects.
        public Site[] Sites(string prefix, int count, string hash)
        {
            var list = new List<Site>();
            for (int i = 0; i < count; i++) list.Add(Site(prefix, 0, hash));
            if (Method != null && markers.Any(m => Fingerprint.Text(m.Text).StartsWith(Fingerprint.Text(prefix)) && !used.Contains(m.Index)))
                engine.Fail(key, "more than " + count + " markers \"" + prefix + "\"");
            return list.ToArray();
        }

        // A place without a marker (a silent error): the nth instruction run matching pattern
        // (opcode names, '*' for any; operands by substring after a space), checked against hash.
        public Site Anchor(string[] pattern, int nth, string hash)
        {
            if (Method == null) return Repairs.Site.Null(this);
            IList<Instruction> all = Method.Body.Instructions;
            int seen = 0;
            for (int i = 0; i + pattern.Length <= all.Count; i++)
            {
                bool ok = true;
                for (int j = 0; j < pattern.Length && ok; j++) ok = Matches(all[i + j], pattern[j]);
                if (!ok) continue;
                if (seen++ != nth) continue;
                var pseudo = new Marker { Index = -1, Text = "anchor " + string.Join(" ", pattern), Ldstr = all[i], Call = all[i + pattern.Length - 1] };
                string now = Fingerprint.Of(pseudo, Method.Body);
                if (hash == null) engine.Pins.Add(key + ": anchor " + string.Join(" ", pattern) + " #" + nth + " " + now);
                else if (hash != now)
                {
                    engine.Fail(key, "anchor " + string.Join(" ", pattern) + ": context hash " + now + ", the rule was written against " + hash);
                    return Repairs.Site.Null(this);
                }
                return new Site(this, pseudo, now);
            }
            engine.Fail(key, "anchor " + string.Join(" ", pattern) + " #" + nth + " not found (stale rule?)");
            return Repairs.Site.Null(this);
        }

        static bool Matches(Instruction ins, string pat)
        {
            string[] p = pat.Split(new[] { ' ' }, 2);
            if (p[0] != "*" && ins.OpCode.Name != p[0]) return false;
            if (p.Length == 1) return true;
            string op = ins.Operand is MemberReference ? ((MemberReference)ins.Operand).FullName : Convert.ToString(ins.Operand, System.Globalization.CultureInfo.InvariantCulture);
            return op != null && op.Contains(p[1]);
        }

        // Rewind typed a local by its native use (an int64 holding an array); give it the real type.
        public MethodRepair RetypeLocal(int index, TypeReference type)
        {
            if (Method == null) return this;
            VariableDefinition v = Method.Body.Variables[index];
            v.VariableType = engine.Module.ImportReference(type);
            return this;
        }

        public MethodRepair RetypeLocal(int index, string typeName)
        {
            return RetypeLocal(index, Types.Parse(engine.Module, typeName));
        }

        public VariableDefinition NewLocal(string typeName)
        {
            var v = new VariableDefinition(engine.Module.ImportReference(Types.Parse(engine.Module, typeName)));
            Method.Body.Variables.Add(v);
            return v;
        }

        // Markers that unreachable-code removal may take with it (the plumbing behind a removed
        // branch); prefixes, as for Site.
        public MethodRepair MayRemove(params string[] prefixes)
        {
            foreach (string p in prefixes) mayDie.Add(new Regex("^" + Regex.Escape(Fingerprint.Text(p))));
            return this;
        }

        internal bool MayDie(string text)
        {
            return mayDie.Any(r => r.IsMatch(Fingerprint.Text(text)));
        }

        // The rule repairs only some markers; the rest are left for later (and reported).
        public MethodRepair Partial()
        {
            IsPartial = true;
            return this;
        }

        public Instruction Arg(int index)
        {
            return IL.LdargFor(Method, index);
        }
    }

    public sealed class Site
    {
        readonly MethodRepair repair;
        public readonly Marker Marker;
        public readonly string Hash;

        internal Site(MethodRepair repair, Marker marker, string hash)
        {
            this.repair = repair;
            Marker = marker;
            Hash = hash;
        }

        internal static Site Null(MethodRepair repair)
        {
            return new Site(repair, null, null);
        }

        MethodBody Body { get { return repair.Method.Body; } }

        // The instructions before and after the marker, inside the hashed window, that a
        // replacement may also take: Replace(before: 1, ...) takes the instruction before the ldstr.
        public Site Replace(int before, int after, Action<IL> build)
        {
            if (Marker == null) return this;
            if (before > Fingerprint.Window || after > Fingerprint.Window)
                throw new RepairException(repair.Key + ": a replacement may reach at most " + Fingerprint.Window + " instructions from its marker");
            IList<Instruction> all = Body.Instructions;
            int first = all.IndexOf(Marker.Ldstr) - before, last = all.IndexOf(Marker.Call) + after;
            var il = new IL(repair);
            build(il);
            Edit.ReplaceRange(Body, first, last, il.Instructions, repair.Key);
            repair.Engine.SiteCount++;
            return this;
        }

        // Opaque<T>: IL that pushes the value. Effect: IL with the effect (none: delete it).
        public Site With(Action<IL> build)
        {
            return Replace(0, 0, build);
        }

        // The inlined card-marking write barrier after a store (Master builds): the marker is the
        // load of the GC flag, `if (__load4(flag) != 0) { card-table loop }`. The test becomes a
        // branch past the loop, which unreachable-code removal then takes, with its markers.
        public Site SkipBarrier()
        {
            if (Marker == null) return this;
            Instruction a = Marker.Call.Next, b = a == null ? null : a.Next, c = b == null ? null : b.Next;
            bool shape = Marker.IsValue && Marker.Text.StartsWith("__load4(") && a != null && Markers.ConstI4(a) == 0
                         && b.OpCode == OpCodes.Ceq && c.OpCode == OpCodes.Brtrue;
            if (!shape) throw new RepairException(repair.Key + ": marker " + Marker.Index + " is not a write barrier's flag test");
            var past = (Instruction)c.Operand;
            repair.MayRemove("__statics()", "__load8(", "__atomic_compare_exchange(");
            return Replace(0, 3, il => il.Br(past));
        }

        // A 2-byte store over two adjacent bool fields, `__store2(&this.F, v)` (mov word [this+off], v):
        // F = low byte, next = high byte. next is the field at F's offset + 1 (the native layout).
        public Site SplitStore2(string declaringType, string next)
        {
            if (Marker == null) return this;
            var m = Regex.Match(Marker.Text, @"^__store2\(&this\.(\w+), (\d+)\)$");
            if (!m.Success) throw new RepairException(repair.Key + ": marker " + Marker.Index + " is not __store2(&this.<field>, <constant>)");
            int v = int.Parse(m.Groups[2].Value);
            string first = declaringType + "::" + m.Groups[1].Value, second = declaringType + "::" + next;
            return With(il => il.This().I4((v & 0xFF) != 0 ? 1 : 0).Stfld(first).This().I4((v >> 8) != 0 ? 1 : 0).Stfld(second));
        }

        // Effect: delete. Opaque: delete with the instruction that consumes the value (stloc or pop).
        public Site Delete()
        {
            if (Marker == null) return this;
            if (!Marker.IsValue) return Replace(0, 0, il => { });
            Instruction next = Marker.Call.Next;
            if (!(next.OpCode.Name.StartsWith("stloc") || next.OpCode == OpCodes.Pop))
                throw new RepairException(repair.Key + ": marker " + Marker.Index + "'s value is used by " + next.OpCode.Name + ", not stored: say what it is");
            return Replace(0, 1, il => { });
        }
    }

    public static class Types
    {
        // "System.Int32", "System.Int32[]", "com.x.Player", "System.Collections.Generic.List`1<System.Int32>" is not supported.
        public static TypeReference Parse(ModuleDefinition module, string name)
        {
            if (name.EndsWith("[]")) return new ArrayType(Parse(module, name.Substring(0, name.Length - 2)));
            switch (name)
            {
                case "System.Void": return module.TypeSystem.Void;
                case "System.Boolean": return module.TypeSystem.Boolean;
                case "System.Int32": return module.TypeSystem.Int32;
                case "System.Int64": return module.TypeSystem.Int64;
                case "System.Single": return module.TypeSystem.Single;
                case "System.Double": return module.TypeSystem.Double;
                case "System.String": return module.TypeSystem.String;
                case "System.Object": return module.TypeSystem.Object;
                case "System.Byte": return module.TypeSystem.Byte;
            }
            TypeDefinition own = module.GetType(name.Replace('+', '/'));
            if (own != null) return own;
            foreach (TypeReference r in module.GetTypeReferences())
                if (r.FullName == name) return r;
            throw new RepairException("no type " + name + " in " + module.Name + " or its references");
        }
    }
}
