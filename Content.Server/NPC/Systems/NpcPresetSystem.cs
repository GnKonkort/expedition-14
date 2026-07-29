using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Clothing.Systems;
using Content.Server.Database;
using Content.Server.Humanoid;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Prototypes;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Movement.Components;
using Content.Shared.NPC;
using Content.Shared.NPC.Dialogue;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Prototypes;
using Content.Shared.NPC.Systems;
using Content.Shared.Preferences;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using YamlDotNet.RepresentationModel;

namespace Content.Server.NPC.Systems;

/// <summary>
/// Persists admin custom-NPC presets and applies them to spawned / existing humanoids.
/// </summary>
public sealed class NpcPresetSystem : EntitySystem
{
    [Dependency] private readonly IServerDbManager _db = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly ISerializationManager _ser = default!;
    [Dependency] private readonly HumanoidAppearanceSystem _humanoid = default!;
    [Dependency] private readonly MetaDataSystem _meta = default!;
    [Dependency] private readonly NpcFactionSystem _factions = default!;
    [Dependency] private readonly OutfitSystem _outfit = default!;
    [Dependency] private readonly SharedTransformSystem _xform = default!;

    private readonly Dictionary<int, NpcPresetListCacheEntry> _cache = new();
    private bool _cacheLoaded;

    public override void Initialize()
    {
        base.Initialize();
        // Fire-and-forget load; UI refresh will wait if needed.
        _ = ReloadCacheAsync();
    }

    public async Task ReloadCacheAsync(CancellationToken cancel = default)
    {
        var rows = await _db.GetNpcPresetsAsync(cancel);
        _cache.Clear();
        foreach (var row in rows)
        {
            if (!TryDeserialize(row.Data, out var data) || data == null)
            {
                Log.Warning($"Failed to deserialize NPC preset {row.Id} ({row.Name})");
                continue;
            }

            _cache[row.Id] = new NpcPresetListCacheEntry(row.Id, row.Name, row.CreatedBy, data);
        }

        _cacheLoaded = true;
    }

    public IReadOnlyCollection<NpcPresetListCacheEntry> GetCachedPresets()
    {
        return _cache.Values;
    }

    public async Task EnsureCacheLoadedAsync(CancellationToken cancel = default)
    {
        if (_cacheLoaded)
            return;
        await ReloadCacheAsync(cancel);
    }

    public string Serialize(NpcPresetData data)
    {
        var node = _ser.WriteValue(data, alwaysWrite: true, notNullableOverride: true);
        using var writer = new StringWriter();
        node.Write(writer);
        return writer.ToString();
    }

    public bool TryDeserialize(string yaml, out NpcPresetData? data)
    {
        data = null;
        try
        {
            using var reader = new StringReader(yaml);
            var stream = new YamlStream();
            stream.Load(reader);
            if (stream.Documents.Count == 0)
                return false;

            data = _ser.Read<NpcPresetData>(stream.Documents[0].RootNode.ToDataNode(), notNullableOverride: true);
            return data != null;
        }
        catch (Exception e)
        {
            Log.Error($"NPC preset deserialize failed: {e}");
            return false;
        }
    }

    public async Task<int> SavePresetAsync(string name, string createdBy, NpcPresetData data, int? existingId = null)
    {
        var id = await _db.UpsertNpcPresetAsync(name, createdBy, Serialize(data), existingId);
        _cache[id] = new NpcPresetListCacheEntry(id, name, createdBy, data);
        _cacheLoaded = true;
        return id;
    }

    public async Task<bool> DeletePresetAsync(int id)
    {
        var ok = await _db.DeleteNpcPresetAsync(id);
        if (ok)
            _cache.Remove(id);
        return ok;
    }

    /// <summary>
    /// Spawns a new humanoid NPC at the given coordinates from preset data.
    /// </summary>
    public EntityUid? SpawnFromPreset(NpcPresetData data, EntityCoordinates coordinates)
    {
        var profile = ResolveProfile(data);
        var speciesId = profile.Species;
        if (!_proto.TryIndex(speciesId, out SpeciesPrototype? species))
        {
            Log.Error($"Unknown species {speciesId}");
            return null;
        }

        var humanoid = EntityManager.CreateEntityUninitialized(species.Prototype, coordinates);

        EnsureComp<InputMoverComponent>(humanoid);
        EnsureComp<MobMoverComponent>(humanoid);

        ApplyFactions(humanoid, data);
        ApplyBehavior(humanoid, data, beforeMapInit: true);

        if (data.JoinSquad)
            EnsureComp<NPCSquadMemberComponent>(humanoid);

        // HumanoidAppearance ComponentInit loads DefaultWithSpecies (Urist) — apply after init.
        EntityManager.InitializeAndStartEntity(humanoid);

        _humanoid.LoadProfile(humanoid, profile);
        _meta.SetEntityName(humanoid, ResolveDisplayName(data, profile));

        if (!string.IsNullOrWhiteSpace(data.StartingGear))
            _outfit.SetOutfit(humanoid, data.StartingGear);

        return humanoid;
    }

    /// <summary>
    /// Applies preset configuration onto an existing humanoid entity.
    /// </summary>
    public bool ApplyToEntity(EntityUid target, NpcPresetData data)
    {
        if (!Exists(target))
            return false;

        var profile = ResolveProfile(data);
        _humanoid.LoadProfile(target, profile);
        _meta.SetEntityName(target, ResolveDisplayName(data, profile));

        EnsureComp<InputMoverComponent>(target);
        EnsureComp<MobMoverComponent>(target);

        ApplyFactions(target, data);
        ApplyBehavior(target, data, beforeMapInit: false);

        if (data.JoinSquad)
            EnsureComp<NPCSquadMemberComponent>(target);
        else
            RemComp<NPCSquadMemberComponent>(target);

        if (!string.IsNullOrWhiteSpace(data.StartingGear))
            _outfit.SetOutfit(target, data.StartingGear);

        return true;
    }

    public EntityUid? SpawnAtSession(NpcPresetData data, ICommonSession session)
    {
        if (session.AttachedEntity is not { } attached)
            return null;

        var coords = _xform.GetMoverCoordinates(attached);
        return SpawnFromPreset(data, coords);
    }

    private static string ResolveDisplayName(NpcPresetData data, HumanoidCharacterProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(data.DisplayName) &&
            !string.Equals(data.DisplayName, "Custom NPC", StringComparison.Ordinal))
            return data.DisplayName;

        // Random / profile-authored names beat the placeholder editor default.
        if (!string.IsNullOrWhiteSpace(profile.Name))
            return profile.Name;

        return data.DisplayName;
    }

    private void ApplyFactions(EntityUid uid, NpcPresetData data)
    {
        var set = data.Factions
            .Where(f => _proto.HasIndex<NpcFactionPrototype>(f))
            .Select(f => (ProtoId<NpcFactionPrototype>) f)
            .ToHashSet();

        HashSet<ProtoId<NpcFactionPrototype>>? addFriendly = data.AddFriendlyFactions.Count == 0
            ? null
            : data.AddFriendlyFactions.Select(f => (ProtoId<NpcFactionPrototype>) f).ToHashSet();
        HashSet<ProtoId<NpcFactionPrototype>>? addHostile = data.AddHostileFactions.Count == 0
            ? null
            : data.AddHostileFactions.Select(f => (ProtoId<NpcFactionPrototype>) f).ToHashSet();

        _factions.SetFactions(uid, set, addFriendly, addHostile);
    }

    private HumanoidCharacterProfile ResolveProfile(NpcPresetData data)
    {
        if (!data.RandomAppearance && data.Profile != null)
            return data.Profile;

        // Randomize within the selected species each spawn/apply.
        return HumanoidCharacterProfile.RandomWithSpecies(data.Species);
    }

    private void ApplyBehavior(EntityUid uid, NpcPresetData data, bool beforeMapInit)
    {
        var htn = EnsureComp<HTNComponent>(uid);
        htn.RootTask = new HTNCompoundTask { Task = data.HtnRoot };
        htn.Blackboard.SetValue(NPCBlackboard.NavClimb, data.NavClimb);
        htn.Blackboard.SetValue(NPCBlackboard.NavInteract, data.NavInteract);
        htn.Blackboard.SetValue(NPCBlackboard.NavPry, data.NavPry);
        htn.Blackboard.SetValue(NPCBlackboard.NavSmash, data.NavSmash);

        // Stand point / no-wander: stay posted when idle (and return after combat).
        var lockStand = data.UseStandPoint;
        var allowWander = data.AllowWander && !lockStand;
        if (!allowWander)
            htn.Blackboard.SetValue(NPCBlackboard.IdleWander, false);
        else
            htn.Blackboard.Remove<bool>(NPCBlackboard.IdleWander);

        if (lockStand)
        {
            var stand = _xform.GetMoverCoordinates(uid);
            htn.Blackboard.SetValue(NPCBlackboard.StandPoint, stand);
            htn.Blackboard.SetValue(NPCBlackboard.StandPointRange, 0.5f);
        }
        else
        {
            htn.Blackboard.Remove<EntityCoordinates>(NPCBlackboard.StandPoint);
            htn.Blackboard.Remove<float>(NPCBlackboard.StandPointRange);
        }

        htn.Plan = null;

        if (string.IsNullOrWhiteSpace(data.RoleProfile) ||
            !_proto.HasIndex<NpcRoleProfilePrototype>(data.RoleProfile))
        {
            RemComp<NPCRoleComponent>(uid);
        }
        else
        {
            var role = EnsureComp<NPCRoleComponent>(uid);
            role.Profile = data.RoleProfile;

            if (!beforeMapInit)
                ApplyRoleBlackboard(uid, role.Profile);
        }

        ApplyDialogue(uid, data);
    }

    private void ApplyDialogue(EntityUid uid, NpcPresetData data)
    {
        var hasCustom = data.CustomDialogue is { } custom && custom.Nodes.Count > 0 && custom.IsValid();
        var hasProto = !string.IsNullOrWhiteSpace(data.DialogueTree) &&
                       _proto.HasIndex<NpcDialoguePrototype>(data.DialogueTree);

        if (!hasCustom && !hasProto)
        {
            RemComp<NpcDialogueComponent>(uid);
            return;
        }

        var dialogue = EnsureComp<NpcDialogueComponent>(uid);
        dialogue.Enabled = true;
        dialogue.CustomDialogue = hasCustom ? data.CustomDialogue!.Clone() : null;
        dialogue.Dialogue = hasProto ? data.DialogueTree : null;
        Dirty(uid, dialogue);
    }

    private void ApplyRoleBlackboard(EntityUid uid, ProtoId<NpcRoleProfilePrototype> profileId)
    {
        EnsureComp<NPCNeedComponent>(uid);

        if (!_proto.TryIndex(profileId, out NpcRoleProfilePrototype? profile))
            return;

        if (!TryComp<HTNComponent>(uid, out var htn))
            return;

        if (profile.InventoryPolicy is { } inv)
            htn.Blackboard.SetValue(NPCBlackboard.InventoryPolicy, inv.Id);

        if (profile.ChemKnowledge is { } chem)
            htn.Blackboard.SetValue(NPCBlackboard.ChemKnowledge, chem.Id);
    }
}

public readonly record struct NpcPresetListCacheEntry(int Id, string Name, string CreatedBy, NpcPresetData Data);
