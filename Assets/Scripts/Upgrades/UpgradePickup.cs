using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;

// A ground upgrade. Drag instances into the scene for now (an autonomous
// spawner comes later). Tints itself from its definition so upgrade types
// read at a glance. Pickup resolves server-side; despawning replicates the
// removal to every client. Taken pickups aren't destroyed — once EVERY
// pickup in the scene has been used, the whole set respawns.
[RequireComponent(typeof(Collider))]
public class UpgradePickup : NetworkBehaviour
{
    // Server-side registry of every pickup in the scene.
    private static readonly List<UpgradePickup> all = new List<UpgradePickup>();

    [SerializeField] private UpgradeDefinition upgrade;
    [SerializeField] private Renderer tintRenderer;

    private bool taken;

    public override void OnNetworkSpawn()
    {
        if (IsServer && !all.Contains(this))
            all.Add(this);
    }

    public override void OnDestroy()
    {
        all.Remove(this);
        base.OnDestroy();
    }

    void OnValidate()
    {
        ApplyColor();
    }

    void Awake()
    {
        ApplyColor();
    }

    private void ApplyColor()
    {
        if (!upgrade || !tintRenderer) return;

        // Property block: tints this instance without instantiating or
        // editing the shared material asset (works in edit mode too).
        var block = new MaterialPropertyBlock();
        tintRenderer.GetPropertyBlock(block);
        block.SetColor("_BaseColor", upgrade.color);
        tintRenderer.SetPropertyBlock(block);
    }

    void OnTriggerEnter(Collider other)
    {
        // Simulation is host-side only; clients just see the despawn.
        if (!IsServer || !IsSpawned) return;

        var slots = other.GetComponentInParent<UpgradeSlots>();
        if (!slots) return;

        var player = slots.GetComponent<Player>();
        if (player && player.IsChoosing) return;

        // Room free -> straight in. Full -> the incoming upgrade opens the
        // replace-a-slot picker on this player instead.
        if (!slots.IsFull)
            slots.Add(upgrade);
        else if (player)
            player.BeginUpgradeChoice(upgrade);
        else
            return;

        // Despawn WITHOUT destroying so this pickup can come back later.
        taken = true;
        NetworkObject.Despawn(false);
        gameObject.SetActive(false);

        RespawnAllIfDepleted();
    }

    private static void RespawnAllIfDepleted()
    {
        all.RemoveAll(p => !p);

        foreach (UpgradePickup p in all)
            if (!p.taken)
                return;

        // Every pickup in the scene has been used: bring the set back.
        foreach (UpgradePickup p in all)
        {
            p.taken = false;
            p.gameObject.SetActive(true);
            p.NetworkObject.Spawn();
        }
    }
}
