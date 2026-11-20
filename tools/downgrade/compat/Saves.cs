using System;
using System.IO;
using UnityEngine;

// Where the game keeps its saves, and what time it is. Copied from butcher-vita's compat/Saves.cs (same author,
// MIT), with the PS Vita's folder for this game.
// The rewriter (Redirect.cs) sends every Application.persistentDataPath read in the game's assemblies here. The game
// reads it only to write and read its two BinaryFormatter files, persistentDataPath + "/Terminator_SaveSettings.dat"
// and "/Terminator_SaveStats.dat" (SaveSettings and SaveStats; SaveData.saveMethod() is SAVE_MODE_UNITY_BINARY on
// every platform this build knows, so PlayerPrefs, the console blobs and the Switch save are never used). The PS
// Vita's player leaves persistentDataPath empty, so there it is ux0:data/T2DNoFate, which this creates. A scripted
// launch (one that applied flags from args.txt: drive.py, vita.py args) keeps its saves apart, in
// ux0:data/T2DNoFate/test, so a test that sets the story's progress and finishes a level never writes over the player's
// saves; it starts from a copy of the player's files the first time (or when the test folder lacks one), and
// -t2d-real-saves runs it on the player's own saves instead, loudly. The flags, the log and the screenshots stay in
// ux0:data/T2DNoFate (FilesFolder). The player's own launch from the LiveArea has no args.txt and uses the saves
// there as before. Desktop
// players keep the game's own path (%USERPROFILE%\AppData\LocalLow\<company>\<product>), so a PC save and a test
// player's are the same files.
// Also the clock (Now, UtcNow), which the redirect sends here if the game reads it; this game's code doesn't.
// Part of T2DCompat; see Compat2018.cs.
namespace T2DCompat
{
    public static class Saves
    {
        public const string VitaFolder = "ux0:data/T2DNoFate";
        public const string TestFolder = VitaFolder + "/test";
        public const string RealSavesFlag = "-t2d-real-saves";
        // The game's save files (SaveSettings, SaveStats), copied into TestFolder when it lacks them.
        static readonly string[] SaveFiles = { "Terminator_SaveSettings.dat", "Terminator_SaveStats.dat" };
        static string vitaSaves;

        static bool Desktop
        {
            get
            {
                switch (Application.platform)
                {
                    case RuntimePlatform.WindowsPlayer:
                    case RuntimePlatform.OSXPlayer:
                    case RuntimePlatform.LinuxPlayer:
                    case RuntimePlatform.WindowsEditor:
                    case RuntimePlatform.OSXEditor:
                    case RuntimePlatform.LinuxEditor:
                        return true;
                    default:
                        return false;
                }
            }
        }

        // Where the game's saves go: the rewriter sends every Application.persistentDataPath read here.
        public static string PersistentDataPath()
        {
            string path = Application.persistentDataPath;
            if (Desktop || !string.IsNullOrEmpty(path)) return path;
            if (vitaSaves == null) vitaSaves = ChooseVitaSaves();
            return vitaSaves;
        }

        // Where the player's other files go (the probe's screenshots): VitaFolder on the PS Vita, whatever the saves.
        public static string FilesFolder()
        {
            string path = Application.persistentDataPath;
            if (Desktop || !string.IsNullOrEmpty(path)) return path;
            Directory.CreateDirectory(VitaFolder);
            return VitaFolder;
        }

        // Chosen once, before the first save is read or written: Flags reads args.txt at its first question.
        static string ChooseVitaSaves()
        {
            Directory.CreateDirectory(VitaFolder);
            if (!Flags.Scripted) return VitaFolder;
            if (Flags.Has(RealSavesFlag))
            {
                Debug.LogWarning("T2D_SAVES WARNING: a scripted launch on the player's own saves (" + RealSavesFlag + "): " + VitaFolder);
                return VitaFolder;
            }
            try
            {
                Directory.CreateDirectory(TestFolder);
                foreach (string name in SaveFiles)
                {
                    string real = VitaFolder + "/" + name, test = TestFolder + "/" + name;
                    if (!File.Exists(test) && File.Exists(real))
                    {
                        File.Copy(real, test);
                        Debug.Log("T2D_SAVES copied the player's " + name + " into " + TestFolder);
                    }
                }
            }
            catch (Exception e)
            {
                // Never the player's saves for a scripted launch: the game finds no save in the test folder, or fails
                // to write it, and the player's stay as they are.
                Debug.LogWarning("T2D_SAVES " + TestFolder + ": " + e.GetType().Name + ": " + e.Message);
            }
            Debug.Log("T2D_SAVES " + TestFolder + ": a scripted launch (args.txt) keeps its own saves (" + RealSavesFlag + " for the player's)");
            return TestFolder;
        }

        // DateTime.Now and DateTime.UtcNow: the rewriter sends every read in the game's assemblies here.
        // The PS Vita's IL2CPP leaves GetSystemTimeAsFileTime unimplemented (libil2cpp os/PSP2/Time.cpp),
        // so both said 1 January 1601 there, and every save slot showed "1/1/1601". A file's
        // modification time is right on the console (sceIoGetstat, UTC), so the clock is a file
        // written in the saves folder plus a stopwatch since. The console's time zone is out of
        // reach, so Now is UTC too (with Kind Utc).
        // Other players read the system clock as before.
        // The stopwatch is the process's time (IL2CPP's PSP2 GetTicks100NanosecondsMonotonic is
        // sceKernelGetProcessTime), which stands still while the console sleeps: on it alone a save after
        // a night's sleep would be dated the evening before. The file is written again on each
        // call that isn't part of a burst (calls are rare: the log's start, each save), and when the
        // file's time is ahead of the clock by over 2 s the clock moves on to it. It only moves forward, so the slots' times keep their order (Continue takes the
        // latest). The re-read doesn't wait on the process's time, which the sleep doesn't add to (a save
        // soon after waking would keep the old date).
        public static DateTime Now()
        {
#if T2D_VITA
            return VitaUtcNow();
#else
            return DateTime.Now;
#endif
        }

        public static DateTime UtcNow()
        {
#if T2D_VITA
            return VitaUtcNow();
#else
            return DateTime.UtcNow;
#endif
        }

#if T2D_VITA
        static readonly object clockGate = new object();
        static System.Diagnostics.Stopwatch sinceStart;
        static DateTime start;
        static TimeSpan lastRead, lastCall;
        static bool clockFailed, rereadFailed;
        // A call within Burst of the process's time after the one before is part of a burst (a save
        // asks Now and UtcNow together; a caller that asks every frame): it reads the file only
        // ReadEvery after the last read, so such a caller writes the card at most every 10 s. A call
        // after a pause, the sleep's included (the player has to wake the console and get to a save),
        // reads it at once.
        static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(10), Burst = TimeSpan.FromSeconds(1), Slack = TimeSpan.FromSeconds(2);

        // The console's clock (UTC), from a file's modification time.
        static DateTime FileTime()
        {
            Directory.CreateDirectory(VitaFolder);
            string file = VitaFolder + "/clock";
            File.WriteAllBytes(file, new byte[1]);
            return DateTime.SpecifyKind(File.GetLastWriteTimeUtc(file), DateTimeKind.Utc);
        }

        static DateTime VitaUtcNow()
        {
            lock (clockGate)
            {
                if (sinceStart == null && !clockFailed)
                {
                    try
                    {
                        start = FileTime();
                        sinceStart = System.Diagnostics.Stopwatch.StartNew();
                        lastRead = lastCall = TimeSpan.Zero;
                    }
                    catch (Exception e)
                    {
                        clockFailed = true;
                        Debug.Log("T2D_CLOCK no file time for the date (" + e.GetType().Name + ": " + e.Message + ")");
                    }
                }
                if (sinceStart == null) return DateTime.UtcNow;
                TimeSpan elapsed = sinceStart.Elapsed;
                bool apart = elapsed - lastCall >= Burst;
                lastCall = elapsed;
                if (apart || elapsed - lastRead >= ReadEvery)
                {
                    lastRead = elapsed;
                    try
                    {
                        TimeSpan behind = FileTime() - (start + elapsed);
                        if (behind > Slack)
                        {
                            start += behind;  // the console slept: the stopwatch left that time out
                            Debug.Log("T2D_CLOCK " + behind.TotalSeconds.ToString("F0") + " s the stopwatch missed (the console slept): clock moved on");
                        }
                    }
                    catch (Exception e)
                    {
                        // The stopwatch's time stands; the next call apart from this one, or after ReadEvery, tries again.
                        if (!rereadFailed) Debug.Log("T2D_CLOCK file time failed (" + e.GetType().Name + ": " + e.Message + "): the stopwatch's time");
                        rereadFailed = true;
                    }
                }
                return start + sinceStart.Elapsed;
            }
        }
#endif
    }
}
