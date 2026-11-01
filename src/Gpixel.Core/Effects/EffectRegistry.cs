using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>
/// 기본 효과 정의의 중앙 목록. 앱이 시작하기 전에 정적 목록으로 정해진다(어셈블리를 훑지 않음).
/// 새 기본 효과는 그 효과 파일(모델·정의·렌더러·앱 패널)을 만들고 <see cref="BuiltIn"/>에 한 줄을 더한다.
/// 효과 종류 목록, 프로젝트·사용자 정의 효과 저장, AI 도구 schema, 효과 창 목록이 모두 이 목록을 쓴다.
/// </summary>
public static class EffectRegistry
{
    /// <summary>
    /// 앱에 들어 있는 기본 효과(효과 창 종류 칸 순서: 고정 효과 먼저, 움직이는 효과는 뒤에). 설정만으로 만들 수 있어 종류 칸·AI 도구 종류 목록이 쓴다.
    /// </summary>
    public static IReadOnlyList<EffectDefinition> BuiltIn { get; } =
    [
        OutlineDefinition.Definition,
        ShadowDefinition.Definition,
        RimLightDefinition.Definition,
        ColorOverlayDefinition.Definition,
        InnerGlowDefinition.Definition,
        PaletteSwapDefinition.Definition,
        ColorAdjustDefinition.Definition,
        ReflectionDefinition.Definition,
        ExplodeDefinition.Definition,
        ShineDefinition.Definition,
        SquashDefinition.Definition,
        HitFlashDefinition.Definition,
        BlinkDefinition.Definition,
        ShakeDefinition.Definition,
        BobDefinition.Definition,
        AppearDefinition.Definition,
        FadeDefinition.Definition,
        MeltDefinition.Definition,
        RotateDefinition.Definition,
        AfterimageDefinition.Definition,
        ColorCycleDefinition.Definition,
        ShockwaveDefinition.Definition,
        HologramDefinition.Definition,
    ];

    /// <summary>
    /// 등록된 모든 효과 정의: 기본 효과와 스크립트 효과(<see cref="ScriptDefinition"/> — 원문이 있어야 만들 수 있어 효과 창에서 따로 더한다).
    /// 저장·읽기·렌더링이 이 목록으로 정의를 찾는다.
    /// </summary>
    public static IReadOnlyList<EffectDefinition> All { get; } = [.. BuiltIn, ScriptDefinition.Definition];

    private static readonly Dictionary<string, EffectDefinition> ById = Index<string>(All, d => [d.TypeId], StringComparer.Ordinal);
    private static readonly Dictionary<string, EffectDefinition> ByLegacyId = Index<string>(All, d => d.LegacyIds, StringComparer.Ordinal);
    private static readonly Dictionary<Type, EffectDefinition> ByType = Index(All, d => [d.ModelType], EqualityComparer<Type>.Default);

    /// <summary>종류 ID가 정확히 <paramref name="typeId"/>인 정의(파일 형식용, 없으면 null).</summary>
    public static EffectDefinition? Find(string? typeId) => typeId is not null && ById.TryGetValue(typeId, out var d) ? d : null;

    /// <summary>효과 모델의 정의(모르는 효과·<see cref="MissingEffect"/>면 null).</summary>
    public static EffectDefinition? Of(LayerEffect effect) => ByType.GetValueOrDefault(effect.GetType());

    /// <summary>효과의 종류 ID(<see cref="MissingEffect"/>면 원본의 ID).</summary>
    public static string TypeIdOf(LayerEffect effect)
        => effect is MissingEffect missing ? missing.TypeId : Of(effect)?.TypeId ?? effect.GetType().Name;

    /// <summary>
    /// 사람·AI가 적은 기본 효과 종류 이름: 앞뒤 공백과 대소문자를 무시하고 종류 ID, 없으면 그 효과의 예전 짧은 ID("outline")로 찾는다.
    /// 다른 효과로 바뀌어 읽히는 없어진 효과의 ID("gradient")와 스크립트 효과는 받지 않는다.
    /// </summary>
    public static EffectDefinition? Resolve(string name)
    {
        var id = name.Trim().ToLowerInvariant();
        foreach (var d in BuiltIn)
            if (d.TypeId.Equals(id, StringComparison.OrdinalIgnoreCase) || d.LegacyIds[0] == id)
                return d;
        return null;
    }

    /// <summary>예전 파일 형식의 짧은 종류 ID(대소문자·앞뒤 공백 무시)로 읽을 정의와 그 ID.</summary>
    internal static (EffectDefinition Definition, string LegacyId)? FindLegacy(string? id)
    {
        var key = id?.Trim().ToLowerInvariant();
        return key is not null && ByLegacyId.TryGetValue(key, out var d) ? (d, key) : null;
    }

    private static Dictionary<TKey, EffectDefinition> Index<TKey>(IReadOnlyList<EffectDefinition> all, Func<EffectDefinition, IEnumerable<TKey>> keys,
        IEqualityComparer<TKey> comparer) where TKey : notnull
    {
        var index = new Dictionary<TKey, EffectDefinition>(comparer);
        foreach (var d in all)
            foreach (var key in keys(d))
                if (!index.TryAdd(key, d))
                    throw new InvalidOperationException($"Duplicate effect registration: {key}");
        return index;
    }
}
