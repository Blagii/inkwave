// The level surface shader: the block's own colour, painted over by the ink from the atlas.
//
// A URP port of the web build's src/world/levelMaterial.js + src/world/inkShading.js. The ink atlas is sampled in
// face space: every paintable face owns a rectangle in the atlas, and the face's UVs point into it.
//
//   R = team share (0 / 1)   G = wetness   B = tone   A = coverage
Shader "INKWAVE/InkSurface"
{
    Properties
    {
        _BaseColor ("Base colour", Color) = (0.85, 0.83, 0.76, 1)
        _TeamColor0 ("Team 0 ink", Color) = (1, 0.54, 0.08, 1)
        _TeamColor1 ("Team 1 ink", Color) = (0.18, 0.36, 1, 1)
        _InkAtlas ("Ink atlas", 2D) = "black" {}
        _InkParams ("Atlas size, used height", Vector) = (4096, 4096, 0, 0)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_InkAtlas);
            SAMPLER(sampler_InkAtlas);

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _TeamColor0;
                half4 _TeamColor1;
                float4 _InkParams;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;   // the atlas UV of this face
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = v.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half4 ink = SAMPLE_TEXTURE2D(_InkAtlas, sampler_InkAtlas, i.uv);
                float cover = saturate(ink.a);

                // the ink thickness: a cubic B-spline read of the coverage, so the ink reads as a rounded height field
                float h = saturate(cover);
                h = h * h * (3.0 - 2.0 * h);
                h = h * h * (3.0 - 2.0 * h);

                half3 inkCol = lerp(_TeamColor0.rgb, _TeamColor1.rgb, saturate(ink.r));
                inkCol *= lerp(0.85, 1.05, ink.b);

                // fresh ink is glossier and a little darker; dry ink is flat
                float wet = saturate(ink.g);
                half3 albedo = lerp(_BaseColor.rgb, inkCol * 0.86, cover);
                float gloss = lerp(0.12, 0.62, cover * wet);

                Light l = GetMainLight();
                float ndl = saturate(dot(normalize(i.normalWS), l.direction));
                half3 lit = albedo * (0.35 + 0.75 * ndl) * l.color;
                lit += l.color * gloss * cover * wet * 0.25;

                return half4(lit, 1);
            }
            ENDHLSL
        }
    }
}
