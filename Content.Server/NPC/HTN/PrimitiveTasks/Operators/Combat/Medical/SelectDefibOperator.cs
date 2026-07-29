using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Systems;
using Robust.Shared.Map;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Combat.Medical;

/// <summary>
/// Resolves an owned defibrillator, or picks a nearby floor defib / wall cabinet source.
/// </summary>
public sealed partial class SelectDefibOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField]
    public string DefibItemKey = NPCBlackboard.DefibItem;

    [DataField]
    public string TargetKey = "Target";

    [DataField]
    public string TargetCoordinatesKey = "TargetCoordinates";

    [DataField]
    public string IsCabinetKey = "DefibSourceIsCabinet";

    /// <summary>
    /// When true, only succeed if a defibrillator is already owned (no world search).
    /// </summary>
    [DataField]
    public bool OwnedOnly;

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var medical = _entManager.System<NPCMedicalSystem>();
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (medical.TryGetOwnedDefib(owner, out var owned) && owned != null)
        {
            return (true, new Dictionary<string, object>
            {
                { DefibItemKey, owned.Value },
                { IsCabinetKey, false },
            });
        }

        if (OwnedOnly)
            return (false, null);

        var range = medical.GetMedSearchRange(blackboard);
        if (!medical.TrySelectNearbyDefibSource(owner, range, out var source, out var isCabinet))
            return (false, null);

        return (true, new Dictionary<string, object>
        {
            { TargetKey, source.Value },
            { TargetCoordinatesKey, new EntityCoordinates(source.Value, Vector2.Zero) },
            { IsCabinetKey, isCabinet },
        });
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        return HTNOperatorStatus.Finished;
    }
}
