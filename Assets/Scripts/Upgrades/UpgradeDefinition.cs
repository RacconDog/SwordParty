using UnityEngine;

// One upgrade type = one asset (Create > SwordParty > Upgrade). For now they
// only carry identity + stat multipliers; exotic effects come later by
// subclassing this and overriding the hooks.
[CreateAssetMenu(menuName = "SwordParty/Upgrade", fileName = "NewUpgrade")]
public class UpgradeDefinition : ScriptableObject
{
    [Header("Identity")]
    public string displayName;
    public Color color = Color.white;

    [Header("Stat Modifiers (multiplicative, 1 = no change)")]
    public float moveSpeedMult = 1f;
    public float throwVelocityMult = 1f;

    [Header("Abilities (granted while held; any one holder grants it)")]
    [Tooltip("Dash button teleports to the thrown sword instead of dashing.")]
    public bool teleportToSword;
    [Tooltip("Thrown sword spins flat like a tornado: slower, hangs in the air longer.")]
    public bool tornadoThrow;

    // Hooks for effects that aren't a simple stat multiplier. Called on the
    // server when the upgrade enters/leaves a player's slots. Anything
    // OnEquip changes, OnRemove must revert.
    public virtual void OnEquip(Player player) { }
    public virtual void OnRemove(Player player) { }
}
