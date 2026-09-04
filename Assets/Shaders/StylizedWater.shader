// Stylized water for a flat plane, following Sebastian Lague's ocean shader
// (Coding Adventure: Atmosphere, ~12:30-16:00):
//   1. Camera depth texture -> distance seen through the water, blended
//      between a shallow and a deep colour.
//   2. More transparent when viewed from a steep angle and in shallow water.
//   3. Specular sun highlight split into three stylized bands.
//   4. Small sin waves in the vertex shader to break up the flat outline.
//   5. White foam outline around intersecting geometry, drawn on the water
//      surface using the shore-distance texture baked by WaterRipples.cs
//      (so it reads correctly even looking straight down), plus ripple
//      rings that march outward from that shoreline.
// Needs "Depth Texture" enabled on the active URP asset. Use a mesh with
// some subdivisions (e.g. Unity's Plane) so the vertex waves have vertices
// to move.
Shader "SwordParty/StylizedWater"
{
    Properties
    {
        [Header(Colour)]
        _ShallowColor("Shallow Color", Color) = (0.32, 0.8, 0.95, 0.3)
        _DeepColor("Deep Color", Color) = (0.07, 0.35, 0.65, 0.9)
        _DepthMaxDistance("Depth Fade Distance", Range(0.1, 20)) = 3

        [Header(Transparency)]
        _FresnelPower("Fresnel Power", Range(0.5, 10)) = 4
        _Murkiness("Murkiness", Range(0, 3)) = 0.3

        [Header(Specular)]
        _Smoothness("Smoothness", Range(0.01, 1)) = 0.1
        _SpecularStrength("Specular Strength", Range(0, 2)) = 1
        [IntRange] _SpecularBands("Specular Bands", Range(1, 8)) = 3

        [Header(Normal Map)]
        [Normal][NoScaleOffset] _NormalMap("Normal Map", 2D) = "bump" {}
        _NormalTiling("Normal Tiling", Range(0.001, 2)) = 0.15
        _NormalStrength("Normal Strength", Range(0, 2)) = 0.5
        _NormalScrollSpeed("Normal Scroll Speed", Range(0, 2)) = 0.1

        [Header(Waves)]
        _WaveHeight("Wave Height", Range(0, 2)) = 0.1
        _WaveFrequency("Wave Frequency", Range(0.02, 5)) = 0.6
        _WaveSpeed("Wave Speed", Range(0, 5)) = 0.8

        [Header(Foam)]
        _FoamColor("Foam Color", Color) = (1, 1, 1, 1)
        _FoamDist("Foam Width", Range(0.01, 5)) = 0.4
        _FoamNoiseScale("Foam Noise Scale", Range(0.05, 10)) = 1.5
        _FoamNoiseStrength("Foam Noise Strength", Range(0, 2)) = 0.2
        _FoamNoiseSpeed("Foam Noise Speed", Range(0, 5)) = 0.5

        [Header(Foam Ripples)]
        _RippleSpacing("Ripple Spacing", Range(0.05, 5)) = 0.9
        _RippleSpeed("Ripple Speed", Range(0, 5)) = 0.8
        _RippleWidth("Ripple Line Width", Range(0.01, 1)) = 0.25
        _RippleExtent("Ripple Fade Distance", Range(0.1, 20)) = 4
        _RippleNoiseScale("Ripple Noise Scale", Range(0.05, 10)) = 0.8
        _RippleNoiseStrength("Ripple Noise Strength", Range(0, 2)) = 0.35
        _RippleNoiseSpeed("Ripple Noise Speed", Range(0, 5)) = 0.4
        _RippleBreakup("Ripple Breakup", Range(0, 2)) = 1
        _RippleBreakScale("Ripple Breakup Scale", Range(0.05, 10)) = 1.2
        _RippleBreakSpeed("Ripple Breakup Speed", Range(0, 5)) = 0.3
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
            Name "StylizedWater"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor;
                half4 _DeepColor;
                float _DepthMaxDistance;
                float _FresnelPower;
                float _Murkiness;
                float _Smoothness;
                float _SpecularStrength;
                float _SpecularBands;
                float _NormalTiling;
                float _NormalStrength;
                float _NormalScrollSpeed;
                float _WaveHeight;
                float _WaveFrequency;
                float _WaveSpeed;
                half4 _FoamColor;
                float _FoamDist;
                float _FoamNoiseScale;
                float _FoamNoiseStrength;
                float _FoamNoiseSpeed;
                float _RippleSpacing;
                float _RippleSpeed;
                float _RippleWidth;
                float _RippleExtent;
                float _RippleNoiseScale;
                float _RippleNoiseStrength;
                float _RippleNoiseSpeed;
                float _RippleBreakup;
                float _RippleBreakScale;
                float _RippleBreakSpeed;
            CBUFFER_END

            // Baked by WaterRipples.cs (set as globals): R = flat on-plane
            // distance, in world units, to the nearest geometry that pokes
            // through the water surface.
            TEXTURE2D(_ShoreDistTex);
            SAMPLER(sampler_ShoreDistTex);
            float4 _ShoreDistRegion; // xy = region min XZ, zw = 1 / region size

            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);

            // Big slow waves that displace the mesh. Returns the height and
            // its XZ gradient so the fragment shader can rebuild the normal
            // analytically instead of needing neighbouring vertices.
            float SumWaves(float2 p, float t, out float2 grad)
            {
                const float2 d1 = normalize(float2(1.0, 0.35));
                const float2 d2 = normalize(float2(-0.4, 1.0));
                float a1 = dot(p, d1) * _WaveFrequency + t;
                float a2 = dot(p, d2) * _WaveFrequency * 1.37 - t * 1.21;

                grad = (cos(a1) * 0.6 * d1 +
                        cos(a2) * 0.4 * 1.37 * d2) * _WaveFrequency * _WaveHeight;
                return (sin(a1) * 0.6 + sin(a2) * 0.4) * _WaveHeight;
            }

            float Hash(float3 p)
            {
                return frac(sin(dot(p, float3(127.1, 311.7, 74.7))) * 43758.5453);
            }

            // 3D value noise: xy = position on the plane, z = time, so the
            // pattern morphs over time instead of just sliding.
            float ValueNoise(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);

                float n00 = lerp(Hash(i),                     Hash(i + float3(1, 0, 0)), f.x);
                float n10 = lerp(Hash(i + float3(0, 1, 0)),   Hash(i + float3(1, 1, 0)), f.x);
                float n01 = lerp(Hash(i + float3(0, 0, 1)),   Hash(i + float3(1, 0, 1)), f.x);
                float n11 = lerp(Hash(i + float3(0, 1, 1)),   Hash(i + float3(1, 1, 1)), f.x);
                return lerp(lerp(n00, n10, f.y), lerp(n01, n11, f.y), f.z);
            }

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

                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                float2 grad;
                posWS.y += SumWaves(posWS.xz, _Time.y * _WaveSpeed, grad);

                OUT.positionWS = posWS;
                OUT.positionCS = TransformWorldToHClip(posWS);

                float4 ndc = OUT.positionCS * 0.5;
                OUT.positionNDC.xy = float2(ndc.x, ndc.y * _ProjectionParams.x) + ndc.w;
                OUT.positionNDC.zw = OUT.positionCS.zw;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 uv = IN.positionNDC.xy / IN.positionNDC.w;
                float t = _Time.y;

                // --- 1. Distance seen through the water ------------------
                // Scene depth behind this pixel minus the depth of the water
                // pixel itself = how far the view ray travels through water.
                float rawDepth = SampleSceneDepth(uv);
                float sceneEyeDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                float viewDist = max(sceneEyeDepth - IN.positionNDC.w, 0.0);
                float depth01 = 1.0 - exp(-viewDist / _DepthMaxDistance);
                half3 col = lerp(_ShallowColor.rgb, _DeepColor.rgb, depth01);

                // --- Surface normal from the scrolling normal map ---------
                // Two copies of the normal map scroll across the surface in
                // different directions to fake little waves that break up
                // the specular highlight. Sampled in world space so tiling
                // is independent of the mesh's UVs. The big vertex waves
                // deliberately don't bend this normal: at Normal Strength 0
                // the surface lights as a perfectly flat plane.
                float2 nuv1 = IN.positionWS.xz * _NormalTiling
                              + float2(1.0, 0.4) * (t * _NormalScrollSpeed);
                float2 nuv2 = IN.positionWS.xz * _NormalTiling * 0.83
                              - float2(0.5, 1.0) * (t * _NormalScrollSpeed * 0.87);
                float3 n1 = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, nuv1), _NormalStrength);
                float3 n2 = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, nuv2), _NormalStrength);
                // The plane faces up, so tangent-space xy maps onto world xz.
                float2 grad = -(n1.xy + n2.xy);
                float3 N = normalize(float3(-grad.x, 1.0, -grad.y));
                float3 V = GetWorldSpaceNormalizeViewDir(IN.positionWS);

                // --- 2. Transparency --------------------------------------
                // Clear in the shallows, and clearer when looking straight
                // down (fresnel: opacity rises towards grazing angles).
                float fresnel = pow(1.0 - saturate(dot(V, N)), _FresnelPower);
                float alpha = lerp(_ShallowColor.a, _DeepColor.a, depth01);
                alpha = lerp(alpha, 1.0, fresnel);

                // Murkiness: opacity keeps climbing with view distance
                // through the water, past the colour fade, so really deep
                // geometry fades out completely. 0 = crystal clear.
                float murk = 1.0 - exp(-viewDist * _Murkiness / _DepthMaxDistance);
                alpha = lerp(alpha, 1.0, murk);

                // --- 3. Banded specular highlight -------------------------
                // Sebastian's calculateSpecularHighlight: angle between the
                // half vector and the normal, Gaussian falloff by smoothness.
                Light mainLight = GetMainLight();
                float3 H = normalize(mainLight.direction + V);
                float specularAngle = acos(saturate(dot(H, N)));
                float specularExponent = specularAngle / _Smoothness;
                float specularHighlight = exp(-specularExponent * specularExponent);

                // Quantize the smooth falloff into N flat bands (brightest
                // in the middle, stepping down to 1/N at the outer ring).
                // The step() cuts off the Gaussian's faint infinite tail so
                // the outermost band has a crisp edge.
                float bandCount = max(_SpecularBands, 1.0);
                float stepped = ceil(saturate(specularHighlight) * bandCount) / bandCount;
                stepped *= step(0.02, specularHighlight);
                float3 specular = mainLight.color * stepped * _SpecularStrength;

                // Subtle diffuse response so the waves read in the shading.
                col *= mainLight.color * (saturate(dot(N, mainLight.direction)) * 0.3 + 0.7);

                // --- 5. Foam outline around intersecting geometry ---------
                // Painted onto the water surface itself: flat distance from
                // this pixel to the nearest waterline, from the baked shore
                // texture. View-independent, so the band is the same width
                // from any angle, including straight down.
                float2 duv = (IN.positionWS.xz - _ShoreDistRegion.xy) * _ShoreDistRegion.zw;
                float shoreDist = SAMPLE_TEXTURE2D(_ShoreDistTex, sampler_ShoreDistTex, duv).r;
                // Until WaterRipples bakes (it runs at Start), the globals
                // are zero and the whole plane would read as foam: skip it.
                float baked = step(0.0001, abs(_ShoreDistRegion.z));

                // Rough up the outline's outer edge with its own morphing
                // noise, independent of the ripple noise below.
                float foamNoise = ValueNoise(float3(IN.positionWS.xz * _FoamNoiseScale,
                                                    t * _FoamNoiseSpeed));
                float foamDist = shoreDist + (foamNoise - 0.5) * _FoamNoiseStrength;
                float foam = step(foamDist, _FoamDist);

                // Ripple rings marching outward from the shoreline, fading
                // out with distance. step() on shoreDist keeps them off the
                // land side of the waterline. Slowly morphing noise warps
                // the distance field so the rings wobble instead of tracing
                // the geometry outline exactly.
                float noise = ValueNoise(float3(IN.positionWS.xz * _RippleNoiseScale,
                                                t * _RippleNoiseSpeed + 17.0));
                float ringDist = shoreDist + (noise - 0.5) * _RippleNoiseStrength;
                float ring = step(1.0 - _RippleWidth,
                                  frac(ringDist / _RippleSpacing - t * _RippleSpeed));

                // Subtractive noise: erases chunks of each ring, eating more
                // the farther it is from the coast, so rings leave the shore
                // solid and progressively dissolve as they travel outward.
                // _RippleBreakup sets how much is eaten by the fade distance
                // (1 = fully dissolved right as they fade out).
                float breakNoise = ValueNoise(float3(IN.positionWS.xz * _RippleBreakScale,
                                                     t * _RippleBreakSpeed + 53.0));
                float erode = _RippleBreakup * saturate(shoreDist / _RippleExtent);
                ring *= step(erode, breakNoise);

                float fade = saturate(1.0 - shoreDist / _RippleExtent);
                foam = saturate(foam + ring * fade * step(0.001, shoreDist)) * baked;

                col = lerp(col, _FoamColor.rgb, foam);
                alpha = lerp(alpha, max(alpha, _FoamColor.a), foam);

                col += specular;
                alpha = max(alpha, saturate(stepped * _SpecularStrength));

                return half4(col, saturate(alpha));
            }
            ENDHLSL
        }
    }
}
