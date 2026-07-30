using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared.NPC;

[Serializable, NetSerializable]
public sealed partial class NpcSimpleDoTaskDoAfterEvent : SimpleDoAfterEvent;
