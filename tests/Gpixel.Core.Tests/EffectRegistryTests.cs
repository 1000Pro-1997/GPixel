using System.Drawing;
using System.Text.Json.Nodes;
using Gpixel.Core.Edit;
using Gpixel.Core.Effects;
using Gpixel.Core.Model;

namespace Gpixel.Core.Tests;

/// <summary>등록된 모든 기본 효과 정의에 같은 계약을 적용한다(설정 schema·codec·렌더·여백).</summary>
public class EffectRegistryTests
{
    public static TheoryData<string> TypeIds()
    {
        var data = new TheoryData<string>();
        foreach (var d in EffectRegistry.BuiltIn) data.Add(d.TypeId);
        return data;
    }

    private static EffectDefinition Def(string typeId) => EffectRegistry.Find(typeId)!;

    /// <summary>schema의 값마다 기본값과 다른 쪽 끝 값(정수 최대·최소, 참거짓 뒤집기, 마지막·첫 선택, 다른 색)을 넣은 효과.</summary>
    private static LayerEffect Extreme(EffectDefinition d, bool high)
    {
        var e = d.Create();
        foreach (var p in d.Parameters)
        {
            JsonNode value = p.Kind switch
            {
                EffectParameterKind.Int => JsonValue.Create(high ? p.Max!.Value : p.Min!.Value),
                EffectParameterKind.Bool => JsonValue.Create(!p.DefaultValue.GetValue<bool>()),
                EffectParameterKind.Choice => JsonValue.Create(high ? p.Choices![^1] : p.Choices![0]),
                EffectParameterKind.Color => JsonValue.Create(high ? "#10203040" : "#FEDCBA"),
                EffectParameterKind.ColorList => new JsonArray("#FF0000", "#00FF00", "#0000FF"),
                _ => new JsonArray(new JsonArray("#FF0000", "#0000FF80"), new JsonArray("#00FF00", "#00000000")),
            };
            Assert.True(p.TryRead(e, value, out e), $"{d.TypeId}.{p.Key}");
        }
        return e;
    }

    [Fact]
    public void Registry_TypeIdsAndLegacyIdsAreUnique_AndEveryEffectModelIsRegistered()
    {
        Assert.All(EffectRegistry.All, d => Assert.False(string.IsNullOrWhiteSpace(d.TypeId)));
        Assert.Equal(EffectRegistry.All.Count, EffectRegistry.All.Select(d => d.TypeId).Distinct().Count());
        var legacy = EffectRegistry.All.SelectMany(d => d.LegacyIds).ToArray();
        Assert.Equal(legacy.Length, legacy.Distinct().Count());
        var models = typeof(LayerEffect).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(LayerEffect)) && !t.IsAbstract && t != typeof(MissingEffect));
        Assert.Equal(models.OrderBy(t => t.Name), EffectRegistry.All.Select(d => d.ModelType).OrderBy(t => t.Name));
    }

    [Theory]
    [MemberData(nameof(TypeIds))]
    public void Definition_CreatesItsModel_WithUniqueSchemaKeysBackedByProperties(string typeId)
    {
        var d = Def(typeId);
        var e = d.Create();
        Assert.IsType(d.ModelType, e);
        Assert.Same(d, EffectRegistry.Of(e));
        Assert.Equal(d.Animated, e is AnimatedEffect);
        var keys = d.Parameters.Select(p => p.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.DoesNotContain(keys, k => Gpixel.Core.IO.EffectSettings.ReservedKeys.Contains(k) || k == EffectCodec.GradientsKey);
        foreach (var p in d.Parameters)
        {
            // 키 = 모델 속성 이름(camelCase), 색 슬롯 = 그 속성 이름
            var property = d.ModelType.GetProperty(char.ToUpperInvariant(p.Key[0]) + p.Key[1..]);
            Assert.True(property is not null, $"{typeId}.{p.Key}");
            var expected = p.Kind switch
            {
                EffectParameterKind.Int => typeof(int),
                EffectParameterKind.Bool => typeof(bool),
                EffectParameterKind.Color => typeof(Color32),
                EffectParameterKind.ColorList => typeof(IReadOnlyList<Color32>),
                EffectParameterKind.ColorPairList => typeof(IReadOnlyList<ColorSwap>),
                _ => property!.PropertyType.IsEnum ? property.PropertyType : typeof(void),
            };
            Assert.Equal(expected, property!.PropertyType);
            if (p.ColorSlot is { } slot) Assert.Equal(property.Name, slot);
        }
    }

    [Theory]
    [MemberData(nameof(TypeIds))]
    public void Schema_DefaultsAreInsideTheirRangesAndChoices(string typeId)
    {
        foreach (var p in Def(typeId).Parameters)
            switch (p.Kind)
            {
                case EffectParameterKind.Int:
                    Assert.NotNull(p.Min);
                    Assert.NotNull(p.Max);
                    Assert.InRange(p.DefaultValue.GetValue<int>(), p.Min!.Value, p.Max!.Value);
                    break;
                case EffectParameterKind.Choice:
                    Assert.Contains(p.DefaultValue.GetValue<string>(), p.Choices!);
                    break;
            }
    }

    [Theory]
    [MemberData(nameof(TypeIds))]
    public void Schema_RangeEndsAreKept_AndValuesOutsideComeBackInside(string typeId)
    {
        var d = Def(typeId);
        foreach (var p in d.Parameters.Where(p => p.Kind == EffectParameterKind.Int))
        {
            foreach (int end in new[] { p.Min!.Value, p.Max!.Value })
            {
                Assert.True(p.TryRead(d.Create(), JsonValue.Create(end), out var kept));
                Assert.Equal(end, p.Write(kept).GetValue<int>());
            }
            foreach (int outside in new[] { p.Min!.Value - 1000, p.Max!.Value + 1000 })
            {
                Assert.True(p.TryRead(d.Create(), JsonValue.Create(outside), out var read));
                Assert.InRange(p.Write(read).GetValue<int>(), p.Min!.Value, p.Max!.Value);
            }
        }
    }

    [Theory]
    [MemberData(nameof(TypeIds))]
    public void Codec_RoundTripsDefaultsAndExtremes(string typeId)
    {
        var d = Def(typeId);
        foreach (var effect in new[] { d.Create(), Extreme(d, high: true), Extreme(d, high: false) })
        {
            Assert.Equal(effect, d.ReadSettings(d.WriteSettings(effect)));
            var framed = effect with { Enabled = false, ClipToPrevious = true, Frames = new FrameRange(1, 4) };
            var json = EffectCodec.Write(framed);
            Assert.Equal(framed, EffectCodec.Read(JsonNode.Parse(json.ToJsonString())!.AsObject()));
            // 설정 JSON의 키는 schema 키와 같다
            Assert.Equal(d.Parameters.Select(p => p.Key), json[EffectCodec.SettingsKey]!.AsObject().Select(entry => entry.Key));
        }
    }

    /// <summary>12×12 모양을 넓은 여백 가운데에 두고 효과를 입혀, 보이는 결과가 정의의 여백 안에만 있는지 본다(움직이는 효과는 범위 여러 곳).</summary>
    [Theory]
    [MemberData(nameof(TypeIds))]
    public void Render_IsDeterministic_AndStaysInsideItsPadding(string typeId)
    {
        const int size = 12, margin = 220, frames = 5;
        var d = Def(typeId);
        var sprite = new PixelImage(size, size);
        for (int y = 2; y < 10; y++)
            for (int x = 3; x < 9; x++)
                sprite[x, y] = new Color32((byte)(x * 20), (byte)(y * 20), 200);
        var source = new Rectangle(margin, margin, size, size);
        foreach (var effect in new[] { d.Create(), Extreme(d, high: true), Extreme(d, high: false) })
        {
            var padding = LayerEffects.Padding([effect], size, size);
            Assert.True(padding.Left >= 0 && padding.Top >= 0 && padding.Right >= 0 && padding.Bottom >= 0, $"{typeId} {padding}");
            var allowed = Rectangle.FromLTRB(margin - padding.Left, margin - padding.Top, margin + size + padding.Right, margin + size + padding.Bottom);
            for (int f = 0; f < frames; f++)
            {
                PixelImage Draw()
                {
                    var image = new PixelImage(size + margin * 2, size + margin * 2);
                    image.Blit(sprite, margin, margin);
                    LayerEffects.Apply(image, LayerEffects.Animate([effect], f, frames), source);
                    return image;
                }
                var first = Draw();
                Assert.Equal(first.Pixels, Draw().Pixels);
                var bounds = first.GetOpaqueBounds();
                Assert.True(bounds.IsEmpty || allowed.Contains(bounds), $"{typeId} frame {f}: {bounds} outside {allowed}");
            }
        }
    }

    [Fact]
    public void Resolve_AcceptsTypeIdsAndOldShortIds_NotRemovedEffects()
    {
        Assert.Same(EffectRegistry.Find(HitFlashDefinition.Id), EffectRegistry.Resolve(" HitFlash "));
        Assert.Same(EffectRegistry.Find(HitFlashDefinition.Id), EffectRegistry.Resolve("GPIXEL.EFFECT.HITFLASH"));
        Assert.Null(EffectRegistry.Resolve("nope"));
        Assert.Null(EffectRegistry.Resolve("gradient")); // 예전 파일에서만 읽힘(색 덮기로 바뀜)
    }
}
