using System.Globalization;
using Gpixel.App.Localization;
using Gpixel.Core.Assets;
using Gpixel.Core.Effects;
using Gpixel.Core.IO;
using Gpixel.Core.Model;

namespace Gpixel.App;

/// <summary>
/// 레시피 매개변수 편집기(계획 14장 3단계): 작성자가 레시피 효과의 어떤 설정을 사용자에게 내보낼지 고르고, 이름·기본값·범위를 정한다.
/// 매개변수 하나에 같은 종류의 설정 여럿을 묶을 수 있고(색 → 외곽선 색 + 색 덮기 색), 정수 범위·고를 수 있는 값은 연결된 설정 schema의 겹침 안에서만 고른다.
/// 연결·범위 검사는 패키지 검사와 같은 <see cref="RecipeParameters.Check"/>로 하고, 문제가 있으면 확인을 막고 문제를 보인다.
/// </summary>
public sealed class RecipeParametersDialog : DialogBase
{
    private readonly IReadOnlyList<LayerEffect> _effects;
    private readonly List<RecipeParameter> _parameters;
    private readonly ListBox _list = new() { Width = 200, Height = 360, Margin = new Thickness(3) };
    private readonly StackPanel _editor = new() { Spacing = 2, Width = 430 };
    private readonly TextBlock _issues = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 640, Foreground = AppTheme.DimTextBrush };
    private readonly Button _remove;
    private bool _syncing;

    public RecipeParametersDialog(EffectRecipe recipe)
        : base(string.Format(CultureInfo.CurrentCulture, UiStrings.RecipeParams_Title, recipe.Name))
    {
        _effects = recipe.Effects;
        _parameters = [.. recipe.Parameters];
        _list.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            _remove!.IsEnabled = _list.SelectedIndex >= 0;
            ShowEditor();
        };
        var add = Ui.Button(VectorIcon.Plus, UiStrings.RecipeParams_Add, (_, _) => AddParameter());
        _remove = Ui.Button(VectorIcon.Trash, UiStrings.RecipeParams_Remove, (_, _) => RemoveParameter());
        var left = Ui.Stack(Ui.Label(UiStrings.RecipeParams_Parameters, bold: true), _list, Ui.Row(add, _remove));
        var right = new ScrollViewer
        {
            Content = _editor, MaxHeight = 460,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        var root = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        root.Children.Add(left);
        root.Children.Add(right);
        Body.Children.Add(root);
        Body.Children.Add(_issues);
        RebuildList(_parameters.Count > 0 ? 0 : -1);
    }

    /// <summary>확인했을 때의 매개변수(<see cref="EffectRecipe.WithParameters"/>에 넘김).</summary>
    public IReadOnlyList<RecipeParameter> Parameters => _parameters;

    /// <summary>개인 레시피의 매개변수를 고치고 확인하면 개인 에셋에 저장한다(효과 창 커스텀 탭·에셋 관리자 상세에서).</summary>
    public static void Edit(Visual owner, AssetEntry entry)
    {
        if (entry is not { Origin: AssetOrigin.Personal, Recipe: { } recipe }) return;
        var dialog = new RecipeParametersDialog(recipe);
        if (dialog.ShowDialog(owner) != DialogResult.OK) return;
        AppAssets.Try(owner, () => AppAssets.Store.SavePersonalRecipe(recipe.WithParameters(dialog.Parameters)), UiStrings.RecipeParams_SaveError);
    }

    private void RebuildList(int select)
    {
        _syncing = true;
        _list.Items.Clear();
        foreach (var parameter in _parameters) _list.Items.Add(new ListBoxItem { Content = parameter.Name });
        _list.SelectedIndex = Math.Clamp(select, -1, _parameters.Count - 1);
        _syncing = false;
        _remove.IsEnabled = _list.SelectedIndex >= 0;
        ShowEditor();
    }

    private void AddParameter()
    {
        int n = 1;
        while (_parameters.Any(p => p.Id == $"p{n}")) n++;
        // 종류·기본값은 첫 설정을 연결할 때 그 설정에서 정한다
        _parameters.Add(new RecipeParameter
        {
            Id = $"p{n}", Name = string.Format(CultureInfo.CurrentCulture, UiStrings.RecipeParams_NewName, n),
            Kind = RecipeParameterKind.Color, Default = RecipeValue.Of(Color32.Black),
        });
        RebuildList(_parameters.Count - 1);
    }

    private void RemoveParameter()
    {
        int k = _list.SelectedIndex;
        if (k < 0) return;
        _parameters.RemoveAt(k);
        RebuildList(Math.Min(k, _parameters.Count - 1));
    }

    private static TextBlock Dim(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Foreground = AppTheme.DimTextBrush, Margin = new Thickness(3, 0, 3, 2),
    };

    /// <summary>매개변수 종류 글(정수는 범위 포함).</summary>
    internal static string KindText(RecipeParameter parameter) => parameter.Kind switch
    {
        RecipeParameterKind.Color => UiStrings.RecipeParams_KindColor,
        RecipeParameterKind.Int => string.Format(CultureInfo.CurrentCulture, UiStrings.RecipeParams_KindInt, parameter.Min, parameter.Max),
        RecipeParameterKind.Bool => UiStrings.RecipeParams_KindBool,
        _ => UiStrings.RecipeParams_KindChoice,
    };

    private static string KindText(EffectParameter setting) => setting.Kind switch
    {
        EffectParameterKind.Color => UiStrings.RecipeParams_KindColor,
        EffectParameterKind.Int => string.Format(CultureInfo.CurrentCulture, UiStrings.RecipeParams_KindInt, setting.Min, setting.Max),
        EffectParameterKind.Bool => UiStrings.RecipeParams_KindBool,
        _ => UiStrings.RecipeParams_KindChoice,
    };

    /// <summary>고른 매개변수의 이름, 연결할 설정(레시피 효과마다 내보낼 수 있는 설정), 기본값·범위·고를 수 있는 값.</summary>
    private void ShowEditor()
    {
        _editor.Children.Clear();
        int k = _list.SelectedIndex;
        if (k < 0 || k >= _parameters.Count)
        {
            _editor.Children.Add(Dim(_parameters.Count == 0 ? UiStrings.RecipeParams_Empty : UiStrings.RecipeParams_Select));
            Validate();
            return;
        }
        var parameter = _parameters[k];

        var name = Ui.Text(parameter.Name, 240);
        name.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            _parameters[k] = _parameters[k] with { Name = (name.Text ?? "").Trim() };
            if (_list.Items[k] is ListBoxItem item) item.Content = _parameters[k].Name;
            Validate();
        };
        _editor.Children.Add(Ui.Row(Ui.Label(UiStrings.RecipeParams_Name), name));

        _editor.Children.Add(Ui.Label(UiStrings.RecipeParams_Settings, bold: true));
        _editor.Children.Add(Dim(UiStrings.RecipeParams_SettingsHint));
        var settings = new StackPanel { Spacing = 0 };
        for (int e = 0; e < _effects.Count; e++)
        {
            if (EffectRegistry.Of(_effects[e]) is not { } definition) continue;
            string effectName = LayerEffectsDialog.UiOf(definition).Name;
            foreach (var setting in definition.Parameters.Where(RecipeParameters.CanExpose))
            {
                var binding = new RecipeBinding(e, setting.Key);
                int owner = _parameters.FindIndex(p => p.Bindings.Contains(binding));
                var check = Ui.Check($"{e + 1}. {effectName} · {setting.Key} ({KindText(setting)})", owner == k);
                check.IsEnabled = owner == k || (owner < 0 && (parameter.Bindings.Count == 0 || RecipeParameters.KindOf(setting.Kind) == parameter.Kind));
                if (owner >= 0 && owner != k)
                    ToolTip.SetTip(check, string.Format(CultureInfo.CurrentCulture, UiStrings.RecipeParams_UsedBy, _parameters[owner].Name));
                check.IsCheckedChanged += (_, _) =>
                {
                    if (!_syncing) Link(k, binding, setting, check.IsChecked == true);
                };
                settings.Children.Add(check);
            }
        }
        _editor.Children.Add(new ScrollViewer { Content = settings, MaxHeight = 220, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });

        if (parameter.Bindings.Count > 0) AddValueEditors(k);
        Validate();
    }

    /// <summary>
    /// 설정 연결을 더하거나 뺀다. 첫 연결이 매개변수 종류와 기본값(그 설정의 지금 값)·범위·고를 수 있는 값을 정하고,
    /// 더 잇는 설정은 범위·고를 수 있는 값을 겹침으로 좁힌다.
    /// </summary>
    private void Link(int k, RecipeBinding binding, EffectParameter setting, bool on)
    {
        var parameter = _parameters[k];
        if (!on)
            parameter = parameter with { Bindings = parameter.Bindings.Where(b => b != binding).ToArray() };
        else if (parameter.Bindings.Count == 0)
            parameter = parameter with
            {
                Kind = RecipeParameters.KindOf(setting.Kind)!.Value,
                Default = RecipeParameters.ValueOf(_effects[binding.Effect], setting.Key)!.Value,
                Min = setting.Min ?? 0, Max = setting.Max ?? 0, Choices = setting.Choices ?? [],
                Bindings = [binding],
            };
        else
        {
            parameter = parameter with { Bindings = [.. parameter.Bindings, binding] };
            if (parameter.Kind == RecipeParameterKind.Int && RecipeParameters.IntRange(_effects, parameter.Bindings) is var (min, max))
            {
                int low = Math.Clamp(parameter.Min, min, max), high = Math.Clamp(parameter.Max, low, max);
                parameter = parameter with { Min = low, Max = high, Default = RecipeValue.Of(Math.Clamp(parameter.Default.Int, low, high)) };
            }
            if (parameter.Kind == RecipeParameterKind.Choice)
            {
                var common = RecipeParameters.CommonChoices(_effects, parameter.Bindings);
                var choices = parameter.Choices.Where(c => common.Contains(c, StringComparer.Ordinal)).ToArray();
                parameter = parameter with
                {
                    Choices = choices,
                    Default = choices.Contains(parameter.Default.Choice, StringComparer.Ordinal) || choices.Length == 0 ? parameter.Default : RecipeValue.OfChoice(choices[0]),
                };
            }
        }
        _parameters[k] = parameter;
        ShowEditor();
    }

    private void AddValueEditors(int k)
    {
        var parameter = _parameters[k];
        void Set(Func<RecipeParameter, RecipeParameter> change)
        {
            _parameters[k] = change(_parameters[k]);
            Validate();
        }

        switch (parameter.Kind)
        {
            case RecipeParameterKind.Color:
                var color = new ColorField(parameter.Default.Color);
                color.ColorChanged += (_, _) => Set(p => p with { Default = RecipeValue.Of(color.Color) });
                _editor.Children.Add(Ui.Row(Ui.Label(UiStrings.RecipeParams_Default), color));
                break;
            case RecipeParameterKind.Bool:
                var flag = Ui.Check("", parameter.Default.Bool);
                flag.IsCheckedChanged += (_, _) => Set(p => p with { Default = RecipeValue.Of(flag.IsChecked == true) });
                _editor.Children.Add(Ui.Row(Ui.Label(UiStrings.RecipeParams_Default), flag));
                break;
            case RecipeParameterKind.Int:
                var (low, high) = RecipeParameters.IntRange(_effects, parameter.Bindings) ?? (parameter.Min, parameter.Max);
                var min = Ui.Num(low, high, Math.Clamp(parameter.Min, low, high), 64);
                var max = Ui.Num(low, high, Math.Clamp(parameter.Max, low, high), 64);
                var value = Ui.Num(low, high, Math.Clamp(parameter.Default.Int, low, high), 64);
                void Range()
                {
                    int a = (int)min.NumValue(), b = (int)max.NumValue();
                    value.Minimum = Math.Min(a, b);
                    value.Maximum = Math.Max(a, b);
                    Set(p => p with { Min = a, Max = b, Default = RecipeValue.Of((int)value.NumValue()) });
                }
                min.ValueChanged += (_, _) => Range();
                max.ValueChanged += (_, _) => Range();
                value.ValueChanged += (_, _) => Set(p => p with { Default = RecipeValue.Of((int)value.NumValue()) });
                _editor.Children.Add(Ui.Row(Ui.Label(UiStrings.RecipeParams_Range), min, Ui.Label("~"), max));
                _editor.Children.Add(Ui.Row(Ui.Label(UiStrings.RecipeParams_Default), value));
                break;
            case RecipeParameterKind.Choice:
                var common = RecipeParameters.CommonChoices(_effects, parameter.Bindings);
                var picks = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 420 };
                foreach (string choice in common)
                {
                    var pick = Ui.Check(choice, parameter.Choices.Contains(choice, StringComparer.Ordinal));
                    pick.IsCheckedChanged += (_, _) =>
                    {
                        // 고를 수 있는 값은 연결된 설정의 순서를 따른다
                        var chosen = common.Where(c => c == choice ? pick.IsChecked == true : _parameters[k].Choices.Contains(c, StringComparer.Ordinal)).ToArray();
                        var current = _parameters[k].Default.Choice;
                        _parameters[k] = _parameters[k] with
                        {
                            Choices = chosen,
                            Default = chosen.Contains(current, StringComparer.Ordinal) || chosen.Length == 0 ? _parameters[k].Default : RecipeValue.OfChoice(chosen[0]),
                        };
                        ShowEditor();
                    };
                    picks.Children.Add(pick);
                }
                _editor.Children.Add(Ui.Label(UiStrings.RecipeParams_Choices));
                _editor.Children.Add(picks);
                var options = parameter.Choices.ToArray();
                if (options.Length > 0)
                {
                    var selected = Ui.Combo(170, options);
                    selected.SelectedIndex = Math.Max(0, Array.IndexOf(options, parameter.Default.Choice));
                    selected.SelectionChanged += (_, _) =>
                    {
                        if (selected.SelectedIndex >= 0) Set(p => p with { Default = RecipeValue.OfChoice(options[selected.SelectedIndex]) });
                    };
                    _editor.Children.Add(Ui.Row(Ui.Label(UiStrings.RecipeParams_Default), selected));
                }
                break;
        }
    }

    /// <summary>패키지 검사와 같은 규칙으로 검사해 문제를 보이고, 문제가 있으면 확인을 막는다.</summary>
    private void Validate()
    {
        var messages = new List<string>();
        RecipeParameters.Check(_effects, _parameters, (code, detail) => messages.Add(new AssetIssue(code, null, detail).Message));
        _issues.Text = string.Join(Environment.NewLine, messages.Take(6));
        _issues.IsVisible = messages.Count > 0;
        OkButton.IsEnabled = messages.Count == 0;
    }
}
