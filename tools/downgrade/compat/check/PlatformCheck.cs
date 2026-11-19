using System;
using System.Linq;

// PlatformCheck: the platform layer's checks without the pipeline. C# 5 and Mono.Cecil 0.10.0-beta6, compiled with the
// .NET Framework's csc as butcher-vita's rewriter is (check/build.sh).
//
//   PlatformCheck.exe inventory <assembly.dll> <out.tsv> <type prefix>...     where the game uses an API (Inventory.cs)
//   PlatformCheck.exe rules <rewind assemblies dir> <compat.dll> <rules.txt>  every rule's target and replacement (Rules.cs)
static partial class PlatformCheck
{
    static int Main(string[] args)
    {
        string command = args.Length > 0 ? args[0] : "";
        string[] rest = args.Skip(1).ToArray();
        switch (command)
        {
            case "inventory": return Inventory(rest);
            case "rules": return Rules(rest);
            default:
                Console.Error.WriteLine("usage: PlatformCheck.exe inventory|rules ...");
                return 2;
        }
    }
}
