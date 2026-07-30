using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
using Content.Server.Atmos.Rotting;
using Content.Server.Medical;
using Content.Shared.Body.Components;
using Content.Shared.Body.Systems;
using Content.Shared.Cabinet;
using Content.Shared.CCVar;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Damage;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Medical;
using Content.Shared.Medical.Healing;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC;
using Content.Shared.NPC.Systems;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Stacks;
using Content.Shared.Tag;
using Content.Shared.Timing;
using Content.Shared.Traits.Assorted;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.NPC.Systems;

/// <summary>
/// Combat medical for humanoid NPCs: kits, medipens, defibrillators, ally pick.
/// Cadence: HTN-driven (plan + Update). Worst-case: local inventory + short lookup.
/// </summary>
public sealed class NPCMedicalSystem : EntitySystem
{
    public const float TreatableDamageThreshold = 10f;
    /// <summary>Damage removed per illusion heal DoAfter (corpse prep and living allies).</summary>
    public const float IllusionHealAmount = 10f;
    /// <summary>Living allies are healed when TotalDamage exceeds this.</summary>
    public const float IllusionHealThreshold = 10f;
    /// <summary>Fraction of BloodMaxVolume restored per illusion heal.</summary>
    public const float IllusionBloodRestoreFraction = 0.10f;
    public const float DefaultMedSearchRange = 7f;
    public const float DefaultMedLootHostileRange = 4f;
    public const int MaxKitStacks = 2;
    public const int MaxMedipens = 2;
    public const string MedSearchRangeKey = "MedSearchRange";
    public const string HealTargetKey = NPCBlackboard.HealTarget;
    public const string HealItemKey = NPCBlackboard.HealItem;
    public const string DefibItemKey = NPCBlackboard.DefibItem;
    public const float DefaultDefibAsphyxHeal = 40f;
    public const float DefaultDefibZapDamage = 5f;
    /// <summary>After defib without a stabilizer pen, projected damage must stay below this to avoid waking into crit.</summary>
    public const float SoftReviveDamageCap = 95f;
    /// <summary>Tricordrazine injected once when a heal course finishes.</summary>
    public const float FinishingTricordrazineUnits = 15f;
    private static readonly ProtoId<ReagentPrototype> TricordrazineReagent = "Tricordrazine";
    private static readonly SoundSpecifier HyposprayInjectSound =
        new SoundPathSpecifier("/Audio/Items/hypospray.ogg");
    public static readonly TimeSpan MedipenInjectCooldown = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefibRetryCooldown = TimeSpan.FromSeconds(12);

    private static readonly ProtoId<TagPrototype> GauzeTag = "Gauze";
    private static readonly ProtoId<TagPrototype> BrutepackTag = "Brutepack";
    private static readonly ProtoId<TagPrototype> OintmentTag = "Ointment";

    /// <summary>
    /// Medipen prototype → damage types they are assumed to treat (chem whitelist, not full sim).
    /// CritAllyOnly pens (Emergency) only score on critical faction allies — never self / non-crit.
    /// </summary>
    private static readonly Dictionary<string, MedipenProfile> MedipenProfiles = new(StringComparer.Ordinal)
    {
        ["BruteAutoInjector"] = new(new[] { "Blunt", "Slash", "Piercing" }, true, 40),
        ["BruizAutoInjector"] = new(new[] { "Blunt" }, false, 35),
        ["LacerAutoInjector"] = new(new[] { "Slash" }, false, 35),
        ["PunctAutoInjector"] = new(new[] { "Piercing" }, false, 35),
        ["BurnAutoInjector"] = new(new[] { "Heat", "Cold", "Shock", "Caustic" }, false, 40),
        ["PyraAutoInjector"] = new(new[] { "Heat", "Cold", "Shock", "Caustic" }, false, 35),
        ["RadAutoInjector"] = new(new[] { "Radiation" }, false, 30),
        ["AntiPoisonMedipen"] = new(new[] { "Poison" }, false, 30),
        ["AirlossAutoInjector"] = new(new[] { "Asphyxiation" }, false, 25),
        ["HemostasisAutoInjector"] = new(Array.Empty<string>(), true, 45),
        ["EmergencyMedipen"] = new(Array.Empty<string>(), true, 100, CritAllyOnly: true),
        ["CombatMedipen"] = new(new[] { "Blunt", "Slash", "Piercing", "Heat", "Cold", "Shock", "Caustic" }, true, 50),
    };

    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedBloodstreamSystem _bloodstream = default!;
    [Dependency] private readonly DefibrillatorSystem _defib = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly HypospraySystem _hypospray = default!;
    [Dependency] private readonly ItemCabinetSystem _itemCabinet = default!;
    [Dependency] private readonly ItemToggleSystem _itemToggle = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly MobThresholdSystem _mobThreshold = default!;
    [Dependency] private readonly NpcFactionSystem _faction = default!;
    [Dependency] private readonly NPCGunAmmoSystem _ammo = default!;
    [Dependency] private readonly OpenableSystem _openable = default!;
    [Dependency] private readonly RottingSystem _rotting = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly TagSystem _tag = default!;
    [Dependency] private readonly UseDelaySystem _useDelay = default!;

    private EntityQuery<DamageableComponent> _damageableQuery;
    private EntityQuery<DefibrillatorComponent> _defibQuery;
    private EntityQuery<HealingComponent> _healingQuery;
    private EntityQuery<HyposprayComponent> _hypoQuery;
    private EntityQuery<ItemCabinetComponent> _cabinetQuery;
    private EntityQuery<MetaDataComponent> _metaQuery;
    private EntityQuery<MobStateComponent> _mobQuery;
    private EntityQuery<StackComponent> _stackQuery;
    private EntityQuery<TransformComponent> _xformQuery;
    private bool _debug;

    /// <summary>Healer → earliest time another medipen may be injected (overdose guard).</summary>
    private readonly Dictionary<EntityUid, TimeSpan> _medipenCooldownUntil = new();

    /// <summary>Healer+patient → earliest time another defib attempt is allowed after a failed revive.</summary>
    private readonly Dictionary<(EntityUid Healer, EntityUid Patient), TimeSpan> _defibRetryUntil = new();

    public override void Initialize()
    {
        base.Initialize();
        _damageableQuery = GetEntityQuery<DamageableComponent>();
        _defibQuery = GetEntityQuery<DefibrillatorComponent>();
        _healingQuery = GetEntityQuery<HealingComponent>();
        _hypoQuery = GetEntityQuery<HyposprayComponent>();
        _cabinetQuery = GetEntityQuery<ItemCabinetComponent>();
        _metaQuery = GetEntityQuery<MetaDataComponent>();
        _mobQuery = GetEntityQuery<MobStateComponent>();
        _stackQuery = GetEntityQuery<StackComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();

        _debug = _cfg.GetCVar(CCVars.NPCDebugMedical);
        Subs.CVar(_cfg, CCVars.NPCDebugMedical, v =>
        {
            _debug = v;
            Log.Info($"[npc.medical] npc.debug_medical={(v ? "ON" : "OFF")}");
        });

        SubscribeLocalEvent<NpcIllusionHealDoAfterEvent>(OnIllusionHealDoAfter);
    }

    private void OnIllusionHealDoAfter(NpcIllusionHealDoAfterEvent args)
    {
        if (args.Cancelled)
            return;

        if (args.Target is not { } patient || !Exists(patient))
            return;

        var healer = args.User;
        TryApplyIllusionHeal(healer, patient);

        // End of course: one hypospray of tricordrazine when the patient no longer needs heals.
        if (IsRevivableCorpse(healer, patient))
        {
            if (IsCorpseReadyForDefib(healer, patient))
                TryInjectFinishingTricordrazine(healer, patient);
        }
        else if (IsValidPatient(patient) && !NeedsLivingHeal(healer, patient))
        {
            TryInjectFinishingTricordrazine(healer, patient);
        }
    }

    public void Debug(EntityUid owner, string message)
    {
        if (!_debug)
            return;

        Log.Info($"[npc.medical] {ToPrettyString(owner)} {message}");
    }

    public float GetMedSearchRange(NPCBlackboard blackboard)
    {
        var range = blackboard.GetValueOrDefault<float>(MedSearchRangeKey, EntityManager);
        return range > 0f ? range : DefaultMedSearchRange;
    }

    public bool IsValidPatient(EntityUid patient, bool allowDead = false)
    {
        if (!Exists(patient) || !_damageableQuery.HasComponent(patient))
            return false;

        if (_mobQuery.TryGetComponent(patient, out var mob) && _mobState.IsDead(patient, mob) && !allowDead)
            return false;

        return true;
    }

    /// <summary>
    /// Friendly dead ally that is not rotten / unrevivable.
    /// </summary>
    public bool IsRevivableCorpse(EntityUid healer, EntityUid patient)
    {
        if (!Exists(patient) || patient == healer)
            return false;

        if (!_damageableQuery.HasComponent(patient) || !_mobQuery.TryGetComponent(patient, out var mob))
            return false;

        if (!_mobState.IsDead(patient, mob))
            return false;

        if (!_faction.IsEntityFriendly(healer, patient))
            return false;

        if (_rotting.IsRotten(patient) || HasComp<UnrevivableComponent>(patient))
            return false;

        return true;
    }

    /// <summary>
    /// True when projected damage after defib ZapHeal (minus electrocution ZapDamage) would fall below the dead threshold.
    /// </summary>
    public bool IsDefibReady(EntityUid patient, EntityUid? defib = null)
    {
        if (!_damageableQuery.TryGetComponent(patient, out var damageable))
            return false;

        if (!_mobThreshold.TryGetThresholdForState(patient, MobState.Dead, out var threshold))
            return false;

        var projected = ProjectDamageAfterDefibZap(damageable, defib);
        return projected < threshold;
    }

    /// <summary>
    /// True when projected post-defib damage is low enough that the patient wakes Alive (not critical).
    /// Uses <see cref="SoftReviveDamageCap"/> (95), capped by the patient's Critical threshold if present.
    /// </summary>
    public bool IsSoftReviveReady(EntityUid patient, EntityUid? defib = null)
    {
        if (!_damageableQuery.TryGetComponent(patient, out var damageable))
            return false;

        var cap = FixedPoint2.New(SoftReviveDamageCap);
        if (_mobThreshold.TryGetThresholdForState(patient, MobState.Critical, out var crit) && crit != null)
            cap = FixedPoint2.Min(cap, crit.Value);

        var projected = ProjectDamageAfterDefibZap(damageable, defib);
        return projected < cap;
    }

    /// <summary>
    /// True when the healer owns an Emergency medipen or a medipen that matches the patient's damage,
    /// so a revive into critical can be stabilized without stacking pens.
    /// </summary>
    public bool HasPostReviveStabilizer(EntityUid healer, EntityUid patient)
    {
        foreach (var candidate in _ammo.EnumerateInventoryAmmoCandidates(healer))
        {
            if (!TryGetToolInfo(candidate, out var info) || !info.IsMedipen)
                continue;

            // Emergency / CritAllyOnly: usable once the corpse wakes into critical.
            if (info.CritAllyOnly)
                return true;

            // Damage-matching pens score against current corpse trauma (same types after revive).
            if (ToolHelpsPatient(healer, patient, info))
                return true;
        }

        return false;
    }

    public bool IsDefibOnUseDelay(EntityUid defib)
    {
        if (!_defibQuery.TryGetComponent(defib, out var comp))
            return false;

        return _useDelay.IsDelayed(defib, comp.DelayId);
    }

    public bool IsDefibRetryBlocked(EntityUid healer, EntityUid patient)
    {
        if (!_defibRetryUntil.TryGetValue((healer, patient), out var until))
            return false;

        if (_timing.CurTime >= until)
        {
            _defibRetryUntil.Remove((healer, patient));
            return false;
        }

        return true;
    }

    public void MarkDefibAttempt(EntityUid healer, EntityUid patient)
    {
        _defibRetryUntil[(healer, patient)] = _timing.CurTime + DefibRetryCooldown;
    }

    public bool CanInjectMedipen(EntityUid healer)
    {
        if (!_medipenCooldownUntil.TryGetValue(healer, out var until))
            return true;

        if (_timing.CurTime >= until)
        {
            _medipenCooldownUntil.Remove(healer);
            return true;
        }

        return false;
    }

    public void MarkMedipenInjected(EntityUid healer)
    {
        _medipenCooldownUntil[healer] = _timing.CurTime + MedipenInjectCooldown;
    }

    /// <summary>
    /// Corpse is healed enough that a defib zap wakes them without waking into deep trauma.
    /// </summary>
    public bool IsCorpseReadyForDefib(EntityUid healer, EntityUid patient, EntityUid? defib = null)
    {
        if (!IsRevivableCorpse(healer, patient))
            return false;

        TryGetOwnedDefib(healer, out var owned);
        return IsSoftReviveReady(patient, defib ?? owned);
    }

    /// <summary>
    /// Medic for now: anyone carrying a defibrillator. Later: squad medic role.
    /// </summary>
    public bool IsMedic(EntityUid owner)
    {
        return TryGetOwnedDefib(owner, out _);
    }

    /// <summary>
    /// Corpse can be zapped: medic owns defib, corpse healed to soft-revive, not on cooldown.
    /// </summary>
    public bool CanSafelyDefibAlly(EntityUid healer, EntityUid patient)
    {
        if (!IsCorpseReadyForDefib(healer, patient))
            return false;

        if (IsDefibRetryBlocked(healer, patient))
            return false;

        if (!TryGetOwnedDefib(healer, out var ownedDefib) || ownedDefib == null)
            return false;

        if (IsDefibOnUseDelay(ownedDefib.Value))
            return false;

        return true;
    }

    /// <summary>
    /// Friendly corpse that still needs incremental heals before defibrillation.
    /// </summary>
    public bool NeedsCorpseHeal(EntityUid healer, EntityUid patient)
    {
        if (!IsMedic(healer) || !IsRevivableCorpse(healer, patient))
            return false;

        TryGetOwnedDefib(healer, out var owned);
        return !IsSoftReviveReady(patient, owned);
    }

    /// <summary>
    /// Living faction ally with more than <see cref="IllusionHealThreshold"/> total damage.
    /// </summary>
    public bool NeedsLivingHeal(EntityUid healer, EntityUid patient)
    {
        if (!IsMedic(healer) || patient == healer)
            return false;

        if (!Exists(patient) || !_faction.IsEntityFriendly(healer, patient))
            return false;

        if (!IsValidPatient(patient, allowDead: false))
            return false;

        if (!_damageableQuery.TryGetComponent(patient, out var damageable))
            return false;

        return damageable.TotalDamage.Float() > IllusionHealThreshold;
    }

    /// <summary>
    /// Remove up to <see cref="IllusionHealAmount"/> damage, stop bleeding, restore 10% blood.
    /// </summary>
    public bool TryApplyIllusionHeal(EntityUid healer, EntityUid patient)
    {
        var applied = false;

        if (_damageableQuery.TryGetComponent(patient, out var damageable) && damageable.TotalDamage > 0)
        {
            var total = damageable.TotalDamage;
            var toHeal = FixedPoint2.Min(FixedPoint2.New(IllusionHealAmount), total);
            var heal = new DamageSpecifier();
            foreach (var (type, dmg) in damageable.Damage.DamageDict)
            {
                if (dmg <= 0)
                    continue;

                heal.DamageDict[type] = -(dmg / total) * toHeal;
            }

            _damageable.TryChangeDamage(patient, heal, ignoreResistances: true, interruptsDoAfters: false);
            applied = true;
            Debug(healer, $"IllusionHeal damage {ToPrettyString(patient)} -{toHeal}");
        }

        if (TryComp<BloodstreamComponent>(patient, out var blood))
        {
            if (blood.BleedAmount > 0)
            {
                _bloodstream.TryModifyBleedAmount((patient, blood), -blood.BleedAmount);
                applied = true;
                Debug(healer, $"IllusionHeal stopped bleed {ToPrettyString(patient)}");
            }

            var restore = blood.BloodMaxVolume * IllusionBloodRestoreFraction;
            if (restore > 0 && _bloodstream.TryModifyBloodLevel((patient, blood), restore))
            {
                applied = true;
                Debug(healer, $"IllusionHeal blood +{restore} ({IllusionBloodRestoreFraction:P0}) {ToPrettyString(patient)}");
            }
        }

        return applied;
    }

    /// <summary>
    /// One finishing hypospray: 15u tricordrazine + inject SFX (illusion med).
    /// </summary>
    public bool TryInjectFinishingTricordrazine(EntityUid healer, EntityUid patient)
    {
        if (!TryComp<BloodstreamComponent>(patient, out var blood))
            return false;

        var solution = new Solution(TricordrazineReagent, FixedPoint2.New(FinishingTricordrazineUnits));
        if (!_bloodstream.TryAddToChemicals((patient, blood), solution))
        {
            Debug(healer, $"FinishingTricord FAIL add {ToPrettyString(patient)}");
            return false;
        }

        try
        {
            _audio.PlayPvs(HyposprayInjectSound, patient);
        }
        catch (FileNotFoundException)
        {
        }

        Debug(healer, $"FinishingTricord {FinishingTricordrazineUnits}u → {ToPrettyString(patient)}");
        return true;
    }

    /// <summary>
    /// Put any held defibrillator back into storage when it is not mid-zap.
    /// </summary>
    public bool TryStowHeldDefib(EntityUid owner)
    {
        if (IsDefibDoAfterRunning(owner))
            return false;

        var stowed = false;
        foreach (var held in _hands.EnumerateHeld(owner).ToList())
        {
            if (!_defibQuery.HasComponent(held))
                continue;

            if (_itemToggle.IsActivated(held))
                _itemToggle.TryDeactivate(held, owner);

            if (_ammo.TryStowItem(owner, held))
            {
                stowed = true;
                Debug(owner, $"StowDefib {ToPrettyString(held)}");
            }
            else
            {
                Debug(owner, $"StowDefib FAIL bag full {ToPrettyString(held)}");
            }
        }

        return stowed;
    }

    public bool IsIllusionHealDoAfterRunning(EntityUid healer)
    {
        if (!TryComp<DoAfterComponent>(healer, out var comp))
            return false;

        foreach (var doAfter in comp.DoAfters.Values)
        {
            if (doAfter.Cancelled || doAfter.Completed)
                continue;

            if (doAfter.Args.Event is NpcIllusionHealDoAfterEvent)
                return true;
        }

        return false;
    }

    /// <summary>Legacy name — corpse prep heals.</summary>
    public bool NeedsCorpseKitHeal(EntityUid healer, EntityUid patient)
    {
        return NeedsCorpseHeal(healer, patient);
    }

    /// <summary>
    /// True when a nearby friendly corpse can be revived with an owned defib (healed or still needs prep).
    /// </summary>
    public bool CanAttemptAllyRevive(EntityUid healer, float range)
    {
        if (!IsMedic(healer))
            return false;

        if (!TryPickRevivableCorpse(healer, range, out var corpse))
            return false;

        return CanSafelyDefibAlly(healer, corpse.Value) || NeedsCorpseHeal(healer, corpse.Value);
    }

    public bool TryPickCorpseNeedingHeal(EntityUid healer, float range, [NotNullWhen(true)] out EntityUid? corpse)
    {
        corpse = null;
        if (!IsMedic(healer) || !_xformQuery.TryGetComponent(healer, out var xform))
            return false;

        var mapCoords = _transform.GetMapCoordinates(healer, xform: xform);
        EntityUid? best = null;
        var bestDamage = 0f;

        foreach (var ent in _lookup.GetEntitiesInRange(mapCoords, range))
        {
            if (!NeedsCorpseHeal(healer, ent))
                continue;

            if (!_damageableQuery.TryGetComponent(ent, out var damageable))
                continue;

            var dmg = damageable.TotalDamage.Float();
            if (dmg <= bestDamage)
                continue;

            bestDamage = dmg;
            best = ent;
        }

        if (best == null)
            return false;

        corpse = best;
        Debug(healer, $"PickCorpseHeal {ToPrettyString(best.Value)} damage={bestDamage:F1}");
        return true;
    }

    public bool TryPickRevivableCorpse(EntityUid healer, float range, [NotNullWhen(true)] out EntityUid? corpse)
    {
        corpse = null;
        if (!_xformQuery.TryGetComponent(healer, out var xform))
            return false;

        var mapCoords = _transform.GetMapCoordinates(healer, xform: xform);
        EntityUid? best = null;
        var bestScore = float.MinValue;
        TryGetOwnedDefib(healer, out var ownedDefib);

        if (!HasDefibAccess(healer, range))
            return false;

        foreach (var ent in _lookup.GetEntitiesInRange(mapCoords, range))
        {
            if (!IsRevivableCorpse(healer, ent))
                continue;

            var score = 0f;
            if (_damageableQuery.TryGetComponent(ent, out var damageable))
                score += (float)damageable.TotalDamage.Float();

            // Ready to zap now (soft revive without pen, or crit+pen path).
            if (!IsCorpseReadyForDefib(healer, ent, ownedDefib))
                continue;

            if (IsDefibRetryBlocked(healer, ent))
                continue;
            if (ownedDefib != null && IsDefibOnUseDelay(ownedDefib.Value))
                continue;

            score += 1000f;

            if (_xformQuery.TryGetComponent(ent, out var entXform))
            {
                var entMap = _transform.GetMapCoordinates(ent, xform: entXform);
                score -= (entMap.Position - mapCoords.Position).Length();
            }

            if (score <= bestScore)
                continue;

            bestScore = score;
            best = ent;
        }

        if (best == null)
            return false;

        corpse = best;
        Debug(healer, $"PickCorpse {ToPrettyString(best.Value)} score={bestScore:F1}");
        return true;
    }

    /// <summary>
    /// Treatable damage using the healer's currently owned tools only.
    /// </summary>
    public float GetOwnedTreatableDamage(EntityUid healer, EntityUid patient)
    {
        var tools = CollectOwnedTools(healer);
        return GetTreatableDamage(healer, patient, tools);
    }

    /// <summary>
    /// True while the healer has an active <see cref="HealingDoAfterEvent"/> (kit bandage etc.).
    /// </summary>
    public bool TryGetActiveHealingDoAfter(EntityUid healer, out ushort id)
    {
        id = 0;
        if (!TryComp<DoAfterComponent>(healer, out var comp))
            return false;

        foreach (var (index, doAfter) in comp.DoAfters)
        {
            if (doAfter.Cancelled || doAfter.Completed)
                continue;

            if (doAfter.Args.Event is HealingDoAfterEvent)
            {
                id = index;
                return true;
            }
        }

        return false;
    }

    public bool IsHealingDoAfterRunning(EntityUid healer)
    {
        return TryGetActiveHealingDoAfter(healer, out _);
    }

    public bool TryGetActiveDefibDoAfter(EntityUid healer, out ushort id)
    {
        id = 0;
        if (!TryComp<DoAfterComponent>(healer, out var comp))
            return false;

        foreach (var (index, doAfter) in comp.DoAfters)
        {
            if (doAfter.Cancelled || doAfter.Completed)
                continue;

            if (doAfter.Args.Event is DefibrillatorZapDoAfterEvent)
            {
                id = index;
                return true;
            }
        }

        return false;
    }

    public bool IsDefibDoAfterRunning(EntityUid healer)
    {
        return TryGetActiveDefibDoAfter(healer, out _);
    }

    public bool NeedsHeal(EntityUid healer, EntityUid patient, bool allowDead = false)
    {
        if (!IsValidPatient(patient, allowDead))
            return false;

        // Corpses: only care about kit damage that still blocks defibrillation.
        if (allowDead && _mobState.IsDead(patient))
            return NeedsCorpseKitHeal(healer, patient);

        var amount = GetOwnedTreatableDamage(healer, patient);
        var needs = amount > TreatableDamageThreshold;
        if (needs)
            Debug(healer, $"NeedsHeal patient={ToPrettyString(patient)} treatable={amount:F1} threshold={TreatableDamageThreshold} => True");
        return needs;
    }

    /// <summary>
    /// Treatable damage if we also consider nearby ground meds (for loot decisions).
    /// </summary>
    public float GetAvailableTreatableDamage(EntityUid healer, EntityUid patient, float searchRange)
    {
        var tools = CollectOwnedTools(healer);
        AppendNearbyTools(healer, searchRange, tools);
        return GetTreatableDamage(healer, patient, tools);
    }

    public bool TrySelectBestOwnedHeal(
        EntityUid healer,
        EntityUid patient,
        bool kits,
        bool medipens,
        [NotNullWhen(true)] out EntityUid? item,
        bool critAllyPensOnly = false,
        bool allowDead = false)
    {
        item = null;
        if (!IsValidPatient(patient, allowDead))
            return false;

        // Chemicals do nothing on corpses — never select medipens for dead patients.
        if (_mobState.IsDead(patient))
            medipens = false;

        // One medipen at a time — avoid stacking / overdose.
        if (medipens && !CanInjectMedipen(healer))
            medipens = false;

        if (!kits && !medipens)
        {
            Debug(healer, $"SelectHeal none kits={kits} pens={medipens} critAllyOnly={critAllyPensOnly} allowDead={allowDead} patient={ToPrettyString(patient)}");
            return false;
        }

        EntityUid? best = null;
        var bestScore = float.MinValue;

        foreach (var candidate in _ammo.EnumerateInventoryAmmoCandidates(healer))
        {
            if (!TryGetToolInfo(candidate, out var info))
                continue;

            if (info.IsKit && !kits)
                continue;
            if (info.IsMedipen && !medipens)
                continue;
            if (critAllyPensOnly && !info.CritAllyOnly)
                continue;
            if (!ToolHelpsPatient(healer, patient, info))
                continue;

            var score = ScoreToolForPatient(healer, patient, info);
            if (score <= bestScore)
                continue;

            bestScore = score;
            best = candidate;
        }

        if (best == null)
        {
            Debug(healer, $"SelectHeal none kits={kits} pens={medipens} critAllyOnly={critAllyPensOnly} allowDead={allowDead} patient={ToPrettyString(patient)}");
            return false;
        }

        item = best;
        Debug(healer, $"SelectHeal best={ToPrettyString(best.Value)} score={bestScore:F1} kits={kits} pens={medipens} critAllyOnly={critAllyPensOnly} allowDead={allowDead} patient={ToPrettyString(patient)}");
        return true;
    }

    /// <summary>
    /// Prefer critical allies when the healer owns an emergency-style pen; otherwise worst treatable ally.
    /// </summary>
    public bool TryPickHealAlly(EntityUid healer, float range, [NotNullWhen(true)] out EntityUid? ally, bool critOnly = false)
    {
        ally = null;
        if (!IsMedic(healer))
            return false;

        if (!_xformQuery.TryGetComponent(healer, out var xform))
            return false;

        var mapCoords = _transform.GetMapCoordinates(healer, xform: xform);
        EntityUid? best = null;
        var bestDamage = 0f;

        foreach (var ent in _lookup.GetEntitiesInRange(mapCoords, range))
        {
            if (!NeedsLivingHeal(healer, ent))
                continue;

            if (critOnly && !_mobState.IsCritical(ent))
                continue;

            if (!_damageableQuery.TryGetComponent(ent, out var damageable))
                continue;

            var dmg = damageable.TotalDamage.Float();
            if (dmg <= bestDamage)
                continue;

            bestDamage = dmg;
            best = ent;
        }

        if (best == null)
            return false;

        ally = best;
        Debug(healer, $"PickAlly {ToPrettyString(best.Value)} damage={bestDamage:F1} critOnly={critOnly}");
        return true;
    }

    /// <summary>
    /// Cheap stock-cap check for Need Board (no world lookups).
    /// </summary>
    public bool IsMedBelowCap(EntityUid owner)
    {
        CountOwnedMeds(owner, out var kits, out var pens);
        GetMedCaps(owner, out var maxKits, out var maxPens);
        return kits < maxKits || pens < maxPens;
    }

    public void GetMedCaps(EntityUid owner, out int maxKits, out int maxPens)
    {
        // Caps kept for leftover heal helpers; illusion medic does not stock kits/pens via AI.
        maxKits = MaxKitStacks;
        maxPens = MaxMedipens;
    }

    public bool HasOwnedMedTools(EntityUid owner, bool kits = true, bool pens = true)
    {
        CountOwnedMeds(owner, out var kitCount, out var penCount);
        return (kits && kitCount > 0) || (pens && penCount > 0);
    }

    public bool TryUseHealItem(EntityUid healer, EntityUid patient, EntityUid item)
    {
        if (!TryGetToolInfo(item, out var info))
            return false;

        // Medipens never work on corpses.
        if (info.IsMedipen && _mobState.IsDead(patient))
        {
            Debug(healer, $"UseHeal FAIL medipen on corpse {ToPrettyString(patient)}");
            return false;
        }

        if (!_ammo.TryObtainInHand(healer, item))
        {
            Debug(healer, $"UseHeal FAIL obtain {ToPrettyString(item)}");
            return false;
        }

        if (info.IsMedipen)
        {
            if (!CanInjectMedipen(healer))
            {
                Debug(healer, $"UseHeal FAIL medipen cooldown → {ToPrettyString(patient)}");
                return false;
            }

            if (!_hypoQuery.TryGetComponent(item, out var hypo))
                return false;

            var ok = _hypospray.TryDoInject((item, hypo), patient, healer);
            Debug(healer, $"UseHeal medipen {ToPrettyString(item)} → {ToPrettyString(patient)} ok={ok}");
            if (ok)
            {
                MarkMedipenInjected(healer);
                if (IsEmptyRecognizedMedipen(item))
                    TryDiscardEmptyMedipen(healer, item);
                else
                    _ammo.TryStowItem(healer, item);
            }

            return ok;
        }

        // Kits: self UseInHand or interact with patient (starts Healing do-after).
        if (patient == healer)
        {
            var ok = _interaction.UseInHandInteraction(healer, item);
            Debug(healer, $"UseHeal kit-self {ToPrettyString(item)} ok={ok}");
            return ok;
        }

        if (!_xformQuery.TryGetComponent(patient, out var patientXform))
            return false;

        _interaction.UserInteraction(healer, patientXform.Coordinates, patient);
        Debug(healer, $"UseHeal kit-ally {ToPrettyString(item)} → {ToPrettyString(patient)}");
        return true;
    }

    public void TryStowHealItem(EntityUid healer, EntityUid item)
    {
        if (_hands.IsHolding(healer, item))
            _ammo.TryStowItem(healer, item);
    }

    public bool HasDefibAccess(EntityUid healer, float range)
    {
        if (TryGetOwnedDefib(healer, out _))
            return true;

        return TrySelectNearbyDefibSource(healer, range, out _, out _);
    }

    public bool TryGetOwnedDefib(EntityUid owner, out EntityUid? defib)
    {
        defib = null;
        foreach (var item in _ammo.EnumerateInventoryAmmoCandidates(owner))
        {
            if (!_defibQuery.HasComponent(item))
                continue;

            defib = item;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Selects a floor defibrillator or a wall cabinet that still contains one.
    /// </summary>
    public bool TrySelectNearbyDefibSource(
        EntityUid owner,
        float range,
        [NotNullWhen(true)] out EntityUid? source,
        out bool isCabinet)
    {
        source = null;
        isCabinet = false;
        if (!_xformQuery.TryGetComponent(owner, out var xform))
            return false;

        if (HasNearbyHostile(owner, DefaultMedLootHostileRange))
        {
            Debug(owner, "NearbyDefib blocked by hostile");
            return false;
        }

        var mapCoords = _transform.GetMapCoordinates(owner, xform: xform);
        EntityUid? best = null;
        var bestScore = float.MinValue;
        var bestCabinet = false;

        foreach (var ent in _lookup.GetEntitiesInRange(mapCoords, range))
        {
            var score = 0f;
            var cabinet = false;

            if (_defibQuery.HasComponent(ent))
            {
                if (_ammo.IsCarriedBy(owner, ent))
                    continue;

                // Prefer loose floor defibs; skip ones still locked in closed cabinets.
                if (_containers.TryGetContainingContainer((ent, null, null), out var container))
                {
                    if (!_cabinetQuery.HasComponent(container.Owner))
                        continue;
                    // Contained in a cabinet — handled via the cabinet entity instead.
                    continue;
                }

                score = 200f;
            }
            else if (_cabinetQuery.TryGetComponent(ent, out var cabComp) &&
                     _itemCabinet.HasItem((ent, cabComp)) &&
                     TryGetCabinetDefib(ent, cabComp, out _))
            {
                cabinet = true;
                score = 150f;
            }
            else
            {
                continue;
            }

            if (_xformQuery.TryGetComponent(ent, out var entXform))
            {
                var entMap = _transform.GetMapCoordinates(ent, xform: entXform);
                score -= (entMap.Position - mapCoords.Position).Length() * 0.5f;
            }

            if (score <= bestScore)
                continue;

            bestScore = score;
            best = ent;
            bestCabinet = cabinet;
        }

        if (best == null)
            return false;

        source = best;
        isCabinet = bestCabinet;
        Debug(owner, $"NearbyDefib best={ToPrettyString(best.Value)} cabinet={bestCabinet} score={bestScore:F1}");
        return true;
    }

    public bool TryObtainDefib(EntityUid owner, EntityUid source, bool isCabinet)
    {
        if (TryGetOwnedDefib(owner, out var owned) && owned != null)
        {
            Debug(owner, $"ObtainDefib already owned {ToPrettyString(owned.Value)}");
            return true;
        }

        TryFreeHandsForLargeItem(owner);

        if (isCabinet)
        {
            if (!_cabinetQuery.TryGetComponent(source, out var cab))
                return false;

            if (_openable.IsClosed(source) && !_openable.TryOpen(source, user: owner))
            {
                Debug(owner, $"ObtainDefib FAIL open cabinet {ToPrettyString(source)}");
                return false;
            }

            if (!TryGetCabinetDefib(source, cab, out var cabinetDefib) || cabinetDefib == null)
            {
                Debug(owner, $"ObtainDefib FAIL empty cabinet {ToPrettyString(source)}");
                return false;
            }

            if (!_ammo.TryObtainInHand(owner, cabinetDefib.Value))
            {
                Debug(owner, $"ObtainDefib FAIL take from cabinet {ToPrettyString(cabinetDefib.Value)}");
                return false;
            }

            Debug(owner, $"ObtainDefib from cabinet {ToPrettyString(source)}");
            return true;
        }

        if (!_defibQuery.HasComponent(source))
            return false;

        if (!_ammo.TryObtainInHand(owner, source))
        {
            Debug(owner, $"ObtainDefib FAIL pickup {ToPrettyString(source)}");
            return false;
        }

        Debug(owner, $"ObtainDefib floor {ToPrettyString(source)}");
        return true;
    }

    public bool TryEnsureDefibPoweredOn(EntityUid owner, EntityUid defib)
    {
        if (!_defibQuery.HasComponent(defib))
            return false;

        if (!_ammo.IsCarriedBy(owner, defib) && !_ammo.TryObtainInHand(owner, defib))
        {
            TryFreeHandsForLargeItem(owner);
            if (!_ammo.TryObtainInHand(owner, defib))
            {
                Debug(owner, $"DefibOn FAIL obtain {ToPrettyString(defib)}");
                return false;
            }
        }

        if (!_hands.IsHolding(owner, defib))
        {
            TryFreeHandsForLargeItem(owner);
            if (!_ammo.TryObtainInHand(owner, defib))
                return false;
        }

        if (!_itemToggle.IsActivated(defib) && !_itemToggle.TryActivate(defib, owner))
        {
            Debug(owner, $"DefibOn FAIL toggle {ToPrettyString(defib)}");
            return false;
        }

        return true;
    }

    public bool TryStartDefibZap(EntityUid owner, EntityUid patient, EntityUid defib)
    {
        if (!IsRevivableCorpse(owner, patient) && !_mobState.IsCritical(patient))
        {
            Debug(owner, $"DefibZap FAIL invalid patient {ToPrettyString(patient)}");
            return false;
        }

        if (IsRevivableCorpse(owner, patient))
        {
            if (!IsCorpseReadyForDefib(owner, patient, defib))
            {
                Debug(owner, $"DefibZap FAIL corpse not healed yet {ToPrettyString(patient)}");
                return false;
            }

            if (IsDefibRetryBlocked(owner, patient))
            {
                Debug(owner, $"DefibZap FAIL retry cooldown {ToPrettyString(patient)}");
                return false;
            }
        }

        if (IsDefibOnUseDelay(defib))
        {
            Debug(owner, $"DefibZap FAIL use-delay {ToPrettyString(defib)}");
            return false;
        }

        if (!TryEnsureDefibPoweredOn(owner, defib))
            return false;

        if (!_defib.CanZap(defib, patient, owner))
        {
            Debug(owner, $"DefibZap FAIL CanZap {ToPrettyString(patient)}");
            // Still block spam while CanZap is false (often the same delay).
            if (IsRevivableCorpse(owner, patient))
                MarkDefibAttempt(owner, patient);
            return false;
        }

        var ok = _defib.TryStartZap(defib, patient, owner);
        Debug(owner, $"DefibZap start {ToPrettyString(defib)} → {ToPrettyString(patient)} ok={ok}");
        if (ok && IsRevivableCorpse(owner, patient))
            MarkDefibAttempt(owner, patient);
        return ok;
    }

    public bool IsEmptyRecognizedMedipen(EntityUid item)
    {
        if (!_hypoQuery.TryGetComponent(item, out var hypo))
            return false;

        if (!_metaQuery.TryGetComponent(item, out var meta) || meta.EntityPrototype is not { } proto)
            return false;

        if (!MedipenProfiles.ContainsKey(proto.ID))
            return false;

        if (!_solutions.TryGetSolution(item, hypo.SolutionName, out _, out var solution))
            return true;

        return solution.Volume <= 0;
    }

    public bool TryFindEmptyMedipen(EntityUid owner, [NotNullWhen(true)] out EntityUid? item)
    {
        item = null;
        foreach (var candidate in _ammo.EnumerateInventoryAmmoCandidates(owner))
        {
            if (!IsEmptyRecognizedMedipen(candidate))
                continue;

            item = candidate;
            return true;
        }

        return false;
    }

    public bool TryDiscardEmptyMedipen(EntityUid owner, EntityUid item)
    {
        if (!IsEmptyRecognizedMedipen(item))
            return false;

        if (!_ammo.IsCarriedBy(owner, item) && !_hands.IsHolding(owner, item))
            return false;

        if (!_hands.IsHolding(owner, item) && !_ammo.TryObtainInHand(owner, item))
        {
            Debug(owner, $"DiscardEmpty FAIL obtain {ToPrettyString(item)}");
            return false;
        }

        var ok = _hands.TryDrop(owner, item, checkActionBlocker: false);
        Debug(owner, $"DiscardEmpty {ToPrettyString(item)} => {ok}");
        return ok;
    }

    private FixedPoint2 ProjectDamageAfterDefibZap(DamageableComponent damageable, EntityUid? defib)
    {
        // Electrocution applies ZapDamage before ZapHeal; both affect TotalDamage vs dead threshold.
        var projected = damageable.TotalDamage + FixedPoint2.New(GetDefibZapDamage(defib));
        var heal = GetDefibAsphyxHealAmount(defib);
        if (heal <= 0)
            return projected;

        if (!damageable.Damage.DamageDict.TryGetValue("Asphyxiation", out var asph) || asph <= 0)
            return projected;

        return projected - FixedPoint2.Min(asph, FixedPoint2.New(heal));
    }

    private float GetDefibAsphyxHealAmount(EntityUid? defib)
    {
        if (defib != null &&
            _defibQuery.TryGetComponent(defib.Value, out var comp) &&
            comp.ZapHeal.DamageDict.TryGetValue("Asphyxiation", out var amount))
        {
            return Math.Abs((float)amount.Float());
        }

        return DefaultDefibAsphyxHeal;
    }

    private float GetDefibZapDamage(EntityUid? defib)
    {
        if (defib != null && _defibQuery.TryGetComponent(defib.Value, out var comp))
            return comp.ZapDamage;

        return DefaultDefibZapDamage;
    }

    private bool TryGetCabinetDefib(EntityUid cabinet, ItemCabinetComponent cab, [NotNullWhen(true)] out EntityUid? defib)
    {
        defib = null;
        if (!_itemCabinet.TryGetSlot((cabinet, cab), out var slot) || slot.Item is not { } item)
            return false;

        if (!_defibQuery.HasComponent(item))
            return false;

        defib = item;
        return true;
    }

    private void TryFreeHandsForLargeItem(EntityUid owner)
    {
        foreach (var held in _hands.EnumerateHeld(owner).ToList())
        {
            if (HasComp<VirtualItemComponent>(held))
                continue;

            if (_defibQuery.HasComponent(held))
                continue;

            _ammo.TryStowItem(owner, held);
            if (_hands.IsHolding(owner, held))
                _hands.TryDrop(owner, held, checkActionBlocker: false);
        }
    }

    private float GetTreatableDamage(EntityUid healer, EntityUid patient, List<HealToolInfo> tools)
    {
        if (!_damageableQuery.TryGetComponent(patient, out var damageable))
            return 0f;

        var treatableTypes = new HashSet<string>(StringComparer.Ordinal);
        var stopsBleed = false;
        var restoresBlood = false;
        var hasCritAllyStabilizer = false;

        foreach (var tool in tools)
        {
            if (tool.CritAllyOnly)
            {
                if (CanUseCritAllyPen(healer, patient))
                    hasCritAllyStabilizer = true;
                continue;
            }

            foreach (var type in tool.DamageTypes)
                treatableTypes.Add(type);
            if (tool.StopsBleed)
                stopsBleed = true;
            if (tool.RestoresBlood)
                restoresBlood = true;
        }

        FixedPoint2 total = FixedPoint2.Zero;
        foreach (var (type, amount) in damageable.Damage.DamageDict)
        {
            if (amount <= 0)
                continue;
            if (!treatableTypes.Contains(type))
                continue;
            total += amount;
        }

        if (TryComp(patient, out BloodstreamComponent? blood))
        {
            if (stopsBleed && blood.BleedAmount > 0)
                total += FixedPoint2.New(blood.BleedAmount);

            if (restoresBlood &&
                _solutions.ResolveSolution(patient, blood.BloodSolutionName, ref blood.BloodSolution, out var sol) &&
                sol.Volume < sol.MaxVolume)
            {
                total += sol.MaxVolume - sol.Volume;
            }
        }

        // Emergency medipen: count as above-threshold treatable only for critical allies.
        if (hasCritAllyStabilizer)
            total += FixedPoint2.New(TreatableDamageThreshold + 1f);

        return (float)total.Float();
    }

    private List<HealToolInfo> CollectOwnedTools(EntityUid owner)
    {
        var list = new List<HealToolInfo>();
        foreach (var item in _ammo.EnumerateInventoryAmmoCandidates(owner))
        {
            if (TryGetToolInfo(item, out var info))
                list.Add(info);
        }

        return list;
    }

    private void AppendNearbyTools(EntityUid owner, float range, List<HealToolInfo> tools)
    {
        if (!_xformQuery.TryGetComponent(owner, out var xform))
            return;

        var mapCoords = _transform.GetMapCoordinates(owner, xform: xform);
        foreach (var ent in _lookup.GetEntitiesInRange(mapCoords, range))
        {
            if (_containers.IsEntityInContainer(ent) || _ammo.IsCarriedBy(owner, ent))
                continue;

            if (TryGetToolInfo(ent, out var info))
                tools.Add(info);
        }
    }

    private bool TryGetToolInfo(EntityUid item, out HealToolInfo info)
    {
        info = default;

        if (_stackQuery.TryGetComponent(item, out var stack) && stack.Count <= 0)
            return false;

        if (_healingQuery.TryGetComponent(item, out var healing) && IsRecognizedKit(item, healing))
        {
            var types = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, amount) in healing.Damage.DamageDict)
            {
                if (amount < 0)
                    types.Add(key);
            }

            var priority = 10f;
            if (_tag.HasTag(item, BrutepackTag))
                priority = Math.Abs((float)healing.Damage.GetTotal().Float()); // suture packs more
            if (_tag.HasTag(item, OintmentTag))
                priority = Math.Abs((float)healing.Damage.GetTotal().Float());
            if (_tag.HasTag(item, GauzeTag))
                priority = 15f;

            // Prefer advanced kits (higher absolute heal).
            priority = Math.Max(priority, Math.Abs((float)healing.Damage.GetTotal().Float()));

            info = new HealToolInfo(item, types, healing.BloodlossModifier < 0, healing.ModifyBloodLevel > 0, true, false, priority, false);
            return types.Count > 0 || info.StopsBleed || info.RestoresBlood;
        }

        if (_hypoQuery.TryGetComponent(item, out var hypo) && IsUsableMedipen(item, hypo, out var profile))
        {
            var types = new HashSet<string>(profile.DamageTypes, StringComparer.Ordinal);
            info = new HealToolInfo(item, types, profile.StopsBleed, false, false, true, profile.Priority, profile.CritAllyOnly);
            return true;
        }

        return false;
    }

    private bool IsRecognizedKit(EntityUid item, HealingComponent healing)
    {
        // Recognized topical tags / advanced parents share tags.
        if (_tag.HasTag(item, GauzeTag) || _tag.HasTag(item, BrutepackTag) || _tag.HasTag(item, OintmentTag))
            return true;

        // Fallback: any Biological healing item with damage entries (covers suture/mesh tags).
        return healing.Damage.DamageDict.Count > 0;
    }

    private bool IsUsableMedipen(EntityUid item, HyposprayComponent hypo, out MedipenProfile profile)
    {
        profile = default;
        if (!_metaQuery.TryGetComponent(item, out var meta) || meta.EntityPrototype is not { } proto)
            return false;

        if (!MedipenProfiles.TryGetValue(proto.ID, out profile))
            return false;

        if (!_solutions.TryGetSolution(item, hypo.SolutionName, out _, out var solution))
            return false;

        return solution.Volume > 0;
    }

    private bool ToolHelpsPatient(EntityUid healer, EntityUid patient, HealToolInfo info)
    {
        return ScoreToolForPatient(healer, patient, info) > 0f;
    }

    private bool CanUseCritAllyPen(EntityUid healer, EntityUid patient)
    {
        if (patient == healer)
            return false;

        if (!_mobState.IsCritical(patient))
            return false;

        return _faction.IsEntityFriendly(healer, patient);
    }

    private float ScoreToolForPatient(EntityUid healer, EntityUid patient, HealToolInfo info)
    {
        if (info.CritAllyOnly)
        {
            if (!CanUseCritAllyPen(healer, patient))
                return 0f;

            // High priority: stabilize crit ally. Bleed is a bonus, not a requirement.
            float critScore = TreatableDamageThreshold + 10f;
            if (TryComp(patient, out BloodstreamComponent? critBlood) && info.StopsBleed && critBlood.BleedAmount > 0)
                critScore += critBlood.BleedAmount * 2f;

            return critScore + info.Priority * 0.01f;
        }

        if (!_damageableQuery.TryGetComponent(patient, out var damageable))
            return 0f;

        float score = 0f;
        foreach (var (type, amount) in damageable.Damage.DamageDict)
        {
            if (amount <= 0 || !info.DamageTypes.Contains(type))
                continue;
            score += (float)amount.Float();
        }

        if (TryComp(patient, out BloodstreamComponent? blood))
        {
            if (info.StopsBleed && blood.BleedAmount > 0)
                score += blood.BleedAmount * 2f;
            if (info.RestoresBlood &&
                _solutions.ResolveSolution(patient, blood.BloodSolutionName, ref blood.BloodSolution, out var sol) &&
                sol.Volume < sol.MaxVolume)
            {
                score += (float)(sol.MaxVolume - sol.Volume).Float() * 0.05f;
            }
        }

        // Pens/kits that only stop bleed must have bleed; empty damage+bleed tools otherwise score 0.
        if (score <= 0f)
            return 0f;

        // Medipens with listed damage types must actually match some of that damage
        // (bleed-only bonus is allowed for hemostasis which has empty DamageTypes).
        if (info.IsMedipen && info.DamageTypes.Count > 0)
        {
            var matchedDamage = false;
            foreach (var (type, amount) in damageable.Damage.DamageDict)
            {
                if (amount > 0 && info.DamageTypes.Contains(type))
                {
                    matchedDamage = true;
                    break;
                }
            }

            if (!matchedDamage)
                return 0f;
        }

        return score + info.Priority * 0.01f;
    }

    private void CountOwnedMeds(EntityUid owner, out int kits, out int pens)
    {
        kits = 0;
        pens = 0;
        foreach (var item in _ammo.EnumerateInventoryAmmoCandidates(owner))
        {
            if (!TryGetToolInfo(item, out var info))
                continue;
            if (info.IsKit)
                kits++;
            else if (info.IsMedipen)
                pens++;
        }
    }

    private bool HasNearbyHostile(EntityUid owner, float range)
    {
        foreach (var _ in _faction.GetNearbyHostiles(owner, range))
            return true;
        return false;
    }

    private readonly record struct HealToolInfo(
        EntityUid Entity,
        HashSet<string> DamageTypes,
        bool StopsBleed,
        bool RestoresBlood,
        bool IsKit,
        bool IsMedipen,
        float Priority,
        bool CritAllyOnly);

    private readonly record struct MedipenProfile(
        string[] DamageTypes,
        bool StopsBleed,
        float Priority,
        bool CritAllyOnly = false);
}
