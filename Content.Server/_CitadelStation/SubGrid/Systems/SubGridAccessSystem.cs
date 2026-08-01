using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Gravity;
using Content.Shared.Mobs.Components;
using Content.Shared.Stunnable;
using Content.Shared._CitadelStation.SubGrid.Components;
using Content.Shared._CitadelStation.SubGrid.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;
using System.Numerics;

namespace Content.Server._CitadelStation.SubGrid.Systems;

/// <summary>
/// Boarding/exit rules: prefer SubGridAccess (stairs / docked ports). Falling off under gravity stuns.
/// Also ejects mobs that try to stand on the station floor under a SubGrid footprint.
/// </summary>
public sealed class SubGridAccessSystem : EntitySystem
{
    [Dependency] private readonly SharedGravitySystem _gravity = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedSubGridCollisionSystem _collision = default!;

    private static readonly TimeSpan FallStun = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan UnderCheckInterval = TimeSpan.FromSeconds(0.2);

    private readonly Dictionary<EntityUid, TimeSpan> _ignoreUntil = new();
    /// <summary>Mobs allowed aboard via stairs/dock/EVA — fall-exit stun only applies to these.</summary>
    private readonly HashSet<EntityUid> _authorizedAboard = new();
    private TimeSpan _nextUnderCheck;
    private bool _inBounce;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MobStateComponent, EntParentChangedMessage>(OnParentChanged);
        SubscribeLocalEvent<MobStateComponent, EntityTerminatingEvent>(OnMobTerminating);
        SubscribeLocalEvent<DockingComponent, MapInitEvent>(OnDockMapInit);
        SubscribeLocalEvent<DockEvent>(OnDocked);
        Log.Info("SubGridAccessSystem initialized");
    }

    /// <summary>True if the entity is standing on / parented to this SubGrid.</summary>
    public bool IsOnSubGrid(EntityUid subGrid, EntityUid entity)
    {
        var xform = Transform(entity);
        return xform.GridUid == subGrid || xform.ParentUid == subGrid;
    }

    /// <summary>
    /// True if boarding barriers for this SubGrid must ignore the entity:
    /// on the SubGrid (GridUid/parent), or currently at this SubGrid's stairs/dock access.
    /// </summary>
    public bool HasBarrierPass(EntityUid subGrid, EntityUid entity)
    {
        return _collision.HasBarrierPass(subGrid, entity);
    }

    /// <summary>Mark as legally boarded (for fall-exit stun). Barriers use GridUid, not a whitelist.</summary>
    private void MarkAuthorized(EntityUid mob)
    {
        if (!_authorizedAboard.Add(mob))
        {
            if (TryComp(mob, out TransformComponent? x) && x.GridTraversal)
                x.GridTraversal = false;
            return;
        }

        if (TryComp(mob, out TransformComponent? xform))
            xform.GridTraversal = false;
    }

    private void ClearAuthorized(EntityUid mob)
    {
        if (!_authorizedAboard.Remove(mob))
            return;

        if (TryComp(mob, out TransformComponent? xform))
            xform.GridTraversal = true;
    }

    private void OnMobTerminating(EntityUid uid, MobStateComponent mob, ref EntityTerminatingEvent args)
    {
        ClearAuthorized(uid);
        _ignoreUntil.Remove(uid);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.CurTime < _nextUnderCheck)
            return;

        _nextUnderCheck = _timing.CurTime + UnderCheckInterval;
        EjectUnderwalkers();
        EjectUnauthorizedBoarders();
    }

    /// <summary>
    /// Players remaining parented to the station (or map) while standing in a SubGrid tile footprint
    /// are treated as walking under the pad — push them out (except exactly at boarding stairs).
    /// </summary>
    private void EjectUnderwalkers()
    {
        var subQuery = EntityQueryEnumerator<SubGridComponent, MapGridComponent, TransformComponent>();
        while (subQuery.MoveNext(out var gridUid, out _, out var grid, out var gridXform))
        {
            if (gridXform.MapID == MapId.Nullspace)
                continue;

            var (_, _, worldMatrix, invWorld) = _transform.GetWorldPositionRotationMatrixWithInv(gridXform);
            var localAabb = grid.LocalAABB.Enlarged(0.05f);
            if (localAabb.IsEmpty())
                continue;

            var mobQuery = EntityQueryEnumerator<MobStateComponent, TransformComponent>();
            while (mobQuery.MoveNext(out var mob, out _, out var mobXform))
            {
                if (mobXform.MapID != gridXform.MapID)
                    continue;

                if (_ignoreUntil.TryGetValue(mob, out var until) && _timing.CurTime < until)
                    continue;

                // Already aboard.
                if (mobXform.GridUid == gridUid)
                    continue;

                var mobWorld = _transform.GetWorldPosition(mobXform);
                var local = Vector2.Transform(mobWorld, invWorld);
                if (!localAabb.Contains(local))
                    continue;

                // Must be over a real floor tile of this SubGrid.
                var indices = _map.WorldToTile(gridUid, grid, mobWorld);
                if (!_map.TryGetTileRef(gridUid, grid, indices, out var tile) || tile.Tile.IsEmpty)
                    continue;

                // Stolen riders (authorized but parented to host) — put them back, don't eject.
                if (_authorizedAboard.Contains(mob))
                {
                    ReclaimOntoSubGrid(mob, mobXform, gridUid, wasAuthorized: true);
                    continue;
                }

                // Only exempt the immediate stair tile approach — not the whole pad.
                if (IsAtBoardingPoint(mob))
                    continue;

                Log.Debug("SubGrid underwalk eject: {Mob} from under {Grid} local={Local}",
                    ToPrettyString(mob), ToPrettyString(gridUid), local);
                PushOutOfFootprint(mob, mobXform, localAabb, invWorld, worldMatrix);
            }
        }
    }

    private bool IsAtBoardingPoint(EntityUid mob)
    {
        return IsAtBoardingPoint(mob, out _);
    }

    private bool IsAtBoardingPoint(EntityUid mob, out EntityUid? accessHit)
    {
        accessHit = null;
        if (!_collision.IsNearBoardingTile(mob, null, out var subGridHit, out var tile))
            return false;

        // Prefer a concrete stairs/dock entity for debug; tile registry is authoritative.
        if (subGridHit is { } gridUid &&
            TryComp(gridUid, out MapGridComponent? grid) &&
            TryGetAccessOnTile(gridUid, grid, tile, out var access) &&
            access != null)
        {
            accessHit = access;
        }
        else
        {
            accessHit = subGridHit;
        }

        return true;
    }

    private bool TryGetAccessOnTile(EntityUid gridUid, MapGridComponent grid, Vector2i indices, out EntityUid? access)
    {
        access = null;

        if (TryComp(gridUid, out SubGridComponent? sub) && sub.BoardingTiles.Contains(indices))
        {
            // Prefer a live access entity for logging / messages.
            foreach (var ent in _map.GetAnchoredEntities(gridUid, grid, indices))
            {
                if (!HasComp<SubGridAccessComponent>(ent))
                    continue;

                access = ent;
                return true;
            }

            var owned = EntityQueryEnumerator<SubGridAccessComponent>();
            while (owned.MoveNext(out var uid, out var accessComp))
            {
                if (accessComp.OwnerGrid != gridUid || accessComp.Tile != indices)
                    continue;

                access = uid;
                return true;
            }

            // Tile is a boarding point even if the stair entity drifted away.
            return true;
        }

        foreach (var ent in _map.GetAnchoredEntities(gridUid, grid, indices))
        {
            if (!HasComp<SubGridAccessComponent>(ent))
                continue;

            access = ent;
            return true;
        }

        // Unanchored / drifted stairs still claiming this SubGrid tile via OwnerGrid.
        var query = EntityQueryEnumerator<SubGridAccessComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var accessComp, out var xform))
        {
            if (accessComp.OwnerGrid == gridUid && accessComp.Tile == indices)
            {
                access = uid;
                return true;
            }

            if (xform.GridUid != gridUid && xform.ParentUid != gridUid)
                continue;

            if (_map.WorldToTile(gridUid, grid, _transform.GetWorldPosition(xform)) != indices)
                continue;

            access = uid;
            return true;
        }

        return false;
    }

    private void PushOutOfFootprint(
        EntityUid mob,
        TransformComponent xform,
        Box2 localAabb,
        Matrix3x2 invWorld,
        Matrix3x2 worldMatrix)
    {
        _ignoreUntil[mob] = _timing.CurTime + TimeSpan.FromSeconds(0.15);

        var worldPos = _transform.GetWorldPosition(xform);
        var local = Vector2.Transform(worldPos, invWorld);

        var dxL = local.X - localAabb.Left;
        var dxR = localAabb.Right - local.X;
        var dyB = local.Y - localAabb.Bottom;
        var dyT = localAabb.Top - local.Y;
        var min = MathF.Min(MathF.Min(dxL, dxR), MathF.Min(dyB, dyT));

        const float margin = 0.12f;
        Vector2 targetLocal;
        if (min == dxL)
            targetLocal = new Vector2(localAabb.Left - margin, local.Y);
        else if (min == dxR)
            targetLocal = new Vector2(localAabb.Right + margin, local.Y);
        else if (min == dyB)
            targetLocal = new Vector2(local.X, localAabb.Bottom - margin);
        else
            targetLocal = new Vector2(local.X, localAabb.Top + margin);

        var targetWorld = Vector2.Transform(targetLocal, worldMatrix);
        _transform.SetWorldPosition(mob, targetWorld);

        if (TryComp(mob, out PhysicsComponent? body))
            _physics.SetLinearVelocity(mob, Vector2.Zero, body: body);

        Log.Debug("SubGrid underwalk pushed {Mob} to {Pos}", ToPrettyString(mob), targetWorld);
    }

    private void OnDockMapInit(EntityUid uid, DockingComponent component, MapInitEvent args)
    {
        TryMarkDockAccess(uid);
    }

    private void TryMarkDockAccess(EntityUid uid)
    {
        var grid = Transform(uid).GridUid;
        if (grid != null && HasComp<SubGridComponent>(grid.Value))
        {
            EnsureComp<SubGridAccessComponent>(uid);
            Log.Debug("Dock marked SubGridAccess: {Dock} on {Grid}", ToPrettyString(uid), ToPrettyString(grid.Value));
        }
        else
        {
            Log.Debug("Dock MapInit skip SubGridAccess: {Dock} grid={Grid}",
                ToPrettyString(uid),
                grid == null ? "null" : ToPrettyString(grid.Value));
        }
    }

    private void OnDocked(DockEvent args)
    {
        var aIsSub = HasComp<SubGridComponent>(args.GridAUid);
        var bIsSub = HasComp<SubGridComponent>(args.GridBUid);
        Log.Debug("DockEvent: gridA={A} sub={ASub} gridB={B} sub={BSub}",
            ToPrettyString(args.GridAUid), aIsSub, ToPrettyString(args.GridBUid), bIsSub);

        if (!aIsSub && !bIsSub)
            return;

        AddAccessOnGridDocks(args.GridAUid);
        AddAccessOnGridDocks(args.GridBUid);
    }

    private void AddAccessOnGridDocks(EntityUid gridUid)
    {
        var count = 0;
        var query = EntityQueryEnumerator<DockingComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var dock, out var xform))
        {
            if (xform.GridUid != gridUid)
                continue;
            if (!dock.Docked)
                continue;
            EnsureComp<SubGridAccessComponent>(uid);
            count++;
        }

        Log.Debug("AddAccessOnGridDocks: grid={Grid} marked={Count}", ToPrettyString(gridUid), count);
    }

    /// <summary>
    /// Physics often re-parents people onto the pad during bounce cooldown. Catch anyone
    /// standing on a SubGrid who never boarded via stairs/dock.
    /// </summary>
    private void EjectUnauthorizedBoarders()
    {
        var mobQuery = EntityQueryEnumerator<MobStateComponent, TransformComponent>();
        while (mobQuery.MoveNext(out var mob, out _, out var xform))
        {
            if (xform.GridUid is not { } gridUid || !HasComp<SubGridComponent>(gridUid))
                continue;

            if (_authorizedAboard.Contains(mob))
                continue;

            if (_gravity.IsWeightless(mob) || IsAtBoardingPoint(mob))
            {
                MarkAuthorized(mob);
                Log.Info("SubGrid board authorized (late): {Mob} on {Grid}", ToPrettyString(mob), ToPrettyString(gridUid));
                continue;
            }

            if (TryComp(gridUid, out MapGridComponent? grid))
            {
                var tile = _map.WorldToTile(gridUid, grid, _transform.GetWorldPosition(xform));
                if (TryGetAccessOnTile(gridUid, grid, tile, out _))
                {
                    MarkAuthorized(mob);
                    Log.Info("SubGrid board authorized (stairs tile): {Mob} on {Grid}", ToPrettyString(mob), ToPrettyString(gridUid));
                    continue;
                }
            }

            Log.Debug("SubGrid eject unauthorized boarder: {Mob} from {Grid}", ToPrettyString(mob), ToPrettyString(gridUid));
            BlockIllegalBoard(mob, xform, null, gridUid);
        }
    }

    private void OnParentChanged(EntityUid uid, MobStateComponent mob, ref EntParentChangedMessage args)
    {
        if (_timing.ApplyingState || _inBounce)
            return;

        var xform = args.Transform;
        var oldParent = args.OldParent;
        var newGrid = xform.GridUid;
        var oldWasSub = oldParent != null && HasComp<SubGridComponent>(oldParent.Value);
        var newIsSub = newGrid != null && HasComp<SubGridComponent>(newGrid.Value);

        if (!oldWasSub && !newIsSub)
            return;

        var ignoring = _ignoreUntil.TryGetValue(uid, out var until) && _timing.CurTime < until;

        // BounceOff reparents away from the pad — ignore that exit. Never ignore boarding onto a SubGrid.
        if (ignoring && oldWasSub && !newIsSub)
        {
            Log.Debug("SubGrid exit ignored (bounce cooldown): {Mob}", ToPrettyString(uid));
            ClearAuthorized(uid);
            return;
        }

        var weightless = _gravity.IsWeightless(uid);
        var atBoarding = IsAtBoardingPoint(uid, out var boardingHit);
        var mobWorld = _transform.GetWorldPosition(xform);

        EntityUid? stairsOnLanding = null;
        Vector2i? landingTile = null;
        var landingIsBoarding = false;
        if (newIsSub && newGrid != null && TryComp(newGrid.Value, out MapGridComponent? newMapGrid))
        {
            landingTile = _map.WorldToTile(newGrid.Value, newMapGrid, mobWorld);
            TryGetAccessOnTile(newGrid.Value, newMapGrid, landingTile.Value, out stairsOnLanding);
            landingIsBoarding = stairsOnLanding != null ||
                (TryComp(newGrid.Value, out SubGridComponent? landSub) &&
                 landSub.BoardingTiles.Contains(landingTile.Value));
        }

        EntityUid? stairsOnExit = null;
        Vector2i? exitTile = null;
        var exitIsBoarding = false;
        if (oldWasSub && oldParent != null && TryComp(oldParent.Value, out MapGridComponent? oldMapGrid))
        {
            exitTile = _map.WorldToTile(oldParent.Value, oldMapGrid, mobWorld);
            TryGetAccessOnTile(oldParent.Value, oldMapGrid, exitTile.Value, out stairsOnExit);
            exitIsBoarding = stairsOnExit != null ||
                (TryComp(oldParent.Value, out SubGridComponent? exitSub) &&
                 exitSub.BoardingTiles.Contains(exitTile.Value));
        }

        Log.Debug(
            "SubGrid traversal: mob={Mob} oldParent={Old} newGrid={New} oldSub={OldSub} newSub={NewSub} atBoarding={Board} boardingHit={Hit} landingTile={Land} stairsOnLanding={StairsLand} exitTile={Exit} stairsOnExit={StairsExit} weightless={WL} ignoring={Ign}",
            ToPrettyString(uid),
            oldParent == null ? "null" : ToPrettyString(oldParent.Value),
            newGrid == null ? "null" : ToPrettyString(newGrid.Value),
            oldWasSub,
            newIsSub,
            atBoarding,
            boardingHit == null ? "none" : ToPrettyString(boardingHit.Value),
            landingTile?.ToString() ?? "n/a",
            stairsOnLanding == null ? "none" : ToPrettyString(stairsOnLanding.Value),
            exitTile?.ToString() ?? "n/a",
            stairsOnExit == null ? "none" : ToPrettyString(stairsOnExit.Value),
            weightless,
            ignoring);

        if (newIsSub && !oldWasSub)
        {
            var allowBoard = weightless || atBoarding || landingIsBoarding;
            if (allowBoard)
            {
                MarkAuthorized(uid);
                var reason = weightless ? "weightless" : atBoarding ? "boarding-point" : "stairs-on-tile";
                Log.Info("SubGrid board allowed: {Mob} reason={Reason}",
                    ToPrettyString(uid), reason);
                return;
            }

            Log.Debug(
                "SubGrid board denied (not at stairs): {Mob} landing={Land} stairs={Stairs}",
                ToPrettyString(uid),
                landingTile?.ToString() ?? "n/a",
                stairsOnLanding == null ? "none" : ToPrettyString(stairsOnLanding.Value));
            // Soft reject: undo parent change. No stun/teleport spam.
            BlockIllegalBoard(uid, xform, oldParent, newGrid!.Value);
            return;
        }

        // SubGrid → SubGrid transfer.
        if (oldWasSub && newIsSub && oldParent != null && newGrid != null && oldParent != newGrid)
        {
            ClearAuthorized(uid);
            MarkAuthorized(uid);
            return;
        }

        if (oldWasSub && !newIsSub)
        {
            var wasAuthorized = _authorizedAboard.Contains(uid);

            // Host steal under the pad footprint is never a legitimate stairs exit —
            // even when atBoarding (stairs still sit on solid SubGrid tiles over the host).
            if (oldParent != null &&
                TryComp(oldParent.Value, out SubGridComponent? oldSubComp) &&
                IsHostSteal(oldSubComp, newGrid, xform.ParentUid) &&
                IsOverSolidSubFloor(oldParent.Value, mobWorld))
            {
                ReclaimOntoSubGrid(uid, xform, oldParent.Value, wasAuthorized);
                return;
            }

            var allowExit = weightless || atBoarding || exitIsBoarding;

            // Stepping off the pad one tile past stairs (e.g. exitTile south of boarding) —
            // parent change fires after leaving BoardingPointRange.
            if (!allowExit && oldParent != null && exitTile != null &&
                TryComp(oldParent.Value, out MapGridComponent? exitGrid))
            {
                foreach (var offset in new[]
                         {
                             new Vector2i(0, 1), new Vector2i(0, -1),
                             new Vector2i(1, 0), new Vector2i(-1, 0),
                         })
                {
                    if (TryGetAccessOnTile(oldParent.Value, exitGrid, exitTile.Value + offset, out _))
                    {
                        allowExit = true;
                        break;
                    }
                }
            }

            ClearAuthorized(uid);

            if (allowExit)
            {
                var reason = weightless ? "weightless" : atBoarding ? "boarding-point" : stairsOnExit != null ? "stairs-on-tile" : "stairs-adjacent";
                Log.Info("SubGrid exit allowed: {Mob} reason={Reason}",
                    ToPrettyString(uid), reason);
                return;
            }

            // Illegal boarders leaving (or bounce leftovers) — no second fall punishment.
            if (!wasAuthorized)
            {
                Log.Debug("SubGrid exit silent (unauthorized): {Mob}", ToPrettyString(uid));
                return;
            }

            Log.Warning(
                "SubGrid fall exit: stun+bruise on {Mob} exitTile={Exit} stairs={Stairs}",
                ToPrettyString(uid),
                exitTile?.ToString() ?? "n/a",
                stairsOnExit == null ? "none" : ToPrettyString(stairsOnExit.Value));
            _stun.TryKnockdown(uid, FallStun, true);
            _damageable.TryChangeDamage(uid,
                new DamageSpecifier { DamageDict = { ["Blunt"] = 8f } });
        }
    }

    private static bool IsHostSteal(SubGridComponent oldSub, EntityUid? newGrid, EntityUid newParent)
    {
        if (oldSub.HostGrid is not { } host)
            return false;
        return newGrid == host || newParent == host;
    }

    private bool IsOverSolidSubFloor(EntityUid subGrid, Vector2 world)
    {
        if (!TryComp(subGrid, out MapGridComponent? grid))
            return false;

        var tile = _map.WorldToTile(subGrid, grid, world);
        return _map.TryGetTileRef(subGrid, grid, tile, out var tileRef) && !tileRef.Tile.IsEmpty;
    }

    /// <summary>Undo GridTraversal host-steal: put the mob back on the SubGrid at the same world pose.</summary>
    private void ReclaimOntoSubGrid(EntityUid uid, TransformComponent xform, EntityUid subGrid, bool wasAuthorized)
    {
        if (_inBounce)
            return;

        _inBounce = true;
        try
        {
            if (!wasAuthorized)
                MarkAuthorized(uid);

            _ignoreUntil[uid] = _timing.CurTime + TimeSpan.FromMilliseconds(100);

            var world = _transform.GetWorldPosition(xform);
            var local = Vector2.Transform(world, _transform.GetInvWorldMatrix(Transform(subGrid)));
            _transform.SetCoordinates(uid, new EntityCoordinates(subGrid, local));

            if (TryComp(uid, out TransformComponent? after))
                after.GridTraversal = false;

            if (TryComp(uid, out PhysicsComponent? body))
                _physics.SetLinearVelocity(uid, Vector2.Zero, body: body);

            Log.Info("SubGrid reclaim after host steal: {Mob} -> {Grid}", ToPrettyString(uid), ToPrettyString(subGrid));
        }
        finally
        {
            _inBounce = false;
        }
    }

    /// <summary>
    /// Denied board: quietly undo parenting. No stun, no large teleport — perimeter rails
    /// should block most attempts; this is the soft fallback when tile parenting still wins.
    /// </summary>
    private void BlockIllegalBoard(EntityUid uid, TransformComponent xform, EntityUid? oldParent, EntityUid deniedSubGrid)
    {
        if (_inBounce)
            return;

        _inBounce = true;
        try
        {
            ClearAuthorized(uid);
            _ignoreUntil[uid] = _timing.CurTime + TimeSpan.FromSeconds(0.2);

            // Nudge just outside the pad footprint (small margin — not a throw).
            if (TryComp(deniedSubGrid, out MapGridComponent? subGrid) &&
                TryComp(deniedSubGrid, out TransformComponent? subXform))
            {
                var (_, _, worldMatrix, invWorld) = _transform.GetWorldPositionRotationMatrixWithInv(subXform);
                var localAabb = subGrid.LocalAABB.Enlarged(0.05f);
                if (!localAabb.IsEmpty())
                {
                    var worldPos = _transform.GetWorldPosition(xform);
                    var local = Vector2.Transform(worldPos, invWorld);
                    if (localAabb.Contains(local))
                    {
                        const float margin = 0.35f;
                        var dxL = local.X - localAabb.Left;
                        var dxR = localAabb.Right - local.X;
                        var dyB = local.Y - localAabb.Bottom;
                        var dyT = localAabb.Top - local.Y;
                        var min = MathF.Min(MathF.Min(dxL, dxR), MathF.Min(dyB, dyT));
                        Vector2 targetLocal;
                        if (min == dxL)
                            targetLocal = new Vector2(localAabb.Left - margin, local.Y);
                        else if (min == dxR)
                            targetLocal = new Vector2(localAabb.Right + margin, local.Y);
                        else if (min == dyB)
                            targetLocal = new Vector2(local.X, localAabb.Bottom - margin);
                        else
                            targetLocal = new Vector2(local.X, localAabb.Top + margin);

                        _transform.SetWorldPosition(uid, Vector2.Transform(targetLocal, worldMatrix));
                    }
                }
            }

            EntityUid? safeParent = oldParent;
            if (safeParent != null && HasComp<SubGridComponent>(safeParent.Value))
                safeParent = null;

            if (safeParent != null && !TerminatingOrDeleted(safeParent.Value))
            {
                var worldPos = _transform.GetWorldPosition(xform);
                var local = Vector2.Transform(worldPos, _transform.GetInvWorldMatrix(Transform(safeParent.Value)));
                _transform.SetCoordinates(uid, new EntityCoordinates(safeParent.Value, local));
            }
            else if (xform.MapUid != null)
            {
                _transform.AttachToGridOrMap(uid, xform);
            }

            if (TryComp(uid, out PhysicsComponent? body))
                _physics.SetLinearVelocity(uid, Vector2.Zero, body: body);
        }
        finally
        {
            _inBounce = false;
        }
    }
}
