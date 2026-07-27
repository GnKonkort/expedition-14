using Robust.Shared.GameStates;

namespace Content.Shared.Cover;

/// <summary>
/// Soft directional cover (e.g. metal barricades). Local south is the "front" that blocks bullets;
/// shots originating from the rear (defender side) pass through.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class DirectionalCoverComponent : Component;
