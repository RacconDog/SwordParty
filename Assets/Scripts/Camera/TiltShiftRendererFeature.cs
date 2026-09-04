using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

// Screen-space, depth-driven tilt-shift ("miniature") post effect for URP 17 /
// RenderGraph. Keeps a world-space depth slab in focus and blurs whatever is
// nearer or farther, so the scene reads like a tiny photographed model.
//
// Pipeline per frame:
//   Copy       activeColor -> sharp              (kept for the final mix)
//   Downsample sharp       -> blurA              (reduced res for speed)
//   BlurH/V    separable gaussian -> blurA
//   Mask       (optional) excludeLayers -> mask  (objects to keep sharp)
//   Comp       blur + sharp + depth (+ mask) -> activeColor
//
// Objects on "Exclude Layers" are stamped into a coverage mask and forced to
// stay sharp, so UI / markers stay crisp on top of the blurred scene.
//
// Add it to PC_Renderer's Renderer Features list and tune in the inspector.
public class TiltShiftRendererFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class TiltShiftSettings
    {
        [Tooltip("Nearest distance (world units from the camera) kept sharp. Anything closer blurs. " +
                 "This camera dollies between ~8 and ~40 units out.")]
        public float focusMin = 14f;

        [Tooltip("Farthest distance (world units from the camera) kept sharp. Anything farther blurs.")]
        public float focusMax = 26f;

        [Tooltip("How far past the sharp band (world units) it takes to reach maximum blur.")]
        [Min(0.01f)] public float focusTransition = 14f;

        [Tooltip("Shapes the sharp->blur ramp. 1 = linear, >1 holds focus longer then falls off fast.")]
        [Range(0.25f, 4f)] public float falloff = 1.6f;

        [Tooltip("Strongest blur mix reached far from focus. 1 = fully blurred.")]
        [Range(0f, 1f)] public float maxBlur = 1f;

        [Tooltip("Gaussian tap spacing in source texels. Bigger = wider blur, but past ~6 a single " +
                 "pass starts to ghost - raise Blur Iterations instead for a bigger smooth blur.")]
        [Range(0.5f, 12f)] public float blurSize = 2.5f;

        [Tooltip("How many times to repeat the separable blur. Each pass roughly doubles the reach " +
                 "while staying smooth - the way to get a strong miniature blur without ghosting.")]
        [Range(1, 6)] public int blurIterations = 1;

        [Tooltip("Blur buffer downscale. 2 = half res (recommended), higher = cheaper, softer & wider.")]
        [Range(1, 4)] public int downsample = 2;

        [Tooltip("Objects on these layers are never blurred - their pixels always render sharp. " +
                 "Put UI / markers you want kept crisp on a layer and select it here.")]
        public LayerMask excludeLayers = 0;

        [Tooltip("Where in the frame the effect runs. Before post-processing is the usual slot.")]
        public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
    }

    [SerializeField] private TiltShiftSettings settings = new TiltShiftSettings();
    [Tooltip("Only apply the effect to the Game view (skip the editor Scene view).")]
    [SerializeField] private bool gameViewOnly = true;
    [SerializeField] private Shader shader;
    [SerializeField] private Shader maskShader;

    private Material material;
    private Material maskMaterial;
    private TiltShiftPass pass;

    public override void Create()
    {
        if (shader == null)
            shader = Shader.Find("Hidden/TiltShift");
        if (maskShader == null)
            maskShader = Shader.Find("Hidden/TiltShiftMask");
        if (shader == null)
            return;

        material = CoreUtils.CreateEngineMaterial(shader);
        maskMaterial = maskShader != null ? CoreUtils.CreateEngineMaterial(maskShader) : null;
        pass = new TiltShiftPass(material, maskMaterial, settings)
        {
            renderPassEvent = settings.renderPassEvent
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (pass == null || material == null)
            return;

        // Never on material previews or reflection probes, and (by default)
        // skip the editor Scene view so the effect only shows in the real game.
        var cameraType = renderingData.cameraData.cameraType;
        if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection)
            return;
        if (gameViewOnly && cameraType == CameraType.SceneView)
            return;

        pass.ConfigureInput(ScriptableRenderPassInput.Depth);
        renderer.EnqueuePass(pass);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(material);
        CoreUtils.Destroy(maskMaterial);
    }

    // --- The pass ---------------------------------------------------------

    private class TiltShiftPass : ScriptableRenderPass
    {
        // Shader pass indices (order must match TiltShift.shader).
        private const int PassCopy = 0;      // also used as the downsample blit
        private const int PassBlurH = 1;
        private const int PassBlurV = 2;
        private const int PassComposite = 3;

        private static readonly int SharpTexId = Shader.PropertyToID("_SharpTex");
        private static readonly int MaskTexId = Shader.PropertyToID("_MaskTex");
        private static readonly int UseMaskId = Shader.PropertyToID("_UseMask");
        private static readonly int FocusMinId = Shader.PropertyToID("_FocusMin");
        private static readonly int FocusMaxId = Shader.PropertyToID("_FocusMax");
        private static readonly int FocusTransitionId = Shader.PropertyToID("_FocusTransition");
        private static readonly int FalloffId = Shader.PropertyToID("_Falloff");
        private static readonly int MaxBlurId = Shader.PropertyToID("_MaxBlur");
        private static readonly int BlurSizeId = Shader.PropertyToID("_BlurSize");

        // Passes the excluded-layer objects are likely to have; the override
        // material replaces the shading, these just decide which renderers draw.
        private static readonly System.Collections.Generic.List<ShaderTagId> MaskTags =
            new System.Collections.Generic.List<ShaderTagId>
            {
                new ShaderTagId("SRPDefaultUnlit"),
                new ShaderTagId("UniversalForward"),
                new ShaderTagId("UniversalForwardOnly"),
            };

        private readonly Material material;
        private readonly Material maskMaterial;
        private readonly TiltShiftSettings settings;

        public TiltShiftPass(Material material, Material maskMaterial, TiltShiftSettings settings)
        {
            this.material = material;
            this.maskMaterial = maskMaterial;
            this.settings = settings;
        }

        private class PassData
        {
            public Material material;
            public int shaderPass;
            public TextureHandle source;
        }

        private class MaskPassData
        {
            public RendererListHandle rendererList;
        }

        // A single fullscreen blit: sample `source` as _BlitTexture, run `shaderPass`, write `dest`.
        private static void AddBlit(RenderGraph renderGraph, string name, Material material, int shaderPass,
                                    TextureHandle source, TextureHandle dest,
                                    System.Action<IRasterRenderGraphBuilder> configure = null)
        {
            using var builder = renderGraph.AddRasterRenderPass<PassData>(name, out var data);
            data.material = material;
            data.shaderPass = shaderPass;
            data.source = source;

            builder.UseTexture(source);
            builder.SetRenderAttachment(dest, 0);
            configure?.Invoke(builder);

            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
            {
                Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1f, 1f, 0f, 0f), d.material, d.shaderPass);
            });
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();

            // Can't round-trip through a texture if we're rendering straight to
            // the backbuffer (e.g. no intermediate texture requested).
            if (resourceData.isActiveTargetBackBuffer)
                return;

            TextureHandle activeColor = resourceData.activeColorTexture;
            if (!activeColor.IsValid())
                return;

            // Push the current (per-frame) tuning onto the shared material.
            // Guard against a crossed-over band (min past max).
            material.SetFloat(FocusMinId, Mathf.Min(settings.focusMin, settings.focusMax));
            material.SetFloat(FocusMaxId, Mathf.Max(settings.focusMin, settings.focusMax));
            material.SetFloat(FocusTransitionId, settings.focusTransition);
            material.SetFloat(FalloffId, settings.falloff);
            material.SetFloat(MaxBlurId, settings.maxBlur);
            material.SetFloat(BlurSizeId, settings.blurSize);

            var srcDesc = renderGraph.GetTextureDesc(activeColor);

            // Full-res stash of the sharp frame.
            var sharpDesc = srcDesc;
            sharpDesc.name = "TiltShift_Sharp";
            sharpDesc.clearBuffer = false;
            sharpDesc.depthBufferBits = 0;
            TextureHandle sharp = renderGraph.CreateTexture(sharpDesc);

            // Downsampled ping-pong buffers for the separable blur.
            int div = Mathf.Max(1, settings.downsample);
            var blurDesc = sharpDesc;
            blurDesc.width = Mathf.Max(1, srcDesc.width / div);
            blurDesc.height = Mathf.Max(1, srcDesc.height / div);
            blurDesc.name = "TiltShift_BlurA";
            TextureHandle blurA = renderGraph.CreateTexture(blurDesc);
            blurDesc.name = "TiltShift_BlurB";
            TextureHandle blurB = renderGraph.CreateTexture(blurDesc);

            // Copy: keep a full-res sharp copy AND expose it globally for the composite.
            AddBlit(renderGraph, "TiltShift Copy", material, PassCopy, activeColor, sharp,
                builder => builder.SetGlobalTextureAfterPass(sharp, SharpTexId));

            // Downsample once, then a separable gaussian entirely at reduced
            // resolution so horizontal and vertical blur radii stay symmetric.
            // Each iteration widens the blur while keeping it smooth; the result
            // always lands back in blurA (H writes blurB, V writes blurA).
            AddBlit(renderGraph, "TiltShift Downsample", material, PassCopy, sharp, blurA);
            int iterations = Mathf.Max(1, settings.blurIterations);
            for (int i = 0; i < iterations; i++)
            {
                AddBlit(renderGraph, "TiltShift BlurH", material, PassBlurH, blurA, blurB);
                AddBlit(renderGraph, "TiltShift BlurV", material, PassBlurV, blurB, blurA);
            }

            // Optional: stamp the "keep sharp" layers into a coverage mask that
            // the composite honors, so those objects never get blurred.
            bool useMask = maskMaterial != null && settings.excludeLayers.value != 0;
            material.SetFloat(UseMaskId, useMask ? 1f : 0f);
            TextureHandle mask = TextureHandle.nullHandle;
            if (useMask)
            {
                var maskDesc = srcDesc;
                maskDesc.name = "TiltShift_Mask";
                maskDesc.format = GraphicsFormat.R8_UNorm;
                maskDesc.depthBufferBits = 0;
                maskDesc.msaaSamples = MSAASamples.None;
                maskDesc.bindTextureMS = false;
                maskDesc.clearBuffer = true;
                maskDesc.clearColor = Color.clear;
                mask = renderGraph.CreateTexture(maskDesc);
                AddMaskPass(renderGraph, frameData, mask);
            }

            // Composite blurred + sharp back into the camera color, keyed by depth.
            AddBlit(renderGraph, "TiltShift Composite", material, PassComposite, blurA, activeColor,
                builder =>
                {
                    builder.UseTexture(sharp);
                    if (useMask)
                        builder.UseTexture(mask);
                });
        }

        // Renders the excluded layers into `mask` (white = keep sharp) and
        // exposes it as the global _MaskTex for the composite.
        private void AddMaskPass(RenderGraph renderGraph, ContextContainer frameData, TextureHandle mask)
        {
            var renderingData = frameData.Get<UniversalRenderingData>();
            var cameraData = frameData.Get<UniversalCameraData>();
            var lightData = frameData.Get<UniversalLightData>();

            var drawSettings = RenderingUtils.CreateDrawingSettings(
                MaskTags, renderingData, cameraData, lightData, SortingCriteria.CommonOpaque);
            drawSettings.overrideMaterial = maskMaterial;
            drawSettings.overrideMaterialPassIndex = 0;

            var filterSettings = new FilteringSettings(RenderQueueRange.all, settings.excludeLayers);
            var listParams = new RendererListParams(renderingData.cullResults, drawSettings, filterSettings);
            RendererListHandle list = renderGraph.CreateRendererList(listParams);

            using var builder = renderGraph.AddRasterRenderPass<MaskPassData>("TiltShift Mask", out var data);
            data.rendererList = list;
            builder.UseRendererList(list);
            builder.SetRenderAttachment(mask, 0);
            builder.SetGlobalTextureAfterPass(mask, MaskTexId);
            builder.SetRenderFunc((MaskPassData d, RasterGraphContext ctx) =>
            {
                ctx.cmd.DrawRendererList(d.rendererList);
            });
        }
    }
}
