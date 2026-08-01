using Content.Server.Shuttles.Components;
using Content.Shared._CitadelStation.SubGrid.Components;
using Content.Shared._CitadelStation.SubGrid.Systems;

namespace Content.Server._CitadelStation.SubGrid.Systems;

/// <summary>
/// Server host-follow: shuttle damping and stair reseat on attach.
/// Force-park while mother grid moves is temporarily disabled.
/// </summary>
public sealed class SubGridHostFollowSystem : SharedSubGridHostFollowSystem
{
    [Dependency] private readonly SubGridSystem _subGrids = default!;
    // Temporarily unused while force-park on host motion is commented out.
    // [Dependency] private readonly SubGridAntigravSystem _antigrav = default!;

    public override void Initialize()
    {
        base.Initialize();
        Log.Info("SubGridHostFollowSystem initialized (shared follow + server attach hooks)");
    }

    protected override void OnHostAttached(EntityUid uid, SubGridComponent sub)
    {
        _subGrids.ReconcileAccessPoints(uid);

        // TEMP: force-park when host is moving — disabled for prediction experiments.
        // if (sub.HostGrid is { } host && _antigrav.IsGridMoving(host, out _, out _))
        //     _antigrav.TryForceParkForHostMotion(uid, sub);
    }

    protected override void OnHostFollowTick(EntityUid uid, SubGridComponent sub, EntityUid host)
    {
        // TEMP: force-park while host moves with drive on — disabled for prediction experiments.
        // if (sub.DriveEnabled)
        //     _antigrav.TryForceParkForHostMotion(uid, sub);
    }

    protected override void SuppressShuttleDamping(EntityUid uid)
    {
        if (TryComp(uid, out ShuttleComponent? shuttle) && shuttle.DampingModifier != 0f)
            shuttle.DampingModifier = 0f;
    }

    protected override void RestoreShuttleDamping(EntityUid uid, SubGridComponent sub)
    {
        if (sub.DriveEnabled && TryComp(uid, out ShuttleComponent? shuttle))
            shuttle.DampingModifier = shuttle.BodyModifier;
    }
}
