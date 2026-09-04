using System.Collections.Generic;
using UnityEngine;

// Lives on the tile map's parent object. Bakes two textures for the TileCube
// shader, one texel per grid cell:
//   _TileHeightTex  R     = world-space top height of the tile column there
//                           (huge negative value where there is no tile).
//   _TileInsetTex   RGBA  = how far the tile's flat top is inset from the
//                           cell's west/east/south/north grid edges, so the
//                           outline follows beveled tops instead of assuming
//                           every tile is a perfect square.
// The shader compares neighboring columns' heights to decide which top edges
// are exposed to air, so same-height tiles merge into one shape with a
// single outline and no interior creases.
//
// Only renderers using the SwordParty/TileCube shader count as tiles, so
// decorations parented under the map can't pollute the bake. Heights and
// insets come from the meshes themselves — no physics, no colliders.
//
// This component is the single source of truth for the grid: Tile Size and
// Grid Offset are pushed to the shader as globals alongside the textures, so
// there is nothing to keep in sync on the material.
//
// Runs in the editor too, and rebakes automatically when tiles are moved,
// added, or removed under it. Select it to see the baked cells as gizmos.
[ExecuteAlways]
public class TileHeightBaker : MonoBehaviour
{
    [Tooltip("World size of one tile.")]
    [SerializeField] private float tileSize = 1f;
    [Tooltip("Vertices within this distance of a mesh's highest point count " +
             "as its flat top when measuring bevel insets.")]
    [SerializeField] private float topSurfaceTolerance = 0.02f;

    // The grid lines are anchored to this object's own X/Z position (plus a
    // natural +1,+1): move the baker to align the grid with your tiles.
    private Vector2 GridOffset => new Vector2(transform.position.x + 1f, transform.position.z + 1f);

    private const float Empty = -1e6f;

    // Non-readable meshes we've already complained about this session.
    private static readonly HashSet<Mesh> warnedMeshes = new HashSet<Mesh>();

    private Texture2D heightTex;
    private Texture2D insetTex;
    private Texture2D cornerTex;

    // Kept around for the gizmo drawing.
    private float[] heights;
    private Vector4[] insets;
    private Vector4[] corners;
    private int bakedMinX, bakedMinZ, bakedNX, bakedNZ;

    private static readonly int TexId = Shader.PropertyToID("_TileHeightTex");
    private static readonly int InsetTexId = Shader.PropertyToID("_TileInsetTex");
    private static readonly int CornerTexId = Shader.PropertyToID("_TileCornerTex");
    private static readonly int RegionId = Shader.PropertyToID("_TileHeightRegion");
    private static readonly int BakedId = Shader.PropertyToID("_TileHeightBaked");
    private static readonly int GridSizeId = Shader.PropertyToID("_TileGridSize");
    private static readonly int GridOffsetId = Shader.PropertyToID("_TileGridOffset");

    void OnEnable()
    {
        Rebuild();
    }

    void OnDisable()
    {
        // Drop the shader back to its unbaked look (no outlines) rather than
        // leaving it reading a stale texture.
        Shader.SetGlobalFloat(BakedId, 0f);
    }

#if UNITY_EDITOR
    // Rebake when tiles under us change while editing, so the outlines in
    // the scene view always match the current layout.
    private int layoutHash;

    void Update()
    {
        if (Application.isPlaying) return;
        int hash = ComputeLayoutHash();
        if (hash != layoutHash)
        {
            layoutHash = hash;
            Rebuild();
        }
    }

    int ComputeLayoutHash()
    {
        int hash = 17;
        foreach (Transform t in GetComponentsInChildren<Transform>())
        {
            hash = hash * 31 + t.position.GetHashCode();
            hash = hash * 31 + t.rotation.GetHashCode();
            hash = hash * 31 + t.lossyScale.GetHashCode();
        }
        return hash;
    }

    void OnValidate()
    {
        // Inspector fields changed; rebake outside of validation.
        UnityEditor.EditorApplication.delayCall += () =>
        {
            if (this) Rebuild();
        };
    }

    // Green squares cap every baked column, pulled in by the baked insets;
    // if they don't match your tile tops, the grid settings or parenting are
    // wrong.
    void OnDrawGizmosSelected()
    {
        if (heights == null || insets == null || insets.Length != heights.Length) return;
        Vector2 gridOffset = GridOffset;
        Gizmos.color = Color.green;
        for (int z = 0; z < bakedNZ; z++)
        {
            for (int x = 0; x < bakedNX; x++)
            {
                int i = z * bakedNX + x;
                if (heights[i] <= Empty) continue;
                Vector4 inset = insets[i];
                float minX = gridOffset.x + (bakedMinX + x) * tileSize + inset.x;
                float maxX = gridOffset.x + (bakedMinX + x + 1) * tileSize - inset.y;
                float minZ = gridOffset.y + (bakedMinZ + z) * tileSize + inset.z;
                float maxZ = gridOffset.y + (bakedMinZ + z + 1) * tileSize - inset.w;
                var center = new Vector3((minX + maxX) * 0.5f, heights[i], (minZ + maxZ) * 0.5f);
                Gizmos.DrawWireCube(center, new Vector3(maxX - minX, 0f, maxZ - minZ));
            }
        }
    }
#endif

    [ContextMenu("Rebuild Tile Heights")]
    public void Rebuild()
    {
        // Only meshes drawn with the tile shader are tiles; bushes, props,
        // and pickups parented under the map are ignored.
        Shader tileShader = Shader.Find("SwordParty/TileCube");
        var tiles = new List<MeshRenderer>();
        foreach (var r in GetComponentsInChildren<MeshRenderer>())
        {
            if (!r.TryGetComponent(out MeshFilter mf) || !mf.sharedMesh) continue;
            foreach (var mat in r.sharedMaterials)
            {
                if (mat && mat.shader == tileShader)
                {
                    tiles.Add(r);
                    break;
                }
            }
        }

        if (tiles.Count == 0 || tileSize <= 0f)
        {
            heights = null;
            Shader.SetGlobalFloat(BakedId, 0f);
            return;
        }

        Vector2 gridOffset = GridOffset;
        Bounds b = tiles[0].bounds;
        foreach (var r in tiles) b.Encapsulate(r.bounds);

        // Snap the region to the grid and pad one empty cell on every side,
        // so tiles at the map's rim still see an "air" neighbor and get their
        // outline (the texture clamps at its border).
        bakedMinX = Mathf.FloorToInt((b.min.x - gridOffset.x) / tileSize) - 1;
        bakedMinZ = Mathf.FloorToInt((b.min.z - gridOffset.y) / tileSize) - 1;
        bakedNX = Mathf.CeilToInt((b.max.x - gridOffset.x) / tileSize) + 1 - bakedMinX;
        bakedNZ = Mathf.CeilToInt((b.max.z - gridOffset.y) / tileSize) + 1 - bakedMinZ;

        if (heights == null || heights.Length != bakedNX * bakedNZ ||
            insets == null || insets.Length != heights.Length ||
            corners == null || corners.Length != heights.Length)
        {
            heights = new float[bakedNX * bakedNZ];
            insets = new Vector4[bakedNX * bakedNZ];
            corners = new Vector4[bakedNX * bakedNZ];
        }
        for (int i = 0; i < heights.Length; i++)
        {
            heights[i] = Empty;
            insets[i] = Vector4.zero;
            corners[i] = Vector4.zero;
        }

        // Rasterize each tile into the cells it covers, keeping the highest
        // top per cell. Bounds are shrunk a hair so a tile flush against a
        // grid line doesn't bleed into its neighbor's cell.
        float pad = tileSize * 0.01f;
        foreach (var r in tiles)
        {
            Bounds rb = r.bounds;
            MeasureTopFace(r, rb, out float topY,
                out float topMinX, out float topMaxX,
                out float topMinZ, out float topMaxZ,
                out Vector4 cornerRadii);

            int x0 = Mathf.Max(Mathf.FloorToInt((rb.min.x + pad - gridOffset.x) / tileSize) - bakedMinX, 0);
            int x1 = Mathf.Min(Mathf.FloorToInt((rb.max.x - pad - gridOffset.x) / tileSize) - bakedMinX, bakedNX - 1);
            int z0 = Mathf.Max(Mathf.FloorToInt((rb.min.z + pad - gridOffset.y) / tileSize) - bakedMinZ, 0);
            int z1 = Mathf.Min(Mathf.FloorToInt((rb.max.z - pad - gridOffset.y) / tileSize) - bakedMinZ, bakedNZ - 1);

            // A multi-cell tile's rounded corners only belong to the cells
            // that actually contain the top face's corners.
            int cwX = Mathf.FloorToInt((topMinX + pad - gridOffset.x) / tileSize) - bakedMinX;
            int ceX = Mathf.FloorToInt((topMaxX - pad - gridOffset.x) / tileSize) - bakedMinX;
            int csZ = Mathf.FloorToInt((topMinZ + pad - gridOffset.y) / tileSize) - bakedMinZ;
            int cnZ = Mathf.FloorToInt((topMaxZ - pad - gridOffset.y) / tileSize) - bakedMinZ;

            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int i = z * bakedNX + x;
                    if (topY <= heights[i]) continue;
                    heights[i] = topY;

                    float cellMinX = gridOffset.x + (bakedMinX + x) * tileSize;
                    float cellMinZ = gridOffset.y + (bakedMinZ + z) * tileSize;
                    insets[i] = new Vector4(
                        Mathf.Max(topMinX - cellMinX, 0f),
                        Mathf.Max(cellMinX + tileSize - topMaxX, 0f),
                        Mathf.Max(topMinZ - cellMinZ, 0f),
                        Mathf.Max(cellMinZ + tileSize - topMaxZ, 0f));

                    corners[i] = new Vector4(
                        x == cwX && z == csZ ? cornerRadii.x : 0f,  // SW
                        x == ceX && z == csZ ? cornerRadii.y : 0f,  // SE
                        x == cwX && z == cnZ ? cornerRadii.z : 0f,  // NW
                        x == ceX && z == cnZ ? cornerRadii.w : 0f); // NE
                }
            }
        }

        // One texel per tile; point filtering so the shader reads exact
        // per-cell values when probing neighbors.
        if (!heightTex || !insetTex || !cornerTex ||
            heightTex.width != bakedNX || heightTex.height != bakedNZ)
        {
            heightTex = NewBakeTexture(TextureFormat.RFloat);
            insetTex = NewBakeTexture(TextureFormat.RGBAFloat);
            cornerTex = NewBakeTexture(TextureFormat.RGBAFloat);
        }
        heightTex.SetPixelData(heights, 0);
        heightTex.Apply(false);
        insetTex.SetPixelData(insets, 0);
        insetTex.Apply(false);
        cornerTex.SetPixelData(corners, 0);
        cornerTex.Apply(false);

        var regionMin = new Vector2(
            gridOffset.x + bakedMinX * tileSize,
            gridOffset.y + bakedMinZ * tileSize);

        Shader.SetGlobalTexture(TexId, heightTex);
        Shader.SetGlobalTexture(InsetTexId, insetTex);
        Shader.SetGlobalTexture(CornerTexId, cornerTex);
        Shader.SetGlobalVector(RegionId, new Vector4(
            regionMin.x, regionMin.y,
            1f / (bakedNX * tileSize), 1f / (bakedNZ * tileSize)));
        Shader.SetGlobalFloat(GridSizeId, tileSize);
        Shader.SetGlobalVector(GridOffsetId, new Vector4(gridOffset.x, gridOffset.y, 0f, 0f));
        Shader.SetGlobalFloat(BakedId, 1f);
    }

    Texture2D NewBakeTexture(TextureFormat format)
    {
        return new Texture2D(bakedNX, bakedNZ, format, false, true)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point,
            hideFlags = HideFlags.HideAndDontSave,
        };
    }

    // The XZ extent of the mesh's flat top (every vertex within
    // topSurfaceTolerance of its highest point), plus the rounding radius of
    // each top corner. A bevel leaves the top extent short of the renderer
    // bounds on that side, which becomes the baked inset there; a rounded
    // corner leaves no vertex near the extent corner, and the arc tangent to
    // both edges through the nearest vertex gives its radius.
    void MeasureTopFace(MeshRenderer r, Bounds rb, out float topY,
        out float topMinX, out float topMaxX, out float topMinZ, out float topMaxZ,
        out Vector4 cornerRadii)
    {
        topY = rb.max.y;
        topMinX = rb.min.x; topMaxX = rb.max.x;
        topMinZ = rb.min.z; topMaxZ = rb.max.z;
        cornerRadii = Vector4.zero;

        Mesh mesh = r.GetComponent<MeshFilter>().sharedMesh;
        if (!mesh.isReadable)
        {
            // Can't inspect the vertices, so this tile bakes as a plain
            // square top (no bevel insets, no rounded corners).
            if (warnedMeshes.Add(mesh))
                Debug.LogWarning(
                    $"{mesh.name}: enable Read/Write in the model's import " +
                    "settings so bevels and rounded corners bake into the " +
                    "tile outline.", r);
            return;
        }

        Matrix4x4 toWorld = r.transform.localToWorldMatrix;
        Vector3[] verts = mesh.vertices;

        float maxY = float.MinValue;
        for (int i = 0; i < verts.Length; i++)
            maxY = Mathf.Max(maxY, toWorld.MultiplyPoint3x4(verts[i]).y);

        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 w = toWorld.MultiplyPoint3x4(verts[i]);
            if (w.y < maxY - topSurfaceTolerance) continue;
            minX = Mathf.Min(minX, w.x); maxX = Mathf.Max(maxX, w.x);
            minZ = Mathf.Min(minZ, w.z); maxZ = Mathf.Max(maxZ, w.z);
        }

        // Corner radii: for each corner of the top extent, fit the smallest
        // arc that is tangent to both adjacent edges and passes through a
        // top vertex. A sharp corner has a vertex at distance 0, giving
        // radius 0; an arc's own vertices all yield its true radius.
        float sw = float.MaxValue, se = float.MaxValue;
        float nw = float.MaxValue, ne = float.MaxValue;
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 w = toWorld.MultiplyPoint3x4(verts[i]);
            if (w.y < maxY - topSurfaceTolerance) continue;
            float dW = Mathf.Max(w.x - minX, 0f);
            float dE = Mathf.Max(maxX - w.x, 0f);
            float dS = Mathf.Max(w.z - minZ, 0f);
            float dN = Mathf.Max(maxZ - w.z, 0f);
            sw = Mathf.Min(sw, dW + dS + Mathf.Sqrt(2f * dW * dS));
            se = Mathf.Min(se, dE + dS + Mathf.Sqrt(2f * dE * dS));
            nw = Mathf.Min(nw, dW + dN + Mathf.Sqrt(2f * dW * dN));
            ne = Mathf.Min(ne, dE + dN + Mathf.Sqrt(2f * dE * dN));
        }

        // Ignore sub-centimeter noise; cap at half a tile.
        float minR = tileSize * 0.01f, maxR = tileSize * 0.5f;
        cornerRadii = new Vector4(
            sw < minR ? 0f : Mathf.Min(sw, maxR),
            se < minR ? 0f : Mathf.Min(se, maxR),
            nw < minR ? 0f : Mathf.Min(nw, maxR),
            ne < minR ? 0f : Mathf.Min(ne, maxR));

        topY = maxY;
        topMinX = minX; topMaxX = maxX;
        topMinZ = minZ; topMaxZ = maxZ;
    }
}
