namespace Content.Server._CitadelStation.Expedition.Components;

[RegisterComponent, Access(typeof(ExpeditionRuleSystem))]
public sealed partial class ExpeditionRuleComponent : Component {
    public string expeditionName;
    public int requiredProfitToProceed = 1000000;
    public int profitAccumulated = 0;
    public bool allTasksComplete = false;
    public TimeSpan completionCheckCooldown = TimeSpan.FromSeconds(5);
    public TimeSpan lastCheckTime = TimeSpan.FromSeconds(0);
}
