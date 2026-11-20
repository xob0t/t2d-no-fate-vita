// The game's sounds, loaded in the background instead of all at start (docs/audio.md, "Start-up").
//
// AssetsEmbed.staticInit loads every one of the game's 930 sounds with AssetsEmbed.loadSound, one after another in the
// first frame: 15.75 s on the PS Vita before anything is on screen (about 12 ms a clip plus 8 MB/s). The rules in
// audio/rewriter/AudioPatches.cs send the game here:
//   A1  staticInit's loadSound(i) in that loop becomes Queue(Assets.soundfilename[i]): the clip is asked for with
//       Resources.LoadAsync, a few at a time, and nothing waits for it
//   A2  Sound.createSoundFromFile's T2DCompat.Sounds.Clip(name) (the platform layer's Resources.Load) becomes Clip(name)
//   A3  createSoundFromFile first asks Defer(name): while the clip is still loading, the Sound is made without it, and
//       the game's reads of Sound.snd and Sound.length (its plays, and a cue's length) go through Sound.t2dClip, which
//       takes the clip from Clip(name) the first time
// loadSound is otherwise as it was: the first time the game asks for a sound it makes the Sound. The game makes the
// Sounds of most clips at start (Sounds.staticInit), so without A3 the start waited for each of those clips in turn.
// Clip returns the loaded clip at once. A clip still loading is waited for, that one only; a clip whose load has not
// started is loaded there and then. So a sound the game plays is never silent: at worst it starts late.
//
// Order: the UI sounds and the attract's first cues (AttractCues) first, then the music (streamed clips load in a fraction of a millisecond), then Skynet's
// voice lines, then the rest. Skynet's are the longest clips loaded into memory (up to 15 s, 0.5 MB, stereo), and a
// frame that finishes one takes 100 to 210 ms on the console; queued among the rest, by name, they came last, in the
// attract cutscene's last seconds. Ahead of the rest they load under the splash and the publisher's logo. Then the rest;
// after the first InFlight, which start as soon as they are queued. On the console Resources.LoadAsync is no faster
// in all than one Resources.Load after another (15.4 s with 16 in flight, docs/audio.md), but the frames go on while
// it loads. Fewer in flight means a clip asked for early waits on fewer loads ahead of it.
//
// Every clip stays loaded for the whole game, as before: entries holds it, as soundMap held the Sounds.
// The async loads use Resources.LoadAsync directly; a name the rule did not queue goes to Sounds.Clip.
//
// C# 5, as T2DCompat is compiled by the .NET Framework's csc. Part of T2DCompat; see Compat2018.cs.

using System;
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

        const int Priorities = 4;

        enum State { Pending, Loading, Done }

        sealed class Entry
        {
            public string Name;
            public State State;
            public ResourceRequest Request;
            public AudioClip Clip;
        }

        static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        static readonly Queue<Entry>[] pending = { new Queue<Entry>(), new Queue<Entry>(), new Queue<Entry>(), new Queue<Entry>() };
        static int queued, loading, done, waits, deferred;
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

        // A3: whether Sound.createSoundFromFile makes the Sound without its clip: the clip was queued and is not loaded
        // yet. The Sound's first play takes it from Clip. A clip loaded, or a name not queued, goes to Clip at once.
        public static bool Defer(string name)
        {
            Entry entry;
            if (name == null || !entries.TryGetValue(name, out entry) || entry.State == State.Done || entry.Clip != null) return false;
            deferred++;
            return true;
        }

        // A2: the clip for Sound.createSoundFromFile, which loadSound calls the first time the game asks for a sound,
        // and (A3) for Sound.t2dClip, the first time the game plays a Sound made without its clip.
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
        public static int Deferred { get { return deferred; } }

        public static bool Ready(string name)
        {
            Entry entry;
            return name != null && entries.TryGetValue(name, out entry) && entry.State == State.Done;
        }

        // Effects the attract cutscene plays before the queue reached them (runs aboot1 and aboot2, docs/audio.md): metal_hit
        // at about 11.5 s, its load not started, so Clip's Resources.Load waited about 330 ms; introduction 48 ms.
        static readonly HashSet<string> AttractCues = new HashSet<string> { "audio/sfx/misc/metal_hit", "audio/sfx/misc/introduction" };

        static int Priority(string name)
        {
            if (name.StartsWith("audio/ui/") || AttractCues.Contains(name)) return 0;
            if (name.StartsWith("audio/music/")) return 1;
            if (name.StartsWith("audio/sfx/voices/Skynet/", StringComparison.OrdinalIgnoreCase)) return 2;
            return 3;
        }

        // Whether new loads wait: while the atlas loads pages (Probe.Install sets it to Atlas.Busy). A page's integration
        // on the main thread waited for the clip read in flight, about 200 ms a page while the clips loaded at boot.
        public static bool HoldForPages = true;
        public static Func<bool> Hold;

        // From Atlas, each frame its background loads are done: starts the loads that waited.
        public static void Resume()
        {
            if (loading < InFlight && queued > done + loading) Pump();
        }

        static void Pump()
        {
            if (HoldForPages && Hold != null && Hold()) return;
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
            Debug.Log(string.Format("T2D sounds: {0} loaded in {1:0.00} s; {5} Sounds made before their clip; {2} asked for before they were loaded, {3:0} ms waited in all, longest {4:0} ms",
                done, Time.realtimeSinceStartup - started, waits, waitedMs, longestWaitMs, deferred));
        }
    }
}
