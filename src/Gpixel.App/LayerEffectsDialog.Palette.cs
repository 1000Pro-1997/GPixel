using Gpixel.Core.Edit;
using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;
using Gpixel.Core.Assets;
using Gpixel.Core.IO;
using Gpixel.Core.Scripting;

namespace Gpixel.App;

/// <summary>
/// 효과 창 왼쪽 끝의 효과 종류 칸: 종류마다 미리보기 그림(지금 프레임 그림에 기본 설정으로 입힌 모습 — 움직이는 효과는 돌려 보임) + 이름 상자.
/// 두 번 누르거나 효과 목록으로 끌어 놓으면 "추가 범위"로 추가한다(끌어 놓으면 놓은 자리에).
/// 커스텀 탭은 에셋 카탈로그의 효과 레시피와 스크립트 효과(개인 에셋 + 켜진 패키지)를 보이고, 저장·이름 바꾸기·삭제는 개인 에셋 저장소에 한다.
/// 레시피를 넣으면 효과 값과 스크립트 원문을 복사하므로 나중에 레시피를 지우거나 꺼도 이미 넣은 효과는 바뀌지 않는다.
/// 스크립트가 든 상자는 모서리에 코드 표시를 그리고, 실행을 허용하지 않은 스크립트가 있으면 풍선에 알린다(넣어도 허용 전에는 그리지 않음).
/// </summary>
public sealed partial class LayerEffectsDialog
{
    private const int TileWidth = 88, TileHeight = 96, ThumbSteps = AssetPreview.Steps, ThumbHold = 4, ThumbMaxSize = 128;
    /// <summary>효과 종류 칸의 한 줄 상자 수.</summary>
    private const int PaletteColumns = 4;

    private readonly List<EffectTile> _tiles = new();
    /// <summary>커스텀 탭의 에셋(카탈로그의 고를 수 있는 효과 레시피·스크립트 효과).</summary>
    private readonly List<AssetEntry> _recipes = new();
    private readonly WrapPanel _customWrap = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _customEmpty = Ui.Label("");
    private readonly DispatcherTimer _thumbTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private PixelImage _thumbSource = SampleSprite();
    private int _thumbTick;
    private EffectTile? _pressedTile;
    private Avalonia.Point _pressPoint;
    private bool _tileDragging;
    private Button? _savePreset;
    private Action? _onAssetsChanged;

    private Control BuildPalette(string? motionTip)
    {
        _thumbSource = ThumbSource();
        var builtInWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        ReloadBuiltInPalette(builtInWrap, motionTip);

        _savePreset = Ui.Button(VectorIcon.Layers, UiStrings.EffectPreset_SaveGroup, (_, _) => SavePresetGroup());
        _savePreset.HorizontalAlignment = HorizontalAlignment.Stretch;
        ToolTip.SetTip(_savePreset, UiStrings.EffectPreset_SaveGroupTip);
        var manager = Ui.Button(VectorIcon.Package, UiStrings.Assets_ManagerButton, (_, _) => AssetManagerWindow.Open(this, AssetKinds.EffectRecipe));
        manager.HorizontalAlignment = HorizontalAlignment.Stretch;
        ToolTip.SetTip(manager, UiStrings.Assets_ManagerTip);
        _customEmpty.TextWrapping = TextWrapping.Wrap;
        _customEmpty.MaxWidth = (TileWidth + 4) * PaletteColumns;
        ReloadRecipes();
        // 에셋 관리자에서 켜고 끄거나 지우면 두 탭을 다시 만든다(이미 넣은 효과는 값 사본이라 그대로).
        _onAssetsChanged = () => Dispatcher.UIThread.Post(() =>
        {
            ReloadBuiltInPalette(builtInWrap, motionTip);
            ReloadRecipes();
        });
        AppAssets.Changed += _onAssetsChanged;

        _thumbTimer.Tick += (_, _) =>
        {
            // 움직이는 효과: 0 → 끝까지 돌리고 끝 모습을 잠깐 보여 준 뒤 처음부터.
            _thumbTick = (_thumbTick + 1) % (ThumbSteps + ThumbHold);
            int step = Math.Min(_thumbTick, ThumbSteps - 1);
            foreach (var tile in _tiles)
                if (tile.Animated) tile.Show(step, RenderThumb);
        };
        _thumbTimer.Start();

        int width = (TileWidth + 4) * PaletteColumns + 40; // + 탭 안쪽 여백과 세로 스크롤 막대
        // 스크롤 칸은 높이를 정하지 않고 탭 안 남은 자리를 채운다 — 정한 높이가 탭 내용 칸보다 크면 맨 아래 상자가 잘려 스크롤로도 닿지 않았다.
        ScrollViewer Scroll(Control content) => new()
        {
            Content = content,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        var customHead = Ui.Stack(_savePreset, manager, Ui.Label(UiStrings.EffectPreset_CustomHelp), _customEmpty);
        customHead.Margin = new Thickness(0);
        DockPanel.SetDock(customHead, Dock.Top);
        var custom = new DockPanel { Children = { customHead, Scroll(_customWrap) } };
        var tabs = new TabControl { Width = width, Height = 590 };
        tabs.Items.Add(new TabItem { Header = UiStrings.EffectPreset_BuiltInTab, Content = Scroll(Ui.Stack(builtInWrap, BuildScriptSection())) });
        tabs.Items.Add(new TabItem { Header = UiStrings.EffectPreset_CustomTab, Content = custom });
        return Ui.Stack(Ui.Row(Ui.Label(UiStrings.S_LayerEffectsDialog_Palette_BuildPalette_F3EEB77504, bold: true), Ui.InfoBadge(UiStrings.S_LayerEffectsDialog_Palette_BuildPalette_938E8B22DD)), tabs);
    }

    private void ReloadBuiltInPalette(WrapPanel builtInWrap, string? motionTip)
    {
        foreach (var tile in _tiles.Where(tile => tile.Entry is null).ToArray())
        {
            if (_pressedTile == tile) EndTileDrag();
            tile.Dispose();
            _tiles.Remove(tile);
        }
        builtInWrap.Children.Clear();
        foreach (var definition in AppAssets.Catalog.BuiltInEffects)
        {
            var ui = UiOf(definition);
            var tile = new EffectTile(ui.Name, [definition.Create()]);
            ToolTip.SetTip(tile, $"{ui.Name}\n{ui.Description}" + (tile.Animated ? UiStrings.S_LayerEffectsDialog_Palette_BuildPalette_164E2EA098 : "")
                                 + (tile.Animated && motionTip is not null ? " " + motionTip : ""));
            AddPaletteTile(tile, builtInWrap);
        }
    }

    private void AddPaletteTile(EffectTile tile, Panel panel)
    {
        tile.Width = TileWidth;
        tile.Height = TileHeight;
        tile.Margin = new Thickness(2);
        tile.Show(0, RenderThumb);
        AttachTileInput(tile);
        if (tile.Entry is { } entry)
            PopupMenus.Attach(tile, () => RecipeMenu(entry));
        _tiles.Add(tile);
        panel.Children.Add(tile);
    }

    /// <summary>카탈로그에서 레시피를 다시 읽어 커스텀 탭을 다시 만든다. 개인 에셋을 읽지 못했으면 그 까닭을 보인다.</summary>
    private void ReloadRecipes()
    {
        _recipes.Clear();
        _recipes.AddRange(AppAssets.Catalog.EffectTemplates);
        _customEmpty.Text = AppAssets.Store.PersonalError is { } error
            ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture, UiStrings.EffectPreset_LoadError, error)
            : UiStrings.EffectPreset_Empty;
        RebuildCustomPalette();
    }

    private void RebuildCustomPalette()
    {
        foreach (var tile in _tiles.Where(tile => tile.Entry is not null).ToArray())
        {
            tile.Dispose();
            _tiles.Remove(tile);
        }
        _customWrap.Children.Clear();
        var culture = global::System.Globalization.CultureInfo.CurrentCulture;
        foreach (var entry in _recipes)
        {
            var recipe = entry.EffectTemplate!;
            var tile = new EffectTile(recipe.Name, recipe.Effects, entry, recipe);
            string tip = (entry.Script is not null, entry.Origin == AssetOrigin.Personal) switch
            {
                (true, true) => UiStrings.EffectPreset_ScriptTileTip,
                (true, false) => string.Format(culture, UiStrings.EffectPreset_PackageScriptTileTip, entry.PackageName),
                (false, true) => string.Format(culture, UiStrings.EffectPreset_TileTip, recipe.Effects.Count),
                (false, false) => string.Format(culture, UiStrings.EffectPreset_PackageTileTip, entry.PackageName, recipe.Effects.Count),
            };
            if (entry.ScriptHashes.Any(hash => ScriptLibrary.TrustOf(hash) != ScriptTrust.Trusted))
                tip += "\n" + string.Format(culture, UiStrings.EffectPreset_ScriptTileNote, recipe.Effects.Count(effect => effect is ScriptEffect));
            ToolTip.SetTip(tile, tip);
            AddPaletteTile(tile, _customWrap);
        }
        _customEmpty.IsVisible = _recipes.Count == 0 || AppAssets.Store.PersonalError is not null;
    }

    private IEnumerable<object> RecipeMenu(AssetEntry entry)
    {
        MenuItem Item(string text, VectorIcon icon, Action action)
        {
            var item = new MenuItem { Header = text, Icon = Ui.Icon(icon, 14) };
            item.Click += (_, _) => Dispatcher.UIThread.Post(action);
            return item;
        }
        var show = Item(UiStrings.EffectPreset_ShowInManager, VectorIcon.Package, () => AssetManagerWindow.Open(this, entry.Kind, entry.Id));
        if (entry.Origin != AssetOrigin.Personal) return [show];
        // 스크립트 에셋의 이름은 원문의 선언이라 여기서 바꾸지 않는다
        if (entry.Script is not null) return [Item(UiStrings.EffectPreset_Delete, VectorIcon.Trash, () => DeletePreset(entry)), new Separator(), show];
        return
        [
            Item(UiStrings.EffectPreset_Rename, VectorIcon.Pencil, () => RenamePreset(entry)),
            Item(UiStrings.RecipeParams_Edit, VectorIcon.SlidersHorizontal, () => RecipeParametersDialog.Edit(this, entry)),
            Item(UiStrings.EffectPreset_Delete, VectorIcon.Trash, () => DeletePreset(entry)),
            new Separator(),
            show,
        ];
    }

    private IEnumerable<AssetEntry> PersonalRecipes => AppAssets.Catalog.Personal.Where(entry => entry.Recipe is not null);

    private void SavePresetGroup()
    {
        if (_effects.Count == 0) return;
        var personal = PersonalRecipes.ToList();
        var name = InputDialog.Ask(this, UiStrings.EffectPreset_SaveTitle, UiStrings.EffectPreset_NamePrompt,
            _effects[0].DisplayName, personal.Select(entry => entry.Name))?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var duplicate = personal.FirstOrDefault(entry => entry.Name == name);
        if (duplicate is not null && Ui.Ask(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,
                UiStrings.EffectPreset_OverwriteQuestion, name), UiStrings.EffectPreset_SaveTitle,
                MessageButtons.YesNo, MessageIcon.Question) != DialogResult.Yes) return;
        var recipe = EffectRecipe.Capture(duplicate?.Id ?? "", name, _effects, _project);
        PersistRecipe(() => AppAssets.Store.SavePersonalRecipe(recipe));
    }

    private void RenamePreset(AssetEntry entry)
    {
        if (entry.Recipe is not { } recipe) return;
        var personal = PersonalRecipes.ToList();
        var name = InputDialog.Ask(this, UiStrings.EffectPreset_RenameTitle, UiStrings.EffectPreset_NamePrompt,
            recipe.Name, personal.Select(item => item.Name))?.Trim();
        if (string.IsNullOrEmpty(name) || name == recipe.Name) return;
        if (personal.Any(item => item.Name == name))
        {
            Ui.Warn(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,
                UiStrings.EffectPreset_DuplicateName, name), UiStrings.EffectPreset_RenameTitle);
            return;
        }
        PersistRecipe(() => AppAssets.Store.SavePersonalRecipe(recipe with { Name = name }));
    }

    private void DeletePreset(AssetEntry entry)
    {
        if (Ui.Ask(this, string.Format(global::System.Globalization.CultureInfo.CurrentCulture,
                UiStrings.EffectPreset_DeleteQuestion, entry.Name), UiStrings.EffectPreset_Delete,
                MessageButtons.YesNo, MessageIcon.Question) != DialogResult.Yes) return;
        PersistRecipe(() => AppAssets.Store.RemovePersonal(entry.Id));
    }

    /// <summary>개인 레시피를 바꾼다. 목록은 저장소 변경 알림(<see cref="ReloadRecipes"/>)으로 다시 만들어진다.</summary>
    private void PersistRecipe(Action change) => AppAssets.Try(this, change, UiStrings.EffectPreset_SaveError);

    /// <summary>미리보기 원본: 지금 프레임 그림(투명 여백을 잘라 냄). 비었거나 너무 크면 작은 견본 그림.</summary>
    private PixelImage ThumbSource()
    {
        if (SourceAt(_frame) is not { } src) return SampleSprite();
        var bounds = src.GetOpaqueBounds();
        if (bounds.IsEmpty || bounds.Width > ThumbMaxSize || bounds.Height > ThumbMaxSize) return SampleSprite();
        return src.Crop(bounds);
    }

    private static PixelImage SampleSprite() => AssetPreview.SampleSprite();

    /// <summary>효과 묶음을 미리보기 원본에 입힌 그림 — 움직이는 효과는 <see cref="ThumbSteps"/>칸 중 <paramref name="step"/>번째.</summary>
    private PixelImage RenderThumb(IReadOnlyList<LayerEffect> template, int step) => AssetPreview.Render(template, _thumbSource, step);

    // ───────────── 두 번 누르기·끌어 놓기 ─────────────

    private void AttachTileInput(EffectTile tile)
    {
        tile.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2)
            {
                Add(tile.Effects, _effects.Count, tile.Template);
                return;
            }
            _pressedTile = tile;
            _pressPoint = e.GetPosition(this);
            _tileDragging = false;
            e.Pointer.Capture(tile);
        };
        tile.PointerMoved += (_, e) =>
        {
            if (_pressedTile != tile) return;
            if (!_tileDragging && ReorderCore.Exceeded(_pressPoint, e.GetPosition(this))) _tileDragging = true;
            if (!_tileDragging) return;
            bool over = DropIndex(e) is not null;
            tile.Cursor = new Cursor(over ? StandardCursorType.DragCopy : StandardCursorType.No);
            ShowDropTarget(over);
        };
        tile.PointerReleased += (_, e) =>
        {
            if (_pressedTile != tile) return;
            int? at = _tileDragging ? DropIndex(e) : null;
            EndTileDrag();
            e.Pointer.Capture(null);
            if (at is { } i) Add(tile.Effects, i, tile.Template);
        };
        tile.PointerCaptureLost += (_, _) =>
        {
            if (_pressedTile == tile) EndTileDrag();
        };
    }

    private void EndTileDrag()
    {
        if (_pressedTile is { } tile) tile.Cursor = new Cursor(StandardCursorType.Hand);
        _pressedTile = null;
        _tileDragging = false;
        ShowDropTarget(false);
    }

    /// <summary>포인터가 효과 목록 위면 넣을 효과 번호(그 위치보다 위에 있는 줄 다음 줄의 효과 — 접힌 묶음 앞뒤), 아니면 null.</summary>
    private int? DropIndex(PointerEventArgs e)
    {
        var p = e.GetPosition(_list);
        if (!new Rect(_list.Bounds.Size).Contains(p)) return null;
        int row = 0;
        foreach (var item in _list.Items.OfType<ListBoxItem>())
            if (item.TranslatePoint(new Avalonia.Point(0, item.Bounds.Height / 2), _list) is { } mid && mid.Y < p.Y) row++;
        return EffectIndexAtRow(row);
    }

    private void ShowDropTarget(bool on)
    {
        if (on)
        {
            _list.BorderBrush = AppTheme.AccentBrush;
            _list.BorderThickness = new Thickness(2);
        }
        else
        {
            _list.ClearValue(BorderBrushProperty);
            _list.ClearValue(BorderThicknessProperty);
        }
    }

    private void DisposePalette()
    {
        _thumbTimer.Stop();
        if (_onAssetsChanged is { } changed) AppAssets.Changed -= changed;
        foreach (var tile in _tiles) tile.Dispose();
    }

    /// <summary>효과 종류 상자: 위 = 미리보기(같은 정수 배율, 작으면 맞춤 축소), 아래 = 이름.</summary>
    private sealed class EffectTile : Control, IDisposable
    {
        private readonly WriteableBitmap?[] _frames;
        private int _shown;
        private bool _hover;

        public EffectTile(string name, IReadOnlyList<LayerEffect> effects, AssetEntry? entry = null, EffectRecipe? template = null)
        {
            Title = name;
            Effects = effects;
            Entry = entry;
            Template = template;
            _frames = new WriteableBitmap?[effects.Any(effect => effect is AnimatedEffect || effect.ColorGradients.Values.Any(gradient => gradient.Animated)) ? ThumbSteps : 1];
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        public string Title { get; }
        public IReadOnlyList<LayerEffect> Effects { get; }
        /// <summary>커스텀 탭의 에셋 항목(기본 효과 상자는 null).</summary>
        public AssetEntry? Entry { get; }
        /// <summary>넣을 레시피(에셋 항목의 <see cref="AssetEntry.EffectTemplate"/> — 스크립트 원문을 함께 복사함, 기본 효과 상자는 null).</summary>
        public EffectRecipe? Template { get; }
        public bool Animated => _frames.Length > 1;
        /// <summary><paramref name="step"/>번째 모습을 보인다(처음 볼 때 한 번만 그림).</summary>
        public void Show(int step, Func<IReadOnlyList<LayerEffect>, int, PixelImage> render)
        {
            step = Math.Clamp(step, 0, _frames.Length - 1);
            _frames[step] ??= ImageUtil.ToBitmap(render(Effects, step));
            if (_shown == step) return;
            _shown = step;
            InvalidateVisual();
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            base.OnPointerEntered(e);
            _hover = true;
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hover = false;
            InvalidateVisual();
        }

        public override void Render(DrawingContext g)
        {
            var b = new Rect(Bounds.Size);
            g.FillRectangle(_hover ? AppTheme.HoverBrush : AppTheme.PanelBrush, b);
            g.DrawRectangle(new Pen(AppTheme.BorderBrush, 1), b.Deflate(0.5));
            if (_frames[_shown] is { } bmp)
            {
                var area = new Rect(5, 5, b.Width - 10, b.Height - 28);
                var src = new Rectangle(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height);
                double fit = Math.Min(area.Width / src.Width, area.Height / src.Height);
                ImageUtil.DrawFitted(g, bmp, src, area, fit >= 1 ? (int)Math.Floor(fit) : 0, checker: true);
            }
            AppTheme.DrawTextIn(g, Title, new Rect(2, b.Height - 21, b.Width - 4, 18), AppTheme.TextBrush, AppTheme.SmallFontSize, center: true);
            // 스크립트 효과가 든 상자: 오른쪽 위에 코드 표시
            if (Template?.Scripts.Count > 0)
                VectorIcons.Draw(g, VectorIcon.Code, new Rect(b.Width - 17, 3, 14, 14), 12, AppTheme.DimTextBrush);
        }

        public void Dispose()
        {
            for (int i = 0; i < _frames.Length; i++)
            {
                _frames[i]?.Dispose();
                _frames[i] = null;
            }
        }
    }
}
