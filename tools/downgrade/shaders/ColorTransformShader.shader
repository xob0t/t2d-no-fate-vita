// Terminator 2D: NO FATE's Unlit/ColorTransformShader, written again for Unity 2018.2 (PS Vita and Direct3D 11).
// The game ships it compiled for Direct3D 11 only; this follows its programs (docs/shaders.md):
// a Flash ColorTransform, texture * _Color + _Coloff, alpha-blended, with the pass state the game's shader stores.
// BitmapData sets _Color to the transform's multipliers and _Coloff to its offsets / 255 (fillRect: _Color 0,
// _Coloff the fill colour).
Shader "Unlit/ColorTransformShader" {
    Properties {
        _MainTex ("Base (RGB) Trans (A)", 2D) = "white" {}
        _Color ("Main Color", Color) = (0, 0, 0, 0)
        _Coloff ("Color Offsets", Vector) = (0, 0, 0, 0)
    }
    SubShader {
        Tags { "IGNOREPROJECTOR"="true" "QUEUE"="Transparent" "RenderType"="Transparent" }
        Pass {
            Tags { "IGNOREPROJECTOR"="true" "QUEUE"="Transparent" "RenderType"="Transparent" }
            Blend SrcAlpha OneMinusSrcAlpha, One One
            ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _Color;
            float4 _Coloff;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 position : SV_POSITION;
                float2 texcoord : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                // The original reads the position's xyz and takes w as 1, and passes the UV on without _MainTex_ST.
                o.position = UnityObjectToClipPos(v.vertex.xyz);
                o.texcoord = v.texcoord;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                // Direct3D 11 clamps a UNORM target's output to [0, 1] before blending, so an offset that takes
                // alpha past 1 still blends as 1. saturate() makes the PS Vita do the same; the game's targets
                // (ARGB32 render textures and the back buffer) are all UNORM, where it changes nothing.
                return saturate(tex2D(_MainTex, i.texcoord) * _Color + _Coloff);
            }
            ENDCG
        }
    }
}
