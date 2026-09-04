using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;

public class Player : NetworkBehaviour
{
    [Header("Movement")]
    [SerializeField] private float moveSpeed = 5f;

    [Header("Dash")]
    [SerializeField] private float dashSpeed = 18f;
    [SerializeField] private float dashDuration = 0.2f;
    [Tooltip("Seconds after a dash ends before the next one is available.")]
    [SerializeField] private float dashCooldown = 0.8f;

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

    // Dash state: while dashTimeLeft > 0 the dash direction overrides the
    // stick entirely.
    private Vector3 dashDir;
    private float dashTimeLeft;
    private float dashCooldownLeft;
    private bool dashHeldLast;

    // Aggregates stat modifiers from held upgrades.
    private UpgradeSlots upgrades;

    // Upgrade-choice state: set while this player is picking which slot the
    // incoming upgrade replaces. All players are movement-paused meanwhile.
    private UpgradeDefinition pendingUpgrade;
    private UpgradeChoiceUI choiceUI;
    private int hoverIndex;
    private bool confirmHeldLast;
    private bool stickReady;
    private bool replacing;

    public bool IsChoosing => pendingUpgrade != null;

    // Defaults to an all-zero InputData so Update() is safe before a
    // controller ever pushes (id 0, zero vectors, all flags false).
    public InputData input = new InputData();

    void Awake()
    {
        // Self-heal if the prefab hasn't had slots added in the editor.
        upgrades = GetComponent<UpgradeSlots>();
        if (!upgrades)
            upgrades = gameObject.AddComponent<UpgradeSlots>();

        // The picker UI lives on the prefab as a child and just gets toggled
        // on/off — never created from code.
        choiceUI = GetComponentInChildren<UpgradeChoiceUI>(true);
        if (choiceUI)
            choiceUI.gameObject.SetActive(false);
        else
            Debug.LogError("Player prefab is missing its UpgradeChoiceUI child", this);
    }

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

        // Never leave the world frozen if we vanish mid-choice.
        if (IsChoosing)
        {
            pendingUpgrade = null;
            PlayerManager.PopMovementPause();
        }
    }

    void Update()
    {
        // Clients only render; the host simulates.
        if (!IsServer) return;

        if (IsChoosing)
        {
            HandleChoice();
            return;
        }

        // Someone's upgrade picker is open: the whole brawl holds still.
        if (PlayerManager.MovementPaused) return;

        HandleDash();
        Move();
        Look();
        HandleThrow();
        HandlePickup();
    }

    private void HandleDash()
    {
        dashCooldownLeft -= Time.deltaTime;

        bool pressed = input.dash && !dashHeldLast;
        dashHeldLast = input.dash;

        if (pressed && dashCooldownLeft <= 0f && dashTimeLeft <= 0f)
        {
            // Teleport Sword upgrade: while our sword is out of hand, the
            // dash becomes a teleport to wherever the sword is.
            if (upgrades.TeleportToSword && !holdingSword)
            {
                TeleportToSword();
                dashCooldownLeft = dashCooldown;
                return;
            }

            // Dash where the stick points; fall back to facing when idle.
            Vector3 stick = new Vector3(input.move.x, 0f, input.move.y);
            dashDir = stick.sqrMagnitude > 0.01f ? stick.normalized : transform.forward;

            dashTimeLeft = dashDuration;
            dashCooldownLeft = dashDuration + dashCooldown;
        }
    }

    private void TeleportToSword()
    {
        // Land on the sword on the XZ plane; keep our own height so we don't
        // sink to wherever the sword physically settled.
        Vector3 p = sword.transform.position;
        p.y = transform.position.y;

        // Snap, don't glide: mark the jump as a teleport so clients don't
        // interpolate us zipping across the map.
        var netTransform = GetComponent<NetworkTransform>();
        if (netTransform)
            netTransform.Teleport(p, transform.rotation, transform.localScale);
        else
            transform.position = p;

        // We're standing on the sword now: let HandlePickup grab it this
        // frame even if it never left pickup range after the throw.
        swordLeftRange = true;
    }

    // Called by UpgradePickup (server-side) when we grab a 4th upgrade.
    public void BeginUpgradeChoice(UpgradeDefinition upgrade)
    {
        pendingUpgrade = upgrade;
        hoverIndex = 1; // start over the middle slot

        // Ignore whatever the stick/button were doing at pickup time.
        confirmHeldLast = input.confirm;
        stickReady = false;

        PlayerManager.PushMovementPause();

        // Presentation fans out to every machine (host included) as colors —
        // clients never need the UpgradeDefinition assets themselves.
        var colors = new Color[upgrades.Slots.Count];
        for (int i = 0; i < colors.Length; i++)
            colors[i] = upgrades.Slots[i].color;

        ShowChoiceRpc(colors, upgrade.color, hoverIndex);
    }

    // ---------- choice-UI presentation, mirrored on every machine ----------
    // The server owns the state; these RPCs only drive visuals + camera.

    [Rpc(SendTo.ClientsAndHost)]
    private void ShowChoiceRpc(Color[] slotColors, Color incomingColor, int hover)
    {
        choiceUI.SetHover(hover);
        choiceUI.Show(slotColors, incomingColor);

        // Camera dives in on the picker for the duration of the choice.
        DynamicCamera.FocusOn(choiceUI.transform);
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void HoverChoiceRpc(int index)
    {
        choiceUI.SetHover(index);
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void ReplaceChoiceRpc(int index)
    {
        choiceUI.PlayReplace(index);
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void HideChoiceRpc()
    {
        choiceUI.Hide();
        DynamicCamera.ClearFocus(choiceUI.transform);
    }

    private void HandleChoice()
    {
        // Confirmed: hold the freeze until the drop-in animation lands (the
        // host's local UI instance is the one we poll — every machine plays
        // the same animation).
        if (replacing)
        {
            if (!choiceUI.IsReplacing)
                EndChoice();
            return;
        }

        // Left stick slides the incoming upgrade between slots: one step per
        // flick, re-armed when the stick returns to neutral.
        float x = input.move.x;
        if (stickReady && Mathf.Abs(x) > 0.6f)
        {
            hoverIndex = Mathf.Clamp(hoverIndex + (x > 0f ? 1 : -1),
                                     0, upgrades.Slots.Count - 1);
            HoverChoiceRpc(hoverIndex);
            stickReady = false;
        }
        else if (Mathf.Abs(x) < 0.3f)
        {
            stickReady = true;
        }

        // South button (rising edge) confirms: replace the hovered slot.
        // Stats apply immediately; the UI plays the drop-in meanwhile.
        if (input.confirm && !confirmHeldLast)
        {
            upgrades.Replace(hoverIndex, pendingUpgrade);
            ReplaceChoiceRpc(hoverIndex);
            replacing = true;
        }
        confirmHeldLast = input.confirm;
    }

    private void EndChoice()
    {
        pendingUpgrade = null;
        replacing = false;
        HideChoiceRpc();
        PlayerManager.PopMovementPause();
    }

    private void Move()
    {
        // An active dash overrides the stick completely.
        if (dashTimeLeft > 0f)
        {
            dashTimeLeft -= Time.deltaTime;
            transform.position += dashDir * (dashSpeed * upgrades.MoveSpeedMult) * Time.deltaTime;
            return;
        }

        // Left stick drives movement on the XZ plane at a constant speed.
        Vector3 dir = new Vector3(input.move.x, 0f, input.move.y);
        transform.position += dir * (moveSpeed * upgrades.MoveSpeedMult) * Time.deltaTime;
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
            float speed = Mathf.Lerp(minThrowVelocity, maxThrowVelocity, t)
                          * upgrades.ThrowVelocityMult;

            sword.Throw(transform.forward, speed, upgrades.TornadoThrow);
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
