// Working bodies for the Unity 6 engine binding calls (*_Injected) that IL2CPP inlined into the game's
// methods and Rewind recovered as they were. Same signatures as gen_injected.py's compile-only stubs;
// each resolves its fake native pointers through Registry and calls 2018.2's public API.
using System;
using UnityEngine;
using UnityEngine.Networking;

namespace Compat2018
{
    public static class Injected
    {
        static T O<T>(IntPtr p) where T : class { return Registry.Get<T>(p); }

        public static float AnimationCurve_Evaluate_Injected(IntPtr p0, float p1) { return O<AnimationCurve>(p0).Evaluate(p1); }
        public static bool AsyncOperation_get_isDone_Injected(IntPtr p0) { return O<AsyncOperation>(p0).isDone; }
        public static float AudioClip_get_length_Injected(IntPtr p0) { return O<AudioClip>(p0).length; }
        public static void AudioSource_PlayHelper_Injected(IntPtr p0, ulong p1) { O<AudioSource>(p0).Play(p1); }
        public static void AudioSource_Play_Injected(IntPtr p0, double p1)
        {
            if (p1 == 0) O<AudioSource>(p0).Play();
            else O<AudioSource>(p0).PlayDelayed((float)p1);
        }
        public static void AudioSource_Stop_Injected(IntPtr p0, bool p1) { O<AudioSource>(p0).Stop(); }
        public static bool AudioSource_get_isPlaying_Injected(IntPtr p0) { return O<AudioSource>(p0).isPlaying; }
        public static void AudioSource_set_panStereo_Injected(IntPtr p0, float p1) { O<AudioSource>(p0).panStereo = p1; }
        public static void AudioSource_set_time_Injected(IntPtr p0, float p1) { O<AudioSource>(p0).time = p1; }
        public static void AudioSource_set_volume_Injected(IntPtr p0, float p1) { O<AudioSource>(p0).volume = p1; }
        public static bool Behaviour_get_enabled_Injected(IntPtr p0) { return O<Behaviour>(p0).enabled; }
        public static void Behaviour_set_enabled_Injected(IntPtr p0, bool p1) { O<Behaviour>(p0).enabled = p1; }
        public static void Camera_ScreenToViewportPoint_Injected(IntPtr p0, ref Vector3 p1, out Vector3 p2) { p2 = O<Camera>(p0).ScreenToViewportPoint(p1); }
        public static void Camera_ScreenToWorldPoint_Injected(IntPtr p0, ref Vector3 p1, Camera.MonoOrStereoscopicEye p2, out Vector3 p3) { p3 = O<Camera>(p0).ScreenToWorldPoint(p1); }
        public static void Camera_ViewportToWorldPoint_Injected(IntPtr p0, ref Vector3 p1, Camera.MonoOrStereoscopicEye p2, out Vector3 p3) { p3 = O<Camera>(p0).ViewportToWorldPoint(p1); }
        public static int Camera_get_cullingMask_Injected(IntPtr p0) { return O<Camera>(p0).cullingMask; }
        public static float Camera_get_depth_Injected(IntPtr p0) { return O<Camera>(p0).depth; }
        public static int Camera_get_pixelHeight_Injected(IntPtr p0) { return O<Camera>(p0).pixelHeight; }
        public static void Camera_get_pixelRect_Injected(IntPtr p0, out Rect p1) { p1 = O<Camera>(p0).pixelRect; }
        public static int Camera_get_pixelWidth_Injected(IntPtr p0) { return O<Camera>(p0).pixelWidth; }
        public static void Camera_get_rect_Injected(IntPtr p0, out Rect p1) { p1 = O<Camera>(p0).rect; }
        public static void Camera_set_rect_Injected(IntPtr p0, ref Rect p1) { O<Camera>(p0).rect = p1; }
        public static IntPtr Component_get_gameObject_Injected(IntPtr p0) { return Registry.Of(O<Component>(p0).gameObject); }
        public static IntPtr DownloadHandlerAudioClip_get_audioClip_Injected(IntPtr p0) { return Registry.Of(O<DownloadHandlerAudioClip>(p0).audioClip); }
        public static void GL_GLClear_Injected(bool p0, bool p1, ref Color p2, float p3) { GL.Clear(p0, p1, p2, p3); }
        public static int GameObject_get_layer_Injected(IntPtr p0) { return O<GameObject>(p0).layer; }
        public static void GameObject_set_layer_Injected(IntPtr p0, int p1) { O<GameObject>(p0).layer = p1; }
        public static void MaterialPropertyBlock_Clear_Injected(IntPtr p0, bool p1) { O<MaterialPropertyBlock>(p0).Clear(); }
        public static void MaterialPropertyBlock_SetColorImpl_Injected(IntPtr p0, int p1, ref Color p2) { O<MaterialPropertyBlock>(p0).SetColor(p1, p2); }
        public static void MaterialPropertyBlock_SetVectorImpl_Injected(IntPtr p0, int p1, ref Vector4 p2) { O<MaterialPropertyBlock>(p0).SetVector(p1, p2); }
        public static void Material_SetColorImpl_Injected(IntPtr p0, int p1, ref Color p2) { O<Material>(p0).SetColor(p1, p2); }
        public static void Quaternion_AngleAxis_Injected(float p0, ref Vector3 p1, out Quaternion p2) { p2 = Quaternion.AngleAxis(p0, p1); }
        public static void RenderTexture_set_useMipMap_Injected(IntPtr p0, bool p1) { O<RenderTexture>(p0).useMipMap = p1; }
        public static IntPtr Renderer_GetSharedMaterial_Injected(IntPtr p0) { return Registry.Of(O<Renderer>(p0).sharedMaterial); }
        public static void Renderer_SetMaterial_Injected(IntPtr p0, IntPtr p1) { O<Renderer>(p0).material = p1 == IntPtr.Zero ? null : O<Material>(p1); }
        public static void Renderer_set_sortingOrder_Injected(IntPtr p0, int p1) { O<Renderer>(p0).sortingOrder = p1; }
        public static IntPtr Resources_UnloadUnusedAssets_Injected() { return Registry.Of(Resources.UnloadUnusedAssets()); }
        public static void SpriteRenderer_get_color_Injected(IntPtr p0, out Color p1) { p1 = O<SpriteRenderer>(p0).color; }
        public static void SpriteRenderer_set_color_Injected(IntPtr p0, ref Color p1) { O<SpriteRenderer>(p0).color = p1; }
        public static void Sprite_get_bounds_Injected(IntPtr p0, out Bounds p1) { p1 = O<Sprite>(p0).bounds; }
        public static void Sprite_get_rect_Injected(IntPtr p0, out Rect p1) { p1 = O<Sprite>(p0).rect; }
        public static int Transform_GetSiblingIndex_Injected(IntPtr p0) { return O<Transform>(p0).GetSiblingIndex(); }
        public static void Transform_SetParent_Injected(IntPtr p0, IntPtr p1, bool p2) { O<Transform>(p0).SetParent(p1 == IntPtr.Zero ? null : O<Transform>(p1), p2); }
        public static void Transform_get_position_Injected(IntPtr p0, out Vector3 p1) { p1 = O<Transform>(p0).position; }
        public static void Transform_set_localPosition_Injected(IntPtr p0, ref Vector3 p1) { O<Transform>(p0).localPosition = p1; }
        public static void Transform_set_localScale_Injected(IntPtr p0, ref Vector3 p1) { O<Transform>(p0).localScale = p1; }
        public static void Transform_set_position_Injected(IntPtr p0, ref Vector3 p1) { O<Transform>(p0).position = p1; }
        public static IntPtr UnityWebRequest_BeginWebRequest_Injected(IntPtr p0) { return Registry.Of(O<UnityWebRequest>(p0).SendWebRequest()); }
        // Unity 6's UnityWebRequest.Result: InProgress 0, Success 1, ConnectionError 2, ProtocolError 3, DataProcessingError 4.
        public static int UnityWebRequest_get_result_Injected(IntPtr p0)
        {
            UnityWebRequest r = O<UnityWebRequest>(p0);
            if (!r.isDone) return 0;
            if (r.isNetworkError) return 2;
            if (r.isHttpError) return 3;
            return 1;
        }
    }
}
