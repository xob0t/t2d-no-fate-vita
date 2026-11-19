using Repairs;

// The save loaders.
static partial class Rules
{
    // SaveSettings.loadSettings: the Haxe catch-all around BinaryFormatter.Deserialize, `catch (Exception e)` (the
    // handler's type in the EH table), in IL2CPP's form: the class test the catch makes (IsAssignableFrom against
    // System.Exception, true for any exception), then Haxe's `haxe.lang.Exceptions.exception = e` through the thread's
    // static data ([ThreadStatic] in the il2cpp dump), e in V6. Then the write barrier after the PlayerPrefs path's
    // controllerKeyboard store, the flag kept in a local.
    static void SaveSettingsLoadSettings(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.savedata.SaveSettings::loadSettings()", "52cf296e");
        m.Site("__IsAssignableFrom(__klass(),", "dba47e8d").With(il => il.I8(1));
        m.Site("__ThreadStaticData(__klass())", 0, "fe6655e6").Delete();
        m.Site("__store8(t#, e21)", "014d06ee").With(il => il.Ldloc(6).Stsfld("haxe.lang.Exceptions::exception"));
        m.Site("__ThreadStaticData(__klass())", 0, "4fc23be2").Delete();
        m.Site("__load4(", "fb0f3790").SkipBarrier();
    }
}

// SaveStats.loadStats: the same catch-all, twice, around the stats' Deserialize: as a StatsBlob (the exception in V40),
// then, in its handler, as the older StatsBlobV1 (V47). Each handler in IL2CPP's form (Unity 6): the class test
// (IsAssignableFrom against System.Exception, true for any exception), the active exception pushed for a rethrow
// (ExceptionStackPush) and popped at the handler's end (ExceptionStackPop), the exception read back from that stack
// into an int64 (V43, V61; it is the caught one, and they become objects), and Haxe's
// `haxe.lang.Exceptions.exception = e` through the thread's static data. The push and pop are the runtime's bookkeeping.
static partial class Rules
{
    static void SaveStatsLoadStats(Engine e)
    {
        MethodRepair m = e.Method("com.bitmapbureau.terminator.savedata.SaveStats::loadStats()", "1e75a4d6");
        m.RetypeLocal(43, "System.Object").RetypeLocal(61, "System.Object");
        int[] caught = { 40, 47 }, read = { 43, 61 };
        string[][] hashes =
        {
            new[] { "ead1e2fb", "4d6ef90e", "33117b68", "c12ec8fc", "2d8f1eb1", "60ec6637" },
            new[] { "5727b4e6", "995b9ddb", "80d0f90a", "fb274d8d", "d3685d6a", "b08f8758" },
        };
        for (int k = 0; k < 2; k++)
        {
            int ex = caught[k], v = read[k];
            m.Site("__IsAssignableFrom(__klass(),", 0, hashes[k][0]).With(il => il.I8(1));
            m.Site("__ExceptionStackPush(__frame(),", 0, hashes[k][1]).Delete();
            m.Site("__load8(__address((__address((__frame() +", 0, hashes[k][2]).With(il => il.Ldloc(ex));
            m.Site("__ThreadStaticData(__klass())", 0, hashes[k][3]).Delete();
            m.Site("__store8(t#, t#)", 0, hashes[k][4]).With(il => il.Ldloc(v).Stsfld("haxe.lang.Exceptions::exception"));
            m.Site("__ThreadStaticData(__klass())", 0, hashes[k][5]).Delete();
        }
        m.Site("__ExceptionStackPop(__frame())", 0, "16c4acfc").Delete();
        m.Site("__ExceptionStackPop(__frame())", 0, "656bab59").Delete();
    }
}
