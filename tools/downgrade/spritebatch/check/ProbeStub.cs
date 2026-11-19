// For check_rules.py only: SpriteBatch.Flush ticks the probe (compat/Probe.cs), which brings in the player's log,
// the flags, the save paths and the image module. The check compiles SpriteBatch.cs without them, against this.
namespace T2DCompat
{
    public static class Probe
    {
        public static void FromRender() { }
    }
}
