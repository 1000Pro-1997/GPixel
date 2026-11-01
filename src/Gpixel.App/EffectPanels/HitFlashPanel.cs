using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름, 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi HitFlashUi => EffectUi.For<HitFlashEffect>(HitFlashDefinition.Id, UiStrings.S_LayerEffectsDialog_KindTable_1725C3F28A, UiStrings.S_LayerEffectsDialog_KindTable_993BDE3846,
        Avalonia.Media.Color.FromRgb(250, 250, 250), (dialog, e, set) => dialog.HitFlashPanel(e, set),
        hit => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_A9AB403D93,hit.Count,hit.Strength,hit.Color.ToHex()) + (hit.Shape == HitFlashShape.Fade ? UiStrings.S_LayerEffectsDialog_Summary_1BF53EA7E5 : ""));

    private static readonly Color32 HitRed = new(255, 40, 40);

    /// <summary>피격 번쩍임 설정: 색(흰색·빨강 단추), 세기, 횟수, 모양, 공통 움직임.</summary>
    private void HitFlashPanel(HitFlashEffect e, Action<Func<LayerEffect, LayerEffect>> set)
    {
        static HitFlashEffect H(LayerEffect v) => (HitFlashEffect)v;
        var color = ColorEditor(e.Color, v => set(o => H(o) with { Color = v }));
        SettingsRow(UiStrings.S_HitFlashPanel_HitFlashPanel_87C0FCC7BF, EffectColorEditor(e, nameof(HitFlashEffect.Color), color, set));
        SettingsSubRow(UiStrings.S_HitFlashPanel_HitFlashPanel_22EFA1DDB7, Ui.Button(UiStrings.S_HitFlashPanel_HitFlashPanel_A0E4CF80CE, (_, _) => color.Color = Color32.White, 44),
            Ui.Button(UiStrings.S_HitFlashPanel_HitFlashPanel_18EE8BF22A, (_, _) => color.Color = HitRed, 44));
        SettingsRow(UiStrings.S_HitFlashPanel_HitFlashPanel_99AEB41746, NumEditor(0, 100, e.Strength, NumKinds.Strength, v => set(o => H(o) with { Strength = v })), Ui.Label("%"),
            Ui.Label(UiStrings.S_HitFlashPanel_HitFlashPanel_0327FF94F2), NumEditor(1, HitFlashEffect.MaxCount, e.Count, NumKinds.Count, v => set(o => H(o) with { Count = v })), Ui.Label(UiStrings.S_HitFlashPanel_HitFlashPanel_229E2DBA32));
        SettingsRow(UiStrings.S_HitFlashPanel_HitFlashPanel_9FA01E0FEA, ChoiceEditor([UiStrings.S_HitFlashPanel_HitFlashPanel_B8D805778F, UiStrings.S_HitFlashPanel_HitFlashPanel_9185899AD1], (int)e.Shape, k => set(o => H(o) with { Shape = (HitFlashShape)k })));
        AddMotionEditors(Selected, set);
        _settings.Children.Add(Hint(UiStrings.S_HitFlashPanel_HitFlashPanel_5F33353139));
    }
}
