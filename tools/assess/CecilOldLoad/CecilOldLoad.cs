// Loads Rewind's assemblies with the Mono.Cecil huntdown-vita's rewriter uses (0.10.0-beta6), the way that
// rewriter is built and run: C# 5, .NET Framework csc, .NET Framework runtime, Mono.Cecil.dll beside the exe.
//
//   CecilOldLoad <assemblies dir> <out dir> <assembly>...
//
// Per assembly: reads it (immediate mode, a resolver on the assemblies dir), reads every method body,
// resolves every type/member reference (signatures, locals, operands, typeref and memberref tables),
// writes it to <out dir> and reads the copy back. Never writes next to its inputs.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class CecilOldLoad
{
    static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: CecilOldLoad <assemblies dir> <out dir> <assembly>...");
            return 2;
        }
        Console.WriteLine("Mono.Cecil " + typeof(ModuleDefinition).Assembly.GetName().Version + " on CLR " + Environment.Version);
        int rc = 0;
        for (int i = 2; i < args.Length; i++)
            if (Check(args[0], args[1], args[i]) != 0) rc = 1;
        return rc;
    }

    static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition m)
    {
        var stack = new Stack<TypeDefinition>(m.Types.Reverse());
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            yield return t;
            foreach (var n in t.NestedTypes.Reverse()) stack.Push(n);
        }
    }

    static int Check(string dir, string outDir, string name)
    {
        var bad = new List<string>();
        int unresolved = 0, readErrors = 0, types = 0, methods = 0, bodies = 0, instrs = 0;
        Action<string> fail = s => { unresolved++; if (bad.Count < 30) bad.Add(s); };
        string path = Path.Combine(dir, name + ".dll");
        Directory.CreateDirectory(outDir);
        string outPath = Path.Combine(outDir, name + ".dll");
        ModuleDefinition m;
        try
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(dir);
            m = ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate });
        }
        catch (Exception e)
        {
            Console.WriteLine(name + ": READ FAILED: " + e);
            return 1;
        }
        using (m)
        {
            foreach (var t in AllTypes(m))
            {
                types++;
                if (!ResolvesType(t.BaseType)) fail(t.FullName + ": base " + t.BaseType);
                foreach (var it in t.Interfaces) if (!ResolvesType(it.InterfaceType)) fail(t.FullName + ": interface " + it.InterfaceType);
                foreach (var f in t.Fields) if (!ResolvesType(f.FieldType)) fail(f.FullName + ": field type");
                foreach (var ca in t.CustomAttributes) if (!ResolvesMember(ca.Constructor)) fail(t.FullName + ": attribute " + ca.Constructor);
                foreach (var md in t.Methods)
                {
                    methods++;
                    if (!ResolvesType(md.ReturnType) || md.Parameters.Any(p => !ResolvesType(p.ParameterType))) fail(md.FullName + ": signature");
                    foreach (var ov in md.Overrides) if (!ResolvesMember(ov)) fail(md.FullName + ": override " + ov);
                    if (!md.HasBody) continue;
                    MethodBody body;
                    try { body = md.Body; bodies++; }
                    catch (Exception e) { readErrors++; if (bad.Count < 30) bad.Add(md.FullName + ": body read error " + e.Message); continue; }
                    foreach (var v in body.Variables) if (!ResolvesType(v.VariableType)) fail(md.FullName + ": local " + v.VariableType);
                    foreach (var ins in body.Instructions)
                    {
                        instrs++;
                        var op = ins.Operand;
                        bool ok = true;
                        if (op is MethodReference || op is FieldReference) ok = ResolvesMember((MemberReference)op);
                        else if (op is TypeReference) ok = ResolvesType((TypeReference)op);
                        else if (op is CallSite) ok = ResolvesType(((CallSite)op).ReturnType);
                        if (!ok) fail(md.FullName + ": " + ins.OpCode + " " + op);
                    }
                }
            }
            foreach (var tr in m.GetTypeReferences()) if (!ResolvesType(tr)) fail("typeref " + tr.FullName);
            foreach (var mr in m.GetMemberReferences()) if (!ResolvesMember(mr)) fail("memberref " + mr.FullName);
            Console.WriteLine(string.Format("{0}: {1} types, {2} methods, {3} bodies, {4} instructions; {5} unresolved, {6} body read errors",
                name, types, methods, bodies, instrs, unresolved, readErrors));
            foreach (var b in bad) Console.WriteLine("  " + b);
            try { m.Write(outPath); }
            catch (Exception e)
            {
                Console.WriteLine(name + ": WRITE FAILED: " + e);
                return 1;
            }
        }
        // Read the written copy back.
        try
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(dir);
            using (var m2 = ModuleDefinition.ReadModule(outPath, new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate }))
            {
                int instrs2 = AllTypes(m2).SelectMany(t => t.Methods).Where(x => x.HasBody).Sum(x => x.Body.Instructions.Count);
                Console.WriteLine(string.Format("  round trip: wrote {0} ({1} bytes, input {2}); re-read {3} instructions ({4})",
                    outPath, new FileInfo(outPath).Length, new FileInfo(path).Length, instrs2, instrs2 == instrs ? "same" : "DIFFERENT"));
                if (instrs2 != instrs) return 1;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(name + ": RE-READ FAILED: " + e);
            return 1;
        }
        return unresolved == 0 && readErrors == 0 ? 0 : 1;
    }

    static bool ResolvesType(TypeReference t)
    {
        if (t == null || t is GenericParameter) return true;
        var git = t as GenericInstanceType;
        if (git != null) return ResolvesType(git.ElementType) && git.GenericArguments.All(ResolvesType);
        var rm = t as RequiredModifierType;
        if (rm != null) return ResolvesType(rm.ElementType) && ResolvesType(rm.ModifierType);
        var om = t as OptionalModifierType;
        if (om != null) return ResolvesType(om.ElementType) && ResolvesType(om.ModifierType);
        var fp = t as FunctionPointerType;
        if (fp != null) return ResolvesType(fp.ReturnType) && fp.Parameters.All(p => ResolvesType(p.ParameterType));
        var ts = t as TypeSpecification;
        if (ts != null) return ResolvesType(ts.ElementType);
        try { return t.Resolve() != null; } catch { return false; }
    }

    static bool ResolvesMember(MemberReference mr)
    {
        if (!ResolvesType(mr.DeclaringType)) return false;
        var gim = mr as GenericInstanceMethod;
        if (gim != null && !gim.GenericArguments.All(ResolvesType)) return false;
        if (mr.DeclaringType is ArrayType) return true; // runtime-provided accessors
        try
        {
            var m = mr as MethodReference;
            if (m != null) return m.Resolve() != null;
            var f = mr as FieldReference;
            if (f != null) return f.Resolve() != null;
            return true;
        }
        catch { return false; }
    }
}
