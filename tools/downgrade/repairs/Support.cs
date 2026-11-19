using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Helpers the engine adds to the repaired assembly, made here so they hold nothing of the game's:
// __Repairs.Bits.ToDouble(long), the reinterpretation the game's stripped mscorlib lacks
// (BitConverter.Int64BitsToDouble), through a struct with a long and a double at offset 0, which
// ILVerify accepts (unlike ldloca of a long read back with ldind.r8).
namespace Repairs
{
    public static class Support
    {
        public static MethodReference BitsToDouble(ModuleDefinition module)
        {
            TypeDefinition bits = module.GetType("__Repairs.Bits");
            if (bits == null) bits = MakeBits(module);
            return bits.Methods.Single(m => m.Name == "ToDouble");
        }

        static TypeDefinition MakeBits(ModuleDefinition module)
        {
            // in the scope every other type reference uses (Rewind's assemblies name mscorlib twice)
            var valueType = new TypeReference("System", "ValueType", module, module.TypeSystem.Object.Scope);
            var union = new TypeDefinition("__Repairs", "BitsUnion",
                TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.ExplicitLayout | TypeAttributes.BeforeFieldInit, valueType);
            var asLong = new FieldDefinition("l", FieldAttributes.Public, module.TypeSystem.Int64) { Offset = 0 };
            var asDouble = new FieldDefinition("d", FieldAttributes.Public, module.TypeSystem.Double) { Offset = 0 };
            union.Fields.Add(asLong);
            union.Fields.Add(asDouble);
            module.Types.Add(union);

            var bits = new TypeDefinition("__Repairs", "Bits",
                TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, module.TypeSystem.Object);
            var toDouble = new MethodDefinition("ToDouble", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Double);
            toDouble.Parameters.Add(new ParameterDefinition("bits", ParameterAttributes.None, module.TypeSystem.Int64));
            var u = new VariableDefinition(union);
            toDouble.Body.Variables.Add(u);
            toDouble.Body.InitLocals = true;
            ILProcessor il = toDouble.Body.GetILProcessor();
            il.Emit(OpCodes.Ldloca, u);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Stfld, asLong);
            il.Emit(OpCodes.Ldloca, u);
            il.Emit(OpCodes.Ldfld, asDouble);
            il.Emit(OpCodes.Ret);
            bits.Methods.Add(toDouble);
            module.Types.Add(bits);
            return bits;
        }
    }
}
