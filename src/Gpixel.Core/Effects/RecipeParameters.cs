using Gpixel.Core.Assets;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>
/// 레시피 매개변수와 기본 효과 설정 schema 사이의 규칙: 내보낼 수 있는 설정, 연결 검사, 값 읽기·넣기.
/// 레시피 파일(패키지 검사·개인 에셋), 작성 편집기, 프로젝트에 넣은 묶음(<see cref="RecipeInstances"/>)이 같은 규칙을 쓴다.
/// </summary>
public static class RecipeParameters
{
    public const int MaxIdLength = 64;

    /// <summary>설정 종류에 맞는 매개변수 종류(색 목록처럼 값 하나가 아닌 설정은 null).</summary>
    public static RecipeParameterKind? KindOf(EffectParameterKind kind) => kind switch
    {
        EffectParameterKind.Color => RecipeParameterKind.Color,
        EffectParameterKind.Int => RecipeParameterKind.Int,
        EffectParameterKind.Bool => RecipeParameterKind.Bool,
        EffectParameterKind.Choice => RecipeParameterKind.Choice,
        _ => null,
    };

    /// <summary>매개변수로 내보낼 수 있는 설정인지(값 하나짜리 색·정수·켜고 끄기·고르기).</summary>
    public static bool CanExpose(EffectParameter setting) => setting.Exposed && KindOf(setting.Kind) is not null;

    /// <summary>효과 schema의 설정 <paramref name="key"/>(없거나 모르는 효과면 null).</summary>
    public static EffectParameter? SettingOf(LayerEffect effect, string key)
        => EffectRegistry.Of(effect)?.Parameters.FirstOrDefault(p => p.Key == key);

    /// <summary>효과의 설정 <paramref name="key"/> 지금 값(내보낼 수 없는 설정이면 null).</summary>
    public static RecipeValue? ValueOf(LayerEffect effect, string key)
        => SettingOf(effect, key) is { } setting && KindOf(setting.Kind) is { } kind && RecipeValue.TryRead(kind, setting.Write(effect), out var value)
            ? value
            : null;

    /// <summary>설정 <paramref name="key"/>에 <paramref name="value"/>를 넣은 새 효과(종류가 맞지 않으면 효과 그대로). 범위 밖 정수는 모델이 자른다.</summary>
    public static LayerEffect With(LayerEffect effect, string key, RecipeValue value)
        => SettingOf(effect, key) is { } setting && KindOf(setting.Kind) == value.Kind && setting.TryRead(effect, value.ToJson(), out var result)
            ? result
            : effect;

    /// <summary>연결 <paramref name="binding"/>이 가리키는 설정(레시피 효과 목록 기준, 없으면 null).</summary>
    public static EffectParameter? SettingOf(IReadOnlyList<LayerEffect> effects, RecipeBinding binding)
        => binding.Effect >= 0 && binding.Effect < effects.Count ? SettingOf(effects[binding.Effect], binding.Key) : null;

    /// <summary>연결된 정수 설정이 모두 받는 범위(schema 범위의 겹침). 정수 설정이 없거나 겹치지 않으면 null.</summary>
    public static (int Min, int Max)? IntRange(IReadOnlyList<LayerEffect> effects, IEnumerable<RecipeBinding> bindings)
    {
        int min = int.MinValue, max = int.MaxValue;
        bool any = false;
        foreach (var binding in bindings)
        {
            if (SettingOf(effects, binding) is not { Kind: EffectParameterKind.Int } setting) continue;
            any = true;
            min = Math.Max(min, setting.Min ?? int.MinValue);
            max = Math.Min(max, setting.Max ?? int.MaxValue);
        }
        return any && min <= max ? (min, max) : null;
    }

    /// <summary>연결된 고르기 설정이 모두 받는 저장 ID(첫 설정의 순서). 없으면 빈 목록.</summary>
    public static IReadOnlyList<string> CommonChoices(IReadOnlyList<LayerEffect> effects, IEnumerable<RecipeBinding> bindings)
    {
        List<string>? common = null;
        foreach (var binding in bindings)
        {
            if (SettingOf(effects, binding) is not { Kind: EffectParameterKind.Choice, Choices: { } choices }) continue;
            common = common is null ? [.. choices] : common.Where(choice => choices.Contains(choice, StringComparer.Ordinal)).ToList();
        }
        return common ?? [];
    }

    /// <summary>레시피 효과 목록에 매개변수 값을 넣은 새 목록(값은 <see cref="RecipeParameter.Clamp"/>로 맞춤, 없는 연결은 건너뜀).</summary>
    public static IReadOnlyList<LayerEffect> Apply(IReadOnlyList<LayerEffect> effects, RecipeParameter parameter, RecipeValue value)
    {
        value = parameter.Clamp(value);
        var result = effects.ToArray();
        foreach (var binding in parameter.Bindings)
            if (binding.Effect >= 0 && binding.Effect < result.Length)
                result[binding.Effect] = With(result[binding.Effect], binding.Key, value);
        return result;
    }

    /// <summary>모든 매개변수의 기본값을 넣은 레시피 효과 목록.</summary>
    public static IReadOnlyList<LayerEffect> ApplyDefaults(IReadOnlyList<LayerEffect> effects, IReadOnlyList<RecipeParameter> parameters)
    {
        var result = effects;
        foreach (var parameter in parameters) result = Apply(result, parameter, parameter.Default);
        return result;
    }

    /// <summary>매개변수 ID 모양: 1~64자의 영문·숫자·<c>_</c>·<c>-</c>·<c>.</c>.</summary>
    public static bool IsValidId(string? id)
        => id is { Length: > 0 and <= MaxIdLength } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    /// <summary>
    /// 매개변수를 레시피 효과 목록(<paramref name="effects"/>)의 schema로 검사한다. 문제마다 <paramref name="add"/>(종류, 필드 경로와 기술 값)를 부르고,
    /// 문제가 있는 매개변수 번호를 돌려준다. 규칙: ID 모양·중복, 이름, 연결이 하나 이상, 연결마다 있는 효과 번호·schema 키·같은 종류의 내보낼 수 있는 설정,
    /// 같은 설정을 두 번 잇지 않음, 정수 범위·기본값이 연결된 모든 설정의 schema 범위 안, 고를 수 있는 값이 연결된 모든 설정에서 고를 수 있고 기본값이 그중 하나.
    /// </summary>
    public static IReadOnlySet<int> Check(IReadOnlyList<LayerEffect> effects, IReadOnlyList<RecipeParameter> parameters, Action<AssetIssueCode, string> add)
    {
        var bad = new HashSet<int>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var bound = new HashSet<RecipeBinding>();
        for (int i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            string field = $"parameters[{i}]";
            int index = i;
            void Report(AssetIssueCode code, string detail)
            {
                add(code, detail);
                bad.Add(index);
            }

            if (!IsValidId(parameter.Id) || !ids.Add(parameter.Id)) Report(AssetIssueCode.InvalidField, $"{field}.id");
            if (!AssetManifest.IsValidName(parameter.Name)) Report(AssetIssueCode.InvalidField, $"{field}.name");
            if (parameter.Default.Kind != parameter.Kind) Report(AssetIssueCode.InvalidField, $"{field}.default");
            if (parameter.Bindings.Count == 0) Report(AssetIssueCode.InvalidField, $"{field}.bindings");

            var settings = new List<(string Where, EffectParameter Setting)>();
            for (int j = 0; j < parameter.Bindings.Count; j++)
            {
                var binding = parameter.Bindings[j];
                string at = $"{field}.bindings[{j}]";
                if (binding.Effect < 0 || binding.Effect >= effects.Count || EffectRegistry.Of(effects[binding.Effect]) is not { } definition)
                {
                    Report(AssetIssueCode.RecipeBindingMissingEffect, $"{at}: effects[{binding.Effect}]");
                    continue;
                }
                string where = $"effects[{binding.Effect}].{binding.Key}";
                if (definition.Parameters.FirstOrDefault(p => p.Key == binding.Key) is not { } setting)
                {
                    Report(AssetIssueCode.RecipeBindingUnknownKey, $"{at}: {definition.TypeId}.{binding.Key}");
                    continue;
                }
                if (!CanExpose(setting) || KindOf(setting.Kind) != parameter.Kind)
                {
                    Report(AssetIssueCode.RecipeBindingKindMismatch,
                        $"{at}: {where} {setting.Kind.ToString().ToLowerInvariant()} / {IO.RecipeParameterFormat.KindName(parameter.Kind)}");
                    continue;
                }
                if (!bound.Add(binding))
                {
                    Report(AssetIssueCode.RecipeBindingDuplicate, $"{at}: {where}");
                    continue;
                }
                settings.Add((where, setting));
            }

            switch (parameter.Kind)
            {
                case RecipeParameterKind.Int:
                    if (parameter.Min > parameter.Max)
                        Report(AssetIssueCode.RecipeParameterOutOfRange, $"{field}.min: {parameter.Min} > {parameter.Max}");
                    foreach (var (where, setting) in settings)
                    {
                        if (setting.Min is { } min && parameter.Min < min)
                            Report(AssetIssueCode.RecipeParameterOutOfRange, $"{field}.min: {parameter.Min} < {where} {min}");
                        if (setting.Max is { } max && parameter.Max > max)
                            Report(AssetIssueCode.RecipeParameterOutOfRange, $"{field}.max: {parameter.Max} > {where} {max}");
                    }
                    if (parameter.Default.Kind == RecipeParameterKind.Int && (parameter.Default.Int < parameter.Min || parameter.Default.Int > parameter.Max))
                        Report(AssetIssueCode.RecipeParameterOutOfRange, $"{field}.default: {parameter.Default.Int}");
                    break;
                case RecipeParameterKind.Choice:
                    if (parameter.Choices.Count == 0 || parameter.Choices.Distinct(StringComparer.Ordinal).Count() != parameter.Choices.Count)
                        Report(AssetIssueCode.InvalidField, $"{field}.choices");
                    foreach (string choice in parameter.Choices)
                        foreach (var (where, setting) in settings)
                            if (setting.Choices is not { } allowed || !allowed.Contains(choice, StringComparer.Ordinal))
                                Report(AssetIssueCode.RecipeParameterOutOfRange, $"{field}.choices: {choice} / {where}");
                    if (parameter.Default.Kind == RecipeParameterKind.Choice && !parameter.Choices.Contains(parameter.Default.Choice, StringComparer.Ordinal))
                        Report(AssetIssueCode.RecipeParameterOutOfRange, $"{field}.default: {parameter.Default.Choice}");
                    break;
            }
        }
        return bad;
    }
}

/// <summary>
/// 프로젝트 효과 목록에 넣은 매개변수 레시피(<see cref="EffectRecipeInstance"/>) 다루기. 모두 새 목록을 돌려주고(효과는 불변 값),
/// 부르는 쪽이 <see cref="SpriteProject.SetLayerEffects"/>로 넣으면 실행취소 한 단계다. 원본 레시피·패키지는 보지 않는다.
/// </summary>
public static class RecipeInstances
{
    /// <summary>묶음 <paramref name="instanceId"/>의 구성 효과 번호(목록 순서).</summary>
    public static IEnumerable<int> Members(IReadOnlyList<LayerEffect> effects, string instanceId)
    {
        for (int i = 0; i < effects.Count; i++)
            if (effects[i].Recipe?.Instance.Id == instanceId) yield return i;
    }

    /// <summary>
    /// 매개변수의 지금 값: 연결된 설정 중 목록에 남아 있는 첫 설정의 값(매개변수가 받는 값으로 맞춤). 연결된 효과가 모두 없어졌으면 기본값.
    /// </summary>
    public static RecipeValue ValueOf(IReadOnlyList<LayerEffect> effects, EffectRecipeInstance instance, RecipeParameter parameter)
    {
        foreach (var binding in parameter.Bindings)
            foreach (int i in Members(effects, instance.Id))
                if (effects[i].Recipe!.Slot == binding.Effect && RecipeParameters.ValueOf(effects[i], binding.Key) is { } value && value.Kind == parameter.Kind)
                    return parameter.Clamp(value);
        return parameter.Default;
    }

    /// <summary>
    /// 묶음 <paramref name="instanceId"/>의 매개변수 <paramref name="parameterId"/>를 <paramref name="value"/>로 바꾼 새 목록: 연결된 모든 구성 효과의 설정을 한 번에 바꾼다.
    /// 정수는 매개변수 범위로 자른다. 묶음·매개변수가 없거나, 종류가 다르거나, 내보내지 않은 고르기 값이면 <paramref name="effects"/> 그대로.
    /// </summary>
    public static IReadOnlyList<LayerEffect> WithValue(IReadOnlyList<LayerEffect> effects, string instanceId, string parameterId, RecipeValue value)
    {
        if (Find(effects, instanceId) is not { } instance || instance.Parameters.FirstOrDefault(p => p.Id == parameterId) is not { } parameter
            || value.Kind != parameter.Kind
            || (value.Kind == RecipeParameterKind.Choice && !parameter.Choices.Contains(value.Choice, StringComparer.Ordinal)))
            return effects;
        value = parameter.Clamp(value);
        LayerEffect[]? result = null;
        foreach (int i in Members(effects, instanceId))
        {
            var effect = effects[i];
            foreach (var binding in parameter.Bindings)
                if (binding.Effect == effect.Recipe!.Slot)
                    effect = RecipeParameters.With(effect, binding.Key, value);
            if (effect == effects[i]) continue;
            result ??= [.. effects];
            result[i] = effect;
        }
        return result ?? effects;
    }

    /// <summary>묶음을 풀어 구성 효과를 보통 효과로 둔 새 목록(설정 값은 그대로).</summary>
    public static IReadOnlyList<LayerEffect> Detach(IReadOnlyList<LayerEffect> effects, string instanceId)
        => effects.Select(effect => effect.Recipe?.Instance.Id == instanceId ? effect with { Recipe = null } : effect).ToArray();

    /// <summary>목록에서 묶음 <paramref name="instanceId"/>의 정의(첫 구성 효과의 것, 없으면 null).</summary>
    public static EffectRecipeInstance? Find(IReadOnlyList<LayerEffect> effects, string instanceId)
    {
        foreach (var effect in effects)
            if (effect.Recipe is { } link && link.Instance.Id == instanceId)
                return link.Instance;
        return null;
    }
}
