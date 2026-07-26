using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;

/// <summary>
/// Fills an incomplete magazine (seated in-place, or spare in-hand) from cartridges / MayTransfer boxes.
/// When finished, chambers a round and draws the gun.
/// </summary>
public sealed partial class FillMagazineOperator : HTNOperator
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

        return (ammo.HasMagazineFillMaterial(owner, gun), null);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard))
            return HTNOperatorStatus.Failed;

        if (!ammo.TryFindIncompleteCompatibleMagazine(owner, gun, out var magazine))
        {
            ammo.TryFinishAmmoWorkReadyToFight(owner, gun, blackboard);
            return HTNOperatorStatus.Failed;
        }

        var fillingSeated = ammo.IsSeatedMagazine(magazine);
        if (fillingSeated)
        {
            if (!ammo.TryDrawOwnedGun(owner, gun))
                return HTNOperatorStatus.Failed;
        }
        else if (!ammo.TryEnsureGunStowedForAmmoWork(owner, blackboard, out gun))
        {
            return HTNOperatorStatus.Failed;
        }

        if (!ammo.TryPrepareMagazineForFill(owner, gun, out magazine))
        {
            ammo.TryFinishAmmoWorkReadyToFight(owner, gun, blackboard);
            return HTNOperatorStatus.Failed;
        }

        if (!ammo.TryFindCompatibleCartridge(owner, magazine, out _) &&
            !ammo.TryFindMayTransferAmmoBox(owner, gun, magazine, out _) &&
            !ammo.TryPullNearbyFillMaterial(owner, gun, magazine))
        {
            ammo.TryFinishAmmoWorkReadyToFight(owner, gun, blackboard);
            return HTNOperatorStatus.Failed;
        }

        if (!ammo.TryFeedOneIntoProvider(owner, gun, magazine))
        {
            ammo.TryFinishAmmoWorkReadyToFight(owner, gun, blackboard);
            return HTNOperatorStatus.Failed;
        }

        if (ammo.IsProviderBelowCapacity(magazine) && ammo.HasFillMaterialFor(owner, gun, magazine))
            return HTNOperatorStatus.Continuing;

        ammo.TryFinishAmmoWorkReadyToFight(owner, gun, blackboard);
        return HTNOperatorStatus.Finished;
    }
}
