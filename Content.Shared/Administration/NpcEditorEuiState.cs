using Content.Shared.Eui;
using Content.Shared.NPC;
using Content.Shared.NPC.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Administration;

[Serializable, NetSerializable]
public sealed class NpcEditorEuiState : EuiStateBase
{
    public NetEntity? TargetNetEntity;
    public List<NpcPresetListEntry> Presets = new();
    public List<NpcFactionListEntry> Factions = new();
    public List<string> StartingGears = new();
    public List<string> SpeciesIds = new();
    public List<string> DialogueIds = new();
}

[Serializable, NetSerializable]
public sealed class NpcPresetListEntry
{
    public int Id;
    public string Name = string.Empty;
    public string CreatedBy = string.Empty;
    public NpcPresetData Data = new();
}

[Serializable, NetSerializable]
public sealed class NpcFactionListEntry
{
    public string Id = string.Empty;
    public NpcFactionCategory Category;
    public string? Description;
}

public static class NpcEditorEuiMsg
{
    [Serializable, NetSerializable]
    public sealed class SavePreset : EuiMessageBase
    {
        public int? ExistingId;
        public string Name = string.Empty;
        public NpcPresetData Data = new();
    }

    [Serializable, NetSerializable]
    public sealed class DeletePreset : EuiMessageBase
    {
        public int Id;
    }

    [Serializable, NetSerializable]
    public sealed class SpawnPreset : EuiMessageBase
    {
        public NpcPresetData Data = new();
    }

    [Serializable, NetSerializable]
    public sealed class ApplyPreset : EuiMessageBase
    {
        public NetEntity Target;
        public NpcPresetData Data = new();
    }

    [Serializable, NetSerializable]
    public sealed class Refresh : EuiMessageBase;
}
