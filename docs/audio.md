# Sound on the PS Vita

This page says how to build Terminator 2D: NO FATE's sound for the Unity 2018.2 PS Vita port, and what it costs there.
The rule is `tools/downgrade/audio/audio_rules.py`; the console test is `tools/downgrade/audio/vita-audio-test`.
Prose here is one sentence per line.
Nothing derived from the game is committed: reports and the test project go under `work/`.

## What the game has

The game's sound is 930 AudioClips in `Resources/audio`, all with the Streaming load type and preloadAudioData on, all stereo at 44.1 kHz.
They come from the game's `resources.resource` (FSB5); AssetRipper exports the music as Ogg Vorbis and everything else as 16-bit WAV.
The folders match the code's own table of audio types (`Assets.audiotype`, read from Rewind's assembly): music 0, ambience 1, sound 2, Skynet 3, all 930 alike.

| Kind | Folder | Clips | Minutes | Longest | Exported | As 16-bit PCM |
| --- | --- | --- | --- | --- | --- | --- |
| Music | `music/*` but ambience | 89 (Ogg) | 94.9 | 287 s | 304.8 MiB | 957.9 MiB |
| Ambience | `music/ambience` | 6 (Ogg) | 5.5 | 110 s | 11.9 MiB | 55.9 MiB |
| Skynet's lines | `sfx/voices/skynet` | 36 | 2.5 | 15 s | 24.8 MiB | 24.8 MiB |
| Other voices | `sfx/voices/*` | 86 | 0.8 | 1.7 s | 8.2 MiB | 8.2 MiB |
| Sound effects | `sfx/*` | 689 | 14.0 | 14.5 s | 141.7 MiB | 141.7 MiB |
| UI | `ui` | 24 | 0.4 | 3.5 s | 4.4 MiB | 4.4 MiB |
| Total | | 930 | 118.2 | | 495.8 MiB | 1,192.8 MiB |

The music's Ogg is about 450 kbit/s, so HEVAG at 44.1 kHz stereo (about 400 kbit/s) is smaller than what the game ships.
The median sound effect is 0.7 s long, 90% are under 2.7 s, and 26 are longer than 5 s (engine and fire loops, cutscene sounds, the HK tank).
Many effects are stereo files with the same signal in both channels: 344 of 689 effects and 78 of 86 voices have identical channels, and 532 effects have a side signal (L−R) at least 20 dB below the mid (L+R).
Skynet's lines and the UI sounds are real stereo.

## How the game plays sound

The recovered code (Rewind's C# of `Audio`, `Sound`, `SoundChannel`, `AssetsEmbed`, `MusicPlayer`) shows:

- `AssetsEmbed.staticInit` calls `loadSound` for every one of the 930 clips when the game starts, and `soundMap` keeps them for the whole game; nothing unloads a clip. So every clip not streamed is in main memory all the time.
- `Sound.createSoundFromFile` first looks for a loose `dataPath/Resources/<name>.wav` (not for music) and loads it with `UnityWebRequest` and `DownloadHandlerAudioClip`; when the file isn't there it uses `Resources.Load<AudioClip>`. The shipped game has no loose files, so it always takes `Resources.Load`, after one `File.Exists` per clip.
- `SoundChannel` makes 32 AudioSources. Channels 0 to 2 are music layers, 3 the pause music, 4 the ambience, 5 Skynet, 6 to 30 sound effects (25), and 31 the second half of an intro and loop pair.
- A sound effect takes the first free channel from 6 to 30 and plays with `PlayOneShot` (or `Play` when it loops or starts late); a channel is free again 10 frames after it stops. More than 25 at once are dropped.
- Music with an intro plays the intro on its channel and the loop on channel 31 with `PlayScheduled`, half a second ahead and gapless: the two clips open in the same frame.
- Levels with layered music (Future War 1 and 4, the hospital, the biker bar) start two or three music channels at once, the extra layers at volume 0, and fade between them.
- The pause screen starts the pause music on its own channel, on top of the level's music.
- Skynet's lines duck the other sound to 0.6.
- `AudioManager` has 32 real voices, 2048 virtual ones and a DSP buffer of 1024.

At worst, then, these play at once: three music layers, the pause music, an ambience, a long intro and its loop for a moment, Skynet, and 25 effects.
That is up to six streams and about 31 voices.
In a normal level it is one or two streams (music, ambience) and a handful of effects.

## What Unity 2018.2's PS Vita build does with sound

The test app's probe imported one 10 s stereo effect and one 35 s ambience in every format and read the built banks back (`check_build.py`):

- HEVAG, Vorbis, MP3 and ATRAC9 all come out as HEVAG banks: the PS Vita's importer makes every compressed clip HEVAG, as huntdown-vita found. ATRAC9 is not available in 2018.2 for the PS Vita, though the player carries FMOD's AT9 codec.
- ADPCM comes out as FMOD's IMA ADPCM (decoded in software), PCM as PCM16.
- Decompress on Load keeps a HEVAG clip HEVAG in memory (the same 1 MB as compressed in memory); only PCM decompresses.
- forceToMono and a sample rate override work: mono halves the bank, 32 kHz and 22.05 kHz banks come out at those rates.
- The editor's `GetOverrideSampleSettings("PSP2")` reports other formats than the build makes (HEVAG as PCM, Vorbis as HEVAG); only the built banks tell.

HEVAG is 16 bytes per 28 samples per channel, 3.5 times smaller than 16-bit PCM, and the console decodes it cheaply.
So the choices are HEVAG or PCM, compressed in memory or streaming, mono or stereo, and the rate.

On the console (probe run p1, clocks at 444/222 MHz):

- `Resources.Load` of a clip kept in memory reads at about 7 to 8 MB/s: 71 ms for a 0.5 MB stereo HEVAG bank, 40 ms for its mono version, 232 ms for its 1.8 MB PCM version.
- A streamed clip loads in 0.1 ms and stays `Unloaded`; 20 of them took no measurable memory (the free-memory reading moves in 1 MB steps), with preloadAudioData on or off.
- `AudioSource.Play` of a streamed clip blocks the main thread for 16 to 57 ms, 25 ms on average (16 plays), and its first samples come about 20 ms later. butcher-vita measured about 35 ms.
- `Play` of a clip in memory takes 0.3 ms.
- Every format played.

## The rule

`audio_rules.decide`, by kind:

| Kind | Load type | Format | Channels | Rate | Preload |
| --- | --- | --- | --- | --- | --- |
| Music and ambience of 10 s or more | Streaming | HEVAG | stereo | 44.1 kHz | off |
| Music shorter than 10 s (boss intros, stings, two short loops) | Compressed in memory | HEVAG | stereo | 44.1 kHz | on |
| Sound effects, voices, Skynet, UI | Compressed in memory | HEVAG | mono if the side is 20 dB or more below the mid, else stereo | 32 kHz | on |

Why:

- Every sound effect must be in memory: the game starts effects in play all the time, and a streamed one costs 16 to 57 ms of main thread per `Play`.
- Music and ambience stream: they are 100 minutes, 290 MiB even as HEVAG, and start once per scene, mostly behind a transition. A short music clip in memory saves one of the two stream opens when an intro starts with its loop.
- Mono for effects whose channels nearly agree saves 10 MiB, and the game pans each effect itself with `panStereo`.
- Real stereo effects, Skynet and the UI keep their width.
- 32 kHz for effects keeps everything below 16 kHz and saves a quarter.
- 22.05 kHz would save another 8.5 MiB, but it cuts the top off the explosions and gunshots.
- HEVAG, not PCM, because PCM takes 3.5 times the memory and nothing in the game reads samples with `GetData`.
- preloadAudioData on for clips in memory, so none loads on the main thread when it first plays; for streams it makes no difference on the console.

The thresholds are constants at the top of `audio_rules.py` (`STREAM_MIN_SECONDS`, `SIDE_DB_MONO`, `MEMORY_RATE`).

## Sizes and memory

With the rule (`python -I tools/downgrade/audio/audio_rules.py`):

| Kind | Streamed | In memory | Mono | VPK | Main memory |
| --- | --- | --- | --- | --- | --- |
| Music | 75 | 14 | 0 | 273.7 MiB | 4.0 MiB |
| Ambience | 6 | 0 | 0 | 16.0 MiB | 0 |
| Skynet | 0 | 36 | 0 | 5.1 MiB | 5.1 MiB |
| Other voices | 0 | 86 | 86 | 0.9 MiB | 0.9 MiB |
| Sound effects | 0 | 689 | 532 | 20.3 MiB | 20.3 MiB |
| UI | 0 | 24 | 1 | 0.9 MiB | 0.9 MiB |
| Total | 81 | 849 | 619 | 316.9 MiB | 31.2 MiB |

Main memory here is the HEVAG banks of the clips in memory, which the game holds from start to end.
Streamed clips take no memory until they play.
The test's build agrees: its 849 banks in memory are 30.1 MiB and its 81 streams 285.7 MiB (built when the 14 short music clips were still at 32 kHz), and on the console the loaded clips took 33 MB (a4).
A playing stream adds under 1 MB.

Other settings, for comparison (clips in memory only):

| Effects, voices, Skynet and UI | Main memory, with the 14 short music clips |
| --- | --- |
| Stereo, 44.1 kHz | 47.2 MiB |
| Mono where side ≤ −20 dB, 44.1 kHz | 41.4 MiB |
| Mono where side ≤ −40 dB, 32 kHz | 34.4 MiB |
| The rule: mono where side ≤ −20 dB, 32 kHz | 31.2 MiB |
| All mono, 32 kHz | 22.6 MiB |
| Mono where side ≤ −20 dB, 22.05 kHz | 22.7 MiB |
| All mono, 22.05 kHz | 16.9 MiB |
| As 16-bit PCM, as decompressing on load would make them | 179 MiB without the music |

The PS Vita gives an empty app about 274 MB of main memory (the test app: 279 MB free at start, 273 MB at its first frame).
The atlases will take 100 to 190 MB of it ([assessment.md](assessment.md#risks-outside-the-code)).
Against that, 31 MiB of sound is a lot, so the rule goes mono and 32 kHz wherever that loses little.
If the textures end at the top of that range, the next step is all effects mono (22.6 MiB) or loading each level's effects only while it runs (below).

## Measurements

All on the console, Unity 2018.2.19f1 PS Vita player (IL2CPP, release), clocks at 444/222 MHz, output at 48 kHz, a 512-sample DSP buffer, 32 real voices.
The app is the test in `tools/downgrade/audio/vita-audio-test`, with all 930 clips imported by the rule; runs p1, a1 to a4 on 2026-11-19, logs in `work/vita-audio-test/runs`.
In a1 the 14 short music clips were still at 32 kHz; a2 to a4 have the rule as it is.
The free-memory reading (`PSVita.Diagnostics.GetFreeMemoryLPDDR`) moves in 1 MB steps.

Loading as the game does (`AssetsEmbed.staticInit`: one `Resources.Load` after another in one frame), run a4:

| Clips | Count | Time | Main memory |
| --- | --- | --- | --- |
| Sound effects in memory | 689 | 11.6 s | 21 MB |
| Skynet in memory | 36 | 1.2 s | 6 MB |
| Other voices in memory | 86 | 1.3 s | 1 MB |
| UI in memory | 24 | 0.4 s | 1 MB |
| Short music in memory | 14 | 0.75 s | 4 MB |
| Music and ambience streamed | 81 | 0.45 s | 0 |
| All | 930 | 15.75 s (a1: 14.9 s) | 33 MB (Unity's own count: 33.0 MB) |

- A clip in memory costs about 12 ms plus its bank at about 8 MB/s: 16 ms for the median 33 KB effect, 90 ms for the largest, Skynet's 15 s stereo line. With 849 clips the fixed part dominates.
- `Resources.LoadAsync` does not make it faster: 16 requests in flight took 15.4 s (a2), one at a time 23.9 s (a3, about one a frame). But the frames go on: no frame over 18.3 ms while it loads.
- The 835 `File.Exists` calls of `createSoundFromFile` took 0.4 s of it (`dataPath` is `/app0/Media`).
- Afterwards the 849 clips in memory are `Loaded` and the 81 streams `Unloaded`, as expected.
- Free main memory went from 272 MB at the first frame to 239 MB: the report's 31 MiB of banks plus Unity's objects.

Playing (each case 6 s, after `load`; voices retrigger a random in-memory effect with `PlayOneShot` whenever they end, 24 to 27 starts a second):

| Streams | Voices | Frames over 20 ms | Frame time p99, max | Spinning threads' throughput | Main memory while playing |
| --- | --- | --- | --- | --- | --- |
| 0 | 0 | 0 of 360 | 17.2, 17.3 ms | 100% | 0 |
| 0 | 24 | 0 of 360 | 17.2, 18.0 ms | 94.7% | +1 to 2 MB |
| 2 | 24 | 0 of 360 | 17.3, 17.8 ms | 93.8% | ≤ 1 MB |
| 4 | 24 | 0 of 360 | 17.3, 17.4 ms | 93.2% | ≤ 1 MB |
| 6 | 25 | 0 of 360 | 17.4, 17.8 ms | 92.2% | +1 to 2 MB |
| 8 | 0 | 0 of 240 | 17.3, 17.5 ms | 96.9% | +1 to 2 MB |

- Once started, six streams and 25 voices (the game's worst case) play without a slow frame; the frame stays at the 60 Hz vsync.
- The audio's CPU shows as the drop in three low-priority spinning threads' count: about 5% for 24 voices, about 0.4% more for each stream. That is a small share of the three cores; a frame's main thread is not slowed.
- `PlayOneShot` of a clip in memory takes 0.2 ms on average, 0.8 ms at most.
- Starting the streams is what costs: `Play` blocked 50 to 78 ms per stream here (2 streams: 107 ms; 6: 271 ms; 8: 378 ms), more than alone (16 to 57 ms in the probe run p1), as each later one waits on the streams already reading.
- Eight streams opened and played; no "too many open files" (the player's message for that names streamed audio).


## How the pipeline applies it

`audio_rules.audio_overlay(exported_project)` returns `{Path('Assets/Resources/audio/<clip>.meta'): bytes}` for every clip, the same shape as huntdown-vita's `asset_rules.audio_overlay`; the pipeline writes it over the export.
It changes `defaultSettings` (loadType, compressionFormat, sampleRateSetting, sampleRateOverride) and the importer's `forceToMono`, `normalize` (off), `preloadAudioData` and `loadInBackground` (off).
The clips keep their paths and GUIDs, so `Resources.Load` finds them as before.

- Built by the editor (as the test app is): the editor's PS Vita import applies all of it.
- Built by `tools/builder` (huntdown's builder, which writes the FSB5 banks itself): its `audio.settings` stops on `forceToMono` and `sampleRateSetting` (`NotImplementedError`), because huntdown never needed them. It needs a mono downmix (the mean of the channels) and a resampler before `hevag.encode`, and `m_Frequency`, `m_Channels` and `m_Length` from the converted clip. The test's banks (`work/vita-audio-test/build/Media/resources.resource`) are the reference for what the editor makes.

For the code side:

- `Sound.createSoundFromFile`: on the PS Vita there are no loose WAV files, so the `UnityWebRequest` branch never runs; replacing the method with `Resources.Load<AudioClip>(name)` also drops 835 `File.Exists` calls at start-up. That is the platform layer's to decide (tools/downgrade/compat), and it needs nothing from the audio rule. If the platform layer ever wants loose files, they would have to be WAV at the full PCM size and loaded on the main thread.
- `UnityWebRequest.Result` and the inlined binding calls in that method are the API rules' business ([assessment.md](assessment.md#api-gaps-against-unity-20182)).
- `AssetsEmbed.staticInit`: its loop that loads all 930 clips takes about 16 s on the console, all in the first frame, before anything is on screen. A rewriter rule should replace it with `Resources.LoadAsync` of every clip started at boot (UI and menu sounds first, then music, then the rest), a few requests in flight, finished in about 15 s while the logo and the menus run at 60 fps (a2). `loadSound` then takes the clip from its request when done; a clip asked for earlier needs a synchronous `Resources.Load`, and whether that waits for the requests ahead of it was not measured. Loading each clip on first use instead would cost 16 to 90 ms the first time each effect plays in a level.
- `AudioManager`: keep 32 real voices. The game asks for a 1024-sample DSP buffer; the PS Vita player kept its 512 when the test asked for 1024 (`AudioSettings.Reset`).

## Open problems

- Start-up: as the game does it, loading the sound takes 16 s before the first screen. The fix is in the game's code (above), not in the import settings: neither the format nor async loading made the per-clip cost smaller. Load in Background (`loadInBackground`) was not tried; huntdown-vita saw clips hang with it when they were unloaded, which this game never does.
- Memory: 31 MiB stay resident for the whole game. Loading each level's effects when it starts (the effects are in per-level folders: `sfx/biker_bar`, `sfx/hospital`, …) and unloading them after would cut that to a few MiB, but changes `AssetsEmbed.loadSound` and needs a list of which level plays which clip.
- Music starts: each stream's `Play` blocks the main thread for 16 to 78 ms, and an intro of 10 s or more starts with its loop (two opens), layered music with up to three. Most happen behind a transition; in-level changes (boss music, the pause music) will drop a few frames each time. Opening a stream ahead of `Play` (`AudioClip.LoadAudioData` on a streamed clip) was not tried; keeping the intros in memory would cost 12 MiB more.
- Concurrency: the worst case (six streams, 25 voices) was measured with the test's own sources and clips, not in the game, and with the PS Vita's 512-sample DSP buffer, which the player keeps whatever the game asks.
- The builder route needs mono and resampling (above).
- Effects forced to mono lose the little width they had; nobody has listened to all 532.

## Reproducing

```sh
# The inventory, the settings and the report (work/audio/clips.tsv, report.json)
python -I tools/downgrade/audio/audio_rules.py
# The console test: build, check what the importer made, run inside the shared lock
python -I tools/downgrade/audio/vita-audio-test/build.py
uv run --with unitypy==1.25.3 --with numpy python -I tools/downgrade/audio/vita-audio-test/check_build.py
python ~/.t3/projects/butcher-vita/tools/downgrade/lock.py run vita-device "t2d: audio" --wait 1800 -- \
  bash -c "python -I tools/downgrade/audio/vita-audio-test/va.py install && \
           python -I tools/downgrade/audio/vita-audio-test/va.py run a1 'load; play 0 0 6; play 0 24 6; play 2 24 6; play 4 24 6; play 6 25 6; play 8 0 4' && \
           python -I tools/downgrade/audio/vita-audio-test/va.py run a2 'loadasync 16'"
```

The check of the folders against the code's audio types read `Assets.audiotype` (an `InitializeArray` blob) from Rewind's `Assembly-CSharp.dll` with dnfile, and `Assets.soundfilename` from Rewind's `Assets.cs`; neither is committed.
