using Gpixel.Core.Edit;
using Gpixel.Core.Model;
using Gpixel.Core.IO;
using Gpixel.App.Localization;

namespace Gpixel.App;

/// <summary>Shared stop editor for the gradient tool and spatial effect colors.</summary>
public sealed class GradientEditor : StackPanel
{
    private readonly GradientBar _bar = new() { Margin = new Thickness(0, 2, 0, 2) };
    private readonly NumericUpDown _position;
    private readonly NumericUpDown _alpha;
    private readonly ColorField _color;
    private readonly StackPanel _colorRow;
    private readonly StackPanel _alphaRow;
    private readonly Button _delete;
    private readonly ComboBox _interpolation;
    private readonly ComboBox _colorSpaceEditor;
    private readonly ComboBox _dither;
    private readonly NumericUpDown? _angleEditor;
    private readonly ComboBox? _spatialMode;
    private readonly ComboBox? _direction;
    private readonly NumericUpDown? _cyclesEditor;
    private readonly ComboBox? _easingEditor;
    private readonly CheckBox? _pingPongEditor;
    private readonly Control? _angleRow;
    private readonly Control? _timingRow;
    private bool _updating;
    private IReadOnlyList<GradientStop> _stops = EditorContext.DefaultGradientStops;
    private IReadOnlyList<GradientAlphaStop> _alphaStops = EditorContext.DefaultGradientAlphaStops;
    private GradientInterpolation _mode;
    private GradientDither _ditherMode;
    private GradientColorSpace _colorSpace = GradientColorSpace.OkLch;
    private int _angle;
    private EffectGradientShape _shape;
    private bool _animated;
    private bool _reverse;
    private int _cycles = 1;
    private EffectEasing _easing = EffectEasing.Linear;
    private bool _pingPong;

    /// <summary>Spatial mode combo index of <see cref="EffectGradientShape.Uniform"/> (after the static/animated pairs).</summary>
    private const int UniformModeIndex = 8;

    /// <param name="presetMenu">False hides the right-click preset menu (the presets window edits the catalog itself).</param>
    public GradientEditor(Func<Color32>? primaryColor = null, bool spatialAngle = false, bool presetMenu = true)
    {
        Spacing = 4;
        _bar.Tip(UiStrings.S_GradientEditor_GradientEditor_5D23258D24);
        _bar.StopsChanged += value => { _stops = value; Changed?.Invoke(); RefreshSelected(); };
        _bar.AlphaStopsChanged += value => { _alphaStops = value; Changed?.Invoke(); RefreshSelected(); };
        _bar.SelectionChanged += (_, _) => RefreshSelected();
        if (presetMenu) PopupMenus.Attach(_bar, BuildPresetMenu);

        _position = Ui.Num(0, 100, 0, 56, 1, 1m).Presets(0, 10, 25, 33.3m, 50, 66.7m, 75, 90, 100).Unit("%").RecentKey("gradient.position");
        _position.FormatString = "0.#";
        _position.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            double value = (double)_position.NumValue() / 100;
            if (_bar.SelectedIsAlpha) ChangeAlpha(s => s with { Position = value });
            else ChangeColor(s => s with { Position = value });
        };
        _color = new ColorField(Color32.Black);
        _color.ColorChanged += (_, _) => { if (!_updating) ChangeColor(s => s with { Color = _color.Color }); };
        _alpha = Ui.Num(0, 100, 100, 56, 1, 1m).Presets(Ui.Percents).Unit("%").RecentKey("opacity");
        _alpha.FormatString = "0.#";
        _alpha.ValueChanged += (_, _) =>
        {
            if (!_updating) ChangeAlpha(s => s with { Alpha = (byte)Math.Round((double)_alpha.NumValue() * 255 / 100) });
        };
        _colorRow = Ui.Row(Ui.Label(UiStrings.S_GradientEditor_GradientEditor_ABB8BC9619), _color);
        if (primaryColor is not null)
            _colorRow.Children.Add(Ui.Button(UiStrings.S_GradientEditor_GradientEditor_9E5B25FE7A, (_, _) => ChangeColor(s => s with { Color = primaryColor() })));
        _alphaRow = Ui.Row(Ui.Label(UiStrings.S_GradientEditor_GradientEditor_D945F5766C), _alpha, Ui.Label("%"));
        _delete = Ui.Button(UiStrings.S_GradientEditor_GradientEditor_01174C479B, (_, _) => _bar.DeleteSelected()).Tip(UiStrings.S_GradientEditor_GradientEditor_303D5EC437);
        _interpolation = Ui.Combo(150, UiStrings.S_GradientEditor_GradientEditor_E0FC6F50AE, UiStrings.S_GradientEditor_GradientEditor_07C7B1752B);
        _dither = Ui.Combo(150, UiStrings.S_GradientEditor_GradientEditor_C103727A20, "2×2 Bayer", "4×4 Bayer");
        _colorSpaceEditor = Ui.Combo(150, "RGB", "HSL", "OKLab", "OKLCH");
        _interpolation.SelectionChanged += (_, _) =>
        {
            if (_updating || _interpolation.SelectedIndex < 0) return;
            _mode = (GradientInterpolation)_interpolation.SelectedIndex;
            _bar.Mode = _mode;
            _dither.IsEnabled = _mode == GradientInterpolation.Smooth;
            Changed?.Invoke();
        };
        _colorSpaceEditor.SelectionChanged += (_, _) =>
        {
            if (_updating || _colorSpaceEditor.SelectedIndex < 0) return;
            _colorSpace = (GradientColorSpace)_colorSpaceEditor.SelectedIndex;
            _bar.ColorSpace = _colorSpace;
            Changed?.Invoke();
        };
        _dither.SelectionChanged += (_, _) =>
        {
            if (_updating || _dither.SelectedIndex < 0) return;
            _ditherMode = (GradientDither)_dither.SelectedIndex;
            Changed?.Invoke();
        };
        Children.Add(_bar);
        Children.Add(Ui.Row(Ui.Label(UiStrings.S_GradientEditor_GradientEditor_3C9416C632), _position, Ui.Label("%"), _delete));
        Children.Add(_colorRow);
        Children.Add(_alphaRow);
        Children.Add(Ui.Row(Ui.Label(UiStrings.S_GradientEditor_GradientEditor_554C6D3CFC), _interpolation));
        Children.Add(Ui.Row(Ui.Label(UiStrings.GradientColorSpace_Label), _colorSpaceEditor));
        Children.Add(Ui.Row(Ui.Label(UiStrings.S_GradientEditor_GradientEditor_75C017194C), _dither));
        if (spatialAngle)
        {
            _spatialMode = Ui.Combo(180,
                UiStrings.EffectGradient_LinearStatic, UiStrings.EffectGradient_LinearAnimated,
                UiStrings.EffectGradient_RadialStatic, UiStrings.EffectGradient_RadialAnimated,
                UiStrings.EffectGradient_SquareStatic, UiStrings.EffectGradient_SquareAnimated,
                UiStrings.EffectGradient_AngularStatic, UiStrings.EffectGradient_AngularAnimated,
                UiStrings.EffectGradient_Uniform);
            _spatialMode.SelectionChanged += (_, _) =>
            {
                if (_updating || _spatialMode.SelectedIndex < 0) return;
                int index = _spatialMode.SelectedIndex;
                _shape = index == UniformModeIndex ? EffectGradientShape.Uniform : (EffectGradientShape)(index / 2);
                _animated = index == UniformModeIndex || index % 2 != 0;
                UpdateDirectionOptions();
                UpdateRows();
                Changed?.Invoke();
            };
            Children.Add(Ui.Row(Ui.Label(UiStrings.EffectGradient_Mode), _spatialMode));
            _direction = Ui.Combo(140);
            _direction.SelectionChanged += (_, _) =>
            {
                if (_updating || _direction.SelectedIndex < 0) return;
                _reverse = _direction.SelectedIndex == 1;
                Changed?.Invoke();
            };
            Children.Add(Ui.Row(Ui.Label(UiStrings.EffectGradient_Direction), _direction));
            _cyclesEditor = Ui.Num(1, 16, 1, 56, 1, 1m).RecentKey("effect.gradient.cycles");
            _cyclesEditor.FormatString = "0";
            _cyclesEditor.ValueChanged += (_, _) =>
            {
                if (_updating) return;
                _cycles = (int)_cyclesEditor.NumValue();
                Changed?.Invoke();
            };
            Children.Add(Ui.Row(Ui.Label(UiStrings.EffectGradient_Cycles), _cyclesEditor));
            _angleEditor = Ui.Num(0, 359, 0, 56, 1, 1m).Unit("°").RecentKey("effect.gradient.angle");
            _angleEditor.FormatString = "0";
            _angleEditor.ValueChanged += (_, _) =>
            {
                if (_updating) return;
                _angle = (int)_angleEditor.NumValue();
                Changed?.Invoke();
            };
            _angleRow = Ui.Row(Ui.Label(UiStrings.S_GradientEditor_GradientEditor_D5C20D5832), _angleEditor, Ui.Label(UiStrings.S_GradientEditor_GradientEditor_DEFF285651));
            Children.Add(_angleRow);
            _easingEditor = Ui.Combo(140, UiStrings.S_LayerEffectsDialog_Easings_4FA414FBAC, UiStrings.S_LayerEffectsDialog_Easings_BDEEAEDE46,
                UiStrings.S_LayerEffectsDialog_Easings_503369007B, UiStrings.S_LayerEffectsDialog_Easings_7665ED231C);
            _easingEditor.SelectionChanged += (_, _) =>
            {
                if (_updating || _easingEditor.SelectedIndex < 0) return;
                _easing = (EffectEasing)_easingEditor.SelectedIndex;
                Changed?.Invoke();
            };
            _pingPongEditor = Ui.Check(UiStrings.S_LayerEffectsDialog_Repeats_466235A991);
            _pingPongEditor.IsCheckedChanged += (_, _) =>
            {
                if (_updating) return;
                _pingPong = _pingPongEditor.IsChecked == true;
                Changed?.Invoke();
            };
            _timingRow = Ui.Row(Ui.Label(UiStrings.S_LayerEffectsDialog_AddTimingEditors_E1B19358AF), _easingEditor, _pingPongEditor);
            Children.Add(_timingRow);
        }
        Load(_stops, _alphaStops, _mode, _ditherMode);
    }

    private IEnumerable<object> BuildPresetMenu()
    {
        var items = new List<object>();
        var presets = GradientPresetUi.Available.Select(entry => entry.Gradient!).ToList();

        static MenuItem Item(string text, Action action)
        {
            var item = new MenuItem { Header = text };
            // Open dialogs only after the context menu has closed.
            item.Click += (_, _) => Dispatcher.UIThread.Post(action);
            return item;
        }

        static Control Header(GradientPreset preset)
        {
            Control name = preset.Favorite ? Ui.IconLabel(VectorIcon.Star, preset.Name) : new TextBlock { Text = preset.Name };
            var swatch = new GradientSwatch
            {
                Preset = preset,
                Width = 180,
                Height = 12,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 3, 0, 1),
            };
            return new StackPanel { Orientation = Orientation.Vertical, Children = { name, swatch } };
        }

        MenuItem PresetItem(GradientPreset preset)
        {
            var entry = Item(preset.Name, () => ApplyPreset(preset));
            entry.Header = Header(preset);
            return entry;
        }

        items.Add(Item(UiStrings.GradientPreset_SaveCurrent, SaveCurrentPreset));
        var favorites = presets.Where(preset => preset.Favorite).ToList();
        if (favorites.Count > 0)
        {
            items.Add(new Separator());
            items.Add(new MenuItem { Header = UiStrings.GradientPreset_Favorites, IsEnabled = false });
            foreach (var preset in favorites) items.Add(PresetItem(preset));
        }
        var others = presets.Where(preset => !preset.Favorite).ToList();
        if (others.Count > 0)
        {
            items.Add(new Separator());
            foreach (var preset in others) items.Add(PresetItem(preset));
        }
        if (presets.Count == 0)
        {
            items.Add(new Separator());
            items.Add(new MenuItem { Header = UiStrings.GradientPreset_Empty, IsEnabled = false });
        }
        items.Add(new Separator());
        items.Add(Item(UiStrings.GradientPreset_ManageMenu, () =>
        {
            if (Ui.Owner(this) is { } window) PresetsWindow.Open(window, PresetsTab.Gradients);
        }));
        return items;
    }

    /// <summary>지금 그라데이션을 개인 그라데이션으로 저장한다(같은 이름의 개인 그라데이션은 물어보고 덮어씀).</summary>
    private void SaveCurrentPreset()
    {
        if (Ui.Owner(this) is not { } owner) return;
        var personal = GradientPresetUi.Personal;
        string? name = InputDialog.Ask(owner, UiStrings.GradientPreset_SaveCurrent,
            UiStrings.GradientPreset_NamePrompt, "", personal.Select(entry => entry.Name))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var existing = personal.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && Ui.Ask(owner, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,
                UiStrings.GradientPreset_OverwriteQuestion, name), UiStrings.GradientPreset_SaveCurrent,
                MessageButtons.YesNo, MessageIcon.Question) != DialogResult.Yes) return;
        GradientPresetUi.Save(owner, existing?.Id, ToPreset(name, existing?.Favorite == true));
    }

    /// <summary>Snapshot of every edited setting, including the spatial state hidden in the tool's editor.</summary>
    public GradientPreset ToPreset(string name, bool favorite) => new()
    {
        Name = name,
        Stops = _stops.ToArray(),
        AlphaStops = _alphaStops.ToArray(),
        Interpolation = _mode,
        Dither = _ditherMode,
        ColorSpace = _colorSpace,
        Angle = _angle,
        Shape = _shape,
        Animated = _animated,
        Reverse = _reverse,
        Cycles = _cycles,
        Easing = _easing,
        PingPong = _pingPong,
        Favorite = favorite,
    };

    /// <summary>Shows every setting of <paramref name="preset"/> without firing Changed.</summary>
    public void LoadPreset(GradientPreset preset)
        => Load(preset.Stops, preset.AlphaStops, preset.Interpolation, preset.Dither, preset.Angle, preset.Shape,
            preset.Animated, preset.Reverse, preset.Cycles, preset.Easing, preset.PingPong, preset.ColorSpace);

    private void ApplyPreset(GradientPreset preset)
    {
        // Spatial controls are absent in the tool's editor; keep its hidden spatial state intact.
        bool spatial = _spatialMode is not null;
        Load(preset.Stops, preset.AlphaStops, preset.Interpolation, preset.Dither,
            spatial ? preset.Angle : _angle, spatial ? preset.Shape : _shape,
            spatial ? preset.Animated : _animated, spatial ? preset.Reverse : _reverse,
            spatial ? preset.Cycles : _cycles, spatial ? preset.Easing : _easing,
            spatial ? preset.PingPong : _pingPong, preset.ColorSpace);
        Changed?.Invoke();
    }

    public event Action? Changed;
    public IReadOnlyList<GradientStop> Stops => _stops;
    public IReadOnlyList<GradientAlphaStop> AlphaStops => _alphaStops;
    public GradientInterpolation Interpolation => _mode;
    public GradientDither Dither => _ditherMode;
    public GradientColorSpace ColorSpace => _colorSpace;
    public int Angle => _angle;
    public EffectGradientShape Shape => _shape;
    public bool Animated => _animated;
    public bool Reverse => _reverse;
    public int Cycles => _cycles;
    public EffectEasing Easing => _easing;
    public bool PingPong => _pingPong;

    /// <summary>Synchronizes externally owned state without firing Changed.</summary>
    public void Load(IReadOnlyList<GradientStop> stops, IReadOnlyList<GradientAlphaStop> alphaStops,
        GradientInterpolation interpolation, GradientDither dither, int angle = 0,
        EffectGradientShape shape = EffectGradientShape.Linear, bool animated = false,
        bool reverse = false, int cycles = 1, EffectEasing easing = EffectEasing.Linear, bool pingPong = false,
        GradientColorSpace colorSpace = GradientColorSpace.OkLch)
    {
        _updating = true;
        try
        {
            _stops = stops;
            _alphaStops = alphaStops.Count < 2 ? EditorContext.DefaultGradientAlphaStops : alphaStops;
            _mode = interpolation;
            _ditherMode = dither;
            _colorSpace = Enum.IsDefined(colorSpace) ? colorSpace : GradientColorSpace.OkLch;
            _angle = angle;
            _shape = shape;
            _animated = animated || shape == EffectGradientShape.Uniform;
            _reverse = reverse;
            _cycles = Math.Clamp(cycles, 1, 16);
            _easing = easing;
            _pingPong = pingPong;
            if (_spatialMode is not null)
                _spatialMode.SelectedIndex = shape == EffectGradientShape.Uniform ? UniformModeIndex : (int)shape * 2 + (animated ? 1 : 0);
            if (_direction is not null) UpdateDirectionOptions();
            if (_cyclesEditor is not null) _cyclesEditor.Value = _cycles;
            if (_angleEditor is not null) _angleEditor.Value = angle;
            if (_easingEditor is not null) _easingEditor.SelectedIndex = (int)easing;
            if (_pingPongEditor is not null) _pingPongEditor.IsChecked = pingPong;
            UpdateRows();
            _bar.Stops = stops;
            _bar.AlphaStops = _alphaStops;
            _bar.Mode = interpolation;
            _bar.ColorSpace = _colorSpace;
            _interpolation.SelectedIndex = (int)interpolation;
            _colorSpaceEditor.SelectedIndex = (int)_colorSpace;
            _dither.SelectedIndex = (int)dither;
            _dither.IsEnabled = interpolation == GradientInterpolation.Smooth;
            RefreshSelected();
        }
        finally { _updating = false; }
    }

    /// <summary>Uniform has no spatial angle; speed curve and ping-pong only apply to moving gradients.</summary>
    private void UpdateRows()
    {
        if (_angleRow is not null) _angleRow.IsVisible = _shape != EffectGradientShape.Uniform;
        if (_timingRow is not null) _timingRow.IsVisible = _animated;
    }

    private void UpdateDirectionOptions()
    {
        if (_direction is null) return;
        bool previous = _updating;
        _updating = true;
        try
        {
            _direction.Items.Clear();
            if (_shape == EffectGradientShape.Angular)
            {
                _direction.Items.Add(UiStrings.EffectGradient_Counterclockwise);
                _direction.Items.Add(UiStrings.EffectGradient_Clockwise);
            }
            else if (_shape is EffectGradientShape.Radial or EffectGradientShape.Square)
            {
                _direction.Items.Add(UiStrings.EffectGradient_Outward);
                _direction.Items.Add(UiStrings.EffectGradient_Inward);
            }
            else
            {
                _direction.Items.Add(UiStrings.EffectGradient_Forward);
                _direction.Items.Add(UiStrings.EffectGradient_Backward);
            }
            _direction.SelectedIndex = _reverse ? 1 : 0;
        }
        finally { _updating = previous; }
    }

    private void ChangeColor(Func<GradientStop, GradientStop> change)
    {
        if (_bar.SelectedIsAlpha || _stops.Count == 0) return;
        var list = _stops.ToArray();
        int index = Math.Clamp(_bar.SelectedIndex, 0, list.Length - 1);
        list[index] = change(list[index]);
        _stops = list;
        _bar.Stops = list;
        Changed?.Invoke();
        RefreshSelected();
    }

    private void ChangeAlpha(Func<GradientAlphaStop, GradientAlphaStop> change)
    {
        if (!_bar.SelectedIsAlpha || _alphaStops.Count == 0) return;
        var list = _alphaStops.ToArray();
        int index = Math.Clamp(_bar.SelectedIndex, 0, list.Length - 1);
        list[index] = change(list[index]);
        _alphaStops = list;
        _bar.AlphaStops = list;
        Changed?.Invoke();
        RefreshSelected();
    }

    private void RefreshSelected()
    {
        bool previous = _updating;
        _updating = true;
        try
        {
            bool alpha = _bar.SelectedIsAlpha;
            _colorRow.IsVisible = !alpha;
            _alphaRow.IsVisible = alpha;
            _delete.IsEnabled = alpha ? _alphaStops.Count > 2 : _stops.Count > 2;
            if (alpha && _alphaStops.Count > 0)
            {
                var stop = _alphaStops[Math.Clamp(_bar.SelectedIndex, 0, _alphaStops.Count - 1)];
                _position.Value = (decimal)Math.Round(stop.Position * 100, 1);
                _alpha.Value = (decimal)Math.Round(stop.Alpha * 100 / 255.0, 1);
            }
            else if (!alpha && _stops.Count > 0)
            {
                var stop = _stops[Math.Clamp(_bar.SelectedIndex, 0, _stops.Count - 1)];
                _position.Value = (decimal)Math.Round(stop.Position * 100, 1);
                _color.Color = stop.Color;
            }
        }
        finally { _updating = previous; }
    }
}
