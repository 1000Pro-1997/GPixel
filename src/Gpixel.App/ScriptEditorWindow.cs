using Gpixel.App.Localization;
using Gpixel.Core.Scripting;

namespace Gpixel.App;

/// <summary>
/// 스크립트 효과 원문 편집 창(13.6, 효과 창에서 띄움 — 효과 창이 닫히면 같이 닫힘): 고정폭 글 상자, "적용", 문법·선언 오류 목록(줄·칸, 누르면 그 자리로),
/// 지금 미리보기의 실행 상태(실행 오류 줄, 프레임당 계산 시간). 적용은 효과 창이 맡는다(<c>apply</c>가 오류 목록을 돌려줌 — 비었으면 적용됨).
/// </summary>
internal sealed class ScriptEditorWindow : Window
{
    private static readonly FontFamily MonoFont = new("Menlo, Consolas, DejaVu Sans Mono, monospace");

    private readonly TextBox _code;
    private readonly StackPanel _errors = new() { Spacing = 2 };
    private readonly TextBlock _result, _status;
    private readonly Func<string, IReadOnlyList<ScriptError>> _apply;

    public ScriptEditorWindow(string name, string source, Func<string, IReadOnlyList<ScriptError>> apply)
    {
        _apply = apply;
        Title = UiStrings.FormatScriptEditor_Title(name: name);
        Width = 760;
        Height = 640;
        MinWidth = 480;
        MinHeight = 320;
        CanResize = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.BackBrush;

        _code = new TextBox
        {
            Text = ScriptEffectProgram.NormalizeSource(source),
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = MonoFont,
            FontSize = 13,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_code, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_code, ScrollBarVisibility.Auto);
        var hint = Ui.Label(UiStrings.ScriptEditor_Hint);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Foreground = AppTheme.DimTextBrush;
        _result = Ui.Label("");
        _status = Ui.Label("");
        _status.TextWrapping = TextWrapping.Wrap;
        var applyButton = Ui.Button(UiStrings.ScriptEditor_Apply, (_, _) => Apply(), 90);
        ToolTip.SetTip(applyButton, Ui.Shortcut(UiStrings.ScriptEditor_ApplyTip));
        var close = Ui.Button(UiStrings.S_Ui_CloseOnly_11312F9A81, (_, _) => Close(), 90);
        var buttons = Ui.Row(applyButton, close, _result);
        var bottom = Ui.Stack(new ScrollViewer { Content = _errors, MaxHeight = 120 }, _status, buttons);
        bottom.Margin = new Thickness(0, 6, 0, 0);
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        Content = new DockPanel { Margin = new Thickness(12), Children = { hint, bottom, _code } };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Ui.HasCommand(e.KeyModifiers))
            {
                Apply();
                e.Handled = true;
            }
        };
        Opened += (_, _) => _code.Focus();
    }

    /// <summary>지금 원문(정리 전).</summary>
    public string Source => _code.Text ?? "";

    /// <summary>미리보기에서 본 실행 상태 글(실행 오류 줄·프레임당 시간). 빈 글이면 숨긴다.</summary>
    public void ShowStatus(string text, bool problem)
    {
        _status.Text = text;
        _status.IsVisible = text.Length > 0;
        _status.Foreground = problem ? AppTheme.WarningBrush : AppTheme.DimTextBrush;
    }

    private void Apply() => ShowErrors(_apply(Source), applied: true);

    /// <summary>
    /// 오류 목록을 보이고 첫 오류 자리로 커서를 옮긴다. <paramref name="applied"/>면 방금 적용한 결과로 "적용했습니다"·"오류 n개"를 함께 보인다.
    /// </summary>
    public void ShowErrors(IReadOnlyList<ScriptError> errors, bool applied = false)
    {
        _errors.Children.Clear();
        foreach (var error in errors)
        {
            var row = new Button
            {
                Content = new TextBlock { Text = ScriptStatusText.Error(error), TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.WarningBrush },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(3, 1),
            };
            row.Click += (_, _) => GoTo(error.Line, error.Column);
            _errors.Children.Add(row);
        }
        _result.Text = errors.Count > 0 ? UiStrings.FormatScriptEditor_Errors(count: errors.Count) : applied ? UiStrings.ScriptEditor_Applied : "";
        _result.Foreground = errors.Count == 0 ? AppTheme.DimTextBrush : AppTheme.WarningBrush;
        if (errors.Count > 0) GoTo(errors[0].Line, errors[0].Column);
    }

    /// <summary>원문의 <paramref name="line"/>줄 <paramref name="column"/>칸(1부터, 0이면 줄 처음)으로 커서를 옮긴다.</summary>
    private void GoTo(int line, int column)
    {
        if (line < 1) return;
        var text = _code.Text ?? "";
        int at = 0;
        for (int l = 1; l < line && at < text.Length; l++)
        {
            int next = text.IndexOf('\n', at);
            if (next < 0) break;
            at = next + 1;
        }
        int end = text.IndexOf('\n', at);
        if (end < 0) end = text.Length;
        _code.CaretIndex = Math.Min(end, at + Math.Max(0, column - 1));
        _code.Focus();
    }
}
