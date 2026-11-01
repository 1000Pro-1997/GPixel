using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름, 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi BlinkUi => EffectUi.For<BlinkEffect>(BlinkDefinition.Id, UiStrings.S_LayerEffectsDialog_KindTable_510993AD9E, UiStrings.S_LayerEffectsDialog_KindTable_4E942C831D,
        Avalonia.Media.Color.FromRgb(200, 170, 255), (dialog, e, set) => dialog.BlinkPanel(e, set),
        blink => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_0080B38202) + (blink.Timing == BlinkTiming.Frames ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_855D00FD34,blink.OnFrames,blink.OffFrames) : string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_7D1229802B,blink.Count))
            + $" · {BlinkHides[(int)blink.Hide]}");

    private static readonly string[] BlinkHides = [UiStrings.S_BlinkPanel_BlinkHides_C91D2F76FA, UiStrings.S_BlinkPanel_BlinkHides_E8E64BF447, UiStrings.S_BlinkPanel_BlinkHides_0C5348FA14];

    /// <summary>깜빡이기 설정: 간격(프레임 수 / 범위 안 횟수), 숨김 방식(+ 반투명 불투명도).</summary>
    private void BlinkPanel(BlinkEffect e, Action<Func<LayerEffect, LayerEffect>> set)
    {
        static BlinkEffect B(LayerEffect v) => (BlinkEffect)v;
        var on = NumEditor(1, BlinkEffect.MaxFrames, e.OnFrames, NumKinds.Frames, v => set(o => B(o) with { OnFrames = v }));
        var off = NumEditor(1, BlinkEffect.MaxFrames, e.OffFrames, NumKinds.Frames, v => set(o => B(o) with { OffFrames = v }));
        var count = NumEditor(1, BlinkEffect.MaxCount, e.Count, NumKinds.Count, v => set(o => B(o) with { Count = v }));
        void Sync(BlinkTiming t)
        {
            on.IsEnabled = off.IsEnabled = t == BlinkTiming.Frames;
            count.IsEnabled = t == BlinkTiming.Count;
        }
        Sync(e.Timing);
        SettingsRow(UiStrings.S_BlinkPanel_BlinkPanel_6222AF2B15, ChoiceEditor([UiStrings.S_BlinkPanel_BlinkPanel_940515B7F4, UiStrings.S_BlinkPanel_BlinkPanel_ED3105570F], (int)e.Timing, k =>
        {
            Sync((BlinkTiming)k);
            set(o => B(o) with { Timing = (BlinkTiming)k });
        }));
        SettingsSubRow(UiStrings.S_BlinkPanel_BlinkPanel_18B5987758, on, Ui.Label(UiStrings.S_BlinkPanel_BlinkPanel_00D4A1F5DF), off, Ui.Label(UiStrings.S_BlinkPanel_BlinkPanel_00777F6ABC));
        SettingsSubRow(UiStrings.S_BlinkPanel_BlinkPanel_641BB97285, count, Ui.Label(UiStrings.S_BlinkPanel_BlinkPanel_731F9E2133));
        var opacity = NumEditor(0, 100, e.Opacity, NumKinds.Opacity, v => set(o => B(o) with { Opacity = v }));
        opacity.IsEnabled = e.Hide == BlinkHide.Translucent;
        SettingsRow(UiStrings.S_BlinkPanel_BlinkPanel_E2A918DFFF, ChoiceEditor(BlinkHides, (int)e.Hide, k =>
        {
            opacity.IsEnabled = (BlinkHide)k == BlinkHide.Translucent;
            set(o => B(o) with { Hide = (BlinkHide)k });
        }), opacity, Ui.Label("%"));
        AddMotionEditors(Selected, set, timing: false);
        _settings.Children.Add(Hint(UiStrings.S_BlinkPanel_BlinkPanel_BE811F0ADD
                                    + UiStrings.S_BlinkPanel_BlinkPanel_A857BB6D37));
    }
}
