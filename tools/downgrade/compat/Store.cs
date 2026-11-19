using System.Collections.Generic;
using UnityEngine;

// The store services, offline: Steam, Epic Games and GOG Galaxy. The Steam release runs Steamworks.NET through its
// SteamManager, and has Epic's and GOG's managers (EpicManager, GalaxyManager) compiled in but unused. On the port
// every player is offline: no sign-in, no achievements, stats or leaderboards on a store.
// - Steamworks.NET itself is stubbed (pipeline.STUBBED_ON): each method returns its default, and nothing loads
//   steam_api64. The rewriter's platform passes (compat/rewriter/PlatformPasses.cs) keep the game's code from calling it:
//   SteamManager, EpicManager and GalaxyManager do nothing (their methods return defaults), the game's direct calls of
//   SteamUserStats.SetAchievement and SetStat come here, and the Steam-only ends of methods are cut.
// - Ready: UnityMain.Start runs the game (Main.main) only when SteamManager.getInstance().m_bInitialized; the Steam
//   build quits without Steam. The rewriter puts Ready() there: the game always starts, and no SteamManager is made.
// - Utils.initUtils sets the game's platform switches (STEAM_BUILD true on the Steam build); the rewriter makes it set
//   them from BuildFlags here instead. With STEAM_BUILD and EPIC_BUILD false the game is its plain PC build: high
//   scores are local (HighscoreTable reads no leaderboard), and the options menu has the game's own achievements list.
// - Achievements still unlock in the game: Achievements.unlock records them in the save (StatsBlob.achievementUnlocked)
//   and shows its "trophy acquired" banner; only the store call is gone.
//   -t2d-store-log    log each achievement and stat the game reports (T2D_STORE)
// Part of T2DCompat; see Compat2018.cs.
namespace T2DCompat
{
    public static class Store
    {
        static readonly HashSet<string> reported = new HashSet<string>();
        static bool? log;

        static bool Log
        {
            get
            {
                if (log == null) log = Flags.Has("-t2d-store-log");
                return log.Value;
            }
        }

        // SteamManager.getInstance().m_bInitialized in UnityMain.Start.
        public static bool Ready()
        {
            return true;
        }

        // SteamUserStats.SetAchievement(name): not achieved on any store.
        public static bool SetAchievement(string name)
        {
            if (Log && reported.Add(name)) Debug.Log("T2D_STORE achievement " + name + " (offline: not sent)");
            return false;
        }

        // SteamUserStats.SetStat(name, value).
        public static bool SetStat(string name, int value)
        {
            if (Log) Debug.Log("T2D_STORE stat " + name + "=" + value + " (offline: not sent)");
            return false;
        }
    }

    // The values Utils.initUtils gives the game's platform switches (com.twissgreen.engine.utils.Utils), by player.
    // The game reads them as build switches: CONSOLE_BUILD (one player and no store menus), STEAM_BUILD and STEAM_DECK
    // (Steam's leaderboards, the Steam Deck's glyphs), FULL_SCREEN (the options' full-screen item, the window's mode),
    // NO_EXIT (no Exit in the main menu, and Application.Quit is never called), VIBRATION_BUILD, CIRCLE_ENTER (Circle
    // confirms). The PS Vita is not a console here: the game's console paths are its PS4, PS5 and Xbox ones (their
    // stores, their saves), and the port wants its PC behaviour, two players on a PS TV included.
    public static class BuildFlags
    {
        static bool Desktop
        {
            get
            {
                RuntimePlatform platform = Application.platform;
                return platform == RuntimePlatform.WindowsPlayer || platform == RuntimePlatform.OSXPlayer || platform == RuntimePlatform.LinuxPlayer;
            }
        }

        public static bool ConsoleBuild() { return false; }
        public static bool SteamBuild() { return false; }
        public static bool SteamDeck() { return false; }
        public static bool FullScreen() { return Desktop; }
        // A PS Vita application doesn't quit itself: the system's PS button closes it.
        public static bool NoExit() { return Flags.Vita; }
        public static bool Vibration() { return !Flags.Vita; }
        public static bool CircleEnter() { return false; }
    }
}
