using System.Linq;
using Content.Server.Chat.Systems;
using Content.Shared.Interaction;
using Content.Shared.Mobs;
using Content.Shared.NPC.Dialogue;
using Content.Shared.Verbs;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server.NPC.Systems;

/// <summary>
/// Runs NPC dialogue trees: NPC speaks via IC say, player picks HUD options.
/// </summary>
public sealed class NpcDialogueSystem : EntitySystem
{
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;

    private readonly Dictionary<NetUserId, DialogueSession> _sessions = new();

    private const float DialogueRange = SharedInteractionSystem.InteractionRange + 0.5f;

    public override void Initialize()
    {
        base.Initialize();

        _net.RegisterNetMessage<MsgDialogueUpdate>();
        _net.RegisterNetMessage<MsgDialogueClose>(OnCloseMessage);
        _net.RegisterNetMessage<MsgDialogueChoice>(OnChoiceMessage);

        SubscribeLocalEvent<NpcDialogueComponent, GetVerbsEvent<AlternativeVerb>>(OnGetAltVerbs);
        SubscribeLocalEvent<NpcDialogueComponent, ComponentShutdown>(OnDialogueShutdown);
        SubscribeLocalEvent<NpcDialogueComponent, MobStateChangedEvent>(OnMobStateChanged);
        _players.PlayerStatusChanged += OnPlayerStatusChanged;
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _players.PlayerStatusChanged -= OnPlayerStatusChanged;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_sessions.Count == 0)
            return;

        var toClose = new List<NetUserId>();
        foreach (var (userId, session) in _sessions)
        {
            if (!_players.TryGetSessionById(userId, out var player) ||
                player.AttachedEntity is not { } user ||
                !Exists(session.Npc) ||
                !Exists(user) ||
                !_interaction.InRangeUnobstructed(user, session.Npc, DialogueRange))
            {
                toClose.Add(userId);
            }
        }

        foreach (var userId in toClose)
            EndDialogue(userId);
    }

    public bool TryStartDialogue(EntityUid user, EntityUid npc, ICommonSession player)
    {
        if (!TryComp<NpcDialogueComponent>(npc, out var dialogue) || !dialogue.Enabled)
            return false;

        if (!TryResolveTree(dialogue, out var tree) || tree == null)
            return false;

        if (!_interaction.InRangeUnobstructed(user, npc, DialogueRange))
            return false;

        if (_sessions.ContainsKey(player.UserId))
            EndDialogue(player.UserId);

        if (!tree.IsValid())
        {
            Log.Error($"Dialogue on {ToPrettyString(npc)} missing start node '{tree.Start}'");
            return false;
        }

        _sessions[player.UserId] = new DialogueSession(npc, tree, tree.Start);
        EnterNode(player, npc, tree, tree.Start);
        return true;
    }

    private bool TryResolveTree(NpcDialogueComponent dialogue, out NpcDialogueTreeData? tree)
    {
        if (dialogue.CustomDialogue is { } custom && custom.Nodes.Count > 0)
        {
            tree = custom;
            return true;
        }

        if (dialogue.Dialogue is { } protoId &&
            _proto.TryIndex(protoId, out NpcDialoguePrototype? proto))
        {
            tree = NpcDialogueTreeData.FromPrototype(proto);
            return true;
        }

        tree = null;
        return false;
    }

    private void OnGetAltVerbs(Entity<NpcDialogueComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess || !ent.Comp.Enabled)
            return;

        if (!TryResolveTree(ent.Comp, out _))
            return;

        var user = args.User;
        var target = ent.Owner;
        AlternativeVerb verb = new()
        {
            Text = Loc.GetString("npc-dialogue-verb-talk"),
            Icon = new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/settings.svg.192dpi.png")),
            Act = () =>
            {
                if (_players.TryGetSessionByEntity(user, out var session))
                    TryStartDialogue(user, target, session);
            },
            Priority = 10,
        };
        args.Verbs.Add(verb);
    }

    private void OnCloseMessage(MsgDialogueClose message)
    {
        var player = _players.GetSessionByChannel(message.MsgChannel);
        EndDialogue(player.UserId);
    }

    private void OnChoiceMessage(MsgDialogueChoice message)
    {
        var player = _players.GetSessionByChannel(message.MsgChannel);
        if (!_sessions.TryGetValue(player.UserId, out var session))
            return;

        if (player.AttachedEntity is not { } user ||
            !Exists(session.Npc) ||
            !Exists(user) ||
            !_interaction.InRangeUnobstructed(user, session.Npc, DialogueRange))
        {
            EndDialogue(player.UserId);
            return;
        }

        if (!session.Tree.Nodes.TryGetValue(session.NodeId, out var node))
        {
            EndDialogue(player.UserId);
            return;
        }

        if (message.OptionIndex >= node.Options.Count)
            return;

        var option = node.Options[message.OptionIndex];
        if (string.IsNullOrEmpty(option.Next))
        {
            EndDialogue(player.UserId);
            return;
        }

        if (!session.Tree.Nodes.ContainsKey(option.Next))
        {
            Log.Error($"Dialogue option points to missing node '{option.Next}'");
            EndDialogue(player.UserId);
            return;
        }

        session.NodeId = option.Next;
        _sessions[player.UserId] = session;
        EnterNode(player, session.Npc, session.Tree, option.Next);
    }

    private void EnterNode(ICommonSession player, EntityUid npc, NpcDialogueTreeData tree, string nodeId)
    {
        if (!tree.Nodes.TryGetValue(nodeId, out var node))
        {
            EndDialogue(player.UserId);
            return;
        }

        var speech = ResolveText(node.Speech);
        if (!string.IsNullOrWhiteSpace(speech))
        {
            _chat.TrySendInGameICMessage(
                npc,
                speech,
                InGameICChatType.Speak,
                hideChat: false,
                ignoreActionBlocker: true);
        }

        if (node.Options.Count == 0)
        {
            EndDialogue(player.UserId);
            return;
        }

        var options = node.Options.Select(o => ResolveText(o.Text)).ToArray();
        var msg = new MsgDialogueUpdate
        {
            Npc = GetNetEntity(npc),
            NpcName = MetaData(npc).EntityName,
            Speech = speech,
            Options = options,
        };
        _net.ServerSendMessage(msg, player.Channel);
    }

    private static string ResolveText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return Robust.Shared.Localization.Loc.TryGetString(text, out var localized) ? localized : text;
    }

    private void EndDialogue(NetUserId userId)
    {
        if (!_sessions.Remove(userId))
            return;

        if (_players.TryGetSessionById(userId, out var player))
            _net.ServerSendMessage(new MsgDialogueClose(), player.Channel);
    }

    private void EndDialoguesWithNpc(EntityUid npc)
    {
        var toClose = _sessions
            .Where(kv => kv.Value.Npc == npc)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var userId in toClose)
            EndDialogue(userId);
    }

    private void OnDialogueShutdown(Entity<NpcDialogueComponent> ent, ref ComponentShutdown args)
    {
        EndDialoguesWithNpc(ent);
    }

    private void OnMobStateChanged(Entity<NpcDialogueComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Alive)
            return;

        EndDialoguesWithNpc(ent);
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs e)
    {
        if (e.NewStatus == SessionStatus.Disconnected)
            EndDialogue(e.Session.UserId);
    }

    private struct DialogueSession
    {
        public EntityUid Npc;
        public NpcDialogueTreeData Tree;
        public string NodeId;

        public DialogueSession(EntityUid npc, NpcDialogueTreeData tree, string nodeId)
        {
            Npc = npc;
            Tree = tree;
            NodeId = nodeId;
        }
    }
}
