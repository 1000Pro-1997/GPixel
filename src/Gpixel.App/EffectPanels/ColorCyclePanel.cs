using Gpixel.Core.Edit;
using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름, 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi ColorCycleUi => EffectUi.For<ColorCycleEffect>(ColorCycleDefinition.Id, UiStrings.S_LayerEffectsDialog_KindTable_23A9E57A2F, UiStrings.S_LayerEffectsDialog_KindTable_1ABE2A05EE,
        Avalonia.Media.Color.FromRgb(230, 60, 120), (dialog, e, set) => dialog.ColorCyclePanel(e, set),
        cycle => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_055B04E8E0,cycle.Colors.Count,cycle.Steps) + (cycle.Reverse ? UiStrings.S_LayerEffectsDialog_Summary_D6E8941524 : ""));

    /// <summary>
    /// 색 순환 설정: 순환할 색(2~8개, 색마다 [−]) + [+ 색 추가]·[그림에서 색 가져오기 ▾](이 프레임 그림에 쓰인 색 — 누르면 그 색 추가), 칸 수, 방향.
    /// 색을 더하거나 빼면 설정 칸을 다시 만든다.
    /// </summary>
    private void ColorCyclePanel(ColorCycleEffect e, Action<Func<LayerEffect, LayerEffect>> set)
    {
        static ColorCycleEffect C(LayerEffect v) => (ColorCycleEffect)v;
        static bool SameRgb(Color32 a, Color32 b) => a.R == b.R && a.G == b.G && a.B == b.B;
        void AddColors(IEnumerable<Color32> colors)
        {
            set(o =>
            {
                var list = C(o).Colors.ToList();
                foreach (var c in colors)
                    if (list.Count < ColorCycleEffect.MaxColors && !list.Any(x => SameRgb(x, c))) list.Add(c);
                return C(o) with { Colors = list };
            });
            ShowSettings();
        }

        var source = SourceAt(_frame);
        IReadOnlyList<Color32> used = source is null ? [] : PaletteRemap.UsedColors(source);

        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        for (int k = 0; k < e.Colors.Count; k++)
        {
            int at = k;
            var field = new ColorField(e.Colors[k], 64);
            field.ColorChanged += (_, _) => set(o => C(o) with { Colors = C(o).Colors.Select((c, j) => j == at ? field.Color : c).ToArray() });
            var remove = Ui.IconButton(VectorIcon.Minus, null, (_, _) =>
            {
                set(o =>
                {
                    var colors = C(o).Colors.Where((_, j) => j != at).ToArray();
                    return C(o) with { Colors = colors, ColorGradients = RemoveIndexedGradient(o.ColorGradients, "Colors", at) };
                });
                ShowSettings();
            }, 26).Tip(UiStrings.S_ColorCyclePanel_ColorCyclePanel_616BE6A276);
            remove.IsEnabled = e.Colors.Count > ColorCycleEffect.MinColors;
            row.Children.Add(Ui.Row(Ui.Label($"{k + 1}"), EffectColorEditor(e, $"Colors[{at}]", field, set), remove));
        }
        SettingsBlock(UiStrings.S_ColorCyclePanel_ColorCyclePanel_8143B75ED5, row);

        bool full = e.Colors.Count >= ColorCycleEffect.MaxColors;
        var add = Ui.Button(VectorIcon.Plus, UiStrings.S_ColorCyclePanel_ColorCyclePanel_2547771D95, (_, _) =>
        {
            var next = used.FirstOrDefault(c => !C(_effects[Selected]).Colors.Any(x => SameRgb(x, c)), Color32.Black);
            AddColors([next]);
        });
        add.IsEnabled = !full;
        var pick = Ui.Button(VectorIcon.ChevronDown, UiStrings.S_ColorCyclePanel_ColorCyclePanel_8226D4FEF2, iconAfter: true);
        if (used.Count == 0 || full) pick.IsEnabled = false;
        else
        {
            var flyout = new Flyout();
            var panel = new StackPanel { Spacing = 4, MaxWidth = 280 };
            panel.Children.Add(Hint(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ColorCyclePanel_ColorCyclePanel_01F3077949,used.Count,ColorCycleEffect.MaxColors), 270));
            panel.Children.Add(ColorSourcePicker.Swatches(used, c =>
            {
                flyout.Hide();
                AddColors([c]);
            }));
            flyout.Content = panel;
            pick.Flyout = flyout;
        }
        SettingsRow("", add, pick.Tip(used.Count == 0 ? UiStrings.S_ColorCyclePanel_ColorCyclePanel_0EEDA7D564 : full ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ColorCyclePanel_ColorCyclePanel_0B215E7023,ColorCycleEffect.MaxColors) : UiStrings.S_ColorCyclePanel_ColorCyclePanel_445502367F));
        SettingsRow(UiStrings.S_ColorCyclePanel_ColorCyclePanel_6FEDC6C546, NumEditor(1, ColorCycleEffect.MaxSteps, e.Steps, StepsKind(e.Colors.Count), v => set(o => C(o) with { Steps = v })), Ui.Label(UiStrings.S_ColorCyclePanel_ColorCyclePanel_83D3EFAB2B));
        SettingsRow(UiStrings.S_ColorCyclePanel_ColorCyclePanel_CABADA1123, ChoiceEditor([UiStrings.S_ColorCyclePanel_ColorCyclePanel_F617CA003E, UiStrings.S_ColorCyclePanel_ColorCyclePanel_642EDB4627], e.Reverse ? 1 : 0, k => set(o => C(o) with { Reverse = k == 1 })));
        AddMotionEditors(Selected, set);
        _settings.Children.Add(Hint(string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ColorCyclePanel_ColorCyclePanel_68BE16C6B6,e.Colors.Count)));
    }

    /// <summary>칸 수 예시: 1과 색 개수의 배수(범위 끝이 처음과 같아 되풀이해도 이어짐 — "N바퀴"로 보임).</summary>
    private static NumKind StepsKind(int colors)
    {
        var values = new SortedSet<decimal> { 1 };
        for (int k = 1; values.Count < 12 && k * colors <= ColorCycleEffect.MaxSteps; k++) values.Add(k * colors);
        return new("effect.cycle.steps", UiStrings.S_ColorCyclePanel_StepsKind_1B32549397, [.. values], v => v % colors == 0 ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ColorCyclePanel_StepsKind_B56274D955,v,v / colors) : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_ColorCyclePanel_StepsKind_AD2B00B33A,v));
    }
}
