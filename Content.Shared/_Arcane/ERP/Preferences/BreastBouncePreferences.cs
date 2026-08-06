using Robust.Shared.Serialization;

namespace Content.Shared._Arcane.ERP.Preferences;

/// <summary>
/// Client-side breast jiggle settings stored on <see cref="ErpOrganPreferences"/>.
/// </summary>
[DataDefinition]
[Serializable, NetSerializable]
public sealed partial class BreastBouncePreferences
{
    public const float DefaultBounce = 0.35f;
    public const float DefaultSideBounce = 0.25f;

    /// <summary>Master toggle for movement bounce.</summary>
    [DataField]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When true, bounce also drives the stretched jumpsuit layer (offset-only).
    /// Forced off under armor/hardsuits.
    /// </summary>
    [DataField]
    public bool EnabledInJumpsuit { get; set; }

    /// <summary>Vertical springiness while moving (0 = subtle, 1 = maximum).</summary>
    [DataField]
    public float Bounce { get; set; } = DefaultBounce;

    /// <summary>Side-to-side sway while moving (0 = none, 1 = maximum). Defaults slightly below vertical.</summary>
    [DataField]
    public float SideBounce { get; set; } = DefaultSideBounce;

    /// <summary>
    /// When true, both breasts share one oscillator.
    /// When false, two phases produce slight asymmetry (front view).
    /// </summary>
    [DataField]
    public bool Synchronized { get; set; } = true;

    public BreastBouncePreferences Clone() => new()
    {
        Enabled = Enabled,
        EnabledInJumpsuit = EnabledInJumpsuit,
        Bounce = Bounce,
        SideBounce = SideBounce,
        Synchronized = Synchronized,
    };

    public bool MemberwiseEquals(BreastBouncePreferences? other)
    {
        if (other == null)
            return false;

        return Enabled == other.Enabled
               && EnabledInJumpsuit == other.EnabledInJumpsuit
               && Math.Abs(Bounce - other.Bounce) < 0.001f
               && Math.Abs(SideBounce - other.SideBounce) < 0.001f
               && Synchronized == other.Synchronized;
    }

    public static BreastBouncePreferences Normalize(BreastBouncePreferences? input)
    {
        var src = input ?? new BreastBouncePreferences();
        return new BreastBouncePreferences
        {
            Enabled = src.Enabled,
            EnabledInJumpsuit = src.EnabledInJumpsuit,
            Bounce = Math.Clamp(float.IsFinite(src.Bounce) ? src.Bounce : DefaultBounce, 0f, 1f),
            SideBounce = Math.Clamp(float.IsFinite(src.SideBounce) ? src.SideBounce : DefaultSideBounce, 0f, 1f),
            Synchronized = src.Synchronized,
        };
    }
}
