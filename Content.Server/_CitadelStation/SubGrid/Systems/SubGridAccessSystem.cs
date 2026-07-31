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
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SubGridDebugLog _dbg = default!;
    [Dependency] private readonly SharedSubGridCollisionSystem _collision = default!;

    private const float BoardingPointRange = 0.55f;
    private static readonly TimeSpan FallStun = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan UnderCheckInterval = TimeSpan.FromSeconds(0.05);

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
        Log.Info("SubGridAccessSystem initialized (board debug -> {Path})", _dbg.Path);
    }

    /// <summary>True if the entity is on this SubGrid's boarded whitelist.</summary>
    public bool IsBarrierWhitelisted(EntityUid subGrid, EntityUid entity)
    {
        return TryComp(subGrid, out SubGridComponent? sg) && sg.BarrierPassThrough.Contains(entity);
    }

    /// <summary>
    /// True if boarding barriers for this SubGrid must ignore the entity:
    /// already aboard (whitelist), or currently at this SubGrid's stairs/dock access.
    /// </summary>
    public bool HasBarrierPass(EntityUid subGrid, EntityUid entity)
    {
        return _collision.HasBarrierPass(subGrid, entity);
    }

    private void GrantBarrierPass(EntityUid subGrid, EntityUid mob)
    {
        _authorizedAboard.Add(mob);
        if (!TryComp(subGrid, out SubGridComponent? sg))
            return;

        if (!sg.BarrierPassThrough.Add(mob))
            return;

        Dirty(subGrid, sg);
        _dbg.Write("access.pass+", $"mob={ToPrettyString(mob)} grid={ToPrettyString(subGrid)} count={sg.BarrierPassThrough.Count}");

        // Drop any lingering barrier contacts so movement isn't jerky for a few ticks.
        if (TryComp(mob, out PhysicsComponent? body))
            _physics.RegenerateContacts((mob, body));
    }

    private void RevokeBarrierPass(EntityUid mob, EntityUid? subGrid = null)
    {
        _authorizedAboard.Remove(mob);

        if (subGrid != null && TryComp(subGrid.Value, out SubGridComponent? one))
        {
            if (one.BarrierPassThrough.Remove(mob))
            {
                Dirty(subGrid.Value, one);
                _dbg.Write("access.pass-", $"mob={ToPrettyString(mob)} grid={ToPrettyString(subGrid.Value)} count={one.BarrierPassThrough.Count}");
            }
            return;
        }

        var query = EntityQueryEnumerator<SubGridComponent>();
        while (query.MoveNext(out var gridUid, out var sg))
        {
            if (!sg.BarrierPassThrough.Remove(mob))
                continue;

            Dirty(gridUid, sg);
            _dbg.Write("access.pass-", $"mob={ToPrettyString(mob)} grid={ToPrettyString(gridUid)} count={sg.BarrierPassThrough.Count}");
        }
    }

    private void OnMobTerminating(EntityUid uid, MobStateComponent mob, ref EntityTerminatingEvent args)
    {
        RevokeBarrierPass(uid);
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
        var coords = _transform.GetMoverCoordinates(mob);
        foreach (var ent in _lookup.GetEntitiesInRange(coords, BoardingPointRange))
        {
            if (!HasComp<SubGridAccessComponent>(ent))
                continue;

            accessHit = ent;
            return true;
        }

        return false;
    }

    private bool TryGetAccessOnTile(EntityUid gridUid, MapGridComponent grid, Vector2i indices, out EntityUid? access)
    {
        access = null;
        foreach (var ent in _map.GetAnchoredEntities(gridUid, grid, indices))
        {
            if (!HasComp<SubGridAccessComponent>(ent))
                continue;

            access = ent;
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

            if (_authorizedAboard.Contains(mob) || IsBarrierWhitelisted(gridUid, mob))
                continue;

            if (_gravity.IsWeightless(mob) || IsAtBoardingPoint(mob))
            {
                GrantBarrierPass(gridUid, mob);
                Log.Info("SubGrid board authorized (late): {Mob} on {Grid}", ToPrettyString(mob), ToPrettyString(gridUid));
                continue;
            }

            if (TryComp(gridUid, out MapGridComponent? grid))
            {
                var tile = _map.WorldToTile(gridUid, grid, _transform.GetWorldPosition(xform));
                if (TryGetAccessOnTile(gridUid, grid, tile, out _))
                {
                    GrantBarrierPass(gridUid, mob);
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
            RevokeBarrierPass(uid, oldParent);
            return;
        }

        var weightless = _gravity.IsWeightless(uid);
        var atBoarding = IsAtBoardingPoint(uid, out var boardingHit);
        var mobWorld = _transform.GetWorldPosition(xform);

        EntityUid? stairsOnLanding = null;
        Vector2i? landingTile = null;
        if (newIsSub && newGrid != null && TryComp(newGrid.Value, out MapGridComponent? newMapGrid))
        {
            landingTile = _map.WorldToTile(newGrid.Value, newMapGrid, mobWorld);
            TryGetAccessOnTile(newGrid.Value, newMapGrid, landingTile.Value, out stairsOnLanding);
        }

        EntityUid? stairsOnExit = null;
        Vector2i? exitTile = null;
        if (oldWasSub && oldParent != null && TryComp(oldParent.Value, out MapGridComponent? oldMapGrid))
        {
            exitTile = _map.WorldToTile(oldParent.Value, oldMapGrid, mobWorld);
            TryGetAccessOnTile(oldParent.Value, oldMapGrid, exitTile.Value, out stairsOnExit);
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

        _dbg.Write(
            "access.traverse",
            $"mob={ToPrettyString(uid)} oldParent={(oldParent == null ? "null" : ToPrettyString(oldParent.Value))} newGrid={(newGrid == null ? "null" : ToPrettyString(newGrid.Value))} oldSub={oldWasSub} newSub={newIsSub} atBoarding={atBoarding} boardingHit={(boardingHit == null ? "none" : ToPrettyString(boardingHit.Value))} landing={landingTile?.ToString() ?? "n/a"} stairsLand={(stairsOnLanding == null ? "none" : ToPrettyString(stairsOnLanding.Value))} exit={exitTile?.ToString() ?? "n/a"} stairsExit={(stairsOnExit == null ? "none" : ToPrettyString(stairsOnExit.Value))} wl={weightless} ignoring={ignoring} world={mobWorld}");

        if (newIsSub && !oldWasSub)
        {
            var allowBoard = weightless || atBoarding || stairsOnLanding != null;
            if (allowBoard)
            {
                GrantBarrierPass(newGrid!.Value, uid);
                var reason = weightless ? "weightless" : atBoarding ? "boarding-point" : "stairs-on-tile";
                Log.Info("SubGrid board allowed: {Mob} reason={Reason}",
                    ToPrettyString(uid), reason);
                _dbg.Write("access.board", $"ALLOW mob={ToPrettyString(uid)} reason={reason} landing={landingTile}");
                return;
            }

            Log.Debug(
                "SubGrid board denied (not at stairs): {Mob} landing={Land} stairs={Stairs}",
                ToPrettyString(uid),
                landingTile?.ToString() ?? "n/a",
                stairsOnLanding == null ? "none" : ToPrettyString(stairsOnLanding.Value));
            _dbg.Write("access.board",
                $"DENY mob={ToPrettyString(uid)} landing={landingTile?.ToString() ?? "n/a"} stairs={(stairsOnLanding == null ? "none" : ToPrettyString(stairsOnLanding.Value))}");
            // Soft reject: undo parent change. No stun/teleport spam.
            BlockIllegalBoard(uid, xform, oldParent, newGrid!.Value);
            return;
        }

        // SubGrid → SubGrid transfer.
        if (oldWasSub && newIsSub && oldParent != null && newGrid != null && oldParent != newGrid)
        {
            RevokeBarrierPass(uid, oldParent);
            GrantBarrierPass(newGrid.Value, uid);
            _dbg.Write("access.transfer",
                $"mob={ToPrettyString(uid)} from={ToPrettyString(oldParent.Value)} to={ToPrettyString(newGrid.Value)}");
            return;
        }

        if (oldWasSub && !newIsSub)
        {
            var wasAuthorized = _authorizedAboard.Contains(uid) ||
                                (oldParent != null && IsBarrierWhitelisted(oldParent.Value, uid));
            RevokeBarrierPass(uid, oldParent);
            var allowExit = weightless || atBoarding || stairsOnExit != null;

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

            if (allowExit)
            {
                var reason = weightless ? "weightless" : atBoarding ? "boarding-point" : stairsOnExit != null ? "stairs-on-tile" : "stairs-adjacent";
                Log.Info("SubGrid exit allowed: {Mob} reason={Reason}",
                    ToPrettyString(uid), reason);
                _dbg.Write("access.exit", $"ALLOW mob={ToPrettyString(uid)} reason={reason} exitTile={exitTile} wasAuth={wasAuthorized}");
                return;
            }

            // Illegal boarders leaving (or bounce leftovers) — no second fall punishment.
            if (!wasAuthorized)
            {
                Log.Debug("SubGrid exit silent (unauthorized): {Mob}", ToPrettyString(uid));
                _dbg.Write("access.exit", $"SILENT (unauthorized) mob={ToPrettyString(uid)} exitTile={exitTile}");
                return;
            }

            Log.Warning(
                "SubGrid fall exit: stun+bruise on {Mob} exitTile={Exit} stairs={Stairs}",
                ToPrettyString(uid),
                exitTile?.ToString() ?? "n/a",
                stairsOnExit == null ? "none" : ToPrettyString(stairsOnExit.Value));
            _dbg.Write("access.exit",
                $"FALL stun mob={ToPrettyString(uid)} exitTile={exitTile?.ToString() ?? "n/a"} stairs={(stairsOnExit == null ? "none" : ToPrettyString(stairsOnExit.Value))}");
            _stun.TryKnockdown(uid, FallStun, true);
            _damageable.TryChangeDamage(uid,
                new DamageSpecifier { DamageDict = { ["Blunt"] = 8f } });
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
            RevokeBarrierPass(uid, deniedSubGrid);
            _ignoreUntil[uid] = _timing.CurTime + TimeSpan.FromSeconds(0.2);
            _dbg.Write("access.block",
                $"BlockIllegalBoard mob={ToPrettyString(uid)} deniedSub={ToPrettyString(deniedSubGrid)} oldParent={(oldParent == null ? "null" : ToPrettyString(oldParent.Value))}");

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
