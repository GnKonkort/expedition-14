using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;
using Content.Shared.Hands.EntitySystems;
using Robust.Shared.Map;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;

/// <summary>
/// Plans a nearby compatible ammo/magazine target, or picks it up and stows it into inventory.
/// Collects mags until MinCompatibleMags, non-empty boxes, and tube cartridges up to MinTubeLooseCartridges.
/// </summary>
public sealed partial class PickupCompatibleAmmoOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public string TargetKey = "Target";

    [DataField]
    public bool Pickup;

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard))
            return (false, null);

        if (Pickup)
        {
            if (!blackboard.TryGetValue<EntityUid>(TargetKey, out var existing, _entManager))
                return (false, null);

            return (ammo.GetNearbyAmmoPickupScore(owner, gun, existing) > 0, null);
        }

        // Prefer finishing a fill already in progress over floor thrash.
        if (ammo.HasMagazineFillMaterial(owner, gun))
            return (false, null);

        // Tube still needs rounds and inventory already has them — ReloadHeldGun should win.
        if (ammo.NeedsBallisticTubeFill(owner, gun))
            return (false, null);

        if (!ammo.TrySelectBestNearbyAmmo(owner, gun, ammo.GetAmmoSearchRange(blackboard), out var best, out _))
            return (false, null);

        return (true, new Dictionary<string, object>
        {
            { TargetKey, best },
            { "TargetCoordinates", new EntityCoordinates(best, Vector2.Zero) },
        });
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        if (!Pickup)
            return HTNOperatorStatus.Finished;

        var ammo = _entManager.System<NPCGunAmmoSystem>();
        var hands = _entManager.System<SharedHandsSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!blackboard.TryGetValue<EntityUid>(TargetKey, out var item, _entManager))
        {
            ammo.SetAmmoSearchCooldown(blackboard);
            return HTNOperatorStatus.Failed;
        }

        if (ammo.TryGetOwnedGun(owner, out var ownedGun, out _, blackboard))
            ammo.TryEnsureGunStowedForAmmoWork(owner, blackboard, out ownedGun);

        if (!ammo.TryObtainInHand(owner, item))
        {
            ammo.SetAmmoSearchCooldown(blackboard);
            return HTNOperatorStatus.Failed;
        }

        if (!ammo.TryGetOwnedGun(owner, out var gun, out _, blackboard))
            return HTNOperatorStatus.Finished;

        // Empty matching boxes are discarded, not stored.
        if (ammo.IsCompatibleAmmoBox(gun, item) && ammo.GetAmmoCount(item) <= 0)
        {
            hands.TryDrop(owner, item, checkActionBlocker: false);
            return HTNOperatorStatus.Finished;
        }

        if (ammo.IsCompatibleMagazine(gun, item))
        {
            // Insert only when the gun needs a mag and this one has ammo; otherwise stow for stock.
            if (ammo.NeedsMagazineInsert(owner, gun) && ammo.GetAmmoCount(item) > 0)
            {
                if (!ammo.TryInsertMagazine(owner, gun, item))
                    ammo.TryStowItem(owner, item);
            }
            else
            {
                ammo.TryStowItem(owner, item);
            }
        }
        else if (ammo.IsCompatibleAmmoBox(gun, item))
        {
            if (!ammo.IsMagazineFed(gun) && ammo.IsProviderBelowCapacity(gun))
            {
                if (!ammo.TryTransferOneFromAmmoBox(owner, item, gun))
                    ammo.TryStowItem(owner, item);
            }
            else
            {
                ammo.TryStowItem(owner, item);
            }
        }
        else if (!ammo.IsMagazineFed(gun) && ammo.IsCompatibleCartridgeFor(gun, item))
        {
            // Prefer loading the shell we just picked when the tube still has room.
            // NeedsBallisticTubeFill alone is false until inventory already has a cart — that
            // used to force a stow-first path and left empty shotguns stacking floor loot.
            if (ammo.IsProviderBelowCapacity(gun))
            {
                if (!ammo.TryFeedCartridgeIntoProvider(owner, gun, item) && !ammo.TryStowItem(owner, item))
                    ammo.SetAmmoSearchCooldown(blackboard);
            }
            else if (!ammo.TryStowItem(owner, item))
            {
                // Never drop over-stock shells: drop→reselect causes an infinite PickupAmmo loop
                // once CountLoose oscillates around MinTubeLooseCartridges.
                ammo.SetAmmoSearchCooldown(blackboard);
            }
        }
        else
        {
            ammo.TryStowItem(owner, item);
        }

        ammo.TryDiscardEmptyAmmoBox(owner, gun);
        return HTNOperatorStatus.Finished;
    }
}
