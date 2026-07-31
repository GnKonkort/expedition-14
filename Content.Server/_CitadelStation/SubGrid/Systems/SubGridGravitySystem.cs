using System.Numerics;
using Content.Shared.Gravity;
using Content.Shared._CitadelStation.SubGrid.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.Server._CitadelStation.SubGrid.Systems;

/// <summary>
/// Mirrors gravity from overlapping non-SubGrid grids (or the map) onto the SubGrid itself.
/// </summary>
public sealed class SubGridGravitySystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(0.25);

    private TimeSpan _nextSync;
    private readonly List<Entity<MapGridComponent>> _gridsBuffer = new();

    public override void Initialize()
    {
        base.Initialize();
        Log.Info("SubGridGravitySystem initialized");
    }

    public void OnSubGridStartup(Entity<SubGridComponent> ent)
    {
        SyncGravity(ent);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.CurTime < _nextSync)
            return;

        _nextSync = _timing.CurTime + SyncInterval;

        var query = EntityQueryEnumerator<SubGridComponent, MapGridComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var grid, out var xform))
        {
            SyncGravity((uid, Comp<SubGridComponent>(uid)), grid, xform);
        }
    }

    private void SyncGravity(Entity<SubGridComponent> ent)
    {
        if (!TryComp(ent, out MapGridComponent? grid) || !TryComp(ent, out TransformComponent? xform))
            return;

        SyncGravity(ent, grid, xform);
    }

    private void SyncGravity(Entity<SubGridComponent> ent, MapGridComponent grid, TransformComponent xform)
    {
        var gravity = EnsureComp<GravityComponent>(ent);
        // Prevent generator RefreshGravity from wiping mirrored state.
        if (!gravity.Inherent)
        {
            gravity.Inherent = true;
            Dirty(ent, gravity);
        }

        var want = ComputeWantGravity(ent, grid, xform);
        if (gravity.Enabled == want)
            return;

        Log.Info("SubGrid gravity {Enabled} -> {Want} on {Grid}", gravity.Enabled, want, ToPrettyString(ent));
        gravity.Enabled = want;
        Dirty(ent, gravity);

        var ev = new GravityChangedEvent(ent, want);
        RaiseLocalEvent(ent, ref ev, true);
    }

    private bool ComputeWantGravity(EntityUid self, MapGridComponent grid, TransformComponent xform)
    {
        if (xform.MapUid is { } mapUid &&
            TryComp<GravityComponent>(mapUid, out var mapGrav) &&
            mapGrav.Enabled)
        {
            return true;
        }

        if (xform.MapID == MapId.Nullspace)
            return false;

        var (_, _, worldMatrix) = _transform.GetWorldPositionRotationMatrix(xform);
        var worldAabb = worldMatrix.TransformBox(grid.LocalAABB).Enlarged(0.05f);

        var grids = _gridsBuffer;
        grids.Clear();
        _mapManager.FindGridsIntersecting(xform.MapID, worldAabb, ref grids);

        foreach (var other in grids)
        {
            if (other.Owner == self)
                continue;

            if (HasComp<SubGridComponent>(other.Owner))
                continue;

            if (TryComp<GravityComponent>(other.Owner, out var otherGrav) && otherGrav.Enabled)
                return true;
        }

        return false;
    }
}
