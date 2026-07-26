using Content.Server.NPC.Systems;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared.Weapons.Ranged.Events;

namespace Content.Server.NPC.HTN.Preconditions;

/// <summary>
/// Gets ammo percent for this NPC's held gun (any hand), falling back to active-hand / innate gun.
/// </summary>
public sealed partial class GunAmmoPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField("minPercent")]
    public float MinPercent = 0f;

    [DataField("maxPercent")]
    public float MaxPercent = 1f;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var ammoSys = _entManager.System<NPCGunAmmoSystem>();
        var gunSystem = _entManager.System<GunSystem>();

        EntityUid gunUid;
        if (!ammoSys.TryGetOwnedGun(owner, out gunUid, out _, blackboard) &&
            !gunSystem.TryGetGun(owner, out gunUid, out _))
        {
            ammoSys.DebugAmmo(owner, "GunAmmoPrecondition: no owned/active gun");
            return false;
        }

        var ammoEv = new GetAmmoCountEvent();
        _entManager.EventBus.RaiseLocalEvent(gunUid, ref ammoEv);
        float percent;

        if (ammoEv.Capacity == 0)
            percent = 0f;
        else
            percent = ammoEv.Count / (float) ammoEv.Capacity;

        percent = System.Math.Clamp(percent, 0f, 1f);

        var met = percent <= MaxPercent && percent >= MinPercent;
        if (percent <= 0.05f || MaxPercent <= 0.05f || MinPercent >= 0.001f)
        {
            ammoSys.DebugAmmo(owner,
                $"GunAmmoPrecondition gun={_entManager.ToPrettyString(gunUid)} {ammoEv.Count}/{ammoEv.Capacity} percent={percent:F3} min={MinPercent} max={MaxPercent} => {met}");
        }

        return met;
    }
}
