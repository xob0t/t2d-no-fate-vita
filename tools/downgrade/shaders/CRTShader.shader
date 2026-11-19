// Terminator 2D: NO FATE's Unlit/CRTShader, written again for Unity 2018.2 (PS Vita and Direct3D 11).
// The game ships it compiled for Direct3D 11 only; this follows its fragment program step by step
// (docs/shaders.md): a 398x224 source drawn over _OutputSize pixels as CRT scanlines, with a sharp
// Gaussian across each line, a raised-cosine beam between lines, an RGB stripe mask every three output
// columns and a brightness correction for what the mask takes away.
// The game creates the material (BitmapData, with _OutputSize the camera's pixel size) but draws nothing
// with it: its CRT option draws crt_layer over the frame with Unlit/NormalShader instead.
//
// T2D_CRT_FAST is a cheaper variant for the PS Vita (docs/shaders.md, "Cost"): 6.0 ms per 960x544 pass on the
// console against the faithful one's 15.7 ms, a mean 0.2 levels from it on the game's pixel art (up to 25 at
// one-pixel lines), but it needs a bilinear-filtered source.
// It is a global keyword: Shader.EnableKeyword("T2D_CRT_FAST") switches every material of this shader
// at run time. multi_compile keeps both variants in every build.
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

            #ifndef T2D_CRT_FAST

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

            float3 line_of_four(float2 at, float4 w)
            {
                return tex2D(_MainTex, at).rgb * w.x
                     + tex2D(_MainTex, at + float2(TEXEL.x, 0.0)).rgb * w.y
                     + tex2D(_MainTex, at + float2(2.0 * TEXEL.x, 0.0)).rgb * w.z
                     + tex2D(_MainTex, at + float2(3.0 * TEXEL.x, 0.0)).rgb * w.w;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 output = i.texcoord * _OutputSize.xy;            // the output pixel
                float2 source = output * (SOURCE_SIZE / _OutputSize.xy); // the same point in source pixels
                // The first of four source columns (x) and the scanline above (y), at texel centres.
                float2 first = floor(source - float2(1.5, 0.5)) + 0.5;
                float2 d = source - first;                               // x in [1, 2), y in [0, 1)
                // Across the line: a Gaussian exp2(-4 x^2) of each column's distance, normalised by its sum.
                float4 dx = d.x - float4(0.0, 1.0, 2.0, 3.0);
                float4 w = exp2(-4.0 * dx * dx);
                float norm = 1.0 / (w.x + w.y + w.z + w.w);
                // Between lines: a raised cosine of 0.7 times the distance to each of the two lines, zero past 0.714.
                float2 beam = cos(min(float2(d.y, 1.0 - d.y) * 0.7, 0.5) * 6.2831855) * 0.5 + 0.5;
                float2 at = first * TEXEL;
                float3 c = line_of_four(at, w) * (beam.x * norm) + line_of_four(at + float2(0.0, TEXEL.y), w) * (beam.y * norm);
                // The aperture mask (output columns 3n, 3n+1, 3n+2 keep red, green, blue at full strength, the
                // others at half).
                float phase = frac(output.x * 0.33333334);
                float3 mask = phase < 0.33333334 ? float3(1.0, 0.5, 0.5) : phase < 0.6666667 ? float3(0.5, 1.0, 0.5) : float3(0.5, 0.5, 1.0);
                c *= mask;
                // The brightest channel m becomes 41m / (20m + 21): up to 1.32 times brighter at m = 0.5, the
                // same at 1, which gives back about what the mask took.
                float m = max(max(max(c.b, c.g), c.r), 5.9604645e-8);
                return float4(c * (1.0 / m) * (m / (m * 0.48780488 + 0.51219511)), 1.0);
            }

            #else

            // The PS Vita variant. Its differences from the faithful one, all in docs/shaders.md:
            // - Across the line it weighs the two nearest columns, as the Gaussian does, and leaves out the outer two
            //   (together at most 1/16 of the weight, at a column's centre).
            // - It reads the two columns on both lines with one bilinear fetch: the fetch lands between the columns
            //   at the Gaussian's split and between the lines at the beams' split, and the beams' sum scales it.
            //   That is the same sum of four, with the filter's own weights; on a point-filtered source it is the
            //   nearest texel instead.
            // - The raised cosine is a fitted odd quintic (within 0.00004 of it), the mask is arithmetic, and the
            //   coordinates are interpolated, not computed per pixel.
            // - The brightness correction is c / (0.4878 m + 0.5122), the original's c / m * m / (0.4878 m + 0.5122),
            //   in half precision.

            struct v2f
            {
                float4 position : SV_POSITION;
                float3 at : TEXCOORD0; // xy: the point in source pixels, less half a pixel; z: the output column / 3
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.position = UnityObjectToClipPos(v.vertex.xyz);
                o.at.xy = v.texcoord * SOURCE_SIZE - 0.5;
                o.at.z = v.texcoord.x * _OutputSize.x * 0.33333334;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 d = frac(i.at.xy);              // from the nearest column to the left and the line above
                float2 first = i.at.xy - d + 0.5;      // their texel centre
                // The right column's share of the two Gaussian weights exp2(-4 d^2) and exp2(-4 (1 - d)^2).
                float right = 1.0 / (1.0 + exp2(4.0 - 8.0 * d.x));
                // 0.5 + 0.5 cos(2 pi min(0.7 d, 0.5)) for each line, as 0.5 - 0.5 sin(pi t), t = min(1.4 d, 1) - 0.5.
                float2 t = min(float2(d.y, 1.0 - d.y) * 1.4, 1.0) - 0.5;
                float2 t2 = t * t;
                float2 beam = 0.5 - t * (1.5703204 + t2 * (-2.5684607 + t2 * 1.1498051));
                float sum = beam.x + beam.y;
                float2 at = (first + float2(right, beam.y / sum)) * TEXEL;
                half3 c = tex2D(_MainTex, at).rgb * sum;
                // Full strength on the channel of the column's place in its group of three, half on the others.
                half group = floor(frac(i.at.z) * 3.0);
                c *= 1.0 - 0.5 * saturate(abs(group - half3(0.0, 1.0, 2.0)));
                half m = max(max(c.r, c.g), c.b);
                return half4(c / (m * 0.48780488 + 0.51219511), 1.0);
            }

            #endif
            ENDCG
        }
    }
}
