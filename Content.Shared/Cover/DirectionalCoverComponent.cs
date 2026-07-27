using Robust.Shared.GameStates;

namespace Content.Shared.Cover;

/// <summary>
/// Soft directional cover (e.g. metal barricades). Local south is the "front" that may block bullets;
/// shots originating from the rear (defender side) always pass through.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class DirectionalCoverComponent : Component
{
    /// <summary>Chance to stop a shot that arrives from the attack face.</summary>
    [DataField, AutoNetworkedField]
    public float BlockChance = 0.8f;
}
