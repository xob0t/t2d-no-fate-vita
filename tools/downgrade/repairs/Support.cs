using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Helpers the engine adds to the repaired assembly, made here so they hold nothing of the game's:
// __Repairs.Bits.ToDouble(long), the reinterpretation the game's stripped mscorlib lacks
// (BitConverter.Int64BitsToDouble), through a struct with a long and a double at offset 0, which
// ILVerify accepts (unlike ldloca of a long read back with ldind.r8); and FromDouble(double), the
// other way (a double read as raw bits, as Rewind types an unboxed Float it loaded with __load8).
namespace Repairs
{
    public static class Support
    {
        public static MethodReference BitsToDouble(ModuleDefinition module)
        {
            return BitsMethod(module, "ToDouble");
        }

        public static MethodReference DoubleToBits(ModuleDefinition module)
        {
            return BitsMethod(module, "FromDouble");
        }

        static MethodReference BitsMethod(ModuleDefinition module, string name)
        {
            TypeDefinition bits = module.GetType("__Repairs.Bits");
            if (bits == null) bits = MakeBits(module);
            return bits.Methods.Single(m => m.Name == name);
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

            var fromDouble = new MethodDefinition("FromDouble", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Int64);
            fromDouble.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, module.TypeSystem.Double));
            var w = new VariableDefinition(union);
            fromDouble.Body.Variables.Add(w);
            fromDouble.Body.InitLocals = true;
            il = fromDouble.Body.GetILProcessor();
            il.Emit(OpCodes.Ldloca, w);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Stfld, asDouble);
            il.Emit(OpCodes.Ldloca, w);
            il.Emit(OpCodes.Ldfld, asLong);
            il.Emit(OpCodes.Ret);
            bits.Methods.Add(fromDouble);
            module.Types.Add(bits);
            return bits;
        }
    }
}
