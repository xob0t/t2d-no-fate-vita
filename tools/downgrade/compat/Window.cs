using UnityEngine;

// The game's fullscreen and resolution calls, redirected here by the rewriter (Redirect.cs, ScreenCalls). Copied from
// butcher-vita's compat/Window.cs (same author, MIT).
// The game calls Screen.SetResolution(w, h, fullscreen) and sets Screen.fullScreen from UnityMain.FixedUpdate (when the
// window's mode changed: 796x448 windowed, or Stage.forceResolutionUnity's 16:9 mode of 60 Hz or more and at most 1920
// wide) and from its options (Stage.setDisplayState). They do what they did, except with -t2d-windowed: then the window
// stays a 960x544 window, so a test run never takes over the PC's screen, whatever the game's saved display setting
// says. On the PS Vita they do nothing: its screen is 960x544, and BuildFlags.FullScreen keeps the game's full-screen
// option out of its menu there.
// Stage.forceResolutionUnity also reads Resolution.refreshRateRatio, Unity 6's; Unity6Shims.cs answers it from 2018.2's
// refreshRate. On the PS Vita the method isn't reached: Screen.width is 960 and the window's mode never changes.
// Part of T2DCompat; see Compat2018.cs.
namespace T2DCompat
{
    public static class Window
    {
        static bool? windowed;
        const int WindowWidth = 960, WindowHeight = 544;  // -t2d-windowed: the PS Vita's screen

        static bool Windowed
        {
            get
            {
                if (windowed == null) windowed = Flags.Has("-t2d-windowed");
                return windowed.Value;
            }
        }

        static bool Fixed { get { return Flags.Vita; } }

        // A test run (-t2d-windowed) never locks or confines the cursor (Windows' cursor clip is global).
        public static void set_lockState(CursorLockMode value)
        {
            Cursor.lockState = Windowed ? CursorLockMode.None : value;
        }

        public static void set_fullScreen(bool value)
        {
            if (Fixed) return;
            Screen.fullScreen = value && !Windowed;
        }

        public static void set_fullScreenMode(FullScreenMode value)
        {
            if (Fixed) return;
            Screen.fullScreenMode = Windowed ? FullScreenMode.Windowed : value;
        }

        public static void SetResolution(int width, int height, bool fullscreen)
        {
            if (Fixed) return;
            if (Windowed) Screen.SetResolution(WindowWidth, WindowHeight, false);
            else Screen.SetResolution(width, height, fullscreen);
        }

        public static void SetResolution(int width, int height, bool fullscreen, int preferredRefreshRate)
        {
            if (Fixed) return;
            if (Windowed) Screen.SetResolution(WindowWidth, WindowHeight, false);
            else Screen.SetResolution(width, height, fullscreen, preferredRefreshRate);
        }

        public static void SetResolution(int width, int height, FullScreenMode mode)
        {
            if (Fixed) return;
            if (Windowed) Screen.SetResolution(WindowWidth, WindowHeight, FullScreenMode.Windowed);
            else Screen.SetResolution(width, height, mode);
        }
    }
}
