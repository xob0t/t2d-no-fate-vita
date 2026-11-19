// Terminator 2D: NO FATE's Unlit/CRTShader, written again for Unity 2018.2 (PS Vita and Direct3D 11).
// The game ships it compiled for Direct3D 11 only; this follows its fragment program step by step
// (docs/shaders.md): a 398x224 source drawn over _OutputSize pixels as CRT scanlines, with a sharp
// Gaussian across each line, a raised-cosine beam between lines, an RGB stripe mask every three output
// columns and a brightness correction for what the mask takes away.
// The game creates the material (BitmapData, with _OutputSize the camera's pixel size) but draws nothing
// with it: its CRT option draws crt_layer over the frame with Unlit/NormalShader instead.
//
// T2D_CRT_FAST is the cheaper variant for the PS Vita (docs/shaders.md, "Cost"). It is a global keyword:
// Shader.EnableKeyword("T2D_CRT_FAST") switches every material of this shader at run time, and a build
// without it never compiles the variant out (multi_compile keeps both).
Shader "Unlit/CRTShader" {
    Properties {
        _MainTex ("Texture", 2D) = "white" {}
        _OutputSize ("OutputSize", Vector) = (398, 224, 0, 0)
    }
    SubShader {
        Pass {
            ZTest Always
            ZWrite Off
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile __ T2D_CRT_FAST
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _OutputSize;

            // The source is always 398x224 in the original: its size is in the code, not a property.
            #define SOURCE_SIZE float2(398.0, 224.0)
            #define TEXEL float2(0.0025125628, 0.0044642859) // 1 / SOURCE_SIZE as the original rounds it

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

            // The aperture mask: output columns 3n, 3n+1 and 3n+2 keep red, green and blue at full strength,
            // the other two channels at half.
            float3 stripe_mask(float column)
            {
                float phase = frac(column * 0.33333334);
                return phase < 0.33333334 ? float3(1.0, 0.5, 0.5)
                     : phase < 0.6666667 ? float3(0.5, 1.0, 0.5)
                     : float3(0.5, 0.5, 1.0);
            }

            // Brings the brightest channel m to 41m / (20m + 21): up by up to 1.32 at m = 0.5, unchanged at 1,
            // which gives back about what the mask's halving took.
            float3 restore_brightness(float3 c)
            {
                float m = max(max(max(c.b, c.g), c.r), 5.9604645e-8);
                return c * (1.0 / m) * (m / (m * 0.48780488 + 0.51219511));
            }

            #ifndef T2D_CRT_FAST

            float3 row(float2 at, float4 w)
            {
                return tex2D(_MainTex, at).rgb * w.x
                     + tex2D(_MainTex, at + float2(TEXEL.x, 0.0)).rgb * w.y
                     + tex2D(_MainTex, at + float2(2.0 * TEXEL.x, 0.0)).rgb * w.z
                     + tex2D(_MainTex, at + float2(3.0 * TEXEL.x, 0.0)).rgb * w.w;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 output = i.texcoord * _OutputSize.xy;          // the output pixel
                float2 source = output * (SOURCE_SIZE / _OutputSize.xy); // the same point in source pixels
                // The first of four source columns (x) and the scanline above (y), at texel centres.
                float2 first = floor(source - float2(1.5, 0.5)) + 0.5;
                float2 d = source - first;                             // x in [1, 2), y in [0, 1)
                // Across the line: a Gaussian exp2(-4 x^2) of each column's distance, normalised by its sum.
                float4 dx = d.x - float4(0.0, 1.0, 2.0, 3.0);
                float4 w = exp2(-4.0 * dx * dx);
                float norm = 1.0 / (w.x + w.y + w.z + w.w);
                // Between lines: a raised cosine of 0.7 times the distance to each of the two lines, zero past 0.714.
                float2 beam = cos(min(float2(d.y, 1.0 - d.y) * 0.7, 0.5) * 6.2831855) * 0.5 + 0.5;
                float2 at = first * TEXEL;
                float3 c = row(at, w) * (beam.x * norm) + row(at + float2(0.0, TEXEL.y), w) * (beam.y * norm);
                return float4(restore_brightness(c * stripe_mask(output.x)), 1.0);
            }

            #else

            // The PS Vita variant. One scanline-weighted fetch pair per line instead of four: across the line it
            // reads the two nearest columns only (the outer two weigh at most 1/16 between them and are left
            // out), with the Gaussian and raised cosine as above; the mask and brightness are the same.
            float4 frag(v2f i) : SV_Target
            {
                float2 output = i.texcoord * _OutputSize.xy;
                float2 source = output * (SOURCE_SIZE / _OutputSize.xy);
                float2 first = floor(source - 0.5) + 0.5;              // the nearest column to the left, the line above
                float2 d = source - first;                             // both in [0, 1)
                float2 dx = float2(d.x, d.x - 1.0);
                float2 w = exp2(-4.0 * dx * dx);
                w *= 1.0 / (w.x + w.y);
                float2 beam = cos(min(float2(d.y, 1.0 - d.y) * 0.7, 0.5) * 6.2831855) * 0.5 + 0.5;
                float2 at = first * TEXEL;
                float3 top = tex2D(_MainTex, at).rgb * w.x + tex2D(_MainTex, at + float2(TEXEL.x, 0.0)).rgb * w.y;
                float3 bottom = tex2D(_MainTex, at + float2(0.0, TEXEL.y)).rgb * w.x + tex2D(_MainTex, at + TEXEL).rgb * w.y;
                float3 c = top * beam.x + bottom * beam.y;
                return float4(restore_brightness(c * stripe_mask(output.x)), 1.0);
            }

            #endif
            ENDCG
        }
    }
}
