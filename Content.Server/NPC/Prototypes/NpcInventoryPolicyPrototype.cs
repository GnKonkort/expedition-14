using Robust.Shared.Prototypes;

namespace Content.Server.NPC.Prototypes;

/// <summary>
/// Shared loadout limits, hand rules, and pickup priority weights for inventory management.
/// </summary>
[Prototype("npcInventoryPolicy")]
public sealed partial class NpcInventoryPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField]
    public int MaxKitStacks = 2;

    [DataField]
    public int MaxMedipens = 2;

    /// <summary>
    /// Prefer keeping a weapon in the active hand when hostiles are present.
    /// </summary>
    [DataField]
    public bool PreferWeaponInHand = true;

    /// <summary>
    /// Soft cap on loose ammo boxes / mags carried outside bags.
    /// </summary>
    [DataField]
    public int MaxLooseAmmoStacks = 4;

    /// <summary>
    /// Medic / triage roles should seek and keep a defibrillator.
    /// </summary>
    [DataField]
    public bool PreferDefib;

    /// <summary>
    /// Allow stocking reagent bottles / beakers (requires chemistry-aware role).
    /// Without this, NPCs discard / ignore chem containers they cannot use.
    /// </summary>
    [DataField]
    public bool AllowReagentContainers;

    /// <summary>
    /// Swap to a larger worn backpack when found and peaceful.
    /// </summary>
    [DataField]
    public bool PreferLargerBackpack = true;

    /// <summary>Base score weights by item class (situation multipliers apply on top).</summary>
    [DataField]
    public float WeightWeapon = 80f;

    [DataField]
    public float WeightAmmo = 55f;

    [DataField]
    public float WeightMedKit = 45f;

    [DataField]
    public float WeightMedipen = 40f;

    [DataField]
    public float WeightDefib = 70f;

    [DataField]
    public float WeightTool = 30f;

    [DataField]
    public float WeightFood = 25f;

    [DataField]
    public float WeightDrink = 25f;

    [DataField]
    public float WeightInternals = 60f;

    [DataField]
    public float WeightSurvivalKit = 35f;

    [DataField]
    public float WeightBackpackUpgrade = 50f;

    /// <summary>
    /// Candidate must beat the lowest droppable owned item by this margin to justify a drop-for-space.
    /// </summary>
    [DataField]
    public float ReplaceMargin = 8f;
}
