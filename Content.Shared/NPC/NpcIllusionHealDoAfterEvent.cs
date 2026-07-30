using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared.NPC;

/// <summary>
/// Visible illusion heal wait — medic applies a fixed damage chunk when this finishes.
/// </summary>
[Serializable, NetSerializable]
public sealed partial class NpcIllusionHealDoAfterEvent : SimpleDoAfterEvent;
