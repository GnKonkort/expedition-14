using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;

/// <summary>
/// Draws the NPC's remembered/carried gun into a hand (from pocket, belt, backpack, etc.).
/// </summary>
public sealed partial class DrawOwnedGunOperator : HTNOperator
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

        // Already in hand — GunCombat continues without this op.
        if (ammo.TryGetHeldGun(owner, out var held, out _) && held == gun)
            return (false, null);

        return (true, null);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard))
            return HTNOperatorStatus.Failed;

        // Writable blackboard: clear stuck charge-wait so we fight until empty, not re-holster mid-mag.
        ammo.TryClearEnergyChargeWaitIfReady(gun, blackboard);

        return ammo.TryDrawOwnedGun(owner, gun)
            ? HTNOperatorStatus.Finished
            : HTNOperatorStatus.Failed;
    }
}
