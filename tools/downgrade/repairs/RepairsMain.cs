using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Repairs;

// The engine on its own, for writing and checking rules; the pipeline runs Engine.Run inside the rewriter.
//
//   Repairs apply <Assembly-CSharp.dll> <out.dll> [--only <rule>[,<rule>...]] [--pin]
//       Applies the rules and writes the result. Errors (stale or drifted rules, markers left, a bad
//       stack) fail it with exit code 1 and nothing written. --pin also accepts rules without hashes
//       and prints the hashes to paste in.
//   Repairs markers <Assembly-CSharp.dll> <filter>
//       Every marker of the methods whose name contains filter, with its index, kind, IL offset and
//       context hash, one per line (native/listing.py reads this), plus each method's body hash.
//   Repairs gaps <Assembly-CSharp.dll>
//       Every method with markers left: name, marker count.
static class RepairsMain
{
    static int Main(string[] args)
    {
        if (args.Length < 2) return Usage();
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1])));
        var read = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate, InMemory = true };
        ModuleDefinition module = ModuleDefinition.ReadModule(args[1], read);
        switch (args[0])
        {
            case "markers":
                foreach (MethodDefinition m in module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody && Engine.Name(m).Contains(args[2])))
                {
                    var markers = Markers.Find(m);
                    if (markers.Count == 0) continue;
                    Cleanup.LongBranches(m.Body);
                    Console.WriteLine(Engine.Name(m) + "\t-\tmethod\t-\thash " + Fingerprint.Of(m));
                    foreach (Marker k in markers)
                        Console.WriteLine(Engine.Name(m) + "\t" + k.Index + "\t" + k.Kind + "\t" + k.Ldstr.Offset.ToString("x4") + "\t" + k.Text + "\t" + Fingerprint.Of(k, m.Body));
                }
                return 0;
            case "gaps":
                foreach (MethodDefinition m in module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
                {
                    int n = Markers.Find(m).Count;
                    if (n > 0) Console.WriteLine(Engine.Name(m) + "\t" + n);
                }
                return 0;
            case "apply":
                if (args.Length < 3) return Usage();
                bool pin = args.Contains("--pin");
                int o = Array.IndexOf(args, "--only");
                Func<string, bool> only = null;
                if (o > 0)
                {
                    string[] names = args[o + 1].Split(',');
                    only = n => names.Contains(n);
                }
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Engine engine = Engine.Run(module, pin ? Mode.Pin : Mode.Strict, only);
                foreach (string p in engine.Pins) Console.WriteLine("PIN " + p);
                foreach (string w in engine.Warnings) Console.WriteLine("WARNING " + w);
                foreach (string e in engine.Errors) Console.WriteLine("ERROR " + e);
                Console.WriteLine(engine.RuleCount + " rules, " + engine.SiteCount + " sites, " + engine.Errors.Count + " errors, " + watch.ElapsedMilliseconds + " ms");
                if (engine.Errors.Count > 0 || (engine.Pins.Count > 0 && !pin)) return 1;
                if (engine.Pins.Count > 0) Console.WriteLine("(pin mode: written, but rules without hashes must get them before a build accepts them)");
                module.Write(args[2]);
                return 0;
        }
        return Usage();
    }

    static int Usage()
    {
        Console.WriteLine("usage: Repairs apply <in.dll> <out.dll> [--only r1,r2] [--pin] | markers <dll> <filter> | gaps <dll>");
        return 2;
    }
}
