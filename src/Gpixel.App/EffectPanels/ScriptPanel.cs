using System.Globalization;
using Gpixel.App.Localization;
using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.Core.Scripting;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름(스크립트 선언의 이름), 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi ScriptUi => EffectUi.For<ScriptEffect>(ScriptDefinition.Id, UiStrings.ScriptEffect_Name, UiStrings.ScriptEffect_Description,
        Avalonia.Media.Color.FromRgb(176, 144, 236), (dialog, e, set) => dialog.ScriptPanel(e, set), e => e.DisplayName);

    /// <summary>고른 스크립트 효과의 상태 칸(실행 상태가 바뀌면 이것만 다시 채운다 — 설정 칸을 다시 만들면 끄는 중인 슬라이더가 끊긴다).</summary>
    private StackPanel? _scriptStatus;

    /// <summary>
    /// 스크립트 효과 설정: 이름·작성자·해시, 실행 상태(오류 줄·프레임당 시간, 허용·다시 실행 단추), 편집·파일 열기 단추,
    /// 스크립트 schema로 만든 설정 칸(정수·실수 슬라이더, 켜고 끔, 색, 고르기), 움직이는 스크립트면 빠르기 곡선·반복.
    /// </summary>
    private void ScriptPanel(ScriptEffect s, Action<Func<LayerEffect, LayerEffect>> set)
    {
        var info = ScriptLibrary.InfoOf(s.ScriptHash);
        var head = Ui.Label(s.DisplayName, bold: true);
        var meta = Ui.Label((info?.Author is { } author ? UiStrings.FormatScriptEffect_Author(author: author) + " · " : "")
                            + UiStrings.FormatScriptEffect_Hash(hash: s.ScriptHash.Length > 12 ? s.ScriptHash[..12] : s.ScriptHash));
        meta.Foreground = AppTheme.DimTextBrush;
        _settings.Children.Add(Ui.Stack(head, meta));
        _scriptStatus = new StackPanel { Spacing = 2, Margin = new Thickness(3, 2, 3, 4) };
        _settings.Children.Add(_scriptStatus);
        RefreshScriptStatus();
        var edit = Ui.Button(VectorIcon.Pencil, UiStrings.ScriptEffect_Edit, (_, _) => OpenScriptEditor(Selected));
        edit.IsEnabled = ScriptLibrary.SourceOf(s.ScriptHash) is not null;
        var open = Ui.Button(VectorIcon.FolderOpen, UiStrings.ScriptEffect_OpenFile, (_, _) => OpenScriptFile(Selected));
        ToolTip.SetTip(open, UiStrings.ScriptEffect_ReplaceFileTip);
        var save = Ui.Button(VectorIcon.Package, UiStrings.ScriptEffect_SaveAsset, (_, _) => SaveScriptAsset(s));
        bool trusted = ScriptLibrary.TrustOf(s.ScriptHash) == ScriptTrust.Trusted;
        save.IsEnabled = edit.IsEnabled && trusted;
        // 꺼진 단추도 풍선이 보이게 감싼다
        var saveHolder = new Border { Child = save, Background = Brushes.Transparent };
        ToolTip.SetTip(saveHolder, trusted ? UiStrings.ScriptEffect_SaveAssetTip : UiStrings.ScriptEffect_SaveAssetNeedsTrust);
        _settings.Children.Add(Ui.Row(edit, open));
        _settings.Children.Add(Ui.Row(saveHolder));

        if (ScriptLibrary.Runnable(s.ScriptHash) is not { } program)
        {
            _settings.Children.Add(Hint(UiStrings.ScriptEffect_Hint));
            return;
        }
        var values = program.NormalizeSettings(s.Settings);
        void SetValue(string key, object value) => set(o => o is ScriptEffect e && ScriptLibrary.Runnable(e.ScriptHash) is { } p
            ? e with { Settings = ScriptSettingValues.Normalized(p, e.Settings.With(key, value)) }
            : o);
        if (program.Settings.Count == 0) _settings.Children.Add(Hint(UiStrings.ScriptEffect_NoSettings));
        foreach (var setting in program.Settings)
        {
            var key = setting.Key;
            switch (setting.Type)
            {
                case ScriptSettingType.Int:
                case ScriptSettingType.Number:
                    SettingsRow(setting.Label, ScriptNumberEditor(setting, Convert.ToDouble(values[key], CultureInfo.InvariantCulture), v => SetValue(key, v)));
                    break;
                case ScriptSettingType.Bool:
                    SettingsRow(setting.Label, CheckEditor("", (bool)values[key], v => SetValue(key, v)));
                    break;
                case ScriptSettingType.Color:
                    SettingsRow(setting.Label, ColorEditor((Color32)values[key], v => SetValue(key, v)));
                    break;
                default:
                    var choices = setting.Choices;
                    int index = Math.Max(0, choices.ToList().FindIndex(c => c.Value == (string)values[key]));
                    SettingsRow(setting.Label, ChoiceEditor(choices.Select(c => c.Label).ToArray(), index, k => SetValue(key, choices[k].Value)));
                    break;
            }
        }
        if (program.Animated) AddMotionEditors(Selected, set);
        _settings.Children.Add(Hint(UiStrings.ScriptEffect_Hint));
    }

    /// <summary>
    /// 정수·실수 설정: 범위가 있으면 슬라이더 + 숫자 칸(값을 바꿀 때마다 반영), 없으면 숫자 칸만. 실수는 <c>step</c>의 소수 자리까지(최대 4자리).
    /// </summary>
    private static Control ScriptNumberEditor(ScriptSetting setting, double value, Action<double> changed)
    {
        bool whole = setting.Type == ScriptSettingType.Int;
        const double Limit = 1e9;
        double min = Math.Max(-Limit, setting.Min ?? -Limit), max = Math.Min(Limit, setting.Max ?? Limit);
        double step = setting.Step ?? (whole ? 1 : 0.01);
        int decimals = whole ? 0 : Math.Clamp((int)Math.Ceiling(-Math.Log10(step) - 1e-9), 0, 4);
        var num = Ui.Num((decimal)min, (decimal)max, (decimal)Math.Clamp(value, min, max), 64, decimals, (decimal)step);
        if (setting.Min is null || setting.Max is null || max <= min)
        {
            num.ValueChanged += (_, _) => changed((double)num.NumValue());
            return num;
        }
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            Width = 150,
            SmallChange = step,
            LargeChange = Math.Max(step, (max - min) / 8),
            TickFrequency = step,
            IsSnapToTickEnabled = setting.Step is not null || whole,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0),
        };
        bool syncing = false;
        num.ValueChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            slider.Value = (double)num.NumValue();
            syncing = false;
            changed((double)num.NumValue());
        };
        slider.ValueChanged += (_, _) =>
        {
            if (syncing) return;
            double v = Math.Round(slider.Value, decimals);
            syncing = true;
            num.Value = (decimal)v;
            syncing = false;
            changed(v);
        };
        return Ui.Row(slider, num);
    }

    /// <summary>고른 스크립트 효과의 상태 칸과 열린 편집 창의 상태 줄, 목록의 오류 표시를 지금 상태로 채운다.</summary>
    private void RefreshScriptStatus()
    {
        RefreshScriptMarks();
        var effect = Selected >= 0 && Selected < _effects.Count ? VirtualEffects()[Selected] as ScriptEffect : null;
        var status = effect is null ? null : ScriptEffectRuntime.StatusOf(_layer, effect);
        if (_scriptEditor is not null && _scriptEditorTarget is ScriptEffect target && IndexOfEffect(target) is var at and >= 0)
        {
            var editing = ScriptEffectRuntime.StatusOf(_layer, (ScriptEffect)VirtualEffects()[at]);
            _scriptEditor.ShowStatus(ScriptStatusText.Of(editing), editing.IsProblem);
        }
        if (_scriptStatus is null || status is null) return;
        _scriptStatus.Children.Clear();
        var text = new TextBlock
        {
            Text = ScriptStatusText.Of(status),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = SettingsWidth - 30,
            Foreground = status.IsProblem ? AppTheme.WarningBrush : AppTheme.DimTextBrush,
        };
        _scriptStatus.Children.Add(status.IsProblem ? Ui.Row(WarningIcon(), text) : text);
        var hash = effect!.ScriptHash;
        switch (status.State)
        {
            case ScriptEffectState.NotTrusted:
                _scriptStatus.Children.Add(Ui.Row(Ui.Button(VectorIcon.Play, UiStrings.ScriptEffect_Allow, (_, _) =>
                {
                    ScriptTrustStore.Decide(new Dictionary<string, bool> { [hash] = true });
                    ScriptTrustStore.Prewarm([hash]);
                    ShowSettings();
                })));
                break;
            case ScriptEffectState.Suspended:
                int index = Selected;
                _scriptStatus.Children.Add(Ui.Row(Ui.Button(VectorIcon.Refresh, UiStrings.ScriptEffect_Resume, (_, _) => ResumeScript(index))));
                break;
        }
    }
}

/// <summary>스크립트 효과 상태·오류의 화면 글(효과 창·편집 창·목록 풍선).</summary>
internal static class ScriptStatusText
{
    /// <summary>오류 하나: 위치가 있으면 "줄 3, 칸 5: …".</summary>
    public static string Error(ScriptError error) => error switch
    {
        { Line: > 0, Column: > 0 } => UiStrings.FormatScriptEffect_ErrorAt(line: error.Line, column: error.Column, message: error.Message),
        { Line: > 0 } => UiStrings.FormatScriptEffect_ErrorAtLine(line: error.Line, message: error.Message),
        _ => error.Message,
    };

    /// <summary>상태 설명(문제면 원인과 오류, 아니면 프레임당 계산 시간).</summary>
    public static string Of(ScriptEffectStatus status)
    {
        string errors = string.Join("\n", status.Errors.Select(Error));
        string With(string head) => errors.Length == 0 ? head : head + "\n" + errors;
        return status.State switch
        {
            ScriptEffectState.MissingSource => UiStrings.ScriptEffect_MissingSource,
            ScriptEffectState.NotTrusted => UiStrings.ScriptEffect_NotTrusted,
            ScriptEffectState.Invalid => With(UiStrings.ScriptEffect_Invalid),
            ScriptEffectState.Failed => With(UiStrings.ScriptEffect_Failed),
            ScriptEffectState.Suspended => With(UiStrings.FormatScriptEffect_Suspended(count: ScriptEffectRuntime.MaxConsecutiveFailures)),
            _ => status.LastMs is { } ms
                ? UiStrings.FormatScriptEffect_Time(ms: ms.ToString("0.0", CultureInfo.CurrentCulture))
                : UiStrings.ScriptEffect_NotRunYet,
        };
    }
}
