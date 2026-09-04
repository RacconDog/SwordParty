Shader "Hidden/TiltShiftMask"
{
    // Coverage stamp used by TiltShiftRendererFeature. It's applied as an
    // override material to whatever renderers sit on the "exclude" layers, so
    // every pixel those objects cover is written as white into a mask texture.
    // The composite pass then forces those pixels to stay sharp (no blur).
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Mask"
            // Draw the silhouette regardless of the depth buffer: UI/markers on
            // the excluded layers should read as sharp even when they'd normally
            // be occlusion-tested against the scene.
            ZWrite Off ZTest Always Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half frag(Varyings input) : SV_Target
            {
                return 1.0;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
