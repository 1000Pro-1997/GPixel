using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using Acornima;
using Acornima.Ast;
using Gpixel.Core.Edit;
using Gpixel.Core.Localization;
using Gpixel.Core.Model;
using Jint;

namespace Gpixel.Core.Scripting;

/// <summary>스크립트 효과의 계산 범위(12.4). 전체는 프레임마다 한 번, 주변은 조각마다 <see cref="ScriptEffectProgram.Radius"/>만큼 여유를 붙여 부른다.</summary>
public enum ScriptEffectArea
{
    Global,
    Local,
}

/// <summary>스크립트 해석 결과: 성공하면 <see cref="Program"/>, 실패하면 <see cref="Errors"/>(줄·칸 포함).</summary>
public sealed record ScriptParseResult(ScriptEffectProgram? Program, IReadOnlyList<ScriptError> Errors)
{
    [MemberNotNullWhen(true, nameof(Program))]
    public bool Success => Program is not null;
}

/// <summary>
/// 해석한 스크립트 효과 하나(13.2): <c>effect({ name, author, animated, area, radius, settings, padding, render })</c>를 선언한 JS 파일.
/// 불변이고 여러 스레드에서 같이 써도 된다. 실행은 부른 스레드의 <see cref="ScriptSandbox"/>에서 <see cref="Limits"/> 안에서 한다.
/// <para>
/// 스크립트 정체는 <see cref="ContentHash"/>(정리한 원문의 SHA-256)다(13.5). 효과 TypeId <c>gpixel.effect.script</c>와 이 해시, 설정값으로 효과 하나가 정해진다.
/// </para>
/// </summary>
public sealed class ScriptEffectProgram
{
    /// <summary>여백 한 쪽과 주변 계산 반경의 최대 px.</summary>
    public const int MaxPadding = 1024;

    private readonly Prepared<Script> _script;
    private readonly EffectPadding? _fixedPadding; // null이면 padding 함수

    /// <summary>
    /// 설정 키로 쓸 수 없는 이름. 효과의 빠르기 곡선·반복이 같은 설정 JSON에 이 키로 저장된다(기본 움직이는 효과와 같은 모양).
    /// </summary>
    public static IReadOnlyList<string> ReservedSettingKeys { get; } = ["easing", "repeat", "repeatCount"];

    internal ScriptEffectProgram(Prepared<Script> script, string source, string contentHash, ScriptLimits limits, string name, string? author,
        bool animated, ScriptEffectArea area, int radius, IReadOnlyList<ScriptSetting> settings, EffectPadding? fixedPadding)
    {
        _script = script;
        Source = source;
        ContentHash = contentHash;
        Limits = limits;
        Name = name;
        Author = author;
        Animated = animated;
        Area = area;
        Radius = radius;
        Settings = settings;
        _fixedPadding = fixedPadding;
    }

    /// <summary>정리한 원문(<see cref="NormalizeSource"/>). 오류의 줄·칸은 이 글 기준이다.</summary>
    public string Source { get; }

    /// <summary>정리한 원문의 SHA-256(소문자 16진수 64자).</summary>
    public string ContentHash { get; }

    public ScriptLimits Limits { get; }

    /// <summary>선언의 <c>name</c>.</summary>
    public string Name { get; }

    /// <summary>선언의 <c>author</c>(선택, 없으면 null): 신뢰 확인 창에 보인다.</summary>
    public string? Author { get; }

    /// <summary>선언의 <c>animated</c>(기본 false): 프레임 진행에 따라 모습이 바뀌는지.</summary>
    public bool Animated { get; }

    /// <summary>선언의 <c>area</c>(기본 <see cref="ScriptEffectArea.Global"/>).</summary>
    public ScriptEffectArea Area { get; }

    /// <summary>선언의 <c>radius</c>: <see cref="ScriptEffectArea.Local"/>일 때 조각 둘레에 붙일 여유(px). 전체 효과는 0.</summary>
    public int Radius { get; }

    /// <summary>선언의 <c>settings</c>(선언 순서).</summary>
    public IReadOnlyList<ScriptSetting> Settings { get; }

    /// <summary>
    /// <paramref name="source"/>를 해석하고 맨 위를 한 번 실행해 선언을 검사한다. 문법 오류, 잘못된 선언, 맨 위 실행의 한도 초과와 오류는
    /// 줄·칸이 붙은 <see cref="ScriptParseResult.Errors"/>로 돌려준다(던지지 않는다).
    /// </summary>
    public static ScriptParseResult Parse(string source, ScriptLimits? limits = null)
    {
        limits ??= ScriptLimits.Default;
        var text = NormalizeSource(source);
        Prepared<Script> script;
        try
        {
            script = ScriptSandbox.Prepare(text);
        }
        catch (ScriptPreparationException ex) when (ex.InnerException is ParseErrorException parse)
        {
            return new(null, [new(ScriptErrorKind.Syntax, DomainStrings.FormatScript_SyntaxError(detail: parse.Description), parse.LineNumber, parse.Column + 1)]);
        }
        try
        {
            return ScriptSandbox.Run(limits, CancellationToken.None, sandbox => ScriptDeclarationReader.Read(sandbox, script, text, HashOf(text)));
        }
        catch (ScriptEffectException ex)
        {
            return new(null, [ex.Error]);
        }
    }

    /// <summary>해시와 해석에 쓰는 원문: 앞의 BOM을 빼고 줄바꿈을 LF로 통일한다(Windows와 macOS에서 같은 해시).</summary>
    public static string NormalizeSource(string source)
        => source.TrimStart('\uFEFF').ReplaceLineEndings("\n");

    /// <summary><paramref name="source"/>를 <see cref="NormalizeSource"/>로 정리한 글의 SHA-256(소문자 16진수).</summary>
    public static string HashOf(string source)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeSource(source))));

    /// <summary>
    /// 모든 설정 키의 값. 빠진 키, 종류가 틀린 값과 고르기 목록에 없는 값은 기본값으로, 숫자는 min~max로 자르고 int는 반올림한다.
    /// 값의 C# 형식은 <see cref="ScriptSettingType"/> 설명과 같다. 색은 <see cref="Color32"/>나 16진수 글로 받는다. 모르는 키는 버린다.
    /// </summary>
    public IReadOnlyDictionary<string, object> NormalizeSettings(IReadOnlyDictionary<string, object>? values)
    {
        var result = new Dictionary<string, object>(Settings.Count, StringComparer.Ordinal);
        foreach (var setting in Settings)
        {
            object? value = null;
            values?.TryGetValue(setting.Key, out value);
            result[setting.Key] = Coerce(setting, value) ?? setting.Default;
        }
        return result;
    }

    /// <summary>
    /// 효과가 <paramref name="width"/>×<paramref name="height"/> 그림 밖으로 넓히는 여백. 선언의 <c>padding</c>이 함수면
    /// <c>padding(s, width, height)</c>를 불러 <c>[왼, 위, 오른, 아래]</c>(0~<see cref="MaxPadding"/> 정수)를 받는다.
    /// </summary>
    public EffectPadding Padding(IReadOnlyDictionary<string, object>? settings, int width, int height, CancellationToken cancel = default)
    {
        if (_fixedPadding is { } fixedPadding) return fixedPadding;
        var values = NormalizeSettings(settings);
        return ScriptSandbox.Run(Limits, cancel, sandbox =>
        {
            var declaration = sandbox.Declare(_script);
            var s = ScriptHostApi.CreateSettings(sandbox.Engine, Settings, values);
            var result = sandbox.Engine.Invoke(declaration.Get("padding"), declaration, [s, width, height]);
            if (ScriptDeclarationReader.ReadPadding(result) is { } padding) return padding;
            var (line, column) = ScriptSourceLocator.Declaration(_script.Program!, "padding");
            throw new ScriptEffectException(new(ScriptErrorKind.Runtime, DomainStrings.FormatScript_InvalidPadding(max: MaxPadding), line, column));
        });
    }

    /// <summary>
    /// <c>render(img, ctx, s)</c>로 <paramref name="image"/>를 제자리에서 바꾼다(기본 효과 렌더러와 같은 계약).
    /// <paramref name="source"/>는 여백을 넣은 그림 안의 원본 자리(<c>ctx.source</c>). 실패하면 그림은 그대로 두고
    /// <see cref="ScriptEffectException"/>을, 취소되면 <see cref="OperationCanceledException"/>을 던진다.
    /// </summary>
    public void Render(PixelImage image, Rectangle source, ScriptFrameContext context, IReadOnlyDictionary<string, object>? settings = null,
        CancellationToken cancel = default)
    {
        if ((long)image.Width * image.Height * 4 > Limits.MaxArrayLength)
            throw new ScriptEffectException(new(ScriptErrorKind.Memory,
                DomainStrings.FormatScript_ImageTooLarge(width: image.Width, height: image.Height), 0, 0));
        var values = NormalizeSettings(settings);
        ScriptSandbox.Run(Limits, cancel, sandbox =>
        {
            var declaration = sandbox.Declare(_script);
            var s = ScriptHostApi.CreateSettings(sandbox.Engine, Settings, values);
            var ctx = ScriptHostApi.CreateContext(sandbox, Settings, s, source, context);
            var (img, data) = ScriptHostApi.CreateImage(sandbox, image);
            sandbox.Engine.Invoke(declaration.Get("render"), declaration, [img, ctx, s]);
            ScriptHostApi.CopyBack(data, image);
            return true;
        });
    }

    private static object? Coerce(ScriptSetting setting, object? value)
    {
        switch (setting.Type)
        {
            case ScriptSettingType.Int:
            case ScriptSettingType.Number:
                if (AsDouble(value) is not { } number || !double.IsFinite(number)) return null;
                if (setting.Type == ScriptSettingType.Int) number = Math.Round(number);
                number = Math.Clamp(number, setting.Min ?? double.MinValue, setting.Max ?? double.MaxValue);
                return setting.Type == ScriptSettingType.Int ? (int)Math.Clamp(number, int.MinValue, int.MaxValue) : (object)number;
            case ScriptSettingType.Bool:
                return value as bool?;
            case ScriptSettingType.Color:
                return value switch
                {
                    Color32 color => color,
                    string hex when Color32.TryParseHex(hex, out var parsed) => parsed,
                    _ => null,
                };
            default:
                return value is string choice && setting.Choices.Any(c => c.Value == choice) ? choice : null;
        }
    }

    private static double? AsDouble(object? value) => value switch
    {
        int i => i,
        long l => l,
        double d => d,
        float f => f,
        decimal m => (double)m,
        short s => s,
        byte b => b,
        _ => null,
    };
}
