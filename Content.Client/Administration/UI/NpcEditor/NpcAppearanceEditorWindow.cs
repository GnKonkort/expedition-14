using System.Numerics;
using Content.Client.Lobby;
using Content.Client.Lobby.UI;
using Content.Client.Players.PlayTimeTracking;
using Content.Shared.Humanoid.Markings;
using Content.Shared.Preferences;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client.Administration.UI.NpcEditor;

/// <summary>
/// Thin wrapper around <see cref="HumanoidProfileEditor"/> for custom NPC presets.
/// </summary>
public sealed class NpcAppearanceEditorWindow : DefaultWindow
{
    private readonly HumanoidProfileEditor _editor;

    public event Action<HumanoidCharacterProfile>? OnConfirmed;

    public NpcAppearanceEditorWindow(HumanoidCharacterProfile profile)
    {
        Title = Loc.GetString("npc-editor-appearance-window-title");
        MinSize = SetSize = new Vector2(980, 720);

        _editor = new HumanoidProfileEditor(
            IoCManager.Resolve<IClientPreferencesManager>(),
            IoCManager.Resolve<IConfigurationManager>(),
            IoCManager.Resolve<IEntityManager>(),
            IoCManager.Resolve<IFileDialogManager>(),
            IoCManager.Resolve<ILogManager>(),
            IoCManager.Resolve<IPlayerManager>(),
            IoCManager.Resolve<IPrototypeManager>(),
            IoCManager.Resolve<IResourceManager>(),
            IoCManager.Resolve<JobRequirementsManager>(),
            IoCManager.Resolve<MarkingManager>());

        // Prefer our confirm button; lobby save writes player prefs.
        _editor.Save += () =>
        {
            if (_editor.Profile == null)
                return;
            OnConfirmed?.Invoke(_editor.Profile.Clone());
            Close();
        };

        var confirm = new Button
        {
            Text = Loc.GetString("npc-editor-appearance-confirm"),
            HorizontalAlignment = HAlignment.Right,
            MinHeight = 32,
        };
        confirm.OnPressed += _ =>
        {
            if (_editor.Profile == null)
                return;

            OnConfirmed?.Invoke(_editor.Profile.Clone());
            Close();
        };

        var cancel = new Button
        {
            Text = Loc.GetString("npc-editor-appearance-cancel"),
            MinHeight = 32,
        };
        cancel.OnPressed += _ => Close();

        var buttons = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalAlignment = HAlignment.Right,
            SeparationOverride = 6,
            Margin = new Thickness(8),
        };
        buttons.AddChild(cancel);
        buttons.AddChild(confirm);

        var root = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            VerticalExpand = true,
        };
        root.AddChild(_editor);
        root.AddChild(buttons);
        Contents.AddChild(root);

        _editor.SetProfile(profile.Clone(), null);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _editor.Dispose();
    }
}
