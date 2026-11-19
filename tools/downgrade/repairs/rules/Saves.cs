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
