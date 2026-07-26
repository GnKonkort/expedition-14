using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// True when the ammo-search cooldown on the blackboard has elapsed.
/// </summary>
public sealed partial class AmmoSearchReadyPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public bool Invert;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var ready = ammo.IsAmmoSearchReady(blackboard);
        ammo.DebugAmmo(owner, $"AmmoSearchReady => {ready}");
        return Invert ? !ready : ready;
    }
}
