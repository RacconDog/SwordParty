using UnityEngine;
using Unity.Netcode;

[RequireComponent(typeof(Rigidbody))]
public class SwordProjectile : NetworkBehaviour
{
    [Header("Normal Throw")]
    [Tooltip("End-over-end tumble in flight, degrees per second. The tumble " +
             "ends on first impact; physics settles the sword from there.")]
    [SerializeField] private float tumbleSpeed = 540f;

    [Header("Tornado Throw (flight overrides while thrown by a Tornado holder)")]
    [Tooltip("Launch speed multiplier on top of the normal charged throw.")]
    [SerializeField] private float tornadoSpeedMult = 0.35f;
    [Tooltip("Fraction of normal gravity in flight; lower = hangs longer.")]
    [SerializeField] private float tornadoGravityScale = 0.15f;
    [Tooltip("Flat spin around world up, degrees per second.")]
    [SerializeField] private float tornadoSpinSpeed = 720f;

    // Replicated so clients can react to it (trail VFX, audio) later.
    // Server-write only.
    private readonly NetworkVariable<bool> inAirVar = new NetworkVariable<bool>();
    public bool inAir => inAirVar.Value;

    // Server-side: this flight was launched as a tornado throw. Decided at
    // release time — losing the upgrade mid-flight doesn't change the flight.
    private bool tornado;

    // Scripted flight spin, set at throw time. Clients never run this —
    // NetworkTransform replicates the resulting rotation.
    private Vector3 spinAxis;
    private float spinDegPerSec;

    private Rigidbody rb;

    // Server-side: the hand this sword rides in whenever it isn't flying.
    // Deliberately NOT parented — NGO parenting is finicky, so the held pose
    // is applied by following instead.
    private Transform hand;
    private Vector3 holdOffset;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();

        // Held swords ride along in the owner's hand; physics stays off
        // until a throw kicks it loose.
        rb.isKinematic = true;
    }

    public override void OnNetworkSpawn()
    {
        // Every machine frames this sword (at sword weight).
        DynamicCamera.RegisterSword(transform);
    }

    public void Init(Transform ownerHand, Vector3 offset)
    {
        hand = ownerHand;
        holdOffset = offset;
    }

    void LateUpdate()
    {
        // Host-side, held: snap to the hand pose after the owner has moved.
        if (!IsServer || inAirVar.Value || !hand) return;

        transform.position = hand.TransformPoint(holdOffset);
        transform.rotation = hand.rotation;
    }

    public void Throw(Vector3 direction, float speed, bool tornadoThrow = false)
    {
        inAirVar.Value = true;
        tornado = tornadoThrow;

        Vector3 dir = direction.normalized;

        // Stop following the hand and become a free physics object.
        rb.isKinematic = false;

        // While held, only the TRANSFORM tracks the hand — the rigidbody's
        // cached pose is stale. Every flight pose below is written to the
        // rigidbody directly, or the first MoveRotation would stomp it.
        rb.position = transform.position;

        Quaternion pose;
        if (tornado)
        {
            speed *= tornadoSpeedMult;

            // Engine gravity off; a scaled-down version is applied in
            // FixedUpdate for the long hang time.
            rb.useGravity = false;

            // Blade (local Y) laid flat and leading along the throw, flat
            // face up: the horizontal spin sweeps tip-first, and the camera
            // sees the full blade silhouette.
            pose = Quaternion.LookRotation(dir)
                 * Quaternion.Euler(90f, 0f, 0f)
                 * Quaternion.AngleAxis(90f, Vector3.up);

            spinAxis = Vector3.up;
            spinDegPerSec = tornadoSpinSpeed;
        }
        else
        {
            // End over end: tumble around the horizontal axis perpendicular
            // to the throw, tip rotating forward like a thrown axe.
            pose = transform.rotation;
            spinAxis = Vector3.Cross(Vector3.up, dir).normalized;
            spinDegPerSec = tumbleSpeed;
        }

        // The spin is scripted; physics doesn't get to tumble it on top.
        rb.freezeRotation = true;
        rb.rotation = pose;
        transform.rotation = pose;

        rb.linearVelocity = dir * speed;
    }

    void FixedUpdate()
    {
        // Server-only: clients' rigidbodies stay kinematic and just receive
        // the replicated transform.
        // While an upgrade picker has the world paused, physics isn't
        // stepping but FixedUpdate still ticks — bail so the custom gravity
        // doesn't accumulate into one giant slam on resume.
        if (!IsServer || !inAirVar.Value || PlayerManager.MovementPaused) return;

        // Reduced gravity = long hang time.
        if (tornado)
            rb.AddForce(Physics.gravity * tornadoGravityScale, ForceMode.Acceleration);

        if (spinDegPerSec > 0f)
            rb.MoveRotation(
                Quaternion.AngleAxis(spinDegPerSec * Time.fixedDeltaTime, spinAxis) * rb.rotation);
    }

    void OnCollisionEnter(Collision _)
    {
        // First impact ends the scripted tumble: rotation goes back to
        // physics so the sword clatters and settles naturally. Tornado
        // flights keep spinning until pickup — the spin IS the effect.
        if (!IsServer || !inAirVar.Value || tornado) return;

        spinDegPerSec = 0f;
        rb.freezeRotation = false;
    }

    public void PickUp()
    {
        inAirVar.Value = false;

        // Physics off; LateUpdate resumes holding it in the hand pose.
        rb.isKinematic = true;

        // Undo all flight overrides for the next throw.
        tornado = false;
        spinDegPerSec = 0f;
        rb.useGravity = true;
        rb.freezeRotation = false;
    }
}
