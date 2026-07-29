using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    /// Max HTN NPCs processed per tick. Raised for denser humanoid combat (~80).
    /// </summary>
    public static readonly CVarDef<int> NPCMaxUpdates =
        CVarDef.Create("npc.max_updates", 192);

    /// <summary>
    /// Seconds of HTN planning budget processed each tick.
    /// </summary>
    public static readonly CVarDef<float> NPCPlanQueueSeconds =
        CVarDef.Create("npc.plan_queue_seconds", 0.008f);

    public static readonly CVarDef<bool> NPCEnabled = CVarDef.Create("npc.enabled", true);

    /// <summary>
    ///     Should NPCs pathfind when steering. For debug purposes.
    /// </summary>
    public static readonly CVarDef<bool> NPCPathfinding = CVarDef.Create("npc.pathfinding", true);

    /// <summary>
    /// Verbose squad formation / leave / rally logs and visible rally markers.
    /// </summary>
    public static readonly CVarDef<bool> NPCDebugSquad =
        CVarDef.Create("npc.debug_squad", false, CVar.SERVERONLY);

    /// <summary>
    /// Verbose soft-cover select / reserve / move / hold / climb logs.
    /// </summary>
    public static readonly CVarDef<bool> NPCDebugCover =
        CVarDef.Create("npc.debug_cover", false, CVar.SERVERONLY);

    /// <summary>
    /// Verbose barricade/table vault steering logs (arrive, do_after, BreakOnMove, stuck).
    /// </summary>
    public static readonly CVarDef<bool> NPCDebugClimb =
        CVarDef.Create("npc.debug_climb", false, CVar.SERVERONLY);

    /// <summary>
    /// Verbose NPC medical heal / medipen / med-loot logs.
    /// </summary>
    public static readonly CVarDef<bool> NPCDebugMedical =
        CVarDef.Create("npc.debug_medical", false, CVar.SERVERONLY);

    /// <summary>
    /// Verbose NPC atmos-flee / survival need logs.
    /// </summary>
    public static readonly CVarDef<bool> NPCDebugAtmos =
        CVarDef.Create("npc.debug_atmos", false, CVar.SERVERONLY);
}
