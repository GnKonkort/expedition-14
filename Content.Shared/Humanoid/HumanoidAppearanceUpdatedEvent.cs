namespace Content.Shared.Humanoid;

/// <summary>
/// Raised on the client after a humanoid's sprite layers/markings were refreshed.
/// Used by systems that overlay additional visual layers (e.g. ERP organs).
/// </summary>
[ByRefEvent]
public readonly record struct HumanoidAppearanceUpdatedEvent;
