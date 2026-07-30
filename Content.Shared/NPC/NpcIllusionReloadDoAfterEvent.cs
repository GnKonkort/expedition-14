using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared.NPC;

/// <summary>
/// Illusion reload wait — refill + bolt close happen when this finishes.
/// </summary>
[Serializable, NetSerializable]
public sealed partial class NpcIllusionReloadDoAfterEvent : SimpleDoAfterEvent;
