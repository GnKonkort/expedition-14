using System.Linq;
using System.Threading.Tasks;
using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Server.NPC.Systems;
using Content.Shared.Administration;
using Content.Shared.Eui;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.NPC.Dialogue;
using Content.Shared.NPC.Systems;
using Content.Shared.Roles;
using JetBrains.Annotations;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server.Administration.UI;

[UsedImplicitly]
public sealed class NpcEditorEui : BaseEui
{
    [Dependency] private readonly IAdminManager _admins = default!;
    [Dependency] private readonly IEntityManager _ent = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;

    private readonly NetEntity? _target;

    public NpcEditorEui(NetEntity? target = null)
    {
        _target = target;
        IoCManager.InjectDependencies(this);
    }

    public override void Opened()
    {
        base.Opened();
        StateDirty();
        _admins.OnPermsChanged += OnPermsChanged;
        _ = RefreshAsync();
    }

    public override void Closed()
    {
        base.Closed();
        _admins.OnPermsChanged -= OnPermsChanged;
    }

    private void OnPermsChanged(AdminPermsChangedEventArgs obj)
    {
        if (obj.Player == Player && !_admins.HasAdminFlag(Player, AdminFlags.Fun))
            Close();
    }

    public override EuiStateBase GetNewState()
    {
        var presets = _ent.System<NpcPresetSystem>().GetCachedPresets()
            .Select(p => new NpcPresetListEntry
            {
                Id = p.Id,
                Name = p.Name,
                CreatedBy = p.CreatedBy,
                Data = p.Data,
            })
            .OrderBy(p => p.Name)
            .ToList();

        var factions = _ent.System<NpcFactionSystem>().EnumerateEditorVisible()
            .Select(f => new NpcFactionListEntry
            {
                Id = f.ID,
                Category = f.Category,
                Description = f.Description,
            })
            .OrderBy(f => f.Category)
            .ThenBy(f => f.Id)
            .ToList();

        var gears = _proto.EnumeratePrototypes<StartingGearPrototype>()
            .Select(g => g.ID)
            .OrderBy(id => id)
            .ToList();

        var species = _proto.EnumeratePrototypes<SpeciesPrototype>()
            .Where(s => s.RoundStart)
            .Select(s => s.ID)
            .OrderBy(id => id)
            .ToList();

        var dialogues = _proto.EnumeratePrototypes<NpcDialoguePrototype>()
            .Select(d => d.ID)
            .OrderBy(id => id)
            .ToList();

        return new NpcEditorEuiState
        {
            TargetNetEntity = _target,
            Presets = presets,
            Factions = factions,
            StartingGears = gears,
            SpeciesIds = species,
            DialogueIds = dialogues,
        };
    }

    public override async void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (!_admins.HasAdminFlag(Player, AdminFlags.Fun))
            return;

        var presets = _ent.System<NpcPresetSystem>();
        var adminName = Player.Name;

        switch (msg)
        {
            case NpcEditorEuiMsg.Refresh:
                await RefreshAsync();
                break;

            case NpcEditorEuiMsg.SavePreset save:
            {
                if (string.IsNullOrWhiteSpace(save.Name))
                    return;
                await presets.SavePresetAsync(save.Name.Trim(), adminName, save.Data, save.ExistingId);
                StateDirty();
                break;
            }

            case NpcEditorEuiMsg.DeletePreset del:
                await presets.DeletePresetAsync(del.Id);
                StateDirty();
                break;

            case NpcEditorEuiMsg.SpawnPreset spawn:
                presets.SpawnAtSession(spawn.Data, Player);
                break;

            case NpcEditorEuiMsg.ApplyPreset apply:
                if (_ent.TryGetEntity(apply.Target, out var target) && target != null)
                    presets.ApplyToEntity(target.Value, apply.Data);
                break;
        }
    }

    private async Task RefreshAsync()
    {
        await _ent.System<NpcPresetSystem>().EnsureCacheLoadedAsync();
        StateDirty();
    }
}
