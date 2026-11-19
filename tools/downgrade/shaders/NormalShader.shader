// Terminator 2D: NO FATE's Unlit/NormalShader, written again for Unity 2018.2 (PS Vita and Direct3D 11).
// The game ships it compiled for Direct3D 11 only; this follows its programs (docs/shaders.md):
// the texture as it is, alpha-blended, with the pass state the game's shader stores.
// BitmapData draws every untinted sprite with it, and UnityMain.OnRenderImage the scaled frame and crt_layer.
Shader "Unlit/NormalShader" {
    Properties {
        _MainTex ("Base (RGB) Trans (A)", 2D) = "white" {}
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
                return tex2D(_MainTex, i.texcoord);
            }
            ENDCG
        }
    }
}
