using System.Linq;
using System.Numerics;
using System.Text;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Shared.CCVar;
using Content.Shared.Maps;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.Physics;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.NPC.Systems;

/// <summary>
/// Forms same-faction NPC squads (max 10), maintains a shared rally point, and pushes it to HTN blackboards.
/// Leader is stored for future follow-leader pathfinding; v1 members still path to the rally themselves.
/// </summary>
public sealed class NPCSquadSystem : EntitySystem
{
    public const int MaxSquadSize = 10;
    public const string SquadRallyCoordinatesKey = "SquadRallyCoordinates";
    public const string SquadLeaderKey = "SquadLeader";
    public const string SquadIdKey = "SquadId";

    private static readonly EntProtoId RallyMarkerProto = "NPCSquadRallyMarker";
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(0.75);

    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly NpcFactionSystem _factions = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly TurfSystem _turf = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;

    private readonly Dictionary<Guid, SquadState> _squads = new();
    private readonly HashSet<Entity<NpcFactionMemberComponent>> _factionScratch = new();
    private TimeSpan _nextUpdate;
    private bool _debug;
    private int _eligibleScratch;
    private int _joinAttemptScratch;
    private int _joinSkipScratch;

    private EntityQuery<NPCSquadMemberComponent> _squadQuery;
    private EntityQuery<NpcFactionMemberComponent> _factionQuery;
    private EntityQuery<TransformComponent> _xformQuery;
    private EntityQuery<HTNComponent> _htnQuery;
    private EntityQuery<ActiveNPCComponent> _activeQuery;

    public enum SquadLeaveReason : byte
    {
        Distance,
        Incapacitated,
        Dead,
        MapOrGridMismatch,
        ManualLeave,
        Removed,
    }

    public override void Initialize()
    {
        base.Initialize();
        _squadQuery = GetEntityQuery<NPCSquadMemberComponent>();
        _factionQuery = GetEntityQuery<NpcFactionMemberComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();
        _htnQuery = GetEntityQuery<HTNComponent>();
        _activeQuery = GetEntityQuery<ActiveNPCComponent>();

        _debug = _cfg.GetCVar(CCVars.NPCDebugSquad);
        Subs.CVar(_cfg, CCVars.NPCDebugSquad, v =>
        {
            _debug = v;
            Debug($"npc.debug_squad={(v ? "ON" : "OFF")}");
            if (!v)
                ClearAllMarkers();
        });

        SubscribeLocalEvent<NPCSquadMemberComponent, ComponentStartup>(OnMemberStartup);
        SubscribeLocalEvent<NPCSquadMemberComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnMemberStartup(Entity<NPCSquadMemberComponent> ent, ref ComponentStartup args)
    {
        Debug($"member STARTUP {ToPrettyString(ent)} join={ent.Comp.JoinRange} leave={ent.Comp.LeaveRange} rally={ent.Comp.RallyRadius} blocked={ent.Comp.AutoJoinBlocked}");
    }

    private void OnShutdown(Entity<NPCSquadMemberComponent> ent, ref ComponentShutdown args)
    {
        Debug($"member SHUTDOWN {ToPrettyString(ent)} squad={Short(ent.Comp.SquadId)}");
        LeaveSquad(ent.Owner, ent.Comp, clearBlackboard: true, reason: SquadLeaveReason.Removed);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.CurTime < _nextUpdate)
            return;

        _nextUpdate = _timing.CurTime + UpdateInterval;

        _eligibleScratch = 0;
        _joinAttemptScratch = 0;
        _joinSkipScratch = 0;

        CleanupInvalidMembers();
        TryFormAndJoin();
        UpdateRallies();

        if (_debug)
        {
            var withComp = Count<NPCSquadMemberComponent>();
            var withHtn = 0;
            var withActive = 0;
            var q = EntityQueryEnumerator<NPCSquadMemberComponent>();
            while (q.MoveNext(out var uid, out _))
            {
                if (_htnQuery.HasComponent(uid))
                    withHtn++;
                if (_activeQuery.HasComponent(uid))
                    withActive++;
            }

            Debug(
                $"tick squads={_squads.Count} eligible={_eligibleScratch} joinAttempts={_joinAttemptScratch} joinSkips={_joinSkipScratch} membersWithComp={withComp} withHTN={withHtn} active={withActive} | {DescribeAllSquads()}");
        }
    }

    /// <summary>Future: long-range pathfinding only on the leader.</summary>
    public bool TryGetLeader(Guid squadId, out EntityUid leader)
    {
        leader = default;
        if (!_squads.TryGetValue(squadId, out var squad) || squad.Leader is not { } led || !Exists(led))
            return false;

        leader = led;
        return true;
    }

    public bool IsLeader(EntityUid npc)
    {
        return _squadQuery.TryGetComponent(npc, out var member) &&
               member.SquadId is { } id &&
               _squads.TryGetValue(id, out var squad) &&
               squad.Leader == npc;
    }

    public bool TryGetSquad(EntityUid npc, out SquadState squad)
    {
        squad = default!;
        if (!_squadQuery.TryGetComponent(npc, out var member) || member.SquadId is not { } id)
            return false;

        return _squads.TryGetValue(id, out squad!);
    }

    /// <summary>
    /// True when both entities are currently members of the same active squad.
    /// </summary>
    public bool AreSameSquad(EntityUid a, EntityUid b)
    {
        if (a == b)
            return true;

        if (!_squadQuery.TryGetComponent(a, out var memberA) ||
            !_squadQuery.TryGetComponent(b, out var memberB) ||
            memberA.SquadId is not { } idA ||
            memberB.SquadId != idA)
        {
            return false;
        }

        return _squads.ContainsKey(idA);
    }

    public bool TryGetSquadGrenadeCooldown(EntityUid npc, out TimeSpan readyAt)
    {
        readyAt = default;
        if (!TryGetSquad(npc, out var squad))
            return false;

        readyAt = squad.GrenadeReadyAt;
        return true;
    }

    public void ApplySquadGrenadeCooldown(EntityUid npc, TimeSpan readyAt)
    {
        if (!TryGetSquad(npc, out var squad))
            return;

        if (readyAt > squad.GrenadeReadyAt)
        {
            squad.GrenadeReadyAt = readyAt;
            Debug($"grenade CD squad={Short(squad.Id)} until={readyAt} by {ToPrettyString(npc)}");
        }
    }

    /// <summary>
    /// Request manual leave from current squad.
    /// Intended for future player control flows.
    /// </summary>
    public bool RequestManualLeave(EntityUid npc)
    {
        if (!_squadQuery.TryGetComponent(npc, out var member))
            return false;

        member.ManualLeaveRequested = true;
        Debug($"manual leave REQUESTED {ToPrettyString(npc)} squad={Short(member.SquadId)}");
        return true;
    }

    /// <summary>
    /// Clears auto-join blacklist so this NPC can be auto-grouped again.
    /// </summary>
    public bool AllowAutoJoin(EntityUid npc)
    {
        if (!_squadQuery.TryGetComponent(npc, out var member))
            return false;

        member.AutoJoinBlocked = false;
        Debug($"auto-join ALLOWED {ToPrettyString(npc)}");
        return true;
    }

    /// <summary>
    /// Manual join API for future player control.
    /// Bypasses auto-join blacklist and tries to join target's squad (or form a new one with target).
    /// </summary>
    public bool TryManualJoin(EntityUid npc, EntityUid target)
    {
        if (npc == target ||
            !_squadQuery.TryGetComponent(npc, out var member) ||
            !_squadQuery.TryGetComponent(target, out var targetMember) ||
            !_factionQuery.HasComponent(npc) ||
            !_factionQuery.HasComponent(target) ||
            !_factions.ShareFaction(npc, target) ||
            !_mobState.IsAlive(npc) ||
            !_mobState.IsAlive(target) ||
            _mobState.IsIncapacitated(npc) ||
            _mobState.IsIncapacitated(target) ||
            !_xformQuery.TryGetComponent(npc, out var xform) ||
            !_xformQuery.TryGetComponent(target, out var targetXform) ||
            xform.GridUid is not { } gridUid ||
            targetXform.GridUid != gridUid ||
            xform.MapID != targetXform.MapID)
        {
            Debug($"manual join FAIL {ToPrettyString(npc)} → {ToPrettyString(target)}");
            return false;
        }

        member.AutoJoinBlocked = false;
        member.ManualLeaveRequested = false;
        targetMember.ManualLeaveRequested = false;

        Combine(npc, member, target, targetMember, gridUid, xform.MapID);
        Debug($"manual join OK {ToPrettyString(npc)} → {ToPrettyString(target)} squad={Short(member.SquadId)}");
        return member.SquadId != null;
    }

    private void CleanupInvalidMembers()
    {
        var toDissolve = new List<Guid>();

        foreach (var (id, squad) in _squads)
        {
            var remove = new List<(EntityUid Uid, SquadLeaveReason Reason)>();
            foreach (var member in squad.Members)
            {
                if (!Exists(member) ||
                    !_squadQuery.TryGetComponent(member, out var comp) ||
                    comp.SquadId != id)
                {
                    remove.Add((member, SquadLeaveReason.Removed));
                    continue;
                }

                if (comp.ManualLeaveRequested)
                {
                    remove.Add((member, SquadLeaveReason.ManualLeave));
                    continue;
                }

                if (!_mobState.IsAlive(member))
                {
                    remove.Add((member, SquadLeaveReason.Dead));
                    continue;
                }

                if (_mobState.IsIncapacitated(member) || !_activeQuery.HasComponent(member))
                {
                    remove.Add((member, SquadLeaveReason.Incapacitated));
                    continue;
                }

                if (!_xformQuery.TryGetComponent(member, out var xform) ||
                    xform.GridUid != squad.GridUid ||
                    xform.MapID != squad.MapId)
                {
                    remove.Add((member, SquadLeaveReason.MapOrGridMismatch));
                    continue;
                }

                var rallyMap = _transform.ToMapCoordinates(squad.RallyPoint);
                var memberMap = _transform.GetMapCoordinates(member, xform: xform);
                if (rallyMap.MapId == memberMap.MapId &&
                    (rallyMap.Position - memberMap.Position).Length() > comp.LeaveRange)
                {
                    remove.Add((member, SquadLeaveReason.Distance));
                }
            }

            foreach (var (uid, reason) in remove)
            {
                squad.Members.Remove(uid);
                if (_squadQuery.TryGetComponent(uid, out var left))
                {
                    left.SquadId = null;
                    left.ManualLeaveRequested = false;
                    left.AutoJoinBlocked = true;
                    ClearSquadBlackboard(uid);
                }

                Debug($"LEAVE {DescribeMember(uid)} squad={Short(id)} reason={reason} remaining={squad.Members.Count}");
            }

            if (squad.Members.Count < 2)
            {
                toDissolve.Add(id);
                continue;
            }

            if (squad.Leader is not { } leader || !squad.Members.Contains(leader))
            {
                squad.Leader = PickLeader(squad.Members);
                Debug($"leader REPICK squad={Short(id)} → {DescribeMember(squad.Leader)}");
            }
        }

        foreach (var id in toDissolve)
            DissolveSquad(id);
    }

    private void TryFormAndJoin()
    {
        var query = EntityQueryEnumerator<NPCSquadMemberComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var squadMember, out var xform))
        {
            if (!_htnQuery.HasComponent(uid))
            {
                _joinSkipScratch++;
                continue;
            }

            if (!_activeQuery.HasComponent(uid))
            {
                _joinSkipScratch++;
                if (_debug)
                    Debug($"skip {ToPrettyString(uid)}: not ActiveNPC");
                continue;
            }

            if (!_factionQuery.HasComponent(uid))
            {
                _joinSkipScratch++;
                if (_debug)
                    Debug($"skip {ToPrettyString(uid)}: no NpcFactionMember");
                continue;
            }

            if (squadMember.AutoJoinBlocked || squadMember.ManualLeaveRequested)
            {
                _joinSkipScratch++;
                continue;
            }

            if (!_mobState.IsAlive(uid) || _mobState.IsIncapacitated(uid))
            {
                _joinSkipScratch++;
                continue;
            }

            if (xform.GridUid is not { } gridUid)
            {
                _joinSkipScratch++;
                continue;
            }

            _eligibleScratch++;
            _factionScratch.Clear();
            var mapCoords = _transform.GetMapCoordinates(uid, xform: xform);
            _lookup.GetEntitiesInRange(mapCoords, squadMember.JoinRange, _factionScratch);

            EntityUid? bestAlly = null;
            var bestScore = float.MinValue;
            var nearbyAllies = 0;

            foreach (var other in _factionScratch)
            {
                if (other.Owner == uid)
                    continue;

                if (!_squadQuery.TryGetComponent(other.Owner, out var otherSquad))
                    continue;

                if (otherSquad.AutoJoinBlocked || otherSquad.ManualLeaveRequested)
                    continue;

                if (!_mobState.IsAlive(other.Owner) || !_activeQuery.HasComponent(other.Owner))
                    continue;

                if (!_xformQuery.TryGetComponent(other.Owner, out var otherXform) ||
                    otherXform.GridUid != gridUid ||
                    otherXform.MapID != xform.MapID)
                    continue;

                if (!_factions.ShareFaction(uid, other.Owner))
                    continue;

                nearbyAllies++;

                if (squadMember.SquadId is { } myId && otherSquad.SquadId == myId)
                    continue;

                if (!CanCombine(squadMember, otherSquad, out _))
                    continue;

                var dist = (mapCoords.Position - _transform.GetMapCoordinates(other.Owner, xform: otherXform).Position).Length();
                var score = -dist;
                if (otherSquad.SquadId is { } oid && _squads.TryGetValue(oid, out var os))
                    score += os.Members.Count * 0.5f;

                if (score <= bestScore)
                    continue;

                bestScore = score;
                bestAlly = other.Owner;
            }

            if (bestAlly is not { } ally || !_squadQuery.TryGetComponent(ally, out var allyMember))
            {
                if (_debug && nearbyAllies == 0 && squadMember.SquadId == null)
                {
                    Debug(
                        $"lonely {ToPrettyString(uid)} factionLookups={_factionScratch.Count} range={squadMember.JoinRange} (no same-faction squad allies)");
                }

                continue;
            }

            _joinAttemptScratch++;
            var before = squadMember.SquadId;
            Combine(uid, squadMember, ally, allyMember, gridUid, xform.MapID);
            if (squadMember.SquadId != before)
            {
                Debug(
                    $"JOIN/MERGE {ToPrettyString(uid)} + {ToPrettyString(ally)} → squad={Short(squadMember.SquadId)} (was {Short(before)})");
            }
        }
    }

    private bool CanCombine(NPCSquadMemberComponent a, NPCSquadMemberComponent b, out int combinedSize)
    {
        combinedSize = 0;
        var sizeA = a.SquadId is { } idA && _squads.TryGetValue(idA, out var sa) ? sa.Members.Count : 1;
        var sizeB = b.SquadId is { } idB && _squads.TryGetValue(idB, out var sb) ? sb.Members.Count : 1;

        if (a.SquadId != null && a.SquadId == b.SquadId)
        {
            combinedSize = sizeA;
            return true;
        }

        combinedSize = sizeA + sizeB;
        return combinedSize <= MaxSquadSize;
    }

    private void Combine(
        EntityUid a,
        NPCSquadMemberComponent aComp,
        EntityUid b,
        NPCSquadMemberComponent bComp,
        EntityUid gridUid,
        MapId mapId)
    {
        if (!CanCombine(aComp, bComp, out _))
        {
            Debug($"combine BLOCKED size {ToPrettyString(a)}/{ToPrettyString(b)}");
            return;
        }

        if (aComp.SquadId is { } same && same == bComp.SquadId)
            return;

        if (aComp.SquadId == null && bComp.SquadId == null)
        {
            CreateSquad(a, aComp, b, bComp, gridUid, mapId);
            return;
        }

        if (aComp.SquadId == null && bComp.SquadId is { } bId && _squads.TryGetValue(bId, out var bSquad))
        {
            if (bSquad.Members.Count >= MaxSquadSize)
                return;
            AddMember(bSquad, a, aComp);
            return;
        }

        if (bComp.SquadId == null && aComp.SquadId is { } aId && _squads.TryGetValue(aId, out var aSquad))
        {
            if (aSquad.Members.Count >= MaxSquadSize)
                return;
            AddMember(aSquad, b, bComp);
            return;
        }

        if (aComp.SquadId is not { } idA || bComp.SquadId is not { } idB)
            return;

        if (!_squads.TryGetValue(idA, out var squadA) || !_squads.TryGetValue(idB, out var squadB))
            return;

        if (squadA.Members.Count + squadB.Members.Count > MaxSquadSize)
            return;

        if (squadA.Members.Count >= squadB.Members.Count)
            MergeInto(squadA, squadB);
        else
            MergeInto(squadB, squadA);
    }

    private void CreateSquad(
        EntityUid a,
        NPCSquadMemberComponent aComp,
        EntityUid b,
        NPCSquadMemberComponent bComp,
        EntityUid gridUid,
        MapId mapId)
    {
        var id = Guid.NewGuid();
        var leader = _random.Prob(0.5f) ? a : b;
        var rally = _transform.GetMoverCoordinates(a);
        var squad = new SquadState
        {
            Id = id,
            GridUid = gridUid,
            MapId = mapId,
            Members = new HashSet<EntityUid> { a, b },
            Leader = leader,
            RallyPoint = ClampRally(rally, gridUid),
            SharedResourceNotes = new List<string>(),
        };

        _squads[id] = squad;
        aComp.SquadId = id;
        bComp.SquadId = id;
        aComp.ManualLeaveRequested = false;
        aComp.AutoJoinBlocked = false;
        bComp.ManualLeaveRequested = false;
        bComp.AutoJoinBlocked = false;

        EnsureRallyMarker(squad);
        PushBlackboard(squad);

        Debug(
            $"CREATE squad={Short(id)} leader={ToPrettyString(leader)} members=[{ToPrettyString(a)}, {ToPrettyString(b)}] rally={DescribeCoords(squad.RallyPoint)}");
    }

    private void AddMember(SquadState squad, EntityUid uid, NPCSquadMemberComponent comp)
    {
        if (squad.Members.Count >= MaxSquadSize)
            return;

        if (comp.SquadId is { } old && old != squad.Id)
            LeaveSquad(uid, comp, clearBlackboard: false, reason: SquadLeaveReason.Removed);

        squad.Members.Add(uid);
        comp.SquadId = squad.Id;
        comp.ManualLeaveRequested = false;
        comp.AutoJoinBlocked = false;
        squad.Leader ??= uid;

        PushBlackboardMember(squad, uid);
        Debug($"ADD {ToPrettyString(uid)} → squad={Short(squad.Id)} size={squad.Members.Count} leader={DescribeMember(squad.Leader)}");
    }

    private void MergeInto(SquadState keep, SquadState absorb)
    {
        if (keep.Id == absorb.Id)
            return;

        Debug($"MERGE {Short(absorb.Id)}({absorb.Members.Count}) → {Short(keep.Id)}({keep.Members.Count})");

        foreach (var member in absorb.Members.ToArray())
        {
            if (keep.Members.Count >= MaxSquadSize)
                break;

            if (!_squadQuery.TryGetComponent(member, out var comp))
                continue;

            absorb.Members.Remove(member);
            keep.Members.Add(member);
            comp.SquadId = keep.Id;
            PushBlackboardMember(keep, member);
        }

        if (keep.Leader is not { } led || !keep.Members.Contains(led))
            keep.Leader = PickLeader(keep.Members);

        DissolveSquad(absorb.Id, reassignMembers: false);
    }

    private void LeaveSquad(EntityUid uid, NPCSquadMemberComponent comp, bool clearBlackboard, SquadLeaveReason reason = SquadLeaveReason.Removed)
    {
        if (comp.SquadId is not { } id)
        {
            if (clearBlackboard)
                ClearSquadBlackboard(uid);
            return;
        }

        if (_squads.TryGetValue(id, out var squad))
        {
            squad.Members.Remove(uid);
            if (squad.Leader == uid)
                squad.Leader = squad.Members.Count > 0 ? PickLeader(squad.Members) : null;

            Debug($"LEAVE {ToPrettyString(uid)} squad={Short(id)} reason={reason} remaining={squad.Members.Count}");

            if (squad.Members.Count < 2)
                DissolveSquad(id);
        }

        comp.SquadId = null;
        comp.ManualLeaveRequested = false;
        if (clearBlackboard)
            ClearSquadBlackboard(uid);
    }

    private void DissolveSquad(Guid id, bool reassignMembers = true)
    {
        if (!_squads.Remove(id, out var squad))
            return;

        Debug($"DISSOLVE squad={Short(id)} reassign={reassignMembers} members={squad.Members.Count}");
        DeleteRallyMarker(squad);

        if (!reassignMembers)
            return;

        foreach (var member in squad.Members)
        {
            if (_squadQuery.TryGetComponent(member, out var comp) && comp.SquadId == id)
            {
                comp.SquadId = null;
                // Dissolved for size — allow auto rejoin later.
                comp.AutoJoinBlocked = false;
            }

            ClearSquadBlackboard(member);
        }
    }

    private void UpdateRallies()
    {
        foreach (var squad in _squads.Values)
        {
            if (squad.Members.Count < 2)
                continue;

            if (!TryComputeCentroid(squad, out var centroid))
            {
                Debug($"rally FAIL centroid squad={Short(squad.Id)}");
                continue;
            }

            var previous = squad.RallyPoint;
            squad.RallyPoint = ClampRally(centroid, squad.GridUid);
            EnsureRallyMarker(squad);
            UpdateRallyMarker(squad);
            PushBlackboard(squad);

            if (_debug && (!previous.Equals(squad.RallyPoint) || _timing.CurTick.Value % 8 == 0))
            {
                Debug(
                    $"rally squad={Short(squad.Id)} point={DescribeCoords(squad.RallyPoint)} size={squad.Members.Count} leader={DescribeMember(squad.Leader)} marker={DescribeMember(squad.RallyMarker)} | {DescribeMemberDistances(squad)}");
            }
        }
    }

    private void PushBlackboard(SquadState squad)
    {
        foreach (var member in squad.Members)
            PushBlackboardMember(squad, member);
    }

    private void PushBlackboardMember(SquadState squad, EntityUid member)
    {
        if (!_htnQuery.HasComponent(member))
            return;

        if (!_squadQuery.TryGetComponent(member, out var comp))
            return;

        _npc.SetBlackboard(member, SquadRallyCoordinatesKey, squad.RallyPoint);
        _npc.SetBlackboard(member, SquadIdKey, squad.Id);
        _npc.SetBlackboard(member, NPCBlackboard.SquadRallyRange, comp.RallyRadius);
        if (squad.Leader is { } leader)
            _npc.SetBlackboard(member, SquadLeaderKey, leader);
    }

    private void EnsureRallyMarker(SquadState squad)
    {
        if (!_debug)
        {
            DeleteRallyMarker(squad);
            return;
        }

        if (squad.RallyMarker is { } existing && Exists(existing))
            return;

        var marker = Spawn(RallyMarkerProto, squad.RallyPoint);
        EnsureComp<NPCSquadRallyMarkerComponent>(marker).SquadId = squad.Id;
        squad.RallyMarker = marker;
        Debug($"marker SPAWN {ToPrettyString(marker)} squad={Short(squad.Id)} at {DescribeCoords(squad.RallyPoint)}");
    }

    private void UpdateRallyMarker(SquadState squad)
    {
        if (squad.RallyMarker is not { } marker || !Exists(marker))
            return;

        _transform.SetCoordinates(marker, squad.RallyPoint);
    }

    private void DeleteRallyMarker(SquadState squad)
    {
        if (squad.RallyMarker is not { } marker)
            return;

        if (Exists(marker))
        {
            Debug($"marker DELETE {ToPrettyString(marker)} squad={Short(squad.Id)}");
            QueueDel(marker);
        }

        squad.RallyMarker = null;
    }

    private void ClearAllMarkers()
    {
        foreach (var squad in _squads.Values)
            DeleteRallyMarker(squad);
    }

    private bool TryComputeCentroid(SquadState squad, out EntityCoordinates centroid)
    {
        centroid = default;
        Vector2 sum = default;
        var count = 0;
        EntityUid? anchor = null;

        foreach (var member in squad.Members)
        {
            if (!_xformQuery.TryGetComponent(member, out var xform) || xform.GridUid != squad.GridUid)
                continue;

            var mover = _transform.GetMoverCoordinates(member, xform);
            sum += mover.Position;
            count++;
            anchor ??= mover.EntityId != EntityUid.Invalid ? mover.EntityId : squad.GridUid;
        }

        if (count == 0 || anchor is not { } grid)
            return false;

        centroid = new EntityCoordinates(grid, sum / count);
        return true;
    }

    private EntityCoordinates ClampRally(EntityCoordinates desired, EntityUid gridUid)
    {
        if (!TryComp(gridUid, out MapGridComponent? grid))
            return desired;

        var tile = _map.CoordinatesToTile(gridUid, grid, desired);
        var tileRef = _map.GetTileRef(gridUid, grid, tile);
        if (!tileRef.Tile.IsEmpty && !_turf.IsTileBlocked(tileRef, CollisionGroup.Impassable))
            return _map.GridTileToLocal(gridUid, grid, tile);

        for (var r = 1; r <= 3; r++)
        {
            for (var dx = -r; dx <= r; dx++)
            {
                for (var dy = -r; dy <= r; dy++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r)
                        continue;

                    var cand = tile + new Vector2i(dx, dy);
                    var candRef = _map.GetTileRef(gridUid, grid, cand);
                    if (candRef.Tile.IsEmpty || _turf.IsTileBlocked(candRef, CollisionGroup.Impassable))
                        continue;

                    return _map.GridTileToLocal(gridUid, grid, cand);
                }
            }
        }

        return desired;
    }

    private EntityUid? PickLeader(HashSet<EntityUid> members)
    {
        if (members.Count == 0)
            return null;

        return _random.Pick(members);
    }

    private void ClearSquadBlackboard(EntityUid uid)
    {
        if (!_htnQuery.TryGetComponent(uid, out var htn))
            return;

        htn.Blackboard.Remove<EntityCoordinates>(SquadRallyCoordinatesKey);
        htn.Blackboard.Remove<Guid>(SquadIdKey);
        htn.Blackboard.Remove<EntityUid>(SquadLeaderKey);
    }

    private void Debug(string message)
    {
        if (!_debug)
            return;

        Log.Info($"[npc.squad] {message}");
    }

    private static string Short(Guid? id)
    {
        if (id is not { } g)
            return "none";
        return g.ToString("N")[..8];
    }

    private string DescribeMember(EntityUid? uid)
    {
        return uid is { } e && Exists(e) ? ToPrettyString(e) : "none";
    }

    private string DescribeCoords(EntityCoordinates coords)
    {
        var map = _transform.ToMapCoordinates(coords);
        return $"({map.Position.X:F1},{map.Position.Y:F1}) map={map.MapId}";
    }

    private string DescribeMemberDistances(SquadState squad)
    {
        var sb = new StringBuilder();
        var rallyMap = _transform.ToMapCoordinates(squad.RallyPoint);
        var first = true;
        foreach (var member in squad.Members)
        {
            if (!_xformQuery.TryGetComponent(member, out var xform))
                continue;

            var pos = _transform.GetMapCoordinates(member, xform: xform);
            var dist = rallyMap.MapId == pos.MapId ? (rallyMap.Position - pos.Position).Length() : -1f;
            if (!first)
                sb.Append(", ");
            first = false;
            sb.Append($"{ToPrettyString(member)} d={dist:F1}");
        }

        return sb.ToString();
    }

    private string DescribeAllSquads()
    {
        if (_squads.Count == 0)
            return "no active squads";

        return string.Join(" ; ", _squads.Values.Select(s =>
            $"{Short(s.Id)} n={s.Members.Count} L={DescribeMember(s.Leader)}"));
    }

    /// <summary>
    /// Runtime squad record. Not an entity — lives in <see cref="NPCSquadSystem"/>.
    /// </summary>
    public sealed class SquadState
    {
        public Guid Id;
        public EntityUid GridUid;
        public MapId MapId;
        public HashSet<EntityUid> Members = new();

        /// <summary>Chosen commander; v1 unused for pathfinding (future: followers use leader coords).</summary>
        public EntityUid? Leader;

        public EntityCoordinates RallyPoint;

        public TimeSpan GrenadeReadyAt;

        /// <summary>Visible debug marker entity at the rally point.</summary>
        public EntityUid? RallyMarker;

        /// <summary>Placeholder for future shared ammo / meds / tools notes.</summary>
        public List<string> SharedResourceNotes = new();
    }
}
