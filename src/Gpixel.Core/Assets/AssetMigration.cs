using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Gpixel.Core.Edit;
using Gpixel.Core.Effects;
using Gpixel.Core.IO;
using Gpixel.Core.Model;

namespace Gpixel.Core.Assets;

/// <summary>migration 결과. <see cref="Ran"/>이 false면 이미 개인 에셋 파일이 있어 아무것도 하지 않았다.</summary>
public sealed record AssetMigrationResult(bool Ran, int Recipes, int Gradients, IReadOnlyList<string> Backups, string? Error)
{
    public static AssetMigrationResult Skipped { get; } = new(false, 0, 0, [], null);
}

/// <summary>
/// 예전 사용자 데이터(<c>effect-presets.json</c>, <c>gradient-presets.json</c>)를 개인 에셋으로 한 번 옮긴다(계획 14장 2단계).
/// 개인 에셋 파일이 없을 때만 돈다: 예전 두 파일을 모두 읽고(없는 그라데이션 파일 = 기본 그라데이션), 개인 에셋 파일을 원자적으로 만든 뒤,
/// 예전 파일을 날짜가 붙은 backup(<c>effect-presets.backup-2026-10-28.json</c>)으로 이름을 바꾼다. 그 뒤 앱은 새 저장소만 읽고 쓴다.
/// 예전 파일을 읽지 못하면 아무것도 쓰지 않고 개인 에셋 쓰기를 막는다(<see cref="AssetStore.PersonalError"/>) — 다음 실행에 다시 시도한다.
/// 예전 형식 읽기는 이 파일에만 남는다.
/// </summary>
public static class AssetMigration
{
    public const string EffectPresetsFileName = "effect-presets.json", GradientPresetsFileName = "gradient-presets.json";

    /// <param name="legacyDirectory">예전 파일이 있던 앱 설정 폴더(<c>…/Gpixel</c>).</param>
    public static AssetMigrationResult Run(AssetStore store, string legacyDirectory, DateTime? nowUtc = null)
    {
        if (store.HasPersonalFile) return AssetMigrationResult.Skipped;
        var now = nowUtc ?? DateTime.UtcNow;
        string effectsPath = Path.Combine(legacyDirectory, EffectPresetsFileName);
        string gradientsPath = Path.Combine(legacyDirectory, GradientPresetsFileName);

        List<(string Name, IReadOnlyList<LayerEffect> Effects)> recipes;
        List<GradientPreset> gradients;
        try
        {
            recipes = File.Exists(effectsPath) ? ReadLegacyEffects(File.ReadAllText(effectsPath)) : [];
            gradients = File.Exists(gradientsPath) ? ReadLegacyGradients(File.ReadAllText(gradientsPath)) : GradientPresets.BuiltIns();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            store.BlockPersonal(ex.Message);
            return new AssetMigrationResult(true, 0, 0, [], ex.Message);
        }

        var items = new List<(string Kind, IAssetData Data, bool Favorite, bool IsBuiltIn)>();
        items.AddRange(recipes.Select((r, i) => (AssetKinds.EffectRecipe,
            (IAssetData)EffectRecipe.Capture("", CleanName(r.Name, Localization.DomainStrings.FormatAssetMigration_UnnamedRecipe(number: i + 1)), r.Effects), false, false)));
        items.AddRange(gradients.Select((g, i) => (AssetKinds.Gradient,
            (IAssetData)new GradientAsset("", g with { Name = CleanName(g.Name, Localization.DomainStrings.FormatAssetMigration_UnnamedGradient(number: i + 1)), Favorite = false }),
            g.Favorite, AssetStore.IsDefaultGradient(g))));
        // 같은 저장소를 쓰는 다른 앱이 먼저 옮겼으면(잠금을 잡은 뒤 개인 에셋 파일이 이미 있음) 그 결과를 쓴다
        if (!store.TryCreatePersonal(items, now)) return AssetMigrationResult.Skipped;

        // 새 저장소가 완성된 뒤에만 예전 파일을 치운다. 이름 바꾸기에 실패해도 새 저장소가 기준이다(예전 파일은 다시 읽지 않음).
        var backups = new List<string>();
        string? error = null;
        foreach (var path in new[] { effectsPath, gradientsPath }.Where(File.Exists))
        {
            try
            {
                string backup = BackupPath(path, now);
                File.Move(path, backup);
                backups.Add(backup);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error ??= ex.Message;
            }
        }
        return new AssetMigrationResult(true, recipes.Count, gradients.Count, backups, error);
    }

    /// <summary>
    /// 예전 파일은 이름 길이·제어 문자를 보지 않았다: 제어 문자를 빼고 에셋 이름 한도로 자른다.
    /// 남는 글자가 없으면(제어 문자뿐) <paramref name="fallback"/> — 빈 이름은 개인 에셋으로 읽히지 않아 사라지므로.
    /// </summary>
    private static string CleanName(string name, string fallback)
    {
        string clean = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) return fallback;
        return clean.Length > AssetManifest.MaxNameLength ? clean[..AssetManifest.MaxNameLength].TrimEnd() : clean;
    }

    /// <summary><c>name.json</c> → <c>name.backup-YYYY-MM-DD.json</c>(있으면 <c>-2</c>, <c>-3</c> …).</summary>
    public static string BackupPath(string path, DateTime nowUtc)
    {
        string dir = Path.GetDirectoryName(path) ?? "";
        string stem = Path.GetFileNameWithoutExtension(path) + ".backup-" + nowUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string candidate = Path.Combine(dir, stem + ".json");
        for (int n = 2; File.Exists(candidate); n++) candidate = Path.Combine(dir, $"{stem}-{n}.json");
        return candidate;
    }

    // ───────────── 예전 사용자 정의 효과(effect-presets.json 1·2판) ─────────────

    private static readonly JsonSerializerOptions EffectOptions = new(ProjectIO.Options);

    private sealed class EffectFileDto
    {
        public int Version { get; set; } = 2;
        public List<EffectPresetDto>? Presets { get; set; }
    }

    private sealed class EffectPresetDto
    {
        public string? Name { get; set; }
        public List<JsonObject?>? Effects { get; set; }
    }

    /// <summary>
    /// 예전 사용자 정의 효과 파일. 1판(짧은 효과 ID)과 2판(종류 ID + 설정 객체)을 <see cref="EffectCodec"/>으로 읽어 모르는 효과도 보존한다.
    /// 빈 이름·빈 목록은 건너뛰고, 같은 이름은 뒤의 것이 앞 자리를 바꾼다.
    /// </summary>
    public static List<(string Name, IReadOnlyList<LayerEffect> Effects)> ReadLegacyEffects(string json)
    {
        EffectFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<EffectFileDto>(json, EffectOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
        if (dto is null) return [];
        if (dto.Version > 2) throw new InvalidDataException($"Unsupported effect preset version: {dto.Version}");

        var result = new List<(string Name, IReadOnlyList<LayerEffect> Effects)>();
        foreach (var saved in dto.Presets ?? [])
        {
            string name = saved.Name?.Trim() ?? "";
            if (name.Length == 0) continue;
            var effects = (saved.Effects ?? []).OfType<JsonObject>().Select(EffectCodec.Read).ToArray();
            if (effects.Length == 0) continue;
            var captured = EffectRecipe.Capture("", name, effects).Effects;
            int duplicate = result.FindIndex(item => item.Name == name);
            if (duplicate >= 0) result[duplicate] = (name, captured);
            else result.Add((name, captured));
        }
        return result;
    }

    // ───────────── 예전 그라데이션 프리셋(gradient-presets.json 1·2판) ─────────────

    private static readonly JsonSerializerOptions GradientOptions = new(ProjectIO.Options) { NumberHandling = JsonNumberHandling.Strict };

    private sealed class GradientFileDto
    {
        public int Version { get; set; } = 2;
        public List<GradientPresetDto?>? Presets { get; set; }
    }

    private sealed class GradientPresetDto
    {
        public string? Name { get; set; }
        public List<ColorStopDto?>? Stops { get; set; }
        public List<AlphaStopDto?>? AlphaStops { get; set; }
        public string? Interpolation { get; set; }
        public string? Dither { get; set; }
        public string? ColorSpace { get; set; }
        public int Angle { get; set; }
        public string? Shape { get; set; }
        public bool Animated { get; set; }
        public bool Reverse { get; set; }
        public int Cycles { get; set; } = 1;
        public string? Easing { get; set; }
        public bool PingPong { get; set; }
        public bool Favorite { get; set; }
    }

    private sealed class ColorStopDto
    {
        public double? Position { get; set; }
        public string? Color { get; set; }
    }

    private sealed class AlphaStopDto
    {
        public double? Position { get; set; }
        public int? Alpha { get; set; }
    }

    private static readonly HashSet<string> AddedInVersion2 = new(StringComparer.OrdinalIgnoreCase) { "Fire", "Forest", "Grayscale" };

    /// <summary>
    /// 예전 그라데이션 프리셋 파일. 손상된 파일은 빈 목록으로 바꾸지 않고 <see cref="InvalidDataException"/>. 같은 이름(대소문자 무시)은 뒤의 것이 앞 자리를 바꾼다.
    /// 알파 곡선이 없던 예전 값은 불투명, 색 공간이 없던 값은 RGB. 1판 파일은 2판에서 더한 기본 그라데이션 중 없는 이름을 덧붙인다.
    /// </summary>
    public static List<GradientPreset> ReadLegacyGradients(string json)
    {
        GradientFileDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<GradientFileDto>(json, GradientOptions)
                  ?? throw new InvalidDataException("Gradient preset catalog must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Invalid gradient preset JSON.", ex);
        }
        if (dto.Version is < 1 or > 2) throw new InvalidDataException($"Unsupported gradient preset version: {dto.Version}.");
        if (dto.Presets is null) throw new InvalidDataException("Gradient preset catalog is missing presets.");

        var result = new List<GradientPreset>(dto.Presets.Count);
        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var saved in dto.Presets)
        {
            if (saved?.Stops is null) throw new InvalidDataException("Gradient preset is missing color stops.");
            var stops = new List<GradientStop>(saved.Stops.Count);
            foreach (var stop in saved.Stops)
            {
                if (stop?.Position is not { } position || !Color32.TryParseHex(stop.Color, out var color))
                    throw new InvalidDataException("Invalid gradient color stop.");
                stops.Add(new GradientStop(position, color));
            }
            var alphaStops = new List<GradientAlphaStop>();
            if (saved.AlphaStops is null) alphaStops.AddRange([new GradientAlphaStop(0, 255), new GradientAlphaStop(1, 255)]);
            else
                foreach (var stop in saved.AlphaStops)
                {
                    if (stop?.Position is not { } position || stop.Alpha is not (>= 0 and <= 255))
                        throw new InvalidDataException("Invalid gradient alpha stop.");
                    alphaStops.Add(new GradientAlphaStop(position, (byte)stop.Alpha.Value));
                }
            var preset = new GradientPreset
            {
                Name = (saved.Name ?? "").Trim(), Stops = stops, AlphaStops = alphaStops,
                Interpolation = LegacyEnum(saved.Interpolation, GradientInterpolation.Smooth),
                Dither = LegacyEnum(saved.Dither, GradientDither.None),
                ColorSpace = LegacyEnum(saved.ColorSpace, GradientColorSpace.Rgb),
                Angle = saved.Angle, Shape = LegacyEnum(saved.Shape, EffectGradientShape.Linear),
                Animated = saved.Animated, Reverse = saved.Reverse, Cycles = saved.Cycles,
                Easing = LegacyEnum(saved.Easing, EffectEasing.Linear),
                PingPong = saved.PingPong, Favorite = saved.Favorite,
            };
            // 이름은 옮길 때 정리하므로(빈 이름은 대신 붙일 이름) 여기서는 이름 말고 나머지 값만 본다
            GradientAssetFormat.Validate(preset with { Name = CleanName(preset.Name, Localization.DomainStrings.FormatAssetMigration_UnnamedGradient(number: result.Count + 1)) });
            if (indexes.TryGetValue(preset.Name, out int index)) result[index] = preset;
            else
            {
                indexes.Add(preset.Name, result.Count);
                result.Add(preset);
            }
        }
        if (dto.Version < 2)
            foreach (var added in GradientPresets.BuiltIns().Where(p => AddedInVersion2.Contains(p.Name)))
                if (!indexes.ContainsKey(added.Name)) result.Add(added);
        return result;
    }

    private static T LegacyEnum<T>(string? value, T fallback) where T : struct, Enum
    {
        if (value is null) return fallback;
        foreach (string name in Enum.GetNames<T>())
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
                return Enum.Parse<T>(name);
        throw new InvalidDataException($"Invalid gradient setting: {value}.");
    }
}
