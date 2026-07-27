using Robust.Shared.GameStates;

namespace Content.Shared.Cover;

/// <summary>
/// Soft probabilistic cover (e.g. tables). Shots from within <see cref="AdjacentPassRange"/>
/// always pass; otherwise blocked with <see cref="BlockChance"/>.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ProbabilisticCoverComponent : Component
{
    /// <summary>Shooter within this many tiles of the cover always passes through.</summary>
    [DataField, AutoNetworkedField]
    public float AdjacentPassRange = 1f;

    /// <summary>Chance to stop a shot when the shooter is farther than <see cref="AdjacentPassRange"/>.</summary>
    [DataField, AutoNetworkedField]
    public float BlockChance = 0.5f;
}
