using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>색 바꾸기 효과 정의: 설정 schema, 예전 형식 키, 렌더러·여백 연결.</summary>
public static class PaletteSwapDefinition
{
    public const string Id = "gpixel.effect.paletteswap";

    public static EffectDefinition Definition { get; } = EffectDefinition.Create<PaletteSwapEffect>(Id, "swap", PaletteSwapRender.Apply, s =>
    [
        s.ColorPairs("pairs", e => e.Pairs, (e, v) => e with { Pairs = v }),
        s.Int("tolerance", 0, PaletteSwapEffect.MaxTolerance, e => e.Tolerance, (e, v) => e with { Tolerance = v }),
    ]);
}
