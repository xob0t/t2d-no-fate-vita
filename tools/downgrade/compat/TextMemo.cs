using System.Collections.Generic;

// Strings the game makes again every frame from the same values, kept (rewriter/MenuStrings.cs, M2): the high score
// table's times, from Utils.getTimeString. At most MaxTimes; past that the table starts again.
// Part of T2DCompat; see Compat2018.cs.
namespace T2DCompat
{
    public static class TextMemo
    {
        public const int MaxTimes = 1024;
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
    }
}
