using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Repairs;

// The controls tutorial (map 4, "LevelControls"), which story mode plays before its first level, and Sarah's
// animation tables: InitializeArray calls the compiler merged.
static partial class Rules
{
    // ControlsExplanation.setupScene: INPUT_DATA = new int[n] { ... } for the player's sprite, six initializers whose
    // InitializeArray calls the compiler merged into one (0x1803E7D1B): each path loads its own field (a FieldInfo
    // metadata usage) into rdx and jumps there. Rewind keeps the merged handle as __fieldinfo() into V1, stored into
    // V23's value before the call. In the order of the IL, with the native code's lengths and the fields the usages
    // name (FI_5061, FI_2222, FI_3604, FI_6137, FI_2736, FI_6082, read from global-metadata.dat's fieldRefs: all of
    // Assembly-CSharp's <PrivateImplementationDetails>, of 40, 264, 160, 328, 264 and 288 bytes, 4 bytes an element):
    static readonly string[] ControlsInputData = {
        "C9D9D5B2E99A653C9B3FF587B42D109D3F210661913D20862A720E1D573D2E14", // int[10], 0x1803E7CD9
        "56AC79B8255EE768258346073F92B7DB193345BC0B449AD2359BD259F35261FA", // int[66], 0x1803E7D0B (image 405)
        "8DA1D1D9513F4F07E60F67A0391CF4D917434DD91835641CED2D1A694F6AF19A", // int[40], 0x1803E7F39
        "F610B130FF1BF7E26BB38EF4117A06CFBB1EDB5AE4A21DBFFA9E3D4FE2EF9ECA", // int[82], 0x1803E8289
        "6B02F10F48C6A0B4F9F2C704F387E4599512BC867A0F13F8A401C2C26171529F", // int[66], 0x1803E84B9
        "F3A2A5EE86FF7F8E183B2303437371E29703E131C1F367D2EE2D50F5031E632A", // int[72], 0x1803E8739
    };

    static void ControlsExplanationSetupScene(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.scripts.ControlsExplanation::setupScene(Int32)", "46eba9f4");
        MergedInitializeArray(m, m.Sites("__fieldinfo()", 6, "d5b97c54 6b2b9dfd 3d77a562 3ffa13f6 532bad07 7b32b49a"),
            ControlsInputData, 23, 1, "ea26b386");
    }

    // Sarah.changeImage: LAUNCH_POSITIONS = new int[120] { ... } for image 252 (FI_4608, 0x18030D2CB) and new int[16]
    // { ... } for images 241 to 243 (FI_2479, 0x18030D395, which jumps to the first's InitializeArray at 0x18030D2D2),
    // merged as in ControlsExplanation.setupScene; the handle is V8, the merged value V1.
    static void SarahChangeImage(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.sprites.heroes.Sarah::changeImage(Int32)", "0679acb3");
        MergedInitializeArray(m, m.Sites("__fieldinfo()", 2, "c4d348ea 0129a5db"), new[] {
            "B9024A783F0725641EBFD5B2494E0E088F778C5ECA00A6ED97568A05967FC78F", // int[120], 480 bytes
            "6083F6AEBB2C8563C8995123A994C597B1F2B8594FC9A7B9BAA1417E9F184CC9", // int[16], 64 bytes
        }, 8, 1, "05fc2e89");
    }

    // InitializeArray calls the compiler merged: each __fieldinfo() site (`ldstr; call Opaque; stloc <value>`) becomes
    // `ldtoken <the field>; stloc <handle>`, and the merged `<handle>.value = <value>` before the call goes. The fields
    // are <PrivateImplementationDetails>'s, named by the FieldInfo metadata usage each native path loads.
    static void MergedInitializeArray(MethodRepair m, Site[] sites, string[] fields, int handle, int value, string anchorHash)
    {
        TypeDefinition pid = m.Method == null ? null : m.Method.Module.Types.First(t => t.Name == "<PrivateImplementationDetails>");
        for (int i = 0; i < sites.Length; i++)
        {
            FieldDefinition data = pid == null ? null : pid.Fields.First(f => f.Name == fields[i]);
            sites[i].Replace(0, 1, il => il.Emit(Instruction.Create(OpCodes.Ldtoken, data)).Stloc(handle));
        }
        m.Anchor(new[] { "ldloca.s V_" + handle, "ldloc." + value, "stfld System.IntPtr System.RuntimeFieldHandle::value" }, 0, anchorHash)
         .With(il => { });
    }

    // ControlsExplanation.draw (0x1803E9C70):
    // - the font's vtable read for the virtual draw that follows (recovered), shifted, narrowed and popped, as in
    //   MissionBriefing.draw: it goes with what consumes it.
    // - each row of the controls list, through the slot invoker 0x180002E30 (drawString), font Fonts.bmpFontSmallWhite
    //   ([rsp+0xA8], Rewind's V49), target Main.CANVAS_BD, ct null (r15, zero throughout); the row's x is r13 (V50,
    //   260, or 290 for player image 397) and its y esi (V7, controlsListTop, then 22 more a row):
    //     0x1803EB5CC  drawString(CANVAS_BD, button glyph (r14, V15), x - 1, y + 1, 3, null)
    //     0x1803EB616  drawString(CANVAS_BD, action's text (rbp, V53), x + 4 (V69), y, 9, null)
    static void ControlsExplanationDraw(Engine e)
    {
        const string drawString = FontType + "::drawString(BitmapData,String,Double,Double,Int32,ColorTransform)";
        MethodRepair m = e.Method("com.bitmapbureau.terminator.scripts.ControlsExplanation::draw()", "32a6c216");
        m.Site("__vtable()", "26bb70f6").Replace(0, 4, il => { });
        m.Site("__native_0x180002E30(", 0, "933de532").With(il =>
            il.Ldloc(49).Ldsfld("com.bitmapbureau.terminator.Main::CANVAS_BD").Ldloc(15)
              .Ldloc(50).I4(1).Op(OpCodes.Sub).Op(OpCodes.Conv_R8).Ldloc(7).I4(1).Op(OpCodes.Add).Op(OpCodes.Conv_R8)
              .I4(3).Null().Callvirt(drawString));
        m.Site("__native_0x180002E30(", 0, "e6bd7a11").With(il =>
            il.Ldloc(49).Ldsfld("com.bitmapbureau.terminator.Main::CANVAS_BD").Ldloc(53)
              .Ldloc(69).Ldloc(7).Op(OpCodes.Conv_R8).I4(9).Null().Callvirt(drawString));
    }
}
