using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;
using Content.Shared.Interaction;
using Content.Shared.Weapons.Ranged.Components;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;

/// <summary>
/// Inserts a loaded magazine, swaps/inserts a power cell, or feeds a ballistic tube (one round per Update tick).
/// </summary>
public sealed partial class ReloadHeldGunOperator : HTNOperator
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
            ammo.DebugAmmo(owner, "ReloadHeldGun.Plan: no owned gun", force: true);
            return (false, null);
        }

        if (ammo.IsMagazineFed(gun))
        {
            var needs = ammo.NeedsMagazineInsert(owner, gun);
            ammo.DebugAmmo(owner, $"ReloadHeldGun.Plan magInsert={needs}", force: true);
            return (needs, null);
        }

        if (ammo.IsPowerCellSwapGun(gun))
        {
            var needs = ammo.NeedsPowerCellInsert(owner, gun);
            ammo.DebugAmmo(owner,
                $"ReloadHeldGun.Plan powerCell={needs} ({ammo.DescribeEnergyGunState(owner, gun, blackboard)})",
                force: true);
            return (needs, null);
        }

        if (_entManager.HasComponent<BallisticAmmoProviderComponent>(gun))
        {
            var needs = ammo.NeedsBallisticTubeFill(owner, gun);
            ammo.DebugAmmo(owner, $"ReloadHeldGun.Plan tubeFill={needs}", force: true);
            return (needs, null);
        }

        ammo.DebugAmmo(owner,
            $"ReloadHeldGun.Plan: no reload path ({ammo.DescribeEnergyGunState(owner, gun, blackboard)})",
            force: true);
        return (false, null);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var interaction = _entManager.System<SharedInteractionSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard))
        {
            ammo.DebugAmmo(owner, "ReloadHeldGun.Update: FAIL no owned gun", force: true);
            ammo.SetAmmoSearchCooldown(blackboard);
            return HTNOperatorStatus.Failed;
        }

        if (ammo.IsMagazineFed(gun))
        {
            if (!ammo.TryFindCompatibleLoadedMagazine(owner, gun, out var magazine))
            {
                ammo.DebugAmmo(owner, "ReloadHeldGun.Update: FAIL no spare mag", force: true);
                ammo.SetAmmoSearchCooldown(blackboard);
                return HTNOperatorStatus.Failed;
            }

            if (ammo.TryInsertMagazine(owner, gun, magazine))
            {
                ammo.DebugAmmo(owner, $"ReloadHeldGun.Update: mag insert OK {_entManager.ToPrettyString(magazine)}", force: true);
                return HTNOperatorStatus.Finished;
            }

            if (!ammo.TryObtainInHand(owner, magazine))
            {
                ammo.DebugAmmo(owner, "ReloadHeldGun.Update: FAIL obtain mag", force: true);
                ammo.SetAmmoSearchCooldown(blackboard);
                return HTNOperatorStatus.Failed;
            }

            var coords = _entManager.GetComponent<TransformComponent>(gun).Coordinates;
            var interacted = interaction.InteractUsing(owner, magazine, gun, coords, checkCanInteract: false, checkCanUse: false);
            if (!interacted)
            {
                ammo.DebugAmmo(owner, "ReloadHeldGun.Update: FAIL InteractUsing mag", force: true);
                ammo.SetAmmoSearchCooldown(blackboard);
                return HTNOperatorStatus.Failed;
            }

            ammo.EnsureChamberReady(gun, owner);
            ammo.DebugAmmo(owner, "ReloadHeldGun.Update: mag InteractUsing OK", force: true);
            return HTNOperatorStatus.Finished;
        }

        if (ammo.IsPowerCellSwapGun(gun))
        {
            if (!ammo.TryFindBestCompatiblePowerCell(owner, gun, out var cell, out var cellScore))
            {
                ammo.DebugAmmo(owner,
                    $"ReloadHeldGun.Update: FAIL no power cell ({ammo.DescribeEnergyGunState(owner, gun, blackboard)}) cells={ammo.DescribeOwnedPowerCells(owner, gun)}",
                    force: true);
                ammo.SetAmmoSearchCooldown(blackboard);
                return HTNOperatorStatus.Failed;
            }

            ammo.DebugAmmo(owner,
                $"ReloadHeldGun.Update: inserting score={cellScore} {ammo.DescribePowerCell(cell, gun)}",
                force: true);

            if (ammo.TryInsertPowerCell(owner, gun, cell))
            {
                ammo.DebugAmmo(owner,
                    $"ReloadHeldGun.Update: cell insert OK {_entManager.ToPrettyString(cell)} after={ammo.DescribeOwnedPowerCells(owner, gun)}",
                    force: true);
                ammo.TryFinishAmmoWorkReadyToFight(owner, gun, blackboard);
                return HTNOperatorStatus.Finished;
            }

            ammo.DebugAmmo(owner, $"ReloadHeldGun.Update: FAIL insert cell {_entManager.ToPrettyString(cell)}", force: true);
            ammo.SetAmmoSearchCooldown(blackboard);
            return HTNOperatorStatus.Failed;
        }

        if (_entManager.HasComponent<BallisticAmmoProviderComponent>(gun))
        {
            if (!ammo.IsProviderBelowCapacity(gun))
                return HTNOperatorStatus.Finished;

            if (!ammo.TryFeedOneIntoProvider(owner, gun, gun))
            {
                ammo.SetAmmoSearchCooldown(blackboard);
                return HTNOperatorStatus.Failed;
            }

            var cont = ammo.IsProviderBelowCapacity(gun) &&
                       (ammo.TryFindCompatibleCartridge(owner, gun, out _) ||
                        ammo.TryFindMayTransferAmmoBox(owner, gun, gun, out _));
            return cont ? HTNOperatorStatus.Continuing : HTNOperatorStatus.Finished;
        }

        ammo.SetAmmoSearchCooldown(blackboard);
        return HTNOperatorStatus.Failed;
    }
}
