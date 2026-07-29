using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Content.Server.Hands.Systems;
using Content.Shared.Access.Systems;
using Content.Shared.ActionBlocker;
using Content.Shared.Hands.Components;
using Content.Shared.Interaction;
using JetBrains.Annotations;
using Robust.Shared.Utility;

namespace Content.Server.NPC;

[DataDefinition]
public sealed partial class NPCBlackboard : IEnumerable<KeyValuePair<string, object>>
{
    /// <summary>
    /// Global defaults for NPCs
    /// </summary>
    private static readonly Dictionary<string, object> BlackboardDefaults = new()
    {
        {"BufferRange", 10f},
        {"FollowCloseRange", 3f},
        {"FollowRange", 7f},
        {"IdleRange", 7f},
        {"StandPointRange", 0.5f},
        {"InteractRange", SharedInteractionSystem.InteractionRange},
        {"MaximumIdleTime", 7f},
        {MedibotInjectRange, 4f},
        {MeleeMissChance, 0.3f},
        {"MeleeRange", 1f},
        {"MinimumIdleTime", 2f},
        {"MovementRangeClose", 0.2f},
        {"MovementRange", 1.5f},
        // Tight enough to reach the hugged stand point without finishing from tile edge.
        {"CoverMovementRange", 0.25f},
        {"RangedRange", 10f},
        {"PreferredRangedRange", 8f},
        {"RotateSpeed", float.MaxValue},
        {"VisionRadius", 10f},
        {"AggroVisionRadius", 10f},
        {"AmmoSearchRange", 7f},
        {"AmmoLootHostileRange", 4f},
        {"MedSearchRange", 7f},
        {"SquadRallyRange", 2.5f},
    };

    /// <summary>
    /// The specific blackboard for this NPC.
    /// </summary>
    private readonly Dictionary<string, object> _blackboard = new();

    /// <summary>
    /// Should we allow setting values on the blackboard. This is true when we are planning.
    /// <remarks>
    /// The effects get stored separately so they can potentially be re-applied during execution.
    /// </remarks>
    /// </summary>
    public bool ReadOnly = false;

    public void Clear()
    {
        _blackboard.Clear();
    }

    public NPCBlackboard ShallowClone()
    {
        var dict = new NPCBlackboard();
        foreach (var item in _blackboard)
        {
            dict.SetValue(item.Key, item.Value);
        }
        return dict;
    }

    [Pure]
    public bool ContainsKey(string key)
    {
        return _blackboard.ContainsKey(key);
    }

    /// <summary>
    /// Get the blackboard data for a particular key.
    /// </summary>
    [Pure]
    public T GetValue<T>(string key)
    {
        return (T) _blackboard[key];
    }

    /// <summary>
    /// Tries to get the blackboard data for a particular key. Returns default if not found
    /// </summary>
    [Pure]
    public T? GetValueOrDefault<T>(string key, IEntityManager entManager)
    {
        if (_blackboard.TryGetValue(key, out var value))
        {
            return (T) value;
        }

        if (TryGetEntityDefault(key, out value, entManager))
        {
            return (T) value;
        }

        if (BlackboardDefaults.TryGetValue(key, out value))
        {
            return (T) value;
        }

        return default;
    }

    /// <summary>
    /// Tries to get the blackboard data for a particular key.
    /// </summary>
    public bool TryGetValue<T>(string key, [NotNullWhen(true)] out T? value, IEntityManager entManager)
    {
        if (_blackboard.TryGetValue(key, out var data))
        {
            value = (T) data;
            return true;
        }

        if (TryGetEntityDefault(key, out data, entManager))
        {
            value = (T) data;
            return true;
        }

        if (BlackboardDefaults.TryGetValue(key, out data))
        {
            value = (T) data;
            return true;
        }

        value = default;
        return false;
    }

    public void SetValue(string key, object value)
    {
        if (ReadOnly)
        {
            AssertReadonly();
            return;
        }

        _blackboard[key] = value;
    }

    private void AssertReadonly()
    {
        DebugTools.Assert(false, $"Tried to write to an NPC blackboard that is readonly!");
    }

    private bool TryGetEntityDefault(string key, [NotNullWhen(true)] out object? value, IEntityManager entManager)
    {
        value = default;
        EntityUid owner;

        var handSys = entManager.System<HandsSystem>();

        switch (key)
        {
            case Access:
            {
                if (!TryGetValue(Owner, out owner, entManager))
                {
                    return false;
                }

                var access = entManager.EntitySysManager.GetEntitySystem<AccessReaderSystem>();
                value = access.FindAccessTags(owner);
                return true;
            }
            case ActiveHand:
            {
                if (!TryGetValue(Owner, out owner, entManager) ||
                    handSys.GetActiveHand(owner) is not { } activeHand)
                {
                    return false;
                }

                value = activeHand;
                return true;
            }
            case ActiveHandFree:
            {
                if (!TryGetValue(Owner, out owner, entManager) ||
                    !entManager.TryGetComponent<HandsComponent>(owner, out var hands) ||
                    handSys.GetActiveHand(owner) is not { } activeHand)
                {
                    return false;
                }

                value = handSys.HandIsEmpty((owner, hands), activeHand);
                return true;
            }
            case CanMove:
            {
                if (!TryGetValue(Owner, out owner, entManager))
                {
                    return false;
                }

                var blocker = entManager.EntitySysManager.GetEntitySystem<ActionBlockerSystem>();
                value = blocker.CanMove(owner);
                return true;
            }
            case FreeHands:
            {
                if (!TryGetValue(Owner, out owner, entManager) ||
                    !entManager.TryGetComponent<HandsComponent>(owner, out var hands) ||
                    handSys.GetActiveHand(owner) is null)
                {
                    return false;
                }

                var handos = new List<string>();

                foreach (var id in hands.Hands.Keys)
                {
                    if (!handSys.HandIsEmpty((owner, hands), id))
                        continue;

                    handos.Add(id);
                }

                value = handos;
                return true;
            }
            case Inventory:
            {
                if (!TryGetValue(Owner, out owner, entManager) ||
                    !entManager.TryGetComponent<HandsComponent>(owner, out var hands) ||
                    handSys.GetActiveHand(owner) is null)
                {
                    return false;
                }

                var handos = new List<string>();

                foreach (var id in hands.Hands.Keys)
                {
                    if (!handSys.HandIsEmpty((owner, hands), id))
                        continue;

                    handos.Add(id);
                }

                value = handos;
                return true;
            }
            case OwnerCoordinates:
            {
                if (!TryGetValue(Owner, out owner, entManager))
                {
                    return false;
                }

                if (entManager.TryGetComponent<TransformComponent>(owner, out var xform))
                {
                    value = xform.Coordinates;
                    return true;
                }

                return false;
            }
            default:
                return false;
        }
    }

    public bool Remove<T>(string key)
    {
        DebugTools.Assert(!_blackboard.ContainsKey(key) || _blackboard[key] is T);
        return _blackboard.Remove(key);
    }

    public string GetVisionRadiusKey(IEntityManager entMan)
    {
        return TryGetValue<EntityUid>("Target", out _, entMan)
            ? AggroVisionRadius
            : VisionRadius;
    }

    // I Ummd and Ahhd about using strings vs enums and decided on tags because
    // if a fork wants to do their own thing they don't need to touch the enum.

    /*
    * Constants to make development easier
    */

    public const string Access = "Access";
    public const string ActiveHand = "ActiveHand";
    public const string ActiveHandFree = "ActiveHandFree";
    public const string CanMove = "CanMove";
    public const string FreeHands = "FreeHands";
    public const string FollowTarget = "FollowTarget";
    public const string Inventory = "Inventory";
    public const string MedibotInjectRange = "MedibotInjectRange";

    public const string MeleeMissChance = "MeleeMissChance";

    public const string PreferredRangedRange = "PreferredRangedRange";
    public const string SquadRallyCoordinates = "SquadRallyCoordinates";
    public const string SquadRallyRange = "SquadRallyRange";
    public const string SquadLeader = "SquadLeader";
    public const string GrenadeCooldownStart = "GrenadeCooldownStart";
    public const string GrenadeCooldownEnd = "GrenadeCooldownEnd";
    public const string GrenadeCombatStart = "GrenadeCombatStart";
    public const string GrenadeCombatTarget = "GrenadeCombatTarget";

    /// <summary>
    /// Patient entity for NPC medical heal (self or faction ally).
    /// </summary>
    public const string HealTarget = "HealTarget";

    /// <summary>
    /// Selected kit / medipen entity for NPC medical heal.
    /// </summary>
    public const string HealItem = "HealItem";

    /// <summary>
    /// Selected defibrillator entity for NPC ally revival.
    /// </summary>
    public const string DefibItem = "DefibItem";

    // --- Need board flags (bool) refreshed by NPCNeedSystem ---
    public const string NeedInVacuum = "NeedInVacuum";
    public const string NeedLowPressure = "NeedLowPressure";
    public const string NeedHasHostile = "NeedHasHostile";
    public const string NeedAmmoCritical = "NeedAmmoCritical";
    public const string NeedMedStockLow = "NeedMedStockLow";
    public const string NeedSeekAtmosphere = "NeedSeekAtmosphere";
    public const string NeedDoorBlocked = "NeedDoorBlocked";

    /// <summary>Safe atmos / room flee destination.</summary>
    public const string AtmosSafeCoordinates = "AtmosSafeCoordinates";

    /// <summary>Door entity blocking the current path that needs bypass.</summary>
    public const string BypassDoorTarget = "BypassDoorTarget";

    /// <summary>Active inventory policy prototype id.</summary>
    public const string InventoryPolicy = "InventoryPolicy";

    /// <summary>Active chem knowledge prototype id.</summary>
    public const string ChemKnowledge = "ChemKnowledge";

    /// <summary>Damaged repairable structure/entity for engineer NPCs.</summary>
    public const string RepairTarget = "RepairTarget";

    /// <summary>Selected repair tool (welder etc.).</summary>
    public const string RepairTool = "RepairTool";

    /// <summary>Hostile/detainee to cuff for security NPCs.</summary>
    public const string ArrestTarget = "ArrestTarget";

    /// <summary>Handcuff entity selected for arrest.</summary>
    public const string ArrestCuffs = "ArrestCuffs";

    public const string Target = "Target";
    public const string TargetCoordinates = "TargetCoordinates";

    public const string Owner = "Owner";
    public const string OwnerCoordinates = "OwnerCoordinates";
    public const string MovementTarget = "MovementTarget";

    /// <summary>
    /// When false, <c>IdleCompound</c> will not pick random wander destinations.
    /// </summary>
    public const string IdleWander = "IdleWander";

    /// <summary>
    /// Fixed idle stand coordinates for posted NPCs.
    /// </summary>
    public const string StandPoint = "StandPoint";

    public const string StandPointRange = "StandPointRange";

    /// <summary>
    /// Can the NPC click open entities such as doors.
    /// </summary>
    public const string NavInteract = "NavInteract";

    /// <summary>
    /// Can the NPC pry open doors for steering.
    /// </summary>
    public const string NavPry = "NavPry";

    /// <summary>
    /// Can the NPC smash obstacles for steering.
    /// </summary>
    public const string NavSmash = "NavSmash";

    /// <summary>
    /// Can the NPC climb obstacles for steering.
    /// </summary>
    public const string NavClimb = "NavClimb";

    /// <summary>
    /// Default key storage for a movement pathfind.
    /// </summary>
    public const string PathfindKey = "MovementPathfind";

    public const string RotateSpeed = "RotateSpeed";
    public const string UtilityTarget = "UtilityTarget";

    private const string VisionRadius = "VisionRadius";
    private const string AggroVisionRadius = "AggroVisionRadius";

    /// <summary>
    /// A configurable "order" enum that can be given to an NPC from an external source.
    /// </summary>
    public const string CurrentOrders = "CurrentOrders";

    /// <summary>
    /// A configurable target that's ordered by external sources.
    /// </summary>
    public const string CurrentOrderedTarget = "CurrentOrderedTarget";

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
    {
        return _blackboard.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
