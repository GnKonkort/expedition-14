using Content.Shared.Preferences;

namespace Content.Shared._Arcane.ERP;

/// <summary>
/// Raised after a humanoid profile is applied to an entity via LoadProfile.
/// </summary>
public readonly record struct HumanoidProfileLoadedEvent(HumanoidCharacterProfile Profile);
