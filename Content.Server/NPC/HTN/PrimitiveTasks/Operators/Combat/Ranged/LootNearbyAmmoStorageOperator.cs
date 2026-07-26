using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;
using Content.Shared.Storage.EntitySystems;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;

/// <summary>
/// Picks a nearby storage with compatible ammo (sampled ≤10) and sets Target,
/// or loots one compatible item from an already-selected Target storage.
/// </summary>
public sealed partial class LootNearbyAmmoStorageOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public string TargetKey = "Target";

    /// <summary>
    /// If false, only select a storage target. If true, take one item from Target.
    /// </summary>
    [DataField]
    public bool TakeItem;

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard))
            return (false, null);

        // Loot step: require an existing storage Target from the pick step.
        if (TakeItem)
        {
            if (!blackboard.TryGetValue<EntityUid>(TargetKey, out var existing, _entManager))
                return (false, null);

            return (ammo.StorageContainsCompatibleAmmo(existing, gun), null);
        }

        if (!ammo.TryPickNearbyAmmoStorage(owner, gun, ammo.GetAmmoSearchRange(blackboard), out var storage))
            return (false, null);

        if (!_entManager.TryGetComponent(storage, out TransformComponent? xform))
            return (false, null);

        return (true, new Dictionary<string, object>
        {
            { TargetKey, storage },
            { "TargetCoordinates", xform.Coordinates },
        });
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        if (!TakeItem)
            return HTNOperatorStatus.Finished;

        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var storageSys = _entManager.System<SharedStorageSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!blackboard.TryGetValue<EntityUid>(TargetKey, out var storage, _entManager) ||
            !ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard))
        {
            ammo.SetAmmoSearchCooldown(blackboard);
            return HTNOperatorStatus.Failed;
        }

        storageSys.OpenStorageUI(storage, owner);

        if (!ammo.TryGetCompatibleItemFromStorage(storage, gun, out var item) ||
            !ammo.TryObtainInHand(owner, item))
        {
            ammo.SetAmmoSearchCooldown(blackboard);
            return HTNOperatorStatus.Failed;
        }

        if (ammo.IsCompatibleMagazine(gun, item) && ammo.GetAmmoCount(item) > 0)
            ammo.TryInsertMagazine(owner, gun, item);
        else if (!ammo.IsMagazineFed(gun) && ammo.IsCompatibleCartridgeFor(gun, item))
            ammo.TryFeedOneIntoProvider(owner, gun, gun);

        return HTNOperatorStatus.Finished;
    }
}
