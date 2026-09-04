Shader "Hidden/TiltShift"
{
    // Screen-space depth-based tilt-shift. A world-space "focus band" (a slab of
    // depth centered on _FocusDistance) stays sharp; everything nearer or farther
    // ramps into a blurred copy, faking the shallow depth of field that makes a
    // scene read as a tiny tabletop model.
    //
    // Driven entirely from TiltShiftRendererFeature.cs. Four passes:
    //   0 Copy       - stash the sharp frame so Composite can read it
    //   1 BlurH      - separable gaussian, horizontal
    //   2 BlurV      - separable gaussian, vertical
    //   3 Composite  - lerp(sharp, blurred, circleOfConfusion(depth))
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        // The unblurred frame, kept aside by the Copy pass and read by Composite.
        TEXTURE2D_X(_SharpTex);
        // Coverage of the "keep sharp" layers (white where they are). Only bound
        // when _UseMask is on.
        TEXTURE2D_X(_MaskTex);
        float _UseMask;

        float  _FocusMin;       // nearest eye-space distance (world units) kept sharp
        float  _FocusMax;       // farthest eye-space distance kept sharp
        float  _FocusTransition;// how far past the band it takes to reach full blur
        float  _Falloff;        // shapes the sharp->blur ramp (1 = linear)
        float  _MaxBlur;        // upper bound on the blur mix (0..1)
        float  _BlurSize;       // gaussian tap spacing, in source texels

        // 9-tap separable gaussian along an arbitrary axis (texel units).
        half4 GaussianBlur(float2 uv, float2 dir)
        {
            float2 texel = _BlitTexture_TexelSize.xy * dir * _BlurSize;

            // Normalized weights for sigma ~ 2.
            const float w0 = 0.2270270270;
            const float w1 = 0.1945945946;
            const float w2 = 0.1216216216;
            const float w3 = 0.0540540541;
            const float w4 = 0.0162162162;

            half4 col = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv) * w0;
            col += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texel * 1.0) * w1;
            col += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - texel * 1.0) * w1;
            col += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texel * 2.0) * w2;
            col += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - texel * 2.0) * w2;
            col += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texel * 3.0) * w3;
            col += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - texel * 3.0) * w3;
            col += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texel * 4.0) * w4;
            col += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - texel * 4.0) * w4;
            return col;
        }

        // How out-of-focus the pixel at this uv is, 0 (sharp) .. 1 (max blur).
        float CircleOfConfusion(float2 uv)
        {
            float rawDepth = SampleSceneDepth(uv);
            float eyeDepth = LinearEyeDepth(rawDepth, _ZBufferParams);

            // How far outside the [min, max] sharp band we are (0 while inside),
            // ramped to full blur over _FocusTransition world units.
            float outside = max(_FocusMin - eyeDepth, eyeDepth - _FocusMax);
            float coc = saturate(max(outside, 0.0) / max(_FocusTransition, 1e-4));
            coc = pow(coc, _Falloff);
            return coc * _MaxBlur;
        }
        ENDHLSL

        Pass // 0 - Copy
        {
            Name "Copy"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            half4 Frag(Varyings i) : SV_Target
            {
                return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, i.texcoord);
            }
            ENDHLSL
        }

        Pass // 1 - Horizontal blur
        {
            Name "BlurH"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            half4 Frag(Varyings i) : SV_Target
            {
                return GaussianBlur(i.texcoord, float2(1.0, 0.0));
            }
            ENDHLSL
        }

        Pass // 2 - Vertical blur
        {
            Name "BlurV"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            half4 Frag(Varyings i) : SV_Target
            {
                return GaussianBlur(i.texcoord, float2(0.0, 1.0));
            }
            ENDHLSL
        }

        Pass // 3 - Composite
        {
            Name "Composite"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            half4 Frag(Varyings i) : SV_Target
            {
                half4 sharp   = SAMPLE_TEXTURE2D_X(_SharpTex,   sampler_LinearClamp, i.texcoord);
                half4 blurred = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, i.texcoord);
                float coc = CircleOfConfusion(i.texcoord);

                // Excluded layers: their covered pixels stay perfectly sharp.
                if (_UseMask > 0.5)
                {
                    float mask = SAMPLE_TEXTURE2D_X(_MaskTex, sampler_PointClamp, i.texcoord).r;
                    coc *= saturate(1.0 - mask);
                }

                return lerp(sharp, blurred, coc);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
