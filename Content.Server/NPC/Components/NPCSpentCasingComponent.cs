namespace Content.Server.NPC.Components;

/// <summary>
/// Marks a spent cartridge ejected (or about to be ejected) from an NPC-fired gun
/// so it can despawn after a short lifetime and not litter the map.
/// </summary>
[RegisterComponent]
public sealed partial class NPCSpentCasingComponent : Component;
