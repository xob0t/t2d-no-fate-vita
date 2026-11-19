using Repairs;

// Inlined write barriers (Master builds) that Rewind leaves after a store to a field of an object it
// can't type (the static-field ones Rewind's runtime fixes now remove): the store itself is
// recovered; the barrier is GC plumbing. Patterns.WriteBarriers does the same where no rule is needed.
static partial class Rules
{
    // Sound.createSoundFromFile: the barrier after UnityWebRequestAsyncOperation.webRequest's store,
    // inside the try whose finally disposes the request.
    static void SoundCreateSoundFromFile(Engine e)
    {
        MethodRepair m = e.Method("com.twissgreen.engine.media.Sound::createSoundFromFile(String)", "2eb23527");
        m.Site("__load4(", "67db8607").SkipBarrier();
    }
}
