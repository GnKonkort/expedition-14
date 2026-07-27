using System.Numerics;
using Content.Shared.Doors.Components;
using Content.Shared.Projectiles;
using Robust.Shared.Map;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Shared.Cover;

/// <summary>
/// Soft-cover bullet / hitscan rules for directional barricades and probabilistic tables.
/// </summary>
public sealed class SharedCoverSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

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
        if (!ShouldDirectionalBlock(ent, origin) ||
            !RollBlockChance(ent.Owner, args.OtherEntity, ent.Comp.BlockChance))
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

    public bool ShouldBlockShot(
        EntityUid cover,
        MapCoordinates shotOrigin,
        EntityUid? seedEntity = null,
        EntityUid? shooter = null,
        int entropy = 0)
    {
        if (IsCoverInactive(cover))
            return false;

        var seed = seedEntity ?? cover;

        if (_directionalQuery.TryGetComponent(cover, out var directional))
        {
            if (!ShouldDirectionalBlock(cover, shotOrigin))
                return false;

            return RollBlockChance(cover, seed, directional.BlockChance, entropy);
        }

        if (_probQuery.TryGetComponent(cover, out var prob))
            return ShouldProbabilisticBlock((cover, prob), shotOrigin, seed, entropy);

        return false;
    }

    public bool IsCoverActive(EntityUid cover)
    {
        return !IsCoverInactive(cover);
    }

    private bool IsCoverInactive(EntityUid cover)
    {
        if (!Exists(cover))
            return true;

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

    public bool ShouldDirectionalBlock(EntityUid cover, MapCoordinates shotOrigin)
    {
        if (!TryComp(cover, out TransformComponent? coverXform))
            return false;

        var coverMap = _transform.GetMapCoordinates(cover, xform: coverXform);
        if (coverMap.MapId != shotOrigin.MapId)
            return false;

        var faceWorld = _transform.GetWorldRotation(coverXform).RotateVec(new Vector2(0f, -1f));
        var toOrigin = shotOrigin.Position - coverMap.Position;
        if (toOrigin.LengthSquared() < 0.0001f)
            return true;

        return Vector2.Dot(toOrigin, faceWorld) > 0f;
    }

    public bool ShouldProbabilisticBlock(
        Entity<ProbabilisticCoverComponent> cover,
        MapCoordinates shotOrigin,
        EntityUid seedEntity,
        int entropy = 0)
    {
        var coverMap = _transform.GetMapCoordinates(cover);
        if (coverMap.MapId != shotOrigin.MapId)
            return false;

        var dist = (shotOrigin.Position - coverMap.Position).Length();
        if (dist <= cover.Comp.AdjacentPassRange)
            return false;

        return RollBlockChance(cover.Owner, seedEntity, cover.Comp.BlockChance, entropy);
    }

    private bool RollBlockChance(EntityUid cover, EntityUid seedEntity, float blockChance, int entropy = 0)
    {
        if (blockChance <= 0f)
            return false;
        if (blockChance >= 1f)
            return true;

        if (entropy == 0)
            entropy = (int)_timing.CurTick.Value;

        var hash = HashCode.Combine(GetNetEntity(seedEntity), GetNetEntity(cover), entropy);
        var roll = (hash & 0xFFFF) / (float)0xFFFF;
        return roll < blockChance;
    }

    public Vector2 GetDirectionalCoverFace(EntityUid cover)
    {
        if (!TryComp(cover, out TransformComponent? xform))
            return Vector2.Zero;

        return _transform.GetWorldRotation(xform).RotateVec(new Vector2(0f, -1f));
    }

    public Vector2i GetDefenderApproachOffset(EntityUid cover)
    {
        return DominantAxis(-GetDirectionalCoverFace(cover));
    }

    public static Vector2i DominantAxis(Vector2 dir)
    {
        if (dir.LengthSquared() < 0.0001f)
            return Vector2i.Zero;

        if (MathF.Abs(dir.X) >= MathF.Abs(dir.Y))
            return new Vector2i(Math.Sign(dir.X), 0);

        return new Vector2i(0, Math.Sign(dir.Y));
    }
}
