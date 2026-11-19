using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// A diagnostic build's pass, under T2D_TRACE=1 (common.BUILD_SWITCHES): every method of the game's assembly starts
// with T2DCompat.Trace.Enter("<Type>::<method>"), so the player's log can say which methods ran before an exception
// (compat/Trace.cs; the PS Vita player's exceptions have no frames on a retail console). A normal build doesn't run it.
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    static int TraceMethods(ModuleDefinition module)
    {
        MethodReference enter = CompatMethod(module, "Trace", "Enter");
        int methods = 0;
        foreach (TypeDefinition type in AllTypes(module.Types).ToList())
        {
            if (type.Name == RewindMarkers) continue;
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
            {
                ILProcessor il = method.Body.GetILProcessor();
                Instruction first = method.Body.Instructions[0];
                il.InsertBefore(first, il.Create(OpCodes.Ldstr, type.Name + "::" + method.Name));
                il.InsertBefore(first, il.Create(OpCodes.Call, enter));
                methods++;
            }
        }
        return methods;
    }
}
