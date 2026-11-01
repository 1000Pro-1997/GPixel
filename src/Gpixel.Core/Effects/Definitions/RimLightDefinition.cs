using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>가장자리 빛·그늘 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class RimLightDefinition
{
    public const string Id = "gpixel.effect.rimlight";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<RimLightEffect>(Id, "rim", RimLightRender.Apply, s =>
    [
        s.Choice("direction", e => e.Direction, (e, v) => e with { Direction = v }),
        s.Int("size", 1, RimLightEffect.MaxSize, e => e.Size, (e, v) => e with { Size = v }, legacy: ["thickness"]),
        s.Color("light", e => e.Light, (e, v) => e with { Light = v }, legacy: ["color"]),
        s.Color("shade", e => e.Shade, (e, v) => e with { Shade = v }),
    ]);
}
