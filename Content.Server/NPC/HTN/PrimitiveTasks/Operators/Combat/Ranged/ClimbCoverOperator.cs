using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;
using Content.Shared.Climbing.Components;
using Content.Shared.Climbing.Systems;
using Content.Shared.Interaction;
using Robust.Shared.Map;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;

/// <summary>
/// Vaults onto the reserved cover entity when the NPC cannot settle on the stand tile without climbing.
/// Skips cleanly if already in cover, not adjacent, or the cover is not climbable.
/// </summary>
public sealed partial class ClimbCoverOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    private ClimbSystem _climb = default!;
    private NPCCoverSystem _cover = default!;
    private SharedInteractionSystem _interaction = default!;

    [DataField]
    public string CoverEntityKey = NPCCoverSystem.CoverEntityKey;

    [DataField]
    public string CoverCoordinatesKey = NPCCoverSystem.CoverCoordinatesKey;

    [DataField]
    public string RangeKey = "InteractRange";

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _climb = sysManager.GetEntitySystem<ClimbSystem>();
        _cover = sysManager.GetEntitySystem<NPCCoverSystem>();
        _interaction = sysManager.GetEntitySystem<SharedInteractionSystem>();
    }

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        // Always allow the branch — Update no-ops when climb is unnecessary.
        return (blackboard.TryGetValue<EntityUid>(CoverEntityKey, out _, _entManager), null);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        if (!blackboard.TryGetValue<EntityUid>(CoverEntityKey, out var cover, _entManager))
            return HTNOperatorStatus.Finished;

        if (blackboard.TryGetValue<EntityCoordinates>(CoverCoordinatesKey, out var stand, _entManager) &&
            _cover.IsInCoverPosition(owner, cover, stand))
            return HTNOperatorStatus.Finished;

        if (!_entManager.TryGetComponent(owner, out ClimbingComponent? climbing))
            return HTNOperatorStatus.Finished;

        if (climbing.IsClimbing)
            return HTNOperatorStatus.Finished;

        // Wait for vault do-after / climb transition.
        if (climbing.DoAfter != null || climbing.NextTransition != null)
            return HTNOperatorStatus.Continuing;

        if (!_entManager.TryGetComponent(cover, out ClimbableComponent? climbable))
            return HTNOperatorStatus.Finished;

        var range = blackboard.GetValueOrDefault<float>(RangeKey, _entManager);
        if (range <= 0f)
            range = SharedInteractionSystem.InteractionRange;

        if (!_interaction.InRangeUnobstructed(owner, cover, range))
            return HTNOperatorStatus.Finished;

        if (!_climb.CanVault(climbable, owner, cover, out _))
            return HTNOperatorStatus.Finished;

        // Climb onto the barricade/table so the following MoveTo can reach the stand.
        if (_climb.TryClimb(owner, owner, cover, out _, climbable, climbing))
            return HTNOperatorStatus.Continuing;

        return HTNOperatorStatus.Finished;
    }
}
