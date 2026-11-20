using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

// The probe flags T2DCompat reads (-t2d-*): a Windows player's command line, and on the PS Vita, which passes none, the
// first line of ux0:data/T2DNoFate/args.txt (vita.py args), read once at the first question. As butcher-vita's
// ProbeFiles does, the launch that reads the file renames it to args.used.txt (and reads it there), so test flags
// apply to one launch only and the user's own launch from the LiveArea gets none; one it can't rename isn't applied.
// Part of T2DCompat; see Compat2018.cs.
namespace T2DCompat
{
    public static class Flags
    {
        public static bool Vita
        {
            get { return Application.platform == RuntimePlatform.PSP2; }
        }

        static string[] args;
        static string note;

        // What happened to args.txt, for the log's header (PlayerLog), or null.
        public static string Note
        {
            get
            {
                string[] unused = Args;
                return note;
            }
        }

        public static string[] Args
        {
            get
            {
                if (args != null) return args;
                var all = new List<string>(Environment.GetCommandLineArgs());
                if (Vita)
                {
                    try
                    {
                        // Claimed first, then read where it went: flags this launch can't rename away would apply
                        // to every launch after it, so a file that stays is not applied (before Nov 2026 it was).
                        string file = Saves.VitaFolder + "/args.txt";
                        if (File.Exists(file))
                        {
                            string used = Saves.VitaFolder + "/args.used.txt";
                            bool claimed = false;
                            try
                            {
                                if (File.Exists(used)) File.Delete(used);
                                File.Move(file, used);
                                claimed = true;
                            }
                            catch (Exception e)
                            {
                                note = "args.txt not applied: it couldn't be renamed (" + e.GetType().Name + ": " + e.Message + ")";
                            }
                            string[] lines = claimed ? File.ReadAllLines(used) : new string[0];
                            if (lines.Length > 0)
                                all.AddRange(lines[0].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
                        }
                    }
                    catch (Exception e)
                    {
                        note = "args.txt unread (" + e.GetType().Name + ": " + e.Message + ")";
                    }
                }
                args = all.ToArray();
                return args;
            }
        }

        public static bool Has(string flag)
        {
            return Array.IndexOf(Args, flag) >= 0;
        }

        // The value after a flag, or null.
        public static string Value(string flag)
        {
            return Word(flag, 1);
        }

        // The nth word after a flag (1 is the value), or null.
        public static string Word(string flag, int nth)
        {
            int at = Array.IndexOf(Args, flag);
            return at >= 0 && at + nth < Args.Length ? Args[at + nth] : null;
        }

        // The nth word after a flag as a number, or fallback when the flag or the word is missing or not a number.
        public static float Number(string flag, int nth, float fallback)
        {
            string word = Word(flag, nth);
            float value;
            return word != null && float.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }
    }
}
