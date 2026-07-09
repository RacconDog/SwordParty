using UnityEngine;
using Unity.Netcode;

[RequireComponent(typeof(Rigidbody))]
public class SwordProjectile : NetworkBehaviour
{
    // Replicated so clients can react to it (trail VFX, audio) later.
    // Server-write only.
    private readonly NetworkVariable<bool> inAirVar = new NetworkVariable<bool>();
    public bool inAir => inAirVar.Value;

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

    public void Throw(Vector3 direction, float speed)
    {
        inAirVar.Value = true;

        // Stop following the hand and become a free physics object.
        rb.isKinematic = false;
        rb.linearVelocity = direction.normalized * speed;
    }

    public void PickUp()
    {
        inAirVar.Value = false;

        // Physics off; LateUpdate resumes holding it in the hand pose.
        rb.isKinematic = true;
    }
}
