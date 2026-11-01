using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Gpixel.Core.Effects;
using Gpixel.Core.IO;

namespace Gpixel.Core.Assets;

/// <summary>
/// 로컬 에셋 저장소(계획 7장). 앱 설정 폴더의 <c>Gpixel/Assets/</c> 아래:
/// <code>
/// installed.json                 설치 상태(버전, 켜짐, 출처, 설치 시각, 자동 업데이트, 이전 버전)
/// Packages/&lt;id&gt;/&lt;version&gt;/       설치한 패키지 원본(읽기 전용 — manifest.json, assets/, previews/, 문서)
/// Personal/personal-assets.json  개인 에셋(효과 레시피·그라데이션)과 즐겨찾기
/// Deleted/                       최근 삭제한 개인 에셋·패키지 10개(되돌리기 가능)
/// Staging/                       설치 중 임시 파일(열 때 비움)
/// Cache/                         원격 카탈로그 cache(<see cref="RemoteAssetClient"/> — 목록, 미리보기, 내려받는 중인 파일)
/// </code>
/// 설치·업데이트는 Staging에서 검사와 풀기를 끝낸 뒤 버전 폴더를 한 번에 옮기고(rename), 마지막에 installed.json을 임시 파일 + 바꿔치기로 쓴다.
/// 어느 단계에서 프로세스가 죽어도 이전 상태 파일은 그대로이고, 다음에 열 때 반쯤 된 폴더를 치운다(<see cref="Open"/>).
/// 네트워크는 다루지 않는다 — 원격 내려받기는 파일을 받은 뒤 <see cref="Install"/>을 부른다.
/// 저장소를 바꾸는 메서드는 한 스레드(앱의 UI 스레드)에서 부른다. 앱을 여러 개 띄워 같은 저장소를 쓰면, 바꾸기 전에 잠금 파일을 잡고
/// 다른 앱이 그 사이 쓴 상태 파일을 다시 읽은 뒤 바꾼다(한쪽이 다른 쪽의 설치·개인 에셋을 덮어 지우지 않게).
/// </summary>
public sealed class AssetStore
{
    public const string InstalledFileName = "installed.json", PersonalFileName = "personal-assets.json";
    public const int InstalledSchemaVersion = 1, PersonalSchemaVersion = 1;
    public const string PersonalIdPrefix = "personal.";
    /// <summary>개인 에셋과 패키지를 합쳐 보관하는 최근 삭제 수.</summary>
    public const int MaxDeletedItems = 10;
    /// <summary>업데이트 뒤 되돌리기용으로 남기는 이전 버전 수.</summary>
    public const int KeptPreviousVersions = 1;

    private readonly AssetValidationOptions _options;
    private List<InstalledPackageState> _installed = [];
    private List<PersonalRecord> _personal = [];
    private HashSet<string> _favorites = new(StringComparer.Ordinal);
    private HashSet<string> _disabledBuiltInEffects = new(StringComparer.Ordinal);
    private readonly List<string> _loadErrors = [];
    /// <summary>마지막으로 읽거나 쓴 상태 파일의 (수정 시각, 길이) — 다른 앱이 바꿨는지 볼 때 쓴다.</summary>
    private (DateTime, long) _installedStamp, _personalStamp;
    private FileStream? _lock;
    private int _lockDepth;

    /// <summary>개인 에셋 파일의 한 줄. 읽지 못한 줄(모르는 종류, 미래 schema)은 <see cref="Raw"/>를 그대로 다시 쓴다.</summary>
    private sealed record PersonalRecord(string Id, string Kind, DateTime CreatedUtc, IAssetData? Data, JsonObject? Raw,
        bool IsBuiltIn = false, bool Enabled = true);

    private AssetStore(string root, AssetValidationOptions options)
    {
        Root = root;
        _options = options;
    }

    public string Root { get; }
    public string PackagesDirectory => Path.Combine(Root, "Packages");
    public string PersonalDirectory => Path.Combine(Root, "Personal");
    public string StagingDirectory => Path.Combine(Root, "Staging");
    public string DeletedDirectory => Path.Combine(Root, "Deleted");
    public string CacheDirectory => Path.Combine(Root, "Cache");
    public string InstalledPath => Path.Combine(Root, InstalledFileName);
    public string PersonalPath => Path.Combine(PersonalDirectory, PersonalFileName);

    /// <summary>개인 에셋 파일이 있는지(없으면 예전 프리셋 migration 전 — <see cref="AssetMigration"/>).</summary>
    public bool HasPersonalFile => File.Exists(PersonalPath);

    /// <summary>
    /// 개인 에셋을 쓸 수 없는 까닭(파일이 손상됐거나 미래 schema, migration 실패). null이면 쓸 수 있다.
    /// 쓸 수 없으면 개인 에셋을 바꾸는 메서드가 <see cref="InvalidOperationException"/>을 던진다 — 읽지 못한 파일을 빈 목록으로 덮어쓰지 않는다.
    /// </summary>
    public string? PersonalError { get; private set; }

    /// <summary>installed.json을 쓸 수 없는 까닭(손상·미래 schema). null이면 설치·삭제 가능.</summary>
    public string? PackagesError { get; private set; }

    /// <summary>열 때 만난 문제(손상된 상태 파일 등). 앱이 한 번 알린다.</summary>
    public IReadOnlyList<string> LoadErrors => _loadErrors;

    public AssetCatalog Catalog { get; private set; } = AssetCatalog.Empty;

    /// <summary>카탈로그가 바뀌었다(설치, 켜기·끄기, 삭제, 개인 에셋 저장). 바꾼 스레드에서 부른다.</summary>
    public event Action? Changed;

    /// <summary>테스트용: 설치·삭제 단계 이름("staged", "extracted", "moved", "committed", "archived", "personal")마다 불린다.</summary>
    internal Action<string>? StepHook { get; set; }

    /// <summary>
    /// 저장소를 연다(폴더가 없으면 만든다). 중간에 끊긴 설치·삭제를 정리한다: Staging 비우기, 상태에 없는 버전 폴더 지우기,
    /// 보관함으로 옮긴 뒤 상태를 쓰지 못한 삭제 마무리, 보관함을 최근 10개로 제한. 상태 파일이 손상돼도 예외 없이 열고 <see cref="LoadErrors"/>에 남긴다.
    /// </summary>
    public static AssetStore Open(string root, AssetValidationOptions? options = null)
    {
        var store = new AssetStore(root, options ?? new AssetValidationOptions());
        store.Load();
        return store;
    }

    private void Load()
    {
        foreach (var dir in new[] { Root, PackagesDirectory, PersonalDirectory, StagingDirectory, DeletedDirectory, CacheDirectory })
            Directory.CreateDirectory(dir);
        TryDeleteContents(StagingDirectory);
        LoadInstalled();
        LoadPersonal();
        if (PackagesError is null) Repair();
        Rebuild(notify: false);
    }

    // ───────────── 상태 파일 ─────────────

    private void LoadInstalled()
    {
        if (!File.Exists(InstalledPath)) return;
        try
        {
            var issues = new List<AssetIssue>();
            var node = AssetJson.Parse(File.ReadAllBytes(InstalledPath), InstalledFileName, 16, 4096, issues) as JsonObject
                       ?? throw new InvalidDataException(issues.FirstOrDefault()?.Message ?? InstalledFileName);
            if (!AssetJson.TryInt(node["schemaVersion"], out int schema) || schema < 1) throw new InvalidDataException(InstalledFileName);
            if (schema > InstalledSchemaVersion) throw new InvalidDataException(new AssetIssue(AssetIssueCode.FutureSchema, InstalledFileName, schema.ToString(CultureInfo.InvariantCulture)).Message);
            var list = new List<InstalledPackageState>();
            foreach (var item in node["packages"] as JsonArray ?? [])
            {
                if (item is not JsonObject o) continue;
                string id = AssetJson.String(o["id"]) ?? "", version = AssetJson.String(o["version"]) ?? "";
                if (!AssetManifest.IsValidId(id) || !AssetManifest.IsValidVersion(version) || list.Any(p => p.Id == id)) continue;
                list.Add(new InstalledPackageState
                {
                    Id = id,
                    Version = version,
                    Enabled = !AssetJson.TryBool(o["enabled"], out bool enabled) || enabled,
                    DisabledAssetIds = (o["disabledAssetIds"] as JsonArray ?? []).Select(AssetJson.String)
                        .OfType<string>().Distinct(StringComparer.Ordinal).ToArray(),
                    Source = AssetJson.String(o["source"]) ?? "file",
                    InstalledUtc = DateTime.TryParse(AssetJson.String(o["installedAt"]), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) ? at : default,
                    AutoUpdate = AssetJson.TryBool(o["autoUpdate"], out bool auto) && auto,
                    PreviousVersions = (o["previousVersions"] as JsonArray ?? []).Select(AssetJson.String)
                        .Where(v => AssetManifest.IsValidVersion(v) && v != version).Select(v => v!).Distinct().ToArray(),
                });
            }
            _installed = list;
            _installedStamp = StampOf(InstalledPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            PackagesError = ex.Message;
            _loadErrors.Add($"{InstalledFileName}: {ex.Message}");
        }
    }

    private void SaveInstalled(IReadOnlyList<InstalledPackageState> list)
    {
        if (PackagesError is { } error) throw new InvalidOperationException(error);
        var packages = new JsonArray();
        foreach (var state in list)
            packages.Add(new JsonObject
            {
                ["id"] = state.Id,
                ["version"] = state.Version,
                ["enabled"] = state.Enabled,
                ["disabledAssetIds"] = new JsonArray(state.DisabledAssetIds.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()),
                ["source"] = state.Source,
                ["installedAt"] = state.InstalledUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["autoUpdate"] = state.AutoUpdate,
                ["previousVersions"] = new JsonArray(state.PreviousVersions.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
            });
        AtomicFile.Write(InstalledPath, AssetJson.ToUtf8(new JsonObject { ["schemaVersion"] = InstalledSchemaVersion, ["packages"] = packages }));
        _installed = list.ToList();
        _installedStamp = StampOf(InstalledPath);
    }

    private void LoadPersonal()
    {
        if (!File.Exists(PersonalPath)) return;
        try
        {
            var issues = new List<AssetIssue>();
            var node = AssetJson.Parse(File.ReadAllBytes(PersonalPath), PersonalFileName, 64, 1 << 20, issues) as JsonObject
                       ?? throw new InvalidDataException(issues.FirstOrDefault()?.Message ?? PersonalFileName);
            if (!AssetJson.TryInt(node["schemaVersion"], out int schema) || schema < 1) throw new InvalidDataException(PersonalFileName);
            if (schema > PersonalSchemaVersion) throw new InvalidDataException(new AssetIssue(AssetIssueCode.FutureSchema, PersonalFileName, schema.ToString(CultureInfo.InvariantCulture)).Message);
            var records = new List<PersonalRecord>();
            foreach (var item in node["assets"] as JsonArray ?? [])
            {
                if (item is not JsonObject o) continue;
                var record = ReadPersonalRecord(o);
                if (record is not null && !records.Any(r => r.Id == record.Id)) records.Add(record);
            }
            _personal = records;
            _disabledBuiltInEffects = new HashSet<string>((node["disabledBuiltInEffects"] as JsonArray ?? [])
                .Select(AssetJson.String).OfType<string>().Where(id => EffectRegistry.BuiltIn.Any(d => d.TypeId == id)), StringComparer.Ordinal);
            _favorites = new HashSet<string>((node["favorites"] as JsonArray ?? []).Select(AssetJson.String).OfType<string>(), StringComparer.Ordinal);
            _personalStamp = StampOf(PersonalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            PersonalError = ex.Message;
            _loadErrors.Add($"{PersonalFileName}: {ex.Message}");
        }
    }

    private static PersonalRecord? ReadPersonalRecord(JsonObject o)
    {
        string id = AssetJson.String(o["id"]) ?? "", kind = AssetJson.String(o["kind"]) ?? "";
        if (id.Length == 0) return null;
        DateTime created = DateTime.TryParse(AssetJson.String(o["createdAt"]), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) ? at : default;
        IAssetData? data = null;
        if (AssetKinds.Find(kind) is { } definition && o["data"] is JsonObject json)
        {
            var context = new AssetReadContext(id, strict: false) { AssetId = id };
            data = definition.ReadPersonal(json, context);
            if (data is not null && data.Id != id) data = WithId(data, id);
        }
        bool builtIn = AssetJson.TryBool(o["isBuiltIn"], out bool marked)
            ? marked && kind == AssetKinds.Gradient
            : kind == AssetKinds.Gradient && data is GradientAsset gradient && IsDefaultGradient(gradient.Gradient);
        bool enabled = !AssetJson.TryBool(o["enabled"], out bool active) || active;
        return new PersonalRecord(id, kind, created, data, data is null ? (JsonObject)o.DeepClone() : null, builtIn, enabled);
    }

    internal static bool IsDefaultGradient(GradientPreset candidate)
        => GradientPresets.BuiltIns().Any(original =>
            string.Equals(candidate.Name, original.Name, StringComparison.OrdinalIgnoreCase)
            && candidate.Stops.SequenceEqual(original.Stops)
            && candidate.AlphaStops.SequenceEqual(original.AlphaStops)
            && candidate.Interpolation == original.Interpolation && candidate.Dither == original.Dither
            && candidate.ColorSpace == original.ColorSpace && candidate.Angle == original.Angle
            && candidate.Shape == original.Shape && candidate.Animated == original.Animated
            && candidate.Reverse == original.Reverse && candidate.Cycles == original.Cycles
            && candidate.Easing == original.Easing && candidate.PingPong == original.PingPong);

    private void SavePersonal(List<PersonalRecord> records, HashSet<string> favorites, HashSet<string>? disabledBuiltInEffects = null)
    {
        if (PersonalError is { } error) throw new InvalidOperationException(error);
        var disabled = disabledBuiltInEffects ?? _disabledBuiltInEffects;
        WritePersonalFile(records, favorites, disabled);
        _personal = records;
        _favorites = favorites;
        _disabledBuiltInEffects = disabled;
        StepHook?.Invoke("personal");
        Rebuild(notify: true);
    }

    private void WritePersonalFile(List<PersonalRecord> records, HashSet<string> favorites, HashSet<string> disabledBuiltInEffects)
    {
        var assets = new JsonArray();
        foreach (var record in records)
            assets.Add(WritePersonalRecord(record));
        var known = records.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var disabled = new JsonArray(disabledBuiltInEffects.Order(StringComparer.Ordinal)
            .Select(id => (JsonNode)JsonValue.Create(id)!).ToArray());
        // 즐겨찾기는 개인 에셋과 설치 에셋 ID를 모두 담는다(패키지를 지웠다 다시 설치해도 남게 설치 에셋 ID는 지우지 않는다).
        var favs = new JsonArray(favorites.Where(id => !id.StartsWith(PersonalIdPrefix, StringComparison.Ordinal) || known.Contains(id))
            .Order(StringComparer.Ordinal).Select(id => (JsonNode)JsonValue.Create(id)!).ToArray());
        Directory.CreateDirectory(PersonalDirectory);
        AtomicFile.Write(PersonalPath, AssetJson.ToUtf8(new JsonObject
        {
            ["schemaVersion"] = PersonalSchemaVersion,
            ["assets"] = assets,
            ["favorites"] = favs,
            ["disabledBuiltInEffects"] = disabled,
        }));
        _personalStamp = StampOf(PersonalPath);
    }

    private static JsonObject WritePersonalRecord(PersonalRecord record)
    {
        if (record.Raw is { } raw) return (JsonObject)raw.DeepClone();
        var definition = AssetKinds.Find(record.Kind)!;
        return new JsonObject
        {
            ["id"] = record.Id,
            ["kind"] = record.Kind,
            ["createdAt"] = record.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["data"] = definition.WritePersonal(record.Data!, record.Id),
            ["isBuiltIn"] = record.IsBuiltIn,
            ["enabled"] = record.Enabled,
        };
    }

    // ───────────── 여러 앱 ─────────────

    private static (DateTime, long) StampOf(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.LastWriteTimeUtc, info.Length) : default;
    }

    /// <summary>여러 앱이 저장소를 바꿀 때 차례를 정하는 잠금 파일(바꾸는 동안만 쥔다).</summary>
    internal const string LockFileName = "store.lock";

    private string LockPath => Path.Combine(Root, LockFileName);

    /// <summary>잠금 파일을 잡을 때까지 기다리는 최대 시간(다른 앱이 설치 중이면 끝날 때까지).</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 저장소를 바꾸기 시작: 잠금 파일을 잡고(다른 앱이 쥐고 있으면 기다림) 다른 앱이 쓴 상태 파일을 다시 읽는다. 돌려받은 것을 닫으면 잠금을 푼다.
    /// 바꾸는 메서드 안에서 다시 불러도 된다.
    /// </summary>
    private IDisposable BeginChange()
    {
        if (_lockDepth++ == 0)
        {
            var deadline = DateTime.UtcNow + LockTimeout;
            while (true)
            {
                try
                {
                    _lock = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    break;
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(50);
                }
                catch
                {
                    _lockDepth--;
                    throw;
                }
            }
            ReloadIfChanged();
        }
        return new ChangeScope(this);
    }

    private sealed class ChangeScope(AssetStore store) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (--store._lockDepth > 0) return;
            store._lock?.Dispose();
            store._lock = null;
        }
    }

    /// <summary>다른 앱이 상태 파일을 바꿨으면 다시 읽는다(읽지 못하면 그 까닭을 남겨 덮어쓰지 않는다).</summary>
    private void ReloadIfChanged()
    {
        bool changed = false;
        if (StampOf(InstalledPath) != _installedStamp)
        {
            PackagesError = null;
            _installed = [];
            LoadInstalled();
            _installedStamp = StampOf(InstalledPath);
            changed = true;
        }
        if (StampOf(PersonalPath) != _personalStamp && PersonalError is null)
        {
            _personal = [];
            _favorites = new HashSet<string>(StringComparer.Ordinal);
            _disabledBuiltInEffects = new HashSet<string>(StringComparer.Ordinal);
            LoadPersonal();
            _personalStamp = StampOf(PersonalPath);
            changed = true;
        }
        if (changed) Rebuild(notify: true);
    }

    // ───────────── 정리 ─────────────

    /// <summary>끊긴 설치·삭제를 마무리하고 보관함을 최근 10개로 제한한다.</summary>
    private void Repair()
    {
        // 완성되지 않은 보관함과 저장 전 중단된 개인 삭제를 정리한다.
        foreach (var archive in SafeDirectories(DeletedDirectory))
        {
            var package = ReadArchive(archive);
            var personal = ReadPersonalArchive(archive);
            if (package is null && personal is null)
            {
                if (ReadPersonalArchiveEntry(archive) is null) TryDeleteDirectory(archive);
            }
            else if (personal is not null && PersonalError is null && _personal.Any(r => r.Id == personal.Id))
                TryDeleteDirectory(archive);
        }

        // 활성 버전 폴더가 없는 상태 줄: 보관함으로 옮긴 뒤 상태를 쓰지 못한 삭제(또는 되돌리기 중 끊김)면 삭제를 마무리한다.
        var archived = SafeDirectories(DeletedDirectory).Select(ReadArchive).OfType<DeletedPackage>().Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var states = _installed.ToList();
        bool changed = states.RemoveAll(s => !Directory.Exists(VersionFolder(s.Id, s.Version)) && archived.Contains(s.Id)) > 0;
        if (changed) TrySaveInstalled(states);

        // 상태에 없는 패키지·버전 폴더는 반쯤 된 설치다(옮긴 뒤 상태를 쓰기 전에 끊김).
        foreach (var packageDir in SafeDirectories(PackagesDirectory))
        {
            var state = _installed.FirstOrDefault(s => s.Id == Path.GetFileName(packageDir));
            if (state is null)
            {
                TryDeleteDirectory(packageDir);
                continue;
            }
            foreach (var versionDir in SafeDirectories(packageDir))
            {
                string version = Path.GetFileName(versionDir);
                if (version != state.Version && !state.PreviousVersions.Contains(version)) TryDeleteDirectory(versionDir);
            }
        }
        PruneDeleted();
    }

    private void TrySaveInstalled(IReadOnlyList<InstalledPackageState> states)
    {
        try
        {
            SaveInstalled(states);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _loadErrors.Add($"{InstalledFileName}: {ex.Message}");
        }
    }

    // ───────────── 카탈로그 ─────────────

    private void Rebuild(bool notify)
    {
        var personal = _personal.Where(r => r.Data is not null)
            .Select(r => new AssetEntry(r.Id, r.Kind, AssetOrigin.Personal, r.Data!)
            {
                Favorite = _favorites.Contains(r.Id), IsBuiltIn = r.IsBuiltIn, Enabled = r.Enabled,
            }).ToArray();
        var packages = _installed.Select(LoadPackage).ToArray();
        Catalog = new AssetCatalog(personal, packages, _disabledBuiltInEffects);
        if (notify) Changed?.Invoke();
    }

    /// <summary>
    /// 설치된 패키지의 manifest와 에셋 JSON만 읽는다(미리보기·문서는 경로만 — 보이는 항목이 늦게 decode).
    /// 에셋 파일의 해시와 schema를 다시 보므로, 디스크에서 바뀌었거나 앱이 그 효과를 더는 모르면 읽지 못한 패키지로 표시한다.
    /// </summary>
    private InstalledPackage LoadPackage(InstalledPackageState state)
    {
        string folder = VersionFolder(state.Id, state.Version);
        var limits = _options.EffectiveLimits;
        var issues = new List<AssetIssue>();
        AssetManifest? manifest = null;
        var entries = new List<AssetEntry>();
        try
        {
            string manifestPath = Path.Combine(folder, AssetManifest.FileName);
            if (!File.Exists(manifestPath)) issues.Add(new AssetIssue(AssetIssueCode.MissingManifest));
            else if (new FileInfo(manifestPath).Length > limits.MaxJsonBytes) issues.Add(new AssetIssue(AssetIssueCode.FileTooLarge, AssetManifest.FileName));
            else if (AssetJson.Parse(File.ReadAllBytes(manifestPath), AssetManifest.FileName, limits.MaxJsonDepth, limits.MaxStringLength, issues) is { } node)
                manifest = AssetManifest.Read(node, issues);
            if (manifest is not null && (manifest.Id != state.Id || manifest.Version != state.Version))
                issues.Add(new AssetIssue(AssetIssueCode.InvalidField, AssetManifest.FileName, "id"));
            if (manifest is not null && issues.Count == 0)
            {
                foreach (var entry in manifest.Assets)
                {
                    if (AssetKinds.Find(entry.Kind) is not { } kind)
                    {
                        issues.Add(new AssetIssue(AssetIssueCode.UnknownKind, AssetManifest.FileName, entry.Kind));
                        continue;
                    }
                    if (!AssetPackageValidator.CheckPath(entry.Path, limits, issues) || !kind.Accepts(entry.Path)) continue;
                    string file = Path.Combine(folder, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(file) || new FileInfo(file).Length > AssetPackageValidator.MaxBytes(entry.Path, limits))
                    {
                        issues.Add(new AssetIssue(AssetIssueCode.MissingFile, entry.Path));
                        continue;
                    }
                    byte[] data = File.ReadAllBytes(file);
                    if (!manifest.Files.TryGetValue(entry.Path, out var hash) || AssetPackageWriter.Hash(data) != hash)
                    {
                        issues.Add(new AssetIssue(AssetIssueCode.HashMismatch, entry.Path));
                        continue;
                    }
                    var value = kind.Read(data, new AssetReadContext(entry.Path, strict: true, limits, issues) { AssetId = entry.Id });
                    if (value is null) continue;
                    string? preview = null;
                    if (entry.Preview is { } p && AssetPackageValidator.CheckPath(p, limits, []))
                    {
                        string previewFile = Path.Combine(folder, p.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(previewFile)) preview = previewFile;
                    }
                    entries.Add(new AssetEntry(entry.Id, entry.Kind, AssetOrigin.Package, value)
                    {
                        PackageId = state.Id,
                        PackageVersion = state.Version,
                        PackageName = manifest.Name,
                        Enabled = state.Enabled && !state.DisabledAssetIds.Contains(entry.Id),
                        Favorite = _favorites.Contains(entry.Id),
                        PreviewPath = preview,
                    });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(new AssetIssue(AssetIssueCode.MissingFile, null, ex.Message));
        }
        if (issues.Count > 0) entries.Clear();
        return new InstalledPackage(state, folder, manifest, entries, issues);
    }

    // ───────────── 설치·업데이트 ─────────────

    /// <summary>
    /// 로컬 <c>.gpixel-asset</c>을 설치한다(계획 7.3). 다른 버전이 설치돼 있으면 업데이트이고, 이전 활성 버전은 되돌리기용으로 남긴다.
    /// 같은 ID·버전이 같은 내용으로 있으면 그 버전을 켜고 활성으로 둔다. 다른 내용이면 <see cref="AssetIssueCode.VersionConflict"/>.
    /// 검사에 실패하면 <see cref="AssetPackageException"/>, 디스크 오류는 IO 예외 — 어느 경우에도 지금 설치 상태는 그대로다.
    /// </summary>
    public InstalledPackage Install(string packagePath, string source = "file", CancellationToken cancellation = default)
    {
        using var change = BeginChange();
        if (PackagesError is { } error) throw new InvalidOperationException(error);
        string token = Guid.NewGuid().ToString("N");
        string stagedFile = Path.Combine(StagingDirectory, token + AssetPackage.Extension);
        string stagedDir = Path.Combine(StagingDirectory, token);
        // 이번 설치가 옮겨 놓은 버전 폴더(상태를 쓰기 전에 실패하면 지운다 — 끊긴 경우는 다음 Open이 지움).
        string? moved = null;
        try
        {
            // 1~2. Staging으로 옮기고 크기를 본다(원격 내려받기도 이 자리에 받는다).
            var limits = _options.EffectiveLimits;
            if (new FileInfo(packagePath).Length > limits.MaxPackageBytes)
                throw new AssetPackageException(new AssetIssue(AssetIssueCode.PackageTooLarge, null, limits.MaxPackageBytes.ToString(CultureInfo.InvariantCulture)));
            Directory.CreateDirectory(StagingDirectory);
            File.Copy(packagePath, stagedFile);
            StepHook?.Invoke("staged");
            cancellation.ThrowIfCancellationRequested();

            // 3~6. 경로, manifest, 앱 호환성, 해시, 모든 에셋 JSON — 디스크에 쓰기 전에 끝낸다.
            var package = AssetPackageValidator.Read(stagedFile, _options);
            var manifest = package.Manifest;
            cancellation.ThrowIfCancellationRequested();

            var current = _installed.FirstOrDefault(s => s.Id == manifest.Id);
            string target = VersionFolder(manifest.Id, manifest.Version);
            bool referenced = current is not null && (current.Version == manifest.Version || current.PreviousVersions.Contains(manifest.Version));
            if (referenced && Directory.Exists(target))
            {
                if (!SameInstalledContent(target, manifest))
                    throw new AssetPackageException(new AssetIssue(AssetIssueCode.VersionConflict, null, $"{manifest.Id} {manifest.Version}"));
            }
            else
            {
                // 7. Staging에 풀고 버전 폴더로 한 번에 옮긴다.
                Directory.CreateDirectory(stagedDir);
                File.WriteAllBytes(Path.Combine(stagedDir, AssetManifest.FileName), Encoding.UTF8.GetBytes(manifest.Serialize()));
                foreach (var (path, data) in package.Files)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string file = Path.Combine(stagedDir, path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllBytes(file, data);
                }
                StepHook?.Invoke("extracted");
                cancellation.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.Combine(PackagesDirectory, manifest.Id));
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true); // 상태에 없는 반쯤 된 폴더
                Directory.Move(stagedDir, target);
                moved = target;
                StepHook?.Invoke("moved");
            }

            // 8. 상태 파일을 바꿔치기한다 — 이 한 번의 rename이 설치의 완료 지점이다.
            var previous = current is null ? []
                : new[] { current.Version }.Concat(current.PreviousVersions).Where(v => v != manifest.Version).Distinct().Take(KeptPreviousVersions).ToArray();
            var state = new InstalledPackageState
            {
                Id = manifest.Id,
                Version = manifest.Version,
                Enabled = current is null || current.Version == manifest.Version || current.Enabled,
                DisabledAssetIds = current?.DisabledAssetIds.Where(id => manifest.Assets.Any(asset => asset.Id == id)).ToArray() ?? [],
                Source = source,
                InstalledUtc = DateTime.UtcNow,
                AutoUpdate = current?.AutoUpdate ?? false,
                PreviousVersions = previous,
            };
            var states = _installed.ToList();
            int index = states.FindIndex(s => s.Id == manifest.Id);
            if (index >= 0) states[index] = state;
            else states.Add(state);
            SaveInstalled(states);
            moved = null;
            StepHook?.Invoke("committed");

            // 9. 남길 버전 밖의 폴더를 치우고 카탈로그를 다시 만든다.
            RemoveUnreferencedVersions(state);
            Rebuild(notify: true);
            return Catalog.FindPackage(manifest.Id)!;
        }
        finally
        {
            TryDeleteFile(stagedFile);
            TryDeleteDirectory(stagedDir);
            if (moved is not null) TryDeleteDirectory(moved);
        }
    }

    private static bool SameInstalledContent(string folder, AssetManifest manifest)
    {
        try
        {
            var issues = new List<AssetIssue>();
            var node = AssetJson.Parse(File.ReadAllBytes(Path.Combine(folder, AssetManifest.FileName)), AssetManifest.FileName, 64, 1 << 20, issues);
            return AssetManifest.Read(node, issues) is { } installed
                   && installed.Files.Count == manifest.Files.Count
                   && installed.Files.All(pair => manifest.Files.TryGetValue(pair.Key, out var hash) && hash == pair.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void RemoveUnreferencedVersions(InstalledPackageState state)
    {
        foreach (var dir in SafeDirectories(Path.Combine(PackagesDirectory, state.Id)))
        {
            string version = Path.GetFileName(dir);
            if (version != state.Version && !state.PreviousVersions.Contains(version)) TryDeleteDirectory(dir);
        }
    }

    /// <summary>패키지를 켜거나 끈다. 끈 패키지의 에셋은 고르는 목록에서 빠지지만 파일은 그대로다(계획 7.4).</summary>
    public void SetEnabled(string packageId, bool enabled)
    {
        using var change = BeginChange();
        var states = _installed.ToList();
        int index = states.FindIndex(s => s.Id == packageId);
        if (index < 0 || states[index].Enabled == enabled) return;
        states[index] = states[index] with { Enabled = enabled };
        SaveInstalled(states);
        Rebuild(notify: true);
    }

    /// <summary>패키지 에셋 하나만 새 선택 목록에서 숨기거나 다시 보인다. 패키지 전체가 꺼져 있었다면 다른 에셋은 계속 숨긴다.</summary>
    public void SetPackageAssetEnabled(string assetId, bool enabled)
    {
        using var change = BeginChange();
        var entry = Catalog.Find(assetId);
        if (entry?.PackageId is not { } packageId) return;
        var states = _installed.ToList();
        int index = states.FindIndex(s => s.Id == packageId);
        if (index < 0 || entry.Enabled == enabled) return;
        var state = states[index];
        var hidden = state.DisabledAssetIds.ToHashSet(StringComparer.Ordinal);
        if (enabled)
        {
            if (!state.Enabled)
            {
                foreach (var sibling in Catalog.FindPackage(packageId)!.Entries) hidden.Add(sibling.Id);
            }
            hidden.Remove(assetId);
        }
        else hidden.Add(assetId);
        states[index] = state with { Enabled = state.Enabled || enabled, DisabledAssetIds = hidden.Order(StringComparer.Ordinal).ToArray() };
        SaveInstalled(states);
        Rebuild(notify: true);
    }

    /// <summary>보관한 이전 버전으로 되돌린다(지금 버전은 이전 버전으로 남음). 이전 버전이 없으면 false.</summary>
    public bool Rollback(string packageId)
    {
        using var change = BeginChange();
        var states = _installed.ToList();
        int index = states.FindIndex(s => s.Id == packageId);
        if (index < 0) return false;
        var state = states[index];
        var target = state.PreviousVersions.FirstOrDefault(v => Directory.Exists(VersionFolder(packageId, v)));
        if (target is null) return false;
        states[index] = state with
        {
            Version = target,
            PreviousVersions = new[] { state.Version }.Concat(state.PreviousVersions.Where(v => v != target)).Take(KeptPreviousVersions).ToArray(),
        };
        SaveInstalled(states);
        Rebuild(notify: true);
        return true;
    }

    // ───────────── 삭제·보관함 ─────────────

    /// <summary>
    /// 패키지를 지운다: 패키지 폴더(모든 버전)를 보관함으로 옮긴 뒤 상태에서 뺀다. 프로젝트에 넣은 효과·그라데이션은 값 사본이라 바뀌지 않고,
    /// 개인 에셋과도 무관하다. 최근 10개 안에 있으면 <see cref="RestoreDeleted"/>로 되돌릴 수 있다.
    /// </summary>
    public void Delete(string packageId)
    {
        using var change = BeginChange();
        var states = _installed.ToList();
        int index = states.FindIndex(s => s.Id == packageId);
        if (index < 0) return;
        if (PackagesError is { } error) throw new InvalidOperationException(error);
        if (Catalog.FindPackage(packageId)?.IsOfficial == true)
            throw new InvalidOperationException("Official packages can be disabled, not deleted.");
        var state = states[index];
        string packageDir = Path.Combine(PackagesDirectory, packageId);
        string name = Catalog.FindPackage(packageId)?.Name ?? packageId;
        DateTime deletedUtc = NextDeletedUtc();
        string archive = Path.Combine(DeletedDirectory, packageId + "@" + deletedUtc.ToString("yyyyMMdd'T'HHmmssfffffff", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(archive);
        var entry = new JsonObject
        {
            ["id"] = state.Id,
            ["version"] = state.Version,
            ["name"] = name,
            ["deletedAt"] = deletedUtc.ToString("O", CultureInfo.InvariantCulture),
            ["enabled"] = state.Enabled,
            ["disabledAssetIds"] = new JsonArray(state.DisabledAssetIds.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()),
            ["source"] = state.Source,
            ["previousVersions"] = new JsonArray(state.PreviousVersions.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
        };
        AtomicFile.Write(Path.Combine(archive, "entry.json"), AssetJson.ToUtf8(entry));
        if (Directory.Exists(packageDir)) Directory.Move(packageDir, Path.Combine(archive, "package"));
        else Directory.CreateDirectory(Path.Combine(archive, "package"));
        StepHook?.Invoke("archived");
        states.RemoveAt(index);
        SaveInstalled(states);
        StepHook?.Invoke("committed");
        Rebuild(notify: true);
        PruneDeleted();
    }

    /// <summary>보관함의 삭제 패키지(최근 것 먼저).</summary>
    public IReadOnlyList<DeletedPackage> DeletedPackages
        => SafeDirectories(DeletedDirectory).Select(ReadArchive).OfType<DeletedPackage>().OrderByDescending(d => d.DeletedUtc).ToArray();

    /// <summary>보관함의 삭제 개인 에셋(최근 것 먼저).</summary>
    public IReadOnlyList<DeletedPersonalAsset> DeletedPersonalAssets
        => SafeDirectories(DeletedDirectory).Select(ReadPersonalArchive).OfType<DeletedPersonalAsset>()
            .OrderByDescending(d => d.DeletedUtc).ThenBy(d => d.Folder, StringComparer.Ordinal).ToArray();

    /// <summary>보관함의 패키지를 되돌린다. 같은 ID가 이미 설치돼 있으면 <see cref="AssetIssueCode.VersionConflict"/>.</summary>
    public void RestoreDeleted(DeletedPackage deleted)
    {
        using var change = BeginChange();
        if (PackagesError is { } error) throw new InvalidOperationException(error);
        if (_installed.Any(s => s.Id == deleted.Id))
            throw new AssetPackageException(new AssetIssue(AssetIssueCode.VersionConflict, null, $"{deleted.Id} {deleted.Version}"));
        var entry = ReadArchiveEntry(deleted.Folder) ?? throw new DirectoryNotFoundException(deleted.Folder);
        var state = new InstalledPackageState
        {
            Id = deleted.Id,
            Version = deleted.Version,
            Enabled = !AssetJson.TryBool(entry["enabled"], out bool enabled) || enabled,
            DisabledAssetIds = (entry["disabledAssetIds"] as JsonArray ?? []).Select(AssetJson.String)
                .OfType<string>().Distinct(StringComparer.Ordinal).ToArray(),
            Source = AssetJson.String(entry["source"]) ?? "file",
            InstalledUtc = DateTime.UtcNow,
            PreviousVersions = (entry["previousVersions"] as JsonArray ?? []).Select(AssetJson.String).OfType<string>().Where(AssetManifest.IsValidVersion).ToArray(),
        };
        // 상태를 먼저 쓴다: 옮기기 전에 끊기면 다음에 열 때 폴더 없는 줄을 지우고 보관함은 그대로 남는다.
        SaveInstalled([.. _installed, state]);
        Directory.Move(Path.Combine(deleted.Folder, "package"), Path.Combine(PackagesDirectory, deleted.Id));
        TryDeleteDirectory(deleted.Folder);
        Rebuild(notify: true);
        PruneDeleted();
    }

    private static JsonObject? ReadArchiveEntry(string archive)
    {
        try
        {
            string file = Path.Combine(archive, "entry.json");
            if (!File.Exists(file) || !Directory.Exists(Path.Combine(archive, "package"))) return null;
            return AssetJson.Parse(File.ReadAllBytes(file), "entry.json", 16, 4096, []) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DeletedPackage? ReadArchive(string archive)
    {
        if (ReadArchiveEntry(archive) is not { } o) return null;
        string id = AssetJson.String(o["id"]) ?? "", version = AssetJson.String(o["version"]) ?? "";
        if (!AssetManifest.IsValidId(id) || !AssetManifest.IsValidVersion(version)) return null;
        if (!DateTime.TryParse(AssetJson.String(o["deletedAt"]), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)) return null;
        return new DeletedPackage(archive, id, version, AssetJson.String(o["name"]) ?? id, at);
    }

    private static JsonObject? ReadPersonalArchiveEntry(string folder)
    {
        try
        {
            string file = Path.Combine(folder, "personal.json");
            return File.Exists(file) ? AssetJson.Parse(File.ReadAllBytes(file), "personal.json", 64, 1 << 20, []) as JsonObject : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    private static DeletedPersonalAsset? ReadPersonalArchive(string folder)
    {
        if (ReadPersonalArchiveEntry(folder) is not { } entry || entry["asset"] is not JsonObject asset) return null;
        string id = AssetJson.String(asset["id"]) ?? "", kind = AssetJson.String(asset["kind"]) ?? "";
        if (!id.StartsWith(PersonalIdPrefix, StringComparison.Ordinal) || AssetKinds.Find(kind) is null
            || !DateTime.TryParse(AssetJson.String(entry["deletedAt"]), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)) return null;
        var record = ReadPersonalRecord(asset);
        return record?.Data is { } data
            ? new DeletedPersonalAsset(id, data.Name, kind, at) { Folder = folder } : null;
    }

    private DateTime NextDeletedUtc()
    {
        var now = DateTime.UtcNow;
        var latest = SafeDirectories(DeletedDirectory)
            .Select(folder => ReadArchive(folder)?.DeletedUtc ?? ReadPersonalArchive(folder)?.DeletedUtc)
            .OfType<DateTime>().DefaultIfEmpty(DateTime.MinValue).Max();
        return now > latest ? now : latest.AddTicks(1);
    }

    private void PruneDeleted()
    {
        var archives = SafeDirectories(DeletedDirectory)
            .Select(folder => (Folder: folder, Deleted: ReadArchive(folder)?.DeletedUtc ?? ReadPersonalArchive(folder)?.DeletedUtc))
            .Where(item => item.Deleted is not null)
            .OrderByDescending(item => item.Deleted).ThenBy(item => item.Folder, StringComparer.Ordinal).ToArray();
        foreach (var item in archives.Skip(MaxDeletedItems))
            TryDeleteDirectory(item.Folder);
    }

    // ───────────── 내보내기 ─────────────

    /// <summary>설치된 패키지의 활성 버전을 <c>.gpixel-asset</c>으로 다시 묶는다(원본 파일과 해시 그대로).</summary>
    public void ExportPackage(string packageId, Stream output)
    {
        var package = Catalog.FindPackage(packageId) ?? throw new ArgumentException(packageId, nameof(packageId));
        if (package.Manifest is not { } manifest) throw new AssetPackageException(package.Issues);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in manifest.Files.Keys)
        {
            if (!AssetPackageValidator.CheckPath(path, _options.EffectiveLimits, [])) continue;
            files[path] = File.ReadAllBytes(Path.Combine(package.Folder, path.Replace('/', Path.DirectorySeparatorChar)));
        }
        AssetPackageWriter.Write(output, manifest, files);
    }

    // ───────────── 개인 에셋 ─────────────

    /// <summary>
    /// 개인 효과 레시피를 저장한다. <see cref="EffectRecipe.Id"/>가 있는 개인 에셋이면 그 자리를 바꾸고, 아니면 새 ID로 끝에 더한다.
    /// 매개변수가 연결·범위 검사(<see cref="Effects.RecipeParameters.Check"/>)를 통과하지 않으면 <see cref="AssetPackageException"/>. 저장된 항목을 돌려준다.
    /// </summary>
    public AssetEntry SavePersonalRecipe(EffectRecipe recipe)
    {
        var issues = new List<AssetIssue>();
        Effects.RecipeParameters.Check(recipe.Effects, recipe.Parameters, (code, detail) => issues.Add(new AssetIssue(code, null, detail)));
        if (issues.Count > 0) throw new AssetPackageException(issues);
        return SavePersonalData(AssetKinds.EffectRecipe, recipe, favorite: null);
    }

    /// <summary>개인 그라데이션을 저장한다(<paramref name="id"/>가 개인 에셋이면 바꾸고, 아니면 새로 더함). 즐겨찾기는 <see cref="GradientPreset.Favorite"/>를 따른다.</summary>
    public AssetEntry SavePersonalGradient(string? id, GradientPreset gradient)
    {
        GradientAssetFormat.Validate(gradient);
        return SavePersonalData(AssetKinds.Gradient, new GradientAsset(id ?? "", gradient with { Favorite = false }), gradient.Favorite);
    }

    /// <summary>
    /// 스크립트 원문을 개인 스크립트 에셋으로 저장한다(이름·작성자는 원문의 선언). 패키지 설치와 같은 검사(<see cref="ScriptAssetFormat.Check"/>)를
    /// 통과하지 못하면 <see cref="AssetPackageException"/>. 같은 원문(해시)의 개인 스크립트가 있으면 새로 더하지 않고 그 항목을 돌려준다.
    /// 실행 허락은 앱이 정한다(이 컴퓨터에서 쓴 원문).
    /// </summary>
    public AssetEntry SavePersonalScript(string source)
    {
        var issues = new List<AssetIssue>();
        var context = new AssetReadContext(AssetKinds.EffectScript, strict: true, _options.EffectiveLimits, issues);
        if (ScriptAssetFormat.Check(source, "source", context) is not { } program) throw new AssetPackageException(issues);
        if (Catalog.Personal.FirstOrDefault(e => e.Data is ScriptAsset s && s.Hash == program.ContentHash) is { } existing) return existing;
        return SavePersonalData(AssetKinds.EffectScript, ScriptAsset.From("", program), favorite: null);
    }

    private AssetEntry SavePersonalData(string kind, IAssetData data, bool? favorite)
    {
        using var change = BeginChange();
        if (PersonalError is { } error) throw new InvalidOperationException(error);
        if (!AssetManifest.IsValidName(data.Name)) throw new InvalidDataException(new AssetIssue(AssetIssueCode.InvalidField, null, "name").Message);
        var records = _personal.ToList();
        int index = records.FindIndex(r => r.Id == data.Id && r.Kind == kind);
        string id = index >= 0 ? data.Id : NewPersonalId();
        var record = new PersonalRecord(id, kind, index >= 0 ? records[index].CreatedUtc : DateTime.UtcNow, WithId(data, id), null,
            index >= 0 && records[index].IsBuiltIn, index < 0 || records[index].Enabled);
        if (index >= 0) records[index] = record;
        else records.Add(record);
        var favorites = new HashSet<string>(_favorites, StringComparer.Ordinal);
        if (favorite == true) favorites.Add(id);
        else if (favorite == false) favorites.Remove(id);
        SavePersonal(records, favorites);
        return Catalog.Find(id)!;
    }

    /// <summary>직접 만든 개인 에셋을 보관함으로 옮긴다. 기본 그라데이션은 끌 수만 있다.</summary>
    public void RemovePersonal(string id)
    {
        using var change = BeginChange();
        if (PersonalError is { } error) throw new InvalidOperationException(error);
        var record = _personal.FirstOrDefault(r => r.Id == id);
        if (record is null) return;
        if (record.IsBuiltIn) throw new InvalidOperationException("Built-in gradients cannot be deleted.");
        if (record.Data is null) throw new InvalidOperationException("Unreadable personal assets cannot be archived.");

        var records = _personal.Where(r => r.Id != id).ToList();
        bool favorite = _favorites.Contains(id);
        DateTime deletedUtc = NextDeletedUtc();
        string archive = Path.Combine(DeletedDirectory, "personal@" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archive);
        AtomicFile.Write(Path.Combine(archive, "personal.json"), AssetJson.ToUtf8(new JsonObject
        {
            ["asset"] = WritePersonalRecord(record),
            ["favorite"] = favorite,
            ["deletedAt"] = deletedUtc.ToString("O", CultureInfo.InvariantCulture),
        }));
        var favorites = new HashSet<string>(_favorites, StringComparer.Ordinal);
        favorites.Remove(id);
        SavePersonal(records, favorites);
        PruneDeleted();
    }

    /// <summary>보관한 개인 에셋을 같은 ID와 즐겨찾기·활성 상태로 복원한다.</summary>
    public void RestoreDeletedPersonal(DeletedPersonalAsset deleted)
    {
        using var change = BeginChange();
        if (PersonalError is { } error) throw new InvalidOperationException(error);
        var entry = ReadPersonalArchiveEntry(deleted.Folder) ?? throw new DirectoryNotFoundException(deleted.Folder);
        if (entry["asset"] is not JsonObject asset || ReadPersonalRecord(asset) is not { Data: not null } record
            || record.Id != deleted.Id || record.Kind != deleted.Kind)
            throw new InvalidDataException("Invalid personal asset archive.");
        if (_personal.Any(r => r.Id == record.Id))
            throw new InvalidOperationException($"Personal asset already exists: {record.Id}");
        var favorites = new HashSet<string>(_favorites, StringComparer.Ordinal);
        if (AssetJson.TryBool(entry["favorite"], out bool favorite) && favorite) favorites.Add(record.Id);
        SavePersonal([.. _personal, record], favorites);
        TryDeleteDirectory(deleted.Folder);
    }

    /// <summary>기본 효과를 새 선택 목록에서 켜거나 끈다(기존 프로젝트의 효과 종류 등록은 그대로).</summary>
    public bool IsBuiltInEffectEnabled(string typeId) => !_disabledBuiltInEffects.Contains(typeId);

    public void SetBuiltInEffectEnabled(string typeId, bool enabled)
    {
        if (!EffectRegistry.BuiltIn.Any(effect => effect.TypeId == typeId))
            throw new ArgumentException("Unknown built-in effect.", nameof(typeId));
        using var change = BeginChange();
        if (PersonalError is { } error) throw new InvalidOperationException(error);
        if (IsBuiltInEffectEnabled(typeId) == enabled) return;
        var disabled = new HashSet<string>(_disabledBuiltInEffects, StringComparer.Ordinal);
        if (enabled) disabled.Remove(typeId);
        else disabled.Add(typeId);
        SavePersonal(_personal.ToList(), new HashSet<string>(_favorites, StringComparer.Ordinal), disabled);
    }

    public void SetPersonalEnabled(string id, bool enabled)
    {
        using var change = BeginChange();
        if (PersonalError is { } error) throw new InvalidOperationException(error);
        var records = _personal.ToList();
        int index = records.FindIndex(record => record.Id == id);
        if (index < 0 || records[index].Enabled == enabled) return;
        records[index] = records[index] with { Enabled = enabled };
        SavePersonal(records, new HashSet<string>(_favorites, StringComparer.Ordinal));
    }

    /// <summary>개인·설치 에셋의 즐겨찾기를 켜거나 끈다(컴퓨터별 상태라 개인 에셋 파일에 둔다).</summary>
    public void SetFavorite(string assetId, bool favorite)
    {
        using var change = BeginChange();
        if (PersonalError is { } error) throw new InvalidOperationException(error);
        if (_favorites.Contains(assetId) == favorite) return;
        var favorites = new HashSet<string>(_favorites, StringComparer.Ordinal);
        if (favorite) favorites.Add(assetId);
        else favorites.Remove(assetId);
        SavePersonal(_personal.ToList(), favorites);
    }

    /// <summary>기본 그라데이션 값을 초기화한다. 직접 만든 동명 에셋은 건드리지 않는다.</summary>
    public void RestoreBuiltInGradients()
    {
        using var change = BeginChange();
        if (PersonalError is { } error) throw new InvalidOperationException(error);
        var records = _personal.ToList();
        foreach (var builtIn in GradientPresets.BuiltIns())
        {
            int index = records.FindIndex(r => r.IsBuiltIn && r.Kind == AssetKinds.Gradient && r.Data is GradientAsset g
                                               && string.Equals(g.Gradient.Name, builtIn.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) records[index] = records[index] with { Data = new GradientAsset(records[index].Id, builtIn) };
            else
            {
                string id = NewPersonalId();
                records.Add(new PersonalRecord(id, AssetKinds.Gradient, DateTime.UtcNow, new GradientAsset(id, builtIn), null, IsBuiltIn: true));
            }
        }
        SavePersonal(records, new HashSet<string>(_favorites, StringComparer.Ordinal));
    }

    /// <summary>
    /// 개인 에셋 파일을 처음 만든다(<see cref="AssetMigration"/> 전용). 잠금을 잡은 뒤 이미 있으면(같은 저장소를 쓰는 다른 앱이 먼저 만듦) 다시 읽고 false.
    /// <paramref name="items"/>는 (종류, 값, 즐겨찾기) 순서대로 새 ID를 받는다.
    /// </summary>
    internal bool TryCreatePersonal(IEnumerable<(string Kind, IAssetData Data, bool Favorite, bool IsBuiltIn)> items, DateTime nowUtc)
    {
        using var change = BeginChange();
        if (HasPersonalFile) return false;
        var records = new List<PersonalRecord>();
        var favorites = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (kind, data, favorite, isBuiltIn) in items)
        {
            string id = NewPersonalId();
            records.Add(new PersonalRecord(id, kind, nowUtc, WithId(data, id), null, isBuiltIn));
            if (favorite) favorites.Add(id);
        }
        PersonalError = null;
        WritePersonalFile(records, favorites, _disabledBuiltInEffects);
        _personal = records;
        _favorites = favorites;
        Rebuild(notify: true);
        return true;
    }

    /// <summary>개인 에셋을 쓸 수 없게 막는다(migration 실패 — 예전 파일을 다시 읽을 수 있을 때까지 새 파일을 만들지 않음).</summary>
    internal void BlockPersonal(string reason) => PersonalError = reason;

    /// <summary>
    /// 개인 에셋으로 새 패키지를 만든다. <paramref name="header"/>는 패키지 정보(ID, 버전, 이름, 작성자, 라이선스),
    /// <paramref name="preview"/>는 에셋마다 미리보기 PNG를 만드는 함수(null이면 넣지 않음).
    /// 만든 패키지는 검사를 다시 통과해야 쓴다 — 통과하지 못하면 <see cref="AssetPackageException"/>이고 <paramref name="output"/>에는 쓰지 않는다.
    /// </summary>
    public AssetManifest ExportPersonal(IEnumerable<string> ids, AssetManifest header, Stream output, Func<AssetEntry, byte[]?>? preview = null)
    {
        var builder = new AssetPackageBuilder(header);
        foreach (var id in ids)
        {
            var entry = Catalog.Personal.FirstOrDefault(e => e.Id == id) ?? throw new ArgumentException(id, nameof(ids));
            builder.Add(entry.Kind, entry.Data, preview?.Invoke(entry));
        }
        using var buffer = new MemoryStream();
        var manifest = builder.Write(buffer);
        buffer.Position = 0;
        AssetPackageValidator.Read(buffer, _options with { AppVersion = null });
        buffer.Position = 0;
        buffer.CopyTo(output);
        return manifest;
    }

    // ───────────── 도구 ─────────────

    public string VersionFolder(string id, string version) => Path.Combine(PackagesDirectory, id, version);

    private string NewPersonalId()
    {
        string id;
        do id = PersonalIdPrefix + Guid.NewGuid().ToString("N");
        while (_personal.Any(r => r.Id == id));
        return id;
    }

    private static IAssetData WithId(IAssetData data, string id) => data switch
    {
        EffectRecipe recipe => recipe with { Id = id },
        GradientAsset gradient => gradient with { Id = id },
        ScriptAsset script => script with { Id = id },
        _ => data,
    };

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetDirectories(dir) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void TryDeleteContents(string dir)
    {
        foreach (var sub in SafeDirectories(dir)) TryDeleteDirectory(sub);
        try
        {
            foreach (var file in Directory.GetFiles(dir)) TryDeleteFile(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>임시 파일에 쓰고 원자적으로 바꿔치기(같은 폴더 안 rename). 쓰는 도중 끊겨도 이전 파일은 그대로다.</summary>
internal static class AtomicFile
{
    public static void Write(string path, byte[] data)
    {
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
