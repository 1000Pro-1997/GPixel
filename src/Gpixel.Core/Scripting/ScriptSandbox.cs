using System.Globalization;
using System.Text.RegularExpressions;
using Acornima.Ast;
using Gpixel.Core.Localization;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace Gpixel.Core.Scripting;

/// <summary>
/// 작업 스레드 하나의 Jint 실행기(13.3, 13.4). 같은 <see cref="ScriptLimits"/>면 스레드마다 하나를 만들어 계속 쓴다.
/// <para>
/// 격리: .NET 접근(<c>AllowClr</c>)은 켜지 않고, 문자열 코드 실행(<c>eval</c>, <c>Function</c>, <c>ShadowRealm</c>)을 막는다.
/// <c>Math.random</c>, <c>Date</c>, <c>Intl</c>, <c>Temporal</c>, <c>WeakRef</c>, <c>FinalizationRegistry</c>와 typed array·<c>ArrayBuffer</c> 계열은 지운다
/// (typed array는 한 번에 크게 할당해 메모리 한도를 건너뛸 수 있다). 남은 기본 객체는 모두 얼린다.
/// </para>
/// <para>
/// 결정론: 호출마다 처음 상태의 전역으로 되돌린 뒤(<c>RestoreGlobalSnapshot</c>) 해석해 둔 스크립트를 다시 실행해 선언을 새로 만든다.
/// 기본 객체는 얼어 있으므로 앞 호출이 남긴 값이 다음 호출에 닿지 않는다. 그래서 어느 스레드에서 몇 번째로 불러도 결과가 같다.
/// </para>
/// </summary>
internal sealed class ScriptSandbox
{
    private const string FileName = "effect.js";

    // 지울 전역: 시간·무작위·로캘·GC 관찰처럼 결과를 바꾸는 것, 문자열 코드 실행, 한 번에 크게 할당하는 버퍼
    private const string Lockdown = """
        (() => {
          'use strict';
          for (const name of ['eval', 'Function', 'Date', 'Intl', 'Temporal', 'ShadowRealm', 'WeakRef', 'FinalizationRegistry',
              'ArrayBuffer', 'SharedArrayBuffer', 'DataView', 'Atomics', 'Int8Array', 'Uint8Array', 'Uint8ClampedArray',
              'Int16Array', 'Uint16Array', 'Int32Array', 'Uint32Array', 'Float16Array', 'Float32Array', 'Float64Array',
              'BigInt64Array', 'BigUint64Array']) {
            delete globalThis[name];
          }
          delete Math.random;
          globalThis.lerp = (a, b, t) => a + (b - a) * t;
          // 문법으로만 닿는 기본 객체(생성기, async, 반복자)도 얼린다. Set은 돌면서 더한 값도 차례로 돈다
          const seen = new Set([globalThis, function* () {}, (function* () {})(), async function () {}, async function* () {},
            [][Symbol.iterator](), new Map()[Symbol.iterator](), new Set()[Symbol.iterator](), ''[Symbol.iterator](), /a/g[Symbol.matchAll]('')]);
          const visit = (value) => {
            if ((typeof value === 'object' || typeof value === 'function') && value !== null) seen.add(value);
          };
          for (const target of seen) {
            for (const key of Reflect.ownKeys(target)) {
              const property = Reflect.getOwnPropertyDescriptor(target, key);
              if (property !== undefined) {
                visit(property.value);
                visit(property.get);
                visit(property.set);
              }
            }
            visit(Reflect.getPrototypeOf(target));
            if (target !== globalThis) Object.freeze(target);
          }
        })();
        """;

    // Jint 호출 스택 첫 줄 "at name (effect.js:3:5)"의 줄과 칸(1부터)
    private static readonly Regex StackFrame = new(@":(\d+):(\d+)\)?\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    [ThreadStatic] private static Dictionary<ScriptLimits, ScriptSandbox>? t_sandboxes;

    private readonly CallCancellation _cancel = new();
    private readonly Jint.GlobalSnapshot _baseline;
    private readonly bool _cached;
    private bool _busy;
    private int _declarations;
    private JsValue _declaration = JsValue.Undefined;

    private ScriptSandbox(ScriptLimits limits, bool cached)
    {
        Limits = limits;
        _cached = cached;
        Engine = new Engine(options => options
            .Strict()
            .DisableStringCompilation()
            .Culture(CultureInfo.InvariantCulture)
            .LocalTimeZone(TimeZoneInfo.Utc)
            .TimeoutInterval(limits.Timeout)
            .RegexTimeoutInterval(limits.Timeout)
            .MaxStatements(limits.MaxStatements)
            .LimitMemory(limits.MemoryBytes)
            .LimitRecursion(limits.MaxRecursion)
            .MaxArraySize(limits.MaxArrayLength)
            .Constraint(_cancel));
        Engine.SetValue("effect", new ClrFunction(Engine, "effect", (_, args) =>
        {
            if (++_declarations == 1) _declaration = args.Length > 0 ? args[0] : JsValue.Undefined;
            return JsValue.Undefined;
        }, 1));
        ImagePrototype = ScriptHostApi.Install(this);
        Engine.Execute(Lockdown);
        _baseline = Engine.Advanced.CaptureGlobalSnapshot();
    }

    public ScriptLimits Limits { get; }
    public Engine Engine { get; }

    /// <summary>이 실행기에서 만든 그림(<c>img</c>, <c>img.blank</c>, <c>ctx.frameSource</c>)의 원형: <c>blank</c>, <c>blendOver</c>.</summary>
    public Jint.Native.Object.ObjectInstance ImagePrototype { get; }

    /// <summary>
    /// 이 스레드의 실행기에서 <paramref name="body"/>를 돌린다. 한도 초과와 스크립트 오류는 <see cref="ScriptEffectException"/>으로,
    /// <paramref name="cancel"/> 취소는 <see cref="OperationCanceledException"/>으로 나간다.
    /// </summary>
    public static T Run<T>(ScriptLimits limits, CancellationToken cancel, Func<ScriptSandbox, T> body)
    {
        var sandboxes = t_sandboxes ??= [];
        if (!sandboxes.TryGetValue(limits, out var sandbox)) sandboxes[limits] = sandbox = new ScriptSandbox(limits, cached: true);
        // 렌더 중 호스트 콜백(ctx.frameSource 등)이 같은 스레드에서 다시 스크립트를 부르면 쓰던 상태를 건드리지 않게 따로 만든다
        if (sandbox._busy) sandbox = new ScriptSandbox(limits, cached: false);
        return sandbox.Enter(cancel, body);
    }

    private T Enter<T>(CancellationToken cancel, Func<ScriptSandbox, T> body)
    {
        cancel.ThrowIfCancellationRequested();
        _busy = true;
        _cancel.Token = cancel;
        try
        {
            Engine.Advanced.RestoreGlobalSnapshot(_baseline);
            return body(this);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (ScriptEffectException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
        finally
        {
            _cancel.Token = default;
            _declaration = JsValue.Undefined;
            _busy = false;
            if (!_cached) Engine.Dispose();
        }
    }

    /// <summary>스크립트 맨 위를 실행하고 <c>effect(...)</c>에 넘긴 선언 객체를 돌려준다.</summary>
    public Jint.Native.Object.ObjectInstance Declare(Prepared<Script> script)
    {
        _declarations = 0;
        _declaration = JsValue.Undefined;
        Engine.Execute(script);
        if (_declarations == 0)
            throw new ScriptEffectException(new(ScriptErrorKind.Declaration, DomainStrings.Script_NoEffect, 0, 0));
        if (_declarations > 1)
            throw Declaration(DomainStrings.Script_DuplicateEffect, ScriptSourceLocator.EffectCall(script.Program!, 1));
        if (_declaration is not Jint.Native.Object.ObjectInstance declaration || _declaration.IsCallable())
            throw Declaration(DomainStrings.Script_DeclarationNotObject, ScriptSourceLocator.EffectCall(script.Program!, 0));
        return declaration;
    }

    public static Prepared<Script> Prepare(string source) => Engine.PrepareScript(source, FileName, strict: true);

    private static ScriptEffectException Declaration(string message, (int Line, int Column) at)
        => new(new(ScriptErrorKind.Declaration, message, at.Line, at.Column));

    private ScriptEffectException Translate(Exception ex)
    {
        var (line, column) = ex is JavaScriptException { Location.Start.Line: > 0 } thrown
            ? (thrown.Location.Start.Line, thrown.Location.Start.Column + 1)
            : StackLocation();
        var (kind, message) = ex switch
        {
            JavaScriptException js => (ScriptErrorKind.Runtime, DomainStrings.FormatScript_RuntimeError(detail: js.Message)),
            StatementsCountOverflowException => (ScriptErrorKind.Statements, DomainStrings.FormatScript_StatementLimit(count: Limits.MaxStatements)),
            TimeoutException or RegexMatchTimeoutException => (ScriptErrorKind.Timeout,
                DomainStrings.FormatScript_Timeout(seconds: Limits.Timeout.TotalSeconds.ToString("0.##", CultureInfo.CurrentCulture))),
            MemoryLimitExceededException or OutOfMemoryException => (ScriptErrorKind.Memory,
                DomainStrings.FormatScript_MemoryLimit(megabytes: Limits.MemoryBytes >> 20, length: Limits.MaxArrayLength)),
            RecursionDepthOverflowException or InsufficientExecutionStackException => (ScriptErrorKind.Recursion,
                DomainStrings.FormatScript_RecursionLimit(depth: Limits.MaxRecursion)),
            _ => (ScriptErrorKind.Runtime, DomainStrings.FormatScript_RuntimeError(detail: ex.Message)),
        };
        return new ScriptEffectException(new(kind, message, line, column));
    }

    // 한도 예외에는 위치가 없으므로 멈춘 순간의 JS 호출 스택에서 읽는다
    private (int Line, int Column) StackLocation()
    {
        var match = StackFrame.Match(Engine.Advanced.StackTrace);
        return match.Success
            ? (int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture))
            : (0, 0);
    }

    /// <summary>호출마다 바꾸는 취소 토큰(Jint 기본 취소 제약은 실행기를 만들 때 토큰이 정해진다).</summary>
    private sealed class CallCancellation : Constraint
    {
        public CancellationToken Token;

        public override void Check() => Token.ThrowIfCancellationRequested();

        public override void Reset()
        {
        }
    }
}
