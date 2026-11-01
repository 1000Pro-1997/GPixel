using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.App.Localization;

namespace Gpixel.App;

/// <summary>
/// 앱이 Core 효과 정의(<see cref="EffectRegistry"/>)에 같은 TypeId로 붙이는 표시 정보의 중앙 목록.
/// 효과마다 자기 패널 파일(<c>EffectPanels/XPanel.cs</c>)에 <see cref="EffectUi"/> 하나를 두고 <see cref="EffectUis"/>에 한 줄로 등록한다.
/// 효과 창 종류 칸·목록·설정 칸, 타임라인 효과 줄 색, AI 도구의 종류 이름·설명이 모두 여기서 나온다(순서는 Core 등록 순서).
/// </summary>
public sealed partial class LayerEffectsDialog
{
    /// <summary>효과 종류 하나의 앱 표시 정보: 현재 언어 이름·한 줄 설명, 타임라인 색, 설정 칸 만들기, 목록 요약 글.</summary>
    internal sealed record EffectUi(string TypeId, string Name, string Description, Avalonia.Media.Color TimelineColor,
        Action<LayerEffectsDialog, LayerEffect, Action<Func<LayerEffect, LayerEffect>>> Panel, Func<LayerEffect, string> Summary)
    {
        /// <summary>모델 타입 <typeparamref name="T"/>의 패널·요약 함수로 만든다.</summary>
        public static EffectUi For<T>(string typeId, string name, string description, Avalonia.Media.Color timelineColor,
            Action<LayerEffectsDialog, T, Action<Func<LayerEffect, LayerEffect>>> panel, Func<T, string> summary) where T : LayerEffect
            => new(typeId, name, description, timelineColor, (dialog, effect, set) => panel(dialog, (T)effect, set), effect => summary((T)effect));
    }

    /// <summary>모르는 효과(<see cref="MissingEffect"/>)의 타임라인 색.</summary>
    private static readonly Avalonia.Media.Color MissingColor = Avalonia.Media.Color.FromRgb(128, 128, 128);

    /// <summary>TypeId → 표시 정보. Core 등록 목록과 하나씩 맞아야 한다(빠지거나 남으면 처음 쓸 때 예외).</summary>
    private static readonly Dictionary<string, EffectUi> EffectUis = Index(
    [
        OutlineUi, ShadowUi, RimLightUi, ColorOverlayUi, InnerGlowUi, PaletteSwapUi, ColorAdjustUi, ReflectionUi,
        ExplodeUi, ShineUi, SquashUi, HitFlashUi, BlinkUi, ShakeUi, BobUi, AppearUi, FadeUi, MeltUi, RotateUi,
        AfterimageUi, ColorCycleUi, ShockwaveUi, HologramUi, ScriptUi,
    ]);

    private static Dictionary<string, EffectUi> Index(EffectUi[] uis)
    {
        var index = uis.ToDictionary(ui => ui.TypeId, StringComparer.Ordinal);
        var registered = EffectRegistry.All.Select(d => d.TypeId).ToHashSet(StringComparer.Ordinal);
        if (index.Count != uis.Length || !registered.SetEquals(index.Keys))
            throw new InvalidOperationException("Effect editors do not match EffectRegistry: "
                                                + string.Join(", ", registered.Except(index.Keys).Concat(index.Keys.Except(registered))));
        return index;
    }

    /// <summary>기본 효과 정의의 표시 정보.</summary>
    internal static EffectUi UiOf(EffectDefinition definition) => EffectUis[definition.TypeId];

    /// <summary>효과의 표시 정보(모르는 효과면 null).</summary>
    private static EffectUi? UiOf(LayerEffect effect) => EffectRegistry.Of(effect) is { } definition ? EffectUis[definition.TypeId] : null;

    /// <summary>타임라인 효과 줄의 효과 종류 색.</summary>
    internal static Avalonia.Media.Color TimelineColorOf(LayerEffect effect) => UiOf(effect)?.TimelineColor ?? MissingColor;

    /// <summary>모르는 효과 설명(종류 ID 포함): 그리지 않고 고칠 수 없으며 저장하면 그대로 남는다.</summary>
    private static string MissingNote(MissingEffect missing)
        => string.Format(global::System.Globalization.CultureInfo.CurrentCulture, UiStrings.EffectMissing_Note, missing.TypeId);

    private static string Summary(LayerEffect e)
        => (UiOf(e)?.Summary(e) ?? (e is MissingEffect missing ? $"{e.DisplayName} · {missing.TypeId}" : e.DisplayName))
           + (e.Frames is { } f ? string.Format(global::System.Globalization.CultureInfo.CurrentCulture, UiStrings.S_LayerEffectsDialog_Summary_58CF9C9156, f) : "");
}
