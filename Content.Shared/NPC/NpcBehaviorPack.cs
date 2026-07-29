using Robust.Shared.Serialization;

namespace Content.Shared.NPC;

/// <summary>
/// High-level behavior bucket for the custom NPC editor. Maps to a curated HTN whitelist.
/// </summary>
[Serializable, NetSerializable]
public enum NpcBehaviorPack : byte
{
    Basic = 0,
    Civilian = 1,
    Combat = 2,
}
