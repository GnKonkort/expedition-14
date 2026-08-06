using Content.Shared._Arcane.ERP.Preferences;
using Content.Shared.Humanoid;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Localization;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.Client._Arcane.ERP.UI;

public sealed class ErpOrganSection : BoxContainer
{
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly IComponentFactory _componentFactory = default!;

    private ErpOrganPreferences _prefs = ErpOrganPreferences.Default();
    private string _species = string.Empty;
    private Sex _sex = Sex.Male;
    private bool _settingPreferences;
    private bool _penisArousedPreview;

    private readonly Dictionary<string, OrganControls> _organControls = new();

    public event Action<ErpOrganPreferences>? OnPreferencesChanged;
    public event Action<bool>? OnPenisArousedPreviewChanged;

    public ErpOrganSection()
    {
        Orientation = LayoutOrientation.Vertical;
        SeparationOverride = 4;
        Margin = new Thickness(0, 8, 0, 0);
        IoCManager.InjectDependencies(this);
        Build();
    }

    private void Build()
    {
        RemoveAllChildren();
        _organControls.Clear();
        _breastBounceControls = null;

        AddChild(new Label
        {
            Text = Loc.GetString("erp-organ-section-title"),
            Margin = new Thickness(0, 0, 0, 2),
        });

        var definitions = ErpOrganEditorDefinitions.GetForSpecies(_species, _sex, _prototype, _componentFactory);
        foreach (var definition in definitions)
        {
            var slotId = definition.SlotId;
            var container = new BoxContainer
            {
                Orientation = LayoutOrientation.Vertical,
                HorizontalExpand = true,
                Margin = new Thickness(0, 2),
            };

            var row = new BoxContainer
            {
                Orientation = LayoutOrientation.Horizontal,
                HorizontalExpand = true,
                SeparationOverride = 8,
            };

            row.AddChild(new Label
            {
                Text = Loc.GetString($"erp-preferences-tab-organ-{slotId}"),
                MinWidth = 90,
                VAlign = Label.VAlignMode.Center,
            });

            OptionButton? variantBtn = null;
            if (definition.Variants.Length > 0)
            {
                variantBtn = new OptionButton { MinWidth = 120 };
                foreach (var v in definition.Variants)
                    variantBtn.AddItem(Loc.GetString($"erp-preferences-tab-variant-{v}"), variantBtn.ItemCount);

                variantBtn.OnItemSelected += args =>
                {
                    variantBtn.SelectId(args.Id);
                    NotifyChange(slotId);
                };

                row.AddChild(variantBtn);
            }

            Slider? sizeSlider = null;
            Label? sizeLabel = null;
            if (definition.MaxSize > 1)
            {
                sizeLabel = new Label
                {
                    Text = "1",
                    MinWidth = 20,
                    VAlign = Label.VAlignMode.Center,
                };

                sizeSlider = new Slider
                {
                    MinValue = 1,
                    MaxValue = definition.MaxSize,
                    Value = 1,
                    HorizontalExpand = true,
                    MinWidth = 80,
                };

                sizeSlider.OnValueChanged += _ =>
                {
                    var snapped = MathF.Round(sizeSlider.Value);
                    if (MathF.Abs(sizeSlider.Value - snapped) > 0.01f)
                        sizeSlider.SetValueWithoutEvent(snapped);
                    sizeLabel.Text = ((int) snapped).ToString();
                    NotifyChange(slotId);
                };

                row.AddChild(new Label
                {
                    Text = Loc.GetString("erp-organ-size-label"),
                    VAlign = Label.VAlignMode.Center,
                });
                row.AddChild(sizeSlider);
                row.AddChild(sizeLabel);
            }

            var skinCheck = new CheckBox
            {
                Text = Loc.GetString("erp-organ-skin-color-label"),
                Pressed = true,
                Visible = definition.AllowColor,
            };

            row.AddChild(skinCheck);

            CheckBox? arousedPreview = null;
            if (slotId == ErpOrganSlots.Penis)
            {
                arousedPreview = new CheckBox
                {
                    Text = Loc.GetString("erp-organ-penis-aroused-preview-label"),
                    Pressed = _penisArousedPreview,
                };

                arousedPreview.OnToggled += args =>
                {
                    if (_settingPreferences)
                        return;

                    _penisArousedPreview = args.Pressed;
                    OnPenisArousedPreviewChanged?.Invoke(_penisArousedPreview);
                };

                row.AddChild(arousedPreview);
            }

            var resetButton = new Button
            {
                Text = Loc.GetString("erp-organ-reset-button"),
                MinWidth = 70,
            };

            resetButton.OnPressed += _ => ResetOrgan(slotId);
            row.AddChild(resetButton);

            container.AddChild(row);

            var colorSelector = new ColorSelectorSliders
            {
                SelectorType = ColorSelectorSliders.ColorSelectorType.Hsv,
                Visible = false,
                HorizontalExpand = true,
                Margin = new Thickness(90, 0, 0, 0),
            };

            colorSelector.OnColorChanged += _ => NotifyChange(slotId);

            skinCheck.OnToggled += args =>
            {
                colorSelector.Visible = !args.Pressed;
                NotifyChange(slotId);
            };

            container.AddChild(colorSelector);

            if (slotId == ErpOrganSlots.Breasts)
                AddBreastBounceControls(container);

            AddChild(container);

            _organControls[slotId] = new OrganControls(container, variantBtn, sizeSlider, skinCheck, arousedPreview, colorSelector, definition);
        }
    }

    private void AddBreastBounceControls(BoxContainer parent)
    {
        var bounceBox = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            Margin = new Thickness(90, 4, 0, 0),
            SeparationOverride = 2,
        };

        var enabled = new CheckBox
        {
            Text = Loc.GetString("erp-breast-bounce-enabled-label"),
            Pressed = true,
        };
        enabled.OnToggled += _ => NotifyBreastBounceChange();
        bounceBox.AddChild(enabled);

        var jumpsuit = new CheckBox
        {
            Text = Loc.GetString("erp-breast-bounce-jumpsuit-label"),
            Pressed = false,
        };
        jumpsuit.OnToggled += _ => NotifyBreastBounceChange();
        bounceBox.AddChild(jumpsuit);

        var bounceValue = new Label
        {
            Text = $"{(int) MathF.Round(BreastBouncePreferences.DefaultBounce * 100f)}%",
            MinWidth = 40,
            VAlign = Label.VAlignMode.Center,
        };

        var bounceSlider = new Slider
        {
            MinValue = 0f,
            MaxValue = 1f,
            Value = BreastBouncePreferences.DefaultBounce,
            HorizontalExpand = true,
            MinWidth = 100,
        };
        bounceSlider.OnValueChanged += _ =>
        {
            bounceValue.Text = $"{(int) MathF.Round(bounceSlider.Value * 100f)}%";
            NotifyBreastBounceChange();
        };

        var bounceRow = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 8,
        };
        bounceRow.AddChild(new Label
        {
            Text = Loc.GetString("erp-breast-bounce-amount-label"),
            VAlign = Label.VAlignMode.Center,
            MinWidth = 80,
        });
        bounceRow.AddChild(bounceSlider);
        bounceRow.AddChild(bounceValue);
        bounceBox.AddChild(bounceRow);

        var sideValue = new Label
        {
            Text = $"{(int) MathF.Round(BreastBouncePreferences.DefaultSideBounce * 100f)}%",
            MinWidth = 40,
            VAlign = Label.VAlignMode.Center,
        };

        var sideSlider = new Slider
        {
            MinValue = 0f,
            MaxValue = 1f,
            Value = BreastBouncePreferences.DefaultSideBounce,
            HorizontalExpand = true,
            MinWidth = 100,
        };
        sideSlider.OnValueChanged += _ =>
        {
            sideValue.Text = $"{(int) MathF.Round(sideSlider.Value * 100f)}%";
            NotifyBreastBounceChange();
        };

        var sideRow = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 8,
        };
        sideRow.AddChild(new Label
        {
            Text = Loc.GetString("erp-breast-bounce-side-label"),
            VAlign = Label.VAlignMode.Center,
            MinWidth = 80,
        });
        sideRow.AddChild(sideSlider);
        sideRow.AddChild(sideValue);
        bounceBox.AddChild(sideRow);

        parent.AddChild(bounceBox);
        _breastBounceControls = new BreastBounceControls(enabled, jumpsuit, bounceSlider, bounceValue, sideSlider, sideValue);
    }

    private BreastBounceControls? _breastBounceControls;

    private void NotifyBreastBounceChange()
    {
        if (_settingPreferences || _breastBounceControls == null)
            return;

        _prefs.BreastBounce = new BreastBouncePreferences
        {
            Enabled = _breastBounceControls.Enabled.Pressed,
            EnabledInJumpsuit = _breastBounceControls.EnabledInJumpsuit.Pressed,
            // Dual-breast split deferred until dedicated L/R sprites exist.
            Synchronized = true,
            Bounce = Math.Clamp(_breastBounceControls.Bounce.Value, 0f, 1f),
            SideBounce = Math.Clamp(_breastBounceControls.SideBounce.Value, 0f, 1f),
        };
        OnPreferencesChanged?.Invoke(_prefs);
    }

    public void Update(string species, Sex sex, ErpOrganPreferences prefs)
    {
        _species = species;
        _sex = sex;
        _prefs = prefs;
        _settingPreferences = true;
        try
        {
            Build();
            ApplyPreferences();
        }
        finally
        {
            _settingPreferences = false;
        }
    }

    public void SetSpecies(string species)
    {
        _species = species;
        _settingPreferences = true;
        try
        {
            Build();
            ApplyPreferences();
        }
        finally
        {
            _settingPreferences = false;
        }
    }

    public void SetSex(Sex sex)
    {
        _sex = sex;
        _settingPreferences = true;
        try
        {
            Build();
            ApplyPreferences();
        }
        finally
        {
            _settingPreferences = false;
        }
    }

    public void SetPenisArousedPreview(bool aroused)
    {
        _penisArousedPreview = aroused;

        if (!_organControls.TryGetValue(ErpOrganSlots.Penis, out var ctrl) || ctrl.ArousedPreview == null)
            return;

        _settingPreferences = true;
        try
        {
            ctrl.ArousedPreview.Pressed = aroused;
        }
        finally
        {
            _settingPreferences = false;
        }
    }

    public void SetPreferences(ErpOrganPreferences prefs)
    {
        _settingPreferences = true;
        try
        {
            _prefs = prefs;
            ApplyPreferences();
        }
        finally
        {
            _settingPreferences = false;
        }
    }

    private void NotifyChange(string slotId)
    {
        if (_settingPreferences)
            return;

        if (!_organControls.TryGetValue(slotId, out var ctrl))
            return;

        var variants = ctrl.Definition.Variants;
        var variantIdx = ctrl.Variant?.SelectedId ?? 0;
        var variant = variantIdx < variants.Length ? variants[variantIdx] : ctrl.Definition.DefaultVariant;

        var size = (int) MathF.Round(ctrl.Size?.Value ?? 1f);
        var color = !ctrl.Definition.AllowColor || ctrl.SkinCheck.Pressed ? (Color?) null : ctrl.ColorSelector.Color;

        _prefs.SetOrgan(slotId, new ErpOrganConfig { Variant = variant, Size = size, Color = color });
        OnPreferencesChanged?.Invoke(_prefs);
    }

    private void ResetOrgan(string slotId)
    {
        if (!_organControls.TryGetValue(slotId, out var ctrl))
            return;

        _prefs.SetOrgan(slotId, ErpOrganEditorDefinitions.CreateDefaultConfig(ctrl.Definition));
        if (slotId == ErpOrganSlots.Breasts)
            _prefs.BreastBounce = new BreastBouncePreferences();

        _settingPreferences = true;
        try
        {
            ApplyPreferences();
        }
        finally
        {
            _settingPreferences = false;
        }

        OnPreferencesChanged?.Invoke(_prefs);
    }

    private void ApplyPreferences()
    {
        foreach (var (slotId, ctrl) in _organControls)
        {
            var cfg = _prefs.GetOrgan(slotId);

            if (ctrl.Variant != null)
            {
                var variants = ctrl.Definition.Variants;
                var idx = Array.IndexOf(variants, cfg.Variant);
                if (idx < 0 && variants.Length > 0)
                {
                    cfg = new ErpOrganConfig { Variant = variants[0], Size = cfg.Size, Color = cfg.Color };
                    _prefs.SetOrgan(slotId, cfg);
                    idx = 0;
                }

                ctrl.Variant.SelectId(Math.Max(0, idx));
            }

            if (ctrl.Size != null)
                ctrl.Size.Value = Math.Clamp(cfg.Size, ctrl.Size.MinValue, ctrl.Size.MaxValue);

            var hasCustomColor = ctrl.Definition.AllowColor && cfg.Color.HasValue;
            ctrl.SkinCheck.Pressed = !hasCustomColor;
            ctrl.ColorSelector.Visible = hasCustomColor;
            if (hasCustomColor)
                ctrl.ColorSelector.Color = cfg.Color!.Value;
        }

        if (_breastBounceControls != null)
        {
            var bounce = BreastBouncePreferences.Normalize(_prefs.BreastBounce);
            _breastBounceControls.Enabled.Pressed = bounce.Enabled;
            _breastBounceControls.EnabledInJumpsuit.Pressed = bounce.EnabledInJumpsuit;
            _breastBounceControls.Bounce.SetValueWithoutEvent(bounce.Bounce);
            _breastBounceControls.BounceLabel.Text = $"{(int) MathF.Round(bounce.Bounce * 100f)}%";
            _breastBounceControls.SideBounce.SetValueWithoutEvent(bounce.SideBounce);
            _breastBounceControls.SideBounceLabel.Text = $"{(int) MathF.Round(bounce.SideBounce * 100f)}%";
        }
    }

    private sealed class BreastBounceControls
    {
        public readonly CheckBox Enabled;
        public readonly CheckBox EnabledInJumpsuit;
        public readonly Slider Bounce;
        public readonly Label BounceLabel;
        public readonly Slider SideBounce;
        public readonly Label SideBounceLabel;

        public BreastBounceControls(
            CheckBox enabled,
            CheckBox enabledInJumpsuit,
            Slider bounce,
            Label bounceLabel,
            Slider sideBounce,
            Label sideBounceLabel)
        {
            Enabled = enabled;
            EnabledInJumpsuit = enabledInJumpsuit;
            Bounce = bounce;
            BounceLabel = bounceLabel;
            SideBounce = sideBounce;
            SideBounceLabel = sideBounceLabel;
        }
    }

    private sealed class OrganControls
    {
        public readonly BoxContainer Container;
        public readonly OptionButton? Variant;
        public readonly Slider? Size;
        public readonly CheckBox SkinCheck;
        public readonly CheckBox? ArousedPreview;
        public readonly ColorSelectorSliders ColorSelector;
        public readonly ErpOrganEditorDefinition Definition;

        public OrganControls(BoxContainer container, OptionButton? variant, Slider? size,
            CheckBox skinCheck, CheckBox? arousedPreview, ColorSelectorSliders colorSelector, ErpOrganEditorDefinition definition)
        {
            Container = container;
            Variant = variant;
            Size = size;
            SkinCheck = skinCheck;
            ArousedPreview = arousedPreview;
            ColorSelector = colorSelector;
            Definition = definition;
        }
    }
}
