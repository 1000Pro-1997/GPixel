using System.Drawing;
using Gpixel.Core.Model;

namespace Gpixel.Core.Scripting;

/// <summary>
/// 렌더 함수의 <c>ctx</c>에 넘길 프레임 정보. 기본 효과가 <see cref="AnimatedEffect"/>에서 받는 값과 같다:
/// <c>ctx.t</c> = <see cref="Time"/>, <c>ctx.progress</c> = <see cref="Progress"/>, <c>ctx.frame</c> = <see cref="FrameIndex"/>,
/// <c>ctx.step</c> = <see cref="FrameStep"/>, <c>ctx.span</c> = <see cref="FrameSpan"/>, <c>ctx.pivot</c> = <see cref="Pivot"/>(그림 좌표로 바꿔서),
/// <c>ctx.frameSource(i)</c> = <see cref="FrameSource"/>.
/// </summary>
public sealed record ScriptFrameContext
{
    /// <summary>움직이지 않는 효과나 첫 프레임: 진행 0, 피벗은 원본 가운데, 다른 프레임 없음.</summary>
    public static ScriptFrameContext Still { get; } = new();

    /// <summary>범위 안 진행(0~1, 반복과 빠르기 곡선 전).</summary>
    public double Time { get; init; }

    /// <summary>반복과 빠르기 곡선을 넣은 진행(0~1).</summary>
    public double Progress { get; init; }

    /// <summary>그리는 프레임 번호.</summary>
    public int FrameIndex { get; init; }

    /// <summary>범위 첫 프레임부터 몇 프레임째인지(0부터).</summary>
    public int FrameStep { get; init; }

    /// <summary>범위 길이 − 1.</summary>
    public int FrameSpan { get; init; }

    /// <summary>피벗(효과 원본 왼쪽 위 기준 px). null이면 원본 가운데.</summary>
    public PointF? Pivot { get; init; }

    /// <summary>프레임 번호 → 그 프레임의 효과 원본(효과 없이, 원본과 같은 크기, 빈 셀이면 null). null이면 다른 프레임을 주지 않는다.</summary>
    public Func<int, PixelImage?>? FrameSource { get; init; }

    /// <summary>그릴 때 채운 <paramref name="effect"/> 사본(<see cref="AnimatedEffect.AtFrame"/>)의 프레임 정보.</summary>
    public static ScriptFrameContext From(AnimatedEffect effect) => new()
    {
        Time = effect.Time,
        Progress = effect.Progress,
        FrameIndex = effect.FrameIndex,
        FrameStep = effect.FrameStep,
        FrameSpan = effect.FrameSpan,
        Pivot = effect.PivotPoint,
        FrameSource = effect.FrameSource,
    };
}
