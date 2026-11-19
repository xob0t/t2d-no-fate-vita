using Repairs;

// Inlined write barriers (Master builds) that Rewind leaves after a store to a field of an object it
// can't type (the static-field ones Rewind's runtime fixes now remove): the store itself is
// recovered; the barrier is GC plumbing. Patterns.cs's barrier family does the same where no rule is needed.
static partial class Rules
{
    // Sound.createSoundFromFile: the barrier after UnityWebRequestAsyncOperation.webRequest's store,
    // inside the try whose finally disposes the request.
    static void SoundCreateSoundFromFile(Engine e)
    {
        MethodRepair m = e.Method("com.twissgreen.engine.media.Sound::createSoundFromFile(String)", "2eb23527");
        m.Site("__load4(", "67db8607").SkipBarrier();
    }

    // AssetsEmbed.loadBytes: the barrier after byteMap[i] = the ByteArray just made, with the GC flag read into V7
    // before the store and tested after it; the card marking indexes the element's address, which the barrier
    // family doesn't follow. Read in the native code as the same flag test and card loop.
    static void AssetsEmbedLoadBytes(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.AssetsEmbed::loadBytes(Int32)", "5c928f80");
        m.Site("__load4(", "c07e9ee0").SkipBarrier();
    }
}
