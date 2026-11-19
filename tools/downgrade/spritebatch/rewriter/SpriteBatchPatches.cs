using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// The batched sprite renderer (docs/drawing.md, spritebatch/compat/SpriteBatch.cs): rules R7 to R9, which stop the
// game from baking four Meshes per sprite frame and draw its sprites through T2DCompat SpriteBatch instead. Each pass
// checks the IL and the fields it expects and throws if the game's code has changed shape.
//
// Part of the assembly rewriter (DowngradeAssemblies.cs); C# 5. The passes go into its Passes list, in this order,
// with Scope.Game, before ListRewindGaps (so the gaps of the bodies they replace are not counted) and before the atlas
// passes (AtlasDraws skips the two draw methods R8 has taken; SpriteBatch takes their pages from Atlas.Get itself):
//   new Pass("spritebatch", Scope.Game, SpriteBatchDraws),    R8
//   new Pass("spritebatch", Scope.Game, SpriteBatchMeshes),   R7
//   new Pass("spritebatch", Scope.Game, SpriteBatchFlush),    R9
//   new Pass("spritebatch", Scope.Game, SpriteBatchFilters),  R10
// spritebatch/check/SpriteCheck.cs runs them, with the atlas passes, on Rewind's Assembly-CSharp alone.
partial class DowngradeAssemblies
{
    const string SpriteBitmapData = "com.twissgreen.engine.display.BitmapData";
    const string SpriteFrames = "com.twissgreen.engine.display.TextureFrames";
    const string SpriteColorTransform = "com.twissgreen.engine.geom.ColorTransform";
    const string SpriteUnityMain = "com.twissgreen.unity.UnityMain";
    const string SpriteMain = "com.bitmapbureau.terminator.Main";
    const string SpriteGfx = "com.bitmapbureau.terminator.Gfx";
    const string SpriteBatchType = "T2DCompat.SpriteBatch";

    // The draw methods' parameters, by type, as Rewind recovers them.
    static readonly string[] SpriteDrawParameters =
    {
        SpriteBitmapData, "System.Double", "System.Double", "System.Int32", "System.Int32", "System.Int32", "System.Int32",
        SpriteColorTransform, "System.Boolean", "System.Boolean", "System.Int32", "System.Int32",
        "com.twissgreen.engine.display.BlendMode", "System.Boolean", "System.Double", "System.Double", "System.Double",
    };
    static readonly string[] SpriteClippedParameters =
    {
        SpriteBitmapData, "System.Double", "System.Double", "System.Int32", "System.Int32", "System.Int32", "System.Int32",
        SpriteColorTransform, "System.Double",
    };

    static Exception SpriteRule(string rule, string what)
    {
        return new InvalidOperationException("spritebatch rule " + rule + ": " + what);
    }

    static MethodDefinition SpriteMethod(string rule, TypeDefinition type, string name, string[] parameters)
    {
        MethodDefinition[] found = type.Methods.Where(m => m.Name == name && m.HasBody).ToArray();
        if (found.Length != 1) throw SpriteRule(rule, type.Name + "." + name + ": " + found.Length + " methods with a body, not 1");
        MethodDefinition method = found[0];
        string[] has = method.Parameters.Select(p => p.ParameterType.FullName).ToArray();
        if (parameters != null && !has.SequenceEqual(parameters))
            throw SpriteRule(rule, type.Name + "." + name + "(" + string.Join(", ", has) + ") is not (" + string.Join(", ", parameters) + ")");
        return method;
    }

    static bool SpriteCalls(Instruction i, string typeName, string name)
    {
        var method = i.Operand as MethodReference;
        return (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Newobj) && method != null &&
               method.Name == name && method.DeclaringType.Name == typeName;
    }

    static bool SpriteUsesField(Instruction i, string name)
    {
        var field = i.Operand as FieldReference;
        return field != null && field.Name == name && field.DeclaringType.FullName == SpriteBitmapData;
    }

    static int SpriteCount(MethodDefinition method, Func<Instruction, bool> what)
    {
        return method.Body.Instructions.Count(what);
    }

    // Whether R8 has replaced the method: its body calls SpriteBatch (atlas/rewriter/AtlasPatches.cs's AtlasDraws asks).
    static bool SpriteBatchOwns(MethodDefinition method)
    {
        return method.HasBody && method.Body.Instructions.Any(i =>
        {
            var called = i.Operand as MethodReference;
            return called != null && called.DeclaringType.FullName == SpriteBatchType;
        });
    }

    // Emits a call to SpriteBatch.<name>, its arguments from the loads given, each checked against the parameter's
    // type, so a field or argument of another type than the port expects fails here, not at run time.
    class SpriteCall
    {
        readonly List<Instruction> code;
        readonly List<string> types = new List<string>();
        readonly List<string> what = new List<string>();

        public SpriteCall(List<Instruction> code) { this.code = code; }

        public SpriteCall Arg(int index, MethodDefinition method)
        {
            ParameterDefinition p = index == 0 ? null : method.Parameters[index - 1];
            code.Add(index == 0 ? Instruction.Create(OpCodes.Ldarg_0) : Instruction.Create(OpCodes.Ldarg, p));
            types.Add(index == 0 ? method.DeclaringType.FullName : p.ParameterType.FullName);
            what.Add(index == 0 ? "this" : p.Name);
            return this;
        }

        public SpriteCall Field(ModuleDefinition module, int argument, MethodDefinition method, FieldDefinition field)
        {
            Arg(argument, method);
            types.RemoveAt(types.Count - 1);
            what.RemoveAt(what.Count - 1);
            code.Add(Instruction.Create(OpCodes.Ldfld, module.ImportReference(field)));
            types.Add(field.FieldType.FullName);
            what.Add(field.DeclaringType.Name + "." + field.Name);
            return this;
        }

        public SpriteCall Static(ModuleDefinition module, FieldDefinition field)
        {
            code.Add(Instruction.Create(OpCodes.Ldsfld, module.ImportReference(field)));
            types.Add(field.FieldType.FullName);
            what.Add(field.DeclaringType.Name + "." + field.Name);
            return this;
        }

        // ct != null
        public SpriteCall NotNull(int argument, MethodDefinition method)
        {
            Arg(argument, method);
            code.Add(Instruction.Create(OpCodes.Ldnull));
            code.Add(Instruction.Create(OpCodes.Cgt_Un));
            types[types.Count - 1] = "System.Boolean";
            return this;
        }

        public void Call(ModuleDefinition module, string rule, string name)
        {
            MethodDefinition target = compat[SpriteBatchType].Methods.Single(m => m.Name == name);
            string[] wanted = target.Parameters.Select(p => p.ParameterType.FullName).ToArray();
            if (wanted.Length != types.Count)
                throw SpriteRule(rule, "SpriteBatch." + name + " takes " + wanted.Length + " arguments, the rule gives " + types.Count);
            for (int k = 0; k < wanted.Length; k++)
                if (wanted[k] != types[k])
                    throw SpriteRule(rule, "SpriteBatch." + name + " argument " + target.Parameters[k].Name + " is " + wanted[k] + ", " + what[k] + " is " + types[k]);
            code.Add(Instruction.Create(OpCodes.Call, CompatMethod(module, "SpriteBatch", name)));
        }
    }

    static FieldDefinition SpriteField(string rule, TypeDefinition type, string name, string fieldType)
    {
        FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == name);
        if (field == null) throw SpriteRule(rule, type.Name + "." + name + ": no such field");
        if (field.FieldType.FullName != fieldType) throw SpriteRule(rule, type.Name + "." + name + " is " + field.FieldType.FullName + ", not " + fieldType);
        return field;
    }

    // A new body: the instructions given, nothing else (no locals, no handlers).
    static void SpriteReplaceBody(MethodDefinition method, List<Instruction> code)
    {
        MethodBody body = method.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();
        body.InitLocals = false;
        foreach (Instruction i in code) body.Instructions.Add(i);
    }

    // The shared part of both new bodies: if (_source == null) return; Target(this...); Source(_source...);
    // if (_ct != null) Colors(_ct.colmul, _ct.coloff). Returns the instruction the null test jumps to on return.
    static Instruction SpriteSetup(ModuleDefinition module, string rule, TypeDefinition bitmapData, MethodDefinition method, List<Instruction> code)
    {
        TypeDefinition ct = method.Parameters[7].ParameterType.Resolve();
        if (ct == null || ct.FullName != SpriteColorTransform) throw SpriteRule(rule, "ColorTransform does not resolve");
        Instruction done = Instruction.Create(OpCodes.Ret);
        code.Add(Instruction.Create(OpCodes.Ldarg_1));
        code.Add(Instruction.Create(OpCodes.Brfalse, done));

        new SpriteCall(code)
            .Field(module, 0, method, SpriteField(rule, bitmapData, "screen", "System.Boolean"))
            .Field(module, 0, method, SpriteField(rule, bitmapData, "rentex", "UnityEngine.RenderTexture"))
            .Field(module, 0, method, SpriteField(rule, bitmapData, "width_half", "System.Double"))
            .Field(module, 0, method, SpriteField(rule, bitmapData, "height_half", "System.Double"))
            .Call(module, rule, "Target");

        var source = new SpriteCall(code).Field(module, 1, method, SpriteField(rule, bitmapData, "rentex", "UnityEngine.RenderTexture"));
        source.Field(module, 1, method, SpriteField(rule, bitmapData, "texpack_tex", "System.Int32[]"));
        foreach (string name in new[] { "texpack_uvx", "texpack_uvy", "texpack_uvw", "texpack_uvh" })
            source.Field(module, 1, method, SpriteField(rule, bitmapData, name, "System.Double[]"));
        foreach (string name in new[] { "texpack_frw", "texpack_frh", "texpack_ofx", "texpack_ofy" })
            source.Field(module, 1, method, SpriteField(rule, bitmapData, name, "System.Int32[]"));
        source.Field(module, 1, method, SpriteField(rule, bitmapData, "texpack_sfw", "System.Int32"))
              .Field(module, 1, method, SpriteField(rule, bitmapData, "texpack_sfh", "System.Int32"))
              .Field(module, 1, method, SpriteField(rule, bitmapData, "mesh_half_width", "System.Double"))
              .Field(module, 1, method, SpriteField(rule, bitmapData, "mesh_half_height", "System.Double"))
              .Field(module, 1, method, SpriteField(rule, bitmapData, "width", "System.Int32"))
              .Field(module, 1, method, SpriteField(rule, bitmapData, "height", "System.Int32"))
              .Call(module, rule, "Source");

        Instruction noColors = Instruction.Create(OpCodes.Nop);
        code.Add(Instruction.Create(OpCodes.Ldarg, method.Parameters[7]));
        code.Add(Instruction.Create(OpCodes.Brfalse, noColors));
        new SpriteCall(code)
            .Field(module, 8, method, SpriteField(rule, ct, "colmul", "UnityEngine.Color"))
            .Field(module, 8, method, SpriteField(rule, ct, "coloff", "UnityEngine.Vector4"))
            .Call(module, rule, "Colors");
        code.Add(noColors);
        return done;
    }

    // ... BitmapData.zorder = <the call>; matused = MATERIAL_UNKNOWN; texused = TEXTURE_UNKNOWN; return.
    static void SpriteFinish(ModuleDefinition module, string rule, TypeDefinition bitmapData, List<Instruction> code, Instruction done)
    {
        code.Add(Instruction.Create(OpCodes.Stsfld, module.ImportReference(SpriteField(rule, bitmapData, "zorder", "System.Double"))));
        code.Add(Instruction.Create(OpCodes.Ldsfld, module.ImportReference(SpriteField(rule, bitmapData, "MATERIAL_UNKNOWN", "System.Int32"))));
        code.Add(Instruction.Create(OpCodes.Stsfld, module.ImportReference(SpriteField(rule, bitmapData, "matused", "System.Int32"))));
        code.Add(Instruction.Create(OpCodes.Ldsfld, module.ImportReference(SpriteField(rule, bitmapData, "TEXTURE_UNKNOWN", "System.Int32"))));
        code.Add(Instruction.Create(OpCodes.Stsfld, module.ImportReference(SpriteField(rule, bitmapData, "texused", "System.Int32"))));
        code.Add(done);
    }

    // R8. BitmapData.unityDraw and unityDrawClippedWidth: a new body each, which hands the target's and the source's
    // fields, the ColorTransform's colours and the call's arguments to SpriteBatch:
    //   if (_source == null) return;
    //   SpriteBatch.Target(screen, rentex, width_half, height_half);
    //   SpriteBatch.Source(_source.rentex, _source.texpack_tex, ..uvx, uvy, uvw, uvh, frw, frh, ofx, ofy, sfw, sfh,
    //                      _source.mesh_half_width, mesh_half_height, width, height);
    //   if (_ct != null) SpriteBatch.Colors(_ct.colmul, _ct.coloff);
    //   zorder = SpriteBatch.Draw(_x, _y, _frame, _HFRAMES, _VFRAMES, _align, _ct != null, _hflip, _vflip, _width,
    //                             _height, _rotation, _hscale, _vscale, main_material, ct_material, zorder);
    //       (unityDrawClippedWidth: DrawClipped(_x, _y, _frame, _HFRAMES, _VFRAMES, _align, _ct != null, _frameWidth,
    //                                           main_material, ct_material, zorder))
    //   matused = MATERIAL_UNKNOWN; texused = TEXTURE_UNKNOWN;
    // The old bodies must still be the mesh draws: Graphics.DrawMesh and DrawMeshNow of _source.meshes, z from
    // zorder. Their texture reads (atlas R5) and Rewind gaps go with them.
    static int SpriteBatchDraws(ModuleDefinition module)
    {
        TypeDefinition bitmapData = GameType(module, SpriteBitmapData);
        if (bitmapData == null) return 0;
        const string rule = "R8";
        MethodDefinition draw = SpriteMethod(rule, bitmapData, "unityDraw", SpriteDrawParameters);
        MethodDefinition clipped = SpriteMethod(rule, bitmapData, "unityDrawClippedWidth", SpriteClippedParameters);
        foreach (MethodDefinition m in new[] { draw, clipped })
        {
            if (m.IsStatic || m.ReturnType.FullName != "System.Void") throw SpriteRule(rule, m.Name + " is not an instance method returning void");
            if (SpriteBatchOwns(m)) throw SpriteRule(rule, m.Name + " already calls SpriteBatch (ran twice?)");
            int meshDraws = SpriteCount(m, i => SpriteCalls(i, "Graphics", "DrawMesh"));
            int nowDraws = SpriteCount(m, i => SpriteCalls(i, "Graphics", "DrawMeshNow"));
            int meshReads = SpriteCount(m, i => i.OpCode == OpCodes.Ldfld && SpriteUsesField(i, "meshes"));
            int zSteps = SpriteCount(m, i => i.OpCode == OpCodes.Stsfld && SpriteUsesField(i, "zorder"));
            if (meshDraws != 1 || nowDraws < 1 || meshReads < 1 || zSteps != 1)
                throw SpriteRule(rule, m.Name + " is not the mesh draw it was written for (DrawMesh " + meshDraws + ", DrawMeshNow " + nowDraws +
                                       ", reads of meshes " + meshReads + ", stores of zorder " + zSteps + "; expected 1, 1+, 1+, 1)");
        }
        TypeDefinition materialHolder = bitmapData;
        FieldDefinition normal = SpriteField(rule, materialHolder, "main_material", "UnityEngine.Material");
        FieldDefinition colorT = SpriteField(rule, materialHolder, "ct_material", "UnityEngine.Material");
        FieldDefinition zorder = SpriteField(rule, bitmapData, "zorder", "System.Double");

        var code = new List<Instruction>();
        Instruction done = SpriteSetup(module, rule, bitmapData, draw, code);
        var call = new SpriteCall(code);
        for (int a = 2; a <= 7; a++) call.Arg(a, draw);
        call.NotNull(8, draw);
        foreach (int a in new[] { 9, 10, 11, 12, 15, 16, 17 }) call.Arg(a, draw);
        call.Static(module, normal).Static(module, colorT).Static(module, zorder).Call(module, rule, "Draw");
        SpriteFinish(module, rule, bitmapData, code, done);
        SpriteReplaceBody(draw, code);

        code = new List<Instruction>();
        done = SpriteSetup(module, rule, bitmapData, clipped, code);
        call = new SpriteCall(code);
        for (int a = 2; a <= 7; a++) call.Arg(a, clipped);
        call.NotNull(8, clipped).Arg(9, clipped);
        call.Static(module, normal).Static(module, colorT).Static(module, zorder).Call(module, rule, "DrawClipped");
        SpriteFinish(module, rule, bitmapData, code, done);
        SpriteReplaceBody(clipped, code);
        return 2;
    }

    // R7. BitmapData.CreateMultiMeshFrames(TextureFrames): four Meshes per frame (new Mesh, MarkDynamic, vertices, uv,
    // triangles, RecalculateNormals), over 150,000 at start. Its body becomes
    //   return new Mesh[tframes.uvx.Length * 4];
    // an array of nulls, since after R8 nothing reads BitmapData.meshes; the rule checks that nothing else does.
    static int SpriteBatchMeshes(ModuleDefinition module)
    {
        TypeDefinition bitmapData = GameType(module, SpriteBitmapData);
        if (bitmapData == null) return 0;
        const string rule = "R7";
        MethodDefinition make = SpriteMethod(rule, bitmapData, "CreateMultiMeshFrames", new[] { SpriteFrames });
        if (!make.IsStatic || make.ReturnType.FullName != "UnityEngine.Mesh[]")
            throw SpriteRule(rule, "CreateMultiMeshFrames is not static Mesh[] (TextureFrames)");
        if (SpriteCount(make, i => SpriteCalls(i, "Mesh", ".ctor")) < 1 || SpriteCount(make, i => SpriteCalls(i, "Mesh", "MarkDynamic")) < 1)
            throw SpriteRule(rule, "CreateMultiMeshFrames does not make Meshes");
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition m in type.Methods.Where(m => m.HasBody && m != make))
            {
                bool drawMethod = type.FullName == SpriteBitmapData && (m.Name == "unityDraw" || m.Name == "unityDrawClippedWidth");
                if (!drawMethod && m.Body.Instructions.Any(i => (i.OpCode == OpCodes.Ldfld || i.OpCode == OpCodes.Ldflda) && SpriteUsesField(i, "meshes")))
                    throw SpriteRule(rule, type.Name + "." + m.Name + " reads BitmapData.meshes, which R7 leaves empty");
            }
        TypeDefinition frames = make.Parameters[0].ParameterType.Resolve();
        FieldDefinition uvx = frames.Fields.SingleOrDefault(f => f.Name == "uvx" && f.FieldType.FullName == "System.Double[]");
        if (uvx == null) throw SpriteRule(rule, "TextureFrames has no double[] uvx");
        var code = new List<Instruction>
        {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, module.ImportReference(uvx)),
            Instruction.Create(OpCodes.Ldlen),
            Instruction.Create(OpCodes.Conv_I4),
            Instruction.Create(OpCodes.Ldc_I4_4),
            Instruction.Create(OpCodes.Mul),
            Instruction.Create(OpCodes.Newarr, ((ArrayType)make.ReturnType).ElementType),
            Instruction.Create(OpCodes.Ret),
        };
        SpriteReplaceBody(make, code);
        return 1;
    }

    // R9. UnityMain.OnRenderObject: after its one call of Main.draw(),
    //   ldsfld UnityMain.m_RenderCamera; call SpriteBatch.Flush(Camera)
    // which draws the frame's queued screen sprites, one Graphics.DrawMesh per run, for the camera the game drew for.
    static int SpriteBatchFlush(ModuleDefinition module)
    {
        TypeDefinition unityMain = GameType(module, SpriteUnityMain);
        TypeDefinition main = GameType(module, SpriteMain);
        if (unityMain == null || main == null) return 0;
        const string rule = "R9";
        MethodDefinition render = SpriteMethod(rule, unityMain, "OnRenderObject", new string[0]);
        Instruction[] draws = render.Body.Instructions.Where(i =>
        {
            var called = i.Operand as MethodReference;
            return (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && called != null && called.Name == "draw" &&
                   called.Parameters.Count == 0 && called.DeclaringType.FullName == SpriteMain;
        }).ToArray();
        if (draws.Length != 1) throw SpriteRule(rule, "UnityMain.OnRenderObject calls Main.draw() " + draws.Length + " times, not once");
        if (((MethodReference)draws[0].Operand).ReturnType.FullName != "System.Void") throw SpriteRule(rule, "Main.draw() returns a value");
        FieldDefinition camera = unityMain.Fields.SingleOrDefault(f => f.Name == "m_RenderCamera" && f.IsStatic && f.FieldType.FullName == "UnityEngine.Camera");
        if (camera == null) throw SpriteRule(rule, "UnityMain has no static Camera m_RenderCamera");
        var code = new List<Instruction>();
        new SpriteCall(code).Static(module, camera).Call(module, rule, "Flush");
        ILProcessor il = render.Body.GetILProcessor();
        InsertAfter(il, draws[0], code.ToArray());

        // try { Main.draw(); } catch (Exception) { SpriteBatch.Discard(); throw; }
        // A draw that throws part way leaves its sprites queued, and the next frame's Flush would draw them under
        // its own. The try starts with the load of the instance Main.draw() is called on, where the stack is empty.
        Instruction start = draws[0];
        if (((MethodReference)draws[0].Operand).HasThis)
        {
            start = draws[0].Previous;
            Code load = start == null ? Code.Nop : start.OpCode.Code;
            bool simple = load == Code.Ldloc || load == Code.Ldloc_S || load == Code.Ldloc_0 || load == Code.Ldloc_1 ||
                          load == Code.Ldloc_2 || load == Code.Ldloc_3 || load == Code.Ldsfld;
            Code before = start == null || start.Previous == null ? Code.Nop : start.Previous.OpCode.Code;
            bool empty = before == Code.Stloc || before == Code.Stloc_S || before == Code.Stloc_0 || before == Code.Stloc_1 ||
                         before == Code.Stloc_2 || before == Code.Stloc_3 || before == Code.Nop || before == Code.Stsfld;
            if (!simple || !empty) throw SpriteRule(rule, "Main.draw()'s instance is not loaded by one ldloc or ldsfld on an empty stack");
        }
        foreach (Instruction i in render.Body.Instructions)
            if (i.Operand == draws[0] || (i.Operand is Instruction[] && ((Instruction[])i.Operand).Contains(draws[0])))
                throw SpriteRule(rule, "a branch goes to Main.draw()'s call itself");
        Instruction after = code[0];
        Instruction handler = Instruction.Create(OpCodes.Call, CompatMethod(module, "SpriteBatch", "Discard"));
        InsertAfter(il, draws[0], Instruction.Create(OpCodes.Leave, after), handler, Instruction.Create(OpCodes.Rethrow));
        render.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = start,
            TryEnd = handler,
            HandlerStart = handler,
            HandlerEnd = after,
            CatchType = new TypeReference("System", "Exception", module, module.TypeSystem.CoreLibrary),
        });
        return 1;
    }

    // R10. Gfx.saturateImage(BitmapData, double): builds a ColorMatrixFilter (a double[20], the filter, a Point) and
    // stores it in the image's bmfilter through applyFilter, for the PC build's unityDraw to pick up. After R8 nothing
    // reads bmfilter, so the call only makes garbage; Button.draw makes it for every locked button every frame (the
    // achievements list, 32 icons). Its body becomes
    //   return image;
    // The rule checks that the method is that filter (one applyFilter call), that it returns its image and does nothing
    // else that outlives it (SpriteFilterEffects), and that nothing reads bmfilter.
    static int SpriteBatchFilters(ModuleDefinition module)
    {
        TypeDefinition gfx = GameType(module, SpriteGfx);
        TypeDefinition bitmapData = GameType(module, SpriteBitmapData);
        if (gfx == null || bitmapData == null) return 0;
        const string rule = "R10";
        MethodDefinition saturate = SpriteMethod(rule, gfx, "saturateImage", new[] { SpriteBitmapData, "System.Double" });
        if (!saturate.IsStatic || saturate.ReturnType.FullName != SpriteBitmapData)
            throw SpriteRule(rule, "Gfx.saturateImage is not static BitmapData (BitmapData, double)");
        if (SpriteCount(saturate, i => SpriteCalls(i, "BitmapData", "applyFilter")) != 1)
            throw SpriteRule(rule, "Gfx.saturateImage does not call BitmapData.applyFilter once");
        SpriteFilterEffects(rule, saturate);
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition m in type.Methods.Where(m => m.HasBody))
                if (m.Body.Instructions.Any(i => (i.OpCode == OpCodes.Ldfld || i.OpCode == OpCodes.Ldflda) && SpriteUsesField(i, "bmfilter")))
                    throw SpriteRule(rule, type.Name + "." + m.Name + " reads BitmapData.bmfilter, which R10 leaves unset");
        SpriteReplaceBody(saturate, new List<Instruction> { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ret) });
        return 1;
    }

    // What `return image;` must keep of Gfx.saturateImage, checked on its body as recovered: it returns its first
    // argument, and nothing it does outlives the call but applyFilter's store. Its body (Steam build 24040403) makes a
    // double[20], a ColorMatrixFilter with Type.GetTypeFromHandle and FormatterServices.GetUninitializedObject (Rewind's
    // inlined constructor) and stores the matrix into that filter's cmfmat and cmfoff (Matrix4x4, Vector4 fields), makes
    // a Point, then image.applyFilter(image, image.rect, point, filter.getFilter()); return image. Before (Nov 2026)
    // only the applyFilter count was checked: a body that returned a new bitmap or also changed the image would have
    // been replaced as well. applyFilter's body must be the one store of bmfilter, and getFilter's `return this`.
    static void SpriteFilterEffects(string rule, MethodDefinition saturate)
    {
        string[] calls =
        {
            "System.Type::GetTypeFromHandle", "System.Runtime.Serialization.FormatterServices::GetUninitializedObject",
            "BitmapFilter::getFilter", "BitmapData::applyFilter", "Point::.ctor",
        };
        string[] stored = { "ColorMatrixFilter", "Matrix4x4", "Vector4" };
        Func<string, Exception> changed = what => SpriteRule(rule, "Gfx.saturateImage " + what + ", which `return image;` would drop");
        foreach (Instruction i in saturate.Body.Instructions)
        {
            OpCode op = i.OpCode;
            var method = i.Operand as MethodReference;
            var field = i.Operand as FieldReference;
            var parameter = i.Operand as ParameterDefinition;
            if (op == OpCodes.Ret && (i.Previous == null || i.Previous.OpCode != OpCodes.Ldarg_0))
                throw changed("returns something other than its image argument");
            if ((op == OpCodes.Starg || op == OpCodes.Starg_S || op == OpCodes.Ldarga || op == OpCodes.Ldarga_S) && parameter != null && parameter.Index == 0)
                throw changed("stores into its image argument");
            if (method != null && (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj))
            {
                string name = (method.DeclaringType.Namespace.StartsWith("System") ? method.DeclaringType.FullName : method.DeclaringType.Name) + "::" + method.Name;
                if (!calls.Contains(name))
                    throw changed("calls " + method.FullName);
            }
            else if (op == OpCodes.Calli || op == OpCodes.Jmp || op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn)
                throw changed("has " + op.Name);
            if (op == OpCodes.Stsfld || op == OpCodes.Stobj || op == OpCodes.Cpblk || op == OpCodes.Initblk || op.Name.StartsWith("stind"))
                throw changed("stores outside its own objects (" + op.Name + ")");
            if (op == OpCodes.Stfld && (field == null || !stored.Contains(field.DeclaringType.Name)))
                throw changed("stores " + (field != null ? field.FullName : "a field") + ", not one of the filter it makes");
            if (op.Name.StartsWith("stelem") && op != OpCodes.Stelem_R8 && !(op == OpCodes.Stelem_Any && (i.Operand as TypeReference) != null && ((TypeReference)i.Operand).FullName == "System.Double"))
                throw changed("stores into an array other than its double[] (" + op.Name + ")");
        }
        MethodDefinition apply = saturate.Body.Instructions.Select(i => i.Operand as MethodReference)
            .First(m => m != null && m.Name == "applyFilter").Resolve();
        if (apply == null || !apply.HasBody || apply.Body.Instructions.Any(i => i.Operand is MethodReference) ||
            apply.Body.Instructions.Count(i => i.OpCode == OpCodes.Stfld || i.OpCode == OpCodes.Stsfld || i.OpCode.Name.StartsWith("stind") || i.OpCode == OpCodes.Stobj) != 1 ||
            apply.Body.Instructions.Count(i => i.OpCode == OpCodes.Stfld && SpriteUsesField(i, "bmfilter")) != 1)
            throw SpriteRule(rule, "BitmapData.applyFilter does more than store its filter in bmfilter");
        MethodDefinition get = saturate.Body.Instructions.Select(i => i.Operand as MethodReference)
            .First(m => m != null && m.Name == "getFilter").Resolve();
        if (get == null || !get.HasBody || !get.Body.Instructions.Where(i => i.OpCode != OpCodes.Nop).Select(i => i.OpCode)
                .SequenceEqual(new[] { OpCodes.Ldarg_0, OpCodes.Ret }))
            throw SpriteRule(rule, "BitmapFilter.getFilter is not `return this;`");
    }
}
