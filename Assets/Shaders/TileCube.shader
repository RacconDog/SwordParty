// Stylized shader for a modular cube tile map. Faces are classified by their
// world normal: upward faces are "Grass", everything else is "Rock". Tops get
// a rock-colored outline of adjustable width — but only along edges exposed
// to air: TileHeightBaker.cs bakes a one-texel-per-tile column-height texture,
// and an edge is outlined only where the neighboring column's top height
// differs. Adjacent same-height tiles merge into one shape with a single
// unbroken outline. Without a baker in the scene, tops render with no
// outline at all. Faces buried between two solid cubes point into the
// neighbor and are occluded, so only air-exposed faces ever show.
Shader "SwordParty/TileCube"
{
    Properties
    {
        _GrassColor("Grass Color", Color) = (0.35, 0.65, 0.25, 1)
        _RockColor("Rock Color", Color) = (0.45, 0.4, 0.35, 1)
        // Transition band drawn between the rock outline and the grass,
        // following the exact same shape as the outline.
        _BufferColor("Buffer Color", Color) = (0.45, 0.55, 0.22, 1)

        // Tile size and grid offset are NOT material properties: the
        // TileHeightBaker in the scene pushes them as shader globals, so the
        // baker and shader can never disagree about the grid.

        [Header(Top Outline)]
        _OutlineWidth("Outline Width", Range(0, 0.5)) = 0.08
        _BufferWidth("Buffer Width", Range(0, 0.5)) = 0.05

        [Header(Outline Noise)]
        _NoiseScale("Noise Scale", Range(0.1, 50)) = 9
        _NoiseStrength("Noise Strength", Range(0, 0.2)) = 0.02
        // Neighbor columns whose tops differ by more than this are treated as
        // a step, so the edge between them gets outlined.
        _HeightEpsilon("Height Step Threshold", Range(0.001, 1)) = 0.1

        [Header(Shading)]
        _TopThreshold("Top Facing Threshold", Range(0.1, 0.99)) = 0.7
        _ShadowStrength("Shadow Darkening", Range(0, 1)) = 0.6
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "TileCubeForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _GrassColor;
                half4 _RockColor;
                half4 _BufferColor;
                float _OutlineWidth;
                float _BufferWidth;
                float _NoiseScale;
                float _NoiseStrength;
                float _HeightEpsilon;
                float _TopThreshold;
                float _ShadowStrength;
            CBUFFER_END

            // Baked by TileHeightBaker.cs: R = world-space top height of the
            // tile column in that cell (one texel per tile, point filtered;
            // empty cells hold a huge negative value).
            TEXTURE2D(_TileHeightTex);
            SAMPLER(sampler_TileHeightTex);
            float4 _TileHeightRegion; // xy = region min XZ, zw = 1 / region size
            float _TileHeightBaked;   // 1 once a baker has published the texture
            float _TileGridSize;      // world size of one tile, from the baker
            float4 _TileGridOffset;   // xy = XZ offset of the grid lines

            // Also baked by TileHeightBaker.cs: per-cell inset of the tile's
            // flat top from each grid edge (x = west, y = east, z = south,
            // w = north). Beveled / chamfered tile models get their outline
            // along the real top edge instead of the grid line.
            TEXTURE2D(_TileInsetTex);
            SAMPLER(sampler_TileInsetTex);

            // Rounding radius of each top corner, measured from the mesh
            // (x = SW, y = SE, z = NW, w = NE; 0 = square corner), so the
            // outline follows tops with rounded corners too.
            TEXTURE2D(_TileCornerTex);
            SAMPLER(sampler_TileCornerTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
            };

            // 1 when the column containing worldXZ tops out at a different
            // height than ours, i.e. the border toward it is exposed to air.
            float Exposed(float2 worldXZ, float ourTopY)
            {
                float2 uv = (worldXZ - _TileHeightRegion.xy) * _TileHeightRegion.zw;
                float h = SAMPLE_TEXTURE2D(_TileHeightTex, sampler_TileHeightTex, uv).r;
                return step(_HeightEpsilon, abs(h - ourTopY));
            }

            // Distance to a top corner rounded with radius r (dx, dy are the
            // distances to the two adjacent top edges). Contributes nothing
            // outside the corner's r x r square, when the corner is square
            // (r = 0), or when it isn't exposed on both sides.
            float CornerDist(float dx, float dy, float bothExposed, float r)
            {
                float active = bothExposed * step(dx, r) * step(dy, r);
                float d = r - length(float2(r - dx, r - dy));
                return lerp(1e5, d, active);
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs vpi = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = vpi.positionCS;
                OUT.positionWS = vpi.positionWS;
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 normalWS = normalize(IN.normalWS);

                // Grid parameters come from the baker's globals; before any
                // bake exists, assume unit cubes at integer positions.
                float baked = step(0.5, _TileHeightBaked);
                float s = baked ? _TileGridSize : 1.0;
                float2 gridOffset = baked ? _TileGridOffset.xy : float2(0.5, 0.5);

                // Position within this tile, and distance (world units) to
                // each of its four edges: x = west, y = east, z = south,
                // w = north.
                float2 local = frac((IN.positionWS.xz - gridOffset) / s);
                float4 toEdge = float4(local.x, 1.0 - local.x,
                                       local.y, 1.0 - local.y) * s;

                float edgeDist = s; // far enough to never outline
                if (baked > 0.5)
                {
                    // Distance to the nearest EXPOSED border of the merged
                    // same-height region this tile belongs to. Edges shared
                    // with a same-height neighbor don't count, so joined
                    // tiles read as one big shape with no interior creases.
                    float2 center = (floor((IN.positionWS.xz - gridOffset) / s) + 0.5)
                                    * s + gridOffset;
                    float y = IN.positionWS.y;

                    // Pull the edge distances in by this cell's baked top
                    // insets, so the outline follows a beveled top's real
                    // edge instead of assuming a perfect square tile.
                    float2 uvC = (center - _TileHeightRegion.xy) * _TileHeightRegion.zw;
                    float4 t = toEdge -
                        SAMPLE_TEXTURE2D(_TileInsetTex, sampler_TileInsetTex, uvC);

                    float eW = Exposed(center + float2(-s, 0), y);
                    float eE = Exposed(center + float2( s, 0), y);
                    float eS = Exposed(center + float2( 0,-s), y);
                    float eN = Exposed(center + float2( 0, s), y);
                    edgeDist = min(edgeDist, lerp(s, t.x, eW));
                    edgeDist = min(edgeDist, lerp(s, t.y, eE));
                    edgeDist = min(edgeDist, lerp(s, t.z, eS));
                    edgeDist = min(edgeDist, lerp(s, t.w, eN));

                    // Corners the MESH rounds get a matching rounded outline;
                    // square geometry (radius 0) keeps sharp corners.
                    float4 cr = SAMPLE_TEXTURE2D(_TileCornerTex, sampler_TileCornerTex, uvC);
                    edgeDist = min(edgeDist, CornerDist(t.x, t.z, eW * eS, cr.x));
                    edgeDist = min(edgeDist, CornerDist(t.y, t.z, eE * eS, cr.y));
                    edgeDist = min(edgeDist, CornerDist(t.x, t.w, eW * eN, cr.z));
                    edgeDist = min(edgeDist, CornerDist(t.y, t.w, eE * eN, cr.w));

                    // Inner corners: the band wraps squarely around the
                    // corner point (Chebyshev distance keeps the outline a
                    // uniform width with a sharp 90-degree turn). Only fires
                    // when JUST the diagonal differs — if an adjacent edge is
                    // exposed too, that edge's band already owns this corner.
                    float eSW = Exposed(center + float2(-s,-s), y) * (1.0 - eW) * (1.0 - eS);
                    float eSE = Exposed(center + float2( s,-s), y) * (1.0 - eE) * (1.0 - eS);
                    float eNW = Exposed(center + float2(-s, s), y) * (1.0 - eW) * (1.0 - eN);
                    float eNE = Exposed(center + float2( s, s), y) * (1.0 - eE) * (1.0 - eN);
                    edgeDist = min(edgeDist, lerp(s, max(t.x, t.z), eSW));
                    edgeDist = min(edgeDist, lerp(s, max(t.y, t.z), eSE));
                    edgeDist = min(edgeDist, lerp(s, max(t.x, t.w), eNW));
                    edgeDist = min(edgeDist, lerp(s, max(t.y, t.w), eNE));
                }
                // No baker in the scene: no outline at all. Missing outlines
                // mean the TileHeightBaker isn't set up / hasn't baked.

                // Hand-drawn wobble: nudge the distance field with cheap
                // world-space noise so every band edge wiggles together and
                // the buffer keeps the exact same shape as the outline.
                float2 np = IN.positionWS.xz * _NoiseScale;
                float noise = sin(np.x + sin(np.y * 1.71)) *
                              sin(np.y * 1.37 + sin(np.x * 0.83));
                float d = edgeDist + noise * _NoiseStrength;

                // From the rim inward: rock out to _OutlineWidth, a buffer
                // band for another _BufferWidth, then grass. Crisp but
                // antialiased boundaries.
                float aa = fwidth(d);
                float rockBand = 1.0 - smoothstep(_OutlineWidth - aa, _OutlineWidth + aa, d);
                float bufferBand = 1.0 - smoothstep(_OutlineWidth + _BufferWidth - aa,
                                                    _OutlineWidth + _BufferWidth + aa, d);

                float isTop = step(_TopThreshold, normalWS.y);
                half3 topColor = lerp(_GrassColor.rgb, _BufferColor.rgb, bufferBand);
                topColor = lerp(topColor, _RockColor.rgb, rockBand);
                half3 albedo = lerp(_RockColor.rgb, topColor, isTop);

                // Flat cartoon lighting: full albedo in the light, darkened in
                // shadow / on faces turned away from the main light.
                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float ndotl = saturate(dot(normalWS, mainLight.direction));
                float lit = min(ndotl, mainLight.shadowAttenuation);
                half3 color = albedo * lerp(1.0 - _ShadowStrength, 1.0, lit);

                return half4(color, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // Same layout as the forward pass so the SRP Batcher stays happy.
            CBUFFER_START(UnityPerMaterial)
                half4 _GrassColor;
                half4 _RockColor;
                half4 _BufferColor;
                float _OutlineWidth;
                float _BufferWidth;
                float _NoiseScale;
                float _NoiseStrength;
                float _HeightEpsilon;
                float _TopThreshold;
                float _ShadowStrength;
            CBUFFER_END

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            float4 vert(Attributes IN) : SV_POSITION
            {
                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(IN.normalOS);

                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    float3 lightDir = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDir = _LightDirection;
                #endif

                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, lightDir));

                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return positionCS;
            }

            half4 frag() : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        // Needed so these tiles show up in the camera depth texture, which
        // CartoonWater samples for its shoreline band.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Same layout as the forward pass so the SRP Batcher stays happy.
            CBUFFER_START(UnityPerMaterial)
                half4 _GrassColor;
                half4 _RockColor;
                half4 _BufferColor;
                float _OutlineWidth;
                float _BufferWidth;
                float _NoiseScale;
                float _NoiseStrength;
                float _HeightEpsilon;
                float _TopThreshold;
                float _ShadowStrength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            float4 vert(Attributes IN) : SV_POSITION
            {
                return TransformObjectToHClip(IN.positionOS.xyz);
            }

            half4 frag() : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
