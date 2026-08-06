using System.Numerics;
using Content.Client._Arcane.ERP.Clothing;
using Content.Shared._Arcane.ERP.OrgansAppearance;
using Content.Shared._Arcane.ERP.Preferences;
using Content.Shared.Armor;
using Content.Shared.Inventory;
using Content.Shared.Tag;
using Robust.Client.GameObjects;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Client._Arcane.ERP.OrgansAppearance;

/// <summary>
/// Breast bounce spring. Nude: organ layer. In jumpsuit: breast-cloth overlay only
/// (body suit stays static). Armor/hardsuits always suppress.
/// </summary>
public sealed class ErpBreastBounceSystem : EntitySystem
{
    private const string BreastLayerKey = "erp_breasts";
    private const string OuterClothingSlot = "outerClothing";

    private static readonly ProtoId<TagPrototype> HardsuitTag = "Hardsuit";

    private const float DisplayScaleY = 0.034f;
    private const float DisplayScaleX = 0.028f;
    private const float MaxDisplayY = 0.055f;
    private const float MaxDisplayX = 0.028f;

    [Dependency] private readonly SpriteSystem _sprite = default!;
    [Dependency] private readonly SharedTransformSystem _xform = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly TagSystem _tag = default!;

    private readonly Dictionary<EntityUid, EntityBounceState> _states = new();
    private readonly List<string> _layerKeyScratch = new();

    public override void Shutdown()
    {
        base.Shutdown();
        _states.Clear();
    }

    public override void FrameUpdate(float frameTime)
    {
        if (frameTime <= 0f)
            return;

        var dt = Math.Clamp(frameTime, 0.001f, 0.05f);
        var query = EntityQueryEnumerator<ErpOrganVisualsComponent, SpriteComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var visuals, out var sprite, out var xform))
        {
            if (!visuals.Organs.TryGetValue(ErpOrganSlots.Breasts, out var breastCfg))
            {
                ClearEntity(uid, sprite);
                continue;
            }

            var bounce = BreastBouncePreferences.Normalize(visuals.BreastBounce);
            if (!bounce.Enabled || IsBlockedByOuterArmor(uid))
            {
                ClearEntity(uid, sprite);
                continue;
            }

            if (!TryResolveBounceLayerKeys(uid, sprite, visuals, bounce, _layerKeyScratch, out var jumpsuitMode))
            {
                ClearEntity(uid, sprite);
                continue;
            }

            var state = GetState(uid);
            state.JumpsuitMode = jumpsuitMode;
            SyncActiveLayers(uid, sprite, state, _layerKeyScratch);

            var vel = TryComp<PhysicsComponent>(uid, out var physics)
                ? physics.LinearVelocity
                : Vector2.Zero;

            var worldRot = _xform.GetWorldRotation(xform);
            var yawDelta = (float) (worldRot - new Angle(state.LastYaw)).Reduced().Theta;
            state.LastYaw = (float) worldRot.Theta;

            var motion = Vector2.Lerp(state.LastVelocity, vel, 0.55f) * dt;
            state.LastVelocity = vel;

            var size = Math.Clamp(breastCfg.Size, 1, 4);
            var sizeNorm = size / 4f;
            var bounceAmt = bounce.Bounce;
            var sideAmt = bounce.SideBounce;

            var intensity = sizeNorm * (0.55f + bounceAmt * 1.8f);
            // Overlay must not permanently sag away from the suit hole.
            var weight = jumpsuitMode ? 0f : sizeNorm * 0.7f * (0.25f + bounceAmt * 0.45f);
            var floppiness = Math.Clamp(0.25f + bounceAmt * 0.55f, 0.15f, 0.85f);

            var walkSpeed = Math.Clamp(vel.Length(), 0f, 6.5f);
            state.WalkPhase += dt * (4.8f + walkSpeed * 2.4f);

            var vertVel = vel.Y;
            if ((state.LastVertVel <= 0f && vertVel > 0.05f) ||
                (state.LastVertVel < -0.05f && MathF.Abs(vertVel) < 0.02f))
            {
                state.RandomLean = _random.Next(2) == 0 ? -1 : 1;
            }

            state.LastVertVel = vertVel;

            var facing = worldRot.GetCardinalDir();
            var sideView = facing is Direction.East or Direction.West;
            var yawImpulse = -(yawDelta / 0.35f) * intensity;

            TickOne(
                state.Sim,
                motion,
                walkSpeed,
                state.WalkPhase,
                yawImpulse,
                intensity,
                weight,
                floppiness,
                sideView ? 0f : sideAmt,
                state.RandomLean,
                dt);

            foreach (var key in _layerKeyScratch)
            {
                if (!_sprite.LayerMapTryGet((uid, sprite), key, out var layerIdx, false))
                    continue;

                ApplyVisual(uid, sprite, layerIdx, state.Sim, sideView, sideAmt, jumpsuitMode);
            }
        }
    }

    private bool IsBlockedByOuterArmor(EntityUid uid)
    {
        if (!_inventory.TryGetSlotEntity(uid, OuterClothingSlot, out var outer))
            return false;

        if (HasComp<ArmorComponent>(outer.Value))
            return true;

        return _tag.HasTag(outer.Value, HardsuitTag);
    }

    private bool TryResolveBounceLayerKeys(
        EntityUid uid,
        SpriteComponent sprite,
        ErpOrganVisualsComponent visuals,
        BreastBouncePreferences bounce,
        List<string> keysOut,
        out bool jumpsuitMode)
    {
        keysOut.Clear();
        jumpsuitMode = false;

        var breastsCovered = visuals.CoveredSlots.Contains(ErpOrganSlots.Breasts);
        if (!breastsCovered)
        {
            if (!_sprite.LayerMapTryGet((uid, sprite), BreastLayerKey, out var breastIdx, false) ||
                !sprite[breastIdx].Visible)
                return false;

            keysOut.Add(BreastLayerKey);
            return true;
        }

        if (!bounce.EnabledInJumpsuit)
            return false;

        if (!TryComp<ErpJumpsuitStretchComponent>(uid, out var stretch) ||
            string.IsNullOrEmpty(stretch.BounceLayerKeyActive))
            return false;

        var key = stretch.BounceLayerKeyActive;
        if (!_sprite.LayerMapTryGet((uid, sprite), key, out var idx, false) || !sprite[idx].Visible)
            return false;

        keysOut.Add(key);
        jumpsuitMode = true;
        return true;
    }

    private void SyncActiveLayers(EntityUid uid, SpriteComponent sprite, EntityBounceState state, List<string> active)
    {
        for (var i = state.ActiveLayerKeys.Count - 1; i >= 0; i--)
        {
            var oldKey = state.ActiveLayerKeys[i];
            if (active.Contains(oldKey))
                continue;

            if (_sprite.LayerMapTryGet((uid, sprite), oldKey, out var oldIdx, false))
                ResetLayer(uid, sprite, oldIdx);

            state.ActiveLayerKeys.RemoveAt(i);
        }

        foreach (var key in active)
        {
            if (!state.ActiveLayerKeys.Contains(key))
                state.ActiveLayerKeys.Add(key);
        }
    }

    private static void TickOne(
        BreastPhysicsSim sim,
        Vector2 motion,
        float walkSpeed,
        float walkPhase,
        float yawImpulse,
        float bounceIntensity,
        float breastWeight,
        float floppiness,
        float sideAmt,
        int leanSign,
        float dt)
    {
        var targetY = motion.Y * bounceIntensity * 12f;
        targetY += breastWeight;

        var f2 = MathF.Max(1f, walkSpeed * walkSpeed);
        targetY += MathF.Cos(walkPhase * 0.6662f + MathF.PI) * 0.45f * walkSpeed * 0.55f / f2 * bounceIntensity * 2.2f;
        targetY += MathF.Sin(walkPhase) * MathF.Abs(motion.X) * bounceIntensity * 2.5f;

        var targetX = -yawImpulse / 10f * (0.35f + sideAmt * 0.65f);
        targetX += motion.X * bounceIntensity * 4f * (0.25f + sideAmt * 0.75f);

        var targetRot = yawImpulse * 0.85f;
        targetRot += motion.Y * bounceIntensity * 2.5f * leanSign;

        var percent = floppiness;
        var bounceAmount = Math.Clamp(0.45f * (1f - percent) + 0.15f, 0.15f, 0.6f);
        var delta = 2.25f - bounceAmount;

        var distanceFromMin = MathF.Abs(sim.BounceY + 1.5f) * 0.5f;
        var distanceFromMax = MathF.Abs(sim.BounceY - 2.65f) * 0.5f;
        if (sim.BounceY < -0.5f)
            targetY += distanceFromMin;
        if (sim.BounceY > 2.5f)
            targetY -= distanceFromMax;

        targetY = Math.Clamp(targetY, -1.5f, 2.5f);
        targetX = Math.Clamp(targetX, -1.2f, 1.2f);
        targetRot = Math.Clamp(targetRot, -25f, 25f);

        var tickScale = Math.Clamp(dt * 20f, 0.35f, 1.6f);

        sim.VelY = Lerp(sim.VelY, (targetY - sim.BounceY) * delta, bounceAmount);
        sim.BounceY += sim.VelY * percent * 1.1625f * tickScale;

        sim.VelX = Lerp(sim.VelX, (targetX - sim.BounceX) * delta, bounceAmount);
        sim.BounceX += sim.VelX * percent * tickScale;

        sim.VelRot = Lerp(sim.VelRot, (targetRot - sim.BounceRot) * delta, bounceAmount);
        sim.BounceRot += sim.VelRot * percent * tickScale;

        sim.PosY = sim.BounceY;
        sim.PosX = sim.BounceX;
        sim.Rot = sim.BounceRot;

        if (sim.PosY < -0.45f)
            sim.PosY = -0.45f;
        if (sim.PosY > 1.15f)
        {
            sim.PosY = 1.15f;
            sim.VelY = 0f;
        }
    }

    private void ApplyVisual(
        EntityUid uid,
        SpriteComponent sprite,
        int layerIdx,
        BreastPhysicsSim sim,
        bool sideView,
        float sideAmt,
        bool jumpsuitMode)
    {
        // Breast overlay is cloth-only — full spring is fine; body suit never moves.
        var yScale = jumpsuitMode ? DisplayScaleY * 0.9f : DisplayScaleY;
        var maxY = jumpsuitMode ? MaxDisplayY * 0.85f : MaxDisplayY;
        var y = Math.Clamp(sim.PosY * yScale, -maxY, maxY);
        var maxX = sideView
            ? (jumpsuitMode ? 0.008f : 0.01f)
            : MaxDisplayX * (0.4f + sideAmt * 0.6f) * (jumpsuitMode ? 0.85f : 1f);
        var x = sideView ? 0f : Math.Clamp(sim.PosX * DisplayScaleX, -maxX, maxX);

        var strain = Math.Clamp(sim.PosY / 1.5f, -1f, 1f);
        var rate = Math.Clamp(sim.VelY / 3f, -1f, 1f);
        var squashAmt = jumpsuitMode ? 0.08f : 0.12f;
        var rateAmt = jumpsuitMode ? 0.04f : 0.06f;
        var squash = strain * squashAmt + rate * rateAmt;
        var scaleY = Math.Clamp(1f - squash, jumpsuitMode ? 0.86f : 0.78f, jumpsuitMode ? 1.14f : 1.22f);
        var scaleX = Math.Clamp(1f / Math.Max(scaleY, 0.7f), jumpsuitMode ? 0.88f : 0.82f, jumpsuitMode ? 1.16f : 1.28f);

        var hang = (1f - scaleY) * (jumpsuitMode ? 0.012f : 0.018f);
        var rotDeg = sideView
            ? 0f
            : Math.Clamp(sim.Rot * 0.22f + x * 40f, jumpsuitMode ? -4f : -5f, jumpsuitMode ? 4f : 5f);

        _sprite.LayerSetOffset((uid, sprite), layerIdx, new Vector2(x, -y - hang));
        _sprite.LayerSetScale((uid, sprite), layerIdx, new Vector2(scaleX, scaleY));
        _sprite.LayerSetRotation((uid, sprite), layerIdx, Angle.FromDegrees(rotDeg));
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);

    private void ClearEntity(EntityUid uid, SpriteComponent sprite)
    {
        if (!_states.TryGetValue(uid, out var state))
            return;

        foreach (var key in state.ActiveLayerKeys)
        {
            if (_sprite.LayerMapTryGet((uid, sprite), key, out var idx, false))
                ResetLayer(uid, sprite, idx);
        }

        state.Sim.Reset();
        state.WalkPhase = 0f;
        state.ActiveLayerKeys.Clear();
        _states.Remove(uid);
    }

    private void ResetLayer(EntityUid uid, SpriteComponent sprite, int layerIdx)
    {
        _sprite.LayerSetOffset((uid, sprite), layerIdx, Vector2.Zero);
        _sprite.LayerSetScale((uid, sprite), layerIdx, Vector2.One);
        _sprite.LayerSetRotation((uid, sprite), layerIdx, Angle.Zero);
    }

    private EntityBounceState GetState(EntityUid uid)
    {
        if (!_states.TryGetValue(uid, out var state))
        {
            state = new EntityBounceState();
            _states[uid] = state;
        }

        return state;
    }
}

internal sealed class EntityBounceState
{
    public readonly BreastPhysicsSim Sim = new();
    public readonly List<string> ActiveLayerKeys = [];
    public bool JumpsuitMode;
    public float WalkPhase;
    public float LastYaw;
    public float LastVertVel;
    public int RandomLean = 1;
    public Vector2 LastVelocity;
}

internal sealed class BreastPhysicsSim
{
    public float BounceY;
    public float VelY;
    public float BounceX;
    public float VelX;
    public float BounceRot;
    public float VelRot;
    public float PosY;
    public float PosX;
    public float Rot;

    public void Reset()
    {
        BounceY = VelY = BounceX = VelX = BounceRot = VelRot = 0f;
        PosY = PosX = Rot = 0f;
    }
}
