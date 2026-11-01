namespace Gpixel.Core.Model;

/// <summary>
/// 레이어 효과 하나(비파괴): 셀 픽셀은 그대로 두고 합성할 때 레이어(그룹이면 그 그룹의 합성)의 그림에 입힌다.
/// 값 객체(불변 record)라 실행취소 기록은 목록 참조만 담는다. 바꿀 때는 <c>with</c>로 새 값을 만들어
/// <see cref="SpriteProject.SetLayerEffects"/>로 넣는다. 모든 효과는 픽셀 단위(번짐·안티에일리어싱 없음)다.
/// </summary>
public abstract record LayerEffect
{
    /// <summary>꺼진 효과는 목록에 남아 있지만 그리지 않는다.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// 효과가 입혀질 프레임 범위(프레임 번호, 양 끝 포함). null = 모든 프레임(나중에 늘어난 프레임까지).
    /// 태그(<see cref="AnimationTag"/>)처럼 프레임 번호 기준이다: 범위 안에 프레임을 넣으면 늘어나고, 범위 앞에 넣으면 뒤로 밀리며,
    /// 프레임을 지우면 줄어든다(범위의 프레임을 모두 지우면 효과도 지워진다). 프레임 순서 바꾸기는 범위를 그대로 둔다
    /// (그 자리에 온 프레임에 입혀짐 — 영상 편집의 클립처럼 시간 위치 기준). 프레임 편집은 <see cref="SpriteProject"/>가 맞춘다.
    /// </summary>
    public FrameRange? Frames { get; init; }

    /// <summary>
    /// 바로 앞의 클리핑되지 않은 효과가 바꾼 픽셀을 기준으로 이 효과를 적용한다.
    /// 연속된 클리핑 효과는 같은 기준 효과의 영역을 공유한다. 반사·잔상은 기준 픽셀을 이동한 결과도 표시한다.
    /// </summary>
    public bool ClipToPrevious { get; init; }

    /// <summary>각 색 설정의 그라데이션. 키는 색 속성 이름(목록 색은 인덱스 포함); 없는 키는 단색 설정을 쓴다.</summary>
    public IReadOnlyDictionary<string, EffectColorGradient> ColorGradients { get; init; }
        = System.Collections.Immutable.ImmutableDictionary<string, EffectColorGradient>.Empty;

    /// <summary>
    /// 매개변수가 있는 효과 레시피를 넣어 생긴 효과면 그 묶음과 레시피 안 자리(<see cref="Effects.RecipeLink"/>), 아니면 null.
    /// 그리기는 이 값을 보지 않는다 — 묶음은 매개변수 하나로 여러 효과의 설정을 함께 바꾸는 편집 정보다.
    /// </summary>
    public Effects.RecipeLink? Recipe { get; init; }

    /// <summary>Render-only progress of an animated color gradient (0 at first frame, 1 at last); never saved.</summary>
    internal double GradientTime { get; init; }

    /// <summary>프레임 <paramref name="frameIndex"/>가 범위 안인지(켜짐 여부와 무관).</summary>
    public bool AppliesTo(int frameIndex) => Frames is not { } r || r.Contains(frameIndex);

    internal bool HasAnimatedColorGradient()
    {
        if (ColorGradients.Count == 0) return false;
        // The editor's dictionaries are concrete; do not box their enumerator on every static render.
        if (ColorGradients is Dictionary<string, EffectColorGradient> colors)
        {
            foreach (var entry in colors)
                if (entry.Value.Animated) return true;
            return false;
        }
        foreach (var entry in ColorGradients)
            if (entry.Value.Animated) return true;
        return false;
    }

    /// <summary>화면에 보일 이름.</summary>
    public abstract string DisplayName { get; }
}

/// <summary>프레임 번호 범위(양 끝 포함, 0 ≤ <see cref="From"/> ≤ <see cref="To"/>).</summary>
public readonly record struct FrameRange
{
    public FrameRange(int from, int to)
    {
        if (from < 0 || to < from) throw new ArgumentOutOfRangeException(nameof(from), Gpixel.Core.Localization.DomainStrings.FormatFrames_InvalidRange(from: from, to: to));
        From = from;
        To = to;
    }

    public int From { get; }
    public int To { get; }

    public bool Contains(int frameIndex) => frameIndex >= From && frameIndex <= To;

    /// <summary>
    /// 프레임 <paramref name="frameCount"/>개 기준으로 맞춘 범위: 범위 밖 번호는 안으로 당기고(<paramref name="from"/> &gt; <paramref name="to"/>면 서로 바꿈),
    /// 모든 프레임을 덮으면 null(= 모든 프레임).
    /// </summary>
    public static FrameRange? Of(int from, int to, int frameCount)
    {
        int last = Math.Max(0, frameCount - 1);
        if (from > to) (from, to) = (to, from);
        from = Math.Clamp(from, 0, last);
        to = Math.Clamp(to, from, last);
        return from == 0 && to == last ? null : new FrameRange(from, to);
    }

    public override string ToString() => From == To ? $"{From + 1}" : $"{From + 1}–{To + 1}";
}

/// <summary>색을 그림 픽셀에 섞는 방법(색 덮기·안쪽 빛).</summary>
public enum EffectBlend
{
    /// <summary>덮기: 그 색 쪽으로.</summary>
    Normal,
    /// <summary>곱하기: 어둡게(흰색 = 그대로).</summary>
    Multiply,
    /// <summary>스크린: 밝게(검정 = 그대로).</summary>
    Screen,
}

/// <summary>순서 디더(Bayer) 무늬 크기. 없음 = 부드러운 단계, 2×2·4×4 = 두 색만 쓰는 픽셀아트 무늬.</summary>
public enum EffectDither
{
    None,
    Bayer2,
    Bayer4,
}

/// <summary>움직이는 효과의 빠르기 곡선(진행 0→1을 어떻게 지나가는지).</summary>
public enum EffectEasing
{
    /// <summary>처음 빠르게(끝으로 갈수록 느려짐).</summary>
    EaseOut,
    /// <summary>일정하게.</summary>
    Linear,
    /// <summary>천천히 시작.</summary>
    EaseIn,
    /// <summary>천천히 시작해 천천히 끝남.</summary>
    EaseInOut,
}

/// <summary>움직이는 효과가 프레임 범위 안에서 몇 번 도는지.</summary>
public enum EffectRepeat
{
    /// <summary>한 번(범위 처음 = 시작, 끝 = 끝).</summary>
    Once,
    /// <summary>N번 반복(끝나면 처음부터).</summary>
    Loop,
    /// <summary>왕복 N번(갔다가 돌아옴 — 범위 끝에서 원래대로).</summary>
    PingPong,
}

/// <summary>
/// 움직이는(애니메이션) 효과: 모습이 효과의 프레임 범위 안 진행 <see cref="Time"/>(범위 첫 프레임 0 → 마지막 프레임 1)에 따라 바뀐다.
/// 범위가 길면 느리게, 짧으면 빠르게 움직인다. 범위가 null이면 애니메이션 전체가 범위다.
/// 저장되는 설정은 빠르기 곡선·반복뿐이고, 그릴 프레임 위치(<see cref="FrameStep"/>·<see cref="FrameSpan"/>)와 피벗(<see cref="PivotPoint"/>)·다른 프레임 원본(<see cref="FrameSource"/>)은
/// 그릴 때 <see cref="Edit.LayerEffects.At"/>이 채운 사본에만 있다(저장 안 함, 목록에 든 효과는 0·null).
/// </summary>
public abstract record AnimatedEffect : LayerEffect
{
    public const int MaxRepeatCount = 8;

    private readonly int _repeatCount = 1, _step, _span;

    public EffectEasing Easing { get; init; } = EffectEasing.Linear;
    public EffectRepeat Repeat { get; init; } = EffectRepeat.Once;

    /// <summary>반복·왕복 횟수 1~<see cref="MaxRepeatCount"/>(한 번이면 쓰지 않음).</summary>
    public int RepeatCount { get => _repeatCount; init => _repeatCount = Math.Clamp(value, 1, MaxRepeatCount); }

    /// <summary>그릴 때만: 범위 첫 프레임부터 몇 프레임째인지(0부터).</summary>
    public int FrameStep { get => _step; init => _step = Math.Max(0, value); }

    /// <summary>그릴 때만: 범위 길이 − 1(끝 − 시작). 0이면 진행은 늘 0.</summary>
    public int FrameSpan { get => _span; init => _span = Math.Max(0, value); }

    /// <summary>그릴 때만: 피벗 자리(효과 원본 — 셀·그룹이면 캔버스 — 왼쪽 위 기준 px). null이면 원본 가운데.</summary>
    public System.Drawing.PointF? PivotPoint { get; init; }

    /// <summary>
    /// 그릴 때만: 프레임 번호 → 그 프레임의 효과 원본(셀, 그룹이면 부위 합성 — 효과 없이, 원본과 같은 크기, 빈 셀이면 null). 잔상처럼 다른 프레임 그림이 필요한 효과가 쓴다.
    /// null이면 다른 프레임을 쓰지 않는다. 주는 쪽이 셀 원본 그대로를 주므로(효과 재귀 없음) 결과는 셀 내용만으로 정해진다.
    /// </summary>
    public Func<int, PixelImage?>? FrameSource { get; init; }

    /// <summary>그릴 때만: 앞 프레임에 잔상을 제외한 효과를 적용한 그림과 원본 대비 위치.</summary>
    public Func<int, PixelImage, (PixelImage Image, int OffsetX, int OffsetY)>? RenderedFrameSource { get; init; }

    /// <summary>그릴 때만: 그리는 프레임 번호(범위 시작 + <see cref="FrameStep"/>).</summary>
    public int FrameIndex => (Frames?.From ?? 0) + FrameStep;

    /// <summary>진행 t = (프레임 − 시작) / max(1, 끝 − 시작), 0~1.</summary>
    public double Time => Math.Clamp((double)FrameStep / Math.Max(1, FrameSpan), 0, 1);

    /// <summary>반복을 넣은 진행(0~1, 빠르기 곡선 전).</summary>
    public double Phase => Cycle(Time, Repeat, RepeatCount);

    /// <summary>반복과 빠르기 곡선을 넣은 진행(0~1).</summary>
    public double Progress => Ease(Phase, Easing);

    /// <summary>
    /// 프레임 <paramref name="frameIndex"/>(프레임 <paramref name="frameCount"/>개 중)에 그릴 사본: 범위 안 위치와 피벗, 다른 프레임 원본(<paramref name="frameSource"/>)을 채운다
    /// (범위 밖이면 가까운 끝).
    /// </summary>
    public AnimatedEffect AtFrame(int frameIndex, int frameCount, System.Drawing.PointF? pivot, Func<int, PixelImage?>? frameSource = null)
    {
        var (from, to) = Frames is { } r ? (r.From, r.To) : (0, Math.Max(0, frameCount - 1));
        return this with { FrameStep = Math.Clamp(frameIndex - from, 0, to - from), FrameSpan = to - from, PivotPoint = pivot, FrameSource = frameSource };
    }

    /// <summary>
    /// 반복: 한 번 = 그대로, N번 반복 = 0→1을 N번(경계 프레임은 끝 1), 왕복 N번 = 0→1→0을 N번. 시작(t = 0)은 늘 0.
    /// </summary>
    public static double Cycle(double t, EffectRepeat repeat, int count)
    {
        t = Math.Clamp(t, 0, 1);
        if (repeat == EffectRepeat.Once || t <= 0) return t;
        double v = t * Math.Max(1, count);
        if (repeat == EffectRepeat.Loop) return v - Math.Ceiling(v) + 1;
        double f = v - Math.Floor(v);
        return 1 - Math.Abs(1 - 2 * f);
    }

    public static double Ease(double u, EffectEasing easing) => easing switch
    {
        EffectEasing.EaseOut => 1 - (1 - u) * (1 - u),
        EffectEasing.EaseIn => u * u,
        EffectEasing.EaseInOut => u * u * (3 - 2 * u),
        _ => u,
    };
}
