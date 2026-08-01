using System.Numerics;
using Content.Server.Shuttles.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Physics;
using Content.Shared._CitadelStation.SubGrid;
using Content.Shared._CitadelStation.SubGrid.Components;
using Content.Shared.Tag;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._CitadelStation.SubGrid.Systems;

/// <summary>
/// Applies movement-mode modifiers (damping, friction, collision damage, arc-rail boost)
/// and hard-blocks walls. Drive input comes from the shuttle console via thrusters.
/// Passenger physics must not shove the hull — that is handled by separate boarding barriers.
/// </summary>
public sealed class SubGridMovementSystem : EntitySystem
{
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly TagSystem _tags = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SubGridHostFollowSystem _hostFollow = default!;

    private static readonly ProtoId<TagPrototype> WallTag = "Wall";
    private static readonly ProtoId<TagPrototype> WindowTag = "Window";

    private const float CarCrushSpeed = 8f;
    private const float TracksCrushSpeed = 1.5f;
    private const int WallResolvePasses = 3;
    private const float MaxSeparation = 1f;

    private readonly Dictionary<EntityUid, TimeSpan> _lastArcSpark = new();
    private readonly Dictionary<EntityUid, bool> _lastArcNearRail = new();
    private readonly List<Box2> _floorBoxes = new();
    private TimeSpan _nextWallLog;
    private int _wallHitsSinceLog;
    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<FixturesComponent> _fixturesQuery;

    public override void Initialize()
    {
        base.Initialize();
        _gridQuery = GetEntityQuery<MapGridComponent>();
        _fixturesQuery = GetEntityQuery<FixturesComponent>();
        SubscribeLocalEvent<SubGridComponent, TileFrictionEvent>(OnTileFriction);
        SubscribeLocalEvent<SubGridComponent, StartCollideEvent>(OnStartCollide);
        Log.Info("SubGridMovementSystem initialized");
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<SubGridComponent, PhysicsComponent, TransformComponent, MapGridComponent>();
        while (query.MoveNext(out var uid, out var sub, out var body, out var xform, out var grid))
        {
            if (!sub.DriveEnabled)
            {
                if (body.LinearVelocity.LengthSquared() > 0.0001f || MathF.Abs(body.AngularVelocity) > 0.0001f)
                {
                    // Host follow owns velocity while parked on a moving grid — don't zero it.
                    if (sub.HostGrid == null)
                    {
                        _physics.SetLinearVelocity(uid, Vector2.Zero, body: body);
                        _physics.SetAngularVelocity(uid, 0f, body: body);
                    }
                }

                // Still resolve walls while parked so a moving host cannot leave us embedded.
                ResolveWallBlocks(uid, sub, body, xform, grid);
                continue;
            }

            switch (sub.Mode)
            {
                case SubGridMovementMode.Tracks:
                    if (IsNearForeignGrid(uid, xform))
                        _physics.SetLinearDamping(uid, body, MathF.Max(body.LinearDamping, 2.5f));
                    break;
                case SubGridMovementMode.Hover:
                    if (body.LinearVelocity.LengthSquared() > 0.0001f)
                    {
                        _physics.SetLinearVelocity(uid, body.LinearVelocity * 0.85f, body: body);
                        _physics.SetAngularVelocity(uid, body.AngularVelocity * 0.85f, body: body);
                    }
                    break;
                case SubGridMovementMode.ArcHover:
                    ApplyArcHover(uid, body, xform);
                    break;
            }

            // Hard-stop against real walls/windows (all drive modes).
            // Station MapGrid tiles still phase via PreventCollide — only wall entities block.
            ResolveWallBlocks(uid, sub, body, xform, grid);
        }
    }

    public void ApplyMode(EntityUid gridUid, SubGridMovementMode mode, bool enabled)
    {
        if (!TryComp<ShuttleComponent>(gridUid, out var shuttle) ||
            !TryComp<PhysicsComponent>(gridUid, out var body))
        {
            Log.Warning("ApplyMode missing comps on {Grid} shuttle={HasShuttle} body={HasBody}",
                ToPrettyString(gridUid),
                HasComp<ShuttleComponent>(gridUid),
                HasComp<PhysicsComponent>(gridUid));
            return;
        }

        if (!enabled)
        {
            // High tile friction + damping freezes the pad without making it Static.
            shuttle.BodyModifier = 2.5f;
            shuttle.DampingModifier = shuttle.BodyModifier;
            _physics.SetLinearDamping(gridUid, body, 8f);
            _physics.SetAngularDamping(gridUid, body, 8f);
            Log.Debug("ApplyMode drive off defaults on {Grid}", ToPrettyString(gridUid));
            return;
        }

        // Fighter = normal shuttle profile (BodyModifier matches ShuttleComponent default).
        shuttle.BodyModifier = mode switch
        {
            SubGridMovementMode.Fighter => 0.25f,
            SubGridMovementMode.Tracks => 0.35f,
            SubGridMovementMode.Car => 0.04f,
            SubGridMovementMode.Hover => 0.2f,
            SubGridMovementMode.ArcHover => 0.15f,
            _ => 0.25f,
        };
        shuttle.DampingModifier = shuttle.BodyModifier;
        // Fighter = free yaw like a normal shuttle; other modes lock rotation.
        var fixedRot = mode is not SubGridMovementMode.Fighter;
        _physics.SetFixedRotation(gridUid, fixedRot, body: body);
        if (mode == SubGridMovementMode.Fighter)
        {
            // Same as ShuttleSystem.Enable: Dynamic InAir, no extra damping.
            _physics.SetLinearDamping(gridUid, body, 0f);
            _physics.SetAngularDamping(gridUid, body, 0f);
        }
        else
        {
            _physics.SetLinearDamping(gridUid, body, mode == SubGridMovementMode.Car ? 0.35f : 0.8f);
            _physics.SetAngularDamping(gridUid, body, fixedRot ? 8f : 0.8f);
        }
        Log.Info("ApplyMode {Mode} on {Grid}: bodyModifier={Mod} fixedRot={Fixed} linDamp={Damp}",
            mode, ToPrettyString(gridUid), shuttle.BodyModifier, fixedRot, body.LinearDamping);
    }

    /// <summary>
    /// Hard stop against real walls/windows. Restitution 0 — kill inbound velocity
    /// and separate via position, no bounce impulse. Runs after drive forces / host follow.
    /// Uses per-floor-tile boxes in SubGrid-local space (oriented) so rotation does not inflate
    /// the bumper into a world AABB. Separation is baked into HostLocalPosition so parked
    /// host-follow does not re-embed next tick.
    /// </summary>
    private void ResolveWallBlocks(
        EntityUid uid,
        SubGridComponent sub,
        PhysicsComponent body,
        TransformComponent xform,
        MapGridComponent grid)
    {
        if (xform.MapID == MapId.Nullspace || xform.MapUid == null)
            return;

        CollectFloorBoxes(uid, sub, grid);
        if (_floorBoxes.Count == 0)
            return;

        var hit = false;
        var totalSeparation = Vector2.Zero;

        for (var pass = 0; pass < WallResolvePasses; pass++)
        {
            var worldMat = _transform.GetWorldMatrix(xform);
            var invMat = _transform.GetInvWorldMatrix(xform);
            var worldRot = _transform.GetWorldRotation(xform);

            var ourWorldAabb = Box2.Empty;
            var hasHull = false;
            foreach (var local in _floorBoxes)
            {
                var world = worldMat.TransformBox(local);
                ourWorldAabb = hasHull ? ourWorldAabb.Union(world) : world;
                hasHull = true;
            }

            if (!hasHull)
                return;

            ourWorldAabb = ourWorldAabb.Enlarged(0.01f);
            var separation = Vector2.Zero;

            foreach (var other in _lookup.GetEntitiesIntersecting(xform.MapID, ourWorldAabb, LookupFlags.Static))
            {
                if (other == uid)
                    continue;
                if (_gridQuery.HasComp(other))
                    continue;
                if (HasComp<SubGridPerimeterComponent>(other))
                    continue;

                if (!TryComp(other, out TransformComponent? otherXform) || otherXform.MapUid == null)
                    continue;
                if (otherXform.GridUid == uid || otherXform.ParentUid == uid)
                    continue;

                if (!_tags.HasTag(other, WallTag) && !_tags.HasTag(other, WindowTag))
                    continue;

                if (!_fixturesQuery.TryGetComponent(other, out var fixtures))
                    continue;

                var otherXformPhys = _physics.GetRelativePhysicsTransform((other, otherXform), otherXform.MapUid.Value);
                var otherAabb = Box2.Empty;
                var hasOther = false;
                foreach (var fix in fixtures.Fixtures.Values)
                {
                    if (!fix.Hard)
                        continue;
                    if ((fix.CollisionLayer & (int) CollisionGroup.Impassable) == 0)
                        continue;
                    var box = fix.Shape.ComputeAABB(otherXformPhys, 0);
                    otherAabb = hasOther ? otherAabb.Union(box) : box;
                    hasOther = true;
                }

                if (!hasOther || !ourWorldAabb.Intersects(otherAabb))
                    continue;

                // Oriented resolve: wall AABB → SubGrid local, MTV vs each floor tile, rotate back.
                var wallLocal = invMat.TransformBox(otherAabb);
                var mtvLocal = Vector2.Zero;
                foreach (var floor in _floorBoxes)
                {
                    if (!TryAabbMtv(floor, wallLocal, out var tileMtv))
                        continue;
                    if (tileMtv.LengthSquared() > mtvLocal.LengthSquared())
                        mtvLocal = tileMtv;
                }

                if (mtvLocal.LengthSquared() <= 0.0001f)
                    continue;

                var mtv = worldRot.RotateVec(mtvLocal);
                if (mtv.LengthSquared() > separation.LengthSquared())
                    separation = mtv;

                var n = mtv.LengthSquared() > 0.0001f ? mtv.Normalized() : Vector2.UnitX;
                var vel = body.LinearVelocity;
                var into = Vector2.Dot(vel, -n);
                if (into > 0f)
                    _physics.SetLinearVelocity(uid, vel + n * into, body: body);
                hit = true;
            }

            if (separation.LengthSquared() <= 0.0001f)
                break;

            if (separation.Length() > MaxSeparation)
                separation = separation.Normalized() * MaxSeparation;

            var pos = _transform.GetWorldPosition(xform);
            _transform.SetWorldPosition(uid, pos + separation);
            totalSeparation += separation;

            var velAfter = body.LinearVelocity;
            var nSep = separation.Normalized();
            var intoSep = Vector2.Dot(velAfter, -nSep);
            if (intoSep > 0f)
                _physics.SetLinearVelocity(uid, velAfter + nSep * intoSep, body: body);
            hit = true;
        }

        if (hit && totalSeparation.LengthSquared() > 0.0001f)
            BakeSeparationIntoHostLocal(uid, sub);

        if (!hit)
            return;

        _wallHitsSinceLog++;

        if (_timing.CurTime < _nextWallLog)
            return;

        Log.Debug(
            "SubGrid wall resolve: {Grid} mode={Mode} hits={Hits} speed={Speed:F2}",
            ToPrettyString(uid),
            sub.Mode,
            _wallHitsSinceLog,
            body.LinearVelocity.Length());
        _wallHitsSinceLog = 0;
        _nextWallLog = _timing.CurTime + TimeSpan.FromSeconds(1);
    }

    private void CollectFloorBoxes(EntityUid uid, SubGridComponent sub, MapGridComponent grid)
    {
        _floorBoxes.Clear();
        var tileSize = grid.TileSize;
        var half = MathF.Max(0.35f, tileSize * 0.5f + sub.BumperEnlarge);
        var tileEnum = _map.GetAllTilesEnumerator(uid, grid);
        while (tileEnum.MoveNext(out var tileRefNullable))
        {
            if (tileRefNullable is not { } tileRef || tileRef.Tile.IsEmpty)
                continue;

            var i = tileRef.GridIndices;
            var cx = (i.X + 0.5f) * tileSize;
            var cy = (i.Y + 0.5f) * tileSize;
            _floorBoxes.Add(Box2.CenteredAround(new Vector2(cx, cy), new Vector2(half * 2f, half * 2f)));
        }
    }

    /// <summary>MTV to move <paramref name="a"/> out of <paramref name="b"/> (AABB, axis-aligned).</summary>
    private static bool TryAabbMtv(Box2 a, Box2 b, out Vector2 mtv)
    {
        mtv = Vector2.Zero;
        if (!a.Intersects(b))
            return false;

        var overlapX = MathF.Min(a.Right, b.Right) - MathF.Max(a.Left, b.Left);
        var overlapY = MathF.Min(a.Top, b.Top) - MathF.Max(a.Bottom, b.Bottom);
        if (overlapX <= 0f || overlapY <= 0f)
            return false;

        if (overlapX < overlapY)
        {
            var sign = a.Center.X < b.Center.X ? -1f : 1f;
            mtv = new Vector2(sign * overlapX, 0f);
        }
        else
        {
            var sign = a.Center.Y < b.Center.Y ? -1f : 1f;
            mtv = new Vector2(0f, sign * overlapY);
        }

        return mtv.LengthSquared() > 0.0001f;
    }

    /// <summary>
    /// Persist wall separation into host-local pose so the next HostFollow tick does not snap back.
    /// </summary>
    private void BakeSeparationIntoHostLocal(EntityUid uid, SubGridComponent sub)
    {
        if (sub.HostGrid is not { } host || !sub.HostPoseValid || Deleted(host))
            return;

        var hostXform = Transform(host);
        var (hostPos, hostRot) = _transform.GetWorldPositionRotation(hostXform);
        var (subPos, subRot) = _transform.GetWorldPositionRotation(uid);
        sub.HostLocalPosition = (-hostRot).RotateVec(subPos - hostPos);
        sub.HostLocalRotation = subRot - hostRot;
        Dirty(uid, sub);
        _hostFollow.RefreshHostWeldAnchors(uid, sub);
    }

    private void OnTileFriction(Entity<SubGridComponent> ent, ref TileFrictionEvent args)
    {
        // Host ride owns world velocity (host + relative). World-space tile friction
        // would bleed off the host component and leave the pad glued to the map.
        // Relative damping is applied in SubGridHostFollowSystem instead.
        if (ent.Comp.HostGrid != null)
        {
            args.Modifier = 0f;
            return;
        }

        if (!ent.Comp.DriveEnabled)
            return;

        switch (ent.Comp.Mode)
        {
            case SubGridMovementMode.Hover:
            case SubGridMovementMode.ArcHover:
                args.Modifier *= 0.2f;
                break;
            case SubGridMovementMode.Tracks:
                args.Modifier *= 0.6f;
                break;
            case SubGridMovementMode.Car:
                args.Modifier *= 0.15f;
                break;
            case SubGridMovementMode.Fighter:
                // Shuttle-like: BodyModifier already mirrors ShuttleComponent; no extra cut.
                break;
        }
    }

    private void OnStartCollide(Entity<SubGridComponent> ent, ref StartCollideEvent args)
    {
        var isHull = args.OurFixtureId == SubGridComponent.BumperFixtureId
                     || args.OurFixtureId.StartsWith(SubGridComponent.DeckFixturePrefix, StringComparison.Ordinal);

        if (isHull)
        {
            var otherLayer = args.OtherFixture.CollisionLayer;
            var hitsImpassable = (otherLayer & (int) CollisionGroup.Impassable) != 0
                                || (otherLayer & (int) CollisionGroup.HighImpassable) != 0;

            Log.Debug(
                "SubGrid hard collide: {Grid} fixture={Fix} vs {Other} otherFix={OtherFix} impassable={Imp} speed={Speed:F2} drive={Drive} ourBody={Body}",
                ToPrettyString(ent),
                args.OurFixtureId,
                ToPrettyString(args.OtherEntity),
                args.OtherFixtureId,
                hitsImpassable,
                args.OurBody.LinearVelocity.Length(),
                ent.Comp.DriveEnabled,
                args.OurBody.BodyType);
        }

        if (!ent.Comp.DriveEnabled)
            return;

        var isCrushFixture = args.OurFixtureId == SubGridComponent.BumperFixtureId
                             || args.OurFixtureId.StartsWith(SubGridComponent.DeckFixturePrefix, StringComparison.Ordinal);
        if (!isCrushFixture)
            return;

        var other = args.OtherEntity;
        var speed = args.OurBody.LinearVelocity.Length();
        Log.Debug(
            "SubGrid bumper collide: {Grid} mode={Mode} other={Other} fixture={Fix} speed={Speed:F2} damageable={Dmg}",
            ToPrettyString(ent),
            ent.Comp.Mode,
            ToPrettyString(other),
            args.OtherFixtureId,
            speed,
            HasComp<DamageableComponent>(other));

        if (!HasComp<DamageableComponent>(other))
            return;

        switch (ent.Comp.Mode)
        {
            case SubGridMovementMode.Tracks:
                if (speed < TracksCrushSpeed)
                {
                    Log.Debug("Tracks crush skipped (slow): {Grid} speed={Speed:F2} need={Need:F2}",
                        ToPrettyString(ent), speed, TracksCrushSpeed);
                    return;
                }
                Log.Debug("Tracks crush: {Grid} -> {Other} speed={Speed:F2}",
                    ToPrettyString(ent), ToPrettyString(other), speed);
                _damageable.TryChangeDamage(other,
                    new DamageSpecifier { DamageDict = { ["Blunt"] = 15f * (speed / 4f) } });
                break;
            case SubGridMovementMode.Car:
                if (speed < CarCrushSpeed)
                {
                    Log.Debug("Car crush skipped (slow): {Grid} speed={Speed:F2} need={Need:F2}",
                        ToPrettyString(ent), speed, CarCrushSpeed);
                    return;
                }
                Log.Debug("Car crush: {Grid} -> {Other} speed={Speed:F2}",
                    ToPrettyString(ent), ToPrettyString(other), speed);
                _damageable.TryChangeDamage(other,
                    new DamageSpecifier { DamageDict = { ["Blunt"] = 10f * (speed / CarCrushSpeed) } });
                break;
            case SubGridMovementMode.Hover:
                if ((args.OtherFixture.CollisionLayer & (int) CollisionGroup.Impassable) == 0)
                {
                    Log.Debug("Hover bumper hit non-impassable: {Grid} -> {Other}",
                        ToPrettyString(ent), ToPrettyString(other));
                    return;
                }
                Log.Debug("Hover bumper hit impassable: {Grid} -> {Other}",
                    ToPrettyString(ent), ToPrettyString(other));
                break;
        }
    }

    private void ApplyArcHover(EntityUid uid, PhysicsComponent body, TransformComponent xform)
    {
        var nearRail = false;

        foreach (var other in _lookup.GetEntitiesInRange(uid, 1.75f))
        {
            if (other == uid)
                continue;

            if (_tags.HasTag(other, WallTag) || _tags.HasTag(other, WindowTag))
            {
                nearRail = true;
                break;
            }
        }

        if (!_lastArcNearRail.TryGetValue(uid, out var wasNear) || wasNear != nearRail)
        {
            Log.Debug("ArcHover rail state {Grid}: nearRail={Near}", ToPrettyString(uid), nearRail);
            _lastArcNearRail[uid] = nearRail;
        }

        if (nearRail)
        {
            _physics.SetLinearDamping(uid, body, 0.05f);
            var facing = _transform.GetWorldRotation(xform).ToWorldVec();
            if (facing != Vector2.Zero)
            {
                facing = facing.Normalized();
                var vel = body.LinearVelocity;
                var forward = Vector2.Dot(vel, facing) * facing;
                var lateral = vel - forward;
                _physics.SetLinearVelocity(uid, forward + lateral * 0.5f, body: body);
            }

            if (!_lastArcSpark.TryGetValue(uid, out var last) ||
                _timing.CurTime > last + TimeSpan.FromSeconds(0.75))
            {
                _lastArcSpark[uid] = _timing.CurTime;
                var shocked = 0;
                foreach (var mob in _lookup.GetEntitiesInRange(uid, 1.2f))
                {
                    if (mob == uid || HasComp<MapGridComponent>(mob))
                        continue;
                    if (!HasComp<MobStateComponent>(mob))
                        continue;

                    _damageable.TryChangeDamage(mob,
                        new DamageSpecifier { DamageDict = { ["Shock"] = 4f } });
                    shocked++;
                }

                if (shocked > 0)
                    Log.Debug("ArcHover spark shocked {Count} mobs near {Grid}", shocked, ToPrettyString(uid));
            }
        }
        else
        {
            _physics.SetLinearDamping(uid, body, MathF.Max(body.LinearDamping, 4f));
        }
    }

    private bool IsNearForeignGrid(EntityUid self, TransformComponent xform)
    {
        foreach (var other in _lookup.GetEntitiesInRange(self, 4f))
        {
            if (other == self)
                continue;
            if (HasComp<MapGridComponent>(other))
                return true;
        }

        return false;
    }
}
