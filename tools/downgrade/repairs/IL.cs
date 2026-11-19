using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The builder rules write their IL with. Members are named as in the IL listings:
// "Namespace.Type::name(Int32,Double)" for a method (parameter types by simple name, the overload
// must exist), "Namespace.Type::field" for a field. Every call returns the builder, so a repair
// reads as one line of IL per call.
namespace Repairs
{
    public sealed class IL
    {
        readonly MethodRepair repair;
        public readonly List<Instruction> Instructions = new List<Instruction>();

        internal IL(MethodRepair repair)
        {
            this.repair = repair;
        }

        ModuleDefinition Module { get { return repair.Engine.Module; } }
        MethodDefinition Method { get { return repair.Method; } }

        public IL Op(OpCode op)
        {
            Instructions.Add(Instruction.Create(op));
            return this;
        }

        public IL Emit(Instruction ins)
        {
            Instructions.Add(ins);
            return this;
        }

        public IL This() { return Op(OpCodes.Ldarg_0); }

        // Argument by its index in the signature (0 is the first declared parameter, not this).
        public IL Arg(int index) { return Emit(LdargFor(Method, index)); }

        public static Instruction LdargFor(MethodDefinition method, int index)
        {
            return Instruction.Create(OpCodes.Ldarg, method.Parameters[index]);
        }

        public IL Ldloc(int index) { return Emit(Instruction.Create(OpCodes.Ldloc, Method.Body.Variables[index])); }
        public IL Ldloc(VariableDefinition v) { return Emit(Instruction.Create(OpCodes.Ldloc, v)); }
        public IL Stloc(int index) { return Emit(Instruction.Create(OpCodes.Stloc, Method.Body.Variables[index])); }
        public IL Stloc(VariableDefinition v) { return Emit(Instruction.Create(OpCodes.Stloc, v)); }
        public IL I4(int value) { return Emit(Instruction.Create(OpCodes.Ldc_I4, value)); }
        public IL I8(long value) { return Emit(Instruction.Create(OpCodes.Ldc_I8, value)); }
        public IL R8(double value) { return Emit(Instruction.Create(OpCodes.Ldc_R8, value)); }
        public IL R4(float value) { return Emit(Instruction.Create(OpCodes.Ldc_R4, value)); }
        public IL Null() { return Op(OpCodes.Ldnull); }
        public IL Pop() { return Op(OpCodes.Pop); }
        public IL Dup() { return Op(OpCodes.Dup); }

        public IL Call(string method) { return Emit(Instruction.Create(OpCodes.Call, ResolveMethod(method))); }
        public IL Callvirt(string method) { return Emit(Instruction.Create(OpCodes.Callvirt, ResolveMethod(method))); }
        public IL Newobj(string ctor) { return Emit(Instruction.Create(OpCodes.Newobj, ResolveMethod(ctor))); }

        public IL Ldfld(string field) { return Emit(Instruction.Create(OpCodes.Ldfld, ResolveField(field))); }
        public IL Stfld(string field) { return Emit(Instruction.Create(OpCodes.Stfld, ResolveField(field))); }
        public IL Ldsfld(string field) { return Emit(Instruction.Create(OpCodes.Ldsfld, ResolveField(field))); }
        public IL Stsfld(string field) { return Emit(Instruction.Create(OpCodes.Stsfld, ResolveField(field))); }

        public IL Newarr(string elementType) { return Emit(Instruction.Create(OpCodes.Newarr, Module.ImportReference(Types.Parse(Module, elementType)))); }
        public IL Castclass(string type) { return Emit(Instruction.Create(OpCodes.Castclass, Module.ImportReference(Types.Parse(Module, type)))); }

        public IL Br(Instruction target) { return Emit(Instruction.Create(OpCodes.Br, target)); }
        public IL Brtrue(Instruction target) { return Emit(Instruction.Create(OpCodes.Brtrue, target)); }
        public IL Brfalse(Instruction target) { return Emit(Instruction.Create(OpCodes.Brfalse, target)); }

        // A label: a nop placed now, for branches inside the replacement.
        public Instruction Label()
        {
            Instruction nop = Instruction.Create(OpCodes.Nop);
            Instructions.Add(nop);
            return nop;
        }

        // A label placed later with Place.
        public Instruction NewLabel() { return Instruction.Create(OpCodes.Nop); }
        public IL Place(Instruction label) { Instructions.Add(label); return this; }

        public MethodReference ResolveMethod(string key)
        {
            int sep = key.IndexOf("::");
            if (sep < 0) throw new RepairException(repair.Key + ": method " + key + " has no ::");
            string typeName = key.Substring(0, sep);
            TypeDefinition type = Types.Parse(Module, typeName).Resolve();
            if (type == null) throw new RepairException(repair.Key + ": no type " + typeName);
            string rest = key.Substring(sep + 2);
            string name = rest.Contains("(") ? rest.Substring(0, rest.IndexOf('(')) : rest;
            string[] parms = rest.Contains("(") ? rest.Substring(rest.IndexOf('(') + 1).TrimEnd(')').Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) : null;
            var hits = type.Methods.Where(m => m.Name == name && (parms == null || m.Parameters.Select(p => p.ParameterType.Name).SequenceEqual(parms))).ToList();
            if (hits.Count != 1) throw new RepairException(repair.Key + ": " + hits.Count + " methods match " + key);
            return Module.ImportReference(hits[0]);
        }

        public FieldReference ResolveField(string key)
        {
            int sep = key.IndexOf("::");
            TypeDefinition type = Types.Parse(Module, key.Substring(0, sep)).Resolve();
            FieldDefinition f = type == null ? null : type.Fields.FirstOrDefault(x => x.Name == key.Substring(sep + 2));
            if (f == null) throw new RepairException(repair.Key + ": no field " + key);
            return Module.ImportReference(f);
        }
    }
}
