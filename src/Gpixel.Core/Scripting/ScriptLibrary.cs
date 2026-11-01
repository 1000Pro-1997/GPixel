using System.Collections.Concurrent;
using Acornima.Ast;
using Gpixel.Core.Edit;
using Jint;

namespace Gpixel.Core.Scripting;

/// <summary>스크립트를 실행할지에 대한 사용자 결정(원문 해시별, 13.5).</summary>
public enum ScriptTrust
{
    /// <summary>아직 묻지 않음 — 실행하지 않는다.</summary>
    Unknown,
    Trusted,
    /// <summary>실행하지 않기로 함.</summary>
    Blocked,
}

/// <summary>실행하지 않고 원문만 읽어 안 정보(신뢰 확인 창·목록 이름용). 선언에 글자 그대로 적은 값만 읽는다.</summary>
public sealed record ScriptInfo(string? Name, string? Author, bool ReadsOtherFrames);

/// <summary>
/// 이 앱 프로세스가 아는 스크립트 원문(해시 → 원문)과 해석 결과, 신뢰 결정(13.5). 여러 스레드에서 같이 쓴다.
/// <para>
/// 원문 해시가 스크립트 정체이므로 열린 프로젝트끼리 같이 써도 된다. 프로젝트의 스크립트 표(<see cref="Model.SpriteProject.Scripts"/>)는
/// 파일에 저장할 원문이고, 여기는 렌더러가 해시로 원문과 해석 결과를 찾는 곳이다. 해석은 해시마다 한 번만 하고, 신뢰한 스크립트만 해석한다
/// (해석이 선언을 읽으려고 맨 위를 실행하기 때문). 신뢰 결정의 저장은 앱이 맡는다(<see cref="Changed"/>).
/// </para>
/// </summary>
public static class ScriptLibrary
{
    /// <summary>스크립트 하나가 기억하는 여백 계산 결과 수(넘으면 비우고 다시 셈).</summary>
    private const int MaxPaddings = 256;

    private sealed class Entry(string source, ScriptInfo info)
    {
        public string Source { get; } = source;
        public ScriptInfo Info { get; } = info;
        public Lazy<ScriptParseResult> Parsed { get; } = new(() => ScriptEffectProgram.Parse(source), LazyThreadSafetyMode.ExecutionAndPublication);
        /// <summary>여백 계산 결과(실패도 — 시간 한도에 걸리는 <c>padding</c>을 프레임마다 다시 부르지 않게).</summary>
        public ConcurrentDictionary<(ScriptSettingValues Settings, int Width, int Height), (EffectPadding Padding, ScriptError? Error)> Paddings { get; } = new();
        /// <summary>설정값마다 마지막 여백 실패(렌더러가 그 효과의 실패로 센다).</summary>
        public ConcurrentDictionary<ScriptSettingValues, ScriptError> PaddingErrors { get; } = new();
    }

    private static readonly ConcurrentDictionary<string, Entry> s_entries = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, bool> s_trust = new(StringComparer.Ordinal);

    /// <summary>
    /// 신뢰 결정이나 실행 상태가 바뀌어 같은 효과 값의 결과가 달라질 수 있음(아무 스레드에서나 불림). 받는 쪽은 효과 결과 캐시를 버리고 다시 그린다.
    /// </summary>
    public static event Action? Changed;

    /// <summary>원문을 더하고 해시를 돌려준다(이미 있으면 그대로). 줄바꿈·BOM은 <see cref="ScriptEffectProgram.NormalizeSource"/>로 정리한다.</summary>
    public static string Add(string source)
    {
        var text = ScriptEffectProgram.NormalizeSource(source);
        var hash = ScriptEffectProgram.HashOf(text);
        s_entries.GetOrAdd(hash, _ => new Entry(text, ReadInfo(text)));
        return hash;
    }

    /// <summary>해시 모양(SHA-256 소문자 16진수 64자)인지.</summary>
    public static bool IsHash(string? hash)
        => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>정리한 원문(모르면 null).</summary>
    public static string? SourceOf(string hash) => s_entries.TryGetValue(hash, out var e) ? e.Source : null;

    /// <summary>실행하지 않고 읽은 정보(원문을 모르면 null).</summary>
    public static ScriptInfo? InfoOf(string hash) => s_entries.TryGetValue(hash, out var e) ? e.Info : null;

    /// <summary>화면 이름: 해석했으면 선언의 이름, 아니면 원문에 글자로 적힌 이름(모르면 null). 해석을 시작하지 않는다.</summary>
    public static string? NameOf(string hash)
    {
        if (!s_entries.TryGetValue(hash, out var e)) return null;
        return e.Parsed.IsValueCreated && e.Parsed.Value.Program is { } program ? program.Name : e.Info.Name;
    }

    public static ScriptTrust TrustOf(string hash)
        => s_trust.TryGetValue(hash, out bool trusted) ? trusted ? ScriptTrust.Trusted : ScriptTrust.Blocked : ScriptTrust.Unknown;

    /// <summary>해시의 신뢰 결정을 정한다(바뀌면 <see cref="Changed"/>).</summary>
    public static void SetTrust(string hash, bool trusted)
    {
        if (s_trust.TryGetValue(hash, out bool old) && old == trusted) return;
        s_trust[hash] = trusted;
        Changed?.Invoke();
    }

    /// <summary>저장해 둔 결정들을 알리지 않고 넣는다(앱 시작).</summary>
    public static void LoadTrust(IEnumerable<KeyValuePair<string, bool>> decisions)
    {
        foreach (var (hash, trusted) in decisions)
            if (IsHash(hash)) s_trust[hash] = trusted;
    }

    /// <summary>
    /// 해석 결과(원문을 모르거나 신뢰하지 않았으면 null). 처음 부르면 그 스레드에서 해석한다 — 오래 걸릴 수 있어 화면 스레드에서는
    /// <see cref="TryGetParsed"/>를 쓴다.
    /// </summary>
    public static ScriptParseResult? Parse(string hash)
        => s_entries.TryGetValue(hash, out var e) && TrustOf(hash) == ScriptTrust.Trusted ? e.Parsed.Value : null;

    /// <summary>이미 해석했으면 그 결과(해석을 시작하지 않음).</summary>
    public static ScriptParseResult? TryGetParsed(string hash)
        => s_entries.TryGetValue(hash, out var e) && TrustOf(hash) == ScriptTrust.Trusted && e.Parsed.IsValueCreated ? e.Parsed.Value : null;

    /// <summary>실행할 수 있는 해석 결과(원문을 알고, 신뢰했고, 오류 없이 해석됨). 아니면 null.</summary>
    public static ScriptEffectProgram? Runnable(string hash) => Parse(hash)?.Program;

    /// <summary>
    /// 이 스레드에서 신뢰한 스크립트를 미리 해석하고 Jint 실행기를 띄운다(13.4: 첫 실행이 느리므로 프로젝트를 열 때 작업 스레드에서).
    /// </summary>
    public static void Prewarm(IEnumerable<string> hashes)
    {
        foreach (var hash in hashes)
            if (Runnable(hash) is { } program)
                ScriptSandbox.Run(program.Limits, CancellationToken.None, _ => true);
    }

    /// <summary>
    /// 설정 <paramref name="settings"/>로 <paramref name="width"/>×<paramref name="height"/>에 더하는 여백(같은 입력은 다시 계산하지 않음).
    /// <c>padding</c>이 실패하면 넓히지 않고(0) 그 실패를 같은 열쇠로 기억하며, 렌더러가 그 효과의 실패로 세도록 설정값별로도 남긴다
    /// (<see cref="PaddingErrorOf"/> — 다시 실행하면 지움).
    /// </summary>
    internal static EffectPadding Padding(string hash, ScriptEffectProgram program, ScriptSettingValues settings, int width, int height)
    {
        var entry = s_entries[hash];
        var key = (settings, width, height);
        if (entry.Paddings.TryGetValue(key, out var known)) return known.Padding;
        (EffectPadding Padding, ScriptError? Error) result;
        try
        {
            result = (program.Padding(settings, width, height), null);
        }
        catch (ScriptEffectException ex)
        {
            result = (default, ex.Error);
            entry.PaddingErrors[settings] = ex.Error;
        }
        if (entry.Paddings.Count >= MaxPaddings) entry.Paddings.Clear();
        entry.Paddings[key] = result;
        return result.Padding;
    }

    /// <summary>설정값 <paramref name="settings"/>로 부른 <c>padding</c>의 실패(없으면 null).</summary>
    internal static ScriptError? PaddingErrorOf(string hash, ScriptSettingValues settings)
        => s_entries.TryGetValue(hash, out var e) && e.PaddingErrors.TryGetValue(settings, out var error) ? error : null;

    /// <summary>기억한 여백 실패를 지운다(사용자가 다시 실행 — 다음 계산에서 <c>padding</c>을 다시 부름).</summary>
    internal static void ForgetPaddingFailures(string hash)
    {
        if (!s_entries.TryGetValue(hash, out var e)) return;
        foreach (var (key, value) in e.Paddings)
            if (value.Error is not null) e.Paddings.TryRemove(key, out _);
        e.PaddingErrors.Clear();
    }

    /// <summary>실행 상태가 바뀌어 결과를 다시 그려야 함(<see cref="ScriptEffectRuntime.Resume"/>).</summary>
    internal static void NotifyChanged() => Changed?.Invoke();

    // 문법 트리만 읽는다(실행 없음). 문법 오류면 아무것도 모른다
    private static ScriptInfo ReadInfo(string text)
    {
        Script program;
        try
        {
            program = ScriptSandbox.Prepare(text).Program!;
        }
        catch (ScriptPreparationException)
        {
            return new ScriptInfo(null, null, false);
        }
        return new ScriptInfo(ScriptSourceLocator.DeclaredString(program, "name")?.Trim() is { Length: > 0 } name ? name : null,
            ScriptSourceLocator.DeclaredString(program, "author")?.Trim() is { Length: > 0 } author ? author : null,
            ScriptSourceLocator.Mentions(program, "frameSource"));
    }
}
