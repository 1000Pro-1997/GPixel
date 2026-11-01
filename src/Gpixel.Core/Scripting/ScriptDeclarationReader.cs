using System.Text.RegularExpressions;
using Acornima.Ast;
using Gpixel.Core.Edit;
using Gpixel.Core.Localization;
using Gpixel.Core.Model;
using Jint;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Object;

namespace Gpixel.Core.Scripting;

/// <summary>
/// <c>effect({...})</c> 선언을 읽고 검사한다(13.2). 잘못된 곳을 모두 모아 위치와 함께 돌려준다.
/// 고칠 곳을 한 번에 보여 주려고 첫 오류에서 멈추지 않는다.
/// </summary>
internal static partial class ScriptDeclarationReader
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex SettingKey();

    public static ScriptParseResult Read(ScriptSandbox sandbox, Prepared<Script> script, string source, string hash)
    {
        var declaration = sandbox.Declare(script);
        var errors = new List<ScriptError>();
        void Fail(string message, params object[] path)
        {
            var (line, column) = ScriptSourceLocator.Declaration(script.Program!, path);
            errors.Add(new(ScriptErrorKind.Declaration, message, line, column));
        }

        var nameValue = declaration.Get("name");
        string name = nameValue is JsString ? nameValue.AsString().Trim() : "";
        if (name.Length == 0) Fail(DomainStrings.Script_InvalidName, "name");

        var authorValue = declaration.Get("author");
        string? author = authorValue is JsString && authorValue.AsString().Trim() is { Length: > 0 } a ? a : null;
        if (!authorValue.IsUndefined() && authorValue is not JsString) Fail(DomainStrings.Script_InvalidAuthor, "author");

        var animatedValue = declaration.Get("animated");
        bool animated = animatedValue.IsBoolean() && animatedValue.AsBoolean();
        if (!animatedValue.IsUndefined() && !animatedValue.IsBoolean()) Fail(DomainStrings.Script_InvalidAnimated, "animated");

        var areaValue = declaration.Get("area");
        var area = ScriptEffectArea.Global;
        if (areaValue is JsString && areaValue.AsString() == "local") area = ScriptEffectArea.Local;
        else if (!areaValue.IsUndefined() && !(areaValue is JsString && areaValue.AsString() == "global")) Fail(DomainStrings.Script_InvalidArea, "area");
        int radius = 0;
        if (area == ScriptEffectArea.Local)
        {
            if (Whole(declaration.Get("radius")) is { } r and >= 0 and <= ScriptEffectProgram.MaxPadding) radius = (int)r;
            else Fail(DomainStrings.FormatScript_InvalidRadius(max: ScriptEffectProgram.MaxPadding), "radius");
        }

        if (!declaration.Get("render").IsCallable()) Fail(DomainStrings.Script_MissingRender, "render");

        var paddingValue = declaration.Get("padding");
        EffectPadding? fixedPadding = paddingValue.IsUndefined() ? default(EffectPadding) : null;
        if (paddingValue is ArrayInstance)
        {
            fixedPadding = ReadPadding(paddingValue);
            if (fixedPadding is null) Fail(DomainStrings.FormatScript_InvalidPadding(max: ScriptEffectProgram.MaxPadding), "padding");
        }
        else if (!paddingValue.IsUndefined() && !paddingValue.IsCallable())
        {
            Fail(DomainStrings.FormatScript_InvalidPadding(max: ScriptEffectProgram.MaxPadding), "padding");
        }

        var settings = new List<ScriptSetting>();
        var settingsValue = declaration.Get("settings");
        if (settingsValue is ArrayInstance list)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (uint i = 0, count = ScriptHostApi.Length(list); i < count; i++)
                if (ReadSetting(list[i], (int)i, keys, Fail) is { } setting) settings.Add(setting);
        }
        else if (!settingsValue.IsUndefined())
        {
            Fail(DomainStrings.Script_InvalidSettings, "settings");
        }

        return errors.Count > 0
            ? new(null, errors)
            : new(new ScriptEffectProgram(script, source, hash, sandbox.Limits, name, author, animated, area, radius, settings, fixedPadding), []);
    }

    /// <summary><c>[왼, 위, 오른, 아래]</c>(0~<see cref="ScriptEffectProgram.MaxPadding"/> 정수 네 개). 아니면 null.</summary>
    public static EffectPadding? ReadPadding(JsValue value)
    {
        if (value is not ArrayInstance sides || ScriptHostApi.Length(sides) != 4) return null;
        Span<int> px = stackalloc int[4];
        for (uint i = 0; i < 4; i++)
        {
            if (Whole(sides[i]) is not { } side || side < 0 || side > ScriptEffectProgram.MaxPadding) return null;
            px[(int)i] = (int)side;
        }
        return new EffectPadding(px[0], px[1], px[2], px[3]);
    }

    private static ScriptSetting? ReadSetting(JsValue value, int index, HashSet<string> keys, Action<string, object[]> fail)
    {
        if (value is not ObjectInstance setting || value.IsCallable())
        {
            fail(DomainStrings.FormatScript_InvalidSetting(index: index), ["settings", index]);
            return null;
        }
        var keyValue = setting.Get("key");
        if (keyValue is not JsString || !SettingKey().IsMatch(keyValue.AsString()))
        {
            fail(DomainStrings.FormatScript_InvalidSettingKey(index: index), ["settings", index, "key"]);
            return null;
        }
        string key = keyValue.AsString();
        if (ScriptEffectProgram.ReservedSettingKeys.Contains(key))
        {
            fail(DomainStrings.FormatScript_ReservedSettingKey(key: key, keys: string.Join(", ", ScriptEffectProgram.ReservedSettingKeys)), ["settings", index, "key"]);
            return null;
        }
        if (!keys.Add(key))
        {
            fail(DomainStrings.FormatScript_DuplicateSettingKey(key: key), ["settings", index, "key"]);
            return null;
        }
        var labelValue = setting.Get("label");
        string label = labelValue is JsString ? labelValue.AsString() : key;
        if (!labelValue.IsUndefined() && labelValue is not JsString)
            fail(DomainStrings.FormatScript_InvalidSettingLabel(key: key), ["settings", index, "label"]);

        var typeValue = setting.Get("type");
        ScriptSettingType? type = typeValue is JsString ? typeValue.AsString() switch
        {
            "int" => ScriptSettingType.Int,
            "number" => ScriptSettingType.Number,
            "bool" => ScriptSettingType.Bool,
            "color" => ScriptSettingType.Color,
            "choice" => ScriptSettingType.Choice,
            _ => null,
        } : null;
        if (type is null)
        {
            fail(DomainStrings.FormatScript_InvalidSettingType(key: key), ["settings", index, "type"]);
            return null;
        }

        var defaultValue = setting.Get("default");
        void BadDefault() => fail(DomainStrings.FormatScript_InvalidSettingDefault(key: key), ["settings", index, "default"]);
        switch (type)
        {
            case ScriptSettingType.Int:
            case ScriptSettingType.Number:
            {
                bool whole = type == ScriptSettingType.Int;
                double? Bound(string field, out bool ok)
                {
                    var bound = setting.Get(field);
                    ok = bound.IsUndefined() || (bound is JsNumber && double.IsFinite(bound.AsNumber()) && (!whole || Whole(bound) is not null));
                    return ok && !bound.IsUndefined() ? bound.AsNumber() : null;
                }
                var min = Bound("min", out bool minOk);
                var max = Bound("max", out bool maxOk);
                var step = Bound("step", out bool stepOk);
                if (!minOk || !maxOk || !stepOk || min > max || step <= 0)
                {
                    fail(DomainStrings.FormatScript_InvalidSettingRange(key: key), ["settings", index, !minOk ? "min" : !maxOk || min > max ? "max" : "step"]);
                    return null;
                }
                double number = Math.Clamp(0, min ?? double.MinValue, max ?? double.MaxValue);
                if (!defaultValue.IsUndefined())
                {
                    bool fits = defaultValue is JsNumber && double.IsFinite(defaultValue.AsNumber()) && (!whole || Whole(defaultValue) is not null)
                        && defaultValue.AsNumber() >= (min ?? double.MinValue) && defaultValue.AsNumber() <= (max ?? double.MaxValue)
                        && (!whole || Math.Abs(defaultValue.AsNumber()) <= int.MaxValue);
                    if (!fits)
                    {
                        BadDefault();
                        return null;
                    }
                    number = defaultValue.AsNumber();
                }
                return new(key, label, type.Value, min, max, step, whole ? (int)number : (object)number, []);
            }
            case ScriptSettingType.Bool:
                if (!defaultValue.IsUndefined() && !defaultValue.IsBoolean())
                {
                    BadDefault();
                    return null;
                }
                return new(key, label, type.Value, null, null, null, defaultValue.IsBoolean() && defaultValue.AsBoolean(), []);
            case ScriptSettingType.Color:
            {
                var color = Color32.White;
                if (!defaultValue.IsUndefined() && !(defaultValue is JsString && Color32.TryParseHex(defaultValue.AsString(), out color)))
                {
                    BadDefault();
                    return null;
                }
                return new(key, label, type.Value, null, null, null, color, []);
            }
            default:
            {
                var choices = ReadChoices(setting.Get("choices"));
                if (choices is null)
                {
                    fail(DomainStrings.FormatScript_InvalidSettingChoices(key: key), ["settings", index, "choices"]);
                    return null;
                }
                string choice = choices[0].Value;
                if (!defaultValue.IsUndefined())
                {
                    if (defaultValue is not JsString || !choices.Any(c => c.Value == defaultValue.AsString()))
                    {
                        BadDefault();
                        return null;
                    }
                    choice = defaultValue.AsString();
                }
                return new(key, label, type.Value, null, null, null, choice, choices);
            }
        }
    }

    // 문자열이나 { value, label } 목록. 값은 비지 않고 겹치지 않아야 한다
    private static List<ScriptChoice>? ReadChoices(JsValue value)
    {
        if (value is not ArrayInstance list || ScriptHostApi.Length(list) == 0) return null;
        uint count = ScriptHostApi.Length(list);
        var choices = new List<ScriptChoice>((int)Math.Min(count, 256));
        for (uint i = 0; i < count; i++)
        {
            var item = list[i];
            ScriptChoice? choice = null;
            if (item is JsString) choice = new(item.AsString(), item.AsString());
            else if (item is ObjectInstance obj && !item.IsCallable() && obj.Get("value") is JsString choiceValue)
            {
                var labelValue = obj.Get("label");
                if (labelValue.IsUndefined()) choice = new(choiceValue.AsString(), choiceValue.AsString());
                else if (labelValue is JsString) choice = new(choiceValue.AsString(), labelValue.AsString());
            }
            if (choice is null || choice.Value.Length == 0 || choices.Any(c => c.Value == choice.Value)) return null;
            choices.Add(choice);
        }
        return choices;
    }

    private static double? Whole(JsValue value)
        => value is JsNumber && double.IsFinite(value.AsNumber()) && value.AsNumber() == Math.Floor(value.AsNumber()) ? value.AsNumber() : null;
}
