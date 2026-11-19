using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The game's sounds load in the background instead of all at start (docs/audio.md, "Start-up";
// audio/compat/SoundLoads.cs): rules A1, A2 and A3. Like the texture rules, each finds its sites by the shape of the
// IL and throws if the game's code has another shape, so a rule never applies to something it was not written for.
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
//   A3  A Sound is made without its clip while the clip is still loading, and gets it when it is first played.
//       The game makes a Sound for most of its clips at start (Sounds.staticInit, through loadSound), long before it
//       plays them; with A2 alone each of those waits for its clip. A3 starts createSoundFromFile with
//         if (SoundLoads.Defer(name)) { Sound s = new Sound(null); s.clipname = name; return s; }
//       (Sound's constructor leaves snd and length alone for a null clip), and adds to Sound
//         static AudioClip t2dClip(Sound s)
//         {
//             if (s.snd == null && s.clipname != null)
//             {
//                 AudioClip c = SoundLoads.Clip(s.clipname);     // the loaded clip, or waits for that one
//                 if (c != null) { s.snd = c; s.length = c.length * 1000f; }   // as the constructor sets them
//             }
//             return s.snd;
//         }
//         static double t2dLength(Sound s) { t2dClip(s); return s.length; }
//       Every read of Sound.snd and Sound.length outside the constructor (playWrap, playScheduleWrap,
//       Audio.playIntroLoopChannels; Audio.cueAudioFull, which compares lengths to drop a sound cued twice) becomes,
//       in place, a call of t2dClip or t2dLength: ldfld <field> takes a Sound and leaves the value, as the call does.
//       So only playing (or cueing) a sound waits for its clip, and no sound plays without it.
//
// A1 composes with the texture rule R3 (atlas/rewriter/AtlasPatches.cs), which changes the splash pack load in the
// same method: each rule changes only the instructions of its own pattern, and checks its pattern alone. A2 needs the
// platform pass, which runs before the retargeting and so before these passes; A3 needs A2.
//
// Part of the assembly rewriter (DowngradeAssemblies.cs); C# 5. The passes go into its Passes list after the
// texture rules, with Scope.Game:
//   new Pass("audio", Scope.Game, AudioStartupLoads),   A1
//   new Pass("audio", Scope.Game, AudioClipLoads),      A2
//   new Pass("audio", Scope.Game, AudioDeferredClips),  A3
// audio/check/AudioCheck.cs checks what they made of the written assembly.
partial class DowngradeAssemblies
{
    const string AudioEmbedType = "com.bitmapbureau.terminator.AssetsEmbed";
    const string AudioSoundType = "com.twissgreen.engine.media.Sound";
    const string AudioQueueMethod = "t2dQueueSound";
    const string AudioClipMethod = "t2dClip";
    const string AudioLengthMethod = "t2dLength";
    const string AudioClipType = "UnityEngine.AudioClip";

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

    static FieldDefinition AudioField(TypeDefinition type, string name, string fieldType)
    {
        FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == name && !f.IsStatic);
        if (field == null || field.FieldType.FullName != fieldType)
            throw AudioRuleFailed("A3", "Sound has no instance field " + fieldType + " " + name);
        return field;
    }

    static bool AudioIsField(Instruction i, FieldDefinition field)
    {
        var f = i.Operand as FieldReference;
        return f != null && f.Name == field.Name && f.DeclaringType.FullName == field.DeclaringType.FullName;
    }

    static bool AudioIsArg1(Instruction i)
    {
        return i != null && (i.OpCode == OpCodes.Ldarg_1 || ((i.OpCode == OpCodes.Ldarg || i.OpCode == OpCodes.Ldarg_S) &&
               ((ParameterDefinition)i.Operand).Index == 1));
    }

    // A3.
    static int AudioDeferredClips(ModuleDefinition module)
    {
        TypeDefinition sound = GameType(module, AudioSoundType);
        if (sound == null) return 0;
        if (sound.Methods.Any(m => m.Name == AudioClipMethod || m.Name == AudioLengthMethod))
            throw AudioRuleFailed("A3", "Sound already has a " + AudioClipMethod + " or " + AudioLengthMethod);
        FieldDefinition snd = AudioField(sound, "snd", AudioClipType);
        FieldDefinition length = AudioField(sound, "length", "System.Double");
        FieldDefinition clipname = AudioField(sound, "clipname", "System.String");

        // The constructor: if (clip != null) { snd = clip; length = snd.length * 1000f; }. A null clip must leave the
        // Sound as it is: the guard on the clip comes before the first store, as ldarg.1; brfalse or ldarg.1; ldnull;
        // ceq; brtrue. Its AudioClip.get_length is the reference t2dClip calls.
        MethodDefinition[] constructors = sound.Methods.Where(m => m.IsConstructor && !m.IsStatic).ToArray();
        if (constructors.Length != 1 || constructors[0].Parameters.Count != 1 ||
            constructors[0].Parameters[0].ParameterType.FullName != AudioClipType)
            throw AudioRuleFailed("A3", "Sound has not one constructor, Sound(AudioClip)");
        MethodDefinition constructor = constructors[0];
        Instruction[] body = constructor.Body.Instructions.ToArray();
        int firstStore = Array.FindIndex(body, i => i.OpCode == OpCodes.Stfld);
        int guard = Array.FindIndex(body, i => AudioIsArg1(i) && i.Next != null &&
            (i.Next.OpCode == OpCodes.Brfalse || i.Next.OpCode == OpCodes.Brfalse_S ||
             (i.Next.OpCode == OpCodes.Ldnull && i.Next.Next != null && i.Next.Next.OpCode == OpCodes.Ceq && i.Next.Next.Next != null &&
              (i.Next.Next.Next.OpCode == OpCodes.Brtrue || i.Next.Next.Next.OpCode == OpCodes.Brtrue_S))));
        if (firstStore < 0 || guard < 0 || guard > firstStore)
            throw AudioRuleFailed("A3", "Sound's constructor does not test its clip for null before it stores a field");
        MethodReference[] getLength = body.Where(i => AudioCallsMethod(i, AudioClipType, "get_length"))
            .Select(i => (MethodReference)i.Operand).ToArray();
        if (getLength.Length != 1 || body.Count(i => i.OpCode == OpCodes.Stfld && AudioIsField(i, length)) != 1)
            throw AudioRuleFailed("A3", "Sound's constructor does not set length from AudioClip.length once");

        // createSoundFromFile after A2: SoundLoads.Clip(name), then new Sound(clip) and clipname = name.
        MethodDefinition[] creates = sound.Methods.Where(m => m.Name == "createSoundFromFile" && m.HasBody).ToArray();
        if (creates.Length != 1 || creates[0].Parameters.Count != 1 || creates[0].Parameters[0].ParameterType.MetadataType != MetadataType.String)
            throw AudioRuleFailed("A3", "Sound has not one createSoundFromFile(string)");
        MethodDefinition create = creates[0];
        if (create.Body.Instructions.Count(i => AudioCallsMethod(i, "T2DCompat.SoundLoads", "Clip")) != 1 ||
            create.Body.Instructions.Count(i => i.OpCode == OpCodes.Newobj && i.Operand == constructor) != 1 ||
            create.Body.Instructions.Count(i => i.OpCode == OpCodes.Stfld && AudioIsField(i, clipname)) != 1)
            throw AudioRuleFailed("A3", "Sound.createSoundFromFile does not take its clip from SoundLoads.Clip (A2) and make one Sound with its clipname");

        MethodReference loaded = CompatMethod(module, "SoundLoads", "Clip");
        MethodReference defer = CompatMethod(module, "SoundLoads", "Defer");
        const MethodAttributes added = MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig;

        // static AudioClip t2dClip(Sound s)
        var clipOf = new MethodDefinition(AudioClipMethod, added, snd.FieldType);
        clipOf.Parameters.Add(new ParameterDefinition("s", ParameterAttributes.None, sound));
        var clip = new VariableDefinition(snd.FieldType);
        clipOf.Body.Variables.Add(clip);
        clipOf.Body.InitLocals = true;
        ILProcessor il = clipOf.Body.GetILProcessor();
        Instruction have = il.Create(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, snd);
        il.Emit(OpCodes.Brtrue, have);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, clipname);
        il.Emit(OpCodes.Brfalse, have);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, clipname);
        il.Emit(OpCodes.Call, loaded);
        il.Emit(OpCodes.Stloc, clip);
        il.Emit(OpCodes.Ldloc, clip);
        il.Emit(OpCodes.Brfalse, have);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, clip);
        il.Emit(OpCodes.Stfld, snd);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, clip);
        il.Emit(OpCodes.Callvirt, getLength[0]);
        il.Emit(OpCodes.Ldc_R4, 1000f);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Stfld, length);
        il.Append(have);
        il.Emit(OpCodes.Ldfld, snd);
        il.Emit(OpCodes.Ret);

        // static double t2dLength(Sound s)
        var lengthOf = new MethodDefinition(AudioLengthMethod, added, length.FieldType);
        lengthOf.Parameters.Add(new ParameterDefinition("s", ParameterAttributes.None, sound));
        il = lengthOf.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, clipOf);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, length);
        il.Emit(OpCodes.Ret);

        // The reads, in place. An address of either field could be read or written past the clip: none is expected.
        int reads = 0;
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody && m != constructor))
                foreach (Instruction i in method.Body.Instructions)
                {
                    bool isSnd = AudioIsField(i, snd), isLength = AudioIsField(i, length);
                    if (!isSnd && !isLength) continue;
                    if (i.OpCode == OpCodes.Ldflda)
                        throw AudioRuleFailed("A3", method.FullName + " takes the address of Sound." + (isSnd ? "snd" : "length"));
                    if (i.OpCode != OpCodes.Ldfld) continue;
                    i.OpCode = OpCodes.Call;
                    i.Operand = isSnd ? clipOf : lengthOf;
                    reads++;
                }
        if (reads == 0) throw AudioRuleFailed("A3", "nothing reads Sound.snd or Sound.length: the play path changed");

        // if (SoundLoads.Defer(name)) { Sound s = new Sound(null); s.clipname = name; return s; }, before the body.
        il = create.Body.GetILProcessor();
        Instruction first = create.Body.Instructions[0];
        foreach (Instruction i in new[]
        {
            il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Call, defer),
            il.Create(OpCodes.Brfalse, first),
            il.Create(OpCodes.Ldnull),
            il.Create(OpCodes.Newobj, constructor),
            il.Create(OpCodes.Dup),
            il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Stfld, clipname),
            il.Create(OpCodes.Ret),
        })
            il.InsertBefore(first, i);

        sound.Methods.Add(clipOf);
        sound.Methods.Add(lengthOf);
        return reads + 1;
    }
}
