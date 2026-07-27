using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;
using Robust.Shared.Map;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Ranged;

/// <summary>
/// Selects and reserves a soft-cover stand tile for the current Target.
/// Sets an approach waypoint when the NPC must walk around a barricade face.
/// </summary>
public sealed partial class PickCoverOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public string TargetKey = "Target";

    [DataField]
    public string TargetCoordinatesKey = "TargetCoordinates";

    [DataField]
    public string CoverCoordinatesKey = NPCCoverSystem.CoverCoordinatesKey;

    [DataField]
    public string CoverApproachCoordinatesKey = NPCCoverSystem.CoverApproachCoordinatesKey;

    [DataField]
    public string CoverEntityKey = NPCCoverSystem.CoverEntityKey;

    [DataField]
    public string CoverSlotKey = NPCCoverSystem.CoverSlotKey;

    [DataField]
    public string SearchRangeKey = "VisionRadius";

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
        {
            _cover.Debug($"PICK plan FAIL {ToPretty(owner)} reason=no-target");
            return (false, null);
        }

        var searchRange = blackboard.GetValueOrDefault<float>(SearchRangeKey, _entManager);
        if (searchRange <= 0f)
            searchRange = NPCCoverSystem.DefaultCoverSearchRange;

        if (!_cover.TrySelectCover(owner, target, searchRange, out var coverEntity, out var stand, out var approach,
                out var slot))
            return (false, null);

        if (!_cover.IsSlotFree(slot, owner))
        {
            _cover.Debug($"PICK plan FAIL {ToPretty(owner)} reason=slot-not-free-after-select");
            return (false, null);
        }

        _cover.Debug($"PICK plan OK {ToPretty(owner)} cover={ToPretty(coverEntity)}");
        return (true, new Dictionary<string, object>
        {
            { CoverCoordinatesKey, stand },
            { CoverApproachCoordinatesKey, approach },
            { NPCCoverSystem.CoverRearCoordinatesKey, approach },
            { CoverEntityKey, coverEntity },
            { CoverSlotKey, slot },
            // First MoveTo: rear tile behind the barricade (tables: same as stand).
            { TargetCoordinatesKey, approach },
        });
    }

    public override void Startup(NPCBlackboard blackboard)
    {
        base.Startup(blackboard);

        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!blackboard.TryGetValue<NPCCoverSystem.CoverSlotId>(CoverSlotKey, out var slot, _entManager))
        {
            _cover.Debug($"PICK startup FAIL {ToPretty(owner)} reason=no-slot-key");
            return;
        }

        _cover.ReleaseAll(owner);
        if (!_cover.TryReserve(slot, owner))
        {
            _cover.Debug($"PICK startup FAIL {ToPretty(owner)} reason=reserve-failed");
            blackboard.Remove<NPCCoverSystem.CoverSlotId>(CoverSlotKey);
            return;
        }

        _cover.Debug($"PICK startup OK {ToPretty(owner)}");
    }

    public override void PlanShutdown(NPCBlackboard blackboard)
    {
        base.PlanShutdown(blackboard);
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        _cover.Debug($"PICK plan-shutdown {ToPretty(owner)}");
        _cover.ReleaseAll(owner);
        blackboard.Remove<EntityCoordinates>(CoverCoordinatesKey);
        blackboard.Remove<EntityCoordinates>(CoverApproachCoordinatesKey);
        blackboard.Remove<EntityUid>(CoverEntityKey);
        blackboard.Remove<NPCCoverSystem.CoverSlotId>(CoverSlotKey);
    }

    public override void TaskShutdown(NPCBlackboard blackboard, HTNOperatorStatus status)
    {
        base.TaskShutdown(blackboard, status);
        if (status != HTNOperatorStatus.Failed)
            return;

        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        _cover.Debug($"PICK task-fail {ToPretty(owner)}");
        _cover.ReleaseAll(owner);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        if (!blackboard.TryGetValue<NPCCoverSystem.CoverSlotId>(CoverSlotKey, out var slot, _entManager))
        {
            _cover.Debug($"PICK update FAIL {ToPretty(owner)} reason=no-slot");
            return HTNOperatorStatus.Failed;
        }

        if (!_cover.IsSlotFree(slot, owner) && !_cover.TryReserve(slot, owner))
        {
            _cover.Debug($"PICK update FAIL {ToPretty(owner)} reason=cannot-reserve");
            return HTNOperatorStatus.Failed;
        }

        _cover.TryReserve(slot, owner);
        return HTNOperatorStatus.Finished;
    }

    private string ToPretty(EntityUid uid) => _entManager.ToPrettyString(uid);
}
