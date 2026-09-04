using UnityEngine;

// Lives on the water plane. Bakes a "distance to shoreline" texture for the
// water shader: a grid of raycasts finds where static level geometry pokes
// above the surface, then a distance transform turns that into flat on-plane
// distance from every water pixel to the nearest waterline. The foam band is
// drawn from this field, so it hugs the level's real outline instead of
// following terrain depth.
//
// Bakes once at Start. Call Rebuild() (or the context menu) if the level
// changes at runtime.
[RequireComponent(typeof(Renderer))]
public class WaterRipples : MonoBehaviour
{
    [Tooltip("Grid resolution of the baked distance texture.")]
    [SerializeField] private int resolution = 128;
    [Tooltip("Raycasts start this far above the surface...")]
    [SerializeField] private float castHeight = 20f;
    [Tooltip("...and probe this far below it.")]
    [SerializeField] private float castDepth = 50f;
    [Tooltip("Layers that count as level geometry. Exclude the water itself.")]
    [SerializeField] private LayerMask levelMask = ~0;

    private Texture2D distTex;

    private static readonly int TexId = Shader.PropertyToID("_ShoreDistTex");
    private static readonly int RegionId = Shader.PropertyToID("_ShoreDistRegion");

    void Start()
    {
        Rebuild();
    }

    [ContextMenu("Rebuild Shore Distance")]
    public void Rebuild()
    {
        Bounds b = GetComponent<Renderer>().bounds;
        float waterY = transform.position.y;
        Vector2 min = new Vector2(b.min.x, b.min.z);
        Vector2 size = new Vector2(b.size.x, b.size.z);

        int n = Mathf.Max(resolution, 8);
        float cellX = size.x / n;
        float cellZ = size.y / n;
        float cellDiag = Mathf.Sqrt(cellX * cellX + cellZ * cellZ);

        // Our own collider (if any) must not register as shoreline.
        Collider selfCollider = GetComponent<Collider>();
        bool selfWasEnabled = selfCollider && selfCollider.enabled;
        if (selfCollider) selfCollider.enabled = false;

        // 1) Solid mask: does level geometry break the surface in this cell?
        var dist = new float[n * n];
        const float far = 1e9f;
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                var origin = new Vector3(
                    min.x + (x + 0.5f) * cellX,
                    waterY + castHeight,
                    min.y + (y + 0.5f) * cellZ);

                bool solid = Physics.Raycast(origin, Vector3.down,
                                 out RaycastHit hit, castHeight + castDepth,
                                 levelMask, QueryTriggerInteraction.Ignore)
                             && hit.point.y >= waterY;

                dist[y * n + x] = solid ? 0f : far;
            }
        }

        if (selfCollider) selfCollider.enabled = selfWasEnabled;

        // 2) Two-pass chamfer distance transform, in world units.
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int i = y * n + x;
                if (x > 0) dist[i] = Mathf.Min(dist[i], dist[i - 1] + cellX);
                if (y > 0) dist[i] = Mathf.Min(dist[i], dist[i - n] + cellZ);
                if (x > 0 && y > 0) dist[i] = Mathf.Min(dist[i], dist[i - n - 1] + cellDiag);
                if (x < n - 1 && y > 0) dist[i] = Mathf.Min(dist[i], dist[i - n + 1] + cellDiag);
            }
        }
        for (int y = n - 1; y >= 0; y--)
        {
            for (int x = n - 1; x >= 0; x--)
            {
                int i = y * n + x;
                if (x < n - 1) dist[i] = Mathf.Min(dist[i], dist[i + 1] + cellX);
                if (y < n - 1) dist[i] = Mathf.Min(dist[i], dist[i + n] + cellZ);
                if (x < n - 1 && y < n - 1) dist[i] = Mathf.Min(dist[i], dist[i + n + 1] + cellDiag);
                if (x > 0 && y < n - 1) dist[i] = Mathf.Min(dist[i], dist[i + n - 1] + cellDiag);
            }
        }

        // 3) Ship it to the shader.
        if (!distTex || distTex.width != n)
        {
            distTex = new Texture2D(n, n, TextureFormat.RFloat, false, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
        }
        distTex.SetPixelData(dist, 0);
        distTex.Apply(false);

        Shader.SetGlobalTexture(TexId, distTex);
        Shader.SetGlobalVector(RegionId,
            new Vector4(min.x, min.y, 1f / size.x, 1f / size.y));
    }
}
