using System.Text.Json.Nodes;
using Gpixel.Core.Assets;
using Gpixel.Core.Edit;
using Gpixel.Core.Model;

namespace Gpixel.Core.IO;

/// <summary>그라데이션 에셋: 영구 ID와 시각 값(<see cref="GradientPreset.Favorite"/>는 컴퓨터별 상태라 쓰지 않는다).</summary>
public sealed record GradientAsset(string Id, GradientPreset Gradient) : IAssetData
{
    public string Name => Gradient.Name;
}

/// <summary>
/// 그라데이션 에셋 JSON(계획 6.4) — 패키지의 <c>assets/gradients/*.json</c>과 개인 에셋이 같이 쓴다:
/// <code>{ "schemaVersion": 1, "id": "…", "name": "Neon Blue", "stops": [{ "position": 0, "color": "#RRGGBBAA" }],
///   "alphaStops": [{ "position": 0, "alpha": 255 }], "interpolation": "Smooth", "dither": "None", "colorSpace": "OkLch",
///   "angle": 0, "shape": "Linear", "animated": false, "reverse": false, "cycles": 1, "easing": "Linear", "pingPong": false }</code>
/// </summary>
public static class GradientAssetFormat
{
    public const int SchemaVersion = 1;
    public const int MaxAngle = 359, MaxCycles = 16;

    private static readonly HashSet<string> Keys =
    [
        "schemaVersion", "id", "name", "stops", "alphaStops", "interpolation", "dither", "colorSpace",
        "angle", "shape", "animated", "reverse", "cycles", "easing", "pingPong",
    ];

    public static JsonObject Write(string id, GradientPreset gradient)
    {
        var stops = new JsonArray();
        foreach (var stop in gradient.Stops) stops.Add(new JsonObject { ["position"] = stop.Position, ["color"] = stop.Color.ToHex() });
        var alpha = new JsonArray();
        foreach (var stop in gradient.AlphaStops) alpha.Add(new JsonObject { ["position"] = stop.Position, ["alpha"] = (int)stop.Alpha });
        return new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["id"] = id,
            ["name"] = gradient.Name.Trim(),
            ["stops"] = stops,
            ["alphaStops"] = alpha,
            ["interpolation"] = gradient.Interpolation.ToString(),
            ["dither"] = gradient.Dither.ToString(),
            ["colorSpace"] = gradient.ColorSpace.ToString(),
            ["angle"] = gradient.Angle,
            ["shape"] = gradient.Shape.ToString(),
            ["animated"] = gradient.Animated,
            ["reverse"] = gradient.Reverse,
            ["cycles"] = gradient.Cycles,
            ["easing"] = gradient.Easing.ToString(),
            ["pingPong"] = gradient.PingPong,
        };
    }

    /// <summary>그라데이션 에셋을 읽는다. 문제가 하나라도 있으면 <paramref name="context"/>에 남기고 null.</summary>
    public static GradientAsset? Read(JsonNode? node, AssetReadContext context)
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

        var stops = new List<GradientStop>();
        if (o["stops"] is JsonArray colorArray)
        {
            if (context.Strict && colorArray.Count > context.Limits.MaxGradientStops) context.Add(AssetIssueCode.TooManyItems, "stops");
            else
                foreach (var item in colorArray)
                {
                    if (item is JsonObject s && AssetJson.TryDouble(s["position"], out double position) && ValidPosition(position)
                        && AssetJson.String(s["color"]) is { } hex && Color32.TryParseHex(hex, out var color))
                        stops.Add(new GradientStop(position, color));
                    else
                    {
                        context.Bad("stops");
                        break;
                    }
                }
        }
        if (stops.Count < 2 && context.Issues.Count == before) context.Bad("stops");

        var alphaStops = new List<GradientAlphaStop>();
        if (o["alphaStops"] is JsonArray alphaArray)
        {
            if (context.Strict && alphaArray.Count > context.Limits.MaxGradientStops) context.Add(AssetIssueCode.TooManyItems, "alphaStops");
            else
                foreach (var item in alphaArray)
                {
                    if (item is JsonObject s && AssetJson.TryDouble(s["position"], out double position) && ValidPosition(position)
                        && AssetJson.TryInt(s["alpha"], out int a) && a is >= 0 and <= 255)
                        alphaStops.Add(new GradientAlphaStop(position, (byte)a));
                    else
                    {
                        context.Bad("alphaStops");
                        break;
                    }
                }
            if (alphaStops.Count < 2 && !(context.Strict && alphaArray.Count > context.Limits.MaxGradientStops)) context.Bad("alphaStops");
        }
        else
        {
            if (o["alphaStops"] is not null) context.Bad("alphaStops");
            alphaStops.AddRange([new GradientAlphaStop(0, 255), new GradientAlphaStop(1, 255)]);
        }

        var interpolation = Enum(o, "interpolation", GradientInterpolation.Smooth, context);
        var dither = Enum(o, "dither", GradientDither.None, context);
        var colorSpace = Enum(o, "colorSpace", GradientColorSpace.OkLch, context);
        var shape = Enum(o, "shape", EffectGradientShape.Linear, context);
        var easing = Enum(o, "easing", EffectEasing.Linear, context);
        int angle = Int(o, "angle", 0, 0, MaxAngle, context);
        int cycles = Int(o, "cycles", 1, 1, MaxCycles, context);
        bool animated = Bool(o, "animated", context), reverse = Bool(o, "reverse", context), pingPong = Bool(o, "pingPong", context);

        if (context.Issues.Count > before) return null;
        return new GradientAsset(id, new GradientPreset
        {
            Name = name.Trim(), Stops = stops, AlphaStops = alphaStops, Interpolation = interpolation, Dither = dither,
            ColorSpace = colorSpace, Angle = angle, Shape = shape, Animated = animated, Reverse = reverse,
            Cycles = cycles, Easing = easing, PingPong = pingPong,
        });
    }

    /// <summary>
    /// 저장할 수 있는 그라데이션인지(이름, 정지점 2개 이상·위치 0~1, 정의된 설정, 각도 0~359, 반복 1~16).
    /// 틀리면 <see cref="InvalidDataException"/> — 개인 에셋 저장 전에 부른다.
    /// </summary>
    public static void Validate(GradientPreset gradient)
    {
        var context = new AssetReadContext(gradient.Name, strict: false);
        if (Read(Write("check", gradient), context) is null)
            throw new InvalidDataException(string.Join(Environment.NewLine, context.Issues.Select(issue => issue.Message)));
    }

    private static bool ValidPosition(double value) => double.IsFinite(value) && value >= 0 && value <= 1;

    private static T Enum<T>(JsonObject o, string key, T fallback, AssetReadContext context) where T : struct, System.Enum
    {
        var node = o[key];
        if (node is null) return fallback;
        if (AssetJson.String(node) is { } name && System.Enum.TryParse(name, true, out T value) && System.Enum.IsDefined(value)
            && !char.IsAsciiDigit(name.TrimStart('-')[0]))
            return value;
        context.Bad(key);
        return fallback;
    }

    private static int Int(JsonObject o, string key, int fallback, int min, int max, AssetReadContext context)
    {
        var node = o[key];
        if (node is null) return fallback;
        if (AssetJson.TryInt(node, out int value) && value >= min && value <= max) return value;
        context.Bad(key);
        return fallback;
    }

    private static bool Bool(JsonObject o, string key, AssetReadContext context)
    {
        var node = o[key];
        if (node is null) return false;
        if (AssetJson.TryBool(node, out bool value)) return value;
        context.Bad(key);
        return false;
    }
}
