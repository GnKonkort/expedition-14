using System.Numerics;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared.Destructible;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared._CitadelStation.SubGrid.Components;
using Content.Shared.Verbs;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Server._CitadelStation.SubGrid.Systems;

/// <summary>
/// Antigrav core: toggles sub-grid drive and cycles movement modes.
/// Drive cannot be unlocked while the mother (host) grid is moving; host motion force-parks.
/// </summary>
public sealed class SubGridAntigravSystem : EntitySystem
{
    /// <summary>Linear / angular speed below this counts as stopped.</summary>
    public const float HostStillEpsilon = 0.05f;

    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly ShuttleSystem _shuttle = default!;
    [Dependency] private readonly SubGridMovementSystem _movement = default!;
    [Dependency] private readonly SubGridSystem _subGrids = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SubGridHostFollowSystem _hostFollow = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SubGridAntigravComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<SubGridAntigravComponent, ActivateInWorldEvent>(OnActivate);
        SubscribeLocalEvent<SubGridAntigravComponent, GetVerbsEvent<AlternativeVerb>>(OnGetVerbs);
        SubscribeLocalEvent<SubGridAntigravComponent, BreakageEventArgs>(OnBreak);
        SubscribeLocalEvent<SubGridAntigravComponent, DestructionEventArgs>(OnDestroyed);
        SubscribeLocalEvent<SubGridAntigravComponent, ComponentShutdown>(OnShutdown);
        Log.Info("SubGridAntigravSystem initialized");
    }

    private void OnStartup(Entity<SubGridAntigravComponent> ent, ref ComponentStartup args)
    {
        Log.Debug("Antigrav startup: {Ent} enabled={Enabled} mode={Mode} grid={Grid}",
            ToPrettyString(ent), ent.Comp.Enabled, ent.Comp.Mode, Transform(ent).GridUid);
        SyncToGrid(ent);
    }

    private void OnActivate(Entity<SubGridAntigravComponent> ent, ref ActivateInWorldEvent args)
    {
        Log.Debug("Antigrav activate: {Ent} user={User} handled={Handled} complex={Complex} wasEnabled={Enabled}",
            ToPrettyString(ent), ToPrettyString(args.User), args.Handled, args.Complex, ent.Comp.Enabled);

        if (args.Handled || !args.Complex)
        {
            Log.Debug("Antigrav activate ignored (handled or not complex): {Ent}", ToPrettyString(ent));
            return;
        }

        args.Handled = true;
        SetEnabled(ent, !ent.Comp.Enabled, args.User);
    }

    private void OnGetVerbs(Entity<SubGridAntigravComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;

        var user = args.User;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("subgrid-antigrav-verb-cycle-mode"),
            Act = () => CycleMode(ent, user),
            Priority = 1,
        });
    }

    private void OnBreak(EntityUid uid, SubGridAntigravComponent component, BreakageEventArgs args)
    {
        Log.Warning("Antigrav broken: {Ent}", ToPrettyString(uid));
        SetEnabled((uid, component), false);
    }

    private void OnDestroyed(EntityUid uid, SubGridAntigravComponent component, DestructionEventArgs args)
    {
        Log.Warning("Antigrav destroyed: {Ent}", ToPrettyString(uid));
        SetEnabled((uid, component), false);
    }

    private void OnShutdown(Entity<SubGridAntigravComponent> ent, ref ComponentShutdown args)
    {
        Log.Debug("Antigrav shutdown: {Ent}", ToPrettyString(ent));
        SetEnabled(ent, false);
    }

    public void CycleMode(Entity<SubGridAntigravComponent> ent, EntityUid? user = null)
    {
        var list = ent.Comp.ModeCycle;
        if (list.Count == 0)
            return;

        var old = ent.Comp.Mode;
        var idx = list.IndexOf(ent.Comp.Mode);
        ent.Comp.Mode = list[(idx + 1) % list.Count];
        Dirty(ent);
        Log.Info("Antigrav mode {Old} -> {New} on {Ent}", old, ent.Comp.Mode, ToPrettyString(ent));
        SyncToGrid(ent);

        if (user != null)
        {
            _popup.PopupEntity(
                Loc.GetString("subgrid-antigrav-mode-set", ("mode", ent.Comp.Mode.ToString())),
                ent,
                user.Value);
        }
    }

    public void SetEnabled(Entity<SubGridAntigravComponent> ent, bool enabled, EntityUid? user = null)
    {
        if (enabled && IsHostMovingUnderAntigrav(ent, out var hostSpd, out var hostAng))
        {
            Log.Info("Antigrav unlock blocked (host moving): {Ent} hostSpd={Spd:F2} hostAng={Ang:F3}",
                ToPrettyString(ent), hostSpd, hostAng);
            if (user != null)
            {
                _popup.PopupEntity(
                    Loc.GetString("subgrid-antigrav-host-moving-block"),
                    ent,
                    user.Value);
            }

            return;
        }

        if (ent.Comp.Enabled == enabled)
        {
            Log.Debug("Antigrav SetEnabled no-op (already {Enabled}): {Ent}", enabled, ToPrettyString(ent));
            SyncToGrid(ent);
            return;
        }

        Log.Info("Antigrav SetEnabled {Enabled}: {Ent} user={User}",
            enabled, ToPrettyString(ent), user == null ? "null" : ToPrettyString(user.Value));
        ent.Comp.Enabled = enabled;
        Dirty(ent);
        SyncToGrid(ent);

        if (user != null)
        {
            _popup.PopupEntity(
                Loc.GetString(enabled ? "subgrid-antigrav-enabled" : "subgrid-antigrav-disabled"),
                ent,
                user.Value);
        }
    }

    /// <summary>
    /// Force park when the mother grid is moving. No-op if already parked.
    /// </summary>
    public bool TryForceParkForHostMotion(EntityUid subGridUid, SubGridComponent sub)
    {
        if (!sub.DriveEnabled)
            return false;

        if (sub.HostGrid is not { } host || Deleted(host) || !IsGridMoving(host, out var spd, out var ang))
            return false;

        if (!TryFindAntigrav(subGridUid, out var antigrav))
        {
            sub.DriveEnabled = false;
            Dirty(subGridUid, sub);
            if (TryComp(subGridUid, out ShuttleComponent? shuttle))
            {
                shuttle.Enabled = false;
                if (TryComp(subGridUid, out PhysicsComponent? body))
                {
                    _physics.SetBodyType(subGridUid, BodyType.Kinematic, body: body);
                    _physics.SetLinearVelocity(subGridUid, Vector2.Zero, body: body);
                    _physics.SetAngularVelocity(subGridUid, 0f, body: body);
                    _physics.SetFixedRotation(subGridUid, true, body: body);
                }
            }

            _hostFollow.RecaptureHostPose(subGridUid, sub);
            return true;
        }

        Log.Info("Antigrav force-park (host moving): {Grid} hostSpd={Spd:F2} hostAng={Ang:F3}",
            ToPrettyString(subGridUid), spd, ang);

        antigrav.Comp.Enabled = false;
        Dirty(antigrav);
        SyncToGrid(antigrav);

        _popup.PopupCoordinates(
            Loc.GetString("subgrid-antigrav-host-moving-forced"),
            Transform(subGridUid).Coordinates);

        return true;
    }

    public bool IsHostMovingUnderAntigrav(Entity<SubGridAntigravComponent> ent, out float speed, out float angVel)
    {
        speed = 0f;
        angVel = 0f;
        var gridUid = Transform(ent).GridUid;
        if (gridUid == null || !TryComp(gridUid.Value, out SubGridComponent? sub))
            return false;
        if (sub.HostGrid is not { } host || Deleted(host))
            return false;
        return IsGridMoving(host, out speed, out angVel);
    }

    public static bool IsGridMoving(PhysicsComponent body, float epsilon = HostStillEpsilon)
    {
        return body.LinearVelocity.LengthSquared() > epsilon * epsilon
               || MathF.Abs(body.AngularVelocity) > epsilon;
    }

    public bool IsGridMoving(EntityUid grid, out float speed, out float angVel, float epsilon = HostStillEpsilon)
    {
        speed = 0f;
        angVel = 0f;
        if (!TryComp(grid, out PhysicsComponent? body))
            return false;

        speed = body.LinearVelocity.Length();
        angVel = body.AngularVelocity;
        return IsGridMoving(body, epsilon);
    }

    public bool TryFindAntigrav(EntityUid subGridUid, out Entity<SubGridAntigravComponent> antigrav)
    {
        var query = EntityQueryEnumerator<SubGridAntigravComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var comp, out var xform))
        {
            if (xform.GridUid != subGridUid)
                continue;

            antigrav = (uid, comp);
            return true;
        }

        antigrav = default;
        return false;
    }

    public void SyncToGrid(Entity<SubGridAntigravComponent> ent)
    {
        var xform = Transform(ent);
        if (xform.GridUid is not { } gridUid)
        {
            Log.Warning("Antigrav SyncToGrid: no GridUid on {Ent}", ToPrettyString(ent));
            return;
        }

        if (!TryComp<SubGridComponent>(gridUid, out var sub))
        {
            Log.Warning("Antigrav SyncToGrid: grid {Grid} missing SubGridComponent (antigrav={Ent})",
                ToPrettyString(gridUid), ToPrettyString(ent));
            return;
        }

        sub.DriveEnabled = ent.Comp.Enabled;
        sub.Mode = ent.Comp.Mode;
        Dirty(gridUid, sub);

        if (!TryComp<ShuttleComponent>(gridUid, out var shuttle))
        {
            Log.Warning("Antigrav SyncToGrid: grid {Grid} missing ShuttleComponent", ToPrettyString(gridUid));
            return;
        }

        if (ent.Comp.Enabled)
        {
            shuttle.Enabled = true;
            _shuttle.Enable(gridUid, shuttle: shuttle);
            Log.Debug("Antigrav enabled shuttle Dynamic on {Grid}", ToPrettyString(gridUid));
        }
        else
        {
            shuttle.Enabled = false;
            if (TryComp(gridUid, out PhysicsComponent? body))
            {
                _physics.SetBodyType(gridUid, BodyType.Kinematic, body: body);
                _physics.SetLinearVelocity(gridUid, Vector2.Zero, body: body);
                _physics.SetAngularVelocity(gridUid, 0f, body: body);
                _physics.SetFixedRotation(gridUid, true, body: body);
            }

            Log.Debug("Antigrav drive-off set Kinematic on {Grid}", ToPrettyString(gridUid));
        }

        Log.Info("Antigrav SyncToGrid done: antigrav={Ent} grid={Grid} drive={Drive} mode={Mode}",
            ToPrettyString(ent), ToPrettyString(gridUid), ent.Comp.Enabled, ent.Comp.Mode);
        _movement.ApplyMode(gridUid, ent.Comp.Mode, ent.Comp.Enabled);
        _subGrids.RebuildBumper((gridUid, sub));
        _hostFollow.RecaptureHostPose(gridUid, sub);
    }
}
