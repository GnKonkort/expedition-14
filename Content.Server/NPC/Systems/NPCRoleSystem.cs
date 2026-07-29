using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Server.NPC.Systems;

/// <summary>
/// Applies <see cref="NpcRoleProfilePrototype"/> data onto HTN blackboard at MapInit.
/// </summary>
public sealed class NPCRoleSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;

    public override void Initialize()
    {
        base.Initialize();
        // Only NPCRole MapInit — HTNComponent.MapInit is already owned by HTNSystem/NPCSystem.
        SubscribeLocalEvent<NPCRoleComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(EntityUid uid, NPCRoleComponent component, MapInitEvent args)
    {
        EnsureComp<NPCNeedComponent>(uid);

        if (!_proto.TryIndex(component.Profile, out NpcRoleProfilePrototype? profile))
            return;

        if (!TryComp<HTNComponent>(uid, out var htn))
            return;

        if (profile.InventoryPolicy is { } inv)
            htn.Blackboard.SetValue(NPCBlackboard.InventoryPolicy, inv.Id);

        if (profile.ChemKnowledge is { } chem)
            htn.Blackboard.SetValue(NPCBlackboard.ChemKnowledge, chem.Id);
    }
}
