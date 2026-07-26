namespace Content.Server.NPC.Queries.Considerations;

/// <summary>
/// Returns 1f when matching ammo for the held gun exists nearby (loose or in sampled storages).
/// Target is ignored; used as a gate consideration on utility queries when needed.
/// </summary>
public sealed partial class HeldGunHasNearbyMatchingAmmoCon : UtilityConsideration
{
}
