using System.Text.Json;
using System.Text.Json.Nodes;
using Gpixel.Core.Edit;
using Gpixel.Core.IO;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>색 덮기 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class ColorOverlayDefinition
{
    public const string Id = "gpixel.effect.coloroverlay";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<ColorOverlayEffect>(Id, "overlay", ColorOverlayRender.Apply, s =>
    [
        s.Color("color", e => e.Color, (e, v) => e with { Color = v }),
        s.Int("amount", 0, 100, e => e.Amount, (e, v) => e with { Amount = v }),
        s.Choice("blend", e => e.Blend, (e, v) => e with { Blend = v }),
    ],
        legacyAliases: new Dictionary<string, Func<JsonObject, LayerEffect>> { ["gradient"] = LegacyGradient });

    /// <summary>없어진 그라데이션 변화 효과("그라데이션 변화")의 기본 정지점.</summary>
    private static readonly GradientStop[] LegacyGradientMapStops =
        [new(0, new Color32(255, 230, 120)), new(1, new Color32(200, 60, 40))];

    /// <summary>
    /// 예전 "gradient" 효과 → 색 덮기. 시간 그라데이션(<c>gradient</c> 키)이 있으면 없어진 그라데이션 변화 효과, 없으면 예전 공간 그라데이션(색 A·B)이다.
    /// 색 그라데이션은 이 변환이 만든다(파일의 <c>colorGradients</c>는 예전 공간 그라데이션의 "Gradient" 칸만 쓴다).
    /// </summary>
    private static LayerEffect LegacyGradient(JsonObject flat)
    {
        if (flat["gradient"] is JsonObject temporal)
            return GradientMap(flat, temporal.Deserialize<ProjectIO.ColorGradientDto>(ProjectIO.Options)!);
        var colorA = EffectJson.ColorOr(flat["color"], LegacyGradientMapStops[0].Color);
        var colorB = EffectJson.ColorOr(flat["color2"], LegacyGradientMapStops[^1].Color);
        var oldDither = EffectJson.EnumOr(flat["dither"], EffectDither.None);
        var spatial = flat["colorGradients"] is JsonObject old && old["Gradient"] is JsonObject saved
            ? saved.Deserialize<ProjectIO.ColorGradientDto>(ProjectIO.Options)!.ToGradient()
            : new EffectColorGradient
            {
                // 예전 A/B의 직선 RGBA 보간·반올림을 그대로 유지한다.
                Stops = [new(0, colorA), new(1, colorB)],
                StraightRgba = true,
                ColorSpace = GradientColorSpace.Rgb,
                Angle = EffectJson.Int(flat["angle"]) ?? 270,
                Dither = oldDither switch
                {
                    EffectDither.Bayer2 => GradientDither.Bayer2,
                    EffectDither.Bayer4 => GradientDither.Bayer4,
                    _ => GradientDither.None,
                },
            };
        return new ColorOverlayEffect
        {
            Color = colorA,
            Amount = EffectJson.Int(flat["amount"]) ?? 100,
            Blend = EffectJson.EnumOr(flat["blend"], EffectBlend.Normal),
            ColorGradients = new Dictionary<string, EffectColorGradient> { [nameof(ColorOverlayEffect.Color)] = spatial },
        };
    }

    /// <summary>
    /// 없어진 그라데이션 변화 효과(시간 그라데이션) → 색 슬롯에 움직이는 그라데이션이 든 색 덮기. 색 바뀜은 Uniform 모양과 정확히 같다(같은 곡선·반복의 진행).
    /// 방사 회전은 움직이는 Angular 모양이 되며 한 주기에 늘 한 바퀴 돈다: 예전 전체 각도는 방향(음수 = 시계 방향)만 남고,
    /// ±360°가 아닌 각도와 예전 반복 횟수는 범위 동안 한 바퀴로 근사한다.
    /// </summary>
    private static ColorOverlayEffect GradientMap(JsonObject flat, ProjectIO.ColorGradientDto temporal)
    {
        var gradient = temporal.ToGradient();
        if (temporal.Stops is not { Length: > 0 }) gradient = gradient with { Stops = LegacyGradientMapStops };
        var repeat = EffectJson.EnumOr(flat["repeat"], EffectRepeat.Once);
        int repeatCount = Math.Clamp(EffectJson.Int(flat["repeatCount"]) ?? 1, 1, AnimatedEffect.MaxRepeatCount);
        var easing = EffectJson.EnumOr(flat["easing"], EffectEasing.Linear);
        bool radial = EffectJson.String(flat["mode"])?.Trim().Equals("RadialRotation", StringComparison.OrdinalIgnoreCase) == true;
        int angle = Math.Clamp(EffectJson.Int(flat["angle"]) ?? 360, -1440, 1440);
        gradient = radial
            ? gradient with
            {
                Shape = EffectGradientShape.Angular, Animated = true, Angle = 0, Reverse = angle < 0, Cycles = 1,
                Easing = easing, PingPong = repeat == EffectRepeat.PingPong,
            }
            : gradient with
            {
                Shape = EffectGradientShape.Uniform, Animated = true, Reverse = false,
                Cycles = repeat == EffectRepeat.Once ? 1 : repeatCount,
                Easing = easing, PingPong = repeat == EffectRepeat.PingPong,
            };
        return new ColorOverlayEffect
        {
            Color = gradient.Stops[0].Color,
            Amount = EffectJson.Int(flat["amount"]) ?? 100,
            Blend = EffectJson.EnumOr(flat["blend"], EffectBlend.Normal),
            ColorGradients = new Dictionary<string, EffectColorGradient> { [nameof(ColorOverlayEffect.Color)] = gradient },
        };
    }
}
