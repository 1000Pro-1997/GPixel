using System.Drawing;
using Gpixel.Core.Edit;
using Gpixel.Core.Model;
using Gpixel.Core.Scripting;

namespace Gpixel.Core.Tests;

/// <summary>스크립트 효과의 결과가 실행기·스레드와 무관하게 같고, JS로 옮긴 빛 지나가기가 C# 렌더러와 픽셀 단위로 같다.</summary>
public class ScriptDeterminismTests
{
    private static readonly string Shine = ScriptTemplates.Shine;

    private static ScriptEffectProgram Program(string source)
    {
        var result = ScriptEffectProgram.Parse(source);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        return result.Program;
    }

    /// <summary>여백 안에 놓인 원본: 투명 구멍, 반투명 픽셀과 여러 색이 섞인 둥근 모양.</summary>
    private static (PixelImage Image, Rectangle Source) Sprite(int width, int height, int margin)
    {
        var image = new PixelImage(width + margin * 2, height + margin * 2);
        double cx = width / 2.0, cy = height / 2.0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double dx = (x + 0.5 - cx) / (width * 0.45), dy = (y + 0.5 - cy) / (height * 0.4);
                if (dx * dx + dy * dy > 1 || (x * 7 + y * 3) % 17 == 0) continue;
                byte alpha = (x + y) % 9 == 0 ? (byte)140 : (byte)255;
                image.Pixels[(y + margin) * image.Width + x + margin] = new Color32((byte)(x * 13), (byte)(y * 7 + 30), (byte)((x ^ y) * 5), alpha);
            }
        return (image, new Rectangle(margin, margin, width, height));
    }

    private static Dictionary<string, object> Settings(ShineEffect shine) => new()
    {
        ["angle"] = shine.Angle,
        ["width"] = shine.Width,
        ["color"] = shine.Color,
        ["strength"] = shine.Strength,
        ["softness"] = shine.Softness,
    };

    [Theory]
    [InlineData(45, 3, 80, 0, 0xFFFFFFu, EffectRepeat.Once, EffectEasing.Linear)]
    [InlineData(0, 1, 100, 2, 0xFFE040u, EffectRepeat.Loop, EffectEasing.EaseOut)]
    [InlineData(135, 8, 55, 1, 0x40A0FFu, EffectRepeat.PingPong, EffectEasing.EaseInOut)]
    [InlineData(300, 32, 30, 2, 0xFF2020u, EffectRepeat.Once, EffectEasing.EaseIn)]
    [InlineData(90, 5, 1, 0, 0x808080u, EffectRepeat.Once, EffectEasing.Linear)]
    public void Shine_port_matches_the_built_in_renderer(int angle, int width, int strength, int softness, uint rgb, EffectRepeat repeat, EffectEasing easing)
    {
        var program = Program(Shine);
        var shine = new ShineEffect
        {
            Angle = angle, Width = width, Strength = strength, Softness = softness, Color = new Color32(0xFF000000u | rgb),
            Repeat = repeat, RepeatCount = 2, Easing = easing,
        };
        var (sprite, source) = Sprite(37, 29, 3);
        const int frames = 9;
        int changed = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            var at = (ShineEffect)shine.AtFrame(frame, frames, null);
            var expected = sprite.Clone();
            ShineRender.Apply(expected, at, source);
            var actual = sprite.Clone();
            program.Render(actual, source, ScriptFrameContext.From(at), Settings(at));
            Assert.True(expected.ContentEquals(actual), $"frame {frame}");
            if (!expected.ContentEquals(sprite)) changed++;
        }
        Assert.True(changed > 0);
    }

    [Fact]
    public void Shine_port_matches_at_256()
    {
        var program = Program(Shine);
        var shine = new ShineEffect { Angle = 30, Width = 12, Softness = 2, Strength = 90 };
        var (sprite, source) = Sprite(256, 256, 0);
        foreach (int frame in new[] { 2, 5 })
        {
            var at = (ShineEffect)shine.AtFrame(frame, 8, null);
            var expected = sprite.Clone();
            ShineRender.Apply(expected, at, source);
            var actual = sprite.Clone();
            program.Render(actual, source, ScriptFrameContext.From(at), Settings(at));
            Assert.True(expected.ContentEquals(actual));
            Assert.False(actual.ContentEquals(sprite));
        }
    }

    [Fact]
    public void Same_input_gives_the_same_output_on_other_threads_and_repeated_calls()
    {
        // 해시, 프레임 무작위, 삼각함수, 색 변환, 그림 합성, 다른 프레임을 모두 쓰는 효과
        var program = Program("""
            effect({
              name: "섞기",
              settings: [{ key: "seed", type: "int", default: 7 }, { key: "glow", type: "color", default: "#80ffc0" }],
              render(img, ctx, s) {
                const d = img.data, w = img.width, h = img.height, prev = ctx.frameSource(ctx.frame - 1);
                const layer = img.blank(w, h);
                const shift = Math.floor(ctx.random(s.seed) * 4);
                for (let y = 0; y < h; y++) {
                  for (let x = 0; x < w; x++) {
                    const i = (y * w + x) * 4;
                    const n = ctx.hash(x, y, s.seed);
                    const c = hsvToRgb((n * 360 + Math.sin(x * 0.3 + ctx.progress * 6.28) * 40) % 360, 0.6, lerp(0.4, 1, n));
                    layer.data[i] = c.r; layer.data[i + 1] = c.g; layer.data[i + 2] = c.b;
                    layer.data[i + 3] = (x + shift) % 3 === 0 ? s.glow.r : 0;
                    if (prev !== null) d[i + 2] = (d[i + 2] + prev.data[i]) / 2;
                  }
                }
                img.blendOver(layer, 1, -1);
              },
            });
            """);
        var (sprite, source) = Sprite(48, 40, 2);
        var frame = new ScriptFrameContext { FrameIndex = 3, FrameStep = 3, FrameSpan = 6, Time = 0.5, Progress = 0.5, FrameSource = _ => sprite };

        PixelImage RenderOnce()
        {
            var image = sprite.Clone();
            program.Render(image, source, frame);
            return image;
        }

        PixelImage RenderOnNewThread()
        {
            PixelImage? result = null;
            var thread = new Thread(() => result = RenderOnce());
            thread.Start();
            thread.Join();
            return result!;
        }

        var first = RenderOnce();
        Assert.False(first.ContentEquals(sprite));
        Assert.True(first.ContentEquals(RenderOnce()));
        Assert.True(first.ContentEquals(RenderOnNewThread()));
        Assert.True(first.ContentEquals(RenderOnNewThread()));
        var parallel = new PixelImage[4];
        Parallel.For(0, parallel.Length, i => parallel[i] = RenderOnce());
        Assert.All(parallel, image => Assert.True(first.ContentEquals(image)));
    }
}
