using System.Numerics;
using Content.Client.Stylesheets;
using Content.Shared._CitadelStation.SubGrid;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using static Robust.Client.UserInterface.Controls.BoxContainer;

namespace Content.Client._CitadelStation.SubGrid.UI;

/// <summary>
/// Fabricator design window: palette + 7×15 cell grid + assemble.
/// Grid display has north at the top (Y flipped vs local grid coords).
/// </summary>
public sealed class SubGridFabricatorWindow : DefaultWindow
{
    private static readonly Vector2 CellSize = new(28, 28);

    private readonly Dictionary<SubGridFabricatorCell, Button> _toolButtons = new();
    private readonly Button[,] _cells = new Button[SubGridFabricatorConstants.Width, SubGridFabricatorConstants.Height];
    private readonly SubGridFabricatorCell[] _blueprint = new SubGridFabricatorCell[SubGridFabricatorConstants.CellCount];
    private readonly Label _materialsLabel;
    private readonly Label _statusLabel;
    private readonly Button _assembleButton;

    private SubGridFabricatorCell _brush = SubGridFabricatorCell.Floor;

    public event Action<SubGridFabricatorCell[]>? OnAssemble;
    public event Action? OnClear;

    public SubGridFabricatorWindow()
    {
        Title = Loc.GetString("subgrid-fab-title");
        MinSize = SetSize = new Vector2(560, 680);

        var root = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            Margin = new Thickness(8),
            SeparationOverride = 6,
        };

        _materialsLabel = new Label { HorizontalExpand = true };
        root.AddChild(_materialsLabel);

        _statusLabel = new Label
        {
            HorizontalExpand = true,
            Text = Loc.GetString("subgrid-fab-hint"),
        };
        root.AddChild(_statusLabel);

        root.AddChild(BuildPalette());
        root.AddChild(BuildGrid());

        var buttons = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 8,
        };

        var clear = new Button
        {
            Text = Loc.GetString("subgrid-fab-clear"),
            HorizontalExpand = true,
        };
        clear.OnPressed += _ =>
        {
            Array.Clear(_blueprint);
            RefreshCells();
            OnClear?.Invoke();
            UpdateStatus();
        };
        buttons.AddChild(clear);

        _assembleButton = new Button
        {
            Text = Loc.GetString("subgrid-fab-assemble"),
            HorizontalExpand = true,
            StyleClasses = { StyleBase.ButtonCaution },
        };
        _assembleButton.OnPressed += _ =>
        {
            UpdateStatus();
            if (!SubGridFabricatorConstants.TryValidate(_blueprint, out var errId))
            {
                _statusLabel.Text = Loc.GetString(errId);
                return;
            }

            OnAssemble?.Invoke((SubGridFabricatorCell[]) _blueprint.Clone());
        };
        buttons.AddChild(_assembleButton);

        root.AddChild(buttons);
        Contents.AddChild(root);

        SelectBrush(SubGridFabricatorCell.Floor);
        RefreshCells();
        UpdateStatus();
    }

    public void UpdateState(SubGridFabricatorBoundUserInterfaceState state)
    {
        if (state.Cells.Length == SubGridFabricatorConstants.CellCount)
            Array.Copy(state.Cells, _blueprint, SubGridFabricatorConstants.CellCount);

        _materialsLabel.Text = Loc.GetString(
            "subgrid-fab-materials",
            ("steelHave", state.SteelAvailable),
            ("steelNeed", state.SteelRequired),
            ("uraniumHave", state.UraniumAvailable),
            ("uraniumNeed", state.UraniumRequired));

        var materialsOk = state.SteelAvailable >= state.SteelRequired
                          && state.UraniumAvailable >= state.UraniumRequired;
        _assembleButton.Disabled = !materialsOk;
        RefreshCells();
        UpdateStatus();
    }

    private Control BuildPalette()
    {
        var col = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            SeparationOverride = 4,
            HorizontalExpand = true,
        };

        var row1 = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            SeparationOverride = 4,
            HorizontalExpand = true,
        };
        AddTool(row1, SubGridFabricatorCell.Empty, "subgrid-fab-tool-erase");
        AddTool(row1, SubGridFabricatorCell.Floor, "subgrid-fab-tool-floor");
        AddTool(row1, SubGridFabricatorCell.StairsNorth, "subgrid-fab-tool-stairs-n");
        AddTool(row1, SubGridFabricatorCell.StairsEast, "subgrid-fab-tool-stairs-e");
        AddTool(row1, SubGridFabricatorCell.StairsSouth, "subgrid-fab-tool-stairs-s");
        AddTool(row1, SubGridFabricatorCell.StairsWest, "subgrid-fab-tool-stairs-w");
        AddTool(row1, SubGridFabricatorCell.Core, "subgrid-fab-tool-core");
        AddTool(row1, SubGridFabricatorCell.Console, "subgrid-fab-tool-console");
        col.AddChild(row1);

        var row2 = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            SeparationOverride = 4,
            HorizontalExpand = true,
        };
        AddTool(row2, SubGridFabricatorCell.ThrusterNorth, "subgrid-fab-tool-thrust-n");
        AddTool(row2, SubGridFabricatorCell.ThrusterEast, "subgrid-fab-tool-thrust-e");
        AddTool(row2, SubGridFabricatorCell.ThrusterSouth, "subgrid-fab-tool-thrust-s");
        AddTool(row2, SubGridFabricatorCell.ThrusterWest, "subgrid-fab-tool-thrust-w");
        AddTool(row2, SubGridFabricatorCell.Gyroscope, "subgrid-fab-tool-gyro");
        col.AddChild(row2);

        return col;
    }

    private void AddTool(BoxContainer row, SubGridFabricatorCell cell, string locId)
    {
        var btn = new Button
        {
            Text = Loc.GetString(locId),
            ToggleMode = true,
            MinWidth = 52,
        };
        btn.OnPressed += _ => SelectBrush(cell);
        _toolButtons[cell] = btn;
        row.AddChild(btn);
    }

    private void SelectBrush(SubGridFabricatorCell cell)
    {
        _brush = cell;
        foreach (var (type, btn) in _toolButtons)
            btn.Pressed = type == cell;
    }

    private Control BuildGrid()
    {
        var grid = new GridContainer
        {
            Columns = SubGridFabricatorConstants.Width,
            HSeparationOverride = 2,
            VSeparationOverride = 2,
            HorizontalAlignment = HAlignment.Center,
        };

        // Display north (high Y) at top.
        for (var row = SubGridFabricatorConstants.Height - 1; row >= 0; row--)
        {
            for (var x = 0; x < SubGridFabricatorConstants.Width; x++)
            {
                var y = row;
                var btn = new Button
                {
                    MinSize = CellSize,
                    MaxSize = CellSize,
                    SetSize = CellSize,
                    ToolTip = $"({x},{y})",
                };
                var cx = x;
                var cy = y;
                btn.OnPressed += _ =>
                {
                    _blueprint[SubGridFabricatorConstants.Index(cx, cy)] = _brush;
                    PaintCell(btn, _brush);
                    UpdateStatus();
                };
                _cells[x, y] = btn;
                grid.AddChild(btn);
            }
        }

        return grid;
    }

    private void RefreshCells()
    {
        for (var x = 0; x < SubGridFabricatorConstants.Width; x++)
        {
            for (var y = 0; y < SubGridFabricatorConstants.Height; y++)
            {
                PaintCell(_cells[x, y], _blueprint[SubGridFabricatorConstants.Index(x, y)]);
            }
        }
    }

    private static void PaintCell(Button btn, SubGridFabricatorCell cell)
    {
        btn.Text = cell switch
        {
            SubGridFabricatorCell.Empty => string.Empty,
            SubGridFabricatorCell.Floor => "■",
            SubGridFabricatorCell.StairsNorth => "▲",
            SubGridFabricatorCell.StairsEast => "▶",
            SubGridFabricatorCell.StairsSouth => "▼",
            SubGridFabricatorCell.StairsWest => "◀",
            SubGridFabricatorCell.Core => "◉",
            SubGridFabricatorCell.Console => "▣",
            SubGridFabricatorCell.ThrusterNorth => "▲",
            SubGridFabricatorCell.ThrusterEast => "▶",
            SubGridFabricatorCell.ThrusterSouth => "▼",
            SubGridFabricatorCell.ThrusterWest => "◀",
            SubGridFabricatorCell.Gyroscope => "◎",
            _ => "?",
        };

        var color = cell switch
        {
            SubGridFabricatorCell.Empty => Color.FromHex("#1a1a1a"),
            SubGridFabricatorCell.Floor => Color.FromHex("#5a5a5a"),
            SubGridFabricatorCell.StairsNorth
                or SubGridFabricatorCell.StairsEast
                or SubGridFabricatorCell.StairsSouth
                or SubGridFabricatorCell.StairsWest => Color.FromHex("#3d6ea5"),
            SubGridFabricatorCell.Core => Color.FromHex("#c9a227"),
            SubGridFabricatorCell.Console => Color.FromHex("#2f8f4e"),
            SubGridFabricatorCell.ThrusterNorth
                or SubGridFabricatorCell.ThrusterEast
                or SubGridFabricatorCell.ThrusterSouth
                or SubGridFabricatorCell.ThrusterWest => Color.FromHex("#a53d3d"),
            SubGridFabricatorCell.Gyroscope => Color.FromHex("#8a4ecf"),
            _ => Color.Magenta,
        };

        btn.ModulateSelfOverride = color;
    }

    private void UpdateStatus()
    {
        if (SubGridFabricatorConstants.TryValidate(_blueprint, out var errId))
            _statusLabel.Text = Loc.GetString("subgrid-fab-ready");
        else
            _statusLabel.Text = Loc.GetString(errId);
    }
}
