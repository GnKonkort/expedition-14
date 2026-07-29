using Content.Client.Eui;
using Content.Shared.Administration;
using Content.Shared.Eui;
using JetBrains.Annotations;

namespace Content.Client.Administration.UI.NpcEditor;

[UsedImplicitly]
public sealed class NpcEditorEui : BaseEui
{
    private readonly NpcEditorWindow _window;

    public NpcEditorEui()
    {
        _window = new NpcEditorWindow();
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
        _window.OnSpawn += data => SendMessage(new NpcEditorEuiMsg.SpawnPreset { Data = data });
        _window.OnApply += (target, data) => SendMessage(new NpcEditorEuiMsg.ApplyPreset { Target = target, Data = data });
        _window.OnSave += (name, data, id) => SendMessage(new NpcEditorEuiMsg.SavePreset
        {
            Name = name,
            Data = data,
            ExistingId = id,
        });
        _window.OnDelete += id => SendMessage(new NpcEditorEuiMsg.DeletePreset { Id = id });
        _window.OnRefresh += () => SendMessage(new NpcEditorEuiMsg.Refresh());
    }

    public override void Opened()
    {
        _window.OpenCentered();
    }

    public override void Closed()
    {
        base.Closed();
        _window.Close();
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is NpcEditorEuiState cast)
            _window.UpdateState(cast);
    }
}
