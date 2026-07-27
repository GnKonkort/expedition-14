using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared.Climbing.Components;
using Content.Shared.Climbing.Systems;
using Content.Shared.Interaction;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators;

/// <summary>
/// Moves an NPC to the specified target key. Hands the actual steering off to NPCSystem.Steering
/// </summary>
public sealed partial class MoveToOperator : HTNOperator, IHtnConditionalShutdown
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    private NPCSteeringSystem _steering = default!;
    private PathfindingSystem _pathfind = default!;
    private SharedTransformSystem _transform = default!;
    private ClimbSystem _climb = default!;
    private SharedInteractionSystem _interaction = default!;

    /// <summary>
    /// When to shut the task down.
    /// </summary>
    [DataField("shutdownState")]
    public HTNPlanState ShutdownState { get; private set; } = HTNPlanState.TaskFinished;

    /// <summary>
    /// Should we assume the MovementTarget is reachable during planning or should we pathfind to it?
    /// </summary>
    [DataField("pathfindInPlanning")]
    public bool PathfindInPlanning = true;

    /// <summary>
    /// When we're finished moving to the target should we remove its key?
    /// </summary>
    [DataField("removeKeyOnFinish")]
    public bool RemoveKeyOnFinish = true;

    /// <summary>
    /// Target Coordinates to move to. This gets removed after execution.
    /// </summary>
    [DataField("targetKey")]
    public string TargetKey = "TargetCoordinates";

    /// <summary>
    /// Where the pathfinding result will be stored (if applicable). This gets removed after execution.
    /// </summary>
    [DataField("pathfindKey")]
    public string PathfindKey = NPCBlackboard.PathfindKey;

    /// <summary>
    /// How close we need to get before considering movement finished.
    /// </summary>
    [DataField("rangeKey")]
    public string RangeKey = "MovementRange";

    /// <summary>
    /// Do we only need to move into line of sight.
    /// </summary>
    [DataField("stopOnLineOfSight")]
    public bool StopOnLineOfSight;

    private const string MovementCancelToken = "MovementCancelToken";
    private const string CoverRubTimeKey = "CoverRubTime";

    /// <summary>
    /// How long the NPC must be stuck against a barricade before vaulting.
    /// </summary>
    private const float CoverRubClimbDelay = 1.5f;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _pathfind = sysManager.GetEntitySystem<PathfindingSystem>();
        _steering = sysManager.GetEntitySystem<NPCSteeringSystem>();
        _transform = sysManager.GetEntitySystem<SharedTransformSystem>();
        _climb = sysManager.GetEntitySystem<ClimbSystem>();
        _interaction = sysManager.GetEntitySystem<SharedInteractionSystem>();
    }

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        if (!blackboard.TryGetValue<EntityCoordinates>(TargetKey, out var targetCoordinates, _entManager))
        {
            return (false, null);
        }

        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!_entManager.TryGetComponent<TransformComponent>(owner, out var xform) ||
            !_entManager.TryGetComponent<PhysicsComponent>(owner, out var body))
            return (false, null);

        if (!_entManager.TryGetComponent<MapGridComponent>(xform.GridUid, out var ownerGrid) ||
            !_entManager.TryGetComponent<MapGridComponent>(_transform.GetGrid(targetCoordinates), out var targetGrid))
        {
            return (false, null);
        }

        var range = blackboard.GetValueOrDefault<float>(RangeKey, _entManager);

        if (xform.Coordinates.TryDistance(_entManager, targetCoordinates, out var distance) && distance <= range)
        {
            // In range
            return (true, new Dictionary<string, object>()
            {
                {NPCBlackboard.OwnerCoordinates, blackboard.GetValueOrDefault<EntityCoordinates>(NPCBlackboard.OwnerCoordinates, _entManager)}
            });
        }

        if (!PathfindInPlanning)
        {
            return (true, new Dictionary<string, object>()
            {
                {NPCBlackboard.OwnerCoordinates, targetCoordinates}
            });
        }

        var flags = _pathfind.GetFlags(blackboard);
        var path = await _pathfind.GetPath(
            owner,
            xform.Coordinates,
            targetCoordinates,
            range,
            cancelToken,
            flags);

        var needClimb = false;
        var steeringSys = _entManager.System<NPCSteeringSystem>();

        // No walk-around path — allow climbing over climbable obstacles (barricades, etc.).
        if (path.Result != PathResult.Path && (flags & PathFlags.Climbing) == 0x0)
        {
            steeringSys.ClimbDebug(owner, "PLAN NoPath → climb-retry");
            flags |= PathFlags.Climbing;
            path = await _pathfind.GetPath(
                owner,
                xform.Coordinates,
                targetCoordinates,
                range,
                cancelToken,
                flags);
            needClimb = path.Result == PathResult.Path;
            steeringSys.ClimbDebug(owner, $"PLAN climb-retry {(needClimb ? "OK" : "FAIL")}");
        }

        if (path.Result != PathResult.Path)
        {
            // Cover stand may still resolve at runtime (rub → vault).
            if (blackboard.TryGetValue<EntityUid>(NPCCoverSystem.CoverEntityKey, out _, _entManager))
            {
                return (true, new Dictionary<string, object>
                {
                    { NPCBlackboard.OwnerCoordinates, targetCoordinates },
                    { NPCBlackboard.NavClimb, true },
                });
            }

            return (false, null);
        }

        var effects = new Dictionary<string, object>
        {
            { NPCBlackboard.OwnerCoordinates, targetCoordinates },
            { PathfindKey, path },
        };

        if (needClimb)
            effects[NPCBlackboard.NavClimb] = true;

        return (true, effects);
    }

    // Given steering is complicated we'll hand it off to a dedicated system rather than this singleton operator.

    public override void Startup(NPCBlackboard blackboard)
    {
        base.Startup(blackboard);

        // Need to remove the planning value for execution.
        blackboard.Remove<EntityCoordinates>(NPCBlackboard.OwnerCoordinates);
        var targetCoordinates = blackboard.GetValue<EntityCoordinates>(TargetKey);
        var uid = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        // Re-use the path we may have if applicable.
        var comp = _steering.Register(uid, targetCoordinates);
        comp.ArriveOnLineOfSight = StopOnLineOfSight;
        // Keep climb/pry/smash flags in sync with the plan (e.g. climb fallback).
        comp.Flags = _pathfind.GetFlags(blackboard);

        if (blackboard.TryGetValue<float>(RangeKey, out var range, _entManager))
        {
            comp.Range = range;
        }

        if (blackboard.TryGetValue<PathResultEvent>(PathfindKey, out var result, _entManager))
        {
            if (blackboard.TryGetValue<EntityCoordinates>(NPCBlackboard.OwnerCoordinates, out var coordinates, _entManager))
            {
                var mapCoords = _transform.ToMapCoordinates(coordinates);
                _steering.PrunePath(uid, mapCoords, _transform.ToMapCoordinates(targetCoordinates).Position - mapCoords.Position, result.Path);
            }

            comp.CurrentPath = new Queue<PathPoly>(result.Path);
        }
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!_entManager.TryGetComponent<NPCSteeringComponent>(owner, out var steering))
            return HTNOperatorStatus.Failed;

        // Just keep moving in the background and let the other tasks handle it.
        if (ShutdownState == HTNPlanState.PlanFinished && steering.Status == SteeringStatus.Moving)
        {
            return HTNOperatorStatus.Finished;
        }

        if (blackboard.TryGetValue<EntityUid>(NPCCoverSystem.CoverEntityKey, out var coverEnt, _entManager))
        {
            // Cover may have been deleted while the HTN plan still references it.
            if (!_entManager.EntityExists(coverEnt))
            {
                blackboard.Remove<EntityUid>(NPCCoverSystem.CoverEntityKey);
                blackboard.Remove<float>(CoverRubTimeKey);
            }
            else
            {
                var coverSys = _entManager.System<NPCCoverSystem>();

                // Already behind / on the cade — stop pathing even if steering still says NoPath.
                if (blackboard.TryGetValue<EntityCoordinates>(NPCCoverSystem.CoverCoordinatesKey, out var standPos, _entManager) &&
                    coverSys.IsInCoverPosition(owner, coverEnt, standPos))
                {
                    blackboard.Remove<float>(CoverRubTimeKey);
                    coverSys.Debug($"MOVE Finished {ToPretty(owner)} cover={ToPretty(coverEnt)} reason=in-cover-pos steer={steering.Status}");
                    return HTNOperatorStatus.Finished;
                }

                // Distance arrival alone accepts the neighboring flank; require the target tile.
                if (steering.Status == SteeringStatus.InRange &&
                    blackboard.TryGetValue<EntityCoordinates>(TargetKey, out var coverTarget, _entManager) &&
                    !coverSys.IsAtCoverStand(owner, coverTarget))
                {
                    coverSys.Debug($"MOVE flank-reject {ToPretty(owner)} status=InRange not-on-stand → ForceMove");
                    steering.Range = System.Math.Min(steering.Range, 0.2f);
                    steering.Status = SteeringStatus.Moving;
                    steering.ForceMove = true;
                }

                if (TryHandleCoverRubClimb(blackboard, owner, coverEnt, steering, frameTime, out var rubStatus))
                    return rubStatus;
            }
        }

        var result = steering.Status switch
        {
            SteeringStatus.InRange => HTNOperatorStatus.Finished,
            SteeringStatus.NoPath => HTNOperatorStatus.Failed,
            SteeringStatus.Moving => HTNOperatorStatus.Continuing,
            _ => throw new ArgumentOutOfRangeException()
        };

        if (result != HTNOperatorStatus.Continuing &&
            blackboard.TryGetValue<EntityUid>(NPCCoverSystem.CoverEntityKey, out var coverLog, _entManager))
        {
            _entManager.System<NPCCoverSystem>().Debug(
                $"MOVE {result} {ToPretty(owner)} cover={ToPretty(coverLog)} steer={steering.Status}");
        }

        return result;
    }

    private string ToPretty(EntityUid uid) => _entManager.ToPrettyString(uid);

    /// <summary>
    /// Vault only when stuck with NoPath against the barricade for <see cref="CoverRubClimbDelay"/>.
    /// Walking past / toward the rear must not start a climb.
    /// </summary>
    private bool TryHandleCoverRubClimb(
        NPCBlackboard blackboard,
        EntityUid owner,
        EntityUid cover,
        NPCSteeringComponent steering,
        float frameTime,
        out HTNOperatorStatus status)
    {
        status = HTNOperatorStatus.Continuing;

        var coverSys = _entManager.System<NPCCoverSystem>();
        var steeringSys = _entManager.System<NPCSteeringSystem>();

        // Still vaulting / climbing — wait it out. Do NOT ForceMove (BreakOnMove cancels the bar).
        if (_entManager.TryGetComponent(owner, out ClimbingComponent? climbing) &&
            (climbing.DoAfter != null || climbing.NextTransition != null || climbing.IsClimbing))
        {
            steeringSys.ClimbDebug(owner,
                $"COVER-RUB climb-wait isClimbing={climbing.IsClimbing} doAfter={climbing.DoAfter != null} transition={climbing.NextTransition != null}");
            if (coverSys.DebugEnabled)
                coverSys.Debug($"MOVE climb-wait {ToPretty(owner)} cover={ToPretty(cover)} climbing={climbing.IsClimbing} doAfter={climbing.DoAfter != null}");
            blackboard.SetValue(NPCBlackboard.NavClimb, true);
            steering.Status = SteeringStatus.Moving;
            steering.ForceMove = false;
            status = HTNOperatorStatus.Continuing;
            return true;
        }

        // Only rub when pathfinding has failed — not while still moving alongside the cade.
        if (steering.Status != SteeringStatus.NoPath)
        {
            blackboard.Remove<float>(CoverRubTimeKey);
            return false;
        }

        // Only vault the reserved cover when standing on the adjacent tile.
        const float coverClimbRange = 1.25f;
        if (!_interaction.InRangeUnobstructed(owner, cover, coverClimbRange))
        {
            steeringSys.ClimbDebug(owner, $"COVER-RUB out-of-range cover={ToPretty(cover)}");
            blackboard.Remove<float>(CoverRubTimeKey);
            return false;
        }

        var rubTime = blackboard.GetValueOrDefault<float>(CoverRubTimeKey, _entManager) + frameTime;
        blackboard.SetValue(CoverRubTimeKey, rubTime);

        // Keep pressing into the barricade until the climb delay elapses.
        if (rubTime < CoverRubClimbDelay)
        {
            if (coverSys.DebugEnabled && (int)(rubTime * 10) != (int)((rubTime - frameTime) * 10))
                coverSys.Debug($"MOVE rub {ToPretty(owner)} cover={ToPretty(cover)} t={rubTime:F1}/{CoverRubClimbDelay:F1} steer=NoPath");
            steeringSys.ClimbDebug(owner, $"COVER-RUB pressing t={rubTime:F1}/{CoverRubClimbDelay:F1}");
            steering.Status = SteeringStatus.Moving;
            steering.ForceMove = true;
            status = HTNOperatorStatus.Continuing;
            return true;
        }

        if (!_entManager.TryGetComponent(cover, out ClimbableComponent? climbable) ||
            !_entManager.TryGetComponent(owner, out ClimbingComponent? climbComp))
        {
            coverSys.Debug($"MOVE rub-fail {ToPretty(owner)} cover={ToPretty(cover)} reason=not-climbable");
            steeringSys.ClimbDebug(owner, $"COVER-RUB fail not-climbable cover={ToPretty(cover)}");
            status = HTNOperatorStatus.Failed;
            return true;
        }

        if (_climb.CanVault(climbable, owner, cover, out var reason) &&
            _climb.TryClimb(owner, owner, cover, out _, climbable, climbComp))
        {
            coverSys.Debug($"MOVE climb-start {ToPretty(owner)} cover={ToPretty(cover)} after-rub={rubTime:F1}s");
            steeringSys.ClimbDebug(owner, $"COVER-RUB climb-start cover={ToPretty(cover)} → HOLD (no ForceMove)");
            blackboard.SetValue(NPCBlackboard.NavClimb, true);
            blackboard.Remove<float>(CoverRubTimeKey);
            steering.Status = SteeringStatus.Moving;
            // Critical: stop moving or BreakOnMove cancels the vault progress bar.
            steering.ForceMove = false;
            status = HTNOperatorStatus.Continuing;
            return true;
        }

        coverSys.Debug($"MOVE rub-fail {ToPretty(owner)} cover={ToPretty(cover)} reason=vault-failed");
        steeringSys.ClimbDebug(owner, $"COVER-RUB vault-failed cover={ToPretty(cover)} reason={reason}");
        status = HTNOperatorStatus.Failed;
        return true;
    }

    public void ConditionalShutdown(NPCBlackboard blackboard)
    {
        // Cleanup the blackboard and remove steering.
        if (blackboard.TryGetValue<CancellationTokenSource>(MovementCancelToken, out var cancelToken, _entManager))
        {
            cancelToken.Cancel();
            blackboard.Remove<CancellationTokenSource>(MovementCancelToken);
        }

        // OwnerCoordinates is only used in planning so dump it.
        blackboard.Remove<PathResultEvent>(PathfindKey);
        blackboard.Remove<float>(CoverRubTimeKey);
        // Do not force NavClimb false — humanoid hostiles keep vaulting barricades on the route.

        if (RemoveKeyOnFinish)
        {
            blackboard.Remove<EntityCoordinates>(TargetKey);
        }

        _steering.Unregister(blackboard.GetValue<EntityUid>(NPCBlackboard.Owner));
    }
}
