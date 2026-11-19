using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Rewind marks what it could not recover with calls to its own __Rewind class, which it adds to each
// assembly: Opaque<T>("...") for a value, Effect("...") for a statement (both throw
// NotSupportedException when they run), and Note("...") at the start of a method it reports
// incomplete. This pass changes nothing: it prints one line per method that still has a marker,
//   REWIND GAP <assembly> <method> sites=<n>
//   REWIND NOTE <assembly> <method>             (a Note without a marker: docs/rewind-issues.md, issue 6)
// for pipeline.py's code stage to count. A method that still has a marker throws when it reaches it,
// so these are the repairs left (docs/assessment.md, "The gaps in Assembly-CSharp").
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    const string RewindMarkers = "__Rewind";

    static int ListRewindGaps(ModuleDefinition module)
    {
        int methods = 0;
        foreach (TypeDefinition type in AllTypes(module.Types))
        {
            if (type.Name == RewindMarkers && type.DeclaringType == null) continue;
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
            {
                int sites = 0;
                bool noted = false;
                foreach (Instruction instruction in method.Body.Instructions)
                {
                    var called = instruction.Operand as MethodReference;
                    if (called == null || called.DeclaringType.Name != RewindMarkers) continue;
                    if (called.Name == "Note") noted = true;
                    else sites++;
                }
                if (sites > 0)
                {
                    System.Console.WriteLine("REWIND GAP " + module.Assembly.Name.Name + " " + method.FullName + " sites=" + sites);
                    methods++;
                }
                else if (noted)
                    System.Console.WriteLine("REWIND NOTE " + module.Assembly.Name.Name + " " + method.FullName);
            }
        }
        return methods;
    }
}
