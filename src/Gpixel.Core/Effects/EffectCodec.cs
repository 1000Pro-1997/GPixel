using System.Text.Json;
using System.Text.Json.Nodes;
using Gpixel.Core.IO;
using Gpixel.Core.Model;

namespace Gpixel.Core.Effects;

/// <summary>
/// 효과 하나의 저장 JSON(프로젝트, 사용자 정의 효과, 실행취소 기록이 같이 씀):
/// <code>{ "type": "gpixel.effect.outline", "version": 1, "enabled": true, "clipToPrevious": false, "frames": [0, 3],
///   "settings": { "color": "#000000", "thickness": 1, … }, "colorGradients": { "Color": { … } } }</code>
/// <c>frames</c>는 범위가 있을 때만, <c>colorGradients</c>는 그라데이션이 있을 때만, <c>recipe</c>는 매개변수 레시피 묶음의 구성 효과일 때만 쓴다
/// (<see cref="RecipeParameterFormat"/> — 프로젝트 형식 4의 선택 필드, 모르는 앱은 무시하고 보통 효과로 읽음). 스크립트 효과는 원문 해시 <c>script</c>가 붙는다(<see cref="ScriptDefinition"/>).
/// <para>
/// 읽기: 등록된 종류 ID면 새 형식, 예전 짧은 ID("outline")면 설정이 효과 객체에 바로 있는 예전 형식(프로젝트 형식 3 이하, 사용자 정의 효과 형식 1)으로 읽는다.
/// 모르는 종류, 더 새 설정 버전, 읽을 수 없는 값은 <see cref="MissingEffect"/>로 원본을 보존한다.
/// </para>
/// </summary>
public static class EffectCodec
{
    public const string TypeKey = "type", VersionKey = "version", EnabledKey = "enabled", ClipKey = "clipToPrevious", FramesKey = "frames",
        SettingsKey = "settings", GradientsKey = "colorGradients";

    /// <summary>효과의 저장 JSON. 정의가 없는 효과 타입이면 <see cref="NotSupportedException"/>.</summary>
    public static JsonObject Write(LayerEffect effect)
    {
        if (effect is MissingEffect missing) return WriteMissing(missing);
        var definition = EffectRegistry.Of(effect)
                         ?? throw new NotSupportedException(Localization.DomainStrings.FormatProject_UnsavableEffect(type: effect.GetType().Name));
        var json = new JsonObject
        {
            [TypeKey] = definition.TypeId,
            [VersionKey] = definition.Version,
            [EnabledKey] = effect.Enabled,
            [ClipKey] = effect.ClipToPrevious,
        };
        if (effect.Frames is { } f) json[FramesKey] = new JsonArray(f.From, f.To);
        json[SettingsKey] = definition.WriteSettings(effect);
        definition.WriteRoot(effect, json);
        if (effect.ColorGradients.Count > 0) json[GradientsKey] = WriteGradients(effect.ColorGradients);
        if (effect.Recipe is { } link) json[RecipeParameterFormat.LinkKey] = RecipeParameterFormat.WriteLink(link);
        return json;
    }

    /// <summary>
    /// 저장 JSON의 효과. 늘 효과를 돌려준다 — 읽을 수 없으면 <see cref="MissingEffect"/>(원본 보존, 부르는 쪽이 경고).
    /// 프레임 범위는 음수만 걸러 두고, 프레임 수에 맞추기는 부르는 쪽이 프레임을 읽은 뒤에 한다.
    /// </summary>
    public static LayerEffect Read(JsonObject json)
    {
        LayerEffect? effect;
        try
        {
            effect = ReadKnown(json);
        }
        catch (JsonException)
        {
            effect = null;
        }
        // 모르는 효과도 레시피 묶음 자리는 읽는다(묶음에서 빠지지 않게 — 쓸 때는 지금 자리를 다시 씀).
        effect ??= new MissingEffect
        {
            TypeId = EffectJson.String(json[TypeKey]) ?? "", Json = json.ToJsonString(), Recipe = RecipeParameterFormat.ReadLink(json[RecipeParameterFormat.LinkKey]),
        };
        return effect with
        {
            Enabled = EffectJson.Bool(json[EnabledKey]) ?? true,
            ClipToPrevious = EffectJson.Bool(json[ClipKey]) ?? false,
            Frames = EffectJson.Frames(json[FramesKey]),
        };
    }

    private static LayerEffect? ReadKnown(JsonObject json)
    {
        var type = EffectJson.String(json[TypeKey]);
        if (EffectRegistry.Find(type) is { } definition)
        {
            int version = json.ContainsKey(VersionKey) ? EffectJson.Int(json[VersionKey]) ?? 0 : 1;
            if (version < 1 || version > definition.Version) return null;
            var settings = json[SettingsKey];
            if (settings is not (null or JsonObject)) return null;
            var effect = definition.Read(settings as JsonObject ?? [], json);
            if (effect is null) return null;
            effect = WithGradients(effect, json[GradientsKey]);
            return json[RecipeParameterFormat.LinkKey] is { } link ? effect with { Recipe = RecipeParameterFormat.ReadLink(link) } : effect;
        }
        if (EffectRegistry.FindLegacy(type) is var (legacy, legacyId))
        {
            var effect = legacy.ReadLegacy(legacyId, json, out bool handlesGradients);
            return handlesGradients ? effect : WithGradients(effect, json[GradientsKey]);
        }
        return null;
    }

    private static LayerEffect WithGradients(LayerEffect effect, JsonNode? node)
    {
        var gradients = ReadGradients(node);
        return gradients.Count == 0 ? effect : effect with { ColorGradients = gradients };
    }

    /// <summary>색 설정별 그라데이션 JSON(키 = 색 슬롯). 값이 없거나 빈 슬롯은 건너뛴다. 모양이 틀리면 <see cref="JsonException"/>.</summary>
    public static IReadOnlyDictionary<string, EffectColorGradient> ReadGradients(JsonNode? node)
    {
        if (node is null) return System.Collections.Immutable.ImmutableDictionary<string, EffectColorGradient>.Empty;
        var dto = node.Deserialize<Dictionary<string, ProjectIO.ColorGradientDto?>>(ProjectIO.Options);
        if (dto is not { Count: > 0 }) return System.Collections.Immutable.ImmutableDictionary<string, EffectColorGradient>.Empty;
        return dto.Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Value!.ToGradient());
    }

    internal static JsonNode WriteGradients(IReadOnlyDictionary<string, EffectColorGradient> gradients)
        => JsonSerializer.SerializeToNode(gradients.ToDictionary(entry => entry.Key, entry => ProjectIO.ColorGradientDto.From(entry.Value)), ProjectIO.Options)!;

    /// <summary>
    /// 모르는 효과: 원본 JSON에서 켜짐·클리핑·프레임 범위·레시피 묶음 자리만 지금 값으로 바꾼다(원본에 없던 기본값 키는 더하지 않음).
    /// </summary>
    private static JsonObject WriteMissing(MissingEffect missing)
    {
        var json = JsonNode.Parse(missing.Json) as JsonObject ?? [];
        if (json.ContainsKey(EnabledKey) || !missing.Enabled) json[EnabledKey] = missing.Enabled;
        if (json.ContainsKey(ClipKey) || missing.ClipToPrevious) json[ClipKey] = missing.ClipToPrevious;
        if (missing.Frames is { } f) json[FramesKey] = new JsonArray(f.From, f.To);
        else json.Remove(FramesKey);
        if (missing.Recipe is { } link) json[RecipeParameterFormat.LinkKey] = RecipeParameterFormat.WriteLink(link);
        else json.Remove(RecipeParameterFormat.LinkKey);
        return json;
    }
}
