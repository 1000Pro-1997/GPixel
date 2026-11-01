using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>반사 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class ReflectionDefinition
{
    public const string Id = "gpixel.effect.reflection";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<ReflectionEffect>(Id, "reflection", ReflectionRender.Apply, s =>
    [
        s.Choice("direction", e => e.Direction, (e, v) => e with { Direction = v }),
        s.Int("gap", 0, ReflectionEffect.MaxGap, e => e.Gap, (e, v) => e with { Gap = v }),
        s.Int("height", 1, 100, e => e.Height, (e, v) => e with { Height = v }),
        s.Int("opacityStart", 0, 100, e => e.OpacityStart, (e, v) => e with { OpacityStart = v }),
        s.Int("opacityEnd", 0, 100, e => e.OpacityEnd, (e, v) => e with { OpacityEnd = v }),
        s.Bool("ditherFade", e => e.DitherFade, (e, v) => e with { DitherFade = v }),
        s.Color("tint", e => e.Tint, (e, v) => e with { Tint = v }, legacy: ["color"]),
        s.Int("wave", 0, ReflectionEffect.MaxWave, e => e.Wave, (e, v) => e with { Wave = v }),
    ], padding: ReflectionRender.Padding, clipsBySource: true,
        // 예전 파일의 반사 위·아래(above) — 방향이 없거나 틀렸으면 그것으로
        legacyFixup: (flat, e) => !EffectJson.TryEnum<ReflectionDirection>(flat["direction"], out _) && EffectJson.Bool(flat["above"]) == true
            ? e with { Direction = ReflectionDirection.Up }
            : e);
}
