using System.Text.Json;
using System.Text.Json.Nodes;
using Gpixel.Core.Assets;
using Gpixel.Core.Effects;
using Gpixel.Core.Model;
using Gpixel.Core.Scripting;

namespace Gpixel.Core.IO;

/// <summary>레시피를 넣을 때 쓸 프레임 범위(권장 정책).</summary>
public enum RecipeFrameRange
{
    /// <summary>효과 창의 "추가 범위"(AI 도구는 받은 범위)를 쓴다.</summary>
    AddRange,
    /// <summary>늘 레이어의 모든 프레임.</summary>
    AllFrames,
}

/// <summary>
/// 효과 레시피(계획 4.2): 기본 효과 여러 개와 설정, 순서, 클리핑 관계를 묶은 데이터. 넣을 때 효과 값을 복사하므로(<see cref="Instantiate"/>)
/// 레시피를 지우거나 끄거나 업데이트해도 이미 넣은 프로젝트는 바뀌지 않는다. 효과는 프레임 번호에 묶이지 않는다.
/// 작성자가 내보낸 매개변수(<see cref="Parameters"/>)가 있으면 넣은 효과들이 한 묶음(<see cref="EffectRecipeInstance"/>)이 되어 매개변수 값만으로 함께 바뀐다.
/// 스크립트 효과가 있으면 그 원문(<see cref="Scripts"/>)을 함께 담아, 넣을 때 대상 프로젝트의 스크립트 표에 복사한다(2.4 — 설치 에셋에 기대지 않음).
/// </summary>
public sealed record EffectRecipe : IAssetData
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public IReadOnlyList<LayerEffect> Effects { get; init; } = [];
    public RecipeFrameRange FrameRange { get; init; } = RecipeFrameRange.AddRange;
    /// <summary>미리보기 애니메이션에 권장하는 프레임 수.</summary>
    public int PreviewFrames { get; init; } = DefaultPreviewFrames;
    /// <summary>사용자에게 내보낸 매개변수(없으면 효과 목록 전체를 그대로 복사해 넣음). <see cref="WithParameters"/>로 바꾼다.</summary>
    public IReadOnlyList<RecipeParameter> Parameters { get; init; } = [];
    /// <summary>스크립트 효과의 원문(해시 → 정리한 원문, 해시 순서). 효과가 쓰는 해시 중 원문을 아는 것만 있다.</summary>
    public IReadOnlyDictionary<string, string> Scripts { get; init; } = EmptyScripts;

    public const int DefaultPreviewFrames = 12, MaxPreviewFrames = 240;

    private static readonly IReadOnlyDictionary<string, string> EmptyScripts = new SortedDictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// 프로젝트 효과 목록의 사본(프레임 범위와 묶음 정보를 지우고 첫 효과의 클리핑을 끔). 스크립트 효과의 원문은 <paramref name="project"/>의
    /// 스크립트 표에서(없으면 앱이 아는 원문 <see cref="ScriptLibrary"/>에서) 가져온다.
    /// </summary>
    public static EffectRecipe Capture(string id, string name, IEnumerable<LayerEffect> effects, SpriteProject? project = null)
    {
        var list = effects.Select((effect, index) => effect with
        {
            Frames = null,
            ClipToPrevious = index > 0 && effect.ClipToPrevious,
            Recipe = null,
        }).ToArray();
        var scripts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var script in list.OfType<ScriptEffect>())
            if ((project is null ? ScriptLibrary.SourceOf(script.ScriptHash) : project.ScriptSourceOf(script.ScriptHash)) is { } source)
                scripts[script.ScriptHash] = source;
        return new() { Id = id, Name = name.Trim(), Effects = list, Scripts = scripts };
    }

    /// <summary>매개변수를 바꾸고 효과 목록에 기본값을 넣은 레시피(효과 미리보기·넣은 결과가 기본값과 같게).</summary>
    public EffectRecipe WithParameters(IReadOnlyList<RecipeParameter> parameters)
        => this with { Parameters = parameters, Effects = RecipeParameters.ApplyDefaults(Effects, parameters) };

    /// <summary>
    /// <paramref name="project"/>에 넣을 독립 효과 목록. <paramref name="addRange"/>는 부르는 쪽의 추가 범위이고,
    /// <see cref="RecipeFrameRange.AllFrames"/> 레시피는 그 대신 모든 프레임을 쓴다.
    /// 매개변수가 있으면 기본값을 넣고, 새 묶음(고유 ID + 매개변수 정의 사본)을 만들어 효과마다 레시피 안 자리와 함께 단다.
    /// 스크립트 원문은 프로젝트 스크립트 표에 넣는다(실행 허락은 바꾸지 않음 — 패키지에서 온 원문은 효과 창에서 허용할 때까지 그리지 않는다).
    /// </summary>
    public IReadOnlyList<LayerEffect> Instantiate(SpriteProject project, FrameRange? addRange)
    {
        foreach (var source in Scripts.Values) project.AddScript(source);
        var frames = FrameRange == RecipeFrameRange.AllFrames ? null : addRange;
        var instance = Parameters.Count == 0
            ? null
            : new EffectRecipeInstance
            {
                Id = Guid.NewGuid().ToString("N"), Name = Name, SourceId = Id.Length > 0 ? Id : null, Parameters = Parameters,
            };
        return RecipeParameters.ApplyDefaults(Effects, Parameters).Select((effect, index) => effect with
        {
            Frames = frames,
            ClipToPrevious = index > 0 && effect.ClipToPrevious,
            Recipe = instance is null ? null : new RecipeLink(instance, index),
        }).ToArray();
    }
}

/// <summary>
/// 효과 레시피 JSON(계획 6.3) — 패키지의 <c>assets/effects/*.json</c>과 개인 에셋이 같이 쓴다:
/// <code>{ "schemaVersion": 3, "id": "…", "name": "Neon Hit", "description": "…", "frameRange": "addRange", "previewFrames": 12,
///   "effects": [ { "type": "gpixel.effect.outline", "version": 1, "enabled": true, "clipToPrevious": false, "settings": { … } },
///                { "type": "gpixel.effect.script", "settings": { … }, "script": "&lt;SHA-256&gt;" } ],
///   "parameters": [ { "id": "glow", "name": "빛 색", "kind": "color", "default": "#00FFC8", "bindings": [ { "effect": 0, "key": "color" } ] } ],
///   "scripts": { "&lt;SHA-256&gt;": "effect({ … })" } }</code>
/// 효과 하나는 프로젝트와 같은 <see cref="EffectCodec"/> 형식이고(<c>frames</c>는 쓰지 않음), 순서 = 목록 순서, 클리핑 = <c>clipToPrevious</c>.
/// 매개변수(<see cref="RecipeParameterFormat"/>)는 형식 2부터, 스크립트 원문 <c>scripts</c>(해시 → 정리한 원문, 해시 순서)는 형식 3부터다 —
/// 그것이 없으면 낮은 형식으로 써서 예전 앱도 읽는다. 원문 글은 문자열 한도 대신 <see cref="AssetPackageLimits.MaxScriptBytes"/>로 잰다.
/// 엄격 모드는 효과를 등록된 schema로 검사한다: 등록된 종류 ID와 지원 버전, 모르는 설정 키 없음, 값 모양·범위·고를 수 있는 값,
/// 색 슬롯에 맞는 그라데이션. 스크립트 효과는 원문이 레시피에 있어야 하고, 원문은 해시가 맞고 해석되어야 하며(<see cref="ScriptAssetFormat.Check"/>),
/// 설정은 그 원문이 선언한 schema로 검사한다. 쓰지 않는 원문도 거부한다.
/// 매개변수는 <see cref="RecipeParameters.Check"/>로 연결과 범위를 검사한다(느슨한 모드는 틀린 매개변수만 뺀다).
/// 느슨한 모드(개인 에셋)는 원문을 실행하지 않고 해시만 맞춰 보며, 해시가 틀린 원문은 버린다(효과는 원문 없음으로 남음).
/// </summary>
public static class EffectRecipeFormat
{
    public const int SchemaVersion = 3;

    /// <summary>스크립트 원문 표의 맨 위 키.</summary>
    public const string ScriptsKey = "scripts";

    private static readonly HashSet<string> Keys = ["schemaVersion", "id", "name", "description", "frameRange", "previewFrames", "effects", "parameters", ScriptsKey];

    private static readonly HashSet<string> EffectKeys =
    [
        EffectCodec.TypeKey, EffectCodec.VersionKey, EffectCodec.EnabledKey, EffectCodec.ClipKey, EffectCodec.SettingsKey, EffectCodec.GradientsKey,
    ];

    public static JsonObject Write(EffectRecipe recipe)
    {
        var effects = new JsonArray();
        for (int i = 0; i < recipe.Effects.Count; i++)
        {
            var effect = recipe.Effects[i] with { Frames = null, ClipToPrevious = i > 0 && recipe.Effects[i].ClipToPrevious, Recipe = null };
            effects.Add(EffectCodec.Write(effect));
        }
        // 효과가 쓰는 원문만(엄격 읽기가 쓰지 않는 원문을 거부하므로)
        var used = recipe.Effects.OfType<ScriptEffect>().Select(e => e.ScriptHash).ToHashSet(StringComparer.Ordinal);
        var scripts = new JsonObject();
        foreach (var (hash, source) in recipe.Scripts.Where(pair => used.Contains(pair.Key)).OrderBy(pair => pair.Key, StringComparer.Ordinal))
            scripts[hash] = source;
        var json = new JsonObject
        {
            ["schemaVersion"] = scripts.Count > 0 ? SchemaVersion : recipe.Parameters.Count > 0 ? 2 : 1,
            ["id"] = recipe.Id,
            ["name"] = recipe.Name.Trim(),
        };
        if (recipe.Description is { Length: > 0 } description) json["description"] = description;
        json["frameRange"] = recipe.FrameRange == RecipeFrameRange.AllFrames ? "allFrames" : "addRange";
        json["previewFrames"] = recipe.PreviewFrames;
        json["effects"] = effects;
        if (recipe.Parameters.Count > 0) json["parameters"] = RecipeParameterFormat.Write(recipe.Parameters);
        if (scripts.Count > 0) json[ScriptsKey] = scripts;
        return json;
    }

    /// <summary>레시피를 읽는다. 문제가 하나라도 있으면 <paramref name="context"/>에 남기고 null.</summary>
    public static EffectRecipe? Read(JsonNode? node, AssetReadContext context)
    {
        int before = context.Issues.Count;
        if (node is not JsonObject o)
        {
            context.Add(AssetIssueCode.InvalidJson);
            return null;
        }
        if (!AssetJson.TryInt(o["schemaVersion"], out int schema) || schema < 1)
        {
            context.Bad("schemaVersion");
            return null;
        }
        if (schema > SchemaVersion)
        {
            context.Add(AssetIssueCode.FutureSchema, schema.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return null;
        }
        if (context.Strict)
            foreach (var (key, _) in o)
                if (!Keys.Contains(key)) context.Bad(key);

        string id = AssetJson.String(o["id"]) ?? "";
        if (id.Length == 0) context.Bad("id");
        string name = AssetJson.String(o["name"]) ?? "";
        if (!AssetManifest.IsValidName(name)) context.Bad("name");
        string? description = AssetJson.String(o["description"]);
        if (o["description"] is not null && (description is null || description.Length > AssetManifest.MaxDescriptionLength)) context.Bad("description");

        var frameRange = RecipeFrameRange.AddRange;
        switch (AssetJson.String(o["frameRange"]))
        {
            case null when o["frameRange"] is null: break;
            case "addRange": break;
            case "allFrames": frameRange = RecipeFrameRange.AllFrames; break;
            default: context.Bad("frameRange"); break;
        }
        int previewFrames = EffectRecipe.DefaultPreviewFrames;
        if (o["previewFrames"] is { } framesNode
            && (!AssetJson.TryInt(framesNode, out previewFrames) || previewFrames is < 1 or > EffectRecipe.MaxPreviewFrames))
        {
            context.Bad("previewFrames");
            previewFrames = EffectRecipe.DefaultPreviewFrames;
        }

        var scripts = o[ScriptsKey] is { } scriptsNode ? ReadScripts(scriptsNode, schema, context) : new Dictionary<string, RecipeScript>(StringComparer.Ordinal);

        var effects = new List<LayerEffect>();
        if (o["effects"] is JsonArray list && list.Count > 0)
        {
            if (context.Strict && list.Count > context.Limits.MaxEffectsPerRecipe) context.Add(AssetIssueCode.TooManyItems, "effects");
            else
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is not JsonObject effectJson)
                    {
                        context.Bad($"effects[{i}]");
                        continue;
                    }
                    if (context.Strict && !CheckEffect(effectJson, $"effects[{i}]", scripts, context)) continue;
                    var effect = EffectCodec.Read(effectJson);
                    if (context.Strict && effect is MissingEffect)
                    {
                        context.Add(AssetIssueCode.InvalidEffectSetting, $"effects[{i}]");
                        continue;
                    }
                    effects.Add(effect with { Frames = null, ClipToPrevious = effects.Count > 0 && effect.ClipToPrevious, Recipe = null });
                }
        }
        else context.Bad("effects");

        // 엄격 모드: 효과가 쓰지 않는 원문은 받지 않는다(설치 전 검사를 거치지 않는 원문을 실어 나르지 않게).
        var used = effects.OfType<ScriptEffect>().Select(e => e.ScriptHash).ToHashSet(StringComparer.Ordinal);
        if (context.Strict)
            foreach (var hash in scripts.Keys.Where(hash => !used.Contains(hash)))
                context.Bad($"{ScriptsKey}.{ShortHash(hash)}");

        var parameters = o["parameters"] is { } parametersNode ? ReadParameters(parametersNode, schema, effects, context, before) : [];

        if (context.Issues.Count > before) return null;
        var sources = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (hash, script) in scripts)
            if (used.Contains(hash))
            {
                sources[hash] = script.Source;
                // 효과 이름·미리보기가 해시로 찾게 원문을 앱에 알린다(실행하지 않음 — 실행 허락은 따로).
                ScriptLibrary.Add(script.Source);
            }
        return new EffectRecipe
        {
            Id = id, Name = name.Trim(), Description = description, Effects = effects,
            FrameRange = frameRange, PreviewFrames = previewFrames, Scripts = sources,
        }.WithParameters(parameters);
    }

    /// <summary>레시피 안 원문 하나: 정리한 원문과(엄격 모드면) 검사용으로 해석한 결과.</summary>
    private sealed record RecipeScript(string Source, ScriptEffectProgram? Program);

    private static string ShortHash(string hash) => hash.Length > 12 ? hash[..12] : hash;

    /// <summary>
    /// 원문 표(형식 3부터): 키는 해시 모양, 값은 원문 글이고 해시가 맞아야 한다. 엄격 모드는 원문을 해석하고(<see cref="ScriptAssetFormat.Check"/>)
    /// 문제를 모두 남기며, 느슨한 모드는 해석하지 않고 해시가 틀린 원문만 조용히 뺀다.
    /// </summary>
    private static Dictionary<string, RecipeScript> ReadScripts(JsonNode node, int schema, AssetReadContext context)
    {
        var result = new Dictionary<string, RecipeScript>(StringComparer.Ordinal);
        if (schema < 3 || node is not JsonObject table)
        {
            context.Bad(ScriptsKey);
            return result;
        }
        if (table.Count > context.Limits.MaxEffectsPerRecipe)
        {
            context.Add(AssetIssueCode.TooManyItems, ScriptsKey);
            return result;
        }
        foreach (var (hash, value) in table)
        {
            string field = $"{ScriptsKey}.{ShortHash(hash)}";
            if (!ScriptLibrary.IsHash(hash) || AssetJson.String(value) is not { } source)
            {
                if (context.Strict) context.Bad(field);
                continue;
            }
            if (ScriptEffectProgram.HashOf(source) != hash)
            {
                if (context.Strict) context.Add(AssetIssueCode.ScriptHashMismatch, field);
                continue;
            }
            // 엄격 모드: 해석하지 못한 원문도 자리는 남긴다(문제는 이미 남김 — 그 원문을 쓰는 효과를 원문 없음으로 다시 알리지 않게).
            var program = context.Strict ? ScriptAssetFormat.Check(source, field, context) : null;
            result[hash] = new RecipeScript(ScriptEffectProgram.NormalizeSource(source), program);
        }
        return result;
    }

    /// <summary>
    /// 매개변수 목록(형식 2부터). 엄격 모드는 모양·한도·연결·범위 문제를 모두 남기고, 느슨한 모드(개인 에셋)는 틀린 매개변수만 조용히 뺀다.
    /// 연결 검사는 효과를 문제없이 읽었을 때만 한다(효과 번호가 맞아야 하므로).
    /// </summary>
    private static List<RecipeParameter> ReadParameters(JsonNode node, int schema, IReadOnlyList<LayerEffect> effects, AssetReadContext context, int before)
    {
        if (schema < 2)
        {
            context.Bad("parameters");
            return [];
        }
        if (node is JsonArray array)
        {
            if (array.Count > context.Limits.MaxRecipeParameters)
            {
                context.Add(AssetIssueCode.TooManyItems, "parameters");
                return [];
            }
            for (int i = 0; i < array.Count; i++)
                if (array[i] is JsonObject item && item["bindings"] is JsonArray { Count: var count } && count > context.Limits.MaxParameterBindings)
                {
                    context.Add(AssetIssueCode.TooManyItems, $"parameters[{i}].bindings");
                    return [];
                }
        }
        var parameters = RecipeParameterFormat.Read(node, context.Strict, field =>
        {
            if (context.Strict) context.Bad(field);
        });
        if (context.Issues.Count > before) return parameters;
        var bad = RecipeParameters.Check(effects, parameters, (code, detail) =>
        {
            if (context.Strict) context.Add(code, detail);
        });
        return context.Strict ? parameters : parameters.Where((_, i) => !bad.Contains(i)).ToList();
    }

    /// <summary>
    /// 효과 JSON 하나를 등록된 schema로 검사한다(엄격 모드). 스크립트 효과는 원문 해시가 <paramref name="scripts"/>에 있어야 하고,
    /// 움직임 설정 밖의 설정은 그 원문이 선언한 schema로 본다. 문제가 없으면 true.
    /// </summary>
    private static bool CheckEffect(JsonObject json, string field, IReadOnlyDictionary<string, RecipeScript> scripts, AssetReadContext context)
    {
        int before = context.Issues.Count;
        void Bad(string detail) => context.Add(AssetIssueCode.InvalidEffectSetting, $"{field}.{detail}");

        string? type = AssetJson.String(json[EffectCodec.TypeKey]);
        if (EffectRegistry.Find(type) is not { } definition)
        {
            context.Add(AssetIssueCode.UnknownEffect, $"{field}: {type}");
            return false;
        }
        bool isScript = definition.TypeId == ScriptDefinition.Id;
        foreach (var (key, _) in json)
            if (!EffectKeys.Contains(key) && !(isScript && key == ScriptDefinition.ScriptKey)) Bad(key);
        if (json[EffectCodec.VersionKey] is { } versionNode
            && (!AssetJson.TryInt(versionNode, out int version) || version < 1 || version > definition.Version))
            Bad(EffectCodec.VersionKey);
        if (json[EffectCodec.EnabledKey] is { } enabled && !AssetJson.TryBool(enabled, out _)) Bad(EffectCodec.EnabledKey);
        if (json[EffectCodec.ClipKey] is { } clip && !AssetJson.TryBool(clip, out _)) Bad(EffectCodec.ClipKey);

        // 스크립트 효과: 원문이 레시피에 있어야 한다. 원문을 해석하지 못했으면(문제는 이미 남김) 스크립트 설정은 보지 않는다.
        ScriptEffectProgram? program = null;
        bool checkScriptSettings = false;
        if (isScript)
        {
            string? hash = AssetJson.String(json[ScriptDefinition.ScriptKey]);
            if (!ScriptLibrary.IsHash(hash)) Bad(ScriptDefinition.ScriptKey);
            else if (!scripts.TryGetValue(hash!, out var script)) context.Add(AssetIssueCode.MissingScript, $"{field}: {ShortHash(hash!)}");
            else
            {
                program = script.Program;
                checkScriptSettings = program is not null;
            }
        }

        var parameters = definition.Parameters.ToDictionary(p => p.Key, StringComparer.Ordinal);
        switch (json[EffectCodec.SettingsKey])
        {
            case null: break;
            case JsonObject settings:
                foreach (var (key, value) in settings)
                {
                    if (parameters.TryGetValue(key, out var parameter))
                    {
                        if (!ValidSetting(parameter, value, context.Limits)) Bad($"settings.{key}");
                    }
                    else if (isScript)
                    {
                        if (checkScriptSettings && (program!.Settings.FirstOrDefault(s => s.Key == key) is not { } setting || !ValidScriptSetting(setting, value)))
                            Bad($"settings.{key}");
                    }
                    else Bad($"settings.{key}");
                }
                break;
            default: Bad(EffectCodec.SettingsKey); break;
        }

        switch (json[EffectCodec.GradientsKey])
        {
            case null: break;
            case JsonObject gradients:
                foreach (var (slot, value) in gradients)
                {
                    if (!ValidSlot(definition, slot)) Bad($"colorGradients.{slot}");
                    else if (!ValidGradient(value, context.Limits)) Bad($"colorGradients.{slot}");
                }
                break;
            default: Bad(EffectCodec.GradientsKey); break;
        }
        return context.Issues.Count == before;
    }

    /// <summary>설정 값이 schema의 모양과 범위에 맞는지(정수는 범위 안, 고르기는 정의된 값, 목록은 모든 칸이 맞고 한도 안).</summary>
    private static bool ValidSetting(EffectParameter parameter, JsonNode? value, AssetPackageLimits limits)
    {
        switch (parameter.Kind)
        {
            case EffectParameterKind.Int:
                return AssetJson.TryInt(value, out int number) && (parameter.Min is not { } min || number >= min) && (parameter.Max is not { } max || number <= max);
            case EffectParameterKind.Bool:
                return AssetJson.TryBool(value, out _);
            case EffectParameterKind.Color:
                return ValidColor(value);
            case EffectParameterKind.Choice:
                return AssetJson.String(value) is { } choice && parameter.Choices is { } choices
                       && choices.Contains(choice, StringComparer.OrdinalIgnoreCase);
            case EffectParameterKind.ColorList:
                return value is JsonArray colors && colors.Count <= limits.MaxListItems && colors.All(ValidColor);
            case EffectParameterKind.ColorPairList:
                return value is JsonArray pairs && pairs.Count <= limits.MaxListItems
                       && pairs.All(pair => pair is JsonArray { Count: 2 } p && ValidColor(p[0]) && ValidColor(p[1]));
            default:
                return false;
        }
    }

    /// <summary>스크립트 설정 값이 선언한 schema에 맞는지(정수는 소수 없는 수, 숫자는 min~max 안, 색은 16진수, 고르기는 선언한 값).</summary>
    private static bool ValidScriptSetting(ScriptSetting setting, JsonNode? value)
    {
        switch (setting.Type)
        {
            case ScriptSettingType.Int:
            case ScriptSettingType.Number:
                return AssetJson.TryDouble(value, out double number) && (setting.Type == ScriptSettingType.Number || number == Math.Round(number))
                       && (setting.Min is not { } min || number >= min) && (setting.Max is not { } max || number <= max);
            case ScriptSettingType.Bool:
                return AssetJson.TryBool(value, out _);
            case ScriptSettingType.Color:
                return ValidColor(value);
            default:
                return AssetJson.String(value) is { } choice && setting.Choices.Any(c => c.Value == choice);
        }
    }

    private static bool ValidColor(JsonNode? node) => AssetJson.String(node) is { } hex && Color32.TryParseHex(hex, out _);

    /// <summary>그라데이션 슬롯: 색 설정의 슬롯 이름, 목록이면 "{슬롯}[i]", 색 쌍이면 "{슬롯}[i].From"·"{슬롯}[i].To".</summary>
    private static bool ValidSlot(EffectDefinition definition, string slot)
    {
        foreach (var parameter in definition.Parameters)
        {
            if (parameter.ColorSlot is not { } name) continue;
            switch (parameter.Kind)
            {
                case EffectParameterKind.Color when slot == name:
                    return true;
                case EffectParameterKind.ColorList when IndexedSlot(slot, name, out string rest) && rest.Length == 0:
                    return true;
                case EffectParameterKind.ColorPairList when IndexedSlot(slot, name, out string rest) && rest is ".From" or ".To":
                    return true;
            }
        }
        return false;
    }

    private static bool IndexedSlot(string slot, string name, out string rest)
    {
        rest = "";
        if (!slot.StartsWith(name + "[", StringComparison.Ordinal)) return false;
        int close = slot.IndexOf(']', name.Length + 1);
        if (close < 0 || !int.TryParse(slot.AsSpan(name.Length + 1, close - name.Length - 1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _)) return false;
        rest = slot[(close + 1)..];
        return true;
    }

    private static readonly HashSet<string> GradientKeys =
    [
        "angle", "shape", "animated", "reverse", "cycles", "interpolation", "dither", "colorSpace", "stops", "alphaStops",
        "straightRgba", "easing", "pingPong",
    ];

    /// <summary>
    /// 효과 색 슬롯의 그라데이션 값: 아는 키만, 정지점 수가 한도 안이고 위치 0~1·올바른 색·알파 0~255, 정의된 설정 이름.
    /// 프로젝트 읽기는 틀린 값을 조용히 고치거나(위치 자르기, 모르는 이름 → 기본값) 버리므로 원본 JSON을 직접 본다.
    /// </summary>
    private static bool ValidGradient(JsonNode? value, AssetPackageLimits limits)
    {
        if (value is not JsonObject o) return false;
        if (o.Any(pair => !GradientKeys.Contains(pair.Key))) return false;
        if (o["stops"] is not JsonArray stops || stops.Count is < 2 || stops.Count > limits.MaxGradientStops) return false;
        if (!stops.All(stop => stop is JsonObject s && ValidPosition(s["position"]) && ValidColor(s["color"]))) return false;
        if (o["alphaStops"] is { } alphaNode)
        {
            if (alphaNode is not JsonArray alpha || alpha.Count > limits.MaxGradientStops) return false;
            if (!alpha.All(stop => stop is JsonObject s && ValidPosition(s["position"]) && AssetJson.TryInt(s["alpha"], out int a) && a is >= 0 and <= 255))
                return false;
        }
        if (o["cycles"] is { } cycles && (!AssetJson.TryInt(cycles, out int c) || c is < 1 or > GradientAssetFormat.MaxCycles)) return false;
        if (o["angle"] is { } angle && (!AssetJson.TryInt(angle, out int an) || an is < 0 or > GradientAssetFormat.MaxAngle)) return false;
        foreach (string key in (string[])["animated", "reverse", "straightRgba", "pingPong"])
            if (o[key] is { } flag && !AssetJson.TryBool(flag, out _)) return false;
        return ValidName<EffectGradientShape>(o["shape"]) && ValidName<Edit.GradientInterpolation>(o["interpolation"])
               && ValidName<Edit.GradientDither>(o["dither"]) && ValidName<Edit.GradientColorSpace>(o["colorSpace"])
               && ValidName<EffectEasing>(o["easing"]);
    }

    private static bool ValidPosition(JsonNode? node) => AssetJson.TryDouble(node, out double position) && position is >= 0 and <= 1;

    /// <summary>없거나(null 포함) 정의된 이름 글자(대소문자 무시, 숫자 글자는 아님).</summary>
    private static bool ValidName<T>(JsonNode? node) where T : struct, Enum
        => node is null || (AssetJson.String(node) is { Length: > 0 } name && !char.IsAsciiDigit(name.TrimStart('-').FirstOrDefault())
                                                                         && Enum.TryParse(name, true, out T parsed) && Enum.IsDefined(parsed));
}
