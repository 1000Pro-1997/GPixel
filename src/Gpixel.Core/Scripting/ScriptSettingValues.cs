using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gpixel.Core.Model;

namespace Gpixel.Core.Scripting;

/// <summary>
/// 스크립트 효과의 설정값(키 순서로 정렬한 불변 표, 값으로 비교). 효과 렌더 캐시 열쇠(<c>EffectListKey</c>)가 값 비교에 기대므로
/// 같은 설정이면 언제 만들었든 같고 해시도 같다.
/// <para>
/// 값은 저장 JSON과 같은 모양으로 정규화한다: 숫자는 <see cref="double"/>, 참거짓은 <see cref="bool"/>, 글(고르기 값·색 16진수)은 <see cref="string"/>,
/// 그 밖의 JSON(배열·객체·null)은 원문 그대로 <see cref="ScriptRawValue"/>. 그래서 원문을 모르는 스크립트의 설정도 바꾸지 않고 다시 쓴다.
/// <see cref="ScriptEffectProgram.NormalizeSettings"/>에 그대로 넘기면 schema에 맞춘 값이 된다(정수는 반올림, 색은 16진수를 읽음).
/// </para>
/// </summary>
public sealed class ScriptSettingValues : IReadOnlyDictionary<string, object>, IEquatable<ScriptSettingValues>
{
    public static ScriptSettingValues Empty { get; } = new(ImmutableSortedDictionary.Create<string, object>(StringComparer.Ordinal));

    private readonly ImmutableSortedDictionary<string, object> _values;
    private readonly int _hash;

    private ScriptSettingValues(ImmutableSortedDictionary<string, object> values)
    {
        _values = values;
        var h = new HashCode();
        foreach (var (key, value) in values)
        {
            h.Add(key);
            h.Add(value);
        }
        _hash = h.ToHashCode();
    }

    /// <summary>C# 값(<see cref="int"/>, <see cref="double"/>, <see cref="bool"/>, <see cref="string"/>, <see cref="Color32"/> 등)으로 만든 표. 같은 키는 뒤의 값.</summary>
    public static ScriptSettingValues From(IEnumerable<KeyValuePair<string, object>> values)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in values) builder[key] = Canonical(value);
        return builder.Count == 0 ? Empty : new(builder.ToImmutable());
    }

    /// <summary>저장 JSON의 설정 객체(모든 키, 값 모양 그대로).</summary>
    public static ScriptSettingValues FromJson(JsonObject json, Func<string, bool>? include = null)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, object>(StringComparer.Ordinal);
        foreach (var (key, node) in json)
            if (include?.Invoke(key) != false)
                builder[key] = FromNode(node);
        return builder.Count == 0 ? Empty : new(builder.ToImmutable());
    }

    /// <summary><paramref name="program"/>의 schema에 맞춘 값(빠진 키는 기본값, 범위 밖은 자름, 모르는 키는 버림).</summary>
    public static ScriptSettingValues Normalized(ScriptEffectProgram program, IReadOnlyDictionary<string, object>? values)
        => From(program.NormalizeSettings(values));

    /// <summary><paramref name="key"/>만 <paramref name="value"/>(C# 값)로 바꾼 표.</summary>
    public ScriptSettingValues With(string key, object value) => new(_values.SetItem(key, Canonical(value)));

    /// <summary>
    /// 저장 JSON 설정 객체. <paramref name="order"/>(schema 순서)의 키를 먼저, 나머지는 키 순서로 쓴다.
    /// </summary>
    public JsonObject ToJson(IEnumerable<string>? order = null)
    {
        var json = new JsonObject();
        if (order is not null)
            foreach (var key in order)
                if (_values.TryGetValue(key, out var value))
                    json[key] = ToNode(value);
        foreach (var (key, value) in _values)
            if (!json.ContainsKey(key))
                json[key] = ToNode(value);
        return json;
    }

    private static object Canonical(object value) => value switch
    {
        double d => d,
        int i => (double)i,
        long l => (double)l,
        float f => (double)f,
        decimal m => (double)m,
        short s => (double)s,
        byte b => (double)b,
        bool b => b,
        string s => s,
        Color32 c => c.ToHex(),
        ScriptRawValue raw => raw,
        JsonNode node => FromNode(node),
        _ => throw new ArgumentException(value.GetType().Name, nameof(value)),
    };

    private static object FromNode(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            switch (v.GetValueKind())
            {
                case JsonValueKind.Number when v.TryGetValue(out double d) && double.IsFinite(d):
                    return d;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return v.GetValue<bool>();
                case JsonValueKind.String:
                    return v.GetValue<string>();
            }
        }
        return new ScriptRawValue(node?.ToJsonString() ?? "null");
    }

    private static JsonNode? ToNode(object value) => value switch
    {
        double d => JsonValue.Create(d),
        bool b => JsonValue.Create(b),
        string s => JsonValue.Create(s),
        ScriptRawValue raw => JsonNode.Parse(raw.Json),
        _ => null,
    };

    public bool Equals(ScriptSettingValues? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || other._hash != _hash || other._values.Count != _values.Count) return false;
        foreach (var (key, value) in _values)
            if (!other._values.TryGetValue(key, out var theirs) || !value.Equals(theirs))
                return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is ScriptSettingValues other && Equals(other);
    public override int GetHashCode() => _hash;

    public override string ToString()
        => string.Join(", ", _values.Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Key}={entry.Value}")));

    public object this[string key] => _values[key];
    public IEnumerable<string> Keys => _values.Keys;
    public IEnumerable<object> Values => _values.Values;
    public int Count => _values.Count;
    public bool ContainsKey(string key) => _values.ContainsKey(key);
    public bool TryGetValue(string key, out object value) => _values.TryGetValue(key, out value!);
    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>숫자·참거짓·글이 아닌 설정 JSON 값(배열·객체·null) — 원문 한 줄 그대로 보존한다.</summary>
public readonly record struct ScriptRawValue(string Json)
{
    public override string ToString() => Json;
}
