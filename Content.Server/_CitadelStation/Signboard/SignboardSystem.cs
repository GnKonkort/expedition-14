using System.Linq;
using Content.Shared._CitadelStation.Signboard;
using Content.Shared.DeviceLinking.Events;
using Content.Shared.Interaction;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._CitadelStation.Signboard;

public sealed class SignboardSystem : SharedSignboardSystem
{
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;

    private EntityQuery<SignboardComponent> _signQuery;
    private EntityQuery<TransformComponent> _xformQuery;

    public override void Initialize()
    {
        base.Initialize();
        _signQuery = GetEntityQuery<SignboardComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();

        SubscribeLocalEvent<SignboardComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<SignboardComponent, AnchorStateChangedEvent>(OnAnchorChanged);
        SubscribeLocalEvent<SignboardComponent, MoveEvent>(OnMove);
        SubscribeLocalEvent<SignboardComponent, SignalReceivedEvent>(OnSignal);
        SubscribeLocalEvent<SignboardComponent, ActivateInWorldEvent>(OnActivate);

        Subs.BuiEvents<SignboardComponent>(SignboardUiKey.Key, subs =>
        {
            subs.Event<BoundUIOpenedEvent>(OnUiOpened);
            subs.Event<SignboardUpdateFramesMessage>(OnUpdateFrames);
            subs.Event<SignboardSetSizeMessage>(OnSetSize);
            subs.Event<SignboardSetDelayMessage>(OnSetDelay);
            subs.Event<SignboardSetNeonMessage>(OnSetNeon);
            subs.Event<SignboardSetPlayingMessage>(OnSetPlaying);
            subs.Event<SignboardSetFrameStyleMessage>(OnSetFrameStyle);
            subs.Event<SignboardSetPoweredMessage>(OnSetPowered);
        });
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<SignboardComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.Master != null)
                continue;

            if (!comp.Powered || !comp.Playing || comp.Frames.Count <= 1)
                continue;

            if (Timing.CurTime < comp.NextFrameAt)
                continue;

            AdvanceFrame((uid, comp));
            ScheduleNextFrame(comp);
            Dirty(uid, comp);
            // Do not push full BUI frame payloads every tick — that resets the editor.
            DirtySlaves(uid, comp);
        }
    }

    private void OnStartup(EntityUid uid, SignboardComponent component, ComponentStartup args)
    {
        ScheduleNextFrame(component);
        RebuildChainsAround(uid);
    }

    private void OnAnchorChanged(EntityUid uid, SignboardComponent component, ref AnchorStateChangedEvent args)
    {
        RebuildChainsAround(uid);
    }

    private void OnMove(EntityUid uid, SignboardComponent component, ref MoveEvent args)
    {
        if (args.NewPosition.EntityId == args.OldPosition.EntityId
            && (args.NewPosition.Position - args.OldPosition.Position).LengthSquared() < 0.01f)
            return;

        RebuildChainsAround(uid);
    }

    private void OnActivate(EntityUid uid, SignboardComponent component, ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        var master = ResolveMaster((uid, component));
        if (master == null)
            return;

        if (!_ui.HasUi(master.Value.Owner, SignboardUiKey.Key))
            return;

        _ui.OpenUi(master.Value.Owner, SignboardUiKey.Key, args.User);
        UpdateUiState(master.Value.Owner, master.Value.Comp);
        args.Handled = true;
    }

    private void OnSignal(EntityUid uid, SignboardComponent component, ref SignalReceivedEvent args)
    {
        var master = ResolveMaster((uid, component));
        if (master == null)
            return;

        var ent = master.Value;
        if (args.Port == ent.Comp.OnPort)
        {
            SetPowered(ent, true);
        }
        else if (args.Port == ent.Comp.OffPort)
        {
            SetPowered(ent, false);
        }
        else if (args.Port == ent.Comp.NextFramePort)
        {
            AdvanceFrame(ent);
            ScheduleNextFrame(ent.Comp);
            Dirty(ent);
            DirtySlaves(ent.Owner, ent.Comp);
        }
    }

    private void OnUiOpened(Entity<SignboardComponent> ent, ref BoundUIOpenedEvent args)
    {
        var master = ResolveMaster(ent) ?? ent;
        UpdateUiState(master.Owner, master.Comp);
    }

    private void OnUpdateFrames(Entity<SignboardComponent> ent, ref SignboardUpdateFramesMessage args)
    {
        var master = ResolveMaster(ent);
        if (master == null || master.Value.Owner != ent.Owner)
            return;

        if (!ValidateFrames(master.Value.Comp.Size, master.Value.Comp.GridWidth, master.Value.Comp.GridHeight, args.Frames, out _))
            return;

        master.Value.Comp.Frames = CloneFrames(args.Frames);
        master.Value.Comp.ActiveEditFrame = Math.Clamp(args.ActiveEditFrame, 0, master.Value.Comp.Frames.Count - 1);
        master.Value.Comp.PlaybackFrame = Math.Clamp(master.Value.Comp.PlaybackFrame, 0, master.Value.Comp.Frames.Count - 1);
        Dirty(master.Value);
        UpdateUiState(master.Value.Owner, master.Value.Comp);
        DirtySlaves(master.Value.Owner, master.Value.Comp);
    }

    private void OnSetSize(Entity<SignboardComponent> ent, ref SignboardSetSizeMessage args)
    {
        var master = ResolveMaster(ent);
        if (master == null || master.Value.Owner != ent.Owner)
            return;

        if (!TryResizeFrames(master.Value.Comp, args.Size, master.Value.Comp.GridWidth, master.Value.Comp.GridHeight))
            return;

        Dirty(master.Value);
        UpdateUiState(master.Value.Owner, master.Value.Comp);
        DirtySlaves(master.Value.Owner, master.Value.Comp);
    }

    private void OnSetDelay(Entity<SignboardComponent> ent, ref SignboardSetDelayMessage args)
    {
        var master = ResolveMaster(ent);
        if (master == null || master.Value.Owner != ent.Owner)
            return;

        master.Value.Comp.FrameDelay = Math.Clamp(args.Delay, 0.05f, 10f);
        ScheduleNextFrame(master.Value.Comp);
        Dirty(master.Value);
        // Skip BUI echo — client already shows the value; round-trips make the slider stutter.
    }

    private void OnSetNeon(Entity<SignboardComponent> ent, ref SignboardSetNeonMessage args)
    {
        var master = ResolveMaster(ent);
        if (master == null || master.Value.Owner != ent.Owner)
            return;

        master.Value.Comp.Neon = args.Neon;
        Dirty(master.Value);
        UpdateUiState(master.Value.Owner, master.Value.Comp);
        DirtySlaves(master.Value.Owner, master.Value.Comp);
    }

    private void OnSetPlaying(Entity<SignboardComponent> ent, ref SignboardSetPlayingMessage args)
    {
        var master = ResolveMaster(ent);
        if (master == null || master.Value.Owner != ent.Owner)
            return;

        master.Value.Comp.Playing = args.Playing;
        ScheduleNextFrame(master.Value.Comp);
        Dirty(master.Value);
        UpdateUiState(master.Value.Owner, master.Value.Comp);
        DirtySlaves(master.Value.Owner, master.Value.Comp);
    }

    private void OnSetFrameStyle(Entity<SignboardComponent> ent, ref SignboardSetFrameStyleMessage args)
    {
        var master = ResolveMaster(ent);
        if (master == null || master.Value.Owner != ent.Owner)
            return;

        if (args.FrameStyleId == null)
            master.Value.Comp.FrameStyleId = null;
        else if (TryGetFrameStyle(args.FrameStyleId, out _))
            master.Value.Comp.FrameStyleId = args.FrameStyleId;
        else
            return;

        Dirty(master.Value);
        UpdateUiState(master.Value.Owner, master.Value.Comp);
        DirtySlaves(master.Value.Owner, master.Value.Comp);
    }

    private void OnSetPowered(Entity<SignboardComponent> ent, ref SignboardSetPoweredMessage args)
    {
        var master = ResolveMaster(ent);
        if (master == null || master.Value.Owner != ent.Owner)
            return;

        SetPowered(master.Value, args.Powered);
    }

    private void SetPowered(Entity<SignboardComponent> ent, bool powered)
    {
        ent.Comp.Powered = powered;
        ScheduleNextFrame(ent.Comp);
        Dirty(ent);
        UpdateUiState(ent.Owner, ent.Comp);
        DirtySlaves(ent.Owner, ent.Comp);
    }

    private static void AdvanceFrame(Entity<SignboardComponent> ent)
    {
        if (ent.Comp.Frames.Count == 0)
            return;

        ent.Comp.PlaybackFrame = (ent.Comp.PlaybackFrame + 1) % ent.Comp.Frames.Count;
    }

    private void ScheduleNextFrame(SignboardComponent comp)
    {
        comp.NextFrameAt = Timing.CurTime + TimeSpan.FromSeconds(Math.Clamp(comp.FrameDelay, 0.05f, 10f));
    }

    private void UpdateUiState(EntityUid uid, SignboardComponent component)
    {
        if (!_ui.HasUi(uid, SignboardUiKey.Key))
            return;

        _ui.SetUiState(uid, SignboardUiKey.Key, new SignboardBoundUserInterfaceState(
            component.Size,
            component.GridWidth,
            component.GridHeight,
            CloneFrames(component.Frames),
            component.ActiveEditFrame,
            component.PlaybackFrame,
            component.FrameDelay,
            component.Neon,
            component.Powered,
            component.Playing,
            component.FrameStyleId?.Id,
            canEdit: true));
    }

    private void DirtySlaves(EntityUid master, SignboardComponent masterComp)
    {
        var query = EntityQueryEnumerator<SignboardComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.Master != master)
                continue;

            // Mirror playback fields so clients refresh slave visuals when the master advances.
            comp.PlaybackFrame = masterComp.PlaybackFrame;
            comp.Powered = masterComp.Powered;
            comp.Playing = masterComp.Playing;
            comp.Neon = masterComp.Neon;
            comp.FrameStyleId = masterComp.FrameStyleId;
            Dirty(uid, comp);
        }
    }

    private void RebuildChainsAround(EntityUid seed)
    {
        if (!_xformQuery.TryComp(seed, out var seedXform) || !seedXform.Anchored)
        {
            DetachAsSolo(seed);
            return;
        }

        if (seedXform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
        {
            DetachAsSolo(seed);
            return;
        }

        var seedTile = _map.TileIndicesFor(gridUid, grid, seedXform.Coordinates);
        var byTile = new Dictionary<Vector2i, (EntityUid Uid, SignboardComponent Comp)>();

        // Wide gather so long walls of monitors still see each other, then flood-fill the real component.
        const int gatherRadius = SignboardComponent.MaxGrid * 4;
        for (var dy = -gatherRadius; dy <= gatherRadius; dy++)
        {
            for (var dx = -gatherRadius; dx <= gatherRadius; dx++)
            {
                var check = new Vector2i(seedTile.X + dx, seedTile.Y + dy);
                foreach (var ent in _map.GetAnchoredEntities(gridUid, grid, check))
                {
                    if (!_signQuery.TryComp(ent, out var sign))
                        continue;
                    if (!_xformQuery.TryComp(ent, out var xform) || !xform.Anchored)
                        continue;

                    var entTile = _map.TileIndicesFor(gridUid, grid, xform.Coordinates);
                    byTile[entTile] = (ent, sign);
                }
            }
        }

        if (byTile.Count == 0)
            return;

        var visited = new HashSet<Vector2i>();
        foreach (var start in byTile.Keys.ToList())
        {
            if (!visited.Add(start))
                continue;

            var component = FloodFill(start, byTile, visited);
            PartitionAndApply(component);
        }
    }

    private static List<(EntityUid Uid, Vector2i Tile, SignboardComponent Comp)> FloodFill(
        Vector2i start,
        Dictionary<Vector2i, (EntityUid Uid, SignboardComponent Comp)> byTile,
        HashSet<Vector2i> visited)
    {
        var result = new List<(EntityUid Uid, Vector2i Tile, SignboardComponent Comp)>();
        var stack = new Stack<Vector2i>();
        stack.Push(start);

        while (stack.Count > 0)
        {
            var tile = stack.Pop();
            if (!byTile.TryGetValue(tile, out var entry))
                continue;

            result.Add((entry.Uid, tile, entry.Comp));

            foreach (var dir in new[] { new Vector2i(1, 0), new Vector2i(-1, 0), new Vector2i(0, 1), new Vector2i(0, -1) })
            {
                var next = tile + dir;
                if (!byTile.ContainsKey(next) || !visited.Add(next))
                    continue;
                stack.Push(next);
            }
        }

        return result;
    }

    /// <summary>
    /// Split a connected blob into filled rectangles of at most MaxGrid×MaxGrid.
    /// Extra adjacent monitors must not dissolve an existing valid board.
    /// </summary>
    private void PartitionAndApply(List<(EntityUid Uid, Vector2i Tile, SignboardComponent Comp)> component)
    {
        if (component.Count == 0)
            return;

        var remaining = component.ToDictionary(p => p.Tile, p => p);

        while (remaining.Count > 0)
        {
            // Prefer north-west seed so boards grow consistently.
            var seed = remaining.Keys
                .OrderByDescending(t => t.Y)
                .ThenBy(t => t.X)
                .First();

            var (origin, width, height) = GrowFilledRect(seed, remaining.Keys.ToHashSet());
            var chunk = new List<(EntityUid Uid, Vector2i Tile, SignboardComponent Comp)>(width * height);

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    // origin is NW: tile Y decreases as panel Y increases
                    var tile = new Vector2i(origin.X + x, origin.Y - y);
                    if (!remaining.Remove(tile, out var panel))
                        continue;
                    chunk.Add(panel);
                }
            }

            if (chunk.Count == 0)
            {
                // Safety: never infinite-loop if grow failed.
                var orphan = remaining[seed];
                remaining.Remove(seed);
                ApplyGrid([orphan], 1, 1, seed);
                continue;
            }

            ApplyGrid(chunk, width, height, origin);
        }
    }

    /// <summary>
    /// Grow the largest filled axis-aligned rectangle ≤ MaxGrid from a NW-ish seed.
    /// </summary>
    private static (Vector2i Origin, int Width, int Height) GrowFilledRect(Vector2i seed, HashSet<Vector2i> available)
    {
        // Expand east and south (decreasing Y) from seed as far as the set stays solid.
        var bestW = 1;
        var bestH = 1;

        var maxW = Math.Min(SignboardComponent.MaxGrid, available.Count);
        for (var w = 1; w <= maxW; w++)
        {
            // Row at seed.Y must be solid for [seed.X, seed.X+w)
            var rowOk = true;
            for (var x = 0; x < w; x++)
            {
                if (!available.Contains(new Vector2i(seed.X + x, seed.Y)))
                {
                    rowOk = false;
                    break;
                }
            }

            if (!rowOk)
                break;

            var h = 1;
            for (var nextH = 2; nextH <= SignboardComponent.MaxGrid; nextH++)
            {
                var y = seed.Y - (nextH - 1);
                var solid = true;
                for (var x = 0; x < w; x++)
                {
                    if (!available.Contains(new Vector2i(seed.X + x, y)))
                    {
                        solid = false;
                        break;
                    }
                }

                if (!solid)
                    break;
                h = nextH;
            }

            if (w * h >= bestW * bestH)
            {
                bestW = w;
                bestH = h;
            }
        }

        return (seed, bestW, bestH);
    }

    private void ApplyGrid(
        List<(EntityUid Uid, Vector2i Tile, SignboardComponent Comp)> panels,
        int gridW,
        int gridH,
        Vector2i origin)
    {
        // Prefer existing master with frames as data source.
        SignboardComponent? dataSource = null;
        foreach (var (_, _, comp) in panels)
        {
            if (comp.Master == null && comp.Frames.Count > 0)
            {
                dataSource = comp;
                break;
            }
        }

        // Structural master = top-left panel.
        EntityUid masterUid = default;
        SignboardComponent masterComp = null!;
        foreach (var (uid, tile, comp) in panels)
        {
            if (tile != origin)
                continue;
            masterUid = uid;
            masterComp = comp;
            break;
        }

        if (masterComp == null)
        {
            masterUid = panels[0].Uid;
            masterComp = panels[0].Comp;
            origin = panels[0].Tile;
        }

        dataSource ??= masterComp;

        if (!ReferenceEquals(dataSource, masterComp) && dataSource.Frames.Count > 0)
        {
            masterComp.Frames = CloneFrames(dataSource.Frames);
            masterComp.Size = dataSource.Size;
            masterComp.FrameDelay = dataSource.FrameDelay;
            masterComp.Neon = dataSource.Neon;
            masterComp.Powered = dataSource.Powered;
            masterComp.Playing = dataSource.Playing;
            masterComp.FrameStyleId = dataSource.FrameStyleId;
            masterComp.ActiveEditFrame = dataSource.ActiveEditFrame;
            masterComp.PlaybackFrame = dataSource.PlaybackFrame;
            dataSource.Frames.Clear();
        }

        if (masterComp.GridWidth != gridW || masterComp.GridHeight != gridH || masterComp.FullWidth != masterComp.Size * gridW || masterComp.FullHeight != masterComp.Size * gridH)
            TryResizeFrames(masterComp, masterComp.Size, gridW, gridH);

        masterComp.Master = null;
        masterComp.PanelX = 0;
        masterComp.PanelY = 0;
        masterComp.GridWidth = gridW;
        masterComp.GridHeight = gridH;
        ScheduleNextFrame(masterComp);
        Dirty(masterUid, masterComp);

        foreach (var (uid, tile, comp) in panels)
        {
            var px = tile.X - origin.X;
            var py = origin.Y - tile.Y; // 0 at top (north)
            if (uid == masterUid)
                continue;

            comp.Master = masterUid;
            comp.PanelX = px;
            comp.PanelY = py;
            comp.GridWidth = gridW;
            comp.GridHeight = gridH;
            comp.Size = masterComp.Size;
            comp.FrameStyleId = masterComp.FrameStyleId;
            comp.PlaybackFrame = masterComp.PlaybackFrame;
            comp.Powered = masterComp.Powered;
            comp.Playing = masterComp.Playing;
            comp.Neon = masterComp.Neon;
            comp.Frames.Clear();
            Dirty(uid, comp);
        }

        UpdateUiState(masterUid, masterComp);
    }

    private void DetachAsSolo(EntityUid uid)
    {
        if (!_signQuery.TryComp(uid, out var comp))
            return;

        if (comp.Master != null)
        {
            comp.Master = null;
            comp.PanelX = 0;
            comp.PanelY = 0;
            comp.GridWidth = 1;
            comp.GridHeight = 1;
            if (comp.Frames.Count == 0)
                comp.Frames.Add(SignboardPixels.CreateFrame("Frame 1", comp.Size, comp.Size));
            Dirty(uid, comp);
            return;
        }

        if (comp.GridWidth != 1 || comp.GridHeight != 1)
        {
            TryResizeFrames(comp, comp.Size, 1, 1);
            comp.PanelX = 0;
            comp.PanelY = 0;
            Dirty(uid, comp);
        }
    }
}
