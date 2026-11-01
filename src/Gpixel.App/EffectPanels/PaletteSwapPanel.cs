using Gpixel.Core.Edit;
using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름, 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi PaletteSwapUi => EffectUi.For<PaletteSwapEffect>(PaletteSwapDefinition.Id, UiStrings.S_LayerEffectsDialog_KindTable_20C2A2DCF6, UiStrings.S_LayerEffectsDialog_KindTable_4E3A3D50CE,
        Avalonia.Media.Color.FromRgb(150, 110, 230), (dialog, e, set) => dialog.PaletteSwapPanel(e, set),
        p => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_53B3F1206A,p.Pairs.Count) + (p.Tolerance > 0 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_E77A16477B,p.Tolerance) : ""));

    /// <summary>
    /// 색 바꾸기 설정: 쌍마다 [바꿀 색] → [바뀐 색] [−], 아래에 [+ 쌍 추가]·[그림에서 색 가져오기 ▾](이 프레임 그림에 쓰인 색, 많이 쓰인 순 — 누르면 그 색의 쌍 추가)와 허용 차이.
    /// 쌍을 더하거나 빼면 설정 칸을 다시 만든다.
    /// </summary>
    private void PaletteSwapPanel(PaletteSwapEffect p, Action<Func<LayerEffect, LayerEffect>> set)
    {
        static PaletteSwapEffect P(LayerEffect v) => (PaletteSwapEffect)v;
        void ChangePair(int at, Func<ColorSwap, ColorSwap> change)
            => set(o => P(o) with { Pairs = P(o).Pairs.Select((q, j) => j == at ? change(q) : q).ToArray() });
        void AddPairs(IEnumerable<Color32> colors)
        {
            set(o =>
            {
                var pairs = P(o).Pairs.ToList();
                foreach (var c in colors)
                    if (!pairs.Any(q => q.From.R == c.R && q.From.G == c.G && q.From.B == c.B)) pairs.Add(new ColorSwap(c, c));
                return P(o) with { Pairs = pairs };
            });
            ShowSettings();
        }

        var source = SourceAt(_frame);
        IReadOnlyList<Color32> used = source is null ? [] : PaletteRemap.UsedColors(source);

        if (p.Pairs.Count == 0) _settings.Children.Add(Ui.Label(UiStrings.S_PaletteSwapPanel_PaletteSwapPanel_3C34A2519A));
        for (int k = 0; k < p.Pairs.Count; k++)
        {
            int at = k;
            var from = new ColorField(p.Pairs[k].From, 64);
            var to = new ColorField(p.Pairs[k].To, 64);
            from.ColorChanged += (_, _) => ChangePair(at, q => q with { From = from.Color });
            to.ColorChanged += (_, _) => ChangePair(at, q => q with { To = to.Color });
            var remove = Ui.IconButton(VectorIcon.Minus, null, (_, _) =>
            {
                set(o => P(o) with
                {
                    Pairs = P(o).Pairs.Where((_, j) => j != at).ToArray(),
                    ColorGradients = RemoveIndexedGradient(o.ColorGradients, "Pairs", at)
                });
                ShowSettings();
            }, 26).Tip(UiStrings.S_PaletteSwapPanel_PaletteSwapPanel_A5869DACA1);
            SettingsRow(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_PaletteSwapPanel_PaletteSwapPanel_AF44DD45FB,k + 1), EffectColorEditor(p, $"Pairs[{at}].From", from, set));
            SettingsSubRow(UiStrings.S_PaletteSwapPanel_PaletteSwapPanel_9A0CDB44A5, EffectColorEditor(p, $"Pairs[{at}].To", to, set), remove);
        }

        var add = Ui.Button(VectorIcon.Plus, UiStrings.S_PaletteSwapPanel_PaletteSwapPanel_6A0B119B5E, (_, _) =>
        {
            // 아직 바꾸지 않는, 가장 많이 쓰인 색부터(없으면 검정)
            var next = used.FirstOrDefault(c => !P(_effects[Selected]).Pairs.Any(q => q.From.R == c.R && q.From.G == c.G && q.From.B == c.B), Color32.Black);
            AddPairs([next]);
        });
        var pick = Ui.Button(VectorIcon.ChevronDown, UiStrings.S_PaletteSwapPanel_PaletteSwapPanel_A0FD948BEA, iconAfter: true);
        if (used.Count == 0) pick.IsEnabled = false;
        else
        {
            var flyout = new Flyout();
            var panel = new StackPanel { Spacing = 4, MaxWidth = 280 };
            panel.Children.Add(Hint(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_PaletteSwapPanel_PaletteSwapPanel_F438B961E1,used.Count), 270));
            panel.Children.Add(ColorSourcePicker.Swatches(used, c =>
            {
                flyout.Hide();
                AddPairs([c]);
            }));
            panel.Children.Add(Ui.Button(UiStrings.S_PaletteSwapPanel_PaletteSwapPanel_F5BECD6FF3, (_, _) =>
            {
                flyout.Hide();
                AddPairs(used);
            }));
            flyout.Content = panel;
            pick.Flyout = flyout;
        }
        SettingsRow("", add, pick.Tip(used.Count == 0 ? UiStrings.S_PaletteSwapPanel_PaletteSwapPanel_303CA10FC1 : UiStrings.S_PaletteSwapPanel_PaletteSwapPanel_2DE0DA2A3A));
        SettingsRow(UiStrings.S_PaletteSwapPanel_PaletteSwapPanel_62C7E01DDC, NumEditor(0, PaletteSwapEffect.MaxTolerance, p.Tolerance, NumKinds.Tolerance, v => set(o => P(o) with { Tolerance = v })), Ui.Label(UiStrings.S_PaletteSwapPanel_PaletteSwapPanel_17E5DDE5F7));
        _settings.Children.Add(Hint(UiStrings.S_PaletteSwapPanel_PaletteSwapPanel_A4E05A1C7D
                                    + UiStrings.S_PaletteSwapPanel_PaletteSwapPanel_3C2DFB743B));
    }
}
