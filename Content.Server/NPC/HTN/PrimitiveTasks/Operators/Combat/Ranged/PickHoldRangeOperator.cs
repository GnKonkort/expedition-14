using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;
using Robust.Shared.Map;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;

/// <summary>
/// Picks a pathable stand point on the preferred ranged ring (default 6–10 tiles).
/// </summary>
public sealed partial class PickHoldRangeOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public string TargetKey = "Target";

    [DataField]
    public string TargetCoordinatesKey = "TargetCoordinates";

    [DataField]
    public string PreferredRangeKey = NPCCoverSystem.PreferredRangedRangeKey;

    private NPCCoverSystem _cover = default!;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _cover = sysManager.GetEntitySystem<NPCCoverSystem>();
    }

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        if (!blackboard.TryGetValue<EntityUid>(TargetKey, out var target, _entManager))
            return (false, null);

        var preferred = blackboard.GetValueOrDefault<float>(PreferredRangeKey, _entManager);
        if (preferred <= 0f)
            preferred = NPCCoverSystem.PreferredHoldRange;

        if (!_cover.TrySelectHoldRange(owner, target, preferred, out var hold))
            return (false, null);

        return (true, new Dictionary<string, object>
        {
            { TargetCoordinatesKey, hold },
        });
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        return HTNOperatorStatus.Finished;
    }
}
