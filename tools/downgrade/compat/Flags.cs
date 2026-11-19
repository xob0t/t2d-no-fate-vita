using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// The probe flags T2DCompat reads (-t2d-*): a Windows player's command line, and on the PS Vita, which passes none, the
// first line of ux0:data/T2DNoFate/args.txt, read once at the first question. Unlike butcher-vita's ProbeFiles the file
// is left as it is; delete it to clear the flags.
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
                        string file = Saves.VitaFolder + "/args.txt";
                        if (File.Exists(file))
                        {
                            string[] lines = File.ReadAllLines(file);
                            if (lines.Length > 0)
                                all.AddRange(lines[0].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.Log("T2D_FLAGS args.txt unread (" + e.GetType().Name + ": " + e.Message + ")");
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
            int at = Array.IndexOf(Args, flag);
            return at >= 0 && at + 1 < Args.Length ? Args[at + 1] : null;
        }
    }
}
