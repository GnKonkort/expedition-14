using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;
using Content.Shared.Hands.EntitySystems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;

/// <summary>
/// Drops an empty external-charge energy gun when no portable recharger is available.
/// </summary>
public sealed partial class DiscardUnusableEnergyGunOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard))
        {
            ammo.DebugAmmo(owner, "DiscardEnergyGun.Plan: no owned gun", force: true);
            return (false, null);
        }

        var discard = ammo.ShouldDiscardEnergyGun(owner, gun);
        ammo.DebugAmmo(owner,
            $"DiscardEnergyGun.Plan discard={discard} ({ammo.DescribeEnergyGunState(owner, gun, blackboard)})",
            force: true);
        return (discard, null);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var hands = _entManager.System<SharedHandsSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard) ||
            !ammo.ShouldDiscardEnergyGun(owner, gun))
        {
            ammo.DebugAmmo(owner, "DiscardEnergyGun.Update: FAIL precondition", force: true);
            return HTNOperatorStatus.Failed;
        }

        if (!hands.IsHolding(owner, gun) && !ammo.TryObtainInHand(owner, gun))
        {
            ammo.DebugAmmo(owner, "DiscardEnergyGun.Update: FAIL obtain gun", force: true);
            return HTNOperatorStatus.Failed;
        }

        ammo.ForgetOwnedGun(blackboard);
        ammo.ClearEnergyChargeWait(blackboard);
        var dropped = hands.TryDrop(owner, gun, checkActionBlocker: false);
        ammo.DebugAmmo(owner, $"DiscardEnergyGun.Update: drop => {dropped}", force: true);
        return dropped ? HTNOperatorStatus.Finished : HTNOperatorStatus.Failed;
    }
}
