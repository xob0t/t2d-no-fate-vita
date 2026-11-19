// The platform layer's rules, as data: which of the game's methods the rewriter's platform passes change, and to what
// T2DCompat member. PlatformPasses.cs applies them inside the rewriter (DowngradeAssemblies); compat/check checks them
// against Rewind's assemblies and T2DCompat.dll without the pipeline. docs/platform.md explains each.
//
// Names are the game's own, as Cecil writes them: a type by its full name ("com.twissgreen.unity.UnityMain"), a member
// as Type::name, a callee by its MethodReference.FullName ("System.Boolean Steamworks.SteamUserStats::SetAchievement(
// System.String)"), and a T2DCompat member as Class.Method (the class in namespace T2DCompat).
// Every rule must match: a rule that changes nothing (the game changed) is a NO MATCH, which fails the rewrite.
//
// C# 5, no references beyond mscorlib: the checker compiles this file on its own.
public static class PlatformRules
{
    // The game's assembly they apply to (renamed Recovered.T2D by the rewriter; the passes run on the game assemblies).
    public const string GameAssembly = "Assembly-CSharp";

    // Methods whose bodies become "return the default" (null, false, 0; out parameters zeroed), as StubAssembly stubs a
    // library. A method name of "*" stubs every method of the type and of its nested types (closures, iterators), but
    // not the constructors: the store managers are MonoBehaviours nothing creates once UnityMain.Start no longer asks
    // for SteamManager (Sequences), and their static constructors only fill fields.
    public static readonly string[][] Stubbed =
    {
        // Steam: init, callbacks, leaderboards (the game reads them only with STEAM_BUILD, which BuildFlags turns off).
        new[] { "com.twissgreen.unity.SteamManager", "*" },
        // Epic Games and GOG Galaxy: compiled in, never set up by this build; getInstance would make a GameObject.
        new[] { "com.twissgreen.unity.EpicManager", "*" },
        new[] { "com.twissgreen.unity.GalaxyManager", "*" },
        // SetAchievement and an inlined SteamUserStats.StoreStats through Steam's interface (Rewind gaps).
        new[] { "com.twissgreen.unity.Platform", "awardTrophy" },
        new[] { "com.twissgreen.unity.Platform", "mapTrophiesAndAward" },
        // DLC: Steam's BIsDlcInstalled (a Rewind gap); the PS4's and Xbox One's asset bundles (no callers).
        new[] { "com.twissgreen.unity.UnityMain", "dlcSteamFound" },
        new[] { "com.twissgreen.unity.UnityMain", "dlcPS4LoadAssetBundle" },
        new[] { "com.twissgreen.unity.UnityMain", "dlcXB1LoadAssetBundle" },
        // ISteamUtils.IsSteamRunningOnSteamDeck (a Rewind gap): STEAM_DECK stays false.
        new[] { "com.twissgreen.engine.utils.Utils", "setSteamDeck" },
        // PlayerPrefs arrays for the PlayerPrefs save mode (SaveData.SAVE_MODE_UNITY_PLAYER_PREFS), which this build never
        // uses: SaveData.saveMethod() is SAVE_MODE_UNITY_BINARY. Rewind gaps (array allocations) and an inlined string
        // index (String._firstChar), on which UnityLinker stopped.
        new[] { "com.twissgreen.unity.PlayerPrefsX", "*" },
    };

    // Calls in every method of the game's assembly that go to a T2DCompat method of the same parameters and result:
    // { callee, T2DCompat class, method }.
    public static readonly string[][] Calls =
    {
        new[] { "System.Boolean Steamworks.SteamUserStats::SetAchievement(System.String)", "Store", "SetAchievement" },
        new[] { "System.Boolean Steamworks.SteamUserStats::SetStat(System.String,System.Int32)", "Store", "SetStat" },
    };

    // A run of instructions in one method that becomes one call: { method, T2DCompat class, method, instructions... },
    // each instruction its opcode and operand as Cecil prints them. Each must match exactly once.
    public static readonly string[][] Sequences =
    {
        // UnityMain.Start: if (!SteamManager.getInstance().m_bInitialized) return; ... Main.main();
        // becomes if (!Store.Ready()) return; the game starts without Steam, and no SteamManager is made.
        new[]
        {
            "com.twissgreen.unity.UnityMain::Start", "Store", "Ready",
            "call com.twissgreen.unity.SteamManager com.twissgreen.unity.SteamManager::getInstance()",
            "ldfld System.Boolean com.twissgreen.unity.SteamManager::m_bInitialized",
        },
    };

    // Methods that become a call of another method of their type with the same parameters: { method, target }.
    public static readonly string[][] Forwards =
    {
        // The language from Steam's GetCurrentGameLanguage, else from Application.systemLanguage (systemLanguageStandalone,
        // which systemLanguageSteam itself falls back to). The Steam part is a Rewind gap and needs Steam.
        new[] { "com.twissgreen.engine.utils.Utils::systemLanguageSteam", "systemLanguageStandalone" },
    };

    // Methods whose bodies become stores of the type's static fields from T2DCompat getters of the fields' types:
    // { method, T2DCompat class, field, getter, field, getter, ... }.
    public static readonly string[][] Stores =
    {
        // Utils.initUtils: the platform switches the Steam build sets (STEAM_BUILD true, STEAM_DECK from Steam, a Rewind
        // gap), from the player instead (T2DCompat BuildFlags). The PS4/PS5/Xbox switches it sets only on those consoles
        // stay false.
        new[]
        {
            "com.twissgreen.engine.utils.Utils::initUtils", "BuildFlags",
            "CONSOLE_BUILD", "ConsoleBuild", "STEAM_BUILD", "SteamBuild", "STEAM_DECK", "SteamDeck",
            "FULL_SCREEN", "FullScreen", "NO_EXIT", "NoExit", "VIBRATION_BUILD", "Vibration", "CIRCLE_ENTER", "CircleEnter",
        },
        // UnityMain.OnPreRender: crt_4hscale = RenderTexture.GetTemporary(398, 896, 0, ARGB32, Default), the CRT filter's
        // target, which IL2CPP inlined into Unity 6's RenderTextureDescriptor internals (graphics formats, mipCount's
        // backing field), on which UnityLinker stopped; and a Rewind gap (the static field's write barrier). T2DCompat
        // Display.CrtTarget makes the same texture through 2018.2's GetTemporary.
        new[] { "com.twissgreen.unity.UnityMain::OnPreRender", "Display", "crt_4hscale", "CrtTarget" },
    };

    // Methods that return right after a call whose result they discard: what follows is Steam-only (an inlined
    // SteamUserStats.StoreStats through Steam's interface, Rewind gaps). { method, callee }; the method returns void,
    // and the callee's result is popped by the next instruction. The code left unreachable is removed. Runs before Calls.
    public static readonly string[][] Cuts =
    {
        // Achievements.unlock: the game's own unlock (the save, the banner), then SetAchievement and StoreStats.
        new[] { "com.bitmapbureau.terminator.ui.Achievements::unlock", "System.Boolean Steamworks.SteamUserStats::SetAchievement(System.String)" },
    };

    // Sound.createSoundFromFile(name): return Sounds.Clip(name) as a Sound, as the game builds one from a clip in
    // Resources (compat/Sounds.cs). { method, Sound's constructor, Sound's name field, T2DCompat class, method }.
    public static readonly string[] SoundLoader =
    {
        "com.twissgreen.engine.media.Sound::createSoundFromFile",
        "System.Void com.twissgreen.engine.media.Sound::.ctor(UnityEngine.AudioClip)",
        "clipname",
        "Sounds", "Clip",
    };

    // Libraries the pipeline leaves out (their roots "-InControl", "-Steamworks"): the rewriter's naming convention sends
    // their types to T2DCompat, Library.X to T2DCompat.Library_X: InControl's to compat/InControl.cs (the API the game
    // uses, over Unity's Input), Steamworks.NET's to compat/Steamworks.cs (the types left once this pass has run).
    public static readonly string[] ReplacedLibraries = { "InControl", "Steamworks" };

    // The library whose members the game calls through T2DCompat without a rule (InControl's), as against Steamworks,
    // whose calls the rules above remove.
    public const string ReplacedApi = "InControl";
}
