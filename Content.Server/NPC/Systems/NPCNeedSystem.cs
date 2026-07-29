using Content.Server.Atmos.EntitySystems;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Shared.Atmos;
using Content.Shared.CCVar;
using Content.Shared.NPC;
using Content.Shared.NPC.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Timing;

namespace Content.Server.NPC.Systems;

/// <summary>
/// Cheap Need Board refresh (TTL) → blackboard flags for HTN priority gating.
/// Cadence: ~0.4s per awake HTN NPC. Worst-case: 1 atmos mixture + short faction probe.
/// </summary>
public sealed class NPCNeedSystem : EntitySystem
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(0.4);

    [Dependency] private readonly AtmosphereSystem _atmos = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly NpcFactionSystem _faction = default!;
    [Dependency] private readonly NPCGunAmmoSystem _ammo = default!;
    [Dependency] private readonly NPCMedicalSystem _medical = default!;
    [Dependency] private readonly NPCAccessBypassSystem _doors = default!;

    private bool _debugAtmos;
    private EntityQuery<TransformComponent> _xformQuery;

    public override void Initialize()
    {
        base.Initialize();
        _xformQuery = GetEntityQuery<TransformComponent>();
        Subs.CVar(_cfg, CCVars.NPCDebugAtmos, v => _debugAtmos = v, true);
    }

    public override void Update(float frameTime)
    {
        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<ActiveNPCComponent, HTNComponent, NPCNeedComponent>();

        while (query.MoveNext(out var uid, out _, out var htn, out var needs))
        {
            if (needs.NextRefresh > now)
            {
                WriteBlackboard(htn, needs);
                continue;
            }

            Refresh(uid, htn, needs, now);
            WriteBlackboard(htn, needs);
        }
    }

    private void Refresh(EntityUid uid, HTNComponent htn, NPCNeedComponent needs, TimeSpan now)
    {
        needs.NextRefresh = now + RefreshInterval;

        needs.InVacuum = false;
        needs.LowPressure = false;
        needs.SeekAtmosphere = false;

        GasMixture? mixture = null;
        if (_xformQuery.TryGetComponent(uid, out var xform))
        {
            mixture = _atmos.GetContainingMixture(uid);
            // Null mixture is common briefly / in odd containers — do NOT treat as vacuum.
            // Only flee on confirmed low pressure / hazard.
            if (mixture != null)
            {
                if (mixture.Pressure <= Atmospherics.HazardLowPressure)
                {
                    needs.InVacuum = mixture.Pressure <= 5f;
                    needs.LowPressure = true;
                    needs.SeekAtmosphere = true;
                }
                else if (mixture.Pressure <= Atmospherics.WarningLowPressure)
                {
                    needs.LowPressure = true;
                    needs.SeekAtmosphere = true;
                }
            }
            else if (xform.GridUid == null)
            {
                // Truly in space with no grid air.
                needs.InVacuum = true;
                needs.SeekAtmosphere = true;
            }

            if (_debugAtmos && needs.SeekAtmosphere)
                Log.Info($"NPCAtmos {ToPrettyString(uid)} seekAtmos vacuum={needs.InVacuum} lowP={needs.LowPressure} p={mixture?.Pressure}");
        }

        needs.HasHostile = false;
        var range = htn.Blackboard.GetValueOrDefault<float>("AggroVisionRadius", EntityManager);
        if (range <= 0f)
            range = 12f;
        foreach (var _ in _faction.GetNearbyHostiles(uid, range))
        {
            needs.HasHostile = true;
            break;
        }

        needs.AmmoCritical = false;
        if (_ammo.TryGetOwnedGun(uid, out var gun, out _, htn.Blackboard))
            needs.AmmoCritical = _ammo.NeedsAmmoStock(uid, gun);

        needs.MedStockLow = _medical.IsMedBelowCap(uid);
        needs.DoorBlocked = _doors.HasBlockingDoorNeed(uid, htn.Blackboard);
    }

    private static void WriteBlackboard(HTNComponent htn, NPCNeedComponent needs)
    {
        var bb = htn.Blackboard;
        bb.SetValue(NPCBlackboard.NeedInVacuum, needs.InVacuum);
        bb.SetValue(NPCBlackboard.NeedLowPressure, needs.LowPressure);
        bb.SetValue(NPCBlackboard.NeedHasHostile, needs.HasHostile);
        bb.SetValue(NPCBlackboard.NeedAmmoCritical, needs.AmmoCritical);
        bb.SetValue(NPCBlackboard.NeedMedStockLow, needs.MedStockLow);
        bb.SetValue(NPCBlackboard.NeedSeekAtmosphere, needs.SeekAtmosphere);
        bb.SetValue(NPCBlackboard.NeedDoorBlocked, needs.DoorBlocked);
    }
}
