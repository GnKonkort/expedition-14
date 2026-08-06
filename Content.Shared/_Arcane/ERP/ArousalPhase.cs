namespace Content.Shared._Arcane.ERP;

/// <summary>
/// Visual arousal phases used by organ RSI state selection.
/// Full arousal gameplay is out of scope; only Calm and Aroused are used for lobby preview.
/// </summary>
public enum ArousalPhase : byte
{
    Calm,
    Interested,
    Aroused,
    Heated,
    Peak,
}
