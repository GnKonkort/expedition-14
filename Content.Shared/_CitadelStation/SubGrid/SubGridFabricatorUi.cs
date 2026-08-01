using Robust.Shared.Serialization;

namespace Content.Shared._CitadelStation.SubGrid;

/// <summary>Cell types for the SubGrid fabricator blueprint canvas.</summary>
[Serializable, NetSerializable]
public enum SubGridFabricatorCell : byte
{
    Empty = 0,
    Floor = 1,
    StairsNorth = 2,
    StairsEast = 3,
    StairsSouth = 4,
    StairsWest = 5,
    Core = 6,
    Console = 7,
    ThrusterNorth = 8,
    ThrusterEast = 9,
    ThrusterSouth = 10,
    ThrusterWest = 11,
    Gyroscope = 12,
}

[Serializable, NetSerializable]
public enum SubGridFabricatorUiKey : byte
{
    Key,
}

public static class SubGridFabricatorConstants
{
    public const int Width = 7;
    public const int Height = 15;
    public const int CellCount = Width * Height;

    public static int Index(int x, int y) => x + y * Width;

    public static bool InBounds(int x, int y) =>
        x >= 0 && x < Width && y >= 0 && y < Height;

    public static bool IsOccupied(SubGridFabricatorCell cell) =>
        cell != SubGridFabricatorCell.Empty;

    public static bool IsStairs(SubGridFabricatorCell cell) =>
        cell is SubGridFabricatorCell.StairsNorth
            or SubGridFabricatorCell.StairsEast
            or SubGridFabricatorCell.StairsSouth
            or SubGridFabricatorCell.StairsWest;

    public static bool IsThruster(SubGridFabricatorCell cell) =>
        cell is SubGridFabricatorCell.ThrusterNorth
            or SubGridFabricatorCell.ThrusterEast
            or SubGridFabricatorCell.ThrusterSouth
            or SubGridFabricatorCell.ThrusterWest;

    /// <summary>Outward cardinal offset for stairs / thrusters.</summary>
    public static (int Dx, int Dy) OutwardOffset(SubGridFabricatorCell cell) => cell switch
    {
        SubGridFabricatorCell.StairsNorth or SubGridFabricatorCell.ThrusterNorth => (0, 1),
        SubGridFabricatorCell.StairsEast or SubGridFabricatorCell.ThrusterEast => (1, 0),
        SubGridFabricatorCell.StairsSouth or SubGridFabricatorCell.ThrusterSouth => (0, -1),
        SubGridFabricatorCell.StairsWest or SubGridFabricatorCell.ThrusterWest => (-1, 0),
        _ => (0, 0),
    };

    /// <summary>
    /// Edge-mounted piece: outward neighbor empty/off-canvas, inward neighbor occupied.
    /// </summary>
    public static bool IsOutwardEdgeMount(SubGridFabricatorCell[] cells, int x, int y, SubGridFabricatorCell cell)
    {
        var (dx, dy) = OutwardOffset(cell);
        if (dx == 0 && dy == 0)
            return false;

        var ox = x + dx;
        var oy = y + dy;
        if (InBounds(ox, oy) && IsOccupied(cells[Index(ox, oy)]))
            return false;

        var ix = x - dx;
        var iy = y - dy;
        if (!InBounds(ix, iy) || !IsOccupied(cells[Index(ix, iy)]))
            return false;

        return true;
    }

    /// <summary>Shared blueprint rules for client preview and server assemble.</summary>
    public static bool TryValidate(SubGridFabricatorCell[] cells, out string errorLocId)
    {
        errorLocId = string.Empty;
        if (cells.Length != CellCount)
        {
            errorLocId = "subgrid-fab-err-empty";
            return false;
        }

        var occupied = 0;
        var cores = 0;
        var consoles = 0;
        var gyros = 0;
        var stairs = 0;
        var outwardStairs = 0;
        var thrusters = 0;
        var edgeThrusters = 0;
        var thrustN = 0;
        var thrustE = 0;
        var thrustS = 0;
        var thrustW = 0;

        for (var x = 0; x < Width; x++)
        {
            for (var y = 0; y < Height; y++)
            {
                var cell = cells[Index(x, y)];
                if (!IsOccupied(cell))
                    continue;

                occupied++;
                switch (cell)
                {
                    case SubGridFabricatorCell.Core:
                        cores++;
                        break;
                    case SubGridFabricatorCell.Console:
                        consoles++;
                        break;
                    case SubGridFabricatorCell.Gyroscope:
                        gyros++;
                        break;
                    case SubGridFabricatorCell.StairsNorth:
                    case SubGridFabricatorCell.StairsEast:
                    case SubGridFabricatorCell.StairsSouth:
                    case SubGridFabricatorCell.StairsWest:
                        stairs++;
                        if (IsOutwardEdgeMount(cells, x, y, cell))
                            outwardStairs++;
                        break;
                    case SubGridFabricatorCell.ThrusterNorth:
                        thrusters++;
                        thrustN++;
                        if (IsOutwardEdgeMount(cells, x, y, cell))
                            edgeThrusters++;
                        break;
                    case SubGridFabricatorCell.ThrusterEast:
                        thrusters++;
                        thrustE++;
                        if (IsOutwardEdgeMount(cells, x, y, cell))
                            edgeThrusters++;
                        break;
                    case SubGridFabricatorCell.ThrusterSouth:
                        thrusters++;
                        thrustS++;
                        if (IsOutwardEdgeMount(cells, x, y, cell))
                            edgeThrusters++;
                        break;
                    case SubGridFabricatorCell.ThrusterWest:
                        thrusters++;
                        thrustW++;
                        if (IsOutwardEdgeMount(cells, x, y, cell))
                            edgeThrusters++;
                        break;
                }
            }
        }

        if (occupied == 0)
        {
            errorLocId = "subgrid-fab-err-empty";
            return false;
        }

        if (cores != 1)
        {
            errorLocId = "subgrid-fab-err-core";
            return false;
        }

        if (consoles != 1)
        {
            errorLocId = "subgrid-fab-err-console";
            return false;
        }

        if (gyros < 1)
        {
            errorLocId = "subgrid-fab-err-gyro";
            return false;
        }

        if (stairs < 1 || outwardStairs < 1)
        {
            errorLocId = "subgrid-fab-err-stairs";
            return false;
        }

        if (outwardStairs < stairs)
        {
            errorLocId = "subgrid-fab-err-stairs-inward";
            return false;
        }

        if (thrustN < 1 || thrustE < 1 || thrustS < 1 || thrustW < 1)
        {
            errorLocId = "subgrid-fab-err-thrusters-cardinal";
            return false;
        }

        if (edgeThrusters < thrusters)
        {
            errorLocId = "subgrid-fab-err-thrusters-edge";
            return false;
        }

        return true;
    }
}

/// <summary>Initial / refreshed fabricator UI state.</summary>
[Serializable, NetSerializable]
public sealed class SubGridFabricatorBoundUserInterfaceState : BoundUserInterfaceState
{
    public SubGridFabricatorCell[] Cells;
    public int SteelAvailable;
    public int UraniumAvailable;
    public int SteelRequired;
    public int UraniumRequired;

    public SubGridFabricatorBoundUserInterfaceState(
        SubGridFabricatorCell[] cells,
        int steelAvailable,
        int uraniumAvailable,
        int steelRequired,
        int uraniumRequired)
    {
        Cells = cells;
        SteelAvailable = steelAvailable;
        UraniumAvailable = uraniumAvailable;
        SteelRequired = steelRequired;
        UraniumRequired = uraniumRequired;
    }
}

[Serializable, NetSerializable]
public sealed class SubGridFabricatorAssembleMessage : BoundUserInterfaceMessage
{
    public readonly SubGridFabricatorCell[] Cells;

    public SubGridFabricatorAssembleMessage(SubGridFabricatorCell[] cells)
    {
        Cells = cells;
    }
}

[Serializable, NetSerializable]
public sealed class SubGridFabricatorClearMessage : BoundUserInterfaceMessage;
