// The game's sounds, loaded in the background instead of all at start (docs/audio.md, "Start-up").
//
// AssetsEmbed.staticInit loads every one of the game's 930 sounds with AssetsEmbed.loadSound, one after another in the
// first frame: 15.75 s on the PS Vita before anything is on screen (about 12 ms a clip plus 8 MB/s). The rules in
// audio/rewriter/AudioPatches.cs send the game here:
//   A1  staticInit's loadSound(i) in that loop becomes Queue(Assets.soundfilename[i]): the clip is asked for with
//       Resources.LoadAsync, a few at a time, and nothing waits for it
//   A2  Sound.createSoundFromFile's T2DCompat.Sounds.Clip(name) (the platform layer's Resources.Load) becomes Clip(name)
// loadSound is otherwise as it was: the first time the game asks for a sound it makes the Sound, from Clip(name).
// Clip returns the loaded clip at once. A clip still loading is waited for, that one only; a clip whose load has not
// started is loaded there and then. So a sound the game plays is never silent: at worst it starts late.
//
// Order: the UI sounds first, then the music (streamed clips load in a fraction of a millisecond), then the rest;
// after the first InFlight, which start as soon as they are queued. On the console Resources.LoadAsync is no faster
// in all than one Resources.Load after another (15.4 s with 16 in flight, docs/audio.md), but the frames go on while
// it loads. Fewer in flight means a clip asked for early waits on fewer loads ahead of it.
//
// Every clip stays loaded for the whole game, as before: entries holds it, as soundMap held the Sounds.
// The async loads use Resources.LoadAsync directly; a name the rule did not queue goes to Sounds.Clip.
//
// C# 5, as T2DCompat is compiled by the .NET Framework's csc. Part of T2DCompat; see Compat2018.cs.

using System.Collections.Generic;
using UnityEngine;

namespace T2DCompat
{
    public static class SoundLoads
    {
        // Loads in flight at once.
        public static int InFlight = 4;
        // A wait longer than this (ms) is logged one by one; the summary counts every wait.
        public static float LogWaitsOverMs = 20;

        const int Priorities = 3;

        enum State { Pending, Loading, Done }

        sealed class Entry
        {
            public string Name;
            public State State;
            public ResourceRequest Request;
            public AudioClip Clip;
        }

        static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        static readonly Queue<Entry>[] pending = { new Queue<Entry>(), new Queue<Entry>(), new Queue<Entry>() };
        static int queued, loading, done, waits;
        static float started = -1, waitedMs, longestWaitMs;
        static bool reported;

        // A1: the startup loop asks for every sound by name.
        public static void Queue(string name)
        {
            if (name == null || entries.ContainsKey(name)) return;
            if (started < 0) started = Time.realtimeSinceStartup;
            var entry = new Entry { Name = name, State = State.Pending };
            entries[name] = entry;
            pending[Priority(name)].Enqueue(entry);
            queued++;
            Pump();
        }

        // A2: the clip for Sound.createSoundFromFile, which loadSound calls the first time the game asks for a sound.
        public static AudioClip Clip(string name)
        {
            Entry entry;
            if (name == null || !entries.TryGetValue(name, out entry)) return Sounds.Clip(name);
            if (entry.State == State.Done || entry.Clip != null) return entry.Clip;
            float start = Time.realtimeSinceStartup;
            if (entry.State == State.Loading)
            {
                // Stalls until this request is done; its completed event still comes, and finishes the entry.
                entry.Clip = entry.Request.asset as AudioClip;
            }
            else
            {
                // Not started: load it now. Pump skips it when it comes up in its queue.
                entry.Clip = Sounds.Clip(name);
                entry.State = State.Done;
                done++;
            }
            float ms = (Time.realtimeSinceStartup - start) * 1000;
            waits++;
            waitedMs += ms;
            if (ms > longestWaitMs) longestWaitMs = ms;
            if (ms > LogWaitsOverMs)
                Debug.Log(string.Format("T2D sounds: waited {0:0} ms for {1} ({2} of {3} loaded)", ms, name, done, queued));
            Report();
            return entry.Clip;
        }

        // Loaded so far, of those queued, and whether a clip is; for tests (tools/downgrade/audio/vita-audio-test).
        public static int Loaded { get { return done; } }
        public static int Queued { get { return queued; } }

        public static bool Ready(string name)
        {
            Entry entry;
            return name != null && entries.TryGetValue(name, out entry) && entry.State == State.Done;
        }

        static int Priority(string name)
        {
            if (name.StartsWith("audio/ui/")) return 0;
            if (name.StartsWith("audio/music/")) return 1;
            return 2;
        }

        static void Pump()
        {
            for (int p = 0; p < Priorities && loading < InFlight; p++)
            {
                Queue<Entry> queue = pending[p];
                while (queue.Count > 0 && loading < InFlight)
                {
                    Entry entry = queue.Dequeue();
                    if (entry.State != State.Pending) continue;
                    entry.State = State.Loading;
                    entry.Request = Resources.LoadAsync<AudioClip>(entry.Name);
                    loading++;
                    Entry captured = entry;
                    // Raised on the main thread; at once if the request is already done.
                    entry.Request.completed += delegate { Completed(captured); };
                }
            }
        }

        static void Completed(Entry entry)
        {
            if (entry.State != State.Loading) return;
            if (entry.Clip == null && entry.Request != null) entry.Clip = entry.Request.asset as AudioClip;
            if (entry.Clip == null) Debug.LogWarning("T2D sounds: no clip " + entry.Name);
            entry.State = State.Done;
            entry.Request = null;
            loading--;
            done++;
            Pump();
            Report();
        }

        static void Report()
        {
            if (reported || done < queued) return;
            reported = true;
            Debug.Log(string.Format("T2D sounds: {0} loaded in {1:0.00} s; {2} asked for before they were loaded, {3:0} ms waited in all, longest {4:0} ms",
                done, Time.realtimeSinceStartup - started, waits, waitedMs, longestWaitMs));
        }
    }
}
