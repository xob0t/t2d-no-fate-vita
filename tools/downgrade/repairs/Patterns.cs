using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Pattern repairs: the same edit wherever a marker has a known shape, with no game method named.
// They run after the per-method rules, on the methods no rule took, and repair a method only when
// it then has no marker left (a method half repaired by a pattern would hide its gaps from the
// rule that has to finish it). These are the fixes that belong in Rewind; they live here until
// Rewind makes them, and drop out on their own when it does (no marker, no match).
//
// The families, each a shape of IL2CPP's or Rewind's (docs/repairs.md, "Patterns"):
//   barrier      the inlined GC write barrier: its flag test becomes a branch past it (SkipBarrier).
//   string       string.Equals(a, b) inlined down to SpanHelpers.SequenceEqual's loops over
//                &a._firstChar and &b._firstChar: the "same address" test becomes String.Equals(a, b),
//                stored where the loops store their result; the loops die.
//   box          il2cpp's Box(Int32, &slot) inlined: valuetype test, nullable test, Object::New and a
//                Memcpy from the frame. The block becomes `box Int32` of the value; the value is the low
//                half of what Rewind computes for the block's reference-type path (*(object*)&slot),
//                read by KnownBits; the block's result local becomes an object.
//   unbox        a read at +16 of a boxed value, `__loadN(__address(__reference_arithmetic((x + 16L))))`,
//                behind `x is T` (unbox.any T), or behind only `x != null` for a 1-byte read (a Haxe Bool).
//   unreachable  `__unreachable()` / `__noreturn()` right after a call whose body can't return (every
//                path ends in a throw): the marker goes.
// One trial per method applies every family the method has, removes the code no path reaches, and
// keeps the result only if no marker is left and the stack checks. A method whose markers are all
// of these families but whose IL isn't the shape expected is put back, with a warning naming it and
// the reason ("pattern refused <method>: <family>: ..."): a new variant from Rewind is seen, not
// repaired by guess. Methods with markers of other kinds aren't tried (Repairs patterns tries them dry).
namespace Repairs
{
    public static class Patterns
    {
        public static readonly string[] Families = { "barrier", "string", "box", "unbox", "unreachable" };

        // Per family: markers in the methods tried, markers removed in the methods kept, methods kept that had it.
        public sealed class Tally
        {
            public int Seen, Removed, Methods;
        }

        public sealed class Report
        {
            public readonly Dictionary<string, Tally> Kept = Families.ToDictionary(f => f, f => new Tally());
            // dry runs (Repairs patterns): markers each family removes in every method it applies to
            public readonly Dictionary<string, Tally> Reach = Families.ToDictionary(f => f, f => new Tally());
            public readonly List<string> Lines = new List<string>();
        }

        // dry: try every method with a marker of a family, report what each family would remove, and keep nothing.
        public static Report Run(Engine engine, bool dry)
        {
            var report = new Report();
            foreach (MethodDefinition method in engine.Module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).ToList())
            {
                if (engine.Taken(method)) continue;
                List<Marker> markers = Markers.Find(method);
                if (markers.Count == 0) continue;
                List<string> families = FamiliesOf(markers);
                bool all = families.All(f => f != null);
                if (!dry && (!all || !families.Any(f => f != null))) continue;
                if (dry && !families.Any(f => f != null)) continue;
                // the barrier family alone keeps its old precondition: a flag test to start from
                if (families.All(f => f == "barrier") && !markers.Any(IsFlagTest)) continue;
                Try(engine, method, markers, families, dry, report);
            }
            if (!dry) engine.PatternReport = report;
            return report;
        }

        static void Try(Engine engine, MethodDefinition method, List<Marker> markers, List<string> families, bool dry, Report report)
        {
            int sites = engine.SiteCount;
            MethodRepair r = engine.Trial(method);
            string name = Engine.Name(method);
            string error = null;
            try
            {
                Apply(r);
            }
            catch (RepairException e)
            {
                error = e.Message;
            }
            List<Marker> left = Markers.Find(method);
            var leftCalls = new HashSet<Instruction>(left.Select(m => m.Call));
            string stack = error == null ? StackCheck.Check(method) : null;
            bool complete = error == null && left.Count == 0 && stack == null;
            if (dry)
            {
                for (int i = 0; i < markers.Count; i++)
                {
                    if (families[i] == null) continue;
                    report.Reach[families[i]].Seen++;
                    if (error == null && !leftCalls.Contains(markers[i].Call)) report.Reach[families[i]].Removed++;
                }
                List<string> leftFamilies = FamiliesOf(left);
                report.Lines.Add((error != null ? "REFUSED " : complete ? "complete " : "partial ") + name + ": " +
                                 (error ?? (left.Count + " left" + (stack != null ? ", stack: " + stack : "") +
                                            string.Concat(left.Select((m, k) => " | " + (leftFamilies[k] ?? "other") + " " + m.Text).Take(4)))));
                r.Snapshot.Restore(method.Body);
                engine.SiteCount = sites;
                return;
            }
            if (!complete)
            {
                string why = error ?? (stack != null ? "stack: " + stack
                    : left.Count + " marker(s) left: " + string.Join("; ", left.Select(m => m.Index + " " + m.Text).Take(3)));
                engine.Warnings.Add("pattern refused " + name + ": " + why + " (method put back as Rewind left it)");
                r.Snapshot.Restore(method.Body);
                engine.SiteCount = sites;
                engine.PatternsDropped++;
                return;
            }
            foreach (string f in families.Distinct()) report.Kept[f].Methods++;
            foreach (string f in families) { report.Kept[f].Seen++; report.Kept[f].Removed++; }
            engine.Adopt(r);
        }

        // Every family on one method, in an order where each sees the code the earlier ones left reachable.
        static void Apply(MethodRepair r)
        {
            MethodDefinition method = r.Method;
            List<Marker> markers = r.AllMarkers.ToList();
            foreach (Marker m in markers.Where(m => Family(m) == "unreachable")) Unreachable(r, m);
            foreach (Marker m in markers.Where(m => Family(m) == "unbox")) Unbox(r, m);
            if (markers.Any(m => Family(m) == "string")) StringEquals(r);
            var boxed = new List<VariableDefinition>();
            List<Marker> tests = markers.Where(IsBoxTest).ToList();
            if (tests.Count > 0)
            {
                KnownBits bits = KnownBits.Of(method);
                if (bits == null) throw new RepairException("box: the stack can't be followed for the boxed values");
                // read every block before editing any (the dataflow is over the IL as Rewind wrote it)
                List<BoxBlock> blocks = tests.Select(m => BoxBlock.Read(r, m, bits)).ToList();
                foreach (BoxBlock b in blocks) b.Replace(r);
                boxed = blocks.Select(b => b.Result).Distinct().ToList();
            }
            Dead(method, "string/box", t => FamilyOfText(t) == "string" || IsBoxText(t) || IsBarrierText(t));
            foreach (VariableDefinition v in boxed) RetypeBoxed(r, v);
            foreach (Marker m in Markers.Find(method).Where(IsFlagTest)) r.SiteOf(m).SkipBarrier();
            Dead(method, "barrier", IsBarrierText);
        }

        // Removes what no path reaches; a marker it takes must be one of the families' plumbing.
        static void Dead(MethodDefinition method, string what, Func<string, bool> may)
        {
            foreach (string t in Cleanup.RemoveUnreachable(method))
                if (!may(t)) throw new RepairException(what + ": unreachable-code removal took marker \"" + t + "\", which no pattern accounts for");
        }

        // ---- which family a marker belongs to ----

        static readonly Regex unboxText = new Regex(@"^__load(1|4|8)\(__address\(__reference_arithmetic\(\((\w+) \+ 16L\)\)\)\)$");
        static readonly Regex stringLoad = new Regex(@"^__load(1|8)\(__address\(\((&[\w.""' ]+\._firstChar \+ \w+|\w+ \+ &[\w.""' ]+\._firstChar)\)\)\)$");
        static readonly Regex cardAddress = new Regex(@"^__address\(\(o\d+ \+ \d+L\)\)$");

        // Each marker's family; an `__undefined()` is the box's only in a method with a box's Memcpy.
        public static List<string> FamiliesOf(List<Marker> markers)
        {
            bool memcpy = markers.Any(m => !m.IsValue && IsBoxText(m.Text));
            return markers.Select(m => Family(m) == "box" && m.Text == "__undefined()" && !memcpy ? null : Family(m)).ToList();
        }

        public static string Family(Marker m)
        {
            if (IsFlagTest(m)) return "barrier";
            if (!m.IsValue && (m.Text == "__unreachable()" || m.Text == "__noreturn()")) return "unreachable";
            if (m.IsValue && unboxText.IsMatch(m.Text)) return "unbox";
            return FamilyOfText(m.Text);
        }

        static string FamilyOfText(string t)
        {
            if (stringLoad.IsMatch(t)) return "string";
            if (IsBoxText(t)) return "box";
            if (IsBarrierText(t)) return "barrier";
            return null;
        }

        static bool IsBoxText(string t)
        {
            return t == "__undefined()" || t.StartsWith("__Memcpy(__address((") && t.EndsWith(" + 16L)), __frame(), (__undefined() + -16L))");
        }

        // A barrier's parts: the GC flag (as a value), the card table, the card CAS, the statics base,
        // and the card address of a store into a new object (o + 16).
        static bool IsBarrierText(string text)
        {
            string t = Fingerprint.Text(text);
            return t == "__statics()" || t.StartsWith("__load8(") || t.StartsWith("__atomic_compare_exchange(")
                   || Regex.IsMatch(text, @"^__load4\(\d+L\)$") || cardAddress.IsMatch(text);
        }

        static bool IsFlagTest(Marker m)
        {
            return m.IsValue && m.Text.StartsWith("__load4(") && m.Text.EndsWith("L)") && m.Text.IndexOf('(', 1) == m.Text.LastIndexOf('(')
                   && Markers.ConstI4(m.Call.Next) == 0 && m.Call.Next.Next.OpCode == OpCodes.Ceq && (m.Call.Next.Next.Next.OpCode == OpCodes.Brtrue || m.Call.Next.Next.Next.OpCode == OpCodes.Brtrue_S);
        }

        // ---- unreachable ----

        static readonly Dictionary<MethodDefinition, bool> neverReturns = new Dictionary<MethodDefinition, bool>();

        static void Unreachable(MethodRepair r, Marker m)
        {
            Instruction call = m.Ldstr.Previous;
            var callee = call == null ? null : call.Operand as MethodReference;
            if (callee == null || (call.OpCode != OpCodes.Call && call.OpCode != OpCodes.Callvirt))
                throw new RepairException("unreachable: marker " + m.Index + " doesn't follow a call");
            if (Markers.IsMarkerCall(call))
                throw new RepairException("unreachable: marker " + m.Index + " follows an unrecovered call (" + (string)call.Previous.Operand + "), which nothing shows can't return");
            MethodDefinition def;
            try { def = callee.Resolve(); }
            catch (AssemblyResolutionException) { def = null; }
            if (def == null) throw new RepairException("unreachable: can't read " + callee.FullName + " to show it doesn't return");
            if (call.OpCode == OpCodes.Callvirt && (def.IsVirtual && !def.IsFinal && !def.DeclaringType.IsSealed))
                throw new RepairException("unreachable: " + callee.FullName + " is virtual; an override could return");
            if (!NeverReturns(def)) throw new RepairException("unreachable: " + callee.FullName + " can return");
            r.SiteOf(m).Delete();
        }

        // No ret reachable in the body (from its entry or a handler): every path throws.
        static bool NeverReturns(MethodDefinition method)
        {
            bool known;
            if (neverReturns.TryGetValue(method, out known)) return known;
            bool result = false;
            if (method.HasBody)
            {
                IList<Instruction> all = method.Body.Instructions;
                var seen = new HashSet<Instruction>();
                var work = new Stack<Instruction>();
                work.Push(all[0]);
                foreach (ExceptionHandler h in method.Body.ExceptionHandlers) work.Push(h.HandlerStart);
                result = true;
                while (work.Count > 0 && result)
                {
                    Instruction i = work.Pop();
                    if (i == null || !seen.Add(i)) continue;
                    if (i.OpCode == OpCodes.Ret || i.OpCode == OpCodes.Jmp || i.OpCode.Code == Code.Tail) { result = false; break; }
                    foreach (Instruction t in Edit.Targets(i)) work.Push(t);
                    FlowControl f = i.OpCode.FlowControl;
                    if (f == FlowControl.Branch || f == FlowControl.Throw || f == FlowControl.Return) continue;
                    if (i.OpCode == OpCodes.Endfinally || i.OpCode == OpCodes.Endfilter) continue;
                    work.Push(i.Next);
                }
            }
            neverReturns[method] = result;
            return result;
        }

        // ---- unbox ----

        static void Unbox(MethodRepair r, Marker m)
        {
            Match t = unboxText.Match(m.Text);
            int width = int.Parse(t.Groups[1].Value);
            string name = t.Groups[2].Value;
            MethodBody body = r.Method.Body;
            Instruction entry = m.Ldstr.Previous != null && m.Ldstr.Previous.OpCode == OpCodes.Nop ? m.Ldstr.Previous : m.Ldstr;
            List<Instruction> jumps = body.Instructions.Where(i => Edit.Targets(i).Any(x => x == entry || x == m.Ldstr)).ToList();
            Instruction fall = entry.Previous;
            bool fallsIn = fall != null && fall.OpCode.FlowControl != FlowControl.Branch && fall.OpCode.FlowControl != FlowControl.Return && fall.OpCode.FlowControl != FlowControl.Throw;
            if (jumps.Count + (fallsIn ? 1 : 0) != 1)
                throw new RepairException("unbox: marker " + m.Index + " has " + (jumps.Count + (fallsIn ? 1 : 0)) + " ways in, not one guarded one");
            Instruction test = fallsIn ? fall : jumps[0];
            Instruction load;
            TypeReference type;
            // x is T: <x> isinst T; ldnull; ceq; ldc.i4.0; ceq; brtrue entry
            Instruction[] g = Back(test, 6);
            if (!fallsIn && g != null && g[1].OpCode == OpCodes.Isinst && g[2].OpCode == OpCodes.Ldnull && g[3].OpCode == OpCodes.Ceq
                && Markers.ConstI4(g[4]) == 0 && g[5].OpCode == OpCodes.Ceq && test.OpCode == OpCodes.Brtrue)
            {
                load = g[0];
                type = (TypeReference)g[1].Operand;
            }
            else
            {
                // x != null only: <x> ldnull ceq brtrue past (falling into the marker), or <x> ldnull ceq ldc.i4.0 ceq brtrue entry
                Instruction[] n3 = Back(test, 3), n5 = Back(test, 5);
                if (fallsIn && test.OpCode == OpCodes.Brtrue && n3 != null && n3[1].OpCode == OpCodes.Ldnull && n3[2].OpCode == OpCodes.Ceq)
                    load = n3[0];
                else if (!fallsIn && test.OpCode == OpCodes.Brtrue && n5 != null && n5[1].OpCode == OpCodes.Ldnull && n5[2].OpCode == OpCodes.Ceq
                         && Markers.ConstI4(n5[3]) == 0 && n5[4].OpCode == OpCodes.Ceq)
                    load = n5[0];
                else throw new RepairException("unbox: marker " + m.Index + " isn't behind an `is T` or a null test of its object");
                // without a type test, only a 1-byte read decides the type: a Haxe Bool (Haxe has no other 1-byte type)
                if (width != 1) throw new RepairException("unbox: marker " + m.Index + " reads " + width + " bytes behind only a null test; the type isn't decided");
                type = r.Engine.Module.TypeSystem.Boolean;
            }
            if (!IsSimpleLoad(load)) throw new RepairException("unbox: marker " + m.Index + "'s object isn't a local or an argument");
            ParameterDefinition p = Parameter(r.Method, load);
            bool named = r.Method.Parameters.Any(x => x.Name == name);
            if (named ? p == null || p.Name != name : p != null && !Regex.IsMatch(name, @"^[tmo]\d+$"))
                throw new RepairException("unbox: marker " + m.Index + " reads " + name + ", not the object tested");
            if (type.IsGenericParameter) throw new RepairException("unbox: marker " + m.Index + ": the type tested is generic");
            string tn = type.FullName, un = m.ValueType.FullName;
            Action<IL> value;
            Instruction copy = Copy(load);
            TypeReference imported = r.Engine.Module.ImportReference(type);
            if (width == 4 && tn == "System.Int32" && un == "System.Int32" || width == 1 && tn == "System.Boolean" && (un == "System.Byte" || un == "System.Boolean"))
                value = il => il.Emit(copy).Emit(Instruction.Create(OpCodes.Unbox_Any, imported));
            else if (width == 8 && tn == "System.Double" && un == "System.Int64")
                value = il => il.Emit(copy).Emit(Instruction.Create(OpCodes.Unbox_Any, imported)).Emit(Instruction.Create(OpCodes.Call, Support.DoubleToBits(r.Engine.Module)));
            else throw new RepairException("unbox: marker " + m.Index + " reads " + width + " bytes as " + un + " of a " + tn + "; no rule for that pair");
            r.SiteOf(m).With(value);
        }

        // The n instructions before ins, in order, or null.
        static Instruction[] Back(Instruction ins, int n)
        {
            var list = new Instruction[n];
            for (int i = n - 1; i >= 0; i--)
            {
                ins = ins.Previous;
                if (ins == null) return null;
                list[i] = ins;
            }
            return list;
        }

        static bool IsSimpleLoad(Instruction i)
        {
            string n = i.OpCode.Name;
            return (n.StartsWith("ldloc") || n.StartsWith("ldarg")) && !n.StartsWith("ldloca") && !n.StartsWith("ldarga");
        }

        static ParameterDefinition Parameter(MethodDefinition method, Instruction load)
        {
            var p = load.Operand as ParameterDefinition;
            if (p != null) return p;
            string n = load.OpCode.Name;
            if (!n.StartsWith("ldarg.") || !char.IsDigit(n[n.Length - 1])) return null;
            int i = n[n.Length - 1] - '0' - (method.HasThis ? 1 : 0);
            return i < 0 ? null : method.Parameters[i];
        }

        static Instruction Copy(Instruction i)
        {
            Instruction c = Instruction.Create(OpCodes.Nop);
            c.OpCode = i.OpCode;
            c.Operand = i.Operand;
            return c;
        }

        // ---- string ----

        // Every `<a> ldflda _firstChar <b> ldflda _firstChar ceq brtrue T` whose T block stores 1 to the
        // loops' result local R and runs into their merge point M: `<a> <b> String.Equals; stloc R; br M`.
        static void StringEquals(MethodRepair r)
        {
            MethodBody body = r.Method.Body;
            var tests = body.Instructions.Where(i => i.OpCode == OpCodes.Ceq && IsFirstChar(i.Previous) && i.Next != null && i.Next.OpCode == OpCodes.Brtrue).ToList();
            if (tests.Count == 0) throw new RepairException("string: no `&a._firstChar == &b._firstChar` test for the markers' loops");
            MethodReference equals = null;
            foreach (Instruction ceq in tests)
            {
                Instruction right = ceq.Previous;
                List<Instruction> b = Expression(right.Previous);
                Instruction left = b == null ? null : b[0].Previous;
                List<Instruction> a = left != null && IsFirstChar(left) ? Expression(left.Previous) : null;
                string at = "string: the test at IL_" + ceq.Offset.ToString("x4");
                if (a == null) throw new RepairException(at + " compares something other than two strings' first characters");
                // string.Equals' length test just before it, on the same two strings:
                // <a> get_Length <b> get_Length ceq ldc.i4.0 ceq brtrue
                Instruction lenTest = Skip(a[0].Previous, -1);
                Instruction[] lt = lenTest == null ? null : Back(lenTest, 4);
                if (lenTest == null || lenTest.OpCode != OpCodes.Brtrue || lt == null || !IsLength(lt[0]) || lt[1].OpCode != OpCodes.Ceq
                    || Markers.ConstI4(lt[2]) != 0 || lt[3].OpCode != OpCodes.Ceq)
                    throw new RepairException(at + " isn't preceded by string.Equals' length test");
                List<Instruction> lb = Expression(lt[0].Previous);
                Instruction lenA = lb == null ? null : lb[0].Previous;
                List<Instruction> la = lenA != null && IsLength(lenA) ? Expression(lenA.Previous) : null;
                if (la == null || !Same(la, a) || !Same(lb, b))
                    throw new RepairException(at + ": the length test is on other strings");
                // T: the result of "equal" into a local (1, or 0 where the game uses a != b), then the merge
                Instruction t = Follow((Instruction)ceq.Next.Operand);
                int? equal = t == null ? null : Markers.ConstI4(t);
                if (equal == null || equal < 0 || equal > 1 || !t.Next.OpCode.Name.StartsWith("stloc"))
                    throw new RepairException(at + ": its \"same string\" branch doesn't store 1 or 0 to a result");
                VariableDefinition result = Fingerprint.Local(t.Next, body);
                Instruction merge = t.Next.Next;
                // the loops' last compare stores the same thing (the compare, or its negation) to the same local and joins
                bool negated = equal == 0;
                bool tail = body.Instructions.Any(i =>
                {
                    if (i.OpCode != OpCodes.Ceq || !Markers.IsMarkerCall(i.Previous) || !stringLoad.IsMatch((string)i.Previous.Previous.Operand)) return false;
                    Instruction st = negated && Markers.ConstI4(i.Next) == 0 && i.Next.Next.OpCode == OpCodes.Ceq ? i.Next.Next.Next : negated ? null : i.Next;
                    return st != null && st.OpCode.Name.StartsWith("stloc") && Fingerprint.Local(st, body) == result
                           && st.Next.OpCode == OpCodes.Br && Follow((Instruction)st.Next.Operand) == Follow(merge);
                });
                if (!tail) throw new RepairException(at + ": no compare loop stores its result to V" + result.Index + " the same way and joins it");
                if (equals == null) equals = new IL(r).ResolveMethod("System.String::Equals(String,String)");
                var added = b.Select(Copy).ToList();
                added.Add(Instruction.Create(OpCodes.Call, equals));
                if (negated) { added.Add(Instruction.Create(OpCodes.Ldc_I4_0)); added.Add(Instruction.Create(OpCodes.Ceq)); }
                added.Add(Instruction.Create(OpCodes.Stloc, result));
                added.Add(Instruction.Create(OpCodes.Br, merge));
                IList<Instruction> all = body.Instructions;
                Edit.ReplaceRange(body, all.IndexOf(left), all.IndexOf(ceq.Next), added, r.Key);
                r.Engine.SiteCount++;
            }
        }

        static bool IsFirstChar(Instruction i)
        {
            var f = i == null ? null : i.Operand as FieldReference;
            return i != null && i.OpCode == OpCodes.Ldflda && f != null && f.Name == "_firstChar" && f.DeclaringType.FullName == "System.String";
        }

        static bool IsLength(Instruction i)
        {
            var m = i.Operand as MethodReference;
            return (i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Call) && m != null && m.Name == "get_Length" && m.DeclaringType.FullName == "System.String";
        }

        // A side-effect-free push ending at last: a local, an argument, a string, a static field, or fields of those.
        static List<Instruction> Expression(Instruction last)
        {
            var list = new List<Instruction>();
            Instruction i = last;
            while (i != null && (i.OpCode == OpCodes.Ldfld))
            {
                list.Insert(0, i);
                i = i.Previous;
            }
            if (i == null) return null;
            if (!(IsSimpleLoad(i) || i.OpCode == OpCodes.Ldstr || i.OpCode == OpCodes.Ldsfld)) return null;
            list.Insert(0, i);
            return list;
        }

        static bool Same(List<Instruction> x, List<Instruction> y)
        {
            if (x.Count != y.Count) return false;
            for (int i = 0; i < x.Count; i++)
                if (x[i].OpCode.Code != y[i].OpCode.Code || !Equals(x[i].Operand, y[i].Operand)) return false;
            return true;
        }

        // From ins, past nops, forward (dir 1) or backward (-1).
        static Instruction Skip(Instruction ins, int dir)
        {
            while (ins != null && ins.OpCode == OpCodes.Nop) ins = dir > 0 ? ins.Next : ins.Previous;
            return ins;
        }

        // From ins, past nops and Rewind's trampolines (`nop; br X`), to the first instruction that does something.
        static Instruction Follow(Instruction ins)
        {
            for (int hops = 0; hops < 16; hops++)
            {
                ins = Skip(ins, 1);
                if (ins == null || ins.OpCode != OpCodes.Br) return ins;
                ins = (Instruction)ins.Operand;
            }
            return null;
        }

        // ---- box ----

        static bool IsBoxTest(Marker m)
        {
            Instruction a = m.Call.Next;
            return m.IsValue && m.Text == "__undefined()" && m.ValueType.FullName == "System.Int32" && Markers.ConstI4(a) == 0
                   && a.Next.OpCode == OpCodes.Clt && a.Next.Next.OpCode == OpCodes.Brtrue;
        }

        static bool IsMarkerAt(Instruction ins, string text, string valueType)
        {
            if (ins == null || ins.OpCode != OpCodes.Ldstr || (string)ins.Operand != text || ins.Next == null || !Markers.IsMarkerCall(ins.Next)) return false;
            var g = ins.Next.Operand as GenericInstanceMethod;
            return valueType == null ? g == null : g != null && g.GenericArguments[0].FullName == valueType;
        }

        static bool IsCall(Instruction i, string declaring, string name)
        {
            var m = i == null ? null : i.Operand as MethodReference;
            return i.OpCode == OpCodes.Call && m != null && m.Name == name && m.DeclaringType.FullName == declaring;
        }

        // From ins, a run with no branch and no marker, to `stloc R; br J`: (the stloc, J), or nulls.
        static Instruction StoreAndJoin(Instruction ins, out Instruction join)
        {
            join = null;
            for (int n = 0; ins != null && n < 40; ins = ins.Next, n++)
            {
                if (Markers.IsMarkerCall(ins)) return null;
                FlowControl f = ins.OpCode.FlowControl;
                if (f == FlowControl.Next || f == FlowControl.Call) continue;
                if (ins.OpCode != OpCodes.Br || ins.Previous == null || !ins.Previous.OpCode.Name.StartsWith("stloc")) return null;
                join = (Instruction)ins.Operand;
                return ins.Previous;
            }
            return null;
        }

        sealed class BoxBlock
        {
            public Marker Test;
            public VariableDefinition Result;
            public Instruction Join;
            public int Value;
            public TypeReference Type;

            public static BoxBlock Read(MethodRepair r, Marker m, KnownBits bits)
            {
                MethodBody body = r.Method.Body;
                string at = "box: the block at marker " + m.Index;
                Func<string, RepairException> no = what => new RepairException(at + ": " + what);
                Instruction branch = m.Call.Next.Next.Next;
                Instruction notRef = (Instruction)branch.Operand;
                // the reference-type path: *(object*)&slot, ending `stloc R; br J`
                Instruction join;
                Instruction store = StoreAndJoin(branch.Next, out join);
                if (store == null) throw no("its reference-type path isn't a straight store to a local and a join");
                VariableDefinition result = Fingerprint.Local(store, body);
                IList<Bits> stack = bits.StackBefore(store);
                if (stack == null || stack.Count == 0) throw no("its reference-type path is never reached in the dataflow");
                Bits v = stack[stack.Count - 1];
                if (!v.KnownLow32) throw no("the boxed value (the low half of *(object*)&slot) isn't known from the IL");
                // the nullable test: klass->nullable && !(flags & 8), then the null result
                Instruction n = Skip(notRef, 1);
                Instruction box;
                if (!IsMarkerAt(n, "__undefined()", "System.Int64")) throw no("no nullable test after the valuetype test");
                n = n.Next.Next;
                if (n.OpCode != OpCodes.Ldc_I8 || (long)n.Operand != 0 || n.Next.OpCode != OpCodes.Ceq || n.Next.Next.OpCode != OpCodes.Brtrue) throw no("the nullable test isn't `klass->nullable == 0`");
                box = (Instruction)n.Next.Next.Operand;
                n = Skip(n.Next.Next.Next, 1);
                if (!IsMarkerAt(n, "__undefined()", "System.Byte")) throw no("no nullable flag test");
                n = n.Next.Next;
                if (Markers.ConstI4(n) != 8 || n.Next.OpCode != OpCodes.And || n.Next.Next.OpCode != OpCodes.Conv_U1 || Markers.ConstI4(n.Next.Next.Next) != 0
                    || n.Next.Next.Next.Next.OpCode != OpCodes.Ceq)
                    throw no("the nullable flag test isn't `(flags & 8) == 0`");
                Instruction f = n.Next.Next.Next.Next.Next, nullPath;
                // `== 0` branches to the allocation and the null result follows, or `!= 0` branches to the null result
                if (f.OpCode == OpCodes.Brtrue && f.Operand == box) nullPath = f.Next;
                else if (Markers.ConstI4(f) == 0 && f.Next.OpCode == OpCodes.Ceq && f.Next.Next.OpCode == OpCodes.Brtrue && f.Next.Next.Next == box)
                    nullPath = (Instruction)f.Next.Next.Operand;
                else throw no("the nullable flag test doesn't branch to the allocation or the null result");
                Instruction nullJoin;
                Instruction nullStore = StoreAndJoin(nullPath, out nullJoin);
                if (nullStore == null || nullJoin != join || Fingerprint.Local(nullStore, body) != result) throw no("its null path doesn't store to the same local and join");
                // the allocation and the copy
                Instruction b = Skip(box, 1);
                if (b.OpCode != OpCodes.Ldtoken || !IsCall(b.Next, "System.Type", "GetTypeFromHandle") || !IsCall(b.Next.Next, "System.Runtime.Serialization.FormatterServices", "GetUninitializedObject")
                    || b.Next.Next.Next.OpCode != OpCodes.Castclass || !b.Next.Next.Next.Next.OpCode.Name.StartsWith("stloc"))
                    throw no("no allocation of the boxed object");
                var type = (TypeReference)b.Operand;
                if (type.FullName != "System.Int32" || ((TypeReference)b.Next.Next.Next.Operand).FullName != "System.Int32")
                    throw no("it boxes a " + type.FullName + ", not an Int32");
                VariableDefinition obj = Fingerprint.Local(b.Next.Next.Next.Next, body);
                Instruction copy = b.Next.Next.Next.Next.Next;
                if (copy.OpCode != OpCodes.Ldstr || !IsBoxText((string)copy.Operand) || ((string)copy.Operand).StartsWith("__undefined") || !Markers.IsMarkerCall(copy.Next))
                    throw no("no Memcpy of the value after the allocation");
                // the object into R, then the join (a write barrier for the copy may come between)
                // (out of line, after the barrier, in some methods: every such store must reach the join)
                var tails = body.Instructions.Where(i => i.OpCode.Name.StartsWith("ldloc") && Fingerprint.Local(i, body) == obj
                                                         && (i.Next.OpCode == OpCodes.Conv_I8 || i.Next.OpCode == OpCodes.Conv_U8)
                                                         && i.Next.Next.OpCode.Name.StartsWith("stloc") && Fingerprint.Local(i.Next.Next, body) == result)
                                            .Select(i => i.Next.Next).ToList();
                if (tails.Count == 0) throw no("the boxed object isn't stored to the block's local");
                foreach (Instruction tail in tails)
                {
                    Instruction after = tail.Next;
                    if (!(Skip(after, 1) == Skip(join, 1) || after.OpCode == OpCodes.Br && after.Operand == join)) throw no("the boxed object's store doesn't reach the join");
                }
                return new BoxBlock { Test = m, Result = result, Join = join, Value = v.Low32, Type = type };
            }

            // The valuetype test becomes the box; the rest of the block dies.
            public void Replace(MethodRepair r)
            {
                TypeReference type = r.Engine.Module.ImportReference(Type);
                int value = Value;
                VariableDefinition result = Result;
                Instruction join = Join;
                r.SiteOf(Test).Replace(0, 3, il => il.I4(value).Emit(Instruction.Create(OpCodes.Box, type)).Stloc(result).Br(join));
            }
        }

        // Rewind typed the block's local as an int64; with the block gone it holds only the box, and is
        // read only to store it into an object array.
        static void RetypeBoxed(MethodRepair r, VariableDefinition v)
        {
            MethodBody body = r.Method.Body;
            foreach (Instruction i in body.Instructions)
            {
                if (Fingerprint.Local(i, body) != v && i.Operand != v) continue;
                string n = i.OpCode.Name;
                if (n.StartsWith("stloc") && i.Previous.OpCode == OpCodes.Box) continue;
                if (n.StartsWith("ldloc") && !n.StartsWith("ldloca") && ConsumedAsObject(i)) continue;
                throw new RepairException("box: V" + v.Index + " is used other than as the boxed object (IL_" + i.Offset.ToString("x4") + " " + n + ")");
            }
            v.VariableType = r.Engine.Module.TypeSystem.Object;
        }

        // Follows the value ins pushes, in straight-line code, to the instruction that pops it: true when
        // that takes an object (an object array's element, an Object parameter, field or local).
        static bool ConsumedAsObject(Instruction ins)
        {
            int depth = 1;  // the value is the depth-th from the top
            for (Instruction i = ins.Next; i != null; i = i.Next)
            {
                if (i.OpCode == OpCodes.Ret || i.OpCode == OpCodes.Dup) return false;
                int pops = StackCheck.Pops(i, null, false);
                if (pops >= depth)
                {
                    int operand = pops - depth;  // which of its operands, from the left
                    var m = i.Operand as MethodReference;
                    if (i.OpCode == OpCodes.Stelem_Ref || i.OpCode == OpCodes.Stelem_Any && ((TypeReference)i.Operand).FullName == "System.Object")
                        return operand == 2;
                    if (m != null && (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Newobj))
                    {
                        int p = operand - (m.HasThis && i.OpCode != OpCodes.Newobj ? 1 : 0);
                        return p >= 0 && m.Parameters[p].ParameterType.FullName == "System.Object";
                    }
                    var f = i.Operand as FieldReference;
                    if (f != null && (i.OpCode == OpCodes.Stsfld || i.OpCode == OpCodes.Stfld && operand == 1)) return f.FieldType.FullName == "System.Object";
                    return false;
                }
                depth += StackCheck.Pushes(i) - pops;
                FlowControl fc = i.OpCode.FlowControl;
                if (fc != FlowControl.Next && fc != FlowControl.Call) return false;
            }
            return false;
        }
    }
}
