using Gpixel.App.Localization;
using Gpixel.Core.Scripting;

namespace Gpixel.App;

/// <summary>
/// 처음 보는 스크립트 효과 실행 확인(13.5): 스크립트마다 이름·작성자·원문 해시와 실행 체크(처음에 켬). "실행"은 켠 것만 실행하고 끈 것은 실행 안 함,
/// "실행 안 함"·창 닫기는 모두 실행 안 함으로 정한다. 결정은 <see cref="Decisions"/>(해시 → 실행)로 돌려준다.
/// 이름과 작성자는 원문을 실행하지 않고 읽은 값이다(<see cref="ScriptLibrary.InfoOf"/>).
/// </summary>
internal sealed class ScriptTrustDialog : DialogBase
{
    private const double TextWidth = 460;
    private static readonly FontFamily MonoFont = new("Menlo, Consolas, DejaVu Sans Mono, monospace");

    private readonly List<(string Hash, CheckBox Run)> _rows = [];

    public ScriptTrustDialog(IReadOnlyList<string> hashes) : base(UiStrings.ScriptTrust_Title, UiStrings.ScriptTrust_Run)
    {
        CancelButtonControl.Content = UiStrings.ScriptTrust_DontRun;
        var message = Ui.Label(UiStrings.ScriptTrust_Message);
        message.TextWrapping = TextWrapping.Wrap;
        message.MaxWidth = TextWidth;
        message.Margin = new Thickness(3, 0, 3, 8);
        Body.Children.Add(message);
        var list = new StackPanel { Spacing = 6 };
        foreach (var hash in hashes)
        {
            var info = ScriptLibrary.InfoOf(hash);
            var run = Ui.Check(info?.Name ?? UiStrings.ScriptTrust_Unnamed, true);
            run.FontWeight = FontWeight.Bold;
            var author = Ui.Label(info?.Author is { } a ? UiStrings.FormatScriptEffect_Author(author: a) : UiStrings.ScriptTrust_NoAuthor);
            author.Foreground = AppTheme.DimTextBrush;
            author.Margin = new Thickness(24, 0, 0, 0);
            var id = new TextBlock
            {
                Text = UiStrings.FormatScriptEffect_Hash(hash: hash),
                FontFamily = MonoFont,
                FontSize = AppTheme.SmallFontSize,
                Foreground = AppTheme.DimTextBrush,
                Margin = new Thickness(24, 0, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = TextWidth,
            };
            list.Children.Add(new StackPanel { Spacing = 1, Children = { run, author, id } });
            _rows.Add((hash, run));
        }
        Body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
    }

    /// <summary>해시 → 실행 여부. "실행"이면 체크한 것만 true, 아니면 모두 false.</summary>
    public IReadOnlyDictionary<string, bool> Decisions
        => _rows.ToDictionary(row => row.Hash, row => DialogResult == DialogResult.OK && row.Run.On(), StringComparer.Ordinal);
}
