using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름, 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi BobUi => EffectUi.For<BobEffect>(BobDefinition.Id, UiStrings.S_LayerEffectsDialog_KindTable_F5A6D8375C, UiStrings.S_LayerEffectsDialog_KindTable_361470DFA9,
        Avalonia.Media.Color.FromRgb(160, 236, 214), (dialog, e, set) => dialog.BobPanel(e, set),
        bob => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_BBAD57A076,(bob.Axis == EffectAxis.Horizontal ? UiStrings.S_LayerEffectsDialog_Summary_3722634F0B : UiStrings.S_LayerEffectsDialog_Summary_EFCA029C51),bob.Height,bob.Cycles));

    /// <summary>둥실둥실 설정: 높이, 횟수, 방향(위아래 / 좌우).</summary>
    private void BobPanel(BobEffect e, Action<Func<LayerEffect, LayerEffect>> set)
    {
        static BobEffect B(LayerEffect v) => (BobEffect)v;
        SettingsRow(UiStrings.S_BobPanel_BobPanel_58A5A73C75, NumEditor(1, BobEffect.MaxHeight, e.Height, NumKinds.Px, v => set(o => B(o) with { Height = v })), Ui.Label("px"),
            Ui.Label(UiStrings.S_BobPanel_BobPanel_93ECFDAAAF), NumEditor(1, BobEffect.MaxCycles, e.Cycles, NumKinds.Count, v => set(o => B(o) with { Cycles = v })), Ui.Label(UiStrings.S_BobPanel_BobPanel_62AE6AF173));
        SettingsRow(UiStrings.S_BobPanel_BobPanel_817E586669, ChoiceEditor([UiStrings.S_BobPanel_BobPanel_178B12E7C6, UiStrings.S_BobPanel_BobPanel_EC9342821C], e.Axis == EffectAxis.Horizontal ? 1 : 0,
            k => set(o => B(o) with { Axis = k == 1 ? EffectAxis.Horizontal : EffectAxis.Vertical })));
        AddMotionEditors(Selected, set);
        _settings.Children.Add(Hint(UiStrings.S_BobPanel_BobPanel_A229A39E90));
    }
}
