using UnityEngine;

// The game's sound loader, Sound.createSoundFromFile(name) (com.twissgreen.engine.media), which AssetsEmbed.loadSound
// calls for each of the game's sounds by its name in Assets.soundfilename. The game first looks for a loose WAV file,
// Application.dataPath + "/Resources/" + name + ".wav" (outside a "/music/" folder), and loads it with
// UnityWebRequest and DownloadHandlerAudioClip, waiting for the request on the main thread; otherwise it loads the clip
// with Resources.Load<AudioClip>(name). The Steam release ships no such files (its data folder's Resources holds only
// Unity's built-in resources), so every sound comes from Resources.Load: the loose files are a hook for development or
// mods. The rewriter (compat/rewriter/PlatformPasses.cs, SoundLoader) makes the method
//   AudioClip clip = Sounds.Clip(name); if (clip == null) return null; return new Sound(clip) { clipname = name };
// which is what the game's own code does for a clip from Resources (its Sound(AudioClip) constructor sets snd and
// length). That leaves out Unity 6's UnityWebRequest internals the method had inlined (BeginWebRequest_Injected,
// get_result_Injected, the UnityWebRequest.Result enum), and Rewind's gap in its finally (an unidentified Dispose).
// Part of T2DCompat; see Compat2018.cs.
namespace T2DCompat
{
    public static class Sounds
    {
        public static AudioClip Clip(string name)
        {
            return Resources.Load<AudioClip>(name);
        }
    }
}
