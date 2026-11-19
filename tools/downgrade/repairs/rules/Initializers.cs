using System.Collections.Generic;
using System.Linq;
using Repairs;

// The asset-table and other static initializers (staticInit*): generated code, thousands of sites of
// a few shapes, all repaired by one recipe.
// - new T[n]: T from the user's GameAssembly.dll at build time (native/array_facts.py, apply --facts),
//   checked against the markers in count, order and length;
// - write barriers: skipped (Site.SkipBarrier);
// - element stores (__ArraySetAt, __store4, __store8): the markers' own text, compiled (Expr.cs), with
//   Rewind's temporaries tN bound to locals N (a wrong binding fails the compile's type checks).
// The table holds each method and its body hash, which must match exactly: a change to the method
// fails the run (regenerate the facts, re-check, re-pin). Nothing of the tables is here.
//
// Not covered, so not listed (11 markers on the 6000.0.60f1 output): AssetsPack.staticInit15 (the
// native code allocates twice where Rewind has one marker), AssetsStaticMap.staticInitPrep (objects
// from another unidentified helper, 0x1800E3F30) and Controls.staticInit (a store through a value
// Rewind keeps as an address). They need per-method rules.
static partial class Rules
{
    static readonly Dictionary<string, string> initializers = new Dictionary<string, string>
    {
        { "com.bitmapbureau.terminator.AssetsStaticMap::staticInit2()", "91ee3a94" },
        { "com.bitmapbureau.terminator.ui.FontsPack::staticInit()", "eb3e729d" },
        { "com.bitmapbureau.terminator.Assets::staticInit()", "dc565083" },
        { "com.bitmapbureau.terminator.Assets::staticInit3()", "975a45fd" },
        { "com.bitmapbureau.terminator.Assets::staticInit4()", "59f0fecc" },
        { "com.bitmapbureau.terminator.Assets::staticInit5()", "3f10a64d" },
        { "com.bitmapbureau.terminator.Assets::staticInit12()", "e32b7059" },
        { "com.bitmapbureau.terminator.Assets::staticInit13()", "c385e223" },
        { "com.bitmapbureau.terminator.Assets::staticInit14()", "74fc0332" },
        { "com.bitmapbureau.terminator.Assets::staticInit15()", "159fdffa" },
        { "com.bitmapbureau.terminator.AssetsEmbed::staticInit()", "10ed8f47" },
        { "com.bitmapbureau.terminator.AssetsPack::staticInit()", "b99b9054" },
        { "com.bitmapbureau.terminator.AssetsPack::staticInit3()", "f2cb612e" },
        { "com.bitmapbureau.terminator.AssetsPack::staticInit4()", "ec87321a" },
        { "com.bitmapbureau.terminator.AssetsPack::staticInit14()", "7de20072" },
        { "com.bitmapbureau.terminator.AssetsStaticMap::staticInit3()", "3234425f" },
        { "com.bitmapbureau.terminator.AssetsStaticMap::staticInit4()", "6511c1d8" },
        { "com.bitmapbureau.terminator.AssetsStaticMap::staticInit5()", "1210026c" },
        { "com.bitmapbureau.terminator.AssetsStaticMap::staticInit6()", "13b2c874" },
        { "com.bitmapbureau.terminator.AssetsStaticMap::staticInit7()", "d337792e" },
        { "com.bitmapbureau.terminator.AssetsStaticMap::staticInit8()", "e7644803" },
        { "com.bitmapbureau.terminator.AssetsStaticMap::staticInit9()", "7ccd69e1" },
        { "com.bitmapbureau.terminator.AssetsStaticMap::staticInit10()", "a740b16d" },
        { "com.bitmapbureau.terminator.AssetsStaticMap::staticInit11()", "50f4ce9d" },
        { "com.bitmapbureau.terminator.Audio::staticInit()", "c76fe70f" },
        { "com.bitmapbureau.terminator.Gfx::staticInit()", "fe01c002" },
        { "com.bitmapbureau.terminator.Res::staticInit()", "6e336002" },
        { "com.bitmapbureau.terminator.Transition::staticInit()", "3e65b5ce" },
        { "com.bitmapbureau.terminator.ui.Achievements::staticInit()", "6349a105" },
        { "com.bitmapbureau.terminator.ui.Fonts::staticInit()", "010f4cd0" },
        { "com.bitmapbureau.terminator.ui.Text::staticInit()", "77dacde3" },
        { "com.bitmapbureau.terminator.game.Game::staticInit()", "ddf17fba" },
        { "com.bitmapbureau.terminator.game.GameMap::staticInit()", "84fa7801" },
        { "com.bitmapbureau.terminator.game.Sprites::staticInit()", "5845b259" },
        { "com.bitmapbureau.terminator.cutscenes.Attract::staticInit()", "95a522bc" },
        { "com.bitmapbureau.terminator.apps.HighscoreTable::staticInit()", "4af98da9" },
        { "com.bitmapbureau.terminator.apps.MainMenu::staticInit()", "491168ad" },
        { "com.bitmapbureau.terminator.apps.MusicPlayer::staticInit()", "389fabc5" },
    };

    static void Initializers(Engine e)
    {
        foreach (KeyValuePair<string, string> row in initializers)
        {
            MethodRepair m = e.GeneratedMethod(row.Key, row.Value);
            if (m.Method == null) continue;
            List<Marker> markers = Markers.Find(m.Method);
            if (markers.Any(k => k.Text.StartsWith(GeneratedRepairs.Alloc))) m.NewArraysFromNative();
            m.SkipBarriers(markers.Count(k => k.Text.StartsWith("__load4(")));
            m.BindAllSame();
            foreach (string store in new[] { "__ArraySetAt(", "__store4(", "__store8(" })
                m.CompileGenerated(store, markers.Count(k => k.Text.StartsWith(store)));
        }
    }
}
