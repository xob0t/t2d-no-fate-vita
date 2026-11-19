using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The game's sounds load in the background instead of all at start (docs/audio.md, "Start-up";
// audio/compat/SoundLoads.cs): rules A1 and A2. Like the texture rules, each finds its sites by the shape of the IL
// and throws if the game's code has another shape, so a rule never applies to something it was not written for.
//
//   A1  AssetsEmbed.staticInit's loop calls loadSound(i) for every sound and drops the Sound:
//         <i>; call Sound AssetsEmbed::loadSound(int32); pop
//       becomes, in place (the loop and whatever else the method does stay as they are),
//         <i>; call void AssetsEmbed::t2dQueueSound(int32); nop
//       t2dQueueSound is a method the rule adds to AssetsEmbed, with loadSound's own bounds check and name lookup:
//         if (i >= 0 && i < <names>.Length) SoundLoads.Queue(<names>[i]);
//       where <names> is the string[] loadSound passes an element of to Sound.createSoundFromFile.
//   A2  Sound.createSoundFromFile's call of T2DCompat.Sounds.Clip(string) (the platform layer's Resources.Load,
//       compat/rewriter/PlatformPasses.cs PlatformSoundLoader) goes to SoundLoads.Clip(string).
//
// A1 composes with the texture rule R3 (atlas/rewriter/AtlasPatches.cs), which changes the splash pack load in the
// same method: each rule changes only the instructions of its own pattern, and checks its pattern alone. A2 needs the
// platform pass, which runs before the retargeting and so before these passes.
//
// Part of the assembly rewriter (DowngradeAssemblies.cs); C# 5. The passes go into its Passes list after the
// texture rules, with Scope.Game:
//   new Pass("audio", Scope.Game, AudioStartupLoads),   A1
//   new Pass("audio", Scope.Game, AudioClipLoads),      A2
// audio/check/AudioCheck.cs checks what they made of the written assembly.
partial class DowngradeAssemblies
{
    const string AudioEmbedType = "com.bitmapbureau.terminator.AssetsEmbed";
    const string AudioSoundType = "com.twissgreen.engine.media.Sound";
    const string AudioQueueMethod = "t2dQueueSound";

    static Exception AudioRuleFailed(string rule, string what)
    {
        return new InvalidOperationException("audio rule " + rule + ": " + what);
    }

    static bool AudioCallsMethod(Instruction i, string type, string name)
    {
        var method = i != null ? i.Operand as MethodReference : null;
        return i != null && (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && method != null &&
               method.Name == name && method.DeclaringType.FullName == type;
    }

    static bool AudioIsArg0(Instruction i)
    {
        return i != null && (i.OpCode == OpCodes.Ldarg_0 || ((i.OpCode == OpCodes.Ldarg || i.OpCode == OpCodes.Ldarg_S) &&
               ((ParameterDefinition)i.Operand).Index == 0));
    }

    static bool AudioIsStringElement(Instruction i)
    {
        return i != null && (i.OpCode == OpCodes.Ldelem_Ref ||
               (i.OpCode == OpCodes.Ldelem_Any && ((TypeReference)i.Operand).MetadataType == MetadataType.String));
    }

    // A1.
    static int AudioStartupLoads(ModuleDefinition module)
    {
        TypeDefinition embed = GameType(module, AudioEmbedType);
        if (embed == null) return 0;
        MethodDefinition[] loads = embed.Methods.Where(m => m.Name == "loadSound" && m.HasBody).ToArray();
        if (loads.Length != 1 || loads[0].Parameters.Count != 1 || loads[0].Parameters[0].ParameterType.MetadataType != MetadataType.Int32 ||
            !loads[0].IsStatic || loads[0].ReturnType.FullName != AudioSoundType)
            throw AudioRuleFailed("A1", "AssetsEmbed.loadSound is not one static Sound loadSound(int)");
        MethodDefinition load = loads[0];

        // The names: ldsfld string[] <names>; ldarg.0; ldelem string; call Sound::createSoundFromFile(string)
        Instruction[] creates = load.Body.Instructions.Where(i => AudioCallsMethod(i, AudioSoundType, "createSoundFromFile")).ToArray();
        if (creates.Length != 1)
            throw AudioRuleFailed("A1", "AssetsEmbed.loadSound calls Sound.createSoundFromFile " + creates.Length + " times, not once");
        Instruction element = creates[0].Previous, index = element != null ? element.Previous : null, array = index != null ? index.Previous : null;
        var names = array != null && array.OpCode == OpCodes.Ldsfld ? array.Operand as FieldReference : null;
        if (!AudioIsStringElement(element) || !AudioIsArg0(index) || names == null || names.FieldType.FullName != "System.String[]")
            throw AudioRuleFailed("A1", "AssetsEmbed.loadSound does not pass <string[] field>[name] to Sound.createSoundFromFile");

        MethodDefinition init = embed.Methods.SingleOrDefault(m => m.Name == "staticInit" && m.Parameters.Count == 0 && m.HasBody);
        if (init == null) throw AudioRuleFailed("A1", "no AssetsEmbed.staticInit()");
        Instruction[] calls = init.Body.Instructions.Where(i => i.Operand == load || AudioCallsMethod(i, AudioEmbedType, "loadSound")).ToArray();
        if (calls.Length != 1)
            throw AudioRuleFailed("A1", "AssetsEmbed.staticInit calls loadSound " + calls.Length + " times, not once");
        Instruction call = calls[0];
        if (call.OpCode != OpCodes.Call || call.Next == null || call.Next.OpCode != OpCodes.Pop)
            throw AudioRuleFailed("A1", "AssetsEmbed.staticInit's loadSound call is not call loadSound; pop");
        if (embed.Methods.Any(m => m.Name == AudioQueueMethod))
            throw AudioRuleFailed("A1", "AssetsEmbed already has a " + AudioQueueMethod);

        // static void t2dQueueSound(int name) { if (name >= 0 && name < <names>.Length) SoundLoads.Queue(<names>[name]); }
        var queue = new MethodDefinition(AudioQueueMethod, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        queue.Parameters.Add(new ParameterDefinition("name", ParameterAttributes.None, module.TypeSystem.Int32));
        ILProcessor il = queue.Body.GetILProcessor();
        Instruction end = il.Create(OpCodes.Ret);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, end);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldsfld, names);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Bge, end);
        il.Emit(OpCodes.Ldsfld, names);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Call, CompatMethod(module, "SoundLoads", "Queue"));
        il.Append(end);
        embed.Methods.Add(queue);

        // In place: a branch to the call or to the pop still lands on an instruction with the same stack.
        call.Operand = queue;
        call.Next.OpCode = OpCodes.Nop;
        call.Next.Operand = null;
        return 2;
    }

    // A2.
    static int AudioClipLoads(ModuleDefinition module)
    {
        TypeDefinition sound = GameType(module, AudioSoundType);
        if (sound == null) return 0;
        MethodDefinition[] creates = sound.Methods.Where(m => m.Name == "createSoundFromFile" && m.HasBody).ToArray();
        if (creates.Length != 1) throw AudioRuleFailed("A2", creates.Length + " Sound.createSoundFromFile methods, not 1");
        int count = 0;
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                foreach (Instruction i in method.Body.Instructions.Where(i => AudioCallsMethod(i, "T2DCompat.Sounds", "Clip")))
                {
                    if (method != creates[0])
                        throw AudioRuleFailed("A2", "T2DCompat.Sounds.Clip called outside Sound.createSoundFromFile, in " + method.FullName);
                    i.OpCode = OpCodes.Call;
                    i.Operand = CompatMethod(module, "SoundLoads", "Clip");
                    count++;
                }
        if (count != 1)
            throw AudioRuleFailed("A2", "Sound.createSoundFromFile calls T2DCompat.Sounds.Clip " + count +
                                        " times, not once (the platform pass's sound loader, PlatformSoundLoader, did not run or changed)");
        return count;
    }
}
