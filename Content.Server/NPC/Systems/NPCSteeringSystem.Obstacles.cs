using System.Numerics;
using Content.Server.Destructible;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Climbing;
using Content.Shared.CombatMode;
using Content.Shared.DoAfter;
using Content.Shared.Doors.Components;
using Content.Shared.Interaction;
using Content.Shared.NPC;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Utility;
using ClimbableComponent = Content.Shared.Climbing.Components.ClimbableComponent;
using ClimbingComponent = Content.Shared.Climbing.Components.ClimbingComponent;

namespace Content.Server.NPC.Systems;

public sealed partial class NPCSteeringSystem
{
    /*
     * For any custom path handlers, e.g. destroying walls, opening airlocks, etc.
     * Putting it onto steering seemed easier than trying to make a custom compound task for it.
     * I also considered task interrupts although the problem is handling stuff like pathfinding overlaps
     * Ideally we could do interrupts but that's TODO.
     */

    /*
     * TODO:
     * - Add path cap
     * - Circle cast BFS in LOS to determine targets.
     * - Store last known coordinates of X targets.
     * - Require line of sight for melee
     * - Add new behavior where they move to melee target's last known position (diffing theirs and current)
     *  then do the thing like from dishonored where it gets passed to a search system that opens random stuff.
     *
     * Also need to make sure it picks nearest obstacle path so it starts smashing in front of it.
     */

    /// <summary>
    /// Only vault when the climbable is on the adjacent tile (not InteractionRange ~1.5+).
    /// Slightly above 1.0 so NPCs pressed into a thin cade strip can still start the vault.
    /// </summary>
    private const float ClimbVaultMaxRange = 1.25f;

    private SteeringObstacleStatus TryHandleFlags(EntityUid uid, NPCSteeringComponent component, PathPoly poly)
    {
        DebugTools.Assert(!poly.Data.IsFreeSpace);
        // TODO: Store PathFlags on the steering comp
        // and be able to re-check it.

        var layer = 0;
        var mask = 0;

        if (TryComp<FixturesComponent>(uid, out var manager))
        {
            (layer, mask) = _physics.GetHardCollision(uid, manager);
        }
        else
        {
            return SteeringObstacleStatus.Failed;
        }

        // TODO: Should cache the fact we're doing this somewhere.
        // See https://github.com/space-wizards/space-station-14/issues/11475
        if ((poly.Data.CollisionLayer & mask) != 0x0 ||
            (poly.Data.CollisionMask & layer) != 0x0)
        {
            var id = component.DoAfterId;

            // Still doing what we were doing before.
            var doAfterStatus = _doAfter.GetStatus(id);

            switch (doAfterStatus)
            {
                case DoAfterStatus.Running:
                    ClimbDebug(uid, $"HANDLE doAfter=Running");
                    return SteeringObstacleStatus.Continuing;
                case DoAfterStatus.Cancelled:
                    ClimbDebug(uid, $"HANDLE doAfter=Cancelled → Failed");
                    return SteeringObstacleStatus.Failed;
            }

            var obstacleEnts = new List<EntityUid>();

            GetObstacleEntities(poly, mask, layer, obstacleEnts);
            var isDoor = (poly.Data.Flags & PathfindingBreadcrumbFlag.Door) != 0x0;
            var isAccessRequired = (poly.Data.Flags & PathfindingBreadcrumbFlag.Access) != 0x0;
            var isClimbable = (poly.Data.Flags & PathfindingBreadcrumbFlag.Climb) != 0x0;

            ClimbDebug(uid,
                $"HANDLE poly climb={isClimbable} door={isDoor} access={isAccessRequired} obstacles={obstacleEnts.Count} flags={component.Flags}");

            // Just walk into it stupid
            if (isDoor && !isAccessRequired)
            {
                var doorQuery = GetEntityQuery<DoorComponent>();

                // ... At least if it's not a bump open.
                foreach (var ent in obstacleEnts)
                {
                    if (!doorQuery.TryGetComponent(ent, out var door))
                        continue;

                    if (!door.BumpOpen && (component.Flags & PathFlags.Interact) != 0x0)
                    {
                        if (door.State != DoorState.Opening)
                        {
                            _interaction.InteractionActivate(uid, ent);
                            return SteeringObstacleStatus.Continuing;
                        }
                    }
                }

                // If we get to here then didn't succeed for reasons.
            }

            if ((component.Flags & PathFlags.Prying) != 0x0 && isDoor)
            {
                var doorQuery = GetEntityQuery<DoorComponent>();

                // Get the relevant obstacle
                foreach (var ent in obstacleEnts)
                {
                    if (doorQuery.TryGetComponent(ent, out var door) && door.State != DoorState.Open)
                    {
                        // TODO: Use the verb.

                        if (door.State != DoorState.Opening)
                            _pryingSystem.TryPry(ent, uid, out id, uid);

                        component.DoAfterId = id;
                        return SteeringObstacleStatus.Continuing;
                    }
                }

                if (obstacleEnts.Count == 0)
                    return SteeringObstacleStatus.Completed;
            }
            // Try climbing obstacles (before smash — barricades/tables are climbable AND destructible).
            // Only the nearest adjacent climbable — never a cade several tiles away.
            else if (isClimbable && TryComp<ClimbingComponent>(uid, out var climbing) && climbing.CanClimb)
            {
                component.Flags |= PathFlags.Climbing;

                // Still sliding onto the climbable — wait (do not dequeue yet).
                if (climbing.NextTransition != null || climbing.DoAfter != null)
                {
                    ClimbDebug(uid,
                        $"CLIMB wait doAfter={climbing.DoAfter != null} transition={climbing.NextTransition != null}");
                    return SteeringObstacleStatus.Continuing;
                }

                // Fixtures already swapped — can path through while IsClimbing.
                if (climbing.IsClimbing)
                {
                    ClimbDebug(uid, "CLIMB isClimbing → Completed (pass through)");
                    return SteeringObstacleStatus.Completed;
                }

                if (TryClimbNearest(uid, component, climbing, obstacleEnts, out id))
                {
                    component.DoAfterId = id;
                    ClimbDebug(uid, $"CLIMB started doAfter={id != null} obstacles={obstacleEnts.Count}");
                    return SteeringObstacleStatus.Continuing;
                }

                if (obstacleEnts.Count == 0)
                {
                    ClimbDebug(uid, "CLIMB no hard obstacles → Completed");
                    return SteeringObstacleStatus.Completed;
                }

                ClimbDebug(uid, $"CLIMB start-failed obstacles={obstacleEnts.Count} → Failed/repath");
                // Vault didn't start — fail so we repath around instead of freezing on the cade.
                return SteeringObstacleStatus.Failed;
            }
            // Try smashing obstacles.
            else if ((component.Flags & PathFlags.Smashing) != 0x0)
            {
                if (_melee.TryGetWeapon(uid, out _, out var meleeWeapon) && meleeWeapon.NextAttack <= _timing.CurTime && TryComp<CombatModeComponent>(uid, out var combatMode))
                {
                    _combat.SetInCombatMode(uid, true, combatMode);
                    var destructibleQuery = GetEntityQuery<DestructibleComponent>();

                    // TODO: This is a hack around grilles and windows.
                    _random.Shuffle(obstacleEnts);
                    var attackResult = false;

                    foreach (var ent in obstacleEnts)
                    {
                        // TODO: Validate we can damage it
                        if (destructibleQuery.HasComponent(ent))
                        {
                            attackResult = _melee.AttemptLightAttack(uid, uid, meleeWeapon, ent);
                            break;
                        }
                    }

                    _combat.SetInCombatMode(uid, false, combatMode);

                    // Blocked or the likes?
                    if (!attackResult)
                        return SteeringObstacleStatus.Failed;

                    if (obstacleEnts.Count == 0)
                        return SteeringObstacleStatus.Completed;

                    return SteeringObstacleStatus.Continuing;
                }
            }

            return SteeringObstacleStatus.Failed;
        }

        return SteeringObstacleStatus.Completed;
    }

    /// <summary>
    /// Vault only the nearest climbable that is actually adjacent and in the way.
    /// </summary>
    private bool TryVaultNearbyClimbable(EntityUid uid, NPCSteeringComponent component)
    {
        if (!TryComp<ClimbingComponent>(uid, out var climbing) || !climbing.CanClimb)
        {
            ClimbDebug(uid, "VAULT-NEAR skip canClimb=false");
            return false;
        }

        component.Flags |= PathFlags.Climbing;

        if (climbing.IsClimbing || climbing.NextTransition != null || climbing.DoAfter != null)
        {
            ClimbDebug(uid,
                $"VAULT-NEAR already busy isClimbing={climbing.IsClimbing} doAfter={climbing.DoAfter != null} transition={climbing.NextTransition != null}");
            return true;
        }

        // Prefer the current path node if it is a climbable obstacle (still adjacent-gated inside).
        if (component.CurrentPath.TryPeek(out var poly) &&
            (poly.Data.Flags & PathfindingBreadcrumbFlag.Climb) != 0x0)
        {
            ClimbDebug(uid, "VAULT-NEAR try path-node");
            var status = TryHandleFlags(uid, component, poly);
            ClimbDebug(uid, $"VAULT-NEAR path-node result={status}");
            if (status != SteeringObstacleStatus.Failed)
                return true;
        }

        if (!_xformQuery.TryGetComponent(uid, out _))
            return false;

        var ents = _entSetPool.Get();
        // Strict adjacent search — do not use full InteractionRange (picks cades 2–3 tiles out).
        _lookup.GetEntitiesInRange(uid, ClimbVaultMaxRange, ents, LookupFlags.Static);

        var candidates = new List<EntityUid>(ents.Count);
        var climbableQuery = GetEntityQuery<ClimbableComponent>();
        foreach (var ent in ents)
        {
            if (climbableQuery.HasComponent(ent))
                candidates.Add(ent);
        }

        _entSetPool.Return(ents);

        ClimbDebug(uid, $"VAULT-NEAR scan range={ClimbVaultMaxRange:F2} candidates={candidates.Count}");

        if (!TryClimbNearest(uid, component, climbing, candidates, out var id))
        {
            ClimbDebug(uid, "VAULT-NEAR no-target");
            return false;
        }

        component.DoAfterId = id;
        ClimbDebug(uid, $"VAULT-NEAR started doAfter={id != null}");
        return true;
    }

    /// <summary>
    /// Among climbables, vault the nearest one within <see cref="ClimbVaultMaxRange"/> that lies
    /// toward the current steering target (the one actually blocking the route).
    /// </summary>
    private bool TryClimbNearest(
        EntityUid uid,
        NPCSteeringComponent component,
        ClimbingComponent climbing,
        List<EntityUid> candidates,
        out DoAfterId? doAfterId)
    {
        doAfterId = null;

        if (candidates.Count == 0 || !_xformQuery.TryGetComponent(uid, out var xform))
            return false;

        var ourMap = _transform.GetMapCoordinates(uid, xform: xform);
        var seek = Vector2.Zero;
        if (component.Coordinates.IsValid(EntityManager))
        {
            var targetMap = _transform.ToMapCoordinates(GetTargetCoordinates(component));
            if (targetMap.MapId == ourMap.MapId)
                seek = targetMap.Position - ourMap.Position;
        }

        var seekLenSq = seek.LengthSquared();
        if (seekLenSq > 0.0001f)
            seek /= MathF.Sqrt(seekLenSq);

        EntityUid? best = null;
        ClimbableComponent? bestComp = null;
        var bestDistSq = ClimbVaultMaxRange * ClimbVaultMaxRange;
        var rejectedDir = 0;
        var rejectedVault = 0;
        var rejectedDist = 0;

        var climbableQuery = GetEntityQuery<ClimbableComponent>();

        foreach (var ent in candidates)
        {
            if (!climbableQuery.TryGetComponent(ent, out var table) || !table.Vaultable)
                continue;

            if (!_xformQuery.TryGetComponent(ent, out var entXform))
                continue;

            var entMap = _transform.GetMapCoordinates(ent, xform: entXform);
            if (entMap.MapId != ourMap.MapId)
                continue;

            var delta = entMap.Position - ourMap.Position;
            var distSq = delta.LengthSquared();
            if (distSq > bestDistSq)
            {
                rejectedDist++;
                continue;
            }

            // Adjacent (pressed into the cade): always allowed.
            // Further out: must not be clearly behind us relative to the destination.
            if (distSq > 0.55f * 0.55f && seekLenSq > 0.0001f && distSq > 0.0001f)
            {
                var dir = delta / MathF.Sqrt(distSq);
                if (Vector2.Dot(dir, seek) < -0.15f)
                {
                    rejectedDir++;
                    continue;
                }
            }

            if (!_climb.CanVault(table, uid, uid, out var reason))
            {
                rejectedVault++;
                ClimbDebug(uid, $"CLIMB-PICK reject {ToPrettyString(ent)} dist={MathF.Sqrt(distSq):F2} reason={reason}");
                continue;
            }

            bestDistSq = distSq;
            best = ent;
            bestComp = table;
        }

        if (best == null || bestComp == null)
        {
            ClimbDebug(uid,
                $"CLIMB-PICK none candidates={candidates.Count} rejDist={rejectedDist} rejDir={rejectedDir} rejVault={rejectedVault}");
            return false;
        }

        ClimbDebug(uid, $"CLIMB-PICK best={ToPrettyString(best.Value)} dist={MathF.Sqrt(bestDistSq):F2}");
        var ok = _climb.TryClimb(uid, uid, best.Value, out doAfterId, bestComp, climbing);
        ClimbDebug(uid, $"CLIMB-PICK TryClimb={(ok ? "OK" : "FAIL")} doAfter={doAfterId != null}");
        return ok;
    }

    private void GetObstacleEntities(PathPoly poly, int mask, int layer, List<EntityUid> ents)
    {
        // TODO: Can probably re-use this from pathfinding or something
        if (!TryComp<MapGridComponent>(poly.GraphUid, out var grid))
        {
            return;
        }

        foreach (var ent in _mapSystem.GetLocalAnchoredEntities(poly.GraphUid, grid, poly.Box))
        {
            if (!_physicsQuery.TryGetComponent(ent, out var body) ||
                !body.Hard ||
                !body.CanCollide ||
                (body.CollisionMask & layer) == 0x0 && (body.CollisionLayer & mask) == 0x0)
            {
                continue;
            }

            ents.Add(ent);
        }
    }

    private enum SteeringObstacleStatus : byte
    {
        Completed,
        Failed,
        Continuing
    }
}
