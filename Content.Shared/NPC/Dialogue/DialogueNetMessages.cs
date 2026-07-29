using Lidgren.Network;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace Content.Shared.NPC.Dialogue;

/// <summary>
/// Server → client: open or refresh the dialogue HUD with current speech + options.
/// </summary>
public sealed class MsgDialogueUpdate : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;
    public override NetDeliveryMethod DeliveryMethod => NetDeliveryMethod.ReliableOrdered;

    public NetEntity Npc;
    public string NpcName = string.Empty;
    public string Speech = string.Empty;
    public string[] Options = Array.Empty<string>();

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
        Npc = new NetEntity(buffer.ReadInt32());
        NpcName = buffer.ReadString();
        Speech = buffer.ReadString();
        var count = buffer.ReadByte();
        Options = new string[count];
        for (var i = 0; i < count; i++)
            Options[i] = buffer.ReadString();
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
        buffer.Write((int) Npc);
        buffer.Write(NpcName);
        buffer.Write(Speech);
        buffer.Write((byte) Options.Length);
        foreach (var option in Options)
            buffer.Write(option);
    }
}

/// <summary>
/// Client → server: player picked an option by index.
/// </summary>
public sealed class MsgDialogueChoice : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;
    public override NetDeliveryMethod DeliveryMethod => NetDeliveryMethod.ReliableOrdered;

    public byte OptionIndex;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
        OptionIndex = buffer.ReadByte();
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
        buffer.Write(OptionIndex);
    }
}

/// <summary>
/// Bidirectional: close the dialogue HUD / end session.
/// </summary>
public sealed class MsgDialogueClose : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.Command;
    public override NetDeliveryMethod DeliveryMethod => NetDeliveryMethod.ReliableOrdered;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
    }
}
