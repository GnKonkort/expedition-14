using Content.Shared._Arcane.ERP;
using Content.Shared._Arcane.ERP.Organs;
using Content.Shared._Arcane.ERP.OrgansAppearance;
using Content.Shared._Arcane.ERP.Preferences;
using Content.Shared.Humanoid;
using Content.Shared.Preferences;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._Arcane.ERP.OrgansAppearance;

/// <summary>
/// Builds networked organ visual state from the character profile (prefs-only, no body organs).
/// </summary>
public sealed class ErpOrganVisualsSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IComponentFactory _factory = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<HumanoidAppearanceComponent, HumanoidProfileLoadedEvent>(OnProfileLoaded);
        SubscribeLocalEvent<HumanoidAppearanceComponent, PlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<EroticOrgansComponent, ComponentStartup>(OnEroticOrgansStartup);
    }

    private void OnProfileLoaded(Entity<HumanoidAppearanceComponent> ent, ref HumanoidProfileLoadedEvent args)
        => ApplyFromProfile(ent, args.Profile);

    private void OnPlayerAttached(Entity<HumanoidAppearanceComponent> ent, ref PlayerAttachedEvent args)
    {
        // Profile may already be loaded; ensure visuals exist for late attach.
        if (!HasComp<EroticOrgansComponent>(ent))
            return;

        if (HasComp<ErpOrganVisualsComponent>(ent))
            return;

        // Without a profile payload here, leave empty; HumanoidProfileLoadedEvent handles spawn.
    }

    private void OnEroticOrgansStartup(Entity<EroticOrgansComponent> ent, ref ComponentStartup args)
    {
        if (!TryComp<HumanoidAppearanceComponent>(ent, out var humanoid))
            return;

        // Default empty visuals until a profile is loaded (or apply defaults for non-players).
        if (HasComp<ErpOrganVisualsComponent>(ent))
            return;

        ApplyPrefs(ent.Owner, humanoid, ErpOrganPreferences.Default());
    }

    private void ApplyFromProfile(EntityUid uid, HumanoidCharacterProfile profile)
    {
        if (!TryComp<HumanoidAppearanceComponent>(uid, out var humanoid))
            return;

        ApplyPrefs(uid, humanoid, profile.ErpOrgans);
    }

    private void ApplyPrefs(EntityUid uid, HumanoidAppearanceComponent humanoid, ErpOrganPreferences prefs)
    {
        if (!HasComp<EroticOrgansComponent>(uid))
        {
            if (TryComp<ErpOrganVisualsComponent>(uid, out var disabled))
            {
                disabled.Organs = [];
                disabled.CoveredSlots = [];
                disabled.HideWhenFlaccid = [];
                disabled.BreastBounce = new BreastBouncePreferences();
                Dirty(uid, disabled);
            }

            return;
        }

        var erotic = Comp<EroticOrgansComponent>(uid);
        var visuals = EnsureComp<ErpOrganVisualsComponent>(uid);
        visuals.Organs = ErpOrganVisualsHelpers.BuildOrgansFromPrefs(
            prefs,
            humanoid.Species,
            humanoid.Sex,
            _proto,
            _factory);
        visuals.BreastBounce = BreastBouncePreferences.Normalize(prefs.BreastBounce);
        visuals.HideWhenFlaccid = new HashSet<string>(erotic.HideWhenFlaccid);
        Dirty(uid, visuals);
    }
}
