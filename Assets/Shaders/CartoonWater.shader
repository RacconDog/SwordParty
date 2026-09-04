// Cartoony water for a flat plane. A crisp white band appears wherever
// geometry intersects the surface (depth-buffer comparison), and stylized
// rings ripple outward from that band. Needs "Depth Texture" enabled on the
// active URP asset.
Shader "SwordParty/CartoonWater"
{
    Properties
    {
        _BaseColor("Water Color", Color) = (0.2, 0.55, 0.85, 0.85)
        _FoamColor("Outline / Ripple Color", Color) = (1, 1, 1, 1)

        [Header(Intersection Outline)]
        _OutlineWidth("Outline Width", Range(0.01, 2)) = 0.25

        [Header(Ripples)]
        _RippleSpacing("Ripple Spacing", Range(0.05, 5)) = 0.9
        _RippleSpeed("Ripple Speed", Range(0, 5)) = 0.8
        _RippleWidth("Ripple Line Width", Range(0.01, 1)) = 0.25
        _RippleExtent("Ripple Fade Distance", Range(0.1, 20)) = 4

        [Header(Hand Drawn Wobble)]
        _WobbleScale("Wobble Scale", Range(0.1, 20)) = 6
        _WobbleStrength("Wobble Strength", Range(0, 1)) = 0.12
        _WobbleSpeed("Wobble Speed", Range(0, 10)) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "CartoonWater"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _FoamColor;
                float _OutlineWidth;
                float _RippleSpacing;
                float _RippleSpeed;
                float _RippleWidth;
                float _RippleExtent;
                float _WobbleScale;
                float _WobbleStrength;
                float _WobbleSpeed;
            CBUFFER_END

            // Baked by WaterRipples.cs: R = distance (world units, on the
            // plane) to the nearest level geometry crossing the surface.
            TEXTURE2D(_ShoreDistTex);
            SAMPLER(sampler_ShoreDistTex);
            float4 _ShoreDistRegion; // xy = region min XZ, zw = 1 / region size

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float4 positionNDC : TEXCOORD1;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs vpi = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = vpi.positionCS;
                OUT.positionWS = vpi.positionWS;
                OUT.positionNDC = vpi.positionNDC;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 uv = IN.positionNDC.xy / IN.positionNDC.w;

                // Reconstruct the world position of whatever the camera sees
                // behind this pixel, then measure how far BELOW the water
                // surface it sits. Unlike raw view-depth, this is measured on
                // the plane's own (vertical) axis and is view-independent, so
                // bands hug the waterline instead of draping over the land's
                // screen-space depth.
                float rawDepth = SampleSceneDepth(uv);
                #if !UNITY_REVERSED_Z
                    rawDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1, rawDepth);
                #endif
                float3 sceneWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                float depth = max(IN.positionWS.y - sceneWS.y, 0.0);

                // Wiggle the distance field so lines look hand-drawn.
                float t = _Time.y * _WobbleSpeed;
                float wobble = sin(IN.positionWS.x * _WobbleScale + t) *
                               sin(IN.positionWS.z * _WobbleScale * 1.37 + t * 1.13);
                depth = max(depth + wobble * _WobbleStrength, 0.0);

                // Crisp white band where meshes touch the surface.
                float outline = step(depth, _OutlineWidth);

                // Rings marching across the PLANE, away from the baked
                // shoreline distance field (true flat distance to the level
                // geometry's waterline — terrain shape can't warp them).
                float2 duv = (IN.positionWS.xz - _ShoreDistRegion.xy) * _ShoreDistRegion.zw;
                float shoreDist = SAMPLE_TEXTURE2D(_ShoreDistTex, sampler_ShoreDistTex, duv).r;
                shoreDist = max(shoreDist + wobble * _WobbleStrength, 0.0);

                float ring = step(1.0 - _RippleWidth,
                                  frac(shoreDist / _RippleSpacing - _Time.y * _RippleSpeed));
                float fade = saturate(1.0 - shoreDist / _RippleExtent);

                // step() keeps ring foam off the land side of the waterline.
                float foam = saturate(outline + ring * fade * step(0.001, shoreDist));

                return lerp(_BaseColor, _FoamColor, foam);
            }
            ENDHLSL
        }
    }
}
