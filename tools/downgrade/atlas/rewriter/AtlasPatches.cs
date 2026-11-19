using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The texture pages (docs/textures.md, atlas/compat/Atlas.cs): rules R1 to R6, which send the game's texture packs
// to T2DCompat Atlas. Each pass checks the IL it expects and throws if the game's code has changed shape, so a rule
// never applies to something it was not written for. The counts are those of the Steam release (build 24040403)
// as Rewind recovers it.
//
// Part of the assembly rewriter (DowngradeAssemblies.cs); C# 5. The passes go into its Passes list, in this order,
// with Scope.Game:
//   new Pass("atlas", Scope.Game, AtlasTables),        R1
//   new Pass("atlas", Scope.Game, AtlasSplash),        R2, R3
//   new Pass("atlas", Scope.Game, AtlasPackLoads),     R4
//   new Pass("atlas", Scope.Game, AtlasDraws),         R5
//   new Pass("atlas", Scope.Game, AtlasSceneHooks),    R6
// atlas/check/RuleCheck.cs runs them on Rewind's Assembly-CSharp alone, outside the pipeline.
partial class DowngradeAssemblies
{
    const string AtlasPackType = "com.bitmapbureau.terminator.AssetsPack";
    const string AtlasBitmapData = "com.twissgreen.engine.display.BitmapData";

    static FieldDefinition AtlasField(TypeDefinition type, string name)
    {
        FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == name);
        if (field == null) throw new InvalidOperationException(type.Name + "." + name + ": no such field");
        return field;
    }

    static MethodDefinition AtlasMethod(TypeDefinition type, string name, int parameters)
    {
        MethodDefinition[] found = type.Methods.Where(m => m.Name == name && m.Parameters.Count == parameters && m.HasBody).ToArray();
        if (found.Length != 1) throw new InvalidOperationException(type.Name + "." + name + ": " + found.Length + " methods, not 1");
        return found[0];
    }

    static bool AtlasIsField(Instruction i, OpCode op, string type, string name)
    {
        var field = i != null ? i.Operand as FieldReference : null;
        return i != null && i.OpCode == op && field != null && field.Name == name && field.DeclaringType.FullName == type;
    }

    static bool AtlasCalls(Instruction i, string type, string name)
    {
        var method = i != null ? i.Operand as MethodReference : null;
        return i != null && (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && method != null &&
               method.Name == name && method.DeclaringType.FullName == type;
    }

    static bool AtlasIsLdloc(Instruction i)
    {
        switch (i.OpCode.Code)
        {
            case Code.Ldloc: case Code.Ldloc_S: case Code.Ldloc_0: case Code.Ldloc_1: case Code.Ldloc_2: case Code.Ldloc_3:
                return true;
        }
        return false;
    }

    static void AtlasExpect(int found, int expected, string what)
    {
        if (found != expected) throw new InvalidOperationException("atlas rule " + what + ": " + found + " sites, expected " + expected);
    }

    // A new body: the instructions given, nothing else (no locals, no handlers).
    static void AtlasReplaceBody(MethodDefinition method, params Instruction[] code)
    {
        MethodBody body = method.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();
        body.InitLocals = false;
        foreach (Instruction i in code) body.Instructions.Add(i);
    }

    // After an Enter: the next draw sets its texture again, as the page under an index may be a new Texture2D.
    static Instruction[] AtlasForgetTexture(ModuleDefinition module, TypeDefinition bitmapData)
    {
        return new[]
        {
            Instruction.Create(OpCodes.Ldsfld, module.ImportReference(AtlasField(bitmapData, "TEXTURE_UNKNOWN"))),
            Instruction.Create(OpCodes.Stsfld, module.ImportReference(AtlasField(bitmapData, "texused"))),
        };
    }

    // R1. AssetsPack.staticInit5..9 each build one table of the frame table with a straight-line initialiser
    // (new T[1211][], then each row) and end with stsfld <table>; ret. Their bodies become
    //   call Atlas.<Table>(); stsfld <table>; ret
    // with the page and the normalised rect of every frame in its page. staticInit10..13 (trims) and the sheet sizes
    // stay. The static constructor's imagetexnumber = 12 becomes imagetexnumber = Atlas.PageCount().
    static readonly string[][] AtlasTableRules =
    {
        new[] { "staticInit5", "imagetexframes_tex", "Tex" },
        new[] { "staticInit6", "imagetexframes_uvx", "Uvx" },
        new[] { "staticInit7", "imagetexframes_uvy", "Uvy" },
        new[] { "staticInit8", "imagetexframes_uvw", "Uvw" },
        new[] { "staticInit9", "imagetexframes_uvh", "Uvh" },
    };

    static int AtlasTables(ModuleDefinition module)
    {
        TypeDefinition pack = GameType(module, AtlasPackType);
        if (pack == null) return 0;
        int count = 0;
        foreach (string[] rule in AtlasTableRules)
        {
            MethodDefinition method = AtlasMethod(pack, rule[0], 0);
            FieldDefinition field = AtlasField(pack, rule[1]);
            Instruction[] stores = method.Body.Instructions.Where(i => i.OpCode == OpCodes.Stsfld).ToArray();
            if (stores.Length != 1 || !AtlasIsField(stores[0], OpCodes.Stsfld, AtlasPackType, rule[1]) ||
                method.Body.Instructions.Any(i => i.OpCode.FlowControl == FlowControl.Branch || i.OpCode.FlowControl == FlowControl.Cond_Branch))
                throw new InvalidOperationException("atlas rule R1: " + pack.Name + "." + rule[0] + " is not a straight-line initialiser of " + rule[1]);
            AtlasReplaceBody(method,
                Instruction.Create(OpCodes.Call, CompatMethod(module, "Atlas", rule[2])),
                Instruction.Create(OpCodes.Stsfld, field),
                Instruction.Create(OpCodes.Ret));
            count++;
        }
        MethodDefinition cctor = pack.Methods.Single(m => m.IsConstructor && m.IsStatic);
        Instruction[] number = cctor.Body.Instructions.Where(i => AtlasIsField(i, OpCodes.Stsfld, AtlasPackType, "imagetexnumber")).ToArray();
        AtlasExpect(number.Length, 1, "R1 AssetsPack..cctor imagetexnumber");
        int packs;
        if (!AtlasConstant(number[0].Previous, out packs) || packs != 12)
            throw new InvalidOperationException("atlas rule R1: AssetsPack..cctor does not set imagetexnumber = 12");
        number[0].Previous.OpCode = OpCodes.Call;
        number[0].Previous.Operand = CompatMethod(module, "Atlas", "PageCount");
        return count + 1;
    }

    static bool AtlasConstant(Instruction i, out int value)
    {
        value = 0;
        if (i == null) return false;
        switch (i.OpCode.Code)
        {
            case Code.Ldc_I4: value = (int)i.Operand; return true;
            case Code.Ldc_I4_S: value = (sbyte)i.Operand; return true;
            case Code.Ldc_I4_M1: value = -1; return true;
        }
        if (i.OpCode.Code < Code.Ldc_I4_0 || i.OpCode.Code > Code.Ldc_I4_8) return false;
        value = i.OpCode.Code - Code.Ldc_I4_0;
        return true;
    }

    // R2. BBSplash.start: range = 25 - AssetsPack.imagetexnumber sets how long the splash shows while the packs load;
    // the ldsfld imagetexnumber becomes ldc.i4 12, the length it always had. BBSplash.update: its one call
    // BitmapData.loadTexturePacks(n) goes to Atlas.SplashStep(n), which loads nothing.
    // R3. AssetsEmbed.staticInit: ldsfld AssetsPack.bblogotexnumber; call BitmapData.loadTexturePacks (the splash's pack)
    // becomes ldstr "app:BBSplash"; call Atlas.Enter(string): Main makes the BBSplash itself, not through getApp.
    static int AtlasSplash(ModuleDefinition module)
    {
        TypeDefinition splash = GameType(module, "com.bitmapbureau.terminator.apps.BBSplash");
        TypeDefinition embed = GameType(module, "com.bitmapbureau.terminator.AssetsEmbed");
        if (splash == null || embed == null) return 0;

        MethodDefinition start = AtlasMethod(splash, "start", 0);
        Instruction[] reads = start.Body.Instructions.Where(i => AtlasIsField(i, OpCodes.Ldsfld, AtlasPackType, "imagetexnumber")).ToArray();
        AtlasExpect(reads.Length, 1, "R2 BBSplash.start imagetexnumber");
        int timer;
        if (!AtlasConstant(reads[0].Previous, out timer) || reads[0].Next.OpCode != OpCodes.Sub)
            throw new InvalidOperationException("atlas rule R2: BBSplash.start is not range = <n> - imagetexnumber");
        reads[0].OpCode = OpCodes.Ldc_I4;
        reads[0].Operand = 12;

        MethodDefinition update = AtlasMethod(splash, "update", 0);
        Instruction[] loads = update.Body.Instructions.Where(i => AtlasCalls(i, AtlasBitmapData, "loadTexturePacks")).ToArray();
        AtlasExpect(loads.Length, 1, "R2 BBSplash.update loadTexturePacks");
        loads[0].Operand = CompatMethod(module, "Atlas", "SplashStep");

        MethodDefinition init = AtlasMethod(embed, "staticInit", 0);
        Instruction[] logo = init.Body.Instructions.Where(i => AtlasCalls(i, AtlasBitmapData, "loadTexturePacks")).ToArray();
        AtlasExpect(logo.Length, 1, "R3 AssetsEmbed.staticInit loadTexturePacks");
        if (!AtlasIsField(logo[0].Previous, OpCodes.Ldsfld, AtlasPackType, "bblogotexnumber"))
            throw new InvalidOperationException("atlas rule R3: AssetsEmbed.staticInit does not load pack bblogotexnumber");
        logo[0].Previous.OpCode = OpCodes.Ldstr;
        logo[0].Previous.Operand = "app:BBSplash";
        logo[0].Operand = CompatMethod(module, "Atlas", "Enter");
        return 3;
    }

    // R4. BitmapData.loadTexturePacks(int): Resources.Load<TextAsset>("texturepack/TexturePack" + n) and LoadImage, a PNG
    // decoded into a 64 MB ARGB32 texture, stored in BitmapData.texpack[n]. Its body becomes
    //   ldarg.0; call Atlas.Preload(int); ret
    // After R2 and R3 nothing in the game calls it; the rule keeps a stray call from decoding a pack.
    static int AtlasPackLoads(ModuleDefinition module)
    {
        TypeDefinition bitmapData = GameType(module, AtlasBitmapData);
        if (bitmapData == null) return 0;
        MethodDefinition load = AtlasMethod(bitmapData, "loadTexturePacks", 1);
        if (!load.Body.Instructions.Any(i => AtlasCalls(i, "UnityEngine.ImageConversion", "LoadImage")) ||
            !load.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "texturepack/TexturePack"))
            throw new InvalidOperationException("atlas rule R4: BitmapData.loadTexturePacks does not load texturepack/TexturePack<n>");
        AtlasReplaceBody(load,
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Call, CompatMethod(module, "Atlas", "Preload")),
            Instruction.Create(OpCodes.Ret));
        return 1;
    }

    // R5. BitmapData.unityDraw and unityDrawClippedWidth pick the texture of a frame as texpack[texpack_tex[frame]],
    // twice each (MaterialPropertyBlock.SetTexture and the main texture):
    //   ldsfld BitmapData.texpack; ldloc <page>; ldelem Texture2D
    // becomes, in place (branches to the first instruction stay right),
    //   ldloc <page>; call Atlas.Get(int); nop
    // Get returns the resident page, or loads it at once and logs the miss.
    static int AtlasDraws(ModuleDefinition module)
    {
        TypeDefinition bitmapData = GameType(module, AtlasBitmapData);
        if (bitmapData == null) return 0;
        MethodReference get = CompatMethod(module, "Atlas", "Get");
        int count = 0;
        foreach (string name in new[] { "unityDraw", "unityDrawClippedWidth" })
        {
            MethodDefinition draw = bitmapData.Methods.Single(m => m.Name == name && m.HasBody);
            int here = 0;
            foreach (Instruction read in draw.Body.Instructions.Where(i => AtlasIsField(i, OpCodes.Ldsfld, AtlasBitmapData, "texpack")).ToArray())
            {
                Instruction index = read.Next, element = index != null ? index.Next : null;
                if (index == null || !AtlasIsLdloc(index) || element == null ||
                    !(element.OpCode == OpCodes.Ldelem_Any || element.OpCode == OpCodes.Ldelem_Ref))
                    throw new InvalidOperationException("atlas rule R5: BitmapData." + name + " reads texpack other than texpack[<local>]");
                read.OpCode = index.OpCode;
                read.Operand = index.Operand;
                index.OpCode = OpCodes.Call;
                index.Operand = get;
                element.OpCode = OpCodes.Nop;
                element.Operand = null;
                here++;
            }
            AtlasExpect(here, 2, "R5 BitmapData." + name);
            count += here;
        }
        return count;
    }

    // R6. The scene hooks; each is followed by BitmapData.texused = BitmapData.TEXTURE_UNKNOWN.
    //   Transition: after each call Transition.getApp(int) (2 sites in Transition.update):
    //     dup; call Atlas.EnterApp(object)            the App's class picks the scene
    //   GameMap.loadFile(int map, int level), at its start:
    //     ldarg.0; ldarg.1; call Atlas.EnterMap(int, int)
    //   Game.loadScriptByLevelID(int level), at its start:
    //     ldarg.0; call Atlas.EnterLevel(int)        acts only while the App is Game (menus call it for previews)
    static int AtlasSceneHooks(ModuleDefinition module)
    {
        TypeDefinition transition = GameType(module, "com.bitmapbureau.terminator.Transition");
        TypeDefinition map = GameType(module, "com.bitmapbureau.terminator.game.GameMap");
        TypeDefinition game = GameType(module, "com.bitmapbureau.terminator.game.Game");
        TypeDefinition bitmapData = GameType(module, AtlasBitmapData);
        if (transition == null || map == null || game == null || bitmapData == null) return 0;
        AtlasMethod(transition, "getApp", 1);
        int apps = 0;
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                foreach (Instruction call in method.Body.Instructions.Where(i => AtlasCalls(i, transition.FullName, "getApp")).ToArray())
                {
                    var code = new List<Instruction>
                    {
                        Instruction.Create(OpCodes.Dup),
                        Instruction.Create(OpCodes.Call, CompatMethod(module, "Atlas", "EnterApp")),
                    };
                    code.AddRange(AtlasForgetTexture(module, bitmapData));
                    InsertAfter(method.Body.GetILProcessor(), call, code.ToArray());
                    apps++;
                }
        AtlasExpect(apps, 2, "R6 calls of Transition.getApp");

        MethodDefinition load = AtlasMethod(map, "loadFile", 2);
        if (load.Parameters.Any(p => p.ParameterType.FullName != "System.Int32") || !load.IsStatic)
            throw new InvalidOperationException("atlas rule R6: GameMap.loadFile is not static loadFile(int, int)");
        Prepend(load, new[]
        {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldarg_1),
            Instruction.Create(OpCodes.Call, CompatMethod(module, "Atlas", "EnterMap")),
        }.Concat(AtlasForgetTexture(module, bitmapData)).ToArray());

        MethodDefinition script = AtlasMethod(game, "loadScriptByLevelID", 1);
        if (script.Parameters[0].ParameterType.FullName != "System.Int32" || !script.IsStatic)
            throw new InvalidOperationException("atlas rule R6: Game.loadScriptByLevelID is not static (int)");
        Prepend(script, new[]
        {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Call, CompatMethod(module, "Atlas", "EnterLevel")),
        }.Concat(AtlasForgetTexture(module, bitmapData)).ToArray());
        return apps + 2;
    }
}
