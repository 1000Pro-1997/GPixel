using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gpixel.Core.Effects;
using Gpixel.Core.Model;

namespace Gpixel.Core.IO;

/// <summary><see cref="EffectSettings.Merge"/>가 받은 설정의 문제.</summary>
public enum EffectSettingError
{
    /// <summary>이 효과 종류에 없는 키(<see cref="EffectSettingException.Detail"/> = 있는 키들).</summary>
    UnknownKey,
    /// <summary>종류·켜짐·프레임 범위·클리핑 키 — 설정이 아니라 따로 정한다.</summary>
    ReservedKey,
    /// <summary>값의 JSON 종류가 다름(<see cref="EffectSettingException.Detail"/> = 기대한 <see cref="EffectParameterKind"/>).</summary>
    WrongType,
    /// <summary>고를 수 있는 이름이 아님(<see cref="EffectSettingException.Detail"/> = 이름들).</summary>
    NotAChoice,
    InvalidColor,
    /// <summary>목록의 일부 항목이 버려짐(잘못된 색·모양, 개수 제한).</summary>
    InvalidEntries,
    /// <summary>파일 형식으로 읽을 수 없음(<see cref="EffectSettingException.Detail"/> = 원인).</summary>
    InvalidValue,
}

/// <summary>효과 설정 오류. 메시지는 앱이 <see cref="Error"/>·<see cref="Key"/>·<see cref="Detail"/>로 화면 언어 문구를 만든다.</summary>
public sealed class EffectSettingException(string key, EffectSettingError error, string? detail = null)
    : ArgumentException($"{key}: {error}{(detail is null ? "" : " (" + detail + ")")}")
{
    public string Key { get; } = key;
    public EffectSettingError Error { get; } = error;
    public string? Detail { get; } = detail;
}

/// <summary>
/// 레이어 효과 설정을 그 정의의 명시적 schema(<see cref="EffectDefinition.Parameters"/>) JSON으로 읽고 고친다 — AI 도구가 효과 종류마다 따로 쓰지 않고
/// 모든 효과 종류를 같은 형식으로 다룬다. 설정 JSON은 저장 형식의 <c>settings</c>와 같고, 색 설정별 그라데이션(<see cref="GradientsKey"/>)을 더한다.
/// 종류·켜짐·프레임 범위·클리핑은 설정이 아니다(<see cref="ReservedKeys"/>).
/// </summary>
public static class EffectSettings
{
    /// <summary>모든 효과에 있는 색 설정별 그라데이션 키(저장 형식 그대로의 객체).</summary>
    public const string GradientsKey = EffectCodec.GradientsKey;

    /// <summary>설정 JSON에 넣을 수 없는 저장 키(효과 종류·버전·켜짐·클리핑·프레임 범위·설정 묶음).</summary>
    public static readonly IReadOnlyList<string> ReservedKeys =
        [EffectCodec.TypeKey, EffectCodec.VersionKey, EffectCodec.EnabledKey, EffectCodec.ClipKey, EffectCodec.FramesKey, EffectCodec.SettingsKey];

    private static readonly HashSet<string> ReservedNormalized = [.. ReservedKeys.Select(Normalize), "cliptoabove"];

    /// <summary>효과 설정(schema 키 전부 + 그라데이션이 있으면 <see cref="GradientsKey"/>). 모르는 효과면 빈 객체.</summary>
    public static JsonObject ToJson(LayerEffect effect)
    {
        var json = EffectRegistry.Of(effect)?.WriteSettings(effect) ?? [];
        if (effect.ColorGradients.Count > 0) json[GradientsKey] = EffectCodec.WriteGradients(effect.ColorGradients);
        return json;
    }

    /// <summary>효과 종류의 설정 schema(모르는 효과면 빈 목록 — 고칠 수 없음).</summary>
    public static IReadOnlyList<EffectParameter> Describe(LayerEffect effect) => EffectRegistry.Of(effect)?.Parameters ?? [];

    /// <summary>
    /// <paramref name="settings"/>(일부 키만, 키는 대소문자·밑줄 무시)를 <paramref name="effect"/> 위에 덮은 새 효과. 켜짐·프레임 범위·클리핑은 그대로다.
    /// 값이 null인 키는 그 종류의 기본값으로 돌아간다. 효과가 범위에 맞춰 바꾼 정수 설정은 <c>Clamped</c>에 "키 = 값"으로 돌려준다.
    /// 잘못된 키·값이면 <see cref="EffectSettingException"/>.
    /// </summary>
    public static (LayerEffect Effect, IReadOnlyList<string> Clamped) Merge(LayerEffect effect, JsonObject? settings)
    {
        if (settings is null || settings.Count == 0) return (effect, []);
        var parameters = Describe(effect);
        var clamped = new List<string>();
        foreach (var (rawKey, value) in settings)
        {
            var normalized = Normalize(rawKey);
            if (ReservedNormalized.Contains(normalized)) throw new EffectSettingException(rawKey, EffectSettingError.ReservedKey);
            if (normalized == Normalize(GradientsKey))
            {
                effect = effect with { ColorGradients = Gradients(value) };
                continue;
            }
            var parameter = parameters.FirstOrDefault(p => Normalize(p.Key) == normalized)
                            ?? throw new EffectSettingException(rawKey, EffectSettingError.UnknownKey,
                                string.Join(", ", parameters.Select(p => p.Key).Append(GradientsKey)));
            var asked = value is null ? parameter.DefaultValue : Check(parameter, value);
            if (!parameter.TryRead(effect, asked, out effect))
                throw new EffectSettingException(parameter.Key, EffectSettingError.WrongType, parameter.Kind.ToString());
            var result = parameter.Write(effect);
            if (parameter.Kind == EffectParameterKind.Int && !JsonNode.DeepEquals(result, asked))
                clamped.Add($"{parameter.Key} = {result.ToJsonString()}");
            else if (asked is JsonArray list && (result as JsonArray)?.Count != list.Count)
                throw new EffectSettingException(parameter.Key, EffectSettingError.InvalidEntries, $"{(result as JsonArray)?.Count ?? 0}/{list.Count}");
        }
        return (effect, clamped);
    }

    // ───────────── 내부 ─────────────

    private static IReadOnlyDictionary<string, EffectColorGradient> Gradients(JsonNode? value)
    {
        if (value is null) return System.Collections.Immutable.ImmutableDictionary<string, EffectColorGradient>.Empty;
        if (value is not JsonObject) throw new EffectSettingException(GradientsKey, EffectSettingError.WrongType, "Object");
        try
        {
            return EffectCodec.ReadGradients(value);
        }
        catch (JsonException ex)
        {
            throw new EffectSettingException(GradientsKey, EffectSettingError.InvalidValue, ex.Message);
        }
    }

    /// <summary>키 비교용: 밑줄·빼기·공백을 빼고 소문자("repeat_count" = "repeatCount").</summary>
    private static string Normalize(string key)
        => string.Concat(key.Where(c => c is not ('_' or '-' or ' '))).ToLowerInvariant();

    /// <summary>받은 값을 schema 종류의 저장 모양으로(글자로 적은 숫자·참거짓, 대소문자가 다른 선택 이름도 받음). 맞지 않으면 오류.</summary>
    private static JsonNode Check(EffectParameter parameter, JsonNode value)
    {
        var key = parameter.Key;
        switch (parameter.Kind)
        {
            case EffectParameterKind.Int:
                if (value is JsonValue v)
                {
                    if (v.TryGetValue(out int i)) return JsonValue.Create(i);
                    if (v.TryGetValue(out double d) && d == Math.Floor(d) && Math.Abs(d) <= int.MaxValue) return JsonValue.Create((int)d);
                    if (v.TryGetValue(out string? s) && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                        return JsonValue.Create(i);
                }
                break;
            case EffectParameterKind.Bool:
                if (value is JsonValue bv)
                {
                    if (bv.TryGetValue(out bool b)) return JsonValue.Create(b);
                    if (bv.TryGetValue(out string? s) && bool.TryParse(s.Trim(), out b)) return JsonValue.Create(b);
                }
                break;
            case EffectParameterKind.Choice:
                if (EffectJson.String(value) is { } name)
                {
                    var wanted = Normalize(name);
                    return JsonValue.Create(parameter.Choices!.FirstOrDefault(c => Normalize(c) == wanted)
                                            ?? throw new EffectSettingException(key, EffectSettingError.NotAChoice, string.Join(", ", parameter.Choices!)));
                }
                break;
            case EffectParameterKind.Color:
                if (EffectJson.String(value) is { } text)
                {
                    var t = text.Trim();
                    if (t.Equals("transparent", StringComparison.OrdinalIgnoreCase) || t.Equals("none", StringComparison.OrdinalIgnoreCase))
                        return JsonValue.Create(Color32.Transparent.ToHex());
                    if (!Color32.TryParseHex(t, out var color)) throw new EffectSettingException(key, EffectSettingError.InvalidColor, text);
                    return JsonValue.Create(color.ToHex());
                }
                break;
            case EffectParameterKind.ColorList:
            case EffectParameterKind.ColorPairList:
                if (value is JsonArray) return value.DeepClone();
                break;
        }
        throw new EffectSettingException(key, EffectSettingError.WrongType, parameter.Kind.ToString());
    }
}
