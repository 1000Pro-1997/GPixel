using System.Text.Json.Nodes;
using Gpixel.Core.Assets;
using Gpixel.Core.Effects;

namespace Gpixel.Core.IO;

/// <summary>
/// 레시피 매개변수 JSON — 레시피 에셋의 <c>parameters</c>와 프로젝트 효과의 묶음 정보(<c>recipe</c>)가 같이 쓴다:
/// <code>{ "id": "glow", "name": "빛 색", "kind": "color", "default": "#00FFC8", "bindings": [ { "effect": 0, "key": "color" }, { "effect": 2, "key": "color" } ] }
/// { "id": "width", "name": "두께", "kind": "int", "default": 2, "min": 1, "max": 3, "bindings": [ … ] }
/// { "id": "mode", "name": "섞기", "kind": "choice", "default": "Screen", "choices": [ "Normal", "Screen" ], "bindings": [ … ] }</code>
/// <c>kind</c> = <c>color</c>·<c>int</c>·<c>bool</c>·<c>choice</c>, 값 모양은 효과 설정 JSON과 같다. <c>min</c>·<c>max</c>는 정수만, <c>choices</c>는 고르기만 쓴다.
/// 여기서는 모양만 읽고, 연결·범위 검사는 <see cref="RecipeParameters.Check"/>가 한다.
/// <para>
/// 프로젝트 효과 JSON의 묶음 정보(구성 효과마다 한 벌, 원본 패키지 없이 편집되게):
/// <code>"recipe": { "instance": "…32자…", "slot": 0, "name": "Neon Hit", "source": "author.neon-pack.neon-hit", "parameters": [ … ] }</code>
/// </para>
/// </summary>
public static class RecipeParameterFormat
{
    public const string LinkKey = "recipe";

    private static readonly HashSet<string> Keys = ["id", "name", "kind", "default", "min", "max", "choices", "bindings"];
    private static readonly HashSet<string> BindingKeys = ["effect", "key"];

    public static string KindName(RecipeParameterKind kind) => kind switch
    {
        RecipeParameterKind.Color => "color",
        RecipeParameterKind.Int => "int",
        RecipeParameterKind.Bool => "bool",
        _ => "choice",
    };

    private static RecipeParameterKind? ParseKind(string? name) => name switch
    {
        "color" => RecipeParameterKind.Color,
        "int" => RecipeParameterKind.Int,
        "bool" => RecipeParameterKind.Bool,
        "choice" => RecipeParameterKind.Choice,
        _ => null,
    };

    public static JsonArray Write(IEnumerable<RecipeParameter> parameters) => new(parameters.Select(p => (JsonNode)Write(p)).ToArray());

    public static JsonObject Write(RecipeParameter parameter)
    {
        var json = new JsonObject
        {
            ["id"] = parameter.Id,
            ["name"] = parameter.Name.Trim(),
            ["kind"] = KindName(parameter.Kind),
            ["default"] = parameter.Default.ToJson(),
        };
        if (parameter.Kind == RecipeParameterKind.Int)
        {
            json["min"] = parameter.Min;
            json["max"] = parameter.Max;
        }
        if (parameter.Kind == RecipeParameterKind.Choice)
            json["choices"] = new JsonArray(parameter.Choices.Select(choice => (JsonNode)JsonValue.Create(choice)).ToArray());
        json["bindings"] = new JsonArray(parameter.Bindings
            .Select(binding => (JsonNode)new JsonObject { ["effect"] = binding.Effect, ["key"] = binding.Key }).ToArray());
        return json;
    }

    /// <summary>
    /// 매개변수 목록을 읽는다. 모양이 틀린 필드는 <paramref name="bad"/>(필드 경로, 예: <c>parameters[0].kind</c>)로 알리고 그 매개변수는 뺀다.
    /// <paramref name="strict"/>면 모르는 키도 틀린 것으로 본다.
    /// </summary>
    public static List<RecipeParameter> Read(JsonNode? node, bool strict, Action<string> bad)
    {
        var result = new List<RecipeParameter>();
        if (node is not JsonArray list)
        {
            bad("parameters");
            return result;
        }
        for (int i = 0; i < list.Count; i++)
            if (ReadOne(list[i], $"parameters[{i}]", strict, bad) is { } parameter)
                result.Add(parameter);
        return result;
    }

    private static RecipeParameter? ReadOne(JsonNode? node, string field, bool strict, Action<string> bad)
    {
        if (node is not JsonObject o)
        {
            bad(field);
            return null;
        }
        bool ok = true;
        void Bad(string key)
        {
            bad($"{field}.{key}");
            ok = false;
        }

        if (strict)
            foreach (var (key, _) in o)
                if (!Keys.Contains(key)) Bad(key);
        if (ParseKind(AssetJson.String(o["kind"])) is not { } kind)
        {
            Bad("kind");
            return null;
        }
        if (!RecipeValue.TryRead(kind, o["default"], out var value)) Bad("default");

        int min = 0, max = 0;
        if (kind == RecipeParameterKind.Int)
        {
            if (!AssetJson.TryInt(o["min"], out min)) Bad("min");
            if (!AssetJson.TryInt(o["max"], out max)) Bad("max");
        }
        else if (strict)
        {
            if (o["min"] is not null) Bad("min");
            if (o["max"] is not null) Bad("max");
        }

        var choices = new List<string>();
        if (kind == RecipeParameterKind.Choice)
        {
            if (o["choices"] is JsonArray array && array.All(item => AssetJson.String(item) is { Length: > 0 }))
                choices.AddRange(array.Select(item => AssetJson.String(item)!));
            else Bad("choices");
        }
        else if (strict && o["choices"] is not null) Bad("choices");

        var bindings = new List<RecipeBinding>();
        if (o["bindings"] is JsonArray links)
            for (int j = 0; j < links.Count; j++)
            {
                if (links[j] is JsonObject link && AssetJson.TryInt(link["effect"], out int effect) && AssetJson.String(link["key"]) is { Length: > 0 } key
                    && (!strict || link.All(pair => BindingKeys.Contains(pair.Key))))
                    bindings.Add(new RecipeBinding(effect, key));
                else Bad($"bindings[{j}]");
            }
        else Bad("bindings");

        return ok
            ? new RecipeParameter
            {
                Id = AssetJson.String(o["id"]) ?? "", Name = (AssetJson.String(o["name"]) ?? "").Trim(), Kind = kind, Default = value,
                Min = min, Max = max, Choices = choices, Bindings = bindings,
            }
            : null;
    }

    /// <summary>효과 하나의 묶음 정보 JSON.</summary>
    public static JsonObject WriteLink(RecipeLink link)
    {
        var json = new JsonObject
        {
            ["instance"] = link.Instance.Id,
            ["slot"] = link.Slot,
            ["name"] = link.Instance.Name,
        };
        if (link.Instance.SourceId is { Length: > 0 } source) json["source"] = source;
        json["parameters"] = Write(link.Instance.Parameters);
        return json;
    }

    /// <summary>
    /// 효과의 묶음 정보. 모양이 틀리면 null(효과는 보통 효과로 읽힘). 모양이 틀린 매개변수는 빼고, 연결 검사는 하지 않는다 —
    /// 프로젝트에서는 없어진 구성 효과나 맞지 않는 연결을 값 바꾸기가 건너뛴다.
    /// </summary>
    public static RecipeLink? ReadLink(JsonNode? node)
    {
        if (node is not JsonObject o
            || AssetJson.String(o["instance"]) is not { Length: > 0 and <= RecipeParameters.MaxIdLength } id
            || !AssetJson.TryInt(o["slot"], out int slot) || slot < 0)
            return null;
        var parameters = Read(o["parameters"], strict: false, _ => { });
        var instance = new EffectRecipeInstance
        {
            Id = id,
            Name = AssetJson.String(o["name"]) ?? "",
            SourceId = AssetJson.String(o["source"]) is { Length: > 0 } source ? source : null,
            Parameters = parameters,
        };
        return new RecipeLink(instance, slot);
    }
}
