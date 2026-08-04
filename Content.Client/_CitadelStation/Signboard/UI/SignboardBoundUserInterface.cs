using Content.Shared._CitadelStation.Signboard;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Shared.Prototypes;

namespace Content.Client._CitadelStation.Signboard.UI;

[UsedImplicitly]
public sealed class SignboardBoundUserInterface : BoundUserInterface
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    private SignboardWindow? _window;

    public SignboardBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<SignboardWindow>();
        _window.PopulateStyles(_prototypes.EnumeratePrototypes<SignboardFramePrototype>());
        _window.OnFramesChanged += (frames, active) =>
            SendMessage(new SignboardUpdateFramesMessage(frames, active));
        _window.OnSizeChanged += size => SendMessage(new SignboardSetSizeMessage(size));
        _window.OnDelayChanged += delay => SendMessage(new SignboardSetDelayMessage(delay));
        _window.OnNeonChanged += neon => SendMessage(new SignboardSetNeonMessage(neon));
        _window.OnPlayingChanged += playing => SendMessage(new SignboardSetPlayingMessage(playing));
        _window.OnPoweredChanged += powered => SendMessage(new SignboardSetPoweredMessage(powered));
        _window.OnStyleSelected += style => SendMessage(new SignboardSetFrameStyleMessage(style));
        _window.OnClose += () => _window?.FlushFrames();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is SignboardBoundUserInterfaceState signState)
            _window?.UpdateState(signState);
    }
}
