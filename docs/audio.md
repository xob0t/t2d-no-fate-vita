# Sound on the PS Vita

This page says how to build Terminator 2D: NO FATE's sound for the Unity 2018.2 PS Vita port, and what it costs there.
The rule is `tools/downgrade/audio/audio_rules.py`; the console test is `tools/downgrade/audio/vita-audio-test`.
The start-up rules are `tools/downgrade/audio/rewriter/AudioPatches.cs` with `tools/downgrade/audio/compat/SoundLoads.cs`, and the builder's side is `tools/builder/audio.py`.
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

The project stage applies the rule: `pipeline.asset_overlay` writes `audio_rules.audio_overlay(exported_project)` over the export, after the shaders and before the texture pages, and logs `Sound: import settings of 930 clips`.
The overlay is `{Path('Assets/Resources/audio/<clip>.meta'): bytes}` for every clip, the same shape as huntdown-vita's `asset_rules.audio_overlay`.
It changes `defaultSettings` (loadType, compressionFormat, sampleRateSetting, sampleRateOverride) and the importer's `forceToMono`, `normalize` (off), `preloadAudioData` and `loadInBackground` (off).
The clips keep their paths and GUIDs, so `Resources.Load` finds them as before.
In the project the stage writes, 619 clips are forced to mono and 81 stream.

- Built by the editor (as the test app is): the editor's PS Vita import applies all of it.
- Built by `tools/builder`: the builder makes the same conversion ([The builder](#the-builder)).

For the code side:

- `Sound.createSoundFromFile`: the platform layer's rule (`compat/rewriter/PlatformPasses.cs`, `PlatformSoundLoader`) makes it `Resources.Load` only, through `T2DCompat.Sounds.Clip`. The audio check confirms it on the written assembly: Rewind's method has one `File.Exists` and ten `UnityWebRequest` references, the written one none, so the 835 `File.Exists` calls at start-up are gone. The game's only `File.Exists` calls left are in `SaveSettings.loadSettings` and `SaveStats.loadStats`.
- `AssetsEmbed.staticInit` and `Sound`: rules A1 to A3 load the sounds in the background, and only a play waits for its clip ([Start-up](#start-up)).
- `AudioManager`: keep 32 real voices. The game asks for a 1024-sample DSP buffer; the PS Vita player kept its 512 when the test asked for 1024 (`AudioSettings.Reset`).

## The builder

`tools/builder/audio.py` converts each clip as the editor's PS Vita import does before it encodes:

- `forceToMono` mixes the channels down to their mean, truncated toward zero: `trunc((L + R) / 2)`.
- A sample rate override makes `n * rate_out // rate` samples out of `n`, and sample `j` is taken at `j * n / n_out` in the source.
- The clip's `m_Frequency` and `m_Channels` are the converted clip's, `m_Length` is `n_out / rate_out`, and the HEVAG frames are `ceil(n_out / 28)`, before the stretch to whole frames.
- The resampler is the editor's own kernel for 44.1 kHz to 32 kHz, as a table (`tools/builder/audio_kernel.py`).

The editor's PCM output made all of this measurable.
`tools/downgrade/audio/importer-reference/make.py` builds 42 synthetic clips (noise, sweeps, impulses, tones, a clip that clips, a 101-sample clip; nothing of the game) with the installed editor in batch mode into one PS Vita asset bundle, in PCM and HEVAG, mono and stereo, at several rates.
A PCM bank holds the importer's converted samples exactly, with no encoder in the way.

- The mono mix is exact: the five PCM clips forced to mono at their own rate are byte for byte the editor's.
- The lengths are exact for every probe and every game clip.
- The kernel is no windowed sinc. With Kaiser, Blackman, Hann, Hamming, Nuttall, Gaussian and Lanczos windows, or any smooth window as a spline, the best fit leaves 29 dB, and quantised phases change nothing. A table of the kernel itself, 641 entries 1/16 of a source sample apart out to 40, fitted by least squares on the stereo probes (`fit_resampler.py`), leaves 1.0 LSB rms.
- Against the editor's output: sweep 62.5 dB (mono, held out of the fit: 61.9 dB), tones 61.3 dB, impulses 69.1 dB (mono 70.4 dB), noise 47.6 dB. Most interior samples are identical (76% of the sweep's); what is left is at the ends.
- The ends differ in the first and last 30 or so output samples. The editor's handling there is none of zeros, a repeated end sample, a mirror or a loop; the builder uses zeros. The 101-sample clip is all ends: 23 dB.
- The clip that clips: 56 dB, as the editor clips the filtered peaks its own way.

The builder makes only the conversions the rule asks for, and stops on the others (`NotImplementedError`):

- A rate conversion other than 44.1 kHz to 32 kHz. The same kernel scaled to other ratios came within 11 to 29 dB of the editor's (22.05 kHz, 11.025 kHz, 48 to 44.1 kHz, upsampling). Another ratio takes a probe at that ratio and a run of `fit_resampler.py`.
- `normalize` with `forceToMono`: the editor scales the mono mix to full scale, which is not modelled.
- Optimize Sample Rate.

The PCM banks' padding was wrong for stereo: the data takes 8 samples per channel more before the 32-byte alignment (16 bytes mono, 32 stereo; `fsb5.bank`), not 16 bytes.
The rule uses no PCM.

`uv run tools/builder/check_audio.py --compare` checks the builder against the importer reference and against the audio test's editor build of the 930 game clips:

| | Clips | Fields and bank headers (but FMOD's content hash) | Data |
| --- | --- | --- | --- |
| Importer reference | 30 (the builder refuses 12 more) | all identical | PCM without resampling identical (5 of 5); resampled PCM and HEVAG not |
| The game's clips, as the rule imports them | 930 | all identical, bank sizes too, so the `.resource` layout is the editor's | HEVAG not identical |

No HEVAG bank is byte for byte the editor's, and none can be: the encoder is not Sony's (`hevag.py`), which holds at the source's rate too.
The error, decoded as the console decodes:

| Game clips | Count | Ours against Unity's, median (p10) | Ours against the source | Unity's against the source |
| --- | --- | --- | --- | --- |
| Mono, 32 kHz | 619 | 23.5 dB (16.7) | 30.5 dB | 24.7 dB |
| Stereo, 32 kHz | 216 | 22.0 dB (15.0) | 29.2 dB | 22.9 dB |
| Music, stereo, 44.1 kHz | 95 | 30.0 dB (27.0) | 35.5 dB | 31.3 dB |

Ours is closer to the source than Unity's by 4 to 6 dB (medians), as huntdown-vita found for its game.

### The cache

Encoded clips go to `work/builder/audio-cache`, one pickle per clip.
The key is SHA-256 of the source file's bytes, the clip's name (the bank holds a hash of it), its importer settings, and the bytes of `audio.py`, `audio_kernel.py`, `hevag.py` and `fsb5.py`.
A build whose audio didn't change encodes nothing: the compare's second run took 1.2 s for the 930 clips, all from the cache.
Encoding all 930 with the rule takes 122 s on 4 workers, down from 12.5 minutes, since the effects are mono and at 32 kHz.
An edit of any of the four files encodes everything again.
The encoded bytes can differ between CPU families (`audio.one_blas_thread`), one more reason the cache stays under `work/`.

## Start-up

As the game does it, loading the sound takes 15.75 s before the first frame.
Three rewriter rules and a T2DCompat helper load it in the background instead:

- A1 (`tools/downgrade/audio/rewriter/AudioPatches.cs`, `AudioStartupLoads`): in `AssetsEmbed.staticInit`, the loop's `call loadSound(i); pop` becomes `call t2dQueueSound(i); nop`, in place. `t2dQueueSound` is a method the rule adds to `AssetsEmbed`, with `loadSound`'s bounds check and name lookup (the `string[]` field whose element `loadSound` passes to `createSoundFromFile`): `if (i >= 0 && i < names.Length) SoundLoads.Queue(names[i])`.
- A2 (`AudioClipLoads`): `Sound.createSoundFromFile`'s call of `T2DCompat.Sounds.Clip` goes to `SoundLoads.Clip`.
- A3 (`AudioDeferredClips`): a `Sound` is made without its clip while the clip is still loading, and gets it when it is first played. `createSoundFromFile` starts with `if (SoundLoads.Defer(name)) { s = new Sound(null); s.clipname = name; return s; }`; `Sound`'s constructor leaves `snd` and `length` alone for a null clip. The rule adds `Sound.t2dClip(s)`, which, while `snd` is null, takes the clip from `SoundLoads.Clip(s.clipname)` and sets `snd` and `length` as the constructor does, and `Sound.t2dLength(s)`, which calls it and returns `length`. Every read of `snd` and `length` outside the constructor becomes, in place, a call of one of them: `Sound.playWrap` (2), `Sound.playScheduleWrap`, `Audio.playIntroLoopChannels` (the intro's length, for the loop's start), and `Audio.cueAudioFull`'s two reads of `length` (it drops a sound cued twice for the same time by comparing lengths, so a length of 0 would drop another sound).
- `T2DCompat.SoundLoads` (`tools/downgrade/audio/compat/SoundLoads.cs`): `Queue` asks for the clip with `Resources.LoadAsync`, four at a time, UI sounds first, then music, then the rest; each request's `completed` event starts the next. `Clip` returns the loaded clip at once. A clip still loading is waited for (that request only), and a clip not started is loaded there and then with `Sounds.Clip`. So a sound the game plays is never silent; at worst it starts late.

`loadSound` is otherwise as it was: the first time the game asks for a sound it makes the `Sound`.
The game asks for most of them at start, not when it plays them: `Sounds.staticInit` makes a `Sound` for most of the clips one after another (652 of them before their clip had loaded, on the console).
With A1 and A2 alone each of those waited for its clip, so the first frame came at 14.2 s, after 9.7 s of waits ([build.md](build.md#on-the-console)).
With A3 they are made at once, and the wait moves to the first play of a sound whose clip hasn't loaded yet; a clip that has loaded by then costs a dictionary lookup.
Every clip stays loaded for the whole game, as before.

The rules find their sites by the shape of the IL, as the texture rules do, and throw on any other shape.
A1 and the texture rule R3 both change `staticInit`, each only the instructions of its own pattern.
`tools/downgrade/audio/check/check_rules.py` checks them with Mono.Cecil:

- On Rewind's assembly, the texture rules and A1 in both orders give the same `staticInit` and `t2dQueueSound`, and their stacks balance. A1 run twice, A1 without the loop's call, A2 without the platform pass's `Sounds.Clip`, and A3 without A2 each throw.
- On the code stage's written `Recovered.T2D`, `staticInit` queues the sounds, enters the splash's pages, and loads neither a sound nor a pack. `createSoundFromFile` starts with `SoundLoads.Defer`, takes its clip from `SoundLoads.Clip` otherwise, and has no `File.Exists`, `UnityWebRequest` or `Sounds.Clip`. Nothing reads `Sound.snd` or `Sound.length` but the constructor, `t2dClip` and `t2dLength`; the game's 4 reads of `snd` and 2 of `length` go through them, and `t2dClip` takes the clip from `SoundLoads.Clip`. So every play takes its clip from `SoundLoads.Clip`, which returns a clip or waits for it: no sound plays without its clip. A3 run again throws. The four calls into `SoundLoads` resolve against `T2DCompat.dll`, and the stacks of `AssetsEmbed`'s and `Sound`'s methods and of the four callers of the new methods, 21 in all, balance.

The code stage reports `audio: AudioStartupLoads=2 AudioClipLoads=1 AudioDeferredClips=7` and nothing unresolved.
`staticInit` still has Rewind's gaps around its arrays (the `soundMap` allocation among them), which the repairs must fill before it runs; the rules don't depend on them.

On the console, the audio test's `rule` step builds in the real `SoundLoads.cs` and queues the 930 clips as the rewritten `staticInit` does.
Every 20 frames it asks for a sound effect that isn't loaded yet, and plays it (runs r1 and r2, 2026-11-19):

| Loads in flight | Queueing the 930 | All loaded | Frame p50 | Frames over 20 ms | Asks | Wait, median | Wait, longest | Silent |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 4 (the default) | 47 ms | 19.4 s | 16.7 ms | 59 of 1161 | 58 | 25.7 ms | 63 ms | 0 |
| 16 | 50 ms | 16.0 s | 16.7 ms | 46 of 952 | 47 | 24.9 ms | 81 ms | 0 |

- The first frame comes about 50 ms after the queueing starts, instead of 15.75 s.
- The frames over 20 ms are the asks. A play that has to wait costs a frame about as long as the clip's own load (12 ms plus its size at 8 MB/s) plus what is in flight ahead of it. Without asks the frames stay at the 60 Hz vsync while the clips load.
- Fewer loads in flight make the longest wait shorter and the whole load longer.
- The test asks only for clips not loaded yet, three times a second: the worst case. In the game the UI sounds and the music load first, so the menus should not wait; a level started within the first 20 s or so may wait on its first plays.
- Main memory afterwards: 34 MB, as with the game's own loading.
- Not measured: a synchronous load during the background loads, such as a texture page at a scene change, may wait for the loads in flight.

`SoundLoads` logs one line when every clip is loaded (`T2D sounds: 930 loaded in ...`, with how many `Sound`s were made before their clip), and each wait over 20 ms.

The game's own start on the console, with `-t2d-heartbeat 5`, Start pressed once the attract cutscene had begun (runs `aboot1` and `aboot2`, 2026-11-19), against the runs before A3 (`boot4`, `menu1`, [build.md](build.md#on-the-console)):

| | A1 and A2 | With A3 |
| --- | --- | --- |
| First frame | 14.2 s, 14.3 s | 4.42 s, 4.42 s |
| The attract cutscene begins | 20.8 s, 22.0 s | 11.2 s, 11.0 s |
| Main menu | 26.7 s (Start pressed at about 26 s) | 15.2 s, 18.7 s (Start pressed after 11 s, when the script saw the attract in the log) |
| Sounds made before their clip | 0 | 652 |
| Plays that waited for their clip | 652 (the `Sound`s made at start) | 2, 1 |
| Waited in all, longest | 9.8 s, 9.7 s; 56 ms, 55 ms | 383 ms, 331 ms; 336 ms, 331 ms |
| All 930 loaded | 19.5 s, 17.9 s | 22.7 s, 19.7 s |
| Splash frame time, p50 and p90 | 22.5 to 23.5 ms, 65 to 68 ms | 23.8 to 26.0 ms, 63 to 67 ms |

- The 2.5 s left before the first frame (from 1.96 s, when `staticInit` enters the splash's pages, to 4.42 s) is the rest of the game's start, not sound: `Sounds.staticInit` makes its `Sound`s at once.
- The splash, the logo and the attract run while the clips load, and their frames are as slow as before (p50 about 24 ms, p90 about 65 ms): the background loads cost the frames, as they did then. The main menu runs at 60 Hz once every clip is loaded (p50 16.7 ms, p90 17.1 ms from about 25 s); before that its frames are slower (p90 30 to 55 ms).
- The one long wait is `audio/sfx/misc/metal_hit`, played in the attract at about 11.5 s, when about 355 of the 930 clips had loaded. Its load had not started, so `Clip` loaded it with `Resources.Load`, and that waited about 330 ms, for the four loads in flight and more. It is one dropped third of a second during the attract, not silence. The other wait was `audio/sfx/misc/introduction`, 48 ms.
- Every clip loads later than before (19.7 to 22.7 s against 17.9 to 19.5 s), since the main thread now runs frames instead of waiting on the loads.

## Open problems

- Start-up waits: a sound first played before its clip has loaded waits on the main thread, during the first 20 s or so. A clip whose load is in flight costs 10 to 80 ms (the audio test above); one not started yet is loaded with `Resources.Load`, which also waits for the loads in flight: 330 ms for `metal_hit` in the attract. `SoundLoads` now queues `metal_hit` and `introduction` with the UI sounds, ahead of the rest (not measured on the console yet); other early cues would need the same, or a clip asked for early could start its async load at the head of the queue and wait for that.
- Frames while the clips load: the splash, logo, attract and the menu's first seconds run at about 24 ms p50 until every clip is loaded (about 20 s). Fewer loads in flight, or a pause of the loads while a scene is on screen, was not tried.
  The longest of those frames, 100 to 213 ms, were Skynet's voice lines finishing (`audio/sfx/voices/Skynet/`, up to 15 s and 0.5 MB, stereo, loaded into memory), which came last by name, at the attract cutscene's end (22 to 26 s). `SoundLoads` now queues them after the music, ahead of the rest, so they load under the splash and the publisher's logo (4 to 11 s): in the attract, frames of 100 ms or more went from 14 to 5 and frames over 50 ms from 34 to 18 (runs `dm`, `em`); the splash and logo got 4 more.
- Memory: 31 MiB stay resident for the whole game. Loading each level's effects when it starts (the effects are in per-level folders: `sfx/biker_bar`, `sfx/hospital`, …) and unloading them after would cut that to a few MiB, but needs a list of which level plays which clip.
- Music starts: each stream's `Play` blocks the main thread for 16 to 78 ms, and an intro of 10 s or more starts with its loop (two opens), layered music with up to three. Most happen behind a transition; in-level changes (boss music, the pause music) will drop a few frames each time. Opening a stream ahead of `Play` (`AudioClip.LoadAudioData` on a streamed clip) was not tried; keeping the intros in memory would cost 12 MiB more.
- Concurrency: the worst case (six streams, 25 voices) was measured with the test's own sources and clips, not in the game, and with the PS Vita's 512-sample DSP buffer, which the player keeps whatever the game asks.
- The builder's resampler differs from the editor's at the clips' ends, and the builder makes no rate conversion but 44.1 kHz to 32 kHz.
- Effects forced to mono lose the little width they had; nobody has listened to all 532.

## Reproducing

```sh
# The inventory, the settings and the report (work/audio/clips.tsv, report.json)
python -I tools/downgrade/audio/audio_rules.py
# The importer reference (the installed editor, batch mode), the kernel table, and the builder's compare
uv run tools/downgrade/audio/importer-reference/make.py
uv run --with scipy tools/downgrade/audio/importer-reference/fit_resampler.py
uv run tools/builder/check_audio.py --compare
# The rules A1 to A3, after the code stage
uv run tools/downgrade/pipeline.py code
python -I tools/downgrade/audio/check/check_rules.py
# The console test: build, check what the importer made, run inside the shared lock
python -I tools/downgrade/audio/vita-audio-test/build.py
uv run --with unitypy==1.25.3 --with numpy python -I tools/downgrade/audio/vita-audio-test/check_build.py
python ~/.t3/projects/butcher-vita/tools/downgrade/lock.py run vita-device "t2d: audio" --wait 1800 -- \
  bash -c "python -I tools/downgrade/audio/vita-audio-test/va.py install && \
           python -I tools/downgrade/audio/vita-audio-test/va.py run a1 'load; play 0 0 6; play 0 24 6; play 2 24 6; play 4 24 6; play 6 25 6; play 8 0 4' && \
           python -I tools/downgrade/audio/vita-audio-test/va.py run a2 'loadasync 16' && \
           python -I tools/downgrade/audio/vita-audio-test/va.py run r1 'vol 0.2; rule 4 20'"
```

The check of the folders against the code's audio types read `Assets.audiotype` (an `InitializeArray` blob) from Rewind's `Assembly-CSharp.dll` with dnfile, and `Assets.soundfilename` from Rewind's `Assets.cs`; neither is committed.
