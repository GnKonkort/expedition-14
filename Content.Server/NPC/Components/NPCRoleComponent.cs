using Content.Server.NPC.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Server.NPC.Components;

/// <summary>
/// Data-only role profile binding for humanoid NPC agents.
/// </summary>
[RegisterComponent]
public sealed partial class NPCRoleComponent : Component
{
    [DataField(required: true)]
    public ProtoId<NpcRoleProfilePrototype> Profile;
}
