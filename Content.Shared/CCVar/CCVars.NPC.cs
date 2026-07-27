using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    public static readonly CVarDef<int> NPCMaxUpdates =
        CVarDef.Create("npc.max_updates", 128);

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
        CVarDef.Create("npc.debug_cover", true, CVar.SERVERONLY);

    /// <summary>
    /// Verbose barricade/table vault steering logs (arrive, do_after, BreakOnMove, stuck).
    /// </summary>
    public static readonly CVarDef<bool> NPCDebugClimb =
        CVarDef.Create("npc.debug_climb", true, CVar.SERVERONLY);
}
