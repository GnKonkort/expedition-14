using System.Linq;
using Robust.Shared.Serialization;

namespace Content.Shared.NPC.Dialogue;

/// <summary>
/// Runtime / editable dialogue tree. Speech and option text may be Fluent keys or plain text.
/// </summary>
[DataDefinition]
[Serializable, NetSerializable]
public sealed partial class NpcDialogueTreeData
{
    [DataField]
    public string Start { get; set; } = "start";

    [DataField]
    public Dictionary<string, NpcDialogueNode> Nodes { get; set; } = new();

    public bool IsValid()
    {
        return !string.IsNullOrWhiteSpace(Start) && Nodes.ContainsKey(Start);
    }

    public NpcDialogueTreeData Clone()
    {
        var copy = new NpcDialogueTreeData { Start = Start };
        foreach (var (id, node) in Nodes)
        {
            copy.Nodes[id] = new NpcDialogueNode
            {
                Speech = node.Speech,
                Options = node.Options.Select(o => new NpcDialogueOption
                {
                    Text = o.Text,
                    Next = o.Next,
                }).ToList(),
            };
        }

        return copy;
    }

    public static NpcDialogueTreeData FromPrototype(NpcDialoguePrototype proto)
    {
        var data = new NpcDialogueTreeData { Start = proto.Start };
        foreach (var (id, node) in proto.Nodes)
        {
            data.Nodes[id] = new NpcDialogueNode
            {
                Speech = node.Speech,
                Options = node.Options.Select(o => new NpcDialogueOption
                {
                    Text = o.Text,
                    Next = o.Next,
                }).ToList(),
            };
        }

        return data;
    }
}
