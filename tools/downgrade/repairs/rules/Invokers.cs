using Mono.Cecil.Cil;
using Repairs;

// Virtual calls through IL2CPP's per-slot invoker thunks, which Rewind leaves as calls to unidentified native
// functions. Each thunk loads the receiver's vtable entry at a fixed offset (VirtualInvokeData: the method pointer, then
// its MethodInfo, 16 bytes a slot from klass+0x138) and passes the caller's register and stack arguments on: so the
// thunk names the slot, and the call site's stores name the arguments. Slots are read from the il2cpp dump's
// "Slot:" lines, fields from its field offsets.
//   0x180003020: [r10+0x1b0], slot 7    0x180002F90: [r10+0x1e0], slot 10    0x180002E30: [r10+0x1f0], slot 11
static partial class Rules
{
    const string ButtonType = "com.bitmapbureau.terminator.ui.Button";
    const string FontType = "com.bitmapbureau.terminator.ui.BmpFont";

    // TextButton.draw(target, x, y): the button's text on BmpFont, slot 7 drawStringLines, 10 draw (a text id),
    // 11 drawString. Native (0x1802B5B40):
    //   0x1802B5D02  bmpFont.drawStringLines(target, textLines, x, y, alignment, ct, 16)      (not a slider, textLines)
    //   0x1802B5D5F  bmpFont.drawString(target, text, x, y, alignment, ct)                    (a slider)
    //   0x1802B5FCD  bmpFont.draw(target, option != 0 ? 533 : 534, left + optionSpacing, bottom, 6, ct)  (an on/off setting)
    //   0x1802B602D  bmpFont.draw(target, option, left + optionSpacing, bottom, 6, ct)       (another setting)
    //   0x1802B60A8  bmpFont.drawString(target, s, ...): merged from two paths, Rewind's V3 the font and V4 the string:
    //                not a setting, no textLines: (text; x, y, alignment); a setting with settingStrings
    //                (settingStrings[option]; left + optionSpacing, bottom, 6); ct on both. `setting` (0x8C) tells them.
    // The statics pointer Rewind reads into V1 is used only as a stray value and goes.
    static void TextButtonDraw(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.ui.TextButton::draw(BitmapData,Double,Double)", "df9968d6");
        m.Site("__native_0x180003020(", "1f3a8d31").With(il =>
        {
            il.Ldloc(6).Arg(0).This().Ldfld("com.bitmapbureau.terminator.ui.TextButton::textLines");
            AtXY(il);
            il.I4(16).Callvirt(FontType + "::drawStringLines(BitmapData,String[],Double,Double,Int32,ColorTransform,Int32)");
        });
        m.Site("__native_0x180002E30(", 0, "b99cbaf7").With(il =>
        {
            il.Ldloc(6).Arg(0).This().Ldfld(ButtonType + "::text");
            AtXY(il);
            il.Callvirt(FontType + "::drawString(BitmapData,String,Double,Double,Int32,ColorTransform)");
        });
        // The id is chosen before the call's arguments go on the stack: IL2CPP 2018 mistypes values held on the stack
        // across a branch.
        VariableDefinition chosen = m.NewLocal("System.Int32");
        m.Site("__native_0x180002F90(", 0, "a34e7895").With(il =>
        {
            Instruction on = il.NewLabel(), id = il.NewLabel();
            il.This().Ldfld(ButtonType + "::option").Brtrue(on).I4(534).Stloc(chosen).Br(id).Place(on).I4(533).Stloc(chosen).Place(id);
            il.This().Ldfld(ButtonType + "::bmpFont").Arg(0).Ldloc(chosen);
            AtOption(il);
            il.Callvirt(FontType + "::draw(BitmapData,Int32,Double,Double,Int32,ColorTransform)");
        });
        m.Site("__native_0x180002F90(", 0, "dd9a97f2").With(il =>
        {
            il.This().Ldfld(ButtonType + "::bmpFont").Arg(0).This().Ldfld(ButtonType + "::option");
            AtOption(il);
            il.Callvirt(FontType + "::draw(BitmapData,Int32,Double,Double,Int32,ColorTransform)");
        });
        m.Site("__native_0x180002E30(", 0, "a84a2180").With(il =>
        {
            Instruction setting = il.NewLabel(), done = il.NewLabel();
            il.This().Ldfld(ButtonType + "::setting").Brtrue(setting);
            il.Ldloc(3).Arg(0).Ldloc(4);
            AtXY(il);
            il.Callvirt(FontType + "::drawString(BitmapData,String,Double,Double,Int32,ColorTransform)").Br(done).Place(setting);
            il.Ldloc(3).Arg(0).Ldloc(4);
            AtOption(il);
            il.Callvirt(FontType + "::drawString(BitmapData,String,Double,Double,Int32,ColorTransform)").Place(done);
        });
        m.Site("__statics()", "f978d700").Delete();
    }

    // x, y, alignment, ct.
    static void AtXY(IL il)
    {
        il.This().Ldfld(ButtonType + "::x").This().Ldfld(ButtonType + "::y").This().Ldfld(ButtonType + "::alignment")
          .This().Ldfld(ButtonType + "::ct");
    }

    // left + optionSpacing, bottom, 6, ct.
    static void AtOption(IL il)
    {
        il.This().Ldfld(ButtonType + "::optionSpacing").Op(OpCodes.Conv_R8).This().Ldfld(ButtonType + "::left").Op(OpCodes.Add);
        il.This().Ldfld(ButtonType + "::bottom").I4(6).This().Ldfld(ButtonType + "::ct");
    }
}
