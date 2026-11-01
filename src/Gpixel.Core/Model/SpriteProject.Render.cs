using System.Drawing;
using Gpixel.Core.Edit;
using Gpixel.Core.Effects;
using Gpixel.Core.Localization;

namespace Gpixel.Core.Model;

// 합성 순서표(RenderPlan) 만들기: 지금 프로젝트 상태를 담아 다른 스레드에서도 그릴 수 있게 한다.
// 동기 합성(Flatten·FlattenInto·FlattenGroup·FlattenOutfit·EffectSource)도 모두 이 순서표로 그린다.
public sealed partial class SpriteProject
{
    /// <summary>
    /// 프레임 합성 순서표(<see cref="FlattenInto"/>와 같은 결과). <paramref name="layerFilter"/>·<paramref name="celMap"/>은 만드는 동안 부른다.
    /// </summary>
    public RenderPlan PlanFrame(int frameIndex, bool includeHidden = false, Func<Layer, bool>? layerFilter = null,
        Func<Layer, PixelImage, PixelImage>? celMap = null)
    {
        var frame = _frames[frameIndex];
        PixelImage? CelOf(Layer layer)
        {
            if (layerFilter is not null && !layerFilter(layer)) return null;
            var cel = GetCel(layer, frame);
            return cel is not null && celMap is not null ? celMap(layer, cel) : cel;
        }
        var steps = new List<RenderStep>();
        foreach (var layer in _layers)
        {
            if (layer.IsGroup)
            {
                if (layer.HasActiveEffectsAt(frameIndex) && (includeHidden || layer.Visible)
                    && GroupStepOf(layer, frameIndex, part => includeHidden || part.Visible ? CelOf(part) : null) is { } group)
                    steps.Add(group);
                continue;
            }
            if (layer.Parent is { IsGroup: true } pg && pg.HasActiveEffectsAt(frameIndex)) continue; // 그룹 차례에 함께 그린다
            if (!includeHidden && !(layer.Visible && (layer.Parent?.Visible ?? true))) continue;
            if (CelOf(layer) is { } cel) steps.Add(CelStepOf(layer, frameIndex, cel, DrawOpacity(layer)));
        }
        return new RenderPlan(_width, _height, steps);
    }

    /// <summary>그룹 하나의 보이는 부위 합성 순서표(<see cref="FlattenGroup"/>와 같은 결과).</summary>
    public RenderPlan PlanGroup(Layer group, int frameIndex, Skin? skin = null)
    {
        if (!group.IsGroup) throw new ArgumentException(DomainStrings.Project_NotGroupLayer, nameof(group));
        var frame = _frames[frameIndex];
        PixelImage? CelOf(Layer part) => !part.Visible ? null : skin is null ? GetCel(part, frame) : GetSkinCel(part, frame, skin);
        var steps = new List<RenderStep>();
        if (group.HasActiveEffectsAt(frameIndex))
        {
            if (GroupStepOf(group, frameIndex, CelOf) is { } step) steps.Add(step);
        }
        else
        {
            foreach (var part in ChildrenOf(group))
                if (CelOf(part) is { } cel) steps.Add(CelStepOf(part, frameIndex, cel, DrawOpacity(part)));
        }
        return new RenderPlan(_width, _height, steps);
    }

    /// <summary>
    /// 셀 하나의 썸네일 순서표: 그룹이면 그 그룹 합성(그릴 부위 셀이 하나도 없으면 null), 일반 레이어면 효과를 입힌 셀(셀 크기로 잘림 —
    /// <see cref="LayerEffects.RenderClipped"/>), 부위면 셀 그대로(부위 화면에서 고치는 그림이라 효과 없이). 빈 셀이면 null.
    /// </summary>
    public RenderPlan? PlanCel(int layerIndex, int frameIndex)
    {
        var layer = _layers[layerIndex];
        var frame = _frames[frameIndex];
        if (layer.IsGroup)
            return ChildrenOf(layer).Any(c => GetCel(c, frame) is not null) ? PlanGroup(layer, frameIndex) : null;
        if (GetCel(layer, frame) is not { } cel) return null;
        IReadOnlyList<LayerEffect> effects = layer.IsPart ? [] : layer.Effects;
        var step = new CelStep(layer, cel, effects, frameIndex, _frames.Count, EffectPivot(layer, frameIndex),
            layer.IsPart ? null : SourcesOf(layer, frameIndex), 255, 0, 0, clip: true);
        return new RenderPlan(cel.Width, cel.Height, [step]);
    }

    /// <summary>그룹 효과의 원본 순서표(효과 없이, 보이는 부위를 부위 불투명도·부위 효과로 합침 — <see cref="EffectSource"/>).</summary>
    private RenderPlan GroupSourcePlan(Layer group, int frameIndex)
    {
        var frame = _frames[frameIndex];
        var steps = new List<RenderStep>();
        foreach (var part in ChildrenOf(group))
        {
            if (!part.Visible || GetCel(part, frame) is not { } cel) continue;
            steps.Add(CelStepOf(part, frameIndex, cel, part.Opacity));
        }
        return new RenderPlan(_width, _height, steps);
    }

    /// <summary>합성할 때의 불투명도: 부위면 그룹 × 부위.</summary>
    private static byte DrawOpacity(Layer layer)
        => layer.IsPart ? (byte)((layer.Opacity * (layer.Parent?.Opacity ?? 255) + 127) / 255) : layer.Opacity;

    /// <summary>셀 하나를 자기 효과까지 입혀 (부위면 부위 위치)에 합성하는 단계.</summary>
    private CelStep CelStepOf(Layer layer, int frameIndex, PixelImage cel, byte opacity)
    {
        int x = layer.IsPart ? layer.PartOffset.X : 0, y = layer.IsPart ? layer.PartOffset.Y : 0;
        return new CelStep(layer, cel, layer.Effects, frameIndex, _frames.Count, EffectPivot(layer, frameIndex), SourcesOf(layer, frameIndex),
            opacity, x, y);
    }

    /// <summary>이 프레임에 효과가 켜진 그룹 단계(그릴 부위 셀이 없으면 null).</summary>
    private GroupStep? GroupStepOf(Layer group, int frameIndex, Func<Layer, PixelImage?> celOf)
    {
        var parts = new List<CelStep>();
        foreach (var part in ChildrenOf(group))
            if (celOf(part) is { } cel) parts.Add(CelStepOf(part, frameIndex, cel, part.Opacity));
        if (parts.Count == 0) return null;
        return new GroupStep(group, parts, group.Effects, frameIndex, _frames.Count, EffectPivot(group, frameIndex), SourcesOf(group, frameIndex),
            group.Opacity, _width, _height);
    }

    /// <summary>
    /// 잔상이 볼 다른 프레임 원본(<see cref="EffectFrameSource"/>와 같음)을 담는다. 잔상이 없으면 null.
    /// 그룹은 이 프레임의 잔상이 실제로 볼 프레임만 부위 합성 순서표를 만든다.
    /// </summary>
    private FrameSources? SourcesOf(Layer layer, int frameIndex)
    {
        var effects = layer.Effects;
        if (!LayerEffects.UsesOtherFrames(effects)) return null;
        int n = _frames.Count;
        var pivots = new PointF[n];
        for (int f = 0; f < n; f++) pivots[f] = EffectPivot(layer, f);
        if (!layer.IsGroup)
        {
            var cels = new PixelImage?[n];
            for (int f = 0; f < n; f++) cels[f] = GetCel(layer, _frames[f]);
            return new FrameSources(layer, cels, null, pivots);
        }
        var plans = new RenderPlan?[n];
        // 다른 프레임을 쓰는 스크립트 효과는 어느 프레임을 볼지 모르므로 모든 프레임의 원본을 준비한다
        if (effects.Any(e => e is ScriptEffect { Enabled: true } && e.AppliesTo(frameIndex) && EffectRegistry.Of(e)!.UsesOtherFrames(e)))
            for (int f = 0; f < n; f++) plans[f] = GroupSourcePlan(layer, f);
        foreach (var effect in effects)
        {
            if (effect is not AfterimageEffect { Enabled: true } a || !a.AppliesTo(frameIndex)) continue;
            var (from, to) = a.Frames is { } r ? (r.From, r.To) : (0, Math.Max(0, n - 1));
            int step = Math.Clamp(frameIndex - from, 0, to - from);
            for (int k = 1; k <= a.Count; k++)
            {
                int back = k * a.Interval;
                if (back > step) break;
                int f = from + step - back;
                if ((uint)f < (uint)n) plans[f] ??= GroupSourcePlan(layer, f);
            }
        }
        return new FrameSources(layer, null, plans, pivots);
    }
}
