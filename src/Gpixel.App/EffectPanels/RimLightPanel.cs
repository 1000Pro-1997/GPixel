using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 창·타임라인 표시: 이름, 설명, 타임라인 색, 설정 칸, 목록 요약.</summary>
    private static EffectUi RimLightUi => EffectUi.For<RimLightEffect>(RimLightDefinition.Id, UiStrings.S_LayerEffectsDialog_KindTable_126136E630, UiStrings.S_LayerEffectsDialog_KindTable_4230F44F0C,
        Avalonia.Media.Color.FromRgb(226, 210, 110), (dialog, e, set) => dialog.RimLightPanel(e, set),
        r => string.Format(global::System.Globalization.CultureInfo.CurrentCulture,UiStrings.S_LayerEffectsDialog_Summary_0B62ABBEE2,Directions.First(d => d.Dir == r.Direction).Name,r.Size));

    /// <summary>가장자리 빛·그늘 설정: 빛 방향, 두께, 빛 색, 그늘 색.</summary>
    private void RimLightPanel(RimLightEffect r, Action<Func<LayerEffect, LayerEffect>> set)
    {
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_66BE69E8AB,
            ChoiceEditor(Directions.Select(d => d.Name).ToArray(), Array.FindIndex(Directions, d => d.Dir == r.Direction),
                k => set(x => (RimLightEffect)x with { Direction = Directions[k].Dir })));
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_3CA6157165, NumEditor(1, RimLightEffect.MaxSize, r.Size, NumKinds.Thickness,
            v => set(x => (RimLightEffect)x with { Size = v })), Ui.Label("px"));
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_1381B5CF89, EffectColorEditor(r, nameof(RimLightEffect.Light), ColorEditor(r.Light, c => set(x => (RimLightEffect)x with { Light = c })), set));
        SettingsRow(UiStrings.S_LayerEffectsDialog_FillSettings_6E13AD4474, EffectColorEditor(r, nameof(RimLightEffect.Shade), ColorEditor(r.Shade, c => set(x => (RimLightEffect)x with { Shade = c })), set));
        _settings.Children.Add(Hint(UiStrings.S_LayerEffectsDialog_FillSettings_0AB9179C30));
    }
}
