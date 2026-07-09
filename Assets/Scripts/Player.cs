using UnityEngine;
using Unity.Netcode;

public class Player : NetworkBehaviour
{
    [Header("Movement")]
    [SerializeField] private float moveSpeed = 5f;

    [Header("Turning (P-controlled rotational lerp)")]
    [Tooltip("Proportional gain: turn speed = angleError * this, then clamped.")]
    [SerializeField] private float rotationP = 10f;
    [SerializeField] private float minTurnSpeed = 90f;   // deg/sec floor
    [SerializeField] private float maxTurnSpeed = 720f;  // deg/sec ceiling
    [Tooltip("Right-stick magnitude needed to override facing.")]
    [SerializeField] private float lookDeadzone = 0.1f;

    [Header("Sword Throw")]
    [Tooltip("Sword prefab this player instantiates and owns.")]
    [SerializeField] private SwordProjectile swordPrefab;
    [Tooltip("Local position the held sword sits at (the 'hand').")]
    [SerializeField] private Vector3 swordHoldOffset = new Vector3(0.5f, 0f, 0.5f);
    [SerializeField] private float minThrowVelocity = 8f;
    [SerializeField] private float maxThrowVelocity = 25f;
    [Tooltip("Seconds of holding to charge from min to max velocity (linear).")]
    [SerializeField] private float chargeTime = 1.5f;
    [Tooltip("How close we must get to our sword to pick it back up.")]
    [SerializeField] private float pickupRange = 1.5f;

    // Constant reference to OUR sword instance, spawned server-side.
    private SwordProjectile sword;

    // Flags the sword we own as currently in our hand.
    private bool holdingSword = true;
    private float throwHoldTime;
    private bool throwHeldLastFrame;

    // A freshly thrown sword starts inside pickup range; it has to leave the
    // radius once before it can be grabbed again.
    private bool swordLeftRange;

    // Defaults to an all-zero InputData so Update() is safe before a
    // controller ever pushes (id 0, zero vectors, all flags false).
    public InputData input = new InputData();

    public override void OnNetworkSpawn()
    {
        // Every machine frames this avatar.
        DynamicCamera.RegisterPlayer(transform);

        // The simulation — and therefore the sword — lives host-side only.
        // Clients just see the replicated transforms.
        if (!IsServer) return;

        sword = Instantiate(swordPrefab,
            transform.TransformPoint(swordHoldOffset), transform.rotation);
        sword.NetworkObject.Spawn();
        sword.Init(transform, swordHoldOffset);
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && sword && sword.NetworkObject.IsSpawned)
            sword.NetworkObject.Despawn();
    }

    void Update()
    {
        // Clients only render; the host simulates.
        if (!IsServer) return;

        Move();
        Look();
        HandleThrow();
        HandlePickup();
    }

    private void Move()
    {
        // Left stick drives movement on the XZ plane at a constant speed.
        Vector3 dir = new Vector3(input.move.x, 0f, input.move.y);
        transform.position += dir * moveSpeed * Time.deltaTime;
    }

    private void Look()
    {
        // Default: face the direction you're moving (left stick).
        // Right stick overrides facing once it's past the deadzone.
        Vector2 face = input.move;
        if (input.look.magnitude > lookDeadzone)
            face = input.look;

        // Neither stick is meaningfully pushed -> hold current facing.
        if (face.sqrMagnitude < lookDeadzone * lookDeadzone)
            return;

        Vector3 faceDir = new Vector3(face.x, 0f, face.y);
        Quaternion target = Quaternion.LookRotation(faceDir, Vector3.up);

        // P controller: the further we're rotated from the target, the faster
        // we turn, clamped between a min and max turn speed.
        float angleError = Quaternion.Angle(transform.rotation, target);
        float turnSpeed = Mathf.Clamp(angleError * rotationP, minTurnSpeed, maxTurnSpeed);

        transform.rotation = Quaternion.RotateTowards(
            transform.rotation, target, turnSpeed * Time.deltaTime);
    }

    private void HandleThrow()
    {
        bool held = input.throwAttack;

        // Charge while the button is held with a sword in hand.
        if (held && holdingSword)
            throwHoldTime += Time.deltaTime;

        // Released this frame -> launch with velocity scaled by hold time:
        // linear from min to max over chargeTime seconds, clamped at max.
        if (!held && throwHeldLastFrame && holdingSword)
        {
            float t = chargeTime > 0f ? Mathf.Clamp01(throwHoldTime / chargeTime) : 1f;
            float speed = Mathf.Lerp(minThrowVelocity, maxThrowVelocity, t);

            sword.Throw(transform.forward, speed);
            holdingSword = false;
            swordLeftRange = false;
            throwHoldTime = 0f;
        }

        throwHeldLastFrame = held;
    }

    private void HandlePickup()
    {
        if (holdingSword) return;

        float dist = Vector3.Distance(transform.position, sword.transform.position);

        // Arm pickup only after the sword has been outside the radius once,
        // so we don't instantly re-grab it the frame we let go.
        if (!swordLeftRange)
        {
            if (dist > pickupRange)
                swordLeftRange = true;
            return;
        }

        if (dist <= pickupRange)
        {
            sword.PickUp();
            holdingSword = true;
        }
    }
}
