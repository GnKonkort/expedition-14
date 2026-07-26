using Content.Server._CitadelStation.Expedition.Components;
using Content.Server._NF.Bank;
using Content.Server.GameTicking.Rules;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Bank.Events;
using Content.Shared.GameTicking.Components;
using Robust.Shared.Timing;

public sealed partial class ExpeditionRuleSystem : GameRuleSystem<ExpeditionRuleComponent> {

    [Dependency] private readonly BankSystem _bankSystem = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    private readonly ISawmill _sawmill;

    public ExpeditionRuleSystem() {
        _sawmill = Logger.GetSawmill("expedition");
    }

    public override void Update(float frameTime) {
        var query = QueryActiveRules();

        while (query.MoveNext(out var uid, out _, out var expedition, out _))
        {
            if (_timing.CurTime < expedition.lastCheckTime + expedition.completionCheckCooldown)
                continue;

            _bankSystem.TryGetBalance(SectorBankAccount.Frontier, out var balance);
        }
    }

    protected override void Started(EntityUid uid,
        ExpeditionRuleComponent component,
        GameRuleComponent gameRule,
        GameRuleStartedEvent args) {
    }
}
