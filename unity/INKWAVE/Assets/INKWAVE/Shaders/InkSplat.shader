// The ink splat shader: draws one splat into the ink atlas (a render target).
//
// A URP port of the web build's src/world/paint.js (PAINT_FS). The blob outline (wob) is the same function the CPU
// gameplay grid evaluates, so the painted ink and the claimed turf agree on the edge.
//
// Atlas encoding (RGBA8):
//   R = team share (0 = team 0, 1 = team 1), composited "over" — the newest splat wins at a team border
//   G = wetness (1 = fresh, 0 = dry)
//   B = per-splat tone
//   A = coverage (MAX blended, so redrawing a spreading splat every frame is idempotent)
Shader "INKWAVE/InkSplat"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" }
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha
            BlendOp One Add
            BlendOpAlpha Max One
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;   // the atlas clip position (already in -1..1)
                float3 local      : TEXCOORD0;  // metres from the splat centre in face space
                float4 splat      : TEXCOORD1;  // radius, team, seed, flags
                float3 stretch    : TEXCOORD2;  // the travel direction in face space + the smear amount
                float4 grow       : TEXCOORD3;  // age / spread, drip progress, drips only
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 local      : TEXCOORD0;
                float4 splat      : TEXCOORD1;
                float3 stretch    : TEXCOORD2;
                float4 grow       : TEXCOORD3;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = float4(v.positionOS.xy, 0, 1);
                o.local = v.local;
                o.splat = v.splat;
                o.stretch = v.stretch;
                o.grow = v.grow;
                return o;
            }

            float hash1(float n) { return frac(sin(n) * 43758.5453123); }

            // the main-blob outline — the same function as PaintSystem.BlobWobble on the CPU
            float wob(float a, float s)
            {
                return 1.0 + 0.12 * sin(3.0 * a + s * 6.2831) + 0.08 * sin(5.0 * a + s * 17.0)
                    + 0.05 * sin(7.0 * a + s * 41.0) + 0.03 * sin(11.0 * a + s * 73.0)
                    + 0.018 * sin(17.0 * a + s * 29.0)
                    + 0.17 * pow(max(cos(a - s * 37.7), 0.0), 28.0)
                    + 0.12 * pow(max(cos(a - s * 53.3 - 2.1), 0.0), 36.0);
            }

            half4 frag(Varyings i) : SV_Target
            {
                float R = i.splat.x, team = i.splat.y, seed = i.splat.z;
                float dn = i.local.z;
                float r2 = R * R - dn * dn;
                if (r2 <= 0) discard;
                float r = sqrt(r2);
                float fall = clamp(r / max(R, 1e-3), 0.0, 1.0);

                float2 p = i.local.xy;
                float2 dir = i.stretch.xy;
                float sa = i.stretch.z;
                if (sa > 0)
                {
                    float a = dot(p, dir);
                    float2 perp = p - a * dir;
                    float s = a > 0 ? 1.0 + sa : 1.0 + 0.25 * sa;
                    p = perp + dir * (a / s);
                }

                float tn = i.grow.x;
                // the body floods out from ~40 % with a strong ease-out
                float tb = 1.0 - pow(1.0 - clamp(tn, 0.0, 1.0), 4.0);
                float grow = lerp(0.4, 1.0, tb);
                float ang = atan2(p.y, p.x);
                float edge = r * wob(ang, seed) * grow;

                float d = length(p);
                float cover = 1.0 - smoothstep(edge - 0.06, edge, d);
                cover *= fall;
                if (cover <= 0.003) discard;

                // wetness (green) fades as the splat dries
                float wet = 1.0 - clamp(tn, 0.0, 1.0);
                float tone = 0.85 + 0.15 * hash1(seed);
                return half4(team, wet * cover, tone * cover, cover);
            }
            ENDHLSL
        }
    }
}
