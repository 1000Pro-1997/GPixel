namespace Gpixel.Core.Model;

/// <summary>색 덮기: 보이는 픽셀의 RGB를 색(또는 공간 그라데이션) 쪽으로 <see cref="Amount"/>% × 색 알파만큼 섞는다. 원본 픽셀 알파는 그대로.</summary>
public sealed record ColorOverlayEffect : LayerEffect
{
    private readonly int _amount = 100;

    public Color32 Color { get; init; } = Color32.White;

    /// <summary>0~100(%).</summary>
    public int Amount { get => _amount; init => _amount = Math.Clamp(value, 0, 100); }

    /// <summary>색을 원본에 섞는 방식. 예전 파일은 일반 덮기.</summary>
    public EffectBlend Blend { get; init; } = EffectBlend.Normal;

    public override string DisplayName => Gpixel.Core.Localization.DomainStrings.Effect_ColorOverlay;
}
