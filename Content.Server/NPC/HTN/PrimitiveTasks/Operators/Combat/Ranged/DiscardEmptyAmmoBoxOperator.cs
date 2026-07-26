using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;

/// <summary>
/// Drops one empty ammo box or drained disposable power cell for the owned gun.
/// </summary>
public sealed partial class DiscardEmptyAmmoBoxOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard))
            return (false, null);

        var has = ammo.TryFindEmptyCompatibleAmmoBox(owner, gun, out _) ||
                  ammo.TryFindDrainedDisposablePowerCell(owner, gun, out _);
        return (has, null);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard))
            return HTNOperatorStatus.Failed;

        if (ammo.TryDiscardEmptyAmmoBox(owner, gun))
            return HTNOperatorStatus.Finished;

        return ammo.TryDiscardDrainedDisposablePowerCell(owner, gun)
            ? HTNOperatorStatus.Finished
            : HTNOperatorStatus.Failed;
    }
}
