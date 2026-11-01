using System.Drawing;
using Gpixel.Core.Edit;
using Gpixel.Core.IO;
using Gpixel.Core.Model;

namespace Gpixel.Core.Tests;

public class LayerEffectTests
{
    private static readonly Color32 Red = new(255, 0, 0);
    private static readonly Color32 Blue = new(0, 0, 255);
    private static readonly Color32 Black = Color32.Black;
    private static readonly Color32 White = Color32.White;

    private static PixelImage Filled(int w, int h, Color32 c)
    {
        var img = new PixelImage(w, h);
        img.Clear(c);
        return img;
    }

    private static int Count(PixelImage img, Color32 c) => img.Pixels.Count(p => p == c);

    // ───────────── 그리기 ─────────────

    [Theory]
    [InlineData(1, false, 4)]
    [InlineData(1, true, 8)]
    [InlineData(2, false, 12)]
    [InlineData(2, true, 24)]
    [InlineData(3, false, 24)]
    public void Outline_OutsideAroundOnePixel_CountsByNeighborhood(int thickness, bool diagonal, int expected)
    {
        var (img, x, y) = LayerEffects.Render(Filled(1, 1, Red), [new OutlineEffect { Color = Blue, Thickness = thickness, Diagonal = diagonal }]);
        Assert.Equal((-thickness, -thickness), (x, y));
        Assert.Equal(new Size(1 + 2 * thickness, 1 + 2 * thickness), img.Size);
        Assert.Equal(expected, Count(img, Blue));
        Assert.Equal(Red, img.Get(thickness, thickness));
        // 4방향은 모서리가 빈다
        Assert.Equal(diagonal ? Blue : Color32.Transparent, img.Get(0, 0));
    }

    [Fact]
    public void Outline_InsidePaintsEdgeKeepsSizeAndAlpha()
    {
        var src = Filled(4, 4, Red.WithAlpha(200));
        var (img, x, y) = LayerEffects.Render(src, [new OutlineEffect { Color = Blue, Inside = true }]);
        Assert.Equal((0, 0, src.Size), (x, y, img.Size));
        Assert.Equal(12, Count(img, Blue.WithAlpha(200)));
        Assert.Equal(4, Count(img, Red.WithAlpha(200)));
        Assert.Equal(Red.WithAlpha(200), img.Get(1, 1));
        // 원본 셀은 그대로
        Assert.All(src.Pixels, p => Assert.Equal(Red.WithAlpha(200), p));
    }

    private static Color32 Shaded(Color32 c, int amount) => Gpixel.Core.Pixelate.OkLab.ShiftLightness(c, amount / 100f);

    private static float Lightness(Color32 c) => Gpixel.Core.Pixelate.OkLab.FromColor(c).L;

    [Fact]
    public void Outline_Shade_OutsideNextToBlueIsDarkerBlueNotBlack()
    {
        var (img, x, y) = LayerEffects.Render(Filled(1, 1, Blue), [new OutlineEffect { ColorMode = OutlineColorMode.Shade }]);
        Assert.Equal((-1, -1), (x, y));
        var line = img.Get(2, 1);
        Assert.Equal(Shaded(Blue, OutlineEffect.DefaultShadeAmount), line);
        Assert.Equal(4, Count(img, line));
        Assert.Equal(Blue, img.Get(1, 1));
        Assert.True(Lightness(line) < Lightness(Blue) - 0.1f);
        // 검정이 아니라 진한 파랑(색상 유지)
        Assert.True(line.B > 60 && line.B > line.R + 40 && line.B > line.G + 40, line.ToHex());
        Assert.Equal(255, line.A);
    }

    [Fact]
    public void Outline_Shade_InsideIsDarkerThanTheOverlaidPixel_KeepsAlpha()
    {
        var grey = new Color32(128, 128, 128, 200);
        var (img, _, _) = LayerEffects.Render(Filled(3, 3, grey), [new OutlineEffect { ColorMode = OutlineColorMode.Shade, Inside = true }]);
        var edge = img.Get(0, 0);
        Assert.Equal(Shaded(grey, -40), edge);
        Assert.Equal(200, edge.A);
        Assert.True(Lightness(edge) < Lightness(grey) - 0.1f);
        Assert.Equal(8, Count(img, edge));
        Assert.Equal(grey, img.Get(1, 1));
    }

    [Fact]
    public void Outline_Shade_PositiveAmountIsLighter()
    {
        var (outside, _, _) = LayerEffects.Render(Filled(1, 1, Blue), [new OutlineEffect { ColorMode = OutlineColorMode.Shade, ShadeAmount = 40 }]);
        var line = outside.Get(2, 1);
        Assert.Equal(Shaded(Blue, 40), line);
        Assert.True(Lightness(line) > Lightness(Blue) + 0.1f);
        Assert.True(line.B > line.R && line.B > line.G, line.ToHex());

        var (inside, _, _) = LayerEffects.Render(Filled(1, 1, Red), [new OutlineEffect { ColorMode = OutlineColorMode.Shade, ShadeAmount = 40, Inside = true }]);
        Assert.True(Lightness(inside.Get(0, 0)) > Lightness(Red) + 0.1f);
    }

    [Fact]
    public void Outline_Shade_PicksOrthogonalNeighborsFirst_ThenMajority_ThenDiagonalOnCorners()
    {
        // R B .
        // B . .
        var src = new PixelImage(2, 2);
        src.Set(0, 0, Red);
        src.Set(1, 0, Blue);
        src.Set(0, 1, Blue);
        var (img, x, y) = LayerEffects.Render(src, [new OutlineEffect { ColorMode = OutlineColorMode.Shade, Diagonal = true }]);
        Assert.Equal((-1, -1), (x, y));
        Color32 At(int px, int py) => img.Get(px - x, py - y);
        Assert.Equal(Shaded(Red, -40), At(-1, -1)); // 모서리: 대각 이웃(빨강)뿐
        Assert.Equal(Shaded(Blue, -40), At(2, -1)); // 모서리: 대각 이웃(파랑)뿐
        Assert.Equal(Shaded(Blue, -40), At(1, 1)); // 오목한 자리: 가로·세로 이웃(파랑 둘)이 대각(빨강)보다 먼저
        Assert.Equal(Shaded(Red, -40), At(-1, 0)); // 가로 이웃(빨강)이 대각 이웃(파랑)보다 먼저
        Assert.Equal(Shaded(Blue, -40), At(2, 1)); // 가로·세로 이웃이 없으면 대각

        // 구멍 한 칸: 왼쪽만 빨강, 나머지 셋은 파랑 → 가장 많은 파랑
        var hole = Filled(3, 3, Blue);
        hole.Set(1, 1, Color32.Transparent);
        hole.Set(0, 1, Red);
        var (filled, hx, hy) = LayerEffects.Render(hole, [new OutlineEffect { ColorMode = OutlineColorMode.Shade }]);
        Assert.Equal(Shaded(Blue, -40), filled.Get(1 - hx, 1 - hy));
    }

    [Fact]
    public void Outline_Shade_ThickRingsFollowTheInnerRingsBase_AndHideOriginalWorks()
    {
        var src = new PixelImage(2, 1);
        src.Set(0, 0, Red);
        src.Set(1, 0, Blue);
        var (img, x, y) = LayerEffects.Render(src, [new OutlineEffect { ColorMode = OutlineColorMode.Shade, Thickness = 2, HideOriginal = true }]);
        Assert.Equal((-2, -2), (x, y));
        Color32 At(int px, int py) => img.Get(px - x, py - y);
        Assert.Equal(Shaded(Red, -40), At(-1, 0));
        Assert.Equal(Shaded(Red, -40), At(-2, 0));
        Assert.Equal(Shaded(Blue, -40), At(2, 0));
        Assert.Equal(Shaded(Blue, -40), At(3, 0));
        Assert.Equal(Shaded(Blue, -40), At(1, -2));
        Assert.Equal(Color32.Transparent, At(0, 0));
        Assert.Equal(Color32.Transparent, At(1, 0));
    }

    [Fact]
    public void Outline_HideOriginal_OutsideLeavesOnlyOuterRing()
    {
        var src = Filled(2, 2, Red);
        var (shown, _, _) = LayerEffects.Render(src, [new OutlineEffect { Color = Blue }]);
        Assert.Equal((8, 4), (Count(shown, Blue), Count(shown, Red)));

        var (img, x, y) = LayerEffects.Render(src, [new OutlineEffect { Color = Blue, HideOriginal = true }]);
        Assert.Equal((-1, -1, new Size(4, 4)), (x, y, img.Size));
        Assert.Equal(8, Count(img, Blue));
        Assert.Equal(0, Count(img, Red));
        Assert.Equal(8, Count(img, Color32.Transparent));
        foreach (var (px, py) in new[] { (1, 1), (2, 1), (1, 2), (2, 2) })
            Assert.Equal(Color32.Transparent, img.Get(px, py));
    }

    [Fact]
    public void Outline_HideOriginal_InsideLeavesOnlyEdgeRing()
    {
        var src = Filled(4, 4, Red.WithAlpha(200));
        var (img, x, y) = LayerEffects.Render(src, [new OutlineEffect { Color = Blue, Inside = true, HideOriginal = true }]);
        Assert.Equal((0, 0, src.Size), (x, y, img.Size));
        Assert.Equal(12, Count(img, Blue.WithAlpha(200)));
        Assert.Equal(0, Count(img, Red.WithAlpha(200)));
        Assert.Equal(4, Count(img, Color32.Transparent));
        Assert.Equal(Color32.Transparent, img.Get(1, 1));
    }

    [Fact]
    public void Outline_HideOriginal_ClippedEffectsSeeOnlyTheLine_LaterEffectsSeeNoOriginal()
    {
        var src = Filled(3, 3, Red);
        var (img, _, _) = LayerEffects.Render(src,
        [
            new OutlineEffect { Color = Blue, HideOriginal = true },
            new ColorOverlayEffect { Color = White, Amount = 100, ClipToPrevious = true },
            // 지워진 원본 자리는 기준 영역이 아니라 클리핑된 그림자가 그 구멍을 채우지 못한다.
            new ShadowEffect { Color = Black, OffsetX = 1, OffsetY = 0, ClipToPrevious = true },
        ]);
        Assert.Equal(new Size(5, 5), img.Size);
        Assert.Equal(12, Count(img, White));
        Assert.Equal(0, Count(img, Black));
        Assert.Equal(0, Count(img, Red));
        for (int py = 1; py <= 3; py++)
            for (int px = 1; px <= 3; px++)
                Assert.Equal(Color32.Transparent, img.Get(px, py));

        // 뒤따르는 클리핑되지 않은 효과는 원본이 지워진 결과를 받는다: 선 고리의 그림자가 비워진 안쪽 자리에도 보인다.
        var (stacked, _, _) = LayerEffects.Render(src,
        [
            new OutlineEffect { Color = Blue, HideOriginal = true },
            new ShadowEffect { Color = Black, OffsetX = 1, OffsetY = 0 },
        ]);
        Assert.Equal(0, Count(stacked, Red));
        for (int py = 1; py <= 3; py++)
            Assert.Equal(Black, stacked.Get(1, py));
    }

    [Fact]
    public void Outline_HideOriginal_OnlyInsideFrameRange()
    {
        var effects = new LayerEffect[] { new OutlineEffect { Color = Blue, HideOriginal = true, Frames = new FrameRange(1, 1) } };
        var src = Filled(2, 2, Red);
        var outside = LayerEffects.At(effects, 0, 3);
        Assert.True(LayerEffects.RenderClipped(src, outside).ContentEquals(src));
        var inside = LayerEffects.RenderClipped(src, LayerEffects.At(effects, 1, 3));
        Assert.Equal(0, Count(inside, Red));
    }

    [Fact]
    public void Shadow_OffsetsShapeBehindAndPadsTowardOffset()
    {
        var (img, x, y) = LayerEffects.Render(Filled(2, 1, Red), [new ShadowEffect { Color = Black, OffsetX = 1, OffsetY = 1 }]);
        Assert.Equal((0, 0, new Size(3, 2)), (x, y, img.Size));
        Assert.Equal([Red, Red, Color32.Transparent, Color32.Transparent, Black, Black], img.Pixels);

        var (left, lx, ly) = LayerEffects.Render(Filled(1, 1, Red), [new ShadowEffect { Color = Black, OffsetX = -2, OffsetY = 0 }]);
        Assert.Equal((-2, 0, new Size(3, 1)), (lx, ly, left.Size));
        Assert.Equal([Black, Color32.Transparent, Red], left.Pixels);
    }

    [Fact]
    public void Shadow_AlphaScalesWithShapeAndSitsUnderOpaquePixels()
    {
        var src = new PixelImage(2, 1);
        src[0, 0] = Red;
        src[1, 0] = Red.WithAlpha(0);
        var (img, _, _) = LayerEffects.Render(src, [new ShadowEffect { Color = Black.WithAlpha(128), OffsetX = 1, OffsetY = 0 }]);
        Assert.Equal(Red, img.Get(0, 0));
        Assert.Equal(Black.WithAlpha(128), img.Get(1, 0));
        Assert.Equal(Color32.Transparent, img.Get(2, 0)); // 투명 픽셀은 그림자가 없다
    }

    [Fact]
    public void Overlay_FullIsSilhouetteKeepingAlpha_ZeroIsUnchanged()
    {
        var src = new PixelImage(3, 1);
        src[0, 0] = Red;
        src[1, 0] = new Color32(10, 200, 30, 77);
        var full = LayerEffects.RenderClipped(src, [new ColorOverlayEffect { Color = White, Amount = 100 }]);
        Assert.Equal([White, White.WithAlpha(77), Color32.Transparent], full.Pixels);

        var none = LayerEffects.RenderClipped(src, [new ColorOverlayEffect { Color = White, Amount = 0 }]);
        Assert.True(none.ContentEquals(src));
    }

    [Fact]
    public void Overlay_ColorAlphaControlsMixWithoutErasingOriginalAlpha()
    {
        var src = new PixelImage(2, 1);
        src[0, 0] = Red;
        src[1, 0] = Red.WithAlpha(77);
        var half = LayerEffects.RenderClipped(src, [new ColorOverlayEffect { Color = Blue.WithAlpha(128), Amount = 100 }]);
        Assert.Equal(new Color32(127, 0, 128), half[0, 0]);
        Assert.Equal(new Color32(127, 0, 128, 77), half[1, 0]);
        Assert.True(LayerEffects.RenderClipped(src, [new ColorOverlayEffect { Color = Blue.WithAlpha(0) }]).ContentEquals(src));
    }

    [Fact]
    public void RimLight_LightsFacingEdgesShadesOpposite()
    {
        var (img, _, _) = LayerEffects.Render(Filled(3, 3, Red),
            [new RimLightEffect { Light = White, Shade = Black, Direction = LightDirection.TopLeft }]);
        Assert.Equal(new Size(3, 3), img.Size);
        // 위 줄·왼쪽 줄 = 빛(5), 오른쪽·아래 나머지 = 그늘(3), 가운데 그대로
        Assert.Equal(5, Count(img, White));
        Assert.Equal(3, Count(img, Black));
        Assert.Equal(Red, img.Get(1, 1));
        Assert.Equal(Black, img.Get(2, 2));
        Assert.Equal(White, img.Get(2, 0));
    }

    [Fact]
    public void RimLight_SizeCoversSeveralPixelsWithoutPaintingTransparentAreas()
    {
        var src = Filled(7, 1, Red);
        var image = LayerEffects.RenderClipped(src,
            [new RimLightEffect { Direction = LightDirection.Left, Light = White, Shade = Black, Size = 2 }]);
        Assert.Equal([White, White, Red, Red, Red, Black, Black], image.Pixels);
        Assert.Equal(1, new RimLightEffect { Size = 0 }.Size);
        Assert.Equal(RimLightEffect.MaxSize, new RimLightEffect { Size = 100 }.Size);
    }

    [Fact]
    public void DisabledEffects_AreNoOp()
    {
        var src = Filled(2, 2, Red);
        LayerEffect[] effects = [new OutlineEffect { Enabled = false }, new ShadowEffect { Enabled = false, OffsetX = 3 }];
        var (img, x, y) = LayerEffects.Render(src, effects);
        Assert.Same(src, img);
        Assert.Equal((0, 0), (x, y));
        Assert.True(LayerEffects.Padding(effects, 2, 2).IsNone);
    }

    [Fact]
    public void Effects_ApplyInListOrder()
    {
        var outline = new OutlineEffect { Color = Black };
        var overlay = new ColorOverlayEffect { Color = White, Amount = 100 };
        var (outlineFirst, _, _) = LayerEffects.Render(Filled(1, 1, Red), [outline, overlay]);
        var (overlayFirst, _, _) = LayerEffects.Render(Filled(1, 1, Red), [overlay, outline]);
        Assert.Equal(White, outlineFirst.Get(1, 0)); // 외곽선까지 덮임
        Assert.Equal(Black, overlayFirst.Get(1, 0));
        Assert.Equal(White, overlayFirst.Get(1, 1));
        // 그림자는 앞 효과(외곽선)까지 옮겨 깐다
        var pad = LayerEffects.Padding([outline, new ShadowEffect { OffsetX = 2, OffsetY = 0 }], 1, 1);
        Assert.Equal(new EffectPadding(1, 1, 3, 1), pad);
    }

    [Fact]
    public void ClippedOverlay_ChangesOnlyPreviousEffectPixels()
    {
        var gradient = new EffectColorGradient
        {
            Stops = [new(0, Red), new(1, Blue)],
            Angle = 270,
        };
        LayerEffect[] effects =
        [
            new OutlineEffect { Color = Black },
            new ColorOverlayEffect
            {
                ClipToPrevious = true,
                Color = Red,
                ColorGradients = new Dictionary<string, EffectColorGradient>
                {
                    [nameof(ColorOverlayEffect.Color)] = gradient,
                },
            },
        ];

        var (image, _, _) = LayerEffects.Render(Filled(1, 1, White), effects);

        Assert.Equal(White, image[1, 1]);
        Assert.Equal(Red, image[1, 0]);
        Assert.Equal(Blue, image[1, 2]);
        Assert.Equal(Color32.Transparent, image[0, 0]);
    }

    [Fact]
    public void ClippedEffects_ShareAnchorMask_AndDisabledAnchorProducesNothing()
    {
        var outline = new OutlineEffect { Color = Black };
        var red = new ColorOverlayEffect { ClipToPrevious = true, Color = Red };
        var blue = new ColorOverlayEffect { ClipToPrevious = true, Color = Blue, Amount = 50 };

        var (image, _, _) = LayerEffects.Render(Filled(1, 1, White), [outline, red, blue]);
        Assert.Equal(White, image[1, 1]);
        Assert.Equal(new Color32(127, 0, 128), image[1, 0]);

        var disabled = LayerEffects.Render(Filled(1, 1, White), [outline with { Enabled = false }, red]).Image;
        Assert.Equal(new Size(1, 1), disabled.Size);
        Assert.Equal(White, disabled[0, 0]);
        Assert.Equal(0, disabled.Pixels.Count(pixel => pixel == Red));
    }

    [Fact]
    public void ClippedReflection_MirrorsOnlyOutlineBeyondOriginalShape()
    {
        var src = Filled(3, 2, White);
        var outline = new OutlineEffect { Color = Black };
        var reflection = new ReflectionEffect
        {
            ClipToPrevious = true, Gap = 1, Height = 100, OpacityStart = 100, OpacityEnd = 100,
        };

        var (image, x, y) = LayerEffects.Render(src, [outline, reflection]);

        Assert.Equal((-1, -1), (x, y));
        Assert.Equal(9, image.Height);
        Assert.Equal(White, image[2, 2]);
        Assert.Equal(Black, image[2, 0]);
        Assert.Equal(Black, image[2, 5]);
        Assert.Equal(Color32.Transparent, image[2, 6]);
        Assert.Equal(Black, image[2, 8]);
    }

    [Fact]
    public void ClippedEffect_DoesNotAddPadding_AndBakesWithWholeGroup()
    {
        LayerEffect[] effects =
        [
            new OutlineEffect(),
            new ShadowEffect { ClipToPrevious = true, OffsetX = 8 },
            new ColorOverlayEffect { ClipToPrevious = true },
            new InnerGlowEffect(),
        ];

        Assert.Equal(new EffectPadding(1, 1, 1, 1), LayerEffects.Padding(effects, 2, 2));
        Assert.Equal([0, 1, 2], LayerEffects.BakeSet(effects, [0]));
        Assert.Equal([0, 1, 2], LayerEffects.BakeSet(effects, [1]));
    }

    // ───────────── 합성 ─────────────

    /// <summary>8×8 캔버스, 그룹 안 부위 둘(2×2 빨강을 (2,2), 2×2 파랑을 (4,2) — 나란히 붙음).</summary>
    private static (SpriteProject P, Layer Group, Layer Left, Layer Right) TwoParts()
    {
        var p = new SpriteProject(8, 8);
        var g = p.AddGroup("몸");
        var left = p.AddPart(g, "왼", 2, 2, new Point(2, 2));
        var right = p.AddPart(g, "오른", 2, 2, new Point(4, 2));
        p.SetCel(p.IndexOf(left), 0, Filled(2, 2, Red));
        p.SetCel(p.IndexOf(right), 0, Filled(2, 2, Blue));
        return (p, g, left, right);
    }

    [Fact]
    public void GroupOutline_WrapsWholeGroupWithoutInnerSeam()
    {
        var (p, g, _, _) = TwoParts();
        p.SetLayerEffects(p.IndexOf(g), [new OutlineEffect { Color = Black }]);
        var flat = p.Flatten(0);
        // 4×2 덩어리 둘레(4방향): 위·아래 4 + 4, 좌우 2 + 2 = 12
        Assert.Equal(12, Count(flat, Black));
        Assert.Equal(Red, flat.Get(3, 2));
        Assert.Equal(Blue, flat.Get(4, 2)); // 부위 사이에는 선이 없다
        Assert.Equal(Black, flat.Get(1, 2));
        Assert.Equal(Black, flat.Get(6, 3));
        // 썸네일(그룹 합성)도 같다
        Assert.True(p.FlattenGroup(g, 0).ContentEquals(flat));
        // 효과 원본은 효과 없이
        Assert.Equal(0, Count(p.EffectSource(g, 0)!, Black));
    }

    [Fact]
    public void PartOutline_OutlinesEachPartSeparately()
    {
        var (p, _, left, right) = TwoParts();
        p.SetLayerEffects(p.IndexOf(left), [new OutlineEffect { Color = Black }]);
        var flat = p.Flatten(0);
        // 왼 부위 둘레 8, 그중 오른쪽 2칸은 위에 그려진 오른 부위가 덮는다
        Assert.Equal(6, Count(flat, Black));
        Assert.Equal(Blue, flat.Get(4, 2));
        Assert.Equal(Black, flat.Get(1, 3));
    }

    [Fact]
    public void EffectsAtCanvasEdge_AreClippedToCanvas()
    {
        var p = new SpriteProject(3, 3);
        var cel = new PixelImage(3, 3);
        cel[0, 0] = Red;
        p.SetCel(0, 0, cel);
        p.SetLayerEffects(0, [new OutlineEffect { Color = Black, Diagonal = true }, new ShadowEffect { Color = Blue, OffsetX = -1, OffsetY = -1 }]);
        var flat = p.Flatten(0);
        Assert.Equal(new Size(3, 3), flat.Size);
        Assert.Equal([Red, Black, Color32.Transparent, Black, Black, Color32.Transparent, Color32.Transparent, Color32.Transparent, Color32.Transparent],
            flat.Pixels);
    }

    [Fact]
    public void HiddenOrExcludedGroupWithEffects_IsNotDrawn()
    {
        var (p, g, _, _) = TwoParts();
        p.SetLayerEffects(p.IndexOf(g), [new OutlineEffect { Color = Black }]);
        p.SetLayerProperties(p.IndexOf(g), visible: false);
        Assert.True(p.Flatten(0).IsRegionEmpty(new Rectangle(0, 0, 8, 8)));
        p.SetLayerProperties(p.IndexOf(g), visible: true);
        Assert.True(p.FlattenOutfit(0, null, [g]).IsRegionEmpty(new Rectangle(0, 0, 8, 8)));
        Assert.True(p.FlattenOutfit(0, null).ContentEquals(p.Flatten(0)));
    }

    [Fact]
    public void MergeDown_BakesUpperEffects()
    {
        var p = new SpriteProject(4, 4);
        var cel = new PixelImage(4, 4);
        cel[1, 1] = Red;
        p.AddLayer();
        p.SetCel(1, 0, cel);
        p.SetLayerEffects(1, [new OutlineEffect { Color = Black }]);
        var before = p.Flatten(0);
        p.MergeLayerDown(1);
        Assert.Single(p.Layers);
        Assert.True(p.Flatten(0).ContentEquals(before));
        Assert.Equal(4, Count(p.GetCel(0, 0)!, Black));
    }

    // ───────────── 실행취소·복제·저장 ─────────────

    [Fact]
    public void SetLayerEffects_IsOneUndoStep_AndSameListRecordsNothing()
    {
        var (p, g, _, _) = TwoParts();
        int gi = p.IndexOf(g);
        int steps = p.History.UndoCount;
        p.SetLayerEffects(gi, [new OutlineEffect { Color = Black }]);
        Assert.Equal(steps + 1, p.History.UndoCount);
        p.SetLayerEffects(gi, [new OutlineEffect { Color = Black }]);
        Assert.Equal(steps + 1, p.History.UndoCount);
        Assert.True(p.Undo());
        Assert.Empty(g.Effects);
        Assert.Equal(0, Count(p.Flatten(0), Black));
        Assert.True(p.Redo());
        Assert.Equal([new OutlineEffect { Color = Black }], g.Effects);
    }

    [Fact]
    public void Preview_CancelRestoresWithoutHistory_CommitIsOneStep()
    {
        var (p, g, _, _) = TwoParts();
        int gi = p.IndexOf(g);
        LayerEffect[] original = [new ColorOverlayEffect { Amount = 50 }];
        p.SetLayerEffects(gi, original);
        p.IsDirty = false;
        int steps = p.History.UndoCount;

        p.PreviewLayerEffects(gi, [new OutlineEffect()]);
        p.PreviewLayerEffects(gi, [new OutlineEffect(), new ShadowEffect()]);
        Assert.Equal(2, g.Effects.Count);
        p.EndLayerEffectsPreview(gi, original, commit: false);
        Assert.Equal(original, g.Effects);
        Assert.Equal(steps, p.History.UndoCount);
        Assert.False(p.IsDirty);

        p.PreviewLayerEffects(gi, [new OutlineEffect()]);
        p.PreviewLayerEffects(gi, [new OutlineEffect { Thickness = 2 }]);
        p.EndLayerEffectsPreview(gi, original, commit: true);
        Assert.Equal([new OutlineEffect { Thickness = 2 }], g.Effects);
        Assert.Equal(steps + 1, p.History.UndoCount);
        Assert.True(p.Undo());
        Assert.Equal(original, g.Effects);
    }

    [Fact]
    public void DuplicateLayer_CopiesEffects()
    {
        var (p, g, left, _) = TwoParts();
        p.SetLayerEffects(p.IndexOf(left), [new ShadowEffect()]);
        p.SetLayerEffects(p.IndexOf(g), [new OutlineEffect()]);
        var copy = p.DuplicateLayer(p.IndexOf(g));
        Assert.Equal(g.Effects, copy.Effects);
        Assert.Equal(left.Effects, p.ChildrenOf(copy).Single(c => c.Name.StartsWith("왼")).Effects);
    }

    [Fact]
    public void SaveLoad_RoundTripsEffects()
    {
        using var dir = new TempDir();
        var (p, g, left, _) = TwoParts();
        LayerEffect[] groupFx =
        [
            new OutlineEffect { Color = new Color32(1, 2, 3, 200), Thickness = 3, Diagonal = true, Inside = true, HideOriginal = true },
            new ShadowEffect { Color = new Color32(9, 8, 7, 100), OffsetX = -4, OffsetY = 5, Enabled = false },
            new RimLightEffect { Light = new Color32(250, 240, 10, 90), Shade = Color32.Transparent, Direction = LightDirection.BottomRight, Size = 3 },
            new ColorOverlayEffect { Color = Blue, Amount = 37 },
        ];
        p.SetLayerEffects(p.IndexOf(g), groupFx);
        p.SetLayerEffects(p.IndexOf(left), [new OutlineEffect()]);
        var path = dir.File("fx.gpixel");
        ProjectIO.Save(p, path);

        var warnings = new List<string>();
        var back = ProjectIO.Load(path, warnings);
        Assert.Empty(warnings);
        Assert.Equal(groupFx, back.Layers.Single(l => l.IsGroup).Effects);
        Assert.Equal([new OutlineEffect()], back.Layers.Single(l => l.Name == "왼").Effects);
        Assert.Empty(back.Layers.Single(l => l.Name == "오른").Effects);
        Assert.True(back.Flatten(0).ContentEquals(p.Flatten(0)));
    }

    // ───────────── 내보내기 ─────────────

    [Fact]
    public void Export_DisabledEffectsMatchNoEffects()
    {
        var (plain, _, _, _) = TwoParts();
        var (off, g, left, _) = TwoParts();
        off.SetLayerEffects(off.IndexOf(g), [new OutlineEffect { Enabled = false }]);
        off.SetLayerEffects(off.IndexOf(left), [new ShadowEffect { Enabled = false }]);
        var a = ProjectExport.BuildPartsSheet(plain, "p");
        var b = ProjectExport.BuildPartsSheet(off, "p");
        Assert.True(a.Image.ContentEquals(b.Image));
        Assert.Equal(a.Entries.Select(e => (e.Name, e.Rect, e.Pivot, e.Border)), b.Entries.Select(e => (e.Name, e.Rect, e.Pivot, e.Border)));
        Assert.True(ProjectExport.BuildSheet(plain, "f").Image.ContentEquals(ProjectExport.BuildSheet(off, "f").Image));
    }

    [Fact]
    public void Export_FrameSheetHasGroupEffect_PartsAtlasGrowsSpriteKeepingPivotPixel()
    {
        var (p, g, left, _) = TwoParts();
        int li = p.IndexOf(left);
        p.SetPartPivot(li, new Pivot(0.5f, 0f));
        p.SetPartBorder(li, new SpriteBorder(1, 0, 0, 0));
        p.SetLayerEffects(p.IndexOf(g), [new OutlineEffect { Color = Black }]);
        p.SetLayerEffects(li, [new ShadowEffect { Color = Black, OffsetX = 1, OffsetY = 1 }]);

        var frames = ProjectExport.BuildSheet(p, "f");
        Assert.True(frames.Image.ContentEquals(p.Flatten(0)));
        Assert.True(Count(frames.Image, Black) > 0);

        var parts = ProjectExport.BuildPartsSheet(p, "p");
        var e = parts.Entries.Single(x => x.Name == "왼");
        // 그림자만큼 오른쪽·아래로 1px 커짐(그룹 외곽선은 부위 아틀라스에 넣지 않음)
        Assert.Equal(new Size(3, 3), e.Rect.Size);
        var sprite = parts.Image.Crop(e.Rect);
        Assert.Equal(Red, sprite.Get(0, 0));
        Assert.Equal(Black, sprite.Get(2, 2));
        Assert.Equal(Color32.Transparent, sprite.Get(2, 0));
        // 피벗: 원래 (1, 2)px(왼쪽 위 기준) → 같은 픽셀
        var (px, py) = e.Pivot.ToLocalPixel(3, 3);
        Assert.Equal((1f, 2f), (px, py));
        Assert.Equal(new SpriteBorder(1, 0, 1, 1), e.Border);
        var right = parts.Entries.Single(x => x.Name == "오른");
        Assert.Equal(new Size(2, 2), right.Rect.Size);
    }

    // ───────────── 프레임 범위 ─────────────

    /// <summary><see cref="TwoParts"/>를 같은 그림 4프레임으로.</summary>
    private static (SpriteProject P, Layer Group, Layer Left, Layer Right) TwoPartsFourFrames()
    {
        var (p, g, left, right) = TwoParts();
        for (int i = 0; i < 3; i++) p.DuplicateFrame(i);
        return (p, g, left, right);
    }

    [Fact]
    public void FrameRange_EffectOnlyOnItsFrames_InEveryCompositeAndExport()
    {
        var (p, g, left, _) = TwoPartsFourFrames();
        p.SetLayerEffects(p.IndexOf(g), [new OutlineEffect { Color = Black, Frames = new FrameRange(1, 2) }]);
        int[] expected = [0, 12, 12, 0];
        for (int f = 0; f < 4; f++)
        {
            Assert.Equal(expected[f], Count(p.Flatten(f), Black));
            Assert.True(p.FlattenGroup(g, f).ContentEquals(p.Flatten(f)));
            Assert.True(p.FlattenOutfit(f, null).ContentEquals(p.Flatten(f)));
        }

        // 프레임 시트: 칸마다 그 프레임 합성 그대로
        var sheet = ProjectExport.BuildSheet(p, "f");
        Assert.Equal(4, sheet.Entries.Count);
        foreach (var e in sheet.Entries)
            Assert.True(sheet.Image.Crop(e.Rect).ContentEquals(p.Flatten(e.FrameIndex)));

        // 부위 아틀라스: 범위 밖 프레임도 같은 크기(효과 여백 포함), 그림자는 범위 안 프레임에만
        p.SetLayerEffects(p.IndexOf(left), [new ShadowEffect { Color = Black, OffsetX = 1, OffsetY = 1, Frames = new FrameRange(3, 3) }]);
        var parts = ProjectExport.BuildPartsSheet(p, "p");
        var sprites = parts.Entries.Where(e => ReferenceEquals(e.Part, left)).OrderBy(e => e.FrameIndex).ToList();
        Assert.Equal(4, sprites.Count);
        Assert.All(sprites, e => Assert.Equal(new Size(3, 3), e.Rect.Size));
        Assert.Single(sprites.Select(e => e.Pivot).Distinct());
        Assert.Equal([0, 0, 0, 3], sprites.Select(e => Count(parts.Image.Crop(e.Rect), Black)));
    }

    [Fact]
    public void FrameRange_EditIsOneUndoStep()
    {
        var (p, g, _, _) = TwoPartsFourFrames();
        int gi = p.IndexOf(g);
        p.SetLayerEffects(gi, [new OutlineEffect { Color = Black }]);
        int steps = p.History.UndoCount;
        p.SetLayerEffects(gi, [g.Effects[0] with { Frames = new FrameRange(2, 3) }]);
        Assert.Equal(steps + 1, p.History.UndoCount);
        Assert.Equal(0, Count(p.Flatten(0), Black));
        Assert.True(p.Undo());
        Assert.Null(g.Effects[0].Frames);
        Assert.Equal(12, Count(p.Flatten(0), Black));
    }

    [Fact]
    public void FrameRange_FollowsFrameInsertDeleteLikeTags_MoveKeepsNumbers()
    {
        var (p, g, _, _) = TwoPartsFourFrames();
        int gi = p.IndexOf(g);
        p.SetLayerEffects(gi, [new OutlineEffect { Frames = new FrameRange(1, 2) }, new ShadowEffect { Frames = new FrameRange(3, 3) }, new ColorOverlayEffect()]);
        FrameRange? Range(int k) => g.Effects[k].Frames;

        p.AddFrame(2); // 범위 안 → 늘어남, 뒤 범위는 밀림
        Assert.Equal((new FrameRange(1, 3), new FrameRange(4, 4)), (Range(0), Range(1)));
        p.AddFrame(1); // 시작 자리 → 통째로 밀림
        Assert.Equal(new FrameRange(2, 4), Range(0));
        p.AddFrame(5); // 끝 바로 뒤 → 그대로
        Assert.Equal(new FrameRange(2, 4), Range(0));
        p.DuplicateFrame(4); // 범위 끝 프레임 복제 → 늘어남
        Assert.Equal((new FrameRange(2, 5), new FrameRange(7, 7)), (Range(0), Range(1)));
        Assert.Null(Range(2));

        p.MoveFrame(0, 6); // 번호 기준: 범위 그대로
        Assert.Equal((new FrameRange(2, 5), new FrameRange(7, 7)), (Range(0), Range(1)));

        p.DeleteFrame(3); // 범위 안 → 줄어듦, 뒤 범위는 당겨짐
        Assert.Equal((new FrameRange(2, 4), new FrameRange(6, 6)), (Range(0), Range(1)));
        int steps = p.History.UndoCount;
        p.DeleteFrame(6); // 범위의 프레임이 모두 지워진 효과는 지워짐(같은 단계)
        Assert.Equal(2, g.Effects.Count);
        Assert.IsType<ColorOverlayEffect>(g.Effects[1]);
        Assert.Equal(steps + 1, p.History.UndoCount);
        Assert.True(p.Undo());
        Assert.Equal(3, g.Effects.Count);
        Assert.Equal(new FrameRange(6, 6), Range(1));
    }

    [Fact]
    public void DeleteFrames_ShrinksRangesAcrossTheSpan_DropsOnesInside_OneUndoStep()
    {
        var p = new SpriteProject(4, 4);
        while (p.Frames.Count < 10) p.AddFrame(p.Frames.Count);
        p.AddTag("mid", 2, 6);
        p.AddTag("tail", 7, 9);
        p.SetLayerEffects(0, [
            new OutlineEffect { Frames = new FrameRange(1, 3) },   // 앞에서 걸침 → 끝이 지우는 칸 앞까지
            new ShadowEffect { Frames = new FrameRange(4, 5) },    // 모두 지움 → 효과 없어짐
            new ColorOverlayEffect { Frames = new FrameRange(5, 8) }, // 뒤에서 걸침 → 남는 첫 프레임부터, 당겨짐
            new OutlineEffect(),                                   // 모든 프레임 → 그대로
        ]);
        int steps = p.History.UndoCount;

        p.DeleteFrames(3, 4); // 3~6

        Assert.Equal(6, p.Frames.Count);
        var effects = p.Layers[0].Effects;
        Assert.Equal([new FrameRange(1, 2), new FrameRange(3, 4), null], effects.Select(e => e.Frames));
        Assert.Equal((3, 5), (p.FindTag("tail")!.From, p.FindTag("tail")!.To));
        Assert.Equal((2, 2), (p.FindTag("mid")!.From, p.FindTag("mid")!.To));
        Assert.Equal(steps + 1, p.History.UndoCount);
        Assert.True(p.Undo());
        Assert.Equal(10, p.Frames.Count);
        Assert.Equal(4, p.Layers[0].Effects.Count);
        Assert.Throws<InvalidOperationException>(() => p.DeleteFrames(0, 10));
    }

    [Fact]
    public void EffectCanvasExpansion_MinimumAddsOnlyOverflowingSides()
    {
        var plan = EffectCanvasExpansion.FromBounds(16, 8, new Rectangle(-3, 2, 22, 7), EffectCanvasExpandMode.Minimum);

        Assert.Equal(new EffectCanvasExpansion(22, 9, 3, 0, false), plan);
        Assert.False(plan.IsNone(16, 8));
    }

    [Fact]
    public void EffectCanvasExpansion_KeepRatioAddsCenteredSlack()
    {
        var plan = EffectCanvasExpansion.FromBounds(16, 8, new Rectangle(-3, 2, 22, 7), EffectCanvasExpandMode.KeepRatio);

        Assert.Equal(new EffectCanvasExpansion(22, 11, 3, 1, true), plan);
        Assert.Equal(2d, (double)plan.Width / plan.Height, 1);
    }

    [Fact]
    public void EffectCanvasExpansion_KeepCenterPadsEverySideEqually()
    {
        var plan = EffectCanvasExpansion.FromBounds(16, 8, new Rectangle(-3, 2, 22, 7), EffectCanvasExpandMode.KeepCenter);

        Assert.Equal(new EffectCanvasExpansion(22, 14, 3, 3, false), plan);
        Assert.Equal(16, plan.Width - 2 * plan.OffsetX);
        Assert.Equal(8, plan.Height - 2 * plan.OffsetY);
    }

    [Fact]
    public void EffectCanvasExpansion_KeepCenterRejectsOversizedSymmetricCanvas()
    {
        int max = PixelImage.MaxSize;
        Assert.Throws<InvalidOperationException>(() =>
            EffectCanvasExpansion.FromBounds(max - 1, 4, new Rectangle(-1, 0, max, 4), EffectCanvasExpandMode.KeepCenter));
    }

    [Fact]
    public void EffectCanvasExpansion_InsideCanvasDoesNothing()
    {
        var plan = EffectCanvasExpansion.FromBounds(16, 8, new Rectangle(2, 1, 10, 6), EffectCanvasExpandMode.KeepRatio);

        Assert.True(plan.IsNone(16, 8));
        Assert.True(plan.RatioPreserved);
    }

    [Theory]
    [InlineData(0, 4, 5, null, null)]
    [InlineData(1, 9, 5, 1, 4)]
    [InlineData(3, 1, 5, 1, 3)]
    [InlineData(-2, 0, 5, 0, 0)]
    [InlineData(7, 8, 5, 4, 4)]
    public void FrameRange_Of_ClampsAndAllFramesIsNull(int from, int to, int count, int? eFrom, int? eTo)
        => Assert.Equal(eFrom is { } a ? new FrameRange(a, eTo!.Value) : null, FrameRange.Of(from, to, count));

    [Fact]
    public void FrameRange_SaveLoad_RoundTrips_OldFilesMeanAllFrames_OutOfRangeIsClamped()
    {
        var (p, g, _, _) = TwoPartsFourFrames();
        LayerEffect[] fx = [new OutlineEffect { Frames = new FrameRange(1, 2) }, new ShadowEffect(), new ColorOverlayEffect { Frames = new FrameRange(3, 3) }];
        p.SetLayerEffects(p.IndexOf(g), fx);
        using var stream = new MemoryStream();
        ProjectIO.Save(p, stream);

        stream.Position = 0;
        Assert.Equal(fx, ProjectIO.Load(stream).Layers.Single(l => l.IsGroup).Effects);

        // 예전 파일(범위 없음) = 모든 프레임, 프레임 수 밖 범위는 안으로(모든 프레임을 덮으면 모든 프레임)
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry(ProjectIO.ProjectEntryName)!;
            System.Text.Json.Nodes.JsonNode root;
            using (var r = entry.Open()) root = System.Text.Json.Nodes.JsonNode.Parse(r)!;
            var effects = root["layers"]!.AsArray().Single(l => l!["effects"] is not null)!["effects"]!.AsArray();
            effects[0]!.AsObject().Remove("frames");
            effects[1]!["frames"] = new System.Text.Json.Nodes.JsonArray(2, 99);
            effects[2]!["frames"] = new System.Text.Json.Nodes.JsonArray(0, 99);
            entry.Delete();
            using var w = zip.CreateEntry(ProjectIO.ProjectEntryName).Open();
            using var writer = new System.Text.Json.Utf8JsonWriter(w);
            root.WriteTo(writer);
        }
        stream.Position = 0;
        var back = ProjectIO.Load(stream).Layers.Single(l => l.IsGroup).Effects;
        Assert.Equal([null, new FrameRange(2, 3), null], back.Select(e => e.Frames));
    }
}
