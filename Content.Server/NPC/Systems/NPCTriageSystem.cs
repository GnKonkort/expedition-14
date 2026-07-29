using Content.Server.NPC.HTN;
using Content.Shared.Damage;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Systems;

namespace Content.Server.NPC.Systems;

/// <summary>
/// Medic triage: pick the most critical faction ally (dead → crit → high damage).
/// Cadence: plan-time. Reuses medical heal compounds after setting HealTarget.
/// </summary>
public sealed class NPCTriageSystem : EntitySystem
{
    public const float DefaultTriageRange = 10f;

    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly NpcFactionSystem _faction = default!;
    [Dependency] private readonly NPCMedicalSystem _medical = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;

    private EntityQuery<DamageableComponent> _damageQuery;
    private EntityQuery<MobStateComponent> _mobQuery;

    public override void Initialize()
    {
        base.Initialize();
        _damageQuery = GetEntityQuery<DamageableComponent>();
        _mobQuery = GetEntityQuery<MobStateComponent>();
    }

    /// <summary>
    /// Score: dead needing kits/defib > crit > soft damage. Higher = more urgent.
    /// </summary>
    public float ScorePatient(EntityUid healer, EntityUid patient)
    {
        if (!_mobQuery.HasComponent(patient) || !_damageQuery.TryGetComponent(patient, out var damage))
            return 0f;

        if (_mobState.IsDead(patient))
            return 1000f + damage.TotalDamage.Float();

        if (_mobState.IsCritical(patient))
            return 500f + damage.TotalDamage.Float();

        var treatable = _medical.GetOwnedTreatableDamage(healer, patient);
        if (treatable <= NPCMedicalSystem.TreatableDamageThreshold)
            return 0f;

        return treatable;
    }

    public bool TrySelectTriagePatient(EntityUid healer, NPCBlackboard blackboard, float range = DefaultTriageRange)
    {
        if (!TryComp(healer, out TransformComponent? xform))
            return false;

        EntityUid? best = null;
        var bestScore = 0f;
        var mapCoords = _transform.GetMapCoordinates(healer, xform);

        var selfScore = ScorePatient(healer, healer);
        if (selfScore > bestScore)
        {
            bestScore = selfScore;
            best = healer;
        }

        foreach (var ent in _lookup.GetEntitiesInRange<MobStateComponent>(mapCoords, range))
        {
            if (ent.Owner == healer)
                continue;

            if (!_faction.IsEntityFriendly(healer, ent.Owner))
                continue;

            var score = ScorePatient(healer, ent.Owner);
            if (score <= bestScore)
                continue;

            bestScore = score;
            best = ent.Owner;
        }

        if (best == null || bestScore <= 0f)
            return false;

        if (!_medical.HasOwnedMedTools(healer) &&
            (best == healer || !_medical.CanSafelyDefibAlly(healer, best.Value)))
            return false;

        blackboard.SetValue(NPCBlackboard.HealTarget, best.Value);
        blackboard.SetValue(NPCBlackboard.Target, best.Value);
        if (TryComp(best.Value, out TransformComponent? tx))
            blackboard.SetValue(NPCBlackboard.TargetCoordinates, tx.Coordinates);
        return true;
    }
}
