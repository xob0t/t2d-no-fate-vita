using System;
using System.Collections.Generic;

// Strings the game makes again every frame from the same values, kept (rewriter/MenuStrings.cs, M2 and M3): the high
// score table's times (Utils.getTimeString), its names' characters and its "(" + rank + ")". At most MaxTimes and
// MaxConcats of each; past that the table starts again.
// Part of T2DCompat; see Compat2018.cs.
namespace T2DCompat
{
    public static class TextMemo
    {
        public const int MaxTimes = 1024, MaxConcats = 1024;
        static readonly Dictionary<double, string> times = new Dictionary<double, string>();

        // The string getTimeString made for ms before, or null.
        public static string Time(double ms)
        {
            string s;
            return times.TryGetValue(ms, out s) ? s : null;
        }

        // What getTimeString returns for ms, kept; returns it.
        public static string KeepTime(string s, double ms)
        {
            if (times.Count >= MaxTimes) times.Clear();
            times[ms] = s;
            return s;
        }

        static readonly string[] chars = new string[65536];

        // s.Substring(pos, 1) as a kept string, when len is 1 and pos is inside s; null otherwise (the caller then
        // calls StringExt.substr as before).
        public static string Char(string s, int pos, int len)
        {
            if (s == null || len != 1 || pos < 0 || pos >= s.Length) return null;
            char c = s[pos];
            return chars[c] ?? (chars[c] = new string(c, 1));
        }

        struct Pair : IEquatable<Pair>
        {
            public string A, B;
            public bool Equals(Pair o) { return string.Equals(A, o.A) && string.Equals(B, o.B); }
            public override bool Equals(object o) { return o is Pair && Equals((Pair)o); }
            public override int GetHashCode() { return (A == null ? 0 : A.GetHashCode()) * 31 + (B == null ? 0 : B.GetHashCode()); }
        }

        static readonly Dictionary<Pair, string> concats = new Dictionary<Pair, string>();

        // String.Concat(a, b), kept by the two strings' values.
        public static string Concat(string a, string b)
        {
            var key = new Pair { A = a, B = b };
            string s;
            if (concats.TryGetValue(key, out s)) return s;
            if (concats.Count >= MaxConcats) concats.Clear();
            s = string.Concat(a, b);
            concats[key] = s;
            return s;
        }
    }
}
