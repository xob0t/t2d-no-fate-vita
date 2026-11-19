using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

// The few members of the rewriter (DowngradeAssemblies, butcher-vita's and this pipeline's) that
// compat/rewriter/PlatformPasses.cs uses, so the checker can run the platform pass without the pipeline. Same names
// and signatures as the rewriter's; the checker compiles this instead of DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    internal static readonly Dictionary<string, TypeDefinition> compat = new Dictionary<string, TypeDefinition>();
    internal static readonly List<string> problems = new List<string>();
    internal static readonly Dictionary<string, int> matches = new Dictionary<string, int>();

    static void Count(string rule, int sites)
    {
        int sum;
        matches.TryGetValue(rule, out sum);
        matches[rule] = sum + sites;
    }

    static MethodReference CompatMethod(ModuleDefinition module, string type, string name, params TypeReference[] typeArguments)
    {
        MethodReference method = module.ImportReference(compat["T2DCompat." + type].Methods.Single(m => m.Name == name));
        if (typeArguments.Length == 0) return method;
        var instance = new GenericInstanceMethod(method);
        foreach (TypeReference argument in typeArguments) instance.GenericArguments.Add(argument);
        return instance;
    }

    static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (TypeDefinition type in types)
        {
            yield return type;
            foreach (TypeDefinition nested in AllTypes(type.NestedTypes)) yield return nested;
        }
    }

    // The checker's way in.
    internal static int RunPlatformPatches(ModuleDefinition module)
    {
        return PlatformPatches(module);
    }
}
