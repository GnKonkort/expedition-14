using Content.Server._CitadelStation.GameTicking.Rules.Components;
using Content.Server.GameTicking.Rules;
using Content.Server.Roles;
using Content.Shared.GameTicking.Components;
using Content.Shared.Mind;
using Robust.Server.Player;
using Content.Server.Antag;
using Content.Server.Mind;
using Content.Shared.Objectives.Components;
using Content.Server._CitadelStation.Objectives.Components;

public sealed partial class YourRuleSystem : GameRuleSystem<EXPGreenShiftComponent>
{
    [Dependency] private RoleSystem _role = default!;
    [Dependency] private SharedMindSystem _minds = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly AntagSelectionSystem _antag = default!;
    [Dependency] private readonly MindSystem _mind = default!;

    public override void Initialize()
    {
        base.Initialize();
        // Subscribe to events here if needed
        SubscribeLocalEvent<AIIntegrityConditionComponent, ObjectiveGetProgressEvent>(OnGetProgress);
    }

    private void OnGetProgress(Entity<AIIntegrityConditionComponent> ent, ref ObjectiveGetProgressEvent args)
    {
        args.Progress = 0.0f; // PLaceholder
    }

    protected override void Added(EntityUid uid, EXPGreenShiftComponent comp, GameRuleComponent gameRule, GameRuleAddedEvent args)
    {
        base.Added(uid, comp, gameRule, args);
        // Called when rule is added but not yet started
        var query = EntityQuery<MindComponent>();

        foreach (var mind in query)
        {
            // Add your custom role to each mind
            _role.MindAddRole(mind.Owner, "ExpeditorRole", mind);

            if (_player.TryGetSessionById(mind.UserId, out var session))
            {
                _antag.SendBriefing(session, "I FUCKING HATE THIS SHIT", Color.White, null);
                _mind.TryAddObjective(mind.Owner, mind, "ExpeditorAIIntegrityObjective");
            }
        }
    }

    protected override void Started(EntityUid uid, EXPGreenShiftComponent comp, GameRuleComponent gameRule, GameRuleStartedEvent args)
    {
        base.Started(uid, comp, gameRule, args);
        // Called when rule becomes active - main initialization logic here
    }

    protected override void Ended(EntityUid uid, EXPGreenShiftComponent comp, GameRuleComponent gameRule, GameRuleEndedEvent args)
    {
        base.Ended(uid, comp, gameRule, args);
        // Cleanup logic when rule ends
    }

    protected override void ActiveTick(EntityUid uid, EXPGreenShiftComponent comp, GameRuleComponent gameRule, float frameTime)
    {
        base.ActiveTick(uid, comp, gameRule, frameTime);
        // Called every frame while rule is active
    }
}
