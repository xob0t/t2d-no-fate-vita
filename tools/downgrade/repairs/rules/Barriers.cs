using Repairs;

// Inlined write barriers (Master builds) that Rewind leaves after a store to a static field, or to a
// field of an object it can't type: the store itself is recovered; the barrier is GC plumbing.
static partial class Rules
{
    // EpicManager.getInstance: s_instance's store at 0x18014B527, barrier at 0x18014B52A..57F.
    static void EpicManagerGetInstance(Engine e)
    {
        MethodRepair m = e.Method("com.twissgreen.unity.EpicManager::getInstance()", null);
        m.Site("__load4(", null).SkipBarrier();
    }

    // Sound.createSoundFromFile: the barrier after UnityWebRequestAsyncOperation.webRequest's store,
    // inside the try whose finally disposes the request.
    static void SoundCreateSoundFromFile(Engine e)
    {
        MethodRepair m = e.Method("com.twissgreen.engine.media.Sound::createSoundFromFile(String)", null);
        m.Site("__load4(", null).SkipBarrier();
    }
}
