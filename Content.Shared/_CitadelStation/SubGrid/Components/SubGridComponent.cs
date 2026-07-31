using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.Maths;

namespace Content.Shared._CitadelStation.SubGrid.Components;

/// <summary>
/// Marks a MapGrid as a vehicle sub-grid: phases through other MapGrid hulls
/// and uses bumper fixtures against walls/objects.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class SubGridComponent : Component
{
    public const string BumperFixtureId = "subgrid_bumper";

    /// <summary>Prefix for per-tile hard deck fixtures (legacy; perimeter rails preferred).</summary>
    public const string DeckFixturePrefix = "subgrid_deck_";

    /// <summary>Prototype for per-tile Kinematic boarding barrier entities.</summary>
    public const string PerimeterPrototypeId = "SubGridPerimeterBlocker";

    /// <summary>Maximum tile count (including empty checks on expand).</summary>
    [DataField, AutoNetworkedField]
    public int MaxTiles = 64;

    /// <summary>Whether the antigrav core currently allows Dynamic movement.</summary>
    [DataField, AutoNetworkedField]
    public bool DriveEnabled;

    [DataField, AutoNetworkedField]
    public SubGridMovementMode Mode = SubGridMovementMode.Fighter;

    /// <summary>
    /// Added to half-tile when building per-floor hull fixtures / wall resolve boxes.
    /// Negative = inset so the pad can sit flush against walls without an early physics gap.
    /// </summary>
    [DataField]
    public float BumperEnlarge = -0.02f;

    /// <summary>
    /// Entities currently aboard this SubGrid. Boarding barriers always ignore these
    /// (no GridUid / parenting lag). Cleared when the entity leaves the grid.
    /// Networked so client prediction can cancel barrier collisions while aboard.
    /// </summary>
    [DataField, AutoNetworkedField]
    public HashSet<EntityUid> BarrierPassThrough = new();

    /// <summary>MapGrid currently under this SubGrid (station / shuttle). Server follow state.</summary>
    [ViewVariables]
    public EntityUid? HostGrid;

    /// <summary>SubGrid origin in host-local space while attached.</summary>
    [ViewVariables]
    public Vector2 HostLocalPosition;

    [ViewVariables]
    public Angle HostLocalRotation;

    [ViewVariables]
    public bool HostPoseValid;

    /// <summary>Previous host world sample for delta follow while driving.</summary>
    [ViewVariables]
    public Vector2 LastHostWorldPosition;

    [ViewVariables]
    public Angle LastHostWorldRotation;

    [ViewVariables]
    public Vector2 LastHostLinearVelocity;

    [ViewVariables]
    public float LastHostAngularVelocity;

    [ViewVariables]
    public bool HostMotionSampleValid;

    /// <summary>Velocity relative to host, in host-local space. Thrusters change this while driving.</summary>
    [ViewVariables]
    public Vector2 RelativeLinearVelocity;

    [ViewVariables]
    public float RelativeAngularVelocity;
}
