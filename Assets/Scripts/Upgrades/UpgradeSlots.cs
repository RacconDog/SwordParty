using UnityEngine;
using System.Collections.Generic;

// The player's upgrade slots (server-side state, lives on the Player object).
// FIFO: picking up an upgrade while full forces the oldest one out.
// Aggregated stat multipliers are recalculated whenever the slots change;
// Player reads them every frame, so effects apply/revert instantly.
public class UpgradeSlots : MonoBehaviour
{
    [SerializeField] private int maxSlots = 3;

    private readonly List<UpgradeDefinition> slots = new List<UpgradeDefinition>();
    private Player player;

    public IReadOnlyList<UpgradeDefinition> Slots => slots;
    public bool IsFull => slots.Count >= maxSlots;

    // Aggregated modifiers (1 = unmodified). Grow this alongside Player stats.
    public float MoveSpeedMult { get; private set; } = 1f;
    public float ThrowVelocityMult { get; private set; } = 1f;

    // Aggregated abilities: on if ANY held upgrade grants them.
    public bool TeleportToSword { get; private set; }
    public bool TornadoThrow { get; private set; }

    void Awake()
    {
        player = GetComponent<Player>();
    }

    public void Add(UpgradeDefinition upgrade)
    {
        // Full -> the oldest upgrade is forced out (destroyed, by design).
        if (slots.Count >= maxSlots)
            RemoveAt(0);

        slots.Add(upgrade);
        upgrade.OnEquip(player);
        Recalculate();
    }

    // The picked slot's upgrade is destroyed and the new one takes its place.
    public void Replace(int index, UpgradeDefinition upgrade)
    {
        RemoveAt(index);
        slots.Insert(index, upgrade);
        upgrade.OnEquip(player);
        Recalculate();
    }

    public void RemoveAt(int index)
    {
        UpgradeDefinition removed = slots[index];
        slots.RemoveAt(index);
        removed.OnRemove(player);
        Recalculate();
    }

    private void Recalculate()
    {
        MoveSpeedMult = 1f;
        ThrowVelocityMult = 1f;
        TeleportToSword = false;
        TornadoThrow = false;

        foreach (UpgradeDefinition u in slots)
        {
            MoveSpeedMult *= u.moveSpeedMult;
            ThrowVelocityMult *= u.throwVelocityMult;
            TeleportToSword |= u.teleportToSword;
            TornadoThrow |= u.tornadoThrow;
        }
    }
}
