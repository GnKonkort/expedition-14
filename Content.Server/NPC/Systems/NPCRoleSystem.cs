using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Server.NPC.Systems;

/// <summary>
/// Applies thin <see cref="NpcRoleProfilePrototype"/> flags onto the HTN blackboard at MapInit.
/// </summary>
public sealed class NPCRoleSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;

    public const string CanDefibKey = "RoleCanDefib";
    public const string CanBypassDoorKey = "RoleCanBypassDoor";

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NPCRoleComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(EntityUid uid, NPCRoleComponent component, MapInitEvent args)
    {
        ApplyProfile(uid, component.Profile);
    }

    public void ApplyProfile(EntityUid uid, ProtoId<NpcRoleProfilePrototype> profileId)
    {
        if (!_proto.TryIndex(profileId, out NpcRoleProfilePrototype? profile))
            return;

        if (!TryComp<HTNComponent>(uid, out var htn))
            return;

        htn.Blackboard.SetValue(CanDefibKey, profile.CanDefib);
        htn.Blackboard.SetValue(CanBypassDoorKey, profile.CanBypassDoor);
    }
}
