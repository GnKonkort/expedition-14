using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;
using Content.Shared.Hands.EntitySystems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;

/// <summary>
/// Docks an empty energy gun into a portable recharger (or holsters a self-recharging gun).
/// Does not idle-wait — after this finishes, HTN can fall through to melee / other tasks while the gun charges.
/// </summary>
public sealed partial class StowEnergyGunForChargeOperator : HTNOperator
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
            ammo.DebugAmmo(owner, "StowEnergyForCharge.Plan: no owned gun");
            return (false, null);
        }

        var needs = ammo.NeedsStowEnergyGunForCharge(owner, gun, blackboard);
        ammo.DebugAmmo(owner,
            $"StowEnergyForCharge.Plan needsStow={needs} ({ammo.DescribeEnergyGunState(owner, gun, blackboard)})");
        return (needs, null);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var hands = _entManager.System<SharedHandsSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard))
        {
            ammo.DebugAmmo(owner, "StowEnergyForCharge.Update: FAIL no owned gun");
            return HTNOperatorStatus.Failed;
        }

        ammo.DebugAmmo(owner,
            $"StowEnergyForCharge.Update: begin ({ammo.DescribeEnergyGunState(owner, gun, blackboard)})");

        // Already docked — nothing to do this tick.
        if (ammo.IsGunInPortableRecharger(owner, gun, out var dock))
        {
            ammo.NoteEnergyChargeWait(blackboard);
            ammo.DebugAmmo(owner, $"StowEnergyForCharge.Update: already docked {_entManager.ToPrettyString(dock)}");
            return HTNOperatorStatus.Finished;
        }

        // Prefer docking when a portable recharger is available.
        if (ammo.TryFindPortableRecharger(owner, out var recharger) && !ammo.IsPowerCellSwapGun(gun))
        {
            var ok = ammo.TryStowEnergyGunForCharge(owner, gun, blackboard);
            ammo.DebugAmmo(owner,
                $"StowEnergyForCharge.Update: dock into {_entManager.ToPrettyString(recharger)} => {ok}");
            return ok ? HTNOperatorStatus.Finished : HTNOperatorStatus.Failed;
        }

        // Self-recharge fallback: holster until full if still held.
        if (hands.IsHolding(owner, gun))
        {
            var ok = ammo.TryStowEnergyGunForCharge(owner, gun, blackboard);
            ammo.DebugAmmo(owner, $"StowEnergyForCharge.Update: holster held gun => {ok}");
            return ok ? HTNOperatorStatus.Finished : HTNOperatorStatus.Failed;
        }

        ammo.DebugAmmo(owner, "StowEnergyForCharge.Update: already stowed on body, OK");
        ammo.NoteEnergyChargeWait(blackboard);
        return HTNOperatorStatus.Finished;
    }
}
