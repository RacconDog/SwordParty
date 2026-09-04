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

    [Header("Choice Focus (upgrade picker zoom-in)")]
    [Tooltip("Movement spring stiffness while diving to/holding on the picker.")]
    [SerializeField] private float focusStiffness = 30f;
    [Tooltip("Movement damping while focused. Lower = bouncier arrival.")]
    [SerializeField] private float focusDamping = 8f;
    [Tooltip("How close the camera dollies to the picker UI while it's open.")]
    [SerializeField] private float focusDistance = 4f;
    [Tooltip("Vertical offset of the camera relative to the picker.")]
    [SerializeField] private float focusHeight = 0f;
    [Tooltip("Rotation spring stiffness: higher = faster pivot to the picker.")]
    [SerializeField] private float focusAimStiffness = 40f;
    [Tooltip("Rotation damping: below ~2*sqrt(stiffness) overshoots the aim.")]
    [SerializeField] private float focusAimDamping = 12f;

    // While set, normal group framing is suspended and the camera dives
    // toward this transform (the picker UI). The same spring drives the
    // move, so the zoom-in overshoots and settles like everything else.
    private static Transform focusTarget;

    private Camera cam;
    private Vector3 velocity;
    private Vector3 angularVelocity; // radians/sec, spring state for aiming

    // The fixed gameplay rotation; picker focus is the one exception where
    // the camera is allowed to pivot away from it (to aim dead-on), and it
    // returns here afterwards.
    private Quaternion baseRotation;

    public static void FocusOn(Transform target)
    {
        focusTarget = target;
    }

    public static void ClearFocus(Transform target)
    {
        // Only release if nobody re-focused elsewhere in the meantime.
        if (focusTarget == target)
            focusTarget = null;
    }

    void Awake()
    {
        instance = this;
        cam = GetComponent<Camera>();
        baseRotation = transform.rotation;
        focusTarget = null; // statics survive scene reloads
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

        // Integrate the springs in fixed substeps: frame-time spikes no
        // longer kick the spring (the source of rubberbandy hiccups), and
        // position/aim can't wobble against each other on long frames.
        const float substep = 1f / 240f;
        float remaining = Mathf.Min(Time.deltaTime, 0.05f);
        while (remaining > 0f)
        {
            float dt = Mathf.Min(remaining, substep);
            StepSprings(dt);
            remaining -= dt;
        }
    }

    private void StepSprings(float dt)
    {
        // Underdamped spring: accelerate toward the desired spot, bleed off
        // velocity slower than critical damping would -> slight overshoot.
        // The picker zoom-in gets its own feel, separate from group framing.
        float posStiffness = focusTarget ? focusStiffness : stiffness;
        float posDamping = focusTarget ? focusDamping : damping;

        Vector3 desired = ComputeDesiredPosition();
        velocity += (desired - transform.position) * (posStiffness * dt);
        velocity *= Mathf.Exp(-posDamping * dt);
        transform.position += velocity * dt;

        // Aim: pivot to look dead-on at the picker while focused (whatever
        // the height offset), then ease back to the fixed gameplay angle.
        // Aim is computed from the DESTINATION, not the live position — the
        // position spring's overshoot would otherwise feed a moving target
        // into the rotation spring and wobble the pitch.
        Quaternion desiredRot = focusTarget
            ? Quaternion.LookRotation(focusTarget.position - desired, Vector3.up)
            : baseRotation;

        Quaternion delta = desiredRot * Quaternion.Inverse(transform.rotation);
        delta.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f) angle -= 360f; // take the short way around

        if (!float.IsNaN(axis.x) && angle != 0f)
            angularVelocity += axis.normalized *
                (angle * Mathf.Deg2Rad * focusAimStiffness * dt);
        angularVelocity *= Mathf.Exp(-focusAimDamping * dt);

        float step = angularVelocity.magnitude * Mathf.Rad2Deg * dt;
        if (step > 0f)
        {
            transform.rotation = Quaternion.AngleAxis(step, angularVelocity.normalized)
                                 * transform.rotation;

            // The shortest arc between two orientations can twist through a
            // bit of roll mid-flight; rebuild from forward + world up to keep
            // Z locked at zero throughout the move.
            transform.rotation = Quaternion.LookRotation(transform.forward, Vector3.up);
        }
    }

    private Vector3 ComputeDesiredPosition()
    {
        // A picker is open: park in front of it (dolly along the BASE
        // forward so the aim pivot doesn't feed back into positioning).
        if (focusTarget)
            return focusTarget.position + Vector3.up * focusHeight
                   - (baseRotation * Vector3.forward) * focusDistance;

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

        // Pull back from the center along the fixed viewing direction (base
        // rotation, so a mid-pivot camera doesn't warp the framing).
        return center - (baseRotation * Vector3.forward) * distance;
    }
}
