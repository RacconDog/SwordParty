using UnityEngine;
using System.Collections.Generic;

// The "replace which slot?" picker hovering over a player's head.
// Three circles show the player's current upgrades; a fourth (the incoming
// upgrade) floats above and slides over whichever slot is hovered. On
// confirm, the incoming circle drops down into the chosen slot while the old
// circle shrinks away. The moving circle rides a slightly underdamped spring
// with Perlin wobble, so every motion overshoots and jitters a little.
// Circles are runtime-generated sprites (no art assets), billboarded to the
// camera. Pure presentation: it runs on EVERY machine, fed colors by the
// owning Player's ClientRpcs (the server decides, the clients mirror).
public class UpgradeChoiceUI : MonoBehaviour
{
    [Header("Layout (world units, relative to player)")]
    [Tooltip("Height of the slot row above the player origin.")]
    [SerializeField] private float yOffset = 2.2f;
    [Tooltip("Horizontal spacing between the slot circles.")]
    [SerializeField] private float xSpacing = 0.7f;
    [Tooltip("How far the incoming upgrade floats above the slot row.")]
    [SerializeField] private float newUpgradeGap = 0.9f;
    [SerializeField] private float circleScale = 0.5f;

    [Header("Motion Dynamics (hover slide + replace drop)")]
    [Tooltip("Spring stiffness of the moving circle; higher = snappier.")]
    [SerializeField] private float circleStiffness = 120f;
    [Tooltip("Damping; below ~2*sqrt(stiffness) overshoots. Lower = bouncier.")]
    [SerializeField] private float circleDamping = 11f;
    [Tooltip("Perlin wobble amplitude on the moving circle. 0 = off.")]
    [SerializeField] private float noiseAmplitude = 0.03f;
    [Tooltip("Perlin wobble speed.")]
    [SerializeField] private float noiseFrequency = 9f;

    [Header("Replace Animation")]
    [Tooltip("How fast the replaced circle shrinks away.")]
    [SerializeField] private float shrinkSpeed = 8f;
    [Tooltip("Failsafe: replace animation never lasts longer than this.")]
    [SerializeField] private float replaceMaxDuration = 1.2f;
    [Tooltip("Spawned on confirm, tinted to the upgrade being replaced.")]
    [SerializeField] private ParticleSystem replaceEffectPrefab;

    public bool IsReplacing { get; private set; }

    private readonly List<SpriteRenderer> slotCircles = new List<SpriteRenderer>();
    private SpriteRenderer newCircle;
    private int hoverIndex;

    // Spring state of the moving circle (local space, noise excluded).
    private Vector3 springPos;
    private Vector3 springVel;

    private SpriteRenderer replacedCircle;
    private float replaceTimer;

    private static Sprite circleSprite;

    public void Show(IReadOnlyList<Color> currentColors, Color incomingColor)
    {
        transform.localPosition = Vector3.up * yOffset;

        // (Re)build one circle per held upgrade.
        foreach (SpriteRenderer old in slotCircles)
            if (old) Destroy(old.gameObject);
        slotCircles.Clear();

        for (int i = 0; i < currentColors.Count; i++)
        {
            SpriteRenderer circle = MakeCircle("Slot" + i, currentColors[i]);
            circle.transform.localPosition =
                new Vector3(SlotX(i, currentColors.Count), 0f, 0f);
            slotCircles.Add(circle);
        }

        if (!newCircle)
            newCircle = MakeCircle("Incoming", incomingColor);
        newCircle.color = incomingColor;
        newCircle.transform.localScale = Vector3.one * circleScale;

        // Start the spring settled at the current hover spot.
        IsReplacing = false;
        springPos = HoverTarget();
        springVel = Vector3.zero;
        newCircle.transform.localPosition = springPos;

        gameObject.SetActive(true);
    }

    public void SetHover(int index)
    {
        hoverIndex = index;
    }

    // Drop the incoming circle into the hovered slot; the old circle shrinks
    // out. Poll IsReplacing to know when it's done.
    public void PlayReplace(int index)
    {
        hoverIndex = index;
        IsReplacing = true;
        replaceTimer = 0f;
        replacedCircle = index < slotCircles.Count ? slotCircles[index] : null;

        SpawnReplaceEffect();
    }

    private void SpawnReplaceEffect()
    {
        if (!replaceEffectPrefab || !replacedCircle) return;

        ParticleSystem fx = Instantiate(replaceEffectPrefab,
            replacedCircle.transform.position,
            replaceEffectPrefab.transform.rotation);

        foreach (Transform t in fx.GetComponentsInChildren<Transform>(true))
            t.gameObject.layer = NoBlurLayer();

        // Tint every system in the prefab to the outgoing upgrade's color.
        Color color = replacedCircle.color;
        foreach (ParticleSystem ps in fx.GetComponentsInChildren<ParticleSystem>())
        {
            var main = ps.main;
            main.startColor = color;
        }

        fx.Play();
        // No scripted cleanup: the effect prefab destroys itself when done.
    }

    public void Hide()
    {
        gameObject.SetActive(false);
    }

    void LateUpdate()
    {
        // Never rotate with the player; always face the way the camera does.
        if (Camera.main)
            transform.rotation = Camera.main.transform.rotation;

        // Deliberately re-apply layout every frame (yes, redundant) so the
        // serialized values can be fiddled live in the inspector.
        ApplyLayout();

        if (!newCircle) return;

        // Underdamped spring toward the current target: overshoots, settles.
        Vector3 target = IsReplacing
            ? new Vector3(SlotX(hoverIndex, slotCircles.Count), 0f, 0f)
            : HoverTarget();

        springVel += (target - springPos) * (circleStiffness * Time.deltaTime);
        springVel *= Mathf.Exp(-circleDamping * Time.deltaTime);
        springPos += springVel * Time.deltaTime;

        // Hand-wobble on top of the spring.
        float t = Time.time * noiseFrequency;
        Vector3 noise = new Vector3(
            Mathf.PerlinNoise(t, 0.37f) - 0.5f,
            Mathf.PerlinNoise(0.73f, t) - 0.5f,
            0f) * (2f * noiseAmplitude);

        newCircle.transform.localPosition = springPos + noise;

        if (IsReplacing)
            TickReplace(target);
    }

    private void TickReplace(Vector3 target)
    {
        replaceTimer += Time.deltaTime;

        // The old circle shrinks away underneath the incoming one.
        if (replacedCircle)
        {
            Vector3 scale = replacedCircle.transform.localScale;
            replacedCircle.transform.localScale =
                Vector3.MoveTowards(scale, Vector3.zero, shrinkSpeed * Time.deltaTime * circleScale);
        }

        bool settled = (springPos - target).magnitude < 0.03f &&
                       springVel.magnitude < 0.15f;

        if (settled || replaceTimer >= replaceMaxDuration)
        {
            if (replacedCircle) Destroy(replacedCircle.gameObject);
            IsReplacing = false;
        }
    }

    private void ApplyLayout()
    {
        transform.localPosition = Vector3.up * yOffset;

        for (int i = 0; i < slotCircles.Count; i++)
        {
            SpriteRenderer circle = slotCircles[i];
            if (!circle) continue;

            circle.transform.localPosition =
                new Vector3(SlotX(i, slotCircles.Count), 0f, 0f);

            // Don't fight the shrink-out animation on the outgoing circle.
            bool shrinking = IsReplacing && circle == replacedCircle;
            if (!shrinking)
                circle.transform.localScale = Vector3.one * circleScale;
        }

        if (newCircle)
            newCircle.transform.localScale = Vector3.one * circleScale;
    }

    private Vector3 HoverTarget()
    {
        return new Vector3(SlotX(hoverIndex, Mathf.Max(slotCircles.Count, 1)),
                           newUpgradeGap, 0f);
    }

    private float SlotX(int index, int count)
    {
        // Center the row: for 3 slots -> -spacing, 0, +spacing.
        return (index - (count - 1) * 0.5f) * xSpacing;
    }

    private SpriteRenderer MakeCircle(string name, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.transform.localScale = Vector3.one * circleScale;
        go.layer = NoBlurLayer();

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = GetCircleSprite();
        sr.color = color;
        return sr;
    }

    private static int noBlurLayer = -2; // -2 = not looked up yet

    // Everything the picker instances goes on "NoBlur" so post-processing
    // can exclude it.
    private static int NoBlurLayer()
    {
        if (noBlurLayer == -2)
        {
            noBlurLayer = LayerMask.NameToLayer("NoBlur");
            if (noBlurLayer < 0)
                Debug.LogWarning("Layer 'NoBlur' doesn't exist; picker UI stays on Default.");
        }
        return Mathf.Max(noBlurLayer, 0);
    }

    // A plain filled circle, drawn once and shared by every picker.
    private static Sprite GetCircleSprite()
    {
        if (circleSprite) return circleSprite;

        const int size = 64;
        float radius = size * 0.5f - 1f;
        float center = (size - 1) * 0.5f;

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center;
                float dy = y - center;
                bool inside = dx * dx + dy * dy <= radius * radius;
                pixels[y * size + x] = inside
                    ? new Color32(255, 255, 255, 255)
                    : new Color32(0, 0, 0, 0);
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply();

        circleSprite = Sprite.Create(
            tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        return circleSprite;
    }
}
