using Content.Client.UserInterface.Screens;
using Content.Client.UserInterface.Systems.Gameplay;
using Content.Shared.NPC.Dialogue;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;
using Robust.Shared.Network;

namespace Content.Client.UserInterface.Systems.Dialogue;

[UsedImplicitly]
public sealed class DialogueUIController : UIController
{
    [Dependency] private readonly IClientNetManager _net = default!;

    private DialoguePanel? _panel;
    private Control? _container;

    public override void Initialize()
    {
        base.Initialize();

        _net.RegisterNetMessage<MsgDialogueUpdate>(OnDialogueUpdate);
        _net.RegisterNetMessage<MsgDialogueClose>(_ => ClosePanel());
        _net.RegisterNetMessage<MsgDialogueChoice>();

        var gameplayStateLoad = UIManager.GetUIController<GameplayStateLoadController>();
        gameplayStateLoad.OnScreenLoad += OnScreenLoad;
        gameplayStateLoad.OnScreenUnload += OnScreenUnload;
    }

    private void OnScreenLoad()
    {
        _container = UIManager.ActiveScreen switch
        {
            DefaultGameScreen game => game.DialogueMenu,
            SeparatedChatGameScreen separated => separated.DialogueMenu,
            _ => null,
        };
    }

    private void OnScreenUnload()
    {
        ClosePanel();
        _container = null;
    }

    private void OnDialogueUpdate(MsgDialogueUpdate message)
    {
        if (_container == null)
            OnScreenLoad();

        if (_container == null)
            return;

        EnsurePanel();
        if (_panel == null)
            return;

        _panel.SetContent(message.NpcName, message.Speech, message.Options);
        _panel.Visible = true;
    }

    private void EnsurePanel()
    {
        if (_panel != null || _container == null)
            return;

        _panel = new DialoguePanel();
        _panel.OptionSelected += index =>
        {
            var msg = new MsgDialogueChoice { OptionIndex = index };
            _net.ClientSendMessage(msg);
        };
        _panel.LeavePressed += () =>
        {
            _net.ClientSendMessage(new MsgDialogueClose());
            ClosePanel();
        };
        _container.AddChild(_panel);
    }

    private void ClosePanel()
    {
        if (_panel == null)
            return;

        _panel.Visible = false;
        _panel.Orphan();
        _panel = null;
    }
}
