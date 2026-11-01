using System.Globalization;
using System.Text.Json.Nodes;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>설정 값의 종류(<see cref="EffectParameter.Kind"/>). 저장 JSON 모양도 정한다.</summary>
public enum EffectParameterKind
{
    /// <summary>정수(<see cref="EffectParameter.Min"/>~<see cref="EffectParameter.Max"/> — 범위 밖 값은 효과가 자르거나 돌려 넣는다).</summary>
    Int,
    Bool,
    /// <summary>"#RRGGBB" / "#RRGGBBAA". 그라데이션 슬롯은 <see cref="EffectParameter.ColorSlot"/>.</summary>
    Color,
    /// <summary>저장 ID 중 하나(<see cref="EffectParameter.Choices"/>).</summary>
    Choice,
    /// <summary>색 목록 ["#…", …]. 칸마다 그라데이션 슬롯 "{슬롯}[i]".</summary>
    ColorList,
    /// <summary>색 쌍 목록 [["#바꿀 색", "#바뀐 색"], …]. 칸마다 그라데이션 슬롯 "{슬롯}[i].From"·"{슬롯}[i].To".</summary>
    ColorPairList,
}

/// <summary>
/// 효과 설정 하나의 명시적 schema: 영구 키, 값 종류, 기본값, 범위, 고를 수 있는 값, 색 슬롯, 예전 키.
/// 저장·검증·AI 도구·레시피 매개변수가 같은 정의를 쓴다. 키는 모델 속성 이름의 camelCase이고 한 번 정하면 바꾸지 않는다.
/// </summary>
public abstract class EffectParameter
{
    private JsonNode? _default;

    private protected EffectParameter(string key, EffectParameterKind kind, int? min, int? max, IReadOnlyList<string>? choices, IReadOnlyList<string>? legacyKeys)
    {
        Key = key;
        Kind = kind;
        Min = min;
        Max = max;
        Choices = choices;
        LegacyKeys = legacyKeys is { Count: > 0 } ? legacyKeys : [key];
    }

    /// <summary>설정 JSON의 영구 키(camelCase).</summary>
    public string Key { get; }

    public EffectParameterKind Kind { get; }

    /// <summary>정수의 최소·최대(양 끝 포함). 정수가 아니면 null.</summary>
    public int? Min { get; }

    public int? Max { get; }

    /// <summary>고를 수 있는 저장 ID(<see cref="EffectParameterKind.Choice"/>만).</summary>
    public IReadOnlyList<string>? Choices { get; }

    /// <summary>예전 파일(짧은 효과 ID 형식)에서 이 설정을 찾을 키들(앞에서부터). 따로 정하지 않았으면 <see cref="Key"/> 하나.</summary>
    public IReadOnlyList<string> LegacyKeys { get; }

    /// <summary>
    /// 색 그라데이션 슬롯(<see cref="LayerEffect.ColorGradients"/> 키) — 모델 속성 이름(키의 첫 글자를 대문자로).
    /// 목록이면 칸 슬롯의 앞부분이다. 색이 아니면 null.
    /// </summary>
    public string? ColorSlot => Kind is EffectParameterKind.Color or EffectParameterKind.ColorList or EffectParameterKind.ColorPairList
        ? char.ToUpperInvariant(Key[0]) + Key[1..]
        : null;

    /// <summary>레시피 매개변수로 사용자에게 내보낼 수 있는지(값 하나짜리 설정만).</summary>
    public bool Exposed => Kind is not (EffectParameterKind.ColorList or EffectParameterKind.ColorPairList);

    /// <summary>그 종류 기본 효과의 값(저장 JSON 모양, 부를 때마다 새 사본).</summary>
    public JsonNode DefaultValue => _default!.DeepClone();

    internal void SetDefault(LayerEffect defaults) => _default = Write(defaults);

    /// <summary><paramref name="effect"/>의 이 설정 값(저장 JSON 모양).</summary>
    internal abstract JsonNode Write(LayerEffect effect);

    /// <summary>
    /// 저장 JSON 값 <paramref name="value"/>를 <paramref name="effect"/>에 넣은 새 효과. 값 모양이 맞지 않으면 false(효과는 그대로).
    /// 범위 밖 정수는 모델이 자르고, 목록의 잘못된 칸은 버린다.
    /// </summary>
    internal abstract bool TryRead(LayerEffect effect, JsonNode? value, out LayerEffect result);
}

/// <summary>모델 속성 하나에 묶인 설정(읽기·쓰기 함수와 JSON 변환).</summary>
internal sealed class EffectParameter<TEffect, TValue>(
    string key, EffectParameterKind kind, Func<TEffect, TValue> get, Func<TEffect, TValue, TEffect> set,
    Func<TValue, JsonNode> write, EffectJson.Parser<TValue> parse,
    int? min = null, int? max = null, IReadOnlyList<string>? choices = null, IReadOnlyList<string>? legacyKeys = null)
    : EffectParameter(key, kind, min, max, choices, legacyKeys)
    where TEffect : LayerEffect
{
    internal override JsonNode Write(LayerEffect effect) => write(get((TEffect)effect));

    internal override bool TryRead(LayerEffect effect, JsonNode? value, out LayerEffect result)
    {
        if (!parse(value, out var parsed))
        {
            result = effect;
            return false;
        }
        result = set((TEffect)effect, parsed);
        return true;
    }
}

/// <summary>
/// 효과 정의가 설정 schema를 적는 도구: <c>s.Int("thickness", 1, 3, e =&gt; e.Thickness, (e, v) =&gt; e with { Thickness = v })</c>.
/// <c>legacy</c>는 예전 파일에서 그 설정을 찾을 키(앞에서부터, 없으면 새 키 그대로).
/// </summary>
internal sealed class EffectSchema<TEffect> where TEffect : LayerEffect
{
    public EffectParameter Int(string key, int min, int max, Func<TEffect, int> get, Func<TEffect, int, TEffect> set, string[]? legacy = null)
        => new EffectParameter<TEffect, int>(key, EffectParameterKind.Int, get, set, v => JsonValue.Create(v), EffectJson.TryInt, min, max, legacyKeys: legacy);

    public EffectParameter Bool(string key, Func<TEffect, bool> get, Func<TEffect, bool, TEffect> set, string[]? legacy = null)
        => new EffectParameter<TEffect, bool>(key, EffectParameterKind.Bool, get, set, v => JsonValue.Create(v), EffectJson.TryBool, legacyKeys: legacy);

    public EffectParameter Color(string key, Func<TEffect, Color32> get, Func<TEffect, Color32, TEffect> set, string[]? legacy = null)
        => new EffectParameter<TEffect, Color32>(key, EffectParameterKind.Color, get, set, v => JsonValue.Create(v.ToHex()), EffectJson.TryColor, legacyKeys: legacy);

    /// <summary>열거형 설정. 저장 ID = 열거 이름. <paramref name="choices"/>를 주면 그 값만 고를 수 있다(모델이 나머지를 바꿔 넣는 경우).</summary>
    public EffectParameter Choice<TEnum>(string key, Func<TEffect, TEnum> get, Func<TEffect, TEnum, TEffect> set, string[]? legacy = null, TEnum[]? choices = null)
        where TEnum : struct, Enum
        => new EffectParameter<TEffect, TEnum>(key, EffectParameterKind.Choice, get, set, v => JsonValue.Create(v.ToString()), EffectJson.TryEnum,
            choices: (choices ?? Enum.GetValues<TEnum>()).Select(v => v.ToString()).ToArray(), legacyKeys: legacy);

    public EffectParameter ColorList(string key, Func<TEffect, IReadOnlyList<Color32>> get, Func<TEffect, IReadOnlyList<Color32>, TEffect> set, string[]? legacy = null)
        => new EffectParameter<TEffect, IReadOnlyList<Color32>>(key, EffectParameterKind.ColorList, get, set,
            v => new JsonArray(v.Select(c => (JsonNode)JsonValue.Create(c.ToHex())).ToArray()), EffectJson.TryColors, legacyKeys: legacy);

    public EffectParameter ColorPairs(string key, Func<TEffect, IReadOnlyList<ColorSwap>> get, Func<TEffect, IReadOnlyList<ColorSwap>, TEffect> set, string[]? legacy = null)
        => new EffectParameter<TEffect, IReadOnlyList<ColorSwap>>(key, EffectParameterKind.ColorPairList, get, set,
            v => new JsonArray(v.Select(p => (JsonNode)new JsonArray(JsonValue.Create(p.From.ToHex()), JsonValue.Create(p.To.ToHex()))).ToArray()),
            EffectJson.TryColorPairs, legacyKeys: legacy);
}

/// <summary>효과 JSON 값 읽기(모양이 틀리면 false — 파일의 잘못된 값은 기본값으로 둔다).</summary>
internal static class EffectJson
{
    public delegate bool Parser<T>(JsonNode? node, out T value);

    public static string? String(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// <summary>정수: 숫자(소수점 아래가 0이면 받음) 또는 숫자 글자.</summary>
    public static bool TryInt(JsonNode? node, out int value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue(out value)) return true;
        if (v.TryGetValue(out double d) && d == Math.Floor(d) && Math.Abs(d) <= int.MaxValue)
        {
            value = (int)d;
            return true;
        }
        return v.TryGetValue(out string? s) && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public static int? Int(JsonNode? node) => TryInt(node, out int v) ? v : null;

    public static bool TryBool(JsonNode? node, out bool value)
    {
        value = false;
        return node is JsonValue v && v.TryGetValue(out value);
    }

    public static bool? Bool(JsonNode? node) => TryBool(node, out bool v) ? v : null;

    public static bool TryColor(JsonNode? node, out Color32 value)
    {
        value = default;
        return String(node) is { } hex && Color32.TryParseHex(hex, out value);
    }

    /// <summary>열거 이름(대소문자 무시, 정의된 값만).</summary>
    public static bool TryEnum<T>(JsonNode? node, out T value) where T : struct, Enum
    {
        value = default;
        return String(node) is { } name && Enum.TryParse(name, true, out value) && Enum.IsDefined(value);
    }

    public static T EnumOr<T>(JsonNode? node, T fallback) where T : struct, Enum => TryEnum(node, out T v) ? v : fallback;

    public static Color32 ColorOr(JsonNode? node, Color32 fallback) => TryColor(node, out var c) ? c : fallback;

    /// <summary>색 목록(잘못된 칸은 버림).</summary>
    public static bool TryColors(JsonNode? node, out IReadOnlyList<Color32> value)
    {
        value = [];
        if (node is not JsonArray array) return false;
        var list = new List<Color32>(array.Count);
        foreach (var item in array)
            if (TryColor(item, out var c)) list.Add(c);
        value = list;
        return true;
    }

    /// <summary>색 쌍 목록(두 색이 모두 맞는 쌍만).</summary>
    public static bool TryColorPairs(JsonNode? node, out IReadOnlyList<ColorSwap> value)
    {
        value = [];
        if (node is not JsonArray array) return false;
        var list = new List<ColorSwap>(array.Count);
        foreach (var item in array)
            if (item is JsonArray { Count: 2 } pair && TryColor(pair[0], out var from) && TryColor(pair[1], out var to))
                list.Add(new ColorSwap(from, to));
        value = list;
        return true;
    }

    /// <summary>프레임 범위 [시작, 끝](음수는 0, 순서가 바뀌었으면 바로잡음). 모양이 틀리면 null(모든 프레임).</summary>
    public static FrameRange? Frames(JsonNode? node)
        => node is JsonArray { Count: 2 } a && TryInt(a[0], out int from) && TryInt(a[1], out int to)
            ? new FrameRange(Math.Max(0, Math.Min(from, to)), Math.Max(0, Math.Max(from, to)))
            : null;
}
