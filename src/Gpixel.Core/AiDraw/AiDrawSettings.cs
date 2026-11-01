namespace Gpixel.Core.AiDraw;

/// <summary>AI 채팅이 그림을 그리는 방식.</summary>
public enum AiDrawMode
{
    /// <summary>이미지 생성(Codex)으로 그림을 만들고 Gpixel이 픽셀 아트로 변환해 넣는다.</summary>
    Generate,
    /// <summary>AI가 MCP 픽셀 도구로 직접 찍는다(예전 방식).</summary>
    Direct,
    /// <summary>직접 찍은 뒤 요청 조건·참고 그림을 자체 평가하고 부족하면 수정·재확인한다.</summary>
    DirectAutoCheck,
}

/// <summary>변환한 그림을 넣는 곳.</summary>
public enum AiDrawInsert
{
    NewLayer,
    NewDocument,
}

/// <summary>생성할 때 함께 보낼 픽셀 아트 스타일 참고 그림.</summary>
public enum AiDrawStyleReference
{
    /// <summary>
    /// 참고 자료 보관소에서 고른다: 사용자가 픽셀 아트가 아닌 그림(캐릭터)을 붙여 그 캐릭터를 그리게 할 때, 또는 그리는 대상과 글이 맞는 자료가 있을 때만.
    /// </summary>
    Auto,
    None,
    /// <summary><see cref="AiDrawSettings.StyleReferenceId"/>의 자료를 늘 쓴다.</summary>
    Specific,
}

/// <summary>애니메이션을 생성하는 방식.</summary>
public enum AiDrawAnimationMethod
{
    /// <summary>한 번의 생성으로 모든 프레임을 한 장(스프라이트 시트)에 — 같은 그림 안이라 캐릭터·크기·카메라가 맞는다(기본).</summary>
    SingleSheet,
    /// <summary>프레임마다 따로 생성(첫·앞 프레임을 참고로) — 프레임 사이 일관성이 낮다.</summary>
    PerFrame,
}

/// <summary>
/// AI 그리기 설정(채팅 창의 설정 칸, 설정 파일에 저장). 그림 생성은 Codex만 할 수 있어, Claude 탭은 설정과 관계없이 직접 찍는다
/// (사용자가 채팅에서 Codex에 맡기라고 하면 그 차례만 생성 도구를 쓴다 — <see cref="EffectiveMode"/>).
/// </summary>
public sealed class AiDrawSettings
{
    /// <summary>결과 크기 직접 입력의 범위(px, 긴 변).</summary>
    public const int MinSize = 8, MaxSize = 256;
    /// <summary>애니메이션 프레임 수 범위.</summary>
    public const int MinFrames = 2, MaxFrames = 12;
    /// <summary>그림을 만들 수 있는 AI(채팅 탭 id).</summary>
    public const string GeneratingProvider = "codex";

    public AiDrawMode Mode { get; set; } = AiDrawMode.Generate;

    /// <summary>
    /// 팔레트 색만 사용(직접 찍기): AI가 픽셀 도구로 칠하는 색을 활성 팔레트의 가장 가까운 색으로 바꾼다(<see cref="PaletteLock"/>). 그림 생성 결과(변환기)와 레이어 효과는 그대로.
    /// </summary>
    public bool PaletteOnly { get; set; }

    /// <summary>
    /// 결과 크기 보정(기본 끔): 켜면 변환 결과를 <see cref="SizeTarget"/>(또는 채팅에서 정한 크기)에 맞춘 고정 크기로, 끄면 그림에서 찾은 칸 크기대로(자동 크기 + 자동 최적화) 변환한다.
    /// 꺼져 있어도 채팅에서 말한 크기는 생성 프롬프트에 대략적인 크기 힌트로 들어간다. 이 설정이 없던 예전 설정 파일은 읽을 때 끔이 된다.
    /// </summary>
    public bool ResizeResult { get; set; }

    /// <summary>결과 크기 보정을 켰을 때 쓸 크기의 기본값(px).</summary>
    public const int DefaultSize = 32;

    /// <summary>결과 크기(긴 변 px, <see cref="ResizeResult"/>일 때만 쓰임). 0 이하(예전 "자동" 값)는 <see cref="DefaultSize"/>로 본다.</summary>
    public int SizeTarget { get; set; } = DefaultSize;

    /// <summary>
    /// 생성 프롬프트에 넣을 크기 힌트와 변환 고정 크기(0 = 없음·자동): <paramref name="requested"/> = 채팅에서 정한 크기(없으면 null).
    /// 보정이 꺼져 있으면 힌트는 채팅에서 정한 크기뿐이고 변환은 자동, 켜져 있으면 둘 다 채팅 크기 또는 설정 크기.
    /// </summary>
    public (int Hint, int Convert) SizesFor(int? requested)
    {
        int clamped = requested is { } r ? Math.Clamp(r, MinSize, MaxSize) : 0;
        if (!ResizeResult) return (clamped, 0);
        int size = requested is not null ? clamped : SizeTargetClamped;
        return (size, size);
    }

    /// <summary>변환할 때 배경을 지운다(기본 켬).</summary>
    public bool RemoveBackground { get; set; } = true;

    /// <summary>변환할 때 자동 최적화를 돌린다(기본 켬).</summary>
    public bool AutoOptimize { get; set; } = true;

    public AiDrawInsert Insert { get; set; } = AiDrawInsert.NewLayer;

    public AiDrawStyleReference StyleReference { get; set; } = AiDrawStyleReference.Auto;

    public AiDrawAnimationMethod AnimationMethod { get; set; } = AiDrawAnimationMethod.SingleSheet;

    /// <summary><see cref="AiDrawStyleReference.Specific"/>일 때 쓸 참고 자료 id.</summary>
    public string? StyleReferenceId { get; set; }

    /// <summary>동작 이름(<see cref="AiDrawActions"/>) → 기본 프레임 수. 빠진 동작은 <see cref="AiDrawActions.DefaultFrames"/>.</summary>
    public Dictionary<string, int> FrameCounts { get; set; } = AiDrawActions.DefaultFrameCounts();

    /// <summary>그 AI 탭에서 실제로 쓰는 방식: Codex 탭은 <see cref="Mode"/>, 다른 AI는 그림 생성 대신 자동 체크 직접 찍기로 그린다(직접 찍기를 명시적으로 고르면 그대로).</summary>
    public AiDrawMode EffectiveMode(string provider) => provider == GeneratingProvider || Mode != AiDrawMode.Generate ? Mode : AiDrawMode.DirectAutoCheck;

    /// <summary>동작의 기본 프레임 수(<see cref="MinFrames"/>~<see cref="MaxFrames"/>).</summary>
    public int FramesFor(string action)
        => Math.Clamp(FrameCounts.TryGetValue(action, out var n) ? n : AiDrawActions.DefaultFrames(action), MinFrames, MaxFrames);

    /// <summary>결과 크기 보정 크기(<see cref="MinSize"/>~<see cref="MaxSize"/>, 0 이하는 <see cref="DefaultSize"/>).</summary>
    public int SizeTargetClamped => SizeTarget <= 0 ? DefaultSize : Math.Clamp(SizeTarget, MinSize, MaxSize);
}
