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
    public int MaxTiles = 128;

    /// <summary>Whether the antigrav core currently allows Dynamic movement.</summary>
    [DataField, AutoNetworkedField]
    public bool DriveEnabled;

    [DataField, AutoNetworkedField]
    public SubGridMovementMode Mode = SubGridMovementMode.Fighter;

    /// <summary>
    /// Authoritative boarding/exit tiles (stairs/docks). Checked via SubGrid world transform,
    /// not stair entity pose — survives host motion and temporary stair reparents.
    /// </summary>
    [DataField, AutoNetworkedField]
    public HashSet<Vector2i> BoardingTiles = new();

    /// <summary>
    /// Added to half-tile when building per-floor hull fixtures / wall resolve boxes.
    /// Negative = inset so the pad can sit flush against walls without an early physics gap.
    /// </summary>
    [DataField]
    public float BumperEnlarge = -0.02f;

    /// <summary>MapGrid currently under this SubGrid (station / shuttle).</summary>
    [DataField, AutoNetworkedField]
    public EntityUid? HostGrid;

    /// <summary>SubGrid origin in host-local space while attached.</summary>
    [DataField, AutoNetworkedField]
    public Vector2 HostLocalPosition;

    [DataField, AutoNetworkedField]
    public Angle HostLocalRotation;

    [DataField, AutoNetworkedField]
    public bool HostPoseValid;

    /// <summary>Velocity relative to host, in host-local space. Thrusters change this while driving.</summary>
    [DataField, AutoNetworkedField]
    public Vector2 RelativeLinearVelocity;

    [DataField, AutoNetworkedField]
    public float RelativeAngularVelocity;

    /// <summary>
    /// Legacy flag from soft host-weld experiment. Always false now — soft welds jerked at high host speed.
    /// Hard kinematic snap is used instead.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool HostWelded;

    /// <summary>Legacy weld joint id; cleared on attach.</summary>
    [ViewVariables]
    public string? HostWeldJointId;

    /// <summary>Previous host world sample for delta follow while driving (server / prediction local).</summary>
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
}
