using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;
using Content.Shared.Interaction;
using Content.Shared.Weapons.Ranged.Components;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;

/// <summary>
/// Inserts a loaded magazine, or feeds a ballistic tube until full (one round per Update tick).
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
            return (false, null);

        if (ammo.IsMagazineFed(gun))
            // NeedsMagazineInsert already requires a spare loaded mag (and won't steal mid-fill).
            return (ammo.NeedsMagazineInsert(owner, gun), null);

        if (_entManager.HasComponent<BallisticAmmoProviderComponent>(gun))
            return (ammo.NeedsBallisticTubeFill(owner, gun), null);

        return (false, null);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var interaction = _entManager.System<SharedInteractionSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard))
        {
            ammo.SetAmmoSearchCooldown(blackboard);
            return HTNOperatorStatus.Failed;
        }

        if (ammo.IsMagazineFed(gun))
        {
            if (!ammo.TryFindCompatibleLoadedMagazine(owner, gun, out var magazine))
            {
                ammo.SetAmmoSearchCooldown(blackboard);
                return HTNOperatorStatus.Failed;
            }

            if (ammo.TryInsertMagazine(owner, gun, magazine))
                return HTNOperatorStatus.Finished;

            if (!ammo.TryObtainInHand(owner, magazine))
            {
                ammo.SetAmmoSearchCooldown(blackboard);
                return HTNOperatorStatus.Failed;
            }

            var coords = _entManager.GetComponent<TransformComponent>(gun).Coordinates;
            var interacted = interaction.InteractUsing(owner, magazine, gun, coords, checkCanInteract: false, checkCanUse: false);
            if (!interacted)
            {
                ammo.SetAmmoSearchCooldown(blackboard);
                return HTNOperatorStatus.Failed;
            }

            ammo.EnsureChamberReady(gun, owner);
            return HTNOperatorStatus.Finished;
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
