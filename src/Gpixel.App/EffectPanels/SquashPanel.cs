using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름, 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi SquashUi => EffectUi.For<SquashEffect>(SquashDefinition.Id, UiStrings.S_LayerEffectsDialog_KindTable_792E7BADCE, UiStrings.S_LayerEffectsDialog_KindTable_7C6C1B6117,
        Avalonia.Media.Color.FromRgb(110, 200, 120), (dialog, e, set) => dialog.SquashPanel(e, set),
        q => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_963828451F,q.WidthAmount,q.HeightAmount,q.Bounces));

    /// <summary>늘었다 줄기 설정: 가로·세로 세기, 출렁임 횟수, 기준점, 먼저 늘어나는 쪽.</summary>
    private void SquashPanel(SquashEffect q, Action<Func<LayerEffect, LayerEffect>> set)
    {
        SquashEffect Q(LayerEffect v) => (SquashEffect)v;
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_3029688617, NumEditor(0, SquashEffect.MaxAmount, q.WidthAmount, NumKinds.Squash, v => set(o => Q(o) with { WidthAmount = v })), Ui.Label("%"),
            Ui.Label(UiStrings.S_LayerEffectsDialog_FillSettings_DA003AD5F5), NumEditor(0, SquashEffect.MaxAmount, q.HeightAmount, NumKinds.Squash, v => set(o => Q(o) with { HeightAmount = v })), Ui.Label("%"));
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_7D4825E02A, NumEditor(1, SquashEffect.MaxBounces, q.Bounces, NumKinds.Count, v => set(o => Q(o) with { Bounces = v })), Ui.Label(UiStrings.S_LayerEffectsDialog_FillSettings_9926E8F548));
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_4165F7C464, ChoiceEditor(Anchors, (int)q.Anchor, k => set(o => Q(o) with { Anchor = (SquashAnchor)k })));
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_4D51C71D99, ChoiceEditor([UiStrings.S_LayerEffectsDialog_FillSettings_4147FCFCA9, UiStrings.S_LayerEffectsDialog_FillSettings_B8F02D5B5D], q.StretchFirst ? 1 : 0, k => set(o => Q(o) with { StretchFirst = k == 1 })));
        AddMotionEditors(Selected, set);
        _settings.Children.Add(Hint(UiStrings.S_LayerEffectsDialog_FillSettings_9691881EA3));
    }
}
