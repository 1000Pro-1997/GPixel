using Gpixel.Core.Edit;
using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

/// <summary>
/// 효과 목록의 줄과 매개변수 레시피 묶음(계획 14장 3단계). 레이어 효과 목록은 그리기 순서 그대로 평평하고, 같은 묶음
/// (<see cref="EffectRecipeInstance"/>)에 속한 이어진 효과들은 목록에서 한 줄(묶음 머리)로 보인다. 머리를 고르면 매개변수만 보이고,
/// 값을 바꾸면 연결된 효과 설정이 모두 함께 바뀐다(<see cref="RecipeInstances.WithValue"/>). 펼치면 구성 효과가 머리 아래 줄로 나와 따로 고칠 수 있다.
/// 접힌 묶음은 옮기기·지우기·영구 적용에서 한 덩어리다.
/// </summary>
public sealed partial class LayerEffectsDialog
{
    /// <summary>
    /// 목록 한 줄: 효과 하나(<see cref="Recipe"/> = null, <see cref="First"/> = <see cref="Last"/> = 효과 번호)
    /// 또는 묶음 머리(<see cref="First"/>~<see cref="Last"/> = 목록에 이어진 구성 효과). <see cref="Member"/> = 펼친 묶음의 구성 효과 줄.
    /// </summary>
    private readonly record struct ListRow(int First, int Last, EffectRecipeInstance? Recipe, bool Member);

    private readonly List<ListRow> _rows = [];
    /// <summary>끌어 옮길 수 있는 줄의 효과 범위(<see cref="DragReorder"/> 등록 순서 — 펼친 묶음 머리는 빠짐).</summary>
    private readonly List<(int First, int Last)> _units = [];
    /// <summary>펼쳐 둔 묶음 ID(창이 열린 동안).</summary>
    private readonly HashSet<string> _expandedRecipes = new(StringComparer.Ordinal);

    private ListRow? SelectedRow => _list.SelectedIndex is var r && r >= 0 && r < _rows.Count ? _rows[r] : null;

    /// <summary>고른 효과 하나의 번호(묶음 머리를 골랐거나 고른 것이 없으면 -1).</summary>
    private int Selected => SelectedRow is { Recipe: null } row ? row.First : -1;

    /// <summary>고른 줄의 효과 범위(머리면 구성 효과 전부). 고른 것이 없으면 null.</summary>
    private (int First, int Last)? SelectedBlock => SelectedRow is { } row ? (row.First, row.Last) : null;

    /// <summary>효과 <paramref name="index"/>가 속한, 목록에 이어진 같은 묶음의 범위(묶음이 아니면 그 효과 하나).</summary>
    private (int First, int Last) RunAt(int index)
    {
        if (_effects[index].Recipe is not { } link) return (index, index);
        string id = link.Instance.Id;
        int first = index, last = index;
        while (first > 0 && _effects[first - 1].Recipe?.Instance.Id == id) first--;
        while (last + 1 < _effects.Count && _effects[last + 1].Recipe?.Instance.Id == id) last++;
        return (first, last);
    }

    /// <summary>옮기기에서 한 칸으로 보는 범위: 접힌 묶음은 묶음 전체, 그 밖은 효과 하나.</summary>
    private (int First, int Last) StepAt(int index)
        => _effects[index].Recipe is { } link && !_expandedRecipes.Contains(link.Instance.Id) ? RunAt(index) : (index, index);

    private void RebuildList(int select, bool header = false)
    {
        _syncing = true;
        _drag.Clear();
        _list.Items.Clear();
        _rows.Clear();
        _units.Clear();
        for (int i = 0; i < _effects.Count; i++)
        {
            if (_effects[i].Recipe is { } link)
            {
                var (first, last) = RunAt(i);
                bool expanded = _expandedRecipes.Contains(link.Instance.Id);
                AddRow(new ListRow(first, last, link.Instance, false), draggable: !expanded);
                if (expanded)
                    for (int k = first; k <= last; k++)
                        AddRow(new ListRow(k, k, null, true), draggable: true);
                i = last;
                continue;
            }
            AddRow(new ListRow(i, i, null, false), draggable: true);
        }
        int row = _rows.FindIndex(r => r.First == select && (r.Recipe is not null) == header);
        if (row < 0) row = _rows.FindIndex(r => r.Recipe is not null && r.First <= select && select <= r.Last);
        _list.SelectedIndex = select < 0 || _rows.Count == 0 ? -1 : row >= 0 ? row : Math.Min(select, _rows.Count - 1);
        _syncing = false;
        RefreshScriptMarks();
        ShowSettings();
        Changed();
        SyncPlayback(restart: SelectedRow is not null);
    }

    private void AddRow(ListRow row, bool draggable)
    {
        _rows.Add(row);
        var item = new ListBoxItem { Content = row.Recipe is { } recipe ? HeaderRow(row, recipe) : EffectRow(row) };
        _list.Items.Add(item);
        if (!draggable) return;
        _units.Add((row.First, row.Last));
        _drag.Attach(item);
    }

    private StackPanel EffectRow(ListRow listRow)
    {
        int at = listRow.First;
        var effect = _effects[at];
        var check = new CheckBox { IsChecked = effect.Enabled, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        check.IsCheckedChanged += (_, _) =>
        {
            if (_syncing || at >= _effects.Count) return;
            // 다시 켜면 연속 실패로 멈춘 스크립트 효과도 다시 계산한다
            if (check.IsChecked == true) ResumeScript(at);
            Update(at, _effects[at] with { Enabled = check.IsChecked == true });
        };
        var text = new TextBlock { Text = EffectSummary(effect), VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
        if (listRow.Member) row.Margin = new Thickness(18, 0, 0, 0); // 묶음 머리 아래 구성 효과
        if (effect is MissingEffect missing)
        {
            // 모르는 효과: 그리지 않고 고칠 수 없다(켜기 상자도 잠금). 순서 바꾸기·지우기만 된다.
            check.IsEnabled = false;
            text.Foreground = AppTheme.DimTextBrush;
            ToolTip.SetTip(row, MissingNote(missing));
        }
        row.Children.Add(check);
        if (effect.ClipToPrevious)
            row.Children.Add(Ui.Icon(VectorIcon.CornerDownRight, 12));
        row.Children.Add(text);
        return row;
    }

    /// <summary>묶음 머리 줄: 켜기 상자(구성 효과 모두), 펼치기 표시, 레시피 이름과 효과 수.</summary>
    private StackPanel HeaderRow(ListRow listRow, EffectRecipeInstance recipe)
    {
        var check = new CheckBox
        {
            IsChecked = Enumerable.Range(listRow.First, listRow.Last - listRow.First + 1).Any(i => _effects[i].Enabled),
            Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        check.IsCheckedChanged += (_, _) =>
        {
            if (_syncing) return;
            SetGroupEnabled(listRow.First, listRow.Last, check.IsChecked == true);
        };
        bool expanded = _expandedRecipes.Contains(recipe.Id);
        var chevron = Ui.Icon(expanded ? VectorIcon.ChevronDown : VectorIcon.ChevronRight, 12);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        chevron.Cursor = new Cursor(StandardCursorType.Hand);
        chevron.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(chevron).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            Dispatcher.UIThread.Post(() => ToggleRecipeExpanded(recipe.Id, listRow.First));
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Spacing = 3 };
        row.Children.Add(check);
        row.Children.Add(chevron);
        row.Children.Add(Ui.Icon(VectorIcon.Layers, 12));
        row.Children.Add(new TextBlock { Text = GroupSummary(listRow, recipe), VerticalAlignment = VerticalAlignment.Center });
        ToolTip.SetTip(row, UiStrings.EffectRecipe_GroupTip);
        return row;
    }

    private string GroupSummary(ListRow row, EffectRecipeInstance recipe)
    {
        string text = string.Format(System.Globalization.CultureInfo.CurrentCulture, UiStrings.EffectRecipe_GroupRow, recipe.Name, row.Last - row.First + 1);
        // 구성 효과의 프레임 범위가 모두 같으면 범위도 보인다
        var frames = _effects[row.First].Frames;
        bool same = Enumerable.Range(row.First, row.Last - row.First + 1).All(i => _effects[i].Frames == frames);
        return same && frames is { } f
            ? text + string.Format(System.Globalization.CultureInfo.CurrentCulture, UiStrings.S_LayerEffectsDialog_Summary_58CF9C9156, f)
            : text;
    }

    private void ToggleRecipeExpanded(string id, int first)
    {
        if (!_expandedRecipes.Remove(id)) _expandedRecipes.Add(id);
        RebuildList(first, header: true);
    }

    /// <summary>묶음의 구성 효과를 모두 켜거나 끈다.</summary>
    private void SetGroupEnabled(int first, int last, bool enabled)
    {
        for (int i = first; i <= last && i < _effects.Count; i++)
            if (_effects[i].Enabled != enabled)
                ReplaceEffect(i, _effects[i] with { Enabled = enabled });
        RefreshRows();
        Changed();
        SyncPlayback();
    }

    /// <summary>줄 글자와 켜기 상자를 효과 값에 다시 맞춘다(줄 구성은 그대로).</summary>
    private void RefreshRows()
    {
        _syncing = true;
        for (int r = 0; r < _rows.Count && r < _list.Items.Count; r++)
        {
            if (_list.Items[r] is not ListBoxItem { Content: StackPanel panel }) continue;
            var row = _rows[r];
            if (panel.Children.OfType<CheckBox>().FirstOrDefault() is { } check)
                check.IsChecked = row.Recipe is null
                    ? _effects[row.First].Enabled
                    : Enumerable.Range(row.First, row.Last - row.First + 1).Any(i => _effects[i].Enabled);
            if (panel.Children.OfType<TextBlock>().FirstOrDefault() is { } text)
                text.Text = row.Recipe is { } recipe ? GroupSummary(row, recipe) : EffectSummary(_effects[row.First]);
        }
        _syncing = false;
    }

    /// <summary>효과 <paramref name="index"/>를 보이는 효과 줄 번호(접힌 묶음 안이면 -1).</summary>
    private int RowOfEffect(int index) => _rows.FindIndex(r => r.Recipe is null && r.First == index);

    /// <summary>목록 줄 번호 <paramref name="row"/> 앞에 넣을 효과 번호(끝이면 효과 수).</summary>
    private int EffectIndexAtRow(int row) => row >= 0 && row < _rows.Count ? _rows[row].First : _effects.Count;

    // ───────────── 옮기기 ─────────────

    /// <summary>
    /// 효과 범위 [<paramref name="first"/>, <paramref name="last"/>]를 꺼내 남은 목록의 <paramref name="at"/>번째(꺼낸 뒤 번호)에 넣는다.
    /// 클리핑은 효과 하나 옮기기와 같다: 범위에 기준 효과(클리핑되지 않은 효과)가 있으면 바로 뒤에 붙어 있던 클리핑 효과를 풀고, 맨 앞으로 오면 첫 효과의 클리핑을 푼다.
    /// </summary>
    private void MoveBlock(int first, int last, int at)
    {
        bool anchor = false;
        for (int i = first; i <= last; i++) anchor |= !_effects[i].ClipToPrevious;
        if (anchor) DetachClippedFollowers(last);
        if (at == 0 && _effects[first].ClipToPrevious) ReplaceEffect(first, _effects[first] with { ClipToPrevious = false });
        var block = _effects.GetRange(first, last - first + 1);
        _effects.RemoveRange(first, block.Count);
        _effects.InsertRange(at, block);
    }

    /// <summary>위·아래 단추: 고른 줄(묶음 머리면 묶음 전체)을 옆 칸(접힌 묶음은 한 칸) 너머로 옮긴다.</summary>
    private void Move(int delta)
    {
        if (SelectedBlock is not (var first, var last) || SelectedRow is not { } row) return;
        int neighbor = delta < 0 ? first - 1 : last + 1;
        if (neighbor < 0 || neighbor >= _effects.Count) return;
        var (nFirst, nLast) = StepAt(neighbor);
        int at = delta < 0 ? nFirst : nLast - (last - first);
        MoveBlock(first, last, at);
        RebuildList(at, header: row.Recipe is not null);
    }

    /// <summary>끌어 옮기기(<see cref="_units"/> 번호): 접힌 묶음은 통째로 옮긴다.</summary>
    private void MoveUnit(int from, int to)
    {
        if (from < 0 || from >= _units.Count || to < 0 || to >= _units.Count) return;
        var (first, last) = _units[from];
        bool header = _rows.Any(r => r.Recipe is not null && r.First == first && r.Last == last && !_expandedRecipes.Contains(r.Recipe.Id));
        var target = _units[to];
        int at = to < from ? target.First : target.Last - (last - first);
        MoveBlock(first, last, at);
        RebuildList(at, header);
    }

    // ───────────── 묶음 설정 ─────────────

    /// <summary>묶음 머리를 골랐을 때의 설정 칸: 레시피 이름·출처, 매개변수 편집기, 펼치기·묶음 풀기.</summary>
    private void FillRecipeSettings(ListRow row, EffectRecipeInstance recipe)
    {
        _settings.Children.Add(Ui.Label(recipe.Name, bold: true));
        if (recipe.SourceId is { } source)
        {
            // 출처 표시만: 개인 에셋이면 그렇게, 설치된 패키지에 아직 있으면 패키지 이름, 없으면 원래 ID(넣은 효과는 출처 없이도 편집된다)
            string where = source.StartsWith(Gpixel.Core.Assets.AssetStore.PersonalIdPrefix, StringComparison.Ordinal) ? UiStrings.Assets_SourcePersonal
                : AppAssets.Catalog.Find(source) is { PackageName: { } package } entry
                    ? string.Format(System.Globalization.CultureInfo.CurrentCulture, UiStrings.Assets_SourcePackage, package, entry.PackageVersion)
                    : source;
            var origin = Ui.Label(string.Format(System.Globalization.CultureInfo.CurrentCulture, UiStrings.EffectRecipe_Source, where));
            origin.Foreground = AppTheme.DimTextBrush;
            _settings.Children.Add(origin);
        }
        var hint = Ui.Label(UiStrings.EffectRecipe_PanelHint);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.MaxWidth = SettingsWidth - 10;
        hint.Foreground = AppTheme.DimTextBrush;
        _settings.Children.Add(hint);

        foreach (var parameter in recipe.Parameters)
        {
            var value = RecipeInstances.ValueOf(_effects, recipe, parameter);
            void Set(RecipeValue next) => SetRecipeValue(recipe.Id, parameter.Id, next);
            switch (parameter.Kind)
            {
                case RecipeParameterKind.Color:
                    SettingsRow(parameter.Name, ColorEditor(value.Color, c => Set(RecipeValue.Of(c))));
                    break;
                case RecipeParameterKind.Int:
                    var number = Ui.Num(parameter.Min, parameter.Max, value.Int, 56);
                    number.ValueChanged += (_, _) => Set(RecipeValue.Of((int)number.NumValue()));
                    SettingsRow(parameter.Name, number, Ui.Label($"{parameter.Min}~{parameter.Max}"));
                    break;
                case RecipeParameterKind.Bool:
                    SettingsRow(parameter.Name, CheckEditor("", value.Bool, b => Set(RecipeValue.Of(b))));
                    break;
                case RecipeParameterKind.Choice:
                    var choices = parameter.Choices.ToArray();
                    SettingsRow(parameter.Name, ChoiceEditor(choices, Array.IndexOf(choices, value.Choice), k => Set(RecipeValue.OfChoice(choices[k]))));
                    break;
            }
        }

        bool expanded = _expandedRecipes.Contains(recipe.Id);
        var toggle = Ui.Button(expanded ? VectorIcon.ChevronDown : VectorIcon.ChevronRight,
            expanded ? UiStrings.EffectRecipe_Collapse : UiStrings.EffectRecipe_Expand, (_, _) => ToggleRecipeExpanded(recipe.Id, row.First));
        var detach = Ui.Button(UiStrings.EffectRecipe_Detach, (_, _) => DetachRecipe(recipe.Id, row.First));
        ToolTip.SetTip(detach, UiStrings.EffectRecipe_DetachTip);
        SettingsRow("", toggle, detach);
    }

    /// <summary>매개변수 값을 바꾼다: 연결된 구성 효과 설정을 모두 함께 바꾸고 캔버스·미리보기에 반영한다(확인하면 창 전체가 실행취소 한 단계).</summary>
    private void SetRecipeValue(string instanceId, string parameterId, RecipeValue value)
    {
        var next = RecipeInstances.WithValue(_effects, instanceId, parameterId, value);
        if (ReferenceEquals(next, _effects)) return;
        for (int i = 0; i < next.Count; i++)
            if (!ReferenceEquals(next[i], _effects[i])) ReplaceEffect(i, next[i]);
        RefreshRows();
        Changed();
        SyncPlayback();
    }

    /// <summary>묶음 풀기: 구성 효과를 지금 값 그대로 보통 효과로 둔다(매개변수는 사라짐).</summary>
    private void DetachRecipe(string instanceId, int first)
    {
        var next = RecipeInstances.Detach(_effects, instanceId);
        for (int i = 0; i < next.Count; i++)
            if (!ReferenceEquals(next[i], _effects[i])) ReplaceEffect(i, next[i]);
        _expandedRecipes.Remove(instanceId);
        RebuildList(first);
    }
}
