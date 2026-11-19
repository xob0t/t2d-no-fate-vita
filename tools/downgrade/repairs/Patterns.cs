using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Pattern repairs: the same edit wherever a marker has a known shape, with no game method named.
// They run after the per-method rules, on the methods no rule took, and repair a method only when
// it then has no marker left (a method half repaired by a pattern would hide its gaps from the
// rule that has to finish it). These are the fixes that belong in Rewind; they live here until
// Rewind makes them, and drop out on their own when it does (no marker, no match).
namespace Repairs
{
    public static class Patterns
    {
        public static int WriteBarriers(Engine engine)
        {
            int methods = 0;
            foreach (MethodDefinition method in engine.Module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).ToList())
            {
                if (engine.Taken(method)) continue;
                List<Marker> markers = Markers.Find(method);
                if (markers.Count == 0 || !markers.Any(m => IsFlagTest(m))) continue;
                if (!markers.All(m => IsFlagTest(m) || IsBarrierPart(m))) continue;
                // try it on the method; keep it only if no marker is left
                MethodRepair repair = engine.Pattern(method);
                foreach (Marker m in markers.Where(IsFlagTest)) repair.Site(m.Text, 0, null, true).SkipBarrier();
                methods++;
            }
            return methods;
        }

        static bool IsFlagTest(Marker m)
        {
            return m.IsValue && m.Text.StartsWith("__load4(") && m.Text.EndsWith("L)") && m.Text.IndexOf('(', 1) == m.Text.LastIndexOf('(')
                   && Markers.ConstI4(m.Call.Next) == 0 && m.Call.Next.Next.OpCode == OpCodes.Ceq && (m.Call.Next.Next.Next.OpCode == OpCodes.Brtrue || m.Call.Next.Next.Next.OpCode == OpCodes.Brtrue_S);
        }

        static bool IsBarrierPart(Marker m)
        {
            string t = Fingerprint.Text(m.Text);
            return t == "__statics()" || t.StartsWith("__load8(") || t.StartsWith("__atomic_compare_exchange(");
        }
    }
}
