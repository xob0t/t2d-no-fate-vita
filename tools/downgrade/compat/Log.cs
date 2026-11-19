using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

// The player's log on the PS Vita, where Unity's log goes only to a development kit's console. After butcher-vita's
// compat/ProbeFiles.cs (same author, MIT), without its thread-priority plugin.
// Every message the player logs goes to ux0:data/T2DNoFate/log.txt (vita.py log; the two runs before are log.prev.txt
// and log.prev2.txt): errors and exceptions with their stack traces, a message repeated back to back as one line with
// its count (a repeat that goes on is written again every 5 s), each line with the seconds since the start. It stops at MaxBytes, so an error every frame cannot fill the
// card. A thread of its own writes the lines, since a write to the card now and then takes seconds and the main thread
// would wait for it; Flush writes what is queued from the calling thread. -t2d-log-sync writes on the logging thread.
// Desktop players keep Unity's own log.
// Part of T2DCompat; see Compat2018.cs.
namespace T2DCompat
{
    public static class PlayerLog
    {
        const long MaxBytes = 8 << 20;

        static StreamWriter output;
        static string path;
        static string last;
        static int repeats;
        static double lastAt;
        static long written;
        static bool sync, full;
        static readonly object gate = new object();
        static readonly Queue<string> pending = new Queue<string>();
        static readonly Stopwatch clock = Stopwatch.StartNew();

        // Seconds since T2DCompat started, the time every line carries (Time.realtimeSinceStartup is the main thread's).
        public static double Seconds
        {
            get { return clock.Elapsed.TotalSeconds; }
        }

        static bool installed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        internal static void Install()
        {
            if (installed) return;
            installed = true;
            if (!Flags.Vita) return;
            try
            {
                Directory.CreateDirectory(Saves.VitaFolder);
                string kept = KeepPrevious();
                path = Saves.VitaFolder + "/log.txt";
                output = new StreamWriter(path, false, new UTF8Encoding(false));
                output.WriteLine("Terminator 2D: NO FATE " + Application.version + ", Unity " + Application.unityVersion + ", T2DCompat log, " +
                                 "player up " + Time.realtimeSinceStartup.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " s");
                string[] args = Flags.Args;
                output.WriteLine("args: " + string.Join(" ", args, 1, Math.Max(0, args.Length - 1)));
                if (Flags.Note != null) output.WriteLine("T2D_LOG " + Flags.Note);
                if (kept != null) output.WriteLine("T2D_LOG the logs before weren't all kept: " + kept);
                output.Flush();
                sync = Flags.Has("-t2d-log-sync");
                if (!sync) new Thread(WriteLines) { IsBackground = true, Name = "T2DLog" }.Start();
                Application.logMessageReceivedThreaded += Write;
                AppDomain.CurrentDomain.UnhandledException += (sender, e) => Debug.Log("T2D_UNHANDLED " + e.ExceptionObject);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("T2DCompat: no log file in " + Saves.VitaFolder + ": " + exception.Message);
            }
        }

        // log.txt becomes log.prev.txt and that log.prev2.txt, so a crashed run's log survives the next launch or two.
        static string KeepPrevious()
        {
            string[] names = { "log.txt", "log.prev.txt", "log.prev2.txt" };
            string failed = null;
            for (int i = names.Length - 1; i > 0; i--)
            {
                try
                {
                    string from = Saves.VitaFolder + "/" + names[i - 1], to = Saves.VitaFolder + "/" + names[i];
                    if (!File.Exists(from)) continue;
                    if (File.Exists(to)) File.Delete(to);
                    File.Move(from, to);
                }
                catch (Exception exception)
                {
                    failed = names[i - 1] + " -> " + names[i] + ": " + exception.Message;
                }
            }
            return failed;
        }

        static void Write(string message, string stack, LogType type)
        {
            lock (gate)
            {
                if (full || output == null) return;
                string entry = type == LogType.Log || type == LogType.Warning ? message : type + ": " + message + "\n" + stack;
                if (entry == last && Seconds - lastAt < 5.0)
                {
                    repeats++;
                    return;
                }
                lastAt = Seconds;
                // In a T2D_TRACE=1 build, the game methods entered before an error (Trace.cs).
                string trail = type == LogType.Log || type == LogType.Warning ? "" : Trace.Trail(24);
                string line = (repeats > 0 ? "  (the line above " + repeats + " more times)\n" : "") +
                              Seconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " " + (entry ?? "").TrimEnd() +
                              (trail.Length > 0 ? "\n  methods entered last, the last first:\n" + trail.TrimEnd() : "");
                last = entry;
                repeats = 0;
                written += line.Length + 1;
                if (written > MaxBytes)
                {
                    full = true;
                    line = null;
                }
                if (sync)
                {
                    Put(line);
                    return;
                }
                pending.Enqueue(line);
                Monitor.PulseAll(gate);  // the writer, and a Flush that waits for it, both wait on gate
            }
        }

        // writing: the writer holds a batch it took from pending and hasn't written yet. flushing: a Flush is draining
        // pending, and the writer takes nothing until it is done. Both under gate.
        static bool writing, flushing;

        static void WriteLines()
        {
            var lines = new List<string>();
            while (true)
            {
                lock (gate)
                {
                    while (pending.Count == 0 || flushing) Monitor.Wait(gate);
                    lines.AddRange(pending);
                    pending.Clear();
                    writing = true;
                }
                bool open = true;
                try
                {
                    lock (output)
                    {
                        foreach (string line in lines)
                            if (!Put(line)) { open = false; break; }
                    }
                }
                finally
                {
                    lock (gate)
                    {
                        writing = false;
                        Monitor.PulseAll(gate);
                    }
                }
                if (!open) return;
                lines.Clear();
            }
        }

        // Writes what is queued from the calling thread (before a deliberate stop). The writer's batch first, which it
        // took before these lines were queued: returning before it was written lost it at a quit just after, or wrote
        // the lines out of order. At most FlushWaitMs for it.
        const int FlushWaitMs = 2000;

        public static void Flush()
        {
            if (sync || output == null) return;
            var lines = new List<string>();
            lock (gate)
            {
                flushing = true;
                int end = Environment.TickCount + FlushWaitMs;
                while (writing)
                {
                    int left = end - Environment.TickCount;
                    if (left <= 0 || !Monitor.Wait(gate, left)) break;
                }
                lines.AddRange(pending);
                pending.Clear();
            }
            try
            {
                lock (output)
                {
                    foreach (string line in lines)
                        if (!Put(line)) return;
                }
            }
            finally
            {
                lock (gate)
                {
                    flushing = false;
                    Monitor.PulseAll(gate);
                }
            }
        }

        // One line, written and flushed; null closes the file with "(log full)". False once closed.
        static bool Put(string line)
        {
            try
            {
                if (line == null)
                {
                    output.WriteLine("(log full)");
                    output.Flush();
                    return false;
                }
                output.WriteLine(line);
                output.Flush();
                return true;
            }
            catch (Exception)
            {
                return true;  // the line is lost; the log goes on
            }
        }
    }
}
