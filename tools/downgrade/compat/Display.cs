using UnityEngine;

// The game's display, where the rewriter's platform pass needs T2DCompat (compat/rewriter/PlatformRules.cs, Stores).
// The game draws its 398x224 picture into a render texture (UnityMain.Start, cam_rt) and, with the CRT filter on,
// through a second one four times as tall (crt_4hscale, made each frame in UnityMain.OnPreRender and blitted in
// OnRenderImage). IL2CPP inlined OnPreRender's RenderTexture.GetTemporary(398, 896, 0, ARGB32, Default) into Unity 6's
// RenderTextureDescriptor internals; CrtTarget makes the same texture with 2018.2's GetTemporary. The 960x544 screen
// itself needs nothing here: UnityMain.ScreenStuff letterboxes the camera to 16:9 by Screen.width and height (the PS
// Vita's 960x544 gives bars of 2 pixels), and Window.cs keeps the game's resolution changes off the PS Vita.
// Part of T2DCompat; see Compat2018.cs.
namespace T2DCompat
{
    public static class Display
    {
        public static RenderTexture CrtTarget()
        {
            return RenderTexture.GetTemporary(398, 896, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
        }
    }
}
