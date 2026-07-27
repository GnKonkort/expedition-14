using Content.Shared.Humanoid;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Server.NPC.HTN;
using Robust.Shared.Physics.Events;

namespace Content.Server.NPC.Systems;

/// <summary>
/// Humanoid NPCs ignore mob-pushing and pass through other mobs / humanoids.
/// Walls and structures still block them.
/// </summary>
public sealed class NPCHumanoidCollisionSystem : EntitySystem
{
    private EntityQuery<HumanoidAppearanceComponent> _humanoidQuery;
    private EntityQuery<MobCollisionComponent> _mobCollisionQuery;

    public override void Initialize()
    {
        base.Initialize();

        _humanoidQuery = GetEntityQuery<HumanoidAppearanceComponent>();
        _mobCollisionQuery = GetEntityQuery<MobCollisionComponent>();

        SubscribeLocalEvent<HTNComponent, AttemptMobCollideEvent>(OnAttemptMobCollide);
        SubscribeLocalEvent<HTNComponent, AttemptMobTargetCollideEvent>(OnAttemptMobTargetCollide);
        SubscribeLocalEvent<HTNComponent, PreventCollideEvent>(OnPreventCollide);
    }

    private void OnAttemptMobCollide(Entity<HTNComponent> ent, ref AttemptMobCollideEvent args)
    {
        if (_humanoidQuery.HasComponent(ent))
            args.Cancelled = true;
    }

    private void OnAttemptMobTargetCollide(Entity<HTNComponent> ent, ref AttemptMobTargetCollideEvent args)
    {
        if (_humanoidQuery.HasComponent(ent))
            args.Cancelled = true;
    }

    private void OnPreventCollide(Entity<HTNComponent> ent, ref PreventCollideEvent args)
    {
        if (args.Cancelled || !_humanoidQuery.HasComponent(ent))
            return;

        // Pass through players, other humanoids, and any mob-collision body.
        if (_humanoidQuery.HasComponent(args.OtherEntity) ||
            _mobCollisionQuery.HasComponent(args.OtherEntity))
            args.Cancelled = true;
    }
}
