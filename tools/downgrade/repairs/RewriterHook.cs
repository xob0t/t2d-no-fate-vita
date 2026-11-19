using System;
using Mono.Cecil;

// The repair engine as a step of the assembly rewriter (DowngradeAssemblies.cs), compiled into it with
// the engine and the rules (pipeline.REWRITER_SOURCES lists them; see docs/repairs.md, "In the rewriter").
// Rewrite() calls RepairRewindGaps first, before the assembly is renamed (Recovered.T2D) and its type
// references retargeted: rules key Rewind's own method names, and their hashes cover the IL as Rewind
// wrote it. The other passes then see repaired bodies; RewindGaps.cs counts what is left.
//
// Prints one line per rule problem (REPAIR ERROR ...) and per warning (REPAIR WARNING ...), adds each
// error to the rewriter's problems, and returns the stats fragment for the assembly's line.
// T2D_REPAIRS=0 skips it (to see Rewind's output as it is); T2D_REPAIR_PATTERNS=0 skips Patterns.cs.
partial class DowngradeAssemblies
{
    const string RepairedAssembly = "Assembly-CSharp";

    static string RepairRewindGaps(AssemblyDefinition assembly)
    {
        if (assembly.Name.Name != RepairedAssembly) return "";
        if (Environment.GetEnvironmentVariable("T2D_REPAIRS") == "0") return " repairs=off";
        bool patterns = Environment.GetEnvironmentVariable("T2D_REPAIR_PATTERNS") != "0";
        Repairs.Engine engine = Repairs.Engine.Run(assembly.MainModule, Repairs.Mode.Strict, null, patterns);
        foreach (string pin in engine.Pins) problems.Add(assembly.Name.Name + ": repair: unpinned rule: " + pin);
        foreach (string warning in engine.Warnings) Console.WriteLine("REPAIR WARNING " + warning);
        foreach (string error in engine.Errors)
        {
            Console.WriteLine("REPAIR ERROR " + error);
            problems.Add(assembly.Name.Name + ": repair: " + error);
        }
        Count("pass RepairRewindGaps", engine.RuleCount);
        return " repairRules=" + engine.RuleCount + " repairSites=" + engine.SiteCount + " repairErrors=" + engine.Errors.Count +
               " barrierPatterns=" + engine.PatternsKept;
    }
}
