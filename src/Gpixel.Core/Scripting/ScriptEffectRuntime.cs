using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;
using Gpixel.Core.Model;

namespace Gpixel.Core.Scripting;

/// <summary>스크립트 효과 하나(레이어의 효과 값)의 지금 상태.</summary>
public enum ScriptEffectState
{
    /// <summary>실행할 수 있음(실패한 적 없거나 마지막 실행이 성공).</summary>
    Ready,
    /// <summary>프로젝트에 원문이 없음 — 그리지 않는다.</summary>
    MissingSource,
    /// <summary>실행을 허락하지 않음(아직 묻지 않았거나 실행 안 함) — 그리지 않는다.</summary>
    NotTrusted,
    /// <summary>원문에 문법·선언 오류가 있음 — 그리지 않는다.</summary>
    Invalid,
    /// <summary>마지막 실행이 실패해 그 프레임에서 건너뜀(다른 프레임은 계속 계산).</summary>
    Failed,
    /// <summary>연속 <see cref="ScriptEffectRuntime.MaxConsecutiveFailures"/>번 실패해 다시 켤 때까지 계산하지 않음.</summary>
    Suspended,
}

/// <summary>
/// 스크립트 효과 상태와 오류(<see cref="ScriptEffectState.Invalid"/>는 해석 오류 전부, 실패는 마지막 실행 오류 하나),
/// 마지막으로 성공한 프레임 하나의 계산 시간(ms, 없으면 null).
/// </summary>
public sealed record ScriptEffectStatus(ScriptEffectState State, IReadOnlyList<ScriptError> Errors, double? LastMs)
{
    /// <summary>그리지 못하거나 못 그린 프레임이 있음(효과 목록·타임라인에 표시).</summary>
    public bool IsProblem => State != ScriptEffectState.Ready;
}

/// <summary>
/// 스크립트 효과 실행과 실패 기록(13.4). 렌더러는 효과마다 <see cref="Render"/>를 부르고, 화면은 <see cref="StatusOf"/>로 상태를 읽는다.
/// <para>
/// 실패(한도 초과·스크립트 오류)는 그 프레임에서 그 효과만 건너뛰고 (레이어, 효과 값)마다 기록한다. 같은 효과가 연속
/// <see cref="MaxConsecutiveFailures"/>번 실패하면 <see cref="Resume"/>을 부를 때까지(사용자가 다시 켤 때까지) 계산하지 않는다.
/// 성공하면 연속 횟수는 0으로 돌아간다. 기록은 실행 중 상태라 저장하지 않으며, 효과 값을 바꾸면(설정·범위·원문) 새 기록으로 시작한다.
/// 레이어를 모르는 그리기(효과만 그리는 공개 함수)는 기록하지 않고 실패한 프레임만 건너뛴다.
/// </para>
/// </summary>
public static class ScriptEffectRuntime
{
    /// <summary>연속으로 이만큼 실패하면 다시 켤 때까지 계산하지 않는다(한도 초과 하나가 2초까지 걸리므로 몇 프레임 안에 멈추게).</summary>
    public const int MaxConsecutiveFailures = 3;

    /// <summary>레이어 하나가 기억하는 효과 값 수(넘으면 실패하지 않은 기록부터 버림 — 값을 바꾸며 끌 때마다 새 값이 생긴다).</summary>
    private const int MaxRecordsPerLayer = 256;

    private sealed class Record
    {
        public int Failures;
        public ScriptError? Error;
        public double? LastMs;
    }

    private static readonly ConditionalWeakTable<Layer, ConcurrentDictionary<ScriptEffect, Record>> s_records = new();

    /// <summary>어느 효과가 실패했거나 실패에서 돌아왔거나 멈춤이 풀림(아무 스레드에서나 불림). 받는 쪽은 효과 목록·타임라인 표시를 다시 그린다.</summary>
    public static event Action? StatusChanged;

    /// <summary>
    /// 레이어 <paramref name="owner"/>의 효과 <paramref name="effect"/>(목록에 든 값이나 그릴 때 채운 사본) 상태. 해석을 시작하지 않으므로 화면 스레드에서 불러도 된다.
    /// </summary>
    public static ScriptEffectStatus StatusOf(Layer? owner, ScriptEffect effect)
    {
        var hash = effect.ScriptHash;
        if (ScriptLibrary.SourceOf(hash) is null) return new(ScriptEffectState.MissingSource, [], null);
        if (ScriptLibrary.TrustOf(hash) != ScriptTrust.Trusted) return new(ScriptEffectState.NotTrusted, [], null);
        if (ScriptLibrary.TryGetParsed(hash) is { Success: false } bad) return new(ScriptEffectState.Invalid, bad.Errors, null);
        if (owner is null || !s_records.TryGetValue(owner, out var records) || !records.TryGetValue(Key(effect), out var record))
            return new(ScriptEffectState.Ready, [], null);
        lock (record)
        {
            IReadOnlyList<ScriptError> errors = record.Error is { } error ? [error] : [];
            var state = record.Failures >= MaxConsecutiveFailures ? ScriptEffectState.Suspended
                : record.Failures > 0 ? ScriptEffectState.Failed : ScriptEffectState.Ready;
            return new(state, state == ScriptEffectState.Ready ? [] : errors, record.LastMs);
        }
    }

    /// <summary>
    /// 실패 기록과 기억한 여백 실패를 지워 다시 계산하게 한다(멈춘 효과를 사용자가 다시 켬). 결과를 다시 그리도록 <see cref="ScriptLibrary.Changed"/>도 알린다.
    /// </summary>
    public static void Resume(Layer owner, ScriptEffect effect)
    {
        ScriptLibrary.ForgetPaddingFailures(effect.ScriptHash);
        if (s_records.TryGetValue(owner, out var records) && records.TryRemove(Key(effect), out _))
        {
            StatusChanged?.Invoke();
            ScriptLibrary.NotifyChanged();
        }
    }

    /// <summary>
    /// <paramref name="image"/>에 효과를 입힌다(효과 정의의 렌더러). 실행할 수 없거나 멈춘 효과는 그대로 두고, 실패하면 그림을 바꾸지 않고 기록한다.
    /// 같은 설정의 <c>padding</c>이 실패했으면(<see cref="ScriptLibrary.Padding"/>이 넓히지 않음) <c>render</c>를 부르지 않고 그 실패를 같은 기록에 센다.
    /// 실패했으면 false — 이번 결과는 효과 값만으로 다시 얻을 수 없으므로 부르는 쪽은 캐시에 담지 않는다. 취소는 <see cref="OperationCanceledException"/>으로 나가고 실패로 세지 않는다.
    /// </summary>
    internal static bool Render(PixelImage image, ScriptEffect effect, Rectangle source, Layer? owner, CancellationToken cancel)
    {
        if (ScriptLibrary.Runnable(effect.ScriptHash) is not { } program) return true;
        var record = owner is null ? null : RecordOf(owner, effect);
        if (record is not null)
            lock (record)
                if (record.Failures >= MaxConsecutiveFailures) return true;
        // 움직이지 않는 스크립트는 진행을 쓰지 않는다(프레임 번호와 다른 프레임 원본은 그대로 준다)
        var context = ScriptFrameContext.From(effect);
        if (!program.Animated) context = context with { Time = 0, Progress = 0 };
        long started = Stopwatch.GetTimestamp();
        ScriptError? failure = ScriptLibrary.PaddingErrorOf(effect.ScriptHash, effect.Settings);
        if (failure is null)
            try
            {
                program.Render(image, source, context, effect.Settings, cancel);
            }
            catch (ScriptEffectException ex)
            {
                failure = ex.Error;
            }
        if (record is null) return failure is null;
        bool changed;
        lock (record)
        {
            if (failure is null)
            {
                changed = record.Failures > 0;
                record.Failures = 0;
                record.Error = null;
                record.LastMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            else
            {
                changed = record.Failures < MaxConsecutiveFailures;
                record.Failures++;
                record.Error = failure;
            }
        }
        if (changed) StatusChanged?.Invoke();
        return failure is null;
    }

    private static Record RecordOf(Layer owner, ScriptEffect effect)
    {
        var records = s_records.GetValue(owner, _ => new ConcurrentDictionary<ScriptEffect, Record>());
        var key = Key(effect);
        if (records.TryGetValue(key, out var record)) return record;
        if (records.Count >= MaxRecordsPerLayer)
            foreach (var (old, value) in records)
                if (Volatile.Read(ref value.Failures) == 0) records.TryRemove(old, out _);
        return records.GetOrAdd(key, _ => new Record());
    }

    // 목록에 든 값과 같아지게 그릴 때만 채운 값(프레임 위치·피벗·다른 프레임 원본)과 켜짐을 지운다
    private static ScriptEffect Key(ScriptEffect effect) => effect with
    {
        Enabled = true,
        FrameStep = 0,
        FrameSpan = 0,
        PivotPoint = null,
        FrameSource = null,
        RenderedFrameSource = null,
    };
}
