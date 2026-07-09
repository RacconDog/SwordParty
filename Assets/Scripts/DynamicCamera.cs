using UnityEngine;
using System.Collections.Generic;

// Keeps every registered target framed at all times. Never rotates — it
// moves laterally to chase the group's center and dollies along its own
// forward axis to zoom. Motion is a slightly underdamped spring, so it
// overshoots a touch and settles: playful, never rigid.
//
// Targets live in two weighted arrays: players matter fully, swords less so.
[RequireComponent(typeof(Camera))]
public class DynamicCamera : MonoBehaviour
{
    private static DynamicCamera instance;

    [Header("Targets")]
    [SerializeField] private List<Transform> targetPlayers = new List<Transform>();
    [SerializeField] private List<Transform> targetSwords = new List<Transform>();
    [Tooltip("How strongly a player pulls focus and zoom.")]
    [SerializeField] private float playerWeight = 1f;
    [Tooltip("How strongly a sword pulls focus and zoom. Lower = less visually important.")]
    [SerializeField] private float swordWeight = 0.4f;

    [Header("Framing")]
    [Tooltip("Extra world-units of breathing room around the group.")]
    [SerializeField] private float framePadding = 3f;
    [SerializeField] private float minDistance = 8f;
    [SerializeField] private float maxDistance = 40f;

    [Header("Dynamics (spring)")]
    [Tooltip("Spring stiffness: higher = snappier chase.")]
    [SerializeField] private float stiffness = 20f;
    [Tooltip("Damping: below ~2*sqrt(stiffness) overshoots. Lower = bouncier.")]
    [SerializeField] private float damping = 6f;

    private Camera cam;
    private Vector3 velocity;

    void Awake()
    {
        instance = this;
        cam = GetComponent<Camera>();
    }

    public static void RegisterPlayer(Transform player)
    {
        if (instance && !instance.targetPlayers.Contains(player))
            instance.targetPlayers.Add(player);
    }

    public static void RegisterSword(Transform sword)
    {
        if (instance && !instance.targetSwords.Contains(sword))
            instance.targetSwords.Add(sword);
    }

    void LateUpdate()
    {
        // Drop destroyed objects.
        targetPlayers.RemoveAll(t => !t);
        targetSwords.RemoveAll(t => !t);

        if (targetPlayers.Count + targetSwords.Count == 0) return;

        Vector3 desired = ComputeDesiredPosition();

        // Underdamped spring: accelerate toward the desired spot, bleed off
        // velocity slower than critical damping would -> slight overshoot.
        velocity += (desired - transform.position) * (stiffness * Time.deltaTime);
        velocity *= Mathf.Exp(-damping * Time.deltaTime);
        transform.position += velocity * Time.deltaTime;
    }

    private Vector3 ComputeDesiredPosition()
    {
        // Weighted group center: heavier targets pull focus harder.
        Vector3 center = Vector3.zero;
        float totalWeight = 0f;

        foreach (Transform t in targetPlayers)
        {
            center += t.position * playerWeight;
            totalWeight += playerWeight;
        }
        foreach (Transform t in targetSwords)
        {
            center += t.position * swordWeight;
            totalWeight += swordWeight;
        }

        if (totalWeight <= 0f) return transform.position;
        center /= totalWeight;

        // Framing radius: each target's distance from center counts scaled by
        // its weight, so a sword widens the shot less than a player would at
        // the same distance.
        float radius = 0f;
        foreach (Transform t in targetPlayers)
            radius = Mathf.Max(radius, Vector3.Distance(center, t.position) * playerWeight);
        foreach (Transform t in targetSwords)
            radius = Mathf.Max(radius, Vector3.Distance(center, t.position) * swordWeight);
        radius += framePadding;

        // Distance needed to fit that radius in both the vertical FOV and
        // the (aspect-derived) horizontal FOV, whichever is tighter.
        float vFov = cam.fieldOfView * Mathf.Deg2Rad * 0.5f;
        float hFov = Mathf.Atan(Mathf.Tan(vFov) * cam.aspect);
        float distance = radius / Mathf.Tan(Mathf.Min(vFov, hFov));
        distance = Mathf.Clamp(distance, minDistance, maxDistance);

        // Pull back from the center along our fixed viewing direction.
        return center - transform.forward * distance;
    }
}
