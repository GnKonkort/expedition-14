using System.Numerics;
using Content.Shared.Doors.Components;
using Content.Shared.Projectiles;
using Robust.Shared.Map;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;

namespace Content.Shared.Cover;

/// <summary>
/// Soft-cover bullet / hitscan rules for directional barricades and probabilistic tables.
/// </summary>
public sealed class SharedCoverSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private EntityQuery<DirectionalCoverComponent> _directionalQuery;
    private EntityQuery<ProbabilisticCoverComponent> _probQuery;
    private EntityQuery<ProjectileComponent> _projectileQuery;
    private EntityQuery<DoorComponent> _doorQuery;

    public override void Initialize()
    {
        base.Initialize();

        _directionalQuery = GetEntityQuery<DirectionalCoverComponent>();
        _probQuery = GetEntityQuery<ProbabilisticCoverComponent>();
        _projectileQuery = GetEntityQuery<ProjectileComponent>();
        _doorQuery = GetEntityQuery<DoorComponent>();

        SubscribeLocalEvent<DirectionalCoverComponent, PreventCollideEvent>(OnDirectionalPreventCollide);
        SubscribeLocalEvent<ProbabilisticCoverComponent, PreventCollideEvent>(OnProbabilisticPreventCollide);
    }

    private void OnDirectionalPreventCollide(Entity<DirectionalCoverComponent> ent, ref PreventCollideEvent args)
    {
        if (args.Cancelled || !_projectileQuery.TryGetComponent(args.OtherEntity, out var projectile))
            return;

        if (IsCoverInactive(ent))
        {
            args.Cancelled = true;
            return;
        }

        var shooter = projectile.Shooter;
        var origin = ResolveShotOrigin(args.OtherEntity, shooter);
        if (!ShouldDirectionalBlock(ent, origin))
            args.Cancelled = true;
    }

    private void OnProbabilisticPreventCollide(Entity<ProbabilisticCoverComponent> ent, ref PreventCollideEvent args)
    {
        if (args.Cancelled || !_projectileQuery.TryGetComponent(args.OtherEntity, out var projectile))
            return;

        var shooter = projectile.Shooter;
        var origin = ResolveShotOrigin(args.OtherEntity, shooter);
        if (!ShouldProbabilisticBlock(ent, origin, args.OtherEntity))
            args.Cancelled = true;
    }

    /// <summary>
    /// True when this cover entity should stop a hitscan/projectile originating at <paramref name="shotOrigin"/>.
    /// </summary>
    public bool ShouldBlockShot(EntityUid cover, MapCoordinates shotOrigin, EntityUid? seedEntity = null, EntityUid? shooter = null)
    {
        if (IsCoverInactive(cover))
            return false;

        if (_directionalQuery.HasComponent(cover))
            return ShouldDirectionalBlock(cover, shotOrigin);

        if (_probQuery.TryGetComponent(cover, out var prob))
            return ShouldProbabilisticBlock((cover, prob), shotOrigin, seedEntity ?? cover);

        return false;
    }

    /// <summary>
    /// Whether a soft-cover entity participates in cover queries (closed door / not deleted).
    /// </summary>
    public bool IsCoverActive(EntityUid cover)
    {
        return !IsCoverInactive(cover);
    }

    private bool IsCoverInactive(EntityUid cover)
    {
        if (!Exists(cover))
            return true;

        // Folding metal barricades use Door — open means folded flat, no cover.
        if (_doorQuery.TryGetComponent(cover, out var door) && door.State != DoorState.Closed)
            return true;

        return false;
    }

    private MapCoordinates ResolveShotOrigin(EntityUid projectile, EntityUid? shooter)
    {
        if (shooter is { } s && Exists(s))
            return _transform.GetMapCoordinates(s);

        return _transform.GetMapCoordinates(projectile);
    }

    /// <summary>
    /// Local south is the barricade face. Shots whose origin lies in the front hemisphere are blocked.
    /// </summary>
    public bool ShouldDirectionalBlock(EntityUid cover, MapCoordinates shotOrigin)
    {
        var coverXform = Transform(cover);
        var coverMap = _transform.GetMapCoordinates(cover, xform: coverXform);
        if (coverMap.MapId != shotOrigin.MapId)
            return false;

        var faceWorld = _transform.GetWorldRotation(coverXform).RotateVec(new Vector2(0f, -1f));
        var toOrigin = shotOrigin.Position - coverMap.Position;
        if (toOrigin.LengthSquared() < 0.0001f)
            return true;

        // Origin in front of the face → incoming from attack side → block.
        return Vector2.Dot(toOrigin, faceWorld) > 0f;
    }

    public bool ShouldProbabilisticBlock(Entity<ProbabilisticCoverComponent> cover, MapCoordinates shotOrigin, EntityUid seedEntity)
    {
        var coverMap = _transform.GetMapCoordinates(cover);
        if (coverMap.MapId != shotOrigin.MapId)
            return false;

        var dist = (shotOrigin.Position - coverMap.Position).Length();
        if (dist <= cover.Comp.AdjacentPassRange)
            return false;

        // Deterministic per (projectile/seed, cover) so predicted collide matches server.
        var hash = HashCode.Combine(GetNetEntity(seedEntity), GetNetEntity(cover.Owner));
        var roll = (hash & 0xFFFF) / (float)0xFFFF;
        return roll < cover.Comp.BlockChance;
    }

    /// <summary>
    /// World-space unit vector pointing out of the barricade's front (local south).
    /// </summary>
    public Vector2 GetDirectionalCoverFace(EntityUid cover)
    {
        return _transform.GetWorldRotation(cover).RotateVec(new Vector2(0f, -1f));
    }

    /// <summary>
    /// Grid offset one tile behind the face (defender side).
    /// Uses dominant-axis snap — <see cref="MathF.Round"/> midpoints to 0 and yields Vector2i.Zero on diagonals.
    /// </summary>
    public Vector2i GetDefenderApproachOffset(EntityUid cover)
    {
        return DominantAxis(-GetDirectionalCoverFace(cover));
    }

    /// <summary>
    /// Snap a world direction to a single cardinal grid step (never Zero unless input is Zero).
    /// </summary>
    public static Vector2i DominantAxis(Vector2 dir)
    {
        if (dir.LengthSquared() < 0.0001f)
            return Vector2i.Zero;

        if (MathF.Abs(dir.X) >= MathF.Abs(dir.Y))
            return new Vector2i(Math.Sign(dir.X), 0);

        return new Vector2i(0, Math.Sign(dir.Y));
    }
}
