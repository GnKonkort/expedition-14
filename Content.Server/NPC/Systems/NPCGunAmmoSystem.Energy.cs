using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Content.Server.Power.Components;
using Content.Server.PowerCell;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.PowerCell;
using Content.Shared.PowerCell.Components;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;

namespace Content.Server.NPC.Systems;

public sealed partial class NPCGunAmmoSystem
{
    public const string ChargerSlot = "charger_slot";
    /// <summary>Blackboard bool: empty energy gun must reach full charge before ranged use again.</summary>
    public const string EnergyChargeWaitUntilFullKey = "EnergyChargeWaitUntilFull";
    /// <summary>Ammo percent at/above which a charging energy gun may be drawn for fight again.</summary>
    public const float EnergyChargeReadyPercent = 0.95f;
    /// <summary>Below this, self-recharging / external energy guns should be stowed to recharge.</summary>
    public const float EnergyChargeEmptyPercent = 0.001f;
    /// <summary>Minimum charge fraction of MaxCharge for a spare cell to be insertable in combat.</summary>
    public const float MinPowerCellInsertChargeFraction = 0.05f;

    [Dependency] private readonly PowerCellSystem _powerCell = default!;

    private EntityQuery<HitscanBatteryAmmoProviderComponent> _hitscanBatteryQuery;
    private EntityQuery<ProjectileBatteryAmmoProviderComponent> _projectileBatteryQuery;
    private EntityQuery<PowerCellSlotComponent> _powerCellSlotQuery;
    private EntityQuery<PowerCellComponent> _powerCellQuery;
    private EntityQuery<BatteryComponent> _batteryQuery;
    private EntityQuery<BatterySelfRechargerComponent> _selfRechargerQuery;
    private EntityQuery<ChargerComponent> _chargerQuery;

    private void InitializeEnergy()
    {
        _hitscanBatteryQuery = GetEntityQuery<HitscanBatteryAmmoProviderComponent>();
        _projectileBatteryQuery = GetEntityQuery<ProjectileBatteryAmmoProviderComponent>();
        _powerCellSlotQuery = GetEntityQuery<PowerCellSlotComponent>();
        _powerCellQuery = GetEntityQuery<PowerCellComponent>();
        _batteryQuery = GetEntityQuery<BatteryComponent>();
        _selfRechargerQuery = GetEntityQuery<BatterySelfRechargerComponent>();
        _chargerQuery = GetEntityQuery<ChargerComponent>();
    }

    /// <summary>
    /// Hitscan/projectile battery ammo provider — laser/energy guns.
    /// </summary>
    public bool IsEnergyGun(EntityUid gun)
    {
        return _hitscanBatteryQuery.HasComponent(gun) || _projectileBatteryQuery.HasComponent(gun);
    }

    /// <summary>
    /// Swappable power-cell energy gun (treat cells like magazines).
    /// </summary>
    public bool IsPowerCellSwapGun(EntityUid gun)
    {
        return _powerCellSlotQuery.HasComponent(gun);
    }

    /// <summary>
    /// Built-in battery that auto-recharges (no cell slot required).
    /// </summary>
    public bool IsSelfRechargingEnergyGun(EntityUid gun)
    {
        if (!IsEnergyGun(gun) || IsPowerCellSwapGun(gun))
            return false;

        return _selfRechargerQuery.TryGetComponent(gun, out var recharge) && recharge.AutoRecharge;
    }

    /// <summary>
    /// Built-in battery with no self-recharge — needs a portable/station charger, else discard when empty.
    /// </summary>
    public bool IsExternalChargeEnergyGun(EntityUid gun)
    {
        if (!IsEnergyGun(gun) || IsPowerCellSwapGun(gun))
            return false;

        return !_selfRechargerQuery.TryGetComponent(gun, out var recharge) || !recharge.AutoRecharge;
    }

    public bool TryGetPowerCellSlot(EntityUid gun, [NotNullWhen(true)] out ItemSlot? slot)
    {
        slot = null;
        if (!_powerCellSlotQuery.TryGetComponent(gun, out var cellSlot))
            return false;

        return _slots.TryGetSlot(gun, cellSlot.CellSlotId, out slot);
    }

    public bool TryGetSeatedPowerCell(EntityUid gun, out EntityUid cell)
    {
        cell = default;
        if (!TryGetPowerCellSlot(gun, out var slot) || slot.Item is not { } seated)
            return false;

        cell = seated;
        return true;
    }

    public bool IsCompatiblePowerCell(EntityUid gun, EntityUid item)
    {
        if (!IsPowerCellSwapGun(gun) || !_powerCellQuery.HasComponent(item))
            return false;

        if (!TryGetPowerCellSlot(gun, out var slot))
            return false;

        // Size/whitelist enforced by ItemSlots on insert; here only reject obvious mismatches.
        return !_whitelist.IsWhitelistFail(slot.Whitelist, item) &&
               !_whitelist.IsBlacklistPass(slot.Blacklist, item);
    }

    public bool IsSelfRechargingBattery(EntityUid item)
    {
        return _selfRechargerQuery.TryGetComponent(item, out var recharge) && recharge.AutoRecharge;
    }

    public float GetBatteryMaxCharge(EntityUid item)
    {
        return _batteryQuery.TryGetComponent(item, out var battery) ? battery.MaxCharge : 0f;
    }

    public float GetBatteryCurrentCharge(EntityUid item)
    {
        return _batteryQuery.TryGetComponent(item, out var battery) ? battery.CurrentCharge : 0f;
    }

    public float GetGunBatteryFireCost(EntityUid gun)
    {
        if (_hitscanBatteryQuery.TryGetComponent(gun, out var hitscan))
            return MathF.Max(1f, hitscan.FireCost);

        if (_projectileBatteryQuery.TryGetComponent(gun, out var projectile))
            return MathF.Max(1f, projectile.FireCost);

        return 100f;
    }

    /// <summary>
    /// True when the cell has enough charge for at least one shot and is not a trickle-charge thrash candidate.
    /// </summary>
    public bool IsPowerCellChargeEnoughToShoot(EntityUid gun, EntityUid cell)
    {
        if (!_batteryQuery.TryGetComponent(cell, out var battery) || battery.MaxCharge <= 0f)
            return false;

        if (battery.CurrentCharge < GetGunBatteryFireCost(gun))
            return false;

        return battery.CurrentCharge / battery.MaxCharge >= MinPowerCellInsertChargeFraction;
    }

    public string DescribePowerCell(EntityUid cell, EntityUid? gun = null)
    {
        var self = IsSelfRechargingBattery(cell);
        var cur = GetBatteryCurrentCharge(cell);
        var max = GetBatteryMaxCharge(cell);
        var frac = max > 0f ? cur / max : 0f;
        var score = gun is { } g ? ScorePowerCell(cell, g) : ScorePowerCellQuality(cell);
        var enough = gun is { } g2 && IsPowerCellChargeEnoughToShoot(g2, cell);
        return $"{ToPrettyString(cell)} cur={cur:F0}/{max:F0} ({frac:P0}) self={self} score={score} enoughShot={enough}";
    }

    public string DescribeOwnedPowerCells(EntityUid owner, EntityUid gun)
    {
        TryGetSeatedPowerCell(gun, out var seated);
        var parts = new List<string>();
        foreach (var item in EnumerateInventoryAmmoCandidates(owner))
        {
            if (!IsCompatiblePowerCell(gun, item))
                continue;

            var tag = item == seated ? "SEATED" : "bag";
            parts.Add($"[{tag} {DescribePowerCell(item, gun)}]");
        }

        return parts.Count == 0 ? "(no compatible cells)" : string.Join(" ", parts);
    }

    /// <summary>
    /// Capacity / self-recharge ranking ignoring current charge (for peace preference).
    /// Self-recharging always beats disposable; higher MaxCharge wins within a class.
    /// </summary>
    public int ScorePowerCellQuality(EntityUid cell)
    {
        if (!_batteryQuery.TryGetComponent(cell, out var battery) || battery.MaxCharge <= 0f)
            return -1;

        var score = (int)battery.MaxCharge;
        if (IsSelfRechargingBattery(cell))
            score += 1_000_000;

        return score;
    }

    /// <summary>
    /// Insert ranking: requires enough charge to fire. Self-recharge &gt; capacity &gt; leftover charge.
    /// </summary>
    public int ScorePowerCell(EntityUid cell, EntityUid gun)
    {
        if (!_batteryQuery.TryGetComponent(cell, out var battery) || battery.MaxCharge <= 0f)
            return -1;

        if (!IsPowerCellChargeEnoughToShoot(gun, cell))
            return -1;

        var score = (int)battery.MaxCharge + (int)(battery.CurrentCharge / 100f);
        if (IsSelfRechargingBattery(cell))
            score += 1_000_000;

        return score;
    }

    /// <summary>Quality-only score (pickup / peace). Prefer ScorePowerCell(cell, gun) for insert.</summary>
    public int ScorePowerCell(EntityUid cell) => ScorePowerCellQuality(cell);

    /// <summary>
    /// Compatible power cells on the body (seated + inventory), including empty.
    /// </summary>
    public int CountCompatiblePowerCells(EntityUid owner, EntityUid gun)
    {
        if (!IsPowerCellSwapGun(gun))
            return 0;

        var count = 0;
        foreach (var item in EnumerateInventoryAmmoCandidates(owner))
        {
            if (!IsCompatiblePowerCell(gun, item))
                continue;

            // Drained disposable cells are trash — do not count toward stock goal.
            if (IsDrainedDisposablePowerCell(gun, item))
                continue;

            count++;
        }

        return count;
    }

    /// <summary>
    /// Lowest <see cref="ScorePowerCellQuality"/> among owned useful compatible cells (gun + inventory). -1 if none.
    /// </summary>
    public int GetWorstOwnedPowerCellQuality(EntityUid owner, EntityUid gun)
    {
        if (!IsPowerCellSwapGun(gun))
            return -1;

        var worst = int.MaxValue;
        var found = false;

        foreach (var item in EnumerateInventoryAmmoCandidates(owner))
        {
            if (!IsCompatiblePowerCell(gun, item) || IsDrainedDisposablePowerCell(gun, item))
                continue;

            var quality = ScorePowerCellQuality(item);
            if (quality < 0)
                continue;

            found = true;
            if (quality < worst)
                worst = quality;
        }

        return found ? worst : -1;
    }

    /// <summary>
    /// Keep the best <see cref="MinCompatiblePowerCells"/> by quality; discard the rest.
    /// If the seated cell is among the losers, seat a kept spare first so the loser can be dropped.
    /// </summary>
    public bool TryTrimExcessPowerCells(EntityUid owner, EntityUid gun)
    {
        if (!IsPowerCellSwapGun(gun))
            return false;

        var changed = false;

        while (true)
        {
            var cells = new List<(EntityUid Uid, int Quality)>();
            foreach (var item in EnumerateInventoryAmmoCandidates(owner))
            {
                if (!IsCompatiblePowerCell(gun, item))
                    continue;

                var quality = ScorePowerCellQuality(item);
                if (quality < 0)
                    continue;

                cells.Add((item, quality));
            }

            if (cells.Count <= MinCompatiblePowerCells)
                break;

            // Worst first among extras to discard.
            cells.Sort((a, b) => a.Quality.CompareTo(b.Quality));

            var worst = cells[0];
            TryGetSeatedPowerCell(gun, out var seated);

            // Seated is the worst — move a better kept cell into the gun so we can drop it.
            if (seated != default && worst.Uid == seated)
            {
                EntityUid? replacement = null;
                var bestKeepQuality = int.MinValue;
                for (var i = cells.Count - 1; i >= cells.Count - MinCompatiblePowerCells && i >= 0; i--)
                {
                    var candidate = cells[i];
                    if (candidate.Uid == seated)
                        continue;

                    if (candidate.Quality > bestKeepQuality)
                    {
                        bestKeepQuality = candidate.Quality;
                        replacement = candidate.Uid;
                    }
                }

                if (replacement == null || !TryInsertPowerCell(owner, gun, replacement.Value))
                {
                    DebugAmmo(owner,
                        $"TryTrimExcessPowerCells FAIL cannot unseat worst seated={DescribePowerCell(seated, gun)}",
                        force: true);
                    return false;
                }

                DebugAmmo(owner,
                    $"TryTrimExcessPowerCells unseated worst → bag, seated now {DescribePowerCell(replacement.Value, gun)}",
                    force: true);
                changed = true;
                // Old seated is now in the bag; discard it on the next loop iteration.
                continue;
            }

            if (!TryDiscardPowerCell(owner, worst.Uid))
            {
                DebugAmmo(owner, $"TryTrimExcessPowerCells FAIL discard {ToPrettyString(worst.Uid)}", force: true);
                return false;
            }

            DebugAmmo(owner,
                $"TryTrimExcessPowerCells discarded worst quality={worst.Quality} {DescribePowerCell(worst.Uid, gun)}",
                force: true);
            changed = true;
        }

        return changed || CountCompatiblePowerCells(owner, gun) <= MinCompatiblePowerCells;
    }

    public bool TryFindBestCompatiblePowerCell(EntityUid owner, EntityUid gun, out EntityUid cell, out int score)
    {
        cell = default;
        score = int.MinValue;
        if (!IsPowerCellSwapGun(gun))
            return false;

        EntityUid? best = null;
        var bestScore = int.MinValue;
        var rejected = 0;

        foreach (var item in EnumerateInventoryAmmoCandidates(owner))
        {
            if (TryGetSeatedPowerCell(gun, out var seated) && item == seated)
                continue;

            if (!IsCompatiblePowerCell(gun, item))
                continue;

            var s = ScorePowerCell(item, gun);
            if (s <= 0)
            {
                rejected++;
                continue;
            }

            if (s <= bestScore)
                continue;

            bestScore = s;
            best = item;
        }

        if (best == null)
        {
            DebugAmmo(owner,
                $"TryFindBestCompatiblePowerCell=none rejectedLowCharge={rejected} fireCost={GetGunBatteryFireCost(gun):F0} cells={DescribeOwnedPowerCells(owner, gun)}",
                force: true);
            return false;
        }

        cell = best.Value;
        score = bestScore;
        DebugAmmo(owner,
            $"TryFindBestCompatiblePowerCell={DescribePowerCell(cell, gun)} (rejectedLowCharge={rejected})",
            force: true);
        return true;
    }

    /// <summary>
    /// Missing or discharged seated cell, and a charged spare exists in inventory.
    /// Does not upgrade mid-fight — only swaps when the gun cannot shoot.
    /// </summary>
    public bool NeedsPowerCellInsert(EntityUid owner, EntityUid gun)
    {
        if (!IsPowerCellSwapGun(gun))
            return false;

        TryGetAmmoPercent(gun, out var percent);
        var hasSeated = TryGetSeatedPowerCell(gun, out var seated);
        var empty = IsEnergyGunEmpty(gun);

        // Only swap when empty / missing — never thrash better cells mid-combat.
        if (hasSeated && !empty)
        {
            DebugAmmo(owner,
                $"NeedsPowerCellInsert=false (seated still has charge pct={percent:F3}) seated={DescribePowerCell(seated, gun)}",
                force: true);
            return false;
        }

        if (!TryFindBestCompatiblePowerCell(owner, gun, out var spare, out var spareScore))
        {
            DebugAmmo(owner,
                $"NeedsPowerCellInsert=false (no usable spare) empty={empty} pct={percent:F3} hasSeated={hasSeated} cells={DescribeOwnedPowerCells(owner, gun)}",
                force: true);
            return false;
        }

        // Don't thrash between near-empty cells: spare must beat seated by >= one shot cost.
        if (hasSeated)
        {
            var seatedCharge = GetBatteryCurrentCharge(seated);
            var spareCharge = GetBatteryCurrentCharge(spare);
            var fireCost = GetGunBatteryFireCost(gun);
            if (spareCharge < seatedCharge + fireCost)
            {
                DebugAmmo(owner,
                    $"NeedsPowerCellInsert=false (spare not meaningfully better) seatedCharge={seatedCharge:F0} spareCharge={spareCharge:F0} fireCost={fireCost:F0} seated={DescribePowerCell(seated, gun)} spare={DescribePowerCell(spare, gun)}",
                    force: true);
                return false;
            }
        }

        DebugAmmo(owner,
            $"NeedsPowerCellInsert=true empty={empty} pct={percent:F3} spareScore={spareScore} spare={DescribePowerCell(spare, gun)} cells={DescribeOwnedPowerCells(owner, gun)}",
            force: true);
        return true;
    }

    public bool TryInsertPowerCell(EntityUid owner, EntityUid gun, EntityUid cell)
    {
        DebugAmmo(owner,
            $"TryInsertPowerCell BEGIN cell={DescribePowerCell(cell, gun)} gunEmpty={IsEnergyGunEmpty(gun)} cells={DescribeOwnedPowerCells(owner, gun)}",
            force: true);

        if (!IsCompatiblePowerCell(gun, cell) || !TryGetPowerCellSlot(gun, out var slot))
        {
            DebugAmmo(owner, $"TryInsertPowerCell FAIL incompatible/no slot cell={ToPrettyString(cell)}", force: true);
            return false;
        }

        if (!_powerCellSlotQuery.TryGetComponent(gun, out var cellSlot))
            return false;

        EntityUid? previous = null;
        if (slot.HasItem)
        {
            if (!_slots.TryEject(gun, slot, user: null, out previous) || previous == null)
            {
                DebugAmmo(owner, $"TryInsertPowerCell FAIL eject from {ToPrettyString(gun)}", force: true);
                return false;
            }

            DebugAmmo(owner, $"TryInsertPowerCell ejected previous={DescribePowerCell(previous.Value, gun)}", force: true);
        }

        var inserted = _slots.TryInsert(gun, cellSlot.CellSlotId, cell, owner);
        if (!inserted)
        {
            DebugAmmo(owner, "TryInsertPowerCell direct insert failed, trying obtain-in-hand", force: true);
            if (TryObtainInHand(owner, cell))
                inserted = _slots.TryInsert(gun, cellSlot.CellSlotId, cell, owner);
        }

        if (!inserted)
        {
            if (previous != null)
                _slots.TryInsert(gun, cellSlot.CellSlotId, previous.Value, owner);

            DebugAmmo(owner, $"TryInsertPowerCell FAIL insert {ToPrettyString(cell)}", force: true);
            return false;
        }

        if (previous != null && Exists(previous.Value))
        {
            var stashed = TryStashItemOnBody(owner, previous.Value);
            DebugAmmo(owner, $"TryInsertPowerCell stash previous => {stashed} {DescribePowerCell(previous.Value, gun)}", force: true);
        }

        TryGetAmmoPercent(gun, out var pctAfter);
        DebugAmmo(owner,
            $"TryInsertPowerCell SUCCESS {DescribePowerCell(cell, gun)} -> {ToPrettyString(gun)} pctAfter={pctAfter:F3} emptyAfter={IsEnergyGunEmpty(gun)} cells={DescribeOwnedPowerCells(owner, gun)}",
            force: true);
        return true;
    }

    /// <summary>
    /// Out of combat: restore the best self-recharging cell into the gun and discard disposable cells.
    /// </summary>
    public bool NeedsPowerCellPeaceOptimize(EntityUid owner, EntityUid gun)
    {
        if (!IsPowerCellSwapGun(gun))
            return false;

        EntityUid? bestSelf = null;
        var bestSelfScore = int.MinValue;
        var bestSelfCharge = -1f;
        var hasSelf = false;
        var hasNormal = false;

        foreach (var item in EnumerateInventoryAmmoCandidates(owner))
        {
            if (!IsCompatiblePowerCell(gun, item))
                continue;

            if (IsSelfRechargingBattery(item))
            {
                hasSelf = true;
                var quality = ScorePowerCellQuality(item);
                var charge = GetBatteryCurrentCharge(item);
                if (quality > bestSelfScore || (quality == bestSelfScore && charge > bestSelfCharge))
                {
                    bestSelf = item;
                    bestSelfScore = quality;
                    bestSelfCharge = charge;
                }
            }
            else
            {
                hasNormal = true;
            }
        }

        if (!hasSelf)
            return false;

        if (hasNormal)
        {
            DebugAmmo(owner, $"NeedsPowerCellPeaceOptimize=true (discard normals) cells={DescribeOwnedPowerCells(owner, gun)}", force: true);
            return true;
        }

        if (bestSelf == null)
            return false;

        if (!TryGetSeatedPowerCell(gun, out var seated) || seated != bestSelf.Value)
        {
            DebugAmmo(owner,
                $"NeedsPowerCellPeaceOptimize=true (restore self) best={ToPrettyString(bestSelf.Value)} seated={(TryGetSeatedPowerCell(gun, out var s) ? ToPrettyString(s) : "null")}",
                force: true);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Prefer the best self-recharging cell in the gun; drop disposable cells when a self-recharger exists.
    /// </summary>
    public bool TryPeaceOptimizePowerCells(EntityUid owner, EntityUid gun)
    {
        if (!IsPowerCellSwapGun(gun))
            return false;

        DebugAmmo(owner, $"TryPeaceOptimizePowerCells BEGIN cells={DescribeOwnedPowerCells(owner, gun)}", force: true);

        EntityUid? bestSelf = null;
        var bestSelfScore = int.MinValue;
        var bestSelfCharge = -1f;
        var normals = new List<EntityUid>();

        foreach (var item in EnumerateInventoryAmmoCandidates(owner))
        {
            if (!IsCompatiblePowerCell(gun, item))
                continue;

            if (IsSelfRechargingBattery(item))
            {
                var quality = ScorePowerCellQuality(item);
                var charge = GetBatteryCurrentCharge(item);
                if (quality > bestSelfScore || (quality == bestSelfScore && charge > bestSelfCharge))
                {
                    bestSelf = item;
                    bestSelfScore = quality;
                    bestSelfCharge = charge;
                }
            }
            else
            {
                normals.Add(item);
            }
        }

        var changed = false;

        if (bestSelf != null)
        {
            if (!TryGetSeatedPowerCell(gun, out var seated) || seated != bestSelf.Value)
            {
                if (TryInsertPowerCell(owner, gun, bestSelf.Value))
                {
                    DebugAmmo(owner, $"TryPeaceOptimizePowerCells restored self-recharge {ToPrettyString(bestSelf.Value)}", force: true);
                    changed = true;
                }
            }

            foreach (var normal in normals)
            {
                if (!Exists(normal))
                    continue;

                if (TryGetSeatedPowerCell(gun, out var nowSeated) && nowSeated == normal)
                    continue;

                if (TryDiscardPowerCell(owner, normal))
                {
                    DebugAmmo(owner, $"TryPeaceOptimizePowerCells discarded disposable {ToPrettyString(normal)}", force: true);
                    changed = true;
                }
            }
        }

        DebugAmmo(owner, $"TryPeaceOptimizePowerCells END changed={changed} cells={DescribeOwnedPowerCells(owner, gun)}", force: true);
        return changed;
    }

    private bool TryDiscardPowerCell(EntityUid owner, EntityUid cell)
    {
        if (!TryObtainInHand(owner, cell))
        {
            DebugAmmo(owner, $"TryDiscardPowerCell FAIL obtain {ToPrettyString(cell)}", force: true);
            return false;
        }

        var ok = _hands.TryDrop(owner, cell, checkActionBlocker: false);
        DebugAmmo(owner, $"TryDiscardPowerCell {ToPrettyString(cell)} => {ok}", force: true);
        return ok;
    }

    /// <summary>
    /// Disposable (non-self-recharging) cell with too little charge to fire — dead weight.
    /// </summary>
    public bool IsDrainedDisposablePowerCell(EntityUid gun, EntityUid cell)
    {
        if (!IsCompatiblePowerCell(gun, cell) || IsSelfRechargingBattery(cell))
            return false;

        return !IsPowerCellChargeEnoughToShoot(gun, cell);
    }

    public bool TryFindDrainedDisposablePowerCell(EntityUid owner, EntityUid gun, out EntityUid cell)
    {
        cell = default;
        if (!IsPowerCellSwapGun(gun))
            return false;

        foreach (var item in EnumerateInventoryAmmoCandidates(owner))
        {
            if (!IsDrainedDisposablePowerCell(gun, item))
                continue;

            cell = item;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Drops one drained disposable power cell. Ejects from the gun first if it is seated.
    /// </summary>
    public bool TryDiscardDrainedDisposablePowerCell(EntityUid owner, EntityUid gun)
    {
        if (!TryFindDrainedDisposablePowerCell(owner, gun, out var cell))
            return false;

        if (TryGetSeatedPowerCell(gun, out var seated) && seated == cell)
        {
            if (!TryGetPowerCellSlot(gun, out var slot) ||
                !_slots.TryEject(gun, slot, user: null, out var ejected) ||
                ejected == null)
            {
                DebugAmmo(owner, $"TryDiscardDrainedDisposablePowerCell FAIL eject seated {ToPrettyString(cell)}", force: true);
                return false;
            }

            cell = ejected.Value;
        }

        var ok = TryDiscardPowerCell(owner, cell);
        DebugAmmo(owner, $"TryDiscardDrainedDisposablePowerCell {DescribePowerCell(cell, gun)} => {ok}", force: true);
        return ok;
    }

    public bool TryFindPortableRecharger(EntityUid owner, out EntityUid recharger)
    {
        recharger = default;
        foreach (var item in EnumerateInventoryAmmoCandidates(owner))
        {
            if (!_chargerQuery.TryGetComponent(item, out var charger) || !charger.Portable)
                continue;

            recharger = item;
            return true;
        }

        return false;
    }

    public bool TryGetPortableRechargerSlot(EntityUid recharger, [NotNullWhen(true)] out ItemSlot? slot)
    {
        slot = null;
        if (!_chargerQuery.TryGetComponent(recharger, out var charger) || !charger.Portable)
            return false;

        return _slots.TryGetSlot(recharger, charger.SlotId, out slot);
    }

    /// <summary>
    /// True when this gun is currently docked in a portable recharger on <paramref name="owner"/>.
    /// </summary>
    public bool IsGunInPortableRecharger(EntityUid owner, EntityUid gun, out EntityUid recharger)
    {
        recharger = default;
        if (!_containers.TryGetContainingContainer((gun, null, null), out var container))
            return false;

        if (!_chargerQuery.TryGetComponent(container.Owner, out var charger) || !charger.Portable)
            return false;

        if (container.ID != charger.SlotId)
            return false;

        if (!IsCarriedBy(owner, container.Owner))
            return false;

        recharger = container.Owner;
        return true;
    }

    public bool TryInsertGunIntoPortableRecharger(EntityUid owner, EntityUid gun, EntityUid? preferred = null)
    {
        if (!IsEnergyGun(gun))
            return false;

        if (IsGunInPortableRecharger(owner, gun, out _))
            return true;

        EntityUid recharger;
        if (preferred is { } pref && Exists(pref) && IsCarriedBy(owner, pref))
            recharger = pref;
        else if (!TryFindPortableRecharger(owner, out recharger))
            return false;

        if (!TryGetPortableRechargerSlot(recharger, out var slot))
            return false;

        if (!_chargerQuery.TryGetComponent(recharger, out var charger))
            return false;

        // Occupied by something else — don't steal another gun mid-charge.
        if (slot.HasItem && slot.Item != gun)
            return false;

        TryUnwieldIfNeeded(owner, gun);

        if (_slots.TryInsert(recharger, charger.SlotId, gun, owner))
        {
            DebugAmmo(owner, $"TryInsertGunIntoPortableRecharger SUCCESS {ToPrettyString(gun)} → {ToPrettyString(recharger)}");
            return true;
        }

        // ItemSlots may require the gun in-hand.
        if (!TryObtainInHand(owner, gun))
            return false;

        TryUnwieldIfNeeded(owner, gun);
        var ok = _slots.TryInsert(recharger, charger.SlotId, gun, owner);
        DebugAmmo(owner, $"TryInsertGunIntoPortableRecharger obtain-insert => {ok}");
        return ok;
    }

    public string DescribeEnergyGunState(EntityUid owner, EntityUid gun, NPCBlackboard? blackboard = null)
    {
        TryGetAmmoPercent(gun, out var percent);
        var ev = new GetAmmoCountEvent();
        RaiseLocalEvent(gun, ref ev);

        var mode =
            IsPowerCellSwapGun(gun) ? "PowerCellSwap" :
            IsSelfRechargingEnergyGun(gun) ? "SelfRecharge" :
            IsExternalChargeEnergyGun(gun) ? "ExternalCharge" :
            IsEnergyGun(gun) ? "EnergyOther" : "NotEnergy";

        var hasRecharger = TryFindPortableRecharger(owner, out var recharger);
        var inCharger = IsGunInPortableRecharger(owner, gun, out var dock);
        var waitingFlag = blackboard != null &&
                          blackboard.TryGetValue<bool>(EnergyChargeWaitUntilFullKey, out var w, EntityManager) &&
                          w;

        var selfRate = "?";
        if (_selfRechargerQuery.TryGetComponent(gun, out var self))
            selfRate = $"auto={self.AutoRecharge} rate={self.AutoRechargeRate} pause={self.AutoRechargePause} next={self.NextAutoRecharge}";

        var bat = _batteryQuery.TryGetComponent(gun, out var battery)
            ? $"bat={battery.CurrentCharge:F0}/{battery.MaxCharge:F0}"
            : "bat=null";

        var eta = "eta=?";
        if (_batteryQuery.TryGetComponent(gun, out var batComp) &&
            _selfRechargerQuery.TryGetComponent(gun, out var selfComp) &&
            selfComp.AutoRecharge &&
            selfComp.AutoRechargeRate > 0f)
        {
            var need = MathF.Max(0f, batComp.MaxCharge * EnergyChargeReadyPercent - batComp.CurrentCharge);
            eta = $"etaToReady={need / selfComp.AutoRechargeRate:F0}s (ready>={EnergyChargeReadyPercent:P0})";
        }

        return
            $"energy mode={mode} shots={ev.Count}/{ev.Capacity} pct={percent:F3} {bat} {selfRate} {eta} " +
            $"hasPortableRecharger={hasRecharger}{(hasRecharger ? $"({ToPrettyString(recharger)})" : "")} " +
            $"inCharger={inCharger}{(inCharger ? $"({ToPrettyString(dock)})" : "")} " +
            $"waitUntilFullFlag={waitingFlag} held={_hands.IsHolding(owner, gun)}" +
            (IsPowerCellSwapGun(gun) ? $" cells={DescribeOwnedPowerCells(owner, gun)}" : "");
    }

    /// <summary>
    /// True when the energy gun has recharged enough to clear the charge-wait flag and be used again.
    /// </summary>
    public bool IsEnergyGunFullyCharged(EntityUid gun)
    {
        return TryGetAmmoPercent(gun, out var percent) && percent >= EnergyChargeReadyPercent;
    }

    public bool IsEnergyGunEmpty(EntityUid gun)
    {
        return !TryGetAmmoPercent(gun, out var percent) || percent <= EnergyChargeEmptyPercent;
    }

    private bool HasEnergyChargeWaitFlag(NPCBlackboard? blackboard)
    {
        return blackboard != null &&
               blackboard.TryGetValue<bool>(EnergyChargeWaitUntilFullKey, out var flag, EntityManager) &&
               flag;
    }

    /// <summary>
    /// True while an energy gun must not be used for ranged combat (empty / mid-recharge until full).
    /// The gun should already be stowed or docked — this does <em>not</em> idle the NPC; melee/other HTN can run.
    /// </summary>
    public bool NeedsEnergyChargeWait(EntityUid owner, EntityUid gun, NPCBlackboard? blackboard = null)
    {
        if (!IsEnergyGun(gun) || IsPowerCellSwapGun(gun))
            return false;

        if (IsEnergyGunFullyCharged(gun))
        {
            ClearEnergyChargeWait(blackboard);
            return false;
        }

        // Docked in portable recharger until full.
        if (IsGunInPortableRecharger(owner, gun, out _))
            return true;

        if (IsSelfRechargingEnergyGun(gun))
        {
            if (IsEnergyGunEmpty(gun))
                return true;

            return HasEnergyChargeWaitFlag(blackboard);
        }

        if (IsExternalChargeEnergyGun(gun) && TryFindPortableRecharger(owner, out _))
        {
            if (IsEnergyGunEmpty(gun))
                return true;

            return HasEnergyChargeWaitFlag(blackboard);
        }

        return false;
    }

    /// <summary>
    /// True when the NPC must actively holster/dock the energy gun this tick.
    /// Only when the gun is empty (or needs docking) — never on partial charge after a fight started.
    /// Once stowed, returns false so HTN can fall through to melee / idle while the battery fills.
    /// </summary>
    public bool NeedsStowEnergyGunForCharge(EntityUid owner, EntityUid gun, NPCBlackboard? blackboard = null)
    {
        if (!IsEnergyGun(gun) || IsPowerCellSwapGun(gun))
            return false;

        if (IsEnergyGunFullyCharged(gun))
        {
            ClearEnergyChargeWait(blackboard);
            return false;
        }

        // Already charging in the portable recharger — nothing to stow.
        if (IsGunInPortableRecharger(owner, gun, out _))
            return false;

        // Stow only when empty — partial charge must not re-holster mid-magazine.
        if (!IsEnergyGunEmpty(gun))
            return false;

        var canChargeOnBody =
            IsSelfRechargingEnergyGun(gun) ||
            (IsExternalChargeEnergyGun(gun) && TryFindPortableRecharger(owner, out _));

        if (!canChargeOnBody)
            return false;

        // Held → put away. Stowed external empty → try dock into portable recharger.
        if (_hands.IsHolding(owner, gun))
            return true;

        return IsExternalChargeEnergyGun(gun) && TryFindPortableRecharger(owner, out _);
    }

    /// <summary>
    /// Call from HTN Update (writable blackboard) when the NPC resumes using a fully charged energy gun.
    /// Plan-time Clear is a no-op on read-only blackboards, which left the wait flag stuck and caused
    /// re-holster after every shot once percent dropped below <see cref="EnergyChargeReadyPercent"/>.
    /// </summary>
    public void TryClearEnergyChargeWaitIfReady(EntityUid gun, NPCBlackboard? blackboard)
    {
        if (blackboard == null || blackboard.ReadOnly)
            return;

        if (!IsEnergyGun(gun) || IsPowerCellSwapGun(gun))
            return;

        if (IsEnergyGunFullyCharged(gun))
            ClearEnergyChargeWait(blackboard);
    }

    public void NoteEnergyChargeWait(NPCBlackboard? blackboard)
    {
        if (blackboard == null || blackboard.ReadOnly)
            return;

        blackboard.SetValue(EnergyChargeWaitUntilFullKey, true);
    }

    public void ClearEnergyChargeWait(NPCBlackboard? blackboard)
    {
        if (blackboard == null || blackboard.ReadOnly)
            return;

        blackboard.Remove<bool>(EnergyChargeWaitUntilFullKey);
    }

    /// <summary>
    /// Empty external-charge energy gun with no portable recharger on the body — discard.
    /// </summary>
    public bool ShouldDiscardEnergyGun(EntityUid owner, EntityUid gun)
    {
        if (!IsExternalChargeEnergyGun(gun))
        {
            DebugAmmo(owner, $"ShouldDiscardEnergyGun=false (not external) ({DescribeEnergyGunState(owner, gun)})", force: true);
            return false;
        }

        if (TryFindPortableRecharger(owner, out var recharger))
        {
            DebugAmmo(owner, $"ShouldDiscardEnergyGun=false (has {ToPrettyString(recharger)})", force: true);
            return false;
        }

        var discard = IsEnergyGunEmpty(gun);
        DebugAmmo(owner, $"ShouldDiscardEnergyGun={discard} ({DescribeEnergyGunState(owner, gun)})", force: true);
        return discard;
    }

    /// <summary>
    /// Dock into portable recharger when possible; otherwise holster (self-recharge only).
    /// </summary>
    public bool TryStowEnergyGunForCharge(EntityUid owner, EntityUid gun, NPCBlackboard? blackboard = null)
    {
        RememberOwnedGun(blackboard, gun);
        NoteEnergyChargeWait(blackboard);
        DebugAmmo(owner, $"TryStowEnergyGunForCharge begin ({DescribeEnergyGunState(owner, gun, blackboard)})", force: true);

        if (IsGunInPortableRecharger(owner, gun, out var docked))
        {
            DebugAmmo(owner, $"TryStowEnergyGunForCharge already docked in {ToPrettyString(docked)}", force: true);
            return true;
        }

        if (TryInsertGunIntoPortableRecharger(owner, gun))
        {
            DebugAmmo(owner, "TryStowEnergyGunForCharge SUCCESS docked into portable recharger", force: true);
            return true;
        }

        // External-only without a free charger slot cannot wait usefully.
        if (IsExternalChargeEnergyGun(gun))
        {
            DebugAmmo(owner, "TryStowEnergyGunForCharge FAIL external gun, no free charger slot", force: true);
            return false;
        }

        var stowed = TryStowGunToStorage(owner, gun) || TryStowGun(owner, gun);
        DebugAmmo(owner, $"TryStowEnergyGunForCharge holster fallback => {stowed}", force: true);
        return stowed;
    }

    /// <summary>
    /// Prefer docking energy guns into the portable recharger; fall back to normal holster slots.
    /// </summary>
    public bool TryStowGunPreferringRecharger(EntityUid owner, EntityUid gun)
    {
        if (IsEnergyGun(gun) && !IsPowerCellSwapGun(gun) && TryInsertGunIntoPortableRecharger(owner, gun))
        {
            DebugAmmo(owner, $"TryStowGunPreferringRecharger docked {ToPrettyString(gun)}", force: true);
            return true;
        }

        // External-only must live in the recharger when present.
        if (IsExternalChargeEnergyGun(gun) && TryFindPortableRecharger(owner, out _))
        {
            var ok = TryInsertGunIntoPortableRecharger(owner, gun);
            DebugAmmo(owner, $"TryStowGunPreferringRecharger external must-dock => {ok}", force: true);
            return ok;
        }

        return TryStowGunToStorage(owner, gun) || TryStowGun(owner, gun);
    }
}
