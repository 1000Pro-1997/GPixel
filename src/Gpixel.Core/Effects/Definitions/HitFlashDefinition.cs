using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>피격 번쩍임 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class HitFlashDefinition
{
    public const string Id = "gpixel.effect.hitflash";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<HitFlashEffect>(Id, "hitflash", HitFlashRender.Apply, s =>
    [
        s.Color("color", e => e.Color, (e, v) => e with { Color = v }),
        s.Int("strength", 0, 100, e => e.Strength, (e, v) => e with { Strength = v }, legacy: ["amount"]),
        s.Int("count", 1, HitFlashEffect.MaxCount, e => e.Count, (e, v) => e with { Count = v }),
        s.Choice("shape", e => e.Shape, (e, v) => e with { Shape = v }, legacy: ["mode"]),
    ]);
}
