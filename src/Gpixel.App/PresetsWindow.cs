using System.Globalization;
using Gpixel.App.Localization;
using Gpixel.Core.Assets;
using Gpixel.Core.IO;

namespace Gpixel.App;

/// <summary>Tabs of <see cref="PresetsWindow"/>, in tab order.</summary>
public enum PresetsTab
{
    Gradients,
    AutoOptimize,
}

/// <summary>
/// One window for every preset kind, one tab each. The gradient tab lists the asset catalog's gradients: personal gradients
/// (editable — saved to the personal asset store) and gradients of enabled installed packages (read-only originals; saving an
/// edited one stores a personal copy). It is modal; changes made from the asset manager opened here refresh the list.
/// </summary>
public sealed class PresetsWindow : DialogBase
{
    private readonly ListBox _list;
    private readonly Button _favorite;
    private readonly Button _rename;
    private readonly Button _delete;
    private readonly Button _save;
    private readonly TextBlock _title;
    private readonly TextBlock _note;
    private readonly GradientSwatch _preview;
    private readonly GradientEditor _editor;
    private readonly Control _detail;
    private readonly Action _onAssetsChanged;
    /// <summary>The entry shown in the editor (null when nothing is selected).</summary>
    private AssetEntry? _loaded;
    private bool _dirty;
    private bool _refreshing;
    private bool _switchPending;

    public static void Open(Window owner, PresetsTab tab = PresetsTab.Gradients)
    {
        AppAssets.ShowProblems(owner);
        new PresetsWindow(tab).ShowDialog(owner);
    }

    private PresetsWindow(PresetsTab tab) : base(UiStrings.Presets_Title)
    {
        CloseOnly();
        CancelButtonControl.IsDefault = false; // Enter belongs to the number fields.
        CanResize = true;
        SizeToContent = SizeToContent.Manual;
        Width = 1000;
        Height = 680;
        MinWidth = 720;
        MinHeight = 480;

        // ── Gradients: list on the left, preview and editor of the selected preset on the right.
        _list = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            Background = AppTheme.InputBrush,
            Foreground = AppTheme.TextBrush,
            BorderBrush = AppTheme.BorderBrush,
            BorderThickness = new Thickness(1),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (_, _) => OnSelectionChanged();
        _favorite = Ui.Button(VectorIcon.Star, UiStrings.GradientPreset_AddFavorite, (_, _) => ToggleFavorite());
        _rename = Ui.Button(UiStrings.GradientPreset_Rename, (_, _) => Rename());
        _delete = Ui.Button(UiStrings.GradientPreset_Delete, (_, _) => Delete());
        var manager = Ui.Button(VectorIcon.Package, UiStrings.Assets_ManagerButton,
            (_, _) => AssetManagerWindow.Open(this, AssetKinds.Gradient, _loaded?.Id));
        ToolTip.SetTip(manager, UiStrings.Assets_ManagerTip);
        var listButtons = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        listButtons.Children.AddRange([_favorite, _rename, _delete, manager]);
        var left = new DockPanel();
        DockPanel.SetDock(listButtons, Dock.Bottom);
        left.Children.Add(listButtons);
        if (AppAssets.Store.PersonalError is { } error)
        {
            var warning = new TextBlock
            {
                Text = string.Format(CultureInfo.CurrentCulture, UiStrings.GradientPreset_LoadError, error),
                Foreground = AppTheme.WarningBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            };
            DockPanel.SetDock(warning, Dock.Top);
            left.Children.Add(warning);
        }
        left.Children.Add(_list);

        _title = new TextBlock
        {
            FontWeight = FontWeight.Bold,
            Foreground = AppTheme.TextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 4),
        };
        _note = new TextBlock { Foreground = AppTheme.DimTextBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        _preview = new GradientSwatch { Height = 56, Margin = new Thickness(0, 0, 0, 8) };
        _editor = new GradientEditor(spatialAngle: true, presetMenu: false);
        _editor.Changed += OnEditorChanged;
        _save = Ui.Button(UiStrings.GradientPreset_SaveChanges, (_, _) => SaveChanges());
        _save.Margin = new Thickness(0, 8, 0, 0);
        _save.HorizontalAlignment = HorizontalAlignment.Left;
        _detail = new StackPanel { Children = { _preview, _editor, _save } };
        var right = new ScrollViewer
        {
            Content = new StackPanel { Children = { _title, _note, _detail } },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(right, 1);
        var gradients = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("300,*"),
            Margin = new Thickness(0, 8, 0, 0),
            Children = { left, right },
        };

        // ── Auto optimize: the panel edits the presets inside the app settings; they are saved when the window closes.
        var options = App.Settings.Pixelate ??= new PixelateOptions();
        options.Migrate();
        var optimizer = new OptimizePresetsPanel(options) { Margin = new Thickness(0, 8, 0, 0) };

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = UiStrings.GradientPreset_Category, Content = gradients });
        tabs.Items.Add(new TabItem { Header = UiStrings.Presets_OptimizerTab, Content = optimizer });
        tabs.SelectedIndex = (int)tab;

        // The dialog body (StackPanel) gives no height, so the tabs go straight into the outer DockPanel's fill slot.
        var root = (DockPanel)Content!;
        root.Children.Remove(Body);
        root.Children.Add(tabs);

        Closing += (_, e) =>
        {
            if (!_dirty || _loaded is null) return;
            e.Cancel = true;
            Dispatcher.UIThread.Post(() =>
            {
                AskSaveChanges();
                Close();
            });
        };
        // The asset manager opened from here can disable, delete or copy gradients: rebuild the list, keeping unsaved edits.
        _onAssetsChanged = () => Dispatcher.UIThread.Post(() => Refresh(_loaded?.Id, keepEdits: true));
        AppAssets.Changed += _onAssetsChanged;
        Closed += (_, _) =>
        {
            AppAssets.Changed -= _onAssetsChanged;
            App.Settings.Save();
        };
        Refresh(null, fallbackRow: 0);
    }

    private AssetEntry? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as AssetEntry;

    private static string Format(string format, params object?[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    private static bool IsPersonal(AssetEntry? entry) => entry?.Origin == AssetOrigin.Personal;

    private static Control Row(AssetEntry entry)
    {
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (entry.Favorite) name.Children.Add(Ui.Icon(VectorIcon.Star, 13, filled: true));
        name.Children.Add(Ui.Label(entry.Name));
        if (entry.PackageName is { } package)
            name.Children.Add(new TextBlock { Text = package, Foreground = AppTheme.DimTextBrush, FontSize = AppTheme.SmallFontSize, VerticalAlignment = VerticalAlignment.Center });
        var swatch = new GradientSwatch { Preset = entry.Gradient, Height = 14, Margin = new Thickness(0, 3, 0, 2) };
        return new StackPanel { Children = { name, swatch } };
    }

    /// <summary>Rebuilds the list from the catalog (favorites first) and selects <paramref name="selectId"/>, else the row at
    /// <paramref name="fallbackRow"/>. <paramref name="keepEdits"/> keeps unsaved editor changes of the same entry.</summary>
    private void Refresh(string? selectId, int fallbackRow = -1, bool keepEdits = false)
    {
        _refreshing = true;
        try
        {
            _list.Items.Clear();
            var entries = GradientPresetUi.Available;
            var ordered = entries.Where(p => p.Favorite).Concat(entries.Where(p => !p.Favorite)).ToList();
            foreach (var entry in ordered)
                _list.Items.Add(new ListBoxItem { Content = Row(entry), Tag = entry, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            if (ordered.Count == 0)
                _list.Items.Add(new ListBoxItem { Content = UiStrings.GradientPreset_Empty, IsEnabled = false });
            else
            {
                int row = selectId is null ? -1 : ordered.FindIndex(p => p.Id == selectId);
                if (row < 0 && fallbackRow >= 0) row = Math.Min(fallbackRow, ordered.Count - 1);
                _list.SelectedIndex = row;
            }
        }
        finally { _refreshing = false; }
        if (keepEdits && _dirty && Selected is { } selected && selected.Id == _loaded?.Id)
        {
            _loaded = selected;
            _title.Text = selected.Name;
            UpdateButtons();
        }
        else LoadSelected();
    }

    private void OnSelectionChanged()
    {
        if (_refreshing) return;
        if (!_dirty)
        {
            LoadSelected();
            return;
        }
        // Ask after the list has finished its selection change.
        if (_switchPending) return;
        _switchPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _switchPending = false;
            var next = Selected?.Id;
            AskSaveChanges();
            Refresh(next);
        });
    }

    private void LoadSelected()
    {
        var entry = Selected;
        _loaded = entry;
        _dirty = false;
        _title.Text = entry?.Name ?? UiStrings.GradientPreset_SelectHint;
        _note.Text = entry is { Origin: AssetOrigin.Package } ? Format(UiStrings.Assets_PackageGradientNote, entry.PackageName) : "";
        _note.IsVisible = _note.Text.Length > 0;
        _detail.IsEnabled = entry is not null;
        if (entry?.Gradient is { } gradient) _editor.LoadPreset(gradient);
        _preview.Preset = entry?.Gradient;
        UpdateButtons();
    }

    private void OnEditorChanged()
    {
        if (_loaded is not { } entry) return;
        _dirty = true;
        _preview.Preset = _editor.ToPreset(entry.Name, entry.Favorite);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool enabled = _loaded is not null;
        _favorite.IsEnabled = enabled;
        _rename.IsEnabled = IsPersonal(_loaded);
        _delete.IsEnabled = IsPersonal(_loaded);
        _delete.Content = _loaded?.IsBuiltIn == true ? UiStrings.Assets_Disable : UiStrings.GradientPreset_Delete;
        _save.Content = _loaded is null || IsPersonal(_loaded) ? UiStrings.GradientPreset_SaveChanges : UiStrings.Assets_SaveAsPersonal;
        _favorite.Content = Ui.IconLabel(VectorIcon.Star, _loaded?.Favorite == true
            ? UiStrings.GradientPreset_RemoveFavorite : UiStrings.GradientPreset_AddFavorite);
    }

    /// <summary>
    /// Saves the editor's values: a personal gradient in place, a package gradient as a new personal copy (asks for its name; an
    /// existing personal gradient of that name is overwritten after asking). Returns the saved entry, or null when cancelled or failed.
    /// </summary>
    private AssetEntry? SaveEdits(AssetEntry entry)
    {
        if (IsPersonal(entry)) return GradientPresetUi.Save(this, entry.Id, _editor.ToPreset(entry.Name, entry.Favorite));
        var personal = GradientPresetUi.Personal;
        string? name = InputDialog.Ask(this, UiStrings.Assets_SaveAsPersonal, UiStrings.GradientPreset_NamePrompt, entry.Name,
            personal.Select(p => p.Name))?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        var existing = personal.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && Ui.Ask(this, Format(UiStrings.GradientPreset_OverwriteQuestion, name), UiStrings.Assets_SaveAsPersonal,
                MessageButtons.YesNo, MessageIcon.Question) != DialogResult.Yes) return null;
        return GradientPresetUi.Save(this, existing?.Id, _editor.ToPreset(name, existing?.Favorite == true));
    }

    /// <summary>Unsaved edits of the previously shown entry: save them on Yes, drop them on No.</summary>
    private void AskSaveChanges()
    {
        if (!_dirty || _loaded is not { } entry) return;
        _dirty = false;
        if (Ui.Ask(this, Format(UiStrings.GradientPreset_SaveChangesQuestion, entry.Name), UiStrings.GradientPreset_SaveChanges,
                MessageButtons.YesNo, MessageIcon.Question) != DialogResult.Yes) return;
        SaveEdits(entry);
    }

    private void SaveChanges()
    {
        if (_loaded is not { } entry || SaveEdits(entry) is not { } saved) return;
        _dirty = false;
        Refresh(saved.Id);
    }

    private void ToggleFavorite()
    {
        if (_loaded is not { } entry || !GradientPresetUi.SetFavorite(this, entry.Id, !entry.Favorite)) return;
        Refresh(entry.Id, keepEdits: true);
    }

    private void Rename()
    {
        if (_loaded is not { } entry || !IsPersonal(entry) || entry.Gradient is not { } gradient) return;
        var others = GradientPresetUi.Personal.Where(p => p.Id != entry.Id).ToList();
        string? name = InputDialog.Ask(this, UiStrings.GradientPreset_RenameTitle,
            UiStrings.GradientPreset_NamePrompt, entry.Name, others.Select(p => p.Name))?.Trim();
        if (string.IsNullOrEmpty(name) || name == entry.Name) return;
        if (others.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Ui.Warn(this, Format(UiStrings.GradientPreset_DuplicateName, name), UiStrings.GradientPreset_RenameTitle);
            return;
        }
        if (GradientPresetUi.Save(this, entry.Id, gradient with { Name = name }) is null) return;
        Refresh(entry.Id, keepEdits: true);
    }

    private void Delete()
    {
        if (_loaded is not { } entry || !IsPersonal(entry)) return;
        if (entry.IsBuiltIn)
        {
            if (!AppAssets.Try(this, () => AppAssets.Store.SetPersonalEnabled(entry.Id, false), UiStrings.GradientPreset_SaveError)) return;
        }
        else
        {
            if (Ui.Ask(this, Format(UiStrings.GradientPreset_DeleteQuestion, entry.Name), UiStrings.GradientPreset_Delete,
                    MessageButtons.YesNo, MessageIcon.Question) != DialogResult.Yes) return;
            if (!GradientPresetUi.Remove(this, entry.Id)) return;
        }
        int row = _list.SelectedIndex;
        _dirty = false;
        Refresh(null, row);
    }
}
