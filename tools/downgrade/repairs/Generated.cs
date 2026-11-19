using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Repairs whose content is read from the user's GameAssembly.dll at build time (native/array_facts.py)
// instead of written in a rule: the asset-table initializers, thousands of sites of one shape.
// Their rules name the method and pin its body hash, strictly (any change to the method fails the
// run: regenerate the facts and re-pin); the sites themselves are checked against the native facts
// (count, order, length) instead of a hash each.
namespace Repairs
{
    public sealed class NativeFacts
    {
        // method ("Namespace.Type::name") -> allocations in address order: class, length (or null)
        readonly Dictionary<string, List<KeyValuePair<string, int?>>> arrays = new Dictionary<string, List<KeyValuePair<string, int?>>>();

        public static NativeFacts Load(string path)
        {
            var f = new NativeFacts();
            foreach (string line in File.ReadAllLines(path))
            {
                string[] c = line.Split('\t');
                if (c.Length < 5) continue;
                List<KeyValuePair<string, int?>> list;
                if (!f.arrays.TryGetValue(c[0], out list)) f.arrays[c[0]] = list = new List<KeyValuePair<string, int?>>();
                list.Add(new KeyValuePair<string, int?>(c[3], c[4].Length == 0 ? (int?)null : int.Parse(c[4])));
            }
            return f;
        }

        public List<KeyValuePair<string, int?>> Arrays(string method)
        {
            List<KeyValuePair<string, int?>> list;
            return arrays.TryGetValue(method, out list) ? list : null;
        }

        // "int[][]" (the metadata's name for the array class) -> "System.Int32[][]"
        public static string ClrName(string name)
        {
            int dims = 0;
            while (name.EndsWith("[]"))
            {
                name = name.Substring(0, name.Length - 2);
                dims++;
            }
            switch (name)
            {
                case "int": name = "System.Int32"; break;
                case "long": name = "System.Int64"; break;
                case "double": name = "System.Double"; break;
                case "float": name = "System.Single"; break;
                case "bool": name = "System.Boolean"; break;
                case "byte": name = "System.Byte"; break;
                case "sbyte": name = "System.SByte"; break;
                case "short": name = "System.Int16"; break;
                case "ushort": name = "System.UInt16"; break;
                case "uint": name = "System.UInt32"; break;
                case "ulong": name = "System.UInt64"; break;
                case "char": name = "System.Char"; break;
                case "string": name = "System.String"; break;
                case "object": name = "System.Object"; break;
            }
            for (int i = 0; i < dims; i++) name += "[]";
            return name;
        }
    }

    public static class GeneratedRepairs
    {
        public const string Alloc = "__native_0x1800E2FD0(";

        // Every Array::NewSpecific marker of the method becomes new T[n], T from the native facts; the
        // int64 local Rewind stores the array in gets the array's type.
        public static MethodRepair NewArraysFromNative(this MethodRepair m)
        {
            if (m.Method == null) return m;
            NativeFacts facts = m.Engine.Facts;
            if (facts == null) throw new RepairException(m.Key + ": no native facts (apply --facts <array-facts.tsv>)");
            string key = m.Key.Substring(0, m.Key.IndexOf('('));
            List<KeyValuePair<string, int?>> arrays = facts.Arrays(key);
            int markers = Markers.Find(m.Method).Count(k => Fingerprint.Text(k.Text).StartsWith(Fingerprint.Text(Alloc)));
            if (arrays == null || arrays.Count != markers)
                throw new RepairException(m.Key + ": " + markers + " allocation markers, " + (arrays == null ? 0 : arrays.Count) + " native allocations");
            for (int i = 0; i < markers; i++)
            {
                Site s = m.Site(Alloc, 0, null, true);
                long n;
                bool literal = long.TryParse(s.Marker.Args.Length > 1 ? s.Marker.Args[1].TrimEnd('L') : "", out n);
                KeyValuePair<string, int?> fact = arrays[i];
                if (fact.Key.Length == 0 || !fact.Key.EndsWith("[]"))
                    throw new RepairException(m.Key + ": allocation " + i + " has no array class in the native code");
                if (literal && fact.Value != null && fact.Value.Value != n)
                    throw new RepairException(m.Key + ": allocation " + i + " is " + n + " long in Rewind's IL and " + fact.Value + " in the native code");
                string array = NativeFacts.ClrName(fact.Key);
                Instruction next = s.Last.Next;
                string element = array.Substring(0, array.Length - 2);
                if (literal) s.NewArray(element);
                else
                {
                    // a length the code computes (a constant field, another table's length): the marker's own text
                    Marker k = s.Marker;
                    s.With(il => { ExprCompiler.CompileArg(m, k, 1, il, Kind.I4); il.Newarr(element); });
                }
                VariableDefinition v = next.Operand as VariableDefinition ?? Fingerprint.Local(next, m.Method.Body);
                if (next.OpCode.Name.StartsWith("stloc") && v != null && v.VariableType.FullName == "System.Int64")
                    m.RetypeLocal(v.Index, array);
            }
            return m;
        }

        // Every write barrier of the method skipped (Site.SkipBarrier).
        public static MethodRepair SkipBarriers(this MethodRepair m, int count)
        {
            for (int i = 0; i < count; i++) m.Site("__load4(", 0, null, true).SkipBarrier();
            return m;
        }

        // tN is local N for every local (Rewind numbers an initializer's temporaries so; a wrong
        // binding fails the compile's type checks).
        public static MethodRepair BindAllSame(this MethodRepair m)
        {
            if (m.Method == null) return m;
            foreach (VariableDefinition v in m.Method.Body.Variables) m.Bind("t" + v.Index, v.Index);
            return m;
        }

        // Compiles every remaining marker that starts with prefix; count is how many.
        public static MethodRepair CompileGenerated(this MethodRepair m, string prefix, int count)
        {
            for (int i = 0; i < count; i++) m.Site(prefix, 0, null, true).Compile();
            return m;
        }
    }
}
